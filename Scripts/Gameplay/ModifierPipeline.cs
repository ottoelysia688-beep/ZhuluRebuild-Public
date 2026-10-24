using System;
using System.Collections.Generic;
using ZhuluRebuild.Core;

namespace ZhuluRebuild.Gameplay
{
    public class ModifierPlan
    {
        public string SourceId { get; set; }        // 如: "policy_gov", "office_pm"
        public string TargetKingdomId { get; set; } // 目标王国 ID
        public string Channel { get; set; }         // 一律使用 ModifierChannels 常量
        public float Additive { get; set; }         // 加算修正（点数；乘算通道填 0）
        public float Multiplicative { get; set; }   // 乘算修正（如 1.15f；加算通道填 1）
        public int Priority { get; set; } = 100;
        public bool IsReversible { get; set; } = true;
        public string Reason { get; set; }
    }

    /// <summary>
    /// 数值修正唯一出口。求值语义：result = (base + ΣAdditive) × ΠMultiplicative。
    /// 因此：乘算类通道（税收/军备/粮食/耐久）必须写入 Multiplicative，
    /// 加算类通道（忠诚/外交）写入 Additive —— 严禁把目标倍率塞进 Additive。
    /// </summary>
    public class ModifierPipeline
    {
        public static ModifierPipeline Instance { get; } = new ModifierPipeline();

        private readonly List<ModifierPlan> _activePlans = new List<ModifierPlan>();

        /// <summary>
        /// 安全软回滚挂起标记：true 时拒绝一切新注册且求值恒返回基准值。
        /// 由主循环检测到“新世界载入”（年份回退）后自动 Resume 并重建管道。
        /// </summary>
        public bool Suspended { get; private set; } = false;

        private ModifierPipeline() { }

        // ── 2026-10-23 [F6] 求值缓存（写时复制 / copy-on-write）──
        // 动机：EvaluateChannelMultiplier 原为 O(激活计划数) 线性扫描，而 Actor.updateStats 后置
        // 每次调用要算 4 个通道（攻/血/速/防），RefreshKingdom 又会全量重算全军。
        // 规模估算：3000 单位 × 500 计划 × 4 通道 ≈ 600 万次字符串比较。
        //
        // ⚠️ 线程安全（2026-10-23 实机回归）：Kingdom.getTaxRateLocal 会被非主线程调用，
        // 两个线程同时看到 _aggDirty=true → 同时进 RebuildAgg → 同时写同一个 Dictionary →
        // 抛 "A concurrent update was performed on this collection"（Player.log 实测）。
        // 因此改为：构建期间用私有字典 + 锁，构建完整体替换只读快照引用；读者只读快照，无锁。
        // 铁律：任何写 _activePlans 的入口都必须在 _aggGate 内完成并置脏。
        private struct Agg { public float Add; public float Mult; }

        private sealed class AggSnapshot
        {
            public readonly Dictionary<string, Agg> Global;
            public readonly Dictionary<string, Agg> Kingdom;
            public AggSnapshot(Dictionary<string, Agg> g, Dictionary<string, Agg> k) { Global = g; Kingdom = k; }
        }

        private readonly object _aggGate = new object();
        private AggSnapshot _aggSnapshot;              // 引用赋值原子；读者无需加锁
        private volatile bool _aggDirty = true;

        /// <summary>置脏。铁律：必须在 _aggGate 内调用（与 _activePlans 的写入同临界区）。</summary>
        private void InvalidateAgg() { _aggDirty = true; }

        /// <summary>构建并发布快照。并发调用时只有第一个真正构建，其余直接复用（双检）。</summary>
        private void RebuildAgg()
        {
            lock (_aggGate)
            {
                if (!_aggDirty && _aggSnapshot != null) return;

                var global = new Dictionary<string, Agg>();
                var kingdom = new Dictionary<string, Agg>();

                // ToArray 在锁内取，避免与 RegisterPlan/RemovePlan 的并发改动冲突
                var plans = _activePlans.ToArray();
                foreach (var p in plans)
                {
                    if (p == null) continue;
                    float add = p.Additive;
                    float mult = p.Multiplicative > 0 ? p.Multiplicative : 1.0f;
                    if (string.IsNullOrEmpty(p.TargetKingdomId))
                    {
                        // 注意：default(Agg).Mult == 0，首次插入必须初始化为乘法单位元 1，否则整条通道恒为 0
                        if (!global.TryGetValue(p.Channel, out var g)) { g = new Agg { Add = 0f, Mult = 1f }; }
                        g.Add += add;
                        g.Mult *= mult;
                        global[p.Channel] = g;
                    }
                    else
                    {
                        string k = p.Channel + "|" + p.TargetKingdomId;
                        if (!kingdom.TryGetValue(k, out var g)) { g = new Agg { Add = 0f, Mult = 1f }; }
                        g.Add += add;
                        g.Mult *= mult;
                        kingdom[k] = g;
                    }
                }

                _aggSnapshot = new AggSnapshot(global, kingdom);   // 一次性发布
                _aggDirty = false;
            }
        }

        public void RegisterPlan(ModifierPlan plan)
        {
            if (Suspended) return;
            if (plan == null || string.IsNullOrEmpty(plan.SourceId)) return;

            lock (_aggGate)
            {
                // 按 SourceId + Target 去重，保证重复注册幂等
                _activePlans.RemoveAll(p => p.SourceId == plan.SourceId && p.TargetKingdomId == plan.TargetKingdomId);
                _activePlans.Add(plan);
                InvalidateAgg();
            }

            // 性能优化：移除逐条注册日志（原版每次 Rehydrate 产生 300+ 条，导致卡顿）
        }

        private int _lastPruneYear = -1;

        /// <summary>阶段3H-19 帧数优化：年度修剪死国/亡城残留计划——
        /// 灭国不触发 RemovePlan，计划按世纪累积拖慢每次 Evaluate 遍历（幂等，每年最多跑一次）。</summary>
        public void PruneDeadPlans(int year)
        {
            if (year == _lastPruneYear || Suspended) return;
            _lastPruneYear = year;
            try
            {
                if (_activePlans.Count == 0 || World.world?.kingdoms?.list == null) return;
                var alive = new HashSet<string>();
                foreach (Kingdom k in World.world.kingdoms.list)
                {
                    try { if (k != null && k.isAlive()) alive.Add(k.id.ToString()); } catch { }
                }
                int removed;
                int remain;
                lock (_aggGate)
                {
                    removed = _activePlans.RemoveAll(p =>
                        !string.IsNullOrEmpty(p.TargetKingdomId) && !alive.Contains(p.TargetKingdomId));
                    if (removed > 0) InvalidateAgg();
                    remain = _activePlans.Count;
                }
                if (removed > 0)
                    ZhuluContext.Instance.LogInfo($"[ModifierPipeline] 年度修剪死国残留计划 {removed} 条（存量 {remain}）");
            }
            catch { }
        }

        public void RemovePlan(string sourceId, string kingdomId = null)
        {
            int removed;
            lock (_aggGate)
            {
                removed = _activePlans.RemoveAll(p => p.SourceId == sourceId && (kingdomId == null || p.TargetKingdomId == kingdomId));
                if (removed > 0) InvalidateAgg();
            }
            if (removed > 0)
                ZhuluContext.Instance.LogInfo($"[ModifierPipeline] 软回滚数值修正: Source={sourceId}, 移除数量={removed}");
        }

        public float EvaluateChannelMultiplier(string channel, string kingdomId, float baseValue)
        {
            if (Suspended || _activePlans.Count == 0) return baseValue;

            // 开关关闭 → 逐字保留修复前的线性路径（随时可回调，见 docs/修复方案-20261023.md F6）
            if (!ZhuluConfig.Instance.ModifierCacheEnabled)
            {
                // 快照后遍历：原实现直接 foreach _activePlans，跨线程注册时会抛"集合已修改"
                ModifierPlan[] plans;
                lock (_aggGate) { plans = _activePlans.ToArray(); }
                float ta = 0f, tm = 1.0f;
                foreach (var plan in plans)
                {
                    if (plan.Channel == channel && (string.IsNullOrEmpty(plan.TargetKingdomId) || plan.TargetKingdomId == kingdomId))
                    {
                        ta += plan.Additive;
                        tm *= plan.Multiplicative > 0 ? plan.Multiplicative : 1.0f;
                    }
                }
                return (baseValue + ta) * tm;
            }

            if (_aggDirty) RebuildAgg();
            var snap = _aggSnapshot;          // 读引用是原子操作，快照发布后只读
            if (snap == null) return baseValue;

            float add = 0f, mult = 1.0f;
            if (snap.Global.TryGetValue(channel, out var gg)) { add += gg.Add; mult *= gg.Mult; }
            if (!string.IsNullOrEmpty(kingdomId) && snap.Kingdom.TryGetValue(channel + "|" + kingdomId, out var gk))
            {
                add += gk.Add;
                mult *= gk.Mult;
            }
            return (baseValue + add) * mult;
        }

        /// <summary>一键安全软回滚：清空全部修正并挂起管道（新世界载入后自动恢复）。</summary>
        public void ClearAll()
        {
            lock (_aggGate)
            {
                _activePlans.Clear();
                InvalidateAgg();
            }
            Suspended = true;
            ZhuluContext.Instance.LogInfo("[ModifierPipeline] 清空所有激活的修正方案并挂起管道 (安全软回滚)");
        }

        /// <summary>解除挂起（检测到新世界载入时由主循环调用）。</summary>
        public void Resume()
        {
            if (!Suspended) return;
            Suspended = false;
            ZhuluContext.Instance.LogInfo("[ModifierPipeline] 检测到新世界，管道恢复接收修正");
        }
    }
}
