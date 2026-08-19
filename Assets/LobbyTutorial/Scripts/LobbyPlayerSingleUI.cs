using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;
using Unity.Services.Lobbies.Models;
using UnityEngine.UI;

public class LobbyPlayerSingleUI : MonoBehaviour {


    [SerializeField] private TextMeshProUGUI playerNameText;
    [SerializeField] private Image characterImage;
    [SerializeField] private Button kickPlayerButton;
    [SerializeField] private GameObject readyImage;


    private Player player;


    private void Awake() {
        kickPlayerButton.onClick.AddListener(KickPlayer);
    }

    public void SetKickPlayerButtonVisible(bool visible) {
        kickPlayerButton.gameObject.SetActive(visible);
    }

    public void UpdatePlayer(Player player) {
        this.player = player;
        
        if (player.Data != null &&
            player.Data.ContainsKey(LobbyManager.KEY_PLAYER_NAME) &&
            player.Data[LobbyManager.KEY_PLAYER_NAME] != null)
        {
            playerNameText.text = player.Data[LobbyManager.KEY_PLAYER_NAME].Value;
        }
        else
        {
            playerNameText.text = "Player";
        }
        
        int characterId = 0; // Default to first character
        if (player.Data != null &&
            player.Data.ContainsKey(LobbyManager.KEY_PLAYER_CHARACTER) &&
            player.Data[LobbyManager.KEY_PLAYER_CHARACTER] != null)
        {
            if (int.TryParse(player.Data[LobbyManager.KEY_PLAYER_CHARACTER].Value, out int parsedId)) {
                characterId = parsedId;
            }
        }
        
        if (PlayerAssets.Instance != null)
        {
            characterImage.sprite = PlayerAssets.Instance.GetSprite(characterId);
        }

        bool isReady = false;
        if (player.Data != null &&
            player.Data.TryGetValue(LobbyManager.KEY_PLAYER_READY, out var readyData) &&
            readyData != null) {
            isReady = readyData.Value == "1";
        }
        if (readyImage != null) readyImage.SetActive(isReady);
    }

    private void KickPlayer() {
        if (player != null) {
            LobbyManager.Instance.KickPlayer(player.Id);
        }
    }


}