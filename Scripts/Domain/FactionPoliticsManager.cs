using System;
using System.Collections.Generic;
using System.Linq;
using ZhuluRebuild.Core;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Domain
{
    /// <summary>
    /// 党争数据层（阶段2·定案 2026-08）。
    /// 依赖 FactionManager 的固定 4 党骨架（皇权/文官/武官/太子），在其上实现：
    ///   亲信度（Favor 0~100）：皇权党=皇帝 + Favor≥60 自动 + 皇帝每年提拔 1 人（+15）
    ///   五事件：构陷(10年)/拉拢(5年)/倾轧(无冷却)/清洗(15年)/政变=废立制(20年)
    ///   世仇：跨代挂在党对上，值≥20 可雪恨（10年冷却）
    ///   烈度=(最大党影响−次大党影响)/总影响；执政党=影响力最高党
    ///   国策影响 ±20%（朝政/天命→皇权党，军事→武官派，经济/社会→文官派，族政→太子党）
    ///   侯级以上才党争；旧档不生成（_freshWorld 保护，与 FactionManager 一致）
    /// 政变（废立制）：发起=太子党或自建党，党魁权势≥65 && 皇权党党势占比<25% &&
    ///   皇帝在位≥10年 && 距上次政变≥20年 && 30% 概率；
    ///   新君=储君→同族宗藩→政变者本人（外姓兜底），不改国号，政变者成权臣（Favor=100 入皇权党）。
    /// 纯逻辑（ClampFavor/IsCooled/AddFeud/ShouldTriggerCoup/PolicyFactor）可独立测试，无 Unity 依赖。
    /// </summary>
    public class FactionPoliticsManager : ISnapshotSection<FactionPoliticsManager.PoliticsSectionState>
    {
        public static FactionPoliticsManager Instance { get; } = new FactionPoliticsManager();

        private bool Enabled => ZhuluConfig.Instance.FactionPoliticsEnabled;

        // ═══════════════════════ 状态结构（每国 JSON 序列化） ═══════════════════════

        public class PoliticsState
        {
            public Dictionary<long, int> Favor = new Dictionary<long, int>();        // 官员ID → 亲信度 0~100
            public Dictionary<string, int> EventCool = new Dictionary<string, int>(); // "事件key" → 上次触发年（-999=未触发）
            public Dictionary<string, int> Feuds = new Dictionary<string, int>();     // "党A|党B" → 世仇值
            public Dictionary<string, int> FeudCool = new Dictionary<string, int>();  // "党A|党B" → 上次雪恨年
            public int LastCoupYear = -999;
            public int CoupCount = 0;
            // 阶段2T-1：政治安置（提拔/转投/政变入伙）——党籍由年度重建据此保留，防"提拔次年蒸发"与"多党籍"并存
            public Dictionary<long, string> Placed = new Dictionary<long, string>();  // 官员ID → 党名
            // 阶段2T-1：近事滚动日志（UI 总览页"党争近事"，上限 8 条）
            public List<string> Recent = new List<string>();
        }

        // 事件 key / 冷却（定案）
        public const string EVT_GOUXIAN = "gouxian";   // 构陷：执政党对最大反对党
        public const string EVT_LALUO = "laluo";       // 拉拢：反对党拉皇权党成员
        public const string EVT_QINGXI = "qingxi";     // 清洗：执政党罢免敌对党官员
        public const string EVT_COUP = "coup";         // 政变：废立制
        public const int COOL_GOUXIAN = 10;
        public const int COOL_LALUO = 5;
        public const int COOL_QINGXI = 15;
        public const int COOL_COUP = 20;
        public const int COOL_FEUD = 10;
        // 阶段3D 党争风味事件（东方4+游牧4）
        public const string EVT_QINGLIU = "qingliu";     // 东·清流议政
        public const string EVT_WAIQI = "waiqi";         // 东·外戚干政
        public const string EVT_MENSHENG = "mensheng";   // 东·门生故吏
        public const string EVT_HUANGUAN = "huanguan";   // 东·宦官近幸
        public const string EVT_KD = "kuriltai";         // 游·库里尔台夺嫡
        public const string EVT_SHAMAN = "shaman";       // 游·萨满谶言
        public const string EVT_BULUO = "buluo";         // 游·部落叛离
        public const string EVT_ZHENGLI = "zhengli";     // 游·汗位争立
        public const int COOL_FLAVOR = 12;

        public const int FAVOR_AUTO_JOIN = 60;   // 亲信度≥60 自动入皇权党
        public const int FAVOR_PROMOTE = 15;     // 皇帝提拔 +15
        public const int FAVOR_GOUXIAN_HIT = -20;
        public const int FAVOR_LALUO_GAIN = 15;
        public const int FAVOR_YEARLY = 2;       // 在朝每年 +2
        public const int FAVOR_QIANGYA = -5;     // 倾轧波及 −5

        public const float TRADITION_CAP = 40f;  // TraditionBonus 账面党势上限（年结/朝议/战争回写共用，原四处硬编码 40）

        public const int COUP_MIN_LEADER_POWER = 65;  // 政变者权势门槛（阶段2U：75→65 对齐权势增速）
        public const float COUP_MAX_ROYAL_SHARE = 0.25f; // 皇权党党势占比上限（D5-② 2026-10-06 相对口径：绝对值25被党势通胀击穿——D批实测皇权党账面党势57-162、政变0/184国年）[PLACEHOLDER] playtest 验证
        public const int COUP_MIN_KING_YEARS = 10;    // 皇帝在位下限
        public const float COUP_PROBABILITY = 0.30f;

        public const int FEUD_SNOWBALL_THRESHOLD = 20; // 世仇≥20 可雪恨

        private readonly Dictionary<string, PoliticsState> _states = new Dictionary<string, PoliticsState>();
        private bool _freshWorld = false;
        /// <summary>测试窥视：新世界标志（批次10-A1 单测断言用）。</summary>
        public bool FreshWorldForTest => _freshWorld;
        // 2026-08-19 朝议议题避重记忆（kId → 上年议题）；内存态——重载丢失只影响一次选择，无数据风险
        private readonly Dictionary<string, string> _lastDebateTopic = new Dictionary<string, string>();

        private FactionPoliticsManager() { }

        // ═══════════════════════ 纯逻辑（可独立测试，无 Unity） ═══════════════════════

        public static int ClampFavor(int v) => Math.Max(0, Math.Min(100, v));

        /// <summary>事件冷却判定：未触发过 或 距上次触发 ≥ 冷却年。</summary>
        public static bool IsCooled(PoliticsState st, string key, int cooldown, int year)
        {
            if (st == null) return true;
            if (!st.EventCool.TryGetValue(key, out int last)) return true;
            return year - last >= cooldown;
        }

        /// <summary>记录事件触发年。</summary>
        public static void NoteEvent(PoliticsState st, string key, int year)
        {
            if (st == null) return;
            st.EventCool[key] = year;
        }

        /// <summary>世仇累积（双向同一数值，挂党对上——跨代继承天然成立）。</summary>
        public static void AddFeud(PoliticsState st, string factionA, string factionB, int amount)
        {
            if (st == null || string.IsNullOrEmpty(factionA) || string.IsNullOrEmpty(factionB)) return;
            if (factionA == factionB) return;
            string key = FeudKey(factionA, factionB);
            st.Feuds.TryGetValue(key, out int cur);
            st.Feuds[key] = Math.Max(0, cur + amount);
        }

        public static int GetFeud(PoliticsState st, string factionA, string factionB)
        {
            if (st == null || string.IsNullOrEmpty(factionA) || string.IsNullOrEmpty(factionB)) return 0;
            if (factionA == factionB) return 0;
            st.Feuds.TryGetValue(FeudKey(factionA, factionB), out int v);
            return v;
        }

        public static string FeudKey(string a, string b) => string.CompareOrdinal(a, b) < 0 ? a + "|" + b : b + "|" + a;

        /// <summary>政变触发判定（纯布尔）：党魁权势≥65 && 皇权党党势占比<25% && 在位≥10 && 冷却≥20 && 概率。</summary>
        public static bool ShouldTriggerCoup(float coupLeaderPower, float royalPower, float totalFactionPower, int kingYears, int lastCoupYear, int year, float rand01)
        {
            if (coupLeaderPower < COUP_MIN_LEADER_POWER) return false;
            // D5-②（2026-10-06）皇权党关改相对口径：绝对上限 25 被党势通胀击穿（D批实测皇权党账面
            // 党势 57-162、政变 0/184 国年）——改查皇权党党势占全体党势比例（皇权党计入分母）。
            // 总党势≤0 = 无党争生态，不成政变。[PLACEHOLDER] 份额 25%——playtest 验证
            // （失败信号：政变 >1次/国/50年 或 全程 0）
            if (totalFactionPower <= 0f) return false;
            if (royalPower / totalFactionPower >= COUP_MAX_ROYAL_SHARE) return false;
            if (kingYears < COUP_MIN_KING_YEARS) return false;
            if (year - lastCoupYear < COOL_COUP) return false;
            return rand01 < COUP_PROBABILITY;
        }

        /// <summary>国策对党影响力系数（±20%）：朝政/天命→皇权党，军事→武官派，经济/社会→文官派，族政→太子党，邦政→无。</summary>
        public static float PolicyFactor(string categoryTitle, FactionManager.FactionType type)
        {
            if (string.IsNullOrEmpty(categoryTitle)) return 1f;
            switch (type)
            {
                case FactionManager.FactionType.HuangQuan:
                    return (categoryTitle.Contains("朝政") || categoryTitle.Contains("天命")) ? 1.2f : 1f;
                case FactionManager.FactionType.WuGuan:
                    return categoryTitle.Contains("军事") ? 1.2f : 1f;
                case FactionManager.FactionType.WenGuan:
                    return (categoryTitle.Contains("经济") || categoryTitle.Contains("社会")) ? 1.2f : 1f;
                case FactionManager.FactionType.TaiZi:
                    return categoryTitle.Contains("族政") ? 1.2f : 1f;
                default:
                    return 1f;
            }
        }

        public static string GetFavorGrade(int favor)
        {
            if (favor >= 80) return "心腹";
            if (favor >= 60) return "亲信";
            if (favor >= 40) return "寻常";
            if (favor >= 20) return "疏远";
            return "疑忌";
        }

        // ═══════════════════════ 政治安置（阶段2T-1） ═══════════════════════

        /// <summary>取某国安置表（actorId → 党名）。无状态返回空表（不缓存，读时拷贝）。</summary>
        public Dictionary<long, string> GetPlacements(string kingdomId)
        {
            var st = GetState(kingdomId);
            return st?.Placed ?? new Dictionary<long, string>();
        }

        /// <summary>安置某人入党（政治事件专用：提拔/转投/政变入伙——年度重建据此保留党籍）。</summary>
        public void SetPlacement(string kingdomId, long actorId, string factionName)
        {
            var st = GetState(kingdomId);
            if (st == null || string.IsNullOrEmpty(factionName)) return;
            st.Placed[actorId] = factionName;
        }

        /// <summary>清除安置（清洗罢官后回归身份归属）。</summary>
        public void ClearPlacement(string kingdomId, long actorId)
        {
            var st = GetState(kingdomId);
            if (st == null) return;
            st.Placed.Remove(actorId);
        }

        /// <summary>近事滚动日志（上限 8 条，最新在前）。格式："建元N年 文本"。</summary>
        public static void AddRecent(PoliticsState st, int year, string text)
        {
            if (st == null || string.IsNullOrEmpty(text)) return;
            st.Recent.Insert(0, $"建元{year}年 {text}");
            if (st.Recent.Count > 8) st.Recent.RemoveRange(8, st.Recent.Count - 8);
        }

        // ═══════════════════════ 公开查询（UI 用） ═══════════════════════

        public PoliticsState GetState(string kingdomId)
        {
            _states.TryGetValue(kingdomId, out var st);
            return st;
        }

        public int GetFavor(string kingdomId, long actorId)
        {
            var st = GetState(kingdomId);
            if (st == null) return DefaultFavor;
            if (!st.Favor.TryGetValue(actorId, out int v)) return DefaultFavor; // 未打交道=寻常（阶段2T-1：原 0"疑忌"观感误导）
            return ClampFavor(v);   // 读取兜底：旧版无上限快照残留（如 160）立即修正为 0~100
        }

        /// <summary>无亲信记录者的默认值（寻常档）。</summary>
        public const int DefaultFavor = 50;

        /// <summary>党影响力 = Σ成员权势 + 成员数×10 + 账面党势（传统分量）+ 国策±20%。成员权势取 OfficeManager 记录 → CorruptionManager.GetPower。</summary>
        public float ComputeFactionPower(Kingdom k, string kId, FactionManager.FactionRecord faction)
        {
            if (k == null || faction == null) return 0f;
            // 2026-08-19 串联：账面党势（传统分量）入总党势——防成员死亡党势跳水（账面制思路）
            float power = faction.MemberIds.Count * 10f + faction.TraditionBonus;
            var records = OfficeManager.Instance.GetRecordsPublic(kId);
            if (records != null)
            {
                foreach (var r in records)
                {
                    if (long.TryParse(r.PersonId, out long pid) && faction.MemberIds.Contains(pid))
                    {
                        power += CorruptionManager.Instance.GetPower(kId, r.OfficeId);
                    }
                }
            }
            // 国策 ±20%
            try
            {
                var acts = PolicyManager.Instance.GetActivePolicies(kId);
                if (acts != null)
                {
                    foreach (var a in acts)
                    {
                        if (a == null) continue;
                        string title = null;
                        try
                        {
                            var def = PolicyManager.Instance.GetDefinition(a.PolicyId);
                            if (def != null) title = def.CategoryTitle;
                        }
                        catch { }
                        power *= PolicyFactor(title, faction.Type);
                    }
                }
            }
            catch { }
            return power;
        }

        /// <summary>执政党 = 影响力最高党。</summary>
        public FactionManager.FactionRecord GetRulingFaction(Kingdom k, string kId)
        {
            var factions = FactionManager.Instance.GetAllFactions(kId);
            if (factions.Count == 0) return null;
            FactionManager.FactionRecord best = null;
            float bestPower = -1f;
            foreach (var f in factions)
            {
                float p = ComputeFactionPower(k, kId, f);
                if (p > bestPower) { bestPower = p; best = f; }
            }
            return best;
        }

        public string GetRulingFactionName(Kingdom k, string kId)
        {
            var r = GetRulingFaction(k, kId);
            if (r == null) return "无";
            // 阶段2V：显示名按国家位阶（天子:皇权党/公:公室党/侯:侯府党）
            return FactionManager.GetFactionDisplayName(k, r);
        }

        // ═══════════════════════ 2026-08-19 串联：执政党立场对外查询（战争/政策挂点用） ═══════════════════════

        /// <summary>某国执政党在某轴的立场（0~1；无党争/异常回退 0.5 中立）。</summary>
        public static float GetRulingStanceAxis(Kingdom k, int axis)
        {
            try
            {
                if (k == null || !k.isAlive()) return 0.5f;
                string kId = k.id.ToString();
                var ruling = Instance.GetRulingFaction(k, kId);
                if (ruling == null) return 0.5f;
                return FactionManager.GetStanceAxis(ruling, axis);
            }
            catch { return 0.5f; }
        }

        /// <summary>
        /// 串联：战争食欲系数——执政党战争轴 ≥0.7（主战党）→ 1.3（更易开战）；
        /// ≤0.3（主和党）→ 0.7（更不易开战）；无党争 → 1.0。
        /// 挂点：AI 宣战概率（全面战争/独立战争/宣称战）与朝议战和之议。
        /// </summary>
        public static float GetWarAppetiteFactor(Kingdom k)
        {
            try
            {
                float axis = GetRulingStanceAxis(k, FactionManager.ST_WAR);
                if (axis >= 0.7f) return 1.3f;
                if (axis <= 0.3f) return 0.7f;
                return 1f;
            }
            catch { return 1f; }
        }

        /// <summary>烈度 = (最大−次大)/总。0~1。</summary>
        public float GetIntensity(Kingdom k, string kId)
        {
            var factions = FactionManager.Instance.GetAllFactions(kId);
            if (factions.Count < 2) return 0f;
            var powers = new List<float>();
            foreach (var f in factions) powers.Add(ComputeFactionPower(k, kId, f));
            powers.Sort((a, b) => b.CompareTo(a));
            float total = powers.Sum();
            if (total <= 0f) return 0f;
            return (powers[0] - powers[1]) / total;
        }

        /// <summary>党魁权势（政变判定用）：有官职→CorruptionManager.GetPower；无官职→50 基础值。</summary>
        /// <summary>成员权势（阶段2A A2-1：公开供党魁选举；无官职者默认 50）。</summary>
        public static float GetMemberPowerPublic(string kId, long actorId)
        {
            try
            {
                var records = OfficeManager.Instance.GetRecordsPublic(kId);
                if (records != null)
                {
                    foreach (var r in records)
                    {
                        if (long.TryParse(r.PersonId, out long pid) && pid == actorId)
                            return CorruptionManager.Instance.GetPower(kId, r.OfficeId);
                    }
                }
            }
            catch { }
            return 50f;
        }

        // ═══════════════════════ 年度流程 ═══════════════════════

        public void OnKingdomYear(Kingdom k, int year)
        {
            // 阶段3G-5 根修（用户实锤"朝议恒空白"）：_freshWorld 硬门把旧档/未置位世界的党争政治
            // 整链静默（朝议/事件/近事全无）。改为有党派即运行——旧档无党派时仍被下方空列表挡住。
            if (!Enabled) return;
            if (k == null || !k.isAlive()) return;
            string kId = k.id.ToString();
            try
            {
                // 侯级以上才党争（与科举同判定）；阶段3H-5：游牧体制按草原档位折算——部盟及以上可开库里尔台
                bool isMandate = PolicyManager.IsMandateHolderForKingdom(k);
                var tier = OfficeManager.GetTierWithMandate(k, isMandate);
                if (KingdomCivilizationService.Instance.IsNomadic(k))
                {
                    string nt = NomadicTierService.GetKingdomTierId(k);
                    if (nt != NomadicTierService.TIER_WANGTING && nt != NomadicTierService.TIER_DAHANTING
                        && nt != NomadicTierService.TIER_HANGUO && nt != NomadicTierService.TIER_BUMENG) return;
                }
                else if (tier == OfficeManager.KingdomTier.Zi || tier == OfficeManager.KingdomTier.Bo) return;

                // 状态先行（自建党也要写安置与近事）
                if (!_states.TryGetValue(kId, out var st))
                {
                    st = new PoliticsState();
                    _states[kId] = st;
                }

                // 阶段2T-1 自建党条件合理化：在野权臣（权势≥60 且不在执政党）可建党。
                // 原条件"无任何党籍"几乎不可达（文武官员按身份自动聚合恒有党籍），自建党形同虚设。
                try
                {
                    var ruling = GetRulingFaction(k, kId);
                    var records = OfficeManager.Instance.GetRecordsPublic(kId);
                    if (records != null)
                    {
                        foreach (var rec in records)
                        {
                            if (rec == null || string.IsNullOrEmpty(rec.PersonId)) continue;
                            if (!long.TryParse(rec.PersonId, out long pid)) continue;
                            if (pid == (k.king != null ? k.king.getID() : -1)) continue;
                            if (CorruptionManager.Instance.GetPower(kId, rec.OfficeId) < 60f) continue;
                            // 执政党权臣不建党（既得利益者无动机）；在野/身份党权臣可自立门户
                            if (ruling != null)
                            {
                                bool inRuling = FactionManager.Instance.IsActorInFaction(kId, pid, ruling.Name);
                                if (inRuling) continue;
                            }
                            string partyName = FactionManager.Instance.CreateCustomFaction(kId, pid, rec.PersonName ?? "权臣", year);
                            if (!string.IsNullOrEmpty(partyName))
                            {
                                SetPlacement(kId, pid, partyName);
                                AddRecent(st, year, $"{rec.PersonName ?? "权臣"} 自立门户，{partyName}成立");
                                break; // 每年至多建一个
                            }
                        }
                    }
                }
                catch { }

                var factions = FactionManager.Instance.GetAllFactions(kId);
                if (factions.Count == 0) return; // 旧档/骨架未建

                // 2026-08-19 串联①：账面党势年度结算——执政党传统分量 +2/年（封顶 40），
                // 非执政 -1/年（下限 0）——党势不随成员生死跳水，长期执政有"根基"加成
                try { TickTraditionBonus(k, kId, factions, year); } catch { }

                // 2026-08-19 串联②：欠俸离心——亏空年全体在职官员亲信 -3（财政危机转化为政治危机）
                try { TickUnpaidSalariesDiscontent(k, kId, st, year); } catch { }

                RefreshFavor(k, kId, st, year);
                KingPromotion(k, kId, st, year);
                TryEvents(k, kId, st, year);

                // 2026-08-19 串联③：朝议（每年一场——议题按国情触发，两党辩手对决，胜方落地国策效果）
                try { TryCourtDebate(k, kId, st, year, factions); } catch { }

                // D2 党争年度插桩（2026-10-06）：年末党势/烈度/权臣峰值/国力点爵位（D4 通胀监测）一行/国/年
                try
                {
                    var fList = FactionManager.Instance.GetAllFactions(kId);
                    var pSb = new System.Text.StringBuilder();
                    var tSb = new System.Text.StringBuilder();
                    float mx1 = 0f, mx2 = 0f, tot = 0f;
                    foreach (var f0 in fList)
                    {
                        if (f0 == null) continue;
                        float p0 = ComputeFactionPower(k, kId, f0);
                        tot += p0;
                        if (p0 > mx1) { mx2 = mx1; mx1 = p0; } else if (p0 > mx2) mx2 = p0;
                        pSb.Append(p0.ToString("F0")).Append("/");
                        tSb.Append(f0.TraditionBonus.ToString("F0")).Append("/");
                    }
                    float intens = tot > 0f ? (mx1 - mx2) / tot : 0f;
                    var rulingF = GetRulingFaction(k, kId);
                    float maxOP = 0f; string maxOffice = "-";
                    var oRecs = OfficeManager.Instance.GetRecordsPublic(kId);
                    if (oRecs != null)
                        foreach (var r0 in oRecs)
                        {
                            if (r0 == null) continue;
                            float pv0 = CorruptionManager.Instance.GetPower(kId, r0.OfficeId);
                            if (pv0 > maxOP) { maxOP = pv0; maxOffice = r0.OfficeId ?? "-"; }
                        }
                    string tierId = ZhuluTierService.GetTierId(ZhuluTierService.GetPowerPoints(k));
                    ZhuluTrace.Write("faction_trace.csv",
                        $"fp_year,{year},{kId},{fList.Count},{rulingF?.Name ?? "-"},{intens.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)},{pSb},{tSb},{ZhuluTierService.GetTierName(tierId)},{maxOP.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)},{maxOffice}");
                }
                catch { }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[FactionPolitics] OnKingdomYear 异常: {ex.Message}");
            }
        }

        // ═══════════════════════ 2026-08-19 串联重构：账面党势 / 欠俸离心 / 朝议 ═══════════════════════

        /// <summary>
        /// 串联①：账面党势年度结算——执政党传统分量 +2/年（封顶 40），非执政党 -1/年（下限 0）。
        /// RulingSince 记本届执政起始年（翻转时重置），UI 可显示"执政 N 年"。
        /// </summary>
        private void TickTraditionBonus(Kingdom k, string kId, List<FactionManager.FactionRecord> factions, int year)
        {
            var ruling = GetRulingFaction(k, kId);
            foreach (var f in factions)
            {
                if (f == null) continue;
                bool isRuling = ruling != null && f.Name == ruling.Name;
                if (isRuling)
                {
                    if (f.RulingSince == 0) f.RulingSince = year;
                    f.TraditionBonus = Math.Min(TRADITION_CAP, f.TraditionBonus + 2f);
                }
                else
                {
                    f.TraditionBonus = Math.Max(0f, f.TraditionBonus - 1f);
                    f.RulingSince = 0;
                }
            }
        }

        /// <summary>
        /// 串联②：欠俸离心——国库亏空年全体在职官员亲信 -3（财政危机转化为政治危机）。
        /// 连续亏空不叠加（每年固定 -3；相对正常年的偏差为 -5/年：正常年仅 +2，亏空年 +2-3=-1）。
        /// </summary>
        private void TickUnpaidSalariesDiscontent(Kingdom k, string kId, PoliticsState st, int year)
        {
            var report = TreasuryManager.Instance.GetReport(kId);
            if (report == null || report.DeficitYears < 1) return;
            var records = OfficeManager.Instance.GetRecordsPublic(kId);
            if (records == null || records.Count == 0) return;
            int hit = 0;
            foreach (var r in records)
            {
                if (r == null || string.IsNullOrEmpty(r.PersonId)) continue;
                if (!long.TryParse(r.PersonId, out long pid)) continue;
                st.Favor[pid] = ClampFavor(GetFavor(kId, pid) - 3);
                hit++;
            }
            if (hit > 0)
                AddRecent(st, year, $"国库亏空，百官俸禄拖欠，人心浮动（{hit} 员亲信 −3）");
        }

        // ── 朝议常量 ──
        public const string EVT_DEBATE = "debate";     // 朝议（近事/史册用）

        /// <summary>
        /// 串联③：朝议——每年一场，议题池按国情触发 + 加权随机 + 上年避重。
        /// 2026-08-19 扩充：由 5 议题扩至 12 议题，联动厌战（息兵）/稳定（赈济/储位/税赋/军备）/
        /// 礼治（祭天）/门第（取士）等系统；两党各出一名辩手（评分=考课×0.5+权势×0.3+党势份额×0.2），
        /// 5 档胜负，胜方党势账面 +（朝议叙事+国策效果双落地）。
        /// </summary>
        private void TryCourtDebate(Kingdom k, string kId, PoliticsState st, int year,
            List<FactionManager.FactionRecord> factions)
        {
            var active = factions.Where(f => f != null && f.MemberIds.Count > 0).ToList();
            if (active.Count < 2) return;

            // ── 议题池（按国情触发；mode：0=执政vs反对 1=战vs和 2=和vs战 3=民生轴 4=忠君轴 5=门第轴 6=太子vs皇权 7=武官vs文官）──
            bool deficit = false, atWar = false;
            try { var rep = TreasuryManager.Instance.GetReport(kId); deficit = rep != null && rep.DeficitYears >= 1; } catch { }
            try { var wars = k.getWars(); if (wars != null) { foreach (var _ in wars) { atWar = true; break; } } } catch { }
            float intensity = GetIntensity(k, kId);
            int exhaustion = 0;
            try { exhaustion = ZhuluWarService.GetWarExhaustion(kId); } catch { }
            float stability = 50f;
            try { stability = StabilityManager.Instance.GetStability(kId); } catch { }
            int netLiZhi = 0;
            try { netLiZhi = ZhuluLiZhiService.GetNetLiZhi(kId); } catch { }
            bool hasTaiZi = active.Any(f => f.Type == FactionManager.FactionType.TaiZi);

            // 阶段3H2：游牧体系议题草原化（同 mode 同权重——只换文本）
            bool debNomad = false;
            try { debNomad = KingdomCivilizationService.Instance.IsNomadic(k); } catch { }
            string T(string e, string n) => debNomad ? n : e;
            var pool = new List<(string topic, float weight, int mode)>();
            if (deficit) pool.Add((T("节流开源", "马政之议"), 3f, 0));
            if (atWar)
            {
                pool.Add((T("战和之议", "掠边之议"), 3f, 1));
                if (exhaustion >= 40) pool.Add((T("息兵之议", "息马之议"), 2.5f, 2));   // 厌战高→和战之争白热化
            }
            if (intensity >= 0.6f) pool.Add((T("整肃朝纲", "整肃汗庭"), 3f, 0));
            if (stability < 40f) pool.Add((T("赈济之议", "赈济部众"), 2f, 3));           // 危局开仓
            if (netLiZhi < 10) pool.Add((T("祭天之议", "祭长生天"), 1.5f, 4));           // 礼崩修礼
            if (hasTaiZi) pool.Add((T("储位之议", "汗位之议"), 1.2f, 6));                // 国本之争
            pool.Add((T("休养生息", "休牧养畜"), 1f, 0));
            pool.Add((T("论功行赏", "论功分俘"), 1f, 0));
            pool.Add((T("取士之议", "选贤之议"), 1f, 5));                                // 门第 vs 寒门
            pool.Add((T("税赋之议", "贡赋之议"), 0.8f, 3));                              // 养民 vs 役民
            pool.Add((T("军备之议", "骑战之议"), 0.8f, 7));                              // 武备 vs 文治

            // 上年议题避重（池中还有别的选择时排除去年同题）
            var candidates = pool;
            if (pool.Count > 1 && _lastDebateTopic.TryGetValue(kId, out string lastTopic))
            {
                var filtered = pool.Where(p => p.topic != lastTopic).ToList();
                if (filtered.Count > 0) candidates = filtered;
            }

            // 加权随机选择
            float totalW = 0f;
            foreach (var p in candidates) totalW += p.weight;
            float roll = UnityEngine.Random.value * totalW;
            int pickIdx = candidates.Count - 1;
            for (int i = 0; i < candidates.Count; i++)
            {
                roll -= candidates[i].weight;
                if (roll <= 0f) { pickIdx = i; break; }
            }
            string topicDisp = candidates[pickIdx].topic;   // 显示文本（游牧=草原议题名）
            int mode = candidates[pickIdx].mode;
            // 阶段3H-13 根修：游牧议题名归一化为东方 key 再进逻辑层——
            // ApplyTopicEffect/DebatePolicyMap/立场的 switch 全部按东方名匹配，
            // 旧代码直接把"马政之议"等喂进去导致游牧朝议效果全部静默失效。
            string topic = debNomad ? NomadTopicToKey(topicDisp) : topicDisp;
            _lastDebateTopic[kId] = topicDisp;

            var (fa, fb) = PickDebateParties(k, kId, active, mode);
            if (fa == null || fb == null || fa == fb) return;

            // ── 辩手与评分（评分 = 考课×0.5 + 权势×0.3 + 党势份额×0.2）──
            float pa = ComputeFactionPower(k, kId, fa), pb = ComputeFactionPower(k, kId, fb);
            float total = pa + pb;
            float sa = DebateScore(kId, fa, pa, total), sb = DebateScore(kId, fb, pb, total);

            string aName = FName(k, fa), bName = FName(k, fb);
            string winner, verdict;
            float diff = sa - sb;
            if (diff > 25f) { winner = aName; verdict = "力排众议"; ApplyDebateResult(fa, fb, 8f); }
            else if (diff > 10f) { winner = aName; verdict = "略占上风"; ApplyDebateResult(fa, fb, 4f); }
            else if (diff < -25f) { winner = bName; verdict = "力排众议"; ApplyDebateResult(fb, fa, 8f); }
            else if (diff < -10f) { winner = bName; verdict = "略占上风"; ApplyDebateResult(fb, fa, 4f); }
            else { winner = ""; verdict = "相持不下"; ApplyDebateResult(fa, fb, 1f); }

            // ── 议题落地（胜方效果——机制真实生效）──
            string effectText = ApplyTopicEffect(k, kId, st, year, topic, winner, fa, fb);
            // 2026-08-19 朝议×政令联动：胜方推行政令/罢废相反政令（一场最多一令，受 AI 施政开关控制）
            string policyText = TryLinkDebatePolicy(k, kId, k.name ?? "", year, topic, winner, fa, fb);
            string fullEffect = string.IsNullOrEmpty(effectText) ? policyText
                : string.IsNullOrEmpty(policyText) ? effectText
                : effectText + "；" + policyText;

            // ── 叙事落地（近事 + 史册）──
            string duelText = string.IsNullOrEmpty(winner)
                ? $"{aName} 与 {bName} 各执一词，{verdict}"
                : $"{aName} {verdict}，压过 {bName}";
            AddRecent(st, year, $"{(debNomad ? "库里尔台「" : "朝议「")}{topicDisp}」：{duelText}{(string.IsNullOrEmpty(fullEffect) ? "" : "，" + fullEffect)}"); // 阶段3H2
            ChronicleManager.Instance.AddEntry(year, ChronicleEventType.CourtDebate, kId, k.name ?? "",
                winner,
                $"<color=#c8b98a>【{(debNomad ? "库里尔台" : "朝议")}】{topicDisp}之议：{duelText}{(string.IsNullOrEmpty(fullEffect) ? "" : "——" + fullEffect)}。</color>", false);
            ZhuluContext.Instance.LogInfo($"[FactionPolitics] 朝议: {k.name}「{topic}」{aName}({sa:F0}) vs {bName}({sb:F0}) → {winner}({verdict}){(string.IsNullOrEmpty(policyText) ? "" : " 【政令】" + policyText)}");
        }

        /// <summary>阶段3H-13：游牧议题显示名 → 东方逻辑 key（池中 12 题一一对应）。</summary>
        private static string NomadTopicToKey(string n)
        {
            switch (n)
            {
                case "马政之议": return "节流开源";
                case "掠边之议": return "战和之议";
                case "息马之议": return "息兵之议";
                case "整肃汗庭": return "整肃朝纲";
                case "赈济部众": return "赈济之议";
                case "祭长生天": return "祭天之议";
                case "汗位之议": return "储位之议";
                case "休牧养畜": return "休养生息";
                case "论功分俘": return "论功行赏";
                case "选贤之议": return "取士之议";
                case "贡赋之议": return "税赋之议";
                case "骑战之议": return "军备之议";
                default: return n;
            }
        }

        /// <summary>朝议对战双方：执政党 vs 最大反对党。</summary>
        private (FactionManager.FactionRecord a, FactionManager.FactionRecord b) RulingVsOpposition(
            Kingdom k, string kId, List<FactionManager.FactionRecord> factions)
        {
            var ruling = GetRulingFaction(k, kId);
            var opp = GetLargestOpposition(k, kId, factions, ruling);
            return (ruling, opp);
        }

        /// <summary>朝议对战双方（战和之议）：战争轴最高党 vs 最低党。</summary>
        private static (FactionManager.FactionRecord a, FactionManager.FactionRecord b) WarVsPeace(
            List<FactionManager.FactionRecord> factions)
        {
            return AxisVs(factions, FactionManager.ST_WAR);
        }

        /// <summary>2026-08-19 议题对战模式分发（mode 见 TryCourtDebate 议题池注释）。</summary>
        private (FactionManager.FactionRecord a, FactionManager.FactionRecord b) PickDebateParties(
            Kingdom k, string kId, List<FactionManager.FactionRecord> factions, int mode)
        {
            switch (mode)
            {
                case 1: return WarVsPeace(factions);
                case 2: { var (hi, lo) = WarVsPeace(factions); return (lo, hi); }  // 息兵之议：主和党先手
                case 3: return AxisVs(factions, FactionManager.ST_LIVELIHOOD);
                case 4: return AxisVs(factions, FactionManager.ST_LOYAL);
                case 5: return AxisVs(factions, FactionManager.ST_LINEAGE);
                case 6: return TypeVs(factions, FactionManager.FactionType.TaiZi, FactionManager.FactionType.HuangQuan);
                case 7: return TypeVs(factions, FactionManager.FactionType.WuGuan, FactionManager.FactionType.WenGuan);
                default: return RulingVsOpposition(k, kId, factions);
            }
        }

        /// <summary>2026-08-19 立场轴对战：指定轴最高党 vs 最低党（轴分歧最大两党对辩）。</summary>
        private static (FactionManager.FactionRecord a, FactionManager.FactionRecord b) AxisVs(
            List<FactionManager.FactionRecord> factions, int axis)
        {
            FactionManager.FactionRecord hi = null, lo = null;
            float hiV = -1f, loV = 2f;
            foreach (var f in factions)
            {
                if (f == null) continue;
                float v = FactionManager.GetStanceAxis(f, axis);
                if (v > hiV) { hiV = v; hi = f; }
                if (v < loV) { loV = v; lo = f; }
            }
            return (hi, lo);
        }

        /// <summary>2026-08-19 党型对战：按党型找两党（储位之议=太子党vs皇权党 / 军备之议=武官派vs文官派）。</summary>
        private static (FactionManager.FactionRecord a, FactionManager.FactionRecord b) TypeVs(
            List<FactionManager.FactionRecord> factions, FactionManager.FactionType typeA, FactionManager.FactionType typeB)
        {
            FactionManager.FactionRecord a = null, b = null;
            foreach (var f in factions)
            {
                if (f == null) continue;
                if (f.Type == typeA && a == null) a = f;
                else if (f.Type == typeB && b == null) b = f;
            }
            return (a, b);
        }

        // ═══════════════ 2026-08-19 朝议×政令联动 ═══════════════
        // 规则：朝议胜方按议题立场推动政令颁布（走 IssuePolicy 真实流程——官席主持/费用/同分类替换/
        // 等级门槛全检查），或罢废立场相反的在位政令；一场朝议最多一次政令动作。
        // ① 槽位空缺 → 推行政令（填补）；② 槽位被相反政令占据 → 改弦更张（IssuePolicy 自动替换）；
        // ③ 相反政令所在类别无推行候选 → 直接罢废。同向/无关政令在位 → 不动（防无谓换政churn）。

        private static readonly string[] NoPolicies = new string[0];
        /// <summary>扩军类政令（主和/罢兵/文治胜方的罢废对象）。</summary>
        private static readonly string[] MilExpansionPolicies =
            { "mil_fubing", "mil_mercenary", "bo_militia", "zi_all_soldiers", "zi_all_soldiers_light", "zi_all_soldiers_heavy" };
        /// <summary>重敛类政令（休养/赈济/轻税胜方的替换对象）。</summary>
        private static readonly string[] HeavyTaxPolicies =
            { "eco_salt_iron", "eco_head_tax", "eco_head_tax_light", "eco_head_tax_heavy" };
        /// <summary>礼制政令（祭天之议候选——高爵位档在前，等级门槛由 IssuePolicy 过滤）。</summary>
        private static readonly string[] RitualPolicies =
            { "zlr_gong_binyi", "zlr_gong_yayue", "zlr_hou_chaojin", "zlr_hou_shixin",
              ZhuluLiZhiService.POLICY_ZONG_MIAO, ZhuluLiZhiService.POLICY_MING_TANG, "zlr_zi_sishi", "zlr_yi_lishe" };

        /// <summary>朝议议题×胜方立场 → 政令联动映射（纯函数——单测覆盖）。
        /// orientation：单取向议题="win"；战和="war"/"peace"；息兵="peace"/"rearm"；
        /// 税赋="light"/"heavy"；取士="humble"/"scholar"；军备="martial"/"civil"。
        /// push=胜方倾向推行的政令候选（高爵位档在前）；revoke=立场相反的在位政令。</summary>
        public static (string[] push, string[] revoke) DebatePolicyMap(string topic, string orientation)
        {
            switch (topic)
            {
                case "节流开源": return (new[] { "eco_head_tax_light", "eco_salt_iron" }, NoPolicies);
                case "战和之议": return orientation == "war"
                    ? (new[] { "mil_fubing", "bo_militia", "zi_all_soldiers_light" }, new[] { "mil_civilian" })
                    : (new[] { "eco_rest", "soc_peace" }, MilExpansionPolicies);
                case "息兵之议": return orientation == "peace"
                    ? (new[] { "eco_rest", "soc_peace" }, MilExpansionPolicies)
                    : (new[] { "mil_elite" }, new[] { "mil_civilian" });
                case "整肃朝纲": return (new[] { "gov_purge", "gov_chayan" }, NoPolicies);
                case "休养生息": return (new[] { "eco_rest", "gov_taoist", "soc_ritual" }, HeavyTaxPolicies);
                case "赈济之议": return (new[] { "eco_granary", "eco_rest" }, HeavyTaxPolicies);
                case "祭天之议": return (RitualPolicies, NoPolicies);
                case "取士之议": return orientation == "humble"
                    ? (new[] { "bo_recruit_talent", "zlr_hou_xingxue", "soc_ritual" }, NoPolicies)
                    : (NoPolicies, NoPolicies);   // 门第胜：维持门荫，不动政令
                case "税赋之议": return orientation == "light"
                    ? (new[] { "eco_rest" }, HeavyTaxPolicies)
                    : (new[] { "eco_head_tax_light", "eco_head_tax_heavy", "eco_salt_iron" }, new[] { "eco_rest" });
                case "军备之议": return orientation == "martial"
                    ? (new[] { "mil_mercenary", "mil_fubing", "bo_militia", "zi_all_soldiers_light", "nomad_hunt", "mil_valour" }, new[] { "mil_civilian" })
                    : (new[] { "mil_civilian", "gov_xibing", "soc_ritual" }, MilExpansionPolicies);
                default: return (NoPolicies, NoPolicies);   // 论功行赏/储位之议：无政令联动
            }
        }

        /// <summary>胜方党在指定立场轴的取值（与 ApplyTopicEffect 的胜方判定同口径）。</summary>
        private static float WinnerStance(FactionManager.FactionRecord fa, FactionManager.FactionRecord fb, string winnerName, int axis)
        {
            var w = fa != null && fa.Name == winnerName ? fa : fb;
            return w == null ? 0.5f : FactionManager.GetStanceAxis(w, axis);
        }

        /// <summary>朝议×政令联动执行器：返回联动文案（空=本场无政令动作）。一场最多一令。</summary>
        private string TryLinkDebatePolicy(Kingdom k, string kId, string kName, int year,
            string topic, string winnerName, FactionManager.FactionRecord fa, FactionManager.FactionRecord fb)
        {
            if (string.IsNullOrEmpty(winnerName)) return "";
            if (!ZhuluConfig.Instance.AiAutoPolicy) return "";   // 玩家关 AI 施政 → 朝议只议不动政令
            // 阶段3H-16：政令联动文案按体系（库里尔台/朝议——议题 key 已归一化，此处只管显示词）
            bool linkNomad = false;
            try { linkNomad = KingdomCivilizationService.Instance.IsNomadic(k); } catch { }
            string linkTag = linkNomad ? "库里尔台" : "朝议";
            try
            {
                // ── 立场定向（与 ApplyTopicEffect 同口径）──
                string orientation;
                switch (topic)
                {
                    case "战和之议":
                        orientation = WinnerStance(fa, fb, winnerName, FactionManager.ST_WAR) >= 0.5f ? "war" : "peace"; break;
                    case "息兵之议":
                        orientation = WinnerStance(fa, fb, winnerName, FactionManager.ST_WAR) < 0.5f ? "peace" : "rearm"; break;
                    case "税赋之议":
                        orientation = WinnerStance(fa, fb, winnerName, FactionManager.ST_LIVELIHOOD) >= 0.5f ? "light" : "heavy"; break;
                    case "取士之议":
                        orientation = WinnerStance(fa, fb, winnerName, FactionManager.ST_LINEAGE) < 0.5f ? "humble" : "scholar"; break;
                    case "军备之议":
                        {
                            var w = fa != null && fa.Name == winnerName ? fa : fb;
                            orientation = w != null && w.Type == FactionManager.FactionType.WuGuan ? "martial" : "civil";
                        }
                        break;
                    default: orientation = "win"; break;
                }
                var (push, revoke) = DebatePolicyMap(topic, orientation);
                bool hasPush = push != null && push.Length > 0;
                bool hasRevoke = revoke != null && revoke.Length > 0;
                if (!hasPush && !hasRevoke) return "";

                var pm = PolicyManager.Instance;

                // ── ① 罢废相反政令（同类别将被②颁布自动替换时跳过——避免"废了又颁"冗余记录）──
                if (hasRevoke)
                {
                    foreach (string rid in revoke)
                    {
                        var rDef = pm.GetDefinition(rid);
                        if (rDef == null || !pm.IsPolicyActive(kId, rid)) continue;
                        bool willReplace = false;
                        if (hasPush)
                        {
                            foreach (string p in push)
                            {
                                var d = pm.GetDefinition(p);
                                if (d != null && d.Category == rDef.Category) { willReplace = true; break; }
                            }
                        }
                        if (willReplace) continue;
                        if (pm.RevokePolicy(kId, kName, rid, year))
                            return $"{linkTag}罢废《{rDef.Title}》";
                    }
                }

                // ── ② 推行政令（槽位空缺填补 / 相反政令改弦更张；高爵位档候选在前，等级门槛自动过滤）──
                if (hasPush)
                {
                    foreach (string pid in push)
                    {
                        var def = pm.GetDefinition(pid);
                        if (def == null) continue;
                        var active = pm.GetActivePolicyInCategory(kId, def.Category);
                        bool oppositeHeld = false;
                        if (active != null && hasRevoke)
                        {
                            foreach (string r in revoke) { if (r == active.PolicyId) { oppositeHeld = true; break; } }
                        }
                        if (active != null && !oppositeHeld) continue;   // 槽位被同向/无关政令占据——不无谓换政
                        if (pm.IssuePolicy(kId, kName, pid, year, false))
                        {
                            if (active != null)
                                return $"{linkTag}改弦更张，颁《{def.Title}》（代《{pm.GetDefinition(active.PolicyId)?.Title ?? active.PolicyId}》）";
                            return $"{linkTag}推行政令《{def.Title}》";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[FactionPolitics] 朝议政令联动异常: {ex.Message}");
            }
            return "";
        }


        /// <summary>辩手评分：党内最优成员（考课×0.5 + 权势×0.3 + 党势份额×0.2）。</summary>
        private float DebateScore(string kId, FactionManager.FactionRecord f, float factionPower, float totalPower)
        {
            try
            {
                float best = 0f;
                foreach (var mid in f.MemberIds)
                {
                    var a = GetActor(mid);
                    if (a == null || !a.isAlive()) continue;
                    int exam = OfficialRatingManager.CalcOfficialScore(a, 0, a.getAge()).score;
                    float power = GetMemberPowerPublic(kId, mid);
                    float s = exam * 0.5f + power * 0.3f;
                    if (s > best) best = s;
                }
                float share = totalPower > 0f ? factionPower / totalPower * 100f : 0f;
                return best + share * 0.2f;
            }
            catch { return 0f; }
        }

        /// <summary>朝议胜负入账面党势（胜方 +delta，败方 −delta/2；平局双方 +1）。</summary>
        private static void ApplyDebateResult(FactionManager.FactionRecord winner, FactionManager.FactionRecord loser, float delta)
        {
            if (winner != null) winner.TraditionBonus = Math.Min(TRADITION_CAP, Math.Max(0f, winner.TraditionBonus + delta));
            if (loser != null && delta > 1f) loser.TraditionBonus = Math.Max(0f, loser.TraditionBonus - delta * 0.5f);
            else if (winner != null && loser != null && delta <= 1f)
            {
                // 平局：双方 +1
                loser.TraditionBonus = Math.Min(TRADITION_CAP, loser.TraditionBonus + 1f);
            }
        }

        /// <summary>
        /// P2-30 串联②（2026-08-23）：战争结算回写执政党账面党势（钳 [0,40]）。
        /// 胜方执政党 +3（战争红利巩固朝局）、败方执政党 −5（丧师辱国主和党抬头）。
        /// 无派系（叛军/邑子国/未启用党争）时静默无操作。数值口径与"在朝+2/年"同量纲。
        /// </summary>
        public void AdjustRulingFactionPower(Kingdom k, string kId, float delta)
        {
            try
            {
                var ruling = GetRulingFaction(k, kId);
                if (ruling == null) return;
                ruling.TraditionBonus = Math.Min(TRADITION_CAP, Math.Max(0f, ruling.TraditionBonus + delta));
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogRateLimited("FactionPolitics.AdjustRuling",
                    "[FactionPolitics] 战争党势回写异常: " + ex.Message, 30f);
            }
        }

        /// <summary>议题落地效果（返回效果文案；空=无附加效果）。败方为 null 表示平局。</summary>
        private string ApplyTopicEffect(Kingdom k, string kId, PoliticsState st, int year,
            string topic, string winnerName, FactionManager.FactionRecord fa, FactionManager.FactionRecord fb)
        {
            bool hasWinner = !string.IsNullOrEmpty(winnerName);
            try
            {
                switch (topic)
                {
                    case "节流开源":
                        if (hasWinner)
                        {
                            // 胜方推行节流：国库一次性 + 岁入 15%（开源充库），稳定 +2
                            var report = TreasuryManager.Instance.GetReport(kId);
                            long grant = (long)((report?.Income ?? 0) * 0.15f);
                            if (grant > 0)
                            {
                                long tre = KingdomDataExt.GetLong(k, "tre", 0);
                                KingdomDataExt.SetLong(k, "tre", tre + grant);
                            }
                            StabilityManager.Instance.AddExternalBonus(kId, 2f);
                            return $"推行节流，国库 +{grant} 金，社稷稍安";
                        }
                        return "";
                    case "战和之议":
                        if (hasWinner)
                        {
                            // 胜方立场定国是：主战方胜→厌战 -5（同仇敌忾）；主和方胜→厌战 +5（厌战情绪主导）
                            bool warWon = fa.Name == winnerName
                                ? FactionManager.GetStanceAxis(fa, FactionManager.ST_WAR) >= 0.5f
                                : FactionManager.GetStanceAxis(fb, FactionManager.ST_WAR) >= 0.5f;
                            if (warWon)
                            {
                                ZhuluWarService.AddWarExhaustion(kId, -5);
                                return $"主战之议既定，三军同仇敌忾（厌战 −5）";
                            }
                            ZhuluWarService.AddWarExhaustion(kId, 5);
                            return $"主和之声高涨，军民思安（厌战 +5）";
                        }
                        return "";
                    case "息兵之议":   // 2026-08-19 新增：厌战≥40 触发——罢兵派 vs 主战派
                        if (hasWinner)
                        {
                            bool peaceWon = fa.Name == winnerName
                                ? FactionManager.GetStanceAxis(fa, FactionManager.ST_WAR) < 0.5f
                                : FactionManager.GetStanceAxis(fb, FactionManager.ST_WAR) < 0.5f;
                            if (peaceWon)
                            {
                                ZhuluWarService.AddWarExhaustion(kId, -6);
                                StabilityManager.Instance.AddExternalBonus(kId, 2f);
                                return "罢兵息民之议既行，军民稍安（厌战 −6，稳定 +2）";
                            }
                            ZhuluWarService.AddWarExhaustion(kId, -4);
                            return "整军经武以备再战，士气复振（厌战 −4）";
                        }
                        return "";
                    case "赈济之议":   // 2026-08-19 新增：稳定<40 触发——开仓赈济
                        if (hasWinner)
                        {
                            long treR = KingdomDataExt.GetLong(k, "tre", 0);
                            long costR = Math.Min(400, treR);
                            if (costR > 0) KingdomDataExt.SetLong(k, "tre", treR - costR);
                            StabilityManager.Instance.AddExternalBonus(kId, 4f);
                            return $"开仓赈济，民困稍苏（库 −{costR}，稳定 +4）";
                        }
                        return "";
                    case "祭天之议":   // 2026-08-19 新增：净礼治<10 触发——修礼入礼治累积（与祭祀同 key，上限 30）
                        if (hasWinner)
                        {
                            long lz = KingdomDataExt.GetLong(k, "li_zhi_points", 0);
                            KingdomDataExt.SetLong(k, "li_zhi_points", Math.Min(30, lz + 2));
                            StabilityManager.Instance.AddExternalBonus(kId, 1f);
                            return "修明礼乐，以奉宗庙（礼治 +2，稳定 +1）";
                        }
                        return "";
                    case "储位之议":   // 2026-08-19 新增：太子党在朝触发——国本之争
                        if (hasWinner)
                        {
                            bool taiZiWon = fa.Name == winnerName
                                ? fa.Type == FactionManager.FactionType.TaiZi
                                : fb.Type == FactionManager.FactionType.TaiZi;
                            if (taiZiWon)
                            {
                                StabilityManager.Instance.AddExternalBonus(kId, 3f);
                                return "储位早定，国本固而人心安（稳定 +3）";
                            }
                            StabilityManager.Instance.AddExternalBonus(kId, 1f);
                            return "乾纲独断，储议暂寝（稳定 +1）";
                        }
                        return "";
                    case "取士之议":   // 2026-08-19 新增：门第 vs 寒门——取士路线之争
                        if (hasWinner)
                        {
                            bool humbleWon = fa.Name == winnerName
                                ? FactionManager.GetStanceAxis(fa, FactionManager.ST_LINEAGE) < 0.5f
                                : FactionManager.GetStanceAxis(fb, FactionManager.ST_LINEAGE) < 0.5f;
                            if (humbleWon)
                            {
                                long treK = KingdomDataExt.GetLong(k, "tre", 0);
                                long costK = Math.Min(200, treK);
                                if (costK > 0) KingdomDataExt.SetLong(k, "tre", treK - costK);
                                StabilityManager.Instance.AddExternalBonus(kId, 2f);
                                return $"广纳寒俊，才路大开（库 −{costK}，稳定 +2）";
                            }
                            StabilityManager.Instance.AddExternalBonus(kId, 1f);
                            return "门荫有序，士族安堵（稳定 +1）";
                        }
                        return "";
                    case "税赋之议":   // 2026-08-19 新增：养民 vs 役民——税赋轻重之争
                        if (hasWinner)
                        {
                            bool lightTaxWon = fa.Name == winnerName
                                ? FactionManager.GetStanceAxis(fa, FactionManager.ST_LIVELIHOOD) >= 0.5f
                                : FactionManager.GetStanceAxis(fb, FactionManager.ST_LIVELIHOOD) >= 0.5f;
                            if (lightTaxWon)
                            {
                                long treL = KingdomDataExt.GetLong(k, "tre", 0);
                                long costL = Math.Min(300, treL);
                                if (costL > 0) KingdomDataExt.SetLong(k, "tre", treL - costL);
                                StabilityManager.Instance.AddExternalBonus(kId, 2f);
                                return $"轻徭薄赋，与民休息（库 −{costL}，稳定 +2）";
                            }
                            var repT = TreasuryManager.Instance.GetReport(kId);
                            long gainT = (long)((repT?.Income ?? 0) * 0.10f);
                            if (gainT > 0)
                            {
                                long treH = KingdomDataExt.GetLong(k, "tre", 0);
                                KingdomDataExt.SetLong(k, "tre", treH + gainT);
                            }
                            StabilityManager.Instance.AddExternalBonus(kId, -2f);
                            return $"加赋充库，民力稍困（库 +{gainT}，稳定 −2）";
                        }
                        return "";
                    case "军备之议":   // 2026-08-19 新增：武备 vs 文治
                        if (hasWinner)
                        {
                            bool warPartyWon = fa.Name == winnerName
                                ? fa.Type == FactionManager.FactionType.WuGuan
                                : fb.Type == FactionManager.FactionType.WuGuan;
                            if (warPartyWon)
                            {
                                var repM = TreasuryManager.Instance.GetReport(kId);
                                long costM = Math.Min((long)((repM?.Income ?? 0) * 0.10f), KingdomDataExt.GetLong(k, "tre", 0));
                                if (costM > 0)
                                {
                                    long treM = KingdomDataExt.GetLong(k, "tre", 0);
                                    KingdomDataExt.SetLong(k, "tre", treM - costM);
                                }
                                ZhuluWarService.AddWarExhaustion(kId, -2);
                                return $"武备修葺，军心复振（库 −{costM}，厌战 −2）";
                            }
                            StabilityManager.Instance.AddExternalBonus(kId, 2f);
                            return "文治为纲，止戈兴学（稳定 +2）";
                        }
                        return "";
                    case "整肃朝纲":
                        if (hasWinner)
                        {
                            // 胜方整肃：全体世仇 −3，稳定 +3
                            var keys = new List<string>(st.Feuds.Keys);
                            foreach (var key in keys) st.Feuds[key] = Math.Max(0, st.Feuds[key] - 3);
                            StabilityManager.Instance.AddExternalBonus(kId, 3f);
                            return $"朝纲一振，宿怨稍解，社稷 +3";
                        }
                        return "";
                    case "休养生息":
                        if (hasWinner)
                        {
                            StabilityManager.Instance.AddExternalBonus(kId, 2f);
                            return $"与民休息，民心渐附（稳定 +2）";
                        }
                        return "";
                    case "论功行赏":
                        if (hasWinner)
                        {
                            // 胜方随机 1 成员亲信 +10
                            var f = fa.Name == winnerName ? fa : fb;
                            if (f != null && f.MemberIds.Count > 0)
                            {
                                long pick = f.MemberIds[UnityEngine.Random.Range(0, f.MemberIds.Count)];
                                st.Favor[pick] = ClampFavor(GetFavor(kId, pick) + 10);
                                return $"{GetActorName(pick)} 得赏，亲信 +10";
                            }
                        }
                        return "";
                }
            }
            catch { }
            return "";
        }


        /// <summary>亲信度：首次=同族+30/忠臣+20/随机0~40；在朝每年+2；死亡清除。</summary>
        private void RefreshFavor(Kingdom k, string kId, PoliticsState st, int year)
        {
            // 死亡成员清除（亲信 + 政治安置——阶段2T-1）
            var dead = new List<long>();
            foreach (var kv in st.Favor)
            {
                var a = GetActor(kv.Key);
                if (a == null || !a.isAlive()) dead.Add(kv.Key);
            }
            foreach (var d in dead) st.Favor.Remove(d);
            var deadPlaced = new List<long>();
            foreach (var kv in st.Placed)
            {
                var a = GetActor(kv.Key);
                if (a == null || !a.isAlive()) deadPlaced.Add(kv.Key);
            }
            foreach (var d in deadPlaced) st.Placed.Remove(d);

            var records = OfficeManager.Instance.GetRecordsPublic(kId);
            if (records == null) return;
            foreach (var r in records)
            {
                if (!long.TryParse(r.PersonId, out long pid)) continue;
                var a = GetActor(pid);
                if (a == null || !a.isAlive()) continue;
                if (!st.Favor.TryGetValue(pid, out int fav))
                {
                    // 首次初始化：同族+30 / 忠臣+20 / 随机 0~40 兜底
                    int init = UnityEngine.Random.Range(0, 41);
                    if (k.king != null && a.clan != null && k.king.clan == a.clan) init += 30;
                    if (TraitManager.Instance.HasTrait(a, TraitManager.TRAIT_LOYAL)) init += 20;
                    st.Favor[pid] = ClampFavor(init);
                }
                else
                {
                    st.Favor[pid] = ClampFavor(fav + FAVOR_YEARLY);
                }
            }
        }

        /// <summary>皇帝每年提拔 1 人（60% 概率）：随机非皇权党官员 → Favor+15 入皇权党。</summary>
        private void KingPromotion(Kingdom k, string kId, PoliticsState st, int year)
        {
            if (k.king == null || !k.king.isAlive()) return;
            if (UnityEngine.Random.value > 0.60f) return;

            var factions = FactionManager.Instance.GetAllFactions(kId);
            var hq = factions.Find(f => f.Type == FactionManager.FactionType.HuangQuan);
            if (hq == null) return;

            var records = OfficeManager.Instance.GetRecordsPublic(kId);
            if (records == null || records.Count == 0) return;

            var candidates = new List<long>();
            foreach (var r in records)
            {
                if (!long.TryParse(r.PersonId, out long pid)) continue;
                if (pid == k.king.getID()) continue;
                if (hq.MemberIds.Contains(pid)) continue;
                if (st.Favor.TryGetValue(pid, out int fav) && fav >= FAVOR_AUTO_JOIN) continue;
                candidates.Add(pid);
            }
            if (candidates.Count == 0) return;

            long pick = candidates[UnityEngine.Random.Range(0, candidates.Count)];
            st.Favor[pick] = ClampFavor(GetFavor(kId, pick) + FAVOR_PROMOTE);
            if (!hq.MemberIds.Contains(pick)) hq.MemberIds.Add(pick);
            SetPlacement(kId, pick, hq.Name); // 阶段2T-1：安置保籍（原增量合并致多党籍/重建致蒸发两难的根本解）
            string pName = GetActorName(pick);
            string hqName = FName(k, hq); // 阶段2V：显示名按位阶
            PersonHistoryManager.Instance.RecordEvent(pick, PersonHistoryManager.CAT_ENYU,
                $"蒙 {k.name} 君主青眼，破格简拔入{hqName}（亲信 +{FAVOR_PROMOTE}）", year);
            AddRecent(st, year, $"君主简拔 {pName} 入{hqName}");
            ZhuluContext.Instance.LogInfo($"[FactionPolitics] 皇帝提拔: {k.name} {pName} 入皇权党");
        }

        /// <summary>事件主流程：每年最多 1 普通事件（70% 概率）；政变独立判定。</summary>
        private void TryEvents(Kingdom k, string kId, PoliticsState st, int year)
        {
            var factions = FactionManager.Instance.GetAllFactions(kId);
            if (factions.Count < 2) return;
            var hq = factions.Find(f => f.Type == FactionManager.FactionType.HuangQuan);
            var wg = factions.Find(f => f.Type == FactionManager.FactionType.WenGuan);
            var wu = factions.Find(f => f.Type == FactionManager.FactionType.WuGuan);
            var tz = factions.Find(f => f.Type == FactionManager.FactionType.TaiZi);
            if (hq == null || wg == null || wu == null || tz == null) return;

            // 阶段3D：体系风味事件（东方4/游牧4——独立冷却与概率，不占既有事件位）
            try { TryFlavorEvents(k, kId, st, year, factions, hq, wg, wu, tz); } catch { }

            // ① 政变判定（独立，不占普通事件位）
            TryCoup(k, kId, st, year, factions, hq, tz);

            if (UnityEngine.Random.value > 0.70f) return; // 30% 无事件年

            // ② 清洗（执政党影响力>60% && 烈度≥0.4 && 冷却15）
            if (IsCooled(st, EVT_QINGXI, COOL_QINGXI, year))
            {
                var ruling = GetRulingFaction(k, kId);
                if (ruling != null)
                {
                    float rp = ComputeFactionPower(k, kId, ruling);
                    float total = factions.Sum(f => ComputeFactionPower(k, kId, f));
                    if (total > 0 && rp / total > 0.60f && GetIntensity(k, kId) >= 0.4f && UnityEngine.Random.value < 0.5f)
                    {
                        ZhuluTrace.Write("faction_trace.csv", $"fp_event,{year},{kId},qingxi_try,{(rp / total).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)},{GetIntensity(k, kId).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}");
                        TryQingxi(k, kId, st, year, factions, ruling);
                        return;
                    }
                }
            }

            // ③ 构陷（执政党对最大反对党，冷却10）
            if (IsCooled(st, EVT_GOUXIAN, COOL_GOUXIAN, year) && UnityEngine.Random.value < 0.45f)
            {
                var ruling = GetRulingFaction(k, kId);
                if (ruling != null)
                {
                    var target = GetLargestOpposition(k, kId, factions, ruling);
                    if (target != null && target.MemberIds.Count > 0)
                    {
                        ZhuluTrace.Write("faction_trace.csv", $"fp_event,{year},{kId},gouxian_try,{target.Name}");
                        TryGouxian(k, kId, st, year, target);
                        return;
                    }
                }
            }

            // ④ 拉拢（最大反对党拉皇权党成员，冷却5）
            if (IsCooled(st, EVT_LALUO, COOL_LALUO, year) && UnityEngine.Random.value < 0.45f)
            {
                var ruling = GetRulingFaction(k, kId);
                var opp = ruling != null ? GetLargestOpposition(k, kId, factions, ruling) : wg;
                if (opp != null && opp.MemberIds.Count > 0)
                {
                    ZhuluTrace.Write("faction_trace.csv", $"fp_event,{year},{kId},laluo_try,{opp.Name}");
                    TryLaluo(k, kId, st, year, opp, hq);
                    return;
                }
            }

            // ⑤ 倾轧（随机两党，无冷却，10%）
            if (UnityEngine.Random.value < 0.10f)
            {
                ZhuluTrace.Write("faction_trace.csv", $"fp_event,{year},{kId},qingya_try");
                TryQingya(k, kId, st, year, factions);
            }

            // 世仇雪恨（独立检查，不占事件位——低概率）
            TryFeudTick(k, kId, st, year, factions);
        }

        private FactionManager.FactionRecord GetLargestOpposition(Kingdom k, string kId, List<FactionManager.FactionRecord> factions, FactionManager.FactionRecord ruling)
        {
            FactionManager.FactionRecord best = null;
            float bestPower = -1f;
            foreach (var f in factions)
            {
                if (f == ruling) continue;
                float p = ComputeFactionPower(k, kId, f);
                if (p > bestPower) { bestPower = p; best = f; }
            }
            return best;
        }

        /// <summary>清洗：罢免敌对党亲信度最低的官员 1 名。</summary>
        private void TryQingxi(Kingdom k, string kId, PoliticsState st, int year, List<FactionManager.FactionRecord> factions, FactionManager.FactionRecord ruling)
        {
            var opp = GetLargestOpposition(k, kId, factions, ruling);
            if (opp == null || opp.MemberIds.Count == 0) return;
            long victim = PickLowestFavor(st, opp.MemberIds);
            var record = FindOfficeRecord(kId, victim);
            if (record == null) return;

            string vName = GetActorName(victim);
            string title = OfficeManager.GetOfficeTitleForKingdom(record.OfficeId, k);
            string rName = FName(k, ruling); // 阶段2V：显示名按位阶
            OfficeManager.Instance.DismissOfficer(kId, k.name ?? "", record.OfficeId, year, "清洗");
            StabilityManager.Instance.AddExternalBonus(kId, -5f);
            ChronicleManager.Instance.AddEntry(year, ChronicleEventType.OfficeAppointed, kId, k.name ?? "", vName,
                $"<color={UI.ZhuluUIStyle.Hex.Negative}>【清洗】{rName} 得势，{title} {vName} 被逐出朝堂。</color>", false);
            PersonHistoryManager.Instance.RecordEvent(victim, PersonHistoryManager.CAT_CHAOZHENG, $"被 {rName} 清洗罢官（{title}）", year);
            PersonHistoryManager.Instance.RecordEvent(victim, PersonHistoryManager.CAT_CHAOZHENG, $"与 {rName} 结下深仇", year);
            ClearPlacement(kId, victim); // 罢官逐出朝堂：安置清除，回归身份党籍
            AddFeud(st, ruling.Name, opp.Name, 3);
            AddRecent(st, year, $"{rName} 得势清洗，{title} {vName} 被逐出朝堂");
            NoteEvent(st, EVT_QINGXI, year);
            ZhuluContext.Instance.LogInfo($"[FactionPolitics] 清洗: {k.name} {vName}({title}) 被 {ruling.Name} 清洗");
        }

        // ═══════════ 阶段3D 体系风味事件（东方4+游牧4）═══════════
        private void TryFlavorEvents(Kingdom k, string kId, PoliticsState st, int year,
            List<FactionManager.FactionRecord> factions,
            FactionManager.FactionRecord hq, FactionManager.FactionRecord wg,
            FactionManager.FactionRecord wu, FactionManager.FactionRecord tz)
        {
            bool nomad = false;
            try { nomad = KingdomCivilizationService.Instance.IsNomadic(k); } catch { }
            string kn = k.name ?? "?";
            Func<FactionManager.FactionRecord, float> share = f =>
            {
                float tot = 0f, own = 0f;
                foreach (var x in factions) { var pw = ComputeFactionPower(k, kId, x); tot += pw; if (x == f) own = pw; }
                return tot > 0 ? own / tot : 0f;
            };

            if (nomad)
            {
                // 库里尔台夺嫡：太子派势大 → 与王庭派结怨
                if (IsCooled(st, EVT_KD, COOL_FLAVOR, year) && share(tz) > 0.40f && UnityEngine.Random.value < 0.5f)
                {
                    AddFeud(st, tz.Name, hq.Name, 3);
                    AddRecent(st, year, "库里尔台夺嫡：" + FName(k, tz) + "势大争位，王庭震动");
                    ChronicleManager.Instance.AddEntry(year, ChronicleEventType.OfficeAppointed, kId, kn, "",
                        "<color=#c8b98a>【库里尔台夺嫡】" + kn + "诸那颜聚议，" + FName(k, tz) + "与" + FName(k, hq) + "势同水火。</color>", false);
                    NoteEvent(st, EVT_KD, year);
                }
                // 萨满谶言：天眷<40 → 长老派(萨满源)借谶言坐大
                float mv = 100f;
                try { mv = NomadicMandateManager.Instance.GetMandateValue(kId); } catch { }
                if (IsCooled(st, EVT_SHAMAN, COOL_FLAVOR, year) && mv < 40f && UnityEngine.Random.value < 0.5f)
                {
                    foreach (var m in wg.MemberIds) st.Favor[m] = ClampFavor(GetFavor(kId, m) + 8);
                    AddRecent(st, year, "萨满谶言：天弃之流言四起，" + FName(k, wg) + "借祭司之言坐大");
                    ChronicleManager.Instance.AddEntry(year, ChronicleEventType.OfficeAppointed, kId, kn, "",
                        "<color=#c8b98a>【萨满谶言】" + kn + "萨满祭天，谶言天弃之流传，人心惶惶。</color>", false);
                    NoteEvent(st, EVT_SHAMAN, year);
                }
                // 部落叛离：某在野派平均亲信<25 → 人心思迁
                if (IsCooled(st, EVT_BULUO, COOL_FLAVOR, year) && UnityEngine.Random.value < 0.4f)
                {
                    var ruling0 = GetRulingFaction(k, kId);
                    foreach (var f in factions)
                    {
                        if (f == ruling0 || f.MemberIds.Count == 0) continue;
                        int sum = 0; foreach (var m in f.MemberIds) sum += GetFavor(kId, m);
                        if ((float)sum / f.MemberIds.Count < 25f)
                        {
                            foreach (var m in f.MemberIds) st.Favor[m] = ClampFavor(GetFavor(kId, m) - 10);
                            AddRecent(st, year, "部落叛离之忧：" + FName(k, f) + "诸部亲信离心（亲信 -10）");
                            ChronicleManager.Instance.AddEntry(year, ChronicleEventType.OfficeAppointed, kId, kn, "",
                                "<color=#f73939>【部落叛离】" + kn + "附庸诸部怨声载道，有举部出走之虞。</color>", false);
                            break;
                        }
                    }
                    NoteEvent(st, EVT_BULUO, year);
                }
                // 汗位争立：王庭派内亲信极差>50 → 洗牌
                if (IsCooled(st, EVT_ZHENGLI, COOL_FLAVOR, year) && hq.MemberIds.Count >= 2 && UnityEngine.Random.value < 0.4f)
                {
                    int maxF = int.MinValue, minF = int.MaxValue;
                    foreach (var m in hq.MemberIds) { int v = GetFavor(kId, m); if (v > maxF) maxF = v; if (v < minF) minF = v; }
                    if (maxF - minF > 50)
                    {
                        foreach (var m in hq.MemberIds)
                        {
                            int v = GetFavor(kId, m);
                            st.Favor[m] = ClampFavor(v >= maxF - 10 ? v - 15 : v + 10);
                        }
                        AddRecent(st, year, "汗位争立：黄金家族内讧，王庭派权势洗牌");
                        ChronicleManager.Instance.AddEntry(year, ChronicleEventType.OfficeAppointed, kId, kn, "",
                            "<color=#f73939>【汗位争立】" + kn + "黄金家族争立，那颜各怀异心。</color>", false);
                        NoteEvent(st, EVT_ZHENGLI, year);
                    }
                }
                return;
            }

            // ── 东方侧 ──
            // 清流议政：权臣权势≥70 → 执政党亲信-5
            var topPower = CorruptionManager.Instance.GetTopPowerfulOfficial(kId);
            if (IsCooled(st, EVT_QINGLIU, COOL_FLAVOR, year) && topPower.Item3 >= 70f && UnityEngine.Random.value < 0.5f)
            {
                var ruling1 = GetRulingFaction(k, kId);
                if (ruling1 != null && ruling1 != wg)
                {
                    foreach (var m in ruling1.MemberIds) st.Favor[m] = ClampFavor(GetFavor(kId, m) - 5);
                    AddRecent(st, year, "清流议政：在野清流联名弹劾权臣，" + FName(k, ruling1) + "亲信受损（-5）");
                    ChronicleManager.Instance.AddEntry(year, ChronicleEventType.OfficeAppointed, kId, kn, topPower.Item2,
                        "<color=#88b8e8>【清流议政】" + kn + "清流联名上疏，弹劾权臣" + topPower.Item2 + "（权势" + topPower.Item3.ToString("F0") + "）。</color>", false);
                    NoteEvent(st, EVT_QINGLIU, year);
                }
            }
            // 外戚干政：太子派势大 → 外戚坐大
            if (IsCooled(st, EVT_WAIQI, COOL_FLAVOR, year) && share(tz) > 0.35f && UnityEngine.Random.value < 0.5f)
            {
                foreach (var m in tz.MemberIds) st.Favor[m] = ClampFavor(GetFavor(kId, m) + 8);
                AddRecent(st, year, "外戚干政：储君外氏一族权势膨胀，" + FName(k, tz) + "亲信 +8");
                ChronicleManager.Instance.AddEntry(year, ChronicleEventType.OfficeAppointed, kId, kn, "",
                    "<color=#FFCC88>【外戚干政】" + kn + "储君外氏坐大，朝野侧目。</color>", false);
                NoteEvent(st, EVT_WAIQI, year);
            }
            // 门生故吏：执政党结网
            if (IsCooled(st, EVT_MENSHENG, COOL_FLAVOR, year) && UnityEngine.Random.value < 0.4f)
            {
                var ruling2 = GetRulingFaction(k, kId);
                if (ruling2 != null)
                {
                    foreach (var m in ruling2.MemberIds) st.Favor[m] = ClampFavor(GetFavor(kId, m) + 5);
                    AddRecent(st, year, "门生故吏：" + FName(k, ruling2) + "广结党羽，亲信 +5");
                    NoteEvent(st, EVT_MENSHENG, year);
                }
            }
            // 宦官近幸：帝权<40 → 近幸跃升
            int pv = 50; try { pv = KingdomExt.GetPowerValue(kId); } catch { }
            if (IsCooled(st, EVT_HUANGUAN, COOL_FLAVOR, year) && pv < 40 && UnityEngine.Random.value < 0.4f)
            {
                long low = 0; int lowV = int.MaxValue;
                foreach (var m in hq.MemberIds) { int v = GetFavor(kId, m); if (v < lowV) { lowV = v; low = m; } }
                if (low != 0)
                {
                    st.Favor[low] = ClampFavor(lowV + 10);
                    AddFeud(st, hq.Name, wg.Name, 1);
                    AddRecent(st, year, "宦官近幸：帝权衰微（" + pv + "），近幸跃升亲信 +10");
                    ChronicleManager.Instance.AddEntry(year, ChronicleEventType.OfficeAppointed, kId, kn, "",
                        "<color=#b8a87a>【宦官近幸】" + kn + "帝权式微，近幸弄权，清流切齿。</color>", false);
                    NoteEvent(st, EVT_HUANGUAN, year);
                }
            }
        }

        /// <summary>构陷：目标亲信度 −20；若 &lt;30 免职。</summary>
        private void TryGouxian(Kingdom k, string kId, PoliticsState st, int year, FactionManager.FactionRecord target)
        {
            var hq = FactionManager.Instance.GetAllFactions(kId).Find(f => f.Type == FactionManager.FactionType.HuangQuan);
            if (hq == null || hq.MemberIds.Count == 0) return;
            long victim = PickHighestFavor(st, hq.MemberIds);
            if (victim == 0) return;

            string vName = GetActorName(victim);
            string tName = FName(k, target); // 阶段2V：显示名按位阶
            st.Favor[victim] = ClampFavor(GetFavor(kId, victim) + FAVOR_GOUXIAN_HIT);
            bool gNomad = false;
            try { gNomad = KingdomCivilizationService.Instance.IsNomadic(k); } catch { }
            PersonHistoryManager.Instance.RecordEvent(victim, PersonHistoryManager.CAT_CHAOZHENG,
                $"遭 {tName} 构陷，{(gNomad ? "可汗" : "皇帝")}疑心渐重（亲信 {FAVOR_GOUXIAN_HIT}）", year);

            var record = FindOfficeRecord(kId, victim);
            if (GetFavor(kId, victim) < 30 && record != null)
            {
                string title = OfficeManager.GetOfficeTitleForKingdom(record.OfficeId, k);
                OfficeManager.Instance.DismissOfficer(kId, k.name ?? "", record.OfficeId, year, "构陷");
                ChronicleManager.Instance.AddEntry(year, ChronicleEventType.OfficeAppointed, kId, k.name ?? "", vName,
                    $"<color={UI.ZhuluUIStyle.Hex.Negative}>【构陷】{tName} 罗织罪名，{title} {vName} 被罢黜下狱。</color>", false);
                PersonHistoryManager.Instance.RecordEvent(victim, PersonHistoryManager.CAT_CHAOZHENG, $"因构陷被罢官（{title}）", year);
                hq.MemberIds.Remove(victim);
                ClearPlacement(kId, victim); // 罢官逐出朝堂
            }
            AddFeud(st, target.Name, "皇权党", 2);
            AddRecent(st, year, $"{tName} 罗织罪名构陷 {vName}（亲信 {FAVOR_GOUXIAN_HIT}）");
            NoteEvent(st, EVT_GOUXIAN, year);
            ZhuluContext.Instance.LogInfo($"[FactionPolitics] 构陷: {target.Name} 构陷 {vName}(亲信{GetFavor(kId, victim)})");
        }

        /// <summary>拉拢：目标亲信度 +15；若≥60 转投皇权党。</summary>
        private void TryLaluo(Kingdom k, string kId, PoliticsState st, int year, FactionManager.FactionRecord opp, FactionManager.FactionRecord hq)
        {
            var candidates = new List<long>();
            foreach (var m in hq.MemberIds)
            {
                if (m == k.king?.getID()) continue;
                if (st.Favor.TryGetValue(m, out int f) && f < FAVOR_AUTO_JOIN) candidates.Add(m);
            }
            if (candidates.Count == 0) return;
            long pick = candidates[UnityEngine.Random.Range(0, candidates.Count)];
            string pName = GetActorName(pick);
            string oName = FName(k, opp); // 阶段2V：显示名按位阶
            st.Favor[pick] = ClampFavor(GetFavor(kId, pick) + FAVOR_LALUO_GAIN);
            PersonHistoryManager.Instance.RecordEvent(pick, PersonHistoryManager.CAT_CHAOZHENG,
                $"{oName} 暗中拉拢，许以高官厚禄（亲信 +{FAVOR_LALUO_GAIN}）", year);
            if (GetFavor(kId, pick) >= FAVOR_AUTO_JOIN && hq.MemberIds.Contains(pick))
            {
                hq.MemberIds.Remove(pick);
                if (!opp.MemberIds.Contains(pick)) opp.MemberIds.Add(pick);
                SetPlacement(kId, pick, opp.Name); // 转投保籍
                PersonHistoryManager.Instance.RecordEvent(pick, PersonHistoryManager.CAT_CHAOZHENG, $"转投 {oName}", year);
                AddRecent(st, year, $"{pName} 背弃君恩，转投 {oName}");
                ZhuluContext.Instance.LogInfo($"[FactionPolitics] 拉拢成功: {pName} 转投 {opp.Name}");
            }
            NoteEvent(st, EVT_LALUO, year);
        }

        /// <summary>倾轧：随机两党世仇+1，双方各 1 成员亲信 −5。</summary>
        private void TryQingya(Kingdom k, string kId, PoliticsState st, int year, List<FactionManager.FactionRecord> factions)
        {
            var active = factions.Where(f => f.MemberIds.Count > 0).ToList();
            if (active.Count < 2) return;
            var a = active[UnityEngine.Random.Range(0, active.Count)];
            var b = active[UnityEngine.Random.Range(0, active.Count)];
            if (a == b) return;
            AddFeud(st, a.Name, b.Name, 1);
            foreach (var f in new[] { a, b })
            {
                if (f.MemberIds.Count == 0) continue;
                long m = f.MemberIds[UnityEngine.Random.Range(0, f.MemberIds.Count)];
                st.Favor[m] = ClampFavor(GetFavor(kId, m) + FAVOR_QIANGYA);
            }
            string aName = FName(k, a), bName = FName(k, b); // 阶段2V：显示名按位阶
            AddRecent(st, year, $"{aName} 与 {bName} 朝堂倾轧，结怨渐深（世仇+1）");
            PersonHistoryManager.Instance.RecordEvent(a.LeaderId != 0 ? a.LeaderId : (a.MemberIds.Count > 0 ? a.MemberIds[0] : 0),
                PersonHistoryManager.CAT_CHAOZHENG, $"{aName} 与 {bName} 在朝堂互相倾轧，结怨渐深", year);
        }

        /// <summary>世仇雪恨：值≥20 且距上次≥10年，30% 概率触发构陷强化版。</summary>
        private void TryFeudTick(Kingdom k, string kId, PoliticsState st, int year, List<FactionManager.FactionRecord> factions)
        {
            foreach (var kv in st.Feuds)
            {
                if (kv.Value < FEUD_SNOWBALL_THRESHOLD) continue;
                if (st.FeudCool.TryGetValue(kv.Key, out int last) && year - last < COOL_FEUD) continue;
                if (UnityEngine.Random.value > 0.30f) continue;

                string[] parts = kv.Key.Split('|');
                if (parts.Length != 2) continue;
                var fa = factions.Find(f => f.Name == parts[0]);
                var fb = factions.Find(f => f.Name == parts[1]);
                var attacker = (fa?.MemberIds.Count ?? 0) > 0 ? fa : fb;
                var defender = attacker == fa ? fb : fa;
                if (attacker == null || defender == null) continue;
                long victim = PickHighestFavor(st, defender.MemberIds);
                if (victim == 0) continue;

                string vName = GetActorName(victim);
                string atkName = FName(k, attacker), defName = FName(k, defender); // 阶段2V：显示名按位阶
                st.Favor[victim] = ClampFavor(GetFavor(kId, victim) - 15);
                PersonHistoryManager.Instance.RecordEvent(victim, PersonHistoryManager.CAT_CHAOZHENG,
                    $"遭 {atkName} 雪恨报复（世仇 {kv.Value}），亲信 −15", year);
                PersonHistoryManager.Instance.RecordEvent(attacker.LeaderId != 0 ? attacker.LeaderId : (attacker.MemberIds.Count > 0 ? attacker.MemberIds[0] : 0),
                    PersonHistoryManager.CAT_CHAOZHENG, $"向 {defName} 雪恨，快意恩仇", year);
                AddRecent(st, year, $"{atkName} 向 {defName} 雪恨，{vName} 遭报复（世仇 {kv.Value}）");
                ChronicleManager.Instance.AddEntry(year, ChronicleEventType.OfficeAppointed, kId, k.name ?? "", vName,
                    $"<color=#ac8b7f>【世仇】{atkName} 与 {defName} 宿怨爆发，朝野侧目。</color>", false);
                st.FeudCool[kv.Key] = year;
                st.Feuds[kv.Key] = kv.Value - 10;
                return;
            }
        }

        /// <summary>政变（废立制）：太子党/自建党党魁发动，亲属继位，不改国号。</summary>
        private void TryCoup(Kingdom k, string kId, PoliticsState st, int year,
            List<FactionManager.FactionRecord> factions, FactionManager.FactionRecord hq, FactionManager.FactionRecord tz)
        {
            if (k.king == null || !k.king.isAlive()) return;

            // 皇帝在位年
            long reignStart = KingdomDataExt.GetLong(k, "reign_start", year);
            int kingYears = year - (int)reignStart;

            // 发起党：太子党（或影响力最大的自建党）
            FactionManager.FactionRecord coupParty = tz;
            var customs = factions.Where(f => f.IsCustom).ToList();
            if (customs.Count > 0)
            {
                FactionManager.FactionRecord bestCustom = null;
                float bestPower = -1f;
                foreach (var c in customs)
                {
                    float p = ComputeFactionPower(k, kId, c);
                    if (p > bestPower) { bestPower = p; bestCustom = c; }
                }
                if (bestCustom != null && bestPower > ComputeFactionPower(k, kId, tz)) coupParty = bestCustom;
            }
            if (coupParty == null || coupParty.LeaderId == 0) return;

            long coupLeader = coupParty.LeaderId;
            float leaderPower = GetMemberPowerPublic(kId, coupLeader);
            float royalPower = ComputeFactionPower(k, kId, hq);

            // D5-② 相对口径分母：全体党势合计（皇权党计入分母，其余按引用去重）
            float totalFactionPower = royalPower;
            foreach (var f in factions)
            {
                if (f == null || ReferenceEquals(f, hq)) continue;
                totalFactionPower += ComputeFactionPower(k, kId, f);
            }

            if (!ShouldTriggerCoup(leaderPower, royalPower, totalFactionPower, kingYears, st.LastCoupYear, year, UnityEngine.Random.value)) return;

            // 废立制选新君：储君 → 同族宗藩 → 政变者本人
            Actor newKing = PickSuccessor(k, coupLeader);
            if (newKing == null) return;
            long oldKingId = k.king.getID();   // 阶段2T-1：setKing 前捕获（原事后取 k.king=新君，"被废"记录错写给新君）
            string oldName = k.king.getName();
            string newName = newKing.getName();
            bool sameFamily = newKing.clan != null && k.king.clan == newKing.clan;

            // D2 政变插桩（2026-10-06）：判定通过且确定废立，一行/次
            ZhuluTrace.Write("faction_trace.csv",
                $"fp_coup,{year},{kId},{coupParty.Name},{leaderPower.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)},{royalPower.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)},{kingYears},{sameFamily}");

            try
            {
                k.setKing(newKing, false);
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[FactionPolitics] 政变 setKing 失败: {ex.Message}");
                return;
            }
            // 阶段2A A1-6：标记篡位（政变=废立，换君观察分类用）
            DynastyObserver.Instance.MarkUsurpation(k.id.ToString());

            // 政变者成权臣：入皇权党、出政变党（唯一党籍），安置保籍
            st.Favor[coupLeader] = 100;
            if (!hq.MemberIds.Contains(coupLeader)) hq.MemberIds.Add(coupLeader);
            coupParty.MemberIds.Remove(coupLeader);
            SetPlacement(kId, coupLeader, hq.Name);
            st.LastCoupYear = year;
            st.CoupCount++;
            NoteEvent(st, EVT_COUP, year);

            StabilityManager.Instance.AddExternalBonus(kId, -15f);
            string coupName = GetActorName(coupLeader);
            string coupPartyName = FName(k, coupParty); // 阶段2V：显示名按位阶
            ChronicleManager.Instance.AddEntry(year, ChronicleEventType.KingChange, kId, k.name ?? "", coupName,
                sameFamily
                    ? $"<color=#FF0000>【政变】{coupPartyName} 党魁 {coupName} 发动宫变！废 {oldName}，迎立宗亲 {newName}，改元不改国号。</color>"
                    : $"<color=#FF0000>【政变】{coupPartyName} 党魁 {coupName} 发动宫变！废 {oldName}，{newName} 以外姓入主，改元不改国号。</color>", true);
            WorldTip.showNow($"【{k.name}】{coupPartyName} 发动政变！{newName} 即位！", pTranslate: false, "top", 8f, UI.ZhuluUIStyle.Hex.Negative);
            AddRecent(st, year, $"{coupPartyName} 宫变得手：{oldName} 被废，{newName} 即位");
            PersonHistoryManager.Instance.RecordEvent(coupLeader, PersonHistoryManager.CAT_CHAOZHENG,
                $"发动政变成功，{newName} 即位，成为新君座下第一权臣", year);
            PersonHistoryManager.Instance.RecordEvent(oldKingId, PersonHistoryManager.CAT_JUNLIN,
                $"遭 {coupPartyName} 政变废黜（{oldName} 退位）", year);
            ZhuluContext.Instance.LogInfo($"[FactionPolitics] 政变: {k.name} {coupName} 废 {oldName} 立 {newName} (同族={sameFamily})");
        }

        /// <summary>废立制选新君：储君（同族）→ 同族宗藩（最年长）→ 政变者本人（外姓兜底）。</summary>
        private Actor PickSuccessor(Kingdom k, long coupLeader)
        {
            // ① 储君
            string heirId = KingdomDataExt.GetStr(k, "heir_id");
            if (!string.IsNullOrEmpty(heirId) && long.TryParse(heirId, out long heirL))
            {
                var heir = GetActor(heirL);
                // 2026-08-20 bug修复：储君必须是本国成员且非他国国王——
                // 原校验只查存活+同族，移居他国的族子会被立为君（一人两国/实际无人继位）
                if (heir != null && heir.isAlive() && heir.clan != null && k.king.clan == heir.clan
                    && heir.kingdom == k && !heir.isKing())
                    return heir;
            }
            // ② 同族宗藩（最年长）
            if (k.units != null)
            {
                Actor best = null;
                int bestAge = -1;
                foreach (var u in k.units)
                {
                    if (u == null || !u.isAlive()) continue;
                    if (u.getID() == k.king.getID()) continue;
                    if (u.clan == null || k.king.clan != u.clan) continue;
                    if (u.getAge() > bestAge) { bestAge = u.getAge(); best = u; }
                }
                if (best != null) return best;
            }
            // ③ 政变者本人（外姓）
            return GetActor(coupLeader);
        }

        // ═══════════════════════ 工具 ═══════════════════════

        /// <summary>阶段2V：党派显示名（按国家位阶——事件/史册/近事文本用）。</summary>
        private static string FName(Kingdom k, FactionManager.FactionRecord f)
        {
            return FactionManager.GetFactionDisplayName(k, f);
        }

        private static Actor GetActor(long id)
        {
            try { return World.world.units.get(id); } catch { return null; }
        }

        private static string GetActorName(long id)
        {
            var a = GetActor(id);
            return a != null ? a.getName() : "无名氏";
        }

        private long PickHighestFavor(PoliticsState st, List<long> members)
        {
            long best = 0; int bestF = -1;
            foreach (var m in members)
            {
                int f = st.Favor.TryGetValue(m, out int v) ? v : DefaultFavor;
                if (f > bestF) { bestF = f; best = m; }
            }
            return best;
        }

        private long PickLowestFavor(PoliticsState st, List<long> members)
        {
            long best = 0; int bestF = 999;
            foreach (var m in members)
            {
                int f = st.Favor.TryGetValue(m, out int v) ? v : DefaultFavor;
                if (f < bestF) { bestF = f; best = m; }
            }
            return best;
        }

        private OfficeAppointmentRecord FindOfficeRecord(string kId, long actorId)
        {
            var records = OfficeManager.Instance.GetRecordsPublic(kId);
            if (records == null) return null;
            foreach (var r in records)
            {
                if (long.TryParse(r.PersonId, out long pid) && pid == actorId) return r;
            }
            return null;
        }

        // ═══════════════════════ 快照 ═══════════════════════

        // ---- batch10-A1: schema v5 section ----
        public sealed class PoliticsSectionState
        {
            public Dictionary<string, PoliticsState> States = new Dictionary<string, PoliticsState>();
            public bool FreshWorld;
        }
        public string SectionName => "faction_politics";
        public Dictionary<string, PoliticsSectionState> CaptureStates()
        {
            return new Dictionary<string, PoliticsSectionState>
            {
                ["__global__"] = new PoliticsSectionState { States = new Dictionary<string, PoliticsState>(_states), FreshWorld = _freshWorld }
            };
        }
        public void ApplyStates(Dictionary<string, PoliticsSectionState> data)
        {
            if (data == null || !data.TryGetValue("__global__", out var st) || st == null) return;
            _states.Clear();
            _freshWorld = false;
            if (st.States != null)
                foreach (var kv in st.States)
                {
                    var ps = kv.Value;
                    if (ps == null) continue;
                    // clamp Favor on restore (kept from v4 semantics)
                    if (ps.Favor != null)
                        foreach (var ak in new List<long>(ps.Favor.Keys)) ps.Favor[ak] = ClampFavor(ps.Favor[ak]);
                    _states[kv.Key] = ps;
                }
            _freshWorld = st.FreshWorld;
        }



        public void MigrateFromCustomData() { }

        public void ResetForNewWorld()
        {
            _states.Clear();
            _freshWorld = true;
            _lastDebateTopic.Clear();
        }

        /// <summary>阶段2T-1：旧档启用党争（由 FactionManager.BootstrapForExistingKingdoms 联动置位）。</summary>
        public void EnableForExistingWorld()
        {
            _freshWorld = true;
        }

        public void ResetRuntime()
        {
            _states.Clear();
            _freshWorld = false;
        }

        public void ClearForTest() { _states.Clear(); _freshWorld = false; _lastDebateTopic.Clear(); }

        /// <summary>测试辅助：注入一国状态。</summary>
        public void SetStateForTest(string kingdomId, PoliticsState state) { _states[kingdomId] = state; }

        public int StateCount => _states.Count;
    }
}
