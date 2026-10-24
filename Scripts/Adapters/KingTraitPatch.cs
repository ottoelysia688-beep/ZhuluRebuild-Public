using System;
using HarmonyLib;
using ZhuluRebuild.Core;
using ZhuluRebuild.Domain;

namespace ZhuluRebuild.Adapters
{
    /// <summary>
    /// 2026-08-20 换君即时特质处理（Harmony 自动扫描挂载——与 RoyalClanTraitPatch 同模式）。
    /// Kingdom.setKing 是继承/篡位/政变/禅让的统一入口：
    /// · 新王即时移除官职/城主/储君特质（君王不任职储——年度扫描前的即时修正）
    /// · 新王若为亡国之君 → 即时赋予复国之君（与年度检测互补）
    /// · 旧君退位清理（2026-10-06 D批bug修复）：禅让/篡位/政变换君后，旧君的风评五特质与
    ///   爵位/游牧等级特质即时移除（TickKingTraits 只扫在位君主，旧君脱离扫描即永久残留）
    /// pFromLoad=true 为读档恢复，跳过（特质状态已随存档）。
    /// </summary>
    [HarmonyPatch(typeof(Kingdom), "setKing")]
    public static class KingTraitPatch
    {
        // 2026-10-23 [F5]：旧君引用改用 Harmony 原生 __state（Prefix→Postfix 单次调用传参）。
        // 旧实现用 static 字段 _prevKing：Postfix 中若 ClearReignOnlyTraits 抛异常，置空语句执行不到，
        // 字段残留上一任君主 → 下次 setKing 会清错人的王权特质。__state 生命周期绑定单次调用，天然无残留。
        [HarmonyPrefix]
        public static void CapturePrevKing(Kingdom __instance, out Actor __state)
        {
            try { __state = __instance != null ? __instance.king : null; } catch { __state = null; }
        }

        public static void Postfix(Kingdom __instance, Actor pActor, bool pFromLoad, Actor __state)
        {
            try
            {
                // ③ 旧君退位清理：先于 pActor 判空执行（setKing(null) 王位空悬同样适用）；
                //    正常驾崩继承的死亡旧君由 ClearReignOnlyTraits 的 isAlive 守卫跳过
                if (!pFromLoad && __state != null && !ReferenceEquals(__state, pActor))
                {
                    TraitManager.ClearReignOnlyTraits(__state);
                }

                if (pFromLoad || pActor == null || !pActor.isAlive()) return;

                // ① 君王不任职储：即时清除官职/城主/太子/世子特质
                foreach (var tid in TraitManager.OfficeTraitIds)
                {
                    try { if (pActor.hasTrait(tid)) pActor.removeTrait(tid); } catch { }
                }
                try { if (pActor.hasTrait(TraitManager.TRAIT_CITY_LORD)) pActor.removeTrait(TraitManager.TRAIT_CITY_LORD); } catch { }
                try { if (pActor.hasTrait(TraitManager.TRAIT_CROWN_PRINCE)) pActor.removeTrait(TraitManager.TRAIT_CROWN_PRINCE); } catch { }
                try { if (pActor.hasTrait(TraitManager.TRAIT_HEIR_APPARENT)) pActor.removeTrait(TraitManager.TRAIT_HEIR_APPARENT); } catch { }

                // ② 复国之君即时检测（亡国之君再登大位——addTrait 互斥链再清一遍官职/储君）
                if (pActor.hasTrait(TraitManager.TRAIT_FALLEN_KING) && !pActor.hasTrait(TraitManager.TRAIT_RESTORED_KING))
                {
                    if (pActor.addTrait(TraitManager.TRAIT_RESTORED_KING))
                    {
                        string kName = __instance?.name ?? "本国";
                        string pName = pActor.getName() ?? "复国之君";
                        try
                        {
                            ChronicleManager.Instance.AddEntry(Core.ZhuluTime.CurrentYear,
                                ChronicleEventType.KingChange, __instance.id.ToString(), kName, pName,
                                $"<color={UI.ZhuluUIStyle.Hex.Imperial}>复国之君</color>：{pName}曾失社稷，今重登大位，中兴旧邦！", true);
                        }
                        catch { }
                        ZhuluContext.Instance.LogInfo($"[KingTrait] 复国之君即时赋予: {pName}（{kName}）");
                    }
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[KingTrait] setKing 特质处理异常: {ex.Message}");
            }
        }
    }
}
