namespace ZhuluRebuild.Gameplay
{
    /// <summary>
    /// ModifierPipeline 通道名唯一权威定义。
    /// 新增通道必须在此登记，并注明：①语义 ②单位（加算/乘算）③原生消费方。
    /// 严禁在业务代码里手写通道字符串 —— 历史上兵部 Buff 因 channel 名对不上而整场失效。
    /// </summary>
    public static class ModifierChannels
    {
        /// <summary>城市忠诚度加算修正（单位：点，加算）。消费方：LoyaltyCalculator.calculate。</summary>
        public const string CityLoyaltyBonus = "city_loyalty_bonus";

        /// <summary>国库金币税收乘算修正（单位：倍率增量，如 0.15 = +15%）。消费方：Kingdom.getTaxRateLocal。</summary>
        public const string GoldIncomeMultiplier = "gold_income_multiplier";

        /// <summary>军队兵员上限乘算修正（单位：倍率增量）。消费方：City.getMaxWarriors。</summary>
        public const string MilitaryReadiness = "military_readiness";

        /// <summary>列国外交好感度加算修正（单位：点）。消费方：DiplomacyManager.getOpinion。</summary>
        public const string DiplomaticOpinionBonus = "diplomatic_opinion_bonus";

        /// <summary>建筑物耐久乘算修正（单位：倍率增量）。消费方：Building.getMaxHealth。</summary>
        public const string BuildingHealthMultiplier = "building_health_multiplier";

        /// <summary>科举贤才拔擢效率乘算修正（单位：倍率增量，吏部 +0.20）。消费方：KejuManager 开科录取名额。</summary>
        public const string CivilExamEfficiency = "civil_exam_efficiency";

        /// <summary>城市人口上限乘算修正（单位：倍率增量，如 1.30 = +30%）。消费方：City.getPopulationMaximum。</summary>
        public const string PopulationGrowthSpeed = "population_growth_speed";
        // ── 阶段3H-19：战斗/内政八通道（消费端见 CombatChannelPatches / ZhuluWarService /
        //    CorruptionManager / VassalManager / StabilityManager.GetStability postfix）──
        public const string AttackPower = "attack_power";             // 乘算 stats["damage"]
        public const string DefensePower = "defense_power";           // 加算 stats["armor"]（减伤%）
        public const string ArmySpeed = "army_speed";                 // 乘算 stats["speed"]
        public const string UnitHealth = "unit_health";               // 乘算 stats["health"]
        public const string StabilityBonus = "stability_bonus";       // 加算 稳定度
        public const string WarExhaustionRate = "war_exhaustion_rate";// 乘算 厌战累积（<1 慢）
        public const string CorruptionRate = "corruption_rate";       // 乘算 腐败累积（<1 廉）
        public const string TributeIncome = "tribute_income";         // 乘算 朝贡收入

        // 2026-08-17 清理：删除零引用的规划通道声明（AgriculturalRecovery/RebellionSuppression/
        // CentralAuthorityBonus/MinisterEfficiencyBonus/MilitarySpeedBonus/MilitaryUpkeepDiscount/CultureSpreadSpeed）。
        // 教训：死声明留在 MultiplierChannels 集合里会误导后来者以为通道已接线。需要时按头部规范重新登记。

        /// <summary>乘算语义通道清单（阶段1 R9：Additive/Multiplicative 写入约定强制化，供各注册方共享）。</summary>
        public static readonly System.Collections.Generic.HashSet<string> MultiplierChannels =
            new System.Collections.Generic.HashSet<string>
            {
                GoldIncomeMultiplier, MilitaryReadiness, BuildingHealthMultiplier, PopulationGrowthSpeed,
                CivilExamEfficiency
            };
    }
}
