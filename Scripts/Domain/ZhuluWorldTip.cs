using System;
using UnityEngine;

namespace ZhuluRebuild.Domain
{
    /// <summary>
    /// 阶段3H-13：世界级播报门槛——减少低等级国家弹窗刷屏（用户卡顿反馈）。
    /// 重大事件（天命易主/灭国/改朝/政变/大一统）始终播报；
    /// 常规事件（爵位升降/朝贡/和约/夺职/祭天/赔款等）仅当当事国达到"可播报档"才弹窗，
    /// 未达标只入史册（ChronicleManager 照记——信息不丢，只是不打断玩家）。
    /// 判定口径与档位服务同源：东方按爵位（侯及以上可播），游牧按草原档位（部盟及以上可播）。
    /// </summary>
    public static class ZhuluWorldTip
    {
        /// <summary>常规事件：当事国是否够格弹世界播报（侯/部盟及以上；天命国恒可播）。</summary>
        public static bool CanBroadcast(Kingdom k)
        {
            try
            {
                if (k == null || !k.isAlive()) return false;
                if (PolicyManager.IsMandateHolderForKingdom(k)) return true;
                bool nomad = KingdomCivilizationService.Instance.IsNomadic(k);
                string tierId = nomad ? NomadicTierService.GetKingdomTierId(k) : ZhuluTierService.GetKingdomTierId(k);
                if (nomad)
                    return tierId == NomadicTierService.TIER_WANGTING || tierId == NomadicTierService.TIER_DAHANTING
                        || tierId == NomadicTierService.TIER_HANGUO || tierId == NomadicTierService.TIER_BUMENG;
                return tierId == ZhuluTierService.TIER_TIANZI || tierId == ZhuluTierService.TIER_GONG
                    || tierId == ZhuluTierService.TIER_HOU;
            }
            catch { return false; }
        }

        /// <summary>常规事件播报（不够格自动静默）。duration&lt;0 用默认 5 秒。</summary>
        public static void Show(Kingdom k, string text, float duration = 5f, string colorHex = null)
        {
            try
            {
                if (!CanBroadcast(k)) return;
                if (string.IsNullOrEmpty(text)) return;
                if (duration < 0f) duration = 5f;
                if (!string.IsNullOrEmpty(colorHex)) WorldTip.showNow(text, false, "top", duration, colorHex);
                else WorldTip.showNow(text, false, "top", duration);
            }
            catch { }
        }

        /// <summary>双方事件：任一方够格即播（战争/和约以两国为当事方）。2026-10-06 顶部弹窗事件分色。</summary>
        public static void ShowPair(Kingdom a, Kingdom b, string text, float duration = 5f, string colorHex = null)
        {
            try
            {
                if (CanBroadcast(a) || CanBroadcast(b)) Show(a, text, duration, colorHex);
            }
            catch { }
        }
    }
}
