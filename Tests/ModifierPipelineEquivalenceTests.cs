using System;
using System.Collections.Generic;
using ZhuluRebuild.Core;
using ZhuluRebuild.Gameplay;

namespace ZhuluRebuild.Tests
{
    /// <summary>
    /// 2026-10-23 [F6] 数值管道求值缓存「等价性守护」。
    ///
    /// 背景：EvaluateChannelMultiplier 从 O(N) 线性扫描改为按 (通道,国家) 预聚合 O(1) 查询。
    /// 缓存类优化的最大风险是**失效点漏挂** —— 某条路径改了 _activePlans 却没 InvalidateAgg()，
    /// 于是读到旧值，表现为"政策改了但数值不变"，且极难复现。
    ///
    /// 本测试用同一批随机计划分别走两条路径（开关 true/false），逐点比对结果：
    ///   · 任一处不一致 → 立刻失败，指明是哪个 通道/国家/基准值
    ///   · 覆盖：全局计划（TargetKingdomId 为空）+ 分国计划、4 通道 × 3 国 + 空国 × 3 基准值
    ///   · 额外覆盖失效链：注册 → 求值 → 移除 → 再求值（验证 RemovePlan 的 InvalidateAgg）
    /// </summary>
    public static class ModifierPipelineEquivalenceTests
    {
        private static readonly string[] Channels =
        {
            ModifierChannels.AttackPower,
            ModifierChannels.GoldIncomeMultiplier,
            ModifierChannels.CityLoyaltyBonus,
            ModifierChannels.DefensePower
        };

        private static readonly string[] Kingdoms = { "k1", "k2", "k3" };
        private static readonly float[] BaseValues = { 1f, 0f, 12.5f };

        public static void Run()
        {
            var pipe = ModifierPipeline.Instance;
            var cfg = ZhuluConfig.Instance;

            bool origCache = cfg.ModifierCacheEnabled;
            bool origDebug = ZhuluContext.Instance.DebugLogging;
            // 测试进程内没有 Unity 运行时，Debug.Log 不可用 —— 关掉日志输出
            ZhuluContext.Instance.DebugLogging = false;
            try
            {
                pipe.ClearAll();
                pipe.Resume();

                var rnd = new Random(20261023);
                for (int i = 0; i < 200; i++)
                {
                    pipe.RegisterPlan(new ModifierPlan
                    {
                        SourceId = "src" + i,
                        TargetKingdomId = (i % 4 == 0) ? null : Kingdoms[rnd.Next(Kingdoms.Length)],
                        Channel = Channels[rnd.Next(Channels.Length)],
                        Additive = (float)(rnd.NextDouble() * 2 - 1),
                        Multiplicative = (float)(0.8 + rnd.NextDouble() * 0.5)
                    });
                }

                CompareBothPaths(pipe, cfg, "注册后");

                // 失效链：移除一半计划后必须仍然一致
                for (int i = 0; i < 100; i++) pipe.RemovePlan("src" + i);
                CompareBothPaths(pipe, cfg, "移除后");

                // 再注册一批（覆盖 RegisterPlan 的失效）
                for (int i = 1000; i < 1100; i++)
                {
                    pipe.RegisterPlan(new ModifierPlan
                    {
                        SourceId = "src" + i,
                        TargetKingdomId = (i % 3 == 0) ? null : Kingdoms[rnd.Next(Kingdoms.Length)],
                        Channel = Channels[rnd.Next(Channels.Length)],
                        Additive = (float)(rnd.NextDouble() * 2 - 1),
                        Multiplicative = (float)(0.8 + rnd.NextDouble() * 0.5)
                    });
                }
                CompareBothPaths(pipe, cfg, "再注册后");
            }
            finally
            {
                cfg.ModifierCacheEnabled = origCache;
                ZhuluContext.Instance.DebugLogging = origDebug;
                pipe.ClearAll();
                pipe.Resume();
            }
        }

        /// <summary>
        /// 2026-10-23 实机回归守护：多线程并发求值 + 并发注册/移除。
        ///
        /// 真实事故：Kingdom.getTaxRateLocal 会被非主线程调用，两个线程同时看到脏标记、
        /// 同时重建缓存、同时写同一个 Dictionary → 抛
        /// "A concurrent update was performed on this collection"（Player.log 实测，游戏内每帧刷屏）。
        /// 单线程等价性测试抓不到这类问题，必须用真并发压。
        ///
        /// 通过判据：全程零异常。修法为写时复制（构建期私有字典 + 锁，构建完整体替换只读快照引用）。
        /// </summary>
        public static void RunConcurrency()
        {
            var pipe = ModifierPipeline.Instance;
            bool origDebug = ZhuluContext.Instance.DebugLogging;
            ZhuluContext.Instance.DebugLogging = false;
            try
            {
                pipe.ClearAll();
                pipe.Resume();

                for (int i = 0; i < 50; i++)
                {
                    pipe.RegisterPlan(new ModifierPlan
                    {
                        SourceId = "seed" + i,
                        TargetKingdomId = (i % 2 == 0) ? null : "k1",
                        Channel = ModifierChannels.AttackPower,
                        Additive = 0.01f,
                        Multiplicative = 1.001f
                    });
                }

                var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
                _running = true;

                var readers = new System.Threading.Tasks.Task[4];
                for (int t = 0; t < readers.Length; t++)
                {
                    readers[t] = System.Threading.Tasks.Task.Run(() =>
                    {
                        while (_running)
                        {
                            try { pipe.EvaluateChannelMultiplier(ModifierChannels.AttackPower, "k1", 1f); }
                            catch (Exception ex) { errors.Add(ex); return; }
                        }
                    });
                }

                var writer = System.Threading.Tasks.Task.Run(() =>
                {
                    for (int i = 0; i < 3000; i++)
                    {
                        try
                        {
                            pipe.RegisterPlan(new ModifierPlan
                            {
                                SourceId = "w" + (i % 50),
                                TargetKingdomId = (i % 2 == 0) ? null : "k1",
                                Channel = ModifierChannels.AttackPower,
                                Additive = 0.01f,
                                Multiplicative = 1.001f
                            });
                            if (i % 3 == 0) pipe.RemovePlan("w" + ((i + 7) % 50));
                        }
                        catch (Exception ex) { errors.Add(ex); return; }
                    }
                });

                System.Threading.Tasks.Task.WaitAll(writer);
                _running = false;
                System.Threading.Tasks.Task.WaitAll(readers, 5000);

                TestRunner.AssertTrue(errors.IsEmpty,
                    "并发压测零异常" + (errors.IsEmpty ? "" : "（首个异常：" + errors.ToArray()[0].GetType().Name + " " + errors.ToArray()[0].Message + "）"));
            }
            finally
            {
                _running = false;
                ZhuluContext.Instance.DebugLogging = origDebug;
                pipe.ClearAll();
                pipe.Resume();
            }
        }

        private static volatile bool _running;

        private static void CompareBothPaths(ModifierPipeline pipe, ZhuluConfig cfg, string phase)        {
            var queries = new List<(string ch, string kid, float b)>();
            foreach (var ch in Channels)
                foreach (var kid in new[] { "k1", "k2", "k3", null })
                    foreach (var b in BaseValues)
                        queries.Add((ch, kid, b));

            cfg.ModifierCacheEnabled = false;
            var linear = queries.ConvertAll(q => pipe.EvaluateChannelMultiplier(q.ch, q.kid, q.b));

            cfg.ModifierCacheEnabled = true;
            var cached = queries.ConvertAll(q => pipe.EvaluateChannelMultiplier(q.ch, q.kid, q.b));

            for (int i = 0; i < queries.Count; i++)
            {
                var q = queries[i];
                TestRunner.AssertClose(linear[i], cached[i],
                    $"[{phase}] 缓存/线性一致 {q.ch}/{q.kid ?? "-"}@{q.b}", 0.001f);
            }
        }
    }
}
