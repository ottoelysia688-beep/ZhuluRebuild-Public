using System;
using HarmonyLib;
using ZhuluRebuild.Core;

namespace ZhuluRebuild.Adapters
{
    /// <summary>
    /// 分崩离析开关补丁（2026-08-15）：
    /// 拦截原版三个分裂谋划（culture_divide 文化 / religion_schism 宗教 / language_divergence 语言），
    /// 配置 SplitEventsEnabled=false（默认）时 check_is_possible 恒 false——谋划不出现在面板（直接没有），
    /// 开启时恢复原版行为。
    /// </summary>
    public static class SplitEventsPatch
    {
        private static readonly string[] SplitPlotIds = { "culture_divide", "religion_schism", "language_divergence" };
        private static bool _patched = false;

        /// <summary>由 Main.OnModLoad 调用（PlotsLibrary 已初始化后）。</summary>
        public static void Apply()
        {
            try
            {
                if (_patched) return;
                _patched = true;

                var plotsLib = AssetManager.plots_library;
                if (plotsLib == null) return;

                foreach (string plotId in SplitPlotIds)
                {
                    try
                    {
                        PlotAsset plot = plotsLib.get(plotId);
                        if (plot == null) continue;
                        // 监视 action：若开关关闭时 action 仍被执行 → 记录（排查分裂来源，2026-08-15 武朝分裂报告）
                        var origAction = plot.action;
                        plot.action = (Actor a) =>
                        {
                            if (!ZhuluModConfig.SplitEventsEnabled)
                            {
                                ZhuluContext.Instance.LogWarning($"[SplitEventsPatch] 分裂 action 被执行（开关=关，check 被绕过！）: {plotId} 执行者={a?.getName()}");
                            }
                            return origAction != null && origAction(a);
                        };
                        // 包装原 check_is_possible：开关关 → 恒 false（谋划不显示/不可用）
                        PlotCheckerDelegate original = plot.check_is_possible;
                        plot.check_is_possible = (Actor actor) =>
                        {
                            if (!ZhuluModConfig.SplitEventsEnabled) return false;
                            return original != null && original(actor);
                        };
                        ZhuluContext.Instance.LogInfo($"[SplitEventsPatch] 分崩离析开关已接管: {plotId}");
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[SplitEventsPatch] 应用失败: {ex.Message}");
            }
        }
    }
}
