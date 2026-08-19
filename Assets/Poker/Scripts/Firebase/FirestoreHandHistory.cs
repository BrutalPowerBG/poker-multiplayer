using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Firebase.Firestore;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════
//  Hand History — Firebase Firestore Implementation
//
//  Uses native Firestore collections and subcollections — NO JSON.
//
//  Firestore document layout:
//
//    /players/{uid}
//      ├── displayName, isGuest, characterId, createdAt, lastSeen
//      └── stats: { totalHands, vpipHands, ... netProfit, ... }
//
//    /sessions/{tableSessionId}
//      ├── tableName, status, smallBlind, bigBlind, ...
//      ├── createdAt, lastActivity (Timestamps)
//      ├── handCount
//      ├── playerIds: [uid1, uid2, ...]   (array-contains queryable)
//      └── /hands/{handNumber}
//            ├── handNumber, timestamp, dealerSlot, potTotal, finalPhase
//            ├── communityCards: [{rank, suit}, ...]
//            ├── seats: { "0": {displayName, startingStack}, ... }
//            ├── actions: [{slot, phase, action, amount}, ...]
//            └── winners: [{slot, displayName, amount}, ...]
//
//  Write pattern:
//    • SaveHandAsync          → called by HOST only
//    • SavePlayerStatsAsync   → called by ALL clients (player-scoped)
//    • RegisterParticipant    → called by ALL clients (arrayUnion)
//
//  Requires FirebaseManager to be initialised (IsFirebaseReady).
// ═══════════════════════════════════════════════════════════════════

public class FirestoreHandHistory : IHandHistoryStorage
{
    // ── Collection / field names ─────────────────────────────────
    private const string COL_PLAYERS  = "players";
    private const string COL_SESSIONS = "sessions";
    private const string COL_HANDS    = "hands";

    // Session document fields
    private const string F_TABLE_NAME         = "tableName";
    private const string F_STATUS             = "status";
    private const string F_SMALL_BLIND        = "smallBlind";
    private const string F_BIG_BLIND          = "bigBlind";
    private const string F_MIN_BUY_IN         = "minBuyIn";
    private const string F_MAX_BUY_IN         = "maxBuyIn";
    private const string F_CREATED_AT         = "createdAt";
    private const string F_LAST_ACTIVITY      = "lastActivity";
    private const string F_HAND_COUNT         = "handCount";
    private const string F_PLAYER_IDS         = "playerIds";
    private const string F_PLAYER_SUMMARIES   = "playerSummaries";

    // Hand document fields
    private const string F_HAND_NUMBER      = "handNumber";
    private const string F_TIMESTAMP        = "timestamp";
    private const string F_DEALER_SLOT      = "dealerSlot";
    private const string F_SB_SLOT          = "smallBlindSlot";
    private const string F_BB_SLOT          = "bigBlindSlot";
    private const string F_POT_TOTAL        = "potTotal";
    private const string F_FINAL_PHASE      = "finalPhase";
    private const string F_COMMUNITY_CARDS  = "communityCards";
    private const string F_SEATS            = "seats";
    private const string F_ACTIONS          = "actions";
    private const string F_WINNERS          = "winners";

    // Player document fields
    private const string F_STATS            = "stats";
    private const string F_RECENT_SESSIONS  = "recentSessions";

    private const int MAX_RECENT_SESSIONS = 100;

    // ── In-memory caches ─────────────────────────────────────────
    private PlayerStats cachedStats;
    private HandHistoryIndex cachedIndex;
    private readonly Dictionary<string, List<HandRecord>> cachedSessionHands
        = new Dictionary<string, List<HandRecord>>();

    // Track which sessions we've already created (avoids redundant reads)
    private readonly HashSet<string> createdSessions = new HashSet<string>();

    // Local session counter — avoids a Firestore read on every registration.
    // Seeded from LoadIndexAsync when available; -1 = not yet initialised.
    private int knownSessionCount = -1;

    // ══════════════════════════════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════════════════════════════

    private bool IsReady() =>
        FirebaseManager.Instance != null && FirebaseManager.Instance.IsFirebaseReady;

    private FirebaseFirestore Db => FirebaseManager.Instance.Firestore;
    private string Uid => FirebaseManager.Instance.UserId;

    private DocumentReference PlayerDoc() =>
        Db.Collection(COL_PLAYERS).Document(Uid);

    private DocumentReference SessionDoc(string sessionId) =>
        Db.Collection(COL_SESSIONS).Document(sessionId);

    private DocumentReference HandDoc(string sessionId, int handNumber) =>
        SessionDoc(sessionId).Collection(COL_HANDS).Document(handNumber.ToString());

    // ══════════════════════════════════════════════════════════════
    //  IHandHistoryWriter — SaveHandAsync  (HOST only)
    // ══════════════════════════════════════════════════════════════

    public async Task SaveHandAsync(HandRecord hand, TableSessionEntry sessionMeta)
    {
        if (!IsReady())
        {
            Debug.LogWarning($"[{LogTag.Save}] Firebase not ready — hand not saved");
            return;
        }

        try
        {
            WriteBatch batch = Db.StartBatch();

            // ── 1. Create / update session document ──────────────
            var sessionRef = SessionDoc(hand.tableSessionId);
            bool isNewSession = !createdSessions.Contains(hand.tableSessionId);

            var sessionData = new Dictionary<string, object>
            {
                { F_TABLE_NAME,    sessionMeta.tableName },
                { F_STATUS,        "active" },
                { F_SMALL_BLIND,   (double)sessionMeta.smallBlind },
                { F_BIG_BLIND,     (double)sessionMeta.bigBlind },
                { F_MIN_BUY_IN,    (double)sessionMeta.minBuyIn },
                { F_MAX_BUY_IN,    (double)sessionMeta.maxBuyIn },
                { F_LAST_ACTIVITY, FieldValue.ServerTimestamp },
                { F_HAND_COUNT,    hand.handNumber },
            };

            // Set createdAt only on the very first hand of the session
            if (isNewSession)
            {
                sessionData[F_CREATED_AT] = FieldValue.ServerTimestamp;
            }

            batch.Set(sessionRef, sessionData, SetOptions.MergeAll);

            // ── 2. Write hand sub-document ───────────────────────
            var handRef = HandDoc(hand.tableSessionId, hand.handNumber);
            var handData = BuildHandDocument(hand);
            batch.Set(handRef, handData);

            // ── 3. Commit ────────────────────────────────────────
            await batch.CommitAsync();

            createdSessions.Add(hand.tableSessionId);

            // Update local cache
            if (!cachedSessionHands.ContainsKey(hand.tableSessionId))
                cachedSessionHands[hand.tableSessionId] = new List<HandRecord>();
            cachedSessionHands[hand.tableSessionId].Add(hand);

            Debug.Log($"[{LogTag.Save}] Hand saved — hand={hand.handNumber}, session={hand.tableSessionId}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[{LogTag.Save}] Hand save failed — hand={hand.handNumber}, error={e.Message}");
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  IHandHistoryWriter — SavePlayerStatsAsync  (ALL clients)
    // ══════════════════════════════════════════════════════════════

    public async Task SavePlayerStatsAsync(PlayerStats stats)
    {
        if (!IsReady())
        {
            Debug.LogWarning($"[{LogTag.Save}] Firebase not ready — stats not saved");
            return;
        }

        try
        {
            var statsMap = new Dictionary<string, object>
            {
                { "totalHands",       stats.totalHands },
                { "vpipHands",        stats.vpipHands },
                { "pfrHands",         stats.pfrHands },
                { "aggressionBets",   stats.aggressionBets },
                { "aggressionCalls",  stats.aggressionCalls },
                { "handsWon",         stats.handsWon },
                { "netProfit",        (double)stats.netProfit },
                { "biggestPotWon",    (double)stats.biggestPotWon },
                { "showdownsReached", stats.showdownsReached },
                { "showdownsWon",     stats.showdownsWon },
            };

            // MergeAll: only touches the stats map, leaves profile fields intact
            await PlayerDoc().SetAsync(
                new Dictionary<string, object> { { F_STATS, statsMap } },
                SetOptions.MergeAll);

            cachedStats = stats;
            Debug.Log($"[{LogTag.Save}] Player stats saved — uid={Uid}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[{LogTag.Save}] Player stats save failed — uid={Uid}, error={e.Message}");
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  IHandHistoryWriter — RegisterSessionParticipantAsync
    // ══════════════════════════════════════════════════════════════

    public async Task RegisterSessionParticipantAsync(string sessionId, TableSessionEntry sessionMeta)
    {
        if (!IsReady() || string.IsNullOrEmpty(Uid)) return;

        try
        {
            // ── Session metadata to embed in the player doc ──────
            // Static fields set once at registration.  Per-hand fields
            // (handCount, netProfit, etc.) are updated by
            // UpdateSessionPlayerSummaryAsync, which uses MergeAll
            // on the same map key — so both coexist without overwriting.
            var sessionEntry = new Dictionary<string, object>
            {
                { "tableName",  sessionMeta?.tableName ?? "" },
                { "smallBlind", (double)(sessionMeta?.smallBlind ?? 0f) },
                { "bigBlind",   (double)(sessionMeta?.bigBlind ?? 0f) },
                { "minBuyIn",   (double)(sessionMeta?.minBuyIn ?? 0f) },
                { "maxBuyIn",   (double)(sessionMeta?.maxBuyIn ?? 0f) },
                { "joinedAt",   FieldValue.ServerTimestamp },
            };

            // Batch: 2 document writes, 1 network round-trip.
            //   1. Add UID to session doc's playerIds array
            //   2. Embed session metadata in player doc's recentSessions map
            WriteBatch batch = Db.StartBatch();

            batch.Set(SessionDoc(sessionId),
                new Dictionary<string, object>
                {
                    { F_PLAYER_IDS, FieldValue.ArrayUnion(Uid) }
                },
                SetOptions.MergeAll);

            batch.Set(PlayerDoc(),
                new Dictionary<string, object>
                {
                    { F_RECENT_SESSIONS, new Dictionary<string, object>
                        {
                            { sessionId, sessionEntry }
                        }
                    }
                },
                SetOptions.MergeAll);

            await batch.CommitAsync();

            // Invalidate cached index so the new session shows up
            cachedIndex = null;

            // Track session count locally to avoid unnecessary Firestore reads
            if (knownSessionCount >= 0)
                knownSessionCount++;

            Debug.Log($"[{LogTag.Save}] Registered as participant — session={sessionId}, uid={Uid}");

            // Only attempt cleanup when the local counter says we've crossed
            // the threshold.  If the counter is uninitialised (-1), skip —
            // it will be seeded the next time LoadIndexAsync runs.
            int threshold = MAX_RECENT_SESSIONS * 2;
            if (knownSessionCount > threshold)
                await EnforceRecentSessionCapAsync();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[{LogTag.Save}] Failed to register as participant — session={sessionId}, error={e.Message}");
        }
    }

    /// <summary>
    /// Trims the player's recentSessions map to <see cref="MAX_RECENT_SESSIONS"/>.
    /// To avoid a Firestore read + write on every single new session, this
    /// only fires when the count exceeds 2× the cap (i.e. 200).  When it
    /// does trigger, it deletes the oldest entries in one batch to bring
    /// the count back down to MAX (100).
    /// </summary>
    private async Task EnforceRecentSessionCapAsync()
    {
        try
        {
            DocumentSnapshot snap = await PlayerDoc().GetSnapshotAsync();
            if (!snap.ContainsField(F_RECENT_SESSIONS)) return;

            var sessionsMap = snap.GetValue<Dictionary<string, object>>(F_RECENT_SESSIONS);

            // Only trigger cleanup when we've accumulated 2× the cap.
            // This turns ~100 individual read+write pairs into 1 bulk operation.
            int threshold = MAX_RECENT_SESSIONS * 2;
            if (sessionsMap.Count <= threshold) return;

            // Collect session IDs with their lastActivity/joinedAt for sorting
            var entries = new List<KeyValuePair<string, long>>();
            foreach (var kvp in sessionsMap)
            {
                long ts = 0;
                if (kvp.Value is Dictionary<string, object> data)
                {
                    // Prefer lastActivity, fall back to joinedAt
                    if (data.ContainsKey("lastActivity") && data["lastActivity"] is Timestamp laTs)
                        ts = laTs.ToDateTimeOffset().ToUnixTimeMilliseconds();
                    else if (data.ContainsKey("joinedAt") && data["joinedAt"] is Timestamp jaTs)
                        ts = jaTs.ToDateTimeOffset().ToUnixTimeMilliseconds();
                }
                entries.Add(new KeyValuePair<string, long>(kvp.Key, ts));
            }

            // Sort oldest first
            entries.Sort((a, b) => a.Value.CompareTo(b.Value));

            // Delete the oldest entries to bring count back down to MAX
            int toRemove = entries.Count - MAX_RECENT_SESSIONS;
            var deletes = new Dictionary<string, object>();
            for (int i = 0; i < toRemove; i++)
            {
                // Dot-notation targets a specific key inside the recentSessions map
                deletes[$"{F_RECENT_SESSIONS}.{entries[i].Key}"] = FieldValue.Delete;
            }

            await PlayerDoc().UpdateAsync(deletes);

            // Reset local counter to reflect the trimmed state
            knownSessionCount = MAX_RECENT_SESSIONS;

            Debug.Log($"[{LogTag.Save}] Trimmed recent sessions — from={entries.Count}, to={MAX_RECENT_SESSIONS}, removed={toRemove}");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[{LogTag.Save}] Failed to enforce session cap — error={e.Message}");
        }
    }

    /// <summary>
    /// Removes junk entries (0-hand sessions) from the player doc's recentSessions map.
    /// Fire-and-forget — uses a single Firestore write with no extra reads.
    /// </summary>
    private async Task PurgeJunkSessionsAsync(List<string> sessionIds)
    {
        try
        {
            var deletes = new Dictionary<string, object>();
            foreach (string id in sessionIds)
                deletes[$"{F_RECENT_SESSIONS}.{id}"] = FieldValue.Delete;

            await PlayerDoc().UpdateAsync(deletes);

            Debug.Log($"[{LogTag.Save}] Purged junk sessions — count={sessionIds.Count}, reason=zeroHands");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[{LogTag.Save}] Failed to purge junk sessions — error={e.Message}");
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  IHandHistoryWriter — UpdateSessionPlayerSummaryAsync
    //  (ALL clients — each writes their own UID-keyed entry)
    // ══════════════════════════════════════════════════════════════

    public async Task UpdateSessionPlayerSummaryAsync(string sessionId, SessionPlayerSummary summary, int playerCount = 0)
    {
        if (!IsReady() || string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(Uid)) return;

        try
        {
            var summaryMap = new Dictionary<string, object>
            {
                { "displayName", summary.displayName ?? "" },
                { "characterId", summary.characterId },
                { "totalBuyIn",  (double)summary.totalBuyIn },
                { "netProfit",   (double)summary.netResult },
                { "handsPlayed", summary.handsPlayed },
            };

            // ── Per-hand fields for the player doc's recentSessions entry ──
            // These merge with the static fields written by
            // RegisterSessionParticipantAsync (tableName, blinds, joinedAt).
            var playerSessionUpdate = new Dictionary<string, object>
            {
                { "lastActivity",  FieldValue.ServerTimestamp },
                { "myBuyIn",       (double)summary.totalBuyIn },
                { "myNetProfit",   (double)summary.netResult },
                { "myHandsPlayed", summary.handsPlayed },
            };

            // Include total player count if provided (> 0)
            if (playerCount > 0)
                playerSessionUpdate["playerCount"] = playerCount;

            // Batch: 2 document writes, 1 network round-trip.
            //   1. Session doc — per-player summary (UID-keyed map)
            //   2. Player doc  — recentSessions.{sessionId} live data
            WriteBatch batch = Db.StartBatch();

            batch.Set(SessionDoc(sessionId),
                new Dictionary<string, object>
                {
                    { F_PLAYER_SUMMARIES, new Dictionary<string, object>
                        {
                            { Uid, summaryMap }
                        }
                    }
                },
                SetOptions.MergeAll);

            batch.Set(PlayerDoc(),
                new Dictionary<string, object>
                {
                    { F_RECENT_SESSIONS, new Dictionary<string, object>
                        {
                            { sessionId, playerSessionUpdate }
                        }
                    }
                },
                SetOptions.MergeAll);

            await batch.CommitAsync();

            // Invalidate cached index since the session data changed
            cachedIndex = null;

            Debug.Log($"[{LogTag.Save}] Updated player summary — uid={Uid}, session={sessionId}, buyIn=${summary.totalBuyIn:F2}, net=${summary.netResult:F2}, hands={summary.handsPlayed}");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[{LogTag.Save}] Failed to update player summary — session={sessionId}, error={e.Message}");
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  IHandHistoryReader — LoadIndexAsync
    // ══════════════════════════════════════════════════════════════

    public async Task<HandHistoryIndex> LoadIndexAsync()
    {
        // ── In-memory cache — 0 reads on repeat calls ────────────
        if (cachedIndex != null) return cachedIndex;

        if (!IsReady()) return new HandHistoryIndex();

        try
        {
            // ── 1 single Firestore read — the player document ────
            // The recentSessions map contains all session metadata
            // embedded at registration + updated per hand, so no
            // additional session-doc fetches are needed.
            DocumentSnapshot playerSnap = await PlayerDoc().GetSnapshotAsync();

            if (!playerSnap.Exists || !playerSnap.ContainsField(F_RECENT_SESSIONS))
            {
                Debug.Log($"[{LogTag.Save}] No recent sessions found — uid={Uid}");
                cachedIndex = new HandHistoryIndex();
                return cachedIndex;
            }

            var sessionsMap = playerSnap.GetValue<Dictionary<string, object>>(F_RECENT_SESSIONS);
            if (sessionsMap.Count == 0)
            {
                cachedIndex = new HandHistoryIndex();
                return cachedIndex;
            }

            // ── Parse each session entry from the embedded map ───
            var index = new HandHistoryIndex();
            var junkSessionIds = new List<string>();

            foreach (var kvp in sessionsMap)
            {
                try
                {
                    var data = (Dictionary<string, object>)kvp.Value;
                    var entry = ParseEmbeddedSessionEntry(kvp.Key, data);

                    // Skip sessions where the player never completed a hand
                    if (entry.handCount <= 0)
                    {
                        junkSessionIds.Add(kvp.Key);
                        continue;
                    }

                    index.sessions.Add(entry);
                    index.totalHandCount += entry.handCount;
                }
                catch (Exception parseEx)
                {
                    Debug.LogWarning($"[{LogTag.Save}] Skipping malformed session — session={kvp.Key}, error={parseEx.Message}");
                    junkSessionIds.Add(kvp.Key);
                }
            }

            // Sort by last activity descending (newest first)
            index.sessions.Sort((a, b) => b.lastActivityUtcMs.CompareTo(a.lastActivityUtcMs));

            cachedIndex = index;

            // Seed the local counter so EnforceRecentSessionCapAsync
            // can skip its Firestore read until we actually exceed the threshold.
            knownSessionCount = index.sessions.Count;

            Debug.Log($"[{LogTag.Save}] Index loaded — sessions={index.sessions.Count}, totalHands={index.totalHandCount}, reads=1");

            // ── Housekeeping: purge junk entries (0-hand sessions) ──
            // Single write, no extra reads — uses the data we already fetched.
            if (junkSessionIds.Count > 0)
                _ = PurgeJunkSessionsAsync(junkSessionIds);

            return index;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[{LogTag.Save}] Failed to load session index — error={e.Message}");
            return new HandHistoryIndex();
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  IHandHistoryReader — LoadSessionHandsAsync
    // ══════════════════════════════════════════════════════════════

    public async Task<SessionHandData> LoadSessionHandsAsync(string sessionId)
    {
        // Check cache first
        if (cachedSessionHands.TryGetValue(sessionId, out var cached))
        {
            return new SessionHandData { sessionId = sessionId, hands = cached };
        }

        if (!IsReady()) return null;

        try
        {
            Query query = SessionDoc(sessionId)
                .Collection(COL_HANDS)
                .OrderBy(F_HAND_NUMBER);

            QuerySnapshot snapshot = await query.GetSnapshotAsync();

            var data = new SessionHandData { sessionId = sessionId };

            foreach (DocumentSnapshot doc in snapshot.Documents)
            {
                var hand = ParseHandRecord(doc, sessionId);
                data.hands.Add(hand);
            }

            cachedSessionHands[sessionId] = data.hands;

            Debug.Log($"[{LogTag.Save}] Session hands loaded — session={sessionId}, hands={data.hands.Count}");
            return data;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[{LogTag.Save}] Failed to load session hands — session={sessionId}, error={e.Message}");
            return null;
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  IHandHistoryReader — LoadSessionEntryAsync
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Reads the full session document (/sessions/{id}) which contains
    /// ALL players' summaries, not just the local player's embedded data.
    /// 1 Firestore read.
    /// </summary>
    public async Task<TableSessionEntry> LoadSessionEntryAsync(string sessionId)
    {
        if (!IsReady()) return null;

        try
        {
            DocumentSnapshot snap = await SessionDoc(sessionId).GetSnapshotAsync();
            if (!snap.Exists)
            {
                Debug.LogWarning($"[{LogTag.Save}] Session not found — session={sessionId}");
                return null;
            }

            var entry = ParseSessionEntry(snap);
            Debug.Log($"[{LogTag.Save}] Session entry loaded — session={sessionId}, playerSummaries={entry.playerSummaries.Count}");
            return entry;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[{LogTag.Save}] Failed to load session entry — session={sessionId}, error={e.Message}");
            return null;
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  IHandHistoryReader — LoadPlayerStatsAsync
    // ══════════════════════════════════════════════════════════════

    public async Task<PlayerStats> LoadPlayerStatsAsync()
    {
        if (cachedStats != null) return cachedStats;
        if (!IsReady()) return new PlayerStats();

        try
        {
            DocumentSnapshot snap = await PlayerDoc().GetSnapshotAsync();

            if (snap.Exists && snap.ContainsField(F_STATS))
            {
                var statsDict = snap.GetValue<Dictionary<string, object>>(F_STATS);
                cachedStats = ParsePlayerStats(statsDict);
                Debug.Log($"[{LogTag.Save}] Player stats loaded — uid={Uid}, source=Firestore");
                return cachedStats;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[{LogTag.Save}] Failed to load player stats — error={e.Message}");
        }

        cachedStats = new PlayerStats();
        return cachedStats;
    }

    // ══════════════════════════════════════════════════════════════
    //  IHandHistoryStorage — ClearCaches
    // ══════════════════════════════════════════════════════════════

    public void ClearCaches()
    {
        cachedStats = null;
        cachedIndex = null;
        cachedSessionHands.Clear();
        createdSessions.Clear();
        knownSessionCount = -1;
    }

    // ══════════════════════════════════════════════════════════════
    //  Building Firestore documents from C# objects
    // ══════════════════════════════════════════════════════════════

    private Dictionary<string, object> BuildHandDocument(HandRecord hand)
    {
        var doc = new Dictionary<string, object>
        {
            { F_HAND_NUMBER,  hand.handNumber },
            { F_TIMESTAMP,    FieldValue.ServerTimestamp },
            { F_DEALER_SLOT,  hand.dealerSlotIndex },
            { F_SB_SLOT,      hand.sbSlotIndex },
            { F_BB_SLOT,      hand.bbSlotIndex },
            { F_POT_TOTAL,    (double)hand.potTotal },
            { F_FINAL_PHASE,  hand.finalPhase ?? "" },
        };

        // ── Community cards: array of {rank, suit} maps ──────────
        var ccList = new List<Dictionary<string, object>>();
        if (hand.communityCardRanks != null)
        {
            for (int i = 0; i < hand.communityCardRanks.Count; i++)
            {
                if (hand.communityCardRanks[i] != 0) // skip undealt cards
                {
                    ccList.Add(new Dictionary<string, object>
                    {
                        { "rank", hand.communityCardRanks[i] },
                        { "suit", hand.communityCardSuits[i] },
                    });
                }
            }
        }
        doc[F_COMMUNITY_CARDS] = ccList;

        // ── Seats: map keyed by slot index ───────────────────────
        var seatsMap = new Dictionary<string, object>();
        foreach (var p in hand.players)
        {
            seatsMap[p.slotIndex.ToString()] = new Dictionary<string, object>
            {
                { "displayName",   p.displayName ?? "" },
                { "startingStack", (double)p.stackAtStart },
            };
        }
        doc[F_SEATS] = seatsMap;

        // ── Actions: ordered array of maps ───────────────────────
        var actionsList = new List<Dictionary<string, object>>();
        foreach (var a in hand.actions)
        {
            actionsList.Add(new Dictionary<string, object>
            {
                { "slot",   a.playerSlotIndex },
                { "phase",  a.phase },
                { "action", a.actionType },
                { "amount", (double)a.amount },
            });
        }
        doc[F_ACTIONS] = actionsList;

        // ── Winners: array of maps ───────────────────────────────
        var winnersList = new List<Dictionary<string, object>>();
        foreach (var w in hand.winners)
        {
            winnersList.Add(new Dictionary<string, object>
            {
                { "slot",        w.slotIndex },
                { "displayName", w.displayName ?? "" },
                { "amount",      (double)w.amount },
            });
        }
        doc[F_WINNERS] = winnersList;

        return doc;
    }

    // ══════════════════════════════════════════════════════════════
    //  Parsing Firestore documents back to C# objects
    // ══════════════════════════════════════════════════════════════

    private TableSessionEntry ParseSessionEntry(DocumentSnapshot doc)
    {
        var entry = new TableSessionEntry
        {
            sessionId  = doc.Id,
            tableName  = GetString(doc, F_TABLE_NAME),
            status     = GetString(doc, F_STATUS, "active"),
            smallBlind = GetFloat(doc, F_SMALL_BLIND),
            bigBlind   = GetFloat(doc, F_BIG_BLIND),
            minBuyIn   = GetFloat(doc, F_MIN_BUY_IN),
            maxBuyIn   = GetFloat(doc, F_MAX_BUY_IN),
            handCount  = GetInt(doc, F_HAND_COUNT),
        };

        if (doc.ContainsField(F_CREATED_AT))
        {
            var ts = doc.GetValue<Timestamp>(F_CREATED_AT);
            entry.createdAtUtcMs = ts.ToDateTimeOffset().ToUnixTimeMilliseconds();
        }

        if (doc.ContainsField(F_LAST_ACTIVITY))
        {
            var ts = doc.GetValue<Timestamp>(F_LAST_ACTIVITY);
            entry.lastActivityUtcMs = ts.ToDateTimeOffset().ToUnixTimeMilliseconds();
        }

        if (doc.ContainsField(F_PLAYER_IDS))
        {
            var ids = doc.GetValue<List<object>>(F_PLAYER_IDS);
            entry.playerIds = new List<string>();
            foreach (var id in ids)
                entry.playerIds.Add(id.ToString());
            entry.playerCount = entry.playerIds.Count;
        }

        // Parse per-player summaries (map keyed by UID)
        if (doc.ContainsField(F_PLAYER_SUMMARIES))
        {
            try
            {
                var summariesMap = doc.GetValue<Dictionary<string, object>>(F_PLAYER_SUMMARIES);
                foreach (var kvp in summariesMap)
                {
                    var data = (Dictionary<string, object>)kvp.Value;
                    entry.playerSummaries.Add(new SessionPlayerSummary
                    {
                        playerId    = kvp.Key,
                        displayName = SafeString(data, "displayName"),
                        characterId = SafeInt(data, "characterId"),
                        totalBuyIn  = SafeFloat(data, "totalBuyIn"),
                        netResult   = SafeFloat(data, "netProfit"),
                        handsPlayed = SafeInt(data, "handsPlayed"),
                    });
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{LogTag.Save}] Failed to parse player summaries — error={e.Message}");
            }
        }

        return entry;
    }

    /// <summary>
    /// Parses a session entry from the embedded recentSessions map
    /// on the player document.  This avoids fetching the session doc.
    /// </summary>
    private TableSessionEntry ParseEmbeddedSessionEntry(string sessionId, Dictionary<string, object> data)
    {
        var entry = new TableSessionEntry
        {
            sessionId   = sessionId,
            tableName   = SafeString(data, "tableName"),
            smallBlind  = SafeFloat(data, "smallBlind"),
            bigBlind    = SafeFloat(data, "bigBlind"),
            minBuyIn    = SafeFloat(data, "minBuyIn"),
            maxBuyIn    = SafeFloat(data, "maxBuyIn"),
            handCount   = SafeInt(data, "myHandsPlayed"),
            playerCount = SafeInt(data, "playerCount"),
            status      = "active",
        };

        // Timestamps (stored as Firestore Timestamp objects)
        if (data.ContainsKey("joinedAt") && data["joinedAt"] is Timestamp joinedTs)
            entry.createdAtUtcMs = joinedTs.ToDateTimeOffset().ToUnixTimeMilliseconds();

        if (data.ContainsKey("lastActivity") && data["lastActivity"] is Timestamp lastTs)
            entry.lastActivityUtcMs = lastTs.ToDateTimeOffset().ToUnixTimeMilliseconds();

        // Populate the local player's summary from the embedded per-hand data
        float myBuyIn       = SafeFloat(data, "myBuyIn");
        float myNetProfit   = SafeFloat(data, "myNetProfit");
        int   myHandsPlayed = SafeInt(data, "myHandsPlayed");

        if (myHandsPlayed > 0 || myBuyIn > 0)
        {
            entry.playerSummaries.Add(new SessionPlayerSummary
            {
                playerId    = Uid,
                totalBuyIn  = myBuyIn,
                netResult   = myNetProfit,
                handsPlayed = myHandsPlayed,
            });
        }

        return entry;
    }

    private HandRecord ParseHandRecord(DocumentSnapshot doc, string sessionId)
    {
        var hand = new HandRecord
        {
            tableSessionId  = sessionId,
            handNumber      = GetInt(doc, F_HAND_NUMBER),
            dealerSlotIndex = GetInt(doc, F_DEALER_SLOT),
            sbSlotIndex     = GetInt(doc, F_SB_SLOT),
            bbSlotIndex     = GetInt(doc, F_BB_SLOT),
            potTotal        = GetFloat(doc, F_POT_TOTAL),
            finalPhase      = GetString(doc, F_FINAL_PHASE),
        };

        if (doc.ContainsField(F_TIMESTAMP))
        {
            var ts = doc.GetValue<Timestamp>(F_TIMESTAMP);
            hand.timestampUtcMs = ts.ToDateTimeOffset().ToUnixTimeMilliseconds();
        }

        // ── Community cards ──────────────────────────────────────
        if (doc.ContainsField(F_COMMUNITY_CARDS))
        {
            var ccList = doc.GetValue<List<object>>(F_COMMUNITY_CARDS);
            foreach (var ccObj in ccList)
            {
                var cc = (Dictionary<string, object>)ccObj;
                hand.communityCardRanks.Add(SafeInt(cc, "rank"));
                hand.communityCardSuits.Add(SafeInt(cc, "suit"));
            }
        }

        // ── Seats ────────────────────────────────────────────────
        if (doc.ContainsField(F_SEATS))
        {
            var seatsMap = doc.GetValue<Dictionary<string, object>>(F_SEATS);
            foreach (var kvp in seatsMap)
            {
                var seatData = (Dictionary<string, object>)kvp.Value;
                hand.players.Add(new HandPlayerInfo
                {
                    slotIndex    = int.Parse(kvp.Key),
                    displayName  = SafeString(seatData, "displayName"),
                    stackAtStart = SafeFloat(seatData, "startingStack"),
                });
            }
        }

        // ── Actions ──────────────────────────────────────────────
        if (doc.ContainsField(F_ACTIONS))
        {
            var actionsList = doc.GetValue<List<object>>(F_ACTIONS);
            foreach (var actObj in actionsList)
            {
                var a = (Dictionary<string, object>)actObj;
                hand.actions.Add(new HandAction
                {
                    playerSlotIndex = SafeInt(a, "slot"),
                    phase           = SafeInt(a, "phase"),
                    actionType      = SafeString(a, "action"),
                    amount          = SafeFloat(a, "amount"),
                });
            }
        }

        // ── Winners ──────────────────────────────────────────────
        if (doc.ContainsField(F_WINNERS))
        {
            var winnersList = doc.GetValue<List<object>>(F_WINNERS);
            foreach (var winObj in winnersList)
            {
                var w = (Dictionary<string, object>)winObj;
                hand.winners.Add(new HandWinner
                {
                    slotIndex   = SafeInt(w, "slot"),
                    displayName = SafeString(w, "displayName"),
                    amount      = SafeFloat(w, "amount"),
                });
            }
        }

        return hand;
    }

    private PlayerStats ParsePlayerStats(Dictionary<string, object> dict)
    {
        var stats = new PlayerStats
        {
            totalHands       = SafeInt(dict, "totalHands"),
            vpipHands        = SafeInt(dict, "vpipHands"),
            pfrHands         = SafeInt(dict, "pfrHands"),
            aggressionBets   = SafeInt(dict, "aggressionBets"),
            aggressionCalls  = SafeInt(dict, "aggressionCalls"),
            handsWon         = SafeInt(dict, "handsWon"),
            netProfit        = SafeFloat(dict, "netProfit"),
            biggestPotWon    = SafeFloat(dict, "biggestPotWon"),
            showdownsReached = SafeInt(dict, "showdownsReached"),
            showdownsWon     = SafeInt(dict, "showdownsWon"),
        };
        return stats;
    }

    // ══════════════════════════════════════════════════════════════
    //  Safe field accessors (Firestore stores ints as long,
    //  floats as double — these handle the casts gracefully)
    // ══════════════════════════════════════════════════════════════

    private static string GetString(DocumentSnapshot doc, string field, string fallback = "")
    {
        return doc.ContainsField(field) ? doc.GetValue<string>(field) : fallback;
    }

    private static int GetInt(DocumentSnapshot doc, string field)
    {
        return doc.ContainsField(field) ? (int)doc.GetValue<long>(field) : 0;
    }

    private static float GetFloat(DocumentSnapshot doc, string field)
    {
        return doc.ContainsField(field) ? (float)doc.GetValue<double>(field) : 0f;
    }

    private static int SafeInt(Dictionary<string, object> dict, string key)
    {
        if (dict.TryGetValue(key, out var val) && val != null)
            return (int)(long)val;
        return 0;
    }

    private static float SafeFloat(Dictionary<string, object> dict, string key)
    {
        if (dict.TryGetValue(key, out var val) && val != null)
            return (float)(double)val;
        return 0f;
    }

    private static string SafeString(Dictionary<string, object> dict, string key)
    {
        if (dict.TryGetValue(key, out var val) && val != null)
            return val.ToString();
        return "";
    }
}
