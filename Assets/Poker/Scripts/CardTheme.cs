using System;
using UnityEngine;

[System.Serializable]
public class CardThemeData
{
    public string themeName = "Unnamed Theme";
    [HideInInspector]
    public Texture2D spriteSheetTexture; // Used by editor script only
    public Sprite[] sprites; // All card sprites for this theme
}

public class CardTheme : MonoBehaviour
{
    public static CardTheme Instance { get; private set; }

    /// <summary>Fired after the theme index changes and sprites are resolved.
    /// All active CardUI instances subscribe to this to refresh their visuals.</summary>
    public static event Action OnThemeChanged;

    private const string PREF_KEY = "CardThemeIndex";

    public CardThemeData[] cardThemes; // Multiple themes, each with all card sprites
    public int currentThemeIndex = 1;
    
    [HideInInspector]
    public Sprite backImage;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        
        Instance = this;
        DontDestroyOnLoad(gameObject);

        // Restore the saved theme (falls back to the inspector default)
        int saved = PlayerPrefs.GetInt(PREF_KEY, currentThemeIndex);
        ApplyTheme(saved, save: false, notify: false);
    }

    public void Init()
    {
        ApplyTheme(currentThemeIndex, save: false, notify: false);
    }

    /// <summary>
    /// Changes the active card theme, persists the choice, and notifies
    /// all active CardUI instances so they refresh immediately.
    /// </summary>
    public void SetCurrentThemeIndex(int index)
    {
        ApplyTheme(index, save: true, notify: true);
    }

    // ── Internal ──────────────────────────────────────────────────

    private void ApplyTheme(int index, bool save, bool notify)
    {
        currentThemeIndex = index;
        if (currentThemeIndex >= cardThemes.Length)
        {
            currentThemeIndex = cardThemes.Length - 1;
            Debug.LogWarning($"[{LogTag.Theme}] Theme index clamped — requested={index}, clamped={currentThemeIndex}, reason=above max", this);
        }
        if (currentThemeIndex < 0)
        {
            currentThemeIndex = 0;
            Debug.LogWarning($"[{LogTag.Theme}] Theme index clamped — requested={index}, clamped=0, reason=below min", this);
        }
        
        // Find card_back sprite in the current theme
        backImage = null;
        if (cardThemes[currentThemeIndex].sprites != null)
        {
            foreach (Sprite sprite in cardThemes[currentThemeIndex].sprites)
            {
                if (sprite != null && sprite.name == "card_back")
                {
                    backImage = sprite;
                    break;
                }
            }
        }
        
        if (backImage == null)
        {
            Debug.LogWarning($"[{LogTag.Theme}] Back sprite missing — themeIndex={currentThemeIndex}", this);
        }

        if (save)
        {
            PlayerPrefs.SetInt(PREF_KEY, currentThemeIndex);
            PlayerPrefs.Save();
        }

        if (notify)
            OnThemeChanged?.Invoke();
    }
    
    public Sprite GetBackImage(int themeIndex)
    {
        if (cardThemes == null || themeIndex < 0 || themeIndex >= cardThemes.Length)
            return backImage;

        Sprite[] sprites = cardThemes[themeIndex].sprites;
        if (sprites == null) return backImage;

        foreach (Sprite sprite in sprites)
        {
            if (sprite != null && sprite.name == "card_back")
                return sprite;
        }
        return backImage;
    }

    public Sprite GetCardImage(Card.Rank rank, Card.Suit suit, int themeIndex)
    {
        if (cardThemes == null || themeIndex < 0 || themeIndex >= cardThemes.Length
            || cardThemes[themeIndex].sprites == null)
        {
            return GetCardImage(rank, suit);
        }

        if (rank == Card.Rank.None || suit == Card.Suit.None) return null;
        if (rank == Card.Rank.Hidden || suit == Card.Suit.Hidden) return GetBackImage(themeIndex);

        string spriteName = $"card_{RankToString(rank)}_{suit.ToString().ToLower()}";
        foreach (Sprite sprite in cardThemes[themeIndex].sprites)
        {
            if (sprite != null && sprite.name == spriteName)
                return sprite;
        }

        Debug.LogWarning($"[{LogTag.Theme}] Sprite not found — themeIndex={themeIndex}, spriteName={spriteName}", this);
        return GetBackImage(themeIndex);
    }

    public Sprite GetCardImage(Card.Rank rank, Card.Suit suit)
    {
        if (cardThemes == null || currentThemeIndex >= cardThemes.Length || cardThemes[currentThemeIndex].sprites == null)
        {
            Debug.LogError($"[{LogTag.Theme}] Card image lookup failed — reason=invalid theme index, themeIndex={currentThemeIndex}", this);
            return backImage;
        }
        
        // None cards should not request a front image sprite
        // They will be handled by CardUI.UpdateCardUI() which will show/hide front/back appropriately
        if (rank == Card.Rank.None || suit == Card.Suit.None)
        {
            Debug.LogWarning($"[{LogTag.Theme}] Card image requested for None card — rank={rank}, suit={suit}", this);
            return null;
        }

        if (rank == Card.Rank.Hidden || suit == Card.Suit.Hidden)
        {
            return backImage;
        }
        
        Sprite[] sprites = cardThemes[currentThemeIndex].sprites;
        
        string spriteName = $"card_{RankToString(rank)}_{suit.ToString().ToLower()}";
        
        foreach (Sprite sprite in sprites)
        {
            if (sprite != null && sprite.name == spriteName)
            {
                return sprite;
            }
        }
        
        Debug.LogWarning($"[{LogTag.Theme}] Sprite not found — spriteName={spriteName}, themeIndex={currentThemeIndex}", this);
        return backImage;
    }

    private static string RankToString(Card.Rank rank) => rank switch
    {
        Card.Rank.Ace   => "a",
        Card.Rank.Two   => "2",
        Card.Rank.Three => "3",
        Card.Rank.Four  => "4",
        Card.Rank.Five  => "5",
        Card.Rank.Six   => "6",
        Card.Rank.Seven => "7",
        Card.Rank.Eight => "8",
        Card.Rank.Nine  => "9",
        Card.Rank.Ten   => "t",
        Card.Rank.Jack  => "j",
        Card.Rank.Queen => "q",
        Card.Rank.King  => "k",
        _               => ""
    };
}
