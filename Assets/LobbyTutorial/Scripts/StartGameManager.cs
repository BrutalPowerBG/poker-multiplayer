using Unity.Netcode.Transports.UTP;
using Unity.Netcode;
using Unity.Networking.Transport.Relay;
using Unity.Services.Relay.Models;
using Unity.Services.Relay;
using UnityEngine;
using UnityEngine.SceneManagement;


public class StartGameManager : MonoBehaviour {
    public static StartGameManager Instance { get; private set; }
    private bool isProcessingGameStart = false;
    
    private void Awake() {
        if (Instance != null && Instance != this) {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void Start() {
        if (LobbyManager.Instance != null) {
            LobbyManager.Instance.OnLobbyStartGame += LobbyManager_OnLobbyStartGame;
        }
    }

    private void OnDestroy() {
        // Clean up event subscription
        if (LobbyManager.Instance != null) {
            LobbyManager.Instance.OnLobbyStartGame -= LobbyManager_OnLobbyStartGame;
        }
    }

    private void LobbyManager_OnLobbyStartGame(object sender, LobbyManager.LobbyEventArgs e) {
        // Prevent multiple simultaneous game start attempts
        if (isProcessingGameStart) {
            Debug.Log("StartGameManager: Already processing game start, ignoring duplicate signal");
            return;
        }

        // Start Game!
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening) {
            // Already running, ignore repeat start signals
            Debug.Log("StartGameManager: NetworkManager already listening, ignoring start signal");
            return;
        }

        isProcessingGameStart = true;

        // Scene is loading - wait for NetworkManager to be available
        if (LobbyManager.IsHost) {
            StartCoroutine(WaitForNetworkManagerAndCreateRelay());
        } else {
            // Client: Wait for relay code to be set via polling, don't try to join here
            // The relay code will be retrieved in LobbyManager.HandleLobbyPolling()
            // This prevents trying to join with an empty relay code
            if (!string.IsNullOrEmpty(LobbyManager.RelayJoinCode)) {
                StartCoroutine(WaitForNetworkManagerAndJoinRelay(LobbyManager.RelayJoinCode));
            } else {
                Debug.Log("StartGameManager: Host hasn't provided relay join code");
                isProcessingGameStart = false;
            }
        }
    }

    /// <summary>
    /// Wait for the game scene to be fully loaded before creating the relay.
    /// The NetworkManager may already exist (persisted from a previous game),
    /// so we must also ensure the game scene's objects are ready.
    /// </summary>
    private System.Collections.IEnumerator WaitForNetworkManagerAndCreateRelay() {
        Debug.Log("StartGameManager: Waiting for game scene and NetworkManager...");
        float timeout = 10f;
        float elapsed = 0f;
        
        // Wait for the game scene to be the active scene AND NetworkManager to exist
        while (elapsed < timeout) {
            bool sceneReady = SceneManager.GetActiveScene().buildIndex == 1;
            bool nmReady = NetworkManager.Singleton != null;
            if (sceneReady && nmReady) break;
            yield return null;
            elapsed += Time.deltaTime;
        }

        if (NetworkManager.Singleton == null) {
            Debug.LogError("StartGameManager: Timeout waiting for NetworkManager! Make sure GameScene has a NetworkManager.");
            isProcessingGameStart = false;
            yield break;
        }
        
        if (SceneManager.GetActiveScene().buildIndex != 1) {
            Debug.LogError("StartGameManager: Timeout waiting for game scene to load!");
            isProcessingGameStart = false;
            yield break;
        }

        Debug.Log("StartGameManager: Game scene loaded and NetworkManager found, creating relay...");
        CreateRelay();
    }

    /// <summary>
    /// Wait for the game scene to be fully loaded before joining the relay.
    /// </summary>
    private System.Collections.IEnumerator WaitForNetworkManagerAndJoinRelay(string joinCode) {
        Debug.Log("StartGameManager: Waiting for game scene and NetworkManager...");
        float timeout = 10f;
        float elapsed = 0f;
        
        // Wait for the game scene to be the active scene AND NetworkManager to exist
        while (elapsed < timeout) {
            bool sceneReady = SceneManager.GetActiveScene().buildIndex == 1;
            bool nmReady = NetworkManager.Singleton != null;
            if (sceneReady && nmReady) break;
            yield return null;
            elapsed += Time.deltaTime;
        }

        if (NetworkManager.Singleton == null) {
            Debug.LogError("StartGameManager: Timeout waiting for NetworkManager! Make sure GameScene has a NetworkManager.");
            isProcessingGameStart = false;
            yield break;
        }
        
        if (SceneManager.GetActiveScene().buildIndex != 1) {
            Debug.LogError("StartGameManager: Timeout waiting for game scene to load!");
            isProcessingGameStart = false;
            yield break;
        }

        Debug.Log("StartGameManager: Game scene loaded and NetworkManager found, joining relay...");
        JoinRelay(joinCode);
    }

    /// <summary>
    /// Call this when returning to lobby to reset state for next game
    /// </summary>
    public void ResetForNewGame() {
        Debug.Log("StartGameManager: Resetting for new game");
        isProcessingGameStart = false;
        StopAllCoroutines();
    }

    public void StartHost() {
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening) {
            return;
        }
        NetworkManager.Singleton.StartHost();
        
        // Ensure ExitHandler callbacks are registered after starting
        var exitHandler = ExitHandler.GetOrCreate();
        exitHandler.EnsureCallbacksRegistered();
    }

    public void StartClient() {
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening) {
            return;
        }
        NetworkManager.Singleton.StartClient();
        
        // Ensure ExitHandler callbacks are registered after starting
        var exitHandler = ExitHandler.GetOrCreate();
        exitHandler.EnsureCallbacksRegistered();
    }

    private async void CreateRelay() {
        try {
            if (NetworkManager.Singleton == null) {
                Debug.LogError("CreateRelay: NetworkManager.Singleton is null! Make sure there is a NetworkManager in the game scene.");
                return;
            }

            var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
            if (transport == null) {
                Debug.LogError("CreateRelay: NetworkManager does not have a UnityTransport component!");
                return;
            }

            Allocation allocation = await RelayService.Instance.CreateAllocationAsync(3);

            string joinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
            Debug.Log("Allocated Relay JoinCode: " + joinCode);

            RelayServerData relayServerData = AllocationUtils.ToRelayServerData(allocation, "dtls");

            transport.SetRelayServerData(relayServerData);

            StartHost();

            LobbyManager.Instance.SetRelayJoinCode(joinCode);
        } catch (RelayServiceException e) {
            Debug.LogError($"CreateRelay: RelayServiceException - {e}");
        } catch (System.Exception e) {
            Debug.LogError($"CreateRelay: Exception - {e}");
        }
    }

    private async void JoinRelay(string joinCode) {
        try {
            if (NetworkManager.Singleton == null) {
                Debug.LogError("JoinRelay: NetworkManager.Singleton is null! Make sure there is a NetworkManager in the game scene.");
                return;
            }

            var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
            if (transport == null) {
                Debug.LogError("JoinRelay: NetworkManager does not have a UnityTransport component!");
                return;
            }

            JoinAllocation joinAllocation = await RelayService.Instance.JoinAllocationAsync(joinCode);

            RelayServerData relayServerData = AllocationUtils.ToRelayServerData(joinAllocation, "dtls");

            transport.SetRelayServerData(relayServerData);

            StartClient();
        } catch (RelayServiceException e) {
            Debug.LogError($"JoinRelay: RelayServiceException - {e}");
        } catch (System.Exception e) {
            Debug.LogError($"JoinRelay: Exception - {e}");
        }
    }
}