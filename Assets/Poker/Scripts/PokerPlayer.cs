using UnityEngine;
using Unity.Netcode;
using Unity.Collections;
using System;

/// <summary>
/// Represents a poker player in the game.
/// Changed to struct to support NetworkList synchronization.
/// Note: Player hands (cards) are not currently synced over network and will need
/// separate NetworkVariable handling or be managed server-side only.
/// </summary>
public struct PokerPlayer : INetworkSerializable, IEquatable<PokerPlayer>
{
    public FixedString64Bytes playerName;  // Using FixedString for network serialization
    public float stack;
    public int iconIndex;
    public int realSlotIndex;

    public Card card1;
    public Card card2;
    
    // Betting round state
    public bool isPlaying;        // True if player is still in the current round (hasn't folded)
    public float currentBet;      // Amount bet in the current betting round
    public bool hasActed;         // True if player has acted in this betting round
    public bool isAllIn;          // True if player is all-in (has bet their entire stack)
    public float totalBetInHand;  // Total amount bet across all betting rounds in the current hand
    
    public PokerPlayer(string playerName, float stack, int iconIndex, int realSlotIndex)
    {
        this.playerName = new FixedString64Bytes(playerName);
        this.stack = stack;
        this.iconIndex = iconIndex;
        this.realSlotIndex = realSlotIndex;
        
        // Initialize cards to None state (not dealt yet)
        this.card1 = new Card(Card.Rank.None, Card.Suit.None);
        this.card2 = new Card(Card.Rank.None, Card.Suit.None);
        
        // Initialize betting state
        this.isPlaying = false;
        this.currentBet = 0;
        this.hasActed = false;
        this.isAllIn = false;
        this.totalBetInHand = 0;
    }

    // Helper property to get playerName as string
    public string PlayerNameString => playerName.ToString();

    // Required for INetworkSerializable
    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref playerName);
        serializer.SerializeValue(ref stack);
        serializer.SerializeValue(ref iconIndex);
        serializer.SerializeValue(ref realSlotIndex);
        serializer.SerializeValue(ref isPlaying);
        serializer.SerializeValue(ref currentBet);
        serializer.SerializeValue(ref hasActed);
        serializer.SerializeValue(ref isAllIn);
        serializer.SerializeValue(ref totalBetInHand);
    }

    // Required for IEquatable.
    //
    // IMPORTANT: This MUST be a full value comparison of every field. Netcode's
    // NetworkList<T>/NetworkVariable<T> use this Equals (via AreEqual/EqualityEquals)
    // to decide whether a write actually changed the value. If Equals only compared
    // identity (playerName + realSlotIndex), then any in-hand mutation that keeps the
    // same seat — isPlaying, stack, currentBet, isAllIn, cards, etc. — would be
    // considered "unchanged" and the assignment `players[i] = player` would be
    // SILENTLY DROPPED. That produced hands where every player read back as
    // isPlaying=false with pristine stacks (activePlayers=0, pot never awarded).
    public bool Equals(PokerPlayer other)
    {
        return playerName.Equals(other.playerName) &&
               realSlotIndex == other.realSlotIndex &&
               iconIndex == other.iconIndex &&
               stack == other.stack &&
               currentBet == other.currentBet &&
               totalBetInHand == other.totalBetInHand &&
               isPlaying == other.isPlaying &&
               hasActed == other.hasActed &&
               isAllIn == other.isAllIn &&
               card1.rank == other.card1.rank && card1.suit == other.card1.suit &&
               card2.rank == other.card2.rank && card2.suit == other.card2.suit;
    }

    public override bool Equals(object obj)
    {
        return obj is PokerPlayer other && Equals(other);
    }

    // Consistent with Equals: identity (name+slot) is sufficient for a valid hash
    // because any two players that are Equal necessarily share the same name+slot.
    public override int GetHashCode()
    {
        return HashCode.Combine(playerName.GetHashCode(), realSlotIndex);
    }
}
