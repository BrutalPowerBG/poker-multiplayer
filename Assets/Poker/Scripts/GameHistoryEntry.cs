using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// ═══════════════════════════════════════════════════════════════════
//  Game History Entry
//
//  Attached to a prefab instantiated in the lobby's game-history
//  scroll view.  Displays a summary of a single table session:
//  table name, net profit/loss, player count, blinds, and dates.
//
//  Clicking the entry opens the GameSessionDetailPanel with full
//  player-by-player breakdown for the session.
// ═══════════════════════════════════════════════════════════════════

public class GameHistoryEntry : MonoBehaviour
{
    [Header("UI References")]
    [SerializeField] private TextMeshProUGUI tableNameText;
    [SerializeField] private TextMeshProUGUI netProfitText;
    [SerializeField] private TextMeshProUGUI playerCountText;
    [SerializeField] private TextMeshProUGUI blindsText;
    [SerializeField] private TextMeshProUGUI startDateText;
    [SerializeField] private TextMeshProUGUI endDateText;

    // ── Internal state ────────────────────────────────────────────
    private TableSessionEntry cachedSession;
    private GameSessionDetailPanel detailPanel;

    // ══════════════════════════════════════════════════════════════
    //  Initialisation
    // ══════════════════════════════════════════════════════════════

    private void Awake()
    {
        // Wire up the Button component on this GameObject (or add one).
        Button btn = GetComponent<Button>();
        if (btn == null)
            btn = gameObject.AddComponent<Button>();

        btn.onClick.AddListener(OnClicked);
    }

    /// <summary>
    /// Assigns the detail panel that should be opened on click.
    /// Called by <see cref="GameHistoryPanel"/> during population.
    /// </summary>
    public void SetDetailPanel(GameSessionDetailPanel panel)
    {
        detailPanel = panel;
    }

    // ══════════════════════════════════════════════════════════════
    //  Population
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Populates the entry from a <see cref="TableSessionEntry"/> and the
    /// local player's <see cref="SessionPlayerSummary"/> (may be null if
    /// the player data isn't embedded in the session).
    /// </summary>
    public void Populate(TableSessionEntry session, SessionPlayerSummary localPlayer)
    {
        if (session == null) return;

        cachedSession = session;

        // Table name
        if (tableNameText != null)
            tableNameText.text = string.IsNullOrEmpty(session.tableName) ? "Unknown Table" : session.tableName;

        // Net profit / loss
        if (netProfitText != null)
        {
            float net = localPlayer != null ? localPlayer.netResult : 0f;
            string sign = net >= 0 ? "+" : "";
            netProfitText.text = $"{sign}{net:F2}";
            netProfitText.color = net > 0f ? Color.green
                                : net < 0f ? Color.red
                                : Color.white;
        }

        // Player count
        if (playerCountText != null)
        {
            int count = session.playerCount > 0
                ? session.playerCount
                : (session.playerIds != null && session.playerIds.Count > 0)
                    ? session.playerIds.Count
                    : (session.playerSummaries != null ? session.playerSummaries.Count : 0);
            playerCountText.text = $"{count} player{(count != 1 ? "s" : "")}";
        }

        // Blinds
        if (blindsText != null)
            blindsText.text = $"{session.smallBlind:F2} / {session.bigBlind:F2}";

        // Start date / time
        if (startDateText != null)
            startDateText.text = MsToLocalString(session.createdAtUtcMs);

        // End date / time
        if (endDateText != null)
            endDateText.text = MsToLocalString(session.lastActivityUtcMs);
    }

    // ══════════════════════════════════════════════════════════════
    //  Click handler
    // ══════════════════════════════════════════════════════════════

    private void OnClicked()
    {
        if (cachedSession == null) return;

        if (detailPanel != null)
        {
            detailPanel.Show(cachedSession);
        }
        else
        {
            Debug.LogWarning($"[{LogTag.History}] Detail panel click ignored — reason=no detail panel assigned", this);
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Helpers
    // ════════════════════════════════════════════════════════════════

    private static string MsToLocalString(long utcMs)
    {
        if (utcMs <= 0) return "—";

        DateTime utc = DateTimeOffset.FromUnixTimeMilliseconds(utcMs).UtcDateTime;
        DateTime local = utc.ToLocalTime();
        return local.ToString("dd MMM yyyy  HH:mm");
    }
}
