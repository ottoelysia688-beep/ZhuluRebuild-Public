using System;
using NeoModLoader.General;
using ZhuluRebuild.Core;

namespace ZhuluRebuild.Domain
{
    /// <summary>
    /// 战争分类管理器：注册自定义战争类型、分类战争、录入史册。
    /// </summary>
    public class WarfareManager
    {
        public static WarfareManager Instance { get; } = new WarfareManager();

        public static WarTypeAsset TotalWar;
        public static WarTypeAsset PunitiveWar;
        public static WarTypeAsset VassalWar;
        public static WarTypeAsset ClaimWar;
        public static WarTypeAsset IndependenceWar;
        public static WarTypeAsset MandateWar; // 2026-08-19 方案5：夺天命之战

        private WarfareManager() { }

        /// <summary>注册三种自定义战争类型到游戏原生战争类型库。</summary>
        public void RegisterWarTypes()
        {
            try
            {
                // ═══ 阶段2B 包3：全面战争恢复（2026-08-15 五重门槛：国力碾压/宣称≥2/厌战<30/世仇/20年冷却）═══
                TotalWar = new WarTypeAsset
                {
                    id = "zlr_total_war",
                    name_template = "war_conquest",
                    localized_type = "war_type_zlr_total",
                    localized_war_name = "war_name_zlr_total",
                    path_icon = "wars/war_conquest",
                    kingdom_for_name_attacker = true,
                    alliance_join = true,
                    can_end_with_plot = true
                };
                AssetManager.war_types_library.add(TotalWar);

                VassalWar = new WarTypeAsset
                {
                    id = "zlr_vassal_war",
                    name_template = "war_conquest",
                    localized_type = "war_type_zlr_vassal",
                    localized_war_name = "war_name_zlr_vassal",
                    path_icon = "wars/war_conquest",
                    kingdom_for_name_attacker = true,
                    alliance_join = true,
                    can_end_with_plot = true
                };
                AssetManager.war_types_library.add(VassalWar);

                ClaimWar = new WarTypeAsset
                {
                    id = "zlr_claim_war",
                    name_template = "war_conquest",
                    localized_type = "war_type_zlr_claim",
                    localized_war_name = "war_name_zlr_claim",
                    path_icon = "wars/war_conquest",
                    kingdom_for_name_attacker = true,
                    alliance_join = false,
                    can_end_with_plot = false
                };
                AssetManager.war_types_library.add(ClaimWar);

                IndependenceWar = new WarTypeAsset
                {
                    id = "zlr_independence_war",
                    name_template = "war_rebellion",
                    localized_type = "war_type_zlr_independence",
                    localized_war_name = "war_name_zlr_independence",
                    path_icon = "wars/war_rebellion",
                    kingdom_for_name_attacker = true,
                    alliance_join = false,
                    rebellion = true
                };
                AssetManager.war_types_library.add(IndependenceWar);

                // 2026-08-19 方案5：夺天命之战（强邻问鼎——挑战天命国，胜则天命易主）
                MandateWar = new WarTypeAsset
                {
                    id = "zlr_mandate_war",
                    name_template = "war_conquest",
                    localized_type = "war_type_zlr_mandate",
                    localized_war_name = "war_name_zlr_mandate",
                    path_icon = "wars/war_conquest",
                    kingdom_for_name_attacker = true,
                    alliance_join = false,
                    can_end_with_plot = false
                };
                AssetManager.war_types_library.add(MandateWar);

                // P2-30 断贡问罪（2026-08-23）：启用预留的讨伐战争类型——宗主对久废朝贡附庸的问罪之师。
                // 发动：VassalManager.TickTribute 概率判定；评估：ZhuluWarService.EvaluatePunitiveWar。
                PunitiveWar = new WarTypeAsset
                {
                    id = "zlr_punitive_war",
                    name_template = "war_conquest",
                    localized_type = "war_type_zlr_punitive",
                    localized_war_name = "war_name_zlr_punitive",
                    path_icon = "wars/war_conquest",
                    kingdom_for_name_attacker = true,
                    alliance_join = false,
                    can_end_with_plot = false
                };
                AssetManager.war_types_library.add(PunitiveWar);

                // 本地化文本（全面/讨伐暂时封印，保留文本以备恢复）
                LM.AddToCurrentLocale("war_type_zlr_total", "全面战争");
                LM.AddToCurrentLocale("war_name_zlr_total", "{0}全面战争");
                LM.AddToCurrentLocale("war_type_zlr_punitive", "讨伐战争");
                LM.AddToCurrentLocale("war_name_zlr_punitive", "{0}讨伐战争");
                LM.AddToCurrentLocale("war_type_zlr_vassal", "附庸战争");
                LM.AddToCurrentLocale("war_name_zlr_vassal", "{0}附庸战争");
                LM.AddToCurrentLocale("war_type_zlr_claim", "宣称战争");
                LM.AddToCurrentLocale("war_name_zlr_claim", "{0}宣称战争");
                LM.AddToCurrentLocale("war_type_zlr_independence", "独立战争");
                LM.AddToCurrentLocale("war_name_zlr_independence", "{0}独立战争");
                LM.AddToCurrentLocale("war_type_zlr_mandate", "争天命/天眷之战");
                LM.AddToCurrentLocale("war_name_zlr_mandate", "{0}争天之战");

                ZhuluContext.Instance.LogInfo("[WarfareManager] 注册自定义战争类型完成（附庸/宣称/独立/夺天命/问罪），全面战仍封印");
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[WarfareManager] 注册战争类型失败", ex);
            }
        }

        /// <summary>
        /// 根据战争双方上下文分类战争类型。
        /// 全面战争和讨伐战争已封印，当前始终返回null（原版normal战争已被网关拦截，此函数不会被触发）。
        /// </summary>
        public WarTypeAsset ClassifyWar(Kingdom attacker, Kingdom defender)
        {
            // 全面战争和讨伐战争已封印，原版normal战争已被WarGateway拦截
            // 未来恢复时取消下方注释即可
            // if (attacker == null || defender == null) return null;
            // string aId = attacker.id.ToString();
            // string dId = defender.id.ToString();
            // bool aIsMandate = OrthodoxMandateManager.Instance.IsMandateHolder(aId);
            // bool dIsMandate = OrthodoxMandateManager.Instance.IsMandateHolder(dId);
            // if (aIsMandate && !dIsMandate) return PunitiveWar;
            // int aCities = attacker.cities != null ? attacker.cities.Count : 0;
            // int dCities = defender.cities != null ? defender.cities.Count : 0;
            // if (aCities >= 12 && dCities >= 6) return TotalWar;
            // if (aCities >= 6 && dCities >= 12) return TotalWar;
            return null;
        }

        /// <summary>获取战争类型的中文名称。</summary>
        public static string GetWarTypeName(string warTypeId)
        {
            switch (warTypeId)
            {
                case "zlr_total_war": return "全面战争";
                case "zlr_punitive_war": return "讨伐战争";
                case "zlr_vassal_war": return "附庸战争";
                case "zlr_claim_war": return "宣称战争";
                case "zlr_independence_war": return "独立战争";
                case "zlr_mandate_war": return "争天命/天眷之战";
                case "normal": return "征服战争";
                case "spite": return "仇恨战争";
                case "rebellion": return "叛乱";
                case "inspire": return "煽动叛乱";
                case "whisper_of_war": return "低语战争";
                default: return "战争";
            }
        }

        /// <summary>获取战争类型的颜色标签（用于UI显示）。</summary>
        public static string GetWarTypeColor(string warTypeId)
        {
            switch (warTypeId)
            {
                case "zlr_total_war": return "#FF4444";
                case "zlr_punitive_war": return "#FFD700";
                case "zlr_vassal_war": return "#4488FF";
                case "zlr_claim_war": return "#FF8800";
                case "zlr_independence_war": return "#AA44FF";
                case "zlr_mandate_war": return "#FFD700";
                default: return "#FFFFFF";
            }
        }

        /// <summary>战争开始时录入史册。</summary>
        public void LogWarStart(War war, Kingdom attacker, Kingdom defender, WarTypeAsset classifiedType)
        {
            try
            {
                int year = ZhuluTime.CurrentYear;
                string aName = attacker.name ?? "未知国";
                string dName = defender.name ?? "未知国";
                string aId = attacker.id.ToString();
                string warTypeName = GetWarTypeName(classifiedType.id);

                string text;
                if (classifiedType.id == "zlr_punitive_war")
                {
                    text = $"<color={UI.ZhuluUIStyle.Hex.Imperial}>【问罪】</color>{aName} 以久废朝贡之罪，对 {dName} 兴<color={UI.ZhuluUIStyle.Hex.Imperial}>问罪之师</color>！";
                }
                else if (classifiedType.id == "zlr_total_war")
                {
                    text = $"<color=#FF4444>{aName}</color> 与 <color=#FF4444>{dName}</color> 爆发<color=#FF4444>全面战争</color>！";
                }
                else
                {
                    text = $"<color=#4488FF>{aName}</color> 对 <color=#6ca3fd>{dName}</color> 发动<color=#4488FF>附庸战争</color>！";
                }

                // 录入攻方史册
                ChronicleManager.Instance.AddEntry(year, ChronicleEventType.WarDeclared, aId, aName, "", text, true);

                ZhuluContext.Instance.LogInfo($"[WarfareManager] 战争分类: {aName} vs {dName} -> {warTypeName}");
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[WarfareManager] 录入战争开始史册失败", ex);
            }
        }

        /// <summary>战争结束时录入史册。</summary>
        public void LogWarEnd(War war, WarWinner winner)
        {
            try
            {
                if (war == null) return;
                var asset = war.getAsset();
                if (asset == null) return;

                // 只记录自定义战争类型的结束（全面/讨伐已封印，不会出现）
                string warTypeId = asset.id;
                if (warTypeId != "zlr_vassal_war" && warTypeId != "zlr_punitive_war")
                    return;

                int year = ZhuluTime.CurrentYear;
                Kingdom attacker = war.getMainAttacker();
                Kingdom defender = war.getMainDefender();
                if (attacker == null) return;

                string aName = attacker.name ?? "未知国";
                string dName = defender != null ? defender.name : "未知国";
                string aId = attacker.id.ToString();
                string warTypeName = GetWarTypeName(warTypeId);

                string winnerText;
                if (winner == WarWinner.Attackers)
                    winnerText = $"<color=#88FF88>{aName}</color> 大获全胜";
                else if (winner == WarWinner.Defenders)
                    winnerText = $"<color=#88FF88>{dName}</color> 成功抵御";
                else
                    winnerText = "双方议和";

                string text = $"{warTypeName}结束，{winnerText}。";

                ChronicleManager.Instance.AddEntry(year, ChronicleEventType.WarDeclared, aId, aName, "", text, true);
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[WarfareManager] 录入战争结束史册失败", ex);
            }
        }
    }
}
