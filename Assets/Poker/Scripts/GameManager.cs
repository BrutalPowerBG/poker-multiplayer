using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Netcode;
using Unity.Services.Authentication;
using UnityEngine;

public class GameManager : NetworkBehaviour
{
    public static GameManager Instance { get; private set; }

    /// <summary>Fired once when Instance is assigned in Awake, so late subscribers can hook up.</summary>
    public static event Action OnInstanceReady;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            // If the old Instance was destroyed (scene reload) but C# ref lingers, take over
            if (Instance.gameObject == null)
            {
                Debug.LogWarning($"[{LogTag.Game}] Stale singleton replaced — old instance was destroyed but reference lingered", this);
                Instance = this;
            }
            else
            {
                Destroy(gameObject);
                return;
            }
        }
        else
        {
            Instance = this;
        }

        OnInstanceReady?.Invoke();

        // Read game settings from lobby (set by host in lobby)
        if (LobbyManager.Instance != null) {
            smallBlind = LobbyManager.Instance.GetSmallBlind();
            bigBlind = LobbyManager.Instance.GetBigBlind();
            maxBuyIn = LobbyManager.Instance.GetMaxBuyIn();
            sessionAvailableBalance = LobbyManager.Instance.GetSessionAvailableBalance();
            unlimitedAvailableBalance = LobbyManager.Instance.IsUnlimitedAvailableBalance();
            playerTurnTimeLimit = LobbyManager.Instance.GetTurnTimer();
            playerTimeBankTotal = LobbyManager.Instance.GetTimeBank();
        } else {
            smallBlind = LobbyManager.DEFAULT_SMALL_BLIND;
            bigBlind = LobbyManager.DEFAULT_BIG_BLIND;
            maxBuyIn = LobbyManager.DEFAULT_MAX_BUY_IN;
            sessionAvailableBalance = LobbyManager.DEFAULT_SESSION_AVAILABLE_BALANCE;
            unlimitedAvailableBalance = LobbyManager.DEFAULT_UNLIMITED_AVAILABLE_BALANCE;
            playerTurnTimeLimit = LobbyManager.DEFAULT_TURN_TIMER;
            playerTimeBankTotal = LobbyManager.DEFAULT_TIME_BANK;
        }

        localAvailableBalance = sessionAvailableBalance;
    }
    public NetworkVariable<int> playersCount = new NetworkVariable<int>(0);
    public int maxPlayersCount = 9;
    public NetworkVariable<bool> isGameStarted = new NetworkVariable<bool>(false);

    // Server-only latch that guards StartGameServerRpc against re-entrant/double
    // starts during the brief window before the first GameLoop runs.
    private bool gameStarting;

    // Server-only latch that is true from the moment a hand's GameLoop begins
    // until that hand is cleaned up (CleanupAndStartNextRound) or the game ends.
    // Prevents two overlapping hand state machines from running on the same
    // player list (symptom: positions/blinds logged twice, activePlayers=0 at
    // every phase, pot never awarded).
    private bool handInProgress;

    // Server-only deck - never synchronized to clients to prevent cheating
    private List<Card> deck;
    
    // Server-side mapping of slot index to client ID (for sending cards privately)
    private Dictionary<int, ulong> slotToClientId = new Dictionary<int, ulong>();
    
    // Server-side mapping of Netcode clientId to Lobby authPlayerId (for seated players only — kick from lobby on disconnect)
    private Dictionary<ulong, string> clientIdToAuthPlayerId = new Dictionary<ulong, string>();
    
    // Server-side: ALL connected Netcode clients (seated + spectators).
    // Populated via RegisterConnectedClientServerRpc on spawn; cleaned up on disconnect.
    // Used by host migration to find a new host and to know which clients to expect.
    private Dictionary<ulong, string> connectedClients = new Dictionary<ulong, string>();

    public NetworkVariable<float> potOnTable = new NetworkVariable<float>(0);
    public float smallBlind;
    public float bigBlind;
    public float maxBuyIn;
    private float sessionAvailableBalance = LobbyManager.DEFAULT_SESSION_AVAILABLE_BALANCE;
    private bool unlimitedAvailableBalance = LobbyManager.DEFAULT_UNLIMITED_AVAILABLE_BALANCE;
    public NetworkList<PokerPlayer> players = new NetworkList<PokerPlayer>();

    // Server-side: authId -> remaining buy-in balance for this session.
    private Dictionary<string, float> availableBalanceByAuthPlayerId = new Dictionary<string, float>();
    // Server-side: authId -> add-cash request queued for next round start.
    private Dictionary<string, float> pendingAddCashByAuthPlayerId = new Dictionary<string, float>();
    // Client-side: local player's latest available balance snapshot (for BuyIn UI).
    private float localAvailableBalance = LobbyManager.DEFAULT_SESSION_AVAILABLE_BALANCE;
    public float LocalAvailableBalance => localAvailableBalance;
    public bool IsUnlimitedAvailableBalance => unlimitedAvailableBalance;
    public event Action<float, bool> OnLocalAvailableBalanceChanged;

    public PokerPlayer dealerPlayer;
    public PokerPlayer smallBlindPlayer;
    public PokerPlayer bigBlindPlayer;

    public float collectBetAnimationDelay = 0.8f;
    public float moveToNextPhaseAfterAllInDelay = 2.4f;

    [Header("Showdown Timing")]
    [Tooltip("Seconds to wait after the last bet is collected before evaluating hands and awarding the pot.")]
    public float showdownEvaluationDelay = 1.0f;

    [Tooltip("Seconds each card-flip animation takes. Total reveal time = players × this + showdownRevealBaseTime.")]
    public float showdownRevealTimePerPlayer = 0.3f;

    [Tooltip("Base seconds added to the card reveal wait regardless of player count. Scaled up by pot size (see below).")]
    public float showdownRevealBaseTime = 1.0f;

    [Tooltip("Seconds players can observe the final result (pot awarded, cards visible) before the next round starts. Scaled up by pot size (see below).")]
    public float showdownObserveTime = 2f;

    [Tooltip("Seconds to pause between awarding each side pot during a multi-pot showdown.")]
    public float sidePotAwardInterval = 1.2f;

    [Tooltip("Pot threshold (in big blinds) below which no scaling is applied to reveal/observe times.")]
    public float potScaleMinBB = 20f;

    [Tooltip("Pot threshold (in big blinds) at which the maximum scale multiplier is reached.")]
    public float potScaleMaxBB = 200f;

    [Tooltip("Maximum multiplier applied to showdownRevealBaseTime and showdownObserveTime when the pot is at or above potScaleMaxBB.")]
    public float potScaleMaxMultiplier = 2f;
    private bool revealedPlayerCards = false;

    // Voluntary hole-card reveal (after pot award, before next hand)
    private bool voluntaryRevealWindowOpen;
    private readonly Dictionary<int, bool> card1RevealedToOthersBySlot = new Dictionary<int, bool>();
    private readonly Dictionary<int, bool> card2RevealedToOthersBySlot = new Dictionary<int, bool>();

    public const int VoluntaryRevealCard1 = 1;
    public const int VoluntaryRevealCard2 = 2;
    public const int VoluntaryRevealBoth = 3;

    public bool VoluntaryRevealWindowActive { get; private set; }
    public bool ShowVoluntaryRevealCard1Button { get; private set; }
    public bool ShowVoluntaryRevealCard2Button { get; private set; }
    public bool ShowVoluntaryRevealBothButton { get; private set; }
    public event Action OnVoluntaryRevealStateChanged;

    // Betting round tracking
    public NetworkVariable<int> currentPlayerTurnIndex = new NetworkVariable<int>(-1); // Index in players list
    public NetworkVariable<float> currentBetToCall = new NetworkVariable<float>(0);     // Current highest bet
    public NetworkVariable<float> lastRaiseAmount = new NetworkVariable<float>(0);
    public NetworkVariable<bool> wasRaiseThisRound = new NetworkVariable<bool>(false);  // True if someone raised
    private int lastRaiserIndex = -1;  // Track who raised last (server-side only)
    private int currentDealerIndex = 0; // Current dealer position index (server-side only)
    private int currentDealerRealSlotIndex = -1; // Real slot index of current dealer (survives list removals)
    
    // Server-side turn timer
    public float playerTurnTimeLimit = 15f; // Time limit for each player's turn in seconds
    public float playerTimeBankTotal = 30f; // Total time bank per player for the session
    private Coroutine currentTurnTimerCoroutine; // Reference to the active turn timer
    
    // Server-side time bank tracking (keyed by realSlotIndex)
    private Dictionary<int, float> timeBankBySlotIndex = new Dictionary<int, float>();
    
    // Community cards (5 cards for flop, turn, river)
    public NetworkVariable<Card> communityCard1 = new NetworkVariable<Card>(new Card(Card.Rank.None, Card.Suit.None));
    public NetworkVariable<Card> communityCard2 = new NetworkVariable<Card>(new Card(Card.Rank.None, Card.Suit.None));
    public NetworkVariable<Card> communityCard3 = new NetworkVariable<Card>(new Card(Card.Rank.None, Card.Suit.None));
    public NetworkVariable<Card> communityCard4 = new NetworkVariable<Card>(new Card(Card.Rank.None, Card.Suit.None));
    public NetworkVariable<Card> communityCard5 = new NetworkVariable<Card>(new Card(Card.Rank.None, Card.Suit.None));
    
    // Game phase tracking
    public enum GamePhase
    {
        PreFlop,    // After dealing hole cards, before flop
        Flop,       // After flop is dealt (3 cards)
        Turn,       // After turn is dealt (4th card)
        River,      // After river is dealt (5th card)
        Showdown    // Revealing hands and determining winner
    }
    public NetworkVariable<GamePhase> currentPhase = new NetworkVariable<GamePhase>(GamePhase.PreFlop);
    
    [HideInInspector] public PokerPlayer localPlayer; // The player on this client
    [HideInInspector] public bool hasLocalPlayer = false;
    
    // Set during host migration to tell InitializeClientState to wait for the host's
    // ApplyMigrationState to set up the local player, rather than building from scratch.
    private bool isMigrationReconnect = false;

    // ==================== HAND HISTORY EVENTS ====================
    // C# events fired on ALL clients (inside ClientRpcs) for hand recording.
    // HandRecorder subscribes to these — they are intentionally decoupled
    // from gameplay so the recording system can be disabled without side effects.

    /// <summary>
    /// Fired on all clients at the start of each new hand.
    /// Args: handNumber, dealerSlot, sbSlot, bbSlot, playerSlots[], playerStacks[]
    /// </summary>
    public event Action<int, int, int, int, int[], float[]> OnHandStarted;

    /// <summary>
    /// Fired on all clients when any player takes an action.
    /// Args: playerSlotIndex, actionType, amount, gamePhase
    /// </summary>
    public event Action<int, string, float, int> OnPlayerActionReceived;

    /// <summary>
    /// Fired on all clients when a hand finishes (before cleanup/next round).
    /// Args: winnerSlots[], winnerAmounts[], potTotal, communityCardRanks[], communityCardSuits[],
    ///       winnerCard1Ranks[], winnerCard1Suits[], winnerCard2Ranks[], winnerCard2Suits[], cardsWereRevealed
    /// </summary>
    public event Action<int[], float[], float, int[], int[], int[], int[], int[], int[], bool> OnHandCompleted;

    /// <summary>
    /// Fired on all clients when a player voluntarily reveals hole cards after the pot is awarded.
    /// Args: playerRealSlotIndex, card1, card2, revealCard1, revealCard2
    /// </summary>
    public event Action<int, Card, Card, bool, bool> OnVoluntaryCardsRevealed;

    /// <summary>
    /// Fired on all clients when any player sits down (initial buy-in or re-buy).
    /// Args: slotIndex, playerName, buyInAmount
    /// </summary>
    public event Action<int, string, float> OnPlayerBuyIn;

    // ── Hand history server-side tracking ──
    /// <summary>Table session identity (survives host migration). Synced via NetworkVariable.</summary>
    public NetworkVariable<FixedString64Bytes> tableSessionId = new NetworkVariable<FixedString64Bytes>();

    /// <summary>Server-side hand counter. Incremented each GameLoop.</summary>
    private int currentHandNumber = 0;

    /// <summary>Accumulates winners during award phase, sent in HandCompletedClientRpc.</summary>
    private List<int> currentHandWinnerSlots = new List<int>();
    private List<float> currentHandWinnerAmounts = new List<float>();

    // ── Accessors for hand history ──
    public int CurrentHandNumber => currentHandNumber;
    public string TableSessionId => tableSessionId.Value.ToString();

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        
        if (IsServer)
        {
            // Register for client disconnect events to clean up ghost players
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;

            // Register the host (server) in connectedClients so host migration
            // always knows about us — even if we never sit down at the table.
            string hostAuth = AuthenticationService.Instance.PlayerId;
            connectedClients[NetworkManager.Singleton.LocalClientId] = hostAuth;
            clientIdToAuthPlayerId[NetworkManager.Singleton.LocalClientId] = hostAuth;
            if (!unlimitedAvailableBalance)
            {
                GetOrInitAvailableBalance(hostAuth);
            }
            SendAvailableBalanceToClient(NetworkManager.Singleton.LocalClientId, hostAuth);
            Debug.Log($"[{LogTag.Game}] Server: Host registered in connectedClients — authId={hostAuth}", this);

            // Hide loading screen — the host's scene is ready
            if (LoadingUI.Instance != null) LoadingUI.Instance.StopLoading();

            // Host migration: register our own auth ID so RestoreGameState
            // knows we (the new host) are connected.
            bool isMigration = HostMigrationManager.Instance != null &&
                               HostMigrationManager.Instance.IsMigrationPending;

            if (isMigration && HostMigrationManager.Instance.IsNewHost)
            {
                HostMigrationManager.Instance.RegisterReconnectedPlayer(hostAuth);
                clientIdToAuthPlayerId[NetworkManager.Singleton.LocalClientId] = hostAuth;
                Debug.Log($"[{LogTag.Migration}] Server: New host registered own auth — authId={hostAuth}", this);
            }

            // Set Table Session ID for fresh games (migration restores it from snapshot)
            if (!isMigration && LobbyManager.Instance != null)
            {
                string sessionId = LobbyManager.Instance.GetTableSessionId();
                if (!string.IsNullOrEmpty(sessionId))
                {
                    tableSessionId.Value = new FixedString64Bytes(sessionId);
                    Debug.Log($"[{LogTag.Game}] Server: Table session ID set — sessionId={sessionId}", this);
                }
            }
        }
        
        // Client (not host): sync UI from network state if game is in progress
        if (!IsServer)
        {
            // Register with the server so connectedClients tracks ALL clients
            // (seated + spectators). This is critical for host migration.
            string myAuth = AuthenticationService.Instance.PlayerId;
            RegisterConnectedClientServerRpc(myAuth);

            // Host migration: tell the new host who we are so they can track reconnections
            if (HostMigrationManager.Instance != null &&
                HostMigrationManager.Instance.IsMigrationPending)
            {
                isMigrationReconnect = true;
                RegisterAuthForMigrationServerRpc(myAuth);
                Debug.Log($"[{LogTag.Migration}] Client sending auth registration — authId={myAuth}", this);
            }
            
            StartCoroutine(InitializeClientState());
        }
    }
    
    public override void OnNetworkDespawn()
    {
        if (IsServer && NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
        }
        base.OnNetworkDespawn();
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
    }

    // ── Accessors for host-migration snapshot ────────────────────
    public int GetLastRaiserIndex() => lastRaiserIndex;
    public int GetCurrentDealerRealSlotIndex() => currentDealerRealSlotIndex;
    public Dictionary<int, ulong> GetSlotToClientId() => slotToClientId;
    public Dictionary<ulong, string> GetClientIdToAuthPlayerId() => clientIdToAuthPlayerId;
    public Dictionary<ulong, string> GetConnectedClients() => connectedClients;
    public List<Card> GetDeck() => deck;
    public Dictionary<int, float> GetTimeBankBySlotIndex() => timeBankBySlotIndex;

    /// <summary>
    /// Returns true if at least one other Netcode client (seated or spectator)
    /// is connected besides the server/host. Used by ExitHandler to decide
    /// whether host migration should be attempted.
    /// </summary>
    public bool HasOtherConnectedClients()
    {
        if (!IsServer) return false;
        foreach (var kvp in connectedClients)
        {
            if (kvp.Key != NetworkManager.Singleton.LocalClientId)
                return true;
        }
        return false;
    }

    // Setters for host-migration restoration
    public void SetLastRaiserIndex(int value) => lastRaiserIndex = value;
    public void SetCurrentDealerRealSlotIndex(int value) => currentDealerRealSlotIndex = value;
    public void SetDeck(List<Card> newDeck) => deck = newDeck;

    private string ResolveAuthIdForClient(ulong clientId, string fallbackAuthId = "")
    {
        if (clientIdToAuthPlayerId.TryGetValue(clientId, out string mapped) && !string.IsNullOrEmpty(mapped))
            return mapped;

        if (!string.IsNullOrEmpty(fallbackAuthId))
            return fallbackAuthId;

        return clientId.ToString();
    }

    private float GetOrInitAvailableBalance(string authPlayerId)
    {
        if (string.IsNullOrEmpty(authPlayerId))
            return sessionAvailableBalance;

        if (!availableBalanceByAuthPlayerId.TryGetValue(authPlayerId, out float balance))
        {
            balance = sessionAvailableBalance;
            availableBalanceByAuthPlayerId[authPlayerId] = balance;
        }
        return balance;
    }

    private void SetAvailableBalance(string authPlayerId, float balance)
    {
        if (string.IsNullOrEmpty(authPlayerId)) return;
        availableBalanceByAuthPlayerId[authPlayerId] = Mathf.Max(0f, balance);
    }

    private bool HasPendingAddCash(string authPlayerId)
    {
        return !string.IsNullOrEmpty(authPlayerId)
            && pendingAddCashByAuthPlayerId.TryGetValue(authPlayerId, out float amount)
            && amount > 0f;
    }

    private void QueueAddCashForNextRound(string authPlayerId, float amount)
    {
        if (string.IsNullOrEmpty(authPlayerId) || amount <= 0f) return;
        pendingAddCashByAuthPlayerId[authPlayerId] = amount;
    }

    private void ClearPendingAddCash(string authPlayerId)
    {
        if (string.IsNullOrEmpty(authPlayerId)) return;
        pendingAddCashByAuthPlayerId.Remove(authPlayerId);
    }

    private bool TryGetSeatedPlayerByAuthId(string authPlayerId, out ulong clientId, out int slotIndex, out PokerPlayer player)
    {
        clientId = 0;
        slotIndex = -1;
        player = default;

        foreach (var kvp in clientIdToAuthPlayerId)
        {
            if (kvp.Value == authPlayerId)
            {
                clientId = kvp.Key;
                break;
            }
        }
        if (clientId == 0) return false;

        foreach (var kvp in slotToClientId)
        {
            if (kvp.Value == clientId)
            {
                slotIndex = kvp.Key;
                break;
            }
        }
        if (slotIndex < 0) return false;

        return TryGetPlayerByRealSlotIndex(slotIndex, out player);
    }

    /// <summary>
    /// Applies queued add-cash requests when a new hand is about to start.
    /// Requests are one-shot: if they cannot be applied now, they are dropped.
    /// </summary>
    private void ApplyQueuedAddCashAtRoundStart()
    {
        if (pendingAddCashByAuthPlayerId.Count == 0) return;

        var keys = new List<string>(pendingAddCashByAuthPlayerId.Keys);
        foreach (string authId in keys)
        {
            float queuedAmount = pendingAddCashByAuthPlayerId[authId];
            ClearPendingAddCash(authId);

            if (queuedAmount <= 0f) continue;

            if (!TryGetSeatedPlayerByAuthId(authId, out ulong clientId, out int slotIndex, out PokerPlayer player))
            {
                Debug.LogWarning($"[{LogTag.Game}] Server: Dropping queued add-cash — authId={authId}, reason=player no longer seated", this);
                continue;
            }

            float maxAddByStack = Mathf.Max(0f, maxBuyIn - player.stack);
            if (maxAddByStack <= 0f)
            {
                Debug.LogWarning($"[{LogTag.Game}] Server: Dropping queued add-cash — authId={authId}, reason=stack at/above max buy-in", this);
                SendAvailableBalanceToClient(clientId, authId);
                continue;
            }

            float addNow = Mathf.Min(queuedAmount, maxAddByStack);
            if (!unlimitedAvailableBalance)
            {
                float available = GetOrInitAvailableBalance(authId);
                addNow = Mathf.Min(addNow, available);
                if (addNow <= 0f)
                {
                    SendAvailableBalanceToClient(clientId, authId);
                    continue;
                }
                SetAvailableBalance(authId, available - addNow);
            }

            PokerPlayer updatedPlayer = player;
            updatedPlayer.stack += addNow;
            for (int i = 0; i < players.Count; i++)
            {
                if (players[i].realSlotIndex == slotIndex)
                {
                    players[i] = updatedPlayer;
                    break;
                }
            }

            UpdateStackClientRpc(slotIndex, updatedPlayer.stack);
            SendAvailableBalanceToClient(clientId, authId);

            Debug.Log($"[{LogTag.Game}] Server: Applied queued add-cash — authId={authId}, amount=${addNow:F2}", this);
        }
    }

    private void SendAvailableBalanceToClient(ulong clientId, string authPlayerId = "")
    {
        float balance = sessionAvailableBalance;
        if (!unlimitedAvailableBalance)
        {
            string resolvedAuth = ResolveAuthIdForClient(clientId, authPlayerId);
            balance = GetOrInitAvailableBalance(resolvedAuth);
        }

        UpdateLocalAvailableBalanceClientRpc(balance, unlimitedAvailableBalance, new ClientRpcParams
        {
            Send = new ClientRpcSendParams
            {
                TargetClientIds = new[] { clientId }
            }
        });
    }

    [ClientRpc]
    private void UpdateLocalAvailableBalanceClientRpc(float balance, bool isUnlimited, ClientRpcParams clientRpcParams = default)
    {
        localAvailableBalance = Mathf.Max(0f, balance);
        unlimitedAvailableBalance = isUnlimited;
        OnLocalAvailableBalanceChanged?.Invoke(localAvailableBalance, unlimitedAvailableBalance);
    }

    private void CashOutPlayerToAvailableBalance(PokerPlayer player, ulong clientId = 0)
    {
        if (unlimitedAvailableBalance) return;

        string authId = ResolveAuthIdForClient(clientId);
        if (string.IsNullOrEmpty(authId))
            return;

        float current = GetOrInitAvailableBalance(authId);
        float cashedOutStack = Mathf.Max(0f, player.stack);
        SetAvailableBalance(authId, current + cashedOutStack);
        ClearPendingAddCash(authId);

        if (clientId != 0)
        {
            SendAvailableBalanceToClient(clientId, authId);
        }
    }

    // Hand history setters (used by host-migration restore)
    public void SetCurrentHandNumber(int value) => currentHandNumber = value;

    /// <summary>
    /// RPC sent by the departing host to ALL clients carrying the full game state
    /// and instructions for who becomes the new host.
    /// </summary>
    [ClientRpc]
    public void MigrateHostClientRpc(byte[] serializedSnapshot)
    {
        // The departing host (server) also receives this ClientRpc.
        // It must NOT process it — the host is leaving, not migrating.
        // Without this check, IsMigrationPending gets set on the departing host,
        // causing them to enter the wrong initialization path if they later rejoin.
        if (IsServer)
        {
            Debug.Log($"[{LogTag.Migration}] Ignoring migration snapshot on departing host (server)", this);
            return;
        }
        
        Debug.Log($"[{LogTag.Migration}] Received migration snapshot from departing host", this);
        var snapshot = GameStateSnapshot.FromBytes(serializedSnapshot);
        HostMigrationManager.GetOrCreate().OnMigrationDataReceived(snapshot);
    }

    /// <summary>
    /// Called by EVERY client on spawn to register in connectedClients.
    /// This tracks ALL connected Netcode clients (seated + spectators) so
    /// host migration can find a new host and know who to expect, even if
    /// a client never sits down at the table.
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    public void RegisterConnectedClientServerRpc(string authId, ServerRpcParams rpcParams = default)
    {
        ulong senderClientId = rpcParams.Receive.SenderClientId;
        connectedClients[senderClientId] = authId;
        clientIdToAuthPlayerId[senderClientId] = authId;
        if (!unlimitedAvailableBalance)
        {
            GetOrInitAvailableBalance(authId);
        }
        SendAvailableBalanceToClient(senderClientId, authId);
        Debug.Log($"[{LogTag.Game}] Server: Client registered — authId={authId}, clientId={senderClientId}, totalConnected={connectedClients.Count}", this);

        TrySendRejoinHistory(authId, senderClientId);
    }

    private void TrySendRejoinHistory(string authId, ulong clientId)
    {
        var recorder = FindObjectOfType<HandRecorder>();
        if (recorder == null)
        {
            Debug.LogWarning($"[{LogTag.Game}] Server: Rejoin history skipped — reason=no HandRecorder in scene, authId={authId}", this);
            return;
        }

        byte[] payload = recorder.TryBuildRejoinPayload(authId);
        if (payload == null)
        {
            Debug.Log($"[{LogTag.Game}] Server: Rejoin history skipped — reason=no prior session data for authId={authId}", this);
            return;
        }

        Debug.Log($"[{LogTag.Game}] Server: Sending rejoin history — authId={authId}, clientId={clientId}, size={payload.Length}B", this);
        SendRejoinHistoryClientRpc(payload,
            new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new ulong[] { clientId } } });
    }

    [ClientRpc]
    private void SendRejoinHistoryClientRpc(byte[] data, ClientRpcParams rpcParams = default)
    {
        Debug.Log($"[{LogTag.Game}] Client: Rejoin history received — size={data?.Length ?? 0}B", this);
        var recorder = FindObjectOfType<HandRecorder>();
        if (recorder != null)
            recorder.RestoreFromRejoinPayload(data);
        else
            Debug.LogWarning($"[{LogTag.Game}] Client: Rejoin history dropped — reason=no HandRecorder in scene", this);
    }

    /// <summary>
    /// Called by clients during host migration to register their auth player ID
    /// with the new host server. This lets the new host know who reconnected
    /// and rebuild the clientId-to-auth mappings for the new Netcode session.
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    public void RegisterAuthForMigrationServerRpc(string authId, ServerRpcParams rpcParams = default)
    {
        ulong senderClientId = rpcParams.Receive.SenderClientId;
        Debug.Log($"[{LogTag.Migration}] Client registered auth ID — authId={authId}, clientId={senderClientId}", this);

        // Track for migration reconnection counting
        if (HostMigrationManager.Instance != null)
        {
            HostMigrationManager.Instance.RegisterReconnectedPlayer(authId);
        }

        // Populate the auth mapping for this new session
        clientIdToAuthPlayerId[senderClientId] = authId;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Host Migration — visual sync + game resumption (new host only)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Called by HostMigrationManager.RestoreGameState after the server-side data
    /// (players NetworkList, pot, community cards, etc.) has been populated.
    /// This coroutine:
    ///   1. Sets up the host's own localPlayer + rotates the table.
    ///   2. Sends UpdatePlayerSlotClientRpc for each player so every client
    ///      establishes its localPlayer and rotates its table.
    ///   3. Sends cards (face-up to owner, hidden to others), bets, community
    ///      cards, and folded states to all clients.
    ///   4. Resumes the game turn (or advances it if the departed host was on turn).
    /// </summary>
    public System.Collections.IEnumerator ApplyMigrationState(bool turnPlayerWasRemoved, int originalTurnSlotIndex)
    {
        // ── Wait for PokerTable + slot UI to be ready ────────────────
        float waitTime = 0f;
        while (waitTime < 5f)
        {
            if (PokerTable.Instance != null)
            {
                var slots = PokerTable.Instance.GetPlayerSlots();
                if (slots != null)
                {
                    bool anyReady = false;
                    foreach (var s in slots)
                    {
                        if (s != null && s.namePlate != null) { anyReady = true; break; }
                    }
                    if (anyReady) break;
                }
            }
            yield return new UnityEngine.WaitForSeconds(0.1f);
            waitTime += 0.1f;
        }
        if (PokerTable.Instance == null)
        {
            Debug.LogError($"[{LogTag.Migration}] PokerTable not ready — cannot apply migration visuals", this);
            yield break;
        }

        // ── 1. Set up the host's own local player ────────────────────
        ulong myClientId = NetworkManager.Singleton.LocalClientId;
        for (int i = 0; i < players.Count; i++)
        {
            if (slotToClientId.TryGetValue(players[i].realSlotIndex, out ulong cid) && cid == myClientId)
            {
                localPlayer = players[i];
                hasLocalPlayer = true;
                Debug.Log($"[{LogTag.Migration}] Host localPlayer set — name={localPlayer.playerName}, slot={localPlayer.realSlotIndex}", this);
                break;
            }
        }

        if (hasLocalPlayer)
        {
            PokerTable.Instance.RotateSlotsToLocalPlayer(localPlayer.realSlotIndex, localPlayer);
            PokerTable.Instance.RemoveNonOccupiedEmptySlotsLocally();
        }
        else
        {
            // This is expected when the new host was a spectator (not seated).
            // The rest of ApplyMigrationState handles this gracefully.
            Debug.LogWarning($"[{LogTag.Migration}] New host has no local player (spectator) — table will not be rotated", this);
        }

        // ── 2. Tell every client which player they are ───────────────
        //   UpdatePlayerSlotClientRpc with the correct clientId causes each
        //   client to set localPlayer when clientId matches LocalClientId,
        //   which triggers RotateSlotsToLocalPlayer on that client.
        foreach (var player in players)
        {
            ulong playerClientId = 0;
            slotToClientId.TryGetValue(player.realSlotIndex, out playerClientId);
            UpdatePlayerSlotClientRpc(player.realSlotIndex, player.playerName.ToString(),
                                      player.stack, player.iconIndex, playerClientId);
        }

        // Give clients time to process the player-setup RPCs
        yield return new UnityEngine.WaitForSeconds(1.0f);

        // ── 3. Send full visual state (cards, bets, community, folds) ──
        if (isGameStarted.Value)
        {
            // For each connected client (including host — host receives its own targeted RPCs):
            foreach (ulong clientId in NetworkManager.Singleton.ConnectedClientsIds)
            {
                var targetParams = new ClientRpcParams
                {
                    Send = new ClientRpcSendParams { TargetClientIds = new ulong[] { clientId } }
                };

                foreach (var player in players)
                {
                    // Cards
                    if (player.card1.rank != Card.Rank.None && player.card1.suit != Card.Suit.None)
                    {
                        ulong ownerCid = slotToClientId.ContainsKey(player.realSlotIndex)
                            ? slotToClientId[player.realSlotIndex] : 0;

                        if (ownerCid == clientId)
                        {
                            // Face-up cards to the owner
                            SendCardsToPlayerClientRpc(player.card1.rank, player.card1.suit,
                                player.card2.rank, player.card2.suit, targetParams);
                        }
                        else
                        {
                            // Hidden (face-down) cards for other players
                            ShowHiddenCardsForPlayerClientRpc(player.realSlotIndex, ownerCid, targetParams);
                        }
                    }

                    // Bet displays
                    if (player.currentBet > 0)
                    {
                        SyncBetDisplayClientRpc(player.realSlotIndex, player.currentBet, targetParams);
                    }
                }

                // Community cards
                if (currentPhase.Value >= GamePhase.Flop)
                {
                    int cardCount = 0;
                    switch (currentPhase.Value)
                    {
                        case GamePhase.Flop: cardCount = 3; break;
                        case GamePhase.Turn: cardCount = 4; break;
                        case GamePhase.River:
                        case GamePhase.Showdown: cardCount = 5; break;
                    }
                    if (cardCount > 0)
                    {
                        SyncCommunityCardsStateClientRpc(cardCount, targetParams);
                    }
                }
            }

            // Update host's localPlayer card data (so UI reads match)
            if (hasLocalPlayer && localPlayer.card1.rank != Card.Rank.None)
            {
                // localPlayer struct is a copy — re-read from the NetworkList
                for (int i = 0; i < players.Count; i++)
                {
                    if (players[i].realSlotIndex == localPlayer.realSlotIndex)
                    {
                        localPlayer.card1 = players[i].card1;
                        localPlayer.card2 = players[i].card2;
                        break;
                    }
                }
            }

            // Show folded state for any players who were already folded
            foreach (var player in players)
            {
                if (!player.isPlaying)
                {
                    ShowFoldedStateClientRpc(player.realSlotIndex);
                }
            }

            // Sync the pot display (potOnTable NetworkVariable is already set,
            // but the UI text/gameobject needs an explicit push)
            if (potOnTable.Value > 0)
            {
                SyncPotDisplayClientRpc(potOnTable.Value);
            }

            // Let visual updates settle before starting timers
            yield return new UnityEngine.WaitForSeconds(0.3f);

            // ── 4. Check if the round should end immediately ─────────────
            //    After removing the departing host (and any unreconnected players),
            //    the hand may already be over:
            //      a) Only one player is still isPlaying → they win the pot.
            //      b) All remaining active players are all-in (or all-but-one) →
            //         skip straight to showdown / deal remaining community cards.
            if (CheckForSinglePlayerWin())
            {
                Debug.Log($"[{LogTag.Migration}] Single player remaining after migration — pot awarded", this);
                // CheckForSinglePlayerWin already starts the award coroutine.
            }
            else
            {
                // Check for all-in scenario (no more betting action possible)
                CountActiveAndAllInPlayers(out int activeCount, out int allInCount);
                if (activeCount > 1 && allInCount >= activeCount - 1)
                {
                    Debug.Log($"[{LogTag.Migration}] All-in detected after migration — allInCount={allInCount}, activeCount={activeCount}, action=skipping to showdown", this);
                    RevealAllPlayersCards();
                    // End the current betting round and run through remaining phases
                    EndBettingRoundAndCollect();
                }
                else
                {
                    // ── Normal turn resumption ───────────────────────────────
                    if (turnPlayerWasRemoved)
                    {
                        Debug.Log($"[{LogTag.Migration}] Turn player was removed — finding next player", this);
                        // Find the next active player. Use the position in the list
                        // that corresponds to the original turn's slot so the search
                        // picks up from roughly the right place.
                        int searchFrom = -1;
                        for (int i = 0; i < players.Count; i++)
                        {
                            if (players[i].realSlotIndex > originalTurnSlotIndex ||
                                (i == players.Count - 1))
                            {
                                searchFrom = (i > 0) ? i - 1 : players.Count - 1;
                                break;
                            }
                        }
                        if (searchFrom < 0) searchFrom = 0;

                        int next = GetNextActivePlayerIndex(searchFrom);
                        if (next >= 0)
                        {
                            Debug.Log($"[{LogTag.Migration}] Advancing turn — playerIndex={next}", this);
                            SetPlayerTurn(next);
                        }
                        else
                        {
                            Debug.Log($"[{LogTag.Migration}] No active players — ending betting round", this);
                            EndBettingRoundAndCollect();
                        }
                    }
                    else if (originalTurnSlotIndex >= 0)
                    {
                        // Resume the same player's turn with a fresh timer
                        int turnIndex = -1;
                        for (int i = 0; i < players.Count; i++)
                        {
                            if (players[i].realSlotIndex == originalTurnSlotIndex)
                            {
                                turnIndex = i;
                                break;
                            }
                        }

                        if (turnIndex >= 0 && players[turnIndex].isPlaying && !players[turnIndex].isAllIn)
                        {
                            Debug.Log($"[{LogTag.Migration}] Resuming turn — playerIndex={turnIndex}, name={players[turnIndex].playerName}", this);
                            SetPlayerTurn(turnIndex);
                        }
                        else
                        {
                            // Original turn player can't act — find next
                            int next = GetNextActivePlayerIndex(turnIndex >= 0 ? turnIndex : 0);
                            if (next >= 0)
                            {
                                SetPlayerTurn(next);
                            }
                            else
                            {
                                EndBettingRoundAndCollect();
                            }
                        }
                    }
                }
            }
        }

        Debug.Log($"[{LogTag.Migration}] Migration visual sync complete — game resumed", this);
    }

    /// <summary>
    /// When the application is quit (Alt+F4, close button, etc.), immediately notify the server
    /// so the player is folded/removed without waiting for the network timeout.
    /// </summary>
    private void OnApplicationQuit()
    {
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient && !IsServer && hasLocalPlayer)
        {
            Debug.Log($"[{LogTag.Game}] Client: Application quitting — slot={localPlayer.realSlotIndex}", this);
            NotifyServerOfQuitServerRpc(localPlayer.realSlotIndex);
        }
    }
    
    /// <summary>
    /// ServerRpc - Client notifies the server it is quitting so the player is immediately
    /// folded and removed without waiting for the disconnect timeout.
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    private void NotifyServerOfQuitServerRpc(int realSlotIndex, ServerRpcParams rpcParams = default)
    {
        ulong clientId = rpcParams.Receive.SenderClientId;
        Debug.Log($"[{LogTag.Game}] Server: Received quit notification — clientId={clientId}, slot={realSlotIndex}", this);
        
        // Trigger the same cleanup as a network disconnect
        OnClientDisconnected(clientId);
    }

    /// <summary>
    /// Client calls this to request sitting at the table
    /// </summary>
    public void SitOnTable(PlayerSlotUI playerSlotUI, float buyInAmount)
    {
        // Send auth player ID so the server can kick from lobby on disconnect
        string authPlayerId = AuthenticationService.Instance.PlayerId;
        // Use the player's lobby username
        string name = PlayerNameData.PlayerName;
        // Use the player's selected character avatar
        int iconIndex = PlayerAssets.Instance != null ? PlayerAssets.Instance.currentCharacterId : 0;
        SitOnTableServerRpc(playerSlotUI.slotIndex, name, buyInAmount, iconIndex, authPlayerId);
    }

    /// <summary>
    /// ServerRpc - runs on server when client requests to sit
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    private void SitOnTableServerRpc(int slotIndex, string playerName, float buyInAmount, int iconIndex, string authPlayerId = "", ServerRpcParams rpcParams = default)
    {
        Debug.Log($"[{LogTag.Game}] Server: Sit request received — slot={slotIndex}, name={playerName}, buyIn=${buyInAmount:F2}, iconIndex={iconIndex}", this);
        // Get the client ID who sent this request
        ulong clientId = rpcParams.Receive.SenderClientId;
        string resolvedAuthId = ResolveAuthIdForClient(clientId, authPlayerId);
        
        // Check if slot is already occupied (prevents ghost player issues)
        if (TryGetPlayerByRealSlotIndex(slotIndex, out PokerPlayer existingPlayer))
        {
            Debug.LogWarning($"[{LogTag.Game}] Server: Sit request rejected — slot={slotIndex}, occupant={existingPlayer.playerName}, clientId={clientId}, reason=slot occupied", this);
            return;
        }
        
        // Find the slot
        PlayerSlotUI slot = FindSlotByRealIndex(slotIndex);
        if (slot == null)
        {
            Debug.LogError($"[{LogTag.Game}] Server: Slot not found — slot={slotIndex}", this);
            return;
        }
        
        // Store the client ID for this slot (server-side only, for sending cards privately)
        slotToClientId[slotIndex] = clientId;
        
        // Store auth player ID mapping for lobby kick on disconnect
        if (!string.IsNullOrEmpty(resolvedAuthId))
        {
            clientIdToAuthPlayerId[clientId] = resolvedAuthId;
        }

        // Validate and consume available session balance (unless unlimited)
        if (!unlimitedAvailableBalance)
        {
            float available = GetOrInitAvailableBalance(resolvedAuthId);
            if (buyInAmount > available + 0.001f)
            {
                Debug.LogWarning($"[{LogTag.Game}] Server: Buy-in rejected — authId={resolvedAuthId}, requested=${buyInAmount:F2}, available=${available:F2}", this);
                SendAvailableBalanceToClient(clientId, resolvedAuthId);
                return;
            }
            SetAvailableBalance(resolvedAuthId, available - buyInAmount);
        }
        
        bool isLocalPlayer = NetworkManager.Singleton.LocalClientId == clientId;
        // Add player on server (modifies NetworkVariables)
        PokerPlayer newPlayer = AddPlayer(slot, playerName, buyInAmount, iconIndex, isLocalPlayer: isLocalPlayer);

        // Initialize time bank for the new player if not already set
        if (!timeBankBySlotIndex.ContainsKey(slotIndex))
        {
            timeBankBySlotIndex[slotIndex] = playerTimeBankTotal;
        }
        // Send time bank to client
        SendTimeBankToClientRpc(slotIndex, timeBankBySlotIndex[slotIndex],
            new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new ulong[] { clientId } } });

        // Push updated available balance snapshot to the buyer
        SendAvailableBalanceToClient(clientId, resolvedAuthId);
        
        // Notify all clients to update UI
        UpdatePlayerSlotClientRpc(slotIndex, playerName, buyInAmount, iconIndex, clientId);
        
        // If game has already started, send hidden cards for all existing players to the newly joined client
        if (isGameStarted.Value)
        {
            Debug.Log($"[{LogTag.Game}] Server: Sending hidden cards to newly joined client — clientId={clientId}", this);
            foreach (var seated in players)
            {
                // Skip the newly joined player (they don't have cards yet or will get them later)
                if (seated.realSlotIndex == slotIndex)
                {
                    continue;
                }
                
                // Only send cards if the player actually has cards (not None state)
                if (seated.card1.rank != Card.Rank.None && seated.card1.suit != Card.Suit.None)
                {
                    // Send hidden cards for this existing player to the new client
                    ShowHiddenCardsForPlayerClientRpc(
                        seated.realSlotIndex,
                        slotToClientId.ContainsKey(seated.realSlotIndex) ? slotToClientId[seated.realSlotIndex] : 0,
                        new ClientRpcParams 
                        { 
                            Send = new ClientRpcSendParams 
                            { 
                                TargetClientIds = new ulong[] { clientId } 
                            } 
                        }
                    );
                }
            }
        }
    }

    /// <summary>
    /// ClientRpc - updates UI on all clients
    /// </summary>
    [ClientRpc]
    private void UpdatePlayerSlotClientRpc(int slotIndex, string playerName, float buyInAmount, int iconIndex, ulong clientId)
    {
        Debug.Log($"[{LogTag.Game}] Client: Player slot updated — slot={slotIndex}, name={playerName}, buyIn=${buyInAmount:F2}, iconIndex={iconIndex}", this);
        
        if (PokerTable.Instance == null)
        {
            Debug.LogWarning($"[{LogTag.Game}] Client: UpdatePlayerSlot skipped — reason=PokerTable.Instance is null (scene not ready)", this);
            return;
        }
        
        // Check if this is the local player
        bool isLocalPlayer = NetworkManager.Singleton.LocalClientId == clientId;
        
        if (isLocalPlayer)
        {
            // This is the local player - create the player data directly from RPC params
            // Don't wait for NetworkList sync which may not have completed yet
            localPlayer = new PokerPlayer(playerName, buyInAmount, iconIndex, slotIndex);
            hasLocalPlayer = true;
            
            if (isMigrationReconnect)
            {
                // During migration, DON'T rotate yet — the players NetworkList may not have
                // synced to this client. InitializeClientState's migration path will handle
                // the rotation after waiting for the NetworkList to sync.
                Debug.Log($"[{LogTag.Migration}] Client: localPlayer set, deferring rotation — name={playerName}, slot={slotIndex}", this);
            }
            else
            {
                PokerTable.Instance.RotateSlotsToLocalPlayer(localPlayer.realSlotIndex, localPlayer);
                PokerTable.Instance.RemoveNonOccupiedEmptySlotsLocally();
            }
        }
        else
        {
            // Remote player - just update their slot
            int newPlayerSlotIndex = GetVisualSlotIndex(slotIndex);
            PlayerSlotUI slot = FindSlotByRealIndex(newPlayerSlotIndex);
            if (slot != null)
            {
                slot.SetPlayerSlot(playerName, buyInAmount, iconIndex);
            }
            else
            {
                Debug.LogError($"[{LogTag.Game}] Client: Slot not found for remote player — visualSlot={newPlayerSlotIndex}, realSlot={slotIndex}, localSlot={localPlayer.realSlotIndex}", this);
            }
        }

        // Notify hand recorder of the buy-in (fires for every player, on all clients)
        OnPlayerBuyIn?.Invoke(slotIndex, playerName, buyInAmount);
    }

    /// <summary>
    /// Client calls this to request standing up from table
    /// </summary>
    public void StandUpFromTable()
    {
        if (!hasLocalPlayer)
        {
            Debug.LogWarning($"[{LogTag.Game}] Client: Stand-up request ignored — reason=no local player", this);
            return;
        }
        
        // Request server to remove player
        StandUpFromTableServerRpc(localPlayer.realSlotIndex);
    }

    /// <summary>
    /// ServerRpc - runs on server when client requests to stand up
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    private void StandUpFromTableServerRpc(int slotIndex, ServerRpcParams rpcParams = default)
    {
        ulong clientId = rpcParams.Receive.SenderClientId;
        
        // Find and remove the player
        if (TryGetPlayerByRealSlotIndex(slotIndex, out PokerPlayer playerToRemove))
        {
            // ── Handle in-progress game state BEFORE removal ──
            if (isGameStarted.Value)
            {
                int playerIndex = GetPlayerIndexBySlotIndex(slotIndex);
                bool wasOnTurn = (playerIndex >= 0 && playerIndex == currentPlayerTurnIndex.Value);

                // Fold the player if they were actively playing this hand
                if (playerIndex >= 0 && playerToRemove.isPlaying)
                {
                    PokerPlayer updated = players[playerIndex];
                    updated.isPlaying = false;
                    updated.hasActed = true;
                    players[playerIndex] = updated;
                    LogPlayerDeactivation(slotIndex, "stand-up");

                    ShowFoldedStateClientRpc(slotIndex);
                    PlayerActionClientRpc(slotIndex, "Stand Up", 0, (int)currentPhase.Value);
                }

                // If it was their turn, cancel the timer and advance
                if (wasOnTurn)
                {
                    CancelTurnAndNotifyClients();

                    if (!CheckForSinglePlayerWin())
                    {
                        MoveToNextPlayer();
                    }
                }
            }

            CashOutPlayerToAvailableBalance(playerToRemove, clientId);

            // Clear time bank for this slot
            timeBankBySlotIndex.Remove(slotIndex);

            // Remove the player from the game
            RemovePlayer(playerToRemove);
            
            // Remove client ID mapping (server-side only)
            if (slotToClientId.ContainsKey(slotIndex))
            {
                slotToClientId.Remove(slotIndex);
            }
            
            // Notify all clients
            RemovePlayerSlotClientRpc(slotIndex, clientId);
            
            Debug.Log($"[{LogTag.Game}] Server: Player stood up — slot={slotIndex}, remainingPlayers={players.Count}", this);
        }
    }

    /// <summary>
    /// ClientRpc - updates UI on all clients when player leaves.
    /// isBustOut: when true, avoids calling RestoreRealSlotPositions which reads
    /// potentially stale NetworkList data during multi-player bust-outs.
    /// </summary>
    [ClientRpc]
    private void RemovePlayerSlotClientRpc(int slotIndex, ulong clientId, bool isBustOut = false)
    {
        if (PokerTable.Instance == null) return;
        
        bool isLocalPlayer = NetworkManager.Singleton.LocalClientId == clientId;
        
        if (isLocalPlayer)
        {
            if (isBustOut)
            {
                // Bust-out: directly empty our visual slot without reading NetworkList
                // (RestoreRealSlotPositions reads NetworkList which may have stale data)
                if (hasLocalPlayer)
                {
                    int visualSlotIndex = GetVisualSlotIndex(slotIndex);
                    PlayerSlotUI slot = FindSlotByRealIndex(visualSlotIndex);
                    if (slot != null)
                    {
                        slot.SetEmptySlot();
                    }
                }
                localPlayer = default;
                hasLocalPlayer = false;
                PokerTable.Instance.RestoreNonOccupiedEmptySlotsLocally();
            }
            else
            {
                // Voluntary stand-up: full restore
            PokerTable.Instance.RestoreRealSlotPositions();
            PokerTable.Instance.RestoreNonOccupiedEmptySlotsLocally();
            localPlayer = default;
            hasLocalPlayer = false;
        }
        }
        else if (hasLocalPlayer)
        {
            // Remote player leaving - need to account for rotation
            int visualSlotIndex = GetVisualSlotIndex(slotIndex);
            PlayerSlotUI slot = FindSlotByRealIndex(visualSlotIndex);
        if (slot != null)
        {
            slot.SetEmptySlot();
            }
            PokerTable.Instance.RemoveNonOccupiedEmptySlotsLocally();
            return; // Early return since we handled it
        }
        
        // If no local player seated, use real slot index
        PlayerSlotUI slotToEmpty = FindSlotByRealIndex(slotIndex);
        if (slotToEmpty != null)
        {
            slotToEmpty.SetEmptySlot();
        }
    }

    public PokerPlayer AddPlayer(PlayerSlotUI playerSlotUI, string playerName, float buyInAmount, int iconIndex, bool isLocalPlayer = false)
    {
        playersCount.Value++;
        PokerPlayer newPlayer = new PokerPlayer(playerName, buyInAmount, iconIndex, playerSlotUI.slotIndex);
        
        // Remember whose turn it is (by realSlotIndex) BEFORE rebuilding the list,
        // because the rebuild shifts indices and would make currentPlayerTurnIndex stale.
        int turnSlotBefore = -1;
        if (isGameStarted.Value &&
            currentPlayerTurnIndex.Value >= 0 &&
            currentPlayerTurnIndex.Value < players.Count)
        {
            turnSlotBefore = players[currentPlayerTurnIndex.Value].realSlotIndex;
        }
        
        // Add player and maintain sorted order by realSlotIndex
        // Since NetworkList doesn't have Insert(), we need to rebuild the list in sorted order
        List<PokerPlayer> tempList = new List<PokerPlayer>();
        foreach (var player in players)
        {
            tempList.Add(player);
        }
        tempList.Add(newPlayer);
        
        // Sort by realSlotIndex
        tempList.Sort((a, b) => a.realSlotIndex.CompareTo(b.realSlotIndex));
        
        // Rebuild NetworkList in sorted order
        players.Clear();
        foreach (var player in tempList)
        {
            players.Add(player);
        }
        
        Debug.Log($"[{LogTag.Game}] Server: Player added — name={playerName}, slot={playerSlotUI.slotIndex}, totalPlayers={players.Count}", this);
        
        // Fix up currentPlayerTurnIndex after the list rebuild.
        // The old index now points to the wrong player because insertion shifted indices.
        RebindTurnIndexToSlot(turnSlotBefore);
        
        if (playersCount.Value >= 2 && !isGameStarted.Value)
        {
            StartGameServerRpc();
        }
        
        return newPlayer;
    }

    public void RemovePlayer(PokerPlayer player)
    {
        if (player.playerName.Length == 0)
        {
            Debug.LogWarning($"[{LogTag.Game}] Server: Cannot remove player — reason=empty name (default player)", this);
            return;
        }

        // Capture the active player's seat BEFORE the list shrinks so the
        // index-based currentPlayerTurnIndex can be re-resolved afterwards.
        // Removing a player whose list index is below the active player shifts
        // the active player's index; skipped when we're removing the active
        // player themselves (their turn must already have been advanced/cancelled).
        int turnSlotBefore = -1;
        if (currentPlayerTurnIndex.Value >= 0 && currentPlayerTurnIndex.Value < players.Count)
        {
            int holderSlot = players[currentPlayerTurnIndex.Value].realSlotIndex;
            if (holderSlot != player.realSlotIndex)
            {
                turnSlotBefore = holderSlot;
            }
        }

        // Remove by seat index rather than by value. PokerPlayer.Equals is now a
        // full-value comparison (required so NetworkList persists in-hand mutations),
        // so a stale copy passed in by callers would no longer match the mutated list
        // entry via players.Remove(...). Locating by realSlotIndex is exact and robust.
        int removeIndex = GetPlayerIndexBySlotIndex(player.realSlotIndex);
        if (removeIndex >= 0)
        {
            players.RemoveAt(removeIndex);
            playersCount.Value--;
            Debug.Log($"[{LogTag.Game}] Server: Player removed — name={player.playerName}, slot={player.realSlotIndex}, totalPlayers={players.Count}", this);
            
            // Note: The list remains sorted by realSlotIndex after removal
            // UI updates are handled by RemovePlayerSlotClientRpc, not here
            
            // Keep the turn pointing at the same physical seat after the index shift.
            RebindTurnIndexToSlot(turnSlotBefore);
            
            if (playersCount.Value < 2 && isGameStarted.Value)
            {
                EndGameServerRpc();
            }
        }
        else
        {
            Debug.LogError($"[{LogTag.Game}] Server: Player not found in players list — could not remove, slot={player.realSlotIndex}", this);
        }
    }

    /// <summary>
    /// Finds a PlayerSlotUI by its real slot index
    /// </summary>
    private PlayerSlotUI FindSlotByRealIndex(int realSlotIndex)
    {
        if (PokerTable.Instance == null)
        {
            Debug.LogError($"[{LogTag.Game}] FindSlotByRealIndex failed — reason=PokerTable.Instance is null", this);
            return null;
        }
        
        // Access the playerSlots array from PokerTable
        // We need to make it public or add a getter method
        foreach (var slot in PokerTable.Instance.GetPlayerSlots())
        {
            if (slot != null && slot.slotIndex == realSlotIndex)
            {
                return slot;
            }
        }
        
        return null;
    }

    /// <summary>
    /// Finds a player by real slot index.
    /// </summary>
    public bool TryGetPlayerByRealSlotIndex(int realSlotIndex, out PokerPlayer player)
    {
        foreach (var existingPlayer in players)
        {
            if (existingPlayer.realSlotIndex == realSlotIndex)
            {
                player = existingPlayer;
                return true;
            }
        }

        player = default;
        return false;
    }
    /// <summary>
    /// ServerRpc to start the game. Can be called by any client, executes on server.
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    public void StartGameServerRpc()
    {
        // Server-only game start logic
        // The cycle consists of:
        // 1. The server allocates the player positions
        // 2. Place their blind bets
        // 3. Shuffle the deck (SERVER ONLY - clients never see full deck)
        // 4. Deal cards to each player (via ServerRpc/ClientRpc)
        // 5. Start the timer on the first player until they take an action or timer runs out (fold)
        // ... etc.

        if (!IsServer) return;

        // Re-entrancy guard: this RPC is RequireOwnership=false and is triggered
        // from several places (a player seating, an editor button, potentially a
        // client). Without this guard two near-simultaneous calls each launch a
        // StartGameWithDelay → GameLoop, producing TWO overlapping hand state
        // machines that share one player list and wipe each other's state
        // (symptom: activePlayers=0 at every phase, pot never awarded, positions
        // allocated twice). gameStarting covers the 0.1s window before
        // isGameStarted-driven logic settles.
        if (isGameStarted.Value || gameStarting)
        {
            Debug.LogWarning($"[{LogTag.Game}] Server: StartGame ignored — a game is already starting/in progress", this);
            return;
        }

        gameStarting = true;
        isGameStarted.Value = true;
        
        // Add a small delay to allow UpdatePlayerSlotClientRpc to complete on all clients
        StartCoroutine(StartGameWithDelay(0.1f));
        
        Debug.Log($"[{LogTag.Game}] Server: Game start initiated — dealDelay=0.1s", this);
    }
    
    /// <summary>
    /// Coroutine to delay game start, allowing all clients to finish setup
    /// </summary>
    private System.Collections.IEnumerator StartGameWithDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        
        Debug.Log($"[{LogTag.Game}] Server: Starting game loop — deckSize=52", this);
        GameLoop(0);

        // Clear the start-in-progress latch now that the first hand is underway.
        gameStarting = false;
    }

    private void GameLoop(int dealerPositionIndex)
    {
        if (players.Count < 2)
        {
            Debug.Log($"[{LogTag.Game}] Server: Round skipped — reason=not enough players, count={players.Count}", this);
            return;
        }

        // Re-entrancy guard: a hand must never be started while another is still
        // running. Two overlapping GameLoops share the same player NetworkList and
        // stomp each other's state, leaving every player with isPlaying=false
        // (activePlayers=0) so the whole hand fast-forwards and the pot is never
        // awarded. This catches double-starts from ANY path (double StartGame,
        // stray next-round trigger, etc.).
        if (handInProgress)
        {
            Debug.LogError($"[{LogTag.Game}] Server: GameLoop re-entry blocked — a hand is already in progress (dealerPositionIndex={dealerPositionIndex})", this);
            return;
        }
        handInProgress = true;

        revealedPlayerCards = false;
        ResetVoluntaryRevealTracking();

        // Apply deferred add-cash requests right before the new hand starts.
        ApplyQueuedAddCashAtRoundStart();
        
        // Regenerate time bank for all seated players (1/30 of max per round, capped at max)
        RegenerateTimeBanks();
        
        // Store current dealer index and real slot index for next round
        currentDealerIndex = dealerPositionIndex;
        currentDealerRealSlotIndex = players[dealerPositionIndex].realSlotIndex;
    
        // Clear folded states from previous round
        ClearAllFoldedStatesClientRpc();
        
        // Initialize and shuffle deck (server-only)
        InitializeDeck();
        ShuffleDeck();

        AllocatePlayersPositions(dealerPositionIndex);
        
        // ── Hand History: broadcast hand start to all clients ──
        currentHandNumber++;
        currentHandWinnerSlots.Clear();
        currentHandWinnerAmounts.Clear();

        // Snapshot player slots and stacks BEFORE blinds are deducted
        int[] hSlots = new int[players.Count];
        float[] hStacks = new float[players.Count];
        for (int i = 0; i < players.Count; i++)
        {
            hSlots[i] = players[i].realSlotIndex;
            hStacks[i] = players[i].stack;
        }
        HandStartedClientRpc(currentHandNumber,
            dealerPlayer.realSlotIndex,
            smallBlindPlayer.realSlotIndex,
            bigBlindPlayer.realSlotIndex,
            hSlots, hStacks);

        // Start the first betting round (pre-flop) - initializes betting state
        StartBettingRound();
        
        // Place blinds (must happen after StartBettingRound to use betting system)
        PlaceBlinds();
        
        // Deal cards
        DealCards();
        
        // Set first player's turn (player after big blind)
        int bigBlindIndex = GetPlayerIndexBySlotIndex(bigBlindPlayer.realSlotIndex);
        int firstPlayerIndex = GetNextActivePlayerIndex(bigBlindIndex);
        if (players.Count == 2)
        {
            firstPlayerIndex = bigBlindIndex;
        }
        if (firstPlayerIndex != -1)
        {
            SetPlayerTurn(firstPlayerIndex);
        }
        
        // TODO: Implement post-flop betting, showdown, and winner determination
        
        //GameLoop(nextDealerPositionIndex);
        // NOTE: Removed recursive GameLoop call to prevent stack overflow
        // The next round should be triggered by game events (e.g., after showdown)
        // int nextDealerPositionIndex = (dealerPositionIndex + 1) % players.Count;
        // Call StartNextRound() or similar when ready for next hand
    }

    private void AllocatePlayersPositions(int startingPositionIndex)
    {
        // Use modulo to wrap around the player list (circular table)
        int playerCount = players.Count;
        
        dealerPlayer = players[startingPositionIndex % playerCount];
        
        if (playerCount > 2)
        {
            // Standard poker: dealer, small blind, big blind
            smallBlindPlayer = players[(startingPositionIndex + 1) % playerCount];
            bigBlindPlayer = players[(startingPositionIndex + 2) % playerCount];
        }
        else if (playerCount == 2)
        {
            // Heads-up poker: dealer is small blind, other player is big blind
            smallBlindPlayer = players[startingPositionIndex % playerCount];
            bigBlindPlayer = players[(startingPositionIndex + 1) % playerCount];
        }
        
        Debug.Log($"[{LogTag.Game}] Server: Positions allocated — dealer={dealerPlayer.playerName}, smallBlind={smallBlindPlayer.playerName}, bigBlind={bigBlindPlayer.playerName}", this);
    }
    [ServerRpc(RequireOwnership = false)]
    public void EndGameServerRpc()
    {
        // Cancel any running turn timer before ending the game
        // to prevent stale coroutines from accessing removed players.
        CancelTurnAndNotifyClients();

        // If a hand is still in progress (pot on the table), finalize it
        // for hand history / net-P&L tracking before the game ends.
        // Without this, BroadcastHandCompleted would be skipped because
        // CleanupAndStartNextRound checks isGameStarted (set to false below).
        FinalizeInProgressHand();

        // Return all money on the table (pot + outstanding bets) to the
        // last remaining player so nothing is lost between rounds.
        ReturnTableMoneyToSurvivors();

        isGameStarted.Value = false;
        gameStarting = false;
        handInProgress = false;
        Debug.Log($"[{LogTag.Game}] Server: Game ended — isGameStarted=false", this);
    }

    /// <summary>
    /// Called by EndGameServerRpc BEFORE isGameStarted is set to false.
    /// If a hand is in progress, records the surviving player as the
    /// winner and broadcasts HandCompleted so that HandRecorder and the
    /// P&amp;L display update correctly for the final hand.
    /// </summary>
    private void FinalizeInProgressHand()
    {
        if (!IsServer) return;

        float pot = potOnTable.Value;

        // No pot → we are between hands, nothing to finalize.
        if (pot <= 0f) return;

        // Only finalize when exactly 1 player remains — the hand has
        // a clear winner and is truly over.  If multiple players are
        // still seated this shouldn't be called, but guard anyway.
        if (players.Count != 1) return;

        int winnerSlot = players[0].realSlotIndex;

        // Record the sole survivor as the winner for hand-history purposes
        // (AwardPotToPlayer may not have run yet because its coroutine
        //  is waiting on animation delays).
        if (!currentHandWinnerSlots.Contains(winnerSlot))
        {
            currentHandWinnerSlots.Add(winnerSlot);
            currentHandWinnerAmounts.Add(pot);
        }

        // Broadcast to all clients so HandRecorder saves the final hand,
        // updates localSessionNetResult, and persists the session summary.
        BroadcastHandCompleted();
    }

    /// <summary>
    /// Returns the entire pot and any outstanding per-player bets to the
    /// surviving player(s). Called when the game ends because fewer than
    /// two players remain at the table.
    /// </summary>
    private void ReturnTableMoneyToSurvivors()
    {
        if (!IsServer) return;

        float pot = potOnTable.Value;

        if (players.Count == 1)
        {
            // Single survivor — give them back everything on the table.
            PokerPlayer winner = players[0];
            winner.stack += pot;
            winner.currentBet = 0;
            winner.totalBetInHand = 0;
            players[0] = winner;

            potOnTable.Value = 0f;
            currentBetToCall.Value = 0f;

            if (pot > 0)
            {
                // Visual: collect bet chips into pot, then animate pot to player
                CollectBetsToTableClientRpc();
                AwardPotToPlayerClientRpc(winner.realSlotIndex, pot, winner.stack);
                Debug.Log($"[{LogTag.Game}] Server: Returned pot to last player — name={winner.playerName}, pot=${pot:F2}, stack=${winner.stack:F2}", this);
            }
            else
            {
                // No pot but clean up any stale bet chip displays
                UpdateStackClientRpc(winner.realSlotIndex, winner.stack);
            }
        }
        else
        {
            // Zero players or somehow >1 (shouldn't happen in normal flow).
            // Just reset pot/bet state so nothing lingers.
            potOnTable.Value = 0f;
            currentBetToCall.Value = 0f;
        }
    }

    /// <summary>
    /// Initializes the deck with 52 cards. SERVER ONLY.
    /// </summary>
    private void InitializeDeck()
    {
        deck = new List<Card>();
        for (int i = 0; i < 52; i++)
        {
            deck.Add(new Card((Card.Rank)(i % 13 + 2), (Card.Suit)(i % 4 + 2)));
        }
    }

    /// <summary>
    /// Shuffles the deck using Fisher-Yates algorithm. SERVER ONLY.
    /// </summary>
    private void ShuffleDeck()
    {
        if (deck == null)
        {
            Debug.LogWarning($"[{LogTag.Game}] Server: Cannot shuffle — reason=deck is null", this);
            return;
        }
        
        // Fisher-Yates shuffle
        for (int i = 0; i < deck.Count; i++)
        {
            int randomIndex = UnityEngine.Random.Range(0, deck.Count);
            Card temp = deck[i];
            deck[i] = deck[randomIndex];
            deck[randomIndex] = temp;
        }
        
        Debug.Log($"[{LogTag.Game}] Server: Deck shuffled", this);
    }
    
    /// <summary>
    /// Gets the number of cards remaining in the deck. Returns 0 on clients.
    /// </summary>
    public int GetDeckCount()
    {
        return IsServer && deck != null ? deck.Count : 0;
    }
    
    /// <summary>
    /// Draws a card from the deck. SERVER ONLY.
    /// </summary>
    public Card DrawCard()
    {
        if (!IsServer)
        {
            Debug.LogError($"[{LogTag.Game}] DrawCard called on client — must be server only", this);
            return default;
        }
        
        if (deck == null || deck.Count == 0)
        {
            Debug.LogError($"[{LogTag.Game}] Server: Cannot draw card — reason=deck is empty or null", this);
            return default;
        }
        
        // Draw a random card from the deck
        int randomIndex = UnityEngine.Random.Range(0, deck.Count);
        Card drawnCard = deck[randomIndex];
        deck.RemoveAt(randomIndex);
        return drawnCard;
    }
    
    /// <summary>
    /// Deals cards to all players. SERVER ONLY.
    /// In Texas Hold'em, each player gets 2 hole cards.
    /// Cards are sent privately to each player via targeted ClientRpc.
    /// </summary>
    public void DealCards()
    {
        if (!IsServer)
        {
            Debug.LogError($"[{LogTag.Game}] DealCards called on client — this method is server-only", this);
            return;
        }
        
        if (deck == null || deck.Count < players.Count * 2)
        {
            Debug.LogError($"[{LogTag.Game}] Server: DealCards failed — reason=insufficient cards, need={players.Count * 2}, have={deck?.Count ?? 0}", this);
            return;
        }
        
        Debug.Log($"[{LogTag.Game}] Server: Dealing cards — playerCount={players.Count}", this);
        Debug.Log($"[{LogTag.Game}] Server: Slot-to-client mapping — {string.Join(", ", slotToClientId.Select(kvp => $"slot{kvp.Key}=client{kvp.Value}"))}", this);
        
        // Deal 2 cards to each player
        for (int i = 0; i < players.Count; i++)
        {
            PokerPlayer player = players[i];
            
            // Draw 2 cards from the deck
            Card card1 = DrawCard();
            Card card2 = DrawCard();
            
            // Store cards in the player struct (server-side only)
            // Since PokerPlayer is a struct, we must modify the copy and reassign it
            player.card1 = card1;
            player.card2 = card2;
            players[i] = player;  // Put the modified struct back in the list

            // Get the client ID for this player's slot
            if (slotToClientId.TryGetValue(player.realSlotIndex, out ulong clientId))
            {
                Debug.Log($"[{LogTag.Game}] Server: Sending cards — player={player.playerName}, slot={player.realSlotIndex}, clientId={clientId}", this);
                
                // Send real cards privately to this specific client only
                SendCardsToPlayerClientRpc(card1.rank, card1.suit, card2.rank, card2.suit, 
                    new ClientRpcParams
                    {
                        Send = new ClientRpcSendParams
                        {
                            TargetClientIds = new ulong[] { clientId }
                        }
                    });
                
                // Send hidden cards to ALL OTHER clients (so they see face-down cards)
                ShowHiddenCardsForPlayerClientRpc(player.realSlotIndex, clientId);
                
                // If this is the server's own player (host is playing), update localPlayer too
                if (hasLocalPlayer && localPlayer.realSlotIndex == player.realSlotIndex)
                {
                    localPlayer.card1 = card1;
                    localPlayer.card2 = card2;
                    Debug.Log($"[{LogTag.Game}] Server: Host localPlayer cards updated — card1={card1}, card2={card2}", this);
                }
                
                Debug.Log($"[{LogTag.Game}] Server: Cards dealt — player={player.playerName}, slot={player.realSlotIndex}, clientId={clientId}, card1={card1}, card2={card2}", this);
            }
            else
            {
                Debug.LogWarning($"[{LogTag.Game}] Server: Client ID not found for player — player={player.playerName}, slot={player.realSlotIndex}", this);
            }
        }
        
        Debug.Log($"[{LogTag.Game}] Server: Dealing complete — remainingCards={deck.Count}", this);
    }
    
    /// <summary>
    /// ClientRpc that sends cards to a specific player.
    /// This is targeted, so only the intended player receives their cards.
    /// </summary>
    [ClientRpc]
    private void SendCardsToPlayerClientRpc(Card.Rank rank1, Card.Suit suit1, 
                                            Card.Rank rank2, Card.Suit suit2,
                                            ClientRpcParams clientRpcParams = default)
    {
        Debug.Log($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Cards received via RPC — rank1={rank1}, suit1={suit1}, rank2={rank2}, suit2={suit2}", this);
        Debug.Log($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Local player check — hasLocalPlayer={hasLocalPlayer}", this);
        
        // Store the received cards locally
        Card card1 = new Card(rank1, suit1);
        Card card2 = new Card(rank2, suit2);
        
        Debug.Log($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Cards constructed — card1={card1}, card2={card2}", this);
        
        // If this targeted ClientRpc reached us, we ARE the local player
        // Don't rely on hasLocalPlayer flag due to RPC ordering issues
        
        // Update local player's cards if we have a local player reference
        if (hasLocalPlayer)
        {
            localPlayer.card1 = card1;
            localPlayer.card2 = card2;
            Debug.Log($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Local player cards updated", this);
            
            // Note: We don't update the NetworkList here because it's server-authoritative.
            // Client-side modifications to NetworkList don't persist.
            // The editor script handles displaying localPlayer cards when viewing the players array.
        }
        
        // Update UI to show the player's cards
        // After rotation, the local player is always at visual position 0 (first slot)
        if (PokerTable.Instance == null) return;
        PlayerSlotUI localPlayerSlot = PokerTable.Instance.GetPlayerSlots()[0];
        
        if (localPlayerSlot != null)
        {
            // Initialize the CardUI components with the received cards
            if (localPlayerSlot.card1 != null)
            {
                localPlayerSlot.card1.gameObject.SetActive(true);
                localPlayerSlot.card1.Init(rank1, suit1, isHidden: false, animate: true);
                Debug.Log($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Card1 UI initialized — card={card1}", this);
            }
            else
            {
                Debug.LogWarning($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Card1 UI missing — target=<none>", this);
            }
            
            if (localPlayerSlot.card2 != null)
            {
                localPlayerSlot.card2.gameObject.SetActive(true);
                localPlayerSlot.card2.Init(rank2, suit2, isHidden: false, animate: true);
                Debug.Log($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Card2 UI initialized — card={card2}", this);
            }
            else
            {
                Debug.LogWarning($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Card2 UI missing — target=<none>", this);
            }
        }
        else
        {
            Debug.LogWarning($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Local player slot UI not found — slotIndex=0", this);
        }
    }
    
    /// <summary>
    /// ClientRpc that shows hidden (face-down) cards for a player on all OTHER clients.
    /// This is sent to everyone EXCEPT the player who owns the cards.
    /// </summary>
    [ClientRpc]
    private void ShowHiddenCardsForPlayerClientRpc(int playerRealSlotIndex, ulong ownerClientId, ClientRpcParams clientRpcParams = default)
    {
        // Skip if this is the owner of these cards (they see them face-up via SendCardsToPlayerClientRpc)
        if (NetworkManager.Singleton.LocalClientId == ownerClientId)
        {
            Debug.Log($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Skipped hidden cards — reason=owner by clientId", this);
            return;
        }
        
        // Also skip if this slot belongs to our local player (after rotation is set up)
        if (hasLocalPlayer && localPlayer.realSlotIndex == playerRealSlotIndex)
        {
            Debug.Log($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Skipped hidden cards — reason=local player slot", this);
            return;
        }
        
        Debug.Log($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Showing hidden cards — realSlot={playerRealSlotIndex}", this);
        
        int visualSlotIndex = GetVisualSlotIndex(playerRealSlotIndex);
        Debug.Log($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Slot mapped — realSlot={playerRealSlotIndex}, visualSlot={visualSlotIndex}", this);
        
        PlayerSlotUI playerSlot = GetPlayerSlotUI(playerRealSlotIndex);
        
        if (playerSlot != null)
        {
            // Show hidden (face-down) cards
            if (playerSlot.card1 != null)
            {
                playerSlot.card1.gameObject.SetActive(true);
                playerSlot.card1.Init(Card.Rank.Hidden, Card.Suit.Hidden, isHidden: true, animate: true);
                Debug.Log($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Hidden card1 initialized — visualSlot={visualSlotIndex}", this);
            }
            
            if (playerSlot.card2 != null)
            {
                playerSlot.card2.gameObject.SetActive(true);
                playerSlot.card2.Init(Card.Rank.Hidden, Card.Suit.Hidden, isHidden: true, animate: true);
                Debug.Log($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Hidden card2 initialized — visualSlot={visualSlotIndex}", this);
            }
        }
        else
        {
            Debug.LogWarning($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Player slot not found — visualSlot={visualSlotIndex}", this);
        }
    }
    
    /// <summary>
    /// Reveals all active players' cards at showdown with flip animations
    /// </summary>
    [ClientRpc]
    private void RevealShowdownCardsClientRpc(int[] playerSlotIndices, Card[] card1s, Card[] card2s)
    {
        Debug.Log($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Revealing showdown cards — playerCount={playerSlotIndices.Length}", this);
        StartCoroutine(RevealShowdownCardsCoroutine(playerSlotIndices, card1s, card2s));
    }
    
    /// <summary>
    /// Coroutine to reveal cards with staggered timing for dramatic effect
    /// </summary>
    private System.Collections.IEnumerator RevealShowdownCardsCoroutine(int[] playerSlotIndices, Card[] card1s, Card[] card2s)
    {
        for (int i = 0; i < playerSlotIndices.Length; i++)
        {
            int playerRealSlotIndex = playerSlotIndices[i];
            Card card1 = card1s[i];
            Card card2 = card2s[i];
            
            // Skip revealing cards for the local player (they already see them)
            if (hasLocalPlayer && localPlayer.realSlotIndex == playerRealSlotIndex)
            {
                Debug.Log($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Skipped reveal for local player — realSlot={playerRealSlotIndex}", this);
                continue;
            }
            
            int visualSlotIndex = GetVisualSlotIndex(playerRealSlotIndex);
            Debug.Log($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Revealing player cards — realSlot={playerRealSlotIndex}, visualSlot={visualSlotIndex}, card1={card1}, card2={card2}", this);
            
            PlayerSlotUI playerSlot = GetPlayerSlotUI(playerRealSlotIndex);
            
            if (playerSlot != null && playerSlot.card1 != null && playerSlot.card2 != null)
            {
                // Re-initialize the cards with the real data (face-down first)
                playerSlot.card1.Init(card1.rank, card1.suit, isHidden: true, animate: false);
                playerSlot.card2.Init(card2.rank, card2.suit, isHidden: true, animate: false);
                
                // Small delay to ensure cards are initialized
                yield return new WaitForSeconds(0.1f);
                
                // Now flip both cards to reveal them with animation
                playerSlot.card1.Flip(instant: false);
                playerSlot.card2.Flip(instant: false);
                
                Debug.Log($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Cards flipped — visualSlot={visualSlotIndex}", this);
            }
            else
            {
                Debug.LogWarning($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Player slot or cards not found — visualSlot={visualSlotIndex}", this);
            }
            
            // Stagger reveals slightly for dramatic effect (0.3 seconds between players)
            if (i < playerSlotIndices.Length - 1)
            {
                yield return new WaitForSeconds(0.3f);
            }
        }
    }
    
    /// <summary>
    /// Reveals all active players' cards when no more betting action is possible (all players all-in or all but one)
    /// </summary>
    private void RevealAllPlayersCards()
    {
        if (!IsServer) return;
        
        if (revealedPlayerCards) return;
        revealedPlayerCards = true;
        
        Debug.Log($"[{LogTag.Game}] Server: Revealing all cards — reason=no more betting action", this);
        
        // Collect all active players' cards
        List<int> playerSlotIndices = new List<int>();
        List<Card> card1List = new List<Card>();
        List<Card> card2List = new List<Card>();
        
        foreach (var player in players)
        {
            if (player.isPlaying)
            {
                playerSlotIndices.Add(player.realSlotIndex);
                card1List.Add(player.card1);
                card2List.Add(player.card2);
                Debug.Log($"[{LogTag.Game}] Server: Adding reveal cards — player={player.playerName}, slot={player.realSlotIndex}, card1={player.card1}, card2={player.card2}", this);
            }
        }
        
        if (playerSlotIndices.Count > 0)
        {
            foreach (int slot in playerSlotIndices)
                MarkPlayerCardsRevealedToOthers(slot);

            // Send to all clients to reveal cards with animations
            RevealShowdownCardsClientRpc(playerSlotIndices.ToArray(), card1List.ToArray(), card2List.ToArray());
        }
    }

    // ==================== VOLUNTARY HOLE-CARD REVEAL ====================

    private void ResetVoluntaryRevealTracking()
    {
        voluntaryRevealWindowOpen = false;
        card1RevealedToOthersBySlot.Clear();
        card2RevealedToOthersBySlot.Clear();
    }

    private void MarkPlayerCardsRevealedToOthers(int realSlotIndex)
    {
        card1RevealedToOthersBySlot[realSlotIndex] = true;
        card2RevealedToOthersBySlot[realSlotIndex] = true;
    }

    private bool IsCardRevealedToOthers(int realSlotIndex, bool card1)
    {
        var map = card1 ? card1RevealedToOthersBySlot : card2RevealedToOthersBySlot;
        return map.TryGetValue(realSlotIndex, out bool revealed) && revealed;
    }

    private bool PlayerHasHoleCards(int realSlotIndex)
    {
        if (!TryGetPlayerByRealSlotIndex(realSlotIndex, out PokerPlayer player))
            return false;
        return player.card1.rank != Card.Rank.None && player.card1.suit != Card.Suit.None;
    }

    private void TryOpenVoluntaryRevealWindow()
    {
        if (!IsServer || voluntaryRevealWindowOpen)
            return;

        voluntaryRevealWindowOpen = true;

        foreach (var player in players)
        {
            if (!PlayerHasHoleCards(player.realSlotIndex))
                continue;

            bool showCard1 = !IsCardRevealedToOthers(player.realSlotIndex, card1: true);
            bool showCard2 = !IsCardRevealedToOthers(player.realSlotIndex, card1: false);
            if (!showCard1 && !showCard2)
                continue;

            if (!slotToClientId.TryGetValue(player.realSlotIndex, out ulong clientId))
                continue;

            var rpcParams = new ClientRpcParams
            {
                Send = new ClientRpcSendParams { TargetClientIds = new ulong[] { clientId } }
            };
            ShowVoluntaryRevealButtonsClientRpc(showCard1, showCard2, rpcParams);
        }
    }

    private void ResetVoluntaryRevealClientState()
    {
        VoluntaryRevealWindowActive = false;
        ShowVoluntaryRevealCard1Button = false;
        ShowVoluntaryRevealCard2Button = false;
        ShowVoluntaryRevealBothButton = false;
        OnVoluntaryRevealStateChanged?.Invoke();
    }

    private void ApplyVoluntaryRevealButtonState(bool showCard1Button, bool showCard2Button)
    {
        VoluntaryRevealWindowActive = showCard1Button || showCard2Button;
        ShowVoluntaryRevealCard1Button = showCard1Button;
        ShowVoluntaryRevealCard2Button = showCard2Button;
        ShowVoluntaryRevealBothButton = showCard1Button || showCard2Button;
        OnVoluntaryRevealStateChanged?.Invoke();
    }

    private int GetClientSlotIndex(ulong clientId)
    {
        foreach (var kvp in slotToClientId)
        {
            if (kvp.Value == clientId)
                return kvp.Key;
        }
        return -1;
    }

    public void RequestVoluntaryReveal(int revealMask)
    {
        if (!hasLocalPlayer)
            return;
        RevealCardsServerRpc(revealMask);
    }

    [ServerRpc(RequireOwnership = false)]
    private void RevealCardsServerRpc(int revealMask, ServerRpcParams rpcParams = default)
    {
        if (!IsServer || !voluntaryRevealWindowOpen)
            return;

        ulong senderClientId = rpcParams.Receive.SenderClientId;
        int slotIndex = GetClientSlotIndex(senderClientId);
        if (slotIndex < 0 || !TryGetPlayerByRealSlotIndex(slotIndex, out PokerPlayer player))
            return;

        if (!PlayerHasHoleCards(slotIndex))
            return;

        bool wantCard1 = (revealMask & VoluntaryRevealCard1) != 0;
        bool wantCard2 = (revealMask & VoluntaryRevealCard2) != 0;
        if (!wantCard1 && !wantCard2)
            return;

        bool revealCard1 = wantCard1 && !IsCardRevealedToOthers(slotIndex, card1: true);
        bool revealCard2 = wantCard2 && !IsCardRevealedToOthers(slotIndex, card1: false);
        if (!revealCard1 && !revealCard2)
            return;

        if (revealCard1) card1RevealedToOthersBySlot[slotIndex] = true;
        if (revealCard2) card2RevealedToOthersBySlot[slotIndex] = true;

        RevealVoluntaryCardsClientRpc(slotIndex, player.card1, player.card2, revealCard1, revealCard2);

        bool showCard1Button = !IsCardRevealedToOthers(slotIndex, card1: true);
        bool showCard2Button = !IsCardRevealedToOthers(slotIndex, card1: false);
        var ownerParams = new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new ulong[] { senderClientId } }
        };

        if (showCard1Button || showCard2Button)
            ShowVoluntaryRevealButtonsClientRpc(showCard1Button, showCard2Button, ownerParams);
        else
            HideVoluntaryRevealButtonsClientRpc(ownerParams);
    }

    [ClientRpc]
    private void ShowVoluntaryRevealButtonsClientRpc(bool showCard1Button, bool showCard2Button, ClientRpcParams rpcParams = default)
    {
        ApplyVoluntaryRevealButtonState(showCard1Button, showCard2Button);
    }

    [ClientRpc]
    private void HideVoluntaryRevealButtonsClientRpc(ClientRpcParams rpcParams = default)
    {
        ResetVoluntaryRevealClientState();
    }

    private void GetWinnerCardDataForHistory(int slot, PokerPlayer player,
        out int card1Rank, out int card1Suit, out int card2Rank, out int card2Suit, out bool anyRevealed)
    {
        bool revealCard1 = revealedPlayerCards || IsCardRevealedToOthers(slot, card1: true);
        bool revealCard2 = revealedPlayerCards || IsCardRevealedToOthers(slot, card1: false);
        anyRevealed = revealCard1 || revealCard2;

        if (revealCard1)
        {
            card1Rank = (int)player.card1.rank;
            card1Suit = (int)player.card1.suit;
        }
        else
        {
            card1Rank = (int)Card.Rank.Hidden;
            card1Suit = (int)Card.Suit.Hidden;
        }

        if (revealCard2)
        {
            card2Rank = (int)player.card2.rank;
            card2Suit = (int)player.card2.suit;
        }
        else
        {
            card2Rank = (int)Card.Rank.Hidden;
            card2Suit = (int)Card.Suit.Hidden;
        }
    }

    [ClientRpc]
    private void RevealVoluntaryCardsClientRpc(int playerRealSlotIndex, Card card1, Card card2, bool revealCard1, bool revealCard2)
    {
        OnVoluntaryCardsRevealed?.Invoke(playerRealSlotIndex, card1, card2, revealCard1, revealCard2);

        if (hasLocalPlayer && localPlayer.realSlotIndex == playerRealSlotIndex)
            return;

        PlayerSlotUI playerSlot = GetPlayerSlotUI(playerRealSlotIndex);
        if (playerSlot == null)
            return;

        if (revealCard1 && playerSlot.card1 != null)
        {
            playerSlot.card1.Init(card1.rank, card1.suit, isHidden: true, animate: false);
            playerSlot.card1.Flip(instant: false);
        }

        if (revealCard2 && playerSlot.card2 != null)
        {
            playerSlot.card2.Init(card2.rank, card2.suit, isHidden: true, animate: false);
            playerSlot.card2.Flip(instant: false);
        }
    }
    
    // ==================== COMMUNITY CARDS (FLOP, TURN, RIVER) ====================
    
    /// <summary>
    /// Server-side function to deal the flop (first 3 community cards)
    /// </summary>
    public void DealFlop()
    {
        if (!IsServer)
        {
            Debug.LogError($"[{LogTag.Game}] DealFlop called on client — this method is server-only", this);
            return;
        }
        
        if (deck == null || deck.Count < 3)
        {
            Debug.LogError($"[{LogTag.Game}] Server: DealFlop failed — reason=insufficient cards, have={deck?.Count ?? 0}, need=3", this);
            return;
        }
        
        Debug.Log($"[{LogTag.Game}] Server: Dealing flop", this);
        
        // Draw 3 cards for flop
        communityCard1.Value = DrawCard();
        communityCard2.Value = DrawCard();
        communityCard3.Value = DrawCard();
        
        Debug.Log($"[{LogTag.Game}] Server: Flop dealt — card1={communityCard1.Value}, card2={communityCard2.Value}, card3={communityCard3.Value}", this);
        
        // Update phase
        currentPhase.Value = GamePhase.Flop;
        
        // Wait a frame for NetworkVariables to sync, then flip cards on all clients
        StartCoroutine(FlipCardsAfterSync(0, 3));
    }
    
    /// <summary>
    /// Server-side function to deal the turn (4th community card)
    /// </summary>
    public void DealTurn()
    {
        if (!IsServer)
        {
            Debug.LogError($"[{LogTag.Game}] DealTurn called on client — this method is server-only", this);
            return;
        }
        
        if (deck == null || deck.Count < 1)
        {
            Debug.LogError($"[{LogTag.Game}] Server: DealTurn failed — reason=insufficient cards, have={deck?.Count ?? 0}, need=1", this);
            return;
        }
        
        Debug.Log($"[{LogTag.Game}] Server: Dealing turn", this);
        
        // Draw 4th card
        communityCard4.Value = DrawCard();
        
        Debug.Log($"[{LogTag.Game}] Server: Turn dealt — card={communityCard4.Value}", this);
        
        // Update phase
        currentPhase.Value = GamePhase.Turn;
        
        // Wait a frame for NetworkVariables to sync, then flip card on all clients
        StartCoroutine(FlipCardsAfterSync(3, 1));
    }
    
    /// <summary>
    /// Server-side function to deal the river (5th community card)
    /// </summary>
    public void DealRiver()
    {
        if (!IsServer)
        {
            Debug.LogError($"[{LogTag.Game}] DealRiver called on client — this method is server-only", this);
            return;
        }
        
        if (deck == null || deck.Count < 1)
        {
            Debug.LogError($"[{LogTag.Game}] Server: DealRiver failed — reason=insufficient cards, have={deck?.Count ?? 0}, need=1", this);
            return;
        }
        
        Debug.Log($"[{LogTag.Game}] Server: Dealing river", this);
        
        // Draw 5th card
        communityCard5.Value = DrawCard();
        
        Debug.Log($"[{LogTag.Game}] Server: River dealt — card={communityCard5.Value}", this);
        
        // Update phase
        currentPhase.Value = GamePhase.River;
        
        // Wait a frame for NetworkVariables to sync, then flip card on all clients
        StartCoroutine(FlipCardsAfterSync(4, 1));
    }
    
    /// <summary>
    /// Coroutine that waits for NetworkVariables to sync before flipping cards
    /// </summary>
    private System.Collections.IEnumerator FlipCardsAfterSync(int startIndex, int count)
    {
        // Wait one frame for NetworkVariables to sync to all clients
        yield return new WaitForSeconds(0.1f);
        
        // Now call the ClientRpc to flip the cards
        FlipCommunityCardsClientRpc(startIndex, count);
    }
    
    /// <summary>
    /// ClientRpc - Flips community cards with animation
    /// </summary>
    /// <param name="startIndex">Starting index of cards to flip (0-4)</param>
    /// <param name="count">Number of cards to flip</param>
    [ClientRpc]
    private void FlipCommunityCardsClientRpc(int startIndex, int count)
    {
        Debug.Log($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Flipping community cards — startIndex={startIndex}, count={count}", this);

        if (PokerSoundEffects.Instance != null)
            PokerSoundEffects.Instance.PlayCommunityCard();

        if (PokerTable.Instance == null)
        {
            Debug.LogError($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Flip community cards failed — reason=PokerTable instance is <none>", this);
            return;
        }
        
        CardUI[] cardSlots = PokerTable.Instance.GetCommunityCardSlots();
        if (cardSlots == null || cardSlots.Length < 5)
        {
            Debug.LogError($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Flip community cards failed — reason=card slots not initialized", this);
            return;
        }
        
        // Get the community cards from NetworkVariables
        Card[] communityCards = GetCommunityCardsArray();
        
        // Flip the specified cards
        for (int i = 0; i < count; i++)
        {
            int cardIndex = startIndex + i;
            if (cardIndex >= 0 && cardIndex < cardSlots.Length)
            {
                CardUI cardUI = cardSlots[cardIndex];
                Card card = communityCards[cardIndex];
                
                if (cardUI != null && card.rank != Card.Rank.None && card.suit != Card.Suit.None)
                {
                    cardUI.gameObject.SetActive(true);
                    cardUI.Init(card.rank, card.suit, isHidden: false, animate: true);
                    Debug.Log($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Community card flipped — index={cardIndex}, card={card}", this);
                }
            }
        }
    }
    
    /// <summary>
    /// ClientRpc - Clears/hides all community cards
    /// </summary>
    [ClientRpc]
    private void ClearCommunityCardsClientRpc()
    {
        Debug.Log($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Clearing community cards", this);
        
        if (PokerTable.Instance == null)
        {
            Debug.LogError($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Clear community cards failed — reason=PokerTable instance is <none>", this);
            return;
        }
        
        CardUI[] cardSlots = PokerTable.Instance.GetCommunityCardSlots();
        if (cardSlots == null)
        {
            Debug.LogError($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Clear community cards failed — reason=card slots are <none>", this);
            return;
        }
        
        // Hide all community cards
        for (int i = 0; i < cardSlots.Length; i++)
        {
            if (cardSlots[i] != null)
            {
                cardSlots[i].gameObject.SetActive(false);
            }
        }
    }
    
    /// <summary>
    /// Server-side function to progress to the next phase of the game
    /// </summary>
    private void MoveToNextPhase()
    {
        if (!IsServer)
        {
            Debug.LogError($"[{LogTag.Game}] MoveToNextPhase called on client — this method is server-only", this);
            return;
        }
        
        Debug.Log($"[{LogTag.Game}] Server: MoveToNextPhase — currentPhase={currentPhase.Value}", this);
        
        switch (currentPhase.Value)
        {
            case GamePhase.PreFlop:
                // Move to flop
                DealFlop();
                StartNewBettingPhase();
                break;
                
            case GamePhase.Flop:
                // Move to turn
                DealTurn();
                StartNewBettingPhase();
                break;
                
            case GamePhase.Turn:
                // Move to river
                DealRiver();
                StartNewBettingPhase();
                break;
                
            case GamePhase.River:
                // Move to showdown
                currentPhase.Value = GamePhase.Showdown;
                Debug.Log($"[{LogTag.Game}] Server: Moving to showdown", this);
                StartCoroutine(EvaluateShowdownAndAwardWinner(showdownEvaluationDelay));
                break;
                
            case GamePhase.Showdown:
                Debug.Log($"[{LogTag.Game}] Server: Already at showdown", this);
                break;
        }
    }
    
    /// <summary>
    /// Starts a new betting round after dealing community cards
    /// </summary>
    private void StartNewBettingPhase()
    {
        if (!IsServer) return;
        
        Debug.Log($"[{LogTag.Game}] Server: Starting new betting round — phase={currentPhase.Value}", this);
        
        // Check if all active players are all-in (no betting action possible)
        CountActiveAndAllInPlayers(out int activePlayers, out int allInPlayers);
        Debug.Log($"[{LogTag.Game}] Server: Player count check — activePlayers={activePlayers}, allInPlayers={allInPlayers}", this);

        // activePlayers==0 is an impossible state during a live hand (a fresh hand
        // sets everyone isPlaying=true). If we ever see it, the hand state has been
        // corrupted — dump every player's state so the root cause is visible, and
        // abort the hand cleanly (returning table money) instead of fast-forwarding
        // into a broken showdown that strands the pot.
        if (activePlayers == 0)
        {
            var dump = new System.Text.StringBuilder();
            dump.Append($"[{LogTag.Game}] Server: CORRUPT hand state — activePlayers=0 at phase={currentPhase.Value}. ");
            dump.Append($"players.Count={players.Count}, playersCount.Value={playersCount.Value}, isGameStarted={isGameStarted.Value}, handInProgress={handInProgress}. Roster: ");
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                dump.Append($"[{i}] name={p.playerName} slot={p.realSlotIndex} isPlaying={p.isPlaying} isAllIn={p.isAllIn} stack={p.stack:F2} bet={p.currentBet:F2}; ");
            }
            Debug.LogError(dump.ToString(), this);

            // Recover the table instead of leaving the pot stuck forever.
            EndGameServerRpc();
            return;
        }
        
        // Accumulate current round bets into totalBetInHand before resetting
        AccumulateBetsToTotal();
        
        // If all players except at most one are all-in, skip betting round and move to next phase immediately
        // (No betting action possible when only 0 or 1 player can still bet)
        if (allInPlayers >= activePlayers - 1)
        {
            Debug.Log($"[{LogTag.Game}] Server: Skipping betting round — reason=all-in majority, allInPlayers={allInPlayers}, activePlayers={activePlayers}", this);
            
            // Reveal all players' cards since no more betting action is possible
            RevealAllPlayersCards();
            
            StartCoroutine(MoveToNextPhaseAfterDelay(moveToNextPhaseAfterAllInDelay)); // Give time for card animations
            return;
        }
        
        // Reset betting state for all players
        for (int i = 0; i < players.Count; i++)
        {
            PokerPlayer player = players[i];
            player.currentBet = 0;
            player.hasActed = false;
            // Don't reset isPlaying or isAllIn - they persist across betting rounds
            players[i] = player;
        }
        
        // Reset betting variables
        currentBetToCall.Value = 0;
        lastRaiseAmount.Value = bigBlind; // Minimum raise resets to big blind
        wasRaiseThisRound.Value = false;
        lastRaiserIndex = -1;
        
        // Find first active player after dealer (or big blind for pre-flop)
        int dealerIndex = GetPlayerIndexBySlotIndex(dealerPlayer.realSlotIndex);
        int firstPlayerIndex = GetNextActivePlayerIndex(dealerIndex);
        
        if (firstPlayerIndex != -1)
        {
            SetPlayerTurn(firstPlayerIndex);
        }
        else
        {
            Debug.LogError($"[{LogTag.Game}] Server: StartNewBettingPhase failed — reason=no active players found", this);
        }
    }

    public void PlaceBlinds()
    {
        if (!IsServer) return;
        
        Debug.Log($"[{LogTag.Game}] Server: Placing blinds — smallBlind=${smallBlind:F2}, bigBlind=${bigBlind:F2}", this);
        
        // Find and update small blind player
        int smallBlindIndex = GetPlayerIndexBySlotIndex(smallBlindPlayer.realSlotIndex);
        if (smallBlindIndex != -1)
        {
            PokerPlayer player = players[smallBlindIndex];
            float actualSB = Mathf.Min(smallBlind, player.stack);
            player.stack -= actualSB;
            player.currentBet = actualSB;
            if (player.stack <= 0f)
            {
                player.stack = 0f;
                player.isAllIn = true;
            }
            players[smallBlindIndex] = player;
            potOnTable.Value += actualSB;
            
            // Animate the blind bet on all clients
            BetClientRpc(player.realSlotIndex, actualSB, player.stack);
            // Record the actual blind amount for hand history (may differ from smallBlind if all-in)
            PlayerActionClientRpc(player.realSlotIndex, "Small Blind", actualSB, (int)currentPhase.Value);
            Debug.Log($"[{LogTag.Game}] Server: Small blind posted — player={player.playerName}, amount=${actualSB:F2}, isAllIn={player.isAllIn}", this);
        }
        
        // Find and update big blind player
        int bigBlindIndex = GetPlayerIndexBySlotIndex(bigBlindPlayer.realSlotIndex);
        if (bigBlindIndex != -1)
        {
            PokerPlayer player = players[bigBlindIndex];
            float actualBB = Mathf.Min(bigBlind, player.stack);
            player.stack -= actualBB;
            player.currentBet = actualBB;
            if (player.stack <= 0f)
            {
                player.stack = 0f;
                player.isAllIn = true;
            }
            players[bigBlindIndex] = player;
            potOnTable.Value += actualBB;
            
            // Animate the blind bet on all clients
            BetClientRpc(player.realSlotIndex, actualBB, player.stack);
            // Record the actual blind amount for hand history (may differ from bigBlind if all-in)
            PlayerActionClientRpc(player.realSlotIndex, "Big Blind", actualBB, (int)currentPhase.Value);
            Debug.Log($"[{LogTag.Game}] Server: Big blind posted — player={player.playerName}, amount=${actualBB:F2}, isAllIn={player.isAllIn}", this);
        }
        
        // Update pot display
        PokerTable.Instance.UpdatePotOnTable(potOnTable.Value);
    }
    
    /// <summary>
    /// Client calls this to place a bet
    /// </summary>
    /// <param name="betAmount">The amount to bet</param>
    public void Bet(float betAmount)
    {
        if (!hasLocalPlayer)
        {
            Debug.LogWarning($"[{LogTag.Game}] Bet rejected — reason=no local player", this);
            return;
        }
        
        // Call the server to process the bet
        BetServerRpc(localPlayer.realSlotIndex, betAmount);
    }

    /// <summary>
    /// Client calls this to add cash to their already seated player.
    /// </summary>
    public void AddCash(float addAmount)
    {
        if (!hasLocalPlayer)
        {
            Debug.LogWarning($"[{LogTag.Game}] Add cash rejected — reason=no local player seated", this);
            return;
        }

        AddCashServerRpc(localPlayer.realSlotIndex, addAmount);
    }

    [ServerRpc(RequireOwnership = false)]
    private void AddCashServerRpc(int playerSlotIndex, float addAmount, ServerRpcParams rpcParams = default)
    {
        ulong senderClientId = rpcParams.Receive.SenderClientId;

        if (addAmount <= 0f)
        {
            Debug.LogWarning($"[{LogTag.Game}] Server: Add cash rejected — reason=invalid amount, amount=${addAmount:F2}", this);
            return;
        }

        // Ensure caller is topping up their own seated player.
        if (!slotToClientId.TryGetValue(playerSlotIndex, out ulong ownerClientId) || ownerClientId != senderClientId)
        {
            Debug.LogWarning($"[{LogTag.Game}] Server: Add cash rejected — reason=sender does not own slot, slot={playerSlotIndex}, senderClientId={senderClientId}", this);
            return;
        }

        if (!TryGetPlayerByRealSlotIndex(playerSlotIndex, out PokerPlayer player))
        {
            Debug.LogWarning($"[{LogTag.Game}] Server: Add cash failed — reason=player not found, slot={playerSlotIndex}", this);
            return;
        }

        // Do not allow add-cash when player already has max stack.
        if (player.stack >= maxBuyIn - 0.001f)
        {
            Debug.LogWarning($"[{LogTag.Game}] Server: Add cash rejected — reason=stack at max, stack=${player.stack:F2}, maxBuyIn=${maxBuyIn:F2}", this);
            SendAvailableBalanceToClient(senderClientId);
            return;
        }

        string authId = ResolveAuthIdForClient(senderClientId);

        // If player is still active in the current hand, defer add-cash to next round start.
        bool isActiveInCurrentHand = isGameStarted.Value && player.isPlaying;
        if (isActiveInCurrentHand)
        {
            if (HasPendingAddCash(authId))
            {
                Debug.LogWarning($"[{LogTag.Game}] Server: Add cash rejected — reason=already queued, authId={authId}", this);
                SendAvailableBalanceToClient(senderClientId, authId);
                return;
            }

            float maxAddAllowedNow = Mathf.Max(0f, maxBuyIn - player.stack);
            addAmount = Mathf.Min(addAmount, maxAddAllowedNow);
            if (addAmount <= 0f)
            {
                SendAvailableBalanceToClient(senderClientId, authId);
                return;
            }

            if (!unlimitedAvailableBalance)
            {
                float available = GetOrInitAvailableBalance(authId);
                if (addAmount > available + 0.001f)
                {
                    Debug.LogWarning($"[{LogTag.Game}] Server: Add cash queue rejected — reason=insufficient balance, requested=${addAmount:F2}, available=${available:F2}", this);
                    SendAvailableBalanceToClient(senderClientId, authId);
                    return;
                }
            }

            QueueAddCashForNextRound(authId, addAmount);
            Debug.Log($"[{LogTag.Game}] Server: Add cash queued — authId={authId}, amount=${addAmount:F2}", this);
            SendAvailableBalanceToClient(senderClientId, authId);
            return;
        }

        // Not active in hand: apply immediately, but never exceed max buy-in.
        float maxAddAllowed = Mathf.Max(0f, maxBuyIn - player.stack);
        addAmount = Mathf.Min(addAmount, maxAddAllowed);
        if (addAmount <= 0f)
        {
            SendAvailableBalanceToClient(senderClientId, authId);
            return;
        }

        if (!unlimitedAvailableBalance)
        {
            float available = GetOrInitAvailableBalance(authId);
            if (addAmount > available + 0.001f)
            {
                Debug.LogWarning($"[{LogTag.Game}] Server: Add cash rejected — reason=insufficient balance, requested=${addAmount:F2}, available=${available:F2}", this);
                SendAvailableBalanceToClient(senderClientId, authId);
                return;
            }

            SetAvailableBalance(authId, available - addAmount);
        }

        PokerPlayer updatedPlayer = player;
        updatedPlayer.stack += addAmount;

        for (int i = 0; i < players.Count; i++)
        {
            if (players[i].realSlotIndex == playerSlotIndex)
            {
                players[i] = updatedPlayer;
                break;
            }
        }

        UpdateStackClientRpc(playerSlotIndex, updatedPlayer.stack);
        SendAvailableBalanceToClient(senderClientId, authId);

        Debug.Log($"[{LogTag.Game}] Server: Cash added — slot={playerSlotIndex}, amount=${addAmount:F2}, newStack=${updatedPlayer.stack:F2}", this);
    }
    
    /// <summary>
    /// ServerRpc - processes bet on server and propagates to all clients
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    private void BetServerRpc(int playerSlotIndex, float betAmount, ServerRpcParams rpcParams = default)
    {
        ulong clientId = rpcParams.Receive.SenderClientId;
        
        // Validate the bet
        if (!TryGetPlayerByRealSlotIndex(playerSlotIndex, out PokerPlayer player))
        {
            Debug.LogError($"[{LogTag.Game}] Server: Bet failed — reason=player not found, slot={playerSlotIndex}", this);
            return;
        }
        
        if (betAmount <= 0)
        {
            Debug.LogError($"[{LogTag.Game}] Server: Bet failed — reason=invalid amount, amount=${betAmount:F2}", this);
            return;
        }
        
        if (player.stack < betAmount)
        {
            Debug.LogError($"[{LogTag.Game}] Server: Bet failed — reason=insufficient chips, player={player.playerName}, stack=${player.stack:F2}, bet=${betAmount:F2}", this);
            return;
        }
        
        // Update player's stack on server
        PokerPlayer updatedPlayer = player;
        updatedPlayer.stack -= betAmount;
        
        // Update the player in the NetworkList
        for (int i = 0; i < players.Count; i++)
        {
            if (players[i].realSlotIndex == playerSlotIndex)
            {
                players[i] = updatedPlayer;
                break;
            }
        }
        
        // Update pot on server
        potOnTable.Value += betAmount;
        
        Debug.Log($"[{LogTag.Game}] Server: Bet placed — player={player.playerName}, bet=${betAmount:F2}, newStack=${updatedPlayer.stack:F2}, pot=${potOnTable.Value:F2}", this);
        
        // Propagate bet to all clients
        BetClientRpc(playerSlotIndex, betAmount, updatedPlayer.stack);
    }
    
    /// <summary>
    /// ClientRpc - updates UI on all clients to show the bet
    /// </summary>
    [ClientRpc]
    private void BetClientRpc(int playerRealSlotIndex, float betAmount, float newStackAmount)
    {
        Debug.Log($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Bet received — slot={playerRealSlotIndex}, amount=${betAmount:F2}", this);
        
        PlayerSlotUI playerSlot = GetPlayerSlotUI(playerRealSlotIndex);
        if (playerSlot != null)
        {
            playerSlot.PlaceBet(betAmount);
            playerSlot.UpdateStackDisplay(newStackAmount);
            
            if (hasLocalPlayer && localPlayer.realSlotIndex == playerRealSlotIndex)
            {
                localPlayer.stack = newStackAmount;
            }
            
            Debug.Log($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Bet animation started — slot={playerRealSlotIndex}", this);
        }
    }
    
    /// <summary>
    /// ClientRpc - Updates stack display for a player (without bet animation)
    /// </summary>
    [ClientRpc]
    private void UpdateStackClientRpc(int playerRealSlotIndex, float newStackAmount)
    {
        Debug.Log($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Stack updated — slot={playerRealSlotIndex}, newStack=${newStackAmount:F2}", this);
        
        PlayerSlotUI playerSlot = GetPlayerSlotUI(playerRealSlotIndex);
        if (playerSlot != null)
        {
            playerSlot.UpdateStackDisplay(newStackAmount);
            
            if (hasLocalPlayer && localPlayer.realSlotIndex == playerRealSlotIndex)
            {
                localPlayer.stack = newStackAmount;
            }
        }
    }
    
    /// <summary>
    /// Awards the pot to a specific player with both logic and visual animation
    /// </summary>
    /// <param name="winnerRealSlotIndex">The real slot index of the winning player</param>
    public void AwardPotToPlayer(int winnerRealSlotIndex)
    {
        if (!IsServer)
        {
            Debug.LogError($"[{LogTag.Game}] AwardPotToPlayer called on client — this method is server-only", this);
            return;
        }
        
        // Find the winning player
        if (!TryGetPlayerByRealSlotIndex(winnerRealSlotIndex, out PokerPlayer winner))
        {
            Debug.LogError($"[{LogTag.Game}] Server: AwardPot failed — reason=player not found, slot={winnerRealSlotIndex}", this);
            return;
        }
        
        float potAmount = potOnTable.Value;
        Debug.Log($"[{LogTag.Game}] Server: Awarding pot — player={winner.playerName}, slot={winnerRealSlotIndex}, pot=${potAmount:F2}", this);

        // Track for hand history
        currentHandWinnerSlots.Add(winnerRealSlotIndex);
        currentHandWinnerAmounts.Add(potAmount);
        
        // Update player's stack
        PokerPlayer updatedPlayer = winner;
        updatedPlayer.stack += potAmount;
        
        // Update the player in the NetworkList
        for (int i = 0; i < players.Count; i++)
        {
            if (players[i].realSlotIndex == winnerRealSlotIndex)
            {
                players[i] = updatedPlayer;
                break;
            }
        }
        
        // Clear the pot
        potOnTable.Value = 0f;

        TryOpenVoluntaryRevealWindow();
        
        // Propagate to all clients for visual animation
        AwardPotToPlayerClientRpc(winnerRealSlotIndex, potAmount, updatedPlayer.stack);
    }
    
    /// <summary>
    /// ClientRpc - Shows pot animation and updates player's stack display on all clients
    /// </summary>
    [ClientRpc]
    private void AwardPotToPlayerClientRpc(int winnerRealSlotIndex, float potAmount, float newStackAmount)
    {
        Debug.Log($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Pot awarded — slot={winnerRealSlotIndex}, pot=${potAmount:F2}", this);
        
        if (PokerTable.Instance == null) return;
        
        int visualSlotIndex = GetVisualSlotIndex(winnerRealSlotIndex);
        PlayerSlotUI playerSlot = GetPlayerSlotUI(winnerRealSlotIndex);
        if (playerSlot != null)
        {
            PokerTable.Instance.AnimatePotToPlayer(visualSlotIndex, potAmount, () =>
            {
                playerSlot.UpdateStackDisplay(newStackAmount);
                Debug.Log($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Pot delivered — newStack=${newStackAmount:F2}", this);
            });
            
            if (hasLocalPlayer && localPlayer.realSlotIndex == winnerRealSlotIndex)
            {
                localPlayer.stack = newStackAmount;
            }
        }
    }
    
    /// <summary>
    /// ClientRpc - Creates side pot visual displays on all clients
    /// </summary>
    [ClientRpc]
    private void CreateSidePotsClientRpc(float[] potAmounts)
    {
        Debug.Log($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Creating pot displays — count={potAmounts.Length}", this);
        if (PokerTable.Instance != null)
        {
            PokerTable.Instance.CreateSidePotDisplays(potAmounts);
        }
    }
    
    /// <summary>
    /// ClientRpc - Awards a specific pot (main or side) to a player with animation
    /// </summary>
    [ClientRpc]
    private void AwardSidePotClientRpc(int potIndex, int winnerRealSlotIndex, float potAmount, float newStackAmount)
    {
        Debug.Log($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Awarding side pot — potIndex={potIndex}, amount=${potAmount:F2}, slot={winnerRealSlotIndex}", this);
        
        int visualSlotIndex = GetVisualSlotIndex(winnerRealSlotIndex);
        PlayerSlotUI playerSlot = GetPlayerSlotUI(winnerRealSlotIndex);
        
        if (playerSlot != null && PokerTable.Instance != null)
        {
            PokerTable.Instance.AnimateSpecificPotToPlayer(potIndex, visualSlotIndex, potAmount, () =>
            {
                playerSlot.UpdateStackDisplay(newStackAmount);
                Debug.Log($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Side pot delivered — potIndex={potIndex}, newStack=${newStackAmount:F2}", this);
            });
            
            if (hasLocalPlayer && localPlayer.realSlotIndex == winnerRealSlotIndex)
            {
                localPlayer.stack = newStackAmount;
            }
        }
    }
    
    /// <summary>
    /// ClientRpc - Clears all side pot visual displays on all clients
    /// </summary>
    [ClientRpc]
    private void ClearSidePotsClientRpc()
    {
        Debug.Log($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Clearing side pot displays", this);
        if (PokerTable.Instance != null)
        {
            PokerTable.Instance.ClearSidePots();
        }
    }
    
    /// <summary>
    /// Server function to collect all bets and animate them to the pot on all clients
    /// </summary>
    public void CollectBetsToTablePot()
    {
        if (IsServer)
        {
            // If called on server, trigger the ClientRpc directly
            CollectBetsToTableClientRpc();
        }
        else
        {
            // If called on client, request server to do it
            CollectBetsToTableServerRpc();
        }
    }
    
    /// <summary>
    /// ServerRpc - Client requests to collect bets
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    private void CollectBetsToTableServerRpc()
    {
        // Server triggers the animation on all clients
        CollectBetsToTableClientRpc();
    }
    
    /// <summary>
    /// ClientRpc - Triggers bet collection animation on all clients
    /// </summary>
    [ClientRpc]
    private void CollectBetsToTableClientRpc()
    {
        Debug.Log($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Collecting bets to pot", this);
        
        if (PokerTable.Instance != null)
        {
            PokerTable.Instance.CollectPotFromSlots();
        }
        else
        {
            Debug.LogError($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Collect bets failed — reason=PokerTable instance is <none>", this);
        }
    }
    
    // ==================== BETTING ROUND SYSTEM ====================
    
    /// <summary>
    /// Starts a new betting round - initializes all players betting state
    /// </summary>
    private void StartBettingRound()
    {
        if (!IsServer) return;
        
        Debug.Log($"[{LogTag.Game}] Server: Initializing betting round state", this);
        
        // Reset phase to PreFlop for new hand
        currentPhase.Value = GamePhase.PreFlop;
        
        // Reset community cards
        communityCard1.Value = new Card(Card.Rank.None, Card.Suit.None);
        communityCard2.Value = new Card(Card.Rank.None, Card.Suit.None);
        communityCard3.Value = new Card(Card.Rank.None, Card.Suit.None);
        communityCard4.Value = new Card(Card.Rank.None, Card.Suit.None);
        communityCard5.Value = new Card(Card.Rank.None, Card.Suit.None);
        
        // Hide community cards and clear side pots on all clients
        ClearCommunityCardsClientRpc();
        ClearSidePotsClientRpc();
        
        // Reset betting state for all players
        for (int i = 0; i < players.Count; i++)
        {
            PokerPlayer player = players[i];
            player.currentBet = 0;
            player.hasActed = false;
            player.isPlaying = true; // All players start as playing
            player.isAllIn = false;  // Reset all-in status
            player.totalBetInHand = 0; // Reset total bet tracking for new hand
            players[i] = player;
        }
        
        // Set current bet to call as big blind
        currentBetToCall.Value = bigBlind;
        lastRaiseAmount.Value = bigBlind;
        wasRaiseThisRound.Value = false;
        lastRaiserIndex = -1;
        
        CountActiveAndAllInPlayers(out int activeAfterInit, out int allInAfterInit);
        Debug.Log($"[{LogTag.Game}] Server: Betting round state initialized — activePlayers={activeAfterInit}, count={players.Count}", this);
    }
    
    /// <summary>
    /// Sets the turn to a specific player and starts their timer
    /// </summary>
    private void SetPlayerTurn(int playerIndex)
    {
        if (!IsServer) return;
        
        // Cancel any existing timer
        if (currentTurnTimerCoroutine != null)
        {
            StopCoroutine(currentTurnTimerCoroutine);
            currentTurnTimerCoroutine = null;
        }
        
        currentPlayerTurnIndex.Value = playerIndex;
        
        PokerPlayer currentPlayer = players[playerIndex];
        Debug.Log($"[{LogTag.Game}] Server: Player turn set — player={currentPlayer.playerName}, index={playerIndex}, timer={playerTurnTimeLimit}s", this);
        
        // Start server-side timer for this player
        currentTurnTimerCoroutine = StartCoroutine(TurnTimerCoroutine(playerIndex, currentPlayer.realSlotIndex));
        
        // Notify all clients to update UI
        SetPlayerTurnClientRpc(playerIndex);
    }
    
    /// <summary>
    /// Server-side coroutine that tracks turn time and auto-folds on timeout
    /// </summary>
    private System.Collections.IEnumerator TurnTimerCoroutine(int playerIndex, int playerSlotIndex)
    {
        float timeRemaining = playerTurnTimeLimit;
        
        // Phase 1: Main timer countdown
        while (timeRemaining > 0f)
        {
            // Bail out early if the game ended or the turn moved elsewhere
            if (!isGameStarted.Value || currentPlayerTurnIndex.Value != playerIndex)
            {
                currentTurnTimerCoroutine = null;
                yield break;
            }

            timeRemaining -= Time.deltaTime;
            yield return null;
        }
        
        // Main timer expired — check for time bank
        float bankRemaining = timeBankBySlotIndex.ContainsKey(playerSlotIndex)
            ? timeBankBySlotIndex[playerSlotIndex]
            : 0f;
        
        if (bankRemaining > 0f)
        {
            Debug.Log($"[{LogTag.Game}] Server: Main timer expired, using time bank — slot={playerSlotIndex}, bankRemaining={bankRemaining:F1}s", this);
            
            // Notify all clients to start time bank display for this slot
            StartTimeBankClientRpc(playerSlotIndex, bankRemaining);
            
            // Phase 2: Time bank countdown
            // Write remaining time to the dictionary every frame so the value
            // stays accurate even if SetPlayerTurn force-stops this coroutine.
            while (bankRemaining > 0f)
            {
                if (!isGameStarted.Value || currentPlayerTurnIndex.Value != playerIndex)
                {
                    // Player acted or game ended — save remaining bank
                    timeBankBySlotIndex[playerSlotIndex] = Mathf.Max(0f, bankRemaining);
                    StopTimeBankClientRpc(playerSlotIndex, Mathf.Max(0f, bankRemaining));
                    currentTurnTimerCoroutine = null;
                    yield break;
                }
                
                bankRemaining -= Time.deltaTime;
                timeBankBySlotIndex[playerSlotIndex] = Mathf.Max(0f, bankRemaining);
                yield return null;
            }
            
            // Time bank fully depleted
            timeBankBySlotIndex[playerSlotIndex] = 0f;
            StopTimeBankClientRpc(playerSlotIndex, 0f);
            Debug.Log($"[{LogTag.Game}] Server: Time bank depleted — slot={playerSlotIndex}, action=auto-fold", this);
        }
        else
        {
            Debug.Log($"[{LogTag.Game}] Server: Timer expired — index={playerIndex}, slot={playerSlotIndex}, timeBank=0s, action=auto-fold", this);
        }
        
        // Time expired (both main timer and time bank) - auto-fold this player
        if (currentPlayerTurnIndex.Value == playerIndex)
        {
            // Safety: ensure the index is still valid (player list may have shrunk)
            if (playerIndex < 0 || playerIndex >= players.Count)
            {
                Debug.LogWarning($"[{LogTag.Game}] Server: Stale timer at expiry — playerIndex={playerIndex}, playerCount={players.Count}, action=recover", this);
                currentTurnTimerCoroutine = null;
                
                // Defensive recovery: the player list changed under us and the index
                // is stale. Don't auto-fold a wrong seat — instead re-evaluate the
                // round so the hand keeps moving instead of freezing the table.
                if (isGameStarted.Value && !CheckForSinglePlayerWin() && !IsBettingRoundComplete())
                {
                    int recoverIndex = GetNextActivePlayerIndex(currentPlayerTurnIndex.Value);
                    if (recoverIndex >= 0)
                    {
                        SetPlayerTurn(recoverIndex);
                    }
                    else
                    {
                        EndBettingRoundAndCollect();
                    }
                }
                yield break;
            }

            // Auto-fold the player
            PokerPlayer player = players[playerIndex];
            player.isPlaying = false;
            player.hasActed = true;
            players[playerIndex] = player;
            LogPlayerDeactivation(playerSlotIndex, "timeout auto-fold");
            
            Debug.Log($"[{LogTag.Game}] Server: Player auto-folded — player={player.playerName}, reason=timeout", this);
            
            // Notify all clients to show action and folded state
            PlayerActionClientRpc(playerSlotIndex, "Fold (Timeout)", 0, (int)currentPhase.Value);
            ShowFoldedStateClientRpc(playerSlotIndex);
            
            // Move to next player
            currentTurnTimerCoroutine = null; // Clear reference before moving
            MoveToNextPlayer();
        }
        else
        {
            Debug.Log($"[{LogTag.Game}] Server: Timer expired — ignored, player already acted", this);
            currentTurnTimerCoroutine = null;
        }
    }
    
    /// <summary>
    /// ClientRpc - Updates UI to show whose turn it is and starts their timer
    /// </summary>
    [ClientRpc]
    private void SetPlayerTurnClientRpc(int playerIndex)
    {
        Debug.Log($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Player turn updated — playerIndex={playerIndex}", this);
        
        if (PokerTable.Instance == null) return;
        
        // Stop all timers first
        PlayerSlotUI[] slots = PokerTable.Instance.GetPlayerSlots();
        foreach (var slot in slots)
        {
            if (slot != null)
            {
                slot.StopTimer();
            }
        }
        
        if (playerIndex < 0 || playerIndex >= players.Count) return;
        
        // Start timer for current player
        PokerPlayer currentPlayer = players[playerIndex];
        PlayerSlotUI playerSlot = GetPlayerSlotUI(currentPlayer.realSlotIndex);
        if (playerSlot != null)
        {
            playerSlot.StartTimer();
            Debug.Log($"[{LogTag.Game}] Client {NetworkManager.Singleton.LocalClientId}: Timer started — slot={currentPlayer.realSlotIndex}", this);
        }
    }
    
    // ==================== TIME BANK RPCs ====================
    
    /// <summary>
    /// Sends the current time bank value to a specific client (e.g. on sit-down).
    /// </summary>
    [ClientRpc]
    private void SendTimeBankToClientRpc(int slotIndex, float remaining, ClientRpcParams rpcParams = default)
    {
        if (PokerTable.Instance == null) return;
        PlayerSlotUI slot = GetPlayerSlotUI(slotIndex);
        if (slot != null)
            slot.SetTimeBankDisplay(remaining);
    }
    
    /// <summary>
    /// Tells all clients to start the time bank countdown + wobble for a given slot.
    /// </summary>
    [ClientRpc]
    private void StartTimeBankClientRpc(int slotIndex, float remaining)
    {
        if (PokerTable.Instance == null) return;
        PlayerSlotUI slot = GetPlayerSlotUI(slotIndex);
        if (slot != null)
            slot.StartTimeBankCountdown(remaining);

        // Play low-time-bank warning sound for the local player when their
        // remaining time bank drops below 25% of the session total.
        bool isLocalPlayerSlot = hasLocalPlayer && localPlayer.realSlotIndex == slotIndex;
        if (isLocalPlayerSlot && playerTimeBankTotal > 0f && remaining < playerTimeBankTotal * 0.25f)
        {
            if (PokerSoundEffects.Instance != null)
                PokerSoundEffects.Instance.PlayTimeBankLow();
        }
    }
    
    /// <summary>
    /// Tells all clients to stop the time bank visual and update remaining seconds.
    /// </summary>
    [ClientRpc]
    private void StopTimeBankClientRpc(int slotIndex, float remaining)
    {
        if (PokerTable.Instance == null) return;
        PlayerSlotUI slot = GetPlayerSlotUI(slotIndex);
        if (slot != null)
        {
            slot.StopTimeBankVisual();
            slot.SetTimeBankDisplay(remaining);
        }
    }
    
    /// <summary>
    /// Server-side: regenerates time bank for all seated players at the start of each round.
    /// Adds 1/30 of the max time bank value, capped at the max.
    /// </summary>
    private void RegenerateTimeBanks()
    {
        if (!IsServer || playerTimeBankTotal <= 0f) return;
        
        float regenAmount = playerTimeBankTotal / 30f;
        
        foreach (var player in players)
        {
            int slot = player.realSlotIndex;
            if (!timeBankBySlotIndex.ContainsKey(slot))
            {
                timeBankBySlotIndex[slot] = playerTimeBankTotal;
            }
            else
            {
                timeBankBySlotIndex[slot] = Mathf.Min(
                    timeBankBySlotIndex[slot] + regenAmount,
                    playerTimeBankTotal);
            }
            
            // Notify all clients of updated time bank
            StopTimeBankClientRpc(slot, timeBankBySlotIndex[slot]);
        }
    }
    
    /// <summary>
    /// Gets the index of the next active player who can act (not folded and not all-in)
    /// </summary>
    private int GetNextActivePlayerIndex(int currentIndex)
    {
        if (players.Count == 0) return -1;
        
        int startIndex = (currentIndex + 1) % players.Count;
        int checkIndex = startIndex;
        
        do
        {
            // Player can act if they're still playing AND not all-in
            if (players[checkIndex].isPlaying && !players[checkIndex].isAllIn)
            {
                return checkIndex;
            }
            checkIndex = (checkIndex + 1) % players.Count;
        } while (checkIndex != startIndex);
        
        return -1; // No active players found who can act
    }
    
    /// <summary>
    /// Helper to get player list index from slot index
    /// </summary>
    private int GetPlayerIndexBySlotIndex(int slotIndex)
    {
        for (int i = 0; i < players.Count; i++)
        {
            if (players[i].realSlotIndex == slotIndex)
            {
                return i;
            }
        }
        return -1;
    }
    
    /// <summary>
    /// Checks if only one player remains playing and awards them the pot
    /// </summary>
    /// <returns>True if only one player remains and round was ended</returns>
    private bool CheckForSinglePlayerWin()
    {
        if (!IsServer) return false;
        
        // Count players still in the hand
        int playingCount = 0;
        int winnerIndex = -1;
        
        for (int i = 0; i < players.Count; i++)
        {
            if (players[i].isPlaying)
            {
                playingCount++;
                winnerIndex = i;
            }
        }
        
        // If only one player remains, they win by default
        if (playingCount == 1 && winnerIndex != -1)
        {
            Debug.Log($"[{LogTag.Game}] Server: Single player remains — winner={players[winnerIndex].playerName}, reason=all others folded", this);
            
            CancelTurnAndNotifyClients();
            // Use realSlotIndex instead of list index to survive list modifications during coroutine delays
            StartCoroutine(CollectBetsAndAwardWinner(players[winnerIndex].realSlotIndex, 1.0f));
            
            return true;
        }
        
        return false;
    }
    
    /// <summary>
    /// Checks if the betting round is complete
    /// </summary>
    private bool IsBettingRoundComplete()
    {
        int activePlayers = 0;
        int playersWhoActed = 0;
        int allInPlayers = 0;
        
        foreach (var player in players)
        {
            if (player.isPlaying)
            {
                activePlayers++;
                
                if (player.isAllIn)
                {
                    allInPlayers++;
                }
                
                // All-in players always count as having acted (they cannot take any further action).
                // Non-all-in players must have explicitly acted AND matched the current bet.
                if (player.isAllIn || (player.hasActed && player.currentBet == currentBetToCall.Value))
                {
                    playersWhoActed++;
                }
            }
        }
        
        // Round complete if only one player left
        if (activePlayers <= 1) return true;
        
        // Round complete if all players except at most one are all-in (no more betting action possible)
        if (allInPlayers >= activePlayers - 1 && playersWhoActed >= activePlayers) return true;
        
        // Round complete if all active players have acted and matched the bet (or are all-in)
        if (playersWhoActed >= activePlayers) return true;
        
        return false;
    }
    
    /// <summary>
    /// Returns the uncalled portion of the highest bet to its owner.
    /// The highest bettor can only win up to the second-highest bet from each opponent,
    /// so any excess above the second-highest bet is returned.
    /// Works for all cases: all players all-in, one not all-in, mixed stacks, etc.
    /// </summary>
    private void ReturnUncalledBet()
    {
        if (!IsServer) return;
        
        // Gather all active players' bets with their indices
        float highestBet = 0f;
        float secondHighestBet = 0f;
        int highestBetPlayerIndex = -1;
        
        for (int i = 0; i < players.Count; i++)
        {
            if (!players[i].isPlaying) continue;
            
            float bet = players[i].currentBet;
            
            if (bet > highestBet)
            {
                // Current highest becomes second highest
                secondHighestBet = highestBet;
                highestBet = bet;
                highestBetPlayerIndex = i;
            }
            else if (bet > secondHighestBet)
            {
                secondHighestBet = bet;
            }
        }
        
        // Need at least 2 active players and a difference to return
        if (highestBetPlayerIndex == -1) return;
        
        float uncalledAmount = highestBet - secondHighestBet;
        
        if (uncalledAmount > 0.01f)
        {
            PokerPlayer player = players[highestBetPlayerIndex];
            Debug.Log($"[{LogTag.Game}] Server: Returning uncalled bet — player={player.playerName}, uncalled=${uncalledAmount:F2}, playerBet=${player.currentBet:F2}, secondHighest=${secondHighestBet:F2}", this);
            
            PokerPlayer updatedPlayer = player;
            updatedPlayer.stack += uncalledAmount;
            updatedPlayer.currentBet -= uncalledAmount;
            players[highestBetPlayerIndex] = updatedPlayer;
            
            // Subtract the returned amount from the pot
            potOnTable.Value -= uncalledAmount;
            
            // Update stack display on all clients
            UpdateStackClientRpc(player.realSlotIndex, updatedPlayer.stack);

            // Record the return for hand history so CalculatePlayerInvestment
            // can subtract it from the player's total investment.
            PlayerActionClientRpc(player.realSlotIndex, "Uncalled Bet Return",
                uncalledAmount, (int)currentPhase.Value);
            
            Debug.Log($"[{LogTag.Game}] Server: Uncalled bet returned — player={player.playerName}, stack=${updatedPlayer.stack:F2}, currentBet=${updatedPlayer.currentBet:F2}, pot=${potOnTable.Value:F2}", this);
        }
    }
    
    /// <summary>
    /// Re-resolves <see cref="currentPlayerTurnIndex"/> after the <c>players</c>
    /// NetworkList has been re-ordered or shrunk, so the turn keeps pointing at the
    /// same physical seat (identified by <paramref name="turnRealSlotIndex"/>).
    /// The list is index-based, so adding/removing a player whose list position is
    /// below the active player shifts the active player's index. Without this fixup
    /// the server turn timer would track the wrong seat — or a stale/out-of-range
    /// index — which can freeze the hand until someone re-seats. Restarts the server
    /// turn timer and notifies clients only when the index actually changed.
    /// No-op when the seat can't be found or the index is unchanged.
    /// </summary>
    private void RebindTurnIndexToSlot(int turnRealSlotIndex)
    {
        if (!IsServer || turnRealSlotIndex < 0) return;
        
        int newTurnIndex = GetPlayerIndexBySlotIndex(turnRealSlotIndex);
        if (newTurnIndex < 0 || newTurnIndex == currentPlayerTurnIndex.Value) return;
        
        Debug.Log($"[{LogTag.Game}] Server: Rebinding turn index after player-list change — oldIndex={currentPlayerTurnIndex.Value}, newIndex={newTurnIndex}, slot={turnRealSlotIndex}", this);
        
        // Cancel the old timer (it references a stale playerIndex)
        if (currentTurnTimerCoroutine != null)
        {
            StopCoroutine(currentTurnTimerCoroutine);
            currentTurnTimerCoroutine = null;
        }
        
        currentPlayerTurnIndex.Value = newTurnIndex;
        
        // Restart the timer with the corrected index
        currentTurnTimerCoroutine = StartCoroutine(TurnTimerCoroutine(newTurnIndex, turnRealSlotIndex));
        
        // Notify clients of the corrected turn (so timers/actions point to the right slot)
        SetPlayerTurnClientRpc(newTurnIndex);
    }
    
    /// <summary>
    /// Cancels the turn timer, resets the turn index, and notifies clients to stop timers
    /// </summary>
    private void CancelTurnAndNotifyClients()
    {
        if (currentTurnTimerCoroutine != null)
        {
            StopCoroutine(currentTurnTimerCoroutine);
            currentTurnTimerCoroutine = null;
        }
        
        currentPlayerTurnIndex.Value = -1;
        SetPlayerTurnClientRpc(-1);
    }
    
    /// <summary>
    /// Ends the current betting round: cancels timers, returns uncalled bets, and collects bets to pot
    /// </summary>
    private void EndBettingRoundAndCollect()
    {
        CancelTurnAndNotifyClients();
        ReturnUncalledBet();
        StartCoroutine(CollectBetsAfterDelay(collectBetAnimationDelay));
    }
    
    /// <summary>
    /// Moves to the next player's turn or ends the betting round
    /// </summary>
    private void MoveToNextPlayer()
    {
        if (!IsServer) return;
        
        // Check if only one player remains (everyone else folded)
        if (CheckForSinglePlayerWin())
        {
            return; // Round ended early, winner determined
        }
        
        // Check if betting round is complete
        if (IsBettingRoundComplete())
        {
            Debug.Log($"[{LogTag.Game}] Server: Betting round complete", this);
            EndBettingRoundAndCollect();
            return;
        }
        
        // Get next active player
        int nextPlayerIndex = GetNextActivePlayerIndex(currentPlayerTurnIndex.Value);
        
        if (nextPlayerIndex == -1)
        {
            // No players can act (all are folded or all-in)
            Debug.Log($"[{LogTag.Game}] Server: Ending betting round — reason=no active players can act", this);
            EndBettingRoundAndCollect();
            return;
        }
        
        SetPlayerTurn(nextPlayerIndex);
    }
    
    /// <summary>
    /// Coroutine that waits for bet animations to finish before collecting bets to pot
    /// </summary>
    private System.Collections.IEnumerator CollectBetsAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        
        Debug.Log($"[{LogTag.Game}] Server: Collecting bets to pot — animations complete", this);
        CollectBetsToTablePot();
        
        // Wait for pot collection animation to complete
        yield return new WaitForSeconds(1.2f);
        
        // Move to next phase
        MoveToNextPhase();
    }
    
    /// <summary>
    /// Coroutine that waits before moving to the next phase (used when all players are all-in)
    /// </summary>
    private System.Collections.IEnumerator MoveToNextPhaseAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        MoveToNextPhase();
    }
    
    /// <summary>
    /// Coroutine that collects bets to pot and awards pot to winner.
    /// Uses realSlotIndex instead of list index to survive list modifications during delays.
    /// </summary>
    private System.Collections.IEnumerator CollectBetsAndAwardWinner(int winnerRealSlotIndex, float delay)
    {
        // Capture hand number to detect if a new round started while we were waiting
        int handAtStart = currentHandNumber;

        // Wait for bet animations to complete
        yield return new WaitForSeconds(delay);
        if (currentHandNumber != handAtStart) yield break; // New round started — stale coroutine
        // Game may have ended (EndGameServerRpc already finalized the hand)
        if (!isGameStarted.Value) yield break;
        
        if (TryGetPlayerByRealSlotIndex(winnerRealSlotIndex, out PokerPlayer winner))
        {
            Debug.Log($"[{LogTag.Game}] Server: Collecting bets for winner — player={winner.playerName}", this);
        }
        CollectBetsToTablePot();
        
        // Wait for collection animation
        yield return new WaitForSeconds(1.2f);
        if (currentHandNumber != handAtStart) yield break;
        if (!isGameStarted.Value) yield break;
        
        // Award pot to winner using their real slot index
        float potAmount = potOnTable.Value;
        float observeDelay = GetPostAwardObserveDelay(potAmount);
        Debug.Log($"[{LogTag.Game}] Server: Awarding pot to winner — pot=${potAmount:F2}, slot={winnerRealSlotIndex}, observeDelay={observeDelay:F2}s", this);
        AwardPotToPlayer(winnerRealSlotIndex);

        // Wait for pot award animation to complete
        yield return new WaitForSeconds(1.0f);
        if (currentHandNumber != handAtStart) yield break;

        // Scaled window for voluntary reveal / observe before next hand
        yield return new WaitForSeconds(observeDelay);
        if (currentHandNumber != handAtStart) yield break;

        CleanupAndStartNextRound();
    }
    
    /// <summary>
    /// Coroutine that evaluates hands at showdown and awards pot to winner
    /// </summary>
    /// <summary>
    /// Returns a multiplier in [1, <see cref="potScaleMaxMultiplier"/>] that
    /// scales showdown reveal/observe durations based on how large the pot is
    /// relative to the big blind.
    ///   pot &lt; potScaleMinBB  BB  → 1×
    ///   pot in [min, max] BB     → lerped 1× … potScaleMaxMultiplier×
    ///   pot &gt; potScaleMaxBB BB  → potScaleMaxMultiplier×
    /// </summary>
    private float GetPotScaleMultiplier(float potAmount)
    {
        if (bigBlind <= 0f) return 1f;
        float potInBB = potAmount / bigBlind;
        float t = Mathf.InverseLerp(potScaleMinBB, potScaleMaxBB, potInBB);
        return Mathf.Lerp(1f, potScaleMaxMultiplier, t);
    }

    private float GetPostAwardObserveDelay(float potAmount)
    {
        float potScale = GetPotScaleMultiplier(potAmount);
        return showdownObserveTime * potScale;
    }

    private System.Collections.IEnumerator EvaluateShowdownAndAwardWinner(float delay)
    {
        // Capture hand number to detect if a new round started while we were waiting
        int handAtStart = currentHandNumber;

        // Wait for bet animations to complete
        yield return new WaitForSeconds(delay);
        if (currentHandNumber != handAtStart) yield break; // New round started — stale coroutine
        // Game may have ended (EndGameServerRpc already finalized the hand)
        if (!isGameStarted.Value) yield break;
        
        Debug.Log($"[{LogTag.Game}] Server: Evaluating hands at showdown", this);
        
        // Accumulate final round bets into totalBetInHand
        AccumulateBetsToTotal();
        
        // Collect bets to pot visually
        CollectBetsToTablePot();

        // Compute a pot-size multiplier for reveal/observe durations once —
        // potOnTable is finalised after AccumulateBetsToTotal + CollectBetsToTablePot.
        float finalPotAmount = potOnTable.Value;
        float potScale = GetPotScaleMultiplier(finalPotAmount);
        float observeDelay = GetPostAwardObserveDelay(finalPotAmount);
        Debug.Log($"[{LogTag.Game}] Server: Showdown pot scale — pot={finalPotAmount:F2}, bigBlind={bigBlind:F2}, scale={potScale:F2}x, observeDelay={observeDelay:F2}s", this);
        
        // Wait for collection animation
        yield return new WaitForSeconds(0.8f);
        
        // Get all active players still in the hand
        PokerPlayer[] activePlayers = GetActivePlayers();
        
        if (activePlayers.Length == 0)
        {
            Debug.LogError($"[{LogTag.Game}] Server: No active players at showdown \u2014 ensure players are not removed before evaluation", this);
            yield break;
        }
        
        if (activePlayers.Length == 1)
        {
            // Only one player left, they win by default
            Debug.Log($"[{LogTag.Game}] Server: Single player remaining at showdown — player={activePlayers[0].playerName}", this);
            AwardPotToPlayer(activePlayers[0].realSlotIndex);
            yield return new WaitForSeconds(observeDelay);
            CleanupAndStartNextRound();
            yield break;
        }
        
        // Multiple players at showdown - reveal all their cards
        Debug.Log($"[{LogTag.Game}] Server: Showdown revealing cards \u2014 playerCount={activePlayers.Length}", this);
        RevealAllPlayersCards();
        
        // Wait for card reveal animations (base time scaled by pot size).
        float revealTime = activePlayers.Length * showdownRevealTimePerPlayer + showdownRevealBaseTime * potScale;
        yield return new WaitForSeconds(revealTime);
        
        // Get community cards and hand evaluator
        Card[] communityCards = GetCommunityCardsArray();
        HandStrength handEvaluator = new HandStrength();
        
        // Calculate side pots
        List<SidePot> sidePots = CalculateSidePots();
        
        if (sidePots.Count <= 1)
        {
            // Single pot (no side pots needed) - award to best hand
            PokerPlayer[] sortedPlayers = handEvaluator.SortPlayersByHandStrength(activePlayers, communityCards);
            
            // Check for split pot (tie)
            List<PokerPlayer> winners = new List<PokerPlayer> { sortedPlayers[0] };
            for (int i = 1; i < sortedPlayers.Length; i++)
            {
                if (handEvaluator.ComparePlayerHands(sortedPlayers[0], sortedPlayers[i], communityCards) == 0)
                    winners.Add(sortedPlayers[i]);
                else
                    break;
            }
            
            if (winners.Count == 1)
            {
                Debug.Log($"[{LogTag.Game}] Server: Showdown winner determined \u2014 player={winners[0].playerName}", this);
                AwardPotToPlayer(winners[0].realSlotIndex);
            }
            else
            {
                // Split pot equally among winners
                float splitAmount = potOnTable.Value / winners.Count;
                Debug.Log($"[{LogTag.Game}] Server: Split pot at showdown \u2014 winners={winners.Count}, each=${splitAmount:F2}", this);
                for (int w = 0; w < winners.Count; w++)
                {
                    AwardAmountToPlayer(winners[w].realSlotIndex, splitAmount);
                    if (w == 0)
                    {
                        TryOpenVoluntaryRevealWindow();
                        // Animate main pot to first winner
                        AwardPotToPlayerClientRpc(winners[w].realSlotIndex, splitAmount, GetPlayerStack(winners[w].realSlotIndex));
                    }
                    else
                    {
                        // Just update stack display for other winners
                        UpdateStackClientRpc(winners[w].realSlotIndex, GetPlayerStack(winners[w].realSlotIndex));
                    }
                }
                potOnTable.Value = 0;
            }
        }
        else
        {
            // Multiple pots - create side pot visuals on all clients
            float[] potAmounts = new float[sidePots.Count];
            for (int i = 0; i < sidePots.Count; i++)
                potAmounts[i] = sidePots[i].amount;
            
            CreateSidePotsClientRpc(potAmounts);
            yield return new WaitForSeconds(0.5f);
            
            // Award each pot to its winner(s)
            for (int p = 0; p < sidePots.Count; p++)
            {
                SidePot pot = sidePots[p];
                
                // Get eligible players for this pot
                PokerPlayer[] eligiblePlayers = new PokerPlayer[pot.eligiblePlayerIndices.Count];
                for (int j = 0; j < pot.eligiblePlayerIndices.Count; j++)
                {
                    eligiblePlayers[j] = players[pot.eligiblePlayerIndices[j]];
                }
                
                // Sort eligible players by hand strength
                PokerPlayer[] sortedEligible = handEvaluator.SortPlayersByHandStrength(eligiblePlayers, communityCards);
                
                // Check for split among tied winners
                List<PokerPlayer> potWinners = new List<PokerPlayer> { sortedEligible[0] };
                for (int i = 1; i < sortedEligible.Length; i++)
                {
                    if (handEvaluator.ComparePlayerHands(sortedEligible[0], sortedEligible[i], communityCards) == 0)
                        potWinners.Add(sortedEligible[i]);
                    else
                        break;
                }
                
                if (potWinners.Count == 1)
                {
                    // Single winner for this pot
                    PokerPlayer potWinner = potWinners[0];
                    Debug.Log($"[{LogTag.Game}] Server: Side pot winner \u2014 potIndex={p}, amount=${pot.amount:F2}, player={potWinner.playerName}", this);
                    AwardAmountToPlayer(potWinner.realSlotIndex, pot.amount);
                    if (p == 0)
                        TryOpenVoluntaryRevealWindow();
                    AwardSidePotClientRpc(p, potWinner.realSlotIndex, pot.amount, GetPlayerStack(potWinner.realSlotIndex));
                }
                else
                {
                    // Split this pot among tied winners
                    float splitAmount = pot.amount / potWinners.Count;
                    Debug.Log($"[{LogTag.Game}] Server: Side pot split \u2014 potIndex={p}, winners={potWinners.Count}, each=${splitAmount:F2}", this);
                    
                    // Award split to first winner with animation, rest without
                    for (int w = 0; w < potWinners.Count; w++)
                    {
                        AwardAmountToPlayer(potWinners[w].realSlotIndex, splitAmount);
                        if (w == 0)
                        {
                            if (p == 0)
                                TryOpenVoluntaryRevealWindow();
                            AwardSidePotClientRpc(p, potWinners[w].realSlotIndex, splitAmount, GetPlayerStack(potWinners[w].realSlotIndex));
                        }
                        else
                        {
                            UpdateStackClientRpc(potWinners[w].realSlotIndex, GetPlayerStack(potWinners[w].realSlotIndex));
                        }
                    }
                }
                
                // Wait for pot award animation
                yield return new WaitForSeconds(sidePotAwardInterval);
            }
            
            // Clear remaining side pot visuals
            ClearSidePotsClientRpc();
        }

        yield return new WaitForSeconds(observeDelay);
        CleanupAndStartNextRound();
    }
    
    /// <summary>
    /// Gets a player's current stack by their real slot index
    /// </summary>
    private float GetPlayerStack(int realSlotIndex)
    {
        for (int i = 0; i < players.Count; i++)
        {
            if (players[i].realSlotIndex == realSlotIndex)
                return players[i].stack;
        }
        return 0f;
    }
    
    /// <summary>
    /// Removes all players with 0 stack from the game (bust out)
    /// </summary>
    private void RemovePlayersWithZeroStack()
    {
        if (!IsServer) return;
        
        Debug.Log($"[{LogTag.Game}] Server: Checking for busted players", this);
        
        // Create list of players to remove (can't modify collection while iterating)
        List<PokerPlayer> playersToRemove = new List<PokerPlayer>();
        
        for (int i = 0; i < players.Count; i++)
        {
            if (players[i].stack <= 0)
            {
                playersToRemove.Add(players[i]);
                Debug.Log($"[{LogTag.Game}] Server: Player busted out \u2014 player={players[i].playerName}, stack=${players[i].stack:F2}", this);
            }
        }
        
        // Remove each player with 0 stack
        foreach (var player in playersToRemove)
        {
            // Get client ID for this player's slot
            if (slotToClientId.TryGetValue(player.realSlotIndex, out ulong clientId))
            {
                CashOutPlayerToAvailableBalance(player, clientId);

                // Clear time bank for busted player
                timeBankBySlotIndex.Remove(player.realSlotIndex);

                // Remove player and notify clients
                RemovePlayer(player);
                
                // Remove client ID mapping
                slotToClientId.Remove(player.realSlotIndex);
                
                // Notify all clients to update UI (bust-out mode: don't restore positions)
                RemovePlayerSlotClientRpc(player.realSlotIndex, clientId, true);
            }
            else
            {
                Debug.LogWarning($"[{LogTag.Game}] Server: Client ID not found for busted player \u2014 player={player.playerName}, slot={player.realSlotIndex}", this);
                timeBankBySlotIndex.Remove(player.realSlotIndex);
                RemovePlayer(player);
            }
        }
        
        if (playersToRemove.Count > 0)
        {
            Debug.Log($"[{LogTag.Game}] Server: Busted players removed \u2014 removed={playersToRemove.Count}, remaining={players.Count}", this);
        }
    }
    
    // ==================== PLAYER ACTIONS ====================
    
    /// <summary>
    /// Client calls this to fold
    /// </summary>
    public void Fold()
    {
        if (!hasLocalPlayer) return;
        FoldServerRpc(localPlayer.realSlotIndex);
    }
    
    [ServerRpc(RequireOwnership = false)]
    private void FoldServerRpc(int playerSlotIndex)
    {
        int playerIndex = GetPlayerIndexBySlotIndex(playerSlotIndex);
        if (playerIndex == -1 || playerIndex != currentPlayerTurnIndex.Value)
        {
            Debug.LogWarning($"[{LogTag.Game}] Server: Invalid fold attempt \u2014 slot={playerSlotIndex}", this);
            return;
        }
        
        PokerPlayer player = players[playerIndex];
        player.isPlaying = false;
        player.hasActed = true;
        players[playerIndex] = player;
        LogPlayerDeactivation(playerSlotIndex, "fold");
        
        Debug.Log($"[{LogTag.Game}] Server: Player folded \u2014 player={player.playerName}, slot={playerSlotIndex}", this);
        
        // Notify all clients to show action and folded state
        PlayerActionClientRpc(playerSlotIndex, "Fold", 0, (int)currentPhase.Value);
        ShowFoldedStateClientRpc(playerSlotIndex);
        
        // Move to next player
        MoveToNextPlayer();
    }
    
    /// <summary>
    /// Client calls this to check or call
    /// </summary>
    public void Check()
    {
        if (!hasLocalPlayer) return;
        CheckCallServerRpc(localPlayer.realSlotIndex);
    }
    
    [ServerRpc(RequireOwnership = false)]
    private void CheckCallServerRpc(int playerSlotIndex)
    {
        int playerIndex = GetPlayerIndexBySlotIndex(playerSlotIndex);
        if (playerIndex == -1 || playerIndex != currentPlayerTurnIndex.Value)
        {
            Debug.LogWarning($"[{LogTag.Game}] Server: Invalid check/call attempt \u2014 slot={playerSlotIndex}", this);
            return;
        }
        
        PokerPlayer player = players[playerIndex];
        
        // If there's a bet to call, cannot check
        float amountToCall = currentBetToCall.Value - player.currentBet;
        float actualBetAmount = amountToCall;
        string action;
        
        if (amountToCall > 0)
        {
            // This is a call
            if (player.stack <= amountToCall)
            {
                // Player doesn't have enough (or exactly enough) - go all-in with remaining stack
                actualBetAmount = player.stack;
                player.currentBet += actualBetAmount;
                player.stack = 0;
                player.isAllIn = true;
                action = "All-In (Call)";
                Debug.Log($"[{LogTag.Game}] Server: Player all-in on call \u2014 player={player.playerName}, amount=${actualBetAmount:F2}, requiredCall=${amountToCall:F2}", this);
            }
            else
            {
                // Normal call
                player.stack -= amountToCall;
                player.currentBet += amountToCall;
                
                // Check if player is now all-in (stack is 0 or very close to 0)
                if (player.stack <= 0.01f)
                {
                    player.stack = 0;
                    player.isAllIn = true;
                    action = "All-In (Call)";
                    Debug.Log($"[{LogTag.Game}] Server: Player all-in after call \u2014 player={player.playerName}, stack=$0.00", this);
                }
                else
                {
                    action = "Call";
                    Debug.Log($"[{LogTag.Game}] Server: Player called \u2014 player={player.playerName}, amount=${amountToCall:F2}", this);
                }
            }
            
            potOnTable.Value += actualBetAmount;
        }
        else
        {
            // Check (no bet to call)
            action = "Check";
            Debug.Log($"[{LogTag.Game}] Server: Player checked \u2014 player={player.playerName}", this);
        }
        
        player.hasActed = true;
        players[playerIndex] = player;
        
        // Notify all clients
        PlayerActionClientRpc(playerSlotIndex, action, actualBetAmount, (int)currentPhase.Value);
        
        // Update stack display
        BetClientRpc(playerSlotIndex, actualBetAmount, player.stack);
        
        // Move to next player
        MoveToNextPlayer();
    }
    
    /// <summary>
    /// Client calls this to raise
    /// </summary>
    /// <param name="raiseAmount">Total amount to bet (must be > currentBetToCall)</param>
    public void Raise(float raiseAmount)
    {
        if (!hasLocalPlayer) return;
        RaiseServerRpc(localPlayer.realSlotIndex, raiseAmount);
    }
    
    [ServerRpc(RequireOwnership = false)]
    private void RaiseServerRpc(int playerSlotIndex, float raiseAmount)
    {
        int playerIndex = GetPlayerIndexBySlotIndex(playerSlotIndex);
        if (playerIndex == -1 || playerIndex != currentPlayerTurnIndex.Value)
        {
            Debug.LogWarning($"[{LogTag.Game}] Server: Invalid raise attempt \u2014 slot={playerSlotIndex}", this);
            return;
        }
        
        PokerPlayer player = players[playerIndex];
        
        // Validate raise amount
        if (raiseAmount <= currentBetToCall.Value)
        {
            Debug.LogWarning($"[{LogTag.Game}] Server: Raise amount too low \u2014 raiseAmount=${raiseAmount:F2}, currentBet=${currentBetToCall.Value:F2}", this);
            return;
        }
        
        float totalCost = raiseAmount - player.currentBet;
        string action;
        float actualBetAmount;
        
        if (player.stack <= totalCost)
        {
            // Player doesn't have enough (or exactly enough) to raise - go all-in
            actualBetAmount = player.stack;
            float newTotalBet = player.currentBet + actualBetAmount;
            player.currentBet = newTotalBet;
            player.stack = 0;
            player.isAllIn = true;
            player.hasActed = true;
            
            potOnTable.Value += actualBetAmount;
            
            // Check if this all-in is actually a raise (higher than current bet)
            if (newTotalBet > currentBetToCall.Value)
            {
                float raiseSize = newTotalBet - currentBetToCall.Value;
                currentBetToCall.Value = newTotalBet;
                lastRaiseAmount.Value = raiseSize;
                wasRaiseThisRound.Value = true;
                lastRaiserIndex = playerIndex;
                action = "All-In (Raise)";
                
                // Reset hasActed for other players (they need to respond)
                for (int i = 0; i < players.Count; i++)
                {
                    if (i != playerIndex && players[i].isPlaying && !players[i].isAllIn)
                    {
                        PokerPlayer p = players[i];
                        p.hasActed = false;
                        players[i] = p;
                    }
                }
                
                Debug.Log($"[{LogTag.Game}] Server: Player all-in raise \u2014 player={player.playerName}, totalBet=${newTotalBet:F2}, requestedRaise=${raiseAmount:F2}", this);
            }
            else
            {
                // All-in but not a raise (just a call or less)
                action = "All-In";
                Debug.Log($"[{LogTag.Game}] Server: Player all-in below raise \u2014 player={player.playerName}, totalBet=${newTotalBet:F2}, requestedRaise=${raiseAmount:F2}", this);
            }
            
            players[playerIndex] = player;
            
            // Notify all clients
            // Send the incremental cost (amount taken from stack), not the total bet,
            // so CalculatePlayerInvestment doesn't double-count blinds.
            PlayerActionClientRpc(playerSlotIndex, action, actualBetAmount, (int)currentPhase.Value);
            BetClientRpc(playerSlotIndex, actualBetAmount, player.stack);
        }
        else
        {
            // Normal raise - player has enough
            player.stack -= totalCost;
            player.currentBet = raiseAmount;
            player.hasActed = true;
            
            // Check if player is now all-in (stack is 0 or very close to 0)
            if (player.stack <= 0.01f)
            {
                player.stack = 0;
                player.isAllIn = true;
                Debug.Log($"[{LogTag.Game}] Server: Player all-in after raise \u2014 player={player.playerName}, stack=$0.00", this);
            }
            
            players[playerIndex] = player;
            
            // Update pot and current bet
            potOnTable.Value += totalCost;
            float raiseSize = raiseAmount - currentBetToCall.Value;
            currentBetToCall.Value = raiseAmount;
            lastRaiseAmount.Value = raiseSize;
            wasRaiseThisRound.Value = true;
            lastRaiserIndex = playerIndex;
            
            // Reset hasActed for all other active players who can still act (they need to respond to the raise)
            // All-in players are excluded — they cannot take any further action.
            for (int i = 0; i < players.Count; i++)
            {
                if (i != playerIndex && players[i].isPlaying && !players[i].isAllIn)
                {
                    PokerPlayer p = players[i];
                    p.hasActed = false;
                    players[i] = p;
                }
            }
            
            Debug.Log($"[{LogTag.Game}] Server: Player raised \u2014 player={player.playerName}, raiseTo=${raiseAmount:F2}", this);
            
            // Notify all clients
            // Send the incremental cost (amount taken from stack), not the total bet,
            // so CalculatePlayerInvestment doesn't double-count blinds.
            PlayerActionClientRpc(playerSlotIndex, "Raise", totalCost, (int)currentPhase.Value);
            BetClientRpc(playerSlotIndex, totalCost, player.stack);
        }
        
        // Move to next player
        MoveToNextPlayer();
    }
    
    // ==================== HAND HISTORY CLIENT RPCs ====================

    /// <summary>
    /// ClientRpc — Broadcast at the start of every hand (before blinds).
    /// HandRecorder uses this to begin building a new HandRecord.
    /// </summary>
    [ClientRpc]
    private void HandStartedClientRpc(int handNumber, int dealerSlot, int sbSlot, int bbSlot,
        int[] playerSlots, float[] playerStacks)
    {
        Debug.Log($"[{LogTag.Game}] Client: Hand started \u2014 clientId={NetworkManager.Singleton.LocalClientId}, hand={handNumber}", this);
        OnHandStarted?.Invoke(handNumber, dealerSlot, sbSlot, bbSlot, playerSlots, playerStacks);
    }

    /// <summary>
    /// ClientRpc — Broadcast after the final winner is awarded, before cleanup.
    /// HandRecorder uses this to finalize and save the HandRecord.
    /// </summary>
    [ClientRpc]
    private void HandCompletedClientRpc(int[] winnerSlots, float[] winnerAmounts, float potTotal,
        int[] ccRanks, int[] ccSuits,
        int[] wCard1Ranks, int[] wCard1Suits, int[] wCard2Ranks, int[] wCard2Suits, bool cardsRevealed)
    {
        Debug.Log($"[{LogTag.Game}] Client: Hand completed \u2014 clientId={NetworkManager.Singleton.LocalClientId}, winners={winnerSlots.Length}, pot=${potTotal:F2}", this);
        OnHandCompleted?.Invoke(winnerSlots, winnerAmounts, potTotal, ccRanks, ccSuits,
            wCard1Ranks, wCard1Suits, wCard2Ranks, wCard2Suits, cardsRevealed);
    }

    /// <summary>
    /// Server-side helper: captures community cards and broadcasts HandCompletedClientRpc.
    /// Called at the start of CleanupAndStartNextRound, before any state is modified.
    /// </summary>
    private void BroadcastHandCompleted()
    {
        if (!IsServer) return;

        // Snapshot community cards before they get reset
        int[] ccRanks = new int[5];
        int[] ccSuits = new int[5];
        Card[] cc = {
            communityCard1.Value, communityCard2.Value,
            communityCard3.Value, communityCard4.Value,
            communityCard5.Value
        };
        for (int i = 0; i < 5; i++)
        {
            ccRanks[i] = (int)cc[i].rank;
            ccSuits[i] = (int)cc[i].suit;
        }

        float pot = 0f;
        foreach (float amt in currentHandWinnerAmounts)
            pot += amt;

        int winnerCount = currentHandWinnerSlots.Count;
        int[] wCard1Ranks = new int[winnerCount];
        int[] wCard1Suits = new int[winnerCount];
        int[] wCard2Ranks = new int[winnerCount];
        int[] wCard2Suits = new int[winnerCount];
        bool cardsRevealedForHistory = revealedPlayerCards;

        for (int i = 0; i < winnerCount; i++)
        {
            int slot = currentHandWinnerSlots[i];
            bool found = false;
            foreach (var p in players)
            {
                if (p.realSlotIndex == slot)
                {
                    GetWinnerCardDataForHistory(slot, p,
                        out wCard1Ranks[i], out wCard1Suits[i],
                        out wCard2Ranks[i], out wCard2Suits[i],
                        out bool winnerHasRevealedCards);
                    if (winnerHasRevealedCards)
                        cardsRevealedForHistory = true;
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                wCard1Ranks[i] = (int)Card.Rank.Hidden;
                wCard1Suits[i] = (int)Card.Suit.Hidden;
                wCard2Ranks[i] = (int)Card.Rank.Hidden;
                wCard2Suits[i] = (int)Card.Suit.Hidden;
            }
        }

        HandCompletedClientRpc(
            currentHandWinnerSlots.ToArray(),
            currentHandWinnerAmounts.ToArray(),
            pot,
            ccRanks,
            ccSuits,
            wCard1Ranks, wCard1Suits, wCard2Ranks, wCard2Suits,
            cardsRevealedForHistory
        );

        // Clear for next hand (also cleared in GameLoop, but be safe)
        currentHandWinnerSlots.Clear();
        currentHandWinnerAmounts.Clear();
    }
    
    /// <summary>
    /// ClientRpc - Notifies all clients of a player action
    /// </summary>
    [ClientRpc]
    private void PlayerActionClientRpc(int playerSlotIndex, string action, float amount, int phase)
    {
        Debug.Log($"[{LogTag.Game}] Client: Player action received \u2014 clientId={NetworkManager.Singleton.LocalClientId}, slot={playerSlotIndex}, action={action}, amount=${amount:F2}", this);
        
        // Show action text on the player's slot UI
        PlayerSlotUI playerSlot = GetPlayerSlotUI(playerSlotIndex);
        if (playerSlot != null)
        {
            playerSlot.ShowAction(action);
        }
        
        // Fire hand-history event
        OnPlayerActionReceived?.Invoke(playerSlotIndex, action, amount, phase);
    }
    
    /// <summary>
    /// ClientRpc - Shows the folded state for a player (overlay and dimmed cards)
    /// </summary>
    [ClientRpc]
    private void ShowFoldedStateClientRpc(int playerRealSlotIndex)
    {
        Debug.Log($"[{LogTag.Game}] Client: Showing folded state \u2014 clientId={NetworkManager.Singleton.LocalClientId}, slot={playerRealSlotIndex}", this);
        
        PlayerSlotUI playerSlot = GetPlayerSlotUI(playerRealSlotIndex);
        if (playerSlot != null)
        {
            playerSlot.ShowFoldedState();
        }
    }
    
    /// <summary>
    /// ClientRpc - Clears folded states from all player slots at the start of a new round
    /// </summary>
    [ClientRpc]
    private void ClearAllFoldedStatesClientRpc()
    {
        Debug.Log($"[{LogTag.Game}] Client: Clearing all folded states \u2014 clientId={NetworkManager.Singleton.LocalClientId}", this);
        revealedPlayerCards = false;
        ResetVoluntaryRevealClientState();
        if (PokerTable.Instance == null) return;
        PlayerSlotUI[] slots = PokerTable.Instance.GetPlayerSlots();
        foreach (var slot in slots)
        {
            if (slot != null)
            {
                slot.ClearFoldedState();
            }
        }
    }

    // ==================== PUBLIC HELPER METHODS ====================
    
    /// <summary>
    /// Converts a real (server-side) slot index to a visual (client-side rotated) slot index
    /// </summary>
    public int GetVisualSlotIndex(int realSlotIndex)
    {
        if (!hasLocalPlayer) return realSlotIndex;
        
        int visualSlotIndex = realSlotIndex - localPlayer.realSlotIndex;
        if (visualSlotIndex < 0)
        {
            visualSlotIndex += maxPlayersCount;
        }
        return visualSlotIndex;
    }
    
    /// <summary>
    /// Gets the PlayerSlotUI for a given real slot index (handles visual rotation and bounds checking)
    /// Returns null if the slot is not found or out of range
    /// </summary>
    public PlayerSlotUI GetPlayerSlotUI(int realSlotIndex)
    {
        if (PokerTable.Instance == null) return null;
        
        int visualSlotIndex = GetVisualSlotIndex(realSlotIndex);
        
        PlayerSlotUI[] slots = PokerTable.Instance.GetPlayerSlots();
        if (visualSlotIndex >= 0 && visualSlotIndex < slots.Length)
        {
            return slots[visualSlotIndex];
        }
        return null;
    }
    
    /// <summary>
    /// Gets the 5 community cards as an array
    /// </summary>
    public Card[] GetCommunityCardsArray()
    {
        return new Card[5]
        {
            communityCard1.Value,
            communityCard2.Value,
            communityCard3.Value,
            communityCard4.Value,
            communityCard5.Value
        };
    }
    
    /// <summary>
    /// Gets all active (still playing) players as an array
    /// </summary>
    public PokerPlayer[] GetActivePlayers()
    {
        List<PokerPlayer> activePlayersList = new List<PokerPlayer>();
        for (int i = 0; i < players.Count; i++)
        {
            if (players[i].isPlaying)
            {
                activePlayersList.Add(players[i]);
            }
        }
        return activePlayersList.ToArray();
    }
    
    /// <summary>
    /// Counts active and all-in players
    /// </summary>
    public void CountActiveAndAllInPlayers(out int activePlayers, out int allInPlayers)
    {
        activePlayers = 0;
        allInPlayers = 0;
        foreach (var player in players)
        {
            if (player.isPlaying)
            {
                activePlayers++;
                if (player.isAllIn)
                {
                    allInPlayers++;
                }
            }
        }
    }

    /// <summary>
    /// Diagnostic: logs every point a player is deactivated (isPlaying=false) during
    /// a hand, together with the resulting active/all-in counts, phase, and turn
    /// index. Lets us pinpoint the exact transition that leaves a hand with
    /// activePlayers=0 (both players folded/deactivated with no winner awarded).
    /// </summary>
    private void LogPlayerDeactivation(int slotIndex, string reason)
    {
        CountActiveAndAllInPlayers(out int active, out int allIn);
        Debug.Log($"[{LogTag.Game}] Server: Player deactivated — slot={slotIndex}, reason={reason} → activePlayers={active}, allInPlayers={allIn}, phase={currentPhase.Value}, turnIdx={currentPlayerTurnIndex.Value}, count={players.Count}", this);
    }
    
    // ==================== SIDE POT SYSTEM ====================
    
    /// <summary>
    /// Represents a pot (main or side) with an amount and list of eligible players
    /// </summary>
    private struct SidePot
    {
        public float amount;
        public List<int> eligiblePlayerIndices; // indices in the players NetworkList
    }
    
    /// <summary>
    /// Accumulates each player's currentBet into their totalBetInHand and resets currentBet to 0.
    /// Call this when transitioning between betting phases.
    /// </summary>
    private void AccumulateBetsToTotal()
    {
        if (!IsServer) return;
        
        for (int i = 0; i < players.Count; i++)
        {
            PokerPlayer player = players[i];
            if (player.currentBet > 0)
            {
                player.totalBetInHand += player.currentBet;
                player.currentBet = 0;
                players[i] = player;
            }
        }
    }
    
    /// <summary>
    /// Calculates side pots from all players' totalBetInHand values.
    /// Returns a list of pots ordered from main pot (most eligible players) to side pots (fewer eligible players).
    /// Adjacent pots with the same eligible player set are merged.
    /// </summary>
    private List<SidePot> CalculateSidePots()
    {
        // Build a list of all contributors (including folded players) with their total bet
        var contributors = new List<(int index, float totalBet, bool isPlaying)>();
        for (int i = 0; i < players.Count; i++)
        {
            if (players[i].totalBetInHand > 0.01f)
            {
                contributors.Add((i, players[i].totalBetInHand, players[i].isPlaying));
            }
        }
        
        // Sort by totalBetInHand ascending
        contributors.Sort((a, b) => a.totalBet.CompareTo(b.totalBet));
        
        var pots = new List<SidePot>();
        float previousLevel = 0f;
        
        for (int i = 0; i < contributors.Count; i++)
        {
            float currentLevel = contributors[i].totalBet;
            
            // Skip if same contribution level as a previous player (already handled)
            if (currentLevel <= previousLevel + 0.01f) continue;
            
            float contribution = currentLevel - previousLevel;
            // All players from index i onwards contributed at least this much
            int contributorsAtThisLevel = contributors.Count - i;
            float potAmount = contribution * contributorsAtThisLevel;
            
            // Find eligible winners (active/playing players who contributed at least this much)
            var eligible = new List<int>();
            for (int j = i; j < contributors.Count; j++)
            {
                if (contributors[j].isPlaying)
                {
                    eligible.Add(contributors[j].index);
                }
            }
            
            if (eligible.Count > 0)
            {
                pots.Add(new SidePot { amount = potAmount, eligiblePlayerIndices = eligible });
            }
            else if (pots.Count > 0)
            {
                // Dead money from folded players - add to the previous pot
                var lastPot = pots[pots.Count - 1];
                lastPot.amount += potAmount;
                pots[pots.Count - 1] = lastPot;
            }
            
            previousLevel = currentLevel;
        }
        
        // Merge adjacent pots with the same eligible player set
        var mergedPots = new List<SidePot>();
        foreach (var pot in pots)
        {
            if (mergedPots.Count > 0 && AreSameEligibleSets(mergedPots[mergedPots.Count - 1].eligiblePlayerIndices, pot.eligiblePlayerIndices))
            {
                var lastPot = mergedPots[mergedPots.Count - 1];
                lastPot.amount += pot.amount;
                mergedPots[mergedPots.Count - 1] = lastPot;
            }
            else
            {
                mergedPots.Add(pot);
            }
        }
        
        // Log side pots
        for (int i = 0; i < mergedPots.Count; i++)
        {
            string eligibleNames = string.Join(", ", mergedPots[i].eligiblePlayerIndices.Select(idx => players[idx].playerName.ToString()));
            Debug.Log($"[{LogTag.Game}] Server: Side pot calculated \u2014 potIndex={i}, amount=${mergedPots[i].amount:F2}, eligible={eligibleNames}", this);
        }
        
        return mergedPots;
    }
    
    /// <summary>
    /// Checks if two eligible player sets are identical
    /// </summary>
    private bool AreSameEligibleSets(List<int> set1, List<int> set2)
    {
        if (set1.Count != set2.Count) return false;
        for (int i = 0; i < set1.Count; i++)
        {
            if (set1[i] != set2[i]) return false;
        }
        return true;
    }
    
    /// <summary>
    /// Awards a specific amount from the pot to a player (used for side pot distribution)
    /// </summary>
    private void AwardAmountToPlayer(int winnerRealSlotIndex, float amount)
    {
        if (!IsServer) return;
        
        if (!TryGetPlayerByRealSlotIndex(winnerRealSlotIndex, out PokerPlayer winner))
        {
            Debug.LogError($"[{LogTag.Game}] Server: Player not found for side pot award \u2014 slot={winnerRealSlotIndex}, check player removal order", this);
            return;
        }
        
        Debug.Log($"[{LogTag.Game}] Server: Awarding side pot \u2014 player={winner.playerName}, amount=${amount:F2}, slot={winnerRealSlotIndex}", this);

        // Track for hand history
        currentHandWinnerSlots.Add(winnerRealSlotIndex);
        currentHandWinnerAmounts.Add(amount);
        
        // Update player's stack
        PokerPlayer updatedPlayer = winner;
        updatedPlayer.stack += amount;
        
        for (int i = 0; i < players.Count; i++)
        {
            if (players[i].realSlotIndex == winnerRealSlotIndex)
            {
                players[i] = updatedPlayer;
                break;
            }
        }
        
        // Reduce the total pot
        potOnTable.Value = Mathf.Max(0, potOnTable.Value - amount);
    }
    
    /// <summary>
    /// Cleans up after a round and starts the next one (shared by all round-ending paths)
    /// </summary>
    private void CleanupAndStartNextRound()
    {
        // Stale-coroutine guard: if the game was already ended by EndGameServerRpc
        // (e.g. because all players left while an award coroutine was running),
        // don't attempt to start a new round or broadcast stale hand data.
        if (!isGameStarted.Value)
        {
            Debug.Log($"[{LogTag.Game}] Server: Cleanup skipped \u2014 game already ended", this);
            handInProgress = false;
            return;
        }

        // The current hand is over; release the re-entrancy latch so the next
        // GameLoop call (below) can start the next hand. Any stray start attempt
        // during this brief window is still harmless because we start the next
        // hand ourselves right after.
        handInProgress = false;

        // ── Hand History: broadcast hand completion BEFORE cleanup ──
        BroadcastHandCompleted();

        RemovePlayersWithZeroStack();
        
        if (players.Count >= 2)
        {
            // Find the current dealer in the (possibly modified) players list using real slot index
            // This is needed because player removals shift list indices
            int resolvedDealerIndex = -1;
            for (int i = 0; i < players.Count; i++)
            {
                if (players[i].realSlotIndex == currentDealerRealSlotIndex)
                {
                    resolvedDealerIndex = i;
                    break;
                }
            }
            
            // If the dealer was removed, find the closest previous player
            // (the next dealer will then correctly be the one after them)
            if (resolvedDealerIndex == -1)
            {
                // Find the player whose realSlotIndex is closest before the dealer's
                int bestIndex = 0;
                for (int i = 0; i < players.Count; i++)
                {
                    // Use circular distance: how far before the dealer is this player?
                    int distBefore = (currentDealerRealSlotIndex - players[i].realSlotIndex + maxPlayersCount) % maxPlayersCount;
                    int bestDistBefore = (currentDealerRealSlotIndex - players[bestIndex].realSlotIndex + maxPlayersCount) % maxPlayersCount;
                    if (distBefore < bestDistBefore)
                    {
                        bestIndex = i;
                    }
                }
                resolvedDealerIndex = bestIndex;
                Debug.Log($"[{LogTag.Game}] Server: Dealer removed, using fallback \u2014 removedSlot={currentDealerRealSlotIndex}, fallbackIndex={resolvedDealerIndex}, fallbackSlot={players[resolvedDealerIndex].realSlotIndex}", this);
            }
            
            int nextDealerIndex = (resolvedDealerIndex + 1) % players.Count;
            Debug.Log($"[{LogTag.Game}] Server: Starting next round \u2014 dealerIndex={nextDealerIndex}, dealerSlot={players[nextDealerIndex].realSlotIndex}", this);
            GameLoop(nextDealerIndex);
        }
        else
        {
            // Not enough players to continue — properly end the game so
            // AddPlayer's check (playersCount >= 2 && !isGameStarted) works.
            isGameStarted.Value = false;
            gameStarting = false;
            handInProgress = false;
            Debug.Log($"[{LogTag.Game}] Server: Game ended \u2014 reason=insufficient players, remaining={players.Count}", this);
        }
    }
    
    /// <summary>
    /// Gets the latest local player data from the NetworkList
    /// </summary>
    private PokerPlayer GetLatestLocalPlayer()
    {
        if (!hasLocalPlayer) return localPlayer;
        
        // Find the latest data in the NetworkList
        for (int i = 0; i < players.Count; i++)
        {
            if (players[i].realSlotIndex == localPlayer.realSlotIndex)
            {
                return players[i];
            }
        }
        
        return localPlayer; // Fallback to cached version
    }
    
    /// <summary>
    /// Gets the minimum raise amount (current bet + last raise amount)
    /// </summary>
    public float GetMinimumRaiseAmount()
    {
        if (!hasLocalPlayer) return 0f;
        
        return currentBetToCall.Value + lastRaiseAmount.Value;
    }
    
    /// <summary>
    /// Gets the amount the local player needs to call
    /// </summary>
    public float GetAmountToCall()
    {
        if (!hasLocalPlayer) return 0f;
        
        PokerPlayer latestPlayer = GetLatestLocalPlayer();
        return Mathf.Max(0, currentBetToCall.Value - latestPlayer.currentBet);
    }
    
    /// <summary>
    /// Gets the maximum amount the local player can bet (their stack + current bet)
    /// </summary>
    public float GetMaximumBetAmount()
    {
        if (!hasLocalPlayer) return 0f;
        
        PokerPlayer latestPlayer = GetLatestLocalPlayer();
        return latestPlayer.stack + latestPlayer.currentBet;
    }
    
    // ==================== GAME STATE SYNC & DISCONNECT HANDLING ====================
    
    /// <summary>
    /// Server-side: Called when a client disconnects. Removes the ghost player, auto-folds if in hand,
    /// and handles turn transition if it was their turn.
    /// </summary>
    private void OnClientDisconnected(ulong clientId)
    {
        if (!IsServer) return;
        
        // Always remove from connectedClients (tracks both seated + spectators)
        if (connectedClients.Remove(clientId))
        {
            Debug.Log($"[{LogTag.Network}] Server: Client removed from connected list \u2014 clientId={clientId}, remaining={connectedClients.Count}", this);
        }
        
        // Find the player's slot by their clientId (only seated players have slot mappings)
        int disconnectedSlotIndex = -1;
        foreach (var kvp in slotToClientId)
        {
            if (kvp.Value == clientId)
            {
                disconnectedSlotIndex = kvp.Key;
                break;
            }
        }
        
        if (disconnectedSlotIndex == -1)
        {
            // Spectator disconnect — not seated, so just clean up auth mapping and lobby
            Debug.Log($"[{LogTag.Network}] Server: Spectator disconnected \u2014 clientId={clientId}", this);
            if (clientIdToAuthPlayerId.TryGetValue(clientId, out string spectatorAuth))
            {
                if (LobbyManager.Instance != null)
                {
                    LobbyManager.Instance.KickPlayer(spectatorAuth);
                    Debug.Log($"[{LogTag.Network}] Server: Kicked spectator from lobby \u2014 authId={spectatorAuth}", this);
                }
                clientIdToAuthPlayerId.Remove(clientId);
            }
            return;
        }
        
        if (!TryGetPlayerByRealSlotIndex(disconnectedSlotIndex, out PokerPlayer disconnectedPlayer))
        {
            Debug.LogWarning($"[{LogTag.Network}] Server: Slot mapping without player data \u2014 clientId={clientId}, slot={disconnectedSlotIndex}, action=cleaning up", this);
            slotToClientId.Remove(disconnectedSlotIndex);
            return;
        }
        
        Debug.Log($"[{LogTag.Network}] Server: Player disconnected \u2014 clientId={clientId}, player={disconnectedPlayer.playerName}, slot={disconnectedSlotIndex}", this);
        
        int playerIndex = GetPlayerIndexBySlotIndex(disconnectedSlotIndex);
        bool wasOnTurn = (playerIndex >= 0 && playerIndex == currentPlayerTurnIndex.Value);
        
        // If the game is running and this player was in the hand, fold them
        if (isGameStarted.Value && playerIndex >= 0 && disconnectedPlayer.isPlaying)
        {
            PokerPlayer updated = players[playerIndex];
            updated.isPlaying = false;
            updated.hasActed = true;
            players[playerIndex] = updated;
            LogPlayerDeactivation(disconnectedSlotIndex, "disconnect");
            
            // Notify all remaining clients of the fold
            ShowFoldedStateClientRpc(disconnectedSlotIndex);
            PlayerActionClientRpc(disconnectedSlotIndex, "Disconnected", 0, (int)currentPhase.Value);
        }
        
        // If it was their turn, handle the turn transition before removing
        if (wasOnTurn && isGameStarted.Value)
        {
            CancelTurnAndNotifyClients();
            
            // Check if only one player remains (auto-win)
            if (!CheckForSinglePlayerWin())
            {
                // Still multiple players active, move to next player or end betting round
                MoveToNextPlayer();
            }
        }
        
        CashOutPlayerToAvailableBalance(disconnectedPlayer, clientId);

        // Clear time bank for this slot
        timeBankBySlotIndex.Remove(disconnectedSlotIndex);

        // Remove the player from the game
        RemovePlayer(disconnectedPlayer);
        slotToClientId.Remove(disconnectedSlotIndex);
        
        // Kick the player from the lobby so the slot frees up for others
        if (clientIdToAuthPlayerId.TryGetValue(clientId, out string authPlayerId))
        {
            if (LobbyManager.Instance != null)
            {
                LobbyManager.Instance.KickPlayer(authPlayerId);
                Debug.Log($"[{LogTag.Network}] Server: Kicked player from lobby \u2014 authId={authPlayerId}", this);
            }
            clientIdToAuthPlayerId.Remove(clientId);
        }
        
        // Notify all remaining clients to update UI
        RemovePlayerSlotClientRpc(disconnectedSlotIndex, clientId);
        
        Debug.Log($"[{LogTag.Network}] Server: Player fully removed after disconnect \u2014 player={disconnectedPlayer.playerName}, remaining={players.Count}", this);
    }
    
    /// <summary>
    /// Client-side: Initializes the UI from network state when joining/rejoining the game.
    /// Waits for NetworkList to sync before building the visual state.
    /// </summary>
    private System.Collections.IEnumerator InitializeClientState()
    {
        // Wait for PokerTable.Instance AND its PlayerSlotUI references to be fully ready.
        // This is a single combined check because Netcode's scene synchronization can
        // destroy and recreate scene objects at any point during the connection handshake.
        // If PokerTable.Instance goes stale mid-check, we need to re-acquire it.
        float waitTime = 0f;
        float maxWait = 10f;
        bool ready = false;
        
        while (!ready && waitTime < maxWait)
        {
            // Re-check PokerTable.Instance every iteration (it can go null if scene reloads)
            PokerTable pokerTable = PokerTable.Instance;
            
            // Fallback: if singleton is null/stale, try to find it in the scene
            if (pokerTable == null)
            {
                pokerTable = UnityEngine.Object.FindObjectOfType<PokerTable>();
                if (pokerTable != null)
                {
                    Debug.LogWarning($"[{LogTag.Game}] Client: PokerTable.Instance was null, found via FindObjectOfType \u2014 clientId={NetworkManager.Singleton.LocalClientId}", this);
                    // The PokerTable exists but its Awake hasn't set Instance yet, or it was cleared.
                    // We don't set Instance here (Awake should do it), but we can use the reference.
                }
            }
            
            if (pokerTable != null)
            {
                var slots = pokerTable.GetPlayerSlots();
                if (slots != null)
                {
                    foreach (var slot in slots)
                    {
                        if (slot != null && slot.namePlate != null)
                        {
                            ready = true;
                            break;
                        }
                    }
                }
            }
            
            if (!ready)
            {
                yield return new WaitForSeconds(0.1f);
                waitTime += 0.1f;
            }
        }

        // Hide loading screen — the game scene is ready (or timed out)
        if (LoadingUI.Instance != null) LoadingUI.Instance.StopLoading();

        if (!ready)
        {
            // Diagnostic logging to help identify the exact failure point
            bool pokerTableExists = PokerTable.Instance != null;
            bool foundViaSearch = UnityEngine.Object.FindObjectOfType<PokerTable>() != null;
            int slotCount = 0;
            int nullSlots = 0;
            int nullNamePlates = 0;
            
            PokerTable diagTable = PokerTable.Instance ?? UnityEngine.Object.FindObjectOfType<PokerTable>();
            if (diagTable != null)
            {
                var slots = diagTable.GetPlayerSlots();
                if (slots != null)
                {
                    slotCount = slots.Length;
                    foreach (var slot in slots)
                    {
                        if (slot == null) nullSlots++;
                        else if (slot.namePlate == null) nullNamePlates++;
                    }
                }
            }
            Debug.LogError($"[{LogTag.Game}] Client: Scene not ready after timeout \u2014 clientId={NetworkManager.Singleton.LocalClientId}, timeout={maxWait}s, pokerTableInstance={pokerTableExists}, foundViaSearch={foundViaSearch}, slots={slotCount}, nullSlots={nullSlots}, nullNamePlates={nullNamePlates}", this);
            yield break;
        }
        
        // Wait for NetworkList to sync
        waitTime = 0f;
        while (players.Count == 0 && waitTime < maxWait)
        {
            yield return new WaitForSeconds(0.2f);
            waitTime += 0.2f;
        }
        
        // ── Migration reconnect: wait for the new host's ApplyMigrationState to set
        //    up our local player via UpdatePlayerSlotClientRpc rather than building
        //    from scratch. The host sends cards/bets/community/turn as part of
        //    the migration sync, so we do NOT request game state sync ourselves.
        if (isMigrationReconnect)
        {
            Debug.Log($"[{LogTag.Network}] Client: Migration reconnect waiting for host \u2014 clientId={NetworkManager.Singleton.LocalClientId}", this);
            float migrationWait = 0f;
            while (!hasLocalPlayer && migrationWait < 15f)
            {
                yield return new WaitForSeconds(0.2f);
                migrationWait += 0.2f;
            }
            
            if (hasLocalPlayer)
            {
                Debug.Log($"[{LogTag.Network}] Client: Migration local player set \u2014 clientId={NetworkManager.Singleton.LocalClientId}, elapsed={migrationWait:F1}s", this);
                
                // Wait for the players NetworkList to sync from the new host
                float syncWait = 0f;
                while (players.Count == 0 && syncWait < 5f)
                {
                    yield return new WaitForSeconds(0.2f);
                    syncWait += 0.2f;
                }
                
                Debug.Log($"[{LogTag.Network}] Client: Migration NetworkList synced \u2014 clientId={NetworkManager.Singleton.LocalClientId}, playerCount={players.Count}, elapsed={syncWait:F1}s", this);
                
                // NOW rotate — the NetworkList is synced so RotateSlotsToLocalPlayer
                // can read all players and place portraits correctly.
                isMigrationReconnect = false;
                PokerTable.Instance.RotateSlotsToLocalPlayer(localPlayer.realSlotIndex, localPlayer);
                PokerTable.Instance.RemoveNonOccupiedEmptySlotsLocally();
            }
            else
            {
                isMigrationReconnect = false;
                Debug.LogWarning($"[{LogTag.Network}] Client: Migration timed out waiting for local player \u2014 clientId={NetworkManager.Singleton.LocalClientId}, fallback=BuildTableUI", this);
                if (players.Count > 0)
                    BuildTableUIFromNetworkState();
                if (isGameStarted.Value)
                    RequestGameStateSyncServerRpc();
            }
            yield break;
        }
        
        // If local player was already set up via UpdatePlayerSlotClientRpc (e.g., player sat
        // very quickly), the rotation is already applied. Don't overwrite with unrotated data.
        if (hasLocalPlayer)
        {
            Debug.Log($"[{LogTag.Game}] Client: Local player already set up \u2014 clientId={NetworkManager.Singleton.LocalClientId}, action=requesting game state sync", this);
            if (isGameStarted.Value)
            {
                RequestGameStateSyncServerRpc();
            }
            yield break;
        }
        
        // If no players exist even after waiting, nothing to sync
        if (players.Count == 0)
        {
            Debug.Log($"[{LogTag.Game}] Client: No players found after wait \u2014 clientId={NetworkManager.Singleton.LocalClientId}, timeout={maxWait}s", this);
            yield break;
        }
        
        Debug.Log($"[{LogTag.Game}] Client: Initializing client state \u2014 clientId={NetworkManager.Singleton.LocalClientId}, playerCount={players.Count}, gameStarted={isGameStarted.Value}", this);
        
        // Build the table UI from the synced NetworkList
        BuildTableUIFromNetworkState();
        
        // Request additional visual state (cards, community cards, bets) from the server
        if (isGameStarted.Value)
        {
            RequestGameStateSyncServerRpc();
        }
    }
    
    /// <summary>
    /// Client-side: Populates all player slot UIs from the synced NetworkList data.
    /// For non-seated clients (spectators/returning players), shows players at real positions.
    /// Empty slots are kept visible so non-seated players can click them to sit down.
    /// </summary>
    private void BuildTableUIFromNetworkState()
    {
        Debug.Log($"[{LogTag.Game}] Client: Building table UI from network state \u2014 clientId={NetworkManager.Singleton.LocalClientId}, playerCount={players.Count}", this);
        
        for (int i = 0; i < players.Count; i++)
        {
            PokerPlayer player = players[i];
            PlayerSlotUI slot = FindSlotByRealIndex(player.realSlotIndex);
            if (slot != null)
            {
                slot.SetPlayerSlot(player.playerName.ToString(), player.stack, player.iconIndex);
                
                // Show folded state if player has folded in the current hand
                if (!player.isPlaying && isGameStarted.Value)
                {
                    slot.ShowFoldedState();
                }
                
                Debug.Log($"[{LogTag.Game}] Client: Slot populated \u2014 clientId={NetworkManager.Singleton.LocalClientId}, slot={player.realSlotIndex}, player={player.playerName}, stack=${player.stack:F2}", this);
            }
        }
        
        // NOTE: Do NOT hide empty slots here. Non-seated players (spectators, returning players)
        // need to see empty slots so they can click them to sit down.
        // Empty slots are hidden later in UpdatePlayerSlotClientRpc after the local player sits and rotation is applied.
    }
    
    /// <summary>
    /// Client requests the server to send all visual game state (cards, bets, community cards, turn).
    /// Called after the client has built basic UI from the NetworkList.
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    private void RequestGameStateSyncServerRpc(ServerRpcParams rpcParams = default)
    {
        ulong clientId = rpcParams.Receive.SenderClientId;
        Debug.Log($"[{LogTag.Network}] Server: Game state sync requested \u2014 clientId={clientId}", this);
        StartCoroutine(SendGameStateToClient(clientId));
    }
    
    /// <summary>
    /// Server-side: Sends all visual game state to a specific client via targeted ClientRpcs.
    /// Includes hidden cards, bet displays, community cards, and current turn info.
    /// </summary>
    private System.Collections.IEnumerator SendGameStateToClient(ulong clientId)
    {
        // Small delay to ensure the client has finished building basic UI
        yield return new WaitForSeconds(0.3f);
        
        var targetParams = new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new ulong[] { clientId } }
        };
        
        if (!isGameStarted.Value)
        {
            Debug.Log($"[{LogTag.Network}] Server: No state to sync, game not started \u2014 clientId={clientId}", this);
            yield break;
        }
        
        Debug.Log($"[{LogTag.Network}] Server: Sending full game state \u2014 clientId={clientId}", this);
        
        // 1. Send cards for all players who have been dealt cards.
        //    The requesting client must receive their OWN cards face-up; everyone
        //    else's cards go face-down. Sending only hidden cards here would leave
        //    the requester with no hole cards on their own seat, because the owner
        //    skip-guard in ShowHiddenCardsForPlayerClientRpc drops their own slot.
        foreach (var player in players)
        {
            if (player.card1.rank != Card.Rank.None && player.card1.suit != Card.Suit.None)
            {
                ulong ownerClientId = slotToClientId.ContainsKey(player.realSlotIndex) 
                    ? slotToClientId[player.realSlotIndex] : 0;
                
                if (ownerClientId == clientId)
                {
                    // The requesting client owns these cards — send them face-up
                    SendCardsToPlayerClientRpc(player.card1.rank, player.card1.suit,
                        player.card2.rank, player.card2.suit, targetParams);
                }
                else
                {
                    ShowHiddenCardsForPlayerClientRpc(player.realSlotIndex, ownerClientId, targetParams);
                }
            }
            
            // 2. Send current bet display for each player
            if (player.currentBet > 0)
            {
                SyncBetDisplayClientRpc(player.realSlotIndex, player.currentBet, targetParams);
            }
        }
        
        // 3. Send community cards if any phase past PreFlop
        if (currentPhase.Value >= GamePhase.Flop)
        {
            int cardCount = 0;
            switch (currentPhase.Value)
            {
                case GamePhase.Flop: cardCount = 3; break;
                case GamePhase.Turn: cardCount = 4; break;
                case GamePhase.River:
                case GamePhase.Showdown: cardCount = 5; break;
            }
            if (cardCount > 0)
            {
                SyncCommunityCardsStateClientRpc(cardCount, targetParams);
            }
        }
        
        // 4. Send current turn info (so client shows the correct timer)
        if (currentPlayerTurnIndex.Value >= 0 && currentPlayerTurnIndex.Value < players.Count)
        {
            SyncCurrentTurnStateClientRpc(currentPlayerTurnIndex.Value, targetParams);
        }
        
        // 5. Send time bank values for all seated players
        foreach (var kvp in timeBankBySlotIndex)
        {
            SendTimeBankToClientRpc(kvp.Key, kvp.Value, targetParams);
        }
        
        Debug.Log($"[{LogTag.Network}] Server: Finished sending game state \u2014 clientId={clientId}", this);
    }
    
    /// <summary>
    /// Targeted ClientRpc - Shows bet amount at a player's endpoint without animation.
    /// Used for state sync to newly connected clients.
    /// </summary>
    [ClientRpc]
    private void SyncBetDisplayClientRpc(int playerRealSlotIndex, float betAmount, ClientRpcParams clientRpcParams = default)
    {
        Debug.Log($"[{LogTag.Game}] Client: Syncing bet display \u2014 clientId={NetworkManager.Singleton.LocalClientId}, slot={playerRealSlotIndex}, bet=${betAmount:F2}", this);
        
        PlayerSlotUI playerSlot = GetPlayerSlotUI(playerRealSlotIndex);
        if (playerSlot != null && betAmount > 0)
        {
            playerSlot.SetBetDisplay(betAmount);
        }
    }

    /// <summary>
    /// ClientRpc - Updates the pot display on all clients.
    /// Used after host migration to ensure the pot amount is visible.
    /// </summary>
    [ClientRpc]
    private void SyncPotDisplayClientRpc(float potAmount)
    {
        Debug.Log($"[{LogTag.Game}] Client: Syncing pot display \u2014 clientId={NetworkManager.Singleton.LocalClientId}, pot=${potAmount:F2}", this);
        
        if (PokerTable.Instance != null)
        {
            PokerTable.Instance.UpdatePotOnTable(potAmount);
            if (potAmount > 0)
            {
                if (PokerTable.Instance.potOnTableGameObject != null)
                {
                    PokerTable.Instance.potOnTableGameObject.SetActive(true);
                }
            }
        }
    }
    
    /// <summary>
    /// Targeted ClientRpc - Shows community cards without animation.
    /// Used for state sync to newly connected clients.
    /// </summary>
    [ClientRpc]
    private void SyncCommunityCardsStateClientRpc(int cardCount, ClientRpcParams clientRpcParams = default)
    {
        Debug.Log($"[{LogTag.Game}] Client: Syncing community cards \u2014 clientId={NetworkManager.Singleton.LocalClientId}, cardCount={cardCount}", this);
        
        if (PokerTable.Instance == null) return;
        
        CardUI[] cardSlots = PokerTable.Instance.GetCommunityCardSlots();
        Card[] communityCards = GetCommunityCardsArray();
        
        for (int i = 0; i < cardCount && i < cardSlots.Length; i++)
        {
            if (cardSlots[i] != null && communityCards[i].rank != Card.Rank.None && communityCards[i].suit != Card.Suit.None)
            {
                cardSlots[i].gameObject.SetActive(true);
                cardSlots[i].Init(communityCards[i].rank, communityCards[i].suit, isHidden: false, animate: false);
            }
        }
    }
    
    /// <summary>
    /// Targeted ClientRpc - Sets the current player turn with timer.
    /// Used for state sync to newly connected clients.
    /// </summary>
    [ClientRpc]
    private void SyncCurrentTurnStateClientRpc(int playerIndex, ClientRpcParams clientRpcParams = default)
    {
        Debug.Log($"[{LogTag.Game}] Client: Syncing current turn \u2014 clientId={NetworkManager.Singleton.LocalClientId}, playerIndex={playerIndex}", this);
        
        if (PokerTable.Instance == null) return;
        
        // Stop all timers first
        PlayerSlotUI[] slots = PokerTable.Instance.GetPlayerSlots();
        foreach (var slot in slots)
        {
            if (slot != null)
            {
                slot.StopTimer();
            }
        }
        
        if (playerIndex < 0 || playerIndex >= players.Count) return;
        
        // Start timer for current player
        PokerPlayer currentPlayer = players[playerIndex];
        PlayerSlotUI playerSlot = GetPlayerSlotUI(currentPlayer.realSlotIndex);
        if (playerSlot != null)
        {
            playerSlot.StartTimer();
        }
    }
    
}
