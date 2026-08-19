using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Firebase;
using Firebase.Auth;
using Firebase.Firestore;
using Unity.Services.Authentication;
using Unity.Services.Core;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════
//  Firebase Manager
//
//  MonoBehaviour singleton that handles:
//    • Firebase App dependency resolution
//    • Firebase Anonymous / Email-Password Authentication
//    • Unity Services anonymous sign-in (required for Lobby/Relay)
//    • Player profile data persistence via Firestore
//    • Exposes Firestore reference for FirestoreHandHistory
//
//  Created automatically by LobbyManager when BackendType.Firebase
//  is selected.  Lives as a child of the LobbyManager GameObject
//  so it persists across scenes via DontDestroyOnLoad.
//
//  Firestore player document layout:
//    /players/{uid}  →  { displayName, characterId, isGuest,
//                          createdAt, lastSeen, stats: {...} }
//
//  The "stats" map is written by FirestoreHandHistory, not here.
//  We use SetAsync with MergeAll so neither side overwrites the
//  other's fields.
// ═══════════════════════════════════════════════════════════════════

public class FirebaseManager : MonoBehaviour, IAuthProvider
{
    public static FirebaseManager Instance { get; private set; }

    // ── Firebase state ───────────────────────────────────────────
    private bool dependenciesResolved;
    private bool initializingDeps;

    // ── Firestore player document fields ─────────────────────────
    private const string COL_PLAYERS        = "players";
    private const string FIELD_DISPLAY_NAME = "displayName";
    private const string FIELD_CHARACTER_ID = "characterId";
    private const string FIELD_IS_GUEST     = "isGuest";
    private const string FIELD_CREATED_AT   = "createdAt";
    private const string FIELD_LAST_SEEN    = "lastSeen";

    // ── PlayerPrefs cache keys ───────────────────────────────────
    private const string PREF_DISPLAY_NAME = "fb_cachedDisplayName";
    private const string PREF_CHARACTER_ID = "fb_cachedCharacterId";

    // ── Sync guard (prevents save-during-load loops) ─────────────
    private bool isSyncing;

    // ══════════════════════════════════════════════════════════════
    //  Public API — used by FirestoreHandHistory and others
    // ══════════════════════════════════════════════════════════════

    /// <summary>True when Firebase deps are resolved and a user is signed in.</summary>
    public bool IsFirebaseReady =>
        dependenciesResolved
        && FirebaseAuth.DefaultInstance.CurrentUser != null;

    /// <summary>
    /// True once <see cref="FirebaseApp.CheckAndFixDependenciesAsync"/> has
    /// completed successfully. Unlike <see cref="IsFirebaseReady"/> this does
    /// NOT require a signed-in user, so callers that only need Firebase services
    /// (e.g. Remote Config) can wait on this without waiting for login. Callers
    /// MUST wait for this before invoking any Firebase function, because calling
    /// Firebase while the dependency check is still running throws.
    /// </summary>
    public bool DependenciesResolved => dependenciesResolved;

    /// <summary>Shortcut to FirebaseFirestore.DefaultInstance.</summary>
    public FirebaseFirestore Firestore => FirebaseFirestore.DefaultInstance;

    // ══════════════════════════════════════════════════════════════
    //  IAuthProvider
    // ══════════════════════════════════════════════════════════════

    public bool IsAuthenticated =>
        dependenciesResolved
        && FirebaseAuth.DefaultInstance.CurrentUser != null
        && UnityServices.State == ServicesInitializationState.Initialized
        && AuthenticationService.Instance.IsSignedIn;

    public string UserId =>
        FirebaseAuth.DefaultInstance?.CurrentUser?.UserId ?? "";

    // ══════════════════════════════════════════════════════════════
    //  Unity lifecycle
    // ══════════════════════════════════════════════════════════════

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    private async void Start()
    {
        await EnsureFirebaseDepsAsync();
    }
    // ══════════════════════════════════════════════════════════════
    //  Internal initialisation helpers
    // ══════════════════════════════════════════════════════════════

    private async Task EnsureFirebaseDepsAsync()
    {
        if (dependenciesResolved) return;
        if (initializingDeps) return;
        initializingDeps = true;

        try
        {
            DependencyStatus status = await FirebaseApp.CheckAndFixDependenciesAsync();
            if (status != DependencyStatus.Available)
            {
                Debug.LogError($"[{LogTag.Auth}] Firebase dependency resolution failed — status={status}", this);
                return;
            }

            dependenciesResolved = true;
            Debug.Log($"[{LogTag.Auth}] Firebase dependencies resolved", this);

#if UNITY_EDITOR
            FirebaseFirestore.DefaultInstance.Settings.PersistenceEnabled = false;
            Debug.Log($"[{LogTag.Auth}] Firestore persistence disabled — mode=editor", this);
#endif
        }
        finally
        {
            initializingDeps = false;
        }
    }

    private async Task EnsureUnityServicesAsync(string playerProfile)
    {
        if (UnityServices.State != ServicesInitializationState.Initialized)
        {
            var options = new InitializationOptions();
            options.SetProfile(SanitizeProfileName(playerProfile));
            await UnityServices.InitializeAsync(options);
        }

        if (!AuthenticationService.Instance.IsSignedIn)
        {
            await AuthenticationService.Instance.SignInAnonymouslyAsync();
            Debug.Log($"[{LogTag.Auth}] Unity Auth anonymous sign-in completed — playerId={AuthenticationService.Instance.PlayerId}", this);
        }
    }

    /// <summary>
    /// Strips characters not allowed in a Unity Services profile name
    /// ([a-zA-Z0-9_-], max 30 chars) and falls back to "default" if the
    /// result would be empty (e.g. when an email address is passed in).
    /// </summary>
    private static string SanitizeProfileName(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "default";

        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-')
                sb.Append(c);
        }

        string sanitized = sb.Length > 30 ? sb.ToString(0, 30) : sb.ToString();
        return sanitized.Length > 0 ? sanitized : "default";
    }

    // ══════════════════════════════════════════════════════════════
    //  IAuthProvider — Sign-in methods
    // ══════════════════════════════════════════════════════════════

    public async Task SignInAsGuestAsync(string playerProfile)
    {
        await EnsureUnityServicesAsync(playerProfile);
        await EnsureFirebaseDepsAsync();

        if (!dependenciesResolved)
            throw new Exception("Firebase dependencies not available.");

        FirebaseAuth auth = FirebaseAuth.DefaultInstance;

#if UNITY_EDITOR
        // MPPM virtual players are separate processes that share the same
        // project folder.  Firebase Auth persists the anonymous token to disk,
        // so the second clone picks up the first clone's session.
        // Sign out any stale persisted session so each process gets its own
        // anonymous identity.  In builds each device is fully isolated.
        if (auth.CurrentUser != null)
        {
            Debug.Log($"[{LogTag.Auth}] Clearing persisted session for fresh guest sign-in — uid={auth.CurrentUser.UserId}, mode=editor", this);
            auth.SignOut();
        }
#endif

        if (auth.CurrentUser == null)
        {
            AuthResult result = await auth.SignInAnonymouslyAsync();
            Debug.Log($"[{LogTag.Auth}] Firebase guest sign-in completed — uid={result.User.UserId}", this);
        }
        else
        {
            Debug.Log($"[{LogTag.Auth}] Firebase session already active — uid={auth.CurrentUser.UserId}", this);
        }

        // Ensure the player document exists with isGuest = true
        await EnsurePlayerDocumentAsync(isGuest: true);
    }

    public async Task<string> LoginAsync(string playerProfile, string email, string password)
    {
        try
        {
            await EnsureUnityServicesAsync(playerProfile);
            await EnsureFirebaseDepsAsync();
            if (!dependenciesResolved) return "Firebase not available.";

            FirebaseAuth auth = FirebaseAuth.DefaultInstance;

            if (auth.CurrentUser != null && auth.CurrentUser.IsAnonymous)
                auth.SignOut();

            AuthResult result = await auth.SignInWithEmailAndPasswordAsync(email, password);
            Debug.Log($"[{LogTag.Auth}] Login successful — uid={result.User.UserId}", this);

            // Update lastSeen and load profile
            await EnsurePlayerDocumentAsync(isGuest: false);
            await LoadProfileDataAsync();

            return null;
        }
        catch (FirebaseException ex)
        {
            Debug.LogError($"[{LogTag.Auth}] Login failed — error={ex.Message}", this);
            return ex.Message;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[{LogTag.Auth}] Login error — error={ex.Message}", this);
            return ex.Message;
        }
    }

    public async Task<string> RegisterAsync(string playerProfile, string username,
        string email, string password)
    {
        try
        {
            await EnsureUnityServicesAsync(playerProfile);
            await EnsureFirebaseDepsAsync();
            if (!dependenciesResolved) return "Firebase not available.";

            FirebaseAuth auth = FirebaseAuth.DefaultInstance;

            if (auth.CurrentUser != null && auth.CurrentUser.IsAnonymous)
                auth.SignOut();

            AuthResult result = await auth.CreateUserWithEmailAndPasswordAsync(email, password);
            Debug.Log($"[{LogTag.Auth}] Registration successful — uid={result.User.UserId}", this);

            // Create the full player document
            string displayName = string.IsNullOrEmpty(username)
                ? PlayerNameData.PlayerName
                : username;
            int characterId = PlayerAssets.Instance != null
                ? PlayerAssets.Instance.currentCharacterId : 0;

            await CreatePlayerDocumentAsync(displayName, characterId, isGuest: false);

            return null;
        }
        catch (FirebaseException ex)
        {
            Debug.LogError($"[{LogTag.Auth}] Registration failed — error={ex.Message}", this);
            return ex.Message;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[{LogTag.Auth}] Registration error — error={ex.Message}", this);
            return ex.Message;
        }
    }

    public void SignOut()
    {
        FirebaseAuth auth = FirebaseAuth.DefaultInstance;
        if (auth.CurrentUser != null)
        {
            auth.SignOut();
            Debug.Log($"[{LogTag.Auth}] Firebase signed out", this);
        }
    }

    public async Task<string> DeleteAccountAsync()
    {
        try
        {
            FirebaseAuth auth = FirebaseAuth.DefaultInstance;
            if (auth.CurrentUser == null)
                return "No user is currently signed in.";

            await auth.CurrentUser.DeleteAsync();
            Debug.Log($"[{LogTag.Auth}] Firebase account deleted", this);
            return null;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[{LogTag.Auth}] Account deletion failed — error={ex.Message}", this);
            return ex.Message;
        }
    }

    /// <summary>
    /// Silently restores a persisted Firebase email/password session from a
    /// previous launch without requiring the user to type credentials again.
    ///
    /// Flow:
    ///   1. Resolve Firebase dependencies.
    ///   2. If CurrentUser exists AND is not anonymous → session is still valid.
    ///   3. Initialise Unity Services (needed for Lobby/Relay).
    ///   4. Refresh lastSeen in Firestore and load displayName + characterId.
    ///   5. Return true so the caller can skip the auth screen.
    ///
    /// Returns false if no valid session exists, so the login UI is shown.
    /// </summary>
    public async Task<bool> TryResumeSessionAsync(string playerProfile)
    {
        try
        {
            await EnsureFirebaseDepsAsync();
            if (!dependenciesResolved)
            {
                Debug.Log($"[{LogTag.Auth}] Session resume skipped — reason=depsNotAvailable", this);
                return false;
            }

            FirebaseAuth auth = FirebaseAuth.DefaultInstance;

            // Only resume for non-anonymous (i.e. real email/password) accounts.
            if (auth.CurrentUser == null || auth.CurrentUser.IsAnonymous)
            {
                Debug.Log($"[{LogTag.Auth}] Session resume skipped — reason=noPersistedSession", this);
                return false;
            }

            Debug.Log($"[{LogTag.Auth}] Resuming persisted session — uid={auth.CurrentUser.UserId}", this);

            // Unity Services must be running for Lobby/Relay to work.
            await EnsureUnityServicesAsync(playerProfile);

            // Refresh lastSeen and load profile data.
            await EnsurePlayerDocumentAsync(isGuest: false);
            await LoadProfileDataAsync();

            Debug.Log($"[{LogTag.Auth}] Session resumed successfully", this);
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[{LogTag.Auth}] Session resume failed — error={ex.Message}", this);
            return false;
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  Player document — Firestore persistence
    //
    //  All profile data lives at:  /players/{uid}
    //  Stats map lives alongside:  /players/{uid}.stats
    //  (stats are written by FirestoreHandHistory, not here)
    // ══════════════════════════════════════════════════════════════

    private DocumentReference PlayerDoc()
    {
        return Firestore
            .Collection(COL_PLAYERS)
            .Document(UserId);
    }

    /// <summary>
    /// Ensures the player document exists.
    /// For new players: creates with createdAt + lastSeen.
    /// For returning players: updates lastSeen only.
    /// </summary>
    private async Task EnsurePlayerDocumentAsync(bool isGuest)
    {
        if (!IsFirebaseReady) return;

        try
        {
            var docRef = PlayerDoc();
            DocumentSnapshot snap = await docRef.GetSnapshotAsync();

            if (!snap.Exists)
            {
                // New player — create full document
                string displayName = PlayerNameData.PlayerName;
                await docRef.SetAsync(new Dictionary<string, object>
                {
                    { FIELD_DISPLAY_NAME, displayName },
                    { FIELD_IS_GUEST,     isGuest },
                    { FIELD_CHARACTER_ID, 0 },
                    { FIELD_CREATED_AT,   FieldValue.ServerTimestamp },
                    { FIELD_LAST_SEEN,    FieldValue.ServerTimestamp },
                });
                Debug.Log($"[{LogTag.Auth}] Created player document — uid={UserId}", this);
            }
            else
            {
                // Existing player — update lastSeen + isGuest
                await docRef.SetAsync(new Dictionary<string, object>
                {
                    { FIELD_IS_GUEST,  isGuest },
                    { FIELD_LAST_SEEN, FieldValue.ServerTimestamp },
                }, SetOptions.MergeAll);
                Debug.Log($"[{LogTag.Auth}] Updated lastSeen — uid={UserId}", this);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[{LogTag.Auth}] Failed to ensure player document — uid={UserId}, error={e.Message}", this);
        }
    }

    /// <summary>
    /// Creates a fresh player document (used after registration).
    /// </summary>
    private async Task CreatePlayerDocumentAsync(string displayName, int characterId, bool isGuest)
    {
        // Local cache
        PlayerPrefs.SetString(GetPrefKey(PREF_DISPLAY_NAME), displayName);
        PlayerPrefs.SetInt(GetPrefKey(PREF_CHARACTER_ID), characterId);
        PlayerPrefs.Save();

        if (!IsFirebaseReady) return;

        try
        {
            await PlayerDoc().SetAsync(new Dictionary<string, object>
            {
                { FIELD_DISPLAY_NAME, displayName },
                { FIELD_CHARACTER_ID, characterId },
                { FIELD_IS_GUEST,     isGuest },
                { FIELD_CREATED_AT,   FieldValue.ServerTimestamp },
                { FIELD_LAST_SEEN,    FieldValue.ServerTimestamp },
            });
            Debug.Log($"[{LogTag.Auth}] Created player document — name={displayName}, characterId={characterId}, isGuest={isGuest}", this);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[{LogTag.Auth}] Failed to create player document — error={e.Message}", this);
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  Profile field updates
    // ══════════════════════════════════════════════════════════════

    private string GetPrefKey(string baseKey)
    {
        string uid = UserId;
        return string.IsNullOrEmpty(uid)
            ? $"{baseKey}_anonymous"
            : $"{baseKey}_{uid}";
    }

    public async Task SaveDisplayNameAsync(string displayName)
    {
        if (isSyncing) return;

        PlayerPrefs.SetString(GetPrefKey(PREF_DISPLAY_NAME), displayName);
        PlayerPrefs.Save();

        if (!IsFirebaseReady) return;

        try
        {
            await PlayerDoc().SetAsync(
                new Dictionary<string, object> { { FIELD_DISPLAY_NAME, displayName } },
                SetOptions.MergeAll);
            Debug.Log($"[{LogTag.Auth}] Saved displayName — name={displayName}", this);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[{LogTag.Auth}] Failed to save displayName — error={e.Message}", this);
        }
    }

    public async Task SaveCharacterIdAsync(int characterId)
    {
        if (isSyncing) return;

        PlayerPrefs.SetInt(GetPrefKey(PREF_CHARACTER_ID), characterId);
        PlayerPrefs.Save();

        if (!IsFirebaseReady) return;

        try
        {
            await PlayerDoc().SetAsync(
                new Dictionary<string, object> { { FIELD_CHARACTER_ID, characterId } },
                SetOptions.MergeAll);
            Debug.Log($"[{LogTag.Auth}] Saved characterId — characterId={characterId}", this);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[{LogTag.Auth}] Failed to save characterId — error={e.Message}", this);
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  Profile loading
    // ══════════════════════════════════════════════════════════════

    private async Task<bool> LoadProfileDataAsync()
    {
        isSyncing = true;

        if (!IsFirebaseReady)
        {
            LoadFromLocalCache();
            isSyncing = false;
            return false;
        }

        try
        {
            DocumentSnapshot snap = await PlayerDoc().GetSnapshotAsync();
            if (!snap.Exists)
            {
                Debug.Log($"[{LogTag.Auth}] No player document found — uid={UserId}", this);
                isSyncing = false;
                return false;
            }

            bool hasData = false;

            if (snap.ContainsField(FIELD_DISPLAY_NAME))
            {
                string name = snap.GetValue<string>(FIELD_DISPLAY_NAME);
                if (!string.IsNullOrEmpty(name))
                {
                    PlayerNameData.SetPlayerName(name);
                    PlayerPrefs.SetString(GetPrefKey(PREF_DISPLAY_NAME), name);
                    hasData = true;
                    Debug.Log($"[{LogTag.Auth}] Loaded displayName — name={name}, source=Firestore", this);
                }
            }

            if (snap.ContainsField(FIELD_CHARACTER_ID))
            {
                long charIdLong = snap.GetValue<long>(FIELD_CHARACTER_ID);
                int charId = (int)charIdLong;
                if (PlayerAssets.Instance != null)
                    PlayerAssets.Instance.currentCharacterId = charId;
                PlayerPrefs.SetInt(GetPrefKey(PREF_CHARACTER_ID), charId);
                hasData = true;
                Debug.Log($"[{LogTag.Auth}] Loaded characterId — characterId={charId}, source=Firestore", this);
            }

            PlayerPrefs.Save();
            return hasData;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[{LogTag.Auth}] Failed to load profile — error={e.Message}", this);
            LoadFromLocalCache();
            return false;
        }
        finally
        {
            isSyncing = false;
        }
    }

    private void LoadFromLocalCache()
    {
        string cachedName = PlayerPrefs.GetString(GetPrefKey(PREF_DISPLAY_NAME), "");
        if (!string.IsNullOrEmpty(cachedName))
            PlayerNameData.SetPlayerName(cachedName);

        int cachedChar = PlayerPrefs.GetInt(GetPrefKey(PREF_CHARACTER_ID), 0);
        if (PlayerAssets.Instance != null)
            PlayerAssets.Instance.currentCharacterId = cachedChar;

        Debug.Log($"[{LogTag.Auth}] Loaded from local cache — name={cachedName}, characterId={cachedChar}", this);
    }
}
