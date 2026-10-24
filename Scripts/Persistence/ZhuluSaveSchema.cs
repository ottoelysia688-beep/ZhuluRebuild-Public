using System;
using System.Collections.Generic;

namespace ZhuluRebuild.Persistence
{
    // ════════════════════════════════════════════════════════════════
    // 快照 Schema v5 引擎（批次10-A1，2026-08-23）。
    // 设计稿：docs/快照SchemaV5设计-20260823.md
    //
    // 三条原则：
    //   1. 状态即类型——每系统声明强类型 State POCO（属性名即 JSON 键），竖线位置协议物理消失；
    //   2. 分区即注册——系统实现 ISnapshotSection<TState> 并 Register 一次，
    //      SaveCore 的 Collect/Restore 变为遍历注册表的固定循环（新增系统零改 SaveCore）；
    //   3. 缺失即跳过——恢复时段不存在=不动内存（与 v4"缺段不覆盖"语义一致），无静默默认填充。
    //
    // 载荷约定：所有分区载荷统一为 Dictionary<string, TState>。
    //   · 按王国分区 → 键=王国ID；全局单例 → 键="__global__"；
    //   · TState 允许是 string（历史卷轴原文协议原样保留——B5 Orthodox 拆分时再处理）。
    //
    // 反序列化路径：Newtonsoft 把 Sections 值反序列化为 JToken；引擎按注册表里登记的
    // StateType 用 JToken.ToObject 显式转回 Dictionary<string,TState>（不依赖 $type 内嵌类型名）。
    // ════════════════════════════════════════════════════════════════

    /// <summary>快照分区接口（非泛型侧——注册表遍历用）。</summary>
    public interface ISnapshotSection
    {
        /// <summary>分区名（快照 JSON Sections 的键；小写下划线，如 "treasury"）。</summary>
        string SectionName { get; }
    }

    /// <summary>强类型快照分区。系统实现本接口后向 ZhuluSaveSchema.Register 注册一次。</summary>
    public interface ISnapshotSection<TState> : ISnapshotSection where TState : class
    {
        /// <summary>内存 → 状态记录（保存时调用；实现须防御判空——World 未就绪时返回空字典）。</summary>
        Dictionary<string, TState> CaptureStates();

        /// <summary>状态记录 → 内存（读档时调用；字典中不存在的键=不动内存，与"缺段不覆盖"语义一致）。</summary>
        void ApplyStates(Dictionary<string, TState> data);
    }

    /// <summary>注册表条目（反射烘焙后的闭包——运行期零反射）。</summary>
    public sealed class SnapEntry
    {
        public string Name;
        public Type StateType;
        public Func<Dictionary<string, object>> CaptureBoxed;
        public Action<object> ApplyBoxed;
    }

    public static class ZhuluSaveSchema
    {
        private static readonly List<SnapEntry> _entries = new List<SnapEntry>();
        private static readonly Dictionary<string, SnapEntry> _byName = new Dictionary<string, SnapEntry>(StringComparer.Ordinal);

        /// <summary>已注册分区数（CompatibilityReport 启动核对用）。</summary>
        public static int RegisteredCount { get { return _entries.Count; } }

        /// <summary>已注册分区名快照（诊断输出用）。</summary>
        public static IReadOnlyList<string> RegisteredNames()
        {
            var names = new List<string>(_entries.Count);
            foreach (var e in _entries) names.Add(e.Name);
            return names;
        }

        /// <summary>注册分区（幂等——同名重复注册以最后一次为准并告警一次）。</summary>
        public static void Register<TState>(ISnapshotSection<TState> owner) where TState : class
        {
            if (owner == null) throw new ArgumentNullException("owner");
            string name = owner.SectionName;
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("[SaveSchema] SectionName 不能为空");
            if (_byName.ContainsKey(name))
            {
                try { Core.ZhuluContext.Instance.LogWarning("[SaveSchema] 分区重名覆盖: " + name); } catch { }
                _entries.RemoveAll(e => e.Name == name);
            }
            var entry = new SnapEntry
            {
                Name = name,
                StateType = typeof(TState),
                CaptureBoxed = () =>
                {
                    var d = owner.CaptureStates();
                    var boxed = new Dictionary<string, object>();
                    if (d != null) foreach (var kv in d) boxed[kv.Key] = kv.Value;
                    return boxed;
                },
                ApplyBoxed = raw => owner.ApplyStates((Dictionary<string, TState>)raw),
            };
            _entries.Add(entry);
            _byName[name] = entry;
        }

        /// <summary>收集全部注册分区到 v5 快照（任一分区异常即上抛——严格存档阻断依赖此语义）。</summary>
        public static void CollectAll(ZhuluSnapshotV5 snap)
        {
            if (snap == null) throw new ArgumentNullException("snap");
            foreach (var e in _entries)
            {
                try
                {
                    snap.Sections[e.Name] = e.CaptureBoxed();
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"[SaveSchema] 分区 {e.Name} 收集失败: {ex.Message}", ex);
                }
            }
        }

        /// <summary>从 v5 快照恢复全部注册分区（段缺失=跳过；JToken→强类型按注册表显式转换）。</summary>
        public static void RestoreAll(ZhuluSnapshotV5 snap)
        {
            if (snap == null || snap.Sections == null) return;
            foreach (var e in _entries)
            {
                if (!snap.Sections.TryGetValue(e.Name, out object raw) || raw == null) continue;   // 缺失即跳过
                try
                {
                    // Sections 值经 Newtonsoft 反序列化后是 JToken——按注册表登记的 StateType 显式转回强类型字典
                    var token = Newtonsoft.Json.Linq.JToken.FromObject(raw);
                    var dictType = typeof(Dictionary<,>).MakeGenericType(typeof(string), e.StateType);
                    var typed = token.ToObject(dictType);
                    e.ApplyBoxed(typed);
                }
                catch (Exception ex)
                {
                    // 单分区损坏不连坐其他分区；必须留痕（读档后数据不完整可排查）
                    try { Core.ZhuluContext.Instance.LogError($"[SaveSchema] 分区 {e.Name} 恢复失败(已跳过): {ex.Message}"); } catch { }
                }
            }
        }

        /// <summary>测试辅助：清空注册表（公开仅因 Tests 为独立工程；生产代码勿调）。</summary>
        public static void ClearForTest()
        {
            _entries.Clear();
            _byName.Clear();
        }
    }
}
