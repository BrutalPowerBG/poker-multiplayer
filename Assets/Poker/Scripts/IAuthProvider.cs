using System.Threading.Tasks;

// ═══════════════════════════════════════════════════════════════════
//  Backend Type — Single Ecosystem Switch
//
//  One enum controls both authentication AND data storage:
//    Firebase      → Firebase Auth + Firestore
//    UnityServices → Unity Authentication + Unity Cloud Save
//
//  Set on LobbyManager (Inspector) before the session starts.
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Determines which cloud ecosystem is used for auth and data storage.
/// </summary>
public enum BackendType
{
    /// <summary>Firebase Auth + Firestore (recommended).</summary>
    Firebase,

    /// <summary>Unity Authentication + Unity Cloud Save.</summary>
    UnityServices,
}

// ═══════════════════════════════════════════════════════════════════
//  Auth Provider Interface
//
//  Abstracts authentication and player profile persistence so
//  LobbyManager works identically with either ecosystem.
//
//  Implementation note: Unity Lobby/Relay always requires Unity
//  Authentication.  When Firebase is selected, the provider also
//  initialises Unity Auth anonymously in the background.
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Abstraction for authentication and player profile persistence.
/// Implemented by <see cref="FirebaseManager"/> and <see cref="UnityCloudManager"/>.
/// </summary>
public interface IAuthProvider
{
    /// <summary>True when the user is fully signed in and ready.</summary>
    bool IsAuthenticated { get; }

    /// <summary>
    /// The provider's user ID used for data storage paths.
    /// Firebase → Firebase UID.  Unity → Unity Auth PlayerId.
    /// </summary>
    string UserId { get; }

    /// <summary>
    /// Sign in as a guest (anonymous).
    /// Also initialises Unity Services for Lobby/Relay.
    /// </summary>
    Task SignInAsGuestAsync(string playerProfile);

    /// <summary>
    /// Sign in with credentials.
    /// Firebase: email + password.  Unity: username + password.
    /// Returns null on success, or an error message on failure.
    /// Loads profile data from cloud after sign-in.
    /// </summary>
    Task<string> LoginAsync(string playerProfile, string username, string password);

    /// <summary>
    /// Register a new account.
    /// Firebase: email + password.  Unity: username + password.
    /// Returns null on success (auto-signed-in), or an error message.
    /// Saves initial profile data after registration.
    /// </summary>
    /// <param name="email">Email address (used by Firebase; ignored by Unity Auth).</param>
    Task<string> RegisterAsync(string playerProfile, string username, string email, string password);

    /// <summary>Save the player display name to the active cloud backend.</summary>
    Task SaveDisplayNameAsync(string displayName);

    /// <summary>Save the character/avatar ID to the active cloud backend.</summary>
    Task SaveCharacterIdAsync(int characterId);

    /// <summary>
    /// Attempts to silently resume a persisted authenticated session
    /// (e.g. a Firebase token saved from a previous app launch).
    /// Returns <c>true</c> if a valid non-guest session was restored and
    /// player profile data was loaded; <c>false</c> if no session exists
    /// or the token has expired (caller should show the login screen).
    /// </summary>
    Task<bool> TryResumeSessionAsync(string playerProfile);

    /// <summary>
    /// Signs the user out of the active backend.
    /// Firebase: signs out of Firebase Auth.
    /// Unity: signs out of Unity Authentication.
    /// Callers should also clear any Remember-Me prefs and navigate
    /// back to the auth screen.
    /// </summary>
    void SignOut();

    /// <summary>
    /// Permanently deletes the authenticated account and signs out.
    /// Firebase: deletes the Firebase Auth user.
    /// Unity: deletes the Unity Authentication player account.
    /// Returns null on success, or an error message on failure.
    /// </summary>
    Task<string> DeleteAccountAsync();
}
