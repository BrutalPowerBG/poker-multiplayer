using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

// ═══════════════════════════════════════════════════════════════════
//  Game History Panel
//
//  Attach to a panel in the lobby's Game History tab.
//  On the FIRST time the panel is enabled it queries the storage
//  backend (Firebase / Cloud Save) via LoadIndexAsync() to fetch
//  all TableSessionEntry data for the local player.  The result is
//  cached so that subsequent opens never trigger another query.
//
//  Setup:
//    1. Create a panel with a child ScrollView (Scroll Rect).
//    2. Assign the ScrollView's Content transform to "scrollContent".
//    3. Create a session entry prefab with a GameHistoryEntry component
//       and assign it to "sessionEntryPrefab".
//    4. Optionally assign a close button.
//
//  Usage:
//    The panel auto-populates in OnEnable().  Newest sessions appear
//    at the top.  Call ForceRefresh() to clear the cache and re-query.
// ═══════════════════════════════════════════════════════════════════

public class GameHistoryPanel : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The Content transform inside the ScrollView.")]
    [SerializeField] private Transform scrollContent;

    [Tooltip("Prefab with a GameHistoryEntry component.")]
    [SerializeField] private GameObject sessionEntryPrefab;

    [Tooltip("Optional close button to hide the panel.")]
    [SerializeField] private Button closeButton;

    [Tooltip("Detail panel opened when a session entry is clicked.")]
    [SerializeField] private GameSessionDetailPanel sessionDetailPanel;

    // ── Internal state ────────────────────────────────────────────
    private IHandHistoryStorage storage;
    private HandHistoryIndex cachedIndex;
    private bool hasQueried;
    private bool isLoading;

    /// <summary>UID we used for the last successful query. If the signed-in
    /// user changes (e.g. stale anonymous → real login) we re-query.</summary>
    private string queriedForUid;

    // Pooled entry instances to avoid GC churn on repeated opens.
    private readonly List<GameObject> entryPool = new List<GameObject>();

    // ══════════════════════════════════════════════════════════════
    //  Unity lifecycle
    // ══════════════════════════════════════════════════════════════

    private void Awake()
    {
        if (closeButton != null)
            closeButton.onClick.AddListener(Hide);

        EnsureContentLayout();
    }

    private void OnEnable()
    {
        // If the signed-in user changed since our last query
        // (e.g. a stale anonymous user was present at startup, then the
        // real user logged in), invalidate the cache and re-query.
        if (hasQueried)
        {
            string currentUid = GetLocalPlayerUid();
            if (!string.IsNullOrEmpty(currentUid) && currentUid != queriedForUid)
            {
                hasQueried = false;
                cachedIndex = null;
                if (storage != null) storage.ClearCaches();
            }
        }

        if (!hasQueried && !isLoading)
            _ = LoadAndPopulateAsync();
        else if (hasQueried)
            PopulateEntries();
    }

    private void Update()
    {
        // Lazy retry — if the first attempt ran before the backend was ready
        // (e.g. panel was active at startup before Firebase initialised),
        // keep retrying until we get a real result.
        if (!hasQueried && !isLoading)
            _ = LoadAndPopulateAsync();
    }

    // ══════════════════════════════════════════════════════════════
    //  Public API
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Hides the panel.
    /// </summary>
    public void Hide()
    {
        gameObject.SetActive(false);
    }

    /// <summary>
    /// Clears the cached data and re-queries the backend on next open.
    /// Useful if you know new sessions were played since last query.
    /// </summary>
    public void ForceRefresh()
    {
        cachedIndex = null;
        hasQueried = false;
        queriedForUid = null;

        // Also clear the storage-level cache so LoadIndexAsync hits the backend
        if (storage != null)
            storage.ClearCaches();

        if (gameObject.activeInHierarchy && !isLoading)
            _ = LoadAndPopulateAsync();
    }

    // ══════════════════════════════════════════════════════════════
    //  Loading
    // ══════════════════════════════════════════════════════════════

    private async Task LoadAndPopulateAsync()
    {
        isLoading = true;

        try
        {
            EnsureStorage();

            if (storage == null)
            {
                Debug.LogWarning($"[{LogTag.History}] Session index load skipped — reason=no storage backend available", this);
                ClearEntries();
                return;
            }

            // If the backend isn't ready yet (e.g. Firebase still initialising),
            // bail out without setting hasQueried so Update() retries later.
            if (!IsStorageReady())
                return;

            cachedIndex = await storage.LoadIndexAsync();
            hasQueried = true;
            queriedForUid = GetLocalPlayerUid();

            // Panel may have been disabled while we were awaiting
            if (gameObject.activeInHierarchy)
                PopulateEntries();
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[{LogTag.History}] Session index load failed — error={e.Message}", this);
        }
        finally
        {
            isLoading = false;
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  Population
    // ══════════════════════════════════════════════════════════════

    private void PopulateEntries()
    {
        if (scrollContent == null || sessionEntryPrefab == null) return;

        // Deactivate all pooled entries
        for (int i = 0; i < entryPool.Count; i++)
        {
            if (entryPool[i] != null)
                entryPool[i].SetActive(false);
        }

        if (cachedIndex == null || cachedIndex.sessions == null || cachedIndex.sessions.Count == 0)
            return;

        string localUid = GetLocalPlayerUid();
        int poolIdx = 0;

        // Sessions are already sorted newest-first by the storage layer.
        for (int i = 0; i < cachedIndex.sessions.Count; i++)
        {
            TableSessionEntry session = cachedIndex.sessions[i];

            // ── Filter out junk sessions ─────────────────────────
            // Skip sessions where the player never completed a hand
            // (e.g. joined + bought in but left before any hand finished).
            SessionPlayerSummary localSummary = FindLocalSummary(session, localUid);
            if (localSummary == null || localSummary.handsPlayed <= 0)
            {
                // Also skip by session-level handCount as a fallback
                if (session.handCount <= 0)
                    continue;
            }

            GameObject entryGO = GetOrCreateEntry(poolIdx);
            entryGO.SetActive(true);
            poolIdx++;

            GameHistoryEntry entry = entryGO.GetComponent<GameHistoryEntry>();
            if (entry != null)
            {
                entry.SetDetailPanel(sessionDetailPanel);
                entry.Populate(session, localSummary);
            }
        }
    }

    private void ClearEntries()
    {
        for (int i = 0; i < entryPool.Count; i++)
        {
            if (entryPool[i] != null)
                entryPool[i].SetActive(false);
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
        GameObject go = Instantiate(sessionEntryPrefab, scrollContent);
        entryPool.Add(go);
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

    /// <summary>
    /// Returns true when the storage backend is actually initialised
    /// and able to make queries.  Prevents marking hasQueried on a
    /// premature empty result.
    /// </summary>
    private bool IsStorageReady()
    {
        BackendType backend = LobbyManager.Instance != null
            ? LobbyManager.Instance.Backend
            : BackendType.Firebase;

        if (backend == BackendType.Firebase)
            return FirebaseManager.Instance != null && FirebaseManager.Instance.IsFirebaseReady;

        // Unity Cloud Save requires services + auth
        return Unity.Services.Core.UnityServices.State == Unity.Services.Core.ServicesInitializationState.Initialized
            && Unity.Services.Authentication.AuthenticationService.Instance.IsSignedIn;
    }

    /// <summary>
    /// Returns the local player's UID used to match against
    /// <see cref="SessionPlayerSummary.playerId"/>.
    /// </summary>
    private static string GetLocalPlayerUid()
    {
        if (FirebaseManager.Instance != null && !string.IsNullOrEmpty(FirebaseManager.Instance.UserId))
            return FirebaseManager.Instance.UserId;

        if (Unity.Services.Authentication.AuthenticationService.Instance != null &&
            Unity.Services.Authentication.AuthenticationService.Instance.IsSignedIn)
            return Unity.Services.Authentication.AuthenticationService.Instance.PlayerId;

        return "";
    }

    /// <summary>
    /// Finds the local player's summary within a session entry.
    /// </summary>
    private static SessionPlayerSummary FindLocalSummary(TableSessionEntry session, string localUid)
    {
        if (session.playerSummaries == null || string.IsNullOrEmpty(localUid))
            return null;

        for (int i = 0; i < session.playerSummaries.Count; i++)
        {
            if (session.playerSummaries[i].playerId == localUid)
                return session.playerSummaries[i];
        }

        return null;
    }

    /// <summary>
    /// Ensures the scroll Content has a VerticalLayoutGroup and a
    /// ContentSizeFitter so the scrollbar works correctly.
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
}
