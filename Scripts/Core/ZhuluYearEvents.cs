using System;

namespace ZhuluRebuild.Core
{
    /// <summary>
    /// 年度事件总线（批次9a，2026-08-23）：
    /// 把"跨年感知"从各 UI 窗口的轮询/每帧年检改为订阅制——
    /// 单发点=年度驱动补丁的跨年检测处（RunGlobalYearIfDue），多订阅者。
    /// 游戏暂停时模拟不走 updateAge → 不跨年 → 不触发事件 = 暂停零开销（P1-17 同源收益）。
    /// 使用约定：OnEnable 订阅 / OnDisable 退订严格配对（窗口关闭不销毁，漏退订=泄漏+重复触发）；
    /// 每个订阅者回调独立 try/catch，一个异常不影响其他订阅者。
    /// </summary>
    public static class ZhuluYearEvents
    {
        /// <summary>跨年事件（参数=新年份）。由年度驱动检测到 year 变化时触发一次。</summary>
        public static event Action<int> YearChanged;

        /// <summary>由年度驱动调用——外部勿手动触发。</summary>
        internal static void RaiseYearChanged(int newYear)
        {
            var h = YearChanged;
            if (h == null) return;
            foreach (Delegate d in h.GetInvocationList())
            {
                try { ((Action<int>)d)(newYear); }
                catch (Exception ex)
                {
                    try { ZhuluContext.Instance.LogWarning("[YearEvents] 订阅者异常(已隔离): " + ex.Message); } catch { }
                }
            }
        }

        /// <summary>安全订阅辅助（幂等——先退后订，防同方法重复挂载）。</summary>
        public static void Subscribe(Action<int> handler)
        {
            if (handler == null) return;
            YearChanged -= handler;
            YearChanged += handler;
        }

        /// <summary>安全退订辅助（未订阅时静默）。</summary>
        public static void Unsubscribe(Action<int> handler)
        {
            if (handler == null) return;
            YearChanged -= handler;
        }
    }
}
