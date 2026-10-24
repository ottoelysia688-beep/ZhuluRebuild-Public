using System;
using UnityEngine;
using HarmonyLib;
using ZhuluRebuild.Core;
using ZhuluRebuild.Gameplay;

namespace ZhuluRebuild.Adapters
{
    /// <summary>
    /// 原生数值修正补丁集。
    /// 铁律：每个补丁独立成类 —— 历史教训：空 [HarmonyPatch] 或签名不匹配会让
    /// PatchAll 中途抛异常，连带拖死后续所有补丁（ISSUE_LOG 005）。
    /// 已删除 City.getFoodLimit 补丁：0.51.2 的 Assembly-CSharp.dll 中不存在该方法。
    /// </summary>
    public static class NativePatchHelpers
    {
        public static Kingdom GetKingdomFromCity(City city)
        {
            if (city == null) return null;
            try
            {
                var k = Traverse.Create(city).Field("kingdom").GetValue<Kingdom>();
                if (k != null) return k;
            }
            catch { }
            try
            {
                var k = Traverse.Create(city).Property("kingdom").GetValue<Kingdom>();
                if (k != null) return k;
            }
            catch { }
            return null;
        }
    }

    // ═══ 1. 城市忠诚度修正（旧版 Zhulu 已验证目标：LoyaltyCalculator.calculate）═══
    [HarmonyPatch(typeof(LoyaltyCalculator), "calculate")]
    public static class LoyaltyModifierPatch
    {
        [HarmonyPostfix]
        public static void Postfix(City pCity, ref int __result)
        {
            try
            {
                Kingdom k = NativePatchHelpers.GetKingdomFromCity(pCity);
                if (k == null) return;
                float extraLoyalty = ModifierPipeline.Instance.EvaluateChannelMultiplier(ModifierChannels.CityLoyaltyBonus, k.id.ToString(), 0f);
                if (!Mathf.Approximately(extraLoyalty, 0f))
                {
                    __result += Mathf.RoundToInt(extraLoyalty);
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[LoyaltyModifierPatch] 忠诚度计算补丁发生异常", ex);
            }
        }
    }

    // ═══ 2. 金币税收修正（旧版 Zhulu 已验证目标：Kingdom.getTaxRateLocal）═══
    [HarmonyPatch(typeof(Kingdom), "getTaxRateLocal")]
    public static class TaxModifierPatch
    {
        [HarmonyPostfix]
        public static void Postfix(Kingdom __instance, ref float __result)
        {
            try
            {
                if (__instance == null) return;
                float taxMult = ModifierPipeline.Instance.EvaluateChannelMultiplier(ModifierChannels.GoldIncomeMultiplier, __instance.id.ToString(), 1.0f);
                if (!Mathf.Approximately(taxMult, 1.0f))
                {
                    __result *= taxMult;
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[TaxModifierPatch] 税收效率计算补丁发生异常", ex);
            }
        }
    }

    // ═══ 3. 军队兵员上限修正（旧版 Zhulu 已验证目标：City.getMaxWarriors）═══
    [HarmonyPatch(typeof(City), "getMaxWarriors")]
    public static class MaxWarriorsModifierPatch
    {
        [HarmonyPostfix]
        public static void Postfix(City __instance, ref int __result)
        {
            try
            {
                Kingdom k = NativePatchHelpers.GetKingdomFromCity(__instance);
                if (k == null) return;
                float armyMult = ModifierPipeline.Instance.EvaluateChannelMultiplier(ModifierChannels.MilitaryReadiness, k.id.ToString(), 1.0f);
                if (!Mathf.Approximately(armyMult, 1.0f))
                {
                    __result = Mathf.Max(0, Mathf.RoundToInt(__result * armyMult));
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[MaxWarriorsModifierPatch] 军队上限计算补丁发生异常", ex);
            }
        }
    }

    // ═══ 4. 列国外交好感度修正 ═══
    // 0.51.2 反编译验证：getOpinion(Kingdom k1, Kingdom k2) 返回 KingdomOpinion（含 public int total），
    // 不是 int —— 参数名与返回类型必须与原生严格一致，否则补丁挂载即失败。
    [HarmonyPatch(typeof(DiplomacyManager), "getOpinion")]
    public static class OpinionModifierPatch
    {
        [HarmonyPostfix]
        public static void Postfix(Kingdom k1, Kingdom k2, KingdomOpinion __result)
        {
            try
            {
                if (k1 == null || k2 == null || __result == null) return;
                // 阶段2A A3-3：方向修正——getOpinion(k1,k2) = k1 对 k2 的看法；
                // 通道语义为「列国对本国的好感」（正朔/礼部描述），故加成挂在**被看方 k2**：
                // 列国(查) → 本国(k2 有通道) 时，好感 +bonus。
                float bonus = ModifierPipeline.Instance.EvaluateChannelMultiplier(ModifierChannels.DiplomaticOpinionBonus, k2.id.ToString(), 0f);
                if (!Mathf.Approximately(bonus, 0f))
                {
                    __result.total += Mathf.RoundToInt(bonus);
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[OpinionModifierPatch] 外交好感度计算补丁发生异常", ex);
            }
        }
    }

    // ═══ 5. 建筑物耐久修正 ═══
    // 0.51.2 反编译验证：getMaxHealth() 实际定义在基类 BaseSimObject 上（public）。
    // 以 Building 声明类型挂载会命中基类方法并被 Actor 等其他子类共享调用，
    // 因此 __instance 必须用基类类型 + 运行时 cast，防止对非建筑对象误伤。
    [HarmonyPatch(typeof(BaseSimObject), "getMaxHealth")]
    public static class BuildingHealthModifierPatch
    {
        [HarmonyPostfix]
        public static void Postfix(BaseSimObject __instance, ref int __result)
        {
            try
            {
                if (!(__instance is Building b)) return;
                City city = null;
                try { city = b.city; } catch { }
                if (city == null) return;

                Kingdom k = NativePatchHelpers.GetKingdomFromCity(city);
                if (k == null) return;
                float healthMult = ModifierPipeline.Instance.EvaluateChannelMultiplier(ModifierChannels.BuildingHealthMultiplier, k.id.ToString(), 1.0f);
                if (!Mathf.Approximately(healthMult, 1.0f))
                {
                    __result = Mathf.Max(1, Mathf.RoundToInt(__result * healthMult));
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[BuildingHealthModifierPatch] 建筑物耐久度计算补丁发生异常", ex);
            }
        }
    }

    // ═══ 6. 城市人口上限修正 ═══
    // 0.51.2 反编译验证：City.getPopulationMaximum() 是 public，返回 status.housing_total。
    // 乘算修正：结果 = 原始上限 × 管道倍率。影响城市可容纳的最大人口数。
    [HarmonyPatch(typeof(City), "getPopulationMaximum")]
    public static class PopulationCapModifierPatch
    {
        [HarmonyPostfix]
        public static void Postfix(City __instance, ref int __result)
        {
            try
            {
                if (__instance == null) return;
                Kingdom k = NativePatchHelpers.GetKingdomFromCity(__instance);
                if (k == null) return;
                float popMult = ModifierPipeline.Instance.EvaluateChannelMultiplier(ModifierChannels.PopulationGrowthSpeed, k.id.ToString(), 1.0f);
                if (!Mathf.Approximately(popMult, 1.0f))
                {
                    __result = Mathf.Max(1, Mathf.RoundToInt(__result * popMult));
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[PopulationCapModifierPatch] 人口上限计算补丁发生异常", ex);
            }
        }
    }

    // ═══ 7. 城镇上限按爵位保底 ═══
    // 天命加持：天命国城市上限最低 12（原有）；2026-08-19 扩展——公国保底 10 / 侯国保底 8 / 伯国保底 6。
    // 语义：保底（floor）——君主数值低导致原版上限不足时托底；上限已高于保底则不变（非封顶）。
    // 等级口径：ZhuluTierService.GetKingdomTierId（特质权威——与称号/政策门槛同源，防错位）。
    [HarmonyPatch(typeof(Kingdom), "getMaxCities")]
    public static class MandateCityCapPatch
    {
        [HarmonyPostfix]
        public static void Postfix(Kingdom __instance, ref int __result)
        {
            try
            {
                if (__instance == null) return;
                bool nomadic = ZhuluRebuild.Domain.KingdomCivilizationService.Instance.IsNomadic(__instance);
                string tierId = nomadic
                    ? ZhuluRebuild.Domain.NomadicTierService.GetKingdomTierId(__instance)
                    : ZhuluRebuild.Domain.ZhuluTierService.GetKingdomTierId(__instance);
                int floor = 0;
                if (ZhuluRebuild.Domain.PolicyManager.IsMandateHolderForKingdom(__instance)
                    || tierId == ZhuluRebuild.Domain.ZhuluTierService.TIER_TIANZI
                    || tierId == ZhuluRebuild.Domain.NomadicTierService.TIER_WANGTING)
                {
                    // 阶段3H-12：天命国保底按天命值分档（75+ 每升 5 分下限 +2；100 直取 30）
                    floor = 12;
                    try
                    {
                        float mv = ZhuluRebuild.Domain.KingdomCivilizationService.Instance.IsNomadic(__instance)
                            ? ZhuluRebuild.Domain.NomadicMandateManager.Instance.GetMandateValue(__instance.id.ToString())
                            : ZhuluRebuild.Domain.OrthodoxMandateManager.Instance.GetMandateValue(__instance.id.ToString());
                        if (mv >= 100f) floor = 30;
                        else if (mv >= 95f) floor = 24;
                        else if (mv >= 90f) floor = 22;
                        else if (mv >= 85f) floor = 20;
                        else if (mv >= 80f) floor = 18;
                        else if (mv >= 75f) floor = 16;
                        else if (mv >= 65f) floor = 14;
                        else if (mv >= 45f) floor = 12;
                        else if (mv >= 35f) floor = 10;
                        else floor = 8;
                    }
                    catch { }
                }
                else if (tierId == ZhuluRebuild.Domain.ZhuluTierService.TIER_GONG || tierId == ZhuluRebuild.Domain.NomadicTierService.TIER_DAHANTING) floor = 10;
                else if (tierId == ZhuluRebuild.Domain.ZhuluTierService.TIER_HOU || tierId == ZhuluRebuild.Domain.NomadicTierService.TIER_HANGUO) floor = 8;
                else if (tierId == ZhuluRebuild.Domain.ZhuluTierService.TIER_BO || tierId == ZhuluRebuild.Domain.NomadicTierService.TIER_BUMENG) floor = 6;
                if (floor > 0 && __result < floor)
                {
                    __result = floor;
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[MandateCityCapPatch] 城镇上限保底补丁发生异常", ex);
            }
        }
    }
}
