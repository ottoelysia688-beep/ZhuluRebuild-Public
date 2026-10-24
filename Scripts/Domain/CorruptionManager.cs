using System;
using System.Collections.Generic;
using UnityEngine;
using ZhuluRebuild.Core;
using ZhuluRebuild.Gameplay;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Domain
{
    /// <summary>
    /// 腐败值 + 权势值管理器。
    /// 恐怖值/胆量/权臣进度三值体系设计。
    /// 映射："胆量"→官员权势值；"恐怖值"→君主权力值(皇权)。
    /// </summary>
    public class CorruptionManager : ISnapshotSection<CorruptionManager.CorruptionState>
    {
        public static CorruptionManager Instance { get; } = new CorruptionManager();

        private const string KEY_STATS = "officials_stats";


        private CorruptionManager() { }

        // ═══════════════════════ 数据结构 ═══════════════════════

        private class OfficialStats
        {
            public string OfficeId;
            public float Corruption;
            public float Power;
        }

        // ═══════════════════════ 主循环 ═══════════════════════

        /// <summary>按国年度处理（由 YearDriver 每国每年调用一次）。</summary>
        public void OnKingdomYear(Kingdom k, int year)
        {
            if (k == null || !k.isAlive()) return;
            TickKingdom(k, year);
        }

        private void TickKingdom(Kingdom k, int currentYear)
        {
            string kId = k.id.ToString();
            var stats = LoadStats(k);
            var records = OfficeManager.Instance.GetRecordsPublic(kId);
            if (records == null || records.Count == 0)
            {
                // 无官员，清除残留数据
                if (stats.Count > 0) SaveStats(k, new List<OfficialStats>());
                return;
            }

            int royalPower = KingdomExt.GetPowerValue(kId);
            bool isMandateHolder = PolicyManager.IsMandateHolderForKingdom(k);
            float mandateVal = isMandateHolder ? PolicyManager.GetMandateValueForKingdom(k) : 0f;

            var newStats = new List<OfficialStats>();

            foreach (var app in records)
            {
                if (app == null || string.IsNullOrEmpty(app.OfficeId)) continue;

                // 找旧数据
                float corruption = 0f, power = 0f;
                foreach (var s in stats)
                {
                    if (s.OfficeId == app.OfficeId) { corruption = s.Corruption; power = s.Power; break; }
                }

                int tenure = currentYear - app.AppointmentYear;
                if (tenure < 0) tenure = 0;

                // ── 权势增长（阶段2U 对齐：原 0.4+tenure*0.04 → 25 年致仕极值≈50，
                //    而野心家80/政变75/篡位90 门槛全线不可达——增速×2.5 + 门槛同步下调15档）──
                // D5-① 分段（2026-10-06）：tenure>30 增速降至 1.0/年、90 以上再折半软顶——
                //    线性增速使长期在任者必然打满（D批实测 29.3% 国年权势≥95，党势通胀与
                //    政变门槛失效的上游根因）。阶段2U 语义保留：25 年内政变门槛 65 仍可达。
                //    [PLACEHOLDER] 拐点 30 / 慢速 1.0 / 软顶 90——playtest 验证（失败信号：权势≥95 国年占比 >10%）
                float powerGrowth = 1.0f + tenure * 0.10f;
                if (tenure > 30) powerGrowth = 1.0f;
                if (power >= 90f) powerGrowth *= 0.5f;
                if (app.OfficeId == "prime_minister") powerGrowth += 0.3f;
                int score = GetOfficerScore(k, app);
                if (score >= 70) powerGrowth += 0.2f;
                if (corruption >= 50f) powerGrowth += 0.2f;
                // 皇权压制
                if (royalPower >= 80) powerGrowth *= 0.5f;
                // 天命压制
                if (isMandateHolder && mandateVal >= 70f) powerGrowth *= 0.6f;
                power = Mathf.Clamp(power + powerGrowth, 0f, 100f);

                // ── 腐败增长（阶段1 D1：基值 1.0→1.5、任期系数 0.08→0.12，使 30-50 年任期可达贪官线≥60；
                //      D2：上任蜜月期前 5 年增长减半，平滑任期惩罚）──
                float corrGrowth = 1.5f + tenure * 0.12f + power * 0.02f;
                if (tenure < 5) corrGrowth *= 0.5f;

                // 特质修正
                Actor actor = FindActorInKingdom(k, app.PersonId);
                float traitMult = 1.0f;
                if (actor != null)
                {
                    try
                    {
                        if (actor.hasTrait("blessed")) traitMult = 0f;
                        else if (actor.hasTrait("immortal")) traitMult = 0.2f;
                        else if (actor.hasTrait("psychopath")) traitMult = 2.0f;
                    }
                    catch { }
                    // 逐鹿特质：贪官加速腐败
                    traitMult *= TraitManager.Instance.GetCorruptionMultiplier(actor);
                }
                // 评分修正
                if (score >= 85) traitMult *= 0.5f;
                else if (score < 35) traitMult *= 1.5f;

                // 君主威慑
                float kingMult = 1.0f;
                if (royalPower >= 80) kingMult = 0.4f;
                else if (royalPower >= 60) kingMult = 0.7f;
                else if (royalPower < 30) kingMult = 1.5f;

                // 天命抑制
                float mandateMult = 1.0f;
                if (isMandateHolder)
                {
                    if (mandateVal >= 90f) mandateMult = 0.3f;
                    else if (mandateVal >= 70f) mandateMult = 0.5f;
                }

                // 体制被动：侯国「朝议制度」腐败增长x0.8（6-11城且五席满员）
                float tierMult = 1.0f;
                int corrEffCities = KingdomExt.GetEffectiveCities(kId, k.cities != null ? k.cities.Count : 0);
                if (OfficeManager.GetTier(corrEffCities) == OfficeManager.KingdomTier.Hou)
                {
                    string[] houSeats = OfficeManager.GetExpectedSeats(corrEffCities);
                    bool houAllFilled = true;
                    foreach (var seat in houSeats)
                    {
                        var seatApp = OfficeManager.Instance.GetAppointment(kId, seat);
                        if (seatApp == null || string.IsNullOrEmpty(seatApp.PersonId)) { houAllFilled = false; break; }
                    }
                    if (houAllFilled) tierMult = 0.8f;
                }

                // 阶段3H-19：腐败率通道（察奸律/高俸养廉——growth×rate，<0 更廉）
                try { corrGrowth *= Gameplay.ModifierPipeline.Instance.EvaluateChannelMultiplier(Gameplay.ModifierChannels.CorruptionRate, kId, 1f); } catch { }

                // C5 自然消退（2026-10-01 [PLACEHOLDER]）：打破「只涨不跌」——
                // 原 corrGrowth ≥1.5 恒正且换官继承旧值，唯一清零途径是改朝清洗；
                // 低增速（新任/清官）+ 强抑制（皇权/天命）组合下净回落，让「换清官+强势君主」成为真实策略；
                // 打满的权臣（corrGrowth 高）不吃衰减，保留权臣张力
                float decay = 0f;
                if (tenure >= 5 && corrGrowth < 4f) decay = 0.5f;
                if (royalPower >= 60) decay += 0.5f;
                if (isMandateHolder && mandateVal >= 70f) decay += 0.5f;
                corruption += corrGrowth * traitMult * kingMult * mandateMult * tierMult - decay;
                corruption = Mathf.Clamp(corruption, 0f, 100f);

                newStats.Add(new OfficialStats { OfficeId = app.OfficeId, Corruption = corruption, Power = power });

                // ── 管道效果 ──
                ApplyCorruptionEffect(kId, app.OfficeId, corruption, power, royalPower);
            }

            SaveStats(k, newStats);

            // ── 权臣事件检测 ──
            CheckPowerfulOfficialEvents(k, kId, newStats, royalPower, currentYear);

            // ── 阶段2D ⑦ 级差忠诚（诡秘参考）：官员权势 ≥ 国王权势 → 忠诚惩罚（权臣压主）──
            ApplyRankLoyaltyPenalty(kId, royalPower, newStats);
        }

        /// <summary>级差忠诚：每名权势 ≥ 国王权势的官员 → 全国忠诚 -2（权臣压主，民心向背）。</summary>
        private void ApplyRankLoyaltyPenalty(string kId, int royalPower, List<OfficialStats> stats)
        {
            try
            {
                if (stats == null) return;
                int penalty = 0;
                foreach (var s in stats)
                {
                    if (s == null) continue;
                    if (s.Power >= royalPower + 10f) penalty += 2; // 权势压主 → -2
                }
                if (penalty <= 0) return;
                Gameplay.ModifierPipeline.Instance.RegisterPlan(new Gameplay.ModifierPlan
                {
                    SourceId = "rank_loyalty_" + kId,
                    TargetKingdomId = kId,
                    Channel = Gameplay.ModifierChannels.CityLoyaltyBonus,
                    Additive = -penalty,
                    Multiplicative = 1.0f,
                    Reason = $"权臣压主（{penalty / 2} 名官员权势逾主）：城市忠诚 -{penalty}"
                });
                ZhuluContext.Instance.LogInfo($"[Corruption] {kId} 级差忠诚: 官员压主 {penalty / 2} 名，忠诚 -{penalty}");
            }
            catch { }
        }

        // ═══════════════════════ 管道效果 ═══════════════════════

        private void ApplyCorruptionEffect(string kId, string officeId, float corruption, float power, int royalPower)
        {
            string sourceId = $"corruption_{kId}_{officeId}";

            // 权臣加成：权势>=60且皇权<50 → 腐败效果×1.5
            float effectMult = 1.0f;
            if (power >= 60f && royalPower < 50) effectMult = 1.5f;

            float effCorr = corruption * effectMult;

            switch (officeId)
            {
                case "revenue": // 户部：税收流失
                    if (effCorr > 1f)
                        RegisterCorrPlan(sourceId, kId, ModifierChannels.GoldIncomeMultiplier, 0f, 1f - effCorr * 0.003f, $"户部腐败（税收 -{effCorr * 0.3f:F0}%）");
                    else ModifierPipeline.Instance.RemovePlan(sourceId);
                    break;
                case "military": // 兵部：兵员虚报
                    if (effCorr > 1f)
                        RegisterCorrPlan(sourceId, kId, ModifierChannels.MilitaryReadiness, 0f, 1f - effCorr * 0.002f, $"兵部腐败（兵员 -{effCorr * 0.2f:F0}%）");
                    else ModifierPipeline.Instance.RemovePlan(sourceId);
                    break;
                case "works": // 工部：建筑质量
                    if (effCorr > 1f)
                        RegisterCorrPlan(sourceId, kId, ModifierChannels.BuildingHealthMultiplier, 0f, 1f - effCorr * 0.002f, $"工部腐败（耐久 -{effCorr * 0.2f:F0}%）");
                    else ModifierPipeline.Instance.RemovePlan(sourceId);
                    break;
                case "rites": // 礼部：外交失信
                    if (effCorr > 1f)
                        RegisterCorrPlan(sourceId, kId, ModifierChannels.DiplomaticOpinionBonus, -effCorr * 0.2f, 1f, $"礼部腐败（外交 -{effCorr * 0.2f:F0}）");
                    else ModifierPipeline.Instance.RemovePlan(sourceId);
                    break;
                case "personnel": // 吏部：忠诚涣散
                    if (effCorr > 1f)
                        RegisterCorrPlan(sourceId, kId, ModifierChannels.CityLoyaltyBonus, -effCorr * 0.15f, 1f, $"吏部腐败（忠诚 -{effCorr * 0.15f:F0}）");
                    else ModifierPipeline.Instance.RemovePlan(sourceId);
                    break;
                case "prime_minister": // 丞相：影响稳定度（由StabilityManager读取）
                case "justice": // 刑部：留白
                default:
                    ModifierPipeline.Instance.RemovePlan(sourceId);
                    break;
            }
        }

        private void RegisterCorrPlan(string sourceId, string kId, string channel, float add, float mult, string reason)
        {
            ModifierPipeline.Instance.RegisterPlan(new ModifierPlan
            {
                SourceId = sourceId,
                TargetKingdomId = kId,
                Channel = channel,
                Additive = add,
                Multiplicative = mult,
                Reason = reason
            });
        }

        // ═══════════════════════ 权臣事件 ═══════════════════════

        private void CheckPowerfulOfficialEvents(Kingdom k, string kId, List<OfficialStats> stats, int royalPower, int year)
        {
            foreach (var s in stats)
            {
                // 阶段2U 门槛对齐（90→70 / 75→60 / 60→45——配合增速×2.5，25年致仕窗口内可达）
                if (s.Power >= 70f && royalPower < 30)
                {
                    // 篡位危机：每10年30%概率逼宫
                    if (year % 10 == 0 && UnityEngine.Random.value < 0.30f)
                    {
                        TriggerUsurpation(k, kId, s.OfficeId, year);
                    }
                }
                else if (s.Power >= 60f && royalPower < 40)
                {
                    // 权臣架空：记录一次（不重复）
                    string flagKey = $"usurp_flag_{s.OfficeId}";
                    if (KingdomDataExt.GetLong(k, flagKey, 0) == 0)
                    {
                        KingdomDataExt.SetLong(k, flagKey, 1);
                        string name = GetOfficerName(kId, s.OfficeId);
                        string title = OfficeManager.GetOfficeTitleForKingdom(s.OfficeId, k);
                        ChronicleManager.Instance.AddEntry(year, ChronicleEventType.KingChange, kId,
                            k.name ?? "", name,
                            $"<color=#FF4500>【{title}】{name} 权倾朝野，架空君权，朝政皆出其门。</color>", false);
                        ZhuluWorldTip.Show(k, $"【{k.name}】{name} 权倾朝野，架空君权！", 5f, UI.ZhuluUIStyle.Hex.Warn);
                    }
                }
                else if (s.Power >= 45f && royalPower < 50)
                {
                    // 尾大不掉：WorldTip（低频；阶段2U：每国每年至多播报权势最高 1 人——防多国多官同帧连弹）
                    if (year % 20 == 0)
                    {
                        string name = GetOfficerName(kId, s.OfficeId);
                        ZhuluWorldTip.Show(k, $"【{k.name}】{name} 尾大不掉，朝廷难以节制！", 4f, UI.ZhuluUIStyle.Hex.Warn);
                    }
                }
            }
        }

        private void TriggerUsurpation(Kingdom k, string kId, string officeId, int year)
        {
            string name = GetOfficerName(kId, officeId);
            string title = OfficeManager.GetOfficeTitleForKingdom(officeId, k);

            // 尝试真篡位：找到官员 Actor，调用 kingdom.setKing
            Actor usurper = FindActorInKingdom(k, GetOfficerPersonId(kId, officeId));
            bool success = false;
            Actor oldKing = null;

            if (usurper != null && usurper.isAlive())
            {
                try { oldKing = k.king; } catch { }
                try
                {
                    k.setKing(usurper, false);
                    success = true;
                }
                catch (Exception ex)
                {
                    ZhuluContext.Instance.LogWarning($"[CorruptionManager] setKing 失败，降级为逼宫未遂: {ex.Message}");
                }
            }

            if (success)
            {
                // 篡位成功：罢免该官员旧官职 + 史册 + 稳定度惩罚
                // 阶段2A A1-6：标记篡位（换君观察分类用）
                DynastyObserver.Instance.MarkUsurpation(kId);
                // 2026-08-18 改朝换代全套：天命新朝卡片/国号/前朝宗室/谱牒隔离/清洗旧臣/中央权力重置
                DynastyObserver.Instance.ProcessDynastyChange(k, kId, usurper, oldKing, year);
                OfficeManager.Instance.DismissOfficer(kId, k.name ?? "", officeId, year, "篡位");
                StabilityManager.Instance.AddExternalBonus(kId, -15f);
                ChronicleManager.Instance.AddEntry(year, ChronicleEventType.KingChange, kId,
                    k.name ?? "", name,
                    $"<color=#FF0000>【{title}】{name} 起兵篡位！废帝自立，改朝换代。</color>", true);
                WorldTip.showNow($"【{k.name}】{name} 篡位成功！废帝自立！", pTranslate: false, "top", 8f, UI.ZhuluUIStyle.Hex.Negative);
                ZhuluContext.Instance.LogInfo($"[CorruptionManager] 篡位成功: {k.name} {name}({title})");
            }
            else
            {
                // 降级：逼宫未遂
                OfficeManager.Instance.DismissOfficer(kId, k.name ?? "", officeId, year, "逼宫");
                StabilityManager.Instance.AddExternalBonus(kId, -20f);
                ChronicleManager.Instance.AddEntry(year, ChronicleEventType.KingChange, kId,
                    k.name ?? "", name,
                    $"<color=#FF0000>【{title}】{name} 起兵逼宫！事败被逐，然朝野震动，社稷动荡。</color>", true);
                WorldTip.showNow($"【{k.name}】{name} 起兵逼宫！事败被逐出朝堂！", pTranslate: false, "top", 6f, UI.ZhuluUIStyle.Hex.Negative);
                ZhuluContext.Instance.LogInfo($"[CorruptionManager] 逼宫未遂: {k.name} {name}({title})");
            }
        }


        // ═══════════════════════ 公开查询接口 ═══════════════════════

        public float GetCorruption(string kingdomId, string officeId)
        {
            Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
            if (k == null) return 0f;
            foreach (var s in LoadStats(k))
                if (s.OfficeId == officeId) return s.Corruption;
            return 0f;
        }

        public float GetPower(string kingdomId, string officeId)
        {
            Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
            if (k == null) return 0f;
            foreach (var s in LoadStats(k))
                if (s.OfficeId == officeId) return s.Power;
            return 0f;
        }

        /// <summary>官员卸任/罢免/致仕/篡位时清除该官职的腐败/权势数据，新任命者从0开始。</summary>
        public void ClearOfficerStats(string kingdomId, string officeId)
        {
            Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
            if (k == null) return;
            var stats = LoadStats(k);
            stats.RemoveAll(s => s.OfficeId == officeId);
            SaveStats(k, stats);
            // 同时移除腐败管道效果
            ModifierPipeline.Instance.RemovePlan($"corruption_{kingdomId}_{officeId}");
        }

        public string GetCorruptionGrade(float corruption)
        {
            float integrity = 100f - corruption;
            if (integrity >= 80f) return "清正廉明";
            if (integrity >= 50f) return "尚属自持";
            if (integrity >= 25f) return "贪墨渐深";
            return "巨贪蠹政";
        }

        public string GetPowerGrade(float power)
        {
            if (power >= 70f) return "权倾朝野";
            if (power >= 40f) return "颇有权势";
            return "恭谨守分";
        }

        /// <summary>获取王国中权势最高的官员信息（供国势页显示）。</summary>
        public (string officeId, string name, float power) GetTopPowerfulOfficial(string kingdomId)
        {
            Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
            if (k == null) return (null, null, 0f);
            var stats = LoadStats(k);
            float maxPow = 0f;
            string maxOff = null;
            foreach (var s in stats)
            {
                if (s.Power > maxPow) { maxPow = s.Power; maxOff = s.OfficeId; }
            }
            if (maxOff == null) return (null, null, 0f);
            string name = GetOfficerName(kingdomId, maxOff);
            return (maxOff, name, maxPow);
        }

        /// <summary>获取丞相腐败值（供 StabilityManager 读取）。</summary>
        public float GetPrimeMinisterCorruption(string kingdomId)
        {
            return GetCorruption(kingdomId, "prime_minister");
        }

        // ═══════════════════════ 持久化 ═══════════════════════

        private List<OfficialStats> LoadStats(Kingdom k)
        {
            var result = new List<OfficialStats>();
            string raw = KingdomDataExt.GetStr(k, KEY_STATS);
            if (string.IsNullOrEmpty(raw)) return result;

            foreach (var chunk in raw.Split('|'))
            {
                if (string.IsNullOrEmpty(chunk)) continue;
                var parts = chunk.Split(':');
                if (parts.Length < 3) continue;
                result.Add(new OfficialStats
                {
                    OfficeId = parts[0],
                    Corruption = float.TryParse(parts[1], out float c) ? c : 0f,
                    Power = float.TryParse(parts[2], out float p) ? p : 0f
                });
            }
            return result;
        }

        private void SaveStats(Kingdom k, List<OfficialStats> stats)
        {
            var chunks = new List<string>();
            foreach (var s in stats)
            {
                chunks.Add($"{s.OfficeId}:{s.Corruption:F1}:{s.Power:F1}");
            }
            KingdomDataExt.SetStr(k, KEY_STATS, string.Join("|", chunks));
        }

        // ═══════════════════════ 辅助 ═══════════════════════

        private Actor FindActorInKingdom(Kingdom k, string personId)
        {
            if (k?.units == null || string.IsNullOrEmpty(personId)) return null;
            try
            {
                foreach (Actor unit in k.units)
                {
                    if (unit == null || !unit.isAlive()) continue;
                    if (unit.getID().ToString() == personId) return unit;
                }
            }
            catch { }
            return null;
        }

        private int GetOfficerScore(Kingdom k, OfficeAppointmentRecord app)
        {
            Actor actor = FindActorInKingdom(k, app.PersonId);
            if (actor == null) return 50;
            try
            {
                int officeIdx = Array.IndexOf(OfficeManager.OfficesList, app.OfficeId);
                if (officeIdx < 0) officeIdx = 6;
                int actorAge = 0; try { actorAge = actor.getAge(); } catch { }
                var (score, _) = OfficialRatingManager.CalcOfficialScore(actor, officeIdx, actorAge); // 阶段2B 方案3：基础x0.7+最终x0.3
                // 逐鹿特质：天纵奇才额外+10分
                score += TraitManager.Instance.GetRatingBonus(actor);
                // 体制被动：天命国「天子临朝」所有官员考评+5
                if (PolicyManager.IsMandateHolderForKingdom(k))
                {
                    score += 5;
                }
                return score;
            }
            catch { return 50; }
        }

        private string GetOfficerName(string kingdomId, string officeId)
        {
            var app = OfficeManager.Instance.GetAppointment(kingdomId, officeId);
            return app?.PersonName ?? "某人";
        }

        private string GetOfficerPersonId(string kingdomId, string officeId)
        {
            var app = OfficeManager.Instance.GetAppointment(kingdomId, officeId);
            return app?.PersonId ?? "";
        }
    
        // ═══════════ v2 快照存档接口 ═══════════
        // ---- batch10-A1: schema v5 section ----
        public sealed class CorruptionState { public string Stats; }
        public string SectionName => "corruption";
        public Dictionary<string, CorruptionState> CaptureStates()
        {
            var d = new Dictionary<string, CorruptionState>();
            if (World.world?.kingdoms?.list == null) return d;
            foreach (Kingdom k in World.world.kingdoms.list)
            {
                if (k == null || !k.isAlive()) continue;
                string v = KingdomDataExt.GetStr(k, KEY_STATS, "");
                if (!string.IsNullOrEmpty(v)) d[k.id.ToString()] = new CorruptionState { Stats = v };
            }
            return d;
        }
        public void ApplyStates(Dictionary<string, CorruptionState> data)
        {
            if (data == null) return;
            foreach (var kv in data)
            {
                Kingdom k = KingdomDataExt.FindKingdomById(kv.Key);
                if (k == null || !k.isAlive() || kv.Value == null) continue;
                KingdomDataExt.SetStr(k, KEY_STATS, kv.Value.Stats ?? "");
            }
        }

        public void MigrateFromCustomData() { }
        public void ResetRuntime() { }

}
}
