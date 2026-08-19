using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A fully serializable snapshot of the poker game state.
/// Captured by the departing host and sent to the new host so the game
/// can resume seamlessly after host migration.
/// </summary>
[Serializable]
public class GameStateSnapshot
{
    // ── Player data ──────────────────────────────────────────────
    [Serializable]
    public struct PlayerSnapshot
    {
        public string playerName;
        public float stack;
        public int iconIndex;
        public int realSlotIndex;
        public int cardRank1, cardSuit1;   // Card enums stored as ints
        public int cardRank2, cardSuit2;
        public bool isPlaying;
        public float currentBet;
        public bool hasActed;
        public bool isAllIn;
        public float totalBetInHand;
        public string authPlayerId;        // Lobby auth ID for reconnection matching
    }

    public List<PlayerSnapshot> players = new List<PlayerSnapshot>();

    // ── Table / betting state ────────────────────────────────────
    public float potOnTable;
    public float smallBlind;
    public float bigBlind;
    public int currentPhase;               // GameManager.GamePhase as int
    public int currentPlayerTurnIndex;
    public float currentBetToCall;
    public float lastRaiseAmount;
    public bool wasRaiseThisRound;
    public int lastRaiserIndex;
    public int currentDealerRealSlotIndex;
    public bool isGameStarted;

    // ── Community cards (5) ──────────────────────────────────────
    public int[] communityCardRanks = new int[5];
    public int[] communityCardSuits = new int[5];

    // ── Remaining deck (server-only secret data) ─────────────────
    public List<int> deckRanks = new List<int>();
    public List<int> deckSuits = new List<int>();

    // ── Migration metadata ───────────────────────────────────────
    public string migrationToken;           // Unique ID clients use to find the new lobby
    public string newHostAuthPlayerId;      // Auth player ID of the promoted client
    public string departingHostAuthPlayerId; // Auth player ID of the host that is leaving
    public List<string> expectedAuthPlayerIds = new List<string>(); // All players that should reconnect

    // ── Time bank per slot ─────────────────────────────────────────
    public List<int> timeBankSlotIndices = new List<int>();
    public List<float> timeBankValues = new List<float>();

    // ── Hand history continuity ───────────────────────────────────
    public string tableSessionId;           // Table identity that survives migration
    public int currentHandNumber;           // Last hand number dealt

    // ── Lobby metadata ────────────────────────────────────────────
    public string lobbyName;                // Original lobby name, preserved across migrations

    // ══════════════════════════════════════════════════════════════
    //  Capture
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Captures the full game state from the running GameManager (server-side).
    /// </summary>
    public static GameStateSnapshot CaptureFromServer(GameManager gm, Dictionary<ulong, string> clientIdToAuth)
    {
        var snap = new GameStateSnapshot
        {
            potOnTable = gm.potOnTable.Value,
            smallBlind = gm.smallBlind,
            bigBlind = gm.bigBlind,
            currentPhase = (int)gm.currentPhase.Value,
            currentPlayerTurnIndex = gm.currentPlayerTurnIndex.Value,
            currentBetToCall = gm.currentBetToCall.Value,
            lastRaiseAmount = gm.lastRaiseAmount.Value,
            wasRaiseThisRound = gm.wasRaiseThisRound.Value,
            isGameStarted = gm.isGameStarted.Value,
            migrationToken = Guid.NewGuid().ToString("N"),
        };

        // Access server-only fields through a helper
        snap.lastRaiserIndex = gm.GetLastRaiserIndex();
        snap.currentDealerRealSlotIndex = gm.GetCurrentDealerRealSlotIndex();

        // Hand history continuity
        snap.tableSessionId = gm.TableSessionId;
        snap.currentHandNumber = gm.CurrentHandNumber;

        // Preserve original lobby name across migrations
        snap.lobbyName = LobbyManager.Instance != null
            ? LobbyManager.Instance.GetLobbyName()
            : "MyLobbyX";

        // Community cards
        Card[] cc = {
            gm.communityCard1.Value, gm.communityCard2.Value,
            gm.communityCard3.Value, gm.communityCard4.Value,
            gm.communityCard5.Value
        };
        for (int i = 0; i < 5; i++)
        {
            snap.communityCardRanks[i] = (int)cc[i].rank;
            snap.communityCardSuits[i] = (int)cc[i].suit;
        }

        // Players
        for (int i = 0; i < gm.players.Count; i++)
        {
            var p = gm.players[i];
            string authId = "";

            // Find auth ID from the clientId mapping
            var slotToClient = gm.GetSlotToClientId();
            if (slotToClient.TryGetValue(p.realSlotIndex, out ulong clientId))
            {
                clientIdToAuth.TryGetValue(clientId, out authId);
            }

            snap.players.Add(new PlayerSnapshot
            {
                playerName = p.playerName.ToString(),
                stack = p.stack,
                iconIndex = p.iconIndex,
                realSlotIndex = p.realSlotIndex,
                cardRank1 = (int)p.card1.rank,
                cardSuit1 = (int)p.card1.suit,
                cardRank2 = (int)p.card2.rank,
                cardSuit2 = (int)p.card2.suit,
                isPlaying = p.isPlaying,
                currentBet = p.currentBet,
                hasActed = p.hasActed,
                isAllIn = p.isAllIn,
                totalBetInHand = p.totalBetInHand,
                authPlayerId = authId ?? "",
            });

            if (!string.IsNullOrEmpty(authId))
                snap.expectedAuthPlayerIds.Add(authId);
        }

        // Time banks
        var timeBanks = gm.GetTimeBankBySlotIndex();
        foreach (var kvp in timeBanks)
        {
            snap.timeBankSlotIndices.Add(kvp.Key);
            snap.timeBankValues.Add(kvp.Value);
        }

        // Deck (remaining cards — server secret)
        var deck = gm.GetDeck();
        if (deck != null)
        {
            foreach (var card in deck)
            {
                snap.deckRanks.Add((int)card.rank);
                snap.deckSuits.Add((int)card.suit);
            }
        }

        return snap;
    }

    // ══════════════════════════════════════════════════════════════
    //  Serialization helpers (JSON ↔ byte[])
    // ══════════════════════════════════════════════════════════════

    public byte[] ToBytes()
    {
        string json = JsonUtility.ToJson(this);
        return System.Text.Encoding.UTF8.GetBytes(json);
    }

    public static GameStateSnapshot FromBytes(byte[] data)
    {
        string json = System.Text.Encoding.UTF8.GetString(data);
        return JsonUtility.FromJson<GameStateSnapshot>(json);
    }
}
