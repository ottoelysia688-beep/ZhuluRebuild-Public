using System;
using HarmonyLib;
using ZhuluRebuild.Domain;
using ZhuluRebuild.Gameplay;

namespace ZhuluRebuild.Adapters
{
    /// <summary>
    /// 退化城三连防护 v4（v3 教训：字符串类型名 HarmonyPatch("Docks",...) 在 NML 动态程序集环境下
    /// 被 Harmony 解析到 0Harmony 程序集 → TypeLoadException → 五个守卫全部挂载失败成空枪
    /// （日志五连"挂载失败"实锤）→ 船坞/扩区 NRE 每帧炸断 CityManager.update 城市队列 →
    /// 排后面的城市每帧被跳过：AI/觅食/生育全停 → 单位静止饿死（人口 3000→300 实锤）。
    ///
    /// v4：全部 typeof 强引用（编译期绑定，零解析）；另补扩区 biome NRE 守卫。
    /// 全部 Finalizer 吞异常（bool 返回值方法显式置 false=放弃扩区，安全侧）。
    /// </summary>
    public static class DegenerateCityGuardPatch
    {
        [HarmonyPatch(typeof(Docks), "buildBoatFromHere")]
        public static class DocksBuildBoatGuard
        {
            [HarmonyFinalizer]
            public static Exception Finalizer(Exception __exception)
            {
                return null;   // 退化城船坞 NRE——遏制 CityManager.update 炸断级联
            }
        }

        [HarmonyPatch(typeof(BuildingAsset), "getRandomBoatAssetToBuild")]
        public static class RandomBoatAssetGuard
        {
            [HarmonyFinalizer]
            public static Exception Finalizer(Exception __exception)
            {
                return null;
            }
        }

        // 单点全覆盖：图标(getSpriteIcon)/背景(getSpriteBackground)/边框(getSpriteFrame)
        // 全部最终汇聚到 loadSpriteFromAsset——守它一处=全家族免疫（Player5 实锤 18586 条
        // 全走 getElementIcon←CityBanner 图标路径，v4 只守了背景路径）
        [HarmonyPatch(typeof(GenericBannerLibrary), "loadSpriteFromAsset")]
        public static class LoadSpriteFromAssetGuard
        {
            [HarmonyFinalizer]
            public static Exception Finalizer(Exception __exception, ref UnityEngine.Sprite __result)
            {
                if (__exception != null) __result = null;   // 空白徽章，优于异常风暴
                return null;
            }
        }

        [HarmonyPatch(typeof(Kingdom), "getElementIcon")]
        public static class KingdomElementIconGuard
        {
            [HarmonyFinalizer]
            public static Exception Finalizer(Exception __exception)
            {
                return null;
            }
        }

        [HarmonyPatch(typeof(CityBanner), "setupBanner")]
        public static class CityBannerSetupGuard
        {
            [HarmonyFinalizer]
            public static Exception Finalizer(Exception __exception)
            {
                return null;
            }
        }

        [HarmonyPatch(typeof(Kingdom), "getElementBackground")]
        public static class KingdomElementBackgroundGuard
        {
            [HarmonyFinalizer]
            public static Exception Finalizer(Exception __exception)
            {
                return null;   // 坏 banner 王国：背景空白，优于每帧越界
            }
        }

        [HarmonyPatch(typeof(KingdomBanner), "setupBanner")]
        public static class KingdomBannerSetupGuard
        {
            [HarmonyFinalizer]
            public static Exception Finalizer(Exception __exception)
            {
                return null;
            }
        }

        // 城建AI守卫：零人口/退化城 calcPossibleBuildings NRE（getActorAsset 采不到市民样本）
        // 类型在 ai.behaviours 命名空间——typeof 经编译期解析（v4 教训：字符串解析会挂）
        [HarmonyPatch(typeof(global::ai.behaviours.CityBehBuild), "calcPossibleBuildings")]
        public static class CityBehBuildGuard
        {
            [HarmonyFinalizer]
            public static Exception Finalizer(Exception __exception)
            {
                return null;
            }
        }

        [HarmonyPatch(typeof(TileZone), "checkCanSettleInThisBiomes")]
        public static class CheckSettleBiomesGuard
        {
            [HarmonyFinalizer]
            public static Exception Finalizer(Exception __exception, ref bool __result)
            {
                if (__exception != null) __result = false;   // 异常=判不可安家（放弃扩区——安全侧）
                return null;
            }
        }
    }

    /// <summary>稳定度通道 postfix（typeof 强引用版——v3 字符串解析挂载失败）。</summary>
    public static class StabilityChannelPatch
    {
        [HarmonyPatch(typeof(StabilityManager), "GetStability")]
        public static class GetStabilityPostfix
        {
            [HarmonyPostfix]
            public static void Postfix(string kingdomId, ref float __result)
            {
                try
                {
                    float add = ModifierPipeline.Instance.EvaluateChannelMultiplier(ModifierChannels.StabilityBonus, kingdomId, 0f);
                    if (!UnityEngine.Mathf.Approximately(add, 0f)) __result += add;
                }
                catch { }
            }
        }
    }
}
