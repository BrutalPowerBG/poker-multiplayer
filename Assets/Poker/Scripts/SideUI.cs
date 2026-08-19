using UnityEngine;
using UnityEngine.UI;


public class SideUI : MonoBehaviour
{
    [SerializeField] private Button addCashButton;
    [SerializeField] private Button chatButton;
    [SerializeField] private Button statsButton;
    [SerializeField] private ChatPanel chatPanel;

    private void Awake()
    {
        if (addCashButton != null)
            addCashButton.onClick.AddListener(OnAddCashClicked);

        if (chatButton != null)
            chatButton.onClick.AddListener(OnChatClicked);
    }

    private void OnDestroy()
    {
        if (addCashButton != null)
            addCashButton.onClick.RemoveListener(OnAddCashClicked);

        if (chatButton != null)
            chatButton.onClick.RemoveListener(OnChatClicked);
    }

    private void Update()
    {
        if (addCashButton != null)
        {
            bool canAddCash = GameManager.Instance != null && GameManager.Instance.hasLocalPlayer;
            if (addCashButton.gameObject.activeSelf != canAddCash)
            {
                addCashButton.gameObject.SetActive(canAddCash);
            }
        }
    }

    private void OnAddCashClicked()
    {
        if (BuyIn.Instance == null)
        {
            Debug.LogWarning($"[{LogTag.UI}] Add-cash panel open failed — reason=BuyIn.Instance is null", this);
            return;
        }

        BuyIn.Instance.OpenAddCashPanel();
    }

    private void OnChatClicked()
    {
        if (chatPanel != null)
            chatPanel.Toggle();
    }
}
