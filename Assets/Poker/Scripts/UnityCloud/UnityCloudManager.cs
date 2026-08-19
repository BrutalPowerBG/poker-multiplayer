using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Services.Authentication;
using Unity.Services.CloudSave;
using Unity.Services.Core;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════
//  Unity Cloud Manager
//
//  MonoBehaviour singleton that handles:
//    • Unity Services initialisation
//    • Unity Authentication (anonymous / username-password)
//    • Player profile data persistence via Unity Cloud Save
//
//  Created automatically by LobbyManager when BackendType.UnityServices
//  is selected.  Lives as a child of the LobbyManager GameObject
//  so it persists across scenes via DontDestroyOnLoad.
//
//  This replaces the former CloudSaveManager with identical
//  functionality, plus authentication (previously in LobbyManager).
// ═══════════════════════════════════════════════════════════════════

public class UnityCloudManager : MonoBehaviour, IAuthProvider
{
    public static UnityCloudManager Instance { get; private set; }

    // ── Cloud Save keys ──────────────────────────────────────────
    private const string KEY_USERNAME      = "username";
    private const string KEY_CHARACTER_ID  = "currentCharacterId";

    // ── PlayerPrefs cache keys ───────────────────────────────────
    private const string PREF_USERNAME     = "cachedUsername";
    private const string PREF_CHARACTER_ID = "cachedCharacterId";

    // ── Sync guard (prevents save-during-load loops) ─────────────
    private bool isSyncing;

    // ══════════════════════════════════════════════════════════════
    //  IAuthProvider
    // ══════════════════════════════════════════════════════════════

    public bool IsAuthenticated =>
        UnityServices.State == ServicesInitializationState.Initialized
        && AuthenticationService.Instance.IsSignedIn;

    public string UserId =>
        AuthenticationService.Instance?.PlayerId ?? "";

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

    // ══════════════════════════════════════════════════════════════
    //  IAuthProvider — Sign-in methods
    // ══════════════════════════════════════════════════════════════

    public async Task SignInAsGuestAsync(string playerProfile)
    {
        if (UnityServices.State != ServicesInitializationState.Initialized)
        {
            var options = new InitializationOptions();
            options.SetProfile(playerProfile);
            await UnityServices.InitializeAsync(options);
        }

        await AuthenticationService.Instance.SignInAnonymouslyAsync();
        Debug.Log($"[{LogTag.Auth}] Guest sign-in successful — playerId={AuthenticationService.Instance.PlayerId}", this);
    }

    public async Task<string> LoginAsync(string playerProfile, string username, string password)
    {
        try
        {
            if (UnityServices.State != ServicesInitializationState.Initialized)
            {
                var options = new InitializationOptions();
                options.SetProfile(playerProfile);
                await UnityServices.InitializeAsync(options);
            }

            await AuthenticationService.Instance.SignInWithUsernamePasswordAsync(username, password);
            Debug.Log($"[{LogTag.Auth}] Login successful — playerId={AuthenticationService.Instance.PlayerId}", this);

            // Load profile data from Cloud Save
            await LoadProfileDataAsync();

            return null;
        }
        catch (AuthenticationException ex)
        {
            Debug.LogError($"[{LogTag.Auth}] Login failed — error={ex.Message}", this);
            return ex.Message;
        }
        catch (RequestFailedException ex)
        {
            Debug.LogError($"[{LogTag.Auth}] Login request failed — error={ex.Message}", this);
            return ex.Message;
        }
    }

    public async Task<string> RegisterAsync(string playerProfile, string username, string email, string password)
    {
        try
        {
            if (UnityServices.State != ServicesInitializationState.Initialized)
            {
                var options = new InitializationOptions();
                options.SetProfile(playerProfile);
                await UnityServices.InitializeAsync(options);
            }

            await AuthenticationService.Instance.SignUpWithUsernamePasswordAsync(username, password);
            Debug.Log($"[{LogTag.Auth}] Registration successful — playerId={AuthenticationService.Instance.PlayerId}", this);

            // Save initial profile data
            string displayName = PlayerNameData.PlayerName;
            int characterId = PlayerAssets.Instance != null
                ? PlayerAssets.Instance.currentCharacterId : 0;
            await SaveAllAsync(displayName, characterId);

            return null;
        }
        catch (AuthenticationException ex)
        {
            Debug.LogError($"[{LogTag.Auth}] Registration failed — error={ex.Message}", this);
            return ex.Message;
        }
        catch (RequestFailedException ex)
        {
            Debug.LogError($"[{LogTag.Auth}] Registration request failed — error={ex.Message}", this);
            return ex.Message;
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  Profile data — Cloud Save persistence
    // ══════════════════════════════════════════════════════════════

    private string GetPrefKey(string baseKey)
    {
        if (AuthenticationService.Instance != null && AuthenticationService.Instance.IsSignedIn)
            return $"{baseKey}_{AuthenticationService.Instance.PlayerId}";
        return $"{baseKey}_anonymous";
    }

    public async Task SaveDisplayNameAsync(string displayName)
    {
        if (isSyncing) return;

        PlayerPrefs.SetString(GetPrefKey(PREF_USERNAME), displayName);
        PlayerPrefs.Save();

        if (!IsAuthenticated) return;

        try
        {
            var data = new Dictionary<string, object> { { KEY_USERNAME, displayName } };
            await CloudSaveService.Instance.Data.Player.SaveAsync(data);
            Debug.Log($"[{LogTag.Save}] Saved displayName — name={displayName}", this);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[{LogTag.Save}] Failed to save displayName — error={e.Message}", this);
        }
    }

    public async Task SaveCharacterIdAsync(int characterId)
    {
        if (isSyncing) return;

        PlayerPrefs.SetInt(GetPrefKey(PREF_CHARACTER_ID), characterId);
        PlayerPrefs.Save();

        if (!IsAuthenticated) return;

        try
        {
            var data = new Dictionary<string, object> { { KEY_CHARACTER_ID, characterId } };
            await CloudSaveService.Instance.Data.Player.SaveAsync(data);
            Debug.Log($"[{LogTag.Save}] Saved characterId — characterId={characterId}", this);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[{LogTag.Save}] Failed to save characterId — error={e.Message}", this);
        }
    }

    private async Task SaveAllAsync(string displayName, int characterId)
    {
        PlayerPrefs.SetString(GetPrefKey(PREF_USERNAME), displayName);
        PlayerPrefs.SetInt(GetPrefKey(PREF_CHARACTER_ID), characterId);
        PlayerPrefs.Save();

        if (!IsAuthenticated) return;

        try
        {
            var data = new Dictionary<string, object>
            {
                { KEY_USERNAME, displayName },
                { KEY_CHARACTER_ID, characterId }
            };
            await CloudSaveService.Instance.Data.Player.SaveAsync(data);
            Debug.Log($"[{LogTag.Save}] Saved profile — name={displayName}, characterId={characterId}", this);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[{LogTag.Save}] Failed to save profile — error={e.Message}", this);
        }
    }

    private async Task<bool> LoadProfileDataAsync()
    {
        isSyncing = true;

        if (!IsAuthenticated)
        {
            LoadFromLocalCache();
            isSyncing = false;
            return false;
        }

        try
        {
            var keys = new HashSet<string> { KEY_USERNAME, KEY_CHARACTER_ID };
            var data = await CloudSaveService.Instance.Data.Player.LoadAsync(keys);
            bool hasData = false;

            if (data.TryGetValue(KEY_USERNAME, out var usernameVal))
            {
                string cloudUsername = usernameVal.Value.GetAsString();
                if (!string.IsNullOrEmpty(cloudUsername))
                {
                    PlayerNameData.SetPlayerName(cloudUsername);
                    PlayerPrefs.SetString(GetPrefKey(PREF_USERNAME), cloudUsername);
                    hasData = true;
                    Debug.Log($"[{LogTag.Save}] Loaded username — name={cloudUsername}, source=CloudSave", this);
                }
            }

            if (data.TryGetValue(KEY_CHARACTER_ID, out var charIdVal))
            {
                if (int.TryParse(charIdVal.Value.GetAsString(), out int cloudCharId))
                {
                    if (PlayerAssets.Instance != null)
                        PlayerAssets.Instance.currentCharacterId = cloudCharId;
                    PlayerPrefs.SetInt(GetPrefKey(PREF_CHARACTER_ID), cloudCharId);
                    hasData = true;
                    Debug.Log($"[{LogTag.Save}] Loaded characterId — characterId={cloudCharId}, source=CloudSave", this);
                }
            }

            if (!hasData)
                Debug.Log($"[{LogTag.Save}] No cloud data found — using defaults", this);

            PlayerPrefs.Save();
            return hasData;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[{LogTag.Save}] Profile load failed — fallback=localCache, error={e.Message}", this);
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
        string cachedName = PlayerPrefs.GetString(GetPrefKey(PREF_USERNAME), "");
        if (!string.IsNullOrEmpty(cachedName))
            PlayerNameData.SetPlayerName(cachedName);

        int cachedChar = PlayerPrefs.GetInt(GetPrefKey(PREF_CHARACTER_ID), 0);
        if (PlayerAssets.Instance != null)
            PlayerAssets.Instance.currentCharacterId = cachedChar;

        Debug.Log($"[{LogTag.Save}] Loaded from local cache — name={cachedName}, characterId={cachedChar}", this);
    }

    /// <summary>
    /// Unity Authentication does not persist a reusable session token the same
    /// way Firebase does, so silent resume is not supported for this backend.
    /// Always returns false — the auth screen will be shown as normal.
    /// </summary>
    public Task<bool> TryResumeSessionAsync(string playerProfile)
    {
        return Task.FromResult(false);
    }

    public void SignOut()
    {
        if (AuthenticationService.Instance != null && AuthenticationService.Instance.IsSignedIn)
        {
            AuthenticationService.Instance.SignOut();
            Debug.Log($"[{LogTag.Auth}] Signed out of Unity Authentication", this);
        }
    }

    public async Task<string> DeleteAccountAsync()
    {
        try
        {
            if (AuthenticationService.Instance == null || !AuthenticationService.Instance.IsSignedIn)
                return "No user is currently signed in.";

            await AuthenticationService.Instance.DeleteAccountAsync();
            Debug.Log($"[{LogTag.Auth}] Unity account deleted", this);
            return null;
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[{LogTag.Auth}] Account deletion failed — error={ex.Message}", this);
            return ex.Message;
        }
    }
}
