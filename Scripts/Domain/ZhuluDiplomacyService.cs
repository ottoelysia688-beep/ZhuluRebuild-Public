using System;
using ZhuluRebuild.Core;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Domain
{
    /// <summary>
    /// 邦交服务（阶段2C 简化版，2026-08-16 用户定案）：
    /// ① 和平条约/停战期（战争结束后 5 年禁互相宣战）
    /// ② 互不侵犯（好感高国家间协议 5 年互不宣战）
    /// ③ 附庸朝贡金币化（附庸每年向宗主贡金 = 附庸人口÷2）
    /// 全部挂现有管线（DeclareWar 检查 + EndWarPostfix + TickTribute），零新系统。
    /// </summary>
    public static class ZhuluDiplomacyService
    {
        public const int TRUCE_YEARS = 5;      // 停战期
        public const int NAP_YEARS = 5;        // 互不侵犯期
        private const string KEY_TRUCE = "truce_until_";     // + 对方id → 停战到期年
        private const string KEY_NAP = "nap_until_";         // + 对方id → 互不侵犯到期年
        private const float NAP_AI_CHANCE = 0.03f;           // AI 年度发起概率
        private const int NAP_MIN_OPINION = 50;              // 好感门槛

        // ═══════════════ ① 停战期 ═══════════════

        public static void SetTruce(string kingdomId, string targetId, int untilYear)
        {
            try
            {
                Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
                if (k == null) return;
                KingdomDataExt.SetLong(k, KEY_TRUCE + targetId, untilYear);
            }
            catch { }
        }

        public static bool IsTruceActive(string kingdomId, string targetId, int year)
        {
            try
            {
                Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
                if (k == null) return false;
                long until = KingdomDataExt.GetLong(k, KEY_TRUCE + targetId, 0);
                return until > 0 && year < (int)until;
            }
            catch { return false; }
        }

        // ═══════════════ ② 互不侵犯 ═══════════════

        public static void SetNonAggression(string kingdomId, string targetId, int untilYear)
        {
            try
            {
                Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
                if (k == null) return;
                KingdomDataExt.SetLong(k, KEY_NAP + targetId, untilYear);
            }
            catch { }
        }

        public static bool IsNonAggressionActive(string kingdomId, string targetId, int year)
        {
            try
            {
                Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
                if (k == null) return false;
                long until = KingdomDataExt.GetLong(k, KEY_NAP + targetId, 0);
                return until > 0 && year < (int)until;
            }
            catch { return false; }
        }

        /// <summary>是否禁止对目标宣战（停战期 或 互不侵犯期）。</summary>
        public static bool IsWarForbidden(string kingdomId, string targetId, int year)
        {
            return IsTruceActive(kingdomId, targetId, year) || IsNonAggressionActive(kingdomId, targetId, year);
        }

        /// <summary>AI 年度发起互不侵犯（好感高国家间随机；配对检查——双方都接受则生效）。</summary>
        public static void TickNonAggressionAI(int year)
        {
            try
            {
                if (World.world?.kingdoms?.list == null) return;
                foreach (Kingdom a in World.world.kingdoms.list)
                {
                    if (a == null || !a.isAlive()) continue;
                    if (UnityEngine.Random.value >= NAP_AI_CHANCE) continue;
                    string aId = a.id.ToString();
                    // 找好感最高的非敌对国
                    Kingdom best = null;
                    int bestOpinion = NAP_MIN_OPINION;
                    foreach (Kingdom b in World.world.kingdoms.list)
                    {
                        if (b == null || !b.isAlive() || b == a) continue;
                        if (IsNonAggressionActive(aId, b.id.ToString(), year)) continue;
                        if (IsTruceActive(aId, b.id.ToString(), year)) continue;
                        // 已附庸关系不互侵（附庸本身不能打宗主）
                        if (VassalManager.Instance.IsVassal(b) || VassalManager.Instance.IsVassal(a)) continue;
                        try
                        {
                            int opinion = 0;
                            var op = World.world.diplomacy.getOpinion(a, b);
                            if (op != null) opinion = op.total;
                            if (opinion > bestOpinion) { bestOpinion = opinion; best = b; }
                        }
                        catch { }
                    }
                    if (best == null) continue;
                    // 双方建立互不侵犯（5 年）
                    string bId = best.id.ToString();
                    SetNonAggression(aId, bId, year + NAP_YEARS);
                    SetNonAggression(bId, aId, year + NAP_YEARS);
                    ZhuluContext.Instance.LogInfo($"[Diplomacy] {a.name} 与 {best.name} 缔结互不侵犯之约（{NAP_YEARS} 年）");
                    try { ZhuluWorldTip.ShowPair(a, best, $"【{a.name}】与【{best.name}】缔结互不侵犯之约！", 5f, UI.ZhuluUIStyle.Hex.Positive); } catch { }
                    // 阶段2J：互不侵犯入史册
                    try
                    {
                        ChronicleManager.Instance.AddEntry(year, ChronicleEventType.NonAggression,
                            aId, a.name ?? "", best.name ?? "",
                            $"<color={UI.ZhuluUIStyle.Hex.Azure}>{a.name}</color> 与 <color=#6ca3fd>{best.name}</color> 缔结互不侵犯之约（{NAP_YEARS} 年）。", false);
                    }
                    catch { }
                }
            }
            catch { }
        }

        // ═══════════════ ③ 附庸朝贡（金币化） ═══════════════

        /// <summary>
        /// 附庸年度朝贡结算（由 VassalManager.TickTribute 调用）。
        /// 贡金 = min(人口÷2, 附庸岁入×25%)，下限 1（阶段2S-13 修复：原人口÷2 无上限——
        /// 千人附庸岁入仅约 120 金却要贡 500 金，国库被瞬间抽干，宗主暴富失衡）。
        /// </summary>
        public static int ComputeTribute(Kingdom vassal)
        {
            try
            {
                if (vassal == null) return 0;
                int byPop = vassal.countUnits() / 2;
                long vassalIncome = KingdomDataExt.GetLong(vassal, "tre_in", 0);
                int cap = (int)(vassalIncome * 0.25f);
                // 首年岁入未采集（0）时退回人口口径的一半，防首年贡金恒 1
                if (cap <= 0) cap = byPop / 2 > 0 ? byPop / 2 : 1;
                return Math.Max(1, Math.Min(byPop, cap));
            }
            catch { return 0; }
        }

        /// <summary>宗主征收朝贡（返回贡金额；国库转给宗主）。
        /// 2026-08-19 串联：朝贡按宗主等级分层（天命 ×1.5 / 公 ×1.2 / 侯及以下 ×1.0）——宗主含金量分层。</summary>
        public static long CollectTribute(Kingdom vassal, Kingdom suzerain)
        {
            try
            {
                if (vassal == null || suzerain == null) return 0;
                int tribute = ComputeTribute(vassal);
                // 串联：宗主等级加成（朝贡分层）
                try
                {
                    var tier = FactionManager.GetKingdomTierOf(suzerain);
                    if (tier == OfficeManager.KingdomTier.Tianming) tribute = (int)(tribute * 1.5f);
                    else if (tier == OfficeManager.KingdomTier.Gong) tribute = (int)(tribute * 1.2f);
                }
                catch { }
                long vassalTre = KingdomDataExt.GetLong(vassal, "tre", 0);
                if (vassalTre <= 0) return 0; // 附庸国库不足 → 断贡
                long actual = Math.Min(tribute, vassalTre);
                KingdomDataExt.SetLong(vassal, "tre", vassalTre - actual);
                long suzTre = KingdomDataExt.GetLong(suzerain, "tre", 0);
                KingdomDataExt.SetLong(suzerain, "tre", suzTre + actual);
                return actual;
            }
            catch { return 0; }
        }
    }
}
