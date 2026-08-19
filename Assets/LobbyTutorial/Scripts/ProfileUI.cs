using TMPro;
using Unity.Services.Authentication;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Displays the player's profile (avatar, name, ID) and lets them pick a
/// character image from a grid populated with all sprites in PlayerAssets.
///
/// Inspector setup:
///   • profileImage   – large Image showing the current character
///   • profileName    – TMP text for the player display name
///   • profileId      – TMP text for the player / auth ID
///   • characterGrid  – GameObject with a GridLayoutGroup (children are generated)
/// </summary>
public class ProfileUI : MonoBehaviour
{
    [Header("Profile Display")]
    [SerializeField] private Image profileImage;
    [SerializeField] private TextMeshProUGUI profileName;
    [SerializeField] private TextMeshProUGUI profileId;

    [Header("Character Selection Grid")]
    [Tooltip("Must have a GridLayoutGroup. Character cells are spawned here.")]
    [SerializeField] private Transform characterGrid;
    [Tooltip("Prefab for each grid cell. Must contain a child Image named 'Icon' (or the first child Image is used).")]
    [SerializeField] private GameObject characterCellPrefab;

    [Header("Actions")]
    [Tooltip("Button that logs the player out and returns to the auth screen.")]
    [SerializeField] private Button logoutButton;
    [Tooltip("Button that permanently deletes the player's account.")]
    [SerializeField] private Button deleteProfileButton;

    [Header("Delete Confirmation Panel")]
    [Tooltip("Panel that asks the user to confirm account deletion. Hidden by default.")]
    [SerializeField] private GameObject deleteConfirmPanel;
    [Tooltip("Confirms the deletion inside the confirmation panel.")]
    [SerializeField] private Button deleteConfirmButton;
    [Tooltip("Cancels and hides the confirmation panel.")]
    [SerializeField] private Button deleteCancelButton;

    [Header("Selection Highlight")]
    [Tooltip("Optional color tint applied to the selected grid cell.")]
    [SerializeField] private Color selectedTint = new Color(0.5f, 1f, 0.5f, 1f);
    [Tooltip("Default color tint for unselected grid cells.")]
    [SerializeField] private Color normalTint = Color.white;

    /// <summary>Index of the currently highlighted grid cell (matches characterId).</summary>
    private int selectedIndex = -1;

    /// <summary>Cached array of spawned grid-cell Images for fast highlight toggling.</summary>
    private Image[] gridCells;

    // ════════════════════════════════════════════════════════════════
    //  Lifecycle
    // ════════════════════════════════════════════════════════════════

    private void Awake()
    {
        if (logoutButton != null)
            logoutButton.onClick.AddListener(OnLogoutClicked);
        if (deleteProfileButton != null)
            deleteProfileButton.onClick.AddListener(ShowDeleteConfirmation);
        if (deleteConfirmButton != null)
            deleteConfirmButton.onClick.AddListener(OnDeleteConfirmed);
        if (deleteCancelButton != null)
            deleteCancelButton.onClick.AddListener(HideDeleteConfirmation);
        if (deleteConfirmPanel != null)
            deleteConfirmPanel.SetActive(false);
    }

    private void OnEnable()
    {
        Refresh();
    }

    // ════════════════════════════════════════════════════════════════
    //  Public API
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Rebuilds the entire panel: profile info + grid.
    /// Safe to call at any time.
    /// </summary>
    public void Refresh()
    {
        UpdateProfileInfo();
        PopulateGrid();
    }

    // ════════════════════════════════════════════════════════════════
    //  Profile Info
    // ════════════════════════════════════════════════════════════════

    private void UpdateProfileInfo()
    {
        PlayerAssets assets = PlayerAssets.Instance;

        // Name
        if (profileName != null)
        {
            profileName.text = LobbyManager.Instance != null
                ? LobbyManager.Instance.PlayerName
                : "Player";
        }

        // ID
        if (profileId != null)
        {
            try
            {
                profileId.text = AuthenticationService.Instance != null && AuthenticationService.Instance.IsSignedIn
                    ? AuthenticationService.Instance.PlayerId
                    : "—";
            }
            catch
            {
                profileId.text = "—";
            }
        }

        // Avatar
        if (profileImage != null && assets != null)
        {
            Sprite s = assets.GetSprite(assets.currentCharacterId);
            if (s != null) profileImage.sprite = s;
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Character Grid
    // ════════════════════════════════════════════════════════════════

    private void PopulateGrid()
    {
        if (characterGrid == null) return;

        PlayerAssets assets = PlayerAssets.Instance;
        if (assets == null)
        {
            Debug.LogWarning("[ProfileUI] PlayerAssets.Instance is null – cannot populate grid.");
            return;
        }

        int count = assets.CharacterSpriteCount;
        if (count == 0) return;

        // Destroy previous children (in case of a re-populate)
        for (int i = characterGrid.childCount - 1; i >= 0; i--)
            Destroy(characterGrid.GetChild(i).gameObject);

        gridCells = new Image[count];
        selectedIndex = assets.currentCharacterId;

        for (int i = 0; i < count; i++)
        {
            int charId = i; // closure capture

            GameObject cell;
            Image iconImg;

            if (characterCellPrefab != null)
            {
                // Instantiate the designer-provided prefab
                cell = Instantiate(characterCellPrefab, characterGrid);
                cell.name = $"Character_{charId}";

                // Find a child Image named "Icon" anywhere in the hierarchy (skip root)
                iconImg = null;
                foreach (Image img in cell.GetComponentsInChildren<Image>(true))
                {
                    if (img.transform == cell.transform) continue; // skip root
                    if (img.gameObject.name == "Icon") { iconImg = img; break; }
                }
                // Fallback: first non-root Image if nothing named "Icon" was found
                if (iconImg == null)
                {
                    foreach (Image img in cell.GetComponentsInChildren<Image>(true))
                    {
                        if (img.transform != cell.transform) { iconImg = img; break; }
                    }
                }
            }
            else
            {
                // Fallback: generate a simple cell at runtime
                cell = new GameObject($"Character_{charId}", typeof(RectTransform));
                cell.transform.SetParent(characterGrid, false);
                iconImg = cell.AddComponent<Image>();
            }

            // Set sprite
            if (iconImg != null)
            {
                iconImg.sprite = assets.GetSprite(charId);
                iconImg.preserveAspect = true;
                iconImg.color = (charId == selectedIndex) ? selectedTint : normalTint;
            }
            gridCells[charId] = iconImg;

            // Ensure a Button exists on the root cell
            Button btn = cell.GetComponent<Button>();
            if (btn == null)
                btn = cell.AddComponent<Button>();
            btn.targetGraphic = iconImg;
            btn.onClick.AddListener(() => OnCharacterClicked(charId));
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Selection
    // ════════════════════════════════════════════════════════════════

    private void OnCharacterClicked(int characterId)
    {
        PlayerAssets assets = PlayerAssets.Instance;
        if (assets == null) return;

        // Update PlayerAssets (saves to cloud as well)
        assets.SetCurrentCharacterId(characterId);

        // Update profile avatar
        if (profileImage != null)
        {
            Sprite s = assets.GetSprite(characterId);
            if (s != null) profileImage.sprite = s;
        }

        // Update highlight
        if (gridCells != null)
        {
            if (selectedIndex >= 0 && selectedIndex < gridCells.Length && gridCells[selectedIndex] != null)
                gridCells[selectedIndex].color = normalTint;

            if (characterId >= 0 && characterId < gridCells.Length && gridCells[characterId] != null)
                gridCells[characterId].color = selectedTint;
        }

        selectedIndex = characterId;
    }

    // ════════════════════════════════════════════════════════════════
    //  Logout
    // ════════════════════════════════════════════════════════════════

    private void OnLogoutClicked()
    {
        // Sign out via the auth provider
        if (LobbyManager.Instance != null)
            LobbyManager.Instance.Logout();

        // Clear the Remember-Me prefs so auto-login doesn't fire next time
        PlayerPrefs.DeleteKey("auth_remember_me");
        PlayerPrefs.DeleteKey("auth_saved_email");
        PlayerPrefs.Save();

        // Navigate back to the authentication screen
        if (LobbyScreenManager.Instance != null)
            LobbyScreenManager.Instance.ShowScreen(LobbyScreenManager.Screen.Auth);
    }

    // ════════════════════════════════════════════════════════════════
    //  Delete Account
    // ════════════════════════════════════════════════════════════════

    private void ShowDeleteConfirmation()
    {
        if (deleteConfirmPanel != null)
            deleteConfirmPanel.SetActive(true);
    }

    private void HideDeleteConfirmation()
    {
        if (deleteConfirmPanel != null)
            deleteConfirmPanel.SetActive(false);
    }

    private async void OnDeleteConfirmed()
    {
        if (LobbyManager.Instance == null) return;

        if (deleteConfirmButton != null)
            deleteConfirmButton.interactable = false;

        string error = await LobbyManager.Instance.DeleteAccountAsync();

        if (error == null)
        {
            HideDeleteConfirmation();
            if (LobbyScreenManager.Instance != null)
                LobbyScreenManager.Instance.ShowScreen(LobbyScreenManager.Screen.Auth);
        }
        else
        {
            Debug.LogWarning($"[ProfileUI] Account deletion failed — {error}");
            if (deleteConfirmButton != null)
                deleteConfirmButton.interactable = true;
        }
    }
}
