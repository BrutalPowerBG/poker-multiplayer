using System;
using UnityEngine;

/// <summary>
/// Pure static class that stores the global player display name.
/// No MonoBehaviour, no GameObject — can't be destroyed.
/// Any number of input fields can read/write through this.
/// </summary>
public static class PlayerNameData {

    private const string DEFAULT_PLAYER_NAME = "Guest";

    private static string playerName;
    private static bool initialized = false;

    /// <summary>
    /// Fires whenever the name changes. Subscribers receive the new name.
    /// </summary>
    public static event Action<string> OnNameChanged;

    /// <summary>
    /// The current player display name. Auto-generates a default on first access.
    /// </summary>
    public static string PlayerName {
        get {
            EnsureInitialized();
            return playerName;
        }
    }

    /// <summary>
    /// Sets the player name and notifies all subscribers (input fields, lobby, etc.).
    /// </summary>
    public static void SetPlayerName(string newName) {
        EnsureInitialized();
        if (string.IsNullOrEmpty(newName) || newName == playerName) return;

        playerName = newName;
        OnNameChanged?.Invoke(playerName);

        // Sync with lobby if available
        if (LobbyManager.Instance != null) {
            LobbyManager.Instance.UpdatePlayerName(playerName);
        }

        // Sync with cloud storage via the active auth provider
        if (LobbyManager.Instance != null && LobbyManager.Instance.AuthProvider != null) {
            _ = LobbyManager.Instance.AuthProvider.SaveDisplayNameAsync(playerName);
        }
    }

    private static void EnsureInitialized() {
        if (!initialized) {
            initialized = true;
            int randomNumber = UnityEngine.Random.Range(1000, 10000);
            playerName = DEFAULT_PLAYER_NAME + randomNumber.ToString();
        }
    }
}
