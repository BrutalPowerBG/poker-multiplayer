using UnityEngine;
using UnityEngine.UI;

public class CardUI : MonoBehaviour
{
    [Header("Card Settings")]
    [SerializeField] private Card.Rank rank = Card.Rank.Ace;
    [SerializeField] private Card.Suit suit = Card.Suit.Hearts;
    [SerializeField] private bool isHidden = false;
    
    [Header("UI References")]
    [SerializeField] private Image frontImage;
    [SerializeField] private Image backImage;
    
    [Header("Menu Scale")]
    [SerializeField] private bool affectedByMenuScale = false;

    [Header("Theme Override")]
    [Tooltip("When >= 0, this card always uses the specified theme instead of the active one.")]
    [SerializeField] private int overrideThemeIndex = -1;

    private Card currentCard;
    private Vector3 originalScale;
    
    // Getters for card state
    public Card.Rank GetRank() => rank;
    public Card.Suit GetSuit() => suit;
    public bool GetIsHidden() => isHidden;
    public bool IsInitialized() => gameObject.activeSelf;
    public bool AffectedByMenuScale => affectedByMenuScale;
    public int OverrideThemeIndex { get => overrideThemeIndex; set => overrideThemeIndex = value; }

    private void Awake()
    {
        originalScale = transform.localScale;
    }

    private void OnEnable()
    {
        CardTheme.OnThemeChanged += RefreshTheme;
    }

    private void OnDisable()
    {
        CardTheme.OnThemeChanged -= RefreshTheme;
    }

    public void ApplyScaleFactor(float factor)
    {
        if (!affectedByMenuScale) return;
        transform.localScale = originalScale * factor;
    }

    /// <summary>
    /// Re-applies the current theme sprites without changing rank/suit/hidden state.
    /// Called automatically when the card theme changes.
    /// </summary>
    public void RefreshTheme()
    {
        if (currentCard.rank == Card.Rank.None || currentCard.suit == Card.Suit.None)
            return;
        UpdateCardUI();
    }

    // Only use no params for testing via inspector
    public void Init()
    {
        Init(rank, suit, isHidden, true);
    }
    
    public void Init(Card.Rank rank, Card.Suit suit, bool isHidden = false, bool animate = false)
    {
        this.rank = rank;
        this.suit = suit;
        this.isHidden = isHidden;

        // If card is None state (not dealt, folded, etc.), deactivate and return
        if (rank == Card.Rank.None || suit == Card.Suit.None)
        {
            gameObject.SetActive(false);
            return;
        }

        // Ensure the card is active before updating
        gameObject.SetActive(true);

        // Simply create a new card - old one will be garbage collected
        currentCard = new Card(rank, suit);

        if (animate)
        {
            StartCoroutine(InitAnimation());
        }
        else
        {
            UpdateCardUI();
        }
    }

    /// <summary>
    /// Plays the same flip-in animation as <see cref="Init"/> with <c>animate</c>,
    /// after the theme sprites have already been updated (e.g. from <see cref="CardTheme.OnThemeChanged"/>).
    /// </summary>
    public void PlayThemeChangeFlipAnimation()
    {
        StartCoroutine(RevealFrom90Coroutine());
    }

    private void UpdateCardUI()
    {
        if (CardTheme.Instance == null || backImage == null || frontImage == null) return;

        if (overrideThemeIndex >= 0)
        {
            backImage.sprite = CardTheme.Instance.GetBackImage(overrideThemeIndex);
            frontImage.sprite = CardTheme.Instance.GetCardImage(currentCard.rank, currentCard.suit, overrideThemeIndex);
        }
        else
        {
            backImage.sprite = CardTheme.Instance.backImage;
            frontImage.sprite = CardTheme.Instance.GetCardImage(currentCard.rank, currentCard.suit);
        }
        if (isHidden)
        {
            frontImage.gameObject.SetActive(false);
            backImage.gameObject.SetActive(true);
        }
        else
        {
            frontImage.gameObject.SetActive(true);
            backImage.gameObject.SetActive(false);
        }
    }

    public void Flip(bool instant = false)
    {
        if (instant)
        {
            isHidden = !isHidden;
            UpdateCardUI();
        }
        else
        {
            StartCoroutine(FlipAnimation());
        }
    }

    private System.Collections.IEnumerator FlipAnimation()
    {
        float duration = 0.25f; // 0.25 seconds for each half of the flip (0.5 total)
        float elapsed = 0f;
        
        // Get the currently visible image
        GameObject visibleImage = isHidden ? backImage.gameObject : frontImage.gameObject;
        
        // First half: Rotate from 0 to 90 degrees
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float angle = Mathf.Lerp(0f, 90f, elapsed / duration);
            visibleImage.transform.localRotation = Quaternion.Euler(0f, angle, 0f);
            yield return null;
        }
        
        // At 90 degrees, swap the card visibility
        isHidden = !isHidden;
        UpdateCardUI();
        
        // Get the now-visible image and set it to -90 degrees
        GameObject newVisibleImage = isHidden ? backImage.gameObject : frontImage.gameObject;
        newVisibleImage.transform.localRotation = Quaternion.Euler(0f, -90f, 0f);
        
        // Second half: Rotate from -90 to 0 degrees
        elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float angle = Mathf.Lerp(-90f, 0f, elapsed / duration);
            newVisibleImage.transform.localRotation = Quaternion.Euler(0f, angle, 0f);
            yield return null;
        }
        
        // Ensure we end at exactly 0 degrees
        newVisibleImage.transform.localRotation = Quaternion.Euler(0f, 0f, 0f);
    }

    private System.Collections.IEnumerator InitAnimation()
    {
        yield return RevealFrom90Coroutine();
    }

    private System.Collections.IEnumerator RevealFrom90Coroutine()
    {
        const float duration = 0.25f;
        UpdateCardUI();
        
        // Get the visible image and set it to 90 degrees
        GameObject visibleImage = isHidden ? backImage.gameObject : frontImage.gameObject;
        visibleImage.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
        
        // Rotate from 90 to 0 degrees
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float angle = Mathf.Lerp(90f, 0f, elapsed / duration);
            visibleImage.transform.localRotation = Quaternion.Euler(0f, angle, 0f);
            yield return null;
        }
        
        // Ensure we end at exactly 0 degrees
        visibleImage.transform.localRotation = Quaternion.Euler(0f, 0f, 0f);
    }
}
