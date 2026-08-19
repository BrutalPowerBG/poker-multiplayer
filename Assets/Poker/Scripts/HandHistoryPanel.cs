using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// ═══════════════════════════════════════════════════════════════════
//  Hand History Panel
//
//  Attach to a panel that contains a ScrollView.
//  Reads completed hands from HandRecorder's in-memory list (no
//  additional Firebase / Cloud Save queries are made).
//
//  Setup:
//    1. Create a panel with a child ScrollView (Scroll Rect).
//    2. Assign the ScrollView's Content transform to "scrollContent".
//    3. Create a hand entry prefab with a HandHistoryEntry component
//       and assign it to "handEntryPrefab".
//    4. Optionally assign a close button.
//
//  Usage:
//    Call Show() to open the panel — it clears old entries and
//    repopulates from the latest in-memory hand list.
//    Newest hands appear at the top.
// ═══════════════════════════════════════════════════════════════════

public class HandHistoryPanel : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The Content transform inside the ScrollView.")]
    [SerializeField] private Transform scrollContent;

    [Tooltip("Prefab with a HandHistoryEntry component.")]
    [SerializeField] private GameObject handEntryPrefab;

    [Tooltip("Optional close button to hide the panel.")]
    [SerializeField] private Button closeButton;

    [Header("Session Info")]
    [Tooltip("Displays the table/lobby name. Set once at game start.")]
    [SerializeField] private TextMeshProUGUI tableNameText;

    [Tooltip("Displays the local player's running net profit/loss. Updated after each hand.")]
    [SerializeField] private TextMeshProUGUI netProfitText;

    // Pooled entry instances so we avoid GC churn on repeated opens.
    private readonly List<GameObject> entryPool = new List<GameObject>();
    private bool subscribedToEvents;
    private bool subscribedToRecorder;
    private HandRecorder cachedRecorder;

    private void Awake()
    {
        if (closeButton != null)
            closeButton.onClick.AddListener(Hide);

        EnsureContentLayout();

        // Start hidden
        gameObject.SetActive(false);
    }

    private void OnEnable()
    {
        TrySubscribe();
    }

    private void OnDisable()
    {
        TryUnsubscribe();
        TryUnsubscribeRecorder();
    }

    private void Update()
    {
        // Lazy subscribe — GameManager may not exist yet on first frame
        if (!subscribedToEvents)
            TrySubscribe();
        if (!subscribedToRecorder)
            TrySubscribeRecorder();
    }

    private void TrySubscribe()
    {
        if (subscribedToEvents) return;
        if (GameManager.Instance == null) return;

        GameManager.Instance.OnHandStarted   += HandleHandStarted;
        GameManager.Instance.OnHandCompleted  += HandleHandCompleted;
        subscribedToEvents = true;

        // Set table name immediately if HandRecorder is already initialised
        UpdateTableName();
    }

    private void TryUnsubscribe()
    {
        if (!subscribedToEvents) return;
        if (GameManager.Instance == null) return;

        GameManager.Instance.OnHandStarted   -= HandleHandStarted;
        GameManager.Instance.OnHandCompleted  -= HandleHandCompleted;
        subscribedToEvents = false;
    }

    private void TrySubscribeRecorder()
    {
        if (subscribedToRecorder) return;

        cachedRecorder = FindHandRecorder();
        if (cachedRecorder == null) return;

        cachedRecorder.OnHandHistoryUpdated += HandleHandHistoryUpdated;
        subscribedToRecorder = true;
    }

    private void TryUnsubscribeRecorder()
    {
        if (!subscribedToRecorder) return;
        if (cachedRecorder != null)
            cachedRecorder.OnHandHistoryUpdated -= HandleHandHistoryUpdated;
        subscribedToRecorder = false;
        cachedRecorder = null;
    }

    private void HandleHandHistoryUpdated()
    {
        if (gameObject.activeInHierarchy)
            Refresh();
    }

    // Called once when the first hand begins — sets the table name
    private void HandleHandStarted(int handNumber, int dealerSlot, int sbSlot, int bbSlot,
        int[] playerSlots, float[] playerStacks)
    {
        UpdateTableName();
    }

    // Called at the end of each hand — refresh net P&L and append the new entry
    private void HandleHandCompleted(int[] winnerSlots, float[] winnerAmounts, float potTotal,
        int[] ccRanks, int[] ccSuits,
        int[] wCard1Ranks, int[] wCard1Suits, int[] wCard2Ranks, int[] wCard2Suits, bool cardsRevealed)
    {
        UpdateNetProfit();

        // If the panel is visible, append the latest hand immediately
        if (gameObject.activeInHierarchy)
            Refresh();
    }

    /// <summary>
    /// Makes sure the scroll Content has a VerticalLayoutGroup and a
    /// ContentSizeFitter so the scrollbar works correctly.
    /// Also anchors the Content to the top so it grows downward.
    /// </summary>
    private void EnsureContentLayout()
    {
        if (scrollContent == null) return;

        // ── VerticalLayoutGroup ──
        VerticalLayoutGroup vlg = scrollContent.GetComponent<VerticalLayoutGroup>();
        if (vlg == null)
        {
            vlg = scrollContent.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.spacing = 4f;
        }

        // ── ContentSizeFitter ──
        ContentSizeFitter csf = scrollContent.GetComponent<ContentSizeFitter>();
        if (csf == null)
        {
            csf = scrollContent.gameObject.AddComponent<ContentSizeFitter>();
        }
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        // ── Anchor Content to top so it grows downward ──
        RectTransform rt = scrollContent as RectTransform;
        if (rt != null)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  Public API
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Shows the panel and populates it with the latest hand history.
    /// </summary>
    public void Show()
    {
        gameObject.SetActive(true);
        UpdateTableName();
        UpdateNetProfit();
        Refresh();
    }

    /// <summary>
    /// Hides the panel.
    /// </summary>
    public void Hide()
    {
        gameObject.SetActive(false);
    }

    /// <summary>
    /// Clears and repopulates the scroll view from HandRecorder's in-memory list.
    /// </summary>
    public void Refresh()
    {
        HandRecorder recorder = FindHandRecorder();
        if (recorder == null)
        {
            Debug.LogWarning($"[{LogTag.History}] Refresh skipped — reason=no HandRecorder in scene", this);
            ClearEntries();
            return;
        }

        IReadOnlyList<HandRecord> hands = recorder.CompletedHands;
        PopulateEntries(hands);
    }

    // ══════════════════════════════════════════════════════════════
    //  Internal
    // ══════════════════════════════════════════════════════════════

    private void PopulateEntries(IReadOnlyList<HandRecord> hands)
    {
        if (scrollContent == null || handEntryPrefab == null) return;

        // Deactivate all pooled entries
        for (int i = 0; i < entryPool.Count; i++)
        {
            if (entryPool[i] != null)
                entryPool[i].SetActive(false);
        }

        if (hands == null || hands.Count == 0) return;

        // Populate newest-first
        for (int i = hands.Count - 1; i >= 0; i--)
        {
            GameObject entryGO = GetOrCreateEntry(hands.Count - 1 - i);
            entryGO.SetActive(true);

            HandHistoryEntry entry = entryGO.GetComponent<HandHistoryEntry>();
            if (entry != null)
            {
                entry.Populate(hands[i]);
            }
        }
    }

    private GameObject GetOrCreateEntry(int poolIndex)
    {
        // Reuse an existing pooled object
        while (poolIndex < entryPool.Count)
        {
            if (entryPool[poolIndex] != null)
                return entryPool[poolIndex];

            // Null slot (destroyed externally) — remove and try next
            entryPool.RemoveAt(poolIndex);
        }

        // Need a new one
        GameObject go = Instantiate(handEntryPrefab, scrollContent);
        entryPool.Add(go);
        return go;
    }

    private void ClearEntries()
    {
        for (int i = 0; i < entryPool.Count; i++)
        {
            if (entryPool[i] != null)
                entryPool[i].SetActive(false);
        }
    }

    private void UpdateTableName()
    {
        if (tableNameText == null) return;

        HandRecorder recorder = FindHandRecorder();
        if (recorder != null)
            tableNameText.text = recorder.TableName;
    }

    private void UpdateNetProfit()
    {
        if (netProfitText == null) return;

        HandRecorder recorder = FindHandRecorder();
        float net = recorder != null ? recorder.LocalSessionNetResult : 0f;

        // Color-code: green for profit, red for loss, white for zero
        if (net > 0.005f)
        {
            netProfitText.text = $"+${net:F2}";
            netProfitText.color = new Color(0.2f, 0.9f, 0.2f); // green
        }
        else if (net < -0.005f)
        {
            netProfitText.text = $"-${Mathf.Abs(net):F2}";
            netProfitText.color = new Color(0.9f, 0.25f, 0.25f); // red
        }
        else
        {
            netProfitText.text = "$0.00";
            netProfitText.color = Color.white;
        }
    }

    private HandRecorder FindHandRecorder()
    {
        return FindObjectOfType<HandRecorder>();
    }
}
