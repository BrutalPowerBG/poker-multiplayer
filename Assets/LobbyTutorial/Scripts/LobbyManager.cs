using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using UnityEngine;
using UnityEngine.SceneManagement;

public class LobbyManager : MonoBehaviour {

    public static LobbyManager Instance { get; private set; }
    public static bool IsHost { get; private set; }
    public static string RelayJoinCode { get; private set; }

    public const string KEY_PLAYER_NAME = "PlayerName";
    public const string KEY_PLAYER_CHARACTER = "Character";
    public const string KEY_PLAYER_READY = "PlayerReady";
    public const string KEY_GAME_MODE = "GameMode";
    public const string KEY_START_GAME = "StartGame";
    public const string KEY_RELAY_JOIN_CODE = "RelayJoinCode";
    public const string KEY_START_TIME = "StartTimeUtcMs";

    // Game settings keys (stored in lobby data)
    public const string KEY_SMALL_BLIND = "SmallBlind";
    public const string KEY_BIG_BLIND = "BigBlind";
    public const string KEY_MIN_BUY_IN = "MinBuyIn";
    public const string KEY_MAX_BUY_IN = "MaxBuyIn";
    public const string KEY_SESSION_AVAILABLE_BALANCE = "SessionAvailableBalance";
    public const string KEY_UNLIMITED_AVAILABLE_BALANCE = "UnlimitedAvailableBalance";
    public const string KEY_TURN_TIMER = "TurnTimer";
    public const string KEY_TIME_BANK = "TimeBank";

    // Table session identity (persists across host migrations)
    public const string KEY_TABLE_SESSION_ID = "TableSessionId";

    // Default game settings
    public const float DEFAULT_SMALL_BLIND = 0.30f;
    public const float DEFAULT_BIG_BLIND = 0.60f;
    public const float DEFAULT_MIN_BUY_IN = 10f;
    public const float DEFAULT_MAX_BUY_IN = 100f;
    public const float DEFAULT_SESSION_AVAILABLE_BALANCE = 500f;
    public const bool DEFAULT_UNLIMITED_AVAILABLE_BALANCE = false;
    public const float DEFAULT_TURN_TIMER = 15f;
    public const float DEFAULT_TIME_BANK = 60f;

    // PlayerPrefs keys for remembering host's last-used table settings
    private const string PREF_SMALL_BLIND = "lobby_SmallBlind";
    private const string PREF_BIG_BLIND = "lobby_BigBlind";
    private const string PREF_MIN_BUY_IN = "lobby_MinBuyIn";
    private const string PREF_MAX_BUY_IN = "lobby_MaxBuyIn";
    private const string PREF_SESSION_BALANCE = "lobby_SessionBalance";
    private const string PREF_UNLIMITED_BALANCE = "lobby_UnlimitedBalance";
    private const string PREF_TURN_TIMER = "lobby_TurnTimer";
    private const string PREF_TIME_BANK = "lobby_TimeBank";

    // ── Backend selection (single switch for auth + data) ─────────
    [Header("Backend")]
    [Tooltip("Switches authentication AND data storage.\nFirebase = Firebase Auth + Firestore.\nUnityServices = Unity Auth + Cloud Save.")]
    [SerializeField] private BackendType backendType = BackendType.Firebase;

    /// <summary>The active backend type (read-only).</summary>
    public BackendType Backend => backendType;

    /// <summary>The active auth provider (Firebase or Unity Cloud).</summary>
    public IAuthProvider AuthProvider => authProvider;

    /// <summary>True when the user is fully authenticated.</summary>
    public bool IsAuthenticated => authProvider != null && authProvider.IsAuthenticated;

    private IAuthProvider authProvider;

    // Timing Constants
    private const float LOBBY_POLL_INTERVAL = 2f;
    
    // Scene Constants
    private const int GAME_SCENE_BUILD_INDEX = 1;

    public event EventHandler OnLeftLobby;

    public event EventHandler<LobbyEventArgs> OnJoinedLobby;
    public event EventHandler<LobbyEventArgs> OnJoinedLobbyUpdate;
    public event EventHandler<LobbyEventArgs> OnKickedFromLobby;
    public event EventHandler<LobbyEventArgs> OnLobbyGameModeChanged;

    public event EventHandler<bool> OnCanStartGameChanged;
    public event EventHandler<bool> OnCanLateJoinGameChanged;
    public event EventHandler<LobbyEventArgs> OnLobbyStartGame;
    public class LobbyEventArgs : EventArgs {
        public Lobby lobby;
    }

    public event EventHandler<OnLobbyListChangedEventArgs> OnLobbyListChanged;
    public class OnLobbyListChangedEventArgs : EventArgs {
        public List<Lobby> lobbyList;
    }

    public event EventHandler<string> OnJoinLobbyFailed;


    public enum GameMode {
        TexasHoldem,
        OmahaHoldem
    }



    private float heartbeatTimer;
    private float lobbyPollTimer = LOBBY_POLL_INTERVAL; // Initialize to prevent immediate polling on join
    private float refreshLobbyListTimer = 5f;
    private Lobby joinedLobby;
    private string playerName;
    public string PlayerName => playerName;
    private bool alreadyStartedGame;
    private bool hasLoadedGameScene;
    private bool hasSeenGameStartFlag;
    private bool allowAutoJoinOnStart = true;
    private long lobbyStartTimeMs;
    private float lobbyStartAgeSeconds = float.MaxValue;
    private bool isJoiningLobby = false; // Flag to prevent multiple simultaneous join attempts
    private bool isPolling = false; // Flag to prevent concurrent polling requests


    private void Awake() {
        if (Instance != null && Instance != this) {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        // Create the auth provider based on the selected backend
        CreateAuthProvider();
    }

    private void Update() {
        //HandleRefreshLobbyList(); // Disabled Auto Refresh for testing with multiple builds
        HandleLobbyHeartbeat();
        HandleLobbyPolling();
    }

    private void OnApplicationQuit() {
        FireAndForgetLeaveLobby();
    }

    private void OnDestroy() {
        FireAndForgetLeaveLobby();
    }

    /// <summary>
    /// Best-effort lobby leave for app quit / object destruction.
    /// Cannot await because Unity is tearing down, so we fire-and-forget.
    /// </summary>
    private void FireAndForgetLeaveLobby() {
        if (joinedLobby == null) return;

        try {
            string lobbyId = joinedLobby.Id;
            string playerId = AuthenticationService.Instance.PlayerId;
            _ = LobbyService.Instance.RemovePlayerAsync(lobbyId, playerId);
            Debug.Log("[LobbyManager] Fired lobby leave on quit/destroy (best-effort).");
        } catch (System.Exception e) {
            Debug.LogWarning($"[LobbyManager] Failed to leave lobby on quit/destroy: {e.Message}");
        }

        joinedLobby = null;
    }

    // ══════════════════════════════════════════════════════════════
    //  Auth Provider Setup
    // ══════════════════════════════════════════════════════════════

    private void CreateAuthProvider() {
        switch (backendType) {
            case BackendType.Firebase:
                var fbGo = new GameObject("[FirebaseManager]");
                fbGo.transform.SetParent(transform);
                authProvider = fbGo.AddComponent<FirebaseManager>();
                Debug.Log("[LobbyManager] Backend: Firebase Auth + Firestore");
                break;

            case BackendType.UnityServices:
            default:
                var ucGo = new GameObject("[UnityCloudManager]");
                ucGo.transform.SetParent(transform);
                authProvider = ucGo.AddComponent<UnityCloudManager>();
                Debug.Log("[LobbyManager] Backend: Unity Auth + Cloud Save");
                break;
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  Authentication (delegates to IAuthProvider)
    // ══════════════════════════════════════════════════════════════

    /// <summary>Signs in as a guest (anonymous).</summary>
    public async void AuthenticateGuest(string playerName) {
        this.playerName = playerName;
        try {
            await authProvider.SignInAsGuestAsync(this.playerName);
            Debug.Log("[LobbyManager] Guest authentication successful.");
            RefreshLobbyList();
        } catch (Exception ex) {
            Debug.LogError($"[LobbyManager] Guest auth failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Logs in with credentials (email+password for Firebase, username+password for Unity).
    /// Returns null on success, or an error message string on failure.
    /// </summary>
    public async Task<string> LoginWithAccount(string playerName, string email, string password) {
        this.playerName = playerName;

        int prevCharId = PlayerAssets.Instance != null ? PlayerAssets.Instance.currentCharacterId : 0;

        string error = await authProvider.LoginAsync(this.playerName, email, password);
        if (error != null) return error;

        // If character changed from cloud sync, update lobby
        int newCharId = PlayerAssets.Instance != null ? PlayerAssets.Instance.currentCharacterId : 0;
        if (prevCharId != newCharId && joinedLobby != null) {
            Debug.Log($"Character synced from cloud ({prevCharId} → {newCharId}), updating lobby");
            UpdatePlayerCharacter(newCharId);
        }

        RefreshLobbyList();
        return null;
    }

    /// <summary>
    /// Registers a new account.
    /// Returns null on success (user is auto-signed-in), or an error message string on failure.
    /// </summary>
    public async Task<string> RegisterAccount(string playerName, string username, string email, string password) {
        this.playerName = playerName;

        string error = await authProvider.RegisterAsync(this.playerName, username, email, password);
        if (error != null) return error;

        RefreshLobbyList();
        return null;
    }

    /// <summary>
    /// Attempts to silently restore a persisted non-guest session from a previous
    /// app launch (e.g. a saved Firebase token).  If successful the lobby list is
    /// refreshed and the caller can skip showing the auth screen.
    /// Returns true when a valid session was restored, false otherwise.
    /// </summary>
    public async Task<bool> TryResumeSessionAsync() {
        if (authProvider == null) return false;

        bool resumed = await authProvider.TryResumeSessionAsync(playerName);
        if (resumed) {
            Debug.Log("[LobbyManager] Session resumed — refreshing lobby list.");
            RefreshLobbyList();
        }
        return resumed;
    }

    /// <summary>
    /// Signs out of the active auth backend and clears session state.
    /// Callers should navigate to the auth screen afterwards
    /// (e.g. via <c>LobbyScreenManager.Instance.ShowScreen(Screen.Auth)</c>).
    /// </summary>
    public void Logout()
    {
        authProvider?.SignOut();
        playerName = "";
        Debug.Log("[LobbyManager] Logged out.");
    }

    /// <summary>
    /// Permanently deletes the authenticated account, signs out, and clears local data.
    /// Returns null on success or an error message on failure.
    /// </summary>
    public async System.Threading.Tasks.Task<string> DeleteAccountAsync()
    {
        if (authProvider == null) return "No auth provider.";

        string error = await authProvider.DeleteAccountAsync();
        if (error == null)
        {
            playerName = "";
            PlayerPrefs.DeleteKey("auth_remember_me");
            PlayerPrefs.DeleteKey("auth_saved_email");
            PlayerPrefs.Save();
            Debug.Log("[LobbyManager] Account deleted.");
        }
        return error;
    }

    // ══════════════════════════════════════════════════════════════
    //  Lobby Heartbeat / Polling
    // ══════════════════════════════════════════════════════════════

    private void HandleRefreshLobbyList() {
        if (UnityServices.State == ServicesInitializationState.Initialized && AuthenticationService.Instance.IsSignedIn) {
            refreshLobbyListTimer -= Time.deltaTime;
            if (refreshLobbyListTimer < 0f) {
                float refreshLobbyListTimerMax = 5f;
                refreshLobbyListTimer = refreshLobbyListTimerMax;

                RefreshLobbyList();
            }
        }
    }

    private async void HandleLobbyHeartbeat() {
        if (IsLobbyHost() && joinedLobby != null) {
            heartbeatTimer -= Time.deltaTime;
            if (heartbeatTimer < 0f) {
                float heartbeatTimerMax = 15f;
                heartbeatTimer = heartbeatTimerMax;

                // Capture the lobby ID before the async call so we can detect if it
                // changed (e.g. due to host migration) while the request was in-flight.
                string heartbeatLobbyId = joinedLobby.Id;

                try {
                    Debug.Log("Heartbeat");
                    await LobbyService.Instance.SendHeartbeatPingAsync(heartbeatLobbyId);
                } catch (LobbyServiceException e) {
                    if (e.Reason == LobbyExceptionReason.LobbyNotFound) {
                        // Only clear if the lobby we pinged is still the current one.
                        // During host migration, SetJoinedLobbyForMigration may have
                        // replaced joinedLobby while this request was in-flight.
                        if (joinedLobby != null && joinedLobby.Id == heartbeatLobbyId) {
                            Debug.LogWarning("Lobby no longer exists during heartbeat. Clearing lobby state.");
                            joinedLobby = null;
                            allowAutoJoinOnStart = false;
                            hasLoadedGameScene = false;
                            OnLeftLobby?.Invoke(this, EventArgs.Empty);
                        } else {
                            Debug.Log($"Stale heartbeat for old lobby {heartbeatLobbyId} returned NotFound — ignoring (current lobby is different).");
                        }
                    } else {
                        Debug.LogWarning($"Heartbeat failed: {e.Message}");
                    }
                }
            }
        }
    }

    private async void HandleLobbyPolling() {
        if (joinedLobby == null || isPolling) {
            return;
        }

        lobbyPollTimer -= Time.deltaTime;
        if (lobbyPollTimer < 0f) {
            lobbyPollTimer = LOBBY_POLL_INTERVAL;
            
            // Prevent concurrent polling requests
            isPolling = true;

            // Capture the lobby ID before the async call.  During host migration,
            // SetJoinedLobbyForMigration may replace joinedLobby while the request
            // is in-flight.  We must not overwrite the new reference with data
            // from the old (now-deleted) lobby.
            string polledLobbyId = joinedLobby.Id;

            try {
                var polledLobby = await LobbyService.Instance.GetLobbyAsync(polledLobbyId);

                // If the lobby reference changed while we were waiting (migration),
                // discard the stale response — the new lobby is authoritative.
                if (joinedLobby != null && joinedLobby.Id != polledLobbyId) {
                    Debug.Log($"LobbyManager: Lobby changed during poll (was {polledLobbyId}, now {joinedLobby.Id}). Discarding stale response.");
                    return; // finally block still runs (isPolling = false)
                }

                joinedLobby = polledLobby;
                bool gameJustStarted = SyncStartedFromLobbyData(joinedLobby);

                OnJoinedLobbyUpdate?.Invoke(this, new LobbyEventArgs { lobby = joinedLobby });

                // Client: auto-join if game is started, within 10s of start, and we've not loaded the scene yet
                if (!IsLobbyHost() && alreadyStartedGame && allowAutoJoinOnStart && !hasLoadedGameScene && lobbyStartAgeSeconds <= 10f) {
                    // Client: Check for relay join code from host
                    if (joinedLobby.Data != null && joinedLobby.Data.ContainsKey(KEY_RELAY_JOIN_CODE)) {
                        string relayCode = joinedLobby.Data[KEY_RELAY_JOIN_CODE].Value;
                        if (!string.IsNullOrEmpty(relayCode)) {
                            // Relay code is available, join the game
                            Debug.Log("LobbyManager: Auto JoinGame via HandleLobbyPolling " + relayCode + hasSeenGameStartFlag);
                            JoinGame(relayCode);
                        }
                        // If relay code is empty, it's normal - host hasn't created relay yet, just wait
                    }
                }
                if (joinedLobby.Players != null && joinedLobby.Players.Count >= 2) {
                if (!alreadyStartedGame) {
                    if (IsLobbyHost()) {
                            //StartGame();
                            OnCanStartGameChanged?.Invoke(this, true);
                        }
                    }
                    else {
                        OnCanLateJoinGameChanged?.Invoke(this, true);
                    }
                }

                if (!IsPlayerInLobby()) {
                    // Player was kicked out of this lobby
                    Debug.Log("Kicked from Lobby!");

                    OnKickedFromLobby?.Invoke(this, new LobbyEventArgs { lobby = joinedLobby });

                    joinedLobby = null;
                }
            } catch (LobbyServiceException e) {
                if (e.Reason == LobbyExceptionReason.RateLimited) {
                    Debug.LogWarning("Rate limited during lobby polling. Will retry on next poll.");
                    // Don't reset timer, let it retry on next cycle
                } else if (e.Reason == LobbyExceptionReason.LobbyNotFound) {
                    // Only clear if the lobby we polled is still the current one.
                    if (joinedLobby != null && joinedLobby.Id == polledLobbyId) {
                        Debug.LogWarning("Lobby no longer exists (deleted or expired). Clearing lobby state.");
                        joinedLobby = null;
                        allowAutoJoinOnStart = false;
                        hasLoadedGameScene = false;
                        OnLeftLobby?.Invoke(this, EventArgs.Empty);
                    } else {
                        Debug.Log($"Stale poll for old lobby {polledLobbyId} returned NotFound — ignoring (current lobby is different).");
                    }
                } else {
                    Debug.LogError($"Error polling lobby: {e}");
                }
            } finally {
                isPolling = false;
            }
        }
    }

    public Lobby GetJoinedLobby() {
        return joinedLobby;
    }

    public bool IsLobbyHost() {
        return joinedLobby != null && joinedLobby.HostId == AuthenticationService.Instance.PlayerId;
    }

    private bool IsPlayerInLobby() {
        if (joinedLobby != null && joinedLobby.Players != null) {
            foreach (Player player in joinedLobby.Players) {
                if (player.Id == AuthenticationService.Instance.PlayerId) {
                    // This player is in this lobby
                    return true;
                }
            }
        }
        return false;
    }

    private Player GetPlayer() {
        // Use saved character ID from PlayerAssets if available, otherwise default to 0
        int savedCharacterId = PlayerAssets.Instance != null ? PlayerAssets.Instance.currentCharacterId : 0;
        
        return new Player(AuthenticationService.Instance.PlayerId, null, new Dictionary<string, PlayerDataObject> {
            { KEY_PLAYER_NAME, new PlayerDataObject(PlayerDataObject.VisibilityOptions.Public, playerName) },
            { KEY_PLAYER_CHARACTER, new PlayerDataObject(PlayerDataObject.VisibilityOptions.Public, savedCharacterId.ToString()) }
        });
    }

    public void ChangeGameMode() {
        if (IsLobbyHost()) {
            GameMode gameMode =
                Enum.Parse<GameMode>(joinedLobby.Data[KEY_GAME_MODE].Value);

            switch (gameMode) {
                default:
                case GameMode.TexasHoldem:
                    gameMode = GameMode.OmahaHoldem;
                    break;
                case GameMode.OmahaHoldem:
                    gameMode = GameMode.TexasHoldem;
                    break;
            }

            UpdateLobbyGameMode(gameMode);
        }
    }

    public async void CreateLobby(string lobbyName, int maxPlayers, bool isPrivate, GameMode gameMode) {
        Player player = GetPlayer();

        // Generate a unique Table Session ID for this lobby (persists across host migrations)
        string tableSessionId = Guid.NewGuid().ToString("N");

        // Load the host's last-used settings from PlayerPrefs (falls back to defaults)
        var saved = LoadSettingsFromPrefs();

        CreateLobbyOptions options = new CreateLobbyOptions {
            Player = player,
            IsPrivate = isPrivate,
            Data = new Dictionary<string, DataObject> {
                { KEY_GAME_MODE, new DataObject(DataObject.VisibilityOptions.Public, gameMode.ToString()) },
                { KEY_RELAY_JOIN_CODE, new DataObject(DataObject.VisibilityOptions.Member, "") },
                { KEY_SMALL_BLIND, new DataObject(DataObject.VisibilityOptions.Public, saved.sb.ToString()) },
                { KEY_BIG_BLIND, new DataObject(DataObject.VisibilityOptions.Public, saved.bb.ToString()) },
                { KEY_MIN_BUY_IN, new DataObject(DataObject.VisibilityOptions.Public, saved.minBuy.ToString()) },
                { KEY_MAX_BUY_IN, new DataObject(DataObject.VisibilityOptions.Public, saved.maxBuy.ToString()) },
                { KEY_SESSION_AVAILABLE_BALANCE, new DataObject(DataObject.VisibilityOptions.Public, saved.sessionBalance.ToString()) },
                { KEY_UNLIMITED_AVAILABLE_BALANCE, new DataObject(DataObject.VisibilityOptions.Public, saved.unlimited ? "1" : "0") },
                { KEY_TURN_TIMER, new DataObject(DataObject.VisibilityOptions.Public, saved.turnTimer.ToString()) },
                { KEY_TIME_BANK, new DataObject(DataObject.VisibilityOptions.Public, saved.timeBank.ToString()) },
                { KEY_TABLE_SESSION_ID, new DataObject(DataObject.VisibilityOptions.Member, tableSessionId) }
            }
        };

        Lobby lobby = await LobbyService.Instance.CreateLobbyAsync(lobbyName, maxPlayers, options);

        joinedLobby = lobby;
        lobbyPollTimer = LOBBY_POLL_INTERVAL; // Reset timer when joining a lobby to prevent immediate polling

        OnJoinedLobby?.Invoke(this, new LobbyEventArgs { lobby = lobby });

        Debug.Log("Created Lobby " + lobby.Name);
    }

    public async void RefreshLobbyList() {
        if (UnityServices.State != ServicesInitializationState.Initialized ||
            !AuthenticationService.Instance.IsSignedIn) return;

        try {
            QueryLobbiesOptions options = new QueryLobbiesOptions();
            options.Count = 25;

            // Filter for open lobbies only
            options.Filters = new List<QueryFilter> {
                new QueryFilter(
                    field: QueryFilter.FieldOptions.AvailableSlots,
                    op: QueryFilter.OpOptions.GT,
                    value: "0")
            };

            // Order by newest lobbies first
            options.Order = new List<QueryOrder> {
                new QueryOrder(
                    asc: false,
                    field: QueryOrder.FieldOptions.Created)
            };

            QueryResponse lobbyListQueryResponse = await LobbyService.Instance.QueryLobbiesAsync();

            OnLobbyListChanged?.Invoke(this, new OnLobbyListChangedEventArgs { lobbyList = lobbyListQueryResponse.Results });
        } catch (LobbyServiceException e) {
            Debug.Log(e);
        }
    }

    public async void JoinLobbyByCode(string lobbyCode) {
        // Prevent multiple simultaneous join attempts
        if (isJoiningLobby) {
            Debug.LogWarning("Already attempting to join a lobby. Please wait.");
            return;
        }

        isJoiningLobby = true;
        
        try {
            Player player = GetPlayer();

            int maxRetries = 3;
            int retryCount = 0;
            float baseDelay = 1f;

            while (retryCount < maxRetries) {
                try {
                    Lobby lobby = await LobbyService.Instance.JoinLobbyByCodeAsync(lobbyCode, new JoinLobbyByCodeOptions {
                        Player = player
                    });

                    joinedLobby = lobby;
                    SyncStartedFromLobbyData(joinedLobby);
                    allowAutoJoinOnStart = !alreadyStartedGame || lobbyStartAgeSeconds <= 10f;
                    lobbyPollTimer = LOBBY_POLL_INTERVAL; // Reset timer when joining a lobby to prevent immediate polling
                    OnJoinedLobby?.Invoke(this, new LobbyEventArgs { lobby = lobby });
                    isJoiningLobby = false;
                    return;
                } catch (LobbyServiceException e) {
                    if (e.Reason == LobbyExceptionReason.RateLimited) {
                        retryCount++;
                        if (retryCount < maxRetries) {
                            float delay = baseDelay * Mathf.Pow(2, retryCount - 1); // Exponential backoff: 1s, 2s, 4s
                            Debug.LogWarning($"Rate limited. Retrying in {delay} seconds... (Attempt {retryCount}/{maxRetries})");
                            await System.Threading.Tasks.Task.Delay((int)(delay * 1000)); // Convert to milliseconds
                        } else {
                            Debug.LogError("Failed to join lobby after multiple retries due to rate limiting. Please try again later.");
                            throw;
                        }
                    } else {
                        // For non-rate-limit errors, don't retry
                        Debug.LogError($"Error joining lobby: {e}");
                        throw;
                    }
                }
            }
        } catch (LobbyServiceException e) {
            Debug.LogError($"Failed to join lobby by code: {e}");

            // Provide a user-friendly error message
            string userMessage;
            if (e.Reason == LobbyExceptionReason.LobbyNotFound) {
                userMessage = "Lobby not found. Check the code and try again.";
            } else if (e.Reason == LobbyExceptionReason.RateLimited) {
                userMessage = "Too many attempts. Please wait a moment.";
            } else if (e.Reason == LobbyExceptionReason.LobbyFull) {
                userMessage = "Lobby is full.";
            } else {
                userMessage = "Failed to join lobby. Please try again.";
            }
            OnJoinLobbyFailed?.Invoke(this, userMessage);
        } finally {
            isJoiningLobby = false;
        }
    }

    public async void JoinLobby(Lobby lobby) {
        // Prevent multiple simultaneous join attempts
        if (isJoiningLobby) {
            Debug.LogWarning("Already attempting to join a lobby. Please wait.");
            return;
        }

        isJoiningLobby = true;
        
        try {
            Player player = GetPlayer();

            int maxRetries = 3;
            int retryCount = 0;
            float baseDelay = 1f; // Start with 1 second delay

            while (retryCount < maxRetries) {
                try {
                    joinedLobby = await LobbyService.Instance.JoinLobbyByIdAsync(lobby.Id, new JoinLobbyByIdOptions {
                        Player = player
                    });

                    SyncStartedFromLobbyData(joinedLobby);
                    allowAutoJoinOnStart = !alreadyStartedGame || lobbyStartAgeSeconds <= 10f;
                    OnJoinedLobby?.Invoke(this, new LobbyEventArgs { lobby = joinedLobby });
                    isJoiningLobby = false;
                    return; // Success, exit the method
                } catch (LobbyServiceException e) {
                    if (e.Reason == LobbyExceptionReason.RateLimited) {
                        retryCount++;
                        if (retryCount < maxRetries) {
                            float delay = baseDelay * Mathf.Pow(2, retryCount - 1); // Exponential backoff: 1s, 2s, 4s
                            Debug.LogWarning($"Rate limited. Retrying in {delay} seconds... (Attempt {retryCount}/{maxRetries})");
                            await System.Threading.Tasks.Task.Delay((int)(delay * 1000)); // Convert to milliseconds
                        } else {
                            Debug.LogError("Failed to join lobby after multiple retries due to rate limiting. Please try again later.");
                            throw;
                        }
                    } else {
                        // For non-rate-limit errors, don't retry
                        Debug.LogError($"Error joining lobby: {e}");
                        throw;
                    }
                }
            }
        } catch (LobbyServiceException e) {
            Debug.LogError($"Failed to join lobby: {e}");
            // Optionally invoke an error event here if you have one
        } finally {
            isJoiningLobby = false;
        }
    }

    public async void UpdatePlayerName(string playerName) {
        this.playerName = playerName;

        if (joinedLobby != null) {
            try {
                UpdatePlayerOptions options = new UpdatePlayerOptions();

                options.Data = new Dictionary<string, PlayerDataObject>() {
                    {
                        KEY_PLAYER_NAME, new PlayerDataObject(
                            visibility: PlayerDataObject.VisibilityOptions.Public,
                            value: playerName)
                    }
                };

                string playerId = AuthenticationService.Instance.PlayerId;

                Lobby lobby = await LobbyService.Instance.UpdatePlayerAsync(joinedLobby.Id, playerId, options);
                joinedLobby = lobby;
                    SyncStartedFromLobbyData(joinedLobby);

                OnJoinedLobbyUpdate?.Invoke(this, new LobbyEventArgs { lobby = joinedLobby });
            } catch (LobbyServiceException e) {
                Debug.Log(e);
            }
        }
    }

    public async void UpdatePlayerCharacter(int characterId) {
        if (joinedLobby != null) {
            try {
                UpdatePlayerOptions options = new UpdatePlayerOptions();

                options.Data = new Dictionary<string, PlayerDataObject>() {
                    {
                        KEY_PLAYER_CHARACTER, new PlayerDataObject(
                            visibility: PlayerDataObject.VisibilityOptions.Public,
                            value: characterId.ToString())
                    }
                };

                string playerId = AuthenticationService.Instance.PlayerId;

                Lobby lobby = await LobbyService.Instance.UpdatePlayerAsync(joinedLobby.Id, playerId, options);
                joinedLobby = lobby;
                    SyncStartedFromLobbyData(joinedLobby);

                OnJoinedLobbyUpdate?.Invoke(this, new LobbyEventArgs { lobby = joinedLobby });
            } catch (LobbyServiceException e) {
                Debug.Log(e);
            }
        }
    }

    public async void UpdatePlayerReady(bool isReady) {
        if (joinedLobby != null) {
            try {
                UpdatePlayerOptions options = new UpdatePlayerOptions {
                    Data = new Dictionary<string, PlayerDataObject>() {
                        { KEY_PLAYER_READY, new PlayerDataObject(
                            visibility: PlayerDataObject.VisibilityOptions.Public,
                            value: isReady ? "1" : "0") }
                    }
                };

                string playerId = AuthenticationService.Instance.PlayerId;
                Lobby lobby = await LobbyService.Instance.UpdatePlayerAsync(joinedLobby.Id, playerId, options);
                joinedLobby = lobby;
            } catch (LobbyServiceException e) {
                Debug.Log(e);
            }
        }
    }

    public async void QuickJoinLobby() {
        try {
            QuickJoinLobbyOptions options = new QuickJoinLobbyOptions();

            Lobby lobby = await LobbyService.Instance.QuickJoinLobbyAsync(options);
            joinedLobby = lobby;
            SyncStartedFromLobbyData(joinedLobby);

            OnJoinedLobby?.Invoke(this, new LobbyEventArgs { lobby = lobby });
        } catch (LobbyServiceException e) {
            Debug.Log(e);
        }
    }

    

    public async void LeaveLobby() {
        if (joinedLobby != null) {
            try {
                await LobbyService.Instance.RemovePlayerAsync(joinedLobby.Id, AuthenticationService.Instance.PlayerId);
            } catch (LobbyServiceException e) {
                Debug.LogWarning($"LeaveLobby: Error removing player (lobby may already be deleted): {e.Message}");
            }
            
            // Always clear state, even if API call failed
            joinedLobby = null;
            allowAutoJoinOnStart = false;
            hasLoadedGameScene = false;

            OnLeftLobby?.Invoke(this, EventArgs.Empty);
            HandleLobbyPlayerCountChanged();
        } else {
            // Ensure UI/buttons reset even if already null
            HandleLobbyPlayerCountChanged();
        }
    }

    public async void KickPlayer(string playerId) {
        if (IsLobbyHost()) {
            try {
                await LobbyService.Instance.RemovePlayerAsync(joinedLobby.Id, playerId);
                HandleLobbyPlayerCountChanged();
            } catch (LobbyServiceException e) {
                Debug.Log(e);
            }
        }
    }
    public void HandleLobbyPlayerCountChanged() {
        if (joinedLobby == null || joinedLobby.Players == null) {
            Debug.Log("HandleLobbyPlayerCountChanged: no lobby; disabling start/late-join.");
            OnCanStartGameChanged?.Invoke(this, false);
            OnCanLateJoinGameChanged?.Invoke(this, false);
            return;
        }

        int count = joinedLobby.Players.Count;
        Debug.Log("HandleLobbyPlayerCountChanged " + count);
        if (count >= 2) {
            if (!IsLobbyHost()) {
                OnCanLateJoinGameChanged?.Invoke(this, true);  
            }
        } else {
            OnCanStartGameChanged?.Invoke(this, false);
            OnCanLateJoinGameChanged?.Invoke(this, false);
        }
    }

    public async void UpdateLobbyGameMode(GameMode gameMode) {
        try {
            Debug.Log("UpdateLobbyGameMode " + gameMode);
            
            Lobby lobby = await LobbyService.Instance.UpdateLobbyAsync(joinedLobby.Id, new UpdateLobbyOptions {
                Data = new Dictionary<string, DataObject> {
                    { KEY_GAME_MODE, new DataObject(DataObject.VisibilityOptions.Public, gameMode.ToString()) }
                }
            });

            joinedLobby = lobby;
            SyncStartedFromLobbyData(joinedLobby);

            OnLobbyGameModeChanged?.Invoke(this, new LobbyEventArgs { lobby = joinedLobby });
        } catch (LobbyServiceException e) {
            Debug.Log(e);
        }
    }

    /// <summary>
    /// Host-only: updates the game settings (blinds, buy-in) in the lobby data.
    /// </summary>
    public async void UpdateGameSettings(
        float smallBlind,
        float bigBlind,
        float minBuyIn,
        float maxBuyIn,
        float sessionAvailableBalance,
        bool unlimitedAvailableBalance,
        float turnTimer,
        float timeBank) {
        if (!IsLobbyHost()) return;

        try {
            Debug.Log($"UpdateGameSettings SB={smallBlind} BB={bigBlind} Min={minBuyIn} Max={maxBuyIn} SessionBalance={sessionAvailableBalance} Unlimited={unlimitedAvailableBalance} TurnTimer={turnTimer} TimeBank={timeBank}");

            Lobby lobby = await LobbyService.Instance.UpdateLobbyAsync(joinedLobby.Id, new UpdateLobbyOptions {
                Data = new Dictionary<string, DataObject> {
                    { KEY_SMALL_BLIND, new DataObject(DataObject.VisibilityOptions.Public, smallBlind.ToString()) },
                    { KEY_BIG_BLIND, new DataObject(DataObject.VisibilityOptions.Public, bigBlind.ToString()) },
                    { KEY_MIN_BUY_IN, new DataObject(DataObject.VisibilityOptions.Public, minBuyIn.ToString()) },
                    { KEY_MAX_BUY_IN, new DataObject(DataObject.VisibilityOptions.Public, maxBuyIn.ToString()) },
                    { KEY_SESSION_AVAILABLE_BALANCE, new DataObject(DataObject.VisibilityOptions.Public, sessionAvailableBalance.ToString()) },
                    { KEY_UNLIMITED_AVAILABLE_BALANCE, new DataObject(DataObject.VisibilityOptions.Public, unlimitedAvailableBalance ? "1" : "0") },
                    { KEY_TURN_TIMER, new DataObject(DataObject.VisibilityOptions.Public, turnTimer.ToString()) },
                    { KEY_TIME_BANK, new DataObject(DataObject.VisibilityOptions.Public, timeBank.ToString()) }
                }
            });

            // Persist for next lobby creation
            SaveSettingsToPrefs(smallBlind, bigBlind, minBuyIn, maxBuyIn,
                sessionAvailableBalance, unlimitedAvailableBalance, turnTimer, timeBank);

            joinedLobby = lobby;
            SyncStartedFromLobbyData(joinedLobby);

            OnJoinedLobbyUpdate?.Invoke(this, new LobbyEventArgs { lobby = joinedLobby });
        } catch (LobbyServiceException e) {
            Debug.Log(e);
        }
    }

    /// <summary>
    /// Reads a float game setting from the joined lobby data. Returns defaultValue if not found.
    /// </summary>
    public float GetLobbyGameSetting(string key, float defaultValue) {
        if (joinedLobby != null && joinedLobby.Data != null && joinedLobby.Data.TryGetValue(key, out var dataObj)) {
            if (float.TryParse(dataObj.Value, out float result)) {
                return result;
            }
        }
        return defaultValue;
    }

    public float GetSmallBlind() => GetLobbyGameSetting(KEY_SMALL_BLIND, DEFAULT_SMALL_BLIND);
    public float GetBigBlind() => GetLobbyGameSetting(KEY_BIG_BLIND, DEFAULT_BIG_BLIND);
    public float GetMinBuyIn() => GetLobbyGameSetting(KEY_MIN_BUY_IN, DEFAULT_MIN_BUY_IN);
    public float GetMaxBuyIn() => GetLobbyGameSetting(KEY_MAX_BUY_IN, DEFAULT_MAX_BUY_IN);
    public float GetSessionAvailableBalance() => GetLobbyGameSetting(KEY_SESSION_AVAILABLE_BALANCE, DEFAULT_SESSION_AVAILABLE_BALANCE);

    public bool GetLobbyBoolSetting(string key, bool defaultValue) {
        if (joinedLobby != null && joinedLobby.Data != null && joinedLobby.Data.TryGetValue(key, out var dataObj)) {
            string value = dataObj.Value;
            if (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
            if (string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)) {
                return false;
            }
        }
        return defaultValue;
    }

    public bool IsUnlimitedAvailableBalance() =>
        GetLobbyBoolSetting(KEY_UNLIMITED_AVAILABLE_BALANCE, DEFAULT_UNLIMITED_AVAILABLE_BALANCE);

    public float GetTurnTimer() => GetLobbyGameSetting(KEY_TURN_TIMER, DEFAULT_TURN_TIMER);
    public float GetTimeBank() => GetLobbyGameSetting(KEY_TIME_BANK, DEFAULT_TIME_BANK);

    // ══════════════════════════════════════════════════════════════
    //  PlayerPrefs — Remember host's last-used table settings
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Saves the current game settings to PlayerPrefs so next lobby
    /// creation automatically uses the same values.
    /// </summary>
    public void SaveSettingsToPrefs(float sb, float bb, float minBuy, float maxBuy,
        float sessionBalance, bool unlimited, float turnTimer, float timeBank) {
        PlayerPrefs.SetFloat(PREF_SMALL_BLIND, sb);
        PlayerPrefs.SetFloat(PREF_BIG_BLIND, bb);
        PlayerPrefs.SetFloat(PREF_MIN_BUY_IN, minBuy);
        PlayerPrefs.SetFloat(PREF_MAX_BUY_IN, maxBuy);
        PlayerPrefs.SetFloat(PREF_SESSION_BALANCE, sessionBalance);
        PlayerPrefs.SetInt(PREF_UNLIMITED_BALANCE, unlimited ? 1 : 0);
        PlayerPrefs.SetFloat(PREF_TURN_TIMER, turnTimer);
        PlayerPrefs.SetFloat(PREF_TIME_BANK, timeBank);
        PlayerPrefs.Save();
        Debug.Log("[LobbyManager] Table settings saved to PlayerPrefs.");
    }

    /// <summary>
    /// Loads previously saved table settings from PlayerPrefs.
    /// Returns the defaults if nothing was saved before.
    /// </summary>
    public (float sb, float bb, float minBuy, float maxBuy,
            float sessionBalance, bool unlimited, float turnTimer, float timeBank) LoadSettingsFromPrefs() {
        return (
            PlayerPrefs.GetFloat(PREF_SMALL_BLIND, DEFAULT_SMALL_BLIND),
            PlayerPrefs.GetFloat(PREF_BIG_BLIND, DEFAULT_BIG_BLIND),
            PlayerPrefs.GetFloat(PREF_MIN_BUY_IN, DEFAULT_MIN_BUY_IN),
            PlayerPrefs.GetFloat(PREF_MAX_BUY_IN, DEFAULT_MAX_BUY_IN),
            PlayerPrefs.GetFloat(PREF_SESSION_BALANCE, DEFAULT_SESSION_AVAILABLE_BALANCE),
            PlayerPrefs.GetInt(PREF_UNLIMITED_BALANCE, DEFAULT_UNLIMITED_AVAILABLE_BALANCE ? 1 : 0) == 1,
            PlayerPrefs.GetFloat(PREF_TURN_TIMER, DEFAULT_TURN_TIMER),
            PlayerPrefs.GetFloat(PREF_TIME_BANK, DEFAULT_TIME_BANK)
        );
    }

    /// <summary>
    /// Returns the Table Session ID from the lobby data.
    /// Returns empty string if not set (e.g., migration lobby that hasn't been updated yet).
    /// </summary>
    public string GetTableSessionId() {
        if (joinedLobby != null && joinedLobby.Data != null &&
            joinedLobby.Data.TryGetValue(KEY_TABLE_SESSION_ID, out var dataObj)) {
            return dataObj.Value ?? "";
        }
        return "";
    }

    /// <summary>
    /// Returns the lobby name. Used by HandRecorder for table metadata.
    /// </summary>
    public string GetLobbyName() {
        return joinedLobby?.Name ?? "Unknown Table";
    }

    public async void StartGame() {
        try {
            Debug.Log("StartGame");

            Lobby lobby = await LobbyService.Instance.UpdateLobbyAsync(joinedLobby.Id, new UpdateLobbyOptions {
                Data = new Dictionary<string, DataObject> {
                    { KEY_START_GAME, new DataObject(DataObject.VisibilityOptions.Public, "1") },
                    { KEY_START_TIME, new DataObject(DataObject.VisibilityOptions.Public, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()) }
                }
            });

            joinedLobby = lobby;
            SyncStartedFromLobbyData(joinedLobby);

            IsHost = true;
            alreadyStartedGame = true;

            if (LoadingUI.Instance != null) LoadingUI.Instance.StartLoading();

            if (!LoadGameScene()) {
                Debug.LogError("LobbyManager: Failed to load game scene. Make sure the scene is added to Build Settings!");
                return;
            }

            hasLoadedGameScene = true;
            OnLobbyStartGame?.Invoke(this, new LobbyEventArgs { lobby = joinedLobby });
        } catch (LobbyServiceException e) {
            Debug.Log(e);
        }
    }

    /// <summary>
    /// Clears relay-related lobby data and local flags so a fresh relay can be created next time.
    /// </summary>
    public async void ClearRelayData() {
        alreadyStartedGame = false;
        hasLoadedGameScene = false;
        lobbyStartTimeMs = 0;
        lobbyStartAgeSeconds = float.MaxValue;
        allowAutoJoinOnStart = true;
        RelayJoinCode = string.Empty;

        if (IsLobbyHost() && joinedLobby != null) {
            try {
                await LobbyService.Instance.UpdateLobbyAsync(joinedLobby.Id, new UpdateLobbyOptions {
                    Data = new Dictionary<string, DataObject> {
                        { KEY_RELAY_JOIN_CODE, new DataObject(DataObject.VisibilityOptions.Member, "") },
                        { KEY_START_GAME, new DataObject(DataObject.VisibilityOptions.Public, "0") },
                        { KEY_START_TIME, new DataObject(DataObject.VisibilityOptions.Public, "0") }
                    }
                });
                Debug.Log("LobbyManager: Cleared relay data in lobby.");
            } catch (LobbyServiceException e) {
                Debug.LogWarning($"LobbyManager: Failed to clear relay data (lobby may be deleted): {e.Message}");
            }
        }
    }

    [ContextMenu("Force Start/Join Game (Debug)")]
    private void ContextMenuForceStartGame() {
        LateJoinGame();
    }

    /// <summary>
    /// Debug utility: host starts, client joins if relay code exists.
    /// </summary>
    public void LateJoinGame() {
        if (joinedLobby == null) {
            Debug.LogWarning("Late Join Game ignored: no joined lobby.");
            return;
        }
        // Shouldn't be the case in general
        if (IsLobbyHost()) {
            StartGame();
            return;
        }

        // Client path: try to join using existing relay code
        if (joinedLobby.Data != null && joinedLobby.Data.ContainsKey(KEY_RELAY_JOIN_CODE)) {
            string relayCode = joinedLobby.Data[KEY_RELAY_JOIN_CODE].Value;
            if (!string.IsNullOrEmpty(relayCode)) {
                Debug.Log("Late Join Game: joining relay as client.");
                JoinGame(relayCode);
            } else {
                Debug.LogWarning("Late Join Game ignored: relay code is empty.");
            }
        } else {
            Debug.LogWarning("Late Join Game ignored: relay code not found in lobby data.");
        }
    }

    private void JoinGame(string relayJoinCode) {
        Debug.Log("JoinGame " + relayJoinCode);
        if (string.IsNullOrEmpty(relayJoinCode)) {
            Debug.Log("Invalid Relay code, wait");
            return;
        }

        IsHost = false;
        RelayJoinCode = relayJoinCode;
        alreadyStartedGame = true;

        if (LoadingUI.Instance != null) LoadingUI.Instance.StartLoading();

        if (!LoadGameScene()) {
            Debug.LogError("LobbyManager: Failed to load game scene. Make sure the scene is added to Build Settings!");
            return;
        }

        hasLoadedGameScene = true;
        OnLobbyStartGame?.Invoke(this, new LobbyEventArgs { lobby = joinedLobby });
    }

    /// <summary>
    /// Safely loads the game scene. Validates that the scene exists in build settings,
    /// then waits for any pending asset unload (from <see cref="ExitHandler"/>) to finish
    /// before starting the actual scene load. This prevents stale prefab instance-ID
    /// mappings from causing null serialized references on the next game session.
    /// </summary>
    /// <returns>True if scene load was initiated, false on validation failure.</returns>
    private bool LoadGameScene() {
        // Check if scene exists in build settings
        int sceneCount = SceneManager.sceneCountInBuildSettings;
        
        if (GAME_SCENE_BUILD_INDEX < 0) {
            Debug.LogError($"LobbyManager: Invalid game scene build index {GAME_SCENE_BUILD_INDEX}!");
            return false;
        }
        
        if (sceneCount > 0 && GAME_SCENE_BUILD_INDEX >= sceneCount) {
            Debug.LogError($"LobbyManager: Game scene at build index {GAME_SCENE_BUILD_INDEX} is not in Build Settings! " +
                          $"Scene count: {sceneCount}. Please add the scene via File -> Build Settings -> Add Open Scenes");
            return false;
        }

        // Defer actual scene loading to a coroutine that gates on asset unload completion.
        StartCoroutine(LoadGameSceneAfterUnload());
        return true;
    }

    /// <summary>
    /// Waits for any in-progress <see cref="ExitHandler.UnloadGameSceneAssets"/> to finish,
    /// then loads the game scene. The ExitHandler runs <c>Resources.UnloadUnusedAssets()</c>
    /// every time the lobby scene loads after a game session, which clears stale prefab
    /// instance-ID mappings. We gate here to guarantee that unload has finished before
    /// we load the game scene again — preventing the race that causes NullNamePlates.
    /// </summary>
    private System.Collections.IEnumerator LoadGameSceneAfterUnload() {
        // Wait for ExitHandler's post-scene-change unload to complete (if one is running).
        // On the very first game start, IsAssetUnloadComplete is already true, so this is a no-op.
        if (ExitHandler.Instance != null && !ExitHandler.Instance.IsAssetUnloadComplete) {
            Debug.Log("LobbyManager: Waiting for asset unload to complete before loading game scene...");
            while (!ExitHandler.Instance.IsAssetUnloadComplete) {
                yield return null;
            }
            Debug.Log("LobbyManager: Asset unload complete. Proceeding with game scene load.");
        }

        // Load the scene
        bool needsRetry = false;
        try {
            var asyncOp = SceneManager.LoadSceneAsync(GAME_SCENE_BUILD_INDEX);
            if (asyncOp == null) {
                Debug.LogError($"LobbyManager: Failed to initiate scene load for index {GAME_SCENE_BUILD_INDEX}. Retrying...");
                needsRetry = true;
            } else {
                Debug.Log($"LobbyManager: Successfully started loading game scene (index: {GAME_SCENE_BUILD_INDEX})");
            }
        } catch (System.Exception e) {
            Debug.LogError($"LobbyManager: Exception while loading game scene: {e.Message}");
            Debug.LogException(e);
            needsRetry = true;
        }

        if (needsRetry) {
            yield return null;
            SceneManager.LoadScene(GAME_SCENE_BUILD_INDEX);
        }
    }

    public async void SetRelayJoinCode(string relayJoinCode) {
        try {
            Debug.Log("SetRelayJoinCode " + relayJoinCode);

            Lobby lobby = await LobbyService.Instance.UpdateLobbyAsync(joinedLobby.Id, new UpdateLobbyOptions {
                Data = new Dictionary<string, DataObject> {
                    { KEY_RELAY_JOIN_CODE, new DataObject(DataObject.VisibilityOptions.Member, relayJoinCode) }
                }
            });

            joinedLobby = lobby;
            SyncStartedFromLobbyData(joinedLobby);
        } catch (LobbyServiceException e) {
            Debug.Log(e);
        }
    }

    private bool SyncStartedFromLobbyData(Lobby lobby) {
        bool previous = alreadyStartedGame;

        lobbyStartTimeMs = GetStartTimeMs(lobby);
        if (lobbyStartTimeMs > 0) {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lobbyStartAgeSeconds = (nowMs - lobbyStartTimeMs) / 1000f;
            if (lobbyStartAgeSeconds < 0) lobbyStartAgeSeconds = 0;
        } else {
            lobbyStartAgeSeconds = float.MaxValue;
        }

        alreadyStartedGame = LobbyHasStarted(lobby);

        bool transitioned = (!previous && alreadyStartedGame);
        if (transitioned && allowAutoJoinOnStart) {
            hasSeenGameStartFlag = true;
        }
        return transitioned;
    }

    private long GetStartTimeMs(Lobby lobby) {
        if (lobby != null && lobby.Data != null && lobby.Data.TryGetValue(KEY_START_TIME, out var startObj)) {
            if (long.TryParse(startObj.Value, out long ms)) {
                return ms;
            }
        }
        return 0;
    }

    private bool LobbyHasStarted(Lobby lobby) {
        if (lobby != null && lobby.Data != null && lobby.Data.TryGetValue(KEY_START_GAME, out var startObj)) {
            return startObj.Value == "1";
        }
        return false;
    }

    // ── Host migration helpers ───────────────────────────────────

    /// <summary>
    /// Sets the internal joinedLobby reference during host migration
    /// so heartbeat / polling work on the newly created migration lobby.
    /// </summary>
    public void SetJoinedLobbyForMigration(Lobby lobby, bool asHost = false) {
        joinedLobby = lobby;
        lobbyPollTimer = LOBBY_POLL_INTERVAL;
        alreadyStartedGame = false;
        hasLoadedGameScene = false;
        allowAutoJoinOnStart = true;
        IsHost = asHost;

        SyncStartedFromLobbyData(joinedLobby);

        Debug.Log($"LobbyManager: Migration lobby set (id={lobby.Id}, code={lobby.LobbyCode}, asHost={asHost})");
    }

}
