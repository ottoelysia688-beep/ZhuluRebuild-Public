using System;
using System.Collections.Generic;
using ZhuluRebuild.Core;
using ZhuluRebuild.Adapters;
using ZhuluRebuild.Domain;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Presentation.ViewModels
{
    public class StateCardVm
    {
        public string Title { get; set; }
        public string GradeLabel { get; set; }
        public string NumericDisplay { get; set; }
        public string Trend { get; set; }
        public string MainReason { get; set; }
        public string DetailedFormula { get; set; }
    }

    public class KingdomStateVm
    {
        public string KingdomId { get; set; }
        public string KingdomName { get; set; }        // 格式化后的国家名称，如《大唐朝-宗主国》或《赵郡-附庸》
        public string KingdomTierName { get; set; }   // 皇朝 / 王国 / 郡 / 邑
        public string RulerTitle { get; set; }         // 皇帝 / 王 / 郡守 / 邑君
        public string HeirTitle { get; set; }          // 太子 / 世子 / 少郡守 / 少邑君
        public string PowerBandName { get; set; }      // 极权郡县 / 君主集权 / 中央主导 / 贵族共治 / 王室衰微
        public float PolicyPowerMult { get; set; }     // 0.3x ~ 1.6x 政策效能倍率
        public string ActiveSubTab { get; set; } = "current"; 
        public string SummarySentence { get; set; }

        public StateCardVm DynastyStageCard { get; set; }
        public StateCardVm PowerCard { get; set; }
        public StateCardVm MandateCard { get; set; }
        public StateCardVm StabilityCard { get; set; }
        public StateCardVm TreasuryCard { get; set; }

        public string RulerName { get; set; }
        public int CityCount { get; set; }
        public int Population { get; set; }
        public int Military { get; set; }
        public int VacantOfficeCount { get; set; }

        public List<string> RecentMajorEvents { get; set; } = new List<string>();

        public static KingdomStateVm CreateFromAdapterOrMock(string kingdomId)
        {
            try
            {
                var kFact = WorldBoxReadOnlyAdapter.Instance.CaptureKingdomSnapshot(kingdomId);
                if (kFact == null)
                {
                    return CreateMockSample(kingdomId, "当前国家");
                }

                // 2026-10-06 移出：TitleSuffixPatches.RebuildSuffixes()（君主名后缀重建）——见 docs/移出代码存档-称号后缀.md

                int effCities = KingdomExt.GetEffectiveCities(kingdomId, kFact.CityCount);
                string formattedName = KingdomExt.GetFormattedName(kingdomId, kFact.KingdomName, kFact.CityCount);

                // 2026-08-18 口径统一：tierName/kingTitle/heirTitle 全部走特质权威
                // （RegimeManager Kingdom 版：君主爵位特质优先→国力点回退，叛军/天命感知）——
                // 原城数近似与特质/国力点体系阈值互不一致，"公国国王称伯君"错位源于此。
                Kingdom kObj = KingdomDataExt.FindKingdomById(kingdomId);
                bool nomadic = kObj != null && KingdomCivilizationService.Instance.IsNomadic(kObj);
                string tierName = RegimeManager.GetTierName(kObj);
                string kingTitle = RegimeManager.GetKingTitle(kObj);
                string heirTitle = RegimeManager.GetHeirTitle(kObj);

                int powerVal = KingdomExt.GetPowerValue(kingdomId);
                string bandName = RegimeManager.GetBandName(powerVal);
                // 阶段2N W6：显示与实际生效一致（政体效能温和映射已接入 PolicyManager 颁行放缩）
                float policyMult = RegimeManager.GetPolicyEfficiencyFactor(powerVal);

                var mData = MandateManager.Instance.GetMandateData(kingdomId);

                // 国库账本卡片
                var tre = TreasuryManager.Instance.GetReport(kingdomId);
                long treBalance = tre.Balance;
                string treGrade = tre.Treasury < 0 ? "国库亏空" : (TreasuryManager.IsRich(tre.Treasury, tre.Income) ? "府库充盈" : "量入为出");
                string treTrend = treBalance >= 0 ? $"↑ 年盈余 +{treBalance}" : $"↓ 年亏空 {treBalance}";
                // 阶段2S-5：收支明细补军费；欠款/朝贡单独成段（朝贡=宗主实收口径，不计岁入）
                // P2-30 收支速览行补全：宫廷用度与净额（设计文档 2.6 原案落地）
                string treMain = $"岁入 {tre.Income} - 俸禄 {tre.Salaries} - 军费 {tre.WarCost} - 宫廷 {tre.CourtCost} - 维护 {tre.PolicyUpkeep} = 净 {tre.Balance}";
                if (tre.WarDebt > 0) treMain += $" · 赔款欠 {tre.WarDebt} 金";
                if (tre.DeficitYears > 0) treMain += $" · 连续亏空 {tre.DeficitYears} 年";
                string treDetail = $"岁入 = 人口 × 税率 × 户部官效 (x{tre.RevenueEfficiency:F2}) · 军费 = 常备兵饷 + 战时加成（每场战争，至多3场）";
                try
                {
                    Kingdom treK = KingdomDataExt.FindKingdomById(kingdomId);
                    var treVassals = treK != null ? VassalManager.Instance.GetVassals(treK) : null;
                    if (treVassals != null && treVassals.Count > 0)
                    {
                        long tributeSum = 0;
                        foreach (var v in treVassals) { if (v != null && v.isAlive()) tributeSum += ZhuluDiplomacyService.ComputeTribute(v); }
                        treDetail += $" · 朝贡 {tributeSum} 金/年（另计）";
                    }
                }
                catch { }
                var treasuryCard = new StateCardVm
                {
                    Title = "国库财政",
                    GradeLabel = treGrade,
                    NumericDisplay = $"{tre.Treasury} 金",
                    Trend = treTrend,
                    MainReason = treMain,
                    DetailedFormula = treDetail
                };

                return new KingdomStateVm
                {
                    KingdomId = kFact.KingdomId,
                    KingdomName = formattedName,
                    KingdomTierName = tierName,
                    RulerTitle = kingTitle,
                    HeirTitle = heirTitle,
                    PowerBandName = bandName,
                    PolicyPowerMult = policyMult,
                    ActiveSubTab = "current",
                    SummarySentence = BuildSummarySentence(formattedName, tierName, kingTitle, bandName, kFact, mData, effCities),

                    DynastyStageCard = new StateCardVm
                    {
                        Title = "爵位等阶",
                        GradeLabel = tierName,
                        NumericDisplay = $"【{kingTitle}】",
                        Trend = BuildTierTrend(tierName, kingTitle), // 阶段2M：按等级动态（降等阈值——不再"永不降级"）
                        // 2026-08-18 文本修正：爵位以国力点定等（城×6+人口÷40，五等+天命），
                        // 旧"据X城之地爵列某国"城数口径已废；各等配先秦爵制语境描述。
                        MainReason = nomadic
                            ? $"游牧等级按国力判定（国力 {ZhuluTierService.GetPowerPoints(kObj)} 点 · {effCities} 城，位列【{tierName}】）"
                            : (tierName == "天朝" ? ZhuluTierService.GetTierDesc(ZhuluTierService.TIER_TIANZI)
                                : $"{ZhuluTierService.GetTierDesc(ZhuluTierService.GetKingdomTierId(kObj))}（国力 {ZhuluTierService.GetPowerPoints(kObj)} 点 · {effCities} 城，爵列【{tierName}】）"),
                        DetailedFormula = nomadic
                            ? $"国号《{formattedName}》 · 国力点 = 城数×6 + 人口÷40（大汗庭55/汗国35/部盟20/大部10）"
                            : $"国号《{formattedName}》 · 国力点 = 城数×6 + 人口÷40（公55/侯35/伯20/子10）"
                    },
                    PowerCard = new StateCardVm
                    {
                        Title = "中央权力",
                        GradeLabel = bandName,
                        NumericDisplay = $"{powerVal}",
                        Trend = $"效能 {policyMult:F2}x",
                        MainReason = $"{kFact.RulerName} 君临朝纲", // 阶段2Q：精简（政体名在等级列，防重叠期文本过长）
                        DetailedFormula = BuildPowerDetail(kFact.KingdomId, powerVal, policyMult)
                    },
                    MandateCard = BuildMandateCard(kFact.KingdomId, mData),
                    TreasuryCard = treasuryCard,
                    StabilityCard = new StateCardVm
                    {
                        Title = "社稷稳定",
                        GradeLabel = StabilityManager.Instance.GetStabilityGrade(StabilityManager.Instance.GetStability(kFact.KingdomId)),
                        NumericDisplay = $"{StabilityManager.Instance.GetStability(kFact.KingdomId):F0} / 100",
                        Trend = StabilityManager.Instance.GetStability(kFact.KingdomId) >= 60f ? "→ 秩序良好" : "↑ 需关注",
                        MainReason = $"民心 · 财政 · 官席 · {(nomadic ? "长生天眷" : "天命")} · 党争 综合而定",
                        DetailedFormula = $"受忠诚/财政/官席/{(nomadic ? "天眷" : "天命")}/战争综合影响"
                    },

                    RulerName = $"{kingTitle} · {kFact.RulerName}",
                    CityCount = kFact.CityCount,
                    Population = kFact.TotalPopulation,
                    Military = kFact.TotalMilitary,
                    VacantOfficeCount = 0,
                    RecentMajorEvents = new List<string>
                    {
                        $"【{tierName}】之国《{formattedName}》，据 {effCities} 城",
                        $"君主尊【{kingTitle}】，储位称【{heirTitle}】",
                        $"朝政 {bandName}，权力 {powerVal} 分，政体效能 {policyMult:F2}x"
                    }
                };
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError($"[KingdomStateVm] 构造 {kingdomId} ViewModel 异常，退回 Mock 数据", ex);
                return CreateMockSample(kingdomId, "当前国家");
            }
        }

        /// <summary>阶段2P：天命观察卡（天命国显示本朝世系——朝代/历N帝/年号；非天命国显示空悬一句话）。</summary>
        private static StateCardVm BuildMandateCard(string kingdomId, MandateData mData)
        {
            string mainReason;
            try
            {
                Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
                bool nomadic = k != null && KingdomCivilizationService.Instance.IsNomadic(k);
                if (nomadic)
                {
                    // 阶段3C：数值/阶段取长生天眷轨道（原误用东方天命数据——游牧国恒显50分）
                    float nv = NomadicMandateManager.Instance.GetMandateValue(kingdomId);
                    string ng = NomadicMandateManager.Instance.GetMandateGrade(nv);
                    bool holder = NomadicMandateManager.Instance.IsMandateHolder(kingdomId);
                    mainReason = holder ? $"{k.name}建立王庭，君主称天可汗" : "长生天眷未归于斯";
                    return new StateCardVm
                    {
                        Title = "长生天眷",
                        GradeLabel = ng,
                        NumericDisplay = $"{nv:F0} / 100",
                        Trend = "→ 天眷传承",
                        MainReason = mainReason,
                        DetailedFormula = $"长生天眷阶段：{ng}"
                    };
                }
                bool isHolder = OrthodoxMandateManager.Instance.IsMandateHolder(kingdomId);
                if (isHolder)
                {
                    string dyn = OrthodoxMandateManager.Instance.GetDynastyName(kingdomId);
                    string era = OrthodoxMandateManager.Instance.GetEraName(kingdomId);
                    var hist = OrthodoxMandateManager.Instance.GetHistory();
                    var curDyn = hist?.FindLast(d => d.EndYear == -1 && d.KingdomId == kingdomId);
                    int empN = curDyn?.Emperors.Count ?? 0;
                    mainReason = $"{(string.IsNullOrEmpty(dyn) ? "本朝" : dyn + "朝")} · 历{empN}帝 · 年号「{era}」";
                }
                else
                {
                    mainReason = "天命未归于斯，养望以待时";
                }
            }
            catch { mainReason = $"天命 {mData.Value:F0}"; }
            return new StateCardVm
            {
                Title = "天命观察",
                GradeLabel = mData.StatusText,
                NumericDisplay = $"{mData.Value:F0} / 100",
                Trend = "→ 正统编年",
                MainReason = mainReason,
                DetailedFormula = $"天命阶段：{mData.StatusText}"
            };
        }

        public static KingdomStateVm CreateMockSample(string kingdomId = "k_1", string kingdomName = "齐国")
        {
            return new KingdomStateVm
            {
                KingdomId = kingdomId,
                KingdomName = kingdomName + "公国",
                KingdomTierName = "公国",
                RulerTitle = "大公",
                HeirTitle = "太子",
                PowerBandName = "中央主导",
                PolicyPowerMult = 1.0f,
                ActiveSubTab = "current",
                SummarySentence = "“公国朝局渐备，【大公】与【中央主导】维系天命秩序。”",
                DynastyStageCard = new StateCardVm
                {
                    Title = "国家位阶",
                    GradeLabel = "公国",
                    NumericDisplay = "【大公】位阶",
                    Trend = "→ 国力跌破 55 降为侯",
                    MainReason = "有效领土 12 城，尊号《公国》",
                    DetailedFormula = $"国名后缀: 《{kingdomName}公国》"
                },
                PowerCard = new StateCardVm
                {
                    Title = "中央权力",
                    GradeLabel = "中央主导",
                    NumericDisplay = "58 / 100",
                    Trend = "效能 1.0x",
                    MainReason = "【大公】国君 执掌朝政",
                    DetailedFormula = "国策效能倍率 1.0x · 继承 嫡长子继承"
                },
                MandateCard = new StateCardVm
                {
                    Title = "天命观察",
                    GradeLabel = "秩序安定",
                    NumericDisplay = "70 / 100",
                    Trend = "→ 天命观察",
                    MainReason = "天命 70 分 · 秩序安定",
                    DetailedFormula = "天命阶段：秩序安定"
                },
                StabilityCard = new StateCardVm
                {
                    Title = "社稷稳定",
                    GradeLabel = "政局安稳",
                    NumericDisplay = "65 / 100",
                    Trend = "→ 秩序良好",
                    MainReason = "稳定度 65 · 政局安稳",
                    DetailedFormula = "受忠诚/财政/官席/天命/战争综合影响"
                },
                RulerName = "大公 · 国君",
                CityCount = 12,
                Population = 350,
                Military = 80,
                VacantOfficeCount = 0,
                RecentMajorEvents = new List<string>
                {
                    $"已拉通四阶国家位阶: 【公国】《{kingdomName}公国》",
                    "君主尊称: 【大公】，储君尊称: 【太子】",
                    "中央权力 58 分 · 中央主导 · 国策效能 1.0x"
                }
            };
        }

        private static string BuildPowerDetail(string kId, int powerVal, float policyMult)
        {
            string baseInfo = $"国策效能 {policyMult:F2}x（政体实装） · 继承 {RegimeManager.GetInheritanceMethod(powerVal)}";
            var (offId, name, pow) = CorruptionManager.Instance.GetTopPowerfulOfficial(kId);
            if (offId != null && pow >= 40f)
            {
                // 阶段2N W11：官名按王国等级动态取（原单参重载默认天命制——非天命国误显"丞相"）
                string title;
                try
                {
                    var k = Persistence.KingdomDataExt.FindKingdomById(kId);
                    int rawCities = (k != null && k.cities != null) ? k.cities.Count : 1;
                    int effCities = KingdomExt.GetEffectiveCities(kId, rawCities);
                    title = OfficeManager.GetOfficeTitleForKingdom(offId, k);
                }
                catch { title = OfficeManager.GetOfficeTitle(offId); }
                baseInfo += $" · 最大权臣: {title} {name} (权势{pow:F0})";
            }
            return baseInfo;
        }

        /// <summary>阶段2M：爵位趋势文本（按等级——降等阈值/天命特殊——不再"永不降级"）。</summary>
        private static string BuildTierTrend(string tierName, string kingTitle)
        {
            try
            {
                if (tierName == "天朝") return "→ 天命失守则失位（礼崩乐坏）";
                if (tierName == "王庭") return "→ 失去长生天眷则降回国力对应等级";
                if (tierName == "部族") return "→ 国力达 10 可晋升【大部】";
                if (tierName == "大部") return "→ 国力跌破 10 降为部族";
                if (tierName == "部盟") return "→ 国力跌破 20 降为大部";
                if (tierName == "汗国") return "→ 国力跌破 35 降为部盟";
                if (tierName == "大汗庭") return "→ 国力跌破 55 降为汗国";
                if (tierName == "邑") return "→ 国力达标可晋升【子】";
                if (tierName == "子") return "→ 国力跌破 10 降为邑";
                if (tierName == "伯") return "→ 国力跌破 20 降为子";
                if (tierName == "侯") return "→ 国力跌破 35 降为伯";
                if (tierName == "公") return "→ 国力跌破 55 降为侯";
                return "→ 尊号随国力升降";
            }
            catch { return "→ 尊号随国力升降"; }
        }

        private static string BuildSummarySentence(string name, string tier, string kingTitle, string band, KingdomFactSnapshot kFact, MandateData mData, int effCities)
        {
            float stability = StabilityManager.Instance.GetStability(kFact.KingdomId);
            string stabGrade = StabilityManager.Instance.GetStabilityGrade(stability);
            string mandateGrade = mData.StatusText;

            string rulerPart = $"【{kingTitle}】{kFact.RulerName} 秉政";
            Kingdom stateK = KingdomDataExt.FindKingdomById(kFact.KingdomId);
            bool nomadic = stateK != null && KingdomCivilizationService.Instance.IsNomadic(stateK);
            string statePart = $"{(nomadic ? "天眷" : "天命")}{mandateGrade}，{stabGrade}";
            string scalePart = effCities >= 6 ? $"据{effCities}城，【{band}】之局" : $"偏安{effCities}城，草创未备";

            return $"《{name}》{scalePart}。{rulerPart}，{statePart}。";
        }
    }
}
