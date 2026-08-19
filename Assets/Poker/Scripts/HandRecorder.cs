using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════
//  Hand Recorder
//
//  MonoBehaviour (NOT NetworkBehaviour) that lives in the poker scene.
//  Subscribes to GameManager's C# events to build HandRecords during
//  play, then persists them via the IHandHistoryStorage abstraction.
//
//  Write pattern depends on the backend:
//
//    Firebase (shared /sessions collection):
//      • HOST  → SaveHandAsync (writes session + hand subcollection)
//      • ALL   → SavePlayerStatsAsync (writes to /players/{uid}/stats)
//      • ALL   → RegisterSessionParticipantAsync (first hand only)
//      • ALL   → UpdateSessionPlayerSummaryAsync (buy-in + per-hand)
//
//    Cloud Save (player-scoped):
//      • ALL   → SaveHandAsync (each client stores their own copy)
//      • ALL   → SavePlayerStatsAsync
//      • ALL   → UpdateSessionPlayerSummaryAsync (buy-in + per-hand)
//
//  The storage backend is determined by the BackendType set on
//  LobbyManager — there is a single enum switch for auth AND data.
//
//  To disable recording, uncheck "Enable Recording" in the Inspector
//  or don't place this component in the scene.
// ═══════════════════════════════════════════════════════════════════

public class HandRecorder : MonoBehaviour
{
    // ── Inspector ──────────────────────────────────────────────────
    [Header("Hand Recording")]
    [Tooltip("Master switch — uncheck to disable all recording and saving.")]
    [SerializeField] private bool enableRecording = true;

    // ── Storage back-end (resolved once in Awake from LobbyManager) ─
    private IHandHistoryStorage storage;
    private bool isSharedBackend; // true = Firestore (host writes sessions)

    // ── Current hand being recorded ───────────────────────────────
    private HandRecord currentHand;
    private bool isRecording;
    private bool localPlayerExitedMidHand;

    // ── Cached local player identity (captured at hand start) ───
    // NetworkList removals can arrive before RPCs in the same frame,
    // so we snapshot these values while the player is still present.
    private int    cachedLocalSlot = -1;
    private string cachedLocalName;

    // ── Session metadata (cached once at startup) ─────────────────
    private TableSessionEntry sessionMeta;
    private PlayerStats playerStats;
    private bool sessionInitialized;
    private bool participantRegistered; // track if we've self-registered

    // ── Buy-in & session summary tracking ─────────────────────────
    // Cumulative buy-ins for ALL players, keyed by displayName
    private readonly Dictionary<string, float> sessionBuyIns = new Dictionary<string, float>();

    // Local player's running session totals
    private float localTotalBuyIn;
    private float localSessionNetResult;
    private int   localSessionHandsPlayed;

    // ── In-memory log of completed hands (no DB queries needed) ──
    private readonly List<HandRecord> completedHands = new List<HandRecord>();

    // ── Cached summary for final-save safety net ─────────────────
    // Updated synchronously every time the P&L changes so that
    // OnDisable can persist the latest data even if the player has
    // already been removed from the game (hasLocalPlayer = false).
    private SessionPlayerSummary cachedSummary;
    private int   cachedPlayerCount;
    private bool  hasCachedSummary;

    // ── Save serialisation ─────────────────────────────────────
    // Tracks the in-flight Firebase write so FinalSaveAsync can
    // wait for it before writing the definitive latest data.
    private Task pendingSave;
    private bool finalSaveDone;

    // ── Auth mapping (host only — maps authPlayerId → displayName) ─
    private readonly Dictionary<string, string> authToDisplayName = new Dictionary<string, string>();

    // ── Departed players (host only) ──────────────────────────────
    // Players who left mid-session. After each hand the server
    // recalculates their P&L and persists it so Firebase stays in sync.
    [Serializable]
    private class DepartedPlayerInfo
    {
        public string authId;
        public string displayName;
        public int characterId;
    }
    private readonly List<DepartedPlayerInfo> departedPlayers = new List<DepartedPlayerInfo>();

    // ── Subscription state ────────────────────────────────────────
    private bool subscribedToEvents;

    // ── Static migration cache (survives scene reload) ──────────
    private static MigrationCache s_migrationCache;

    private class MigrationCache
    {
        public List<HandRecord> completedHands;
        public float localSessionNetResult;
        public int   localSessionHandsPlayed;
        public float localTotalBuyIn;
        public Dictionary<string, float> sessionBuyIns;
        public Dictionary<string, string> authToDisplayName;
        public TableSessionEntry sessionMeta;
        public PlayerStats playerStats;
        public bool participantRegistered;
        public SessionPlayerSummary cachedSummary;
        public int  cachedPlayerCount;
        public bool hasCachedSummary;
        public bool sessionInitialized;

        // In-progress hand state (so the remaining client can complete it)
        public HandRecord currentHand;
        public bool isRecording;
        public bool localPlayerExitedMidHand;
        public int  cachedLocalSlot;
        public string cachedLocalName;

        // Departed players (server keeps updating their summaries)
        public List<DepartedPlayerInfo> departedPlayers;
    }

    [Serializable]
    private class RejoinPayload
    {
        public List<HandRecord> completedHands;
        public float playerTotalBuyIn;
        public List<string> buyInNames;
        public List<float> buyInAmounts;
    }

    // ══════════════════════════════════════════════════════════════
    //  Unity lifecycle
    // ══════════════════════════════════════════════════════════════

    private void Awake()
    {
        BackendType backend = LobbyManager.Instance != null
            ? LobbyManager.Instance.Backend
            : BackendType.Firebase;

        isSharedBackend = (backend == BackendType.Firebase);

        switch (backend)
        {
            case BackendType.Firebase:
                storage = new FirestoreHandHistory();
                Debug.Log($"[{LogTag.Save}] Storage backend resolved — backend=Firebase Firestore", this);
                break;

            case BackendType.UnityServices:
            default:
                storage = new CloudSaveHandHistory();
                Debug.Log($"[{LogTag.Save}] Storage backend resolved — backend=Unity Cloud Save", this);
                break;
        }

        RestoreFromMigrationCache();
    }

    private void OnEnable()
    {
        TrySubscribe();
    }

    private void OnDisable()
    {
        TryUnsubscribe();

        if (enableRecording && HostMigrationManager.Instance != null &&
            HostMigrationManager.Instance.IsMigrationPending)
        {
            SaveToMigrationCache();
            return;
        }

        // If a hand is still in progress and the local player hasn't
        // already been finalised, count their pot investment as a loss.
        // This covers the host exiting mid-hand (no "Stand Up" RPC is
        // dispatched for the departing host).
        if (enableRecording && isRecording && currentHand != null && !localPlayerExitedMidHand)
        {
            int localSlot = cachedLocalSlot >= 0 ? cachedLocalSlot : GetLocalPlayerSlot();
            if (localSlot >= 0)
            {
                float invested = PlayerStatsTracker.CalculatePlayerInvestment(currentHand, localSlot);
                localSessionNetResult -= invested;
                localSessionHandsPlayed++;
                hasCachedSummary = true; // ensure FinalSaveAsync runs
                Debug.Log($"[{LogTag.Save}] OnDisable: finalizing mid-hand exit — invested=${invested:F2}, sessionNet=${localSessionNetResult:F2}", this);
            }
        }

        // Final safety-net save: persist the latest cached summary
        // so that data is never lost, even if a previous SaveAsync
        // was interrupted or raced with another write.
        if (enableRecording && (hasCachedSummary || sessionInitialized) && sessionMeta != null)
        {
            _ = FinalSaveAsync();
        }
    }

    private void Update()
    {
        if (!subscribedToEvents && enableRecording)
        {
            TrySubscribe();
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  Event subscription
    // ══════════════════════════════════════════════════════════════

    private void TrySubscribe()
    {
        if (subscribedToEvents) return;
        if (!enableRecording) return;
        if (GameManager.Instance == null) return;

        GameManager.Instance.OnHandStarted          += HandleHandStarted;
        GameManager.Instance.OnPlayerActionReceived  += HandlePlayerAction;
        GameManager.Instance.OnHandCompleted         += HandleHandCompleted;
        GameManager.Instance.OnVoluntaryCardsRevealed += HandleVoluntaryCardsRevealed;
        GameManager.Instance.OnPlayerBuyIn           += HandleBuyIn;
        GameManager.Instance.isGameStarted.OnValueChanged += HandleGameStartedChanged;
        subscribedToEvents = true;

        Debug.Log($"[{LogTag.Save}] Subscribed to GameManager events", this);
    }

    private void TryUnsubscribe()
    {
        if (!subscribedToEvents) return;
        if (GameManager.Instance == null) return;

        GameManager.Instance.OnHandStarted          -= HandleHandStarted;
        GameManager.Instance.OnPlayerActionReceived  -= HandlePlayerAction;
        GameManager.Instance.OnHandCompleted         -= HandleHandCompleted;
        GameManager.Instance.OnVoluntaryCardsRevealed -= HandleVoluntaryCardsRevealed;
        GameManager.Instance.OnPlayerBuyIn           -= HandleBuyIn;
        GameManager.Instance.isGameStarted.OnValueChanged -= HandleGameStartedChanged;
        subscribedToEvents = false;

        Debug.Log($"[{LogTag.Save}] Unsubscribed from GameManager events", this);
    }

    /// <summary>
    /// Called on ALL clients when isGameStarted changes.
    /// When the game ends (true → false), triggers a definitive save
    /// so the loser's net P&L is always persisted — even if the
    /// normal SaveAsync path was interrupted or raced with teardown.
    /// </summary>
    private void HandleGameStartedChanged(bool previousValue, bool newValue)
    {
        if (previousValue && !newValue && enableRecording && hasCachedSummary)
        {
            Debug.Log($"[{LogTag.Save}] Game ended — running final save — net=${localSessionNetResult:F2}, hands={localSessionHandsPlayed}", this);
            _ = FinalSaveAsync();
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  Session initialisation (lazy, first hand or first buy-in)
    // ══════════════════════════════════════════════════════════════

    private async void EnsureSessionInitialized()
    {
        if (sessionInitialized) return;
        sessionInitialized = true;

        string sessionId = GameManager.Instance != null
            ? GameManager.Instance.TableSessionId
            : "";

        if (string.IsNullOrEmpty(sessionId))
        {
            sessionId = Guid.NewGuid().ToString("N");
            Debug.LogWarning($"[{LogTag.Save}] Table session ID unavailable — generated fallback — id={sessionId}", this);
        }

        string tableName = "Unknown Table";
        float sb = 0f, bb = 0f, minBuy = 0f, maxBuy = 0f;

        if (LobbyManager.Instance != null)
        {
            tableName = LobbyManager.Instance.GetLobbyName();
            sb     = LobbyManager.Instance.GetSmallBlind();
            bb     = LobbyManager.Instance.GetBigBlind();
            minBuy = LobbyManager.Instance.GetMinBuyIn();
            maxBuy = LobbyManager.Instance.GetMaxBuyIn();
        }
        else if (GameManager.Instance != null)
        {
            sb = GameManager.Instance.smallBlind;
            bb = GameManager.Instance.bigBlind;
        }

        sessionMeta = new TableSessionEntry
        {
            sessionId  = sessionId,
            tableName  = tableName,
            status     = "active",
            smallBlind = sb,
            bigBlind   = bb,
            minBuyIn   = minBuy,
            maxBuyIn   = maxBuy,
        };

        // Load existing player stats
        try
        {
            playerStats = await storage.LoadPlayerStatsAsync();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[{LogTag.Save}] Player stats load failed — error={e.Message}", this);
            playerStats = new PlayerStats();
        }

        Debug.Log($"[{LogTag.Save}] Session initialized — id={sessionId}, table={tableName}, blinds=${sb:F2}/${bb:F2}, buyIn=${minBuy:F2}-${maxBuy:F2}", this);
    }

    // ══════════════════════════════════════════════════════════════
    //  Buy-in handler
    // ══════════════════════════════════════════════════════════════

    private void HandleBuyIn(int slotIndex, string playerName, float buyInAmount)
    {
        if (!enableRecording) return;

        EnsureSessionInitialized();

        // Track buy-in for ALL players (keyed by displayName to survive slot changes)
        if (!sessionBuyIns.ContainsKey(playerName))
            sessionBuyIns[playerName] = 0f;
        sessionBuyIns[playerName] += buyInAmount;

        // Host: track auth → name so we can rebuild history for returning players
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
        {
            string authId = GetAuthIdForSlot(slotIndex);
            if (!string.IsNullOrEmpty(authId))
            {
                authToDisplayName[authId] = playerName;
                Debug.Log($"[{LogTag.Save}] Auth mapped — authId={authId}, player={playerName}, totalMapped={authToDisplayName.Count}", this);
            }
            else
            {
                Debug.LogWarning($"[{LogTag.Save}] Auth mapping failed — slot={slotIndex}, player={playerName}, reason=GetAuthIdForSlot returned null", this);
            }
        }

        // Populate sessionMeta.playerSummaries so Cloud Save can use it
        UpdateSessionMetaSummary(playerName, buyInAmount);

        Debug.Log($"[{LogTag.Save}] Buy-in recorded — player={playerName}, amount=${buyInAmount:F2}, total=${sessionBuyIns[playerName]:F2}", this);

        // If this is the local player, persist the summary immediately
        int localSlot = GetLocalPlayerSlot();
        if (localSlot >= 0 && localSlot == slotIndex)
        {
            localTotalBuyIn = sessionBuyIns[playerName];
            SaveLocalSummaryAsync();
        }
    }

    /// <summary>
    /// Keeps sessionMeta.playerSummaries in sync with accumulated buy-in data.
    /// Cloud Save reads these when building session index entries.
    /// </summary>
    private void UpdateSessionMetaSummary(string playerName, float buyInDelta)
    {
        if (sessionMeta == null) return;

        var summary = sessionMeta.playerSummaries.Find(s => s.displayName == playerName);
        if (summary == null)
        {
            summary = new SessionPlayerSummary { displayName = playerName };
            sessionMeta.playerSummaries.Add(summary);
        }
        summary.totalBuyIn += buyInDelta;

        // Set playerId for the local player
        int localSlot = GetLocalPlayerSlot();
        if (localSlot >= 0)
        {
            string localName = GetDisplayNameBySlot(localSlot);
            if (localName == playerName)
            {
                summary.playerId = LobbyManager.Instance?.AuthProvider?.UserId ?? "";
            }
        }
    }

    /// <summary>
    /// Ensures ALL tracked buy-ins are reflected in sessionMeta.playerSummaries
    /// before a SaveHandAsync call (so Cloud Save can pick them up).
    /// </summary>
    private void SyncAllBuyInsToSessionMeta()
    {
        if (sessionMeta == null) return;

        foreach (var kvp in sessionBuyIns)
        {
            var summary = sessionMeta.playerSummaries.Find(s => s.displayName == kvp.Key);
            if (summary == null)
            {
                summary = new SessionPlayerSummary { displayName = kvp.Key };
                sessionMeta.playerSummaries.Add(summary);
            }
            summary.totalBuyIn = kvp.Value;
        }

        // Set playerId for local player
        int localSlot = GetLocalPlayerSlot();
        if (localSlot >= 0)
        {
            string localName = GetDisplayNameBySlot(localSlot);
            string uid = LobbyManager.Instance?.AuthProvider?.UserId ?? "";
            var localSummary = sessionMeta.playerSummaries.Find(s => s.displayName == localName);
            if (localSummary != null && string.IsNullOrEmpty(localSummary.playerId))
            {
                localSummary.playerId = uid;
            }
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  Event handlers
    // ══════════════════════════════════════════════════════════════

    private void HandleHandStarted(int handNumber, int dealerSlot, int sbSlot, int bbSlot,
        int[] playerSlots, float[] playerStacks)
    {
        if (!enableRecording) return;

        EnsureSessionInitialized();

        string sessionId = GameManager.Instance != null
            ? GameManager.Instance.TableSessionId
            : sessionMeta?.sessionId ?? "";

        currentHand = new HandRecord
        {
            handNumber      = handNumber,
            tableSessionId  = sessionId,
            timestampUtcMs  = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            smallBlind      = GameManager.Instance != null ? GameManager.Instance.smallBlind : 0f,
            bigBlind        = GameManager.Instance != null ? GameManager.Instance.bigBlind : 0f,
            dealerSlotIndex = dealerSlot,
            sbSlotIndex     = sbSlot,
            bbSlotIndex     = bbSlot,
            players         = new List<HandPlayerInfo>(),
            actions         = new List<HandAction>(),
            communityCardRanks = new List<int>(),
            communityCardSuits = new List<int>(),
            winners         = new List<HandWinner>(),
        };

        for (int i = 0; i < playerSlots.Length; i++)
        {
            currentHand.players.Add(new HandPlayerInfo
            {
                displayName  = GetDisplayNameBySlot(playerSlots[i]),
                slotIndex    = playerSlots[i],
                stackAtStart = playerStacks[i],
            });
        }

        isRecording = true;

        // Snapshot local identity while still valid — NetworkList removal
        // can race with the Stand Up / Disconnected RPC.
        cachedLocalSlot = GetLocalPlayerSlot();
        cachedLocalName = cachedLocalSlot >= 0 ? GetDisplayNameBySlot(cachedLocalSlot) : PlayerNameData.PlayerName;

        Debug.Log($"[{LogTag.Save}] Recording hand started — hand=#{handNumber}, players={currentHand.players.Count}", this);
    }

    private void HandlePlayerAction(int playerSlotIndex, string actionType, float amount, int phase)
    {
        if (!isRecording || currentHand == null) return;

        currentHand.actions.Add(new HandAction
        {
            phase           = phase,
            playerSlotIndex = playerSlotIndex,
            actionType      = actionType,
            amount          = amount,
        });

        // If the LOCAL player just left the hand mid-play (stand-up or
        // disconnect), their HandleHandCompleted will never fire.
        // Record their pot investment as a loss and save immediately
        // while hasLocalPlayer is still true (RemovePlayerSlotClientRpc
        // hasn't arrived yet).
        if (actionType == "Stand Up" || actionType == "Disconnected")
        {
            // Server: track the departing player so we can update their
            // Firebase summary after each subsequent hand completes.
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
            {
                TrackDepartedPlayer(playerSlotIndex);
            }

            // Use cached slot — GetLocalPlayerSlot() may already return -1
            // if the NetworkList removal arrived before this RPC.
            int localSlot = cachedLocalSlot >= 0 ? cachedLocalSlot : GetLocalPlayerSlot();
            if (localSlot >= 0 && playerSlotIndex == localSlot)
            {
                FinalizeLocalPlayerExit();
            }
        }
    }

    /// <summary>
    /// Called when the local player leaves mid-hand (stand-up or disconnect).
    /// Calculates their unfinished-hand investment as a loss, updates the
    /// running session totals, and triggers an immediate Firebase save.
    /// </summary>
    private void FinalizeLocalPlayerExit()
    {
        if (currentHand == null) return;

        int localSlot = cachedLocalSlot >= 0 ? cachedLocalSlot : GetLocalPlayerSlot();
        if (localSlot < 0) return;

        // The player won nothing — they folded / left.
        float invested = PlayerStatsTracker.CalculatePlayerInvestment(currentHand, localSlot);
        localSessionNetResult -= invested;
        localSessionHandsPlayed++;

        Debug.Log($"[{LogTag.Save}] Local player left mid-hand — invested=${invested:F2}, sessionNet=${localSessionNetResult:F2}", this);

        // Save the updated summary to Firebase immediately
        SaveLocalSummaryAsync();

        // Keep recording alive so HandleHandCompleted can finalize
        // the full hand record (winners, community cards, etc.).
        // The flag prevents double-counting the P&L we just saved.
        localPlayerExitedMidHand = true;
    }

    private void HandleHandCompleted(int[] winnerSlots, float[] winnerAmounts, float potTotal,
        int[] ccRanks, int[] ccSuits,
        int[] wCard1Ranks, int[] wCard1Suits, int[] wCard2Ranks, int[] wCard2Suits, bool cardsRevealed)
    {
        if (!isRecording || currentHand == null) return;

        isRecording = false;

        // Community cards
        if (ccRanks != null && ccSuits != null)
        {
            for (int i = 0; i < ccRanks.Length; i++)
            {
                currentHand.communityCardRanks.Add(ccRanks[i]);
                currentHand.communityCardSuits.Add(ccSuits[i]);
            }
        }

        // Derive final phase from community cards dealt
        currentHand.finalPhase = DerivePhase(ccRanks);

        // Winners
        if (winnerSlots != null && winnerAmounts != null)
        {
            for (int i = 0; i < winnerSlots.Length; i++)
            {
                var winner = new HandWinner
                {
                    slotIndex   = winnerSlots[i],
                    displayName = GetDisplayNameBySlot(winnerSlots[i]),
                    amount      = winnerAmounts[i],
                };

                if (wCard1Ranks != null && i < wCard1Ranks.Length)
                {
                    winner.card1Rank = wCard1Ranks[i];
                    winner.card1Suit = wCard1Suits[i];
                    winner.card2Rank = wCard2Ranks[i];
                    winner.card2Suit = wCard2Suits[i];
                }

                winner.cardsRevealed = WinnerHasVisibleCards(winner);

                currentHand.winners.Add(winner);
            }
        }

        currentHand.potTotal = potTotal;

        // Update local player stats + session running totals.
        // Skip if the local player already exited mid-hand (FinalizeLocalPlayerExit
        // already recorded their loss — updating again would double-count).
        if (localPlayerExitedMidHand)
        {
            localPlayerExitedMidHand = false;
        }
        else
        {
        int localSlot = GetLocalPlayerSlot();
        if (localSlot >= 0)
        {
            // Lifetime stats
            if (playerStats != null)
            {
                PlayerStatsTracker.UpdateStats(playerStats, currentHand, localSlot);
            }

            // Per-session running totals
            float invested = PlayerStatsTracker.CalculatePlayerInvestment(currentHand, localSlot);
            float won = 0f;
            foreach (var w in currentHand.winners)
            {
                if (w.slotIndex == localSlot) won += w.amount;
            }
            localSessionNetResult += won - invested;
            localSessionHandsPlayed++;
            }
        }

        Debug.Log($"[{LogTag.Save}] Hand completed — hand=#{currentHand.handNumber}, winners={currentHand.winners.Count}, pot=${potTotal:F2}", this);

        // Keep an in-memory copy so the UI can display it without DB queries
        completedHands.Add(currentHand);

        SaveAsync(currentHand);

        // Server: update Firebase summaries for players who left the session
        UpdateDepartedPlayerSummaries();

        currentHand = null;
    }

    private void HandleVoluntaryCardsRevealed(int slotIndex, Card card1, Card card2, bool revealCard1, bool revealCard2)
    {
        if (!enableRecording || completedHands.Count == 0)
            return;

        HandRecord hand = completedHands[completedHands.Count - 1];
        if (!UpdateWinnerCardsInHand(hand, slotIndex, card1, card2, revealCard1, revealCard2))
            return;

        Debug.Log($"[{LogTag.Save}] Voluntary reveal applied to hand history — hand=#{hand.handNumber}, slot={slotIndex}", this);
        ResaveHandAsync(hand);
        OnHandHistoryUpdated?.Invoke();
    }

    private static bool UpdateWinnerCardsInHand(HandRecord hand, int slotIndex, Card card1, Card card2, bool revealCard1, bool revealCard2)
    {
        if (hand.winners == null || hand.winners.Count == 0)
            return false;

        bool updated = false;
        foreach (var winner in hand.winners)
        {
            if (winner.slotIndex != slotIndex)
                continue;

            if (revealCard1)
            {
                winner.card1Rank = (int)card1.rank;
                winner.card1Suit = (int)card1.suit;
            }

            if (revealCard2)
            {
                winner.card2Rank = (int)card2.rank;
                winner.card2Suit = (int)card2.suit;
            }

            winner.cardsRevealed = WinnerHasVisibleCards(winner);
            updated = true;
        }

        return updated;
    }

    private static bool WinnerHasVisibleCards(HandWinner winner)
    {
        return IsStoredCardVisible(winner.card1Rank, winner.card1Suit)
            || IsStoredCardVisible(winner.card2Rank, winner.card2Suit);
    }

    private static bool IsStoredCardVisible(int rank, int suit)
    {
        return rank != (int)Card.Rank.Hidden && rank != (int)Card.Rank.None
            && suit != (int)Card.Suit.Hidden && suit != (int)Card.Suit.None;
    }

    private async void ResaveHandAsync(HandRecord hand)
    {
        try
        {
            bool shouldSaveHand = !isSharedBackend || IsHost();
            if (shouldSaveHand)
                await storage.SaveHandAsync(hand, sessionMeta);
        }
        catch (Exception e)
        {
            Debug.LogError($"[{LogTag.Save}] Hand re-save failed after voluntary reveal — hand=#{hand.handNumber}, error={e.Message}", this);
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  Persistence (async, fire-and-forget with error logging)
    // ══════════════════════════════════════════════════════════════

    private async void SaveAsync(HandRecord hand)
    {
        try
        {
            // Sync all tracked buy-ins into sessionMeta before saving
            SyncAllBuyInsToSessionMeta();

            // ── Session player summary FIRST ──────────────────────
            // Must run before any other await so that GetLocalPlayerSlot()
            // and GetDisplayNameBySlot() capture valid data synchronously.
            // After a bust-out or stand-up, RemovePlayerSlotClientRpc
            // sets hasLocalPlayer = false, which would cause
            // SaveLocalSummaryInternalAsync to skip if called later.
            await SaveLocalSummaryInternalAsync();

            // ── Hand data ────────────────────────────────────────
            // Firestore: only the host writes to /sessions/{id}/hands/{num}
            // Cloud Save: every client writes their own copy
            bool shouldSaveHand = !isSharedBackend || IsHost();

            if (shouldSaveHand)
            {
                await storage.SaveHandAsync(hand, sessionMeta);
            }

            // ── Player stats ─────────────────────────────────────
            // ALL clients save their own stats
            if (playerStats != null)
            {
                await storage.SavePlayerStatsAsync(playerStats);
            }

            // ── Session participant registration (Firestore only) ─
            // Each client self-registers once per session
            if (isSharedBackend && !participantRegistered)
            {
                participantRegistered = true;
                await storage.RegisterSessionParticipantAsync(hand.tableSessionId, sessionMeta);
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[{LogTag.Save}] Hand save failed — hand=#{hand.handNumber}, error={e.Message}. Check storage backend connectivity.", this);
        }
    }

    /// <summary>
    /// Fire-and-forget wrapper for writing the local player's session summary.
    /// Called from HandleBuyIn (outside the SaveAsync flow).
    /// </summary>
    private async void SaveLocalSummaryAsync()
    {
        try
        {
            await SaveLocalSummaryInternalAsync();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[{LogTag.Save}] Session summary save failed on buy-in — error={e.Message}", this);
        }
    }

    /// <summary>
    /// Builds the local player's SessionPlayerSummary and writes it to storage.
    /// Also caches the data so that OnDisable can re-save it as a safety net.
    /// </summary>
    private async Task SaveLocalSummaryInternalAsync()
    {
        if (sessionMeta == null) return;

        int localSlot = GetLocalPlayerSlot();
        if (localSlot < 0) localSlot = cachedLocalSlot;
        if (localSlot < 0) return;

        string uid  = LobbyManager.Instance?.AuthProvider?.UserId ?? "";
        string name = GetDisplayNameBySlot(localSlot);
        if (name == "Unknown" && !string.IsNullOrEmpty(cachedLocalName))
            name = cachedLocalName;

        int charId = PlayerAssets.Instance != null ? PlayerAssets.Instance.currentCharacterId : 0;

        var summary = new SessionPlayerSummary
        {
            playerId    = uid,
            displayName = name,
            characterId = charId,
            totalBuyIn  = localTotalBuyIn,
            netResult   = localSessionNetResult,
            handsPlayed = localSessionHandsPlayed,
        };

        // Pass the total unique player count for this session
        int playerCount = sessionBuyIns.Count;

        // Cache synchronously so FinalSaveAsync / OnDisable can re-save
        // even if hasLocalPlayer has already been cleared.
        cachedSummary     = summary;
        cachedPlayerCount = playerCount;
        hasCachedSummary  = true;

        // Track the in-flight write so FinalSaveAsync can wait for it.
        var writeTask = storage.UpdateSessionPlayerSummaryAsync(
            sessionMeta.sessionId, summary, playerCount);
        pendingSave = writeTask;
        await writeTask;
    }

    /// <summary>
    /// Definitive save that fires when the game ends or the recorder
    /// is disabled.  Waits for any in-flight write to finish first,
    /// then writes the absolute latest running totals.  This prevents
    /// a stale concurrent write from overwriting the correct data.
    /// </summary>
    private async Task FinalSaveAsync()
    {
        if (finalSaveDone) return;     // only run once
        finalSaveDone = true;

        try
        {
            if (storage == null || sessionMeta == null) return;

            // Wait for any in-flight save to finish first so we don't
            // race with it and end up with the older value "winning".
            if (pendingSave != null)
            {
                try { await pendingSave; } catch { /* swallow — we'll overwrite anyway */ }
            }

            // Build a summary from scratch if no prior save cached one
            // (e.g., player exits during their very first hand).
            if (cachedSummary == null)
            {
                string uid  = LobbyManager.Instance?.AuthProvider?.UserId ?? "";
                string name = !string.IsNullOrEmpty(cachedLocalName) ? cachedLocalName : PlayerNameData.PlayerName;
                int charId  = PlayerAssets.Instance != null ? PlayerAssets.Instance.currentCharacterId : 0;

                cachedSummary = new SessionPlayerSummary
                {
                    playerId    = uid,
                    displayName = name,
                    characterId = charId,
                };
                cachedPlayerCount = sessionBuyIns.Count;
            }

            // Stamp the absolute latest running totals.
            cachedSummary.totalBuyIn  = localTotalBuyIn;
            cachedSummary.netResult   = localSessionNetResult;
            cachedSummary.handsPlayed = localSessionHandsPlayed;

            // Fix display name if an earlier save wrote "Unknown"
            if (cachedSummary.displayName == "Unknown" && !string.IsNullOrEmpty(cachedLocalName))
                cachedSummary.displayName = cachedLocalName;

            Debug.Log($"[{LogTag.Save}] Final save — net=${localSessionNetResult:F2}, hands={localSessionHandsPlayed}", this);

            await storage.UpdateSessionPlayerSummaryAsync(
                sessionMeta.sessionId, cachedSummary, cachedPlayerCount);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[{LogTag.Save}] Final save failed — error={e.Message}", this);
        }
    }

    /// <summary>
    /// Called by ExitHandler BEFORE the scene transition so the Firebase
    /// write can complete while the scene and networking are still alive.
    /// OnDisable's FinalSaveAsync acts as a fallback but is fire-and-forget
    /// during scene teardown, which can lose writes.
    /// </summary>
    public async Task FinalizeAndSaveBeforeExit()
    {
        if (!enableRecording || sessionMeta == null) return;

        // Deduct mid-hand investment if needed (same logic as OnDisable)
        if (isRecording && currentHand != null && !localPlayerExitedMidHand)
        {
            int localSlot = cachedLocalSlot >= 0 ? cachedLocalSlot : GetLocalPlayerSlot();
            if (localSlot >= 0)
            {
                float invested = PlayerStatsTracker.CalculatePlayerInvestment(currentHand, localSlot);
                localSessionNetResult -= invested;
                localSessionHandsPlayed++;
                hasCachedSummary = true;
                Debug.Log($"[{LogTag.Save}] Pre-exit finalize — invested=${invested:F2}, sessionNet=${localSessionNetResult:F2}", this);
            }
            isRecording = false;
        }

        await FinalSaveAsync();
    }

    // ══════════════════════════════════════════════════════════════
    //  Host-migration state preservation
    // ══════════════════════════════════════════════════════════════

    private void SaveToMigrationCache()
    {
        s_migrationCache = new MigrationCache
        {
            completedHands        = new List<HandRecord>(completedHands),
            localSessionNetResult = localSessionNetResult,
            localSessionHandsPlayed = localSessionHandsPlayed,
            localTotalBuyIn       = localTotalBuyIn,
            sessionBuyIns         = new Dictionary<string, float>(sessionBuyIns),
            authToDisplayName     = new Dictionary<string, string>(authToDisplayName),
            sessionMeta           = sessionMeta,
            playerStats           = playerStats,
            participantRegistered = participantRegistered,
            cachedSummary         = cachedSummary,
            cachedPlayerCount     = cachedPlayerCount,
            hasCachedSummary      = hasCachedSummary,
            sessionInitialized    = sessionInitialized,
            currentHand           = currentHand,
            isRecording           = isRecording,
            localPlayerExitedMidHand = localPlayerExitedMidHand,
            cachedLocalSlot       = cachedLocalSlot,
            cachedLocalName       = cachedLocalName,
            departedPlayers       = new List<DepartedPlayerInfo>(departedPlayers),
        };

        Debug.Log($"[{LogTag.Save}] Migration cache saved — hands={completedHands.Count}, net=${localSessionNetResult:F2}, handsPlayed={localSessionHandsPlayed}, midHand={isRecording}, departed={departedPlayers.Count}", this);
    }

    private void RestoreFromMigrationCache()
    {
        if (s_migrationCache == null) return;

        var c = s_migrationCache;
        s_migrationCache = null;

        completedHands.AddRange(c.completedHands);
        localSessionNetResult   = c.localSessionNetResult;
        localSessionHandsPlayed = c.localSessionHandsPlayed;
        localTotalBuyIn         = c.localTotalBuyIn;

        foreach (var kvp in c.sessionBuyIns)
            sessionBuyIns[kvp.Key] = kvp.Value;
        foreach (var kvp in c.authToDisplayName)
            authToDisplayName[kvp.Key] = kvp.Value;

        sessionMeta           = c.sessionMeta;
        playerStats           = c.playerStats;
        participantRegistered = c.participantRegistered;
        cachedSummary         = c.cachedSummary;
        cachedPlayerCount     = c.cachedPlayerCount;
        hasCachedSummary      = c.hasCachedSummary;
        sessionInitialized    = c.sessionInitialized;
        finalSaveDone         = false;

        // Restore in-progress hand so HandleHandCompleted can finalize it
        currentHand              = c.currentHand;
        isRecording              = c.isRecording;
        localPlayerExitedMidHand = c.localPlayerExitedMidHand;
        cachedLocalSlot          = c.cachedLocalSlot;
        cachedLocalName          = c.cachedLocalName;

        if (c.departedPlayers != null)
            departedPlayers.AddRange(c.departedPlayers);

        Debug.Log($"[{LogTag.Save}] Migration cache restored — hands={completedHands.Count}, net=${localSessionNetResult:F2}, handsPlayed={localSessionHandsPlayed}, midHand={isRecording}, departed={departedPlayers.Count}", this);
    }

    // ══════════════════════════════════════════════════════════════
    //  Rejoin history transfer (host → returning client)
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Called by HostMigrationManager.RestoreGameState after migration so the
    /// new host can resolve returning players by authId.  The snapshot
    /// contains authPlayerId + playerName for ALL players (including the
    /// departing host), which is exactly what we need.
    /// </summary>
    public void SeedAuthMappingsFromSnapshot(List<GameStateSnapshot.PlayerSnapshot> players)
    {
        foreach (var ps in players)
        {
            if (!string.IsNullOrEmpty(ps.authPlayerId) && !string.IsNullOrEmpty(ps.playerName))
            {
                authToDisplayName[ps.authPlayerId] = ps.playerName;
            }
        }
        Debug.Log($"[{LogTag.Save}] Auth mappings seeded from snapshot — total={authToDisplayName.Count}", this);
    }

    /// <summary>
    /// Host only. Builds a serialised payload for a returning player so they
    /// can restore their hand history and P&amp;L.  Returns null if the
    /// authId was never seen in this session.
    /// </summary>
    public byte[] TryBuildRejoinPayload(string authId)
    {
        if (!authToDisplayName.TryGetValue(authId, out string displayName))
        {
            Debug.Log($"[{LogTag.Save}] Rejoin lookup miss — authId={authId}, knownAuths={authToDisplayName.Count}, hands={completedHands.Count}", this);
            return null;
        }

        var payload = new RejoinPayload
        {
            completedHands  = new List<HandRecord>(completedHands),
            playerTotalBuyIn = sessionBuyIns.ContainsKey(displayName) ? sessionBuyIns[displayName] : 0f,
            buyInNames  = new List<string>(sessionBuyIns.Keys),
            buyInAmounts = new List<float>(sessionBuyIns.Values),
        };

        string json = UnityEngine.JsonUtility.ToJson(payload);
        byte[] data = System.Text.Encoding.UTF8.GetBytes(json);
        Debug.Log($"[{LogTag.Save}] Rejoin payload built — authId={authId}, player={displayName}, hands={completedHands.Count}, size={data.Length}B", this);
        return data;
    }

    /// <summary>
    /// Client only. Restores hand history and running totals from a payload
    /// sent by the host after reconnecting to an existing session.
    /// </summary>
    public void RestoreFromRejoinPayload(byte[] data)
    {
        if (data == null || data.Length == 0) return;

        string json = System.Text.Encoding.UTF8.GetString(data);
        var payload = UnityEngine.JsonUtility.FromJson<RejoinPayload>(json);
        if (payload == null) return;

        completedHands.Clear();
        if (payload.completedHands != null)
            completedHands.AddRange(payload.completedHands);

        localTotalBuyIn = payload.playerTotalBuyIn;

        sessionBuyIns.Clear();
        if (payload.buyInNames != null && payload.buyInAmounts != null)
        {
            for (int i = 0; i < payload.buyInNames.Count && i < payload.buyInAmounts.Count; i++)
                sessionBuyIns[payload.buyInNames[i]] = payload.buyInAmounts[i];
        }

        // Recalculate local player P&L from the hand records
        string localName = GetLocalDisplayName();
        localSessionNetResult = 0f;
        localSessionHandsPlayed = 0;
        foreach (var hand in completedHands)
        {
            int slot = FindSlotForPlayer(hand, localName);
            if (slot < 0) continue;

            localSessionHandsPlayed++;
            float invested = PlayerStatsTracker.CalculatePlayerInvestment(hand, slot);
            float won = 0f;
            foreach (var w in hand.winners)
            {
                if (w.slotIndex == slot) won += w.amount;
            }
            localSessionNetResult += won - invested;
        }

        Debug.Log($"[{LogTag.Save}] Rejoin payload restored — hands={completedHands.Count}, net=${localSessionNetResult:F2}, handsPlayed={localSessionHandsPlayed}, buyIn=${localTotalBuyIn:F2}", this);
    }

    private string GetLocalDisplayName()
    {
        string name = PlayerNameData.PlayerName;
        if (!string.IsNullOrEmpty(name)) return name;

        int localSlot = GetLocalPlayerSlot();
        return localSlot >= 0 ? GetDisplayNameBySlot(localSlot) : "Unknown";
    }

    private static int FindSlotForPlayer(HandRecord hand, string displayName)
    {
        foreach (var p in hand.players)
        {
            if (p.displayName == displayName) return p.slotIndex;
        }
        return -1;
    }

    // ══════════════════════════════════════════════════════════════
    //  Departed-player tracking (host only)
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Server: captures a player's identity before they're removed from the game.
    /// Called from HandlePlayerAction when a Stand Up / Disconnected action arrives.
    /// </summary>
    private void TrackDepartedPlayer(int slotIndex)
    {
        string authId = GetAuthIdForSlot(slotIndex);
        string name   = GetDisplayNameBySlot(slotIndex);
        int charId    = GetCharacterIdBySlot(slotIndex);

        if (string.IsNullOrEmpty(authId) || string.IsNullOrEmpty(name) || name == "Unknown")
        {
            Debug.LogWarning($"[{LogTag.Save}] Cannot track departed player — slot={slotIndex}, authId={authId ?? "<null>"}, name={name}", this);
            return;
        }

        // Avoid duplicates (player may stand up and rejoin multiple times)
        for (int i = 0; i < departedPlayers.Count; i++)
        {
            if (departedPlayers[i].authId == authId)
            {
                departedPlayers[i].displayName = name;
                departedPlayers[i].characterId = charId;
                Debug.Log($"[{LogTag.Save}] Departed player updated — player={name}, authId={authId}", this);
                return;
            }
        }

        departedPlayers.Add(new DepartedPlayerInfo
        {
            authId      = authId,
            displayName = name,
            characterId = charId,
        });
        Debug.Log($"[{LogTag.Save}] Departed player tracked — player={name}, authId={authId}, total={departedPlayers.Count}", this);
    }

    /// <summary>
    /// Called from HostMigrationManager.RestoreGameState to register the
    /// departing host as a departed player (no Stand Up RPC is dispatched
    /// for the host during migration).
    /// </summary>
    public void TrackDepartedPlayerFromSnapshot(string authId, string displayName, int characterId)
    {
        if (string.IsNullOrEmpty(authId)) return;

        for (int i = 0; i < departedPlayers.Count; i++)
        {
            if (departedPlayers[i].authId == authId) return; // already tracked
        }

        departedPlayers.Add(new DepartedPlayerInfo
        {
            authId      = authId,
            displayName = displayName,
            characterId = characterId,
        });
        Debug.Log($"[{LogTag.Save}] Departed host tracked from snapshot — player={displayName}, authId={authId}", this);
    }

    /// <summary>
    /// Server only. Recalculates and persists the session summary for every
    /// player who left mid-session. Called after each hand is added to
    /// completedHands so their Firebase entry stays in sync.
    /// </summary>
    private async void UpdateDepartedPlayerSummaries()
    {
        if (!IsHost() || departedPlayers.Count == 0 || sessionMeta == null) return;

        int playerCount = sessionBuyIns.Count;

        foreach (var dp in departedPlayers)
        {
            float netResult   = 0f;
            int   handsPlayed = 0;

            foreach (var hand in completedHands)
            {
                int slot = FindSlotForPlayer(hand, dp.displayName);
                if (slot < 0) continue;

                handsPlayed++;
                float invested = PlayerStatsTracker.CalculatePlayerInvestment(hand, slot);
                float won = 0f;
                foreach (var w in hand.winners)
                {
                    if (w.slotIndex == slot) won += w.amount;
                }
                netResult += won - invested;
            }

            float totalBuyIn = sessionBuyIns.ContainsKey(dp.displayName)
                ? sessionBuyIns[dp.displayName]
                : 0f;

            var summary = new SessionPlayerSummary
            {
                playerId    = dp.authId,
                displayName = dp.displayName,
                characterId = dp.characterId,
                totalBuyIn  = totalBuyIn,
                netResult   = netResult,
                handsPlayed = handsPlayed,
            };

            try
            {
                await storage.UpdateSessionPlayerSummaryAsync(
                    sessionMeta.sessionId, summary, playerCount);
                Debug.Log($"[{LogTag.Save}] Departed player summary synced — player={dp.displayName}, net=${netResult:F2}, hands={handsPlayed}", this);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{LogTag.Save}] Departed player summary sync failed — player={dp.displayName}, error={e.Message}", this);
            }
        }
    }

    private int GetCharacterIdBySlot(int slotIndex)
    {
        if (GameManager.Instance == null) return 0;
        foreach (var p in GameManager.Instance.players)
        {
            if (p.realSlotIndex == slotIndex) return p.iconIndex;
        }
        return 0;
    }

    // ══════════════════════════════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════════════════════════════

    private bool IsHost()
    {
        return NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;
    }

    private string GetAuthIdForSlot(int slotIndex)
    {
        if (GameManager.Instance == null) return null;
        var slotToClient = GameManager.Instance.GetSlotToClientId();
        if (!slotToClient.TryGetValue(slotIndex, out ulong clientId)) return null;
        var clientToAuth = GameManager.Instance.GetClientIdToAuthPlayerId();
        clientToAuth.TryGetValue(clientId, out string authId);
        return authId;
    }

    private string GetDisplayNameBySlot(int slotIndex)
    {
        if (GameManager.Instance == null) return "Unknown";

        foreach (var p in GameManager.Instance.players)
        {
            if (p.realSlotIndex == slotIndex)
                return p.playerName.ToString();
        }

        return "Unknown";
    }

    private int GetLocalPlayerSlot()
    {
        if (GameManager.Instance == null || !GameManager.Instance.hasLocalPlayer)
            return -1;

        return GameManager.Instance.localPlayer.realSlotIndex;
    }

    /// <summary>
    /// Derives the final phase name from the community cards dealt.
    /// </summary>
    private static string DerivePhase(int[] ccRanks)
    {
        if (ccRanks == null) return "preflop";

        int dealt = 0;
        for (int i = 0; i < ccRanks.Length; i++)
        {
            if (ccRanks[i] != 0) dealt++;
        }

        switch (dealt)
        {
            case 0:  return "preflop";
            case 3:  return "flop";
            case 4:  return "turn";
            case 5:  return "river";
            default: return "unknown";
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  Public API — for UI / debugging
    // ══════════════════════════════════════════════════════════════

    public async Task<HandHistoryIndex> LoadIndexAsync()
    {
        return await storage.LoadIndexAsync();
    }

    public async Task<SessionHandData> LoadSessionHandsAsync(string sessionId)
    {
        return await storage.LoadSessionHandsAsync(sessionId);
    }

    public async Task<PlayerStats> LoadPlayerStatsAsync()
    {
        return await storage.LoadPlayerStatsAsync();
    }

    public TableSessionEntry GetCurrentSessionMeta() => sessionMeta;

    public PlayerStats GetCachedPlayerStats() => playerStats;

    /// <summary>All hands completed this session, in chronological order. No DB query needed.</summary>
    public IReadOnlyList<HandRecord> CompletedHands => completedHands;

    /// <summary>Fired when a completed hand record is updated after the initial save (e.g. voluntary card reveal).</summary>
    public event Action OnHandHistoryUpdated;

    /// <summary>Local player's running net profit/loss for this session.</summary>
    public float LocalSessionNetResult => localSessionNetResult;

    /// <summary>Table name for the current session.</summary>
    public string TableName => sessionMeta?.tableName ?? "Unknown Table";

    public string GetActiveBackendName()
    {
        if (storage is FirestoreHandHistory) return "Firebase Firestore";
        if (storage is CloudSaveHandHistory) return "Unity Cloud Save";
        return "Unknown";
    }
}
