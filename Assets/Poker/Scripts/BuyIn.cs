using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class BuyIn : MonoBehaviour
{
    private enum BuyInMode
    {
        SitDown,
        AddCash
    }

    public static BuyIn Instance { get; private set; }
    [HideInInspector] public PlayerSlotUI playerSlotUI;
    public GameObject buyInPanel;
    private float buyInAmount;
    public float minBuyIn;
    public float maxBuyIn;
    public float availableBalance;
    public bool unlimitedAvailableBalance;
    public float initialAvailableBalance;
    public float bigBlind;

    [SerializeField] private TextMeshProUGUI buyInAmountText;
    [SerializeField] private TextMeshProUGUI minBuyInText;
    [SerializeField] private TextMeshProUGUI maxBuyInText;
    [SerializeField] private TextMeshProUGUI availableBalanceText;
    [SerializeField] private TextMeshProUGUI initialAvailableBalanceText;
    [SerializeField] private TextMeshProUGUI bigBlindText;

    [Tooltip("Extra width added to availableBalanceText when showing 'Unlimited'.")]
    [SerializeField] private float unlimitedTextWidthBonus = 60f;
    private float availableBalanceTextBaseWidth = -1f;

    [SerializeField] private Slider slider;
    
    public Button maxButton;
    public Button minButton;
    public Button cancelButton;
    public Button confirmButton;
    private BuyInMode currentMode = BuyInMode.SitDown;
    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            if (Instance.gameObject == null)
            {
                Instance = this;
            }
            else
            {
                Destroy(gameObject);
                return;
            }
        }
        else
        {
            Instance = this;
        }

        // Gather data from LobbyManager and set min/max buy in values from LobbyData
        if (LobbyManager.Instance != null) {
            minBuyIn = LobbyManager.Instance.GetMinBuyIn();
            maxBuyIn = LobbyManager.Instance.GetMaxBuyIn();
            bigBlind = LobbyManager.Instance.GetBigBlind();
            initialAvailableBalance = LobbyManager.Instance.GetSessionAvailableBalance();
            availableBalance = initialAvailableBalance;
            unlimitedAvailableBalance = LobbyManager.Instance.IsUnlimitedAvailableBalance();
        }

        InitializeBuyIn();
    }

    private void Start()
    {
        // Add slider listener
        if (slider != null)
        {
            slider.onValueChanged.AddListener(OnSliderValueChanged);
        }
        if (GameManager.Instance != null)
        {
            GameManager.Instance.OnLocalAvailableBalanceChanged += HandleLocalAvailableBalanceChanged;
        }

        // Add button listeners
        if (maxButton != null)
        {
            maxButton.onClick.AddListener(SetMaxBuyIn);
        }
        if (minButton != null)
        {
            minButton.onClick.AddListener(SetMinBuyIn);
        }
        if (confirmButton != null)
        {
            confirmButton.onClick.AddListener(OnConfirmClicked);
        }
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }

        // Remove listeners
        if (slider != null)
        {
            slider.onValueChanged.RemoveListener(OnSliderValueChanged);
        }
        if (GameManager.Instance != null)
        {
            GameManager.Instance.OnLocalAvailableBalanceChanged -= HandleLocalAvailableBalanceChanged;
        }
        if (confirmButton != null)
        {
            confirmButton.onClick.RemoveListener(OnConfirmClicked);
        }
    }

    /// <summary>
    /// Initializes the buy-in UI with current values
    /// </summary>
    public void InitializeBuyIn()
    {
        RefreshAvailableBalanceFromGameManager();

        float effectiveMax = GetEffectiveMaxBuyIn();
        float sliderMin = GetModeMinBuyIn(effectiveMax);

        // Set up slider
        if (slider != null)
        {
            slider.minValue = sliderMin;
            slider.maxValue = effectiveMax;
            slider.value = sliderMin; // Start at minimum
        }

        // Initialize buyInAmount
        buyInAmount = sliderMin;

        // Update all text fields
        UpdateBuyInText();
        minBuyInText.text = minBuyIn.ToString("F2");
        maxBuyInText.text = maxBuyIn.ToString("F2");
        if (availableBalanceText != null)
        {
            availableBalanceText.text = unlimitedAvailableBalance ? "Unlimited" : availableBalance.ToString("F2");

            if (unlimitedAvailableBalance)
            {
                availableBalanceTextBaseWidth = availableBalanceText.rectTransform.sizeDelta.x;

                float targetWidth = availableBalanceTextBaseWidth + unlimitedTextWidthBonus;
                var size = availableBalanceText.rectTransform.sizeDelta;
                size.x = targetWidth;
                availableBalanceText.rectTransform.sizeDelta = size;
            }    
        }
        if (initialAvailableBalanceText != null)
        {
            initialAvailableBalanceText.text = unlimitedAvailableBalance ? "" : "/ " + initialAvailableBalance.ToString("F0");
        }
        bigBlindText.text = bigBlind.ToString("F2");
        if (confirmButton != null)
        {
            bool hasEnoughToBuy = unlimitedAvailableBalance || availableBalance >= sliderMin;
            if (currentMode == BuyInMode.AddCash)
            {
                hasEnoughToBuy = hasEnoughToBuy && GetMaxAddCashByStack() > 0f;
            }
            confirmButton.interactable = hasEnoughToBuy;
        }
    }

    /// <summary>
    /// Opens the buy-in panel for a specific player slot
    /// </summary>
    public void OpenBuyInPanel(PlayerSlotUI slotUI)
    {
        currentMode = BuyInMode.SitDown;
        playerSlotUI = slotUI;
        InitializeBuyIn();
        buyInPanel.SetActive(true);
    }

    /// <summary>
    /// Opens the buy-in panel in Add Cash mode for the local seated player.
    /// </summary>
    public void OpenAddCashPanel()
    {
        if (GameManager.Instance == null || !GameManager.Instance.hasLocalPlayer)
        {
            Debug.LogWarning($"[{LogTag.BuyIn}] Add cash panel blocked — reason=local player not seated", this);
            return;
        }

        currentMode = BuyInMode.AddCash;
        playerSlotUI = null;
        if (GetMaxAddCashByStack() <= 0f)
        {
            Debug.LogWarning($"[{LogTag.BuyIn}] Add cash panel blocked — reason=stack at or above max buy-in", this);
            return;
        }
        InitializeBuyIn();
        buyInPanel.SetActive(true);
    }

    /// <summary>
    /// Closes the buy-in panel
    /// </summary>
    public void CloseBuyInPanel()
    {
        buyInPanel.SetActive(false);
        playerSlotUI = null;
    }

    public void BuyInNow()
    {
        if (currentMode == BuyInMode.AddCash)
        {
            AddCashNow();
            return;
        }

        if (playerSlotUI == null)
        {
            Debug.LogError($"[{LogTag.BuyIn}] Buy-in failed — reason=PlayerSlotUI is null, call OpenBuyInPanel() first", this);
            return;
        }

        if (!unlimitedAvailableBalance && buyInAmount > availableBalance + 0.001f)
        {
            Debug.LogWarning($"[{LogTag.BuyIn}] Buy-in rejected — reason=insufficient balance, requested=${buyInAmount:F2}, available=${availableBalance:F2}", this);
            return;
        }

        GameManager.Instance.SitOnTable(playerSlotUI, buyInAmount);
        CloseBuyInPanel();
    }

    public void AddCashNow()
    {
        if (GameManager.Instance == null || !GameManager.Instance.hasLocalPlayer)
        {
            Debug.LogWarning($"[{LogTag.BuyIn}] Add cash rejected — reason=local player not seated", this);
            return;
        }

        if (buyInAmount <= 0f)
        {
            Debug.LogWarning($"[{LogTag.BuyIn}] Add cash rejected — reason=amount must be greater than zero, amount=${buyInAmount:F2}", this);
            return;
        }

        if (!unlimitedAvailableBalance && buyInAmount > availableBalance + 0.001f)
        {
            Debug.LogWarning($"[{LogTag.BuyIn}] Add cash rejected — reason=insufficient balance, requested=${buyInAmount:F2}, available=${availableBalance:F2}", this);
            return;
        }

        float maxAddByStack = GetMaxAddCashByStack();
        if (maxAddByStack <= 0f)
        {
            Debug.LogWarning($"[{LogTag.BuyIn}] Add cash rejected — reason=stack at or above max buy-in", this);
            return;
        }
        if (buyInAmount > maxAddByStack + 0.001f)
        {
            Debug.LogWarning($"[{LogTag.BuyIn}] Add cash rejected — reason=exceeds stack cap, requested=${buyInAmount:F2}, maxByStack=${maxAddByStack:F2}", this);
            return;
        }

        GameManager.Instance.AddCash(buyInAmount);
        CloseBuyInPanel();
    }

    /// <summary>
    /// Called when slider value changes
    /// </summary>
    private void OnSliderValueChanged(float value)
    {
        // Round to nearest 0.20 increment
        buyInAmount = Mathf.Round(value * 5f) / 5f;
        
        UpdateBuyInText();
    }

    /// <summary>
    /// Updates the buy-in amount text display
    /// </summary>
    private void UpdateBuyInText()
    {
        if (buyInAmountText != null)
        {
            // Check if it's a whole number
            if (buyInAmount % 1 == 0)
            {
                // Show without decimals (e.g., "100")
                buyInAmountText.text = buyInAmount.ToString("F0");
            }
            else
            {
                // Show with decimals (e.g., "100.50")
                buyInAmountText.text = buyInAmount.ToString("F2");
            }
        }
    }

    /// <summary>
    /// Sets the buy-in to maximum value
    /// </summary>
    public void SetMaxBuyIn()
    {
        if (slider != null)
        {
            slider.value = GetEffectiveMaxBuyIn();
        }
    }

    /// <summary>
    /// Sets the buy-in to minimum value
    /// </summary>
    public void SetMinBuyIn()
    {
        if (slider != null)
        {
            slider.value = GetModeMinBuyIn(GetEffectiveMaxBuyIn());
        }
    }

    /// <summary>
    /// Gets the current buy-in amount
    /// </summary>
    public float GetBuyInAmount()
    {
        return buyInAmount;
    }

    private void HandleLocalAvailableBalanceChanged(float balance, bool unlimited)
    {
        availableBalance = balance;
        unlimitedAvailableBalance = unlimited;
        if (buyInPanel != null && buyInPanel.activeSelf)
        {
            InitializeBuyIn();
        }
    }

    private void RefreshAvailableBalanceFromGameManager()
    {
        if (GameManager.Instance == null) return;
        availableBalance = GameManager.Instance.LocalAvailableBalance;
        unlimitedAvailableBalance = GameManager.Instance.IsUnlimitedAvailableBalance;
    }

    private float GetEffectiveMaxBuyIn()
    {
        if (currentMode == BuyInMode.AddCash)
        {
            float byStack = GetMaxAddCashByStack();
            if (unlimitedAvailableBalance)
                return byStack;
            return Mathf.Min(byStack, Mathf.Max(0f, availableBalance));
        }

        if (unlimitedAvailableBalance)
            return maxBuyIn;

        return Mathf.Clamp(availableBalance, 0f, maxBuyIn);
    }

    private float GetModeMinBuyIn(float effectiveMax)
    {
        if (currentMode == BuyInMode.AddCash)
        {
            // Allow small top-ups while still respecting slider precision (0.20).
            return Mathf.Min(0.2f, effectiveMax);
        }
        return Mathf.Min(minBuyIn, effectiveMax);
    }

    private void OnConfirmClicked()
    {
        if (currentMode == BuyInMode.AddCash)
        {
            AddCashNow();
        }
        else
        {
            BuyInNow();
        }
    }

    private float GetMaxAddCashByStack()
    {
        if (GameManager.Instance == null || !GameManager.Instance.hasLocalPlayer)
            return 0f;

        float currentStack = Mathf.Max(0f, GameManager.Instance.localPlayer.stack);
        return Mathf.Max(0f, maxBuyIn - currentStack);
    }
}
