using System;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

public class ChatManager : NetworkBehaviour
{
    public static ChatManager Instance { get; private set; }

    public event Action<int, string, string> OnChatMessageReceived;

    private const int MAX_MESSAGE_LENGTH = 120;
    private const float SEND_COOLDOWN = 1f;

    private float lastSendTime;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    public override void OnDestroy()
    {
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    public void SendChat(string message)
    {
        if (GameManager.Instance == null || !GameManager.Instance.hasLocalPlayer) return;

        message = message.Trim();
        if (string.IsNullOrEmpty(message)) return;

        if (Time.time - lastSendTime < SEND_COOLDOWN) return;
        lastSendTime = Time.time;

        if (message.Length > MAX_MESSAGE_LENGTH)
            message = message.Substring(0, MAX_MESSAGE_LENGTH);

        int senderSlot = GameManager.Instance.localPlayer.realSlotIndex;
        SendChatServerRpc(senderSlot, new FixedString128Bytes(message));
    }

    [ServerRpc(RequireOwnership = false)]
    private void SendChatServerRpc(int senderSlot, FixedString128Bytes message)
    {
        string senderName = "Unknown";
        bool found = false;
        foreach (var p in GameManager.Instance.players)
        {
            if (p.realSlotIndex == senderSlot)
            {
                senderName = p.playerName.ToString();
                found = true;
                break;
            }
        }

        if (!found) return;

        string text = message.ToString().Trim();
        if (string.IsNullOrEmpty(text)) return;

        BroadcastChatClientRpc(senderSlot, new FixedString64Bytes(senderName), message);
    }

    [ClientRpc]
    private void BroadcastChatClientRpc(int senderSlot, FixedString64Bytes senderName, FixedString128Bytes message)
    {
        OnChatMessageReceived?.Invoke(senderSlot, senderName.ToString(), message.ToString());
    }
}
