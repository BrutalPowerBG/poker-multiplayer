using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class LobbyCreateUI : MonoBehaviour {

    public static LobbyCreateUI Instance { get; private set; }

    [Header("Input Fields")]
    [SerializeField] private TMP_InputField lobbyNameInput;
    [SerializeField] private TMP_InputField maxPlayersInput;

    [Header("Toggle Buttons")]
    [SerializeField] private Button publicPrivateButton;
    [SerializeField] private Button gameModeButton;
    [SerializeField] private TextMeshProUGUI publicPrivateText;
    [SerializeField] private TextMeshProUGUI gameModeText;

    [Header("Actions")]
    [SerializeField] private Button createButton;
    [SerializeField] private Button closeButton;

    private bool isPrivate;
    private LobbyManager.GameMode gameMode;

    private void Awake() {
        Instance = this;

        createButton.onClick.AddListener(OnCreateClicked);

        publicPrivateButton.onClick.AddListener(() => {
            isPrivate = !isPrivate;
            UpdateToggleTexts();
        });

        gameModeButton.onClick.AddListener(() => {
            gameMode = gameMode == LobbyManager.GameMode.TexasHoldem
                ? LobbyManager.GameMode.OmahaHoldem
                : LobbyManager.GameMode.TexasHoldem;
            UpdateToggleTexts();
        });

        if (closeButton != null) {
            closeButton.onClick.AddListener(GoToLobbyList);
        }

        // Initial hide is handled by LobbyScreenManager.Awake → ShowScreen(Auth)
    }

    private void OnCreateClicked() {
        string lobbyName = lobbyNameInput != null ? lobbyNameInput.text.Trim() : "MyLobby";
        if (string.IsNullOrEmpty(lobbyName)) lobbyName = "MyLobby";

        int maxPlayers = 9;
        if (maxPlayersInput != null && int.TryParse(maxPlayersInput.text, out int parsed)) {
            maxPlayers = Mathf.Clamp(parsed, 2, 9);
        }

        LobbyManager.Instance.CreateLobby(lobbyName, maxPlayers, isPrivate, gameMode);
        GoToLobbyList();
    }

    private void UpdateToggleTexts() {
        if (publicPrivateText != null) publicPrivateText.text = isPrivate ? "Private" : "Public";
        if (gameModeText != null) gameModeText.text = gameMode.ToString();
    }

    /// <summary>
    /// Returns to the lobby list via LobbyScreenManager.
    /// </summary>
    private void GoToLobbyList() {
        if (LobbyScreenManager.Instance != null)
            LobbyScreenManager.Instance.ShowScreen(LobbyScreenManager.Screen.LobbyList);
        else
            gameObject.SetActive(false);
    }

    /// <summary>
    /// Transitions to this panel and resets fields to defaults.
    /// Called from LobbyListUI when the "Create Lobby" button is clicked.
    /// </summary>
    public void Show() {
        if (LobbyScreenManager.Instance != null)
            LobbyScreenManager.Instance.ShowScreen(LobbyScreenManager.Screen.LobbyCreate);
        else
            gameObject.SetActive(true);

        // Reset defaults
        isPrivate = false;
        gameMode = LobbyManager.GameMode.TexasHoldem;

        if (lobbyNameInput != null) lobbyNameInput.text = "MyLobby";
        if (maxPlayersInput != null) maxPlayersInput.text = "9";
        UpdateToggleTexts();
    }
}
