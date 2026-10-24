using System;

namespace ZhuluRebuild.Core
{
    /// <summary>
    /// 防御性代码守卫助手（P0-3 空 catch 治理，2026-08-23）。
    /// 背景：全库约 570 处 catch{} 空吞——出错既无日志也无降级信号，线上排查困难；
    /// 但防御性路径又确实需要"吞异常继续"。折中方案：吞掉但留**限频告警**信号。
    /// 用法：
    ///   ZhuluGuard.Run("Warfare.EndWar.附庸收编", () => { ... });      // 无返回值
    ///   int v = ZhuluGuard.Get("X.Y", () => Compute(), 0);             // 带返回值+兜底
    /// 同一 tag 默认 30 秒最多告警一次（热路径防刷屏）；每次世界加载后首错必现。
    /// 改造策略：优先治理存档/战争结算/天命结算等"静默失效=状态损坏"的关键路径；
    /// UI 渲染型 best-effort 守卫可保留空 catch（失败只影响显示不影响数据）。
    /// </summary>
    public static class ZhuluGuard
    {
        /// <summary>执行 action；异常时限频告警并继续（不外泄异常）。</summary>
        public static void Run(string tag, Action action)
        {
            try { action(); }
            catch (Exception ex) { Warn(tag, ex); }
        }

        /// <summary>执行 func 返回结果；异常时返回 fallback 并限频告警。</summary>
        public static T Get<T>(string tag, Func<T> func, T fallback = default(T))
        {
            try { return func(); }
            catch (Exception ex) { Warn(tag, ex); return fallback; }
        }

        private static void Warn(string tag, Exception ex)
        {
            try
            {
                ZhuluContext.Instance.LogRateLimited(
                    "guard:" + tag,
                    "[Guard] " + tag + " 异常(限频30s): " + ex.GetType().Name + ": " + ex.Message,
                    30f);
            }
            catch { /* 守卫自身异常绝不外泄 */ }
        }
    }
}
