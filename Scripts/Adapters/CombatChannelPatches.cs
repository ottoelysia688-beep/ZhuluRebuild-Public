using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ZhuluRebuild.Gameplay;

namespace ZhuluRebuild.Adapters
{
    /// <summary>
    /// 阶段3H-19：战斗四通道进主循环（反编译实锤：BaseSimObject.stats 是 public BaseStats 字典，
    /// 键 "damage"/"health"/"speed"/"armor"——armor 为减伤百分比（1-armor/100）；Actor.updateStats()
    /// 是统一重算点（internal，Harmony 可补丁，重触发用反射调用）。
    /// 通道语义：
    ///   AttackPower  乘算 stats["damage"]   （1.10 = 攻击 +10%）
    ///   UnitHealth   乘算 stats["health"]   （1.10 = 血量 +10%）
    ///   ArmySpeed    乘算 stats["speed"]    （1.10 = 移速 +10%）
    ///   DefensePower 加算 stats["armor"]    （+10 = 受伤 -10%，armor 本身是减伤百分比）
    /// updateStats 每次从特质/物品重merge后 normalize——postfix 在其末尾乘算，无叠加污染。
    /// 政策颁布/废止/读档恢复时经 RefreshKingdom 反射重算全军。
    /// </summary>
    public static class CombatChannelPatches
    {
        private static readonly FieldInfo StatsField = AccessTools.Field(typeof(BaseSimObject), "stats");
        private static readonly MethodInfo UpdateStatsMethod = AccessTools.Method(typeof(Actor), "updateStats");

        [HarmonyPatch(typeof(Actor), "updateStats")]
        public static class ActorUpdateStatsPatch
        {
            [HarmonyPostfix]
            public static void Postfix(Actor __instance)
            {
                try
                {
                    Kingdom k = __instance?.kingdom;
                    if (k == null || !k.isAlive()) return;
                    string kId = k.id.ToString();
                    var bs = StatsField?.GetValue(__instance) as BaseStats;
                    if (bs == null) return;

                    float atk = ModifierPipeline.Instance.EvaluateChannelMultiplier(ModifierChannels.AttackPower, kId, 1f);
                    float hp = ModifierPipeline.Instance.EvaluateChannelMultiplier(ModifierChannels.UnitHealth, kId, 1f);
                    float spd = ModifierPipeline.Instance.EvaluateChannelMultiplier(ModifierChannels.ArmySpeed, kId, 1f);
                    float def = ModifierPipeline.Instance.EvaluateChannelMultiplier(ModifierChannels.DefensePower, kId, 0f);

                    if (atk != 1f) bs["damage"] = bs["damage"] * atk;
                    if (hp != 1f) bs["health"] = bs["health"] * hp;
                    if (spd != 1f) bs["speed"] = bs["speed"] * spd;
                    if (def != 0f) bs["armor"] = bs["armor"] + def;
                }
                catch { }
            }
        }

        /// <summary>政策变动/读档恢复后：全军重算战斗属性（updateStats 为 internal——反射调用）。</summary>
        public static void RefreshKingdom(Kingdom k)
        {
            try
            {
                if (k == null || !k.isAlive() || k.units == null || UpdateStatsMethod == null) return;
                foreach (Actor u in k.units)
                {
                    try { if (u != null && u.isAlive()) UpdateStatsMethod.Invoke(u, null); } catch { }
                }
            }
            catch { }
        }

        /// <summary>稳定度通道 postfix 已迁至 DegenerateCityGuardPatch.StabilityChannelPatch（typeof 强引用版）。</summary>
    }
}
