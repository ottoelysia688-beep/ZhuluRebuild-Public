using System;
using ZhuluRebuild.Core;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Domain
{
    /// <summary>
    /// 爵位体系服务（阶段2B 包1，2026-08-15 设计定稿）：
    /// 国力点 = 城数×6 + 人口÷40；五等 邑/子/伯/侯/公；天命国独立位阶（天子）。
    /// 爵位以身份特质承载（trait_zlr_tier_*，互为 traits_to_remove 自动替换），
    /// 称号并入特质（TitleSuffixPatches 读特质显示）。
    /// </summary>
    public static class ZhuluTierService
    {
        // ── 爵位特质 id ──
        // id 无 trait_ 前缀（2026-08-15 修复：原版显示名 key = "trait_" + id，
        // 带前缀会双拼成 trait_trait_zlr_tier_gong 显示英文——现有 zlr_* 特质模式一致）
        public const string TIER_TIANZI = "zlr_tier_tianzi"; // 天子（天命国，独立位阶）
        public const string TIER_GONG = "zlr_tier_gong";     // 公
        public const string TIER_HOU = "zlr_tier_hou";       // 侯
        public const string TIER_BO = "zlr_tier_bo";         // 伯
        public const string TIER_ZI = "zlr_tier_zi";         // 子
        public const string TIER_NAN = "zlr_tier_nan";       // 邑（男爵）

        public static readonly string[] NonMandateTiers = { TIER_GONG, TIER_HOU, TIER_BO, TIER_ZI, TIER_NAN };
        public static readonly string[] AllTiers = { TIER_TIANZI, TIER_GONG, TIER_HOU, TIER_BO, TIER_ZI, TIER_NAN };

        private const string KEY_TIER_SINCE = "tier_since"; // 升等确认起始年（KingdomDataExt 自带 Prefix）

        /// <summary>当前档位阈值（降等缓冲带用：跌破阈值×60% 才降）。</summary>
        private static int TierThreshold(string tierId)
        {
            switch (tierId)
            {
                case TIER_GONG: return 55;
                case TIER_HOU: return 35;
                case TIER_BO: return 20;
                case TIER_ZI: return 10;
                default: return 0; // 邑国不降（最低档）
            }
        }

        // ── 国力点 ──
        public static int GetPowerPoints(Kingdom k)
        {
            if (k == null) return 0;
            int cities = k.cities != null ? k.cities.Count : 0;
            int pop = k.units != null ? k.units.Count : 0;
            return cities * 6 + pop / 40;
        }
        public static int GetPowerPoints(string kId)
        {
            var k = KingdomDataExt.FindKingdomById(kId);
            return GetPowerPoints(k);
        }

        // ── 爵位判定 ──
        public static string GetTierId(int powerPoints)
        {
            if (powerPoints >= 55) return TIER_GONG;
            if (powerPoints >= 35) return TIER_HOU;
            if (powerPoints >= 20) return TIER_BO;
            if (powerPoints >= 10) return TIER_ZI;
            return TIER_NAN;
        }

        public static string GetTierName(string tierId)
        {
            switch (tierId)
            {
                case TIER_GONG: return "公";
                case TIER_HOU: return "侯";
                case TIER_BO: return "伯";
                case TIER_ZI: return "子";
                case TIER_NAN: return "邑";
                default: return "天";
            }
        }

        public static string GetSuffix(string tierId)
        {
            switch (tierId)
            {
                case TIER_GONG: return "公国";
                case TIER_HOU: return "侯国";
                case TIER_BO: return "伯国";
                case TIER_ZI: return "子国";
                case TIER_NAN: return "邑国";
                default: return "朝";
            }
        }

        public static string GetKingTitle(string tierId)
        {
            switch (tierId)
            {
                case TIER_GONG: return "大公"; // 2026-08-20：公国国王后缀改「大公」（用户拍板）
                case TIER_HOU: return "侯";    // 2026-08-20：侯国国王后缀改「侯」（用户拍板）
                case TIER_BO: return "伯君";
                case TIER_ZI: return "子君";
                case TIER_NAN: return "邑君";
                default: return "皇帝";
            }
        }

        /// <summary>读君主当前爵位特质（无 = null）。</summary>
        public static string GetActorTier(Actor king)
        {
            if (king == null) return null;
            foreach (var t in AllTiers)
            {
                try { if (king.hasTrait(t)) return t; } catch { }
            }
            return null;
        }

        /// <summary>
        /// 国家当前爵位（2026-08-18 显示口径统一）：君主爵位特质优先（权威——EnsureTierTrait
        /// 年度同步，含 10 年确认期），特质缺失（新君即位首年/特质未同步）回退实时国力点判定。
        /// 全部显示入口（国名后缀/国势称号/储君称号）统一走此口，消除"公国国王称伯君"多口径错位。
        /// 天子特质不占五等：天命国由调用方特判；此处返回按国力点的五等归位值。
        /// </summary>
        public static string GetKingdomTierId(Kingdom k)
        {
            if (k == null) return TIER_NAN;
            string tier = GetActorTier(k.king);
            if (tier != null && tier != TIER_TIANZI) return tier;
            return GetTierId(GetPowerPoints(k));
        }

        /// <summary>仅同步身份特质，不触发晋降播报、史册或稳定度变化；用于跨体系转制。</summary>
        public static void ForceTierTraitForCurrentStatus(Kingdom k)
        {
            if (k == null || !k.isAlive() || k.king == null || !k.king.isAlive()) return;
            if (KingdomCivilizationService.Instance.IsNomadic(k))
            {
                NomadicTierService.ForceTierTraitForCurrentStatus(k);
                return;
            }
            string target = OrthodoxMandateManager.Instance.IsMandateHolder(k.id.ToString())
                ? TIER_TIANZI : GetTierId(GetPowerPoints(k));
            if (!k.king.hasTrait(target)) k.king.addTrait(target);
            KingdomDataExt.SetLong(k, KEY_TIER_SINCE, 0);
        }

        /// <summary>
        /// 爵位等阶描述（2026-08-18 爵位等阶卡片文案——先秦五等爵语境，替代旧"据X城之地"失实文本）。
        /// </summary>
        public static string GetTierDesc(string tierId)
        {
            switch (tierId)
            {
                case TIER_TIANZI: return "受天命而王，居中国而抚四夷";
                case TIER_GONG: return "位尊三公，礼绝百僚，为一方霸长";
                case TIER_HOU: return "藩屏王室，守土牧民，拱卫天子";
                case TIER_BO: return "为一方之长，绥抚乡邑，威加一部";
                case TIER_ZI: return "国小民寡，筚路蓝缕，以启山林";
                default: return "一邑之主，躬亲政事，抚绥乡里";
            }
        }

        private static int TierOrder(string tierId)
        {
            switch (tierId)
            {
                case TIER_TIANZI: return 6;
                case TIER_GONG: return 5;
                case TIER_HOU: return 4;
                case TIER_BO: return 3;
                case TIER_ZI: return 2;
                case TIER_NAN: return 1;
                default: return 0;
            }
        }

        /// <summary>
        /// 年度检查（YearDriver 按国调用）：国力点 → 爵位特质同步。
        /// 变化时 addTrait（traits_to_remove 互斥链自动移除旧爵位）+ 世界播报 + 史册 + 稳定度（升+3/降-5）。
        /// </summary>
        public static void EnsureTierTrait(Kingdom k, int year)
        {
            try
            {
                if (k == null || !k.isAlive()) return;
                if (KingdomCivilizationService.Instance.IsNomadic(k))
                {
                    NomadicTierService.EnsureTierTrait(k, year);
                    return;
                }
                if (k.king == null || !k.king.isAlive()) return;
                string kId = k.id.ToString();
                string kName = k.name ?? "本国";

                // 天命国：天子特质（不占五等；失天命后由下方按国力点归位）
                if (OrthodoxMandateManager.Instance.IsMandateHolder(kId))
                {
                    if (!k.king.hasTrait(TIER_TIANZI))
                    {
                        k.king.addTrait(TIER_TIANZI);
                        ZhuluContext.Instance.LogInfo($"[ZhuluTier] {kName} 受命为天子");
                    }
                    return;
                }

                int pp = GetPowerPoints(k);
                string target = GetTierId(pp);
                string current = GetActorTier(k.king);

                // ═══ 阶段2B 升降等规则（2026-08-15 自检发现：原实现边界国力频繁跳变）═══
                // 升等：达标持续 10 年（KEY_TIER_SINCE 记录首次达标年）；降等：跌破当前档阈值×60%（缓冲带）
                if (current != null && current != TIER_TIANZI)
                {
                    bool currentOk = TierOrder(target) == TierOrder(current);
                    if (currentOk)
                    {
                        // 当前档位正确 → 重置达标计时
                        KingdomDataExt.SetLong(k, KEY_TIER_SINCE, 0);
                        return;
                    }
                    if (TierOrder(target) > TierOrder(current))
                    {
                        // 升等：需达标持续 10 年
                        long since = KingdomDataExt.GetLong(k, KEY_TIER_SINCE, 0);
                        if (since == 0)
                        {
                            KingdomDataExt.SetLong(k, KEY_TIER_SINCE, year);
                            ZhuluContext.Instance.LogInfo($"[ZhuluTier] {kName} 国力达标（{pp}→{target}），确认期开始（10 年）");
                            return;
                        }
                        if (year - (int)since < 10) return;
                        KingdomDataExt.SetLong(k, KEY_TIER_SINCE, 0);
                    }
                    else
                    {
                        // 降等：跌破当前档阈值×60% 才降（缓冲带防边界抖动）
                        int currentThreshold = TierThreshold(current);
                        if (currentThreshold > 0 && pp >= (int)(currentThreshold * 0.6f)) return;
                        KingdomDataExt.SetLong(k, KEY_TIER_SINCE, 0);
                    }
                }

                if (current == target) return;
                // 保护：天命标记未恢复时（读档瞬间）不降级天子
                if (current == TIER_TIANZI) return;

                // 旧档兼容（2026-08-15）：清理带 trait_ 前缀的旧爵位特质（id 重构前的孤儿）
                string[] legacyIds = { "trait_zlr_tier_tianzi", "trait_zlr_tier_gong", "trait_zlr_tier_hou",
                    "trait_zlr_tier_bo", "trait_zlr_tier_zi", "trait_zlr_tier_nan" };
                foreach (var lid in legacyIds)
                {
                    try { if (k.king.hasTrait(lid)) k.king.removeTrait(lid); } catch { }
                }

                bool up = TierOrder(target) > TierOrder(current);
                k.king.addTrait(target); // traits_to_remove 链自动移除旧爵位特质
                string tierName = GetTierName(target);

                // 最低等级（邑国）不播报世界弹窗（用户反馈）；史册保留
                if (target != TIER_NAN)
                {
                    try { ZhuluWorldTip.Show(k, $"【{kName}】国势{(up ? "日隆" : "衰落")}，{(up ? "晋" : "降")}为{tierName}国！", 6f, up ? UI.ZhuluUIStyle.Hex.Warn : UI.ZhuluUIStyle.Hex.Muted); } catch { }
                }
                ChronicleManager.Instance.AddEntry(year, ChronicleEventType.TierChange, kId, kName, tierName,
                    $"<color={UI.ZhuluUIStyle.Hex.Azure}>{kName}</color> {(up ? "晋升" : "降为")}【{tierName}】。", true);
                // 阶段3：个人史册（君临类——君主身份变迁全程可溯）
                PersonHistoryManager.Instance.RecordEvent(k.king.getID(), PersonHistoryManager.CAT_JUNLIN,
                    $"国势{(up ? "日隆" : "衰落")}，{(up ? "晋" : "降")}为{tierName}国", year);
                try { StabilityManager.Instance.AddExternalBonus(kId, up ? 3f : -5f); } catch { }

                ZhuluContext.Instance.LogInfo($"[ZhuluTier] {kName} 爵位变化: {current ?? "无"} → {target} (国力点 {pp}, {(up ? "晋升" : "降级")})");
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[ZhuluTier] EnsureTierTrait 异常: {ex.Message}");
            }
        }
    }
}
