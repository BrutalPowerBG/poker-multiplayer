using UnityEngine;
using UnityEngine.UI;
using TMPro;
public class PlayerSlotUI : MonoBehaviour
{
    private const float ACTION_DISPLAY_DURATION = 1f;
    public int slotIndex;
    public GameObject player;
    public GameObject namePlate;
    public GameObject emptySlot;
    public Image playerIcon;
    public Image timer;
    public GameObject foldedOverlay;
    public GameObject actionDisplay;
    public TextMeshProUGUI playerName;
    public TextMeshProUGUI playerStackText;
    public CardUI card1;
    public CardUI card2;
    public GameObject betStartPoint;
    public GameObject betEndPoint;
    public TextMeshProUGUI betStartPointText;
    public TextMeshProUGUI betEndPointText;
    [Range(0f, 1f)]
    [Tooltip("Position of bet endpoint between start point (0) and poker table (1)")]
    private float betEndPointDistance = 0.4f;

    // Chat bubble
    [Header("Chat")]
    public ChatBubble chatBubble;

    // Time bank UI
    [Header("Time Bank")]
    public GameObject timeBankDisplay;
    public TextMeshProUGUI timeBankSecondsText;
    public Image timeBankIcon;
    public Image timeBankLight;

    public bool isEmpty = true;
    public float timerValue = 15;
    public float timerMaxValue = 15;
    private bool isTimerRunning = false;
    private bool isTimerPaused = false;
    private Coroutine timerCoroutine; // Reference to the timer coroutine
    
    // Time bank state
    private float timeBankRemaining;
    private float timeBankMaxForSession; // cached max for depletion ratio
    private bool isTimeBankActive = false;
    private Coroutine timeBankWobbleCoroutine;
    private Coroutine timeBankCountdownCoroutine;
    private Coroutine timeBankLightCoroutine;
    private Quaternion timeBankIconDefaultRotation;
    private Color timeBankIconDefaultColor;
    public float URGENCY_THRESHOLD = 0.35f; // switch at 35% remaining
    
    // Time bank light constants
    private static readonly Color TIMEBANK_LIGHT_GREEN = new Color(0f, 232f/255f, 41f/255f, 0f); // #00E829
    private static readonly Color TIMEBANK_LIGHT_RED = new Color(147f/255f, 22f/255f, 22f/255f, 0f);   // #931616
    
    // Action display
    private TextMeshProUGUI actionDisplayText;
    private Coroutine actionDisplayCoroutine;
    
    // Betting system
    private Vector3 betStartPointInitialPosition;
    private Vector3 betEndPointInitialPosition; // Store endpoint's calculated position
    private float betEndPointCurrentAmount = 0f; // Tracks the total bet at endpoint
    private TextMeshProUGUI stackText; // Cached reference to stack text component

    private void Awake()
    {
        // Cache the stack text component
        if (namePlate != null)
        {
            stackText = namePlate.transform.Find("Stack")?.GetComponent<TextMeshProUGUI>();
        }
        
        // Cache the action display text component
        if (actionDisplay != null)
        {
            actionDisplayText = actionDisplay.GetComponentInChildren<TextMeshProUGUI>();
            actionDisplay.SetActive(false);
        }
        
        // Cache time bank defaults
        if (timeBankIcon != null)
        {
            timeBankIconDefaultRotation = timeBankIcon.transform.localRotation;
            timeBankIconDefaultColor = timeBankIcon.color;
        }
        if (timeBankDisplay != null)
        {
            timeBankDisplay.SetActive(false);
        }
        
        // Store the initial position of betStartPoint for resetting later
        if (betStartPoint != null)
        {
            betStartPointInitialPosition = betStartPoint.transform.position;
        }
        
        // Initialize betting UI
        InitializeBetUI();
    }
    
    private void Start()
    {
        TrySetBetEndPointPosition();
    }
    
    private void TrySetBetEndPointPosition()
    {
        // Position betEndPoint on the line between betStartPoint and PokerTable
        if (betStartPoint != null && betEndPoint != null && PokerTable.Instance != null)
        {
            Vector3 startPos = betStartPoint.transform.position;
            Vector3 tablePos = PokerTable.Instance.transform.position;
            
            // Calculate position along the line based on betEndPointDistance (0 = start, 1 = table)
            Vector3 endPointPosition = Vector3.Lerp(startPos, tablePos, betEndPointDistance);
            betEndPoint.transform.position = endPointPosition;
            
            // Store this as the initial position for resetting later
            betEndPointInitialPosition = endPointPosition;
            
            Debug.Log($"[{LogTag.Player}] Bet endpoint positioned — slot={slotIndex}, distance={betEndPointDistance * 100:F0}%", this);
        }
        else if (betEndPoint != null)
        {
            // Provide detailed debugging information
            string missingRefs = "";
            if (betStartPoint == null) missingRefs += "betStartPoint ";
            if (betEndPoint == null) missingRefs += "betEndPoint ";
            if (PokerTable.Instance == null) missingRefs += "PokerTable.Instance ";
            
            Debug.LogWarning($"[{LogTag.Player}] Bet endpoint position failed — slot={slotIndex}, missingRefs={missingRefs.Trim()}", this);
        }
    }
    
    private void InitializeBetUI()
    {
        // Ensure betEndPoint is initially inactive
        if (betEndPoint != null)
        {
            betEndPoint.SetActive(false);
        }
        
        // Initialize bet texts to 0
        if (betStartPointText != null)
        {
            betStartPointText.text = "0";
        }
        if (betEndPointText != null)
        {
            betEndPointText.text = "0";
        }
    }

    public bool IsEmpty()
    {
        return isEmpty;
    }
    public void SetPlayerSlot(string name, float stack, int iconIndex, bool preserveCards = false)
    {
        Debug.Log($"[{LogTag.Player}] Slot occupied — slot={slotIndex}, name={name}, stack=${stack:F2}, iconIndex={iconIndex}", this);
        
        // Guard against uninitialized serialized references (can happen on scene reload)
        if (namePlate == null || player == null || emptySlot == null)
        {
            Debug.LogWarning($"[{LogTag.Player}] Serialized refs not ready — slot={slotIndex}, namePlate={namePlate != null}, player={player != null}, emptySlot={emptySlot != null}", this);
            return;
        }
        
        isEmpty = false;
        // Get TextMeshProUGUI components from Name and Stack GameObjects
        Transform nameTransform = namePlate.transform.Find("Name");
        Transform stackTransform = namePlate.transform.Find("Stack");
        
        if (nameTransform != null)
            nameTransform.GetComponent<TextMeshProUGUI>().text = name;
        if (stackTransform != null)
            stackTransform.GetComponent<TextMeshProUGUI>().text = stack.ToString();

        // Set the player avatar sprite based on the icon index
        if (playerIcon != null && PlayerAssets.Instance != null)
        {
            Sprite avatarSprite = PlayerAssets.Instance.GetSprite(iconIndex);
            if (avatarSprite != null)
                playerIcon.sprite = avatarSprite;
        }

        player.gameObject.SetActive(true);
        namePlate.gameObject.SetActive(true);
        emptySlot.gameObject.SetActive(false);
        
        // Hide cards unless explicitly preserving them (e.g., during rotation)
        if (!preserveCards)
        {
            HideCards();
        }
        
        // Optionally set the player icon
        // playerIcon.sprite = someSprite;
    }

    public void SetEmptySlot()
    {
        isEmpty = true;
        
        // Stop all running coroutines (bet animations, timer, etc.)
        StopAllCoroutines();
        timerCoroutine = null;
        actionDisplayCoroutine = null;
        isTimerRunning = false;
        isTimerPaused = false;
        StopTimeBankVisual();
        
        // Hide action display
        if (actionDisplay != null) actionDisplay.SetActive(false);
        
        if (player != null) player.gameObject.SetActive(false);
        if (namePlate != null) namePlate.gameObject.SetActive(false);
        if (emptySlot != null) emptySlot.gameObject.SetActive(true);
        
        // Hide cards when slot is empty
        HideCards();
        
        // Clear bet UI so no lingering bet displays remain
        ResetBetUI();
        
        // Clear folded overlay
        if (foldedOverlay != null) foldedOverlay.SetActive(false);
        
        // Clear timer visual
        if (timer != null)
        {
            timer.fillAmount = 1f;
            timer.gameObject.SetActive(false);
        }
    }

    public void SetPlayerStack(float stack)
    {
        playerStackText.text = stack.ToString();
    }
    
    /// <summary>
    /// Hides both cards for this player slot
    /// </summary>
    private void HideCards()
    {
        if (card1 != null)
        {
            card1.gameObject.SetActive(false);
        }
        
        if (card2 != null)
        {
            card2.gameObject.SetActive(false);
        }
    }


    /// <summary>
    /// Called when an empty slot is clicked - opens the buy-in panel
    /// </summary>
    public void OnEmptySlotClicked()
    {
        if (isEmpty)
        {
            if (BuyIn.Instance == null)
            {
                Debug.LogError($"[{LogTag.Player}] Empty slot click failed — reason=BuyIn.Instance is null, ensure BuyIn GameObject is active", this);
                return;
            }
            BuyIn.Instance.OpenBuyInPanel(this);
        }
    }

    /// <summary>
    /// Starts the countdown timer from timerMaxValue to 0
    /// </summary>
    public void StartTimer()
    {
        timer.gameObject.SetActive(true);
        if (isTimerRunning && timerCoroutine != null)
        {
            StopCoroutine(timerCoroutine); // Stop any existing timer
        }
        
        // Sync timer value with server's turn time limit
        if (GameManager.Instance != null)
        {
            timerMaxValue = GameManager.Instance.playerTurnTimeLimit;
        }
        
        timerValue = timerMaxValue;
        isTimerRunning = true;
        timerCoroutine = StartCoroutine(TimerCountdown());
    }

    /// <summary>
    /// Stops the timer and time bank visual. Called externally (e.g. turn change).
    /// </summary>
    public void StopTimer()
    {
        isTimerRunning = false;
        isTimerPaused = false;
        timerValue = timerMaxValue;
        
        if (timer != null)
        {
            timer.fillAmount = timerValue / timerMaxValue;
            timer.gameObject.SetActive(false);
        }
        
        // Only stop the timer coroutine, not all coroutines (to preserve bet animations)
        if (timerCoroutine != null)
        {
            StopCoroutine(timerCoroutine);
            timerCoroutine = null;
        }
        
        StopTimeBankVisual();
    }

    /// <summary>
    /// Pauses the timer
    /// </summary>
    public void PauseTimer()
    {
        if (isTimerRunning && !isTimerPaused)
        {
            isTimerPaused = true;
            Debug.Log($"[{LogTag.Player}] Timer paused — slot={slotIndex}, remaining={timerValue:F1}s", this);
        }
    }

    /// <summary>
    /// Resumes the timer
    /// </summary>
    public void ResumeTimer()
    {
        if (isTimerRunning && isTimerPaused)
        {
            isTimerPaused = false;
            Debug.Log($"[{LogTag.Player}] Timer resumed — slot={slotIndex}, remaining={timerValue:F1}s", this);
        }
    }

    private System.Collections.IEnumerator TimerCountdown()
    {
        while (timerValue > 0f)
        {
            // Only count down if not paused
            if (!isTimerPaused)
            {
                timerValue -= Time.deltaTime;
                
                // Clamp to 0 to avoid negative values
                if (timerValue < 0f)
                {
                    timerValue = 0f;
                }
                
                // Update fill amount (1 when full, 0 when empty)
                timer.fillAmount = timerValue / timerMaxValue;
            }
            
            yield return null;
        }
        
        // Timer finished
        isTimerRunning = false;
        isTimerPaused = false;
        timerCoroutine = null; // Clear the reference
        OnTimerExpired();
    }

    /// <summary>
    /// Called when the client-side timer naturally reaches 0.
    /// Only hides the main timer visual — does NOT touch the time bank,
    /// because the server may be about to start the time bank phase via RPC.
    /// </summary>
    private void OnTimerExpired()
    {
        Debug.Log($"[{LogTag.Player}] Timer expired — slot={slotIndex}, scope=visual only", this);
        
        isTimerRunning = false;
        isTimerPaused = false;
        
        if (timer != null)
        {
            timer.fillAmount = 0f;
            timer.gameObject.SetActive(false);
        }
        
        // timerCoroutine is already null (set before this call in TimerCountdown)
        // Do NOT call StopTimeBankVisual() — the server controls the time bank lifecycle.
    }
    
    /// <summary>
    /// Initiates a bet animation, moving chips from start point to end point
    /// </summary>
    /// <param name="betAmount">The amount being bet</param>
    public void PlaceBet(float betAmount)
    {
        if (betAmount <= 0)
        {
            Debug.LogWarning($"[{LogTag.Player}] Bet rejected — slot={slotIndex}, reason=invalid amount, amount=${betAmount:F2}", this);
            return;
        }
        
        StartCoroutine(AnimateBet(betAmount));
    }
    
    /// <summary>
    /// Coroutine that animates the bet from start point to end point
    /// </summary>
    private System.Collections.IEnumerator AnimateBet(float betAmount)
    {
        // 1. Set the betStartPointText to the bet amount
        if (betStartPointText != null)
        {
            betStartPointText.text = betAmount.ToString("F2");
        }
        
        // Ensure betStartPoint is visible
        if (betStartPoint != null)
        {
            betStartPoint.SetActive(true);
        }
        
        // Small delay to show the bet amount at start point
        yield return new WaitForSeconds(0.2f);
        
        // 2. Gradually move betStartPoint to betEndPoint
        if (betStartPoint != null && betEndPoint != null)
        {
            float duration = 0.5f; // Animation duration
            float elapsed = 0f;
            Vector3 startPosition = betStartPoint.transform.position;
            Vector3 endPosition = betEndPoint.transform.position;
            
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = elapsed / duration;
                // Use ease-out curve for smooth deceleration
                t = 1f - Mathf.Pow(1f - t, 3f);
                
                betStartPoint.transform.position = Vector3.Lerp(startPosition, endPosition, t);
                yield return null;
            }
            
            // Ensure we end at exact position
            betStartPoint.transform.position = endPosition;
        }
        
        // 3. Set betEndPoint to active and transfer the amount
        if (betEndPoint != null)
        {
            betEndPoint.SetActive(true);
        }
        
        // 4. Transfer the bet amount to endpoint
        betEndPointCurrentAmount += betAmount;
        if (betEndPointText != null)
        {
            betEndPointText.text = betEndPointCurrentAmount.ToString("F2");
        }
        
        // 5. Disable betStartPoint (visually, it's now "at" the endpoint)
        if (betStartPoint != null)
        {
            betStartPoint.SetActive(false);
        }
        
        // 6. Reset betStartPoint text to 0 and move back to initial position
        if (betStartPointText != null)
        {
            betStartPointText.text = "0";
        }
        
        if (betStartPoint != null)
        {
            betStartPoint.transform.position = betStartPointInitialPosition;
        }
    }
    
    /// <summary>
    /// Resets the betting UI for a new betting round
    /// </summary>
    public void ResetBetUI()
    {
        // Reset endpoint amount
        betEndPointCurrentAmount = 0f;
        
        // Reset texts
        if (betStartPointText != null)
        {
            betStartPointText.text = "0";
        }
        if (betEndPointText != null)
        {
            betEndPointText.text = "0";
        }
        
        // Hide betEndPoint
        if (betEndPoint != null)
        {
            betEndPoint.SetActive(false);
        }
        
        // Reset betStartPoint position and hide it
        if (betStartPoint != null)
        {
            betStartPoint.transform.position = betStartPointInitialPosition;
            betStartPoint.SetActive(false);
        }
    }
    
    /// <summary>
    /// Gets the current bet amount at the endpoint
    /// </summary>
    public float GetCurrentBet()
    {
        return betEndPointCurrentAmount;
    }
    
    /// <summary>
    /// Updates the player's stack display text
    /// </summary>
    /// <param name="stackAmount">The new stack amount to display</param>
    public void UpdateStackDisplay(float stackAmount)
    {
        if (stackText != null)
        {
            stackText.text = stackAmount.ToString("F2");
        }
        else
        {
            Debug.LogWarning($"[{LogTag.Player}] Stack display failed — slot={slotIndex}, reason=stack text component not found", this);
        }
    }
    
    /// <summary>
    /// Animates the bet from endpoint to the pot on the table
    /// </summary>
    /// <param name="potPosition">The world position of the pot on the table</param>
    /// <param name="onComplete">Optional callback when animation completes</param>
    public void SendBetToPot(Vector3 potPosition, System.Action onComplete = null)
    {
        // Only animate if there's an active bet at the endpoint
        if (betEndPointCurrentAmount > 0 && betEndPoint != null)
        {
            StartCoroutine(AnimateBetToPot(potPosition, onComplete));
        }
        else
        {
            // No bet to send, but still call the callback
            onComplete?.Invoke();
        }
    }
    
    /// <summary>
    /// Coroutine that animates chips from betEndPoint to the pot
    /// </summary>
    private System.Collections.IEnumerator AnimateBetToPot(Vector3 potPosition, System.Action onComplete)
    {
        if (betEndPoint == null)
        {
            onComplete?.Invoke();
            yield break;
        }
        
        // Ensure betEndPoint is visible during animation
        betEndPoint.SetActive(true);
        
        float duration = 0.6f; // Animation duration
        float elapsed = 0f;
        Vector3 startPosition = betEndPoint.transform.position;
        
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / duration;
            // Use ease-in curve for acceleration towards pot
            t = Mathf.Pow(t, 2f);
            
            betEndPoint.transform.position = Vector3.Lerp(startPosition, potPosition, t);
            yield return null;
        }
        
        // Ensure we end at exact position
        betEndPoint.transform.position = potPosition;
        
        // Small delay at pot position
        yield return new WaitForSeconds(0.1f);
        
        // Hide betEndPoint
        betEndPoint.SetActive(false);
        
        // Clear the bet at endpoint
        betEndPointCurrentAmount = 0f;
        if (betEndPointText != null)
        {
            betEndPointText.text = "0";
        }
        
        // Reset betEndPoint position to its original calculated position
        if (betEndPoint != null)
        {
            betEndPoint.transform.position = betEndPointInitialPosition;
        }
        
        // Call completion callback
        onComplete?.Invoke();
    }
    
    /// <summary>
    /// Sets the bet display at the endpoint directly without animation.
    /// Used for state sync to newly connected clients.
    /// </summary>
    public void SetBetDisplay(float betAmount)
    {
        if (betAmount <= 0) return;
        
        // Show bet at endpoint without animation
        if (betEndPoint != null)
        {
            betEndPoint.SetActive(true);
        }
        betEndPointCurrentAmount = betAmount;
        if (betEndPointText != null)
        {
            betEndPointText.text = betAmount.ToString("F2");
        }
    }
    
    // ==================== TIME BANK ====================
    
    /// <summary>
    /// Sets the time bank remaining display (static, no countdown).
    /// Called when a player first sits down or when their bank changes between hands.
    /// </summary>
    public void SetTimeBankDisplay(float seconds)
    {
        timeBankRemaining = Mathf.Max(0f, seconds);
        if (timeBankSecondsText != null)
            timeBankSecondsText.text = Mathf.CeilToInt(timeBankRemaining).ToString();
    }
    
    /// <summary>
    /// Starts the time bank countdown with wobble animation.
    /// Called by the server RPC when the main timer expires.
    /// </summary>
    public void StartTimeBankCountdown(float remaining)
    {
        timeBankRemaining = Mathf.Max(0f, remaining);
        isTimeBankActive = true;
        
        // Cache the max for depletion ratio (use GameManager's total if available)
        if (GameManager.Instance != null && GameManager.Instance.playerTimeBankTotal > 0f)
            timeBankMaxForSession = GameManager.Instance.playerTimeBankTotal;
        else if (timeBankMaxForSession <= 0f)
            timeBankMaxForSession = remaining; // fallback
        
        if (timeBankDisplay != null)
            timeBankDisplay.SetActive(true);
        
        if (timeBankSecondsText != null)
            timeBankSecondsText.text = Mathf.CeilToInt(timeBankRemaining).ToString();
        
        // Start wobble animation
        if (timeBankWobbleCoroutine != null)
            StopCoroutine(timeBankWobbleCoroutine);
        timeBankWobbleCoroutine = StartCoroutine(TimeBankWobbleLoop());
        
        // Start countdown for the seconds text
        if (timeBankCountdownCoroutine != null)
            StopCoroutine(timeBankCountdownCoroutine);
        timeBankCountdownCoroutine = StartCoroutine(TimeBankCountdown());
        
        // Start pulsing light
        if (timeBankLightCoroutine != null)
            StopCoroutine(timeBankLightCoroutine);
        if (timeBankLight != null)
        {
            timeBankLight.gameObject.SetActive(true);
            timeBankLightCoroutine = StartCoroutine(TimeBankLightPulse());
        }
    }
    
    /// <summary>
    /// Updates the time bank display each frame while active (called from TimerCountdown or externally).
    /// </summary>
    public void UpdateTimeBankDisplay(float remaining)
    {
        timeBankRemaining = Mathf.Max(0f, remaining);
        if (timeBankSecondsText != null)
            timeBankSecondsText.text = Mathf.CeilToInt(timeBankRemaining).ToString();
    }
    
    /// <summary>
    /// Stops the time bank visual and wobble animation.
    /// </summary>
    public void StopTimeBankVisual()
    {
        isTimeBankActive = false;
        
        if (timeBankWobbleCoroutine != null)
        {
            StopCoroutine(timeBankWobbleCoroutine);
            timeBankWobbleCoroutine = null;
        }
        
        if (timeBankCountdownCoroutine != null)
        {
            StopCoroutine(timeBankCountdownCoroutine);
            timeBankCountdownCoroutine = null;
        }
        
        if (timeBankLightCoroutine != null)
        {
            StopCoroutine(timeBankLightCoroutine);
            timeBankLightCoroutine = null;
        }
        
        // Reset icon to defaults
        if (timeBankIcon != null)
        {
            timeBankIcon.transform.localRotation = timeBankIconDefaultRotation;
            timeBankIcon.color = timeBankIconDefaultColor;
        }
        
        // Reset and hide light
        if (timeBankLight != null)
        {
            Color c = timeBankLight.color;
            c.a = 0f;
            timeBankLight.color = c;
            timeBankLight.gameObject.SetActive(false);
        }
        
        if (timeBankDisplay != null)
            timeBankDisplay.SetActive(false);
    }
    
    /// <summary>
    /// Wobble animation loop for the time bank icon.
    /// Waits idle until 3/4 of the time bank is depleted, then starts wobbling:
    ///   0.3s anticlockwise + green →  0.3s clockwise + red →  0.2s return →  2.0s rest
    /// </summary>
    private System.Collections.IEnumerator TimeBankWobbleLoop()
    {
        const float wobbleAngle = 12f;
        const float wobbleDuration = 0.3f;
        const float restDuration = 2.0f;
        
        // Wait until 3/4 depleted before wobbling
        while (isTimeBankActive)
        {
            float ratio = (timeBankMaxForSession > 0f)
                ? timeBankRemaining / timeBankMaxForSession : 0f;
            if (ratio <= URGENCY_THRESHOLD) break;
            yield return null;
        }
        
        // Now wobble for the remaining time
        while (isTimeBankActive)
        {
            // Phase 1: default → green, rotate anticlockwise
            yield return StartCoroutine(WobblePhase(wobbleAngle, timeBankIconDefaultColor, Color.green, wobbleDuration));
            if (!isTimeBankActive) yield break;
            
            // Phase 2: white → red, rotate clockwise
            yield return StartCoroutine(WobblePhase(-wobbleAngle, Color.white, Color.red, wobbleDuration));
            if (!isTimeBankActive) yield break;
            
            // Phase 3: red → default, return to default rotation, then rest
            if (timeBankIcon != null)
            {
                Quaternion startRot = timeBankIcon.transform.localRotation;
                Color startColor = timeBankIcon.color;
                const float returnDuration = 0.2f;
                float returnElapsed = 0f;
                while (returnElapsed < returnDuration && isTimeBankActive)
                {
                    returnElapsed += Time.deltaTime;
                    float t = Mathf.SmoothStep(0f, 1f, returnElapsed / returnDuration);
                    timeBankIcon.transform.localRotation = Quaternion.Slerp(startRot, timeBankIconDefaultRotation, t);
                    timeBankIcon.color = Color.Lerp(startColor, timeBankIconDefaultColor, t);
                    yield return null;
                }
                timeBankIcon.transform.localRotation = timeBankIconDefaultRotation;
                timeBankIcon.color = timeBankIconDefaultColor;
            }
            if (!isTimeBankActive) yield break;
            
            float restElapsed = 0f;
            while (restElapsed < restDuration && isTimeBankActive)
            {
                restElapsed += Time.deltaTime;
                yield return null;
            }
        }
    }
    
    /// <summary>
    /// Smoothly rotates the time bank icon to the target angle and lerps color from startColor to endColor.
    /// </summary>
    private System.Collections.IEnumerator WobblePhase(float targetAngle, Color startColor, Color endColor, float duration)
    {
        if (timeBankIcon == null) yield break;
        
        Quaternion startRot = timeBankIcon.transform.localRotation;
        Quaternion endRot = timeBankIconDefaultRotation * Quaternion.Euler(0f, 0f, targetAngle);
        
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.SmoothStep(0f, 1f, elapsed / duration);
            timeBankIcon.transform.localRotation = Quaternion.Slerp(startRot, endRot, t);
            timeBankIcon.color = Color.Lerp(startColor, endColor, t);
            yield return null;
        }
        
        timeBankIcon.transform.localRotation = endRot;
        timeBankIcon.color = endColor;
    }
    
    /// <summary>
    /// Client-side countdown that ticks the time bank seconds text every frame.
    /// </summary>
    private System.Collections.IEnumerator TimeBankCountdown()
    {
        while (timeBankRemaining > 0f && isTimeBankActive)
        {
            timeBankRemaining -= Time.deltaTime;
            if (timeBankRemaining < 0f) timeBankRemaining = 0f;
            
            if (timeBankSecondsText != null)
                timeBankSecondsText.text = Mathf.CeilToInt(timeBankRemaining).ToString();
            
            yield return null;
        }
        
        timeBankCountdownCoroutine = null;
    }
    
    /// <summary>
    /// Pulsing glow for the time bank light.
    /// Green (#00E829) while more than 1/4 of the bank remains, red (#931616) after.
    /// Pulse half-cycle: 2s while green, 1s while red.
    /// </summary>
    private System.Collections.IEnumerator TimeBankLightPulse()
    {
        if (timeBankLight == null) yield break;
        
        const float HALF_CYCLE_SLOW = 2f;
        const float HALF_CYCLE_FAST = 0.5f;

        
        while (isTimeBankActive)
        {
            float ratio = (timeBankMaxForSession > 0f)
                ? Mathf.Clamp01(timeBankRemaining / timeBankMaxForSession)
                : 0f;
            
            bool urgent = ratio <= URGENCY_THRESHOLD;
            Color baseColor = urgent ? TIMEBANK_LIGHT_RED : TIMEBANK_LIGHT_GREEN;
            // Lerp half-cycle duration from slow to fast as bank depletes
            float halfCycle = Mathf.Lerp(HALF_CYCLE_FAST, HALF_CYCLE_SLOW, ratio);
            
            // Fade alpha 0 → 1
            yield return StartCoroutine(TimeBankLightFade(baseColor, 0f, 1f, halfCycle));
            if (!isTimeBankActive) yield break;
            
            // Fade alpha 1 → 0
            yield return StartCoroutine(TimeBankLightFade(baseColor, 1f, 0f, halfCycle));
        }
        
        timeBankLightCoroutine = null;
    }
    
    /// <summary>
    /// Fades the timeBankLight alpha between two values over a duration.
    /// </summary>
    private System.Collections.IEnumerator TimeBankLightFade(Color baseColor, float fromAlpha, float toAlpha, float duration)
    {
        float elapsed = 0f;
        while (elapsed < duration && isTimeBankActive)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.SmoothStep(0f, 1f, elapsed / duration);
            
            baseColor.a = Mathf.Lerp(fromAlpha, toAlpha, t);
            timeBankLight.color = baseColor;
            yield return null;
        }
    }

    // ==================== ACTION DISPLAY ====================
    
    /// <summary>
    /// Briefly displays the action the player just took (e.g. CALL, FOLD, RAISE).
    /// The actionDisplay object is shown for ACTION_DISPLAY_DURATION seconds.
    /// </summary>
    public void ShowAction(string action)
    {
        if (actionDisplay == null || actionDisplayText == null) return;
        
        // Cancel any previous display timer
        if (actionDisplayCoroutine != null)
        {
            StopCoroutine(actionDisplayCoroutine);
        }
        
        actionDisplayText.text = action.ToUpper();
        actionDisplay.SetActive(true);
        actionDisplayCoroutine = StartCoroutine(HideActionAfterDelay());
    }
    
    private System.Collections.IEnumerator HideActionAfterDelay()
    {
        yield return new WaitForSeconds(ACTION_DISPLAY_DURATION);
        
        if (actionDisplay != null)
        {
            actionDisplay.SetActive(false);
        }
        actionDisplayCoroutine = null;
    }
    
    /// <summary>
    /// Shows the folded state: activates overlay and dims cards
    /// </summary>
    public void ShowFoldedState()
    {
        // Activate folded overlay
        if (foldedOverlay != null)
        {
            foldedOverlay.SetActive(true);
        }
        
        // Dim cards to 160/255 alpha
        SetCardsAlpha(160f / 255f);
        
        Debug.Log($"[{LogTag.Player}] Folded state shown — slot={slotIndex}", this);
    }
    
    /// <summary>
    /// Clears the folded state: deactivates overlay and restores card alpha
    /// </summary>
    public void ClearFoldedState()
    {
        // Deactivate folded overlay
        if (foldedOverlay != null)
        {
            foldedOverlay.SetActive(false);
        }
        
        // Restore cards to full alpha
        SetCardsAlpha(1f);
        
        Debug.Log($"[{LogTag.Player}] Folded state cleared — slot={slotIndex}", this);
    }
    
    /// <summary>
    /// Sets the alpha of both card images
    /// </summary>
    private void SetCardsAlpha(float alpha)
    {
        if (card1 != null)
        {
            SetCardAlpha(card1, alpha);
        }
        
        if (card2 != null)
        {
            SetCardAlpha(card2, alpha);
        }
    }
    
    /// <summary>
    /// Sets the alpha of a single card's front and back images
    /// </summary>
    private void SetCardAlpha(CardUI cardUI, float alpha)
    {
        if (cardUI == null) return;
        
        // Get all Image components in the card (front and back)
        Image[] images = cardUI.GetComponentsInChildren<Image>(true);
        foreach (var image in images)
        {
            Color color = image.color;
            color.a = alpha;
            image.color = color;
        }
    }
}
