using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;


/// <summary>
/// Handles clean exits from game to lobby for both host and clients.
/// Also handles automatic lobby return when host disconnects.
///
/// When <see cref="enableHostMigration"/> is FALSE (default):
///   Host exit → delete lobby → all clients return to lobby.
///
/// When <see cref="enableHostMigration"/> is TRUE:
///   Host exit → capture game state → send to clients → promote next client →
///   clients reconnect through a new lobby → game resumes from snapshot.
/// </summary>
public class ExitHandler : MonoBehaviour {
    public static ExitHandler Instance { get; private set; }

    // For this to work, the Serializer class needs to take the correct data for the game (currently Poker)
    [Header("Host Migration")]
    [Tooltip("When true, leaving as host will migrate the game to the next client instead of ending the session.")]
    public bool enableHostMigration = true;

    /// <summary>
    /// Ensure an instance exists (callable from any scene).
    /// </summary>
    public static ExitHandler GetOrCreate() {
        if (Instance != null) return Instance;

        var go = new GameObject("ExitHandler");
        var mgr = go.AddComponent<ExitHandler>();
        DontDestroyOnLoad(go);
        return mgr;
    }

    /// <summary>
    /// Call this after NetworkManager is spawned to ensure callbacks are registered.
    /// </summary>
    public void EnsureCallbacksRegistered() {
        if (NetworkManager.Singleton == null) {
            Debug.LogWarning($"[{LogTag.Exit}] Cannot register callbacks — NetworkManager.Singleton=<none>", this);
            return;
        }

        // Unregister first to avoid duplicates
        NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnect;
        NetworkManager.Singleton.OnServerStopped -= OnServerStopped;
        
        // Re-register disconnection callbacks
        NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnect;
        NetworkManager.Singleton.OnServerStopped += OnServerStopped;
        
        Debug.Log($"[{LogTag.Exit}] Network disconnect callbacks registered", this);
    }

    private void Awake() {
        if (Instance != null && Instance != this) {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        
        // Subscribe to scene loaded event to reset state
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
        // Reset flag when we return to lobby (scene 0)
        if (scene.buildIndex == 0) {
            Debug.Log($"[{LogTag.Exit}] Lobby scene loaded — resetting state", this);
            isReturningToLobby = false;
            
            // Keep the NetworkManager alive (just shut down) so it can be reused next game.
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening) {
                Debug.Log($"[{LogTag.Exit}] NetworkManager still listening in lobby — shutting down", this);
                NetworkManager.Singleton.Shutdown();
            }
            
            // Force Unity to release cached prefab assets from the game scene.
            // Without this, Unity reuses stale internal instance-ID mappings from
            // the previous game scene load, causing ALL prefab-instance object references
            // (GameObjects, Images, TMP, etc.) to resolve to null on the next load
            // while primitive values (floats) survive.
            StartCoroutine(UnloadGameSceneAssets());
        }
        
        // Also reset the flag when the game scene loads (e.g. after host migration)
        // so future disconnect callbacks aren't blocked.
        if (scene.buildIndex == 1) {
            isReturningToLobby = false;
        }
    }
    
    /// <summary>
    /// True once the post-scene-change asset unload has finished.
    /// Checked by <see cref="LobbyManager.LoadGameScene"/> to prevent loading
    /// the game scene with stale prefab instance-ID mappings.
    /// </summary>
    public bool IsAssetUnloadComplete { get; private set; } = true;

    private System.Collections.IEnumerator UnloadGameSceneAssets() {
        IsAssetUnloadComplete = false;

        // Give one frame for any pending destroys to finalize
        yield return null;
        
        Debug.Log($"[{LogTag.Exit}] Unloading unused assets — clearing stale prefab cache", this);
        var op = Resources.UnloadUnusedAssets();
        yield return op;
        System.GC.Collect();
        IsAssetUnloadComplete = true;
        Debug.Log($"[{LogTag.Exit}] Asset unload complete", this);
    }

    private void OnDestroy() {
        // Unsubscribe from events when destroyed
        if (NetworkManager.Singleton != null) {
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnect;
            NetworkManager.Singleton.OnServerStopped -= OnServerStopped;
        }
        
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Application Quit
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// When the host quits the application (Alt+F4, close button, task-kill, etc.),
    /// immediately shut down the NetworkManager so clients detect the disconnect
    /// within milliseconds instead of waiting for the transport timeout (~5-10s).
    ///
    /// Note: host migration is NOT attempted on application quit because there
    /// isn't enough time for the async lobby/relay flow. The departing-host RPC
    /// may still arrive if the quit is graceful (close button), triggering
    /// migration on the client side.
    /// </summary>
    private void OnApplicationQuit() {
        if (NetworkManager.Singleton == null) return;

        if (NetworkManager.Singleton.IsServer) {
            Debug.Log($"[{LogTag.Exit}] Host application quitting — shutting down network immediately", this);

            // If migration is enabled, try to fire the migration RPC before shutting down.
            // The RPC is small and should be flushed before the transport closes.
            if (enableHostMigration && GameManager.Instance != null) {
                    Debug.Log($"[{LogTag.Exit}] Attempting last-second migration RPC before quit", this);
                try {
                    HostMigrationManager.GetOrCreate().InitiateMigration(GameManager.Instance);
                    // Give Netcode a moment to flush the RPC buffer
                    NetworkManager.Singleton.Shutdown();
                } catch (System.Exception e) {
                    Debug.LogWarning($"[{LogTag.Exit}] Migration RPC on quit failed — error={e.Message}", this);
                    NetworkManager.Singleton.Shutdown();
                }
            } else {
                NetworkManager.Singleton.Shutdown();
            }

            // Fire-and-forget lobby deletion (best-effort, may not complete before exit)
            if (LobbyManager.Instance != null && LobbyManager.Instance.GetJoinedLobby() != null) {
                try {
                    string lobbyId = LobbyManager.Instance.GetJoinedLobby().Id;
                    _ = Unity.Services.Lobbies.LobbyService.Instance.DeleteLobbyAsync(lobbyId);
                    Debug.Log($"[{LogTag.Exit}] Lobby delete fired — mode=best-effort", this);
                } catch (System.Exception e) {
                    Debug.LogWarning($"[{LogTag.Exit}] Lobby delete on quit failed — error={e.Message}", this);
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Network disconnect callbacks
    // ═══════════════════════════════════════════════════════════════

    private bool isReturningToLobby = false;

    /// <summary>
    /// Called when a client disconnects. If we're a client and the server (host) disconnects, return to lobby.
    /// </summary>
    private void OnClientDisconnect(ulong clientId) {
        if (isReturningToLobby) return;
        
        Debug.Log($"[{LogTag.Exit}] Client disconnected — clientId={clientId}", this);
        
        if (NetworkManager.Singleton == null) {
            Debug.LogWarning($"[{LogTag.Exit}] NetworkManager=<none> during OnClientDisconnect — clientId={clientId}", this);
            return;
        }

        Debug.Log($"[{LogTag.Exit}] Disconnect context — isServer={NetworkManager.Singleton.IsServer}, isClient={NetworkManager.Singleton.IsClient}, serverClientId={NetworkManager.ServerClientId}, localClientId={NetworkManager.Singleton.LocalClientId}", this);
        
        // If we're a client (not server)
        if (!NetworkManager.Singleton.IsServer && NetworkManager.Singleton.IsClient) {
            // If WE got disconnected (our own clientId), it means the host shut down
            if (clientId == NetworkManager.Singleton.LocalClientId) {
                Debug.Log($"[{LogTag.Exit}] Local client disconnected from host — clientId={clientId}, action=ReturnToLobby", this);
                HandleHostLost();
            }
            // OR if the host/server disconnected
            else if (clientId == NetworkManager.ServerClientId) {
                Debug.Log($"[{LogTag.Exit}] Host disconnected — hostClientId={clientId}, action=ReturnToLobby", this);
                HandleHostLost();
            } else {
                Debug.Log($"[{LogTag.Exit}] Peer client disconnected — peerId={clientId}, localId={NetworkManager.Singleton.LocalClientId}, hostId={NetworkManager.ServerClientId}", this);
            }
        }
    }

    /// <summary>
    /// Called when the server stops (only called on clients when connection to server is lost).
    /// </summary>
    private void OnServerStopped(bool wasHost) {
        if (isReturningToLobby) return;
        
        Debug.Log($"[{LogTag.Exit}] Server stopped — wasHost={wasHost}", this);
        
        // If we were a client (not the host), the server has stopped
        if (!wasHost) {
            Debug.Log($"[{LogTag.Exit}] Server stopped — client handling host loss", this);
            HandleHostLost();
        }
    }

    /// <summary>
    /// Decides whether to migrate or return to lobby when the host disconnects.
    /// </summary>
    private void HandleHostLost() {
        if (isReturningToLobby) return;

        // Check if we have pending migration data
        if (enableHostMigration &&
            HostMigrationManager.Instance != null &&
            HostMigrationManager.Instance.IsMigrationPending) {
            Debug.Log($"[{LogTag.Exit}] Host migration data available — executing migration", this);
            isReturningToLobby = true; // Prevent duplicate calls
            HostMigrationManager.Instance.ExecuteMigration();
            return;
        }

        // No migration — fall back to the original behaviour
        ReturnToLobbyAfterHostDisconnect();
    }

    /// <summary>
    /// Cleanly exit to lobby when host has disconnected unexpectedly.
    /// Client-side only - doesn't try to modify the lobby (no authority).
    /// Original behaviour (enableHostMigration == false).
    /// </summary>
    private void ReturnToLobbyAfterHostDisconnect() {
        if (isReturningToLobby) {
            Debug.Log($"[{LogTag.Exit}] Already returning to lobby — ignoring duplicate call", this);
            return;
        }
        
        isReturningToLobby = true;
        Debug.Log($"[{LogTag.Exit}] Host disconnected — returning to lobby", this);
        
        // Reset StartGameManager for next game
        if (StartGameManager.Instance != null) {
            StartGameManager.Instance.ResetForNewGame();
        }
        
        // DON'T try to clear relay data - client has no authority and lobby may be deleted
        // The lobby was deleted by the host, so just clean up locally
        
        // Shutdown NetworkManager but keep it alive for reuse next game.
        // Destroying it causes Netcode to break prefab references on the client's next session.
        if (NetworkManager.Singleton != null) {
            Debug.Log($"[{LogTag.Exit}] Shutting down NetworkManager — keepAlive=true", this);
            NetworkManager.Singleton.Shutdown();
        }
        
        // Return to lobby scene - LobbyManager will detect we're no longer in a lobby
        Debug.Log($"[{LogTag.Exit}] Loading lobby scene — sceneIndex=0", this);
        SceneManager.LoadScene(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Explicit exit-to-lobby (Menu button)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Entry point from Menu.ExitToLobby — handles clean exit for both host and clients.
    /// </summary>
    public void HandleExitToLobby() {
        Debug.Log($"[{LogTag.Exit}] Exit to lobby requested", this);
        
        if (NetworkManager.Singleton == null) {
            Debug.Log($"[{LogTag.Exit}] No NetworkManager — loading lobby scene directly", this);
            SceneManager.LoadScene(0);
            return;
        }

        if (NetworkManager.Singleton.IsServer) {
            Debug.Log($"[{LogTag.Exit}] Current node is host — handling host exit", this);
            HandleHostExit();
        } else if (NetworkManager.Singleton.IsClient) {
            Debug.Log($"[{LogTag.Exit}] Current node is client — handling client exit", this);
            HandleClientExit();
        } else {
            Debug.Log($"[{LogTag.Exit}] Neither server nor client — loading lobby scene directly", this);
            SceneManager.LoadScene(0);
        }
    }

    /// <summary>
    /// Handles host exiting.
    /// If migration is enabled: captures state → sends to clients → shuts down.
    /// If migration is disabled: deletes lobby → disconnects everyone → lobby.
    /// </summary>
    private async void HandleHostExit() {
        // Reset StartGameManager for next game
        if (StartGameManager.Instance != null) {
            StartGameManager.Instance.ResetForNewGame();
        }

        // Persist hand history / session summary while the scene is alive.
        var recorder = FindObjectOfType<HandRecorder>();
        if (recorder != null) {
            try { await recorder.FinalizeAndSaveBeforeExit(); }
            catch (System.Exception e) {
                Debug.LogWarning($"[{LogTag.Exit}] Pre-exit save failed — error={e.Message}", this);
            }
        }

        // ── Migration path ───────────────────────────────────────
        // Migration is attempted whenever other clients are connected (seated OR spectators).
        // This ensures that a host who is spectating (sat out) still transfers the
        // session to another client instead of ejecting everyone.
        if (enableHostMigration &&
            GameManager.Instance != null &&
            GameManager.Instance.HasOtherConnectedClients()) {

            // Ensure the migration manager exists
            HostMigrationManager.GetOrCreate();

            Debug.Log($"[{LogTag.Exit}] Host exiting with migration — sending state to clients", this);

            HostMigrationManager.Instance.InitiateMigration(GameManager.Instance);

            // Give a brief window for the RPC to be flushed
            await System.Threading.Tasks.Task.Delay(200);

            // Shut down networking (clients will detect disconnect and start migration)
            if (NetworkManager.Singleton != null) {
                NetworkManager.Singleton.Shutdown();
            }

            // The departing host also receives its own MigrateHostClientRpc, which sets
            // IsMigrationPending. Clear it so the host doesn't accidentally enter the
            // migration path when they later rejoin a game.
            if (HostMigrationManager.Instance != null) {
                HostMigrationManager.Instance.ClearMigration();
                Debug.Log($"[{LogTag.Exit}] Cleared migration state on departing host", this);
            }

            // Delete the old lobby (best-effort)
            if (LobbyManager.Instance != null && LobbyManager.Instance.GetJoinedLobby() != null) {
                try {
                    await Unity.Services.Lobbies.LobbyService.Instance.DeleteLobbyAsync(
                        LobbyManager.Instance.GetJoinedLobby().Id);
                    Debug.Log($"[{LogTag.Exit}] Old lobby deleted", this);
                } catch (System.Exception e) {
                    Debug.LogWarning($"[{LogTag.Exit}] Old lobby delete failed — error={e.Message}", this);
                }
            }

            SceneManager.LoadScene(0);
            return;
        }

        // ── Original path (no migration) ─────────────────────────
        Debug.Log($"[{LogTag.Exit}] Host exiting to lobby — deleting lobby for all players", this);

        if (LobbyManager.Instance != null && LobbyManager.Instance.GetJoinedLobby() != null) {
            // Clear relay data before deleting (so lobby still exists)
            LobbyManager.Instance.ClearRelayData();
            await System.Threading.Tasks.Task.Delay(100); // Brief delay to let clear complete
            
            try {
                await Unity.Services.Lobbies.LobbyService.Instance.DeleteLobbyAsync(
                    LobbyManager.Instance.GetJoinedLobby().Id);
                Debug.Log($"[{LogTag.Exit}] Lobby deleted — clients will be disconnected", this);
            } catch (System.Exception e) {
                Debug.LogWarning($"[{LogTag.Exit}] Lobby delete failed — error={e.Message}", this);
            }
            
            // Don't call LeaveLobby after deleting - the lobby no longer exists
        }

        // Shutdown NetworkManager but keep it alive for reuse.
        if (NetworkManager.Singleton != null) {
            NetworkManager.Singleton.Shutdown();
        }

        // Return to lobby scene
        SceneManager.LoadScene(0);
    }

    /// <summary>
    /// Handles client exiting - leaves lobby and shuts down network connection.
    /// Always uses the original path (migration is host-initiated only).
    /// </summary>
    private async void HandleClientExit() {
        Debug.Log($"[{LogTag.Exit}] Client exiting to lobby", this);

        // Reset StartGameManager for next game
        if (StartGameManager.Instance != null) {
            StartGameManager.Instance.ResetForNewGame();
        }

        // Persist hand history / session summary BEFORE tearing down
        // networking — the Firebase write needs to complete while we're
        // still connected and the scene is alive.
        var recorder = FindObjectOfType<HandRecorder>();
        if (recorder != null) {
            try { await recorder.FinalizeAndSaveBeforeExit(); }
            catch (System.Exception e) {
                Debug.LogWarning($"[{LogTag.Exit}] Pre-exit save failed — error={e.Message}", this);
            }
        }

        // Just leave the lobby - don't try to clear relay data (client has no authority)
        if (LobbyManager.Instance != null) {
            LobbyManager.Instance.LeaveLobby();
        }

        // Shutdown NetworkManager but keep it alive for reuse.
        if (NetworkManager.Singleton != null) {
            NetworkManager.Singleton.Shutdown();
        }

        // Return to lobby scene
        SceneManager.LoadScene(0);
    }
}
