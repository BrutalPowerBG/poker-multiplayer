using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class ChatBubble : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI bubbleText;
    [SerializeField] private Image background;
    [SerializeField] private float displayDuration = 3f;
    [SerializeField] private float fadeDuration = 0.5f;

    private int slotIndex = -1;
    private Coroutine fadeCoroutine;
    private CanvasGroup canvasGroup;
    private bool subscribedToChat;

    private void Awake()
    {
        canvasGroup = GetComponent<CanvasGroup>();
        if (canvasGroup == null)
            canvasGroup = gameObject.AddComponent<CanvasGroup>();

        canvasGroup.alpha = 0f;
        canvasGroup.blocksRaycasts = false;

        if (slotIndex < 0)
        {
            var slot = GetComponentInParent<PlayerSlotUI>();
            if (slot != null) slotIndex = slot.slotIndex;
        }
    }

    private void OnEnable()
    {
        TrySubscribe();
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

    public void SetSlotIndex(int index)
    {
        slotIndex = index;
    }

    private void OnChatMessage(int senderSlot, string senderName, string message)
    {
        if (GameManager.Instance.GetVisualSlotIndex(senderSlot) != slotIndex) return;
        Show(message);
    }

    public void Show(string message)
    {
        if (bubbleText == null) return;

        if (fadeCoroutine != null)
            StopCoroutine(fadeCoroutine);

        bubbleText.text = message;
        canvasGroup.alpha = 1f;
        canvasGroup.blocksRaycasts = true;

        fadeCoroutine = StartCoroutine(FadeOut());
    }

    private System.Collections.IEnumerator FadeOut()
    {
        yield return new WaitForSeconds(displayDuration);

        float elapsed = 0f;
        while (elapsed < fadeDuration)
        {
            elapsed += Time.deltaTime;
            canvasGroup.alpha = 1f - (elapsed / fadeDuration);
            yield return null;
        }

        canvasGroup.alpha = 0f;
        canvasGroup.blocksRaycasts = false;
        fadeCoroutine = null;
    }
}
