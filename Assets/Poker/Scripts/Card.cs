using Unity.Netcode;

[System.Serializable]
public struct Card : INetworkSerializable
{
    public enum Suit
    {
        None,     // Card doesn't exist (not dealt, folded, etc.)
        Hidden,   // Used when the card is face down
        Hearts,   // Classic red heart
        Diamonds, // Classic red diamond
        Clubs,    // Classic black club
        Spades,   // Classic black spade
    }
    public enum Rank
    {
        None,      // Card doesn't exist (not dealt, folded, etc.)
        Hidden,    // Used when the card is face down
        Ace,       // High card: Ace
        Two,       // The number 2 card
        Three,     // The number 3 card
        Four,      // The number 4 card
        Five,      // The number 5 card
        Six,       // The number 6 card
        Seven,     // The number 7 card
        Eight,     // The number 8 card
        Nine,      // The number 9 card
        Ten,       // The number 10 card
        Jack,      // Face card: Jack
        Queen,     // Face card: Queen
        King,      // Face card: King
    }
    public Suit suit; // The card's suit (Hearts, Spades, etc.)
    public Rank rank; // The card's rank (Ace, 2, King, etc.)

    public Card(Rank rank, Suit suit)
    {
        this.rank = rank;
        this.suit = suit;
    }

    public void UpdateCard(Rank rank, Suit suit)
    {
        this.suit = suit;
        this.rank = rank;
    }
    
    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref rank);
        serializer.SerializeValue(ref suit);
    }
    
    public override string ToString() => $"{rank} of {suit}";
}
