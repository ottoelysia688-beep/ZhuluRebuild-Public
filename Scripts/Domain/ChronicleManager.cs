using System;
using System.Collections.Generic;
using System.Linq;
using ZhuluRebuild.Core;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Domain
{
    public enum ChronicleEventType
    {
        DynastyFounding,    // 开国
        KingChange,         // 换君/继位
        OfficeAppointed,    // 重大任免
        PolicyEnacted,      // 颁行政令
        PolicyRevoked,      // 废止政令
        WarDeclared,        // 爆发战争
        MandateCollapsed,   // 天命崩绝
        MandateGranted,     // 受天命/正统转移
        KingdomFallen,      // 王国覆灭
        DynasticChange,     // 改朝换代（阶段2A A1-7）
        TierChange,         // 爵位升降（阶段2B 包1）
        // 阶段2J 邦交系（追加值——存档兼容）
        PeaceTreaty,        // 和约停战
        NonAggression,      // 互不侵犯
        VassalChange,       // 附庸变动（收编/独立）
        // 2026-08-19 串联重构（追加值——存档兼容）
        CourtDebate,        // 朝议（党争辩论定国是）
        // P2-30 串联④（追加值——存档兼容）
        ClanMilestone,      // 家族里程碑（书香门第等——P2-30 2026-08-23）
        ImperialExam        // 殿试策论（P2-30 2026-08-23）
    }

    public class ChronicleEntry
    {
        public int Year { get; set; }
        public ChronicleEventType Type { get; set; }
        public string KingdomId { get; set; }
        public string KingdomName { get; set; }
        public string PrimaryPersonName { get; set; }
        public string ContentText { get; set; }
        public bool IsWorldLevelMajor { get; set; }
        /// <summary>渲染辅助：朝议归并区间终止年（>Year 时显示"建元X-Y年"）。仅显示层构造，不入持久化。</summary>
        public int YearEnd { get; set; }
    }

    /// <summary>
    /// 史册纪事。每国独立持久化到 custom_data（key: zlr_chronicle），随存档保存；
    /// 世界史册 = 各王国 major 事件的聚合视图。
    /// 2026-08-20 持久化增强（外部归档思路借鉴，自研实现）：
    /// ① 单国上限 150 → 600（快照文件为权威源，容量无虞）；
    /// ② 灭国史册归档——王国覆灭后史册不再丢失（_archive 保留最后已知版本，世界史册仍可查）。
    /// </summary>
    public class ChronicleManager : ISnapshotSection<ChronicleManager.ChronicleState>
    {
        public static ChronicleManager Instance { get; } = new ChronicleManager();

        private const string KEY_CHRONICLE = "chronicle";
        private const int MaxEntriesPerKingdom = 600;

        /// <summary>史册归档：王国ID → 最后已知史册串（灭国后仍保留，世界史册可查亡国旧事）。</summary>
        private readonly Dictionary<string, string> _archive = new Dictionary<string, string>();
        // 2026-08-20 阶段C：数据治理（历史裁剪）
        private const int SoftMaxEntriesPerKingdom = 600;      // 软上限——超出只删小事件，不删主干
        private const int MaintenanceIntervalYears = 25;        // 跨 25 年桶才裁剪该国
        private readonly Dictionary<string, int> _lastMaintenanceBucketByKingdom = new Dictionary<string, int>();

        private ChronicleManager() { }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("|", "¦").Replace("\n", " ").Replace("\r", " ");
        }

        public void AddEntry(int year, ChronicleEventType type, string kingdomId, string kingdomName, string personName, string text, bool isMajor = false)
        {
            if (string.IsNullOrEmpty(kingdomId)) return;

            Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
            if (k == null) return;

            string newLine = string.Join("|", new[]
            {
                year.ToString(),
                ((int)type).ToString(),
                Sanitize(kingdomName),
                Sanitize(personName),
                Sanitize(text),
                isMajor ? "1" : "0"
            });

            string raw = KingdomDataExt.GetStr(k, KEY_CHRONICLE);

            // 去重：同年同类型同文本只记一次
            if (!string.IsNullOrEmpty(raw))
            {
                var lines = raw.Split('\n');
                foreach (var line in lines)
                {
                    if (line == newLine) return;
                }
            }

            var allLines = new List<string>();
            if (!string.IsNullOrEmpty(raw))
            {
                allLines.AddRange(raw.Split('\n'));
            }
            allLines.Add(newLine);

            // 2026-08-20 阶段C：去掉无差别删最旧（会删开国/天命/君主主干）——改为 25 年维护桶触发智能裁剪
            int bucket = year / MaintenanceIntervalYears;
            if (!_lastMaintenanceBucketByKingdom.TryGetValue(kingdomId, out int lastB) || bucket > lastB)
            {
                _lastMaintenanceBucketByKingdom[kingdomId] = bucket;
                string comp = CompactRawChronicle(kingdomId, string.Join("\n", allLines), year, out int removedC);
                if (removedC > 0)
                {
                    // 2026-10-23 [B4]：历史容量维护是常规行为，不是异常——降为 Info（debug_logging 关闭时不输出）。
                    // 旧实现打 WARN + 10s 限频，但限频键按国家拆分，实测单局仍刷 40 条，淹没真告警。
                    ZhuluContext.Instance.LogInfo($"[Chronicle] 历史维护：{kingdomName} 删除小事件 {removedC} 条");
                    allLines = comp.Length > 0 ? comp.Split('\n').ToList() : new List<string>();
                }
            }

            KingdomDataExt.SetStr(k, KEY_CHRONICLE, string.Join("\n", allLines));
            // 2026-08-20：同步更新归档（存档时王国已死也能保留最后版本）
            _archive[kingdomId] = string.Join("\n", allLines);
        }

        private List<ChronicleEntry> ParseKingdomEntries(string kingdomId, string raw)
        {
            var result = new List<ChronicleEntry>();
            if (string.IsNullOrEmpty(raw)) return result;

            foreach (var line in raw.Split('\n'))
            {
                if (string.IsNullOrEmpty(line)) continue;
                var parts = line.Split('|');
                if (parts.Length < 6) continue;

                var entry = new ChronicleEntry
                {
                    KingdomId = kingdomId,
                    Year = int.TryParse(parts[0], out int y) ? y : 1,
                    Type = int.TryParse(parts[1], out int t) && Enum.IsDefined(typeof(ChronicleEventType), t)
                        ? (ChronicleEventType)t : ChronicleEventType.OfficeAppointed,
                    KingdomName = parts[2],
                    PrimaryPersonName = parts[3],
                    ContentText = parts[4],
                    IsWorldLevelMajor = parts[5] == "1"
                };
                result.Add(entry);
            }
            return result;
        }


        // ═══ 2026-08-20 阶段C：历史数据治理（裁剪小事件，保主干）═══
        private static bool IsPermanentType(ChronicleEventType t) =>
            t == ChronicleEventType.DynastyFounding || t == ChronicleEventType.KingChange ||
            t == ChronicleEventType.DynasticChange || t == ChronicleEventType.MandateGranted ||
            t == ChronicleEventType.MandateCollapsed || t == ChronicleEventType.KingdomFallen;
        private static bool IsImportantType(ChronicleEventType t) =>
            t == ChronicleEventType.WarDeclared || t == ChronicleEventType.PeaceTreaty ||
            t == ChronicleEventType.VassalChange || t == ChronicleEventType.PolicyEnacted ||
            t == ChronicleEventType.PolicyRevoked || t == ChronicleEventType.NonAggression ||
            t == ChronicleEventType.TierChange;
        private static bool IsMinorType(ChronicleEventType t) =>
            t == ChronicleEventType.OfficeAppointed || t == ChronicleEventType.CourtDebate;
        private static bool IsPermanentEntry(ChronicleEntry e) => e == null ? false :
            e.IsWorldLevelMajor || IsPermanentType(e.Type);
        private static bool IsImportantEntry(ChronicleEntry e) => e == null ? false : IsImportantType(e.Type);

        /// <summary>裁剪小事件（确定性、无随机）：永久/重要全留；小事件按年龄分桶抽样；超软上限删最旧小事件。</summary>
        public string CompactRawChronicle(string kingdomId, string raw, int currentYear, out int removedCount)
        {
            removedCount = 0;
            if (string.IsNullOrEmpty(raw)) return raw ?? "";
            var entries = ParseKingdomEntries(kingdomId, raw);
            if (entries.Count == 0) return raw;
            var minor = new Dictionary<string, ChronicleEntry>(); // 键=类型@年代桶 → 留较晚
            var kept = new List<ChronicleEntry>();
            foreach (var e in entries)
            {
                if (IsPermanentEntry(e) || IsImportantEntry(e)) { kept.Add(e); continue; }
                // 只裁剪 OfficeAppointed / CourtDebate（第一版小事件）
                if (!IsMinorType(e.Type)) { kept.Add(e); continue; }
                int age = currentYear - e.Year;
                if (age <= 100) { kept.Add(e); continue; }
                int bucketSize = age <= 250 ? 5 : 20;
                string key = ((int)e.Type) + "@" + (e.Year / bucketSize);
                if (!minor.TryGetValue(key, out var cur) || e.Year > cur.Year) minor[key] = e;
            }
            foreach (var kv in minor) kept.Add(kv.Value);
            removedCount = entries.Count - kept.Count;
            // 软上限：仅剩超过 600 时从最旧小事件删；无小事件可删则允许临时超限（限频提示）
            if (kept.Count > SoftMaxEntriesPerKingdom)
            {
                var oldOnes = kept.Where(e => !IsPermanentEntry(e) && !IsImportantEntry(e))
                    .OrderBy(e => e.Year).ThenBy(e => e.Year).ToList();
                int i = 0;
                while (kept.Count > SoftMaxEntriesPerKingdom && i < oldOnes.Count)
                {
                    if (kept.Remove(oldOnes[i])) removedCount++;
                    i++;
                }
                if (kept.Count > SoftMaxEntriesPerKingdom)
                    ZhuluContext.Instance.LogRateLimited("chronicle-soft-" + kingdomId,
                        $"[Chronicle] {kingdomId} 超软上限 {SoftMaxEntriesPerKingdom} 仍保留（主干无小事件可删）", 60f);
            }
            kept.Sort((a, b) => a.Year != b.Year ? a.Year.CompareTo(b.Year) : string.CompareOrdinal(a.KingdomId ?? "", b.KingdomId ?? ""));
            return string.Join("\n", kept.Select(SerializeEntry));
        }

        private static string SerializeEntry(ChronicleEntry e) => string.Join("|", new[]
        {
            e.Year.ToString(), ((int)e.Type).ToString(),
            Sanitize(e.KingdomName), Sanitize(e.PrimaryPersonName), Sanitize(e.ContentText),
            e.IsWorldLevelMajor ? "1" : "0"
        });

        public List<ChronicleEntry> GetKingdomChronicle(string kingdomId)
        {
            Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
            if (k == null || !k.isAlive())
            {
                // 2026-08-20：亡国 → 查归档（灭国史册不丢）
                return _archive.TryGetValue(kingdomId, out var archived)
                    ? ParseKingdomEntries(kingdomId, archived) : new List<ChronicleEntry>();
            }
            return ParseKingdomEntries(kingdomId, KingdomDataExt.GetStr(k, KEY_CHRONICLE));
        }

        /// <summary>世界史册 = 全图各国 major 事件聚合（含亡国归档），按年份排序。</summary>
        public List<ChronicleEntry> GetWorldMajorChronicle()
        {
            var result = new List<ChronicleEntry>();

            // 现存国：读 custom_data 实时值，并同步刷新归档
            var aliveIds = new HashSet<string>();
            if (World.world?.kingdoms?.list != null)
            {
                foreach (Kingdom k in World.world.kingdoms.list)
                {
                    if (k == null) continue;
                    string kId = k.id.ToString();
                    aliveIds.Add(kId);
                    string raw = KingdomDataExt.GetStr(k, KEY_CHRONICLE);
                    if (!string.IsNullOrEmpty(raw)) _archive[kId] = raw;
                    var entries = ParseKingdomEntries(kId, raw);
                    foreach (var e in entries)
                    {
                        if (e.IsWorldLevelMajor) result.Add(e);
                    }
                }
            }

            // 亡国：读归档（世界史册保留亡国旧事）
            foreach (var kv in _archive)
            {
                if (aliveIds.Contains(kv.Key)) continue;
                var entries = ParseKingdomEntries(kv.Key, kv.Value);
                foreach (var e in entries)
                {
                    if (e.IsWorldLevelMajor) result.Add(e);
                }
            }

            result.Sort((a, b) => a.Year != b.Year ? a.Year.CompareTo(b.Year) : 0);
            return result;
        }
    
        // ═══════════ v2 快照存档接口 ═══════════
        private void LegacyCollectToSnapshot(Persistence.ZhuluSnapshot snap)
        {
            // 2026-08-20：先刷现存国归档，再整包落盘（含亡国——灭国史册随存档走）
            int curYear = ZhuluTime.CurrentYear;
            int totalRemoved = 0, totalKingdoms = 0;
            if (World.world?.kingdoms?.list != null)
            {
                foreach (Kingdom k in World.world.kingdoms.list)
                {
                    if (k == null || !k.isAlive()) continue;
                    string v = KingdomDataExt.GetStr(k, KEY_CHRONICLE, "");
                    string comp = CompactRawChronicle(k.id.ToString(), v, curYear, out int rm);
                    totalRemoved += rm; totalKingdoms++;
                    KingdomDataExt.SetStr(k, KEY_CHRONICLE, comp);
                    if (!string.IsNullOrEmpty(comp)) _archive[k.id.ToString()] = comp;
                    else _archive.Remove(k.id.ToString());
                }
            }
            // 亡国归档同样治理（防止长期不再新增事件的亡国史无限膨胀）
            foreach (var kId in new List<string>(_archive.Keys))
            {
                string comp2 = CompactRawChronicle(kId, _archive[kId], curYear, out int rm2);
                totalRemoved += rm2;
                if (!string.IsNullOrEmpty(comp2)) _archive[kId] = comp2; else _archive.Remove(kId);
            }
            if (totalRemoved > 0)
                ZhuluContext.Instance.LogInfo($"[Chronicle] 保存前维护：处理 {totalKingdoms} 国，删除小事件 {totalRemoved} 条");
            snap.Chronicle.Clear();
            foreach (var kv in _archive) snap.Chronicle[kv.Key] = kv.Value;
        }
        private void LegacyRestoreFromSnapshot(Persistence.ZhuluSnapshot snap)
        {
            _archive.Clear();
            _lastMaintenanceBucketByKingdom.Clear();
            foreach (var kv in snap.Chronicle)
            {
                _archive[kv.Key] = kv.Value; // 归档全量恢复（含亡国）
                Kingdom k = KingdomDataExt.FindKingdomById(kv.Key);
                if (k == null || !k.isAlive()) continue;
                KingdomDataExt.SetStr(k, KEY_CHRONICLE, kv.Value);
            }
        }

        // ---- batch10-A1: schema v5 section ----
        public sealed class ChronicleState { public Dictionary<string, string> Archive = new Dictionary<string, string>(); }
        public string SectionName => "chronicle";
        public Dictionary<string, ChronicleState> CaptureStates()
        {
            var v4 = new ZhuluSnapshot();
            LegacyCollectToSnapshot(v4);   // delegate: pre-save compaction side effect preserved
            return new Dictionary<string, ChronicleState>
            {
                ["__global__"] = new ChronicleState { Archive = v4.Chronicle ?? new Dictionary<string, string>() }
            };
        }
        public void ApplyStates(Dictionary<string, ChronicleState> data)
        {
            if (data == null || !data.TryGetValue("__global__", out var st) || st == null) return;
            var v4 = new ZhuluSnapshot { Chronicle = st.Archive ?? new Dictionary<string, string>() };
            LegacyRestoreFromSnapshot(v4);
        }

        public void MigrateFromCustomData()
        {
            // 2026-08-20：迁移前先清空归档——防同一进程切换世界（A 亡国史被 B 追加）
            _archive.Clear();
            _lastMaintenanceBucketByKingdom.Clear();
            // 旧档迁移：现存国 custom_data → 归档（首次保存即落盘）
            if (World.world?.kingdoms?.list == null) return;
            foreach (Kingdom k in World.world.kingdoms.list)
            {
                if (k == null || !k.isAlive()) continue;
                string v = KingdomDataExt.GetStr(k, KEY_CHRONICLE, "");
                if (!string.IsNullOrEmpty(v)) _archive[k.id.ToString()] = v;
            }
        }
        public void ResetRuntime() { _archive.Clear(); _lastMaintenanceBucketByKingdom.Clear(); }

}
}
