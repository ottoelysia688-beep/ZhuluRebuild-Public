using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ZhuluRebuild.Core;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Domain
{
    /// <summary>
    /// 个人史册管理器（借鉴 ActorHistory 1.8.0 人生叙事分类，逐鹿分级版）。
    /// 每个 Actor 一生记录，按身份分级：
    ///   - 普通人：只记出生/死亡（2 条简单信息）
    ///   - 官员/皇帝/城主：详细（身份变化/官职/党争大事）
    /// 人生叙事分类：机遇/抉择/挫折/成就/牵绊/日常。
    /// 存储：内存字典 + 快照（Dictionary&lt;long, string&gt;，JSON per actor）。
    /// </summary>
    public class PersonHistoryManager : ISnapshotSection<PersonHistoryManager.PersonHistState>
    {
        public static PersonHistoryManager Instance { get; } = new PersonHistoryManager();

        // ── 叙事分类常量（阶段2S-10 逐鹿专属六类，替代 ActorHistory 通用六类）──
        public const string CAT_GONGMING = "功名";    // 科举及第/状元/武举/开科入人才库
        public const string CAT_SHITU = "仕途";       // 任命/授职/升迁/罢黜/致仕
        public const string CAT_CHAOZHENG = "朝争";   // 入党/转党/构陷/清洗/拉拢/政变/结仇
        public const string CAT_JUNLIN = "君临";      // 即位/被废/禅让（君主专属）
        public const string CAT_ENYU = "恩遇";        // 皇帝提拔/破格简拔（君恩）
        public const string CAT_JIAZU = "家族";       // 婚育/血脉（预留）

        private const int MaxEntriesPerPerson = 50;
        private const int MaxEntryTextLength = 80; // 阶段2S-7：单条文本截断上限

        /// <summary>单条历史条目。</summary>
        public class HistoryEntry
        {
            public int Year { get; set; }
            public string Category { get; set; }
            public string Text { get; set; }
        }

        /// <summary>个人史册记录（序列化形态）。</summary>
        public class PersonRecord
        {
            public long ActorId { get; set; }
            public int BirthYear { get; set; }
            public int DeathYear { get; set; } = -1;
            public bool IsDetailed { get; set; }            // 官员/皇帝/城主=true（有详细条目权限）
            public List<HistoryEntry> Entries { get; set; } = new List<HistoryEntry>();
        }

        private readonly Dictionary<long, PersonRecord> _records = new Dictionary<long, PersonRecord>();

        // 出生/死亡扫描状态
        private readonly Dictionary<long, bool> _seenAlive = new Dictionary<long, bool>(); // ActorId → 上次年度是否存活
        private bool _initialScanDone = false;
        private int _lastScanYear = -1;

        private PersonHistoryManager() { }

        /// <summary>开关（FeatureFlags groundwork_enabled；旧档也可运行——纯数据无副作用）。</summary>
        private bool Enabled => ZhuluConfig.Instance.GroundworkEnabled;

        // ═══════════════════════ 公开查询 ═══════════════════════

        /// <summary>取某人史册（无则 null）。</summary>
        public PersonRecord GetRecord(long actorId)
        {
            _records.TryGetValue(actorId, out var rec);
            return rec;
        }

        /// <summary>取某人史册（无则惰性创建，附出生年）。</summary>
        public PersonRecord GetOrCreate(long actorId, int currentYear)
        {
            if (!_records.TryGetValue(actorId, out var rec))
            {
                rec = new PersonRecord { ActorId = actorId, BirthYear = currentYear };
                _records[actorId] = rec;
            }
            return rec;
        }

        /// <summary>记录一条个人史册事件。普通人不记（分级保护）。</summary>
        public void RecordEvent(long actorId, string category, string text, int year)
        {
            if (!Enabled) return;
            try
            {
                var rec = GetOrCreate(actorId, year);
                if (!rec.IsDetailed) return; // 普通人只记出生/死亡

                // 阶段2S-6：同年同分类同文本去重（防党争/科举等年度事件重复刷屏，史册 Chronicle 同款机制）
                for (int i = 0; i < rec.Entries.Count; i++)
                {
                    var e = rec.Entries[i];
                    if (e != null && e.Year == year && e.Category == category && e.Text == text) return;
                }

                // 阶段2S-7：长文本截断保护（防 UI 卡片溢出——上限 80 字符）
                if (!string.IsNullOrEmpty(text) && text.Length > MaxEntryTextLength)
                {
                    text = text.Substring(0, MaxEntryTextLength - 1) + "…";
                }

                rec.Entries.Add(new HistoryEntry { Year = year, Category = category, Text = text });
                if (rec.Entries.Count > MaxEntriesPerPerson)
                {
                    rec.Entries.RemoveAt(0); // 丢最旧
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[PersonHistory] RecordEvent 异常: {ex.Message}");
            }
        }

        /// <summary>标记某人为详细记录对象（官员/皇帝/城主），并补记出生年。</summary>
        public void MarkDetailed(long actorId, int currentYear)
        {
            if (!Enabled) return;
            var rec = GetOrCreate(actorId, currentYear);
            if (!rec.IsDetailed)
            {
                rec.IsDetailed = true;
                // 普通人升级为详记时补一条入仕级记录由调用方负责
            }
        }

        // ═══════════════════════ 年度扫描（出生/死亡） ═══════════════════════

        /// <summary>全局年度扫描：新 Actor 建出生记录；消失 Actor 记死亡。每年一次。</summary>
        public void OnGlobalYear(int year)
        {
            if (!Enabled) return;
            if (year == _lastScanYear) return;
            _lastScanYear = year;

            try
            {
                if (World.world?.units == null) return;

                var currentAlive = new HashSet<long>();
                var unitsList = World.world.units.getSimpleList();

                // 阶段3 性能：国君集合一次构建（原 IsKing 每个新 unit 遍历全部王国——
                // 新档首年全员首见 = 万级 unit × 60 国比较）
                bool hasNewcomer = false;
                for (int i = 0; i < unitsList.Count; i++)
                {
                    var u = unitsList[i];
                    if (u == null) continue;
                    long uid = u.getID();
                    if (uid <= 0) continue;
                    if (u.isAlive() && !_seenAlive.ContainsKey(uid)) { hasNewcomer = true; break; }
                }
                HashSet<Actor> kings = null;
                if (hasNewcomer)
                {
                    kings = new HashSet<Actor>();
                    if (World.world.kingdoms?.list != null)
                        foreach (var k in World.world.kingdoms.list)
                            if (k != null && k.king != null) kings.Add(k.king);
                }

                for (int i = 0; i < unitsList.Count; i++)
                {
                    var u = unitsList[i];
                    if (u == null) continue;
                    long uid = u.getID();
                    if (uid <= 0) continue;

                    if (u.isAlive())
                    {
                        currentAlive.Add(uid);
                        if (!_seenAlive.ContainsKey(uid))
                        {
                            // 首次见到：出生记录（出生年用 Actor 出生数据，无则当前年）
                            var rec = GetOrCreate(uid, year);
                            rec.BirthYear = GetActorBirthYear(u, year);
                            if (u.isCityLeader() || (kings != null && kings.Contains(u)))
                            {
                                MarkDetailed(uid, year);
                            }
                        }
                        _seenAlive[uid] = true;
                    }
                    // 已死亡但仍暂存在 World.units 的 Actor 不加入 currentAlive，
                    // 由下方统一完成“活→死”转换并补卒年；这里提前写 false 会吞掉死亡事件。
                }

                // 之前见过但本次不再存活/不在列表 → 死亡记录
                var dead = new List<long>();
                foreach (var kv in _seenAlive)
                {
                    if (kv.Value && !currentAlive.Contains(kv.Key))
                    {
                        dead.Add(kv.Key);
                    }
                }
                foreach (long deadId in dead)
                {
                    if (_records.TryGetValue(deadId, out var rec))
                    {
                        rec.DeathYear = year;
                        // 阶段3：详记人物补"卒"条目（享年按出生年推算；普通人只更新字段不加条目）
                        if (rec.IsDetailed && rec.BirthYear > 0 && year > rec.BirthYear)
                        {
                            RecordEvent(deadId, CAT_JIAZU, $"卒，享年{year - rec.BirthYear}岁", year);
                        }
                    }
                    _seenAlive[deadId] = false;
                }

                if (!_initialScanDone) _initialScanDone = true;
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[PersonHistory] OnGlobalYear 异常: {ex.Message}");
            }
        }

        // （阶段3 性能：IsKing 已由年度扫描开头的国君 HashSet 预构建替代——O(1) 查询）

        private static int GetActorBirthYear(Actor a, int fallbackYear)
        {
            try
            {
                // 原版 Actor 出生年：data.birthYear（尽力读取，失败回退）
                var birth = Traverse.Create(a).Field("data").Field("birthYear").GetValue<int>();
                if (birth > 0) return birth;
            }
            catch { }
            return fallbackYear;
        }

        // ═══════════════════════ 快照 ═══════════════════════

        // ---- batch10-A1: schema v5 section ----
        public sealed class PersonHistState { public Dictionary<long, PersonRecord> Records = new Dictionary<long, PersonRecord>(); }
        public string SectionName => "person_histories";
        public Dictionary<string, PersonHistState> CaptureStates()
        {
            return new Dictionary<string, PersonHistState>
            {
                ["__global__"] = new PersonHistState { Records = new Dictionary<long, PersonRecord>(_records) }
            };
        }
        public void ApplyStates(Dictionary<string, PersonHistState> data)
        {
            if (data == null || !data.TryGetValue("__global__", out var st) || st == null) return;
            _records.Clear();
            _seenAlive.Clear();
            _initialScanDone = false;
            _lastScanYear = -1;
            if (st.Records == null) return;
            foreach (var kv in st.Records)
            {
                var rec = kv.Value;
                if (rec == null) continue;
                // legacy category migration kept from v4 semantics
                if (rec.Entries != null)
                    foreach (var entry in rec.Entries)
                        if (entry != null) entry.Category = MigrateCategory(entry.Category, entry.Text);
                _records[kv.Key] = rec;
            }
        }



        /// <summary>
        /// 旧分类 → 逐鹿六类迁移（阶段2S-10）：先按文本关键词精确归类，无命中按旧类兜底。
        /// 纯函数，只读旧数据不改语义。
        /// </summary>
        private static string MigrateCategory(string oldCategory, string text)
        {
            if (string.IsNullOrEmpty(oldCategory)) return CAT_SHITU;
            // 新类直通
            if (oldCategory == CAT_GONGMING || oldCategory == CAT_SHITU || oldCategory == CAT_CHAOZHENG
                || oldCategory == CAT_JUNLIN || oldCategory == CAT_ENYU || oldCategory == CAT_JIAZU) return oldCategory;

            string t = text ?? "";
            if (ContainsAny(t, "及第", "开科", "状元", "秀才", "举人", "进士", "人才库", "队长", "武举", "科场")) return CAT_GONGMING;
            if (ContainsAny(t, "即位", "登基", "继位", "践祚", "被废", "废帝", "禅让", "退位")) return CAT_JUNLIN;
            if (ContainsAny(t, "提拔", "青眼", "简拔", "破格")) return CAT_ENYU;
            if (ContainsAny(t, "构陷", "清洗", "转投", "入党", "拉拢", "政变", "结下深仇", "倾轧", "党", "亲信")) return CAT_CHAOZHENG;
            if (ContainsAny(t, "罢", "黜", "致仕", "授", "任命", "为官", "封")) return CAT_SHITU;

            // 兜底：按旧类整体归并
            switch (oldCategory)
            {
                case "成就": return CAT_GONGMING;
                case "抉择": return CAT_CHAOZHENG;
                case "牵绊": return CAT_CHAOZHENG;
                case "日常": return CAT_SHITU;
                default: return CAT_SHITU; // 机遇/挫折多为任免与科举，归仕途
            }
        }

        private static bool ContainsAny(string text, params string[] keys)
        {
            foreach (var k in keys) { if (text.Contains(k)) return true; }
            return false;
        }

        public void MigrateFromCustomData() { }

        public void ResetForNewWorld()
        {
            _records.Clear();
            _seenAlive.Clear();
            _initialScanDone = false;
            _lastScanYear = -1;
        }

        public void ResetRuntime()
        {
            _records.Clear();
            _seenAlive.Clear();
            _initialScanDone = false;
            _lastScanYear = -1;
        }

        /// <summary>测试辅助：清空全部（Tests 用）。</summary>
        public void ClearForTest()
        {
            _records.Clear();
            _seenAlive.Clear();
            _initialScanDone = false;
            _lastScanYear = -1;
        }

        /// <summary>测试辅助：记录条数。</summary>
        public int RecordCount => _records.Count;
    }
}
