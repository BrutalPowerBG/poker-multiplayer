using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class AuthenticateUI : MonoBehaviour {

    // ==================== TAB BUTTONS ====================
    [Header("Tab Buttons")]
    [SerializeField] private Button loginTabButton;
    [SerializeField] private Button registerTabButton;

    // ==================== TAB PANELS ====================
    [Header("Tab Panels")]
    [SerializeField] private GameObject loginTab;
    [SerializeField] private GameObject registerTab;

    // ==================== GUEST TAB ====================
    [Header("Guest Tab")]
    [SerializeField] private Button guestAuthenticateButton;

    // ==================== LOGIN TAB ====================
    [Header("Login Tab")]
    [SerializeField] private TMP_InputField loginEmailField;
    [SerializeField] private Image loginEmailIcon;
    [SerializeField] private TMP_InputField loginPasswordField;
    [SerializeField] private Image loginPasswordIcon;
    [SerializeField] private Button loginSubmitButton;
    [SerializeField] private Toggle rememberMeToggle;
    [SerializeField] private GameObject rememberMeCheckmark;

    // ==================== REGISTER TAB ====================
    [Header("Register Tab")]
    [SerializeField] private TMP_InputField registerUsernameField;
    [SerializeField] private Image registerUsernameIcon;
    [SerializeField] private TMP_InputField registerEmailField;
    [SerializeField] private Image registerEmailIcon;
    [SerializeField] private TMP_InputField registerPasswordField;
    [SerializeField] private Image registerPasswordIcon;
    [SerializeField] private Button registerSubmitButton;

    // ==================== TAB COLORS ====================
    [Header("Tab Button Colors")]
    [SerializeField] private Color activeTabColor = new Color(1f, 1f, 1f, 1f);
    [SerializeField] private Color inactiveTabColor = new Color(0.6f, 0.6f, 0.6f, 1f);

    // ==================== INPUT ICON COLORS ====================
    private static readonly Color IconActiveColor = Color.white;
    private static readonly Color IconIdleColor = new Color(0.898f, 0.698f, 0.357f, 1f); // #E5B25B

    // ==================== FEEDBACK ====================
    [Header("Feedback")]
    [SerializeField] private TextMeshProUGUI errorText;

    private Coroutine hideErrorCoroutine;
    private GameObject activeTab;
    private float submitCooldownUntil = 0f;
    private const float SUBMIT_COOLDOWN = 4f;

    // ---- Remember Me ----
    // Only the email is persisted. Storing a password in PlayerPrefs (even encoded)
    // is plaintext-accessible on desktop and rooted mobile devices. Firebase Auth
    // already persists its own session token securely; if that token is still valid
    // the user is re-authenticated silently without needing a password at all.
    private const string PREF_REMEMBER_ME  = "auth_remember_me";
    private const string PREF_SAVED_EMAIL  = "auth_saved_email";

    private void Awake() {
        // Tab button listeners
        if (loginTabButton != null)
            loginTabButton.onClick.AddListener(() => ShowTab(loginTab));
        if (registerTabButton != null)
            registerTabButton.onClick.AddListener(() => ShowTab(registerTab));

        // Guest authenticate
        if (guestAuthenticateButton != null)
            guestAuthenticateButton.onClick.AddListener(OnGuestAuthenticate);

        // Login submit
        if (loginSubmitButton != null)
            loginSubmitButton.onClick.AddListener(OnLoginClicked);

        // Register submit
        if (registerSubmitButton != null)
            registerSubmitButton.onClick.AddListener(OnRegisterClicked);

        // Input field icon color listeners
        BindInputIcon(loginEmailField, loginEmailIcon);
        BindInputIcon(loginPasswordField, loginPasswordIcon);
        BindInputIcon(registerUsernameField, registerUsernameIcon);
        BindInputIcon(registerEmailField, registerEmailIcon);
        BindInputIcon(registerPasswordField, registerPasswordIcon);

        // Remember Me — restore toggle state and pre-fill email if opted in
        LoadRememberMe();
        if (rememberMeToggle != null)
            rememberMeToggle.onValueChanged.AddListener(OnRememberMeToggled);

        // Hide error text
        ClearError();

        // Default to login tab
        ShowTab(loginTab);
    }

    private
#if !UNITY_EDITOR
        async
#endif
        void Start() {
        // Already authenticated from this session — nothing to do.
        if (LobbyManager.Instance != null && LobbyManager.Instance.IsAuthenticated) {
            GoToLobbyList();
            return;
        }

#if !UNITY_EDITOR
        // If the player previously checked "Remember Me", try to silently
        // restore the persisted Firebase session without showing the UI.
        // Disabled in the Editor so each Play-Mode run requires a fresh login
        // (avoids stale tokens and MPPM identity conflicts).
        if (PlayerPrefs.GetInt(PREF_REMEMBER_ME, 0) == 1 && LobbyManager.Instance != null) {
            bool resumed = await LobbyManager.Instance.TryResumeSessionAsync();
            if (resumed) {
                GoToLobbyList();
            }
            // If not resumed (expired token, no internet, etc.) the UI stays
            // visible so the player can log in manually.
        }
#endif
    }

    private void Update() {
        // Re-enable submit buttons after cooldown expires
        if (Time.time >= submitCooldownUntil) {
            if (loginSubmitButton != null && !loginSubmitButton.interactable)
                loginSubmitButton.interactable = true;
            if (registerSubmitButton != null && !registerSubmitButton.interactable)
                registerSubmitButton.interactable = true;
        }

        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) {
            if (activeTab == loginTab && IsSubmitReady(loginSubmitButton))
                OnLoginClicked();
            else if (activeTab == registerTab && IsSubmitReady(registerSubmitButton))
                OnRegisterClicked();
        }
    }

    private bool IsSubmitReady(Button button) {
        return button != null && button.interactable && Time.time >= submitCooldownUntil;
    }

    private void StartSubmitCooldown() {
        submitCooldownUntil = Time.time + SUBMIT_COOLDOWN;
        if (loginSubmitButton != null) loginSubmitButton.interactable = false;
        if (registerSubmitButton != null) registerSubmitButton.interactable = false;
    }

    // ==================== TAB SWITCHING ====================

    private void ShowTab(GameObject tab) {
        if ((loginTab == null) || (registerTab == null))
            return;

        activeTab = tab;
        loginTab.SetActive(loginTab == tab);
        registerTab.SetActive(registerTab == tab);

        UpdateTabButtonColors(tab);
        ClearError();
    }

    private void UpdateTabButtonColors(GameObject activeTab) {
        SetTabButtonColor(loginTabButton, activeTab == loginTab);
        SetTabButtonColor(registerTabButton, activeTab == registerTab);
    }

    private void SetTabButtonColor(Button button, bool isActive) {
        if (button == null) return;
        Image img = button.GetComponent<Image>();
        if (img != null) {
            img.color = isActive ? activeTabColor : inactiveTabColor;
        }
    }

    // ==================== AUTHENTICATION ACTIONS ====================

    private void OnGuestAuthenticate() {
        LobbyManager.Instance.AuthenticateGuest(PlayerNameData.PlayerName);
        GoToLobbyList();
    }

    private async void OnLoginClicked() {
        string email = loginEmailField != null ? loginEmailField.text.Trim() : "";
        string password = loginPasswordField != null ? loginPasswordField.text : "";

        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password)) {
            ShowError("Email and password are required.");
            return;
        }

        if (!email.Contains("@")) {
            ShowError("Please enter a valid email address.");
            return;
        }

        StartSubmitCooldown();
        string error = await LobbyManager.Instance.LoginWithAccount(PlayerNameData.PlayerName, email, password);

        if (error != null) {
            ShowError(error);
            // Cooldown keeps buttons disabled — Update() will re-enable after SUBMIT_COOLDOWN
        } else {
            // Save email if Remember Me is on, clear it if it was turned off
            SaveRememberMe(email);
            // Display name is loaded from profile by FirebaseManager.LoadProfileDataAsync
            GoToLobbyList();
        }
    }

    private async void OnRegisterClicked() {
        string username = registerUsernameField != null ? registerUsernameField.text.Trim() : "";
        string email = registerEmailField != null ? registerEmailField.text.Trim() : "";
        string password = registerPasswordField != null ? registerPasswordField.text : "";

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)) {
            ShowError("Username and password are required.");
            return;
        }

        if (!string.IsNullOrEmpty(email) && !email.Contains("@")) {
            ShowError("Please enter a valid email address.");
            return;
        }

        StartSubmitCooldown();
        string error = await LobbyManager.Instance.RegisterAccount(PlayerNameData.PlayerName, username, email, password);

        if (error != null) {
            ShowError(error);
            // Cooldown keeps buttons disabled — Update() will re-enable after SUBMIT_COOLDOWN
        } else {
            // Registration auto-signs in the user — update display name and proceed
            PlayerNameData.SetPlayerName(username);
            GoToLobbyList();
        }
    }

    // ==================== ERROR DISPLAY ====================

    private void ShowError(string message) {
        if (errorText == null) return;
        errorText.text = message;
        errorText.gameObject.SetActive(true);

        if (hideErrorCoroutine != null) StopCoroutine(hideErrorCoroutine);
        hideErrorCoroutine = StartCoroutine(HideErrorAfterDelay(5f));
    }

    private void ClearError() {
        if (errorText != null) errorText.gameObject.SetActive(false);
        if (hideErrorCoroutine != null) {
            StopCoroutine(hideErrorCoroutine);
            hideErrorCoroutine = null;
        }
    }

    private IEnumerator HideErrorAfterDelay(float delay) {
        yield return new WaitForSeconds(delay);
        if (errorText != null) errorText.gameObject.SetActive(false);
        hideErrorCoroutine = null;
    }

    // ==================== REMEMBER ME ====================

    /// <summary>
    /// Restores the Remember Me toggle and pre-fills the email field if the
    /// player previously opted in. The password is intentionally never stored —
    /// Firebase Auth persists its own session token, so repeat logins are rare.
    /// </summary>
    private void LoadRememberMe() {
        bool rememberMe = PlayerPrefs.GetInt(PREF_REMEMBER_ME, 0) == 1;

        if (rememberMeToggle != null)
            rememberMeToggle.SetIsOnWithoutNotify(rememberMe);

        SetCheckmark(rememberMe);

        if (rememberMe) {
            string savedEmail = PlayerPrefs.GetString(PREF_SAVED_EMAIL, "");
            if (!string.IsNullOrEmpty(savedEmail) && loginEmailField != null)
                loginEmailField.text = savedEmail;
        }
    }

    /// <summary>
    /// Persists or clears the remembered email based on current toggle state.
    /// Called after a successful login.
    /// </summary>
    private void SaveRememberMe(string email) {
        bool rememberMe = rememberMeToggle != null && rememberMeToggle.isOn;

        PlayerPrefs.SetInt(PREF_REMEMBER_ME, rememberMe ? 1 : 0);

        if (rememberMe)
            PlayerPrefs.SetString(PREF_SAVED_EMAIL, email);
        else
            PlayerPrefs.DeleteKey(PREF_SAVED_EMAIL);

        PlayerPrefs.Save();
    }

    /// <summary>
    /// Called when the toggle is flipped manually.
    /// Immediately clears the stored email if the user opts out.
    /// </summary>
    private void OnRememberMeToggled(bool isOn) {
        SetCheckmark(isOn);

        if (!isOn) {
            PlayerPrefs.DeleteKey(PREF_SAVED_EMAIL);
            PlayerPrefs.SetInt(PREF_REMEMBER_ME, 0);
            PlayerPrefs.Save();
        }
    }

    private void SetCheckmark(bool visible) {
        if (rememberMeCheckmark != null)
            rememberMeCheckmark.SetActive(visible);
    }

    // ==================== INPUT ICON COLORS ====================

    private void BindInputIcon(TMP_InputField field, Image icon) {
        if (field == null || icon == null) return;

        icon.color = IconIdleColor;

        field.onSelect.AddListener(_ => icon.color = IconActiveColor);
        field.onDeselect.AddListener(_ => icon.color = IconIdleColor);
    }

    // ==================== HELPERS ====================

    private void SetAllButtonsInteractable(bool interactable) {
        if (guestAuthenticateButton != null) guestAuthenticateButton.interactable = interactable;
        if (loginSubmitButton != null) loginSubmitButton.interactable = interactable;
        if (registerSubmitButton != null) registerSubmitButton.interactable = interactable;
    }

    /// <summary>
    /// Transitions away from the authentication screen to the lobby list
    /// via <see cref="LobbyScreenManager"/>. Falls back to a plain hide
    /// if the manager is not present in the scene.
    /// </summary>
    private void GoToLobbyList() {
        if (LobbyScreenManager.Instance != null)
            LobbyScreenManager.Instance.ShowScreen(LobbyScreenManager.Screen.LobbyList);
        else
            gameObject.SetActive(false);
    }
}
