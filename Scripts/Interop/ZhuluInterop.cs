using System.Collections.Generic;
using System.Reflection;

namespace ZhuluRebuild.Interop
{
    /// <summary>
    /// 外部联动公共门面（生态契约层）。
    ///
    /// 背景：社区 mod「天下名人传记」的 ZhuluLink 通过反射读取本 mod 内部成员消费数据
    /// （帝王庙号谥号 / 个人史册五分类 / 王国爵位）。该反射路径脆弱——内部字段改名或类型
    /// 重构即静默断链（联动方 catch-all 回落，无报错、无感知）。本门面把消费点收敛为
    /// 稳定只读 API；同时不改变任何内部结构，旧反射路径继续可用（零破坏增量）。
    ///
    /// 契约纪律（重构红线）：
    /// 1. 本类公开成员一经发布只能新增，不得改名/移除/改语义；
    /// 2. Domain 层重构涉及下列契约点时，必须同步维护 Tests/InteropContractTests.cs
    ///    （该测试以「天下名人传记 ZhuluLink」的实际反射方式逐项校验，红 = 下游 mod 静默失效）：
    ///    - PersonHistoryManager.Instance / .GetRecord(long) / ._records（private 字段）
    ///    - PersonHistoryManager+PersonRecord.Entries / +HistoryEntry(Year/Category/Text)
    ///    - OrthodoxMandateManager.Instance / ._history（private List&lt;DynastyRecord&gt;）
    ///    - DynastyRecord.Emperors / EmperorRecord(Name/Era/PosthumousName/TempleName)
    ///    - ZhuluTierService.GetKingdomTierId(Kingdom) / GetTierName(string)
    /// </summary>
    public static class ZhuluInterop
    {
        /// <summary>门面 API 版本。新增成员时递增；不允许语义破坏性变更（如需另起新门面类）。</summary>
        public const int ApiVersion = 1;

        // _history 为 private（类级别隔离，C# 无程序集内直访）——门面以反射读取，
        // 与下游 ZhuluLink 完全同构；字段存在性由 InteropContractTests 守护，此处缓存 FieldInfo。
        private static FieldInfo _fHistory;

        private static List<Domain.DynastyRecord> TryGetHistory()
        {
            try
            {
                if (_fHistory == null)
                    _fHistory = typeof(Domain.OrthodoxMandateManager).GetField("_history",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                return _fHistory?.GetValue(Domain.OrthodoxMandateManager.Instance)
                    as List<Domain.DynastyRecord>;
            }
            catch { return null; }
        }

        // ═══════════════ 帝王 / 朝代（消费场景：外部传记盖棺谥号显示） ═══════════════

        /// <summary>帝王记录只读快照。</summary>
        public sealed class EmperorInfo
        {
            public string Dynasty;           // 朝代名（不含「朝」后缀）
            public int ReignIndex;           // 期号（同国多次受命：一期/二期…）
            public string KingdomId;
            public int DynastyStartYear;
            public int DynastyEndYear;       // -1 = 朝代延续中
            public string DynastyEndReason;  // active/亡国失命/衰微失命/篡位失国/vacant
            public string Name;              // 帝王显示名
            public string Era;               // 年号
            public string TempleName;        // 庙号（空 = 未盖棺）
            public string PosthumousName;    // 谥号（空 = 未盖棺）
            public int ReignStartYear;
            public int ReignEndYear;         // -1 = 在位中
        }

        /// <summary>
        /// 全量帝王记录快照（含在位中者；已盖棺者带庙号+谥号）。
        /// 永不返回 null；异常返回空列表。门面不抛异常。
        /// </summary>
        public static List<EmperorInfo> GetEmperorRecords()
        {
            var result = new List<EmperorInfo>();
            try
            {
                var history = TryGetHistory();
                if (history == null) return result;
                foreach (var dyn in history)
                {
                    if (dyn?.Emperors == null) continue;
                    foreach (var e in dyn.Emperors)
                    {
                        if (e == null) continue;
                        result.Add(new EmperorInfo
                        {
                            Dynasty = dyn.Dynasty,
                            ReignIndex = dyn.ReignIndex,
                            KingdomId = dyn.KingdomId,
                            DynastyStartYear = dyn.StartYear,
                            DynastyEndYear = dyn.EndYear,
                            DynastyEndReason = dyn.EndReason,
                            Name = e.Name,
                            Era = e.Era,
                            TempleName = e.TempleName,
                            PosthumousName = e.PosthumousName,
                            ReignStartYear = e.StartYear,
                            ReignEndYear = e.EndYear,
                        });
                    }
                }
            }
            catch { }
            return result;
        }

        /// <summary>
        /// 按帝王显示名精确匹配取「庙号+谥号+帝」尊号串（如「太祖武帝」）。
        /// 同名多处盖棺时取最新一次（倒序查找——旧反射路径取首个匹配，本 API 语义更正确，
        /// 消费方切换时请注意差异）。未盖棺（无谥号）/ 匹配不到 / 异常返回 null。
        /// </summary>
        public static string GetEmperorPosthumous(string actorDisplayName)
        {
            if (string.IsNullOrEmpty(actorDisplayName)) return null;
            try
            {
                var history = TryGetHistory();
                if (history == null) return null;
                for (int i = history.Count - 1; i >= 0; i--)
                {
                    var dyn = history[i];
                    if (dyn?.Emperors == null) continue;
                    for (int j = dyn.Emperors.Count - 1; j >= 0; j--)
                    {
                        var e = dyn.Emperors[j];
                        if (e == null || string.IsNullOrEmpty(e.PosthumousName)) continue;
                        if (e.Name != actorDisplayName) continue;
                        string temple = string.IsNullOrEmpty(e.TempleName) ? "" : e.TempleName;
                        return temple + e.PosthumousName + "帝";
                    }
                }
            }
            catch { }
            return null;
        }

        // ═══════════════ 个人史册（消费场景：外部传记五分类镜像） ═══════════════

        /// <summary>个人史册条目只读快照。</summary>
        public sealed class PersonEntryInfo
        {
            public int Year;
            public string Category;   // 仕途/功名/朝争/君临/恩遇…
            public string Text;
        }

        /// <summary>
        /// 取某人物的个人史册条目。categories 为空或 null 时返回全部条目（旧反射路径要求
        /// 至少一个分类，本 API 放宽——消费方按需过滤）。永不返回 null；异常返回空列表。
        /// </summary>
        public static List<PersonEntryInfo> GetPersonHistory(long actorId, params string[] categories)
        {
            var result = new List<PersonEntryInfo>();
            try
            {
                var rec = Domain.PersonHistoryManager.Instance.GetRecord(actorId);
                if (rec?.Entries == null) return result;
                foreach (var entry in rec.Entries)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.Text)) continue;
                    if (categories != null && categories.Length > 0)
                    {
                        bool hit = false;
                        foreach (var c in categories)
                            if (c == entry.Category) { hit = true; break; }
                        if (!hit) continue;
                    }
                    result.Add(new PersonEntryInfo
                    {
                        Year = entry.Year,
                        Category = entry.Category,
                        Text = entry.Text,
                    });
                }
            }
            catch { }
            return result;
        }

        // ═══════════════ 王国爵位（消费场景：外部传记封爵升降检测） ═══════════════

        /// <summary>取王国当前爵位 trait id（"zlr_tier_tianzi" 等）。非王国/异常返回 null。</summary>
        public static string GetKingdomTierId(global::Kingdom k)
        {
            if (k == null) return null;
            try { return Domain.ZhuluTierService.GetKingdomTierId(k); }
            catch { return null; }
        }

        /// <summary>取王国当前爵位中文名（天子/公/侯/伯/子/邑）。无爵位/异常返回空串。</summary>
        public static string GetKingdomTierName(global::Kingdom k)
        {
            try
            {
                string tierId = GetKingdomTierId(k);
                if (string.IsNullOrEmpty(tierId)) return "";
                return Domain.ZhuluTierService.GetTierName(tierId) ?? "";
            }
            catch { return ""; }
        }
    }
}
