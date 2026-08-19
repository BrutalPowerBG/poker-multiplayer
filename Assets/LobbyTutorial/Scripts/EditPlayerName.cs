using TMPro;
using UnityEngine;

/// <summary>
/// Lightweight component that syncs a TMP_InputField with PlayerNameData.
/// Can be placed on any number of GameObjects — no singleton, no destruction.
/// Each instance keeps its input field in sync with the global player name.
/// </summary>
public class EditPlayerName : MonoBehaviour {

    private const string ALLOWED_CHARACTERS = "abcdefghijklmnopqrstuvxywzABCDEFGHIJKLMNOPQRSTUVXYWZ1234567890-";
    private const int MAX_NAME_LENGTH = 20;

    [SerializeField] private TMP_InputField playerNameInputField;

    private bool isSyncing = false; // Prevents feedback loops

    private void OnEnable() {
        // Sync input field with current global name
        if (playerNameInputField != null) {
            playerNameInputField.text = PlayerNameData.PlayerName;
            playerNameInputField.characterLimit = MAX_NAME_LENGTH;
            playerNameInputField.onValidateInput += ValidateChar;
            playerNameInputField.onEndEdit.AddListener(OnLocalNameEdited);
        }

        // Listen for name changes from other sources
        PlayerNameData.OnNameChanged += OnGlobalNameChanged;
    }

    private void OnDisable() {
        if (playerNameInputField != null) {
            playerNameInputField.onValidateInput -= ValidateChar;
            playerNameInputField.onEndEdit.RemoveListener(OnLocalNameEdited);
        }

        PlayerNameData.OnNameChanged -= OnGlobalNameChanged;
    }

    /// <summary>
    /// Called when the user finishes editing THIS input field — push to global data.
    /// </summary>
    private void OnLocalNameEdited(string newName) {
        if (isSyncing || string.IsNullOrEmpty(newName)) return;

        isSyncing = true;
        PlayerNameData.SetPlayerName(newName);
        isSyncing = false;
    }

    /// <summary>
    /// Called when the global name changes (from another input field or code) — pull into this field.
    /// </summary>
    private void OnGlobalNameChanged(string newName) {
        if (isSyncing) return;

        isSyncing = true;
        if (playerNameInputField != null && playerNameInputField.text != newName) {
            playerNameInputField.text = newName;
        }
        isSyncing = false;
    }

    private char ValidateChar(string text, int charIndex, char addedChar) {
        return ALLOWED_CHARACTERS.IndexOf(addedChar) != -1 ? addedChar : '\0';
    }
}
