using System;
using NeoModLoader.api;

namespace ZhuluRebuild.Core
{
    /// <summary>
    /// 逐鹿 Mod 配置（NML ModConfig 模式，参考鬼谷修仙：default_config.json 定义 + Callback 反射同步）。
    /// 配置 UI 由 NML 自动生成（Mod 列表 → 配置窗口）。
    /// </summary>
    public static class ZhuluModConfig
    {
        /// <summary>
        /// 分崩离析事件开关（原版 culture_divide / religion_schism / language_divergence 三个分裂谋划）。
        /// 默认 false（禁用）：分裂事件在逐鹿战争体系下未处理（战争期间国家突然分家、分裂继承战争有 bug），
        /// 玩家可在 NML 配置窗口开启。
        /// </summary>
        public static bool SplitEventsEnabled = true; // 阶段2L：默认开启（拦截原版分裂——用户要求全开）

        /// <summary>NML 配置窗口回调（default_config.json Callback 指向此方法）。</summary>
        public static void SetSplitEventsEnabled(bool value)
        {
            SplitEventsEnabled = value;
            ZhuluContext.Instance.LogInfo($"[ZhuluModConfig] 分崩离析事件开关: {(value ? "开启" : "关闭")}");
        }

        /// <summary>注册配置 UI 中文显示名（NML 配置窗口组名/项名走 LM.Get(id)，2026-08-15 用户反馈英文）。</summary>
        public static void RegisterLocalization()
        {
            try
            {
                NeoModLoader.General.LM.AddToCurrentLocale("SplitEvents", "分崩离析");
                NeoModLoader.General.LM.AddToCurrentLocale("SplitEventsEnabled", "分崩离析事件开关");
                NeoModLoader.General.LM.AddToCurrentLocale("SplitEventsEnabled Description", "开启后允许原版文化/宗教/语言分裂事件（默认关闭：逐鹿战争体系下分裂处理不完善）");
            }
            catch { }
        }

        /// <summary>启动时从 NML ModConfig 同步初始值（参考鬼谷 ConfigSync 模式）。</summary>
        public static void Initialize(ModConfig cfg)
        {
            try
            {
                if (cfg == null) return;
                if (cfg["SplitEvents"] != null && cfg["SplitEvents"].TryGetValue("SplitEventsEnabled", out var item))
                {
                    SplitEventsEnabled = item.BoolVal;
                    ZhuluContext.Instance.LogInfo($"[ZhuluModConfig] 配置已同步: 分崩离析={(SplitEventsEnabled ? "开" : "关")}");
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[ZhuluModConfig] 初始化同步失败: {ex.Message}");
            }
        }
    }
}
