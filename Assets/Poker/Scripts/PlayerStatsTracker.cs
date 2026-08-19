using System.Collections.Generic;

// ═══════════════════════════════════════════════════════════════════
//  Player Statistics Tracker
//
//  Pure C# utility — no MonoBehaviour.  Given a completed HandRecord
//  and the local player's slot index, updates a PlayerStats object
//  with VPIP, PFR, aggression, showdown, and profit data.
// ═══════════════════════════════════════════════════════════════════

public static class PlayerStatsTracker
{
    /// <summary>
    /// Updates <paramref name="stats"/> in-place using the data from the
    /// completed <paramref name="hand"/> for the player sitting at
    /// <paramref name="localSlotIndex"/>.
    /// </summary>
    public static void UpdateStats(PlayerStats stats, HandRecord hand, int localSlotIndex)
    {
        // Verify the local player actually participated in this hand
        bool participated = false;
        foreach (var p in hand.players)
        {
            if (p.slotIndex == localSlotIndex)
            {
                participated = true;
                break;
            }
        }
        if (!participated) return;

        stats.totalHands++;

        // ── Collect fold info for all players (needed for showdown) ──
        var foldedSlots = new HashSet<int>();
        bool localPlayerFolded = false;

        // ── Analyse preflop actions ─────────────────────────────────
        bool voluntarilyPutMoney = false;
        bool raisedPreflop = false;

        foreach (var action in hand.actions)
        {
            // Track folds for showdown detection
            if (IsFoldAction(action.actionType))
            {
                foldedSlots.Add(action.playerSlotIndex);
                if (action.playerSlotIndex == localSlotIndex)
                    localPlayerFolded = true;
            }

            if (action.playerSlotIndex != localSlotIndex) continue;

            // Only preflop actions for VPIP / PFR
            if (action.phase == 0) // GamePhase.PreFlop
            {
                switch (action.actionType)
                {
                    case "Call":
                    case "All-In (Call)":
                        voluntarilyPutMoney = true;
                        break;
                    case "Raise":
                    case "All-In (Raise)":
                        voluntarilyPutMoney = true;
                        raisedPreflop = true;
                        break;
                }
            }

            // ── Aggression tracking (all streets) ───────────────────
            switch (action.actionType)
            {
                case "Raise":
                case "All-In (Raise)":
                    stats.aggressionBets++;
                    break;
                case "Call":
                case "All-In (Call)":
                    stats.aggressionCalls++;
                    break;
            }
        }

        if (voluntarilyPutMoney) stats.vpipHands++;
        if (raisedPreflop) stats.pfrHands++;

        // ── Showdown tracking ───────────────────────────────────────
        // Showdown = 5 community cards dealt + 2+ players still active
        int dealtCards = 0;
        if (hand.communityCardRanks != null)
        {
            for (int i = 0; i < hand.communityCardRanks.Count; i++)
            {
                if (hand.communityCardRanks[i] != 0) dealtCards++;
            }
        }

        if (dealtCards == 5 && !localPlayerFolded)
        {
            int activePlayers = hand.players.Count - foldedSlots.Count;
            if (activePlayers >= 2)
            {
                stats.showdownsReached++;

                // Check if the local player won at showdown
                foreach (var w in hand.winners)
                {
                    if (w.slotIndex == localSlotIndex)
                    {
                        stats.showdownsWon++;
                        break;
                    }
                }
            }
        }

        // ── Profit tracking ─────────────────────────────────────────
        float invested = CalculatePlayerInvestment(hand, localSlotIndex);
        float won = 0f;

        foreach (var winner in hand.winners)
        {
            if (winner.slotIndex == localSlotIndex)
            {
                won += winner.amount;
            }
        }

        float handProfit = won - invested;
        stats.netProfit += handProfit;

        // ── Win tracking ────────────────────────────────────────────
        if (won > 0f)
        {
            stats.handsWon++;

            if (won > stats.biggestPotWon)
                stats.biggestPotWon = won;
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════════════════════════════

    private static bool IsFoldAction(string actionType)
    {
        return actionType == "Fold"
            || actionType == "Fold (Timeout)"
            || actionType == "Disconnected";
    }

    public static float CalculatePlayerInvestment(HandRecord hand, int slotIndex)
    {
        float total = 0f;
        bool hasSBAction = false;
        bool hasBBAction = false;

        // Actions (including blind actions that carry the actual amount posted)
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
        if (!hasSBAction && slotIndex == hand.sbSlotIndex) total += hand.smallBlind;
        if (!hasBBAction && slotIndex == hand.bbSlotIndex) total += hand.bigBlind;

        return total;
    }
}
