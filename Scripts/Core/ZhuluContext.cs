using System;
using System.IO;
using UnityEngine;

namespace ZhuluRebuild.Core
{
    public class ZhuluContext
    {
        public static ZhuluContext Instance { get; } = new ZhuluContext();

        public bool IsInitialized { get; private set; }
        public string ModPath { get; private set; }
        public bool DebugLogging { get; set; } = true;

        private ZhuluContext() { }

        public void Initialize(string modPath)
        {
            ModPath = modPath;
            ZhuluConfig.Instance.Initialize(modPath);
            DebugLogging = ZhuluConfig.Instance.DebugLogging;
        ZhuluBenchmark.Enabled = ZhuluConfig.Instance.PerformanceProfiling; // 2026-08-20：性能计时开关——关闭时仍执行全部年度系统
            IsInitialized = true;
            LogInfo($"[ZhuluRebuild] 上下文初始化完成，路径: {modPath}（配置: 日志={DebugLogging}）");
        }

        public void LogInfo(string message)
        {
            if (DebugLogging)
            {
                Emit(Debug.Log, "[ZhuluRebuild INFO] " + message);
            }
        }

        private readonly System.Collections.Generic.Dictionary<string, float> _rateLimitLast = new System.Collections.Generic.Dictionary<string, float>();

        /// <summary>限频日志（手册阶段3）：同一 key 在 intervalSeconds 内最多输出一次——防刷屏又不完全静默。</summary>
        public void LogRateLimited(string key, string message, float intervalSeconds = 10f)
        {
            try
            {
                float now = UnityEngine.Time.unscaledTime;
                if (_rateLimitLast.TryGetValue(key, out float last) && (now - last) < intervalSeconds) return;
                _rateLimitLast[key] = now;
                LogWarning(message);
            }
            catch { /* 限频日志自身异常不干扰主逻辑 */ }
        }

        public void LogWarning(string message)
        {
            Emit(Debug.LogWarning, "[ZhuluRebuild WARN] " + message);
        }

        public void LogError(string message, Exception ex = null)
        {
            // 输出完整堆栈而非仅 Message —— 只记一句话的日志根本没法定位屎山
            Emit(Debug.LogError, "[ZhuluRebuild ERROR] " + message + (ex != null ? "\n" + ex.ToString() : ""));
        }

        /// <summary>
        /// 日志落盘统一出口，**保证不向调用方抛异常**。
        ///
        /// 2026-10-23：单元测试进程里 `Debug.LogWarning` 会直接抛（Unity 原生未初始化），
        /// 结果"打一条警告"把整个调用链炸掉 —— 说明日志本身是脆弱点。
        /// 线上同理：日志通道异常不该让业务逻辑失败。这里吞掉异常并退化为控制台输出。
        /// 注：这是**刻意**的空 catch，属"日志不得影响主流程"的边界，不在空 catch 治理范围内。
        /// </summary>
        private static void Emit(Action<string> sink, string line)
        {
            try
            {
                sink(line);
            }
            catch
            {
                try { Console.WriteLine(line); } catch { /* 连控制台都不可用则彻底放弃，绝不外抛 */ }
            }
        }
    }
}
