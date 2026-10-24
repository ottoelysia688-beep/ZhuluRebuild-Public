using System;

namespace ZhuluRebuild.Domain
{
    /// <summary>无嗣时的三种结局。</summary>
    public enum NoHeirOutcome
    {
        /// <summary>远亲继承：宗室旁支/国内贤者入继，国祚延续。</summary>
        DistantKin = 0,
        /// <summary>权臣篡位：权势最高的官员取君位而代之，改朝换代。</summary>
        Usurpation = 1,
        /// <summary>分崩离析：无主可立，诸城离心、叛乱四起。</summary>
        Collapse = 2
    }

    /// <summary>判定输入：全部取自既有系统，不新增状态。</summary>
    public struct NoHeirInput
    {
        /// <summary>中央权力值 0-100（KingdomExt.GetPowerValue）。</summary>
        public float CentralPower;
        /// <summary>权势最高官员的权势 0-100+（CorruptionManager.GetPower）。</summary>
        public float TopOfficialPower;
        /// <summary>该官员的腐败 0-100（CorruptionManager.GetCorruption）。</summary>
        public float TopOfficialCorruption;
        /// <summary>天命值/天眷值 0-100（PolicyManager / NomadicMandateManager）。</summary>
        public float Mandate;
        /// <summary>稳定度 0-100（StabilityManager.GetStability）。</summary>
        public float Stability;
        /// <summary>城市数（大国离心面更大）。</summary>
        public int CityCount;
        /// <summary>是否天命/天眷持有国（非持有国不参与天命抑制）。</summary>
        public bool IsMandateHolder;
        /// <summary>国内是否存在可篡位的在任官员。</summary>
        public bool HasTopOfficial;
    }

    /// <summary>判定结果（含可解释性数据，便于写史册与排障）。</summary>
    public struct NoHeirDecision
    {
        public NoHeirOutcome Outcome;
        public float UsurpScore;
        public float CollapseScore;
        public string Reason;
    }

    /// <summary>
    /// 无嗣继承判定（F9）。
    ///
    /// 设计原则：
    ///   1. **不新造数值**——输入全部来自既有系统（中央权力/权臣权势/腐败/天命/稳定度/城数）；
    ///   2. **纯函数**——不读世界、不写状态，可被单测完全覆盖；
    ///   3. **可解释**——每次判定都产出分数与理由，写进史册与日志，不做黑箱。
    ///
    /// 三条轴（用户 2026-10-23 定调，且明确"不止这三个"）：
    ///   中央权力（越弱越易生变）× 权臣权力（越强越易篡）× 天命值（越高越能镇）
    ///   另外补入：权臣腐败（篡位动机）、稳定度（离心程度）、城市数（分裂面）。
    ///
    /// 阈值刻意偏保守——正常国家即使无嗣也应大概率走「远亲继承」，
    /// 篡位与分裂只在"中央崩坏 + 权臣坐大"叠加时出现。
    /// </summary>
    public static class NoHeirResolver
    {
        // ── 权重（可调）──
        private const float W_UsurpOfficialPower = 0.40f;
        private const float W_UsurpCorruption = 0.20f;
        private const float W_UsurpWeakCenter = 0.25f;
        private const float W_UsurpLowStability = 0.15f;

        private const float W_CollapseWeakCenter = 0.30f;
        private const float W_CollapseLowStability = 0.25f;
        private const float W_CollapseMandateGap = 0.20f;
        private const float W_CollapseCitySpread = 0.25f;

        // ── 天命抑制（仅天命/天眷持有国）──
        private const float MandateSuppressUsurp = 0.35f;
        private const float MandateSuppressCollapse = 0.50f;

        // ── 判定门槛 ──
        // 标定依据：满值局面（权臣100/腐败100/中央0/稳定0）篡位分约 81，
        // 典型崩坏局面（权臣80/腐败70/中央20/稳定45）约 56，故取 50 为线；
        // 分裂分满值约 100，典型崩坏（中央15/稳定20/12城）约 42，故取 40 为线。
        private const float UsurpThreshold = 50f;
        private const float UsurpMinOfficialPower = 55f;
        private const float UsurpMaxCentralPower = 45f;
        private const float CollapseThreshold = 40f;

        private static float Clamp100(float v)
        {
            if (float.IsNaN(v)) return 0f;
            if (v < 0f) return 0f;
            if (v > 100f) return 100f;
            return v;
        }

        /// <summary>核心判定。纯函数：同样输入必得同样结果。</summary>
        public static NoHeirDecision Decide(NoHeirInput p)
        {
            float weakCenter = Clamp100(50f - p.CentralPower);   // 中央低于 50 才开始计分
            float lowStability = Clamp100(60f - p.Stability);    // 稳定低于 60 才开始计分
            float mandateGap = Clamp100(40f - p.Mandate);        // 天命低于 40 才算失德

            float usurp =
                W_UsurpOfficialPower * Clamp100(p.TopOfficialPower) +
                W_UsurpCorruption * Clamp100(p.TopOfficialCorruption) +
                W_UsurpWeakCenter * weakCenter +
                W_UsurpLowStability * lowStability;

            float collapse =
                W_CollapseWeakCenter * weakCenter +
                W_CollapseLowStability * lowStability +
                W_CollapseMandateGap * mandateGap +
                W_CollapseCitySpread * Clamp100(p.CityCount * 5f);

            string mandateNote = "";
            if (p.IsMandateHolder)
            {
                float suppress = Clamp100(p.Mandate - 50f);   // 天命 50 以上才有镇压力
                float cutU = MandateSuppressUsurp * suppress;
                float cutC = MandateSuppressCollapse * suppress;
                usurp -= cutU;
                collapse -= cutC;
                if (suppress > 0f) mandateNote = $"，天命镇压 篡位-{cutU:F0}/分裂-{cutC:F0}";
            }

            usurp = Clamp100(usurp);
            collapse = Clamp100(collapse);

            var d = new NoHeirDecision { UsurpScore = usurp, CollapseScore = collapse };

            // 无人可篡 → 只能是远亲或分裂
            if (!p.HasTopOfficial)
            {
                if (collapse >= CollapseThreshold)
                {
                    d.Outcome = NoHeirOutcome.Collapse;
                    d.Reason = $"无嗣且无在任权臣；稳定{p.Stability:F0}/中央{p.CentralPower:F0} 已至崩解线（分裂{collapse:F0}）{mandateNote}";
                }
                else
                {
                    d.Outcome = NoHeirOutcome.DistantKin;
                    d.Reason = $"无嗣且无在任权臣，宗室旁支可继（分裂{collapse:F0} 未及阈值）{mandateNote}";
                }
                return d;
            }

            bool usurpQualified = usurp >= UsurpThreshold
                                  && p.TopOfficialPower >= UsurpMinOfficialPower
                                  && p.CentralPower < UsurpMaxCentralPower;

            if (usurpQualified && usurp > collapse)
            {
                d.Outcome = NoHeirOutcome.Usurpation;
                d.Reason = $"无嗣；权臣权势{p.TopOfficialPower:F0}/腐败{p.TopOfficialCorruption:F0}，中央仅{p.CentralPower:F0} → 篡位倾向{usurp:F0}（分裂{collapse:F0}）{mandateNote}";
                return d;
            }

            if (collapse >= CollapseThreshold && collapse > usurp)
            {
                d.Outcome = NoHeirOutcome.Collapse;
                d.Reason = $"无嗣；中央{p.CentralPower:F0}/稳定{p.Stability:F0}/城{p.CityCount} → 分裂倾向{collapse:F0}（篡位{usurp:F0}）{mandateNote}";
                return d;
            }

            d.Outcome = NoHeirOutcome.DistantKin;
            d.Reason = $"无嗣但局面可控：篡位{usurp:F0}/分裂{collapse:F0} 均未越线 → 远亲继承{mandateNote}";
            return d;
        }
    }
}
