using System;
using System.Collections.Generic;
using System.Linq;
using ZhuluRebuild.Core;

namespace ZhuluRebuild.Core
{
    /// <summary>
    /// 轻量性能计时器。记录每个子系统每次年度处理的耗时，定期输出最慢的N个。
    /// 设计原则：零分配热路径（Measure 内部不 new 对象），报告时才排序。
    /// </summary>
    public static class ZhuluBenchmark
    {
        private static readonly Dictionary<string, double> _totals = new Dictionary<string, double>();
        private static readonly Dictionary<string, int> _counts = new Dictionary<string, int>();
        private static readonly Dictionary<string, double> _peaks = new Dictionary<string, double>();
        private static int _lastReportYear = -1;
        private static bool _enabled = true;

        /// <summary>是否启用计时（可通过设置窗口关闭）。</summary>
        public static bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        /// <summary>包裹一次子系统调用并记录耗时（毫秒）。</summary>
        public static void Measure(string name, Action action)
        {
            if (!_enabled)
            {
                action();
                return;
            }

            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                action();
            }
            finally
            {
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - start)
                            * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

                if (!_totals.ContainsKey(name))
                {
                    _totals[name] = 0;
                    _counts[name] = 0;
                    _peaks[name] = 0;
                }
                _totals[name] += ms;
                _counts[name]++;
                if (ms > _peaks[name]) _peaks[name] = ms;
            }
        }

        /// <summary>每10年输出一次最慢的5个系统到游戏日志。</summary>
        public static void ReportIfNeeded(int year)
        {
            if (!_enabled) return;
            if (_lastReportYear < 0)
            {
                _lastReportYear = year;
                return;
            }
            if (year - _lastReportYear < 10) return;
            _lastReportYear = year;

            if (_totals.Count == 0) return;

            try
            {
                var sorted = _totals
                    .Select(kv => new
                    {
                        Name = kv.Key,
                        AvgMs = kv.Value / Math.Max(1, _counts[kv.Key]),
                        PeakMs = _peaks.ContainsKey(kv.Key) ? _peaks[kv.Key] : 0,
                        Count = _counts[kv.Key]
                    })
                    .OrderByDescending(x => x.AvgMs)
                    .Take(5)
                    .ToList();

                ZhuluContext.Instance.LogInfo($"[Benchmark] 第{year}年性能报告（平均耗时 Top5）：");
                foreach (var item in sorted)
                {
                    ZhuluContext.Instance.LogInfo(
                        $"  {item.Name,-12} 平均 {item.AvgMs:F3}ms  峰值 {item.PeakMs:F3}ms  调用 {item.Count} 次");
                }
            }
            catch { }
        }

        /// <summary>世界重载时清空所有计时数据。</summary>
        public static void Reset()
        {
            _totals.Clear();
            _counts.Clear();
            _peaks.Clear();
            _lastReportYear = -1;
        }
    }
}
