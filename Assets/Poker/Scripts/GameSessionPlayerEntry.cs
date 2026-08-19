using TMPro;
using UnityEngine;
using UnityEngine.UI;

// ═══════════════════════════════════════════════════════════════════
//  Game Session Player Entry
//
//  Attached to a prefab instantiated inside the GameSessionDetailPanel
//  scroll view.  Displays a single player's summary for a table
//  session: avatar, display name, and net profit/loss.
// ═══════════════════════════════════════════════════════════════════

public class GameSessionPlayerEntry : MonoBehaviour
{
    [Header("UI References")]
    [SerializeField] private Image avatarImage;
    [SerializeField] private TextMeshProUGUI playerNameText;
    [SerializeField] private TextMeshProUGUI netProfitText;
    [SerializeField] private TextMeshProUGUI handsPlayedText;
    [SerializeField] private TextMeshProUGUI buyInText;

    /// <summary>
    /// Populates the entry from a <see cref="SessionPlayerSummary"/>.
    /// </summary>
    public void Populate(SessionPlayerSummary summary)
    {
        if (summary == null) return;

        // ── Avatar ──────────────────────────────────────────────
        if (avatarImage != null)
        {
            Sprite sprite = null;
            if (PlayerAssets.Instance != null)
                sprite = PlayerAssets.Instance.GetSprite(summary.characterId);

            if (sprite != null)
            {
                avatarImage.sprite = sprite;
                avatarImage.enabled = true;
            }
            else
            {
                avatarImage.enabled = false;
            }
        }

        // ── Display name ────────────────────────────────────────
        if (playerNameText != null)
            playerNameText.text = string.IsNullOrEmpty(summary.displayName)
                ? "Unknown"
                : summary.displayName;

        // ── Net profit / loss ───────────────────────────────────
        if (netProfitText != null)
        {
            float net = summary.netResult;
            string sign = net >= 0 ? "+" : "";
            netProfitText.text = $"{sign}{net:F2}";
            netProfitText.color = net > 0f ? Color.green
                                : net < 0f ? Color.red
                                : Color.white;
        }

        // ── Hands played (optional) ────────────────────────────
        if (handsPlayedText != null)
            handsPlayedText.text = $"{summary.handsPlayed} hands";

        // ── Total buy-in (optional) ─────────────────────────────
        if (buyInText != null)
            buyInText.text = $"Buy-in: {summary.totalBuyIn:F2}";
    }
}
