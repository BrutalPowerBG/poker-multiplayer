using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class HandStrength
{
    public enum HandStrengthType
    {
        HighCard,
        OnePair,
        TwoPair,
        ThreeOfAKind,
        Straight,
        Flush,
        FullHouse,
        FourOfAKind,
        StraightFlush,
    }

    public struct HandStrengthResult
    {
        public HandStrengthType handStrengthType;
        public Card.Rank highestCardInCombo; // The highest card in the combo
        public List<Card.Rank> kickers; // Cards that are not part of the combo
        public List<Card> cards; // All 5 cards that make up the best hand

        public HandStrengthResult(HandStrengthType type, Card.Rank highCard, List<Card.Rank> kickerList, List<Card> handCards)
        {
            handStrengthType = type;
            highestCardInCombo = highCard;
            kickers = kickerList ?? new List<Card.Rank>();
            cards = handCards ?? new List<Card>();
        }
    }

    /// <summary>
    /// Sorts players by hand strength (strongest first)
    /// </summary>
    public PokerPlayer[] SortPlayersByHandStrength(PokerPlayer[] players, Card[] communityCards)
    {
        // Create array to store player hand strengths
        var playerHands = new List<(PokerPlayer player, HandStrengthResult handResult)>();

        // Calculate hand strength for each player
        foreach (var player in players)
        {
            Card[] allCards = GetAllCardsForPlayer(player, communityCards);
            HandStrengthResult bestHand = CalculateHandStrength(allCards);
            playerHands.Add((player, bestHand));
            
            Debug.Log($"[{LogTag.Hand}] Hand evaluated — player={player.playerName}, hand={bestHand.handStrengthType}, highCard={bestHand.highestCardInCombo}");
        }

        // Sort by hand strength (strongest first)
        playerHands.Sort((a, b) => CompareHands(b.handResult, a.handResult)); // Note: b, a for descending

        // Extract sorted players
        return playerHands.Select(ph => ph.player).ToArray();
    }

    /// <summary>
    /// Gets all 7 cards (2 hole + 5 community) for a player, sorted by rank descending
    /// </summary>
    private Card[] GetAllCardsForPlayer(PokerPlayer player, Card[] communityCards)
    {
        Card[] hand = new Card[] { player.card1, player.card2 };
        Card[] allCards = new Card[hand.Length + communityCards.Length];
        hand.CopyTo(allCards, 0);
        communityCards.CopyTo(allCards, hand.Length);
        
        // Sort by rank descending (Ace highest)
        Array.Sort(allCards, (a, b) => GetRankValue(b.rank).CompareTo(GetRankValue(a.rank)));
        
        return allCards;
    }

    /// <summary>
    /// Calculates the best 5-card poker hand from 7 cards
    /// </summary>
    private HandStrengthResult CalculateHandStrength(Card[] cards)
    {
        if (cards.Length != 7)
        {
            Debug.LogError($"[{LogTag.Hand}] Hand calculation failed — reason=wrong card count, expected=7, got={cards.Length}");
            return new HandStrengthResult(HandStrengthType.HighCard, Card.Rank.Two, new List<Card.Rank>(), new List<Card>());
        }

        // Check all hands in order of strength (highest to lowest)
        HandStrengthResult result;

        result = CheckStraightFlush(cards);
        if (result.handStrengthType == HandStrengthType.StraightFlush) return result;

        result = CheckFourOfAKind(cards);
        if (result.handStrengthType == HandStrengthType.FourOfAKind) return result;

        result = CheckFullHouse(cards);
        if (result.handStrengthType == HandStrengthType.FullHouse) return result;

        result = CheckFlush(cards);
        if (result.handStrengthType == HandStrengthType.Flush) return result;

        result = CheckStraight(cards);
        if (result.handStrengthType == HandStrengthType.Straight) return result;

        result = CheckThreeOfAKind(cards);
        if (result.handStrengthType == HandStrengthType.ThreeOfAKind) return result;

        result = CheckTwoPair(cards);
        if (result.handStrengthType == HandStrengthType.TwoPair) return result;

        result = CheckOnePair(cards);
        if (result.handStrengthType == HandStrengthType.OnePair) return result;

        // Default to high card
        return CheckHighCard(cards);
    }

    /// <summary>
    /// Compares two players' hands given community cards. 
    /// Returns positive if player1 is stronger, negative if player2 is stronger, 0 if equal.
    /// </summary>
    public int ComparePlayerHands(PokerPlayer player1, PokerPlayer player2, Card[] communityCards)
    {
        Card[] cards1 = GetAllCardsForPlayer(player1, communityCards);
        Card[] cards2 = GetAllCardsForPlayer(player2, communityCards);
        HandStrengthResult result1 = CalculateHandStrength(cards1);
        HandStrengthResult result2 = CalculateHandStrength(cards2);
        return CompareHands(result1, result2);
    }
    
    /// <summary>
    /// Compares two hands. Returns positive if hand1 is stronger, negative if hand2 is stronger, 0 if equal
    /// </summary>
    private int CompareHands(HandStrengthResult hand1, HandStrengthResult hand2)
    {
        // Compare hand types
        if (hand1.handStrengthType != hand2.handStrengthType)
        {
            return hand1.handStrengthType.CompareTo(hand2.handStrengthType);
        }

        // Same hand type, compare high card in combo
        int highCardComparison = GetRankValue(hand1.highestCardInCombo).CompareTo(GetRankValue(hand2.highestCardInCombo));
        if (highCardComparison != 0) return highCardComparison;

        // Compare kickers
        int minKickers = Mathf.Min(hand1.kickers.Count, hand2.kickers.Count);
        for (int i = 0; i < minKickers; i++)
        {
            int kickerComparison = GetRankValue(hand1.kickers[i]).CompareTo(GetRankValue(hand2.kickers[i]));
            if (kickerComparison != 0) return kickerComparison;
        }

        return 0; // Hands are equal
    }

    // ==================== HAND CHECKING METHODS ====================

    private HandStrengthResult CheckStraightFlush(Card[] cards)
    {
        var flushResult = CheckFlush(cards);
        if (flushResult.handStrengthType != HandStrengthType.Flush) return default;

        var straightResult = CheckStraight(flushResult.cards.ToArray());
        if (straightResult.handStrengthType == HandStrengthType.Straight)
        {
            return new HandStrengthResult(HandStrengthType.StraightFlush, straightResult.highestCardInCombo, 
                new List<Card.Rank>(), straightResult.cards);
        }

        return default;
    }

    private HandStrengthResult CheckFourOfAKind(Card[] cards)
    {
        var groups = cards.GroupBy(c => c.rank).Where(g => g.Count() == 4).OrderByDescending(g => GetRankValue(g.Key));
        
        if (groups.Any())
        {
            var fourKind = groups.First();
            var fourCards = fourKind.ToList();
            var kicker = cards.Where(c => c.rank != fourKind.Key).OrderByDescending(c => GetRankValue(c.rank)).First();
            
            var handCards = new List<Card>(fourCards);
            handCards.Add(kicker);
            
            return new HandStrengthResult(HandStrengthType.FourOfAKind, fourKind.Key, 
                new List<Card.Rank> { kicker.rank }, handCards);
        }
        
        return default;
    }

    private HandStrengthResult CheckFullHouse(Card[] cards)
    {
        var threes = cards.GroupBy(c => c.rank).Where(g => g.Count() >= 3).OrderByDescending(g => GetRankValue(g.Key)).ToList();
        var pairs = cards.GroupBy(c => c.rank).Where(g => g.Count() >= 2).OrderByDescending(g => GetRankValue(g.Key)).ToList();

        if (threes.Any() && pairs.Any())
        {
            var threeKind = threes.First();
            var pair = pairs.FirstOrDefault(p => p.Key != threeKind.Key);
            
            if (pair != null)
            {
                var handCards = new List<Card>();
                handCards.AddRange(threeKind.Take(3));
                handCards.AddRange(pair.Take(2));
                
                return new HandStrengthResult(HandStrengthType.FullHouse, threeKind.Key, 
                    new List<Card.Rank> { pair.Key }, handCards);
            }
        }
        
        return default;
    }

    private HandStrengthResult CheckFlush(Card[] cards)
    {
        var suitGroups = cards.GroupBy(c => c.suit).Where(g => g.Count() >= 5);
        
        if (suitGroups.Any())
        {
            var flushCards = suitGroups.First().OrderByDescending(c => GetRankValue(c.rank)).Take(5).ToList();
            return new HandStrengthResult(HandStrengthType.Flush, flushCards[0].rank, 
                flushCards.Skip(1).Select(c => c.rank).ToList(), flushCards);
        }
        
        return default;
    }

    private HandStrengthResult CheckStraight(Card[] cards)
    {
        var distinctRanks = cards.Select(c => c.rank).Distinct().OrderByDescending(r => GetRankValue(r)).ToList();
        
        // Check for regular straight (including Ace-high)
        for (int i = 0; i <= distinctRanks.Count - 5; i++)
        {
            bool isStraight = true;
            for (int j = 0; j < 4; j++)
            {
                if (GetRankValue(distinctRanks[i + j]) - GetRankValue(distinctRanks[i + j + 1]) != 1)
                {
                    isStraight = false;
                    break;
                }
            }
            
            if (isStraight)
            {
                var straightCards = new List<Card>();
                for (int j = 0; j < 5; j++)
                {
                    straightCards.Add(cards.First(c => c.rank == distinctRanks[i + j]));
                }
                return new HandStrengthResult(HandStrengthType.Straight, distinctRanks[i], 
                    new List<Card.Rank>(), straightCards);
            }
        }
        
        // Check for Ace-low straight (A-2-3-4-5)
        if (distinctRanks.Contains(Card.Rank.Ace) && distinctRanks.Contains(Card.Rank.Two) && 
            distinctRanks.Contains(Card.Rank.Three) && distinctRanks.Contains(Card.Rank.Four) && 
            distinctRanks.Contains(Card.Rank.Five))
        {
            var straightCards = new List<Card>
            {
                cards.First(c => c.rank == Card.Rank.Five),
                cards.First(c => c.rank == Card.Rank.Four),
                cards.First(c => c.rank == Card.Rank.Three),
                cards.First(c => c.rank == Card.Rank.Two),
                cards.First(c => c.rank == Card.Rank.Ace)
            };
            return new HandStrengthResult(HandStrengthType.Straight, Card.Rank.Five, // Ace-low straight, 5 is high
                new List<Card.Rank>(), straightCards);
        }
        
        return default;
    }

    private HandStrengthResult CheckThreeOfAKind(Card[] cards)
    {
        var groups = cards.GroupBy(c => c.rank).Where(g => g.Count() == 3).OrderByDescending(g => GetRankValue(g.Key));
        
        if (groups.Any())
        {
            var threeKind = groups.First();
            var threeCards = threeKind.ToList();
            var kickers = cards.Where(c => c.rank != threeKind.Key)
                .OrderByDescending(c => GetRankValue(c.rank)).Take(2).ToList();
            
            var handCards = new List<Card>(threeCards);
            handCards.AddRange(kickers);
            
            return new HandStrengthResult(HandStrengthType.ThreeOfAKind, threeKind.Key, 
                kickers.Select(c => c.rank).ToList(), handCards);
        }
        
        return default;
    }

    private HandStrengthResult CheckTwoPair(Card[] cards)
    {
        var pairs = cards.GroupBy(c => c.rank).Where(g => g.Count() >= 2)
            .OrderByDescending(g => GetRankValue(g.Key)).ToList();
        
        if (pairs.Count >= 2)
        {
            var firstPair = pairs[0];
            var secondPair = pairs[1];
            var kicker = cards.Where(c => c.rank != firstPair.Key && c.rank != secondPair.Key)
                .OrderByDescending(c => GetRankValue(c.rank)).First();
            
            var handCards = new List<Card>();
            handCards.AddRange(firstPair.Take(2));
            handCards.AddRange(secondPair.Take(2));
            handCards.Add(kicker);
            
            return new HandStrengthResult(HandStrengthType.TwoPair, firstPair.Key, 
                new List<Card.Rank> { secondPair.Key, kicker.rank }, handCards);
        }
        
        return default;
    }

    private HandStrengthResult CheckOnePair(Card[] cards)
    {
        var groups = cards.GroupBy(c => c.rank).Where(g => g.Count() == 2).OrderByDescending(g => GetRankValue(g.Key));
        
        if (groups.Any())
        {
            var pair = groups.First();
            var pairCards = pair.ToList();
            var kickers = cards.Where(c => c.rank != pair.Key)
                .OrderByDescending(c => GetRankValue(c.rank)).Take(3).ToList();
            
            var handCards = new List<Card>(pairCards);
            handCards.AddRange(kickers);
            
            return new HandStrengthResult(HandStrengthType.OnePair, pair.Key, 
                kickers.Select(c => c.rank).ToList(), handCards);
        }
        
        return default;
    }

    private HandStrengthResult CheckHighCard(Card[] cards)
    {
        var topCards = cards.OrderByDescending(c => GetRankValue(c.rank)).Take(5).ToList();
        
        return new HandStrengthResult(HandStrengthType.HighCard, topCards[0].rank, 
            topCards.Skip(1).Select(c => c.rank).ToList(), topCards);
    }

    // ==================== HELPER METHODS ====================

    /// <summary>
    /// Gets numeric value for rank (Ace = 14, King = 13, etc.)
    /// </summary>
    private int GetRankValue(Card.Rank rank)
    {
        switch (rank)
        {
            case Card.Rank.Ace: return 14;
            case Card.Rank.King: return 13;
            case Card.Rank.Queen: return 12;
            case Card.Rank.Jack: return 11;
            case Card.Rank.Ten: return 10;
            case Card.Rank.Nine: return 9;
            case Card.Rank.Eight: return 8;
            case Card.Rank.Seven: return 7;
            case Card.Rank.Six: return 6;
            case Card.Rank.Five: return 5;
            case Card.Rank.Four: return 4;
            case Card.Rank.Three: return 3;
            case Card.Rank.Two: return 2;
            default: return 0;
        }
    }
}
