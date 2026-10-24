using System;
using HarmonyLib;

namespace ZhuluRebuild.Adapters
{
    /// <summary>
    /// 阶段3H-22b：王位统一拦截——Kingdom.setKing 前缀（覆盖政变/篡位/继承/原版全部立王路径）。
    /// 用户实锤"大量人口在时船只当上国王"：船等载具 Actor（asset.is_boat）住在 kingdom.units，
    /// 属性数值高，任何"从全体单位择优"的路径都可能评分反超真市民。
    /// 拦截：新王非智慧生物/载具 → 从本国智慧生物中择优替换；一个都没有 → 放行原版（避免无限循环，
    /// 由原版无王逻辑接管）。只有智慧生物入选=既有设定的恢复与统一强制。
    /// </summary>
    [HarmonyPatch(typeof(Kingdom), "setKing")]
    public static class KingSapientGatePatch
    {
        [HarmonyPrefix]
        public static void Prefix(Kingdom __instance, ref Actor __0)
        {
            try
            {
                Actor cand = __0;
                if (cand != null && IsCapable(cand)) return;

                // 候选不合格 → 本国智慧生物择优替换
                Actor best = null;
                int bestScore = -1;
                try
                {
                    if (__instance?.units == null) return;
                    foreach (Actor u in __instance.units)
                    {
                        if (u == null || !u.isAlive() || u == cand) continue;
                        try { if (u.asset != null && u.asset.is_boat) continue; } catch { }
                        if (!u.isSapient()) continue;
                        try { if (u.asset != null && !u.asset.civ) continue; } catch { }
                        int age = 0; try { age = u.getAge(); } catch { }
                        int score = (u.intelligence + u.stewardship + u.diplomacy) * 10 + 100 - age / 5;
                        if (score > bestScore) { bestScore = score; best = u; }
                    }
                }
                catch { }
                if (best != null)
                {
                    ZhuluRebuild.Core.ZhuluContext.Instance.LogWarning(
                        $"[KingGate] 拦截非智慧立王: {(cand != null ? cand.getName() ?? "载具" : "null")} → 改立 {best.getName()}（{__instance.name}）");
                    __0 = best;
                }
                // 无智慧生物可立 → 放行原调用（原版无王逻辑接管，不制造死循环）
            }
            catch { }
        }

        private static bool IsCapable(Actor unit)
        {
            try
            {
                if (unit.asset != null && unit.asset.is_boat) return false;
                if (!unit.isSapient()) return false;
                if (unit.asset != null && !unit.asset.civ) return false;
                return true;
            }
            catch { return true; }
        }
    }
}
