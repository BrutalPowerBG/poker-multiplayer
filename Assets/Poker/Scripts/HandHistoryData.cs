using System;
using System.Collections.Generic;

// ═══════════════════════════════════════════════════════════════════
//  Hand History — Data Classes
//
//  Plain C# classes used by HandRecorder, IHandHistoryStorage, UI.
//
//  Firestore layout (native collections — NO JSON blobs):
//    /players/{uid}                     → profile + stats map
//    /sessions/{sessionId}              → table session metadata
//    /sessions/{sessionId}/hands/{num}  → hand document (maps + arrays)
//
//  Cloud Save layout (JSON-serialised, player-scoped):
//    hh_index           → HandHistoryIndex
//    hh_s_{sessionId}   → SessionHandData
//    hh_stats           → PlayerStats
// ═══════════════════════════════════════════════════════════════════

// ── Per-hand record ───────────────────────────────────────────────

/// <summary>
/// Complete record of a single poker hand.
/// Built incrementally by HandRecorder during play.
/// </summary>
[Serializable]
public class HandRecord
{
    public int handNumber;
    public string tableSessionId;
    public long timestampUtcMs;

    // Blinds
    public float smallBlind;
    public float bigBlind;

    // Positions (real slot indices)
    public int dealerSlotIndex;
    public int sbSlotIndex;
    public int bbSlotIndex;

    // Total pot at end of hand
    public float potTotal;

    /// <summary>How far the hand progressed: "preflop", "flop", "turn", "river".</summary>
    public string finalPhase;

    // Snapshot of every player at the start of the hand
    public List<HandPlayerInfo> players = new List<HandPlayerInfo>();

    // Ordered list of every action (blinds are implicit)
    public List<HandAction> actions = new List<HandAction>();

    // Community cards that were dealt (rank/suit = 0 means not dealt)
    public List<int> communityCardRanks = new List<int>();
    public List<int> communityCardSuits = new List<int>();

    // Winner(s) and amounts
    public List<HandWinner> winners = new List<HandWinner>();
}

/// <summary>
/// Snapshot of one player at the start of a hand.
/// </summary>
[Serializable]
public class HandPlayerInfo
{
    /// <summary>Firebase UID of the player (empty if not known — e.g. for non-local players).</summary>
    public string playerId;

    /// <summary>Display name shown at the table.</summary>
    public string displayName;

    public int slotIndex;
    public float stackAtStart;
}

/// <summary>
/// A single player action within a hand.
/// </summary>
[Serializable]
public class HandAction
{
    /// <summary>GameManager.GamePhase cast to int (0=PreFlop, 1=Flop, 2=Turn, 3=River).</summary>
    public int phase;
    public int playerSlotIndex;
    public string actionType; // "Fold", "Check", "Call", "Raise", "All-In", etc.
    public float amount;
}

/// <summary>
/// Records one winner and how much they won.
/// A hand can have multiple winners (split pot / side pots).
/// </summary>
[Serializable]
public class HandWinner
{
    public int slotIndex;
    public string displayName;
    public float amount;

    public int card1Rank;
    public int card1Suit;
    public int card2Rank;
    public int card2Suit;
    public bool cardsRevealed;
}

// ── Table session ─────────────────────────────────────────────────

/// <summary>
/// Metadata for one table session.
/// Firestore: stored as fields on /sessions/{sessionId}.
/// Cloud Save: stored inside the HandHistoryIndex JSON.
/// </summary>
[Serializable]
public class TableSessionEntry
{
    public string sessionId;
    public string tableName;

    /// <summary>"active" or "completed".</summary>
    public string status;

    public float smallBlind;
    public float bigBlind;
    public float minBuyIn;
    public float maxBuyIn;
    public long createdAtUtcMs;
    public long lastActivityUtcMs;
    public int handCount;

    /// <summary>Total number of unique players who participated in the session.</summary>
    public int playerCount;

    /// <summary>Firebase UIDs of players who participated (populated by self-registration).</summary>
    public List<string> playerIds = new List<string>();

    /// <summary>Per-player summaries within this session (used by Cloud Save index).</summary>
    public List<SessionPlayerSummary> playerSummaries = new List<SessionPlayerSummary>();
}

/// <summary>
/// Per-player summary within a single table session.
/// Tracks cumulative buy-ins and net profit across all hands.
/// </summary>
[Serializable]
public class SessionPlayerSummary
{
    /// <summary>Firebase UID (empty for non-local players whose UID is unknown).</summary>
    public string playerId;

    public string displayName;

    /// <summary>Index into <see cref="PlayerAssets.characterSprites"/> for the avatar.</summary>
    public int characterId;

    /// <summary>Sum of every buy-in at the table (initial + re-buys).</summary>
    public float totalBuyIn;

    /// <summary>Net profit/loss for this player in the session (positive = won).</summary>
    public float netResult;

    public int handsPlayed;
}

// ── Persistent index (Cloud Save — lightweight, loaded on startup) ──

/// <summary>
/// Top-level index stored in Cloud Save.  Contains a list of session
/// summaries so the UI can render a session list without loading
/// every hand record.  For Firestore, sessions are queried directly.
/// </summary>
[Serializable]
public class HandHistoryIndex
{
    public List<TableSessionEntry> sessions = new List<TableSessionEntry>();
    public int totalHandCount;
}

/// <summary>
/// All hand records for one table session.
/// Cloud Save: stored in its own key.
/// Firestore: hands are individual subcollection documents.
/// </summary>
[Serializable]
public class SessionHandData
{
    public string sessionId;
    public List<HandRecord> hands = new List<HandRecord>();
}

// ── Player statistics (running aggregates) ────────────────────────

/// <summary>
/// Lifetime poker statistics for a player.
/// Firestore: stored as a map in /players/{uid}.stats.
/// Cloud Save: stored as JSON in hh_stats key.
/// Updated incrementally after every hand.
/// </summary>
[Serializable]
public class PlayerStats
{
    public int totalHands;

    /// <summary>Hands where the player voluntarily put money in preflop (call or raise, excluding forced blinds).</summary>
    public int vpipHands;

    /// <summary>Hands where the player raised preflop.</summary>
    public int pfrHands;

    /// <summary>Total aggressive actions (bet / raise) across all hands and streets.</summary>
    public int aggressionBets;

    /// <summary>Total passive actions (call) across all hands and streets.</summary>
    public int aggressionCalls;

    public int handsWon;

    /// <summary>Cumulative net profit/loss (positive = winning player, negative = losing).</summary>
    public float netProfit;

    /// <summary>Largest single pot won.</summary>
    public float biggestPotWon;

    /// <summary>Number of hands where the player reached showdown without folding.</summary>
    public int showdownsReached;

    /// <summary>Number of showdowns won.</summary>
    public int showdownsWon;

    // ── Derived stats (computed client-side from the counters above) ──

    /// <summary>Voluntarily Put money In Pot percentage.</summary>
    public float VPIP => totalHands > 0 ? (float)vpipHands / totalHands * 100f : 0f;

    /// <summary>Pre-Flop Raise percentage.</summary>
    public float PFR => totalHands > 0 ? (float)pfrHands / totalHands * 100f : 0f;

    /// <summary>Aggression Factor (bets+raises / calls). 0 when calls == 0.</summary>
    public float AF => aggressionCalls > 0 ? (float)aggressionBets / aggressionCalls : 0f;

    /// <summary>Win rate as a percentage of hands played.</summary>
    public float WinRate => totalHands > 0 ? (float)handsWon / totalHands * 100f : 0f;

    /// <summary>Went to Showdown percentage.</summary>
    public float WTSD => totalHands > 0 ? (float)showdownsReached / totalHands * 100f : 0f;

    /// <summary>Won at Showdown percentage (of showdowns reached).</summary>
    public float WSD => showdownsReached > 0 ? (float)showdownsWon / showdownsReached * 100f : 0f;
}
