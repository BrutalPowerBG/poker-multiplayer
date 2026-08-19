using UnityEngine;

/// <summary>
/// Local asset provider for player sprites/avatars.
/// Cloud persistence is handled by the active IAuthProvider
/// (FirebaseManager or UnityCloudManager via LobbyManager).
/// </summary>
public class PlayerAssets : MonoBehaviour {

    public static PlayerAssets Instance { get; private set; }
    public int currentCharacterId = 0;
    [SerializeField] private Sprite[] characterSprites;

    public int CharacterSpriteCount => characterSprites != null ? characterSprites.Length : 0;

    private void Awake() {
        if (Instance != null && Instance != this) {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        // Default is 0. Cloud data (via the auth provider's LoadProfileData)
        // is the authoritative source and will overwrite this after authentication.
    }

    public Sprite GetSprite(int characterId) {
        if (characterSprites == null || characterSprites.Length == 0) {
            Debug.LogError("PlayerAssets: characterSprites array is not set up!");
            return null;
        }

        if (characterId < 0 || characterId >= characterSprites.Length) {
            Debug.LogWarning($"PlayerAssets: Invalid characterId {characterId}, using default (0)");
            return characterSprites[0];
        }

        return characterSprites[characterId];
    }

    /// <summary>
    /// Sets the character ID locally and saves to the active cloud backend.
    /// </summary>
    public async void SetCurrentCharacterId(int characterId) {
        currentCharacterId = characterId;

        if (LobbyManager.Instance != null && LobbyManager.Instance.AuthProvider != null) {
            await LobbyManager.Instance.AuthProvider.SaveCharacterIdAsync(characterId);
        }
    }
}
