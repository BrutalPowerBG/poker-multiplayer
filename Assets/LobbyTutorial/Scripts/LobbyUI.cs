using System.Collections;
using System.Collections.Generic;
using TMPro;
using Unity.Services.Authentication;
using Unity.Services.Lobbies.Models;
using UnityEngine;
using UnityEngine.UI;

public class LobbyUI : MonoBehaviour {


    public static LobbyUI Instance { get; private set; }


    [SerializeField] private Transform playerSingleTemplate;
    [SerializeField] private Transform container;
    [SerializeField] private TextMeshProUGUI lobbyNameText;
    [SerializeField] private TextMeshProUGUI playerCountText;
    [SerializeField] private TextMeshProUGUI gameModeText;
    [SerializeField] private TextMeshProUGUI lobbyCodeText;
    [SerializeField] private Button lobbyCodeButton;
    [SerializeField] private Button leaveLobbyButton;
    [SerializeField] private Button startGameButton;
    [SerializeField] private Button lateJoinButton;

    [Header("Game Settings Containers")]
    [SerializeField] private GameObject hostGameSettings;
    [SerializeField] private GameObject clientGameSettings;

    [Header("Game Settings - Host Input Fields")]
    [SerializeField] private TMP_InputField smallBlindInput;
    [SerializeField] private TMP_InputField bigBlindInput;
    [SerializeField] private TMP_InputField minBuyInInput;
    [SerializeField] private TMP_InputField maxBuyInInput;
    [SerializeField] private TMP_InputField sessionAvailableBalanceInput;
    [SerializeField] private Toggle unlimitedAvailableBalanceToggle;
    [SerializeField] private TMP_InputField turnTimerInput;
    [SerializeField] private TMP_InputField timeBankInput;
    [SerializeField] private Button applySettingsButton;

    [Header("Game Settings - Client Display")]
    [SerializeField] private TextMeshProUGUI smallBlindText;
    [SerializeField] private TextMeshProUGUI bigBlindText;
    [SerializeField] private TextMeshProUGUI minBuyInText;
    [SerializeField] private TextMeshProUGUI maxBuyInText;
    [SerializeField] private TextMeshProUGUI sessionAvailableBalanceText;
    [SerializeField] private TextMeshProUGUI unlimitedAvailableBalanceText;
    [SerializeField] private GameObject unlimitedAvailableBalanceImage;
    [SerializeField] private TextMeshProUGUI turnTimerText;
    [SerializeField] private TextMeshProUGUI timeBankText;
    [SerializeField] private Button readyButton;

    private string currentLobbyCode;
    private Coroutine copyFeedbackCoroutine;
    private bool hostSettingsInitialized = false;
    private bool isReady = false;


    private void Awake() {
        Instance = this;

        playerSingleTemplate.gameObject.SetActive(false);

        leaveLobbyButton.onClick.AddListener(() => {
            LobbyManager.Instance.LeaveLobby();
        });

        startGameButton.onClick.AddListener(() => {
            if (LobbyManager.Instance.IsLobbyHost())
                ApplyGameSettings();
            LobbyManager.Instance.StartGame();
        });
        lateJoinButton.onClick.AddListener(() => {
            LobbyManager.Instance.LateJoinGame();
        });

        if (lobbyCodeButton != null) {
            lobbyCodeButton.onClick.AddListener(CopyLobbyCodeToClipboard);
        }

        if (applySettingsButton != null) {
            applySettingsButton.onClick.AddListener(ApplyGameSettings);
        }

        if (unlimitedAvailableBalanceToggle != null) {
            unlimitedAvailableBalanceToggle.onValueChanged.AddListener((isOn) => {
                if (hostSettingsInitialized && LobbyManager.Instance.IsLobbyHost())
                    ApplyGameSettings();
            });
        }

        if (readyButton != null) {
            readyButton.onClick.AddListener(ToggleReady);
        }
    }

    private bool subscribedToEvents = false;

    private void Start() {
        SubscribeToEvents();
    }

    /// <summary>
    /// Called every time the panel is activated. Ensures event subscriptions
    /// are in place and populates the lobby data immediately (handles the case
    /// where Start() hasn't run yet when the panel is first activated by
    /// LobbyScreenManager in response to OnJoinedLobby).
    /// </summary>
    private void OnEnable() {
        SubscribeToEvents();

        // Populate immediately if lobby data already exists
        if (LobbyManager.Instance != null) {
            Lobby lobby = LobbyManager.Instance.GetJoinedLobby();
            if (lobby != null)
                UpdateLobby(lobby);
        }
    }

    private void SubscribeToEvents() {
        if (subscribedToEvents || LobbyManager.Instance == null) return;

        LobbyManager.Instance.OnJoinedLobby += UpdateLobby_Event;
        LobbyManager.Instance.OnJoinedLobbyUpdate += UpdateLobby_Event;
        LobbyManager.Instance.OnLobbyGameModeChanged += UpdateLobby_Event;
        LobbyManager.Instance.OnLeftLobby += LobbyManager_OnLeftLobby;
        LobbyManager.Instance.OnKickedFromLobby += LobbyManager_OnLeftLobby;
        LobbyManager.Instance.OnCanStartGameChanged += LobbyManager_OnCanStartGameChanged;
        LobbyManager.Instance.OnCanLateJoinGameChanged += LobbyManager_OnCanLateJoinGameChanged;
        subscribedToEvents = true;
    }

    private void OnDestroy() {
        // Unsubscribe to avoid callbacks after this UI is destroyed (scene unload)
        if (subscribedToEvents && LobbyManager.Instance != null) {
            LobbyManager.Instance.OnJoinedLobby -= UpdateLobby_Event;
            LobbyManager.Instance.OnJoinedLobbyUpdate -= UpdateLobby_Event;
            LobbyManager.Instance.OnLobbyGameModeChanged -= UpdateLobby_Event;
            LobbyManager.Instance.OnLeftLobby -= LobbyManager_OnLeftLobby;
            LobbyManager.Instance.OnKickedFromLobby -= LobbyManager_OnLeftLobby;
            LobbyManager.Instance.OnCanStartGameChanged -= LobbyManager_OnCanStartGameChanged;
            LobbyManager.Instance.OnCanLateJoinGameChanged -= LobbyManager_OnCanLateJoinGameChanged;
            subscribedToEvents = false;
        }
    }

    private void LobbyManager_OnCanStartGameChanged(object sender, bool e) {
        UpdateStartGameButton(e);
    }

    private void LobbyManager_OnCanLateJoinGameChanged(object sender, bool e) {
        UpdateLateJoinGameButton(e);
    }

    public void UpdateStartGameButton(bool canStartGame) {
        startGameButton.gameObject.SetActive(canStartGame);
    }

    public void UpdateLateJoinGameButton(bool canLateJoinGame) {
        lateJoinButton.gameObject.SetActive(canLateJoinGame);
    }

    private void LobbyManager_OnLeftLobby(object sender, System.EventArgs e) {
        hostSettingsInitialized = false;
        isReady = false;
        if (readyButton != null) {
            Color c = readyButton.image.color;
            c.a = 1f;
            readyButton.image.color = c;
        }
        ClearLobby();
        // Screen transition handled by LobbyScreenManager
    }

    private void UpdateLobby_Event(object sender, LobbyManager.LobbyEventArgs e) {
        UpdateLobby();
    }

    private void UpdateLobby() {
        UpdateLobby(LobbyManager.Instance.GetJoinedLobby());
    }

    private void UpdateLobby(Lobby lobby) {
        if (lobby == null || container == null) {
            return;
        }

        ClearLobby();

        foreach (Player player in lobby.Players) {
            Transform playerSingleTransform = Instantiate(playerSingleTemplate, container);
            playerSingleTransform.gameObject.SetActive(true);
            LobbyPlayerSingleUI lobbyPlayerSingleUI = playerSingleTransform.GetComponent<LobbyPlayerSingleUI>();

            lobbyPlayerSingleUI.SetKickPlayerButtonVisible(
                LobbyManager.Instance.IsLobbyHost() &&
                player.Id != AuthenticationService.Instance.PlayerId // Don't allow kick self
            );

            lobbyPlayerSingleUI.UpdatePlayer(player);
        }

        lobbyNameText.text = lobby.Name;
        playerCountText.text = lobby.Players.Count + "/" + lobby.MaxPlayers;
        gameModeText.text = lobby.Data[LobbyManager.KEY_GAME_MODE].Value;

        currentLobbyCode = lobby.LobbyCode;

        if (lobbyCodeText != null) {
            lobbyCodeText.text = !string.IsNullOrEmpty(currentLobbyCode)
                ? $"Code: {currentLobbyCode}"
                : "";
        }

        UpdateGameSettingsUI(lobby);

        // Panel visibility is handled by LobbyScreenManager via OnJoinedLobby.
        // No need to call Show() here — the panel is already active.
    }

    private void ClearLobby() {
        if (container == null) return;

        foreach (Transform child in container) {
            if (child == playerSingleTemplate) continue;
            Destroy(child.gameObject);
        }
    }

    private void UpdateGameSettingsUI(Lobby lobby) {
        bool isHost = LobbyManager.Instance.IsLobbyHost();

        // Read values from lobby data (fall back to defaults)
        string sb = GetLobbyDataValue(lobby, LobbyManager.KEY_SMALL_BLIND, LobbyManager.DEFAULT_SMALL_BLIND.ToString());
        string bb = GetLobbyDataValue(lobby, LobbyManager.KEY_BIG_BLIND, LobbyManager.DEFAULT_BIG_BLIND.ToString());
        string minBuy = GetLobbyDataValue(lobby, LobbyManager.KEY_MIN_BUY_IN, LobbyManager.DEFAULT_MIN_BUY_IN.ToString());
        string maxBuy = GetLobbyDataValue(lobby, LobbyManager.KEY_MAX_BUY_IN, LobbyManager.DEFAULT_MAX_BUY_IN.ToString());
        string sessionBalance = GetLobbyDataValue(
            lobby,
            LobbyManager.KEY_SESSION_AVAILABLE_BALANCE,
            LobbyManager.DEFAULT_SESSION_AVAILABLE_BALANCE.ToString());
        string unlimitedBalance = GetLobbyDataValue(
            lobby,
            LobbyManager.KEY_UNLIMITED_AVAILABLE_BALANCE,
            LobbyManager.DEFAULT_UNLIMITED_AVAILABLE_BALANCE ? "1" : "0");
        bool isUnlimited = unlimitedBalance == "1" || unlimitedBalance.ToLower() == "true";
        string turnTimer = GetLobbyDataValue(lobby, LobbyManager.KEY_TURN_TIMER, LobbyManager.DEFAULT_TURN_TIMER.ToString());
        string timeBank = GetLobbyDataValue(lobby, LobbyManager.KEY_TIME_BANK, LobbyManager.DEFAULT_TIME_BANK.ToString());

        // Toggle entire containers based on host/client
        if (hostGameSettings != null) hostGameSettings.SetActive(isHost);
        if (clientGameSettings != null) clientGameSettings.SetActive(!isHost);

        // Host: only populate input fields once so polling doesn't overwrite while typing
        if (isHost && !hostSettingsInitialized) {
            if (smallBlindInput != null) smallBlindInput.text = sb;
            if (bigBlindInput != null) bigBlindInput.text = bb;
            if (minBuyInInput != null) minBuyInInput.text = minBuy;
            if (maxBuyInInput != null) maxBuyInInput.text = maxBuy;
            if (sessionAvailableBalanceInput != null) sessionAvailableBalanceInput.text = sessionBalance;
            if (unlimitedAvailableBalanceToggle != null) unlimitedAvailableBalanceToggle.isOn = isUnlimited;
            if (turnTimerInput != null) turnTimerInput.text = turnTimer;
            if (timeBankInput != null) timeBankInput.text = timeBank;
            hostSettingsInitialized = true;
        }

        // Client: always update the value texts (labels are separate game objects)
        if (!isHost) {
            if (smallBlindText != null) smallBlindText.text = sb;
            if (bigBlindText != null) bigBlindText.text = bb;
            if (minBuyInText != null) minBuyInText.text = minBuy;
            if (maxBuyInText != null) maxBuyInText.text = maxBuy;
            if (sessionAvailableBalanceText != null) sessionAvailableBalanceText.text = sessionBalance;
            if (unlimitedAvailableBalanceText != null) unlimitedAvailableBalanceText.text = isUnlimited ? "Yes" : "No";
            if (unlimitedAvailableBalanceImage != null) unlimitedAvailableBalanceImage.SetActive(isUnlimited);
            if (turnTimerText != null) turnTimerText.text = turnTimer + "s";
            if (timeBankText != null) timeBankText.text = timeBank + "s";
        }
    }

    private string GetLobbyDataValue(Lobby lobby, string key, string defaultValue) {
        if (lobby.Data != null && lobby.Data.TryGetValue(key, out var dataObj)) {
            return dataObj.Value;
        }
        return defaultValue;
    }

    private void ApplyGameSettings() {
        if (!LobbyManager.Instance.IsLobbyHost()) return;

        if (!float.TryParse(smallBlindInput?.text, out float sb)) sb = LobbyManager.DEFAULT_SMALL_BLIND;
        if (!float.TryParse(bigBlindInput?.text, out float bb)) bb = LobbyManager.DEFAULT_BIG_BLIND;
        if (!float.TryParse(minBuyInInput?.text, out float minBuy)) minBuy = LobbyManager.DEFAULT_MIN_BUY_IN;
        if (!float.TryParse(maxBuyInInput?.text, out float maxBuy)) maxBuy = LobbyManager.DEFAULT_MAX_BUY_IN;
        if (!float.TryParse(sessionAvailableBalanceInput?.text, out float sessionBalance))
            sessionBalance = LobbyManager.DEFAULT_SESSION_AVAILABLE_BALANCE;
        bool unlimited = unlimitedAvailableBalanceToggle != null && unlimitedAvailableBalanceToggle.isOn;
        if (!float.TryParse(turnTimerInput?.text, out float turnTimer)) turnTimer = LobbyManager.DEFAULT_TURN_TIMER;
        if (!float.TryParse(timeBankInput?.text, out float timeBank)) timeBank = LobbyManager.DEFAULT_TIME_BANK;

        LobbyManager.Instance.UpdateGameSettings(sb, bb, minBuy, maxBuy, sessionBalance, unlimited, turnTimer, timeBank);
    }

    private void ToggleReady() {
        isReady = !isReady;
        if (readyButton != null) {
            Color c = readyButton.image.color;
            c.a = isReady ? 0.5f : 1f;
            readyButton.image.color = c;
        }
        LobbyManager.Instance.UpdatePlayerReady(isReady);
    }

    private void CopyLobbyCodeToClipboard() {
        if (string.IsNullOrEmpty(currentLobbyCode)) return;

        GUIUtility.systemCopyBuffer = currentLobbyCode;
        Debug.Log($"Lobby code copied to clipboard: {currentLobbyCode}");

        if (lobbyCodeText != null) {
            if (copyFeedbackCoroutine != null) {
                StopCoroutine(copyFeedbackCoroutine);
            }
            copyFeedbackCoroutine = StartCoroutine(ShowCopyFeedback());
        }
    }

    private IEnumerator ShowCopyFeedback() {
        string originalText = lobbyCodeText.text;
        lobbyCodeText.text = "Copied!";
        yield return new WaitForSeconds(1f);
        lobbyCodeText.text = originalText;
        copyFeedbackCoroutine = null;
    }

}