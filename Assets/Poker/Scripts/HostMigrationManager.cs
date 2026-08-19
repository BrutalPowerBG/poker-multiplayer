using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Services.Authentication;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Orchestrates seamless host migration for the Poker game.
///
/// Flow:
///   1. Departing host captures a GameStateSnapshot and sends it to all clients
///      via MigrateHostClientRpc (includes a migration token + new host auth ID).
///   2. Old host shuts down networking.
///   3. Each client receives the snapshot and stores it here.
///   4. Clients detect the host disconnect (ExitHandler fires).
///   5. ExitHandler checks HostMigrationManager.IsMigrationPending:
///        • New host  → creates lobby with the migration token, creates relay, starts hosting,
///                       waits for other clients, then restores the game from the snapshot.
///        • Other client → polls for lobby by migration token, joins, starts client.
///   6. Once all expected players have reconnected (or a timeout elapses),
///      the new host restores the game state and play resumes.
/// </summary>
public class HostMigrationManager : MonoBehaviour
{
    public static HostMigrationManager Instance { get; private set; }

    // ── Migration state (set by the departing host's RPC) ────────
    public bool IsMigrationPending { get; private set; }
    public GameStateSnapshot PendingSnapshot { get; private set; }
    public bool IsNewHost { get; private set; }

    // ── Reconnection tracking (new host only) ─────────────────
    // Auth IDs of players who have connected to the new Netcode session.
    // Populated by GameManager.RegisterAuthForMigrationServerRpc and
    // directly by the new host in NewHostFlow.
    private HashSet<string> reconnectedAuthIds = new HashSet<string>();

    // ── Constants ────────────────────────────────────────────────
    private const string LOBBY_KEY_MIGRATION = "MigrationToken";
    private const float CLIENT_RECONNECT_TIMEOUT = 8f;
    private const float LOBBY_POLL_INTERVAL = 1f;

    // ═══════════════════════════════════════════════════════════════
    //  Singleton lifecycle
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Ensure an instance exists (callable from any scene).
    /// </summary>
    public static HostMigrationManager GetOrCreate()
    {
        if (Instance != null) return Instance;
        var go = new GameObject("HostMigrationManager");
        var mgr = go.AddComponent<HostMigrationManager>();
        DontDestroyOnLoad(go);
        return mgr;
    }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Reconnection tracking (new host only)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Called by GameManager.RegisterAuthForMigrationServerRpc (for clients)
    /// and directly by NewHostFlow (for the server).
    /// Tracks which players have connected to the new Netcode session.
    /// </summary>
    public void RegisterReconnectedPlayer(string authPlayerId)
    {
        if (string.IsNullOrEmpty(authPlayerId)) return;
        reconnectedAuthIds.Add(authPlayerId);
        Debug.Log($"[{LogTag.Migration}] Player reconnected — authId={authPlayerId}, totalReconnected={reconnectedAuthIds.Count}", this);
    }

    /// <summary>
    /// How many players have reconnected so far (used by NewHostFlow's smart wait).
    /// </summary>
    public int ReconnectedCount => reconnectedAuthIds.Count;

    // ═══════════════════════════════════════════════════════════════
    //  Step 1 – Departing host initiates migration (called from ExitHandler)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Called on the HOST that is about to leave. Captures state, picks the next
    /// host, sends the snapshot to all clients, then returns so ExitHandler can
    /// shut down networking.
    /// </summary>
    public void InitiateMigration(GameManager gm)
    {
        if (!NetworkManager.Singleton.IsServer)
        {
            Debug.LogWarning($"[{LogTag.Migration}] InitiateMigration called on non-server — isServer=false. Only the host can initiate migration.", this);
            return;
        }

        // Pick the next host from ALL connected clients (seated + spectators).
        // This ensures migration works even when the departing host is a spectator.
        ulong newHostClientId = 0;
        string newHostAuthId = "";
        var allClients = gm.GetConnectedClients();
        
        foreach (var kvp in allClients)
        {
            if (kvp.Key != NetworkManager.Singleton.LocalClientId)
            {
                newHostClientId = kvp.Key;
                newHostAuthId = kvp.Value;
                break;
            }
        }

        if (string.IsNullOrEmpty(newHostAuthId))
        {
            Debug.LogWarning($"[{LogTag.Migration}] No eligible client to promote — aborting migration. Check connected clients.", this);
            return;
        }

        // Capture full state (uses clientIdToAuthPlayerId for seated player data)
        var authMap = gm.GetClientIdToAuthPlayerId();
        var snapshot = GameStateSnapshot.CaptureFromServer(gm, authMap);
        snapshot.newHostAuthPlayerId = newHostAuthId;
        snapshot.departingHostAuthPlayerId = AuthenticationService.Instance.PlayerId;

        // Add ALL connected clients (including spectators) to expectedAuthPlayerIds
        // so the new host knows how many clients should reconnect.
        foreach (var kvp in allClients)
        {
            if (!snapshot.expectedAuthPlayerIds.Contains(kvp.Value))
                snapshot.expectedAuthPlayerIds.Add(kvp.Value);
        }

        Debug.Log($"[{LogTag.Migration}] Initiating migration — newHost={newHostAuthId}, token={snapshot.migrationToken}, seatedPlayers={snapshot.players.Count}, totalExpected={snapshot.expectedAuthPlayerIds.Count}", this);

        // Send to every client
        byte[] data = snapshot.ToBytes();
        gm.MigrateHostClientRpc(data);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Step 3 – Client receives the migration snapshot
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Called from GameManager.MigrateHostClientRpc on every client.
    /// </summary>
    public void OnMigrationDataReceived(GameStateSnapshot snapshot)
    {
        PendingSnapshot = snapshot;
        IsMigrationPending = true;

        string myAuthId = AuthenticationService.Instance.PlayerId;
        IsNewHost = (myAuthId == snapshot.newHostAuthPlayerId);

        Debug.Log($"[{LogTag.Migration}] Snapshot received — role={(IsNewHost ? "NewHost" : "Client")}, token={snapshot.migrationToken}, expectedPlayers={snapshot.expectedAuthPlayerIds.Count}", this);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Step 5 – Execute migration (called by ExitHandler after disconnect)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Kicks off the migration process. ExitHandler calls this instead of
    /// loading the lobby scene when IsMigrationPending is true.
    /// </summary>
    public void ExecuteMigration()
    {
        if (!IsMigrationPending || PendingSnapshot == null)
        {
            Debug.LogError($"[{LogTag.Migration}] ExecuteMigration called with no snapshot pending — snapshot=<none>. Was MigrateHostClientRpc received?", this);
            return;
        }

        // Show loading overlay for the duration of migration
        if (LoadingUI.Instance != null) LoadingUI.Instance.StartLoading();

        // Shut down current networking (keep NM alive for reuse)
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
        {
            NetworkManager.Singleton.Shutdown();
        }

        // CRITICAL: Reset StartGameManager so it accepts the new game-start signal.
        // Without this, isProcessingGameStart is still true from the previous session,
        // causing "Already processing game start, ignoring duplicate signal" and
        // the relay never gets created.
        if (StartGameManager.Instance != null)
        {
            StartGameManager.Instance.ResetForNewGame();
        }

        if (IsNewHost)
        {
            StartCoroutine(NewHostFlow());
        }
        else
        {
            StartCoroutine(ClientFlow());
        }
    }

    // ── New-host flow ────────────────────────────────────────────

    private IEnumerator NewHostFlow()
    {
        Debug.Log($"[{LogTag.Migration}] New host — creating migration lobby", this);

        // 1 — Create lobby with migration token
        string lobbyCode = null;
        bool lobbyCreated = false;

        CreateMigrationLobbyAsync(PendingSnapshot.migrationToken, (code) =>
        {
            lobbyCode = code;
            lobbyCreated = true;
        });

        float elapsed = 0f;
        while (!lobbyCreated && elapsed < 10f)
        {
            yield return null;
            elapsed += Time.deltaTime;
        }
        if (!lobbyCreated)
        {
            Debug.LogError($"[{LogTag.Migration}] Migration lobby creation timed out — elapsed={elapsed:F1}s, fallback=lobby. Check Unity Lobby service status.", this);
            if (LoadingUI.Instance != null) LoadingUI.Instance.StopLoading();
            ClearMigration();
            SceneManager.LoadScene(0);
            yield break;
        }

        Debug.Log($"[{LogTag.Migration}] Migration lobby created — code={lobbyCode}, starting game as host", this);

        // Remember the old GameManager so we can detect a fresh scene load.
        // We're likely still on scene 1 from the previous session, so checking
        // buildIndex alone would exit immediately.
        var oldGM = GameManager.Instance;

        // 2 — Start the game (uses the normal StartGameManager path:
        //     updates lobby data → LoadSceneAsync(1) → fires OnLobbyStartGame
        //     → StartGameManager creates relay → starts hosting)
        LobbyManager.Instance?.StartGame();

        // 3 — Wait for a NEW GameManager instance AND for this node to be the server.
        //     Scene 1 reloads even if we're already on it (LoadSceneAsync triggers
        //     a full unload+load), which destroys the old GameManager and creates
        //     a new one. The relay is created concurrently by StartGameManager.
        float serverTimeout = 20f;
        elapsed = 0f;
        Debug.Log($"[{LogTag.Migration}] Waiting for scene reload + server start — timeout={serverTimeout:F0}s", this);
        while (elapsed < serverTimeout)
        {
            yield return null;
            elapsed += Time.deltaTime;

            // A new GameManager exists (scene reloaded), it's network-spawned,
            // and we are the server — ready to restore.
            if (GameManager.Instance != null &&
                GameManager.Instance != oldGM &&
                GameManager.Instance.IsServer)
            {
                Debug.Log($"[{LogTag.Migration}] Server ready — elapsed={elapsed:F1}s", this);
                break;
            }
        }

        if (GameManager.Instance == null || !GameManager.Instance.IsServer)
        {
            Debug.LogError($"[{LogTag.Migration}] Server start timed out — elapsed={elapsed:F1}s, GameManager={(GameManager.Instance != null ? "exists" : "<none>")}, isServer={(GameManager.Instance != null ? GameManager.Instance.IsServer.ToString() : "N/A")}. Falling back to lobby.", this);
            if (LoadingUI.Instance != null) LoadingUI.Instance.StopLoading();
            ClearMigration();
            SceneManager.LoadScene(0);
            yield break;
        }

        // 3b — Register ourselves (new host) as reconnected
        string myAuth = AuthenticationService.Instance.PlayerId;
        RegisterReconnectedPlayer(myAuth);

        // Also seed the GameManager's auth mapping for the new session
        var gm = GameManager.Instance;
        gm.GetClientIdToAuthPlayerId()[NetworkManager.Singleton.LocalClientId] = myAuth;

        // 4 — Wait for clients to reconnect (or timeout).
        //     Exit early if everyone expected has reconnected.
        int expectedReconnections = PendingSnapshot.expectedAuthPlayerIds.Count;
        // Subtract the departing host (they will never reconnect)
        if (PendingSnapshot.expectedAuthPlayerIds.Contains(PendingSnapshot.departingHostAuthPlayerId))
            expectedReconnections--;

        Debug.Log($"[{LogTag.Migration}] Waiting for player reconnections — timeout={CLIENT_RECONNECT_TIMEOUT:F0}s, expected={expectedReconnections}, current={reconnectedAuthIds.Count}", this);

        float waitElapsed = 0f;
        while (waitElapsed < CLIENT_RECONNECT_TIMEOUT)
        {
            if (reconnectedAuthIds.Count >= expectedReconnections)
            {
                Debug.Log($"[{LogTag.Migration}] All players reconnected early — expected={expectedReconnections}, elapsed={waitElapsed:F1}s", this);
                break;
            }
            yield return null;
            waitElapsed += Time.deltaTime;
        }

        if (reconnectedAuthIds.Count < expectedReconnections)
        {
            Debug.LogWarning($"[{LogTag.Migration}] Reconnection timeout — reconnected={reconnectedAuthIds.Count}/{expectedReconnections}, elapsed={CLIENT_RECONNECT_TIMEOUT:F0}s", this);
        }

        // 5 — Restore game state
        Debug.Log($"[{LogTag.Migration}] Restoring game state from snapshot", this);
        RestoreGameState();

        // Hide loading screen — migration complete for the new host
        if (LoadingUI.Instance != null) LoadingUI.Instance.StopLoading();

        ClearMigration();
    }

    // ── Regular-client flow ──────────────────────────────────────

    private IEnumerator ClientFlow()
    {
        Debug.Log($"[{LogTag.Migration}] Client — searching for migration lobby — token={PendingSnapshot.migrationToken}", this);

        // Remember old GameManager — we need to detect when a NEW one spawns
        // after the scene reloads with the new Netcode session.
        var oldGM = GameManager.Instance;

        // Poll for the lobby created by the new host (identified by migration token)
        bool joined = false;
        float elapsed = 0f;
        float nextPoll = 0f;

        while (!joined && elapsed < 15f)
        {
            if (elapsed >= nextPoll)
            {
                nextPoll = elapsed + LOBBY_POLL_INTERVAL;
                FindAndJoinMigrationLobbyAsync(PendingSnapshot.migrationToken, (success) =>
                {
                    joined = success;
                });
            }
            yield return null;
            elapsed += Time.deltaTime;
        }

        if (!joined)
        {
            Debug.LogError($"[{LogTag.Migration}] Migration lobby not found — elapsed={elapsed:F1}s, token={PendingSnapshot.migrationToken}. Returning to lobby.", this);
            if (LoadingUI.Instance != null) LoadingUI.Instance.StopLoading();
            ClearMigration();
            SceneManager.LoadScene(0);
            yield break;
        }

        Debug.Log($"[{LogTag.Migration}] Joined migration lobby — waiting for game start", this);

        // LobbyManager polling will detect the relay code and auto-join the game.
        // Wait for a NEW GameManager to be network-spawned (scene reload + Netcode connected).
        float sceneWait = 0f;
        while (sceneWait < 30f)
        {
            yield return null;
            sceneWait += Time.deltaTime;

            if (GameManager.Instance != null &&
                GameManager.Instance != oldGM &&
                GameManager.Instance.IsSpawned)
            {
                Debug.Log($"[{LogTag.Migration}] Client — new GameManager spawned — elapsed={sceneWait:F1}s", this);
                break;
            }
        }

        if (GameManager.Instance == null || !GameManager.Instance.IsSpawned)
        {
            Debug.LogError($"[{LogTag.Migration}] Game scene load timed out — elapsed={sceneWait:F1}s, GameManager={(GameManager.Instance != null ? "exists" : "<none>")}. Returning to lobby.", this);
            if (LoadingUI.Instance != null) LoadingUI.Instance.StopLoading();
            ClearMigration();
            SceneManager.LoadScene(0);
            yield break;
        }

        Debug.Log($"[{LogTag.Migration}] Client — game scene loaded, awaiting state sync from new host", this);

        // Hide loading screen — client migration complete
        if (LoadingUI.Instance != null) LoadingUI.Instance.StopLoading();

        ClearMigration();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Game state restoration (new host only)
    // ═══════════════════════════════════════════════════════════════

    private void RestoreGameState()
    {
        var gm = GameManager.Instance;
        if (gm == null || !gm.IsServer)
        {
            Debug.LogError($"[{LogTag.Migration}] Cannot restore game state — GameManager={(gm != null ? "exists" : "<none>")}, isServer={(gm != null ? gm.IsServer.ToString() : "N/A")}. Check scene load order.", this);
            return;
        }

        var snap = PendingSnapshot;

        // If the game wasn't active (e.g. host was spectating, no hand in progress),
        // there's no game state to restore. The new host just takes over with a fresh
        // game scene and players can sit down / start a new hand.
        if (!snap.isGameStarted || snap.players.Count == 0)
        {
            Debug.Log($"[{LogTag.Migration}] No active game to restore — isGameStarted={snap.isGameStarted}, players={snap.players.Count}. Scene loaded fresh.", this);
            return;
        }

        // Set table-level state
        gm.potOnTable.Value = snap.potOnTable;
        gm.smallBlind = snap.smallBlind;
        gm.bigBlind = snap.bigBlind;
        gm.currentPhase.Value = (GameManager.GamePhase)snap.currentPhase;
        gm.currentBetToCall.Value = snap.currentBetToCall;
        gm.lastRaiseAmount.Value = snap.lastRaiseAmount;
        gm.wasRaiseThisRound.Value = snap.wasRaiseThisRound;
        gm.isGameStarted.Value = snap.isGameStarted;

        // Restore server-only fields
        gm.SetLastRaiserIndex(snap.lastRaiserIndex);
        gm.SetCurrentDealerRealSlotIndex(snap.currentDealerRealSlotIndex);

        // Restore hand history continuity
        if (!string.IsNullOrEmpty(snap.tableSessionId))
        {
            gm.tableSessionId.Value = new Unity.Collections.FixedString64Bytes(snap.tableSessionId);
        }
        gm.SetCurrentHandNumber(snap.currentHandNumber);

        // Restore time banks
        var timeBanks = gm.GetTimeBankBySlotIndex();
        timeBanks.Clear();
        for (int i = 0; i < snap.timeBankSlotIndices.Count; i++)
        {
            timeBanks[snap.timeBankSlotIndices[i]] = snap.timeBankValues[i];
        }

        // Community cards
        Card[] cc = new Card[5];
        for (int i = 0; i < 5; i++)
            cc[i] = new Card((Card.Rank)snap.communityCardRanks[i], (Card.Suit)snap.communityCardSuits[i]);
        gm.communityCard1.Value = cc[0];
        gm.communityCard2.Value = cc[1];
        gm.communityCard3.Value = cc[2];
        gm.communityCard4.Value = cc[3];
        gm.communityCard5.Value = cc[4];

        // Restore deck
        var deck = new List<Card>();
        for (int i = 0; i < snap.deckRanks.Count; i++)
            deck.Add(new Card((Card.Rank)snap.deckRanks[i], (Card.Suit)snap.deckSuits[i]));
        gm.SetDeck(deck);

        // Clear existing NetworkList
        while (gm.players.Count > 0)
            gm.players.RemoveAt(gm.players.Count - 1);

        // Build a reverse auth→clientId map from the registrations that arrived
        // during the reconnection wait period.
        var authToClientId = new Dictionary<string, ulong>();
        foreach (var kvp in gm.GetClientIdToAuthPlayerId())
            authToClientId[kvp.Value] = kvp.Key;

        var slotMap = gm.GetSlotToClientId();
        slotMap.Clear();

        // Determine the turn player's real slot index from the snapshot
        int originalTurnSlotIndex = -1;
        if (snap.currentPlayerTurnIndex >= 0 && snap.currentPlayerTurnIndex < snap.players.Count)
        {
            originalTurnSlotIndex = snap.players[snap.currentPlayerTurnIndex].realSlotIndex;
        }

        bool turnPlayerWasRemoved = false;

        // Restore players — SKIP the departing host and unreconnected players entirely
        foreach (var ps in snap.players)
        {
            // Departing host — exclude from restored game
            if (!string.IsNullOrEmpty(snap.departingHostAuthPlayerId) &&
                ps.authPlayerId == snap.departingHostAuthPlayerId)
            {
                Debug.Log($"[{LogTag.Migration}] Player excluded — player={ps.playerName}, reason=departingHost, slot={ps.realSlotIndex}", this);
                if (ps.realSlotIndex == originalTurnSlotIndex)
                    turnPlayerWasRemoved = true;
                continue;
            }
            // Unreconnected players — exclude
            if (!reconnectedAuthIds.Contains(ps.authPlayerId) &&
                !string.IsNullOrEmpty(ps.authPlayerId))
            {
                Debug.Log($"[{LogTag.Migration}] Player excluded — player={ps.playerName}, reason=notReconnected, authId={ps.authPlayerId}, slot={ps.realSlotIndex}", this);
                if (ps.realSlotIndex == originalTurnSlotIndex)
                    turnPlayerWasRemoved = true;
                continue;
            }

            var pp = new PokerPlayer(ps.playerName, ps.stack, ps.iconIndex, ps.realSlotIndex)
            {
                isPlaying = ps.isPlaying,
                currentBet = ps.currentBet,
                hasActed = ps.hasActed,
                isAllIn = ps.isAllIn,
                totalBetInHand = ps.totalBetInHand,
                card1 = new Card((Card.Rank)ps.cardRank1, (Card.Suit)ps.cardSuit1),
                card2 = new Card((Card.Rank)ps.cardRank2, (Card.Suit)ps.cardSuit2),
            };

            gm.players.Add(pp);

            // Rebuild slotToClientId for connected players
            if (authToClientId.TryGetValue(ps.authPlayerId, out ulong clientId))
            {
                slotMap[ps.realSlotIndex] = clientId;
            }
        }

        gm.playersCount.Value = gm.players.Count;

        // Don't set currentPlayerTurnIndex here — ApplyMigrationState handles it
        // after the visual sync is complete.
        gm.currentPlayerTurnIndex.Value = -1;

        // Seed HandRecorder auth mappings from snapshot so the new host can
        // build rejoin payloads for returning players (including the departing host).
        var recorder = FindObjectOfType<HandRecorder>();
        if (recorder != null)
        {
            recorder.SeedAuthMappingsFromSnapshot(snap.players);

            // Track the departing host as a departed player so the new host
            // updates their Firebase summary after the hand completes.
            if (!string.IsNullOrEmpty(snap.departingHostAuthPlayerId))
            {
                string depName = "";
                int depChar = 0;
                foreach (var ps in snap.players)
                {
                    if (ps.authPlayerId == snap.departingHostAuthPlayerId)
                    {
                        depName = ps.playerName;
                        depChar = ps.iconIndex;
                        break;
                    }
                }
                recorder.TrackDepartedPlayerFromSnapshot(
                    snap.departingHostAuthPlayerId, depName, depChar);
            }
        }

        Debug.Log($"[{LogTag.Migration}] Server state restored — players={gm.players.Count}, phase={(GameManager.GamePhase)snap.currentPhase}, pot=${snap.potOnTable:F2}, reconnected={reconnectedAuthIds.Count}, turnPlayerRemoved={turnPlayerWasRemoved}", this);

        // Kick off visual sync + game resumption on GameManager
        gm.StartCoroutine(gm.ApplyMigrationState(turnPlayerWasRemoved, originalTurnSlotIndex));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Lobby helpers (async wrappers for coroutine consumption)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds a Player object with proper name and character data for lobby operations.
    /// Mirrors LobbyManager.GetPlayer() but is accessible from here.
    /// </summary>
    private Player BuildLobbyPlayer()
    {
        string playerName = PlayerNameData.PlayerName;
        int characterId = PlayerAssets.Instance != null ? PlayerAssets.Instance.currentCharacterId : 0;

        return new Player(
            AuthenticationService.Instance.PlayerId,
            null,
            new Dictionary<string, PlayerDataObject>
            {
                { LobbyManager.KEY_PLAYER_NAME, new PlayerDataObject(PlayerDataObject.VisibilityOptions.Public, playerName) },
                { LobbyManager.KEY_PLAYER_CHARACTER, new PlayerDataObject(PlayerDataObject.VisibilityOptions.Public, characterId.ToString()) }
            }
        );
    }

    private async void CreateMigrationLobbyAsync(string migrationToken, Action<string> onComplete)
    {
        try
        {
            // Preserve table session ID across migration
            string tableSessionId = "";
            if (PendingSnapshot != null && !string.IsNullOrEmpty(PendingSnapshot.tableSessionId))
            {
                tableSessionId = PendingSnapshot.tableSessionId;
            }

            var options = new CreateLobbyOptions
            {
                Player = BuildLobbyPlayer(),
                IsPrivate = false,
                Data = new Dictionary<string, DataObject>
                {
                    { LobbyManager.KEY_GAME_MODE, new DataObject(DataObject.VisibilityOptions.Public, LobbyManager.GameMode.TexasHoldem.ToString()) },
                    { LobbyManager.KEY_RELAY_JOIN_CODE, new DataObject(DataObject.VisibilityOptions.Member, "") },
                    // Index S1 so clients can query for this lobby by migration token
                    { LOBBY_KEY_MIGRATION, new DataObject(DataObject.VisibilityOptions.Public, migrationToken, DataObject.IndexOptions.S1) },
                    // Preserve table session identity for hand history continuity
                    { LobbyManager.KEY_TABLE_SESSION_ID, new DataObject(DataObject.VisibilityOptions.Member, tableSessionId) },
                }
            };

            // Reuse the original lobby name — Unity Lobby identifies rooms by
            // unique ID/code, not by name, so duplicates are fine.
            string lobbyName = PendingSnapshot?.lobbyName ?? "MyLobbyX";
            var lobby = await LobbyService.Instance.CreateLobbyAsync(lobbyName, 10, options);

            Debug.Log($"[{LogTag.Migration}] Lobby created — lobbyId={lobby.Id}, code={lobby.LobbyCode}", this);

            // Tell LobbyManager about this lobby so polling/heartbeat work
            LobbyManager.Instance?.SetJoinedLobbyForMigration(lobby, asHost: true);

            onComplete?.Invoke(lobby.LobbyCode);
        }
        catch (Exception e)
        {
            Debug.LogError($"[{LogTag.Migration}] Migration lobby creation failed — error={e.Message}. Check Unity Lobby service credentials and quota.", this);
            onComplete?.Invoke(null);
        }
    }

    private async void FindAndJoinMigrationLobbyAsync(string migrationToken, Action<bool> onComplete)
    {
        try
        {
            var queryOptions = new QueryLobbiesOptions
            {
                Count = 5,
                Filters = new List<QueryFilter>
                {
                    new QueryFilter(QueryFilter.FieldOptions.S1, migrationToken, QueryFilter.OpOptions.EQ)
                }
            };

            var response = await LobbyService.Instance.QueryLobbiesAsync(queryOptions);

            if (response.Results.Count > 0)
            {
                var lobby = response.Results[0];
                Debug.Log($"[{LogTag.Migration}] Found migration lobby — lobbyId={lobby.Id}", this);

                // Join it
                var joinOptions = new JoinLobbyByIdOptions
                {
                    Player = BuildLobbyPlayer()
                };
                var joined = await LobbyService.Instance.JoinLobbyByIdAsync(lobby.Id, joinOptions);

                // Tell LobbyManager
                LobbyManager.Instance?.SetJoinedLobbyForMigration(joined);

                onComplete?.Invoke(true);
            }
            else
            {
                onComplete?.Invoke(false);
            }
        }
        catch (LobbyServiceException e)
        {
            if (e.Reason != LobbyExceptionReason.RateLimited)
                Debug.LogWarning($"[{LogTag.Migration}] Lobby query failed — error={e.Message}", this);
            onComplete?.Invoke(false);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Utility
    // ═══════════════════════════════════════════════════════════════

    public void ClearMigration()
    {
        IsMigrationPending = false;
        PendingSnapshot = null;
        IsNewHost = false;
        reconnectedAuthIds.Clear();
    }
}
