using System;
using System.Collections.Generic;
using HarmonyLib;
using ZhuluRebuild.Core;

namespace ZhuluRebuild.Naming
{
    /// <summary>
    /// 批次14：Actor.getName 前置补丁（借鉴 ChineseName_1.5.0 的成熟方案）。
    /// 在任何 mod/系统读取单位名时，如果该单位有逐鹿跟踪中文名但 data.name 尚未写入，
    /// 则先行写入——保证任何调用方读到的一定是中文。
    /// 解决鬼谷/玄鉴等 mod 自行 spawn 单位绕过常规命名钩子导致排行榜显示英文的问题。
    /// </summary>
    public static class ActorGetNamePrefixPatch
    {
        private static bool _patched;

        public static void Mount(Harmony harmony)
        {
            if (_patched) return;
            _patched = true;
            try
            {
                var method = AccessTools.Method(typeof(Actor), nameof(Actor.getName));
                if (method == null)
                {
                    ZhuluContext.Instance.LogWarning("[GetNamePrefix] 未找到 Actor.getName，补丁跳过");
                    return;
                }
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(ActorGetNamePrefixPatch), nameof(Prefix)));
                ZhuluContext.Instance.LogInfo("[GetNamePrefix] Actor.getName 前置补丁挂载成功");
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[GetNamePrefix] 挂载异常", ex);
            }
        }

        /// <summary>
        /// Prefix 返回 void 不截断原方法——只是确保 data.name 已是中文后再执行原始 getName。
        /// </summary>
        static void Prefix(Actor __instance)
        {
            try
            {
                if (__instance == null) return;
                string svcName = ZhuluChineseNameService.Instance.GetTrackedName(__instance);
                if (string.IsNullOrEmpty(svcName)) return;
                string cur = __instance.getName();
                if (cur == svcName || (!string.IsNullOrEmpty(cur) && cur.Contains(svcName))) return;
                // data.name 与跟踪名不一致 → 写入跟踪名（保证任何读取方拿到中文）
                Naming.ActorAccess.SetName(__instance, svcName);
            }
            catch { }
        }
    }
}
