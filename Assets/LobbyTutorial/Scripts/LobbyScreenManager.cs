using System;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Central controller for lobby-flow navigation with two layers:
///
///   1. Auth screen  (pre-login — tab bar hidden)
///   2. Tab bar      (post-login — Profile, Lobby, Settings)
///
/// Each tab owns a set of sub-panels. Switching tabs hides the current
/// tab's panels and memorises which one was open. Returning to that tab
/// restores the last-active panel.  Switching panels inside a tab does
/// NOT change the active tab.
///
/// Named screens (see <see cref="Screen"/>) map to specific tab + panel
/// combinations so existing callers (AuthenticateUI, LobbyCreateUI, etc.)
/// keep working unchanged.
///
/// Uses [DefaultExecutionOrder(100)] so every panel's Awake() (which sets
/// static Instances and wires buttons) runs before this script hides them.
/// </summary>
[DefaultExecutionOrder(100)]
public class LobbyScreenManager : MonoBehaviour
{
    public static LobbyScreenManager Instance { get; private set; }

    // ── Enums ──────────────────────────────────────────────────────

    public enum Tab { Profile, Lobby, Settings }

    /// <summary>
    /// Named shortcuts for direct navigation.
    /// <c>Auth</c> exits the tab bar; every other value maps to a
    /// specific panel inside one of the tabs.
    /// </summary>
    public enum Screen
    {
        Auth,
        LobbyList,
        LobbyCreate,
        LobbyRoom
    }

    // ── Tab definition ─────────────────────────────────────────────

    [System.Serializable]
    public class TabDefinition
    {
        public Tab tab;
        public Button tabButton;
        [Tooltip("Optional highlight / indicator activated when this tab is selected.")]
        public GameObject highlight;
        [Tooltip("Sub-panels belonging to this tab. Index 0 is the default.")]
        public GameObject[] panels;

        /// <summary>Index of the last panel the user had open in this tab.</summary>
        [System.NonSerialized] public int lastActivePanelIndex;
    }

    // ── Inspector ──────────────────────────────────────────────────

    [Header("Auth")]
    [SerializeField] private GameObject authPanel;

    [Header("Tab Bar")]
    [Tooltip("Root GameObject of the tab-button bar. Hidden in auth mode.")]
    [SerializeField] private GameObject tabBar;

    [Header("Tabs")]
    [SerializeField] private TabDefinition[] tabs;

    [Header("Panel Quick-Access (must also appear in a tab's panels array)")]
    [SerializeField] private GameObject lobbyListPanel;
    [SerializeField] private GameObject lobbyCreatePanel;
    [SerializeField] private GameObject lobbyRoomPanel;

    // ── Runtime state ──────────────────────────────────────────────

    private int activeTabIndex = -1;

    /// <summary>The currently active tab, or null when in auth mode.</summary>
    public Tab? ActiveTab => activeTabIndex >= 0 ? tabs[activeTabIndex].tab : (Tab?)null;

    // ════════════════════════════════════════════════════════════════
    //  Lifecycle
    // ════════════════════════════════════════════════════════════════

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        // Wire tab-button click listeners
        for (int i = 0; i < tabs.Length; i++)
        {
            int captured = i;
            if (tabs[i].tabButton != null)
                tabs[i].tabButton.onClick.AddListener(() => SwitchToTabIndex(captured));
        }

        // Begin on the auth screen with everything else hidden.
        EnterAuthMode();
    }

    private void Start()
    {
        if (LobbyManager.Instance == null) return;

        LobbyManager.Instance.OnJoinedLobby += HandleJoinedLobby;
        LobbyManager.Instance.OnLeftLobby += HandleLeftLobby;
        LobbyManager.Instance.OnKickedFromLobby += HandleKickedFromLobby;
    }

    private void OnDestroy()
    {
        if (LobbyManager.Instance == null) return;

        LobbyManager.Instance.OnJoinedLobby -= HandleJoinedLobby;
        LobbyManager.Instance.OnLeftLobby -= HandleLeftLobby;
        LobbyManager.Instance.OnKickedFromLobby -= HandleKickedFromLobby;
    }

    // ════════════════════════════════════════════════════════════════
    //  Public API
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Navigate to a named screen. <c>Auth</c> hides tabs and shows the
    /// login panel. Any other value ensures the tab bar is visible and
    /// activates the correct tab + panel.
    /// </summary>
    public void ShowScreen(Screen screen)
    {
        if (screen == Screen.Auth)
        {
            EnterAuthMode();
            return;
        }

        ExitAuthMode();

        GameObject target = ScreenToPanel(screen);
        if (target != null)
            ActivatePanelByRef(target);
    }

    /// <summary>
    /// Navigate to any panel by direct reference. The panel must be
    /// listed in one of the <see cref="TabDefinition.panels"/> arrays.
    /// Switches tab automatically if the panel belongs to a different tab.
    /// </summary>
    public void ShowPanel(GameObject panel)
    {
        if (panel == null) return;
        ExitAuthMode();
        ActivatePanelByRef(panel);
    }

    /// <summary>
    /// Switch to a tab by enum. Restores the last-active panel in that tab.
    /// Does nothing if already on the requested tab.
    /// </summary>
    public void SwitchTab(Tab tab)
    {
        int idx = FindTabIndex(tab);
        if (idx < 0) return;
        ExitAuthMode();
        SwitchToTabIndex(idx);
    }

    // ════════════════════════════════════════════════════════════════
    //  Auth Mode
    // ════════════════════════════════════════════════════════════════

    private void EnterAuthMode()
    {
        HideAllTabPanels();
        if (tabBar != null) tabBar.SetActive(false);
        activeTabIndex = -1;

        if (authPanel != null) authPanel.SetActive(true);
    }

    private void ExitAuthMode()
    {
        if (authPanel != null) authPanel.SetActive(false);
        if (tabBar != null) tabBar.SetActive(true);
    }

    // ════════════════════════════════════════════════════════════════
    //  Tab Switching (internal)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Switch to the tab at <paramref name="tabIndex"/>, restoring its
    /// last-active panel.  No-op if already on that tab.
    /// </summary>
    private void SwitchToTabIndex(int tabIndex)
    {
        if (tabIndex < 0 || tabIndex >= tabs.Length) return;
        if (tabIndex == activeTabIndex) return;

        // Hide current tab's panels
        if (activeTabIndex >= 0)
            HideTabPanels(activeTabIndex);

        activeTabIndex = tabIndex;

        // Restore remembered panel (default to 0)
        int panelIdx = tabs[tabIndex].lastActivePanelIndex;
        if (panelIdx < 0 || panelIdx >= tabs[tabIndex].panels.Length)
            panelIdx = 0;

        ShowPanelInTab(tabIndex, panelIdx);
        UpdateTabHighlights();
    }

    /// <summary>
    /// Finds the tab that contains <paramref name="panel"/> and activates
    /// that panel inside its tab. Switches tab if the panel lives in a
    /// different tab than the currently active one.
    /// </summary>
    private void ActivatePanelByRef(GameObject panel)
    {
        for (int t = 0; t < tabs.Length; t++)
        {
            var td = tabs[t];
            for (int p = 0; p < td.panels.Length; p++)
            {
                if (td.panels[p] != panel) continue;

                // Different tab → switch (hides old tab's panels)
                if (t != activeTabIndex)
                {
                    if (activeTabIndex >= 0)
                        HideTabPanels(activeTabIndex);

                    activeTabIndex = t;
                    UpdateTabHighlights();
                }

                ShowPanelInTab(t, p);
                return;
            }
        }

        Debug.LogWarning($"[LobbyScreenManager] Panel '{panel.name}' not found in any tab definition.");
    }

    // ════════════════════════════════════════════════════════════════
    //  Panel Helpers
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Activates exactly one panel inside a tab and deactivates the rest.
    /// Updates the tab's <c>lastActivePanelIndex</c>.
    /// </summary>
    private void ShowPanelInTab(int tabIndex, int panelIndex)
    {
        var td = tabs[tabIndex];
        for (int i = 0; i < td.panels.Length; i++)
        {
            if (td.panels[i] != null)
                td.panels[i].SetActive(i == panelIndex);
        }
        td.lastActivePanelIndex = panelIndex;
    }

    private void HideTabPanels(int tabIndex)
    {
        foreach (var p in tabs[tabIndex].panels)
        {
            if (p != null) p.SetActive(false);
        }
    }

    private void HideAllTabPanels()
    {
        for (int t = 0; t < tabs.Length; t++)
            HideTabPanels(t);
    }

    private void UpdateTabHighlights()
    {
        for (int i = 0; i < tabs.Length; i++)
        {
            if (tabs[i].highlight != null)
                tabs[i].highlight.SetActive(i == activeTabIndex);
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Lookups
    // ════════════════════════════════════════════════════════════════

    private int FindTabIndex(Tab tab)
    {
        for (int i = 0; i < tabs.Length; i++)
        {
            if (tabs[i].tab == tab) return i;
        }
        return -1;
    }

    private GameObject ScreenToPanel(Screen screen)
    {
        switch (screen)
        {
            case Screen.LobbyList:   return lobbyListPanel;
            case Screen.LobbyCreate: return lobbyCreatePanel;
            case Screen.LobbyRoom:   return lobbyRoomPanel;
            default:                 return null;
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Event Handlers (automatic transitions)
    // ════════════════════════════════════════════════════════════════

    private void HandleJoinedLobby(object sender, LobbyManager.LobbyEventArgs e)
        => ShowScreen(Screen.LobbyRoom);

    private void HandleLeftLobby(object sender, EventArgs e)
        => ShowScreen(Screen.LobbyList);

    private void HandleKickedFromLobby(object sender, LobbyManager.LobbyEventArgs e)
        => ShowScreen(Screen.LobbyList);
}
