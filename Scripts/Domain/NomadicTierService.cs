using System;
using ZhuluRebuild.Core;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Domain
{
    /// <summary>游牧六级体制。国力公式、阈值、十年晋级确认和 60% 降级缓冲与东方体系完全相同。</summary>
    public static class NomadicTierService
    {
        public const string TIER_WANGTING = "zlr_nomad_tier_wangting";
        public const string TIER_DAHANTING = "zlr_nomad_tier_dahanting";
        public const string TIER_HANGUO = "zlr_nomad_tier_hanguo";
        public const string TIER_BUMENG = "zlr_nomad_tier_bumeng";
        public const string TIER_DABU = "zlr_nomad_tier_dabu";
        public const string TIER_BUZU = "zlr_nomad_tier_buzu";

        public static readonly string[] NonMandateTiers = { TIER_DAHANTING, TIER_HANGUO, TIER_BUMENG, TIER_DABU, TIER_BUZU };
        public static readonly string[] AllTiers = { TIER_WANGTING, TIER_DAHANTING, TIER_HANGUO, TIER_BUMENG, TIER_DABU, TIER_BUZU };
        private const string KEY_TIER_SINCE = "nomad_tier_since";

        public static string GetTierId(int powerPoints)
        {
            if (powerPoints >= 55) return TIER_DAHANTING;
            if (powerPoints >= 35) return TIER_HANGUO;
            if (powerPoints >= 20) return TIER_BUMENG;
            if (powerPoints >= 10) return TIER_DABU;
            return TIER_BUZU;
        }

        public static string GetTierName(string tierId)
        {
            switch (tierId)
            {
                case TIER_WANGTING: return "王庭";
                case TIER_DAHANTING: return "大汗庭";
                case TIER_HANGUO: return "汗国";
                case TIER_BUMENG: return "部盟";
                case TIER_DABU: return "大部";
                default: return "部族";
            }
        }

        public static string GetKingTitle(string tierId)
        {
            switch (tierId)
            {
                case TIER_WANGTING: return "天可汗";
                case TIER_DAHANTING: return "大汗";
                case TIER_HANGUO: return "可汗";
                case TIER_BUMENG: return "盟主";
                case TIER_DABU: return "大首领";
                default: return "首领";
            }
        }

        public static string GetSuffix(string tierId) => GetTierName(tierId);

        public static string GetActorTier(Actor king)
        {
            if (king == null) return null;
            foreach (string t in AllTiers)
                try { if (king.hasTrait(t)) return t; } catch { }
            return null;
        }

        public static string GetKingdomTierId(Kingdom k)
        {
            if (k == null) return TIER_BUZU;
            string current = GetActorTier(k.king);
            if (current != null && current != TIER_WANGTING) return current;
            return GetTierId(ZhuluTierService.GetPowerPoints(k));
        }

        /// <summary>仅同步身份特质，不触发晋降播报、史册或稳定度变化；用于授眷、失眷和跨体系转制。</summary>
        public static void ForceTierTraitForCurrentStatus(Kingdom k)
        {
            if (k == null || !k.isAlive() || k.king == null || !k.king.isAlive()) return;
            string target = NomadicMandateManager.Instance.IsMandateHolder(k.id.ToString())
                ? TIER_WANGTING : GetTierId(ZhuluTierService.GetPowerPoints(k));
            if (!k.king.hasTrait(target)) k.king.addTrait(target);
            KingdomDataExt.SetLong(k, KEY_TIER_SINCE, 0);
        }

        private static int Order(string tierId)
        {
            if (tierId == TIER_WANGTING) return 6;
            if (tierId == TIER_DAHANTING) return 5;
            if (tierId == TIER_HANGUO) return 4;
            if (tierId == TIER_BUMENG) return 3;
            if (tierId == TIER_DABU) return 2;
            if (tierId == TIER_BUZU) return 1;
            return 0;
        }

        private static int Threshold(string tierId)
        {
            if (tierId == TIER_DAHANTING) return 55;
            if (tierId == TIER_HANGUO) return 35;
            if (tierId == TIER_BUMENG) return 20;
            if (tierId == TIER_DABU) return 10;
            return 0;
        }

        public static void EnsureTierTrait(Kingdom k, int year)
        {
            try
            {
                if (k == null || !k.isAlive() || k.king == null || !k.king.isAlive()) return;
                string kId = k.id.ToString();
                if (NomadicMandateManager.Instance.IsMandateHolder(kId))
                {
                    if (!k.king.hasTrait(TIER_WANGTING)) k.king.addTrait(TIER_WANGTING);
                    return;
                }

                int pp = ZhuluTierService.GetPowerPoints(k);
                string target = GetTierId(pp);
                string current = GetActorTier(k.king);
                if (current != null && current != TIER_WANGTING)
                {
                    if (Order(target) == Order(current))
                    {
                        KingdomDataExt.SetLong(k, KEY_TIER_SINCE, 0);
                        return;
                    }
                    if (Order(target) > Order(current))
                    {
                        long since = KingdomDataExt.GetLong(k, KEY_TIER_SINCE, 0);
                        if (since == 0) { KingdomDataExt.SetLong(k, KEY_TIER_SINCE, year); return; }
                        if (year - since < 10) return;
                    }
                    else
                    {
                        int threshold = Threshold(current);
                        if (threshold > 0 && pp >= (int)(threshold * 0.6f)) return;
                    }
                    KingdomDataExt.SetLong(k, KEY_TIER_SINCE, 0);
                }

                if (current == target) return;
                // 王庭只属于长生天眷持有国；失眷后必须立即落到当前国力对应等级。
                bool up = current != TIER_WANGTING && Order(target) > Order(current);
                k.king.addTrait(target);
                string tierName = GetTierName(target);
                if (target != TIER_BUZU)
                    try { ZhuluWorldTip.Show(k, $"【{k.name}】势力{(up ? "壮大" : "衰落")}，{(up ? "晋为" : "降为")}{tierName}！", 6f, up ? UI.ZhuluUIStyle.Hex.Warn : UI.ZhuluUIStyle.Hex.Muted); } catch { }
                ChronicleManager.Instance.AddEntry(year, ChronicleEventType.TierChange, kId, k.name ?? "", tierName,
                    $"<color=#c59a60>{k.name}</color> {(up ? "晋为" : "降为")}【{tierName}】。", true);
                PersonHistoryManager.Instance.RecordEvent(k.king.getID(), PersonHistoryManager.CAT_JUNLIN,
                    $"势力{(up ? "壮大" : "衰落")}，{(up ? "晋为" : "降为")}{tierName}", year);
                try { StabilityManager.Instance.AddExternalBonus(kId, up ? 3f : -5f); } catch { }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[NomadicTier] 同步异常: {ex.Message}");
            }
        }
    }
}
