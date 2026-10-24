using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ZhuluRebuild.Core;
using ZhuluRebuild.Naming;
using ZhuluRebuild.Gameplay;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Domain
{
    /// <summary>
    /// 长生天眷独立轨道。初始值、结算公式、等级效果、8 年保护、连续 50 年不足 6 城失眷、
    /// 国力 40/国祚 15/威望 8 的资格，均与东方天命现行数值一一对应。
    /// </summary>
    public sealed class NomadicMandateManager : ISnapshotSection<NomadicMandateManager.NomadicState>
    {
        public static NomadicMandateManager Instance { get; } = new NomadicMandateManager();
        private const int ProtectYears = 8;
        private const int BelowSixYearsLimit = 50;

        private sealed class State
        {
            public float Value = 60f;
            public int BelowSixSince;
            public int ProtectUntil;
            public float Trend0 = -1f;
            public float Trend1 = -1f;
            public float Trend2 = -1f;
            public int LastPopulation;
            public string Detail = "";
        }

        private readonly Dictionary<string, State> _states = new Dictionary<string, State>();
        private readonly List<string> _history = new List<string>();
        private string _holderId;
        private int _lastCalcYear = -1;
        private int _lastTransferYear = -1;

        private NomadicMandateManager() { }

        public string GetMandateHolderId()
        {
            if (string.IsNullOrEmpty(_holderId)) return null;
            Kingdom k = KingdomDataExt.FindKingdomById(_holderId);
            if (k == null || !k.isAlive()) return _holderId; // 年度转移负责封口
            return _holderId;
        }

        public bool IsMandateHolder(string kingdomId) => !string.IsNullOrEmpty(kingdomId) && kingdomId == _holderId;
        public float GetMandateValue(string kingdomId) => _states.TryGetValue(kingdomId ?? "", out var s) ? s.Value : 0f;
        public string GetDetail(string kingdomId) => _states.TryGetValue(kingdomId ?? "", out var s) ? s.Detail : "";
        public List<string> GetHistory() => new List<string>(_history);

        // ── 阶段3H：王庭/可汗结构化记录（与东方朝代/皇帝同构，草原式文本）──
        public sealed class KhanRecord
        {
            public string Name = ""; public string Era = "";
            public int StartYear = -1, EndYear = -1;
            public int StartCities, StartArmy, EndCities, EndArmy;
            public string Verdict = "";
            // 阶段3H-5：即位/盖棺快照扩展（-1=未采集旧档，UI 自动省略）
            public int StartPop = -1, EndPop = -1;
            public long StartTreasury = -1, EndTreasury = -1;
            public int StartVassals = -1, EndVassals = -1;
            public float StartMandate = -1f, EndMandate = -1f;
            public float StartStability = -1f, EndStability = -1f;
            public int WarCount = -1, PolicyCount = -1;
            // 阶段3H-9：草原史评（盖棺时生成——九等第+汗号）
            public string RankLabel = "";   // 等第称号（天可汗之业/守成之汗…）
            public string KhanTitle = "";    // 汗号（对应东方庙号：太祖汗/末汗…）
            public string VerdictText = "";  // 盖棺史评句（多维功过+结语）
            // 阶段3H-10：在位期间灭掉东方天命国（盖棺回溯史册判定）
            public int SlewHolder;
            public string SlewHolderNames = "";
        }
        public sealed class CourtRecord
        {
            public string Court = ""; public string KingdomId = "";
            public int StartYear = -1, EndYear = -1; public string EndReason = "";
            public List<KhanRecord> Khans = new List<KhanRecord>();
        }
        private readonly List<CourtRecord> _courts = new List<CourtRecord>();
        public List<CourtRecord> GetCourts() => new List<CourtRecord>(_courts);

        private static string CleanKhanName(string raw)
        {
            string n = Adapters.TitleSuffixPatches.StripKingSuffix(raw ?? "").Trim();
            return n;
        }
        private static KhanRecord OpenKhan(string khanName, string era, int year, Kingdom k)
        {
            var kr = new KhanRecord { Name = CleanKhanName(khanName), Era = era ?? "", StartYear = year };
            try
            {
                if (k != null)
                {
                    kr.StartCities = k.cities?.Count ?? 0;
                    kr.StartArmy = k.countTotalWarriors();
                    // 阶段3H-5：即汗位全维起卷（与东方皇帝即位快照同口径）
                    string kid = k.id.ToString();
                    kr.StartPop = k.units != null ? k.units.Count : 0;
                    try { kr.StartTreasury = TreasuryManager.Instance.GetReport(kid).Treasury; } catch { }
                    kr.StartVassals = CountVassalsOf(kid);
                    kr.StartMandate = Instance.GetMandateValue(kid);
                    try { kr.StartStability = StabilityManager.Instance.GetStability(kid); } catch { }
                }
            } catch { }
            return kr;
        }

        private static int CountVassalsOf(string kingdomId)
        {
            try
            {
                Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
                if (k == null) return 0;
                int n = 0;
                if (World.world?.kingdoms?.list != null)
                    foreach (Kingdom other in World.world.kingdoms.list)
                        if (other != null && other.isAlive() && VassalManager.Instance.GetSuzerain(other) == k) n++;
                return n;
            }
            catch { return 0; }
        }
        private static void CloseKhan(KhanRecord kr, int year, Kingdom k)
        {
            if (kr == null) return;
            kr.EndYear = year;
            try
            {
                if (k != null)
                {
                    kr.EndCities = k.cities?.Count ?? 0;
                    kr.EndArmy = k.countTotalWarriors();
                    string kid = k.id.ToString();
                    kr.EndPop = k.units != null ? k.units.Count : 0;
                    try { kr.EndTreasury = TreasuryManager.Instance.GetReport(kid).Treasury; } catch { }
                    kr.EndVassals = CountVassalsOf(kid);
                    kr.EndMandate = Instance.GetMandateValue(kid);
                    try { kr.EndStability = StabilityManager.Instance.GetStability(kid); } catch { }
                    // 阶段3H-5：在位征伐/施政回溯（王国史册按年段统计——与东方皇帝功过同口径）
                    try
                    {
                        var chronicle = ChronicleManager.Instance.GetKingdomChronicle(kid);
                        int wars = 0, pols = 0;
                        if (chronicle != null)
                        {
                            foreach (var ce in chronicle)
                            {
                                if (ce.Year < kr.StartYear || ce.Year > year) continue;
                                if (ce.Type == ChronicleEventType.WarDeclared) wars++;
                                else if (ce.Type == ChronicleEventType.PolicyEnacted) pols++;
                            }
                        }
                        kr.WarCount = wars;
                        kr.PolicyCount = pols;
                    }
                    catch { kr.WarCount = -1; kr.PolicyCount = -1; }
                    // 阶段3H-10：覆灭王庭判定——在位年间灭掉东方天命持有国
                    try { DetectSlewHolder(kr, kid, k, year); } catch { }
                }
            } catch { }
            kr.Verdict = ComputeVerdict(kr.StartYear, year, kr.EndCities - kr.StartCities, kr.EndArmy - kr.StartArmy);
            // 阶段3H-9：盖棺史评（等第+汗号+评语）
            var (rk, lb, txt) = BuildSteppeVerdict(kr);
            kr.RankLabel = lb;
            kr.VerdictText = txt;
            kr.KhanTitle = rk >= 0 ? SteppeKhanTitle(kr, rk) : "";
        }

        private static string ComputeVerdict(int startYear, int endYear, int dCity, int dArmy)
        {
            int reign = endYear - startYear;
            if (reign <= 4 && dCity < 0) return "短祚失蹄";
            if (dCity >= 3) return dCity >= 6 ? "天可汗之业" : "拓土之汗";
            if (dCity > 0) return "扩帐之汗";
            if (dCity < 0) return "失蹄之汗";
            if (dArmy >= 30) return "控弦十万";
            if (reign >= 30) return "久牧之汗";
            return "守成之汗";
        }

        /// <summary>阶段3H-10：覆灭王庭判定——扫天下王国史册 KingdomFallen 条目（在位年段内），
        /// 亡者名单含本国名 且 被灭国当年持有东方天命。</summary>
        private static void DetectSlewHolder(KhanRecord kr, string kingdomId, Kingdom k, int closeYear)
        {
            if (k == null || World.world?.kingdoms?.list == null) return;
            string myName = k.name ?? "";
            if (string.IsNullOrEmpty(myName)) return;
            var slain = new List<string>();
            foreach (Kingdom other in World.world.kingdoms.list)
            {
                if (other == null || !other.isAlive()) continue;
                string oId = other.id.ToString();
                var chron = ChronicleManager.Instance.GetKingdomChronicle(oId);
                if (chron == null) continue;
                foreach (var c in chron)
                {
                    if (c.Type != ChronicleEventType.KingdomFallen) continue;
                    if (c.Year < kr.StartYear || c.Year > closeYear) continue;
                    string detail = c.ContentText ?? "";
                    if (!detail.Contains(myName)) continue;
                    // 被灭国当年持有东方天命（本轨是游牧——对面即东方轨道）
                    bool easternHeldAt = false;
                    try
                    {
                        foreach (var d in OrthodoxMandateManager.Instance.GetHistory())
                            if (d.Dynasty != "空悬" && d.StartYear <= c.Year && (d.EndYear == -1 || d.EndYear >= c.Year))
                            { easternHeldAt = true; break; }
                    }
                    catch { }
                    if (easternHeldAt)
                    {
                        string vn = string.IsNullOrEmpty(c.PrimaryPersonName) ? "敌国" : c.PrimaryPersonName;
                        if (!slain.Contains(vn)) slain.Add(vn);
                    }
                }
            }
            if (slain.Count > 0)
            {
                kr.SlewHolder = 1;
                kr.SlewHolderNames = string.Join("、", slain);
            }
        }

        // ═══════════ 阶段3H-9：草原史评引擎（镜像东方谥法——九等第+细分称号+多维功过+结语）═══════════

        /// <summary>草原史评总入口：返回 (rank, 等第称号, 评语句)。rank -1=无法评价（旧档无数据）。</summary>
        public static (int rank, string label, string verdict) BuildSteppeVerdict(KhanRecord k)
        {
            if (k == null || k.EndYear < 0) return (-1, "", "");
            if (k.StartCities < 0 && k.EndCities < 0) return (-1, "", "");
            int score = ScoreSteppeReign(k);
            int fault = CountSteppeFaults(k);

            string[] ranks = { "天可汗之业", "雄主之汗", "英明之汗", "贤汗", "守成之汗", "中平之汗", "平庸之汗", "失政之汗", "丧乱之汗" };
            int rank; string label;
            if (fault >= 3 || score <= -9) { rank = 8; label = SteppeFlavorLabel(k, 8); }
            else if (fault == 2 || score <= -6) { rank = 7; label = SteppeFlavorLabel(k, 7); }
            else if (fault == 1 || score <= -3) { rank = 6; label = SteppeFlavorLabel(k, 6); }
            else if (score >= 10) { rank = 0; label = SteppeFlavorLabel(k, 0); }
            else if (score >= 7) { rank = 1; label = SteppeFlavorLabel(k, 1); }
            else if (score >= 4) { rank = 2; label = SteppeFlavorLabel(k, 2); }
            else if (score >= 2) { rank = 3; label = SteppeFlavorLabel(k, 3); }
            else if (score >= 0) { rank = 4; label = SteppeFlavorLabel(k, 4); }
            else { rank = 5; label = SteppeFlavorLabel(k, 5); }

            int reignYears = k.EndYear - k.StartYear;
            var merits = SteppeMeritPhrases(k);
            var faults = SteppeFaultPhrases(k);
            if (k.SlewHolder == 1)
            {
                string slainTxt = string.IsNullOrEmpty(k.SlewHolderNames) ? "南朝" : k.SlewHolderNames;
                merits.Insert(0, $"铁蹄踏破{slainTxt}之天命，克成草原不世之功");
            }
            string meritText = merits.Count > 0 ? string.Join("，", merits.Count > 2 ? merits.GetRange(0, 2) : merits) : "";
            string faultText = faults.Count > 0 ? string.Join("；", faults.Count > 2 ? faults.GetRange(0, 2) : faults) : "";
            string closing = SteppeClosingPhrase(k, rank, reignYears);
            string verdict;
            if (meritText.Length == 0 && faultText.Length == 0)
                verdict = $"{SteppeOpening(k)}牧国{CnNum(reignYears)}年。{closing}";
            else if (rank >= 6)
                verdict = $"{SteppeOpening(k)}牧国{CnNum(reignYears)}年。{meritText}，然{faultText}。{closing}";
            else if (faultText.Length == 0)
                verdict = $"{SteppeOpening(k)}牧国{CnNum(reignYears)}年。{meritText}。{closing}";
            else
                verdict = $"{SteppeOpening(k)}牧国{CnNum(reignYears)}年。{meritText}；{faultText}。{closing}";
            return (rank, label, verdict);
        }

        /// <summary>草原功业评分（镜像东方 ScoreReign 权重，维度换成 帐落/控弦/部众/府库/天眷/附庸）。</summary>
        private static int ScoreSteppeReign(KhanRecord k)
        {
            int dCity = k.EndCities - k.StartCities;
            int dArmy = k.EndArmy - k.StartArmy;
            long dTreas = k.StartTreasury >= 0 && k.EndTreasury >= 0 ? k.EndTreasury - k.StartTreasury : 0;
            float dMandate = k.StartMandate >= 0f && k.EndMandate >= 0f ? k.EndMandate - k.StartMandate : 0f;
            float dStab = k.StartStability >= 0f && k.EndStability >= 0f ? k.EndStability - k.StartStability : 0f;
            int popPct = 0;
            if (k.StartPop > 0 && k.EndPop >= 0) popPct = (int)Mathf.Round((k.EndPop - k.StartPop) * 100f / k.StartPop);
            int reignYears = k.EndYear - k.StartYear;
            bool mandateCollected = k.StartMandate >= 0f || k.EndMandate >= 0f;

            int s = 0;
            // 功（拓帐 2+2 / 控弦 2 / 部众 2 / 天眷 3 / 附庸 1 / 府库 1 / 征伐 2 / 施政 2）
            if (dCity >= 2) s += 2; if (dCity >= 5) s += 2;
            if (dArmy >= 30) s += 2; if (dArmy >= 60) s += 1;
            if (popPct >= 30) s += 2;
            if (mandateCollected && dMandate >= 8f) s += 3;
            if (dStab >= 8f) s += 2;
            if (k.EndVassals >= 0 && k.StartVassals >= 0 && k.EndVassals - k.StartVassals >= 1) s += 1;
            if (k.WarCount >= 3 && dCity > 0) s += 2;
            if (k.PolicyCount >= 4 && dStab > 0f) s += 2;
            if (dTreas >= 500) s += 1;
            // 过（失帐 2+2 / 丧师 2 / 部众凋 2 / 天眷坠 3 / 部离 2 / 府空 1 / 怠政 1）
            if (dCity <= -2) s -= 2; if (dCity <= -5) s -= 2;
            if (dArmy <= -30) s -= 2;
            if (popPct <= -20) s -= 2;
            if (mandateCollected && (dMandate <= -8f || k.EndMandate < 30f)) s -= 3;
            if (dStab <= -8f) s -= 2;
            if (k.EndVassals >= 0 && k.StartVassals >= 0 && k.EndVassals - k.StartVassals <= -1) s -= 1;
            if (k.WarCount >= 3 && dCity < 0) s -= 2;
            if (k.PolicyCount == 0 && dStab < 0f) s -= 1;
            if (dTreas <= -500) s -= 1;
            // 年限修正：短祚正分减半；久牧加成
            if (reignYears >= 1 && reignYears <= 4 && s > 0) s /= 2;
            if (reignYears >= 40) s += 2;
            if (k.SlewHolder == 1) s += 4;   // 阶段3H-10：覆灭王庭（草原不世之功）
            return s;
        }

        private static int CountSteppeFaults(KhanRecord k)
        {
            int f = 0;
            int dCity = k.EndCities - k.StartCities;
            if (dCity <= -2) f++;
            if (k.StartPop > 0 && k.EndPop >= 0 && (k.EndPop - k.StartPop) * 100f / k.StartPop <= -20f) f++;
            if (k.StartMandate >= 0f || k.EndMandate >= 0f)
            {
                float dM = (k.StartMandate >= 0f && k.EndMandate >= 0f) ? k.EndMandate - k.StartMandate : 0f;
                if (dM <= -8f || k.EndMandate < 30f) f++;
            }
            if (k.StartStability >= 0f && k.EndStability >= 0f && k.EndStability - k.StartStability <= -8f) f++;
            if (k.EndArmy - k.StartArmy <= -30) f++;
            return f;
        }

        /// <summary>草原等第细分称号（镜像东方 FlavorLabel——按拓帐/控弦/久牧/施政细分）。</summary>
        private static string SteppeFlavorLabel(KhanRecord k, int rank)
        {
            int dCity = k.EndCities - k.StartCities;
            int dArmy = k.EndArmy - k.StartArmy;
            int reignYears = k.EndYear - k.StartYear;
            switch (rank)
            {
                case 0: // 天可汗之业
                    if (dCity >= 5) return "混一苍狼之主";
                    if (dArmy >= 60) return "控弦百万之汗";
                    if (dCity >= 3) return "拓土天可汗";
                    if (k.PolicyCount >= 6) return "立扎撒之汗";
                    return "天可汗之业";
                case 1: // 雄主之汗
                    if (dCity >= 3) return "鹰扬雄主";
                    if (dArmy >= 30) return "铁骑雄主";
                    if (reignYears >= 30) return "久牧雄主";
                    return "雄主之汗";
                case 2: // 英明之汗
                    if (dCity >= 2) return "拓帐英汗";
                    if (k.PolicyCount >= 4) return "整肃英汗";
                    if (reignYears >= 20) return "持重英汗";
                    return "英明之汗";
                case 3: // 贤汗
                    if (k.PolicyCount >= 4) return "立法贤汗";
                    if (dCity >= 1) return "拓边贤汗";
                    if (dArmy > 0) return "练兵贤汗";
                    return "贤汗";
                case 4: // 守成之汗
                    if (reignYears >= 30) return "久牧守成";
                    if (dArmy > 0) return "励武守成";
                    return "守成之汗";
                case 5: // 中平之汗
                    if (dCity < 0) return "蹙帐中汗";
                    if (reignYears >= 30) return "苟安中汗";
                    return "中平之汗";
                case 6: // 平庸之汗
                    if (reignYears <= 4) return "短祚之汗";
                    return "平庸之汗";
                case 7: // 失政之汗
                    if (dCity < 0) return "失帐之汗";
                    if (k.EndArmy - k.StartArmy <= -30) return "丧师之汗";
                    return "失政之汗";
                default: // 丧乱之汗
                    if (k.EndMandate >= 0f && k.EndMandate < 30f) return "天眷弃之";
                    if (dCity <= -5) return "部落离散";
                    return "丧乱之汗";
            }
        }

        private static List<string> SteppeMeritPhrases(KhanRecord k)
        {
            var list = new List<string>();
            int dCity = k.EndCities - k.StartCities;
            int dArmy = k.EndArmy - k.StartArmy;
            int reignYears = k.EndYear - k.StartYear;
            if (dCity >= 5) list.Add("连年开帐，牧地极于漠北");
            else if (dCity >= 2) list.Add("拓帐四方，诸部来附");
            if (dArmy >= 60) list.Add("控弦之士倍增，兵锋大振");
            else if (dArmy >= 30) list.Add("厉兵秣马，控弦日盛");
            if (k.StartPop > 0 && k.EndPop > 0 && (k.EndPop - k.StartPop) * 100f / k.StartPop >= 30f)
                list.Add("部众滋繁，帐落相望");
            if (k.StartMandate >= 0f && k.EndMandate >= 0f && k.EndMandate - k.StartMandate >= 8f)
                list.Add("天眷日隆，草原归心");
            if (k.StartStability >= 0f && k.EndStability >= 0f && k.EndStability - k.StartStability >= 8f)
                list.Add("诸部辑睦，牧野晏然");
            if (k.EndVassals > k.StartVassals && k.StartVassals >= 0)
                list.Add("远方部落纳贡称臣");
            if (k.WarCount >= 3 && dCity > 0) list.Add("屡兴挞伐，威行诸部");
            if (k.PolicyCount >= 4) list.Add("颁行政令，汗庭整肃");
            if (k.StartTreasury >= 0 && k.EndTreasury >= 0 && k.EndTreasury - k.StartTreasury >= 500)
                list.Add("牛羊孳息，府库充盈");
            if (reignYears >= 40) list.Add("御极之久，草原罕见");
            return list;
        }

        private static List<string> SteppeFaultPhrases(KhanRecord k)
        {
            var list = new List<string>();
            int dCity = k.EndCities - k.StartCities;
            int dArmy = k.EndArmy - k.StartArmy;
            int reignYears = k.EndYear - k.StartYear;
            if (dCity <= -5) list.Add("帐落大片沦失，牧地蹙缩");
            else if (dCity <= -2) list.Add("失帐于邻部，边地不守");
            if (k.EndArmy - k.StartArmy <= -30) list.Add("丧师折将，控弦大损");
            if (k.StartPop > 0 && k.EndPop >= 0 && (k.EndPop - k.StartPop) * 100f / k.StartPop <= -20f)
                list.Add("部众流散，帐落萧条");
            if (k.StartMandate >= 0f && k.EndMandate >= 0f && k.EndMandate - k.StartMandate <= -8f)
                list.Add("天眷渐衰，人心思去");
            else if (k.EndMandate >= 0f && k.EndMandate < 30f) list.Add("天眷将坠，汗位岌岌");
            if (k.StartStability >= 0f && k.EndStability >= 0f && k.EndStability - k.StartStability <= -8f)
                list.Add("诸部离心，汗庭多故");
            if (k.EndVassals >= 0 && k.StartVassals >= 0 && k.EndVassals - k.StartVassals <= -1)
                list.Add("附庸部落相继叛去");
            if (k.WarCount >= 3 && dCity < 0) list.Add("连年兴兵而寸土未得");
            if (k.PolicyCount == 0 && reignYears >= 10) list.Add("怠于政令，汗庭因循");
            if (k.StartTreasury >= 0 && k.EndTreasury >= 0 && k.EndTreasury - k.StartTreasury <= -500)
                list.Add("府库耗竭，牛羊典尽");
            return list;
        }

        private static string SteppeClosingPhrase(KhanRecord k, int rank, int reignYears)
        {
            // 批次12②：每等第多结语池 + 按即位年轮换——相邻可汗不再同款结语（防重叠）
            string[][] pool =
            {
                new[] { "草原诸部至今犹颂其名", "其名与长生长天同铸于碑", "后世的牧歌里，仍唱着他的远征" },
                new[] { "其牧政之迹，萨满载于口传史诗", "英武之名，随商队传遍四方", "草原记得他纵马如风的年代" },
                new[] { "守牧团之成，亦足称述", "承先启后，帐落安然", "无大功亦无大过，牧火不熄" },
                new[] { "牧国无事，亦无大失", "风调雨顺的岁月里悄然更替", "平静得像一片没有波澜的湖" },
                new[] { "在位虽久而建树寥寥", "岁月漫长，功业却未曾落地", "汗旗依旧，牧场未广一寸" },
                new[] { "旋立旋废，如风中残纛", "来去匆匆，若春汛转瞬", "汗位未暖，已让贤途" },
                new[] { "其失政之烈，为草原数十年所未见", "败象丛生，诸部引以为鉴", "他的名字成了牧民警示孩童的训诫" },
            };
            int idx = rank < 0 || rank >= pool.Length ? 0 : rank;
            var arr = pool[idx];
            int pick = System.Math.Abs(k.StartYear * 31 + k.EndYear * 17 + rank) % arr.Length;
            return arr[pick];
        }


        /// <summary>批次12②：史评开场变体（防相邻可汗同款起句——按即位/终年轮换）。</summary>
        private static string SteppeOpening(KhanRecord k)
        {
            string[] o = { "牧国", "在位", "临御", "御帐" };
            return o[System.Math.Abs(k.StartYear * 7 + k.EndYear * 13) % o.Length];
        }
        /// <summary>汗号（对应东方庙号）：首汗太祖汗、末汗（王庭终结）末汗、其余按等第。</summary>
        private static string SteppeKhanTitle(KhanRecord k, int rank)
        {
            if (rank < 0) return "";
            if (rank == 0) return "太祖汗";
            if (rank == 1) return "烈汗";
            if (rank == 2) return "英汗";
            if (rank == 3) return "贤汗";
            if (rank == 4) return rank.ToString() == "" ? "" : "守汗";
            if (rank == 5) return "平汗";
            if (rank == 6) return "庸汗";
            if (rank == 7) return "厉汗";
            return "末汗";
        }

        private static string CnNum(int n)
        {
            if (n >= 1 && n <= 9) return "一二三四五六七八九"[n - 1].ToString();
            if (n >= 10 && n < 20) return "十" + (n % 10 == 0 ? "" : "一二三四五六七八九"[n % 10 - 1].ToString());
            if (n >= 20 && n < 100)
            {
                int t = n / 10, o = n % 10;
                return "一二三四五六七八九"[t - 1].ToString() + "十" + (o == 0 ? "" : "一二三四五六七八九"[o - 1].ToString());
            }
            return n.ToString();
        }

        public List<float> GetTrend(string kingdomId)
        {
            if (!_states.TryGetValue(kingdomId ?? "", out var s)) return new List<float>();
            return new List<float> { s.Trend0, s.Trend1, s.Trend2 };
        }

        public void AdjustMandateValue(string kingdomId, float delta)
        {
            if (_states.TryGetValue(kingdomId ?? "", out var s)) s.Value = Mathf.Clamp(s.Value + delta, 0f, 100f);
        }

        public string GetMandateGrade(float val)
        {
            if (val >= 90f) return "天眷隆盛";
            if (val >= 70f) return "天眷昌隆";
            if (val >= 50f) return "天眷安定";
            if (val >= 30f) return "天眷式微";
            if (val >= 10f) return "天眷将倾";
            return "天眷欲坠";
        }

        public string GetMandateColor(float val) => OrthodoxMandateManager.Instance.GetMandateColor(val);
        public float GetStabilityBonus(float val) => OrthodoxMandateManager.Instance.GetStabilityBonus(val);

        private string _lastKhanName; // 阶段3C：汗位传承检测（运行时——读档后首个 Tick 重新播种）

        public void TickAll(int year)
        {
            try
            {
                // 阶段3C：汗位传承记录——持有国可汗更替（含·全名）入历代 + 史册
                try
                {
                    Kingdom hk = string.IsNullOrEmpty(_holderId) ? null : KingdomDataExt.FindKingdomById(_holderId);
                    string khanName = hk != null && hk.king != null && hk.king.isAlive() ? hk.king.getName() : null;
                    if (!string.IsNullOrEmpty(khanName))
                    {
                        // 阶段3H-8根修：比较两侧统一洗净名。getName() 对持有国可汗返回带" - 天可汗"
                        // 称号后缀的原始名，而存侧是洗净名——旧比较恒不等，同一可汗被逐年误记为传位
                        //（王庭"历33汗"虚胖、一年一改元的根因）。
                        string cleanKhan = CleanKhanName(khanName);
                        if (_lastKhanName == null) _lastKhanName = cleanKhan;
                        else if (_lastKhanName != cleanKhan)
                        {
                            string era = ZhuluChineseNameService.Instance.GetNomadicEraName();
                            // 阶段3H：闭旧汗（盖棺快照+草原评）→ 开新汗
                            try
                            {
                                var cur = _courts.FindLast(c => c.EndYear == -1);
                                if (cur != null && cur.Khans.Count > 0)
                                    CloseKhan(cur.Khans[cur.Khans.Count - 1], year, hk);
                                if (cur != null)
                                    cur.Khans.Add(OpenKhan(cleanKhan, era, year, hk));
                            } catch { }
                            _history.Add($"{year}|汗位|{_holderId}|{cleanKhan}|即位·改元{era}");
                            ChronicleManager.Instance.AddEntry(year, ChronicleEventType.KingChange, _holderId, hk.name ?? "", cleanKhan,
                                $"<color=#c8b98a>{cleanKhan} 继天可汗位，改元「{era}」。</color>", true);
                            ZhuluContext.Instance.LogInfo($"[NomadicMandate] 汗位传承: {_lastKhanName} → {cleanKhan}（{era}）");
                            _lastKhanName = cleanKhan;
                        }
                    }
                }
                catch { }
                if (year != _lastTransferYear)
                {
                    _lastTransferYear = year;
                    CheckTransfer(year);
                }
                if (_lastCalcYear < 0 || year - _lastCalcYear >= ZhuluConfig.Instance.MandateTickInterval)
                {
                    _lastCalcYear = year;
                    CalculateValue(year);
                }
                ApplyEffects();
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[NomadicMandate] TickAll 异常", ex);
            }
        }

        private void CheckTransfer(int year)
        {
            if (!string.IsNullOrEmpty(_holderId))
            {
                Kingdom holder = KingdomDataExt.FindKingdomById(_holderId);
                if (holder == null || !holder.isAlive())
                {
                    Revoke(year, "亡国失眷");
                    GrantBestCandidate(year);
                    return;
                }
                if (!KingdomCivilizationService.Instance.IsNomadic(holder))
                {
                    Revoke(year, "异体系改朝换代");
                    GrantBestCandidate(year);
                    return;
                }

                State state = GetOrCreate(_holderId);
                int cities = holder.cities?.Count ?? 0;
                if (cities < 6)
                {
                    if (year < state.ProtectUntil) state.BelowSixSince = 0;
                    else if (state.BelowSixSince == 0) state.BelowSixSince = year;
                    else if (year - state.BelowSixSince >= BelowSixYearsLimit)
                    {
                        Revoke(year, "衰微失眷");
                        GrantBestCandidate(year);
                    }
                }
                else state.BelowSixSince = 0;
                return;
            }
            GrantBestCandidate(year);
        }

        private void GrantBestCandidate(int year)
        {
            Kingdom candidate = FindBestCandidate();
            // 与东方天命相同：本世界从未产生过本轨天命时，允许使用开局候选规则，
            // 防止多国一城的世界永久没有长生天眷持有者。
            if (candidate == null && _history.Count == 0) candidate = FindInitialCandidate();
            if (candidate != null) GrantMandate(candidate, year, "天眷空悬结束");
        }

        private Kingdom FindInitialCandidate()
        {
            if (World.world?.kingdoms?.list == null) return null;
            Kingdom best = null;
            int bestPopulation = 0;
            foreach (Kingdom k in World.world.kingdoms.list)
            {
                if (k == null || !k.isAlive() || !KingdomCivilizationService.Instance.IsNomadic(k)) continue;
                if (VassalManager.Instance.IsVassal(k) || ZhuluRebelService.IsRebel(k)) continue;
                if (k.getAge() < 15 || ZhuluLiZhiService.GetNetLiZhi(k.id.ToString()) < 8) continue;
                int effectiveCities = KingdomExt.GetEffectiveCities(k.id.ToString(), k.cities?.Count ?? 0);
                int population = k.units?.Count ?? 0;
                if ((effectiveCities >= 3 || population >= 300) && population > bestPopulation)
                {
                    best = k;
                    bestPopulation = population;
                }
            }
            return best;
        }

        private Kingdom FindBestCandidate()
        {
            if (World.world?.kingdoms?.list == null) return null;
            Kingdom best = null;
            int score = -1;
            foreach (Kingdom k in World.world.kingdoms.list)
            {
                if (k == null || !k.isAlive()) continue;
                if (!KingdomCivilizationService.Instance.IsNomadic(k)) continue;
                if (VassalManager.Instance.IsVassal(k) || ZhuluRebelService.IsRebel(k)) continue;
                int pp = ZhuluTierService.GetPowerPoints(k);
                if (pp < 40 || k.getAge() < 15) continue;
                // 第一版按用户要求直接复用东方现有数值来源；游牧界面显示为“汗威”。
                if (ZhuluLiZhiService.GetNetLiZhi(k.id.ToString()) < 8) continue;
                if (pp > score) { best = k; score = pp; }
            }
            return best;
        }

        /// <summary>按当前游牧受眷规则检查单一国家，不改变任何数据。</summary>
        public bool IsQualifiedCandidate(Kingdom k)
        {
            if (k == null || !k.isAlive() || !KingdomCivilizationService.Instance.IsNomadic(k)) return false;
            if (VassalManager.Instance.IsVassal(k) || ZhuluRebelService.IsRebel(k)) return false;
            if (ZhuluTierService.GetPowerPoints(k) < 40 || k.getAge() < 15) return false;
            return ZhuluLiZhiService.GetNetLiZhi(k.id.ToString()) >= 8;
        }

        /// <summary>异体系篡夺原天命国后：长生天眷空悬且该国达标时立即受眷。</summary>
        public bool TryGrantVacantTo(Kingdom k, int year)
        {
            if (!string.IsNullOrEmpty(_holderId) || !IsQualifiedCandidate(k)) return false;
            GrantMandate(k, year, "异体系改朝后受眷");
            return true;
        }

        private void GrantMandate(Kingdom k, int year, string reason)
        {
            if (k == null || !k.isAlive() || !KingdomCivilizationService.Instance.IsNomadic(k)) return;
            if (!string.IsNullOrEmpty(_holderId) && _holderId != k.id.ToString()) Revoke(year, "天眷转移");
            _holderId = k.id.ToString();
            State state = GetOrCreate(_holderId);
            state.Value = 60f;
            state.BelowSixSince = 0;
            state.ProtectUntil = year + ProtectYears;
            state.Trend0 = state.Trend1 = state.Trend2 = -1f;
            // 立即同步王庭身份，避免等到下一轮按国年度结算才在人物特质与窗口中出现。
            try { NomadicTierService.ForceTierTraitForCurrentStatus(k); } catch { }
            _history.Add($"{year}|获得|{_holderId}|{k.name}|{reason}");
            // 阶段3H：开王庭记录 + 首汗
            try
            {
                string kn = ""; try { kn = k.king != null && k.king.isAlive() ? k.king.getName() : ""; } catch { }
                var court = new CourtRecord { Court = k.name ?? "?", KingdomId = _holderId, StartYear = year };
                court.Khans.Add(OpenKhan(kn, ZhuluChineseNameService.Instance.GetNomadicEraName(), year, k));
                _courts.Add(court);
                _lastKhanName = CleanKhanName(kn);
            } catch { }
            try { WorldTip.showNow($"【{k.name}】蒙长生天眷，建立王庭！", false, "top", 7f, "#c8b98a"); } catch { }
            ChronicleManager.Instance.AddEntry(year, ChronicleEventType.MandateGranted, _holderId, k.name ?? "", "长生天眷",
                $"<color=#c8b98a>长生天垂眷，{k.name}建立王庭，首领称天可汗。</color>", true);
        }

        private void Revoke(int year, string reason)
        {
            if (string.IsNullOrEmpty(_holderId)) return;
            Kingdom old = KingdomDataExt.FindKingdomById(_holderId);
            _history.Add($"{year}|失去|{_holderId}|{old?.name ?? ""}|{reason}");
            // 阶段3H：闭王庭 + 末汗盖棺
            try
            {
                var cc = _courts.FindLast(c => c.EndYear == -1);
                if (cc != null)
                {
                    cc.EndYear = year;
                    cc.EndReason = reason ?? "";
                    if (cc.Khans.Count > 0) CloseKhan(cc.Khans[cc.Khans.Count - 1], year, old);
                }
                _lastKhanName = null;
            } catch { }
            ModifierPipeline.Instance.RemovePlan("nomadic_mandate_" + _holderId);
            _holderId = null;
            // 收回王庭身份，并立即按现有国力转换为大汗庭/汗国/部盟/大部/部族。
            try { if (old != null && old.isAlive()) NomadicTierService.ForceTierTraitForCurrentStatus(old); } catch { }
        }

        /// <summary>夺长生天眷战争胜利的强制转移入口。</summary>
        public void ForceTransferMandate(Kingdom oldHolder, Kingdom newHolder, int year, string reason)
        {
            if (!string.IsNullOrEmpty(_holderId)) Revoke(year, reason);
            if (newHolder != null && newHolder.isAlive() && KingdomCivilizationService.Instance.IsNomadic(newHolder))
                GrantMandate(newHolder, year, reason);
        }

        /// <summary>异体系篡位时由体制转换器先收回旧天眷。</summary>
        public void RevokeForRegimeChange(Kingdom kingdom, int year)
        {
            if (kingdom != null && IsMandateHolder(kingdom.id.ToString())) Revoke(year, "异体系改朝换代");
        }

        private void CalculateValue(int year)
        {
            if (string.IsNullOrEmpty(_holderId)) return;
            Kingdom k = KingdomDataExt.FindKingdomById(_holderId);
            if (k == null || !k.isAlive()) return;
            State s = GetOrCreate(_holderId);
            var detail = new System.Text.StringBuilder();
            Action<string, float> note = (name, delta) =>
            {
                if (Mathf.Abs(delta) >= 0.01f) detail.Append(name).Append(delta >= 0f ? "+" : "").Append(delta.ToString("F0")).Append("　");
            };

            float target = 40f;
            float dCities = Mathf.Min(20f, (k.cities?.Count ?? 0) * 2f); target += dCities; note("牧地", dCities);
            // 阶段3H-13：王庭周期律精确化（与东方同曲线——逐年累计，320 年后停摆纯看行为）
            int age = KingdomExt.GetDynastyAge(_holderId, year);
            if (age < 320)
            {
                float dAge;
                if (age < 30) dAge = age * 0.2f;
                else if (age < 80) dAge = 6f + (age - 30) * 0.3f;
                else if (age < 150) dAge = 21f + (age - 80) * 0.1f;
                else if (age < 240) dAge = 28f;
                else dAge = 28f - (age - 240) * 0.1f;
                target += dAge; note("汗庭国祚", dAge);
            }
            float stability = StabilityManager.Instance.GetStability(_holderId);
            float dStab = stability > 70f ? 10f : stability < 30f ? -10f : 0f; target += dStab; note("部众安定", dStab);
            int power = KingdomExt.GetPowerValue(_holderId);
            float dPower = power > 70 ? 3f : power < 30 ? -3f : 0f; target += dPower; note("汗权", dPower);
            bool atWar = false;
            try { foreach (var _ in k.getWars()) { atWar = true; break; } } catch { }
            float dWar = atWar ? -5f : 0f; target += dWar; note("交战", dWar);
            float dDeficit = 0f;
            try { if (TreasuryManager.Instance.GetReport(_holderId).DeficitYears >= 3) dDeficit = -5f; } catch { }
            target += dDeficit; note("亏空", dDeficit);
            float dAuthority = ZhuluLiZhiService.GetNetLiZhi(_holderId) < 0 ? -2f : 0f; target += dAuthority; note("汗威", dAuthority);
            float dFaction = 0f;
            try { if (FactionPoliticsManager.Instance.GetIntensity(k, _holderId) >= 0.7f) dFaction = -2f; } catch { }
            target += dFaction; note("部族争斗", dFaction);
            int pop = 0;
            try { pop = k.getPopulationPeople(); } catch { }
            float dPop = 0f;
            if (s.LastPopulation > 0 && pop > 0)
            {
                float growth = (pop - s.LastPopulation) / (float)s.LastPopulation;
                if (growth >= 0.02f) dPop = 2f; else if (growth < 0f) dPop = -2f;
            }
            s.LastPopulation = pop; target += dPop; note("部众", dPop);

            float value = Mathf.Clamp(s.Value * 0.90f + target * 0.10f, 0f, 100f);
            if (year < s.ProtectUntil) value = Mathf.Max(value, 50f);
            s.Value = value;
            s.Detail = detail.ToString().TrimEnd('　');
            s.Trend2 = s.Trend1; s.Trend1 = s.Trend0; s.Trend0 = value;
        }

        private void ApplyEffects()
        {
            if (string.IsNullOrEmpty(_holderId)) return;
            float value = GetMandateValue(_holderId);
            string planId = "nomadic_mandate_" + _holderId;
            // 阶段3H-12：忠诚档位与东方同构（75+ 每 5 分一档 +10 至 +70；100 直取 +100）。
            // 稳定度档由 StabilityManager 单独读取 GetStabilityBonus，不能误写进忠诚通道。
            float loyalty = value >= 100f ? 100f : value >= 95f ? 70f : value >= 90f ? 60f
                : value >= 85f ? 50f : value >= 80f ? 40f : value >= 75f ? 30f
                : value >= 65f ? 20f : value >= 55f ? 10f
                : value >= 45f ? 0f : value >= 35f ? -10f : value >= 25f ? -20f : value >= 15f ? -30f : -40f;
            if (!Mathf.Approximately(loyalty, 0f)) ModifierPipeline.Instance.RegisterPlan(new ModifierPlan
            {
                SourceId = planId,
                TargetKingdomId = _holderId,
                Channel = ModifierChannels.CityLoyaltyBonus,
                Additive = loyalty,
                Multiplicative = 1f,
                Reason = $"长生天眷·{GetMandateGrade(value)} (忠诚{(loyalty > 0 ? "+" : "")}{loyalty:F0})"
            });
            else ModifierPipeline.Instance.RemovePlan(planId);

            if (value < 10f && UnityEngine.Random.value < 0.05f)
            {
                Kingdom k = KingdomDataExt.FindKingdomById(_holderId);
                if (k != null) try { WorldTip.showNow($"【{k.name}】天眷欲绝，王庭将失长生天之眷！", false, "top", 3f, "#FF4500"); } catch { }
            }
        }

        private State GetOrCreate(string id)
        {
            if (!_states.TryGetValue(id, out var s)) { s = new State(); _states[id] = s; }
            return s;
        }

        private void LegacyCollectToSnapshot(ZhuluSnapshot snap)
        {
            snap.NomadicMandateHistory = string.Join("||", _history);
            // 阶段3H：王庭/可汗记录序列化（court|kid|s|e|r⟂khan条;...  以\n分庭）
            try
            {
                var cs = new List<string>();
                foreach (var c in _courts)
                {
                    var ks = new List<string>();
                    foreach (var k2 in c.Khans)
                        ks.Add(string.Join("|", k2.Name, k2.Era, k2.StartYear, k2.EndYear, k2.StartCities, k2.StartArmy, k2.EndCities, k2.EndArmy, k2.Verdict,
                            k2.StartPop, k2.EndPop, k2.StartTreasury, k2.EndTreasury, k2.StartVassals, k2.EndVassals,
                            k2.StartMandate.ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
                            k2.EndMandate.ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
                            k2.StartStability.ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
                            k2.EndStability.ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
                            k2.WarCount, k2.PolicyCount, k2.RankLabel, k2.KhanTitle, k2.VerdictText,
                            k2.SlewHolder, k2.SlewHolderNames));
                    cs.Add(string.Join("|", c.Court, c.KingdomId, c.StartYear, c.EndYear, c.EndReason) + "⟂" + string.Join(";", ks));
                }
                snap.NomadicCourtHistory = string.Join("\n", cs);
            }
            catch { snap.NomadicCourtHistory = ""; }
            snap.NomadicMandateData.Clear();
            if (string.IsNullOrEmpty(_holderId) || !_states.TryGetValue(_holderId, out var s)) return;
            snap.NomadicMandateData[_holderId] = string.Join("|", s.Value.ToString("F2"), s.BelowSixSince,
                s.ProtectUntil, s.Trend0.ToString("F2"), s.Trend1.ToString("F2"), s.Trend2.ToString("F2"), s.LastPopulation, s.Detail ?? "");
        }
        private void LegacyRestoreFromSnapshot(ZhuluSnapshot snap)
        {
            ResetRuntime();
            if (!string.IsNullOrEmpty(snap?.NomadicMandateHistory)) _history.AddRange(snap.NomadicMandateHistory.Split(new[] { "||" }, StringSplitOptions.RemoveEmptyEntries));
            // 阶段3H：恢复王庭/可汗记录
            _courts.Clear();
            try
            {
                if (!string.IsNullOrEmpty(snap?.NomadicCourtHistory))
                {
                    foreach (var cChunk in snap.NomadicCourtHistory.Split('\n'))
                    {
                        if (string.IsNullOrWhiteSpace(cChunk)) continue;
                        var parts = cChunk.Split('⟂');
                        var cf = parts[0].Split('|');
                        if (cf.Length < 5) continue;
                        var court = new CourtRecord
                        {
                            Court = cf[0], KingdomId = cf[1],
                            StartYear = int.TryParse(cf[2], out int csy) ? csy : -1,
                            EndYear = int.TryParse(cf[3], out int cey) ? cey : -1,
                            EndReason = cf[4]
                        };
                        if (parts.Length > 1 && !string.IsNullOrEmpty(parts[1]))
                        {
                            foreach (var kChunk in parts[1].Split(';'))
                            {
                                if (string.IsNullOrEmpty(kChunk)) continue;
                                var kf = kChunk.Split('|');
                                if (kf.Length < 9) continue;
                                court.Khans.Add(new KhanRecord
                                {
                                    Name = kf[0], Era = kf[1],
                                    StartYear = int.TryParse(kf[2], out int ksy) ? ksy : -1,
                                    EndYear = int.TryParse(kf[3], out int key_) ? key_ : -1,
                                    StartCities = int.TryParse(kf[4], out int sc) ? sc : 0,
                                    StartArmy = int.TryParse(kf[5], out int sa) ? sa : 0,
                                    EndCities = int.TryParse(kf[6], out int ec) ? ec : 0,
                                    EndArmy = int.TryParse(kf[7], out int ea) ? ea : 0,
                                    Verdict = kf[8],
                                    StartPop = kf.Length > 9 && int.TryParse(kf[9], out int spp) ? spp : -1,
                                    EndPop = kf.Length > 10 && int.TryParse(kf[10], out int epp) ? epp : -1,
                                    StartTreasury = kf.Length > 11 && long.TryParse(kf[11], out long stt) ? stt : -1,
                                    EndTreasury = kf.Length > 12 && long.TryParse(kf[12], out long ett) ? ett : -1,
                                    StartVassals = kf.Length > 13 && int.TryParse(kf[13], out int svv) ? svv : -1,
                                    EndVassals = kf.Length > 14 && int.TryParse(kf[14], out int evv) ? evv : -1,
                                    StartMandate = kf.Length > 15 && float.TryParse(kf[15], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float smm) ? smm : -1f,
                                    EndMandate = kf.Length > 16 && float.TryParse(kf[16], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float emm) ? emm : -1f,
                                    StartStability = kf.Length > 17 && float.TryParse(kf[17], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float sss) ? sss : -1f,
                                    EndStability = kf.Length > 18 && float.TryParse(kf[18], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float ess) ? ess : -1f,
                                    WarCount = kf.Length > 19 && int.TryParse(kf[19], out int wcw) ? wcw : -1,
                                    PolicyCount = kf.Length > 20 && int.TryParse(kf[20], out int pcp) ? pcp : -1,
                                    RankLabel = kf.Length > 21 ? (kf[21] ?? "") : "",
                                    KhanTitle = kf.Length > 22 ? (kf[22] ?? "") : "",
                                    VerdictText = kf.Length > 23 ? (kf[23] ?? "") : "",
                                    SlewHolder = kf.Length > 24 && kf[24] == "1" ? 1 : 0,
                                    SlewHolderNames = kf.Length > 25 ? (kf[25] ?? "") : ""
                                });
                            }
                        }
                        _courts.Add(court);
                    }
                }
            } catch { }
            // 阶段3H-5：回填旧档 + 合并同人连续汗位（在持有态解析前——历史无当前持有者也要出历代卡）
            BackfillCourtsFromHistory();
            MergeConsecutiveKhans();
            if (snap?.NomadicMandateData == null || snap.NomadicMandateData.Count == 0) return;
            var kv = snap.NomadicMandateData.First();
            _holderId = kv.Key;
            string[] p = (kv.Value ?? "").Split('|');
            var s = GetOrCreate(_holderId);
            if (p.Length > 0 && float.TryParse(p[0], out float f)) s.Value = f;
            if (p.Length > 1 && int.TryParse(p[1], out int i1)) s.BelowSixSince = i1;
            if (p.Length > 2 && int.TryParse(p[2], out int i2)) s.ProtectUntil = i2;
            if (p.Length > 3 && float.TryParse(p[3], out f)) s.Trend0 = f;
            if (p.Length > 4 && float.TryParse(p[4], out f)) s.Trend1 = f;
            if (p.Length > 5 && float.TryParse(p[5], out f)) s.Trend2 = f;
            if (p.Length > 6 && int.TryParse(p[6], out int pop)) s.LastPopulation = pop;
            if (p.Length > 7) s.Detail = p[7];
        }

        // ---- batch10-A1: schema v5 section ----
        public sealed class NomadicState
        {
            public string History;
            public string CourtHistory;
            public Dictionary<string, string> HolderData = new Dictionary<string, string>();
        }
        public string SectionName => "nomadic_mandate";
        public Dictionary<string, NomadicState> CaptureStates()
        {
            var v4 = new ZhuluSnapshot();
            LegacyCollectToSnapshot(v4);   // delegate (courts blob builder untouched)
            return new Dictionary<string, NomadicState>
            {
                ["__global__"] = new NomadicState
                {
                    History = v4.NomadicMandateHistory,
                    CourtHistory = v4.NomadicCourtHistory,
                    HolderData = v4.NomadicMandateData ?? new Dictionary<string, string>(),
                }
            };
        }
        public void ApplyStates(Dictionary<string, NomadicState> data)
        {
            if (data == null || !data.TryGetValue("__global__", out var st) || st == null) return;
            var v4 = new ZhuluSnapshot
            {
                NomadicMandateHistory = st.History ?? "",
                NomadicCourtHistory = st.CourtHistory ?? "",
                NomadicMandateData = st.HolderData ?? new Dictionary<string, string>()
            };
            LegacyRestoreFromSnapshot(v4);   // delegate: backfill/merge/holder-parse all preserved
        }


        /// <summary>阶段3H补：旧档迁移——快照无 NomadicCourtHistory 时从历史日志行重建王庭/可汗结构。</summary>
        private void BackfillCourtsFromHistory()
        {
            if (_courts.Count > 0) return;
            CourtRecord cur = null;
            int lastYear = -1;
            try
            {
                foreach (string line in _history)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    string[] p = line.Split('|');
                    if (p.Length < 5 || !int.TryParse(p[0], out int y)) continue;
                    lastYear = y;
                    if (p[1] == "获得" || p[1] == "转移")
                    {
                        if (cur != null) { cur.EndYear = y; cur.EndReason = p[1] == "转移" ? "转移" : "改朝"; }
                        cur = new CourtRecord { Court = p[3] ?? "?", KingdomId = p[2] ?? "", StartYear = y };
                        _courts.Add(cur);
                    }
                    else if (p[1] == "汗位" && cur != null)
                    {
                        var prev = cur.Khans.Count > 0 ? cur.Khans[cur.Khans.Count - 1] : null;
                        if (prev != null && prev.EndYear == -1) { prev.EndYear = y; prev.Verdict = "早岁史阙"; }
                        string era = p.Length > 4 && (p[4] ?? "").StartsWith("即位·改元") ? p[4].Substring("即位·改元".Length) : "";
                        cur.Khans.Add(new KhanRecord { Name = CleanKhanName(p[3]), Era = era, StartYear = y, StartCities = -1, StartArmy = -1 });
                    }
                    else if (p[1] == "失去" && cur != null)
                    {
                        var prev = cur.Khans.Count > 0 ? cur.Khans[cur.Khans.Count - 1] : null;
                        if (prev != null && prev.EndYear == -1) { prev.EndYear = y; prev.Verdict = "早岁史阙"; }
                        cur.EndYear = y; cur.EndReason = p[4] ?? ""; cur = null;
                    }
                }
            }
            catch { }
            // 在位兜底：王庭无当今可汗（获得行未录首汗名）→ 以当前君主补开，快照从今起算
            try
            {
                if (!string.IsNullOrEmpty(_holderId))
                {
                    var open = _courts.FindLast(c => c.EndYear == -1);
                    if (open == null || open.KingdomId != _holderId)
                    {
                        Kingdom k = KingdomDataExt.FindKingdomById(_holderId);
                        if (k != null && k.isAlive())
                        {
                            open = new CourtRecord { Court = k.name ?? "?", KingdomId = _holderId, StartYear = lastYear > 0 ? lastYear : 0 };
                            _courts.Add(open);
                        }
                    }
                    if (open != null && open.KingdomId == _holderId && (open.Khans.Count == 0 || open.Khans[open.Khans.Count - 1].EndYear != -1))
                    {
                        Kingdom k = KingdomDataExt.FindKingdomById(_holderId);
                        if (k != null && k.king != null && k.king.isAlive())
                            open.Khans.Add(OpenKhan(k.king.getName(), "", open.Khans.Count == 0 ? open.StartYear : lastYear, k));
                    }
                }
            }
            catch { }
            if (_courts.Count > 0)
                ZhuluContext.Instance.LogInfo($"[NomadicMandate] 旧档王庭记录回填完成: {_courts.Count} 王庭");
        }

        /// <summary>阶段3H-5：合并同人连续汗位记录（旧档"称号后缀比较"bug 造成同一可汗逐年重复；真实多次改元并成年号串）。</summary>
        private void MergeConsecutiveKhans()
        {
            try
            {
                foreach (var c in _courts)
                {
                    for (int i = c.Khans.Count - 1; i >= 1; i--)
                    {
                        var cur = c.Khans[i];
                        var prev = c.Khans[i - 1];
                        if (string.IsNullOrEmpty(cur.Name) || cur.Name != prev.Name) continue;
                        prev.EndYear = cur.EndYear;
                        if (cur.EndCities >= 0) prev.EndCities = cur.EndCities;
                        if (cur.EndArmy >= 0) prev.EndArmy = cur.EndArmy;
                        if (cur.EndPop >= 0) prev.EndPop = cur.EndPop;
                        if (cur.EndTreasury >= 0) prev.EndTreasury = cur.EndTreasury;
                        if (cur.EndVassals >= 0) prev.EndVassals = cur.EndVassals;
                        if (cur.EndMandate >= 0f) prev.EndMandate = cur.EndMandate;
                        if (cur.EndStability >= 0f) prev.EndStability = cur.EndStability;
                        if (cur.WarCount >= 0) prev.WarCount = cur.WarCount;
                        if (cur.PolicyCount >= 0) prev.PolicyCount = cur.PolicyCount;
                        if (!string.IsNullOrEmpty(cur.RankLabel)) prev.RankLabel = cur.RankLabel;
                        if (!string.IsNullOrEmpty(cur.VerdictText)) prev.VerdictText = cur.VerdictText;
                        if (!string.IsNullOrEmpty(cur.KhanTitle)) prev.KhanTitle = cur.KhanTitle;
                        if (cur.SlewHolder == 1) { prev.SlewHolder = 1; prev.SlewHolderNames = cur.SlewHolderNames; }
                        if (!string.IsNullOrEmpty(cur.Era))
                        {
                            bool has = false;
                            foreach (var ep in prev.Era.Split('·')) if (ep == cur.Era) { has = true; break; }
                            if (!has && prev.Era.Length < 12) prev.Era = prev.Era + "·" + cur.Era;
                        }
                        if (prev.EndYear > 0)
                        {
                            if (prev.StartCities >= 0 && prev.EndCities >= 0)
                                prev.Verdict = ComputeVerdict(prev.StartYear, prev.EndYear, prev.EndCities - prev.StartCities, prev.EndArmy - prev.StartArmy);
                        }
                        else prev.Verdict = "";
                        c.Khans.RemoveAt(i);
                    }
                }
            }
            catch { }
        }


        public void ResetRuntime()
        {
            if (!string.IsNullOrEmpty(_holderId)) ModifierPipeline.Instance.RemovePlan("nomadic_mandate_" + _holderId);
            _holderId = null;
            _states.Clear();
            _history.Clear();
            _courts.Clear();
            _lastKhanName = null;
            _lastCalcYear = _lastTransferYear = -1;
        }
    }
}
