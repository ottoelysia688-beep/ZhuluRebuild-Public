using System;
using HarmonyLib;
using ZhuluRebuild.Core;
using ZhuluRebuild.Domain;

namespace ZhuluRebuild.Adapters
{
    /// <summary>
    /// 分裂来源排查补丁（2026-08-15 武朝"和平分裂"报告）：
    /// City.makeOwnKingdom 是城市自立建国的唯一路径（叛乱/煽动/分崩离析都经过它），
    /// Postfix 记录触发参数（pRebellion/pFellApart）与城市归属，锁定分裂来源。
    /// </summary>
    [HarmonyPatch(typeof(City), "makeOwnKingdom")]
    public static class CityMakeOwnKingdomPatch
    {
        private static Kingdom _oldKingdom;

        [HarmonyPrefix]
        public static void Prefix(City __instance)
        {
            try
            {
                _oldKingdom = Traverse.Create(__instance).Field("kingdom").GetValue<Kingdom>();
            }
            catch { _oldKingdom = null; }
        }

        [HarmonyPostfix]
        public static void Postfix(City __instance, Actor pActor, bool pRebellion, bool pFellApart, Kingdom __result)
        {
            try
            {
                string cityName = __instance != null && __instance.data != null ? __instance.data.name ?? "?" : "?";
                string actorName = pActor != null ? pActor.getName() ?? "?" : "?";
                string newKing = __result != null ? __result.name ?? "?" : "?";
                string oldKing = _oldKingdom != null ? _oldKingdom.name ?? "?" : "?";
                string reason = pFellApart ? "分崩离析" : (pRebellion ? "叛乱/煽动" : "自立");
                ZhuluContext.Instance.LogWarning(
                    $"[SplitMonitor] 城市建国: {cityName} 从 {oldKing} 分裂 → {newKing}（原因={reason}, 发起={actorName}）");
            }
            catch { }
        }
    }

    /// <summary>
    /// 分崩离析门控（2026-08-19 根因修复）：
    /// ═════════════════════════════════════════════════════════════════
    /// IL 扫描实证（70 次分裂日志 + Assembly-CSharp.dll 逆向）：
    /// "诸城独立"来自 ai.behaviours.KingdomBehCheckKing.checkKingdomChaos（无主之国城池带主自立），
    /// "王旗多方执掌"来自 checkShatteredCrownEvent（碎裂王冠——王死族散，各城主称王）。
    /// 两者与 mod 的稳定度/天命/继承体系完全脱钩——燕朝（天命国）也随机裂成 10+ 邑国。
    /// ═════════════════════════════════════════════════════════════════
    /// 门控规则（保留原版机制于真正混乱之国）：
    ///   - 天命国：天命值 ≥40 不裂（天命未坠则社稷不倾；<40 时天命欲坠，听任原版裂解）
    ///   - 普通国：mod 稳定度 ≥40 不裂（朝纲尚稳无分崩之理；<40 时国将不国，原版裂解照常）
    /// 返回 false = 拦截原版分裂；true = 放行。
    /// </summary>
    [HarmonyPatch(typeof(ai.behaviours.KingdomBehCheckKing), "checkKingdomChaos")]
    public static class KingdomChaosGatePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Kingdom pMainKingdom)
        {
            return VanillaSplitGate.AllowVanillaSplit(pMainKingdom, "诸城独立");
        }
    }

    [HarmonyPatch(typeof(ai.behaviours.KingdomBehCheckKing), "checkShatteredCrownEvent")]
    public static class ShatteredCrownGatePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Kingdom pMainKingdom)
        {
            return VanillaSplitGate.AllowVanillaSplit(pMainKingdom, "碎裂王冠");
        }
    }

    /// <summary>分裂门控共用判定（稳定不裂 / 混乱放行）。
    /// 性能：按国年度缓存判定结果（AI 行为可能每 tick 调用，而 IsMandateHolder 是全王国实时扫描——
    /// 不缓存 = 每帧 O(国²) 扫描；分裂判定年内不变，缓存安全）。</summary>
    internal static class VanillaSplitGate
    {
        private static readonly System.Collections.Generic.Dictionary<string, int> _cacheYear = new System.Collections.Generic.Dictionary<string, int>();
        private static readonly System.Collections.Generic.Dictionary<string, bool> _cacheAllow = new System.Collections.Generic.Dictionary<string, bool>();

        public static bool AllowVanillaSplit(Kingdom k, string source)
        {
            try
            {
                if (k == null || !k.isAlive()) return true; // 灭国边缘不拦
                string kId = k.id.ToString();
                // 年度缓存命中（年内判定不变）
                int year = ZhuluTime.CurrentYear;
                if (_cacheYear.TryGetValue(kId, out int cachedYear) && cachedYear == year
                    && _cacheAllow.TryGetValue(kId, out bool cachedAllow))
                {
                    return cachedAllow;
                }

                bool allow = ComputeAllow(k, kId, source);
                _cacheYear[kId] = year;
                _cacheAllow[kId] = allow;
                if (_cacheYear.Count > 500) { _cacheYear.Clear(); _cacheAllow.Clear(); } // 亡国残留兜底清理
                return allow;
            }
            catch { return true; }
        }

        private static bool ComputeAllow(Kingdom k, string kId, string source)
        {
            // 天命国：天命 ≥40 不裂
            if (PolicyManager.IsMandateHolderForKingdom(k))
            {
                float mandate = PolicyManager.GetMandateValueForKingdom(k);
                if (mandate >= 40f)
                {
                    ZhuluContext.Instance.LogInfo($"[SplitGate] 拦截{source}: 【{k.name}】天命犹在（{mandate:F0}），社稷不裂");
                    return false;
                }
                return true; // 天命欲坠——听任裂解
            }
            // 普通国：稳定度 ≥40 不裂
            float stab = StabilityManager.Instance.GetStability(kId);
            if (stab >= 40f)
            {
                ZhuluContext.Instance.LogInfo($"[SplitGate] 拦截{source}: 【{k.name}】朝纲尚稳（{stab:F0}），不裂");
                return false;
            }
            return true;
        }
    }
}
