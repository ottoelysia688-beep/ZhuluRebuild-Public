using System;

namespace ZhuluRebuild.Domain
{
    /// <summary>
    /// 战争分数与白和的纯函数规则层（P1-A 批，2026-08-23）。
    /// 设计参照 AncientWarfare3.0 的 WarScoreRules：无状态、可单测、增量与时间项分离——
    /// 时间惩罚（AnnualTimePenalty）只允许调用方每年施加一次，根治历史 n(n+1)/2 类累计误差。
    /// 数值口径与 ZhuluWarService.EvaluateWarScore 既有实现严格一致（15/城、击杀差÷2、首都±20、年度−1）。
    /// </summary>
    public static class WarScoreRules
    {
        public const int CityWeight = 15;         // 每城得失
        public const int CapitalSwing = 20;       // 首都沦陷摆动
        public const int AnnualTimePenalty = -1;  // 时间厌战：攻方净分每年一次

        /// <summary>守方失城 → 攻方得分（≥0）。</summary>
        public static int CitiesGained(int defenderStartCities, int defenderNowCities)
            => Math.Max(0, defenderStartCities - defenderNowCities) * CityWeight;

        /// <summary>攻方失城 → 攻方失分（≤0，返回值已带符号）。</summary>
        public static int CitiesLost(int attackerStartCities, int attackerNowCities)
            => -Math.Max(0, attackerStartCities - attackerNowCities) * CityWeight;

        /// <summary>击杀差分量：(守方阵亡 − 攻方阵亡) ÷ 2（C# 整除截断，与既有口径一致）。</summary>
        public static int KillComponent(int deadAttackers, int deadDefenders)
            => (deadDefenders - deadAttackers) / 2;

        /// <summary>首都分量：攻占对方京都 ±CapitalSwing。</summary>
        public static int CapitalComponent(bool attackerHoldsDefenderCapital, bool defenderHoldsAttackerCapital)
        {
            int v = 0;
            if (attackerHoldsDefenderCapital) v += CapitalSwing;
            if (defenderHoldsAttackerCapital) v -= CapitalSwing;
            return v;
        }

        // ═══════════ 白和（AW3 式概率化，替代"满10年硬触发"） ═══════════

        /// <summary>兵力僵持带：军力比 ∈[0.8, 1.25] 视为久持不下。</summary>
        public static bool IsStalemate(float myArmyOverEnemyArmy)
            => myArmyOverEnemyArmy >= 0.8f && myArmyOverEnemyArmy <= 1.25f;

        /// <summary>求和意愿·僵持加成（AW3 僵持因子移植：谁也吃不掉谁 → 都想体面收场）。</summary>
        public static int PeaceWillingnessStalemateBonus(float myArmyOverEnemyArmy)
            => IsStalemate(myArmyOverEnemyArmy) ? 10 : 0;

        /// <summary>
        /// 白和概率 [0, 0.45]：
        ///   开战 &lt;5 年 → 0（血气正盛）；
        ///   基础坡道 = min(0.20, max(0, 年限−10)×0.01)；
        ///   + 僵持 0.10（兵力比 0.8~1.25）；
        ///   + 和平倾向 (1−双方执政党战争轴均值)×0.25；
        ///   − 主战惩罚（轴均值 ≥0.7 时 −0.10）。
        /// 触发由调用方以 DeterministicRandom.Roll01(warId, year, "whitepeace") 判定；
        /// 另设硬上限年限强制收场（防止极端低概率永战）。
        /// </summary>
        public static float WhitePeaceProbability(int elapsedYears, float myArmyOverEnemyArmy, float avgWarAxis)
        {
            if (elapsedYears < 5) return 0f;
            float p = Math.Min(0.20f, Math.Max(0, elapsedYears - 10) * 0.01f);
            if (IsStalemate(myArmyOverEnemyArmy)) p += 0.10f;
            float axis = Clamp01(avgWarAxis);
            p += (1f - axis) * 0.25f;
            if (axis >= 0.7f) p -= 0.10f;
            return Clamp(p, 0f, 0.45f);
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
