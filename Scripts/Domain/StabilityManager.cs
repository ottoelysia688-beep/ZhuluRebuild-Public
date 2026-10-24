using System;
using System.Collections.Generic;
using UnityEngine;
using ZhuluRebuild.Core;
using ZhuluRebuild.Gameplay;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Domain
{
    /// <summary>
    /// 独立稳定度系统。稳定度反映当前社会秩序（短期波动），区别于天命（长期合法性）。
    /// 惯性制：今年 = 去年×0.80 + 目标值×0.20，每年结算一次。
    /// 效果通过 ModifierPipeline 影响税收和忠诚。
    /// </summary>
    public class StabilityManager : ISnapshotSection<StabilityManager.StabilityState>
    {
        public static StabilityManager Instance { get; } = new StabilityManager();

        private const string KEY_STABILITY = "stability";
        private const float InertiaOld = 0.80f;
        private const float InertiaNew = 0.20f;

        // 外部加成缓存（由专属政策/事件注入，年度 Tick 后清零）
        private readonly Dictionary<string, float> _externalBonus = new Dictionary<string, float>();

        private StabilityManager() { }

        /// <summary>外部系统注入稳定度加成/惩罚（如封禅泰山+15、逼宫-20）。在下次 Tick 时生效并消耗。</summary>
        public void AddExternalBonus(string kingdomId, float value)
        {
            if (string.IsNullOrEmpty(kingdomId)) return;
            if (_externalBonus.ContainsKey(kingdomId)) _externalBonus[kingdomId] += value;
            else _externalBonus[kingdomId] = value;
        }

        public float GetStability(string kingdomId)
        {
            Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
            if (k == null) return 50f;
            return KingdomDataExt.GetFloat(k, KEY_STABILITY, 50f);
        }

        public string GetStabilityGrade(float val)
        {
            if (val >= 80f) return "四海升平";
            if (val >= 60f) return "政局安稳";
            if (val >= 40f) return "暗流涌动";
            if (val >= 20f) return "风雨飘摇";
            return "岌岌可危";
        }

        public string GetStabilityColor(float val)
        {
            if (val >= 80f) return "#FFD700";
            if (val >= 60f) return "#90EE90";
            if (val >= 40f) return "#DDD060";
            if (val >= 20f) return "#FF8C00";
            return "#FF4500";
        }

        /// <summary>按国年度处理（由 YearDriver 每国每年调用一次）。</summary>
        public void OnKingdomYear(Kingdom k, int year)
        {
            if (k == null || !k.isAlive()) return;
            TickKingdom(k, year);
        }

        private void TickKingdom(Kingdom k, int currentYear)
        {
            string kId = k.id.ToString();
            float old = KingdomDataExt.GetFloat(k, KEY_STABILITY, 50f);

            // ── 计算目标值 ──
            float target = 50f;

            // 忠诚贡献：用 unhappy 城市比例估算（unhappy 越少越稳定）
            int totalCities = k.cities != null ? k.cities.Count : 0;
            if (totalCities > 0)
            {
                int unhappy = 0;
                try { unhappy = k.countUnhappyCities(); } catch { }
                float happyRatio = 1f - (float)unhappy / totalCities;
                target += happyRatio * 20f; // 全部开心 → +20，全部不开心 → +0
            }

            // 财政贡献
            var tre = TreasuryManager.Instance.GetReport(kId);
            target += tre.Balance >= 0 ? 5f : -10f;

            // 官席贡献
            int effCities = KingdomExt.GetEffectiveCities(kId, totalCities);
            string[] expectedSeats = OfficeManager.GetExpectedSeats(effCities);
            int vacant = 0;
            foreach (var off in expectedSeats)
            {
                var app = OfficeManager.Instance.GetAppointment(kId, off);
                if (app == null || string.IsNullOrEmpty(app.PersonId)) vacant++;
            }
            target += vacant == 0 ? 5f : -3f * vacant;

            // 天命贡献（仅正统天命国获得加成）
            if (PolicyManager.IsMandateHolderForKingdom(k))
            {
                float mandateVal = PolicyManager.GetMandateValueForKingdom(k);
                target += KingdomCivilizationService.Instance.IsNomadic(k)
                    ? NomadicMandateManager.Instance.GetStabilityBonus(mandateVal)
                    : OrthodoxMandateManager.Instance.GetStabilityBonus(mandateVal);
            }

            // 战争惩罚
            bool atWar = false;
            try { var wars = k.getWars(); if (wars != null) { foreach (var _ in wars) { atWar = true; break; } } } catch { }
            if (atWar) target -= 8f;

            // 阶段2A A2-5：党争烈度惩罚（党争越烈稳定越差；GetIntensity 现成）
            try
            {
                float intensity = Domain.FactionPoliticsManager.Instance.GetIntensity(k, kId);
                target -= intensity * 10f;
            }
            catch { }

            // 政策代价：当前有忠诚为负的政策 → -5
            foreach (var p in PolicyManager.Instance.GetActivePolicies(kId))
            {
                var def = PolicyManager.Instance.GetDefinition(p.PolicyId);
                if (def != null && def.TradeoffChannel == ModifierChannels.CityLoyaltyBonus && def.TradeoffValue < 0)
                {
                    target -= 5f;
                    break;
                }
            }

            // 丞相腐败惩罚
            float pmCorruption = CorruptionManager.Instance.GetPrimeMinisterCorruption(kId);
            if (pmCorruption > 1f) target -= pmCorruption * 0.15f;

            // 逐鹿特质：仁君+5 / 暴君-10 / 昏君-5
            if (k.king != null && k.king.isAlive())
            {
                target += TraitManager.Instance.GetStabilityBonus(k.king);
            }

            // 体制被动：子国「宗族议事」稳定度+5（小国宗族团结）
            if (effCities <= 2)
            {
                target += 5f;
            }

            // 外部加成（封禅/罪己诏/逼宫等）
            if (_externalBonus.TryGetValue(kId, out float extBonus))
            {
                target += extBonus;
                _externalBonus.Remove(kId);
            }

            // ── E2 软饱和（阶段1）：贡献和超出 ±50 的部分减半，防多项叠加溢出失真 ──
            float contrib = target - 50f;
            if (contrib > 50f) contrib = 50f + (contrib - 50f) * 0.5f;
            else if (contrib < -50f) contrib = -50f + (contrib + 50f) * 0.5f;
            target = 50f + contrib;

            // ── 惯性滑动 ──
            float val = old * InertiaOld + target * InertiaNew;
            val = Math.Max(0f, Math.Min(100f, val));

            // ── 连续化管道效果（阶段1 E1：消除断层，替代 70/30/15 阶梯）──
            // 税收倍率 = 1 + (val-50)×0.004（100 稳 +20% / 0 稳 -20%）；忠诚 = (val-50)×0.4
            string planId = $"stability_{kId}";
            string loyaltyPlanId = $"stability_loyalty_{kId}";

            float taxMult = 1f + (val - 50f) * 0.004f;
            float loyaltyBonus = (val - 50f) * 0.4f;

            ModifierPipeline.Instance.RegisterPlan(new ModifierPlan
            {
                SourceId = planId, TargetKingdomId = kId,
                Channel = ModifierChannels.GoldIncomeMultiplier,
                Additive = 0f, Multiplicative = taxMult,
                Reason = $"稳定度 {val:F0} (税收 {((taxMult - 1f) * 100f):F0}%)"
            });
            if (Mathf.Abs(loyaltyBonus) >= 1f)
            {
                ModifierPipeline.Instance.RegisterPlan(new ModifierPlan
                {
                    SourceId = loyaltyPlanId, TargetKingdomId = kId,
                    Channel = ModifierChannels.CityLoyaltyBonus,
                    Additive = loyaltyBonus, Multiplicative = 1.0f,
                    Reason = $"稳定度 {val:F0} (忠诚 {loyaltyBonus:F0})"
                });
            }
            else
            {
                ModifierPipeline.Instance.RemovePlan(loyaltyPlanId);
            }

            // ── 持久化 ──
            KingdomDataExt.SetFloat(k, KEY_STABILITY, val);
        }
    
        // ═══════════ v2 快照存档接口 ═══════════
        // ---- batch10-A1: schema v5 section ----
        public sealed class StabilityState { public float Stability; }
        public string SectionName => "stability";
        public Dictionary<string, StabilityState> CaptureStates()
        {
            var d = new Dictionary<string, StabilityState>();
            if (World.world?.kingdoms?.list == null) return d;
            foreach (Kingdom k in World.world.kingdoms.list)
            {
                if (k == null || !k.isAlive()) continue;
                d[k.id.ToString()] = new StabilityState { Stability = KingdomDataExt.GetFloat(k, KEY_STABILITY, 0f) };
            }
            return d;
        }
        public void ApplyStates(Dictionary<string, StabilityState> data)
        {
            if (data == null) return;
            foreach (var kv in data)
            {
                Kingdom k = KingdomDataExt.FindKingdomById(kv.Key);
                if (k == null || !k.isAlive() || kv.Value == null) continue;
                KingdomDataExt.SetFloat(k, KEY_STABILITY, kv.Value.Stability);
            }
        }

        public void MigrateFromCustomData() { }
        public void ResetRuntime()
        {
            _externalBonus.Clear();
        }

}
}
