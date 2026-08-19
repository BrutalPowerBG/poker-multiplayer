using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Services.Authentication;
using Unity.Services.CloudSave;
using Unity.Services.Core;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════
//  Hand History — Unity Cloud Save Implementation
//
//  Cloud Save keys used (all player-scoped):
//    hh_index              → HandHistoryIndex   (session list + totals)
//    hh_s_{sessionId}      → SessionHandData    (hand records per session)
//    hh_stats              → PlayerStats         (lifetime aggregates)
//
//  Size management:
//    • Each key has a ~200 KB limit.
//    • When totalHandCount exceeds MAX_TOTAL_HANDS (100), the oldest
//      session's hand data is deleted and removed from the index until
//      the count is back within the limit.
//
//  Unlike Firestore, Cloud Save is player-scoped so every client
//  records their own copy of hand history via SaveHandAsync.
// ═══════════════════════════════════════════════════════════════════

public class CloudSaveHandHistory : IHandHistoryStorage
{
    private const string KEY_INDEX = "hh_index";
    private const string KEY_STATS = "hh_stats";
    private const int MAX_TOTAL_HANDS = 100;

    // ── In-memory caches ─────────────────────────────────────────
    private HandHistoryIndex cachedIndex;
    private PlayerStats cachedStats;
    private readonly Dictionary<string, SessionHandData> cachedSessions
        = new Dictionary<string, SessionHandData>();

    // ══════════════════════════════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════════════════════════════

    private static string SessionKey(string sessionId) => $"hh_s_{sessionId}";

    private bool IsReady()
    {
        return UnityServices.State == ServicesInitializationState.Initialized
            && AuthenticationService.Instance.IsSignedIn;
    }

    // ══════════════════════════════════════════════════════════════
    //  IHandHistoryWriter
    // ══════════════════════════════════════════════════════════════

    public async Task SaveHandAsync(HandRecord hand, TableSessionEntry sessionMeta)
    {
        if (!IsReady())
        {
            Debug.LogWarning($"[{LogTag.Save}] Cloud Save not ready — hand not saved");
            return;
        }

        try
        {
            // 1. Load / create index
            HandHistoryIndex index = await LoadIndexInternalAsync();

            // 2. Find or create the session entry in the index
            TableSessionEntry entry = index.sessions.Find(s => s.sessionId == hand.tableSessionId);
            if (entry == null)
            {
                entry = new TableSessionEntry
                {
                    sessionId       = sessionMeta.sessionId,
                    tableName       = sessionMeta.tableName,
                    status          = "active",
                    smallBlind      = sessionMeta.smallBlind,
                    bigBlind        = sessionMeta.bigBlind,
                    minBuyIn        = sessionMeta.minBuyIn,
                    maxBuyIn        = sessionMeta.maxBuyIn,
                    createdAtUtcMs  = hand.timestampUtcMs,
                    playerSummaries = new List<SessionPlayerSummary>()
                };
                index.sessions.Add(entry);
            }

            entry.lastActivityUtcMs = hand.timestampUtcMs;
            entry.handCount++;
            index.totalHandCount++;

            // Update per-player summaries in the session entry
            UpdateSessionPlayerSummaries(entry, hand, sessionMeta);

            // 3. Load / create session hand data
            SessionHandData sessionData = await LoadSessionInternalAsync(hand.tableSessionId);
            if (sessionData == null)
            {
                sessionData = new SessionHandData { sessionId = hand.tableSessionId };
            }
            sessionData.hands.Add(hand);

            // 4. Enforce the 100-hand cap
            await EnforceHandCapAsync(index);

            // 5. Persist session data + index
            string sessionJson = JsonUtility.ToJson(sessionData);
            string indexJson = JsonUtility.ToJson(index);

            var savePayload = new Dictionary<string, object>
            {
                { SessionKey(hand.tableSessionId), sessionJson },
                { KEY_INDEX, indexJson }
            };

            await CloudSaveService.Instance.Data.Player.SaveAsync(savePayload);

            cachedIndex = index;
            cachedSessions[hand.tableSessionId] = sessionData;

            Debug.Log($"[{LogTag.Save}] Hand saved — hand={hand.handNumber}, session={hand.tableSessionId}, totalHands={index.totalHandCount}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[{LogTag.Save}] Hand save failed — error={e.Message}");
        }
    }

    public async Task SavePlayerStatsAsync(PlayerStats stats)
    {
        if (!IsReady())
        {
            Debug.LogWarning($"[{LogTag.Save}] Cloud Save not ready — stats not saved");
            return;
        }

        try
        {
            string json = JsonUtility.ToJson(stats);
            var data = new Dictionary<string, object> { { KEY_STATS, json } };
            await CloudSaveService.Instance.Data.Player.SaveAsync(data);
            cachedStats = stats;
            Debug.Log($"[{LogTag.Save}] Player stats saved");
        }
        catch (Exception e)
        {
            Debug.LogError($"[{LogTag.Save}] Player stats save failed — error={e.Message}");
        }
    }

    /// <summary>
    /// No-op for Cloud Save — player-scoped data doesn't need participant registration.
    /// </summary>
    public Task RegisterSessionParticipantAsync(string sessionId, TableSessionEntry sessionMeta)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// For Cloud Save, updates the cached index entry with local player summary data
    /// (totalBuyIn, playerId). The data is persisted on the next SaveHandAsync call.
    /// netResult and handsPlayed are still computed per-hand in UpdateSessionPlayerSummaries.
    /// </summary>
    public async Task UpdateSessionPlayerSummaryAsync(string sessionId, SessionPlayerSummary summary, int playerCount = 0)
    {
        HandHistoryIndex index = await LoadIndexInternalAsync();

        TableSessionEntry entry = index.sessions.Find(s => s.sessionId == sessionId);
        if (entry == null) return; // Session not yet created — will be handled in SaveHandAsync

        // Update session-level player count
        if (playerCount > 0)
            entry.playerCount = playerCount;

        var existing = entry.playerSummaries.Find(s => s.displayName == summary.displayName);
        if (existing != null)
        {
            // Preserve totalBuyIn and playerId; netResult + handsPlayed are
            // authoritative from UpdateSessionPlayerSummaries.
            existing.totalBuyIn = summary.totalBuyIn;
            if (!string.IsNullOrEmpty(summary.playerId))
                existing.playerId = summary.playerId;
        }
        else
        {
            entry.playerSummaries.Add(new SessionPlayerSummary
            {
                playerId    = summary.playerId,
                displayName = summary.displayName,
                totalBuyIn  = summary.totalBuyIn,
                // netResult and handsPlayed start at 0, will be incremented
                // by UpdateSessionPlayerSummaries on the first hand.
            });
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  IHandHistoryReader
    // ══════════════════════════════════════════════════════════════

    public async Task<HandHistoryIndex> LoadIndexAsync()
    {
        return await LoadIndexInternalAsync();
    }

    public async Task<SessionHandData> LoadSessionHandsAsync(string sessionId)
    {
        return await LoadSessionInternalAsync(sessionId);
    }

    public async Task<PlayerStats> LoadPlayerStatsAsync()
    {
        if (cachedStats != null) return cachedStats;

        if (!IsReady()) return new PlayerStats();

        try
        {
            var keys = new HashSet<string> { KEY_STATS };
            var data = await CloudSaveService.Instance.Data.Player.LoadAsync(keys);

            if (data.TryGetValue(KEY_STATS, out var item))
            {
                string json = item.Value.GetAsString();
                cachedStats = JsonUtility.FromJson<PlayerStats>(json);
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

    public async Task<TableSessionEntry> LoadSessionEntryAsync(string sessionId)
    {
        // Cloud Save is player-scoped so we can only return the local
        // player's data from the cached index.
        var index = await LoadIndexAsync();
        if (index?.sessions != null)
        {
            foreach (var s in index.sessions)
            {
                if (s.sessionId == sessionId) return s;
            }
        }
        return null;
    }

    // ══════════════════════════════════════════════════════════════
    //  Internal load helpers (with caching)
    // ══════════════════════════════════════════════════════════════

    private async Task<HandHistoryIndex> LoadIndexInternalAsync()
    {
        if (cachedIndex != null) return cachedIndex;

        if (!IsReady()) return new HandHistoryIndex();

        try
        {
            var keys = new HashSet<string> { KEY_INDEX };
            var data = await CloudSaveService.Instance.Data.Player.LoadAsync(keys);

            if (data.TryGetValue(KEY_INDEX, out var item))
            {
                string json = item.Value.GetAsString();
                cachedIndex = JsonUtility.FromJson<HandHistoryIndex>(json);
                return cachedIndex;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[{LogTag.Save}] Failed to load index — error={e.Message}");
        }

        cachedIndex = new HandHistoryIndex();
        return cachedIndex;
    }

    private async Task<SessionHandData> LoadSessionInternalAsync(string sessionId)
    {
        if (cachedSessions.TryGetValue(sessionId, out var cached)) return cached;

        if (!IsReady()) return null;

        try
        {
            string key = SessionKey(sessionId);
            var keys = new HashSet<string> { key };
            var data = await CloudSaveService.Instance.Data.Player.LoadAsync(keys);

            if (data.TryGetValue(key, out var item))
            {
                string json = item.Value.GetAsString();
                var sessionData = JsonUtility.FromJson<SessionHandData>(json);
                cachedSessions[sessionId] = sessionData;
                return sessionData;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[{LogTag.Save}] Failed to load session — session={sessionId}, error={e.Message}");
        }

        return null;
    }

    // ══════════════════════════════════════════════════════════════
    //  Hand cap enforcement
    // ══════════════════════════════════════════════════════════════

    private async Task EnforceHandCapAsync(HandHistoryIndex index)
    {
        while (index.totalHandCount > MAX_TOTAL_HANDS && index.sessions.Count > 0)
        {
            int oldestIdx = 0;
            long oldestTime = long.MaxValue;
            for (int i = 0; i < index.sessions.Count; i++)
            {
                if (index.sessions.Count == 1) break;

                if (index.sessions[i].createdAtUtcMs < oldestTime)
                {
                    oldestTime = index.sessions[i].createdAtUtcMs;
                    oldestIdx = i;
                }
            }

            if (index.sessions.Count == 1)
            {
                await TrimOldestHandsFromSession(index, index.sessions[0]);
                break;
            }

            TableSessionEntry oldest = index.sessions[oldestIdx];
            Debug.Log($"[{LogTag.Save}] Pruning oldest session — session={oldest.sessionId}, hands={oldest.handCount}, cap={MAX_TOTAL_HANDS}");

            try
            {
                await CloudSaveService.Instance.Data.Player.DeleteAsync(SessionKey(oldest.sessionId));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{LogTag.Save}] Failed to delete session key — session={oldest.sessionId}, error={e.Message}");
            }

            cachedSessions.Remove(oldest.sessionId);

            index.totalHandCount -= oldest.handCount;
            index.sessions.RemoveAt(oldestIdx);
        }
    }

    private async Task TrimOldestHandsFromSession(HandHistoryIndex index, TableSessionEntry entry)
    {
        SessionHandData sessionData = await LoadSessionInternalAsync(entry.sessionId);
        if (sessionData == null || sessionData.hands.Count == 0) return;

        int excess = index.totalHandCount - MAX_TOTAL_HANDS;
        if (excess <= 0) return;

        int toRemove = Mathf.Min(excess, sessionData.hands.Count - 1);
        if (toRemove <= 0) return;

        sessionData.hands.RemoveRange(0, toRemove);
        entry.handCount -= toRemove;
        index.totalHandCount -= toRemove;

        if (sessionData.hands.Count > 0)
        {
            entry.createdAtUtcMs = sessionData.hands[0].timestampUtcMs;
        }

        try
        {
            string json = JsonUtility.ToJson(sessionData);
            var data = new Dictionary<string, object> { { SessionKey(entry.sessionId), json } };
            await CloudSaveService.Instance.Data.Player.SaveAsync(data);
            cachedSessions[entry.sessionId] = sessionData;

            Debug.Log($"[{LogTag.Save}] Trimmed oldest hands — session={entry.sessionId}, removed={toRemove}");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[{LogTag.Save}] Failed to save trimmed session — session={entry.sessionId}, error={e.Message}");
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  Session player summary helpers
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Updates per-player netResult and handsPlayed from the completed hand.
    /// Also merges buy-in data from <paramref name="sessionMeta"/> which
    /// HandRecorder keeps up-to-date via OnPlayerBuyIn tracking.
    /// </summary>
    private void UpdateSessionPlayerSummaries(TableSessionEntry entry, HandRecord hand,
        TableSessionEntry sessionMeta)
    {
        var winnerAmounts = new Dictionary<int, float>();
        foreach (var w in hand.winners)
        {
            if (winnerAmounts.ContainsKey(w.slotIndex))
                winnerAmounts[w.slotIndex] += w.amount;
            else
                winnerAmounts[w.slotIndex] = w.amount;
        }

        foreach (var player in hand.players)
        {
            SessionPlayerSummary summary = entry.playerSummaries
                .Find(s => s.displayName == player.displayName);

            if (summary == null)
            {
                summary = new SessionPlayerSummary { displayName = player.displayName };
                entry.playerSummaries.Add(summary);
            }

            summary.handsPlayed++;

            float invested = CalculatePlayerInvestment(hand, player.slotIndex);

            if (winnerAmounts.TryGetValue(player.slotIndex, out float won))
            {
                summary.netResult += won - invested;
            }
            else
            {
                summary.netResult -= invested;
            }

            // Merge buy-in & playerId from sessionMeta (populated by HandRecorder)
            if (sessionMeta != null)
            {
                var metaSummary = sessionMeta.playerSummaries
                    .Find(s => s.displayName == player.displayName);
                if (metaSummary != null)
                {
                    summary.totalBuyIn = metaSummary.totalBuyIn;
                    if (!string.IsNullOrEmpty(metaSummary.playerId))
                        summary.playerId = metaSummary.playerId;
                }
            }
        }
    }

    private float CalculatePlayerInvestment(HandRecord hand, int slotIndex)
    {
        float total = 0f;
        bool hasSBAction = false;
        bool hasBBAction = false;

        foreach (var action in hand.actions)
        {
            if (action.playerSlotIndex != slotIndex) continue;

            switch (action.actionType)
            {
                case "Small Blind":
                    hasSBAction = true;
                    total += action.amount;
                    break;
                case "Big Blind":
                    hasBBAction = true;
                    total += action.amount;
                    break;
                case "Call":
                case "All-In (Call)":
                case "Raise":
                case "All-In (Raise)":
                case "All-In":
                    total += action.amount;
                    break;
                case "Uncalled Bet Return":
                    total -= action.amount;
                    break;
            }
        }

        // Fallback for older hand records that don't have explicit blind actions
        if (!hasSBAction && slotIndex == hand.sbSlotIndex)
            total += hand.smallBlind;
        if (!hasBBAction && slotIndex == hand.bbSlotIndex)
            total += hand.bigBlind;

        return total;
    }

    // ══════════════════════════════════════════════════════════════
    //  Cache management
    // ══════════════════════════════════════════════════════════════

    public void ClearCaches()
    {
        cachedIndex = null;
        cachedStats = null;
        cachedSessions.Clear();
    }
}
