using System;
using ZhuluRebuild.Core;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Domain
{
    /// <summary>
    /// 礼治服务（阶段2B 包2 天命重构，2026-08-15 设计定稿）：
    /// 礼治分 = 礼制政策（诸侯礼 宗庙血食/明堂修礼 +2/项；天子礼 祭天/封禅/正朔 +2/项）
    ///        + 祭祀累积分（祭祖 +3、祭天 +8，永久累积上限 30）
    ///        + 君主德行（仁君 +3 / 开国之主 +2 / 天纵奇才 +1 / 暴君 -5 / 昏君 -3）
    ///        - 失道扣减（每年：在战 -1、连亏 -1）
    /// 用途：受命资格（礼治≥5）+ 天命维持（礼治<0 → 礼崩乐坏天命-2/年）+ 篡位重置
    /// </summary>
    public static class ZhuluLiZhiService
    {
        // 注意：KingdomDataExt 自带 Prefix="zlr_"，key 不带前缀（2026-08-15 双前缀 bug 修复）
        private const string KEY_LI_ZHI = "li_zhi_points";      // 祭祀累积分
        private const string KEY_LAST_SACRIFICE = "last_sacrifice"; // 最近祭祀年（10 年冷却）
        private const int SACRIFICE_COOLDOWN = 10;
        private const int MAX_ACCUM = 30;

        // ── 礼制政策 id ──
        public const string POLICY_ZONG_MIAO = "zlr_zongmiao";   // 宗庙血食（诸侯礼）
        public const string POLICY_MING_TANG = "zlr_mingtang";   // 明堂修礼（诸侯礼）
        public const string POLICY_JI_TIAN = "mand_jitian";      // 祭天大典（天子礼）
        public const string POLICY_FENG_SHAN = "mand_fengshan";  // 封禅泰山（天子礼）
        public const string POLICY_ZHENG_SHUO = "mand_zhengshuo";// 颁布正朔（天子礼）

        /// <summary>礼治分（总）。</summary>
        public static int GetLiZhiScore(string kingdomId)
        {
            try
            {
                int score = 0;
                // 1. 礼制政策（2026-08-18 字段化：按 PolicyDefinition.LiZhiBonus 求和，
                //    替代硬编码 ID 白名单——新增邑/子/侯/公礼治政策自动纳入）
                var active = PolicyManager.Instance.GetActivePolicies(kingdomId);
                if (active != null)
                {
                    foreach (var p in active)
                    {
                        if (p == null) continue;
                        var def = PolicyManager.Instance.GetDefinition(p.PolicyId);
                        if (def != null && def.LiZhiBonus != 0) score += def.LiZhiBonus;
                    }
                }
                // 2. 祭祀累积分
                Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
                if (k != null)
                {
                    score += (int)KingdomDataExt.GetLong(k, KEY_LI_ZHI, 0);
                }
                // 3. 君主德行
                if (k?.king != null && k.king.isAlive())
                {
                    try
                    {
                        if (k.king.hasTrait(TraitManager.TRAIT_BENEVOLENT)) score += 3;
                        if (k.king.hasTrait(TraitManager.TRAIT_FOUNDER)) score += 2;
                        if (k.king.hasTrait(TraitManager.TRAIT_GENIUS)) score += 1;
                        if (k.king.hasTrait(TraitManager.TRAIT_TYRANT)) score -= 5;
                        if (k.king.hasTrait(TraitManager.TRAIT_FOOLISH)) score -= 3;
                    }
                    catch { }
                }
                return score;
            }
            catch { return 0; }
        }

        /// <summary>失道扣减后净礼治（用于受命/维持判定）：净 = 总 − 在战 − 连亏。</summary>
        public static int GetNetLiZhi(string kingdomId)
        {
            int score = GetLiZhiScore(kingdomId);
            try
            {
                Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
                if (k != null)
                {
                    bool atWar = false;
                    try { var wars = k.getWars(); if (wars != null) { foreach (var _ in wars) { atWar = true; break; } } } catch { }
                    if (atWar) score -= 1;
                }
                var tre = TreasuryManager.Instance.GetReport(kingdomId);
                if (tre != null && tre.DeficitYears >= 1) score -= 1;
            }
            catch { }
            return score;
        }

        /// <summary>执行祭祀（年度自动决策）：礼治不足时祭祖（非天命）/祭天（天命国）；10 年冷却。</summary>
        public static void TickSacrifice(string kingdomId, Kingdom k, int year)
        {
            try
            {
                if (k == null || !k.isAlive()) return;
                bool isMandate = PolicyManager.IsMandateHolderForKingdom(k);

                // 冷却检查
                long lastSac = KingdomDataExt.GetLong(k, KEY_LAST_SACRIFICE, 0);
                if (lastSac > 0 && year - (int)lastSac < SACRIFICE_COOLDOWN) return;

                // 礼治已够则不祭祀（受命后维持——祭祀用于补充）
                int net = GetNetLiZhi(kingdomId);
                if (net >= 8) return;

                // 国库检查（2026-08-15 调参：祭祖需国力≥10 且国库≥50——小国先发展；祭天需国力≥40 且国库≥150）
                long treasury = 0;
                try { treasury = TreasuryManager.Instance.GetReport(kingdomId).Treasury; } catch { }
                int pp = ZhuluTierService.GetPowerPoints(k);
                if (isMandate)
                {
                    if (pp < 40 || treasury < 150) return;
                    KingdomDataExt.SetLong(k, "tre", treasury - 150);
                    AddSacrificePoints(k, 8);
                    try { ZhuluWorldTip.Show(k, $"【{k.name}】举国祭天，礼治+8！", 5f, UI.ZhuluUIStyle.Hex.Positive); } catch { }
                    ZhuluContext.Instance.LogInfo($"[LiZhi] {k.name} 祭天大典（礼治+8，耗 150 金）");
                }
                else
                {
                    // 2026-08-15 调参：祭祖需国力≥10 且国库≥50（小国先发展，祭祀有仪式感）
                    if (pp < 10 || treasury < 50) return;
                    KingdomDataExt.SetLong(k, "tre", treasury - 50);
                    AddSacrificePoints(k, 3);
                    ZhuluContext.Instance.LogInfo($"[LiZhi] {k.name} 祭祖（礼治+3，耗 50 金）");
                }
                KingdomDataExt.SetLong(k, KEY_LAST_SACRIFICE, year);
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[LiZhi] TickSacrifice 异常: {ex.Message}");
            }
        }

        private static void AddSacrificePoints(Kingdom k, int points)
        {
            long cur = KingdomDataExt.GetLong(k, KEY_LI_ZHI, 0);
            KingdomDataExt.SetLong(k, KEY_LI_ZHI, Math.Min(MAX_ACCUM, cur + points));
        }

        /// <summary>篡位礼治重置（新朝从零积累礼治，德政重建合法性）。</summary>
        public static void ResetOnUsurpation(string kingdomId)
        {
            try
            {
                Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
                if (k == null) return;
                long old = KingdomDataExt.GetLong(k, KEY_LI_ZHI, 0);
                KingdomDataExt.SetLong(k, KEY_LI_ZHI, 0);
                KingdomDataExt.SetLong(k, KEY_LAST_SACRIFICE, 0);
                ZhuluContext.Instance.LogInfo($"[LiZhi] {k.name} 篡位改朝，礼治重置（原 {old}）");
            }
            catch { }
        }
    }
}
