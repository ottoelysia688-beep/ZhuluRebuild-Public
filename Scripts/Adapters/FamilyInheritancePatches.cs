using HarmonyLib;
using ZhuluRebuild.Core;
using ZhuluRebuild.Naming;

namespace ZhuluRebuild.Adapters
{
    /// <summary>
    /// 父母关系写入后的即时家族继承。
    /// setParent1/setParent2 都执行：第一次可先按父系归家，第二次发现王室母亲时会按更高优先级
    /// 覆盖为王室氏族，从而保证女皇及王室女性的子女不随男方改姓。
    /// </summary>
    [HarmonyPatch(typeof(Actor), "setParent1")]
    public static class FamilyInheritanceParent1Patch
    {
        [HarmonyPostfix]
        private static void Postfix(Actor __instance)
        {
            // P1-8：亲子关系变化 → KinshipTracker 查询缓存置脏（出生/过继必经此口）
            try { Domain.KinshipTracker.MarkDirty(); }
            catch { /* 置脏失败只影响缓存新鲜度，不阻断继承 */ }

            try { ZhuluChineseNameService.Instance.EnforceChildFamily(__instance); }
            catch (System.Exception ex) { ZhuluContext.Instance.LogWarning($"[FamilyInheritance] setParent1 后处理失败: {ex.Message}"); }
        }
    }

    [HarmonyPatch(typeof(Actor), "setParent2")]
    public static class FamilyInheritanceParent2Patch
    {
        [HarmonyPostfix]
        private static void Postfix(Actor __instance)
        {
            // P1-8：亲子关系变化 → KinshipTracker 查询缓存置脏（出生/过继必经此口）
            try { Domain.KinshipTracker.MarkDirty(); }
            catch { /* 置脏失败只影响缓存新鲜度，不阻断继承 */ }

            try { ZhuluChineseNameService.Instance.EnforceChildFamily(__instance); }
            catch (System.Exception ex) { ZhuluContext.Instance.LogWarning($"[FamilyInheritance] setParent2 后处理失败: {ex.Message}"); }
        }
    }
}
