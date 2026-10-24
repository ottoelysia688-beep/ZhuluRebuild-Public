using ZhuluRebuild.Core;

namespace ZhuluRebuild.Domain
{
    /// <summary>
    /// 宣称/战争系统纯判定规则（无状态，可独立测试）。
    /// 所有方法只做"是否满足条件"的判断，不执行任何状态变更。
    /// </summary>
    public static class ClaimRules
    {
        private const int CLAIM_WAR_MAX_YEARS = 5;
        private const int INDEPENDENCE_WAR_MAX_YEARS = 5;
        private const int VASSAL_WAR_CAPTURE_THRESHOLD = 50;
        private const int CLAIM_COOLDOWN = 10;
        private const int CLAIM_MIN_KINGDOM_AGE = 10;

        /// <summary>能否谋划宣称（冷却、等级、兵力等纯判定）。</summary>
        public static bool CanPlotClaim(Kingdom k, int currentYear)
        {
            if (k == null || !k.isAlive()) return false;
            // 外交关闭时不允许谋划
            if (!WorldLawLibrary.world_law_diplomacy.isEnabled()) return false;
            // 已在战争中不允许再谋划
            if (k.getWars().GetEnumerator().MoveNext()) return false;
            // 王国年龄至少10年（避免初期一群国家同时谋划）
            if (k.getAge() < CLAIM_MIN_KINGDOM_AGE) return false;
            // 10年谋划冷却
            if (!ClaimManager.Instance.CheckClaimCooldownPublic(k)) return false;
            // 阶段2B ④：宣称战争后 3 年冷却（防连环开战——用户 2026-08-15 确认）
            if (!ClaimManager.Instance.CheckClaimWarCooldownPublic(k)) return false;
            return true;
        }

        /// <summary>宣称谋划冷却年数。</summary>
        public static int ClaimCooldown => CLAIM_COOLDOWN;

        /// <summary>
        /// 宣称战争目标合法性统一判定（所有入口共用，杜绝漏网）：
        /// 禁止打自己、已在战争中、自己的宗主、自己的附庸。
        /// 其他宗主国的附庸（包括与自己同属一个宗主的另一附庸）允许成为目标；
        /// 已脱离本国的旧附庸当然也允许，严格对应“只排除自己的附庸”。
        /// </summary>
        public static bool IsForbiddenTarget(Kingdom attacker, Kingdom target)
        {
            if (attacker == null || target == null) return true;
            if (target == attacker) return true;
            if (!target.isAlive()) return true;

            try
            {
                // 已在战争中（互战）——2026-08-16 用户指正：战争中守方【允许】被宣战（趁火打劫/多线战争合理），
                // 问题在结算而非宣战（曾错误加"守方在战争中禁宣战"已回滚）
                if (World.world.wars.isInWarWith(attacker, target)) return true;
            }
            catch { return true; }

            try
            {
                var vassalMgr = VassalManager.Instance;
                string mySuzId = vassalMgr.GetSuzerainId(attacker);
                string targetSuzId = vassalMgr.GetSuzerainId(target);

                // 目标是我的宗主 → 附庸不能打宗主
                if (mySuzId == target.id.ToString()) return true;
                // 目标是我的附庸
                if (vassalMgr.IsVassal(target) && targetSuzId == attacker.id.ToString()) return true;
            }
            catch { }

            return false;
        }

        /// <summary>宣称战争是否应停战（目标城市离开守方 或 超时）。</summary>
        public static bool ShouldEndClaimWar(City targetCity, Kingdom defender, int startYear, int currentYear)
        {
            // 守方已灭 → 停战
            if (defender == null || !defender.isAlive()) return true;

            // 城市不存在了 → 停战
            if (targetCity == null) return true;

            // 城市已离开守方 → 攻方阵营已拿下 → 停战
            Kingdom cityOwner = ClaimManager.GetCityKingdom(targetCity);
            if (cityOwner != null && cityOwner != defender) return true;

            // 超时停战
            if (startYear > 0 && currentYear - startYear >= CLAIM_WAR_MAX_YEARS) return true;

            return false;
        }

        /// <summary>宣称战争超时年数。</summary>
        public static int ClaimWarMaxYears => CLAIM_WAR_MAX_YEARS;

        /// <summary>
        /// 附庸战争停战判定。返回枚举表示结果：
        /// None=继续打, AttackerCapitalFallen=攻方首都被破(守方胜),
        /// DefenderCapitalCaptured=守方首都被攻方占(附庸成功),
        /// DefenderCapitalProgress=守方首都占领度达标(附庸成功)
        /// </summary>
        public static VassalWarOutcome CheckVassalWarOutcome(Kingdom attacker, Kingdom defender)
        {
            if (attacker == null || defender == null) return VassalWarOutcome.None;

            // 攻方首都被攻破 → 守方胜
            if (attacker.capital != null)
            {
                Kingdom capitalOwner = ClaimManager.GetCityKingdom(attacker.capital);
                if (capitalOwner != null && capitalOwner != attacker)
                    return VassalWarOutcome.AttackerCapitalFallen;
            }

            // 守方首都被攻方完全占领 → 附庸成功
            if (defender.capital != null)
            {
                Kingdom defCapitalOwner = ClaimManager.GetCityKingdom(defender.capital);
                if (defCapitalOwner == attacker)
                    return VassalWarOutcome.DefenderCapitalCaptured;
            }

            // 守方首都正在被攻方攻打，占领进度达标 → 附庸成功
            if (defender.capital != null && defender.capital.isGettingCapturedBy(attacker))
            {
                int captureProgress = defender.capital.last_visual_capture_ticks;
                if (captureProgress >= VASSAL_WAR_CAPTURE_THRESHOLD)
                    return VassalWarOutcome.DefenderCapitalProgress;
            }

            return VassalWarOutcome.None;
        }

        /// <summary>
        /// 独立战争停战判定。返回枚举表示结果：
        /// None=继续打, VassalCapitalFallen=附庸首都被破(独立失败),
        /// SuzerainCapitalFallen=宗主首都被占(独立成功),
        /// TimeExpired=坚持满年限(独立成功)
        /// </summary>
        public static IndependenceWarOutcome CheckIndependenceWarOutcome(
            Kingdom vassal, Kingdom suzerain, int startYear, int currentYear, int vassalStartCapitalId = 0)
        {
            if (vassal == null || suzerain == null) return IndependenceWarOutcome.None;

            // 附庸国首都被攻破 → 独立失败（优先用开战时记录的首都城市 id，防原版自动换首都）
            if (vassalStartCapitalId > 0)
            {
                City capitalCity = World.world.cities.get(vassalStartCapitalId);
                if (capitalCity == null)
                    return IndependenceWarOutcome.VassalCapitalFallen; // 首都城市被摧毁
                Kingdom vCapitalOwner = ClaimManager.GetCityKingdom(capitalCity);
                if (vCapitalOwner != null && vCapitalOwner != vassal)
                    return IndependenceWarOutcome.VassalCapitalFallen;
            }
            else if (vassal.capital != null)
            {
                Kingdom vCapitalOwner = ClaimManager.GetCityKingdom(vassal.capital);
                if (vCapitalOwner != null && vCapitalOwner != vassal)
                    return IndependenceWarOutcome.VassalCapitalFallen;
            }

            // 附庸国打下宗主首都 → 独立成功
            if (suzerain.capital != null)
            {
                Kingdom sCapitalOwner = ClaimManager.GetCityKingdom(suzerain.capital);
                if (sCapitalOwner != null && sCapitalOwner != suzerain)
                    return IndependenceWarOutcome.SuzerainCapitalFallen;
            }

            // 坚持满年限 → 独立成功
            if (startYear > 0 && currentYear - startYear >= INDEPENDENCE_WAR_MAX_YEARS)
                return IndependenceWarOutcome.TimeExpired;

            return IndependenceWarOutcome.None;
        }

        /// <summary>独立战争坚持年限。</summary>
        public static int IndependenceWarMaxYears => INDEPENDENCE_WAR_MAX_YEARS;

        /// <summary>附庸战争首都占领度阈值。</summary>
        public static int VassalWarCaptureThreshold => VASSAL_WAR_CAPTURE_THRESHOLD;
    }

    /// <summary>附庸战争判定结果。</summary>
    public enum VassalWarOutcome
    {
        None,
        AttackerCapitalFallen,
        DefenderCapitalCaptured,
        DefenderCapitalProgress
    }

    /// <summary>独立战争判定结果。</summary>
    public enum IndependenceWarOutcome
    {
        None,
        VassalCapitalFallen,
        SuzerainCapitalFallen,
        TimeExpired
    }
}
