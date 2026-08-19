using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Generic tab controller. Each tab pairs a Button with a panel GameObject.
/// Clicking a tab activates its panel and deactivates the rest.
/// A highlight Image stretches and moves to sit behind the active tab button.
///
/// Inspector setup:
///   • tabs[]        – one entry per tab (button + panel)
///   • highlight     – an Image (e.g. behind the buttons) that slides to the active tab
///   • defaultTab    – index of the tab selected on enable (default 0)
///   • animateSpeed  – lerp speed for the highlight; 0 = instant snap
/// </summary>
public class TabGroup : MonoBehaviour
{
    [System.Serializable]
    public class TabEntry
    {
        public Button tabButton;
        public GameObject panel;
    }

    [Header("Tabs")]
    [SerializeField] private TabEntry[] tabs;

    [Header("Highlight")]
    [Tooltip("Image that stretches to match the active tab button.")]
    [SerializeField] private RectTransform highlight;

    [Tooltip("Index of the tab selected by default (0-based).")]
    [SerializeField] private int defaultTab;

    [Tooltip("Lerp speed for the highlight movement. 0 = instant.")]
    [SerializeField] private float animateSpeed = 12f;

    private int activeIndex = -1;
    private RectTransform targetRect;

    // ════════════════════════════════════════════════════════════════
    //  Lifecycle
    // ════════════════════════════════════════════════════════════════

    private void Awake()
    {
        for (int i = 0; i < tabs.Length; i++)
        {
            int captured = i;
            if (tabs[i].tabButton != null)
                tabs[i].tabButton.onClick.AddListener(() => SelectTab(captured));
        }
    }

    private void OnEnable()
    {
        int idx = (defaultTab >= 0 && defaultTab < tabs.Length) ? defaultTab : 0;
        SelectTab(idx, true);
    }

    private void Update()
    {
        if (highlight == null || targetRect == null) return;
        if (animateSpeed <= 0f) return; // instant mode handled in SelectTab

        // Smoothly lerp position and size toward the target tab button
        float t = Time.unscaledDeltaTime * animateSpeed;

        highlight.position = Vector3.Lerp(highlight.position, targetRect.position, t);
        highlight.sizeDelta = Vector2.Lerp(highlight.sizeDelta, targetRect.sizeDelta, t);
    }

    // ════════════════════════════════════════════════════════════════
    //  Public API
    // ════════════════════════════════════════════════════════════════

    /// <summary>Current active tab index (-1 if none).</summary>
    public int ActiveIndex => activeIndex;

    /// <summary>
    /// Activate a tab by index. Activates its panel, deactivates the rest,
    /// and moves the highlight to the tab button.
    /// </summary>
    public void SelectTab(int index, bool instant = false)
    {
        if (tabs == null || tabs.Length == 0) return;
        if (index < 0 || index >= tabs.Length) return;

        activeIndex = index;

        // Panels
        for (int i = 0; i < tabs.Length; i++)
        {
            if (tabs[i].panel != null)
                tabs[i].panel.SetActive(i == index);
        }

        // Highlight
        if (highlight != null && tabs[index].tabButton != null)
        {
            targetRect = tabs[index].tabButton.GetComponent<RectTransform>();

            if (instant || animateSpeed <= 0f)
            {
                highlight.position = targetRect.position;
                highlight.sizeDelta = targetRect.sizeDelta;
            }
        }
    }
}
