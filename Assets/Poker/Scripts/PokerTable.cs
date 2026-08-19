using System.Collections.Generic;
using UnityEngine;
using TMPro;

public class PokerTable : MonoBehaviour
{
    public static PokerTable Instance { get; private set; }

    [SerializeField] private CardUI[] cardSlots = new CardUI[5];
    [SerializeField] private PlayerSlotUI[] playerSlots = new PlayerSlotUI[9];

    public GameObject potOnTableGameObject;
    TextMeshProUGUI potOnTableText;
    // Note: Pot amount is stored in GameManager.potOnTable (single source of truth)
    
    // Side pot tracking
    private List<GameObject> sidePotObjects = new List<GameObject>();

    /// <summary>
    /// Gets the player slots array
    /// </summary>
    public PlayerSlotUI[] GetPlayerSlots()
    {
        return playerSlots;
    }
    
    /// <summary>
    /// Gets the community card slots array (5 cards for flop, turn, river)
    /// </summary>
    public CardUI[] GetCommunityCardSlots()
    {
        return cardSlots;
    }

    private void Awake()
    {
        // If the old Instance was destroyed (scene reload), Unity's == null returns true,
        // but the C# reference might not be null yet. Explicitly handle this case.
        if (Instance != null && Instance != this)
        {
            // Double check: if the old Instance is actually a destroyed Unity object, take over
            if (Instance.gameObject == null)
            {
                Debug.LogWarning($"[{LogTag.Table}] Singleton takeover — reason=stale destroyed instance", this);
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

        Debug.Log($"[{LogTag.Table}] Instance initialized — name={gameObject.name}, instanceId={gameObject.GetInstanceID()}", this);
        
        // Initialize pot text component
        if (potOnTableGameObject != null)
        {
            potOnTableText = potOnTableGameObject.GetComponentInChildren<TextMeshProUGUI>();
            if (potOnTableText != null)
            {
                potOnTableText.text = "0";
            }
        }
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            Debug.Log($"[{LogTag.Table}] Instance cleared — reason=OnDestroy", this);
            Instance = null;
        }
    }

    public void RemoveNonOccupiedEmptySlotsLocally()
    {
        for (int i = 0; i < playerSlots.Length; i++)
        {
            if (playerSlots[i] != null && playerSlots[i].IsEmpty())
            {
                playerSlots[i].emptySlot.SetActive(false);
            }
        }
    }

    public void RestoreNonOccupiedEmptySlotsLocally()
    {
        for (int i = 0; i < playerSlots.Length; i++)
        {
            if (playerSlots[i] != null && playerSlots[i].IsEmpty())
            {
                playerSlots[i].emptySlot.SetActive(true);
            }
        }
    }

    // Helper struct to store card state for rotation
    private struct CardState
    {
        public bool isActive;
        public Card.Rank rank;
        public Card.Suit suit;
        public bool isHidden;
        
        public CardState(CardUI cardUI)
        {
            isActive = cardUI != null && cardUI.gameObject.activeSelf && cardUI.IsInitialized();
            rank = isActive ? cardUI.GetRank() : Card.Rank.None;
            suit = isActive ? cardUI.GetSuit() : Card.Suit.None;
            isHidden = isActive ? cardUI.GetIsHidden() : false;
            
            // Don't consider a card active if it's in None state
            if (isActive && (rank == Card.Rank.None || suit == Card.Suit.None))
            {
                isActive = false;
            }
            
            if (isActive)
            {
                Debug.Log($"[{LogTag.Table}] Card state saved — rank={rank}, suit={suit}, hidden={isHidden}");
            }
        }
    }
    
    /// <summary>
    /// Rotates all slots so the local player appears in the first slot position
    /// </summary>
    public void RotateSlotsToLocalPlayer(int localPlayerRealSlotIndex, PokerPlayer localPlayer)
    {
        Debug.Log($"[{LogTag.Table}] Rotating slots to local player — realSlot={localPlayerRealSlotIndex}, player={localPlayer.playerName}", this);
        
        // Store card states before rotation (copy actual data, not references)
        CardState[][] cardStates = new CardState[playerSlots.Length][];
        for (int i = 0; i < playerSlots.Length; i++)
        {
            if (playerSlots[i] != null)
            {
                cardStates[i] = new CardState[] 
                { 
                    new CardState(playerSlots[i].card1), 
                    new CardState(playerSlots[i].card2) 
                };
            }
        }
        
        // Calculate rotation offset - how many positions to rotate
        int rotationOffset = localPlayerRealSlotIndex;
        
        // Create a mapping of real positions to local visual positions
        // If player is at real slot 3, we rotate by -3 so they appear at visual slot 0
        for (int visualIndex = 0; visualIndex < playerSlots.Length; visualIndex++)
        {
            if (playerSlots[visualIndex] == null) continue;
            
            // Calculate which real slot should appear at this visual position
            int realIndex = (visualIndex + rotationOffset) % playerSlots.Length;
            
            // Special case: if this is the local player's real slot, use the passed-in data
            // to avoid race condition with NetworkList synchronization
            if (realIndex == localPlayerRealSlotIndex)
            {
                playerSlots[visualIndex].SetPlayerSlot(
                    localPlayer.playerName.ToString(), 
                    localPlayer.stack, 
                    localPlayer.iconIndex,
                    preserveCards: true  // Preserve cards during rotation
                );
                
                // Transfer cards from old position to new position
                if (cardStates[realIndex] != null)
                {
                    TransferCards(cardStates[realIndex], playerSlots[visualIndex]);
                }
            }
            // Find if there's another player at this real slot
            else if (GameManager.Instance.TryGetPlayerByRealSlotIndex(realIndex, out PokerPlayer playerAtRealSlot))
            {
                // This slot is occupied - update UI with player info
                playerSlots[visualIndex].SetPlayerSlot(
                    playerAtRealSlot.playerName.ToString(), 
                    playerAtRealSlot.stack, 
                    playerAtRealSlot.iconIndex,
                    preserveCards: true  // Preserve cards during rotation
                );
                
                // Transfer cards from old position to new position
                if (cardStates[realIndex] != null)
                {
                    TransferCards(cardStates[realIndex], playerSlots[visualIndex]);
                }
            }
            else
            {
                // This slot is empty - show empty slot
                playerSlots[visualIndex].SetEmptySlot();
            }
        }
    }

    /// <summary>
    /// Restores all slots to their real positions (reverses the rotation)
    /// </summary>
    public void RestoreRealSlotPositions()
    {
        Debug.Log($"[{LogTag.Table}] Restoring real slot positions", this);
        
        // Store current visual card states before restoration
        CardState[][] visualCardStates = new CardState[playerSlots.Length][];
        for (int i = 0; i < playerSlots.Length; i++)
        {
            if (playerSlots[i] != null)
            {
                visualCardStates[i] = new CardState[] 
                { 
                    new CardState(playerSlots[i].card1), 
                    new CardState(playerSlots[i].card2) 
                };
            }
        }
        
        // Calculate the rotation offset (how much we rotated by)
        int rotationOffset = 0;
        if (GameManager.Instance.hasLocalPlayer)
        {
            rotationOffset = GameManager.Instance.localPlayer.realSlotIndex;
        }
        
        // Reset all slots to show real positions
        for (int realIndex = 0; realIndex < playerSlots.Length; realIndex++)
        {
            if (playerSlots[realIndex] == null) continue;
            
            // Find if there's a player at this real slot
            if (GameManager.Instance.TryGetPlayerByRealSlotIndex(realIndex, out PokerPlayer playerAtRealSlot))
            {
                // Calculate where this player's cards currently are in the visual layout
                int oldVisualIndex = (realIndex - rotationOffset + playerSlots.Length) % playerSlots.Length;
                
                // This slot is occupied - update UI with player info
                playerSlots[realIndex].SetPlayerSlot(
                    playerAtRealSlot.playerName.ToString(), 
                    playerAtRealSlot.stack, 
                    playerAtRealSlot.iconIndex,
                    preserveCards: true  // Preserve cards during restore
                );
                
                // Transfer cards from their old visual position to their real position
                if (visualCardStates[oldVisualIndex] != null)
                {
                    TransferCards(visualCardStates[oldVisualIndex], playerSlots[realIndex]);
                }
            }
            else
            {
                // This slot is empty - show empty slot
                playerSlots[realIndex].SetEmptySlot();
            }
        }
    }
    
    /// <summary>
    /// Helper method to transfer card state from stored data to a slot
    /// </summary>
    private void TransferCards(CardState[] sourceCardStates, PlayerSlotUI targetSlot)
    {
        if (sourceCardStates == null || sourceCardStates.Length < 2) return;
        
        // Transfer card 1 state and data
        if (targetSlot.card1 != null)
        {
            if (sourceCardStates[0].isActive)
            {
                // Card was active and initialized, so restore its data
                targetSlot.card1.Init(
                    sourceCardStates[0].rank,
                    sourceCardStates[0].suit,
                    sourceCardStates[0].isHidden,
                    animate: false  // No animation during rotation
                );
                targetSlot.card1.gameObject.SetActive(true);
            }
            else
            {
                // Card was inactive, keep it inactive
                targetSlot.card1.gameObject.SetActive(false);
            }
        }
        
        // Transfer card 2 state and data
        if (targetSlot.card2 != null)
        {
            if (sourceCardStates[1].isActive)
            {
                // Card was active and initialized, so restore its data
                targetSlot.card2.Init(
                    sourceCardStates[1].rank,
                    sourceCardStates[1].suit,
                    sourceCardStates[1].isHidden,
                    animate: false  // No animation during rotation
                );
                targetSlot.card2.gameObject.SetActive(true);
            }
            else
            {
                // Card was inactive, keep it inactive
                targetSlot.card2.gameObject.SetActive(false);
            }
        }
    }

    /// <summary>
    /// Updates the pot display to show the current pot amount from GameManager
    /// </summary>
    public void UpdatePotDisplay()
    {
        if (potOnTableText != null && GameManager.Instance != null)
        {
            potOnTableText.text = GameManager.Instance.potOnTable.Value.ToString("F2");
        }
    }
    
    /// <summary>
    /// Updates the pot display with a specific amount (for compatibility)
    /// </summary>
    public void UpdatePotOnTable(float amount)
    {
        // Just update the display - amount should already be in GameManager
        if (potOnTableText != null)
        {
            potOnTableText.text = amount.ToString("F2");
        }
    }
    
    /// <summary>
    /// Collects all bets from player slot endpoints and animates them to the pot
    /// </summary>
    public void CollectPotFromSlots()
    {
        if (potOnTableGameObject == null)
        {
            Debug.LogWarning($"[{LogTag.Table}] Pot collection skipped — reason=pot GameObject not assigned", this);
            return;
        }
        
        Vector3 potPosition = potOnTableGameObject.transform.position;
        int activeBets = 0;
        int completedBets = 0;
        
        // Count how many players have active bets
        foreach (PlayerSlotUI slot in playerSlots)
        {
            if (slot != null && slot.GetCurrentBet() > 0)
            {
                activeBets++;
            }
        }
        
        if (activeBets == 0)
        {
            Debug.Log($"[{LogTag.Table}] No bets to collect from slots", this);
            UpdatePotDisplay(); // Still update the display
            return;
        }
        
        float currentPot = GameManager.Instance != null ? GameManager.Instance.potOnTable.Value : 0;
        Debug.Log($"[{LogTag.Table}] Collecting bets — activeBets={activeBets}, currentPot=${currentPot:F2}", this);
        
        // Animate each slot's bet to the pot
        foreach (PlayerSlotUI slot in playerSlots)
        {
            if (slot != null && slot.GetCurrentBet() > 0)
            {
                slot.SendBetToPot(potPosition, () =>
                {
                    completedBets++;
                    if (completedBets >= activeBets)
                    {
                        // All animations complete, update the pot display
                        UpdatePotDisplay();
                        float finalPot = GameManager.Instance != null ? GameManager.Instance.potOnTable.Value : 0;
                        if (finalPot > 0) 
                        {
                            potOnTableGameObject.SetActive(true);
                        }
                        Debug.Log($"[{LogTag.Table}] All bets collected — finalPot=${finalPot:F2}", this);
                    }
                });
            }
        }
    }
    
    /// <summary>
    /// Gives the pot to a player (handles real slot index to visual slot conversion)
    /// </summary>
    /// <param name="playerRealSlotIndex">The real slot index of the winning player</param>
    /// <param name="potAmount">The amount of pot being awarded</param>
    public void GivePotToPlayer(int playerRealSlotIndex, float potAmount)
    {
        // Calculate the visual slot index (accounting for rotation)
        int visualSlotIndex = playerRealSlotIndex;
        if (GameManager.Instance != null && GameManager.Instance.hasLocalPlayer)
        {
            PokerPlayer localPlayer = GameManager.Instance.localPlayer;
            visualSlotIndex = playerRealSlotIndex - localPlayer.realSlotIndex;
            if (visualSlotIndex < 0)
            {
                visualSlotIndex += playerSlots.Length;
            }
        }
        
        Debug.Log($"[{LogTag.Table}] Giving pot to player — realSlot={playerRealSlotIndex}, visualSlot={visualSlotIndex}, amount=${potAmount:F2}", this);
        
        // Animate pot to player and update their stack
        AnimatePotToPlayer(visualSlotIndex, potAmount, () =>
        {
            // Update player stack display after animation
            if (visualSlotIndex >= 0 && visualSlotIndex < playerSlots.Length)
            {
                PlayerSlotUI slot = playerSlots[visualSlotIndex];
                if (slot != null)
                {
                    float newStackAmount = GameManager.Instance.players[playerRealSlotIndex].stack + potAmount;
                    slot.UpdateStackDisplay(newStackAmount);
                }
            }
        });
    }
    
    // ==================== SIDE POT DISPLAY ====================
    
    /// <summary>
    /// Creates side pot visual displays. The first amount updates the main pot, 
    /// subsequent amounts create new pot GameObjects next to it.
    /// </summary>
    public void CreateSidePotDisplays(float[] potAmounts)
    {
        ClearSidePots();
        
        if (potAmounts == null || potAmounts.Length == 0) return;
        
        // First amount is the main pot - update existing display
        if (potOnTableText != null)
        {
            potOnTableText.text = potAmounts[0].ToString("F2");
        }
        if (potOnTableGameObject != null)
        {
            potOnTableGameObject.SetActive(true);
        }
        
        // Create side pot objects for additional pots
        for (int i = 1; i < potAmounts.Length; i++)
        {
            if (potOnTableGameObject != null)
            {
                GameObject sidePot = Instantiate(potOnTableGameObject, potOnTableGameObject.transform.parent);
                TextMeshProUGUI sidePotText = sidePot.GetComponentInChildren<TextMeshProUGUI>();
                if (sidePotText != null)
                {
                    sidePotText.text = potAmounts[i].ToString("F2");
                }
                sidePot.SetActive(true);
                sidePotObjects.Add(sidePot);
                Debug.Log($"[{LogTag.Table}] Side pot created — index={i}, amount=${potAmounts[i]:F2}", this);
            }
        }
    }
    
    /// <summary>
    /// Clears all side pot visual objects
    /// </summary>
    public void ClearSidePots()
    {
        foreach (var sidePot in sidePotObjects)
        {
            if (sidePot != null) Destroy(sidePot);
        }
        sidePotObjects.Clear();
    }
    
    /// <summary>
    /// Gets a specific pot GameObject by index (0 = main pot, 1+ = side pots)
    /// </summary>
    public GameObject GetPotObject(int potIndex)
    {
        if (potIndex == 0) return potOnTableGameObject;
        int sideIndex = potIndex - 1;
        if (sideIndex >= 0 && sideIndex < sidePotObjects.Count)
            return sidePotObjects[sideIndex];
        return null;
    }
    
    /// <summary>
    /// Animates a specific pot object (main or side) to a player and destroys side pot objects
    /// </summary>
    public void AnimateSpecificPotToPlayer(int potIndex, int visualSlotIndex, float potAmount, System.Action onComplete = null)
    {
        GameObject potObj = GetPotObject(potIndex);
        if (potObj == null || visualSlotIndex < 0 || visualSlotIndex >= playerSlots.Length)
        {
            Debug.LogWarning($"[{LogTag.Table}] Pot animation skipped — potIndex={potIndex}, visualSlot={visualSlotIndex}", this);
            onComplete?.Invoke();
            return;
        }
        
        PlayerSlotUI targetSlot = playerSlots[visualSlotIndex];
        if (targetSlot == null)
        {
            onComplete?.Invoke();
            return;
        }
        
        bool isSidePot = potIndex > 0;
        StartCoroutine(AnimateSpecificPotCoroutine(potObj, targetSlot, potAmount, isSidePot, onComplete));
    }
    
    /// <summary>
    /// Coroutine that animates a specific pot object to a player
    /// </summary>
    private System.Collections.IEnumerator AnimateSpecificPotCoroutine(
        GameObject potObj, PlayerSlotUI targetSlot, float potAmount, bool destroyAfter, System.Action onComplete)
    {
        if (potObj == null)
        {
            onComplete?.Invoke();
            yield break;
        }
        
        Vector3 startPosition = potObj.transform.position;
        Vector3 targetPosition = targetSlot.transform.position;
        
        potObj.SetActive(true);
        
        float duration = 0.8f;
        float elapsed = 0f;
        
        while (elapsed < duration)
        {
            if (potObj == null) break;
            elapsed += Time.deltaTime;
            float t = elapsed / duration;
            t = t * t * (3f - 2f * t); // Smoothstep
            potObj.transform.position = Vector3.Lerp(startPosition, targetPosition, t);
            yield return null;
        }
        
        if (potObj != null)
        {
            potObj.transform.position = targetPosition;
        }
        
        yield return new WaitForSeconds(0.2f);
        
        if (destroyAfter)
        {
            // Side pot - destroy after animation
            if (potObj != null) Destroy(potObj);
        }
        else
        {
            // Main pot - reset position and hide
            if (potObj != null)
            {
                potObj.transform.position = startPosition;
                UpdatePotOnTable(0);
                potObj.SetActive(false);
            }
        }
        
        onComplete?.Invoke();
        Debug.Log($"[{LogTag.Table}] Pot delivered — amount=${potAmount:F2}, slot={targetSlot.slotIndex}", this);
    }
    
    /// <summary>
    /// Animates the pot from the table to a specific player's slot
    /// </summary>
    /// <param name="visualSlotIndex">The visual slot index of the winning player</param>
    /// <param name="potAmount">The amount of pot being awarded</param>
    /// <param name="onComplete">Callback when animation completes</param>
    public void AnimatePotToPlayer(int visualSlotIndex, float potAmount, System.Action onComplete = null)
    {
        if (potOnTableGameObject == null)
        {
            Debug.LogWarning($"[{LogTag.Table}] Pot animation skipped — reason=pot GameObject not assigned", this);
            onComplete?.Invoke();
            return;
        }
        
        if (visualSlotIndex < 0 || visualSlotIndex >= playerSlots.Length)
        {
            Debug.LogError($"[{LogTag.Table}] Pot animation failed — reason=invalid visual slot index, visualSlot={visualSlotIndex}", this);
            onComplete?.Invoke();
            return;
        }
        
        PlayerSlotUI targetSlot = playerSlots[visualSlotIndex];
        if (targetSlot == null)
        {
            Debug.LogError($"[{LogTag.Table}] Pot animation failed — reason=no PlayerSlotUI at target, visualSlot={visualSlotIndex}", this);
            onComplete?.Invoke();
            return;
        }
        
        Debug.Log($"[{LogTag.Table}] Animating pot to player — amount=${potAmount:F2}, visualSlot={visualSlotIndex}", this);
        StartCoroutine(AnimatePotToPlayerCoroutine(targetSlot, potAmount, onComplete));
    }
    
    /// <summary>
    /// Coroutine that animates the pot to the winning player
    /// </summary>
    private System.Collections.IEnumerator AnimatePotToPlayerCoroutine(PlayerSlotUI targetSlot, float potAmount, System.Action onComplete)
    {
        if (potOnTableGameObject == null)
        {
            onComplete?.Invoke();
            yield break;
        }
        
        // Store initial position
        Vector3 startPosition = potOnTableGameObject.transform.position;
        Vector3 targetPosition = targetSlot.transform.position;
        
        // Ensure pot is visible
        potOnTableGameObject.SetActive(true);
        
        float duration = 0.8f; // Animation duration
        float elapsed = 0f;
        
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / duration;
            // Use ease-in-out curve for smooth motion
            t = t * t * (3f - 2f * t); // Smoothstep
            
            potOnTableGameObject.transform.position = Vector3.Lerp(startPosition, targetPosition, t);
            yield return null;
        }
        
        // Ensure we end at exact position
        potOnTableGameObject.transform.position = targetPosition;
        
        // Small delay at player position
        yield return new WaitForSeconds(0.2f);
        
        // Reset pot to original position
        potOnTableGameObject.transform.position = startPosition;
        
        // Note: Pot amount is cleared in GameManager.AwardPotToPlayer
        // Just update the display to 0
        UpdatePotOnTable(0);
        potOnTableGameObject.SetActive(false);

        // Call completion callback
        onComplete?.Invoke();
        
        Debug.Log($"[{LogTag.Table}] Pot delivered — amount=${potAmount:F2}, slot={targetSlot.slotIndex}", this);
    }
}
