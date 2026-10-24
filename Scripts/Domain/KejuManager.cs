using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using HarmonyLib;
using ZhuluRebuild.Core;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Domain
{
    /// <summary>
    /// 科举管理器（阶段1·用户定案）。
    /// 范围：侯/公/天命国（GetTierWithMandate ≥ Hou）。
    /// 设置 zlr_keju：interval(5/10/20 默认10) / count(2/4/6 默认4) / enabled(默认true)。
    /// 评分：综合（智力/战力/魅力+特质加成）+ 文/武分科侧重（复用 CalcOfficialScore 权重表）。
    /// 寒门机制（皇权联动）：GetPowerValue≥60 只看评分（公平取士）；&lt;60 世家评分加成。
    /// 文举：录取→人才库待命（zlr_talent_pool）→官位空缺/清洗后按评分补位。
    /// 武举：首名→城主（冷却10年+现任劣质才换）；其余→队长称号记录（GetCaptainTitle）。
    /// 快照：15 系统标准四方法。
    /// </summary>
    public class KejuManager : ISnapshotSection<KejuManager.KejuState>
    {
        public static KejuManager Instance { get; } = new KejuManager();

        // ── 设置默认值 ──
        public const int DefaultInterval = 10;
        public const int DefaultCount = 4;
        public const int MinInterval = 5, MaxInterval = 20;
        public const int MinCount = 2, MaxCount = 6;
        private const int CityReplaceCooldownYears = 10; // 武举替换城主冷却
        private const int AdultAge = 16;

        /// <summary>科举设置（王国级）。</summary>
        public class KejuSettings
        {
            public int Count = DefaultCount;
            public bool Enabled = true;
            // 阶段2R：删除死字段 Interval——窗口/开科统一走 ZhuluConfig.KejuIntervalYears（2M-21 定稿），
            // 本字段从未被读取（旧快照 JSON 多余字段 Newtonsoft 自动忽略，读档兼容）
        }

        /// <summary>人才库条目。</summary>
        public class TalentEntry
        {
            public long ActorId;
            public string Name;
            public int Score;
            public int Year;
            public bool BranchWu; // true=武举
        }

        /// <summary>武举队长记录。</summary>
        public class CaptainMark
        {
            public long ActorId;
            public string Name;
            public string Title;
            public int Year;
        }

        private readonly Dictionary<string, KejuSettings> _settings = new Dictionary<string, KejuSettings>();
        private readonly Dictionary<string, List<TalentEntry>> _talentPool = new Dictionary<string, List<TalentEntry>>();
        private readonly Dictionary<string, List<CaptainMark>> _captainMarks = new Dictionary<string, List<CaptainMark>>();
        private readonly Dictionary<string, Dictionary<long, int>> _cityCool = new Dictionary<string, Dictionary<long, int>>(); // kingdomId → cityId → 冷却截止年

        private readonly Dictionary<string, int> _lastKejuYear = new Dictionary<string, int>(); // 防同年重复开科

        private KejuManager() { }

        private bool Enabled => ZhuluConfig.Instance.KejuEnabled;

        // ═══════════════════════ 公开查询 ═══════════════════════

        public KejuSettings GetSettings(string kingdomId)
        {
            if (!_settings.TryGetValue(kingdomId, out var s))
            {
                s = new KejuSettings();
                _settings[kingdomId] = s;
            }
            return s;
        }

        public void SetSettings(string kingdomId, int? interval = null, int? count = null, bool? enabled = null)
        {
            var s = GetSettings(kingdomId);
            // 阶段2R：interval 参数废弃（保留签名兼容旧调用——间隔统一走 ZhuluConfig）
            if (count.HasValue) s.Count = Mathf.Clamp(count.Value, MinCount, MaxCount);
            if (enabled.HasValue) s.Enabled = enabled.Value;
            _settings[kingdomId] = s;
        }

        // ── 阶段2R：功名归一（修复特质堆叠——存档2实测 25 人多功名并存，最狠 5 项）──
        private static readonly string[] AllGongming =
        {
            TraitManager.TRAIT_XIUCAI, TraitManager.TRAIT_WUXIUCAI,
            TraitManager.TRAIT_JUREN, TraitManager.TRAIT_WUJUREN,
            TraitManager.TRAIT_JINSHI, TraitManager.TRAIT_WUJINSHI,
            TraitManager.TRAIT_ZHUANGYUAN,
            TraitManager.NOMAD_TRIBE_SCHOLAR, TraitManager.NOMAD_WARRIOR,
            TraitManager.NOMAD_LEAGUE_SCHOLAR, TraitManager.NOMAD_CENTURION,
            TraitManager.NOMAD_COURT_SCHOLAR, TraitManager.NOMAD_CHILIARCH,
            TraitManager.NOMAD_FIRST_SCHOLAR
        };
        // 归一级别（高→低）：状元 3 / 进士·武进士 3 / 举人·武举人 2 / 秀才·武秀才 1
        public static int GongmingRank(string t)
        {
            if (t == TraitManager.TRAIT_ZHUANGYUAN || t == TraitManager.TRAIT_JINSHI || t == TraitManager.TRAIT_WUJINSHI
                || t == TraitManager.NOMAD_COURT_SCHOLAR || t == TraitManager.NOMAD_CHILIARCH || t == TraitManager.NOMAD_FIRST_SCHOLAR) return 3;
            if (t == TraitManager.TRAIT_JUREN || t == TraitManager.TRAIT_WUJUREN
                || t == TraitManager.NOMAD_LEAGUE_SCHOLAR || t == TraitManager.NOMAD_CENTURION) return 2;
            return 1;
        }

        /// <summary>授予功名（归一制）：移除全部低级功名，只保留最高新功名（状元不再叠进士）。</summary>
        private static void ReplaceGongmingTraits(Actor a, string topTrait)
        {
            if (a == null || string.IsNullOrEmpty(topTrait)) return;
            try
            {
                foreach (var t in AllGongming)
                {
                    if (t == topTrait) continue;
                    try { if (a.hasTrait(t)) a.removeTrait(t); } catch { }
                }
                if (!a.hasTrait(topTrait)) a.addTrait(topTrait);
            }
            catch { }
        }

        /// <summary>存量归一（旧档自愈）：功名只留最高一项—— Cleanup 每年调用，一年内全图修正。</summary>
        private void NormalizeGongming(Kingdom k)
        {
            try
            {
                if (k?.units == null) return;
                foreach (var u in k.units)
                {
                    if (u == null || !u.isAlive()) continue;
                    string best = null; int bestRank = 0; int cnt = 0;
                    try
                    {
                        foreach (var t in AllGongming)
                        {
                            if (u.hasTrait(t)) { cnt++; int r = GongmingRank(t); if (r > bestRank) { bestRank = r; best = t; } }
                        }
                    }
                    catch { continue; }
                    if (cnt >= 2 && best != null)
                    {
                        foreach (var t in AllGongming)
                        {
                            if (t != best) { try { if (u.hasTrait(t)) u.removeTrait(t); } catch { } }
                        }
                    }
                }
                // 阶段2R：君储不入科场——现任国王与储君身上的功名特质全部剥离（科举出身登基后功名卸除）
                try
                {
                    var kingA = k.king;
                    if (kingA != null) StripAllGongming(kingA);
                    string heirId = KingdomDataExt.GetStr(k, "heir_id");
                    if (!string.IsNullOrEmpty(heirId) && long.TryParse(heirId, out long hid))
                    {
                        var heirA = World.world?.units?.get(hid);
                        if (heirA != null) StripAllGongming(heirA);
                    }
                }
                catch { }
            }
            catch { }
        }

        /// <summary>阶段2R：剥离全部功名特质（君储专用）。</summary>
        private static void StripAllGongming(Actor a)
        {
            try
            {
                foreach (var t in AllGongming)
                {
                    try { if (a.hasTrait(t)) a.removeTrait(t); } catch { }
                }
            }
            catch { }
        }

        public List<TalentEntry> GetTalentPool(string kingdomId)
        {
            _talentPool.TryGetValue(kingdomId, out var list);
            return list ?? new List<TalentEntry>();
        }

        public List<CaptainMark> GetCaptainMarks(string kingdomId)
        {
            _captainMarks.TryGetValue(kingdomId, out var list);
            return list ?? new List<CaptainMark>();
        }

        // ═══════════════════════ 年度处理 ═══════════════════════

        /// <summary>按国年度：开科判定 + 人才库补位 + 武举替换城主。</summary>
        public void OnKingdomYear(Kingdom k, int year)
        {
            if (!Enabled) return;
            if (k == null || !k.isAlive()) return;
            string kId = k.id.ToString();
            try
            {
                int raw = k.cities != null ? k.cities.Count : 1;
                int eff = KingdomExt.GetEffectiveCities(kId, raw);
                bool isMandate = PolicyManager.IsMandateHolderForKingdom(k);
                var tier = OfficeManager.GetTierWithMandate(k, isMandate);
                // 阶段2E ⑦+：三级科举——县试伯国起（子/邑不开）
                if (tier == OfficeManager.KingdomTier.Zi) return;

                var s = GetSettings(kId);
                if (!s.Enabled) return;

                // 1. 三级开科（5 年一届：第 1 年县试/第 3 年乡试/第 5 年殿试）
                if (year != (_lastKejuYear.TryGetValue(kId, out int ly) ? ly : -1))
                {
                    int interval = ZhuluConfig.Instance.KejuIntervalYears <= 0 ? 5 : ZhuluConfig.Instance.KejuIntervalYears; // 阶段2L：轮次可配置
                    int round = year % interval;
                    bool canXiang = tier == OfficeManager.KingdomTier.Hou || tier == OfficeManager.KingdomTier.Gong || tier == OfficeManager.KingdomTier.Tianming;
                    bool canDian = tier == OfficeManager.KingdomTier.Gong || tier == OfficeManager.KingdomTier.Tianming;
                    if (round == 1)
                    {
                        _lastKejuYear[kId] = year;
                        if (TryPayExamFee(k, tier, year)) RunXianShi(k, kId, s, year); // 县试：无功名者 → 秀才
                    }
                    else if (round == 3 && canXiang)
                    {
                        _lastKejuYear[kId] = year;
                        if (TryPayExamFee(k, tier, year)) RunXiangShi(k, kId, s, year); // 乡试：秀才 → 举人
                    }
                    else if (round == 0 && canDian)
                    {
                        _lastKejuYear[kId] = year;
                        if (TryPayExamFee(k, tier, year)) RunDianShi(k, kId, s, year); // 殿试：举人 → 进士/状元
                    }
                }

                // 2. 人才库补位（官位空缺时）
                FillVacancies(k, kId, s, year);

                // 3. 清理过期冷却/死亡成员
                Cleanup(kId);

                // 4. 队长任期限制（2026-08-18：45 年届满卸任）
                EnforceCaptainTermLimits(k, kId, year);
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[Keju] OnKingdomYear 异常: {ex.Message}");
            }
        }

        // ═══════════════════════ 开科 ═══════════════════════

        /// <summary>
        /// 武举授职记录（阶段2S-12 修复：阶段1 G1 误删 MarkCaptain 为"死代码"，但武举授职卡窗口仍在读——
        /// 数据链断裂卡片恒空。此处恢复写入：及第授队长衔即记 CaptainMark，滚动上限 20 条。
        /// 2026-08-18 真受职：原版 Army 有队长（Captain）机制——setCaptain/getCaptain/hasCaptain，
        /// 此前仅记虚衔。现尝试将及第者实授为本国无队长军队的队长（反射调用，失败降级为虚衔），
        /// 并转 Warrior 职业（真吃兵饷、可参战——"受职"落到实处）。
        /// </summary>
        private void MarkCaptain(Kingdom k, string kId, Actor a, int year)
        {
            try
            {
                if (a == null) return;
                if (!_captainMarks.TryGetValue(kId, out var list))
                {
                    list = new List<CaptainMark>();
                    _captainMarks[kId] = list;
                }
                // 同人去重（新授覆盖旧衔）
                long aid = a.getID();
                list.RemoveAll(m => m.ActorId == aid);
                string title;
                try { title = k != null ? RegimeManager.GetCaptainTitle(k) : "队长"; } // 2026-08-18 特质权威版
                catch { title = "队长"; }
                list.Add(new CaptainMark { ActorId = aid, Name = a.getName() ?? "将", Title = title, Year = year });
                if (list.Count > 20) list.RemoveAt(0);

                // 真受职：实授军队队长 + Warrior 职业
                bool appointed = TryAppointRealCaptain(k, a);
                try
                {
                    PersonHistoryManager.Instance.RecordEvent(aid, PersonHistoryManager.CAT_GONGMING,
                        appointed
                            ? $"武举及第，实授军职统兵（{k?.name ?? "本国"}）"
                            : $"武举及第，授队长衔（{k?.name ?? "本国"}）",
                        year);
                }
                catch { }
            }
            catch { }
        }

        // ── 原版队长实授（反射缓存——Army.setCaptain(Actor) 为 public，ArmyManager.list 为 armies 容器）──
        private static System.Reflection.MethodInfo _setCaptainMethod;
        private static System.Reflection.FieldInfo _armiesListField;
        private static System.Reflection.PropertyInfo _armyKingdomProp;
        private static bool _captainReflectResolved;

        /// <summary>把及第者实授为本国某支无队长军队的队长。返回是否成功。</summary>
        private static bool TryAppointRealCaptain(Kingdom k, Actor a)
        {
            try
            {
                if (k == null || a == null || World.world?.armies == null) return false;
                if (!_captainReflectResolved)
                {
                    _setCaptainMethod = HarmonyLib.AccessTools.Method(typeof(Army), "setCaptain");
                    _armiesListField = HarmonyLib.AccessTools.Field(typeof(ArmyManager), "list");
                    _armyKingdomProp = HarmonyLib.AccessTools.Property(typeof(Army), "kingdom")
                        ?? HarmonyLib.AccessTools.Property(typeof(Army), "Kingdom");
                    _captainReflectResolved = true;
                }
                if (_setCaptainMethod == null || _armiesListField == null) return false;

                var armies = _armiesListField.GetValue(World.world.armies) as IEnumerable<Army>;
                if (armies == null) return false;

                Func<Army, Kingdom> kingdomOf = army =>
                {
                    try
                    {
                        if (_armyKingdomProp != null) return _armyKingdomProp.GetValue(army, null) as Kingdom;
                        var f = HarmonyLib.AccessTools.Field(typeof(Army), "_kingdom") ?? HarmonyLib.AccessTools.Field(typeof(Army), "kingdom");
                        return f != null ? f.GetValue(army) as Kingdom : null;
                    }
                    catch { return null; }
                };

                foreach (var army in armies)
                {
                    if (army == null) continue;
                    var owner = kingdomOf(army);
                    if (owner == null || owner != k) continue;
                    bool hasCap;
                    try { hasCap = army.hasCaptain(); } catch { continue; }
                    if (hasCap) continue;
                    _setCaptainMethod.Invoke(army, new object[] { a });
                    try { Traverse.Create(a).Method("setProfession", UnitProfession.Warrior, true).GetValue(); } catch { }
                    ZhuluContext.Instance.LogInfo($"[Keju] 队长实授: {a.getName()} 就任 {k.name} 军队长");
                    return true;
                }
                // 无可授军队（本国无军队或皆有队长）：至少转为 Warrior 备军
                try { Traverse.Create(a).Method("setProfession", UnitProfession.Warrior, true).GetValue(); } catch { }
                return false;
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[Keju] 队长实授失败（降级虚衔）: {ex.Message}");
                return false;
            }
        }

        // ── 2026-08-18 队长任期限制（45 年届满卸任；独立 "zlr_cap_since" 标记——与城主分开，防兼任互清）──
        private const string KEY_CAPTAIN_SINCE = "zlr_cap_since";
        private static System.Reflection.MethodInfo _captainLeftMethod;
        private static int _capScanYear = -1;

        /// <summary>
        /// 军队队长任期检查（年度全局单次）：同一队长在职 ≥ OfficeManager.TermLimitYears（45 年）→ 卸任
        /// （Army.captainLeft 反射调用；标记存队长本人 custom_data，"actorId|year"，换人自愈）。
        /// 性能：全年只做一次世界军队扫描（按国调用时同年跳过——O(军队数)/年，非 O(国×军)）。
        /// </summary>
        private static void EnforceCaptainTermLimits(Kingdom k, string kId, int year)
        {
            try
            {
                if (World.world?.armies == null) return;
                if (_capScanYear == year) return; // 同年已扫过（按国多次调用只扫一次）
                _capScanYear = year;
                if (!_captainReflectResolved)
                {
                    _setCaptainMethod = HarmonyLib.AccessTools.Method(typeof(Army), "setCaptain");
                    _armiesListField = HarmonyLib.AccessTools.Field(typeof(ArmyManager), "list");
                    _captainLeftMethod = HarmonyLib.AccessTools.Method(typeof(Army), "captainLeft")
                        ?? HarmonyLib.AccessTools.Method(typeof(Army), "CaptainLeft");
                    _captainReflectResolved = true;
                }
                if (_armiesListField == null) return;

                var armies = _armiesListField.GetValue(World.world.armies) as IEnumerable<Army>;
                if (armies == null) return;

                foreach (var army in armies)
                {
                    if (army == null) continue;
                    try
                    {
                        bool hasCap;
                        try { hasCap = army.hasCaptain(); } catch { continue; }
                        if (!hasCap) continue;

                        Actor cap;
                        try { cap = army.getCaptain(); } catch { continue; }
                        if (cap == null || !cap.isAlive()) continue;

                        string mark = Naming.ActorAccess.GetString(cap, KEY_CAPTAIN_SINCE);
                        string aid = cap.getID().ToString();
                        if (string.IsNullOrEmpty(mark) || !mark.StartsWith(aid + "|"))
                        {
                            Naming.ActorAccess.SetString(cap, KEY_CAPTAIN_SINCE, aid + "|" + year); // 新队长记录起始年
                            continue;
                        }
                        int start = 0;
                        int.TryParse(mark.Substring(mark.IndexOf('|') + 1), out start);
                        if (start <= 0 || year - start < OfficeManager.TermLimitYears) continue;

                        // 届满卸任：captainLeft()（原版正式卸任路径）
                        bool removed = false;
                        try
                        {
                            if (_captainLeftMethod != null) { _captainLeftMethod.Invoke(army, null); removed = true; }
                            else if (_setCaptainMethod != null) { _setCaptainMethod.Invoke(army, new object[] { null, false }); removed = true; }
                        }
                        catch { }
                        if (removed)
                        {
                            Naming.ActorAccess.SetString(cap, KEY_CAPTAIN_SINCE, "");
                            try { PersonHistoryManager.Instance.RecordEvent(cap.getID(), PersonHistoryManager.CAT_GONGMING,
                                $"统军 {OfficeManager.TermLimitYears} 年届满，卸任荣休", year); } catch { }
                            ZhuluContext.Instance.LogInfo($"[Keju] 队长任期届满卸任: {cap.getName() ?? "队长"}");
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        // 阶段2S-3：开科费用 = 基数 10 + 爵位档（伯 7 / 侯 10 / 公 15 / 天命 20——与政策维护同档口径）
        private const long ExamFeeBase = 10;

        private static long ExamFeeFor(OfficeManager.KingdomTier tier)
        {
            return tier == OfficeManager.KingdomTier.Tianming ? 20
                : tier == OfficeManager.KingdomTier.Gong ? 15
                : tier == OfficeManager.KingdomTier.Hou ? 10
                : 7; // 伯国
        }

        /// <summary>
        /// 开科费用结算（阶段2S-3）：国库不足 → 停科一届（史册记"国用不足"）。
        /// 计费异常时保守放行（宁可偶有一届免费，不误停科举）。
        /// </summary>
        private bool TryPayExamFee(Kingdom k, OfficeManager.KingdomTier tier, int year)
        {
            try
            {
                long fee = ExamFeeBase + ExamFeeFor(tier);
                // P2-30 串联⑤：兴学立教政策——开科费七折（文档"开科费-30%"原值落地）
                try
                {
                    if (PolicyManager.Instance.IsPolicyActive(k.id.ToString(), "zlr_hou_xingxue"))
                        fee = fee * 7 / 10;
                }
                catch { }
                if (fee < 1) fee = 1;
                if (!TreasuryManager.Instance.TryPayOneTime(k, fee, "开科取士"))
                {
                    ZhuluContext.Instance.LogWarning($"[Keju] {k.name ?? "本国"} 国用不足（需 {fee} 金），本届停科");
                    ChronicleManager.Instance.AddEntry(year, ChronicleEventType.PolicyEnacted, k.id.ToString(), k.name ?? "本国", null,
                        $"<color=#FF9A76>{k.name}</color> 国用不足（需 {fee} 金），停科举一届。", false);
                    return false;
                }
                TreasuryManager.Instance.RecordExamCost(k, fee);
                return true;
            }
            catch { return true; }
        }

        /// <summary>
        /// P2-30 殿试策论（2026-08-23）：按国情命题——亏空《荒政疏》/交战《平戎策》/
        /// 党争烈度≥0.6《整肃朝纲策》/稳定&lt;40《安民策》/礼治&lt;8《复礼疏》/承平《经义策》。
        /// 策论入王国史册（ImperialExam）+ 状元个人史册——科举自此有"时政感"。
        /// </summary>
        private void RecordPalaceEssay(Kingdom k, string kId, Actor zhuangyuan, int year)
        {
            try
            {
                string topic; string note;
                var rep = TreasuryManager.Instance.GetReport(kId);
                bool atWar = false;
                try { var ws = k.getWars(); if (ws != null) foreach (var _w in ws) { atWar = true; break; } } catch { }
                float stab = StabilityManager.Instance.GetStability(kId);
                float intensity = 0f;
                try { intensity = FactionPoliticsManager.Instance.GetIntensity(k, kId); } catch { }
                float lizhi = ZhuluLiZhiService.GetNetLiZhi(kId);

                if (rep != null && rep.DeficitYears >= 1) { topic = "《荒政疏》"; note = "府库虚竭，问救灾恤患之策"; }
                else if (atWar) { topic = "《平戎策》"; note = "兵凶战危，问攻守进取之道"; }
                else if (intensity >= 0.6f) { topic = "《整肃朝纲策》"; note = "朋党交攻，问弭争靖共之方"; }
                else if (stab < 40f) { topic = "《安民策》"; note = "人心浮动，问固本宁邦之要"; }
                else if (lizhi < 8) { topic = "《复礼疏》"; note = "礼崩乐坏，问正名复礼之序"; }
                else { topic = "《经义策》"; note = "承平之世，问稽古右文之义"; }

                string zName = zhuangyuan.getName() ?? "状元";
                ChronicleManager.Instance.AddEntry(year, ChronicleEventType.ImperialExam,
                    kId, k.name ?? "", zName,
                    $"殿试放榜：<color=#FFD700>{zName}</color> 进呈{topic}——{note}，御笔点为魁首。", false);
                PersonHistoryManager.Instance.RecordEvent(zhuangyuan.getID(), PersonHistoryManager.CAT_GONGMING,
                    $"殿试进呈{topic}，称旨擢魁首", year);
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogRateLimited("Keju.PalaceEssay",
                    "[Keju] 策论生成异常: " + ex.Message, 30f);
            }
        }

        /// <summary>县试（伯国起，5 年一届第 1 年）：无任何功名者报考 → 录取秀才/武秀才。</summary>
        private void RunXianShi(Kingdom k, string kId, KejuSettings s, int year)
        {
            RunLevelKeju(k, kId, s, year, 1);
        }

        /// <summary>乡试（侯国起，第 3 年）：秀才报考 → 录取举人/武举人。</summary>
        private void RunXiangShi(Kingdom k, string kId, KejuSettings s, int year)
        {
            RunLevelKeju(k, kId, s, year, 2);
        }

        /// <summary>殿试（公国/天命，第 5 年）：举人报考 → 录取进士/武进士（榜首状元）。</summary>
        private void RunDianShi(Kingdom k, string kId, KejuSettings s, int year)
        {
            RunLevelKeju(k, kId, s, year, 3);
        }

        /// <summary>
        /// 三级科举通用执行（阶段2E ⑦+）：
        /// 级 1=县试（智力为主，officeIdx 0）、级 2=乡试（+管理，officeIdx 1）、级 3=殿试（+外交，officeIdx 6）。
        /// 候选范围：级 1=无功名者；级 2=秀才；级 3=举人。
        /// </summary>
        private void RunLevelKeju(Kingdom k, string kId, KejuSettings s, int year, int level)
        {
            if (k.units == null) return;
            bool isNomadic = KingdomCivilizationService.Instance.IsNomadic(k);
            int power = KingdomExt.GetPowerValue(kId);
            bool fairMode = power >= 60; // 皇权强=只看评分
            int officeIdx = level == 1 ? 0 : (level == 2 ? 1 : 6); // 逐级加权：吏部→户部→丞相

            // 候选：成年、存活、非国王、非城主（文举/武举）
            // 阶段2R：储君亦不预科（君储不入科场——存档2实锤皇帝带进士特质）
            string heirIdStr = "";
            try { heirIdStr = KingdomDataExt.GetStr(k, "heir_id") ?? ""; } catch { }
            var wenCandidates = new List<(Actor a, int score)>();
            var wuCandidates = new List<(Actor a, int score)>();
            foreach (var u in k.units)
            {
                if (u == null || !u.isAlive()) continue;
                if (u.getAge() < AdultAge) continue;
                if (k.king != null && u.getID() == k.king.getID()) continue;
                if (!string.IsNullOrEmpty(heirIdStr) && u.getID().ToString() == heirIdStr) continue;
                // 逐级筛选（功名门槛）：
                // 级 1：无任何功名；级 2：秀才/武秀才；级 3：举人/武举人
                try
                {
                    bool hasXiu = u.hasTrait(TraitManager.TRAIT_XIUCAI) || u.hasTrait(TraitManager.TRAIT_WUXIUCAI)
                        || u.hasTrait(TraitManager.NOMAD_TRIBE_SCHOLAR) || u.hasTrait(TraitManager.NOMAD_WARRIOR);
                    bool hasJu = u.hasTrait(TraitManager.TRAIT_JUREN) || u.hasTrait(TraitManager.TRAIT_WUJUREN)
                        || u.hasTrait(TraitManager.NOMAD_LEAGUE_SCHOLAR) || u.hasTrait(TraitManager.NOMAD_CENTURION);
                    if (level == 1 && (hasXiu || hasJu)) continue;
                    if (level == 2 && !hasXiu) continue;
                    if (level == 3 && !hasJu) continue;
                }
                catch { }

                int wenScore = ScoreLevel(u, false, power, fairMode, officeIdx);
                int wuScore = ScoreLevel(u, true, power, fairMode, officeIdx);
                if (wenScore > 0) wenCandidates.Add((u, wenScore));
                if (wuScore > 0) wuCandidates.Add((u, wuScore));
            }

            // 文举录取（去重：同一人只录一次；优先已任官者不录）
            // 阶段1 R3：吏部 CivilExamEfficiency 通道 → 录取名额加成（吏部尚书在位 = 录取+1~2）
            int wenQuota = s.Count;
            try
            {
                float civilEff = Gameplay.ModifierPipeline.Instance.EvaluateChannelMultiplier(
                    Gameplay.ModifierChannels.CivilExamEfficiency, kId, 1.0f);
                if (civilEff > 1.01f) wenQuota += (int)Mathf.Round(s.Count * (civilEff - 1f));
            }
            catch { }
            var wenPick = wenCandidates
                .Where(c => !IsOfficial(kId, c.a))
                .OrderByDescending(c => c.score)
                .Take(wenQuota)
                .ToList();
            // 阶段2E ⑦+：功名按考试层级定（级 1=秀才/级 2=举人/级 3=进士+状元）
            int wenCount = 0;
            for (int i = 0; i < wenPick.Count; i++)
            {
                var (a, score) = wenPick[i];
                // 功名特质（阶段2R 归一制：升级移除低级——状元即进士魁首不再叠进士）
                try
                {
                    string civilTrait = isNomadic
                        ? (level == 3 && i == 0 ? TraitManager.NOMAD_FIRST_SCHOLAR
                            : level == 3 ? TraitManager.NOMAD_COURT_SCHOLAR
                            : level == 2 ? TraitManager.NOMAD_LEAGUE_SCHOLAR : TraitManager.NOMAD_TRIBE_SCHOLAR)
                        : (level == 3 && i == 0 ? TraitManager.TRAIT_ZHUANGYUAN
                            : level == 3 ? TraitManager.TRAIT_JINSHI
                            : level == 2 ? TraitManager.TRAIT_JUREN : TraitManager.TRAIT_XIUCAI);
                    ReplaceGongmingTraits(a, civilTrait);
                    // P2-30 串联④·游牧适配：传特质 id 走等级判定
                    if (level == 3)
                        FamilyRegistry.Instance.NotifyExamPass(kId, a.getID(), civilTrait);
                }
                catch { }
                AddToTalentPool(kId, a, score, year, false);
                PersonHistoryManager.Instance.MarkDetailed(a.getID(), year);
                PersonHistoryManager.Instance.RecordEvent(a.getID(), PersonHistoryManager.CAT_GONGMING,
                    $"开科取士，文举及第（{k.name}）", year);
                // 阶段2N W8 科举衔接：殿试及第直接授地方官（状元→郡守；进士→县丞；席满则留人才库等年度补位）
                if (level == 3)
                {
                    TryPlaceGraduateInLocalOffice(k, kId, a, i == 0, year);
                    // P2-30 殿试策论：状元按国情命题，策论入王国史册与个人史册
                    if (i == 0) RecordPalaceEssay(k, kId, a, year);
                }
                wenCount++;
            }

            // 武举录取：首名城主，其余队长
            var wuPick = wuCandidates
                .OrderByDescending(c => c.score)
                .Take(s.Count)
                .ToList();
            int wuCount = 0;
            if (wuPick.Count > 0)
            {
                // 阶段2E ⑦+：武功名按层级（级 1=武秀才/级 2=武举人/级 3=武进士）
                for (int i = 0; i < wuPick.Count; i++)
                {
                    var (a, score) = wuPick[i];
                    // 阶段2R 归一制：武举升级同样移除低级（文武功名只保留最高一项）
                    try
                    {
                        string militaryTrait = isNomadic
                            ? (level == 3 ? TraitManager.NOMAD_CHILIARCH : level == 2 ? TraitManager.NOMAD_CENTURION : TraitManager.NOMAD_WARRIOR)
                            : (level == 3 ? TraitManager.TRAIT_WUJINSHI : level == 2 ? TraitManager.TRAIT_WUJUREN : TraitManager.TRAIT_WUXIUCAI);
                        ReplaceGongmingTraits(a, militaryTrait);
                        // P2-30 串联④·游牧适配：传特质 id 走等级判定
                        if (level == 3)
                            FamilyRegistry.Instance.NotifyExamPass(kId, a.getID(), militaryTrait);
                    }
                    catch { }
                }
                var first = wuPick[0];
                if (TryAppointCityLeader(k, kId, first.a, first.score, year))
                {
                    wuCount++;
                }
                else
                {
                    // 2026-08-18 修复：首名城主任命失败（无空城/冷却）不再丢失——照列入人才库+授队长
                    AddToTalentPool(kId, first.a, first.score, year, true);
                    PersonHistoryManager.Instance.MarkDetailed(first.a.getID(), year);
                    MarkCaptain(k, kId, first.a, year);
                    wuCount++;
                }
                for (int i = 1; i < wuPick.Count; i++)
                {
                    var (a, score) = wuPick[i];
                    AddToTalentPool(kId, a, score, year, true);
                    PersonHistoryManager.Instance.MarkDetailed(a.getID(), year);
                    MarkCaptain(k, kId, a, year);
                    wuCount++;
                }
            }

            // 史册
            if (wenCount > 0 || wuCount > 0)
            {
                ChronicleManager.Instance.AddEntry(year, ChronicleEventType.PolicyEnacted, kId, k.name, null,
                    isNomadic
                        ? $"<color=#c8b98a>{k.name}</color> 举行部族选拔：贤士{wenCount}人入人才库，勇士{wuCount}人授职。"
                        : $"<color=#6ca3fd>{k.name}</color> 开科取士：文举{wenCount}人入人才库，武举{wuCount}人授职。", true);
                string levelName = isNomadic
                    ? (level == 1 ? "部族选拔" : level == 2 ? "部盟选拔" : "汗庭选拔")
                    : (level == 1 ? "县试" : level == 2 ? "乡试" : "殿试");
            ZhuluContext.Instance.LogInfo($"[Keju] {k.name} {levelName}: 文{wenCount} 武{wuCount} (权力{power}, 公平模式={fairMode})");
            }
        }

        /// <summary>
        /// 阶段2N W8：科举衔接地方官——殿试及第者直接授职（状元→郡守·候选县丞；进士→县丞）。
        /// 对齐功名文案设计："进士可授县丞/郡守（政绩起点50）/状元直接授郡守（政绩起点60）"。
        /// 席满（在任者存活）不抢占，留人才库等年度吏治补位；刺史留给升迁链（不跃级授官）。
        /// </summary>
        private bool TryPlaceGraduateInLocalOffice(Kingdom k, string kId, Actor a, bool isZhuangyuan, int year)
        {
            try
            {
                if (a == null || !a.isAlive()) return false;
                string tierId = KingdomCivilizationService.Instance.IsNomadic(k)
                    ? NomadicTierService.GetTierId(ZhuluTierService.GetPowerPoints(k))
                    : ZhuluTierService.GetTierId(ZhuluTierService.GetPowerPoints(k));
                string[] localSeats = OfficeManager.GetLocalSeats(tierId);
                if (localSeats == null || localSeats.Length == 0) return false;

                var targets = isZhuangyuan
                    ? new[] { OfficeManager.LOCAL_JUNSHOU, OfficeManager.LOCAL_XIANCHENG }
                    : new[] { OfficeManager.LOCAL_XIANCHENG };
                foreach (var targetId in targets)
                {
                    if (System.Array.IndexOf(localSeats, targetId) < 0) continue;

                    var app = OfficeManager.Instance.GetAppointment(kId, targetId);
                    if (app != null && !string.IsNullOrEmpty(app.PersonId))
                    {
                        bool alive = false;
                        try
                        {
                            var p = long.TryParse(app.PersonId, out long pid) ? World.world?.units?.get(pid) : null;
                            alive = p != null && p.isAlive();
                        }
                        catch { }
                        if (alive) continue; // 在任存活：不抢占
                        OfficeManager.Instance.DismissOfficer(kId, k.name ?? "", targetId, year, "亡故");
                    }

                    OfficeManager.Instance.AppointOfficer(kId, k.name ?? "", targetId,
                        a.getID().ToString(), a.getName() ?? "士子", year, isPlayer: false);

                    // 辖城分配（与年度地方官补位同口径：全部非首都城）
                    try
                    {
                        var juris = new System.Collections.Generic.List<string>();
                        if (k.cities != null)
                        {
                            foreach (var c in k.cities)
                            {
                                if (c == null) continue;
                                if (k.capital != null && c.id == k.capital.id) continue;
                                juris.Add(c.id.ToString());
                            }
                        }
                        var rec = OfficeManager.Instance.GetAppointment(kId, targetId);
                        if (rec != null) rec.JurisdictionCities = string.Join(",", juris);
                    }
                    catch { }

                    PersonHistoryManager.Instance.RecordEvent(a.getID(), PersonHistoryManager.CAT_GONGMING,
                        $"金榜题名，直接授任{OfficeManager.GetOfficeTitleShort(targetId)}（{k.name}）", year);
                    ZhuluContext.Instance.LogInfo($"[Keju] 科举衔接授职: {a.getName()} → {OfficeManager.GetOfficeTitleShort(targetId)}（{(isZhuangyuan ? "状元" : "进士")}）");
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[Keju] 科举衔接地方官异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>综合评分：CalcOfficialScore 文/武权重 + 特质加成 + 皇权寒门联动。</summary>
        private int Score(Actor a, bool branchWu, int power, bool fairMode)
        {
            return ScoreLevel(a, branchWu, power, fairMode, branchWu ? 3 : 0);
        }

        private int ScoreLevel(Actor a, bool branchWu, int power, bool fairMode, int officeIdx)
        {
            try
            {
                if (branchWu) officeIdx = 3; // 武举恒兵部权重（军事为主）
                int aAge2 = 0; try { aAge2 = a.getAge(); } catch { }
                var (baseScore, _) = OfficialRatingManager.CalcOfficialScore(a, officeIdx, aAge2); // 阶段2B 方案3

                // 阶段3D 贤能轨（游牧文选）：四维文系权重 + 声望renown(封顶30) + 仁君/奇才特质加成
                try
                {
                    if (a.kingdom != null && Domain.KingdomCivilizationService.Instance.IsNomadic(a.kingdom))
                    {
                        int ren = 0; try { ren = a.renown; } catch { }
                        baseScore += System.Math.Min(ren, 30);
                        if (a.hasTrait(TraitManager.TRAIT_BENEVOLENT)) baseScore += 8;
                        if (a.hasTrait(TraitManager.TRAIT_GENIUS)) baseScore += 8;
                    }
                }
                catch { }

                // 特质加成（逐鹿特质四维加成和 ×0.6）
                float traitBonus = 0f;
                if (a.traits != null)
                {
                    foreach (var t in a.traits)
                    {
                        if (t != null) traitBonus += TraitManager.GetTraitBonus(t.id);
                    }
                }
                int score = baseScore + Mathf.RoundToInt(traitBonus * 0.6f);

                // 2026-08-18 武举战功项：击杀（每 8 首级 +1，封顶 25）+ 伤害（每 5 点 +1，封顶 10）——
                // 与三榜武将榜同口径（OfficialRatingManager.GetWarMeritBonus），弓马实艺入试
                if (branchWu)
                {
                    score += OfficialRatingManager.GetWarMeritBonus(a);
                }

                // 皇权联动寒门机制：皇权弱 → 世家评分加成
                if (!fairMode)
                {
                    bool isShijia = a.clan != null;
                    if (isShijia)
                    {
                        score += Mathf.RoundToInt((60 - power) * 0.3f);
                    }
                }

                // 2026-08-19 串联：家风科场加成（家族反向影响科举——描述"经学传家族内科举评分+5"成真）
                // 经学：文举 +5；将门：武举 +5；忠孝：+2；奢靡：-2（纨绔不振）
                try
                {
                    string kid = a.kingdom?.id.ToString();
                    if (!string.IsNullOrEmpty(kid))
                    {
                        var fam = FamilyRegistry.Instance.FindFamilyOfActor(kid, a.getID());
                        if (fam != null && !string.IsNullOrEmpty(fam.Style))
                        {
                            if (fam.Style == "jingxue" && !branchWu) score += 5;
                            else if (fam.Style == "jiangmen" && branchWu) score += 5;
                            else if (fam.Style == "zhongxiao") score += 2;
                            else if (fam.Style == "shemi") score -= 2;
                        }
                    }
                }
                catch { }
                // P1-A4 卷面扰动（2026-08-23，玄鉴式确定性哈希骰）：一次性临场发挥——
                // 数值按逐鹿口径取 ±6：与家风(±5)同量级、不淹没特质/寒门等策略分；
                // 同科(年)同人恒定（防SL可回放），文/武分盐互不干扰；落第只失此科，不删人。
                try
                {
                    score += DeterministicRandom.PerturbPercent(
                        a.getID(), ZhuluTime.CurrentYear, branchWu ? "keju_wu" : "keju_wen", 6);
                }
                catch { }
                return Mathf.Clamp(score, 0, 100);
            }
            catch { return 0; }
        }

        private static bool IsOfficial(string kId, Actor a)
        {
            try
            {
                var records = OfficeManager.Instance.GetRecordsPublic(kId);
                if (records == null) return false;
                long aid = a.getID();
                foreach (var r in records)
                {
                    if (r.PersonId == aid.ToString()) return true;
                }
                return false;
            }
            catch { return false; }
        }

        // ═══════════════════════ 人才库 ═══════════════════════

        private void AddToTalentPool(string kId, Actor a, int score, int year, bool branchWu)
        {
            if (!_talentPool.TryGetValue(kId, out var list))
            {
                list = new List<TalentEntry>();
                _talentPool[kId] = list;
            }
            long aid = a.getID();
            list.RemoveAll(e => e.ActorId == aid); // 去重（新录取覆盖旧）
            list.Add(new TalentEntry { ActorId = aid, Name = a.getName() ?? "贤士", Score = score, Year = year, BranchWu = branchWu });
        }

        /// <summary>官位空缺 → 人才库按评分补位（只补文举；武举已授职）。</summary>
        private void FillVacancies(Kingdom k, string kId, KejuSettings s, int year)
        {
            if (!_talentPool.TryGetValue(kId, out var pool) || pool.Count == 0) return;
            if (k.cities == null) return;
            int eff = KingdomExt.GetEffectiveCities(kId, k.cities.Count);
            bool isMandate = PolicyManager.IsMandateHolderForKingdom(k);
            var seats = OfficeManager.GetExpectedSeatsWithMandate(k, isMandate);
            if (seats == null) return;

            foreach (var officeId in seats)
            {
                if (OfficeManager.Instance.GetAppointment(kId, officeId) != null) continue; // 已有人

                // 找人才库最高分且存活者（阶段1 G1：文武分科——兵部席武举优先，文官席文举优先）
                bool isMilitarySeat = officeId == "military";
                var best = pool
                    .Where(e => IsAlive(e.ActorId) && !IsOfficial(kId, GetActor(e.ActorId)))
                    .OrderByDescending(e => isMilitarySeat ? (e.BranchWu ? 1 : 0) : (e.BranchWu ? 0 : 1))
                    .ThenByDescending(e => e.Score)
                    .FirstOrDefault();
                if (best == null) break;

                string name = best.Name;
                string title = OfficeManager.GetOfficeTitleForKingdom(officeId, k);
                if (OfficeManager.Instance.AppointOfficer(kId, k.name, officeId, best.ActorId.ToString(), name, year, false))
                {
                    pool.Remove(best);
                    PersonHistoryManager.Instance.RecordEvent(best.ActorId, PersonHistoryManager.CAT_SHITU,
                        $"自人才库补位为【{title}】（{k.name}）", year);
                    ZhuluContext.Instance.LogInfo($"[Keju] 人才库补位: {name} -> {title} ({k.name})");
                }
            }
        }

        // ═══════════════════════ 武举城主 ═══════════════════════

        /// <summary>武举首名任命城主：冷却（每城10年）+ 现任劣质（评分低）才可替换。</summary>
        private bool TryAppointCityLeader(Kingdom k, string kId, Actor candidate, int candidateScore, int year)
        {
            try
            {
                if (k.cities == null || k.cities.Count == 0) return false;
                // 2026-08-16 修复：武举授城主筛年龄（<55——防授后立即告老）+ 已任城主检查（防同一人授多城）
                int cAge = 0;
                try { cAge = candidate.getAge(); } catch { }
                if (cAge >= 55) return false;
                long cid0 = candidate.getID();
                foreach (var cc in k.cities)
                {
                    if (cc != null && cc.data.leaderID.hasValue() && cc.data.leaderID == cid0) return false;
                }
                if (!_cityCool.TryGetValue(kId, out var cool)) { cool = new Dictionary<long, int>(); _cityCool[kId] = cool; }

                // 目标：无主城优先；否则找"劣质城主"的可替换城
                City target = null;
                foreach (var c in k.cities)
                {
                    if (c == null) continue;
                    if (!c.data.leaderID.hasValue()) { target = c; break; } // 空城直接任命
                }
                if (target == null)
                {
                    // 找劣质现任 + 冷却到期
                    foreach (var c in k.cities)
                    {
                        if (c == null || !c.data.leaderID.hasValue()) continue;
                        long cityId = c.id;
                        if (cool.TryGetValue(cityId, out int until) && year <= until) continue; // 冷却中
                        Actor cur = World.world.units.get(c.data.leaderID);
                        if (cur == null || !cur.isAlive()) { target = c; break; } // 现任死亡
                var (curScore, _) = OfficialRatingManager.CalcOfficialScore(cur, 3); // 阶段2B 方案3
                        if (curScore < candidateScore) { target = c; break; } // 现任劣质
                    }
                }

                if (target == null) return false; // 无可替换

                // 任命（武官派执政保护在党争阶段接入）
                target.setLeader(candidate, false);
                cool[target.id] = year + CityReplaceCooldownYears;
                string title = GetLeaderTitle(kId, target);
                PersonHistoryManager.Instance.MarkDetailed(candidate.getID(), year);
                PersonHistoryManager.Instance.RecordEvent(candidate.getID(), PersonHistoryManager.CAT_SHITU,
                    $"武举第一，授【{title}】（{k.name}·{target.name}）", year);
                ZhuluContext.Instance.LogInfo($"[Keju] 武举授城主: {candidate.getName()} -> {target.name} ({k.name})");
                return true;
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[Keju] TryAppointCityLeader 异常: {ex.Message}");
                return false;
            }
        }

        private string GetLeaderTitle(string kId, City c)
        {
            try
            {
                // 2026-08-18 特质权威版（爵位特质优先，城数近似兜底）
                Kingdom lk = c.data != null && c.data.kingdomID > 0 && World.world?.kingdoms != null
                    ? World.world.kingdoms.get(c.data.kingdomID) : null;
                if (lk != null) return RegimeManager.GetCaptainTitle(lk);
                int cityCount = lk != null && lk.cities != null ? lk.cities.Count : 1;
                return RegimeManager.GetCaptainTitle(cityCount);
            }
            catch { return "城守"; }
        }

        // ═══════════════════════ 工具 ═══════════════════════

        private static Actor GetActor(long id)
        {
            try { return World.world.units.get(id); } catch { return null; }
        }

        private static bool IsAlive(long id)
        {
            var a = GetActor(id);
            return a != null && a.isAlive();
        }

        private void Cleanup(string kId)
        {
            // 阶段3D 军功轨（游牧）：击杀阈值实时晋身——5杀勇士/15杀百夫长/40杀千夫长；千夫长入人才库
            try
            {
                Kingdom mk = KingdomDataExt.FindKingdomById(kId);
                if (mk != null && mk.isAlive() && Domain.KingdomCivilizationService.Instance.IsNomadic(mk) && mk.units != null)
                {
                    string[] milTraits = { TraitManager.NOMAD_WARRIOR, TraitManager.NOMAD_CENTURION, TraitManager.NOMAD_CHILIARCH };
                    int[] thresholds = { 5, 15, 40 };
                    foreach (var u in mk.units)
                    {
                        if (u == null || !u.isAlive() || u.getAge() < AdultAge) continue;
                        int kills = 0;
                        try { kills = OfficialRatingManager.GetActorKills(u); } catch { }
                        int tier = kills >= 40 ? 2 : kills >= 15 ? 1 : kills >= 5 ? 0 : -1;
                        if (tier < 0) continue;
                        string target = milTraits[tier];
                        bool changed = false;
                        for (int i = 0; i < milTraits.Length; i++)
                        {
                            try
                            {
                                if (i == tier) { if (!u.hasTrait(milTraits[i])) { u.addTrait(milTraits[i]); changed = true; } }
                                else if (u.hasTrait(milTraits[i])) { u.removeTrait(milTraits[i]); changed = true; }
                            }
                            catch { }
                        }
                        if (tier == 2 && (changed || !IsOfficial(kId, u)))
                        {
                            try { AddToTalentPool(kId, u, 40 + System.Math.Min(kills, 60), ZhuluTime.CurrentYear, false); } catch { }
                        }
                    }
                    ZhuluContext.Instance.LogInfo($"[Keju] 游牧军功轨年度判定完成: {mk.name}");
                }
            }
            catch { }

            // 人才库清理（死亡/已任官移除）
            if (_talentPool.TryGetValue(kId, out var pool))
            {
                pool.RemoveAll(e => !IsAlive(e.ActorId) || IsOfficial(kId, GetActor(e.ActorId)));
            }
            // 队长记录清理
            if (_captainMarks.TryGetValue(kId, out var caps))
            {
                caps.RemoveAll(c => !IsAlive(c.ActorId));
            }
            // 阶段2R：存量功名归一（旧档堆叠自愈——只留最高一项）
            try { NormalizeGongming(KingdomDataExt.FindKingdomById(kId)); } catch { }
        }

        // 阶段1 G1：移除 MarkCaptain 死代码（定义后从未被调用；CaptainMark 数据结构与快照保留兼容）

        // ═══════════════════════ 快照 ═══════════════════════

        // ---- batch10-A1: schema v5 section ----
        public sealed class KejuState
        {
            public Dictionary<string, KejuSettings> Settings = new Dictionary<string, KejuSettings>();
            public Dictionary<string, List<TalentEntry>> TalentPool = new Dictionary<string, List<TalentEntry>>();
            public Dictionary<string, List<CaptainMark>> Captains = new Dictionary<string, List<CaptainMark>>();
            public Dictionary<string, Dictionary<long, int>> CityCool = new Dictionary<string, Dictionary<long, int>>();
        }
        public string SectionName => "keju";
        public Dictionary<string, KejuState> CaptureStates()
        {
            var st = new KejuState
            {
                Settings = new Dictionary<string, KejuSettings>(_settings),
                TalentPool = new Dictionary<string, List<TalentEntry>>(_talentPool),
                Captains = new Dictionary<string, List<CaptainMark>>(_captainMarks),
                CityCool = new Dictionary<string, Dictionary<long, int>>(_cityCool),
            };
            return new Dictionary<string, KejuState> { ["__global__"] = st };
        }
        public void ApplyStates(Dictionary<string, KejuState> data)
        {
            if (data == null || !data.TryGetValue("__global__", out var st) || st == null) return;
            ResetRuntime();
            if (st.Settings != null) foreach (var kv in st.Settings) if (kv.Value != null) _settings[kv.Key] = kv.Value;
            if (st.TalentPool != null) foreach (var kv in st.TalentPool) if (kv.Value != null) _talentPool[kv.Key] = kv.Value;
            if (st.Captains != null) foreach (var kv in st.Captains) if (kv.Value != null) _captainMarks[kv.Key] = kv.Value;
            if (st.CityCool != null) foreach (var kv in st.CityCool) if (kv.Value != null) _cityCool[kv.Key] = kv.Value;
        }



        public void MigrateFromCustomData() { }

        public void ResetForNewWorld() => ResetRuntime();

        public void ResetRuntime()
        {
            _settings.Clear(); _talentPool.Clear(); _captainMarks.Clear(); _cityCool.Clear(); _lastKejuYear.Clear();
        }

        /// <summary>测试辅助。</summary>
        public void ClearForTest() => ResetRuntime();
    }
}
