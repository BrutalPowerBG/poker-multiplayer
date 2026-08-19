using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TicTacToe {
    public class PlayerUI : MonoBehaviour {


    [SerializeField] private GameObject crossArrowGameObject;
    [SerializeField] private GameObject circleArrowGameObject;
    [SerializeField] private GameObject crossYouTextGameObject;
    [SerializeField] private GameObject circleYouTextGameObject;
    [SerializeField] private TextMeshProUGUI playerCrossScoreTextMesh;
    [SerializeField] private TextMeshProUGUI playerCircleScoreTextMesh;
    [SerializeField] private Button exitToLobbyButton;



    private void Awake() {
        crossArrowGameObject.SetActive(false);
        circleArrowGameObject.SetActive(false);
        crossYouTextGameObject.SetActive(false);
        circleYouTextGameObject.SetActive(false);

        playerCrossScoreTextMesh.text = "";
        playerCircleScoreTextMesh.text = "";

        // Dynamically connect the exit button to avoid broken references on scene reload
        if (exitToLobbyButton != null) {
            exitToLobbyButton.onClick.RemoveAllListeners(); // Clear any existing listeners
            exitToLobbyButton.onClick.AddListener(() => {
                if (GameManager.Instance != null) {
                    GameManager.Instance.ExitToLobby();
                }
            });
        }
    }

    private void Start() {
        GameManager.Instance.OnGameStarted += GameManager_OnGameStarted;
        GameManager.Instance.OnCurrentPlayablePlayerTypeChanged += GameManager_OnCurrentPlayablePlayerTypeChanged;
        GameManager.Instance.OnScoreChanged += GameManager_OnScoreChanged;
    }

    private void GameManager_OnScoreChanged(object sender, System.EventArgs e) {
        GameManager.Instance.GetScores(out int playerCrossScore, out int playerCircleScore);

        playerCrossScoreTextMesh.text = playerCrossScore.ToString();
        playerCircleScoreTextMesh.text = playerCircleScore.ToString();
    }

    private void GameManager_OnCurrentPlayablePlayerTypeChanged(object sender, System.EventArgs e) {
        UpdateCurrentArrow();
    }

    private void GameManager_OnGameStarted(object sender, System.EventArgs e) {
        if (GameManager.Instance.GetLocalPlayerType() == GameManager.PlayerType.Cross) {
            crossYouTextGameObject.SetActive(true);
        } else {
            circleYouTextGameObject.SetActive(true);
        }
        playerCrossScoreTextMesh.text = "0";
        playerCircleScoreTextMesh.text = "0";

        UpdateCurrentArrow();
    }

    private void UpdateCurrentArrow() {
        if (GameManager.Instance.GetCurrentPlayablePlayerType() == GameManager.PlayerType.Cross) {
            crossArrowGameObject.SetActive(true);
            circleArrowGameObject.SetActive(false);
        } else {
            crossArrowGameObject.SetActive(false);
            circleArrowGameObject.SetActive(true);
        }
    }

}
}