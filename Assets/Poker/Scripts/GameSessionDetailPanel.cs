using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// ═══════════════════════════════════════════════════════════════════
//  Game Session Detail Panel
//
//  Opened when the player clicks on a GameHistoryEntry.
//  Fetches the full session document (which contains ALL players'
//  summaries, not just the local player's) and displays each
//  participant's avatar, display name, and net profit/loss.
//
//  Setup:
//    1. Create a panel with header texts and a ScrollView.
//    2. Assign the ScrollView's Content transform to "playerListContent".
//    3. Create a player row prefab with a GameSessionPlayerEntry
//       component and assign it to "playerEntryPrefab".
//    4. Assign the optional header texts and close button.
//    5. GameHistoryEntry receives a reference to this panel so it
//       can call Show(sessionId, session) on click.
// ═══════════════════════════════════════════════════════════════════

public class GameSessionDetailPanel : MonoBehaviour
{
    [Header("Header UI")]
    [Tooltip("Displays the table name at the top of the panel.")]
    [SerializeField] private TextMeshProUGUI tableNameText;

    [Tooltip("Displays the blinds (e.g. '1.00 / 2.00').")]
    [SerializeField] private TextMeshProUGUI blindsText;

    [Tooltip("Displays the total number of hands played.")]
    [SerializeField] private TextMeshProUGUI handCountText;

    [Tooltip("Displays the session duration or start/end.")]
    [SerializeField] private TextMeshProUGUI durationText;

    [Tooltip("Displays the player count.")]
    [SerializeField] private TextMeshProUGUI playerCountText;

    [Header("Player List")]
    [Tooltip("The Content transform inside the players ScrollView.")]
    [SerializeField] private Transform playerListContent;

    [Tooltip("Prefab with a GameSessionPlayerEntry component.")]
    [SerializeField] private GameObject playerEntryPrefab;

    [Header("Controls")]
    [Tooltip("Button to close / hide the detail panel.")]
    [SerializeField] private Button closeButton;

    [Tooltip("Optional loading indicator shown while fetching data.")]
    [SerializeField] private GameObject loadingIndicator;

    // ── Internal state ────────────────────────────────────────────
    private IHandHistoryStorage storage;
    private readonly List<GameObject> playerEntryPool = new List<GameObject>();
    private bool isLoading;

    // ══════════════════════════════════════════════════════════════
    //  Unity lifecycle
    // ══════════════════════════════════════════════════════════════

    private void Awake()
    {
        if (closeButton != null)
            closeButton.onClick.AddListener(Hide);

        EnsureContentLayout();
        gameObject.SetActive(false);
    }

    // ══════════════════════════════════════════════════════════════
    //  Public API
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Opens the detail panel for a specific session.
    /// <paramref name="cachedSession"/> is the lightweight entry from
    /// the index (used for header info).  The method then fetches the
    /// full session document to get ALL player summaries.
    /// </summary>
    public void Show(TableSessionEntry cachedSession)
    {
        if (cachedSession == null) return;

        gameObject.SetActive(true);

        // Populate header immediately from cached data
        PopulateHeader(cachedSession);

        // Clear previous player rows
        ClearPlayerEntries();

        // If the cached entry already has multiple player summaries
        // (e.g. from Cloud Save or a previous full load), use them directly.
        // Otherwise, fetch the full session document from Firestore.
        if (cachedSession.playerSummaries != null && cachedSession.playerSummaries.Count > 1)
        {
            PopulatePlayers(cachedSession.playerSummaries);
        }
        else
        {
            _ = LoadFullSessionAsync(cachedSession);
        }
    }

    /// <summary>
    /// Hides the detail panel.
    /// </summary>
    public void Hide()
    {
        gameObject.SetActive(false);
    }

    // ══════════════════════════════════════════════════════════════
    //  Loading
    // ══════════════════════════════════════════════════════════════

    private async Task LoadFullSessionAsync(TableSessionEntry cachedSession)
    {
        if (isLoading) return;
        isLoading = true;

        SetLoadingVisible(true);

        try
        {
            EnsureStorage();

            if (storage == null)
            {
                Debug.LogWarning($"[{LogTag.History}] Session detail load skipped — reason=no storage backend available", this);
                // Fall back to whatever we already have
                if (cachedSession.playerSummaries != null && cachedSession.playerSummaries.Count > 0)
                    PopulatePlayers(cachedSession.playerSummaries);
                return;
            }

            TableSessionEntry fullEntry =
                await storage.LoadSessionEntryAsync(cachedSession.sessionId);

            // Panel may have been closed while we were waiting
            if (!gameObject.activeInHierarchy) return;

            if (fullEntry != null && fullEntry.playerSummaries != null
                                  && fullEntry.playerSummaries.Count > 0)
            {
                // Update header with more accurate data from the full doc
                PopulateHeader(fullEntry);
                PopulatePlayers(fullEntry.playerSummaries);
            }
            else
            {
                // Full doc unavailable — fall back to embedded data
                if (cachedSession.playerSummaries != null && cachedSession.playerSummaries.Count > 0)
                    PopulatePlayers(cachedSession.playerSummaries);
                else
                    Debug.LogWarning($"[{LogTag.History}] No player summaries found — sessionId={cachedSession.sessionId}", this);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[{LogTag.History}] Session detail load failed — error={e.Message}", this);
            // Fall back to embedded data
            if (cachedSession.playerSummaries != null && cachedSession.playerSummaries.Count > 0)
                PopulatePlayers(cachedSession.playerSummaries);
        }
        finally
        {
            isLoading = false;
            SetLoadingVisible(false);
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  Header population
    // ══════════════════════════════════════════════════════════════

    private void PopulateHeader(TableSessionEntry session)
    {
        if (tableNameText != null)
            tableNameText.text = string.IsNullOrEmpty(session.tableName)
                ? "Unknown Table"
                : session.tableName;

        if (blindsText != null)
            blindsText.text = $"{session.smallBlind:F2} / {session.bigBlind:F2}";

        if (handCountText != null)
            handCountText.text = $"{session.handCount} hand{(session.handCount != 1 ? "s" : "")} played";

        if (durationText != null)
        {
            if (session.createdAtUtcMs > 0 && session.lastActivityUtcMs > session.createdAtUtcMs)
            {
                long durationMs = session.lastActivityUtcMs - session.createdAtUtcMs;
                int minutes = (int)(durationMs / 60000);
                durationText.text = $"{minutes} min";
            }
            else
            {
                durationText.text = "—";
            }
        }

        if (playerCountText != null)
        {
            int count = session.playerCount > 0
                ? session.playerCount
                : (session.playerSummaries != null ? session.playerSummaries.Count : 0);
            playerCountText.text = $"{count} player{(count != 1 ? "s" : "")}";
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  Player list population
    // ══════════════════════════════════════════════════════════════

    private void PopulatePlayers(List<SessionPlayerSummary> summaries)
    {
        if (playerListContent == null || playerEntryPrefab == null) return;

        ClearPlayerEntries();

        // Sort by net result descending (biggest winner first)
        summaries.Sort((a, b) => b.netResult.CompareTo(a.netResult));

        for (int i = 0; i < summaries.Count; i++)
        {
            GameObject go = GetOrCreatePlayerEntry(i);
            go.SetActive(true);

            GameSessionPlayerEntry entry = go.GetComponent<GameSessionPlayerEntry>();
            if (entry != null)
                entry.Populate(summaries[i]);
        }
    }

    private void ClearPlayerEntries()
    {
        for (int i = 0; i < playerEntryPool.Count; i++)
        {
            if (playerEntryPool[i] != null)
                playerEntryPool[i].SetActive(false);
        }
    }

    private GameObject GetOrCreatePlayerEntry(int poolIndex)
    {
        while (poolIndex < playerEntryPool.Count)
        {
            if (playerEntryPool[poolIndex] != null)
                return playerEntryPool[poolIndex];

            playerEntryPool.RemoveAt(poolIndex);
        }

        GameObject go = Instantiate(playerEntryPrefab, playerListContent);
        playerEntryPool.Add(go);
        return go;
    }

    // ══════════════════════════════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════════════════════════════

    private void EnsureStorage()
    {
        if (storage != null) return;

        BackendType backend = LobbyManager.Instance != null
            ? LobbyManager.Instance.Backend
            : BackendType.Firebase;

        switch (backend)
        {
            case BackendType.Firebase:
                storage = new FirestoreHandHistory();
                break;

            case BackendType.UnityServices:
            default:
                storage = new CloudSaveHandHistory();
                break;
        }
    }

    private void SetLoadingVisible(bool visible)
    {
        if (loadingIndicator != null)
            loadingIndicator.SetActive(visible);
    }

    private static string MsToLocalString(long utcMs)
    {
        if (utcMs <= 0) return "—";

        DateTime utc   = DateTimeOffset.FromUnixTimeMilliseconds(utcMs).UtcDateTime;
        DateTime local = utc.ToLocalTime();
        return local.ToString("dd MMM yyyy  HH:mm");
    }

    /// <summary>
    /// Ensures the scroll Content has a VerticalLayoutGroup and a
    /// ContentSizeFitter so the scrollbar works correctly.
    /// </summary>
    private void EnsureContentLayout()
    {
        if (playerListContent == null) return;

        VerticalLayoutGroup vlg = playerListContent.GetComponent<VerticalLayoutGroup>();
        if (vlg == null)
        {
            vlg = playerListContent.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.childAlignment        = TextAnchor.UpperCenter;
            vlg.childControlWidth     = true;
            vlg.childControlHeight    = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.spacing               = 4f;
        }

        ContentSizeFitter csf = playerListContent.GetComponent<ContentSizeFitter>();
        if (csf == null)
            csf = playerListContent.gameObject.AddComponent<ContentSizeFitter>();
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        RectTransform rt = playerListContent as RectTransform;
        if (rt != null)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot     = new Vector2(0.5f, 1f);
        }
    }
}
