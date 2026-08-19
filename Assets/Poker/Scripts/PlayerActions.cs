using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class PlayerActions : MonoBehaviour
{
    // ==================== MAIN ACTION BUTTONS ====================
    public Button foldButton;
    public Button checkButton;
    public Button callButton;
    public Button raiseButton;
    public Slider raiseSlider;
    public TMP_InputField raiseInputField;
    public TextMeshProUGUI raiseText;
    public TextMeshProUGUI callText;

    // ==================== RAISE PRESET BUTTONS ====================
    [Header("Raise Preset Buttons")]
    public Button raisePreset1Button;
    public Button raisePreset2Button;
    public Button raisePreset3Button;
    public Button raisePreset4Button;
    public TextMeshProUGUI raisePreset1Text;
    public TextMeshProUGUI raisePreset2Text;
    public TextMeshProUGUI raisePreset3Text;
    public TextMeshProUGUI raisePreset4Text;

    // ==================== PRE-ACTION BUTTONS ====================
    [Header("Pre-Action Buttons (shown when NOT on turn)")]
    public Button autoFoldButton;
    public Button autoCheckButton;
    public Button autoCallAnyButton;
    
    [Header("Pre-Action Tick Images")]
    public GameObject autoFoldTick;
    public GameObject autoCheckTick;
    public GameObject autoCallAnyTick;

    [Header("Voluntary Reveal Buttons (shown after pot award if cards are still hidden)")]
    public Button revealCard1Button;
    public Button revealCard2Button;
    public Button revealBothCardsButton;

    [Header("Voluntary Reveal Card Previews")]
    public CardUI revealCard1Preview;
    public CardUI revealCard2Preview;
    public CardUI revealBothCard1Preview;
    public CardUI revealBothCard2Preview;

    [Header("Internal State")]
    public float currentRaiseAmount;
    private bool isUpdatingSlider = false;
    private bool isUpdatingInputField = false;
    private bool wasMyTurn = false;
    
    // Pre-action state
    private enum PreActionType { None, CheckFold, AutoCheck, AutoCallAny }
    private PreActionType selectedPreAction = PreActionType.None;
    private GameManager.GamePhase lastTrackedPhase;

    private void Awake()
    {
        // Disable all main action UI elements on awake
        if (foldButton != null) foldButton.gameObject.SetActive(false);
        if (checkButton != null) checkButton.gameObject.SetActive(false);
        if (callButton != null) callButton.gameObject.SetActive(false);
        if (raiseButton != null) raiseButton.gameObject.SetActive(false);
        if (raiseSlider != null) raiseSlider.gameObject.SetActive(false);
        if (raiseInputField != null) raiseInputField.gameObject.SetActive(false);
        
        // Disable raise preset buttons on awake
        if (raisePreset1Button != null) raisePreset1Button.gameObject.SetActive(false);
        if (raisePreset2Button != null) raisePreset2Button.gameObject.SetActive(false);
        if (raisePreset3Button != null) raisePreset3Button.gameObject.SetActive(false);
        if (raisePreset4Button != null) raisePreset4Button.gameObject.SetActive(false);
        
        // Disable all pre-action UI elements on awake
        if (autoFoldButton != null) autoFoldButton.gameObject.SetActive(false);
        if (autoCheckButton != null) autoCheckButton.gameObject.SetActive(false);
        if (autoCallAnyButton != null) autoCallAnyButton.gameObject.SetActive(false);
        
        // Ensure all ticks are hidden
        SetAllPreActionTicks(false);

        EnsureVoluntaryRevealButtons();
        ResolveVoluntaryRevealPreviews();
        SetVoluntaryRevealButtonsActive(false);
    }

    private void OnEnable()
    {
        if (GameManager.Instance != null)
            SubscribeVoluntaryReveal();
        else
            GameManager.OnInstanceReady += OnGameManagerReadyForVoluntaryReveal;
    }

    private void OnDisable()
    {
        GameManager.OnInstanceReady -= OnGameManagerReadyForVoluntaryReveal;
        if (GameManager.Instance != null)
            UnsubscribeVoluntaryReveal();
    }

    private void OnGameManagerReadyForVoluntaryReveal()
    {
        GameManager.OnInstanceReady -= OnGameManagerReadyForVoluntaryReveal;
        SubscribeVoluntaryReveal();
    }

    private void SubscribeVoluntaryReveal()
    {
        GameManager.Instance.OnVoluntaryRevealStateChanged += UpdateVoluntaryRevealButtons;
        UpdateVoluntaryRevealButtons();
    }

    private void UnsubscribeVoluntaryReveal()
    {
        GameManager.Instance.OnVoluntaryRevealStateChanged -= UpdateVoluntaryRevealButtons;
    }

    private void Start()
    {
        // Add listeners for slider and input field
        if (raiseSlider != null)
        {
            raiseSlider.onValueChanged.AddListener(OnSliderValueChanged);
        }
        
        if (raiseInputField != null)
        {
            raiseInputField.onValueChanged.AddListener(OnInputFieldValueChanged);
        }
        
        // Add listeners for raise preset buttons
        if (raisePreset1Button != null)
            raisePreset1Button.onClick.AddListener(() => OnRaisePresetClicked(0));
        if (raisePreset2Button != null)
            raisePreset2Button.onClick.AddListener(() => OnRaisePresetClicked(1));
        if (raisePreset3Button != null)
            raisePreset3Button.onClick.AddListener(() => OnRaisePresetClicked(2));
        if (raisePreset4Button != null)
            raisePreset4Button.onClick.AddListener(() => OnRaisePresetClicked(3));
        
        // Add listeners for pre-action buttons
        if (autoFoldButton != null)
            autoFoldButton.onClick.AddListener(OnCheckFoldClicked);
        if (autoCheckButton != null)
            autoCheckButton.onClick.AddListener(OnAutoCheckClicked);
        if (autoCallAnyButton != null)
            autoCallAnyButton.onClick.AddListener(OnAutoCallAnyClicked);

        if (revealCard1Button != null)
            revealCard1Button.onClick.AddListener(OnRevealCard1Clicked);
        if (revealCard2Button != null)
            revealCard2Button.onClick.AddListener(OnRevealCard2Clicked);
        if (revealBothCardsButton != null)
            revealBothCardsButton.onClick.AddListener(OnRevealBothCardsClicked);
    }

    private void Update()
    {
        if (GameManager.Instance == null || !GameManager.Instance.hasLocalPlayer)
        {
            return;
        }
        
        // Detect phase changes and reset pre-action selection
        GameManager.GamePhase currentPhase = GameManager.Instance.currentPhase.Value;
        if (currentPhase != lastTrackedPhase)
        {
            lastTrackedPhase = currentPhase;
            ClearPreAction();
        }
        
        bool isMyTurn = IsPlayerTurn();
        
        if (isMyTurn)
        {
            UpdateButtonTexts();
        }
        
        // Only update UI when turn state changes
        if (isMyTurn != wasMyTurn)
        {
            wasMyTurn = isMyTurn;
            
            if (isMyTurn)
            {
                // Turn just started - try to auto-execute pre-action
                if (TryExecutePreAction())
                {
                    return; // Pre-action was executed, skip normal UI update
                }
                
                // No pre-action or it was invalid - show normal action buttons
                HidePreActionButtons();
            }
            else
            {
                // Turn ended - show pre-action buttons if still in the hand
                UpdatePreActionButtonVisibility();
            }
            
            RefreshRaiseControls();
            UpdateButtonStates();
        }
    }

    // ==================== VOLUNTARY REVEAL BUTTONS ====================

    private void EnsureVoluntaryRevealButtons()
    {
        if (revealCard1Button != null && revealCard2Button != null && revealBothCardsButton != null)
            return;

        if (autoCheckButton == null)
            return;

        Transform preActionRow = autoCheckButton.transform.parent;
        if (preActionRow == null)
            return;

        Transform actionsRoot = preActionRow.parent;
        if (actionsRoot == null)
            actionsRoot = transform;

        Transform revealRow = actionsRoot.Find("PlayerActionsVoluntaryReveal");
        if (revealRow == null)
        {
            var container = new GameObject("PlayerActionsVoluntaryReveal", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            revealRow = container.transform;
            revealRow.SetParent(actionsRoot, false);

            var preActionRect = preActionRow.GetComponent<RectTransform>();
            var revealRect = container.GetComponent<RectTransform>();
            if (preActionRect != null)
            {
                revealRect.anchorMin = preActionRect.anchorMin;
                revealRect.anchorMax = preActionRect.anchorMax;
                revealRect.pivot = preActionRect.pivot;
                revealRect.sizeDelta = preActionRect.sizeDelta;
                revealRect.anchoredPosition = preActionRect.anchoredPosition + new Vector2(0f, 35f);
            }

            var layout = container.GetComponent<HorizontalLayoutGroup>();
            layout.spacing = 5f;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = true;
        }

        revealCard1Button ??= CreateVoluntaryRevealButtonClone(revealRow, "Reveal Card 1", "Card 1");
        revealCard2Button ??= CreateVoluntaryRevealButtonClone(revealRow, "Reveal Card 2", "Card 2");
        revealBothCardsButton ??= CreateVoluntaryRevealButtonClone(revealRow, "Reveal Both", "Both");
    }

    private Button CreateVoluntaryRevealButtonClone(Transform parent, string objectName, string label)
    {
        var clone = Instantiate(autoCheckButton.gameObject, parent);
        clone.name = objectName;
        clone.SetActive(false);

        foreach (Transform child in clone.transform)
        {
            if (child.name == "Checkbox" || child.name.Contains("Tick"))
                child.gameObject.SetActive(false);
        }

        var labelText = clone.GetComponentInChildren<TextMeshProUGUI>(true);
        if (labelText != null)
            labelText.text = label;

        return clone.GetComponent<Button>();
    }

    private const int VoluntaryRevealPreviewThemeIndex = 0;

    private void ResolveVoluntaryRevealPreviews()
    {
        revealCard1Preview ??= FindCardPreviewOnButton(revealCard1Button);
        revealCard2Preview ??= FindCardPreviewOnButton(revealCard2Button);

        if (revealBothCardsButton != null)
        {
            CardUI[] bothPreviews = revealBothCardsButton.GetComponentsInChildren<CardUI>(true);
            if (revealBothCard1Preview == null && bothPreviews.Length > 0)
                revealBothCard1Preview = bothPreviews[0];
            if (revealBothCard2Preview == null && bothPreviews.Length > 1)
                revealBothCard2Preview = bothPreviews[1];
        }

        ConfigureRevealPreviewTheme(revealCard1Preview);
        ConfigureRevealPreviewTheme(revealCard2Preview);
        ConfigureRevealPreviewTheme(revealBothCard1Preview);
        ConfigureRevealPreviewTheme(revealBothCard2Preview);
    }

    private static CardUI FindCardPreviewOnButton(Button button)
    {
        return button != null ? button.GetComponentInChildren<CardUI>(true) : null;
    }

    private void UpdateVoluntaryRevealButtons()
    {
        if (GameManager.Instance == null)
        {
            SetVoluntaryRevealButtonsActive(false);
            return;
        }

        bool showWindow = GameManager.Instance.VoluntaryRevealWindowActive;
        bool showCard1 = showWindow && GameManager.Instance.ShowVoluntaryRevealCard1Button;
        bool showCard2 = showWindow && GameManager.Instance.ShowVoluntaryRevealCard2Button;
        bool showBoth = showWindow && GameManager.Instance.ShowVoluntaryRevealBothButton;

        SetButtonActive(revealCard1Button, showCard1);
        SetButtonActive(revealCard2Button, showCard2);
        SetButtonActive(revealBothCardsButton, showBoth);

        Card card1 = GetLocalHoleCard(1);
        Card card2 = GetLocalHoleCard(2);
        UpdateRevealPreview(revealCard1Preview, showCard1, card1);
        UpdateRevealPreview(revealCard2Preview, showCard2, card2);
        UpdateRevealPreview(revealBothCard1Preview, showBoth, card1);
        UpdateRevealPreview(revealBothCard2Preview, showBoth, card2);
    }

    private void SetVoluntaryRevealButtonsActive(bool active)
    {
        SetButtonActive(revealCard1Button, active);
        SetButtonActive(revealCard2Button, active);
        SetButtonActive(revealBothCardsButton, active);

        if (!active)
        {
            UpdateRevealPreview(revealCard1Preview, false, default);
            UpdateRevealPreview(revealCard2Preview, false, default);
            UpdateRevealPreview(revealBothCard1Preview, false, default);
            UpdateRevealPreview(revealBothCard2Preview, false, default);
        }
    }

    private static Card GetLocalHoleCard(int cardIndex)
    {
        if (GameManager.Instance == null || !GameManager.Instance.hasLocalPlayer)
            return default;

        return cardIndex == 1
            ? GameManager.Instance.localPlayer.card1
            : GameManager.Instance.localPlayer.card2;
    }

    private static void UpdateRevealPreview(CardUI preview, bool show, Card card)
    {
        if (preview == null)
            return;

        if (!show || card.rank == Card.Rank.None || card.suit == Card.Suit.None)
        {
            preview.gameObject.SetActive(false);
            return;
        }

        ConfigureRevealPreviewTheme(preview);
        preview.Init(card.rank, card.suit, isHidden: false, animate: false);
    }

    private static void ConfigureRevealPreviewTheme(CardUI preview)
    {
        if (preview != null)
            preview.OverrideThemeIndex = VoluntaryRevealPreviewThemeIndex;
    }

    private static void SetButtonActive(Button button, bool active)
    {
        if (button != null)
            button.gameObject.SetActive(active);
    }

    private void OnRevealCard1Clicked()
    {
        GameManager.Instance?.RequestVoluntaryReveal(GameManager.VoluntaryRevealCard1);
    }

    private void OnRevealCard2Clicked()
    {
        GameManager.Instance?.RequestVoluntaryReveal(GameManager.VoluntaryRevealCard2);
    }

    private void OnRevealBothCardsClicked()
    {
        GameManager.Instance?.RequestVoluntaryReveal(GameManager.VoluntaryRevealBoth);
    }

    // ==================== PRE-ACTION SYSTEM ====================
    
    /// <summary>
    /// Toggle Check/Fold pre-action — check when no bet to call, fold if someone raised.
    /// </summary>
    private void OnCheckFoldClicked()
    {
        SetPreAction(selectedPreAction == PreActionType.CheckFold ? PreActionType.None : PreActionType.CheckFold);
    }
    
    /// <summary>
    /// Toggle Auto Check pre-action
    /// </summary>
    private void OnAutoCheckClicked()
    {
        SetPreAction(selectedPreAction == PreActionType.AutoCheck ? PreActionType.None : PreActionType.AutoCheck);
    }
    
    /// <summary>
    /// Toggle Auto Call Any pre-action
    /// </summary>
    private void OnAutoCallAnyClicked()
    {
        SetPreAction(selectedPreAction == PreActionType.AutoCallAny ? PreActionType.None : PreActionType.AutoCallAny);
    }
    
    /// <summary>
    /// Sets the selected pre-action and updates tick visuals
    /// </summary>
    private void SetPreAction(PreActionType action)
    {
        selectedPreAction = action;
        UpdatePreActionTicks();
    }
    
    /// <summary>
    /// Clears the selected pre-action and hides all ticks
    /// </summary>
    private void ClearPreAction()
    {
        selectedPreAction = PreActionType.None;
        UpdatePreActionTicks();
    }
    
    /// <summary>
    /// Updates tick image visibility based on selected pre-action
    /// </summary>
    private void UpdatePreActionTicks()
    {
        if (autoFoldTick != null) autoFoldTick.SetActive(selectedPreAction == PreActionType.CheckFold);
        if (autoCheckTick != null) autoCheckTick.SetActive(selectedPreAction == PreActionType.AutoCheck);
        if (autoCallAnyTick != null) autoCallAnyTick.SetActive(selectedPreAction == PreActionType.AutoCallAny);
    }
    
    /// <summary>
    /// Hides all tick images
    /// </summary>
    private void SetAllPreActionTicks(bool active)
    {
        if (autoFoldTick != null) autoFoldTick.SetActive(active);
        if (autoCheckTick != null) autoCheckTick.SetActive(active);
        if (autoCallAnyTick != null) autoCallAnyTick.SetActive(active);
    }
    
    /// <summary>
    /// Shows/hides pre-action buttons based on game state.
    /// Buttons are shown when: not on turn, game is active, player is still in the hand.
    /// </summary>
    private void UpdatePreActionButtonVisibility()
    {
        bool showPreActions = false;
        
        if (GameManager.Instance != null && GameManager.Instance.hasLocalPlayer)
        {
            bool isMyTurn = IsPlayerTurn();
            bool isGameActive = GameManager.Instance.isGameStarted.Value;
            bool isInHand = IsLocalPlayerInHand();
            
            showPreActions = !isMyTurn && isGameActive && isInHand;
        }
        
        if (autoFoldButton != null) autoFoldButton.gameObject.SetActive(showPreActions);
        if (autoCheckButton != null) autoCheckButton.gameObject.SetActive(showPreActions);
        if (autoCallAnyButton != null) autoCallAnyButton.gameObject.SetActive(showPreActions);
        
        // Hide ticks if buttons are hidden
        if (!showPreActions)
        {
            SetAllPreActionTicks(false);
        }
        else
        {
            // Restore tick state
            UpdatePreActionTicks();
        }
    }
    
    /// <summary>
    /// Hides all pre-action buttons and their ticks
    /// </summary>
    private void HidePreActionButtons()
    {
        if (autoFoldButton != null) autoFoldButton.gameObject.SetActive(false);
        if (autoCheckButton != null) autoCheckButton.gameObject.SetActive(false);
        if (autoCallAnyButton != null) autoCallAnyButton.gameObject.SetActive(false);
        SetAllPreActionTicks(false);
    }
    
    /// <summary>
    /// Checks if the local player is still playing in the current hand (not folded)
    /// </summary>
    private bool IsLocalPlayerInHand()
    {
        if (GameManager.Instance == null || !GameManager.Instance.hasLocalPlayer) return false;
        
        int localSlot = GameManager.Instance.localPlayer.realSlotIndex;
        foreach (var player in GameManager.Instance.players)
        {
            if (player.realSlotIndex == localSlot)
            {
                return player.isPlaying;
            }
        }
        return false;
    }
    
    /// <summary>
    /// Attempts to execute the selected pre-action when the player's turn arrives.
    /// Returns true if a pre-action was successfully executed, false otherwise.
    /// </summary>
    private bool TryExecutePreAction()
    {
        if (selectedPreAction == PreActionType.None) return false;
        
        float amountToCall = GameManager.Instance.GetAmountToCall();
        bool needsToCall = amountToCall > 0;
        
        switch (selectedPreAction)
        {
            case PreActionType.CheckFold:
                ClearPreAction();
                HidePreActionButtons();
                if (!needsToCall)
                {
                    Check();
                }
                else
                {
                    Fold();
                }
                return true;
                
            case PreActionType.AutoCheck:
                if (!needsToCall)
                {
                    // Can check - execute it
                    ClearPreAction();
                    HidePreActionButtons();
                    Check();
                    return true;
                }
                else
                {
                    // Someone raised since we selected Auto Check - cancel and show normal buttons
                    ClearPreAction();
                    return false;
                }
                
            case PreActionType.AutoCallAny:
                ClearPreAction();
                HidePreActionButtons();
                if (needsToCall)
                {
                    Call();
                }
                else
                {
                    // No bet to call, just check
                    Check();
                }
                return true;
        }
        
        return false;
    }

    // ==================== TURN DETECTION ====================

    bool IsPlayerTurn()
    {
        // Check if it's the local player's turn
        bool isMyTurn = false;
        if (GameManager.Instance.currentPlayerTurnIndex.Value != -1)
        {
            int currentTurnIndex = GameManager.Instance.currentPlayerTurnIndex.Value;
            if (currentTurnIndex >= 0 && currentTurnIndex < GameManager.Instance.players.Count)
            {
                PokerPlayer currentTurnPlayer = GameManager.Instance.players[currentTurnIndex];
                isMyTurn = currentTurnPlayer.realSlotIndex == GameManager.Instance.localPlayer.realSlotIndex;
            }
        }
        return isMyTurn;
    }

    // ==================== MAIN ACTION BUTTON STATES ====================

    private void UpdateButtonStates()
    {
        // Check if it's the local player's turn
        bool isMyTurn = false;
        
        if (GameManager.Instance.hasLocalPlayer && GameManager.Instance.currentPlayerTurnIndex.Value != -1)
        {
            int currentTurnIndex = GameManager.Instance.currentPlayerTurnIndex.Value;
            if (currentTurnIndex >= 0 && currentTurnIndex < GameManager.Instance.players.Count)
            {
                PokerPlayer currentTurnPlayer = GameManager.Instance.players[currentTurnIndex];
                isMyTurn = currentTurnPlayer.realSlotIndex == GameManager.Instance.localPlayer.realSlotIndex;
            }
        }
        
        float amountToCall = GameManager.Instance.GetAmountToCall();
        bool needsToCall = amountToCall > 0;
        
        // Check if there are other active players who can respond to a raise
        bool hasOtherActivePlayersWhoCanAct = false;
        if (GameManager.Instance.hasLocalPlayer)
        {
            int localPlayerSlot = GameManager.Instance.localPlayer.realSlotIndex;
            foreach (var player in GameManager.Instance.players)
            {
                // Check if this is another player who is still playing and NOT all-in
                if (player.realSlotIndex != localPlayerSlot && player.isPlaying && !player.isAllIn)
                {
                    hasOtherActivePlayersWhoCanAct = true;
                    break;
                }
            }
        }
        
        // Check if player can afford to raise
        float minRaise = GameManager.Instance.GetMinimumRaiseAmount();
        float maxBet = GameManager.Instance.GetMaximumBetAmount();
        float currentBet = GameManager.Instance.currentBetToCall.Value;
        bool canAffordRaise = (maxBet >= minRaise || maxBet > currentBet) && hasOtherActivePlayersWhoCanAct;
        
        // Update button visibility with delayed deactivation
        SetActiveWithDelay(foldButton?.gameObject, isMyTurn);
        SetActiveWithDelay(checkButton?.gameObject, isMyTurn && !needsToCall);
        SetActiveWithDelay(callButton?.gameObject, isMyTurn && needsToCall);
        SetActiveWithDelay(raiseButton?.gameObject, isMyTurn && canAffordRaise);
        SetActiveWithDelay(raiseSlider?.gameObject, isMyTurn && canAffordRaise);
        SetActiveWithDelay(raiseInputField?.gameObject, isMyTurn && canAffordRaise);
        SetActiveWithDelay(raisePreset1Button?.gameObject, isMyTurn && canAffordRaise);
        SetActiveWithDelay(raisePreset2Button?.gameObject, isMyTurn && canAffordRaise);
        SetActiveWithDelay(raisePreset3Button?.gameObject, isMyTurn && canAffordRaise);
        SetActiveWithDelay(raisePreset4Button?.gameObject, isMyTurn && canAffordRaise);
        
        // Update preset button labels and values when showing
        if (isMyTurn && canAffordRaise)
        {
            UpdateRaisePresetLabels();
        }
    }

    /// <summary>
    /// Sets a GameObject active/inactive with optional delay for deactivation
    /// </summary>
    private void SetActiveWithDelay(GameObject obj, bool active, float delay = 0f)
    {
        if (obj == null) return;
        
        if (active)
        {
            obj.SetActive(true);
        }
        else if (obj.activeSelf)
        {
            StartCoroutine(DeactivateAfterDelay(obj, delay));
        }
    }

    /// <summary>
    /// Deactivates a GameObject after a short delay
    /// </summary>
    private System.Collections.IEnumerator DeactivateAfterDelay(GameObject obj, float delay)
    {
        yield return new WaitForSeconds(delay);
        if (obj != null)
        {
            obj.SetActive(false);
        }
    }

    // ==================== BUTTON TEXT UPDATES ====================

    private void UpdateButtonTexts()
    {
        float amountToCall = GameManager.Instance.GetAmountToCall();
        
        // Update call button text
        if (callText != null)
        {
            if (amountToCall > 0)
            {
                callText.text = $"Call {amountToCall:F2}";
            }
            else
            {
                callText.text = "Call";
            }
        }
        
        // Update raise button text
        if (raiseText != null)
        {
            raiseText.text = $"Raise {currentRaiseAmount:F2}";
        }
    }

    // ==================== RAISE CONTROLS ====================

    private void UpdateRaiseControls()
    {
        float minRaise = GameManager.Instance.GetMinimumRaiseAmount();
        float maxRaise = GameManager.Instance.GetMaximumBetAmount();
        
        // Update slider range
        if (raiseSlider != null && !isUpdatingSlider)
        {
            raiseSlider.minValue = minRaise;
            raiseSlider.maxValue = maxRaise;
            
            // Clamp current value to new range
            if (raiseSlider.value < minRaise)
            {
                raiseSlider.value = minRaise;
            }
            else if (raiseSlider.value > maxRaise)
            {
                raiseSlider.value = maxRaise;
            }
        }
        
        // Initialize current raise amount if needed
        if (currentRaiseAmount < minRaise)
        {
            currentRaiseAmount = minRaise;
            UpdateSliderFromAmount();
            UpdateInputFieldFromAmount();
        }
    }

    private void OnSliderValueChanged(float value)
    {
        if (isUpdatingSlider) return;
        
        isUpdatingSlider = true;
        currentRaiseAmount = value;
        UpdateInputFieldFromAmount();
        UpdateButtonTexts();
        isUpdatingSlider = false;
    }
    private void OnInputFieldValueChanged(string text)
    {
        if (isUpdatingInputField) return;
        
        isUpdatingInputField = true;
        
        if (float.TryParse(text, out float value))
        {
            // Clamp to min/max
            float minRaise = GameManager.Instance.GetMinimumRaiseAmount();
            float maxRaise = GameManager.Instance.GetMaximumBetAmount();
            
            currentRaiseAmount = Mathf.Clamp(value, minRaise, maxRaise);
            UpdateSliderFromAmount();
            UpdateButtonTexts();
        }
        
        isUpdatingInputField = false;
    }

    private void UpdateSliderFromAmount()
    {
        if (raiseSlider != null)
        {
            raiseSlider.value = currentRaiseAmount;
        }
    }

    private void UpdateInputFieldFromAmount()
    {
        if (raiseInputField != null)
        {
            raiseInputField.text = currentRaiseAmount.ToString("F2");
        }
    }

    // ==================== RAISE PRESET SYSTEM ====================

    /// <summary>
    /// Returns true if no voluntary raise has been made yet this round.
    /// Preflop: currentBetToCall is still the big blind. Post-flop: currentBetToCall is 0.
    /// </summary>
    private bool IsNoRaiseYet()
    {
        if (GameManager.Instance == null) return true;
        
        float currentBet = GameManager.Instance.currentBetToCall.Value;
        float bb = GameManager.Instance.bigBlind;
        bool isPreflop = GameManager.Instance.currentPhase.Value == GameManager.GamePhase.PreFlop;
        
        // Preflop: no raise if bet is still the big blind. Post-flop: no raise if bet is 0
        return isPreflop ? currentBet <= bb : false;
    }

    /// <summary>
    /// Updates the raise preset button labels based on the current game state.
    /// No raise yet → 2BB, 3BB, 4BB, POT.  After a raise → 33%, 50%, 75%, 100%.
    /// </summary>
    private void UpdateRaisePresetLabels()
    {
        bool isPreflop = GameManager.Instance.currentPhase.Value == GameManager.GamePhase.PreFlop;
        if (IsNoRaiseYet())
        {
            if (raisePreset1Text != null) raisePreset1Text.text = "2BB";
            if (raisePreset2Text != null) raisePreset2Text.text = "3BB";
            if (raisePreset3Text != null) raisePreset3Text.text = "4BB";
            if (raisePreset4Text != null) raisePreset4Text.text = "POT";
        }
        else if (isPreflop)
        {
            if (raisePreset1Text != null) raisePreset1Text.text = "1X";
            if (raisePreset2Text != null) raisePreset2Text.text = "2X";
            if (raisePreset3Text != null) raisePreset3Text.text = "3X";
            if (raisePreset4Text != null) raisePreset4Text.text = "POT";
        }
        else
        {
            if (raisePreset1Text != null) raisePreset1Text.text = "33%";
            if (raisePreset2Text != null) raisePreset2Text.text = "50%";
            if (raisePreset3Text != null) raisePreset3Text.text = "75%";
            if (raisePreset4Text != null) raisePreset4Text.text = "100%";
        }
    }

    /// <summary>
    /// Called when a raise preset button is clicked. Index 0-3 maps to the 4 buttons.
    /// </summary>
    private void OnRaisePresetClicked(int index)
    {
        if (GameManager.Instance == null || !GameManager.Instance.hasLocalPlayer) return;
        
        float targetAmount;
        float currentBet = GameManager.Instance.currentBetToCall.Value;
        float bb = GameManager.Instance.bigBlind;
        float pot = GameManager.Instance.potOnTable.Value;
        bool isPreflop = GameManager.Instance.currentPhase.Value == GameManager.GamePhase.PreFlop;
        
        if (IsNoRaiseYet())
        {
            // BB-based presets: 2BB, 3BB, 4BB, POT
            switch (index)
            {
                case 0: targetAmount = bb * 2f; break;
                case 1: targetAmount = bb * 3f; break;
                case 2: targetAmount = bb * 4f; break;
                case 3: targetAmount = pot; break;
                default: return;
            }
        }
        else if (isPreflop)
        {
            float addToBet = 0f;
            if (currentBet > pot)
            {
                addToBet = pot;
            }
            switch (index)
            {
                case 0: targetAmount = addToBet + currentBet * 1f; break;
                case 1: targetAmount = addToBet + currentBet * 2f; break;
                case 2: targetAmount = addToBet + currentBet * 3f; break;
                case 3: targetAmount = pot; break;
                default: return;
            }
        }
        else
        {
            // Pot-percentage presets: 33%, 50%, 75%, 100%
            switch (index)
            {
                case 0: targetAmount = pot * 0.33f; break;
                case 1: targetAmount = pot * 0.50f; break;
                case 2: targetAmount = pot * 0.75f; break;
                case 3: targetAmount = pot * 1.00f; break;
                default: return;
            }
        }
        
        float minRaise = GameManager.Instance.GetMinimumRaiseAmount();
        float maxRaise = GameManager.Instance.GetMaximumBetAmount();
        
        // Clamp to valid raise range
        currentRaiseAmount = Mathf.Clamp(targetAmount, minRaise, maxRaise);
        
        UpdateSliderFromAmount();
        UpdateInputFieldFromAmount();
        UpdateButtonTexts();
    }

    /// <summary>
    /// Forces an immediate update of raise controls (called after actions)
    /// </summary>
    private void RefreshRaiseControls()
    {
        if (GameManager.Instance == null || !GameManager.Instance.hasLocalPlayer)
        {
            return;
        }
        
        float minRaise = GameManager.Instance.GetMinimumRaiseAmount();
        float maxRaise = GameManager.Instance.GetMaximumBetAmount();
        
        // Reset to minimum raise
        currentRaiseAmount = minRaise;
        
        // Update slider
        if (raiseSlider != null)
        {
            raiseSlider.minValue = minRaise;
            raiseSlider.maxValue = maxRaise;
            raiseSlider.value = minRaise;
        }
        
        // Update input field
        if (raiseInputField != null)
        {
            raiseInputField.text = minRaise.ToString("F2");
        }
    }

    // ==================== PLAYER ACTIONS ====================

    public void Fold()
    {
        GameManager.Instance.Fold();
        RefreshRaiseControls();
        UpdateButtonStates();
        UpdateButtonTexts();
        UpdateRaiseControls();
    }
    
    public void Check()
    {
        GameManager.Instance.Check();
        RefreshRaiseControls();
        UpdateButtonStates();
        UpdateButtonTexts();
        UpdateRaiseControls();
    }
    
    public void Call()
    {
        GameManager.Instance.Check(); // Check/Call use the same method
        RefreshRaiseControls();
        UpdateButtonStates();
        UpdateButtonTexts();
        UpdateRaiseControls();
    }
    
    public void Raise()
    {
        GameManager.Instance.Raise(currentRaiseAmount);
        RefreshRaiseControls();
        UpdateButtonStates();
        UpdateButtonTexts();
        UpdateRaiseControls();
    }
}
