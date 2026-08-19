using TMPro;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════
//  Hand History Entry
//
//  Attached to the hand entry prefab instantiated by HandHistoryPanel.
//  Displays a summary of a single completed hand: hand number,
//  winner(s), amount won, pot total, and final phase.
// ═══════════════════════════════════════════════════════════════════

public class HandHistoryEntry : MonoBehaviour
{
    [Header("UI References")]
    [SerializeField] private TextMeshProUGUI handNumberText;
    [SerializeField] private TextMeshProUGUI winnersText;
    [SerializeField] private TextMeshProUGUI potText;
    [SerializeField] private TextMeshProUGUI phaseText;

    [Header("Winner Cards")]
    [SerializeField] private CardUI winnerCard1;
    [SerializeField] private CardUI winnerCard2;

    /// <summary>
    /// Populates the entry UI from a completed HandRecord.
    /// </summary>
    public void Populate(HandRecord hand)
    {
        if (hand == null) return;

        // Hand number
        if (handNumberText != null)
            handNumberText.text = $"#{hand.handNumber}";

        // Winner(s) — "PlayerName won $123.45" or multi-winner lines
        if (winnersText != null)
        {
            if (hand.winners == null || hand.winners.Count == 0)
            {
                winnersText.text = "No winner";
            }
            else if (hand.winners.Count == 1)
            {
                var w = hand.winners[0];
                winnersText.text = $"{w.displayName}";
            }
            else
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < hand.winners.Count; i++)
                {
                    var w = hand.winners[i];
                    if (i > 0) sb.Append("\n");
                    sb.Append($"{w.displayName} won ${w.amount:F2}");
                }
                winnersText.text = sb.ToString();
            }
        }

        // Pot total
        if (potText != null)
            potText.text = $"${hand.potTotal:F2}";

        // Final phase
        if (phaseText != null)
        {
            string phase = string.IsNullOrEmpty(hand.finalPhase) ? "?" : CapitalizeFirst(hand.finalPhase);
            phaseText.text = phase;
        }

        // Winner cards — show the cards of the player who won the most
        PopulateWinnerCards(hand);
    }

    private void PopulateWinnerCards(HandRecord hand)
    {
        if (winnerCard1 == null || winnerCard2 == null) return;
        if (hand.winners == null || hand.winners.Count == 0)
        {
            winnerCard1.gameObject.SetActive(false);
            winnerCard2.gameObject.SetActive(false);
            return;
        }

        // Sum amounts per slot to find the player who won the most overall
        var totalBySlot = new System.Collections.Generic.Dictionary<int, float>();
        foreach (var w in hand.winners)
        {
            if (!totalBySlot.ContainsKey(w.slotIndex))
                totalBySlot[w.slotIndex] = 0f;
            totalBySlot[w.slotIndex] += w.amount;
        }

        int bestSlot = hand.winners[0].slotIndex;
        float bestAmount = 0f;
        foreach (var kvp in totalBySlot)
        {
            if (kvp.Value > bestAmount)
            {
                bestAmount = kvp.Value;
                bestSlot = kvp.Key;
            }
        }

        // Find the first winner entry for that slot (all entries share the same cards)
        HandWinner bestWinner = hand.winners[0];
        foreach (var w in hand.winners)
        {
            if (w.slotIndex == bestSlot) { bestWinner = w; break; }
        }

        var r1 = (Card.Rank)bestWinner.card1Rank;
        var s1 = (Card.Suit)bestWinner.card1Suit;
        var r2 = (Card.Rank)bestWinner.card2Rank;
        var s2 = (Card.Suit)bestWinner.card2Suit;

        bool card1Hidden = IsStoredCardHidden(r1, s1);
        bool card2Hidden = IsStoredCardHidden(r2, s2);

        winnerCard1.gameObject.SetActive(true);
        winnerCard2.gameObject.SetActive(true);

        winnerCard1.OverrideThemeIndex = card1Hidden ? -1 : 0;
        winnerCard2.OverrideThemeIndex = card2Hidden ? -1 : 0;

        if (card1Hidden)
            winnerCard1.Init(Card.Rank.Hidden, Card.Suit.Hidden, isHidden: true);
        else
            winnerCard1.Init(r1, s1, isHidden: false);

        if (card2Hidden)
            winnerCard2.Init(Card.Rank.Hidden, Card.Suit.Hidden, isHidden: true);
        else
            winnerCard2.Init(r2, s2, isHidden: false);
    }

    private static bool IsStoredCardHidden(Card.Rank rank, Card.Suit suit)
    {
        return rank == Card.Rank.Hidden || rank == Card.Rank.None
            || suit == Card.Suit.Hidden || suit == Card.Suit.None;
    }

    private static string CapitalizeFirst(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return char.ToUpper(s[0]) + s.Substring(1);
    }
}
