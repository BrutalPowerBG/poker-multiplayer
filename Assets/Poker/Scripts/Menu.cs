using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using Unity.Netcode;

public class Menu : MonoBehaviour
{
    [SerializeField] Button exitToLobbyButton;
    [SerializeField] Slider cardScaleSlider;
    [SerializeField] TextMeshProUGUI cardScalePercentText;

    void Start() {
        if (exitToLobbyButton != null)
            exitToLobbyButton.onClick.AddListener(ExitToLobby);

        if (cardScaleSlider != null) {
            cardScaleSlider.minValue = 0.5f;
            cardScaleSlider.maxValue = 1.5f;
            cardScaleSlider.value = 1f;
            cardScaleSlider.onValueChanged.AddListener(OnCardScaleChanged);
            UpdateScalePercentText(cardScaleSlider.value);
        }
    }

    private void OnCardScaleChanged(float value) {
        foreach (var card in FindObjectsOfType<CardUI>(true))
            card.ApplyScaleFactor(value);
        UpdateScalePercentText(value);
    }

    private void UpdateScalePercentText(float value) {
        if (cardScalePercentText != null)
            cardScalePercentText.text = $"{Mathf.RoundToInt(value * 100)}%";
    }

    public void ExitToLobby() {
        var mgr = ExitHandler.GetOrCreate();
        if (mgr != null) {
            mgr.HandleExitToLobby();
        } else {
            // Fallback: shutdown but keep NM alive for reuse
            if (NetworkManager.Singleton != null && (NetworkManager.Singleton.IsServer || NetworkManager.Singleton.IsClient)) {
                NetworkManager.Singleton.Shutdown();
            }
            if (LobbyManager.Instance != null) {
                // Clear relay data so next game run will create a fresh allocation
                LobbyManager.Instance.ClearRelayData();
                LobbyManager.Instance.LeaveLobby();
            }
            SceneManager.LoadScene(0);
        }
    }
}
