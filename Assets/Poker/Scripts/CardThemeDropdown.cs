using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// Populates a TMP_Dropdown with all card themes from <see cref="CardTheme"/>
/// and syncs selection with <see cref="CardTheme.SetCurrentThemeIndex"/>,
/// which handles persistence and notifying all CardUI instances.
/// </summary>
[RequireComponent(typeof(TMP_Dropdown))]
public class CardThemeDropdown : MonoBehaviour
{
    [Header("Optional preview cards: 0 = theme back (face-down); 1–3 = unique random face-up cards")]
    [SerializeField] private CardUI previewCard0;
    [SerializeField] private CardUI previewCard1;
    [SerializeField] private CardUI previewCard2;
    [SerializeField] private CardUI previewCard3;

    private TMP_Dropdown dropdown;

    private void Awake()
    {
        dropdown = GetComponent<TMP_Dropdown>();
    }

    private void OnEnable()
    {
        PopulateDropdown();

        // Sync dropdown to the currently active theme (already loaded from PlayerPrefs by CardTheme).
        if (CardTheme.Instance != null)
            dropdown.SetValueWithoutNotify(CardTheme.Instance.currentThemeIndex);

        PopulatePreviewCardsWithRandomThemeCards();

        dropdown.onValueChanged.AddListener(OnDropdownChanged);
        CardTheme.OnThemeChanged += OnCardThemeChanged;
    }

    private void OnDisable()
    {
        CardTheme.OnThemeChanged -= OnCardThemeChanged;
        if (dropdown != null)
            dropdown.onValueChanged.RemoveListener(OnDropdownChanged);
    }

    private void OnCardThemeChanged()
    {
        StartCoroutine(PlayPreviewFlipAnimationsAfterThemeRefresh());
    }

    private IEnumerator PlayPreviewFlipAnimationsAfterThemeRefresh()
    {
        // Let all CardUI.RefreshTheme handlers run first (subscription order is not guaranteed).
        yield return null;
        foreach (var card in EnumeratePreviewCards())
        {
            if (card != null)
                card.PlayThemeChangeFlipAnimation();
        }
    }

    private void PopulatePreviewCardsWithRandomThemeCards()
    {
        if (CardTheme.Instance == null)
            return;

        var deck = BuildShuffledDeck();
        int d = 0;

        if (previewCard0 != null && d < deck.Count)
        {
            var (rank, suit) = deck[d++];
            previewCard0.Init(rank, suit, isHidden: true, animate: true);
        }

        if (previewCard1 != null && d < deck.Count)
        {
            previewCard1.Init(deck[d].rank, deck[d].suit, false, true);
            d++;
        }
        if (previewCard2 != null && d < deck.Count)
        {
            previewCard2.Init(deck[d].rank, deck[d].suit, false, true);
            d++;
        }
        if (previewCard3 != null && d < deck.Count)
        {
            previewCard3.Init(deck[d].rank, deck[d].suit, false, true);
            d++;
        }
    }

    /// <summary>All 52 playing cards, shuffled (Fisher–Yates).</summary>
    private static List<(Card.Rank rank, Card.Suit suit)> BuildShuffledDeck()
    {
        var deck = new List<(Card.Rank, Card.Suit)>(52);
        foreach (Card.Suit suit in System.Enum.GetValues(typeof(Card.Suit)))
        {
            if (suit == Card.Suit.None || suit == Card.Suit.Hidden)
                continue;
            foreach (Card.Rank rank in System.Enum.GetValues(typeof(Card.Rank)))
            {
                if (rank == Card.Rank.None || rank == Card.Rank.Hidden)
                    continue;
                deck.Add((rank, suit));
            }
        }

        for (int n = deck.Count - 1; n > 0; n--)
        {
            int k = Random.Range(0, n + 1);
            (deck[n], deck[k]) = (deck[k], deck[n]);
        }

        return deck;
    }

    private IEnumerable<CardUI> EnumeratePreviewCards()
    {
        if (previewCard0 != null) yield return previewCard0;
        if (previewCard1 != null) yield return previewCard1;
        if (previewCard2 != null) yield return previewCard2;
        if (previewCard3 != null) yield return previewCard3;
    }

    private void PopulateDropdown()
    {
        dropdown.ClearOptions();

        if (CardTheme.Instance == null || CardTheme.Instance.cardThemes == null)
        {
            Debug.LogWarning($"[{LogTag.Theme}] Dropdown population skipped — reason=CardTheme.Instance not available", this);
            return;
        }

        var options = new List<string>();
        for (int i = 0; i < CardTheme.Instance.cardThemes.Length; i++)
        {
            string name = CardTheme.Instance.cardThemes[i].themeName;
            if (string.IsNullOrEmpty(name))
                name = $"Theme {i + 1}";
            options.Add(name);
        }

        dropdown.AddOptions(options);
    }

    private void OnDropdownChanged(int index)
    {
        if (CardTheme.Instance == null) return;
        CardTheme.Instance.SetCurrentThemeIndex(index);
    }
}
