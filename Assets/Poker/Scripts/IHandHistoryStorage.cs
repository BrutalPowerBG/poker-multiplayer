using System.Threading.Tasks;

// ═══════════════════════════════════════════════════════════════════
//  Hand History — Storage Abstraction
//
//  These interfaces decouple recording logic from the persistence
//  back-end.  Implementations exist for Unity Cloud Save and
//  Firebase Firestore.  HandRecorder references the combined
//  IHandHistoryStorage interface so the backend can be swapped
//  via BackendType (on LobbyManager) without touching game logic.
//
//  Write pattern:
//    • SaveHandAsync          → HOST only (writes session + hand data)
//    • SavePlayerStatsAsync   → ALL clients (each writes their own)
//    • RegisterParticipant    → ALL clients (self-register in session)
//
//  For Cloud Save (player-scoped), all clients call SaveHandAsync
//  because each player stores their own copy.  The HandRecorder
//  handles this distinction based on BackendType.
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Write-side of the hand history persistence layer.
/// Called by <see cref="HandRecorder"/> after each hand completes.
/// </summary>
public interface IHandHistoryWriter
{
    /// <summary>
    /// Persists a completed hand record and creates/updates the session.
    /// For Firestore: writes to /sessions/{id}/hands/{num} (host only).
    /// For Cloud Save: writes to player-scoped JSON keys (all clients).
    /// </summary>
    Task SaveHandAsync(HandRecord hand, TableSessionEntry sessionMeta);

    /// <summary>
    /// Persists the player's lifetime statistics.
    /// For Firestore: writes to /players/{uid}.stats map.
    /// For Cloud Save: writes to player-scoped JSON key.
    /// </summary>
    Task SavePlayerStatsAsync(PlayerStats stats);

    /// <summary>
    /// Registers the current player as a participant in the session.
    /// For Firestore: adds UID to the session's playerIds array AND
    ///   embeds session metadata in the player doc's recentSessions map
    ///   (so LoadIndexAsync only needs 1 read).
    /// For Cloud Save: no-op (player-scoped data doesn't need this).
    /// </summary>
    Task RegisterSessionParticipantAsync(string sessionId, TableSessionEntry sessionMeta);

    /// <summary>
    /// Updates the local player's per-session summary (buy-in, net profit, hands played).
    /// For Firestore: writes to /sessions/{id}.playerSummaries.{uid} using MergeAll.
    /// For Cloud Save: updates the cached index entry (persisted on next SaveHandAsync).
    /// Called after every buy-in and after every hand completion.
    /// </summary>
    /// <param name="playerCount">Total unique players in the session (written alongside summary).</param>
    Task UpdateSessionPlayerSummaryAsync(string sessionId, SessionPlayerSummary summary, int playerCount = 0);
}

/// <summary>
/// Read-side of the hand history persistence layer.
/// Called by UI or analytics code to display past sessions / hands.
/// </summary>
public interface IHandHistoryReader
{
    /// <summary>
    /// Loads the session list for the current player.
    /// For Firestore: queries /sessions where playerIds contains uid.
    /// For Cloud Save: loads the player-scoped index JSON.
    /// Returns an empty index if no data exists yet.
    /// </summary>
    Task<HandHistoryIndex> LoadIndexAsync();

    /// <summary>
    /// Loads all hand records for a specific table session.
    /// For Firestore: queries /sessions/{id}/hands subcollection.
    /// For Cloud Save: loads the player-scoped session JSON.
    /// Returns null if the session data doesn't exist.
    /// </summary>
    Task<SessionHandData> LoadSessionHandsAsync(string sessionId);

    /// <summary>
    /// Loads the player's lifetime statistics.
    /// For Firestore: reads /players/{uid}.stats map.
    /// For Cloud Save: loads the player-scoped stats JSON.
    /// Returns a fresh <see cref="PlayerStats"/> if none exist yet.
    /// </summary>
    Task<PlayerStats> LoadPlayerStatsAsync();

    /// <summary>
    /// Loads the full session metadata including ALL player summaries.
    /// For Firestore: reads /sessions/{id} document (contains every
    ///   player's summary written via UpdateSessionPlayerSummaryAsync).
    /// For Cloud Save: returns the entry from the cached index (local
    ///   player's data only, since Cloud Save is player-scoped).
    /// Returns null if the session does not exist.
    /// </summary>
    Task<TableSessionEntry> LoadSessionEntryAsync(string sessionId);
}

/// <summary>
/// Combined read + write interface with cache management.
/// Both CloudSaveHandHistory and FirestoreHandHistory implement this
/// so HandRecorder can hold a single reference regardless of backend.
/// </summary>
public interface IHandHistoryStorage : IHandHistoryWriter, IHandHistoryReader
{
    /// <summary>
    /// Clears all in-memory caches.  Call when switching accounts
    /// or when the user signs out.
    /// </summary>
    void ClearCaches();
}
