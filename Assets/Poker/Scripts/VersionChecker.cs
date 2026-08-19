using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Firebase;
using Firebase.Extensions;
using Firebase.RemoteConfig;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Cross-platform (Android + iOS) forced-update gate driven by Firebase Remote Config.
///
/// Instead of scraping the public store page — which is unreliable (Play Store
/// hides the version for App-Bundle apps, and neither store exposes the version
/// of closed-testing / TestFlight tracks) — this reads the minimum required
/// version from a Remote Config parameter you control and compares it against
/// <see cref="Application.version"/>. If the installed build is older than the
/// configured minimum, a full-screen blocker is shown with a button that opens
/// the correct store listing.
///
/// Because YOU set the required version in the Firebase console, this works on
/// every track, including closed testing and TestFlight, on both platforms.
///
/// Remote Config setup (Firebase console → Remote Config):
///   • Add a parameter named "version" (configurable via <see cref="versionParameterKey"/>).
///   • Set its value to the MINIMUM version users must have, e.g. "1.2.0".
///   • To require different minimums per platform, add a Condition on
///     "Platform == Android" / "Platform == iOS" and serve per-platform values
///     from the same "version" parameter.
///
/// In-app default: this script registers <see cref="Application.version"/> as the
/// in-app default for the parameter, so if the fetch fails or the parameter is
/// absent, the check fails open (never blocks the user).
///
/// Inspector setup:
///   • updateBlocker     – root GameObject (full-screen overlay, inactive by default)
///   • openStoreButton   – Button inside the blocker that opens the store page
///   • versionParameterKey – Remote Config key holding the min version ("version")
///   • store URL fallbacks – used to open the correct store listing
/// </summary>
public class VersionChecker : MonoBehaviour
{
    [Header("UI")]
    [Tooltip("Full-screen overlay that blocks interaction. Inactive by default.")]
    [SerializeField] private GameObject updateBlocker;

    [Tooltip("Button that opens the store listing for this app.")]
    [SerializeField] private Button openStoreButton;

    [Header("Remote Config")]
    [Tooltip("Remote Config parameter key holding the minimum required version (e.g. \"version\").")]
    [SerializeField] private string versionParameterKey = "version";

    [Tooltip("How long a cached fetch stays fresh (seconds). Production: 3600+. Set low (e.g. 0) only while testing — Remote Config throttles frequent fetches.")]
    [SerializeField] private long minimumFetchIntervalSeconds = 3600;

    [Tooltip("TESTING ONLY: also run the version check inside the Unity Editor. Leave OFF for production.")]
    [SerializeField] private bool checkInEditor = false;

    [Header("Store URLs")]
    [Tooltip("Android Play Store listing. Leave empty to derive from the package name.")]
    [SerializeField] private string androidStoreUrl;

    [Tooltip("iOS App Store listing, e.g. https://apps.apple.com/app/id000000000")]
    [SerializeField] private string iosStoreUrl;

    // Resolved at runtime; used by the OpenStore button.
    private string resolvedStoreUrl;

    // ════════════════════════════════════════════════════════════════
    //  Lifecycle
    // ════════════════════════════════════════════════════════════════

    private void Awake()
    {
        if (updateBlocker != null)
            updateBlocker.SetActive(false);

        resolvedStoreUrl = DefaultStoreUrl();

        if (openStoreButton != null)
            openStoreButton.onClick.AddListener(OpenStore);
    }

    private void Start()
    {
#if UNITY_EDITOR
        if (checkInEditor)
            StartCoroutine(CheckVersionRoutine());
#elif UNITY_ANDROID || UNITY_IOS
        StartCoroutine(CheckVersionRoutine());
#endif
    }

    // ════════════════════════════════════════════════════════════════
    //  Version Check (Remote Config)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Waits for Firebase dependency resolution to finish — owned by
    /// <see cref="FirebaseManager"/> — BEFORE touching any Firebase API.
    ///
    /// We must NOT call <c>CheckAndFixDependenciesAsync</c> ourselves: Firebase
    /// throws "Don't call other Firebase functions while CheckDependencies is
    /// running" if two dependency checks overlap, which is exactly what happened
    /// when this ran concurrently with FirebaseManager's startup on device.
    /// </summary>
    private IEnumerator CheckVersionRoutine()
    {
        Debug.Log($"[{LogTag.Version}] Starting version check — installed={Application.version}, key='{versionParameterKey}', fetchInterval={minimumFetchIntervalSeconds}s", this);

        const float timeoutSeconds = 30f;
        float elapsed = 0f;

        // Wait for FirebaseManager to exist (it owns dependency resolution).
        while (FirebaseManager.Instance == null && elapsed < timeoutSeconds)
        {
            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }

        if (FirebaseManager.Instance != null)
        {
            // Wait for its CheckAndFixDependenciesAsync to complete. Calling any
            // Firebase function before this would throw on device.
            while (!FirebaseManager.Instance.DependenciesResolved && elapsed < timeoutSeconds)
            {
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            if (!FirebaseManager.Instance.DependenciesResolved)
            {
                Debug.LogWarning($"[{LogTag.Version}] Firebase deps not resolved after {timeoutSeconds}s — skipping version check (fail open)", this);
                yield break;
            }

            OnFirebaseReady();
        }
        else
        {
            // No FirebaseManager in this context — there is nothing to race with,
            // so it is safe to resolve dependencies ourselves.
            Debug.LogWarning($"[{LogTag.Version}] FirebaseManager not found — resolving dependencies directly", this);
            FirebaseApp.CheckAndFixDependenciesAsync().ContinueWithOnMainThread(task =>
            {
                if (task.IsFaulted || task.IsCanceled)
                {
                    Debug.LogWarning($"[{LogTag.Version}] Version check skipped — dependency check failed: {task.Exception?.Flatten().Message}", this);
                    return;
                }

                if (task.Result != DependencyStatus.Available)
                {
                    Debug.LogWarning($"[{LogTag.Version}] Version check skipped — Firebase not available: {task.Result}", this);
                    return;
                }

                OnFirebaseReady();
            });
        }
    }

    private void OnFirebaseReady()
    {
        // Confirm the device is talking to the project where you published the
        // parameter. A mismatch here means a wrong/missing google-services config.
        try
        {
            var opts = FirebaseApp.DefaultInstance.Options;
            Debug.Log($"[{LogTag.Version}] Firebase ready — projectId={opts.ProjectId}, appId={opts.AppId}", this);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[{LogTag.Version}] Could not read Firebase options: {e.Message}", this);
        }

        InitializeRemoteConfig();
    }

    private void InitializeRemoteConfig()
    {
        FirebaseRemoteConfig remoteConfig = FirebaseRemoteConfig.DefaultInstance;

        // The in-app default is ONLY used on the very first launch, before any
        // successful fetch has cached a real value on disk. We set it to the
        // current build so an unverified first launch fails open (never blocks).
        var defaults = new Dictionary<string, object>
        {
            { versionParameterKey, Application.version }
        };

        remoteConfig.SetDefaultsAsync(defaults).ContinueWithOnMainThread(_ =>
        {
            // 1) Decide immediately using the value already persisted on disk
            //    from a previous successful fetch. This works offline and when
            //    throttled, so the result no longer flickers between launches.
            //    On a brand-new install there is no cache yet → in-app default
            //    → fails open until the background refresh below completes.
            EvaluateVersion(remoteConfig, "cached");

            // 2) Refresh from the server to update the on-disk cache for the
            //    next launch (and possibly this one). This can only ADD a
            //    blocker — it never hides one already shown.
            RefreshFromServer(remoteConfig);
        });
    }

    private void RefreshFromServer(FirebaseRemoteConfig remoteConfig)
    {
        TimeSpan cacheExpiration = TimeSpan.FromSeconds(minimumFetchIntervalSeconds);

        remoteConfig.FetchAsync(cacheExpiration).ContinueWithOnMainThread(fetchTask =>
        {
            var info = remoteConfig.Info;
            Debug.Log($"[{LogTag.Version}] Fetch complete — faulted={fetchTask.IsFaulted}, canceled={fetchTask.IsCanceled}, lastFetchStatus={info.LastFetchStatus}, fetchTime={info.FetchTime}", this);

            if (fetchTask.IsFaulted || fetchTask.IsCanceled)
            {
                // Cached decision from step 1 stands — nothing else to do.
                Debug.LogWarning($"[{LogTag.Version}] Remote Config fetch failed — keeping cached decision: {fetchTask.Exception?.Flatten().Message}", this);
                return;
            }

            remoteConfig.ActivateAsync().ContinueWithOnMainThread(activateTask =>
            {
                bool newConfigActivated = !activateTask.IsFaulted && activateTask.Result;
                Debug.Log($"[{LogTag.Version}] Activate complete — newConfigActivated={(activateTask.IsFaulted ? "ERROR" : newConfigActivated.ToString())}", this);

                // Re-evaluate with the freshly fetched value. EvaluateVersion
                // only ever SHOWS the blocker, so this safely upgrades the
                // decision (e.g. first install once it finally gets the value)
                // without un-blocking anyone.
                EvaluateVersion(remoteConfig, "fresh");
            });
        });
    }

    /// <summary>
    /// Reads the current value of the version parameter and shows the blocker if
    /// the installed build is older. Never hides the blocker, so it is safe to
    /// call multiple times (cached pass, then fresh pass).
    /// </summary>
    private void EvaluateVersion(FirebaseRemoteConfig remoteConfig, string phase)
    {
        ConfigValue configValue = remoteConfig.GetValue(versionParameterKey);
        string minVersion = configValue.StringValue;

        // Source tells us where the value came from:
        //   RemoteValue  → fetched from the Firebase server (persisted cache)
        //   DefaultValue → the in-app default (no cached value yet)
        //   StaticValue  → no value at all (key unknown / never set)
        Debug.Log($"[{LogTag.Version}] [{phase}] Read '{versionParameterKey}'=\"{minVersion}\" source={configValue.Source}", this);

        if (string.IsNullOrEmpty(minVersion))
        {
            Debug.LogWarning($"[{LogTag.Version}] [{phase}] '{versionParameterKey}' is empty — allowing user to continue", this);
            return;
        }

        if (IsOutdated(Application.version, minVersion))
        {
            Debug.Log($"[{LogTag.Version}] [{phase}] Update required — installed={Application.version}, minRequired={minVersion}", this);
            ShowBlocker();
        }
        else
        {
            Debug.Log($"[{LogTag.Version}] [{phase}] App is up to date — installed={Application.version}, minRequired={minVersion}", this);
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Version Comparison
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns true when <paramref name="installed"/> is strictly older than
    /// <paramref name="minRequired"/>.  Compares each numeric segment left-to-right.
    /// Example: "1.2.3" vs "1.3.0" → true (outdated).
    /// </summary>
    private static bool IsOutdated(string installed, string minRequired)
    {
        if (string.IsNullOrEmpty(installed) || string.IsNullOrEmpty(minRequired))
            return false;

        int[] installedParts = ParseVersionParts(installed);
        int[] requiredParts = ParseVersionParts(minRequired);

        int length = Mathf.Max(installedParts.Length, requiredParts.Length);
        for (int i = 0; i < length; i++)
        {
            int a = i < installedParts.Length ? installedParts[i] : 0;
            int b = i < requiredParts.Length ? requiredParts[i] : 0;

            if (a < b) return true;   // installed is older
            if (a > b) return false;  // installed is newer (dev build?)
        }

        return false; // identical
    }

    private static int[] ParseVersionParts(string version)
    {
        // Strip any non-numeric prefix/suffix (e.g. "v1.2.3-beta")
        var cleaned = Regex.Replace(version, @"[^0-9.]", "").Trim('.');
        string[] segments = cleaned.Split('.');
        int[] parts = new int[segments.Length];
        for (int i = 0; i < segments.Length; i++)
            int.TryParse(segments[i], out parts[i]);
        return parts;
    }

    // ════════════════════════════════════════════════════════════════
    //  UI
    // ════════════════════════════════════════════════════════════════

    private void ShowBlocker()
    {
        if (updateBlocker != null)
            updateBlocker.SetActive(true);
    }

    private void OpenStore()
    {
        string url = !string.IsNullOrEmpty(resolvedStoreUrl) ? resolvedStoreUrl : DefaultStoreUrl();

        if (string.IsNullOrEmpty(url))
        {
            Debug.LogWarning($"[{LogTag.Version}] Open Store pressed but no store URL is set for this platform.", this);
            return;
        }

        Debug.Log($"[{LogTag.Version}] Opening store URL: {url}", this);
        Application.OpenURL(url);
    }

    /// <summary>
    /// Platform-appropriate store URL for the OpenStore button.
    /// </summary>
    private string DefaultStoreUrl()
    {
#if UNITY_IOS
        return iosStoreUrl;
#else
        if (!string.IsNullOrEmpty(androidStoreUrl))
            return androidStoreUrl;
        return $"https://play.google.com/store/apps/details?id={Application.identifier}";
#endif
    }
}
