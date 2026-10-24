using System.Collections.Generic;
using ZhuluRebuild.Core;

namespace ZhuluRebuild.Domain
{
    /// <summary>
    /// 逐鹿战争统一入口。所有系统战争通过此网关发起，原版文明间随机战争被拦截。
    /// 设计原则：网关只做"放行/拦截"判定和日志，不改变任何战争规则。
    /// </summary>
    public static class WarGateway
    {
        /// <summary>逐鹿系统战争类型白名单（这些类型永远放行）。</summary>
        private static readonly HashSet<string> AllowedWarTypes = new HashSet<string>
        {
            "zlr_claim_war",
            "zlr_vassal_war",
            "zlr_independence_war",
            "zlr_total_war", // 阶段2B 包3：全面战争
            "zlr_mandate_war", // 2026-08-19 方案5：夺天命之战
            "zlr_punitive_war", // P2-30 断贡问罪之师（宗主惩久废朝贡的附庸）
            "rebellion",   // 原版叛乱
            "spite",       // 原版复仇
            "inspire"      // 原版煽动
        };

        /// <summary>
        /// 判断是否应拦截这场战争（前置补丁调用）。
        /// 返回 true = 拦截（阻止战争发生），false = 放行。
        /// </summary>
        public static bool ShouldBlockWar(Kingdom atk, Kingdom def, WarTypeAsset type)
        {
            if (type == null) return true;

            // 逐鹿系统战争和叛乱/特殊战争放行
            if (AllowedWarTypes.Contains(type.id)) return false;

            // 原版 normal 战争：只拦截文明王国之间的（野蛮人/中立/动物不拦截）
            if (type.id == "normal")
            {
                if (atk != null && def != null && !atk.isNeutral() && !def.isNeutral())
                    return true;  // 拦截文明间随机战争
            }

            // 其他未知类型默认放行（保守策略，不误杀）
            return false;
        }

        /// <summary>
        /// 统一开战入口。各系统调用此方法而非直接调 World.world.wars.newWar()。
        /// 提供统一的日志记录和史册录入。
        /// </summary>
        /// <summary>主动战争类型（受同时战争上限约束）；被动（独立/叛乱/复仇/煽动）豁免。</summary>
        private static bool IsPassiveWar(string typeId)
        {
            return typeId == "zlr_independence_war" || typeId == "rebellion"
                || typeId == "spite" || typeId == "inspire";
        }

        /// <summary>阶段2B 战争修复 ④：主动战争同时上限（每国最多 2 场；被动战争豁免）。</summary>
        private static bool ExceedsWarLimit(Kingdom atk)
        {
            try
            {
                if (atk == null) return false;
                var wars = atk.getWars();
                if (wars == null) return false;
                int count = 0;
                foreach (var _ in wars) { count++; if (count >= 2) return true; }
            }
            catch { }
            return false;
        }

        // D5-③（2026-10-06）宣战战力比下限：攻方兵力 < 守方×0.7 禁止主动扩张宣战
        // [PLACEHOLDER] 0.7——playtest 验证（失败信号：宣称战攻方胜率仍 <40%，或宣战量骤降 >50%）
        private const float WAR_DECLARE_MIN_STRENGTH_RATIO = 0.7f;

        public static War DeclareWar(Kingdom atk, Kingdom def, WarTypeAsset type, string reason)
        {
            // 阶段3H-2：夺天命之战最终防线——仅限同体系（游牧国永不可夺东方天命，反之亦然）
            try
            {
                if (type != null && type.id == "zlr_mandate_war" && atk != null && def != null
                    && KingdomCivilizationService.Instance.Get(atk) != KingdomCivilizationService.Instance.Get(def))
                {
                    ZhuluContext.Instance.LogWarning($"[WarGateway] 拒绝跨体系夺天命之战: {atk.name} → {def.name}");
                    return null;
                }
            }
            catch { }
            if (atk == null || def == null || type == null) return null;

            // 阶段2B ④：主动战争上限 2 场（防宣称连环开战；独立/叛乱等被动豁免）
            if (type != null && !IsPassiveWar(type.id) && ExceedsWarLimit(atk))
            {
                ZhuluContext.Instance.LogWarning($"[WarGateway] {atk.name} 已达同时战争上限(2)，拒绝宣战 {def.name}（{type.id}）");
                return null;
            }

            // D5-③（2026-10-06）宣战战力比下限：主动扩张战（宣称/全面/附庸等）攻方兵力 < 守方×0.7 时拒绝——
            // D批实测宣称战攻方胜率 26%（14/54），AI 无战力评估送宣战。
            // 问鼎（zlr_mandate_war 挑战者另行择优）/问罪（宗主权执行）/被动战争（独立/叛乱等弱势阀门）豁免。
            if (type != null && !IsPassiveWar(type.id)
                && type.id != "zlr_mandate_war" && type.id != "zlr_punitive_war")
            {
                float atkWarriors = 0f, defWarriors = 0f;
                try { atkWarriors = atk.countTotalWarriors(); } catch { }
                try { defWarriors = def.countTotalWarriors(); } catch { }
                if (defWarriors > 0f && atkWarriors < defWarriors * WAR_DECLARE_MIN_STRENGTH_RATIO)
                {
                    ZhuluContext.Instance.LogWarning($"[WarGateway] {atk.name} 兵力({atkWarriors:F0})不足守方({defWarriors:F0})×0.7，拒绝宣战 {def.name}（{type.id}）");
                    return null;
                }
            }

            // 阶段2C 邦交：停战期/互不侵犯期禁止宣战（和平条约与互不侵犯生效）
            // 2026-08-20 bug修复：独立战争原被 IsPassiveWar 豁免 → 刚签和约的附庸可立刻谋反开战
            // （用户实锤"合约期开战"）。现仅原版原生事件（叛乱/复仇/煽动）豁免；独立战争
            // 是附庸主动宣战，必须遵守和约——新附5年停战期内不可举兵。
            if (type != null && type.id != "rebellion" && type.id != "spite" && type.id != "inspire")
            {
                if (ZhuluDiplomacyService.IsWarForbidden(atk.id.ToString(), def.id.ToString(), ZhuluTime.CurrentYear))
                {
                    ZhuluContext.Instance.LogWarning($"[WarGateway] {atk.name} 与 {def.name} 有和约/互不侵犯，拒绝宣战");
                    return null;
                }
            }

            // 阶段2B 包3：主动宣战厌战门槛（厌战≥50 禁止；≥30 需正当理由——有宣称目标时视为正当）
            if (type != null && !IsPassiveWar(type.id))
            {
                bool needReason;
                if (!ZhuluWarService.CanDeclareWar(atk.id.ToString(), out needReason))
                {
                    ZhuluContext.Instance.LogWarning($"[WarGateway] {atk.name} 厌战过高，拒绝宣战 {def.name}");
                    return null;
                }
                if (needReason && type.id != "zlr_claim_war" && type.id != "zlr_total_war" && type.id != "zlr_mandate_war"
                    && type.id != "zlr_punitive_war")
                {
                    // 高厌战时仅允许有正当理由的战争（宣称=有领土诉求；全面战=世仇门槛已含正当性；问鼎=夺天命大义）
                    ZhuluContext.Instance.LogWarning($"[WarGateway] {atk.name} 厌战偏高，需正当理由（{type.id} 被拒）");
                    return null;
                }
            }

            War war = World.world.wars.newWar(atk, def, type);
            if (war != null)
            {
                // 阶段2B 包3：战争目标数据化（goal/start/score/开战城市数）
                ZhuluWarService.InitializeWarData(war, type.id, null, atk, def);
                ZhuluContext.Instance.LogInfo(
                    $"[WarGateway] {atk.name} 对 {def.name} 发动 {type.id}（{reason}）");
            }
            return war;
        }
    }
}
