using ZhuluRebuild.Core;

namespace ZhuluRebuild.Domain
{
    /// <summary>
    /// 附庸系统纯判定规则（无状态，可独立测试）。
    /// 所有方法只做"能不能"的判断，不执行任何状态变更。
    /// </summary>
    public static class VassalRules
    {
        private const int INDEPENDENCE_COOLDOWN_YEARS = 20;
        private const int INDEPENDENCE_CHECK_INTERVAL = 5;
        private const float INDEPENDENCE_CHANCE_80 = 0.30f;
        private const float JOINT_INDEPENDENCE_CHANCE = 0.30f; // 阶段2B ⑥：20%→30%

        /// <summary>能否发动附庸战争（攻方等级>守方、守方人口兵力少于攻方、守方非天命国）。</summary>
        public static bool CanStartVassalWar(Kingdom attacker, Kingdom defender)
        {
            if (attacker == null || defender == null) return false;
            if (attacker == defender) return false;

            // 阶段2B 战争修复（用户 2026-08-15 报告）：攻方/守方任一已在战争中 → 禁止附庸战争。
            // 根因：附庸国独立战争期间被第三方附庸作战 → 战争叠加（同时打附庸国和宗主国）。
            // 主动战争约束（被动战争如独立/叛乱不受此限）。
            try
            {
                var atkWars = attacker.getWars();
                if (atkWars != null && atkWars.GetEnumerator().MoveNext()) return false;
                var defWars = defender.getWars();
                if (defWars != null && defWars.GetEnumerator().MoveNext()) return false;
            }
            catch { }

            var vassalMgr = VassalManager.Instance;

            // 守方不是天命国
            string dId = defender.id.ToString();
            if (PolicyManager.IsMandateHolderForKingdom(defender)) return false;

            // 守方不是攻方的附庸
            if (vassalMgr.IsVassal(defender) && vassalMgr.GetSuzerainId(defender) == attacker.id.ToString()) return false;

            // 攻方等级 > 守方等级
            int aCities = attacker.cities != null ? attacker.cities.Count : 0;
            int dCities = defender.cities != null ? defender.cities.Count : 0;
            var aTier = OfficeManager.GetTier(aCities);
            var dTier = OfficeManager.GetTier(dCities);

            // 天命国以人口+城市数综合判断
            string aId = attacker.id.ToString();
            if (PolicyManager.IsMandateHolderForKingdom(attacker))
            {
                int aPop = attacker.getPopulationPeople();
                int dPop = defender.getPopulationPeople();
                if (aPop <= dPop || aCities <= dCities) return false;
            }
            else
            {
                if (aTier <= dTier) return false;
            }

            // 守方人口 < 攻方人口
            int atkPop = attacker.getPopulationPeople();
            int defPop = defender.getPopulationPeople();
            if (defPop >= atkPop) return false;

            // 守方兵力 < 攻方兵力
            int atkArmy = attacker.countTotalWarriors();
            int defArmy = defender.countTotalWarriors();
            if (defArmy >= atkArmy) return false;

            return true;
        }

        /// <summary>附庸国能否发动独立战争（冷却期外、未在战争中）。</summary>
        public static bool CanStartIndependence(Kingdom vassal, Kingdom suzerain, int currentYear)
        {
            if (vassal == null || suzerain == null) return false;
            if (!vassal.isAlive() || !suzerain.isAlive()) return false;
            // 已经在打仗就不重复宣战
            if (World.world.wars.isInWarWith(vassal, suzerain)) return false;
            // 冷却期内不允许
            if (VassalManager.Instance.IsInIndependenceCooldown(vassal)) return false;
            // 2026-08-20 bug修复：和约/互不侵犯期内不得谋独（与 WarGateway 停战拦截一致，
            // 原版随机独立路径走 WarGateway 同样被拦——此处提前拦截使谋划不可酝酿）
            if (ZhuluDiplomacyService.IsWarForbidden(vassal.id.ToString(), suzerain.id.ToString(), currentYear))
                return false;
            return true;
        }

        /// <summary>
        /// 独立战争判定（阶段2B ⑥，2026-08-15 用户确认）：人口比 + 兵力比双判定。
        /// 修复"小国差距很大还独立"：宗主战争损失致人口骤降会误触发，兵力比兜底
        /// （附庸兵力 < 宗主 60% 不独立，防送死）。
        /// </summary>
        public static bool ShouldTriggerIndependence(int vassalPop, int suzerainPop, int vassalArmy, int suzerainArmy)
        {
            if (suzerainPop <= 0) return false;
            float ratio = (float)vassalPop / suzerainPop;
            bool popOk = ratio >= 1.0f || (ratio >= 0.8f && UnityEngine.Random.value < INDEPENDENCE_CHANCE_80);
            if (!popOk) return false;
            // 兵力比兜底：附庸兵力 < 宗主 60% → 不独立（差距悬殊）
            if (suzerainArmy > 0 && vassalArmy < suzerainArmy * 0.6f) return false;
            return true;
        }

        /// <summary>联合独立概率判定（阶段2B ⑥：放宽到总附庸人口≥宗主70%，概率 20%→30%——原条件苛刻致从未触发）。</summary>
        public static bool ShouldTriggerJointIndependence(int totalVassalPop, int suzerainPop, int vassalCount)
        {
            if (vassalCount < 2) return false;
            if (totalVassalPop < suzerainPop * 0.7f) return false;
            return UnityEngine.Random.value < JOINT_INDEPENDENCE_CHANCE;
        }

        /// <summary>附庸国能否结盟（当前规则：不能）。</summary>
        public static bool CanCreateAlliance(bool isVassal) => !isVassal;

        /// <summary>同宗主附庸之间能否开战（当前规则：不能）。</summary>
        public static bool CanVassalsFight(bool sameSuzerain) => !sameSuzerain;

        /// <summary>独立战争检查间隔（年）。</summary>
        public static int IndependenceCheckInterval => INDEPENDENCE_CHECK_INTERVAL;

        /// <summary>独立战争冷却年数。</summary>
        public static int IndependenceCooldownYears => INDEPENDENCE_COOLDOWN_YEARS;
    }
}
