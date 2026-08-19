using System.Threading.Tasks;
using TMPro;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════
//  Player Stats Panel
//
//  Displays the local player's lifetime poker statistics.
//  On the first OnEnable it queries the storage backend (Firebase /
//  Cloud Save) for PlayerStats and populates the text fields.
//  Subsequent opens use the cached result unless ForceRefresh()
//  is called.  The panel also detects user-identity changes
//  (e.g. stale anonymous → real login) and re-queries automatically.
//
//  Setup:
//    1. Create a panel in the lobby UI.
//    2. Add TextMeshProUGUI elements for each stat.
//    3. Drag them onto the matching serialized fields.
//
//  All text references are optional — leave any field unassigned
//  and it will simply be skipped.
// ═══════════════════════════════════════════════════════════════════

public class PlayerStatsPanel : MonoBehaviour
{
    [Header("Overall")]
    [SerializeField] private TextMeshProUGUI totalHandsText;
    [SerializeField] private TextMeshProUGUI handsWonText;
    [SerializeField] private TextMeshProUGUI winRateText;
    [SerializeField] private TextMeshProUGUI netProfitText;
    [SerializeField] private TextMeshProUGUI biggestPotWonText;

    [Header("Preflop")]
    [SerializeField] private TextMeshProUGUI vpipText;
    [SerializeField] private TextMeshProUGUI pfrText;

    [Header("Aggression")]
    [SerializeField] private TextMeshProUGUI aggressionFactorText;
    [SerializeField] private TextMeshProUGUI aggressionBetsText;
    [SerializeField] private TextMeshProUGUI aggressionCallsText;

    [Header("Showdown")]
    [SerializeField] private TextMeshProUGUI wtsdText;
    [SerializeField] private TextMeshProUGUI wsdText;
    [SerializeField] private TextMeshProUGUI showdownsReachedText;
    [SerializeField] private TextMeshProUGUI showdownsWonText;

    // ── Internal state ────────────────────────────────────────────
    private IHandHistoryStorage storage;
    private PlayerStats cachedStats;
    private bool hasQueried;
    private bool isLoading;
    private string queriedForUid;

    // ══════════════════════════════════════════════════════════════
    //  Unity lifecycle
    // ══════════════════════════════════════════════════════════════

    private void OnEnable()
    {
        // If the signed-in user changed since our last query, invalidate cache
        if (hasQueried)
        {
            string currentUid = GetLocalPlayerUid();
            if (!string.IsNullOrEmpty(currentUid) && currentUid != queriedForUid)
            {
                hasQueried = false;
                cachedStats = null;
                if (storage != null) storage.ClearCaches();
            }
        }

        if (!hasQueried && !isLoading)
            _ = LoadAndPopulateAsync();
        else if (hasQueried)
            PopulateUI();
    }

    private void Update()
    {
        // Lazy retry — keeps attempting if the backend wasn't ready yet
        if (!hasQueried && !isLoading)
            _ = LoadAndPopulateAsync();
    }

    // ══════════════════════════════════════════════════════════════
    //  Public API
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Clears the cached stats and re-queries the backend.
    /// </summary>
    public void ForceRefresh()
    {
        cachedStats = null;
        hasQueried = false;
        queriedForUid = null;

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
                Debug.LogWarning($"[{LogTag.Stats}] Player stats load skipped — reason=no storage backend available", this);
                return;
            }

            if (!IsStorageReady())
                return;

            cachedStats = await storage.LoadPlayerStatsAsync();
            hasQueried = true;
            queriedForUid = GetLocalPlayerUid();

            if (gameObject.activeInHierarchy)
                PopulateUI();
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[{LogTag.Stats}] Player stats load failed — error={e.Message}", this);
        }
        finally
        {
            isLoading = false;
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  Population
    // ══════════════════════════════════════════════════════════════

    private void PopulateUI()
    {
        if (cachedStats == null)
        {
            ClearUI();
            return;
        }

        PlayerStats s = cachedStats;

        // Overall
        SetText(totalHandsText,    s.totalHands.ToString("N0"));
        SetText(handsWonText,      s.handsWon.ToString("N0"));
        SetText(winRateText,       $"{s.WinRate:F1}%");
        SetText(biggestPotWonText, FormatCurrency(s.biggestPotWon));

        // Net profit — colour green/red
        if (netProfitText != null)
        {
            netProfitText.text = FormatCurrency(s.netProfit);
            netProfitText.color = s.netProfit >= 0f
                ? new Color32(0x00, 0xC8, 0x53, 0xFF)   // green
                : new Color32(0xE5, 0x39, 0x35, 0xFF);  // red
        }

        // Preflop
        SetText(vpipText, $"{s.VPIP:F1}%");
        SetText(pfrText,  $"{s.PFR:F1}%");

        // Aggression
        SetText(aggressionFactorText, $"{s.AF:F2}");
        SetText(aggressionBetsText,   s.aggressionBets.ToString("N0"));
        SetText(aggressionCallsText,  s.aggressionCalls.ToString("N0"));

        // Showdown
        SetText(wtsdText,             $"{s.WTSD:F1}%");
        SetText(wsdText,              $"{s.WSD:F1}%");
        SetText(showdownsReachedText, s.showdownsReached.ToString("N0"));
        SetText(showdownsWonText,     s.showdownsWon.ToString("N0"));
    }

    private void ClearUI()
    {
        SetText(totalHandsText,       "—");
        SetText(handsWonText,         "—");
        SetText(winRateText,          "—");
        SetText(netProfitText,        "—");
        SetText(biggestPotWonText,    "—");
        SetText(vpipText,            "—");
        SetText(pfrText,             "—");
        SetText(aggressionFactorText, "—");
        SetText(aggressionBetsText,   "—");
        SetText(aggressionCallsText,  "—");
        SetText(wtsdText,            "—");
        SetText(wsdText,             "—");
        SetText(showdownsReachedText, "—");
        SetText(showdownsWonText,    "—");
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

    private bool IsStorageReady()
    {
        BackendType backend = LobbyManager.Instance != null
            ? LobbyManager.Instance.Backend
            : BackendType.Firebase;

        if (backend == BackendType.Firebase)
            return FirebaseManager.Instance != null && FirebaseManager.Instance.IsFirebaseReady;

        return Unity.Services.Core.UnityServices.State == Unity.Services.Core.ServicesInitializationState.Initialized
            && Unity.Services.Authentication.AuthenticationService.Instance.IsSignedIn;
    }

    private static string GetLocalPlayerUid()
    {
        if (FirebaseManager.Instance != null && !string.IsNullOrEmpty(FirebaseManager.Instance.UserId))
            return FirebaseManager.Instance.UserId;

        if (Unity.Services.Authentication.AuthenticationService.Instance != null &&
            Unity.Services.Authentication.AuthenticationService.Instance.IsSignedIn)
            return Unity.Services.Authentication.AuthenticationService.Instance.PlayerId;

        return "";
    }

    private static void SetText(TextMeshProUGUI tmp, string value)
    {
        if (tmp != null) tmp.text = value;
    }

    private static string FormatCurrency(float value)
    {
        string sign = value >= 0f ? "" : "-";
        return $"{sign}${Mathf.Abs(value):N0}";
    }
}
