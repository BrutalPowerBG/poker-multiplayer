using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class ChatPanel : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private ScrollRect scrollRect;
    [SerializeField] private Transform messageContent;
    [SerializeField] private TMP_InputField inputField;
    [SerializeField] private Button sendButton;

    [Header("Message Prefab")]
    [SerializeField] private GameObject messagePrefab;

    [Header("Settings")]
    [SerializeField] private int maxMessages = 50;

    private readonly List<GameObject> messageEntries = new List<GameObject>();
    private bool subscribedToChat;
    private bool needsScrollUpdate;

    private void Awake()
    {
        if (sendButton != null)
            sendButton.onClick.AddListener(OnSendClicked);

        if (inputField != null)
            inputField.onSubmit.AddListener(OnInputSubmit);

        TrySubscribe();
        gameObject.SetActive(false);
    }

    private void OnEnable()
    {
        TrySubscribe();

        if (inputField != null)
            inputField.text = "";

        if (needsScrollUpdate)
        {
            Canvas.ForceUpdateCanvases();
            if (scrollRect != null)
                scrollRect.verticalNormalizedPosition = 0f;
            needsScrollUpdate = false;
        }
    }

    private void Update()
    {
        if (!subscribedToChat)
            TrySubscribe();
    }

    private void OnDestroy()
    {
        if (subscribedToChat && ChatManager.Instance != null)
            ChatManager.Instance.OnChatMessageReceived -= OnChatMessage;
    }

    private void TrySubscribe()
    {
        if (subscribedToChat) return;
        if (ChatManager.Instance == null) return;

        ChatManager.Instance.OnChatMessageReceived += OnChatMessage;
        subscribedToChat = true;
    }

    public void Toggle()
    {
        gameObject.SetActive(!gameObject.activeSelf);
        if (gameObject.activeSelf && inputField != null)
            inputField.ActivateInputField();
    }

    private void OnChatMessage(int senderSlot, string senderName, string message)
    {
        AddMessage(senderName, message);
    }

    private void AddMessage(string senderName, string message)
    {
        if (messageContent == null) return;

        GameObject entry;

        if (messagePrefab != null)
        {
            entry = Instantiate(messagePrefab, messageContent);
        }
        else
        {
            entry = CreateDefaultMessageEntry();
        }

        entry.SetActive(true);

        var text = entry.GetComponentInChildren<TextMeshProUGUI>(true);
        if (text != null)
            text.text = $"<b>{senderName}:</b> {message}";

        messageEntries.Add(entry);

        while (messageEntries.Count > maxMessages)
        {
            Destroy(messageEntries[0]);
            messageEntries.RemoveAt(0);
        }

        if (gameObject.activeInHierarchy)
        {
            Canvas.ForceUpdateCanvases();
            if (scrollRect != null)
                scrollRect.verticalNormalizedPosition = 0f;
        }
        else
        {
            needsScrollUpdate = true;
        }
    }

    private GameObject CreateDefaultMessageEntry()
    {
        var go = new GameObject("ChatMessage", typeof(RectTransform));
        go.transform.SetParent(messageContent, false);

        var layout = go.AddComponent<LayoutElement>();
        layout.minHeight = 24f;

        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.fontSize = 14f;
        tmp.enableWordWrapping = true;
        tmp.richText = true;
        tmp.color = Color.white;

        return go;
    }

    private void OnSendClicked()
    {
        SubmitMessage();
    }

    private void OnInputSubmit(string text)
    {
        SubmitMessage();
    }

    private void SubmitMessage()
    {
        if (inputField == null) return;

        string text = inputField.text;
        if (string.IsNullOrWhiteSpace(text)) return;

        if (ChatManager.Instance != null)
            ChatManager.Instance.SendChat(text);

        inputField.text = "";
        inputField.ActivateInputField();
    }
}
