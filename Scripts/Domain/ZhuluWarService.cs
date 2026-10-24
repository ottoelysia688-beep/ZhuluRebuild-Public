using System;
using System.Collections.Generic;
using NeoModLoader.General;
using ZhuluRebuild.Core;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Domain
{
    /// <summary>
    /// 战争架构服务（阶段2B 包3 重构，2026-08-15 方案定稿）：
    /// ① 战争目标数据化（custom_data：goal/target/start/score）
    /// ② 国家厌战（warExhaustion：宣战门槛 + 战争停战）
    /// ③ 战争分数制（年度评估：占城/击杀/首都 → 结算档位）
    /// ④ 求和扩展（按目标：让城/臣服/割地赔款）
    /// ⑤ 全面战争（五重门槛 + AI 触发 + 天下震惊）
    /// </summary>
    public static class ZhuluWarService
    {
        // ── custom_data key ──
        public const string WAR_GOAL = "zlr_war_goal";        // claim_city / vassalize / independent / annex
        public const string WAR_TARGET = "zlr_war_target";    // 目标城 id（宣称战）
        public const string WAR_SETTLED = "zlr_settled";      // 已按条款结算标记（防 EndWarPostfix 二次赔款）
        public const string WAR_START = "zlr_war_start";      // 开战年
        public const string WAR_SCORE = "zlr_war_score";      // 攻方净分数（可为负）
        public const string WAR_SCORE_DETAIL = "zlr_war_score_detail"; // 2026-08-19 串联·方案2.1③：分数构成（占城|失城|击杀|首都|时间——战争详情页四段横条）
        public const string WAR_ATK_START_CITIES = "zlr_war_atk_start_cities"; // 开战攻方城市数
        public const string WAR_DEF_START_CITIES = "zlr_war_def_start_cities"; // 开战守方城市数
        public const string WAR_ATK_START_ARMY = "zlr_war_atk_start_army"; // 开战攻方军力（长期枯竭判定）
        public const string WAR_DEF_START_ARMY = "zlr_war_def_start_army"; // 开战守方军力
        private const string WAR_LAST_PRESSURE_YEAR = "zlr_war_pressure_year";
        private const string WAR_LAST_DEAD_ATTACKERS = "zlr_war_last_dead_attackers";
        private const string WAR_LAST_DEAD_DEFENDERS = "zlr_war_last_dead_defenders";

        // ── 国家厌战（KingdomDataExt 自带 Prefix，key 不带前缀——2026-08-15 双前缀 bug 修复）──
        private const string KEY_EXHAUSTION = "war_exhaustion"; // 0-100
        public const int EXHAUSTION_DECLARE_LIMIT = 50;   // 厌战≥50 禁止主动宣战
        public const int EXHAUSTION_REASONABLE = 30;      // 厌战≥30 需正当理由（宣称/被挑衅）

        // ── 分数档位 ──
        public const int SCORE_ANNEX = 75;    // 大胜：吞并/灭国
        public const int SCORE_WIN = 50;      // 胜：割地/臣服
        public const int SCORE_MINOR = 30;    // 小胜：赔款
        // P1-A1 数值口径说明：白和概率公式为全新机制（无逐鹿旧值可循），参数集中于 WarScoreRules；
        // 硬上限取 15 年——贴近原"满10年必白和"的收束节奏（典型局面在10~14年概率性收场，
        // 仅"双主战党+非僵持"的极端死磕拖到15年强制），而非照搬 AW3 的年限刻度。
        public const int PEACE_YEARS_HARD = 15;

        // ── 全面战争 ──
        public const int TOTAL_WAR_POWER_RATIO = 150;  // 国力比 ≥150%（攻/守×100）
        public const int TOTAL_WAR_CLAIMS = 2;         // 宣称 ≥2
        public const int TOTAL_WAR_EXHAUSTION = 30;    // 攻方厌战 <30
        public const int TOTAL_WAR_PAST_WARS = 2;      // 近 50 年交战 ≥2 次
        public const int TOTAL_WAR_COOLDOWN = 20;      // 全局冷却 20 年
        public const float TOTAL_WAR_AI_CHANCE = 0.10f;// AI 年度概率

        private const string KEY_LAST_TOTAL_WAR = "zlr_last_total_war_year"; // 全局（存天命国？存 map）——用首个参战国记录

        // ═══════════════ ① 战争目标数据化 ═══════════════

        /// <summary>DeclareWar 统一写入战争附加数据（由 WarGateway 调用）。</summary>
        public static void InitializeWarData(War war, string typeId, string targetCityId, Kingdom atk, Kingdom def)
        {
            try
            {
                if (war == null) return;
                string goal = typeId == "zlr_claim_war" ? "claim_city"
                    : typeId == "zlr_vassal_war" ? "vassalize"
                    : typeId == "zlr_independence_war" ? "independent"
                    : typeId == "zlr_total_war" ? "annex"
                    : typeId == MANDATE_WAR_ID ? "mandate" : "unknown";
                ClaimManager.SetWarCustomStr(war, WAR_GOAL, goal);
                if (!string.IsNullOrEmpty(targetCityId))
                    ClaimManager.SetWarCustomStr(war, WAR_TARGET, targetCityId);
                EnsureWarTrackingData(war, atk, def, ZhuluTime.CurrentYear);
            }
            catch { }
        }

        /// <summary>
        /// 为所有文明战争（含原版战争、叛乱、旧档存量战争）补齐年度评估基线。
        /// 只补空值，不覆盖已有开战快照。旧档第一次观测从当年起算，避免读档立即强制停战。
        /// </summary>
        public static void EnsureWarTrackingData(War war, Kingdom atk, Kingdom def, int year)
        {
            try
            {
                if (war == null) return;
                if (string.IsNullOrEmpty(ClaimManager.GetWarCustomStr(war, WAR_START)))
                    ClaimManager.SetWarCustomStr(war, WAR_START, year.ToString());
                if (string.IsNullOrEmpty(ClaimManager.GetWarCustomStr(war, WAR_SCORE)))
                    ClaimManager.SetWarCustomStr(war, WAR_SCORE, "0");
                if (atk != null)
                {
                    if (string.IsNullOrEmpty(ClaimManager.GetWarCustomStr(war, WAR_ATK_START_CITIES)))
                        ClaimManager.SetWarCustomStr(war, WAR_ATK_START_CITIES, (atk.cities != null ? atk.cities.Count : 0).ToString());
                    if (string.IsNullOrEmpty(ClaimManager.GetWarCustomStr(war, WAR_ATK_START_ARMY)))
                        ClaimManager.SetWarCustomStr(war, WAR_ATK_START_ARMY, Math.Max(0, atk.countTotalWarriors()).ToString());
                }
                if (def != null)
                {
                    if (string.IsNullOrEmpty(ClaimManager.GetWarCustomStr(war, WAR_DEF_START_CITIES)))
                        ClaimManager.SetWarCustomStr(war, WAR_DEF_START_CITIES, (def.cities != null ? def.cities.Count : 0).ToString());
                    if (string.IsNullOrEmpty(ClaimManager.GetWarCustomStr(war, WAR_DEF_START_ARMY)))
                        ClaimManager.SetWarCustomStr(war, WAR_DEF_START_ARMY, Math.Max(0, def.countTotalWarriors()).ToString());
                }
            }
            catch { }
        }

        public static string GetGoal(War war)
        {
            try { return ClaimManager.GetWarCustomStr(war, WAR_GOAL) ?? ""; } catch { return ""; }
        }

        // ═══════════════ ② 国家厌战 ═══════════════

        public static int GetWarExhaustion(string kingdomId)
        {
            Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
            if (k == null) return 0;
            return (int)KingdomDataExt.GetLong(k, KEY_EXHAUSTION, 0);
        }

        public static void AddWarExhaustion(string kingdomId, int amount)
        {
            try
            {
                Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
                if (k == null) return;
                int cur = (int)KingdomDataExt.GetLong(k, KEY_EXHAUSTION, 0);
                // 阶段3H-19：厌战速率通道——累积×rate，消退÷rate（息兵政策让消退也加速）
                try
                {
                    float r = Gameplay.ModifierPipeline.Instance.EvaluateChannelMultiplier(Gameplay.ModifierChannels.WarExhaustionRate, kingdomId, 1f);
                    if (r != 1f) amount = amount > 0 ? (int)Math.Round(amount * r) : (int)Math.Round(amount / r);
                }
                catch { }
                KingdomDataExt.SetLong(k, KEY_EXHAUSTION, Math.Max(0, Math.Min(100, cur + amount)));
            }
            catch { }
        }

        /// <summary>年度厌战结算（YearDriver 按国调用）。</summary>
        public static void TickWarExhaustion(Kingdom k, int year)
        {
            try
            {
                if (k == null || !k.isAlive()) return;
                string kId = k.id.ToString();
                bool atWar = false;
                try { var wars = k.getWars(); if (wars != null) { foreach (var _ in wars) { atWar = true; break; } } } catch { }

                int delta = 0;
                if (atWar) delta += 3;                  // 战争期间 +3/年
                try
                {
                    var tre = TreasuryManager.Instance.GetReport(kId);
                    if (tre != null && tre.DeficitYears >= 1) delta += 2; // 亏空 +2/年
                }
                catch { }
                if (!atWar) delta -= 3;                 // 和平 -3/年（2026-08-15 调参：休整期 50→0 约 17 年）
                // 稳定高 → 厌战缓解
                try
                {
                    if (StabilityManager.Instance.GetStability(kId) > 70f) delta -= 2;
                }
                catch { }
                if (delta != 0) AddWarExhaustion(kId, delta);
            }
            catch { }
        }

        /// <summary>能否主动宣战（厌战门槛；reasonRequired=是否需正当理由）。</summary>
        public static bool CanDeclareWar(string kingdomId, out bool needReason)
        {
            needReason = false;
            int ex = GetWarExhaustion(kingdomId);
            if (ex >= EXHAUSTION_DECLARE_LIMIT) return false;
            if (ex >= EXHAUSTION_REASONABLE) { needReason = true; return true; }
            return true;
        }

        // ═══════════════ ③ 战争分数（年度评估） ═══════════════

        /// <summary>
        /// 年度评估一场逐鹿战争：计算攻方净分数（正=攻方占优）。
        /// 分数 = 守方失城×15 − 攻方失城×15 + 击杀差÷2（整除截断） + 首都沦陷±20。
        /// </summary>
        public static int EvaluateWarScore(War war)
        {
            try
            {
                Kingdom atk = war.getMainAttacker();
                Kingdom def = war.getMainDefender();
                if (atk == null || def == null) return 0;

                int atkStart = 0, defStart = 0;
                string s1 = ClaimManager.GetWarCustomStr(war, WAR_ATK_START_CITIES);
                string s2 = ClaimManager.GetWarCustomStr(war, WAR_DEF_START_CITIES);
                int.TryParse(s1 ?? "", out atkStart);
                int.TryParse(s2 ?? "", out defStart);

                int atkNow = atk.cities != null ? atk.cities.Count : 0;
                int defNow = def.cities != null ? def.cities.Count : 0;

                int score = 0;
                // P1-A2（2026-08-23）：分量计算抽到纯函数层 WarScoreRules——无状态可单测，
                // 时间惩罚只在本方法按年施加一次（AnnualTimePenalty），杜绝累计误差复发。
                // 守方失城（攻方得分）
                int cityGain = WarScoreRules.CitiesGained(defStart, defNow);
                score += cityGain;
                // 攻方失城（攻方扣分）
                int cityLoss = WarScoreRules.CitiesLost(atkStart, atkNow); // 已带符号（≤0）
                score += cityLoss;
                // 击杀差（2026-08-15 调参定稿口径：(守亡−攻亡)/2）
                int killDelta = 0;
                try
                {
                    int deadA = war.data != null ? war.data.dead_attackers : 0;
                    int deadD = war.data != null ? war.data.dead_defenders : 0;
                    killDelta = WarScoreRules.KillComponent(deadA, deadD);
                    score += killDelta;
                }
                catch { }
                // 首都沦陷
                int capitalDelta = 0;
                try
                {
                    bool atkHoldsDefCap = false, defHoldsAtkCap = false;
                    if (def.capital != null)
                    {
                        var capOwner = ClaimManager.GetCityKingdom(def.capital);
                        if (capOwner != null && capOwner == atk) atkHoldsDefCap = true;
                    }
                    if (atk.capital != null)
                    {
                        var capOwner = ClaimManager.GetCityKingdom(atk.capital);
                        if (capOwner != null && capOwner == def) defHoldsAtkCap = true;
                    }
                    capitalDelta = WarScoreRules.CapitalComponent(atkHoldsDefCap, defHoldsAtkCap);
                    score += capitalDelta;
                }
                catch { }

                // 存档分（含时间厌战：战争越久分数向白和收敛——每年 -1，仅此一处施加）
                string sc = ClaimManager.GetWarCustomStr(war, WAR_SCORE);
                int old = 0; int.TryParse(sc ?? "", out old);
                score = old + score + WarScoreRules.AnnualTimePenalty;
                ClaimManager.SetWarCustomStr(war, WAR_SCORE, score.ToString());
                // 分数构成透明化（占城|失城|击杀|首都|时间——战争详情页数据源；失城列已带符号）
                try { ClaimManager.SetWarCustomStr(war, WAR_SCORE_DETAIL, $"{cityGain}|{cityLoss}|{killDelta}|{capitalDelta}|{WarScoreRules.AnnualTimePenalty}"); } catch { }
                return score;
            }
            catch { return 0; }
        }

        // ═══════════════ ⑤ 全面战争 ═══════════════

        /// <summary>全面战争五重门槛判定（2026-08-15 用户确认）：国力碾压/宣称≥2/厌战<30/世仇/20年冷却。</summary>
        public static bool CanTotalWar(Kingdom atk, Kingdom def, int year)
        {
            try
            {
                if (atk == null || def == null || atk == def) return false;
                if (!atk.isAlive() || !def.isAlive()) return false;
                string atkId = atk.id.ToString();
                string defId = def.id.ToString();

                // ① 国力碾压：攻方国力点 ≥ 守方×1.5
                int atkPp = ZhuluTierService.GetPowerPoints(atk);
                int defPp = ZhuluTierService.GetPowerPoints(def);
                if (defPp <= 0 || atkPp * 100 < defPp * TOTAL_WAR_POWER_RATIO) return false;

                // ② 领土诉求：对守方宣称 ≥2
                if (ClaimManager.Instance.CountClaimsAgainst(atk, def) < TOTAL_WAR_CLAIMS) return false;

                // ③ 国内稳定：攻方厌战 <30
                if (GetWarExhaustion(atkId) >= TOTAL_WAR_EXHAUSTION) return false;

                // ④ 历史正当：近 50 年交战 ≥2 次（用 past_wars 数据）
                int pastWars = CountPastWars(atk, def, 50);
                if (pastWars < TOTAL_WAR_PAST_WARS) return false;

                // ⑤ 全局冷却 20 年（记录在攻方）
                long last = KingdomDataExt.GetLong(atk, "last_total_war", 0);
                if (last > 0 && year - (int)last < TOTAL_WAR_COOLDOWN) return false;

                return true;
            }
            catch { return false; }
        }

        /// <summary>近 N 年双方交战次数（用原版 kingdom 的 past_wars 无法按年过滤——简化：past_rulers 不行；
        /// 用双方史册/或战争记录——先统计当前活跃+历史战争（Kingdom 无历史战争列表，用 map 级？）
        /// 简化实现：统计 past_wars 字段（KingdomData.past_wars? 不存在）——退回：近 50 年交战 ≥2 次
        /// 用双方 renown 战争事件近似——先实现为"曾交战过即可"（>=1 次近 50 年记录用世界史册近似），
        /// 后续压测调参。此处用 World.world.wars 历史？——简化：统计双方战争史（原版无公开 API），
        /// 先用"是否曾为世仇"替代：近 50 年战争次数统计走逐鹿史册（Chronicle WarDeclared 计数）。
        private static int CountPastWars(Kingdom atk, Kingdom def, int years)
        {
            try
            {
                // 用逐鹿史册：双方 WarDeclared 条目计数（近 years 年）
                int count = 0;
                var chronicles = ChronicleManager.Instance.GetWorldMajorChronicle();
                if (chronicles == null) return 0;
                int cur = ZhuluTime.CurrentYear;
                string aId = atk.id.ToString();
                string dId = def.id.ToString();
                foreach (var e in chronicles)
                {
                    if (e == null || e.Type != ChronicleEventType.WarDeclared) continue;
                    if (cur - e.Year > years) continue;
                    // 事件方是攻方或守方（用 KingdomId 匹配；战争事件 KingdomId=攻方）
                    if (e.KingdomId == aId || e.KingdomId == dId) count++;
                }
                return count;
            }
            catch { return 0; }
        }

        /// <summary>全面战争 AI 年度触发（YearDriver 全局年度调用）。</summary>
        public static void TickTotalWarAI(int year)
        {
            try
            {
                if (World.world?.kingdoms?.list == null) return;
                foreach (Kingdom atk in World.world.kingdoms.list)
                {
                    if (atk == null || !atk.isAlive()) continue;
                    // 已经在战争中不触发
                    bool atWar = false;
                    try { var wars = atk.getWars(); if (wars != null) { foreach (var _ in wars) { atWar = true; break; } } } catch { }
                    if (atWar) continue;
                    foreach (Kingdom def in World.world.kingdoms.list)
                    {
                        if (def == null || !def.isAlive() || def == atk) continue;
                        if (CanTotalWar(atk, def, year))
                        {
                            // 2026-08-19 串联：执政党战争轴修正 AI 宣战概率（主战党 ×1.3 / 主和党 ×0.7）
                            float chance = TOTAL_WAR_AI_CHANCE * FactionPoliticsManager.GetWarAppetiteFactor(atk);
                            if (UnityEngine.Random.value < chance)
                            {
                                War war = WarGateway.DeclareWar(atk, def, WarfareManager.TotalWar, "全面战争");
                                if (war != null)
                                {
                                    KingdomDataExt.SetLong(atk, "last_total_war", year);
                                    // 天下震惊：攻方对列国外交好感 -15（穷兵黩武）
                                    try
                                    {
                                        var pipeline = Gameplay.ModifierPipeline.Instance;
                                        pipeline.RegisterPlan(new Gameplay.ModifierPlan
                                        {
                                            SourceId = "total_war_notoriety_" + atk.id + "_" + year,
                                            TargetKingdomId = atk.id.ToString(),
                                            Channel = Gameplay.ModifierChannels.DiplomaticOpinionBonus,
                                            Additive = -15f,
                                            Multiplicative = 1.0f,
                                            Reason = "穷兵黩武，天下侧目（全面战争）"
                                        });
                                    }
                                    catch { }
                                    ZhuluContext.Instance.LogInfo($"[TotalWar] {atk.name} 发动全面战争！目标 {def.name}");
                                    try { WorldTip.showNow($"【{atk.name}】兴兵伐国，发动全面战争！天下震动！", false, "top", 8f, UI.ZhuluUIStyle.Hex.Negative); } catch { }
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        // ═══════════════ ③b 年度评估入口（YearDriver 全局年度调用） ═══════════════

        /// <summary>
        /// 年度评估所有进行中的文明战争：逐鹿自定义战争走目标结算，
        /// 原版/其他 Mod 战争走通用厌战和谈，叛乱与夺天命使用更高门槛的专用线。
        /// </summary>
        public static void TickAllWars(int year)
        {
            try
            {
                if (World.world?.wars == null) return;
                foreach (var war in World.world.wars) // WarManager 实现 IEnumerable
                {
                    try
                    {
                        if (war == null || war.hasEnded()) continue;
                        string typeId = war.data?.war_type ?? "";
                        if (string.IsNullOrEmpty(typeId))
                        {
                            try { typeId = war.getAsset()?.id ?? ""; } catch { }
                        }

                        Kingdom atk = war.getMainAttacker();
                        Kingdom def = war.getMainDefender();
                        // 只处理两个文明国家之间的战争，不碰灾害/动物/特殊非文明战争。
                        if (atk == null || def == null || !atk.isCiv() || !def.isCiv()) continue;

                        EnsureWarTrackingData(war, atk, def, year);
                        ApplyAnnualWarPressure(war, atk, def, year);

                        // 夺天命之战：独立评估线（天命动摇/据都/问鼎成败——不走通用分数结算）
                        if (typeId == MANDATE_WAR_ID)
                        {
                            if (EvaluateMandateWar(war, year)) continue;
                            continue; // 未终结继续战斗（分数已在 EvaluateMandateWar 之外由下面求和线处理？——不，问鼎战不打分数）
                        }

                        // P2-30 问罪之战：独立评估线（宗主胜/附庸撑住/师老两清）；
                        // 未终结则落入下方通用分数结算继续累积。
                        if (typeId == "zlr_punitive_war")
                        {
                            if (EvaluatePunitiveWar(war, year)) continue;
                        }

                        // 叛乱战争求和非常困难：只有长期僵持且双方同时枯竭才允许和局。
                        if (typeId == "rebellion" || typeId == "inspire")
                        {
                            EvaluateRebellionPeace(war, atk, def, year);
                            continue;
                        }

                        bool isZhuluWar = typeId == "zlr_claim_war" || typeId == "zlr_vassal_war"
                            || typeId == "zlr_independence_war" || typeId == "zlr_total_war";
                        if (!isZhuluWar)
                        {
                            EvaluateGenericPeace(war, atk, def, typeId, year);
                            continue;
                        }

                        int score = EvaluateWarScore(war);

                        // D1 战争年度插桩（2026-10-06）：活跃逐鹿战争一行/年（节奏/厌战/兵力比曲线）
                        try
                        {
                            string wsTick = ClaimManager.GetWarCustomStr(war, WAR_START);
                            int.TryParse(wsTick ?? "", out int tickStartYear);
                            int tickA = 0, tickD = 0;
                            try { tickA = atk.countTotalWarriors(); } catch { }
                            try { tickD = def.countTotalWarriors(); } catch { }
                            ZhuluTrace.Write("war_trace.csv",
                                $"war_tick,{year},{war.getID()},{typeId},{score},{GetWarExhaustion(atk.id.ToString())},{GetWarExhaustion(def.id.ToString())},{tickA},{tickD},{tickStartYear}");
                        }
                        catch { }

                        // 守方灭亡 → 攻方大胜
                        if (def == null || !def.isAlive())
                        {
                            if (!war.hasEnded()) World.world.wars.endWar(war, WarWinner.Attackers);
                            // 阶段2U：守方灭亡世界播报（原静默）
                            if (atk != null && atk.isAlive())
                            {
                                try { WorldTip.showNow($"【{atk.name}】灭【{def?.name ?? "敌国"}】，社稷为墟！", false, "top", 8f, UI.ZhuluUIStyle.Hex.Negative); } catch { }
                            }
                            continue;
                        }
                        if (atk == null || !atk.isAlive())
                        {
                            if (!war.hasEnded()) World.world.wars.endWar(war, WarWinner.Defenders);
                            continue;
                        }

                        // 结算档位
                        // 阶段2U 修复：守方大胜分支（原只处理攻方 ≥75，攻方被反推 5 城的战争永不结束）
                        if (score <= -SCORE_ANNEX)
                        {
                            if (!war.hasEnded()) World.world.wars.endWar(war, WarWinner.Defenders);
                            ZhuluContext.Instance.LogInfo($"[WarEval] 守方大胜: {def.name} 反败为胜，{atk.name} 惨败求和（分数 {score}）");
                            try { WorldTip.showNow($"【{def.name}】反败为胜，【{atk.name}】惨败请和！", false, "top", 6f, UI.ZhuluUIStyle.Hex.Positive); } catch { }
                            continue;
                        }
                        if (score >= SCORE_ANNEX)
                        {
                            // 大胜：按目标（宣称=吞并宣称国？不——宣称战大胜=攻方控制宣称城即胜；全面战=灭国）
                            if (typeId == "zlr_total_war")
                            {
                                if (!war.hasEnded()) World.world.wars.endWar(war, WarWinner.Attackers);
                                ZhuluContext.Instance.LogInfo($"[WarEval] 全面战争大胜: {atk.name} 灭 {def.name}");
                                // 阶段2U：灭国世界播报（原只写日志——天下大事不可无闻）
                                try { WorldTip.showNow($"【{atk.name}】灭【{def.name}】，社稷为墟！", false, "top", 8f, UI.ZhuluUIStyle.Hex.Negative); } catch { }
                                continue;
                            }
                            // 宣称/附庸/独立：攻方拿到目标即可结束（转由原目标判定——分数已足够，直接攻方胜）
                            // 2026-08-16 回归修复：附庸战必须收编（原每帧 CheckVassalWarEnd 的 MakeVassal 在
                            // 2B-17 空壳化后丢失——年度评估只 endWar 导致"直接占领"而非收编附庸）
                            if (typeId == "zlr_vassal_war")
                            {
                                try { VassalManager.Instance.MakeVassal(atk, def); } catch { }
                            }
                            if (!war.hasEnded()) World.world.wars.endWar(war, WarWinner.Attackers);
                            continue;
                        }
                        if (score >= SCORE_WIN && typeId == "zlr_claim_war")
                        {
                            // 宣称战胜：若宣称城已离开守方 → 攻方胜（城市易主判定在下方旧逻辑仍保留？——年度替代）
                            Kingdom cityOwner = null;
                            string targetId = ClaimManager.GetWarCustomStr(war, WAR_TARGET);
                            City tc = !string.IsNullOrEmpty(targetId) ? ClaimManager.FindCityById(targetId) : null;
                            if (tc != null) cityOwner = ClaimManager.GetCityKingdom(tc);
                            if (cityOwner == atk || (cityOwner != null && cityOwner != def))
                            {
                                if (!war.hasEnded()) World.world.wars.endWar(war, cityOwner == atk ? WarWinner.Attackers : WarWinner.Nobody);
                                continue;
                            }
                        }

                        // 攻方深陷求和（2026-08-19 意愿权重制：战力/战况/厌战/年限四维 ≥70 才止损——
                        // 挑起战争者更难脱身；条款按守方优势度分级：赔款 → 割城+赔款）
                        int atkWill = GetPeaceWillingness(atk, def, war, -score, year);
                        if (atkWill >= PEACE_WILLINGNESS_ATTACKER)
                        {
                            string atkTerms = ApplyWarSettlement(war, def, atk, -score, null);
                            World.world.wars.endWar(war, WarWinner.Defenders);
                            ZhuluContext.Instance.LogInfo($"[WarEval] 攻方请和: {atk.name} 师老兵疲，向 {def.name} {atkTerms}（分数 {score}，意愿 {atkWill}）");
                            try { WorldTip.showNow($"【{atk.name}】久战师老，向【{def.name}】请和：{atkTerms}！", false, "top", 6f, UI.ZhuluUIStyle.Hex.Azure); } catch { }
                            continue;
                        }

                        // 守方求和（2026-08-19 意愿权重制：≥60 才屈膝；条款按攻方优势度分级：
                        // 轻=赔款 / 中=割城+赔款（宣称战割宣称城）/ 重=割城+赔款半库）
                        int defWill = GetPeaceWillingness(def, atk, war, score, year);
                        if (defWill >= PEACE_WILLINGNESS_DEFENDER)
                        {
                            string goal = GetGoal(war);
                            // 附庸战求和 = 臣服；独立战求和 = 解除附庸（非割地赔款线）
                            // 批次11修复①：收编结果必须真实——MakeVassal 被防链规则拒绝时降级为割地赔款，
                            // 文本与实际效果严格一致（旧版弹"臣服"却可能静默不生效）
                            bool vassalized = false;
                            bool released = false;
                            if (goal == "vassalize")
                            {
                                try { vassalized = VassalManager.Instance.MakeVassal(atk, def); } catch { }
                            }
                            if (goal == "independent")
                            {
                                try { VassalManager.Instance.ReleaseVassal(def, atk); released = true; } catch { }
                            }

                            string terms;
                            if (goal == "vassalize") terms = vassalized ? "臣服为附庸" : ApplyWarSettlement(war, atk, def, score, null);
                            else if (goal == "independent") terms = released ? "承认独立" : ApplyWarSettlement(war, atk, def, score, null);
                            else
                            {
                                // 宣称战：割城首选 = 宣称目标城
                                City preferred = null;
                                if (goal == "claim_city")
                                {
                                    string targetId = ClaimManager.GetWarCustomStr(war, WAR_TARGET);
                                    if (!string.IsNullOrEmpty(targetId)) preferred = ClaimManager.FindCityById(targetId);
                                }
                                terms = ApplyWarSettlement(war, atk, def, score, preferred);
                            }
                            World.world.wars.endWar(war, WarWinner.Attackers);
                            int defCities = def.cities != null ? def.cities.Count : 0;
                            ZhuluContext.Instance.LogInfo($"[WarEval] 守方求和: {def.name} {terms}（分数 {score}，意愿 {defWill}，{defCities}城）");
                            try { WorldTip.showNow($"【{def.name}】力不能支，{terms}！", false, "top", 6f, UI.ZhuluUIStyle.Hex.Vermilion); } catch { }
                            continue;
                        }

                        // 白和（P1-A1 概率化，2026-08-23）：低分僵局按 AW3 式公式逐年掷确定性骰；
                        // elapsed ≥ PEACE_YEARS_HARD 强制收场（防极端低概率永战——保留旧硬触发的兜底语义）
                        string sy = ClaimManager.GetWarCustomStr(war, WAR_START);
                        int startYear = 0; int.TryParse(sy ?? "", out startYear);
                        if (Math.Abs(score) < SCORE_MINOR && startYear > 0)
                        {
                            int elapsed = year - startYear;
                            bool forceEnd = elapsed >= PEACE_YEARS_HARD;
                            bool diceHit = false;
                            if (!forceEnd)
                            {
                                int atkArmy = 0, defArmy = 0;
                                try { atkArmy = atk.countTotalWarriors(); } catch { }
                                try { defArmy = def.countTotalWarriors(); } catch { }
                                float ratio = defArmy > 0 ? (float)atkArmy / defArmy : (atkArmy > 0 ? 9f : 1f);
                                float axisAvg = 0.5f;
                                try
                                {
                                    float a1 = FactionPoliticsManager.GetRulingStanceAxis(atk, FactionManager.ST_WAR);
                                    float a2 = FactionPoliticsManager.GetRulingStanceAxis(def, FactionManager.ST_WAR);
                                    axisAvg = (a1 + a2) * 0.5f;
                                }
                                catch { }
                                float p = WarScoreRules.WhitePeaceProbability(elapsed, ratio, axisAvg);
                                diceHit = DeterministicRandom.Roll01(war.getID(), year, "whitepeace") < p;
                            }
                            if (forceEnd || diceHit)
                            {
                                ZhuluContext.Instance.LogInfo($"[WarEval] {(forceEnd ? "久战不决强制白和" : "白和")}（elapsed={elapsed}, score={score}）: {atk.name} vs {def.name}");
                                if (!war.hasEnded()) World.world.wars.endWar(war, WarWinner.Peace);
                                continue;
                            }
                        }
                    }
                    catch { }
                }

                // 2026-08-19 方案5：AI 年度问鼎扫描（天命国稳定时强邻觊觎）
                TryStartMandateWar(year);
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[WarEval] TickAllWars 异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 每场战争每年只执行一次的附加厌战：长期动员、当年新增伤亡与军力枯竭。
        /// 国家年度基础值仍由 TickWarExhaustion 处理；本处只表达“这一场仗打得有多惨”。
        /// </summary>
        private static void ApplyAnnualWarPressure(War war, Kingdom atk, Kingdom def, int year)
        {
            try
            {
                string lastYearRaw = ClaimManager.GetWarCustomStr(war, WAR_LAST_PRESSURE_YEAR);
                int lastYear = 0;
                int.TryParse(lastYearRaw ?? "", out lastYear);
                if (!string.IsNullOrEmpty(lastYearRaw) && lastYear == year) return;
                // 阶段3H-11：失城大国厌战增速（年+2）——丢城的一方民怨沸腾快于占领方，
                // 天命国被夺城越多先撑不住（也为"压倒战果"提供厌战侧呼应）。
                ClaimManager.SetWarCustomStr(war, WAR_LAST_PRESSURE_YEAR, year.ToString());

                int startYear = GetWarStartYear(war);
                int elapsed = startYear > 0 ? Math.Max(0, year - startYear) : 0;
                int atkDelta = elapsed >= 15 ? 3 : elapsed >= 10 ? 2 : elapsed >= 5 ? 1 : 0;
                int defDelta = atkDelta;

                int deadAtk = war.data != null ? Math.Max(0, war.data.dead_attackers) : 0;
                int deadDef = war.data != null ? Math.Max(0, war.data.dead_defenders) : 0;
                string lastAtkRaw = ClaimManager.GetWarCustomStr(war, WAR_LAST_DEAD_ATTACKERS);
                string lastDefRaw = ClaimManager.GetWarCustomStr(war, WAR_LAST_DEAD_DEFENDERS);
                int lastDeadAtk = 0, lastDeadDef = 0;
                int.TryParse(lastAtkRaw ?? "", out lastDeadAtk);
                int.TryParse(lastDefRaw ?? "", out lastDeadDef);

                // 旧档战争首次观测只建立伤亡基线，不把历史累计一次性重复计入。
                if (!string.IsNullOrEmpty(lastAtkRaw)) atkDelta += CasualtyPressure(Math.Max(0, deadAtk - lastDeadAtk));
                if (!string.IsNullOrEmpty(lastDefRaw)) defDelta += CasualtyPressure(Math.Max(0, deadDef - lastDeadDef));
                ClaimManager.SetWarCustomStr(war, WAR_LAST_DEAD_ATTACKERS, deadAtk.ToString());
                ClaimManager.SetWarCustomStr(war, WAR_LAST_DEAD_DEFENDERS, deadDef.ToString());

                if (IsArmyDepleted(war, atk, true, 35)) atkDelta += 3;
                if (IsArmyDepleted(war, def, false, 35)) defDelta += 3;

                // 阶段3H-11：失城加速——丢城方年+2（国土沦丧，民怨沸腾快于占领方；
                // 天命国被夺城越多先撑不住，与"压倒战果强制夺鼎"呼应）
                try
                {
                    int atkStartC = 0, defStartC = 0;
                    int.TryParse(ClaimManager.GetWarCustomStr(war, WAR_ATK_START_CITIES) ?? "", out atkStartC);
                    int.TryParse(ClaimManager.GetWarCustomStr(war, WAR_DEF_START_CITIES) ?? "", out defStartC);
                    int atkNowC = atk.cities != null ? atk.cities.Count : 0;
                    int defNowC = def.cities != null ? def.cities.Count : 0;
                    if (atkStartC > 0 && atkNowC < atkStartC) atkDelta += 2;
                    if (defStartC > 0 && defNowC < defStartC) defDelta += 2;
                }
                catch { }

                if (atkDelta > 0) AddWarExhaustion(atk.id.ToString(), atkDelta);
                if (defDelta > 0) AddWarExhaustion(def.id.ToString(), defDelta);
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[WarExhaust] 战争压力结算失败: {ex.Message}");
            }
        }

        private static int CasualtyPressure(int newDeaths)
        {
            if (newDeaths >= 250) return 4;
            if (newDeaths >= 100) return 3;
            if (newDeaths >= 30) return 2;
            if (newDeaths >= 10) return 1;
            return 0;
        }

        private static int GetWarStartYear(War war)
        {
            int startYear = 0;
            try { int.TryParse(ClaimManager.GetWarCustomStr(war, WAR_START) ?? "", out startYear); }
            catch { }
            return startYear;
        }

        private static int GetStartArmy(War war, bool attacker)
        {
            int value = 0;
            try
            {
                int.TryParse(ClaimManager.GetWarCustomStr(war,
                    attacker ? WAR_ATK_START_ARMY : WAR_DEF_START_ARMY) ?? "", out value);
            }
            catch { }
            return Math.Max(0, value);
        }

        private static bool IsArmyDepleted(War war, Kingdom side, bool attacker, int remainingPercent)
        {
            try
            {
                int start = GetStartArmy(war, attacker);
                if (start <= 0) return side.countTotalWarriors() <= 0;
                int now = Math.Max(0, side.countTotalWarriors());
                return now * 100 <= start * remainingPercent;
            }
            catch { return false; }
        }

        /// <summary>
        /// 通用文明战争求和。不擅自割城/收附庸，只在明确溃败或长期双方厌战时终止战争。
        /// 这使旧存档、原版类型与其他 Mod 已建战争也能进入停战机制。
        /// </summary>
        private static bool EvaluateGenericPeace(War war, Kingdom atk, Kingdom def, string typeId, int year)
        {
            try
            {
                if (!atk.isAlive()) { World.world.wars.endWar(war, WarWinner.Defenders); return true; }
                if (!def.isAlive()) { World.world.wars.endWar(war, WarWinner.Attackers); return true; }

                int elapsed = Math.Max(0, year - GetWarStartYear(war));
                if (elapsed < 3) return false;
                int score = EvaluateWarScore(war);
                int atkWill = GetPeaceWillingness(atk, def, war, -score, year);
                int defWill = GetPeaceWillingness(def, atk, war, score, year);
                int atkEx = GetWarExhaustion(atk.id.ToString());
                int defEx = GetWarExhaustion(def.id.ToString());

                // 大败方主动认输；门槛比逐鹿目标战争更高，避免频繁短战。
                if (score >= SCORE_ANNEX && defWill >= 75)
                {
                    World.world.wars.endWar(war, WarWinner.Attackers);
                    ZhuluContext.Instance.LogInfo($"[Peace] {def.name} 战局崩坏，向 {atk.name} 认输（{typeId}, {elapsed}年, score={score}）");
                    return true;
                }
                if (score <= -SCORE_ANNEX && atkWill >= 85)
                {
                    World.world.wars.endWar(war, WarWinner.Defenders);
                    ZhuluContext.Instance.LogInfo($"[Peace] {atk.name} 征伐惨败，向 {def.name} 认输（{typeId}, {elapsed}年, score={score}）");
                    return true;
                }

                bool stalemate = Math.Abs(score) < SCORE_MINOR;
                bool mutualWill = atkWill >= 60 && defWill >= 60;
                bool exhaustedStalemate = elapsed >= 8 && stalemate && mutualWill;
                bool prolongedCollapse = elapsed >= 12 && Math.Abs(score) < SCORE_WIN
                    && atkEx >= 60 && defEx >= 60;
                bool absoluteLimit = elapsed >= 18 && atkEx >= 75 && defEx >= 75;
                if (!exhaustedStalemate && !prolongedCollapse && !absoluteLimit) return false;

                World.world.wars.endWar(war, WarWinner.Peace);
                ZhuluContext.Instance.LogInfo($"[Peace] 久战俱疲，{atk.name} 与 {def.name} 议和（{typeId}, {elapsed}年, score={score}, 厌战={atkEx}/{defEx}）");
                try { WorldTip.showNow($"【{atk.name}】与【{def.name}】久战俱疲，遂罢兵议和。", false, "top", 6f, UI.ZhuluUIStyle.Hex.Azure); } catch { }
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 叛乱困难和谈：不满 10 年不谈；常规情况要求至少 12 年、双方高厌战、
        /// 战局僵持且双方军力都低于开战时 60%。和局表示旧国默认叛军事实独立。
        /// </summary>
        private static bool EvaluateRebellionPeace(War war, Kingdom atk, Kingdom def, int year)
        {
            try
            {
                if (!atk.isAlive()) { World.world.wars.endWar(war, WarWinner.Defenders); return true; }
                if (!def.isAlive()) { World.world.wars.endWar(war, WarWinner.Attackers); return true; }

                int elapsed = Math.Max(0, year - GetWarStartYear(war));
                if (elapsed < 10) return false;
                int score = EvaluateWarScore(war);
                int atkEx = GetWarExhaustion(atk.id.ToString());
                int defEx = GetWarExhaustion(def.id.ToString());
                bool bothDepleted = IsArmyDepleted(war, atk, true, 60)
                    && IsArmyDepleted(war, def, false, 60);

                bool hardPeace = elapsed >= 12 && Math.Abs(score) < 25
                    && atkEx >= 60 && defEx >= 60 && bothDepleted;
                bool veryLongPeace = elapsed >= 15 && Math.Abs(score) < 35
                    && atkEx >= 70 && defEx >= 70;
                bool absoluteLimit = elapsed >= 20 && Math.Abs(score) < SCORE_WIN
                    && atkEx >= 80 && defEx >= 80;
                if (!hardPeace && !veryLongPeace && !absoluteLimit) return false;

                World.world.wars.endWar(war, WarWinner.Peace);
                ZhuluContext.Instance.LogInfo($"[RebellionPeace] 叛乱久战无力，{atk.name} 与 {def.name} 罢兵（{elapsed}年, score={score}, 厌战={atkEx}/{defEx}）");
                try { WorldTip.showNow($"【{atk.name}】与【{def.name}】相持十余载，人疲兵尽，遂罢战议和。", false, "top", 8f, UI.ZhuluUIStyle.Hex.Azure); } catch { }
                return true;
            }
            catch { return false; }
        }

        // （2026-08-19 意愿权重制：旧 ShouldDefenderSueForPeace / ShouldAttackerSueForPeace /
        //  GetPeaceText 已由 GetPeaceWillingness + ApplyWarSettlement 取代——删除防误用）

        /// <summary>
        /// 2026-08-20 割城挑选（外部思路借鉴重写）：① 胜方有宣称的败方城优先（宣称战略价值落地）；
        /// ② 无宣称 → 离胜方首都最近的非首都城（割地接壤，符合"攻取邻土"史实）。
        /// 无合适城返回 null → 降级纯赔款。
        /// </summary>
        private static City PickCedeCity(Kingdom def, Kingdom winner)
        {
            try
            {
                if (def?.cities == null || def.cities.Count == 0) return null;
                // 候选集：非首都城
                var candidates = new List<City>();
                foreach (var c in def.cities)
                {
                    if (c == null) continue;
                    try
                    {
                        if (def.capital != null && c.getID() == def.capital.getID()) continue; // 不割首都
                    }
                    catch { }
                    candidates.Add(c);
                }
                if (candidates.Count == 0) return null;

                // ① 宣称城优先：胜方对该城有未过期宣称 → 割之（名正言顺）
                try
                {
                    var claims = ClaimManager.Instance.GetClaims(winner);
                    if (claims != null && claims.Count > 0)
                    {
                        var claimSet = new HashSet<string>();
                        foreach (var cl in claims)
                        {
                            if (cl == null || string.IsNullOrEmpty(cl.CityId)) continue;
                            if (cl.ExpireYear <= 0 || cl.ExpireYear >= ZhuluTime.CurrentYear)
                                claimSet.Add(cl.CityId);
                        }
                        if (claimSet.Count > 0)
                        {
                            foreach (var c in candidates)
                            {
                                try { if (claimSet.Contains(c.getID().ToString())) return c; } catch { }
                            }
                        }
                    }
                }
                catch { }

                // ② 无宣称 → 离胜方首都最近（getTile 瓦片坐标直线距离）
                try
                {
                    var winnerTile = winner?.capital?.getTile();
                    if (winnerTile != null)
                    {
                        float wx = winnerTile.x;
                        float wy = winnerTile.y;
                        City nearest = null;
                        float nearestDist = float.MaxValue;
                        foreach (var c in candidates)
                        {
                            var t = c?.getTile();
                            if (t == null) continue;
                            float dx = t.x - wx;
                            float dy = t.y - wy;
                            float dist = dx * dx + dy * dy;
                            if (dist < nearestDist) { nearestDist = dist; nearest = c; }
                        }
                        if (nearest != null) return nearest;
                    }
                }
                catch { }

                return candidates[candidates.Count - 1]; // 兜底：最后一座非首都城
            }
            catch { return null; }
        }

        // ═══════════════ ⑥ 求和意愿权重制 + 割城/赔款分级结算（2026-08-19 方案4+6）═══════════════
        // 旧症结：① 求和条件单薄（分数+兵力比），开战 2 年即割地求和，"一求就同意"；
        // ② "赔款息兵"只弹文案不动账——文本与效果断裂；③ 割城未按严重度分级。
        // 新制：求和意愿（战力/战况/厌战/年限四维加权 0-100）门槛制 + 轻中重三级条款（赔款→割城→割城+赔款）。

        /// <summary>守方求和意愿门槛（≥60 才屈膝）。</summary>
        public const int PEACE_WILLINGNESS_DEFENDER = 60;
        /// <summary>攻方求和意愿门槛（≥70——挑起战争者止损更难）。</summary>
        public const int PEACE_WILLINGNESS_ATTACKER = 70;

        /// <summary>
        /// 求和意愿（0-100，越高越想求和）：
        /// 战力劣势(0-40) + 战况恶化(0-30) + 厌战(0-30) + 年限修正(-15~+25) + 执政党立场。
        /// 战争 &lt;2 年直接 0（血气正盛，仗刚开打无人求和——用户反馈"一求就同意"核心矫治）。
        /// </summary>
        public static int GetPeaceWillingness(Kingdom sueSide, Kingdom otherSide, War war, int scoreAgainstSue, int year)
        {
            try
            {
                if (sueSide == null || otherSide == null || war == null) return 0;

                int startYear = 0;
                int.TryParse(ClaimManager.GetWarCustomStr(war, WAR_START) ?? "", out startYear);
                int elapsed = startYear > 0 ? year - startYear : 0;
                if (elapsed < 2) return 0; // 开战不满 2 年：不求和

                int w = 0;
                // ① 战力劣势（0-40）
                int myArmy = sueSide.countTotalWarriors();
                int otherArmy = otherSide.countTotalWarriors();
                if (otherArmy > 0)
                {
                    float ratio = (float)myArmy / otherArmy;
                    if (ratio < 0.35f) w += 40;
                    else if (ratio < 0.5f) w += 25;
                    else if (ratio < 0.7f) w += 10;
                }
                else if (myArmy <= 0) w += 40; // 全军覆没

                // ①b P1-A1（2026-08-23）僵持加成（AW3 僵持因子移植）：兵力相持不下(0.8~1.25)
                // → 双方都更想体面收场 +10——补齐"谁也吃不掉谁却永远打下去"的死角
                if (otherArmy > 0 && myArmy > 0)
                    w += WarScoreRules.PeaceWillingnessStalemateBonus((float)myArmy / otherArmy);

                // ② 战况恶化（0-30）：对我不利的战争分数
                if (scoreAgainstSue >= 60) w += 30;
                else if (scoreAgainstSue >= 40) w += 20;
                else if (scoreAgainstSue >= 20) w += 10;

                // ③ 厌战（0-30）：高厌战必须真正推动长期战争走向停战。
                int exh = GetWarExhaustion(sueSide.id.ToString());
                if (exh >= 85) w += 30;
                else if (exh >= 70) w += 25;
                else if (exh >= 55) w += 20;
                else if (exh >= 40) w += 12;
                else if (exh >= 25) w += 5;

                // ④ 年限：早期不轻和；10 年后厌战意愿显著增长。
                if (elapsed < 5) w -= 15;
                else if (elapsed >= 15) w += 25;
                else if (elapsed >= 10) w += 15;
                else if (elapsed >= 6) w += 5;

                // ⑤ 2026-08-19 串联：执政党战争轴修正——主和党（≤0.3）执政→求和意愿 +10；
                // 主战党（≥0.7）执政→求和意愿 -10（党争直接影响战争走向）
                try
                {
                    float axis = FactionPoliticsManager.GetRulingStanceAxis(sueSide, FactionManager.ST_WAR);
                    if (axis <= 0.3f) w += 10;
                    else if (axis >= 0.7f) w -= 10;
                }
                catch { }

                return Math.Max(0, Math.Min(100, w));
            }
            catch { return 0; }
        }

        /// <summary>
        /// 战败条款分级结算（方案6）：按优势度（scoreAgainstLoser）分三级——
        /// 轻（&lt;50）纯赔款 30% / 中（50-74）割城+赔款 30% / 重（≥75）割城+赔款 50%。
        /// 保护：败方仅 1 城不割（转纯赔款）；首都永不割；赔款真转账（mod 国库）。
        /// 返回条款文本（与实际效果严格一致——用于日志/弹窗/史册）。
        /// </summary>
        public static string ApplyWarSettlement(War war, Kingdom winner, Kingdom loser,
            int scoreAgainstLoser, City preferredCedeCity)
        {
            try
            {
                if (winner == null || loser == null) return "罢兵休战";
                // 2026-08-19 修复双重赔款：本结算已含割城/赔款，打标防止 EndWarPostfix.HandleWarReparation
                // 再按"20+智外×2+死亡×2"公式二次扣款（败方实际支出远超条款文本——用户实锤路径）
                try { ClaimManager.SetWarCustomStr(war, WAR_SETTLED, "1"); } catch { }

                // 2026-08-19 翻身勾销旧债：上次战争败者今番翻胜 → 其对今番败者未偿清的旧赔款一笔勾销
                try
                {
                    string winnerId = winner.id.ToString();
                    if (_reparations.TryGetValue(winnerId, out var oldDebt) && oldDebt.ReceiverId == loser.id.ToString() && oldDebt.Remaining > 0)
                    {
                        _reparations.Remove(winnerId);
                        string debtText = $"【{winner.name}】翻胜旧仇，先前战败未偿清的 {oldDebt.Remaining} 金赔款一笔勾销。";
                        ChronicleManager.Instance.AddEntry(ZhuluTime.CurrentYear, ChronicleEventType.PeaceTreaty,
                            winnerId, winner.name ?? "胜国", loser.name ?? "败国", debtText, false);
                        ZhuluContext.Instance.LogInfo($"[Reparations] {oldDebt.PayerName} 翻胜 {oldDebt.ReceiverName}，旧赔款 {oldDebt.Remaining} 金勾销");
                    }
                }
                // 2026-10-23 [P1-2]：这里包着 _reparations 的删除与史册写入——静默失败=旧债勾销不掉，
                // 属"静默失效即状态损坏"，改为限频告警（其余纯读取型守卫保留空 catch）
                catch (Exception ex)
                {
                    ZhuluContext.Instance.LogRateLimited("War.DebtCancel", "[War] 旧赔款勾销异常: " + ex.Message, 30f);
                }
                int loserCities = loser.cities != null ? loser.cities.Count : 0;
                bool heavy = scoreAgainstLoser >= SCORE_ANNEX;
                bool mid = scoreAgainstLoser >= SCORE_WIN;

                // ── 阶段3H-20：战争类型系数（赔款额度按战争性质合理化）──
                string settleWt = "";
                try { settleWt = war.data?.war_type ?? war.getAsset()?.id ?? ""; } catch { }
                bool indepCrownWin = settleWt == "zlr_independence_war" && winner.id != loser.id
                    && war.main_attacker != null && loser.id == war.main_attacker.id;   // 独立战：叛军战败（王师平叛）
                double typeFactor = 1.0;
                if (settleWt == "zlr_claim_war") typeFactor = 0.85;        // 宣称战：只为夺城，赔款从轻
                else if (settleWt == "zlr_total_war") typeFactor = 1.15;   // 全面战：灭国级敌意，赔款从重
                else if (settleWt == "zlr_punitive_war") typeFactor = 1.10;// 问罪之师：本为索贡
                if (indepCrownWin) typeFactor = 0.5;                        // 平叛：薄惩，不竭泽而渔

                // ── 割城（中/重级；1 城国与首都不割；独立战平叛不割——叛军留于域内）──
                string cededName = null;
                if ((mid || heavy) && loserCities >= 2 && !indepCrownWin)
                {
                    City cede = preferredCedeCity;
                    // 首选城校验：仍属败方且非其首都
                    try
                    {
                        if (cede != null)
                        {
                            if (ClaimManager.GetCityKingdom(cede) != loser) cede = null;
                            else if (loser.capital != null && cede.getID() == loser.capital.getID()) cede = null;
                        }
                    }
                    catch { cede = null; }
                    if (cede == null) cede = PickCedeCity(loser, winner); // 2026-08-20：宣称城优先 → 离胜方最近
                    if (cede != null)
                    {
                        try
                        {
                            cededName = cede.data != null ? cede.data.name ?? "边城" : "边城";
                            cede.joinAnotherKingdom(winner); // 原版割城 API（PowerBox 同款）
                        }
                        catch (Exception cex)
                        {
                            ZhuluContext.Instance.LogWarning($"[WarEval] 割城失败（降级赔款）: {cex.Message}");
                            cededName = null;
                        }
                    }
                }

                // ── 赔款（2026-08-20 分期制：小额即付，大额 1-10 年分期年度转账）──
                long paidNow = 0;
                int installmentYears = 0;
                try
                {
                    var report = TreasuryManager.Instance.GetReport(loser.id.ToString());
                    long treasury = report.Treasury;
                    long income = report.Income;
                    double pct = heavy ? 0.50 : 0.30;
                    if (loserCities <= 1) pct = 0.50; // 无城可割 → 倾库赎国

                    // ── 批次11②：四维修正（用户口径：大国败小国/势均/小国败大国 分档；叠加战损比/军队对比/剩余国力）──
                    int ppW = 0, ppL = 0;
                    try { ppW = ZhuluTierService.GetPowerPoints(winner); } catch { }
                    try { ppL = ZhuluTierService.GetPowerPoints(loser); } catch { }
                    double powerRatio = ppL > 0 ? (double)ppW / ppL : (ppW > 0 ? 2.0 : 1.0);
                    double powerFactor = powerRatio >= 2.0 ? 1.5      // 大国碾压小国：重索
                                       : powerRatio >= 1.0 ? 1.0      // 强者胜：标准
                                       : powerRatio >= 0.5 ? 0.8      // 以弱胜强：量力
                                       : 0.6;                          // 小国胜大国：象征性

                    // 战损比：败方开战兵力 vs 当前兵力（含伤亡减员）——打得越惨赔越多
                    double lossRatio = 0.0;
                    try
                    {
                        string sa = ClaimManager.GetWarCustomStr(war, WAR_DEF_START_ARMY);
                        int startArmy; int curArmy = 0;
                        int.TryParse(sa ?? "", out startArmy);
                        try { curArmy = loser.countTotalWarriors(); } catch { }
                        if (startArmy > 0) lossRatio = Math.Max(0.0, Math.Min(1.0, (startArmy - curArmy) / (double)startArmy));
                    }
                    catch { }
                    double attritionFactor = 1.0 + lossRatio * 0.6;   // 全损最多 ×1.6

                    // 军队对比：胜方当前军力 ≥3 倍败方残余 → 无力反抗加压 ×1.25；败方仍强(≥胜方) → 减压 ×0.85
                    int armyW = 0, armyL = 0;
                    try { armyW = winner.countTotalWarriors(); } catch { }
                    try { armyL = loser.countTotalWarriors(); } catch { }
                    double armyFactor = armyL > 0 ? (armyW >= armyL * 3 ? 1.25 : (armyW <= armyL ? 0.85 : 1.0)) : 1.15;

                    long demand = (long)Math.Round(treasury * pct * powerFactor * attritionFactor * armyFactor * typeFactor);

                    // 剩余国力封顶：不许榨干到亡国（国库70% + 4年岁入 为可执行上限）
                    long cap = (long)(treasury * 0.7 + income * 4);
                    if (demand > cap) demand = cap;
                    // 重创保底：重级且对方仍有库 → 至少 50 金（避免小库被乘数清零）
                    if (heavy && treasury > 40 && demand < 50) demand = 50;
                    if (demand > 0)
                    {
                        // 分期年限：每年偿付不超过岁入一半（岁入缺失/为零 → 即付）
                        long annualIncome = report.Income;
                        int years = 1;
                        if (annualIncome > 0)
                        {
                            double y = demand / (annualIncome * 0.5);
                            years = (int)Math.Ceiling(y);
                            years = Math.Max(1, Math.Min(10, years));
                        }
                        if (years <= 1)
                        {
                            paidNow = TreasuryManager.TransferIndemnity(loser, winner, demand);
                        }
                        else
                        {
                            // 分期：首期即付，余额逐年偿付（败方国库不足年份顺延，灭国则勾销）
                            installmentYears = years;
                            long annual = demand / years;
                            long firstPay = Math.Min(annual, Math.Max(0, treasury));
                            if (firstPay > 0)
                                paidNow = TreasuryManager.TransferIndemnity(loser, winner, firstPay);
                            ScheduleReparations(loser, winner, demand - paidNow, annual, ZhuluTime.CurrentYear + 1);
                        }
                    }
                }
                catch (Exception tex)
                {
                    ZhuluContext.Instance.LogWarning($"[WarEval] 赔款转账失败: {tex.Message}");
                }

                // ── 条款文本（与实际效果一致：割了才写割，赔到才写赔；分期写明年限）──
                string moneyText = paidNow > 0
                    ? (installmentYears > 1 ? $"赔款{paidNow}金（余款分{installmentYears - 1}年偿付）" : $"赔款{paidNow}金")
                    : (installmentYears > 1 ? $"允诺赔款分{installmentYears}年偿付" : "");
                if (cededName != null && paidNow > 0)
                    return $"割【{cededName}】于{winner.name}，{moneyText}";
                if (cededName != null && installmentYears > 1)
                    return $"割【{cededName}】于{winner.name}，{moneyText}";
                if (cededName != null)
                    return $"割【{cededName}】于{winner.name}";
                if (paidNow > 0)
                    return indepCrownWin ? $"{moneyText}息兵，叛众赦归编户" : $"{moneyText}息兵";
                if (installmentYears > 1)
                    return $"{moneyText}息兵";
                return indepCrownWin ? "叛众归籍，王师罢征" : "力屈请和，罢兵休战";
            }
            catch { return "罢兵休战"; }
        }

        // ═══════════════ ⑥-2 赔款分期偿付（2026-08-20 分期制设计）═══════════════
        // 大额赔款不再一次性抽空败方国库：首期即付，余款按年偿付（每年 ≤ 岁入一半的额度）；
        // 败方国库不足年份自动顺延；败方灭国 → 剩余债务勾销（人死账消）。

        /// <summary>进行中的赔款偿付计划（payerId → 计划）。</summary>
        private static readonly Dictionary<string, ReparationPlan> _reparations = new Dictionary<string, ReparationPlan>();

        /// <summary>赔款偿付计划。</summary>
        public class ReparationPlan
        {
            public string PayerId;
            public string ReceiverId;
            public string PayerName;
            public string ReceiverName;
            public long AnnualAmount;   // 年度应偿额
            public long Remaining;      // 剩余总额
            public int NextDueYear;     // 下次偿付年
        }

        /// <summary>登记赔款分期计划（同对旧计划覆盖——新和约优先）。</summary>
        private static void ScheduleReparations(Kingdom payer, Kingdom receiver, long remaining, long annual, int firstDueYear)
        {
            try
            {
                if (payer == null || receiver == null || remaining <= 0 || annual <= 0) return;
                _reparations[payer.id.ToString()] = new ReparationPlan
                {
                    PayerId = payer.id.ToString(),
                    ReceiverId = receiver.id.ToString(),
                    PayerName = payer.name ?? "败国",
                    ReceiverName = receiver.name ?? "胜国",
                    AnnualAmount = annual,
                    Remaining = remaining,
                    NextDueYear = firstDueYear
                };
                ZhuluContext.Instance.LogInfo($"[Reparations] {payer.name} 分期赔款登记: 剩余 {remaining} 金，每年 {annual} 金，{firstDueYear} 年起偿");
            }
            // 2026-10-23 [P1-2]：赔款登记写入失败必须可见——静默失败=这笔债从此不存在
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogRateLimited("War.Reparations.Register", "[War] 赔款登记异常: " + ex.Message, 30f);
            }
        }

        /// <summary>赔款年度偿付（YearDriver 按国每年调用：付款国视角）。</summary>
        public static void TickReparations(Kingdom payer, int year)
        {
            try
            {
                if (payer == null) return;
                string payerId = payer.id.ToString();
                if (!_reparations.TryGetValue(payerId, out var plan)) return;
                if (!payer.isAlive())
                {
                    // 灭国勾销：人死账消
                    _reparations.Remove(payerId);
                    ZhuluContext.Instance.LogInfo($"[Reparations] {plan.PayerName} 灭国，剩余赔款 {plan.Remaining} 金勾销");
                    return;
                }
                if (year < plan.NextDueYear) return;

                Kingdom receiver = KingdomDataExt.FindKingdomById(plan.ReceiverId);
                if (receiver == null || !receiver.isAlive())
                {
                    _reparations.Remove(payerId); // 收款方灭国 → 债务消失
                    return;
                }

                // 国库不足 → 顺延一年（不计息）
                long treasury = TreasuryManager.Instance.GetReport(payerId).Treasury;
                if (treasury < plan.AnnualAmount)
                {
                    plan.NextDueYear = year + 1;
                    ZhuluContext.Instance.LogInfo($"[Reparations] {plan.PayerName} 国库不足（{treasury}），赔款顺延");
                    return;
                }

                long pay = Math.Min(plan.AnnualAmount, plan.Remaining);
                long paid = TreasuryManager.TransferIndemnity(payer, receiver, pay);
                plan.Remaining -= paid;
                plan.NextDueYear = year + 1;

                if (plan.Remaining <= 0)
                {
                    _reparations.Remove(payerId);
                    try
                    {
                        ChronicleManager.Instance.AddEntry(year, ChronicleEventType.PeaceTreaty,
                            plan.PayerId, plan.PayerName, "",
                            $"{plan.PayerName} 偿清对 {plan.ReceiverName} 的全部战争赔款。", false);
                        // 阶段3H-13：赔款偿清高频低价值——仅天命/侯级以上国播报
                        try
                        {
                            var payerK = KingdomDataExt.FindKingdomById(plan.PayerId);
                            var recvK = KingdomDataExt.FindKingdomById(plan.ReceiverId);
                            ZhuluWorldTip.ShowPair(payerK, recvK, $"【{plan.PayerName}】偿清对【{plan.ReceiverName}】战争赔款，债了两清。", 5f, UI.ZhuluUIStyle.Hex.Azure);
                        }
                        catch { }
                    }
                    catch { }
                    ZhuluContext.Instance.LogInfo($"[Reparations] {plan.PayerName} 偿清全部赔款");
                }
            }
            // 2026-10-23 [P1-2]：年度偿付包着金额扣减与赔款移除——静默失败会导致重复扣款或债务不销
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogRateLimited("War.Reparations.Tick", "[War] 赔款年度偿付异常: " + ex.Message, 30f);
            }
        }

        /// <summary>查询某国剩余赔款（供财政报表/UI 展示）。</summary>
        public static long GetOutstandingReparations(string payerId)
        {
            return _reparations.TryGetValue(payerId, out var p) ? p.Remaining : 0;
        }

        // ═══════════════ v2 快照存档接口（赔款计划落盘，防读档丢失）═══════════════
        public static void ResetReparations() => _reparations.Clear();
        // ---- batch10-A1: schema v5 section (adapter for static host; ReparationPlan used directly as state POCO) ----
        public sealed class ReparationsSection : ISnapshotSection<ReparationPlan>
        {
            public string SectionName => "reparations";
            public Dictionary<string, ReparationPlan> CaptureStates()
            {
                var d = new Dictionary<string, ReparationPlan>();
                foreach (var kv in _reparations)
                {
                    if (kv.Value == null) continue;
                    d[kv.Key] = kv.Value;
                }
                return d;
            }
            public void ApplyStates(Dictionary<string, ReparationPlan> data)
            {
                _reparations.Clear();
                if (data == null) return;
                foreach (var kv in data)
                {
                    if (kv.Value == null) continue;
                    var plan = kv.Value;
                    plan.PayerId = kv.Key;   // payer id is the dict key by contract
                    _reparations[kv.Key] = plan;
                }
            }
        }


        // ═══════════════ ⑦ 夺天命之战（2026-08-19 方案5）═══════════════
        // 强邻问鼎：人口/军队接近或超过天命国 + 礼治具备 + 爵位≥侯 的强国，
        // 可对天命国发动夺天命之战。挑战者据天命国首都 3 年 / 天命国降伏 → 天命转移；
        // 挑战者首都沦陷 / 超 12 年 / 兵力跌破开战 30% → 挑战失败；
        // 若七年后双方高厌战、军力枯竭且战局僵持，可以艰难达成罢兵（天命不转移）。
        // 战争期间天命国天命值 -2/年（天命动摇）。

        public const string MANDATE_WAR_ID = "zlr_mandate_war";
        public const string WAR_MANDATE_HOLD_SINCE = "zlr_mandate_hold_since"; // 挑战者据天命国首都起始年（0=未据）
        public const string WAR_MANDATE_ATK_ARMY = "zlr_mandate_atk_army";     // 挑战者开战兵力快照
        private const string KEY_MANDATE_WAR_FAIL = "zlr_mandate_war_fail_year"; // 挑战失败冷却标记
        private const string KEY_MANDATE_WAR_COOLDOWN_YEARS = "zlr_mandate_war_cooldown_years";
        public const int MANDATE_WAR_MIN_RATIO = 80;    // 人口/军队 ≥ 天命国 80%（2026-08-20 用户定稿：维持双 80% 高门槛，靠提升 AI 概率保证触发）
        public const int MANDATE_WAR_MIN_LIZHI = 10;    // 挑战者礼治 ≥10
        public const int MANDATE_WAR_MIN_RANK = 2;      // 挑战者爵位 ≥ 侯（rank 2）
        public const int MANDATE_WAR_MAX_YEARS = 12;    // 最长问鼎年限；超时挑战失败
        public const int MANDATE_WAR_HOLD_YEARS = 3;    // 据天命国首都年数
        public const int MANDATE_WAR_COOLDOWN = 20;     // 失败冷却
        // 2026-08-19 概率制：双 80%/礼治是准入门槛（基础条件）；数据超越天命国越多概率越高，封顶 30%
        public const float MANDATE_WAR_AI_CHANCE = 0.08f;    // AI 年度基础概率（刚达双 80% 门槛时）
        public const float MANDATE_WAR_AI_CHANCE_MAX = 0.30f; // 概率封顶（数据全面碾压时）
        public const float NOMADIC_MANDATE_WAR_CHANCE_FACTOR = 1.50f; // 游牧体系问鼎意愿更高；只放大触发率，不改资格门槛

        private static Kingdom GetTrackMandateHolder(Kingdom challenger)
        {
            if (challenger == null) return null;
            string holderId = KingdomCivilizationService.Instance.IsNomadic(challenger)
                ? NomadicMandateManager.Instance.GetMandateHolderId()
                : OrthodoxMandateManager.Instance.GetMandateHolderId();
            return string.IsNullOrEmpty(holderId) ? null : KingdomDataExt.FindKingdomById(holderId);
        }

        private static bool IsTrackMandateHolder(Kingdom kingdom)
        {
            if (kingdom == null) return false;
            string id = kingdom.id.ToString();
            return KingdomCivilizationService.Instance.IsNomadic(kingdom)
                ? NomadicMandateManager.Instance.IsMandateHolder(id)
                : OrthodoxMandateManager.Instance.IsMandateHolder(id);
        }

        private static void AdjustTrackMandate(Kingdom holder, float delta)
        {
            if (holder == null) return;
            string id = holder.id.ToString();
            if (KingdomCivilizationService.Instance.IsNomadic(holder))
                NomadicMandateManager.Instance.AdjustMandateValue(id, delta);
            else
                OrthodoxMandateManager.Instance.AdjustMandateValue(id, delta);
        }

        private static void TransferTrackMandate(Kingdom holder, Kingdom challenger, int year, string reason)
        {
            if (holder == null) return;
            if (KingdomCivilizationService.Instance.IsNomadic(holder))
                NomadicMandateManager.Instance.ForceTransferMandate(holder, challenger, year, reason);
            else
                OrthodoxMandateManager.Instance.ForceTransferMandate(holder, challenger, year, reason);
        }

        private static bool IsNomadicMandateTrack(Kingdom kingdom)
            => kingdom != null && KingdomCivilizationService.Instance.IsNomadic(kingdom);

        private static string TrackMandateName(Kingdom kingdom)
            => IsNomadicMandateTrack(kingdom) ? "长生天眷" : "天命";

        private static string TrackHolderName(Kingdom kingdom)
            => IsNomadicMandateTrack(kingdom) ? "王庭" : "天命国";

        private static string TrackRulerTitle(Kingdom kingdom)
            => IsNomadicMandateTrack(kingdom) ? "天可汗" : "皇帝";

        private static string TrackChallengeName(Kingdom kingdom)
            => IsNomadicMandateTrack(kingdom) ? "会盟争天眷" : "问鼎中原";

        /// <summary>
        /// 2026-08-20 任务5：注册"争夺天命"谋划（个人谋划·外交栏——与附庸/独立/宣称/全面战争同栏）。
        /// 玩家/AI 均可通过谋划发动夺天命之战；图标同"发起战争"（plot_new_war）。
        /// </summary>
        public static void RegisterMandateWarPlot()
        {
            try
            {
                var plot = new PlotAsset
                {
                    id = "zlr_mandate_war_plot",
                    is_basic_plot = true,
                    path_icon = "plots/icons/plot_new_war",
                    group_id = "diplomacy",
                    can_be_done_by_king = true,
                    check_target_kingdom = true,
                    progress_needed = 80f,   // 问鼎乃天下至重之事，蓄谋最深
                    min_level = 8,
                    money_cost = 50,
                    check_is_possible = (Actor pActor) =>
                    {
                        if (pActor == null || !pActor.hasKingdom()) return false;
                        if (!WorldLawLibrary.world_law_diplomacy.isEnabled()) return false;
                        Kingdom challenger = pActor.kingdom;
                        Kingdom holder = GetTrackMandateHolder(challenger);
                        if (holder == null || holder == challenger) return false;
                        if (holder == null || !holder.isAlive()) return false;
                        return CanMandateWar(challenger, holder, ZhuluTime.CurrentYear);
                    },
                    try_to_start_advanced = (Actor pActor, PlotAsset pAsset, bool pForced) =>
                    {
                        Kingdom holder = GetTrackMandateHolder(pActor != null ? pActor.kingdom : null);
                        if (holder == null) return false;
                        Plot p = World.world.plots.newPlot(pActor, pAsset, pForced);
                        p.target_kingdom = holder;
                        return true;
                    },
                    check_should_continue = (Actor pActor) =>
                    {
                        if (pActor == null || pActor.plot == null) return false;
                        Kingdom target = pActor.plot.target_kingdom;
                        Kingdom challenger = pActor.kingdom;
                        return challenger != null && target != null && target.isAlive()
                            && KingdomCivilizationService.Instance.Get(challenger) == KingdomCivilizationService.Instance.Get(target)
                            && IsTrackMandateHolder(target);
                    },
                    action = (Actor pActor) =>
                    {
                        Kingdom challenger = pActor.kingdom;
                        Kingdom holder = pActor.plot != null ? pActor.plot.target_kingdom : null;
                        if (challenger == null || holder == null) return false;
                        string mandateName = TrackMandateName(challenger);
                        string challengeName = TrackChallengeName(challenger);
                        // 发动前二次验证（谋划期间局势可能变化：和约签订/陷入他战/灭国）
                        if (!CanMandateWar(challenger, holder, ZhuluTime.CurrentYear))
                        {
                            ZhuluContext.Instance.LogInfo($"[MandateWar] {challengeName}谋划发动失败：{challenger.name}（资格已失）");
                            try { WorldTip.showNow($"争夺{mandateName}失败：资格已失（和约/交战/势衰）", false, "top", 3f, UI.ZhuluUIStyle.Hex.Negative); } catch { }
                            return false;
                        }
                        War war = WarGateway.DeclareWar(challenger, holder, WarfareManager.MandateWar, "争夺" + mandateName);
                        if (war != null)
                        {
                            ClaimManager.SetWarCustomStr(war, WAR_MANDATE_ATK_ARMY,
                                (challenger.countTotalWarriors()).ToString());
                            try { WorldTip.showNow($"【{challenger.name}】{challengeName}！兴兵争夺{mandateName}！", false, "top", 8f, UI.ZhuluUIStyle.Hex.Vermilion); } catch { }
                            ChronicleManager.Instance.AddEntry(ZhuluTime.CurrentYear, ChronicleEventType.WarDeclared,
                                challenger.id.ToString(), challenger.name ?? "", holder.name ?? "",
                                $"<color=#FF4444>{challengeName}</color>：<color={UI.ZhuluUIStyle.Hex.Imperial}>{challenger.name}</color> 谋划既成，兴兵争夺{mandateName}！", true);
                            ZhuluContext.Instance.LogInfo($"[MandateWar] 谋划发动: {challenger.name} 对 {holder.name}");
                            return true;
                        }
                        return false;
                    }
                };
                plot.check_can_be_forced = plot.check_is_possible;
                AssetManager.plots_library.add(plot);
                AssetManager.plots_library.basic_plots.Add(plot);

                LM.AddToCurrentLocale("plot_zlr_mandate_war_plot", "争夺天命/天眷");
                LM.AddToCurrentLocale("plot_zlr_mandate_war_plot_info", "$initiator_actor$ 正谋划挑战本体系最高权统持有国 $target_kingdom$");
                LM.AddToCurrentLocale("plot_zlr_mandate_war_plot_info_base",
                    "挑战本体系最高权统持有国：据其首都三年或使其降伏即可夺取天命/长生天眷；兵败则进入二十年冷却（两套体系沿用同一资格数值）");

                ZhuluContext.Instance.LogInfo("[ZhuluWarService] 争夺天命谋划注册成功");
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[ZhuluWarService] 注册争夺天命谋划失败", ex);
            }
        }

        /// <summary>2026-08-20：挑战者是否处于问鼎失败冷却期（20 年）——供资格判定与 UI 诊断共用。</summary>
        public static bool IsInMandateWarCooldown(Kingdom challenger, int year)
        {
            try
            {
                long failYear = KingdomDataExt.GetLong(challenger, KEY_MANDATE_WAR_FAIL, 0);
                int cooldownYears = (int)KingdomDataExt.GetLong(challenger,
                    KEY_MANDATE_WAR_COOLDOWN_YEARS, MANDATE_WAR_COOLDOWN);
                if (cooldownYears <= 0) cooldownYears = MANDATE_WAR_COOLDOWN;
                return failYear > 0 && year - (int)failYear < cooldownYears;
            }
            catch { return false; }
        }

        /// <summary>夺天命之战资格判定。</summary>
        public static bool CanMandateWar(Kingdom challenger, Kingdom holder, int year)
        {
            try
            {
                if (challenger == null || holder == null || challenger == holder) return false;
                if (!challenger.isAlive() || !holder.isAlive()) return false;
                string cId = challenger.id.ToString();
                string hId = holder.id.ToString();

                // 两国必须属于同一体系，且目标是该体系当前唯一的天命持有国。
                if (KingdomCivilizationService.Instance.Get(challenger) != KingdomCivilizationService.Instance.Get(holder)) return false;
                if (!IsTrackMandateHolder(holder)) return false;
                if (IsTrackMandateHolder(challenger)) return false;
                // 附庸不可夺宗主天命（先独立再问鼎）
                if (VassalManager.Instance.GetSuzerain(challenger) == holder) return false;
                // 盟友不背刺
                try { if (challenger.hasAlliance() && challenger.getAlliance() == holder.getAlliance() && holder.hasAlliance()) return false; } catch { }
                // 爵位 ≥ 侯
                if (PolicyManager.GetKingdomRank(challenger, false) < MANDATE_WAR_MIN_RANK) return false;
                // 礼治 ≥10
                if (ZhuluLiZhiService.GetLiZhiScore(cId) < MANDATE_WAR_MIN_LIZHI) return false;
                // 人口/军队 ≥ 80%
                int cPop = challenger.units != null ? challenger.units.Count : 0;
                int hPop = holder.units != null ? holder.units.Count : 0;
                int cArmy = challenger.countTotalWarriors();
                int hArmy = holder.countTotalWarriors();
                if (hPop > 0 && cPop * 100 < hPop * MANDATE_WAR_MIN_RATIO) return false;
                if (hArmy > 0 && cArmy * 100 < hArmy * MANDATE_WAR_MIN_RATIO) return false;
                // 挑战者不在战争中（问鼎需倾国之力）
                try { var wars = challenger.getWars(); if (wars != null) { foreach (var _ in wars) return false; } } catch { }
                // 失败冷却 20 年
                long failYear = KingdomDataExt.GetLong(challenger, KEY_MANDATE_WAR_FAIL, 0);
                if (failYear > 0 && year - (int)failYear < MANDATE_WAR_COOLDOWN) return false;
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 2026-08-19：AI 问鼎年度概率——双 80%/礼治为准入门槛（CanMandateWar），
        /// 数据（人口+军队均值）超越天命国越多概率越高：刚达标 8% → 均值每超 1% 加 0.28% → 封顶 30%。
        /// </summary>
        public static float GetMandateWarChance(Kingdom challenger, Kingdom holder)
        {
            try
            {
                if (challenger == null || holder == null) return MANDATE_WAR_AI_CHANCE;
                int cPop = challenger.units != null ? challenger.units.Count : 0;
                int hPop = holder.units != null ? holder.units.Count : 0;
                int cArmy = challenger.countTotalWarriors();
                int hArmy = holder.countTotalWarriors();
                // 天命国数据为 0 时视为全面超越（给最大比值）
                float popRatio = hPop > 0 ? (float)cPop / hPop : 1.6f;
                float armyRatio = hArmy > 0 ? (float)cArmy / hArmy : 1.6f;
                float avg = (popRatio + armyRatio) * 0.5f;
                float chance = MANDATE_WAR_AI_CHANCE + (avg - MANDATE_WAR_MIN_RATIO / 100f) * 0.28f;
                float max = MANDATE_WAR_AI_CHANCE_MAX;
                if (KingdomCivilizationService.Instance.IsNomadic(challenger))
                {
                    chance *= NOMADIC_MANDATE_WAR_CHANCE_FACTOR;
                    max *= NOMADIC_MANDATE_WAR_CHANCE_FACTOR;
                }
                return Math.Max(MANDATE_WAR_AI_CHANCE, Math.Min(max, chance));
            }
            catch { return MANDATE_WAR_AI_CHANCE; }
        }

        /// <summary>AI 年度问鼎扫描（TickAllWars 尾部调用）：对现任天命国，从合格强国中择优发动（概率随数据优势递增）。</summary>
        private static void TryStartMandateWar(int year)
        {
            try
            {
                string easternHolderId = OrthodoxMandateManager.Instance.GetMandateHolderId();
                string nomadicHolderId = NomadicMandateManager.Instance.GetMandateHolderId();
                TryStartMandateWarForHolder(string.IsNullOrEmpty(easternHolderId)
                    ? null : KingdomDataExt.FindKingdomById(easternHolderId), year);
                TryStartMandateWarForHolder(string.IsNullOrEmpty(nomadicHolderId)
                    ? null : KingdomDataExt.FindKingdomById(nomadicHolderId), year);
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[MandateWar] TryStartMandateWar 异常: {ex.Message}");
            }
        }

        private static void TryStartMandateWarForHolder(Kingdom holder, int year)
        {
            try
            {
                if (holder == null || !holder.isAlive()) return;
                // 天命国已在夺天命战争中 → 不重复开战
                try
                {
                    var wars = holder.getWars();
                    if (wars != null)
                    {
                        foreach (var w in wars)
                        {
                            if (w?.data?.war_type == MANDATE_WAR_ID) return;
                        }
                    }
                }
                catch { }

                // 择优挑战者：人口最多且合格者
                Kingdom best = null; int bestPop = -1;
                foreach (Kingdom k in World.world.kingdoms.list)
                {
                    if (k == null || !k.isAlive() || k == holder) continue;
                    if (!CanMandateWar(k, holder, year)) continue;
                    int pop = k.units != null ? k.units.Count : 0;
                    if (pop > bestPop) { bestPop = pop; best = k; }
                }
                if (best == null) return;
                if (UnityEngine.Random.value > GetMandateWarChance(best, holder)) return;

                string mandateName = TrackMandateName(best);
                string challengeName = TrackChallengeName(best);
                War war = WarGateway.DeclareWar(best, holder, WarfareManager.MandateWar, "争夺" + mandateName + "之战");
                if (war != null)
                {
                    ClaimManager.SetWarCustomStr(war, WAR_MANDATE_ATK_ARMY,
                        (best.countTotalWarriors()).ToString());
                    try { WorldTip.showNow($"【{best.name}】{challengeName}！对{TrackHolderName(holder)}【{holder.name}】发动争夺{mandateName}之战！", false, "top", 8f, UI.ZhuluUIStyle.Hex.Vermilion); } catch { }
                    ChronicleManager.Instance.AddEntry(year, ChronicleEventType.WarDeclared,
                        best.id.ToString(), best.name ?? "", holder.name ?? "",
                        $"<color=#FF4444>{challengeName}</color>：<color={UI.ZhuluUIStyle.Hex.Imperial}>{best.name}</color> 兴兵争夺{mandateName}，与【{holder.name}】决战！", true);
                    ZhuluContext.Instance.LogInfo($"[MandateWar] {best.name} 对 {holder.name} 发动夺天命之战");
                }
            }
            catch (Exception ex) { ZhuluContext.Instance.LogWarning($"[MandateWar] 体系扫描异常: {ex.Message}"); }
        }

        /// <summary>
        /// P2-30 问罪之战年度评估（TickAllWars 分派）。返回 true = 战争已终结。
        /// 三出口：宗主胜(score≥SCORE_WIN) / 附庸撑住(≤−SCORE_WIN) / 师老两清(≥3年低分白和)。
        /// 欠贡清零与稳定惩罚在 EndWarPostfix 问罪收尾段统一处理。
        /// </summary>
        private static bool EvaluatePunitiveWar(War war, int year)
        {
            try
            {
                Kingdom suz = war.getMainAttacker();
                Kingdom vas = war.getMainDefender();
                if (suz == null || vas == null || !suz.isAlive() || !vas.isAlive()) return false;

                string startRaw = ClaimManager.GetWarCustomStr(war, WAR_START);
                int.TryParse(startRaw ?? "", out int startYear);
                int elapsed = startYear > 0 ? year - startYear : 0;
                int score = EvaluateWarScore(war);

                if (score >= SCORE_WIN)
                {
                    ZhuluContext.Instance.LogInfo($"[Punitive] 问罪得胜: {suz.name} 惩 {vas.name}（score={score}）");
                    if (!war.hasEnded()) World.world.wars.endWar(war, WarWinner.Attackers);
                    return true;
                }
                if (score <= -SCORE_WIN)
                {
                    ZhuluContext.Instance.LogInfo($"[Punitive] 附庸撑住问罪: {vas.name}（score={score}）");
                    if (!war.hasEnded()) World.world.wars.endWar(war, WarWinner.Defenders);
                    return true;
                }
                if (elapsed >= 3 && Math.Abs(score) < SCORE_MINOR)
                {
                    ZhuluContext.Instance.LogInfo($"[Punitive] 问罪师老，两清罢兵: {suz.name} / {vas.name}");
                    if (!war.hasEnded()) World.world.wars.endWar(war, WarWinner.Peace);
                    return true;
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>夺天命之战年度评估（TickAllWars 内调用）。返回 true = 战争已终结（调用方 continue）。</summary>
        private static bool EvaluateMandateWar(War war, int year)
        {
            Kingdom challenger = war.getMainAttacker();
            Kingdom holder = war.getMainDefender();
            if (challenger == null || holder == null) return false;
            string cName = challenger.name ?? "挑战者";
            string hName = holder.name ?? TrackHolderName(holder);
            string mandateName = TrackMandateName(holder);

            // ── 双方存亡 ──
            if (!challenger.isAlive())
            {
                World.world.wars.endWar(war, WarWinner.Defenders);
                ZhuluContext.Instance.LogInfo($"[MandateWar] 挑战者 {cName} 灭国，问鼎失败");
                return true;
            }
            if (!holder.isAlive())
            {
                World.world.wars.endWar(war, WarWinner.Attackers);
                // 2026-08-19 修复：灭国分支天命强制转移给挑战者——原只 endWar，
                // 天命由 CheckTransferConditions→FindBestCandidate 择优（可能给无关第三方强国），
                // 与"问鼎"语义不符：谁灭天命国谁受命
                try
                {
                    TransferTrackMandate(holder, challenger, year, "争天灭国");
                    try { WorldTip.showNow($"<color={UI.ZhuluUIStyle.Hex.Imperial}>{mandateName}易主！</color>【{cName}】灭【{hName}】，受{mandateName}，立为{TrackHolderName(challenger)}，其主称{TrackRulerTitle(challenger)}！", false, "top", 10f); } catch { }
                    ChronicleManager.Instance.AddEntry(year, ChronicleEventType.MandateGranted,
                        challenger.id.ToString(), cName, hName,
                        $"<color={UI.ZhuluUIStyle.Hex.Imperial}>{mandateName}易主</color>：{cName} 灭 {hName}，本体系最高权统遂移。", true);
                }
                catch { }
                ZhuluContext.Instance.LogInfo($"[MandateWar] 天命国 {hName} 灭国，问鼎终局（天命转移给 {cName}）");
                return true;
            }

            // ── 天命动摇：战争期间天命国 -2/年 ──
            try { AdjustTrackMandate(holder, -2f); } catch { }

            // ── 挑战者首都沦陷 → 失败 ──
            try
            {
                if (challenger.capital != null)
                {
                    var capOwner = ClaimManager.GetCityKingdom(challenger.capital);
                    if (capOwner != null && capOwner != challenger)
                    {
                        FinishMandateWarLoss(war, challenger, holder, year, "挑战者首都沦陷");
                        return true;
                    }
                }
            }
            catch { }

            // ── 兵力跌破开战 30% → 失败 ──
            try
            {
                int startArmy = 0;
                int.TryParse(ClaimManager.GetWarCustomStr(war, WAR_MANDATE_ATK_ARMY) ?? "", out startArmy);
                int nowArmy = challenger.countTotalWarriors();
                if (startArmy > 0 && nowArmy * 100 < startArmy * 30)
                {
                    FinishMandateWarLoss(war, challenger, holder, year, "兵力折损殆尽");
                    return true;
                }
            }
            catch { }

            // ── 夺天命之战专用和谈：比普通战争更难 ──
            int startYear = 0;
            int.TryParse(ClaimManager.GetWarCustomStr(war, WAR_START) ?? "", out startYear);
            int elapsed = startYear > 0 ? Math.Max(0, year - startYear) : 0;
            if (elapsed >= 7)
            {
                try
                {
                    int score = EvaluateWarScore(war);
                    int challengerEx = GetWarExhaustion(challenger.id.ToString());
                    int holderEx = GetWarExhaustion(holder.id.ToString());
                    bool bothDepleted = IsArmyDepleted(war, challenger, true, 60)
                        && IsArmyDepleted(war, holder, false, 60);
                    bool challengerHoldingCapital = false;
                    try
                    {
                        challengerHoldingCapital = holder.capital != null
                            && ClaimManager.GetCityKingdom(holder.capital) == challenger;
                    }
                    catch { }

                    // 阶段3H-11 根修：战果压倒时厌战不可阻止问鼎。
                    // 旧逻辑 desperate/Loss 只看厌战不看战果——挑战者已把天命国打剩两三城
                    // （score 70+，SCORE_ANNEX 量级）却因自家厌战 92 被判"无力再举"失败，
                    // 天命国反获 +10 天命与 20 年和平，明显不合理。
                    // 天命国残存城数是压倒性战果的直观判据：≤3 城（或 ≤ 开战三成）即大势已去。
                    int holderNowCities = holder.cities != null ? holder.cities.Count : 0;
                    int defStart2 = 0;
                    int.TryParse(ClaimManager.GetWarCustomStr(war, WAR_DEF_START_CITIES) ?? "", out defStart2);
                    bool holderCrumbling = holderNowCities <= 3
                        || (defStart2 > 0 && holderNowCities * 10 < defStart2 * 3);
                    if (holderCrumbling || score >= SCORE_ANNEX)
                    {
                        // 强制夺鼎成功：破国在即，天命当归挑战者（战果论英雄，厌战不构成阻碍）
                        World.world.wars.endWar(war, WarWinner.Attackers);
                        TransferTrackMandate(holder, challenger, year, "问鼎功成");
                        try { WorldTip.showNow($"<color={UI.ZhuluUIStyle.Hex.Imperial}>{mandateName}易主！</color>【{cName}】兵锋已破【{hName}】社稷，虽厌战沸腾，将士犹请命于都下——{TrackMandateName(challenger)}归【{cName}】！", false, "top", 10f); } catch { }
                        ChronicleManager.Instance.AddEntry(year, ChronicleEventType.MandateGranted,
                            challenger.id.ToString(), cName, hName,
                            $"<color={UI.ZhuluUIStyle.Hex.Imperial}>{mandateName}易主</color>：{cName} 破 {hName} 社稷（残{holderNowCities}城），兵锋所向，{mandateName}遂移。", true);
                        ZhuluContext.Instance.LogInfo($"[MandateWar] 压倒性战果强制夺鼎: {cName} score={score} 残城={holderNowCities} 厌战={challengerEx}/{holderEx}");
                        return true;
                    }

                    // 僵局议和：仅限战局胶着（低分差）——双方高厌战、军队均只剩六成以下才谈成；
                    // 十年后的极高厌战僵局可不强制军力比例。有明显战果/败绩（±SCORE_MINOR）不许和。
                    bool negotiated = Math.Abs(score) < SCORE_MINOR
                        && challengerEx >= 70 && holderEx >= 70 && bothDepleted;
                    bool desperate = elapsed >= 10 && Math.Abs(score) < SCORE_WIN
                        && challengerEx >= 85 && holderEx >= 85;
                    if (!challengerHoldingCapital && (negotiated || desperate))
                    {
                        FinishMandateWarStalemate(war, challenger, holder, year, score,
                            challengerEx, holderEx);
                        return true;
                    }

                    // 挑战者自身厌战崩溃且没有明显战果（score < SCORE_MINOR 已保证不冤枉优势方）：
                    // 只能撤军认输，不得无代价白和。厌战条件收紧 92→95（有战果优势时更耐战）。
                    if (!challengerHoldingCapital && challengerEx >= 95 && score < SCORE_MINOR)
                    {
                        FinishMandateWarLoss(war, challenger, holder, year, "国中厌战，无力再举");
                        return true;
                    }
                }
                catch { }
            }

            // ── 据天命国首都：连续 3 年 → 夺鼎成功 ──
            try
            {
                bool holdingCap = false;
                if (holder.capital != null)
                {
                    var capOwner = ClaimManager.GetCityKingdom(holder.capital);
                    holdingCap = capOwner != null && capOwner == challenger;
                }
                long holdSince = 0;
                string hs = ClaimManager.GetWarCustomStr(war, WAR_MANDATE_HOLD_SINCE) ?? "0";
                long.TryParse(hs, out holdSince);
                if (holdingCap)
                {
                    if (holdSince <= 0)
                    {
                        ClaimManager.SetWarCustomStr(war, WAR_MANDATE_HOLD_SINCE, year.ToString());
                        try { WorldTip.showNow($"【{cName}】已据【{hName}】首都！再守 3 年，{mandateName}归之…", false, "top", 6f, UI.ZhuluUIStyle.Hex.Warn); } catch { }
                    }
                    else if (year - (int)holdSince >= MANDATE_WAR_HOLD_YEARS)
                    {
                        // （据都三年主判定——保留）
                        // 夺鼎成功：天命强制转移
                        World.world.wars.endWar(war, WarWinner.Attackers);
                        TransferTrackMandate(holder, challenger, year, "争夺" + mandateName + "之战");
                        try { WorldTip.showNow($"<color={UI.ZhuluUIStyle.Hex.Imperial}>{mandateName}易主！</color>【{cName}】克【{hName}】都三年，立为{TrackHolderName(challenger)}，其主称{TrackRulerTitle(challenger)}！", false, "top", 10f); } catch { }
                        ChronicleManager.Instance.AddEntry(year, ChronicleEventType.MandateGranted,
                            challenger.id.ToString(), cName, hName,
                            $"<color={UI.ZhuluUIStyle.Hex.Imperial}>{mandateName}易主</color>：{cName} 克 {hName} 都城三年，本体系最高权统遂移。", true);
                        ZhuluContext.Instance.LogInfo($"[MandateWar] {cName} 夺鼎成功，天命转移（据都 {year - holdSince} 年）");
                        return true;
                    }
                }
                else if (holdSince > 0)
                {
                    ClaimManager.SetWarCustomStr(war, WAR_MANDATE_HOLD_SINCE, "0"); // 都城失守重计
                }
            }
            catch { }

            // ── 超过最长年限 → 挑战失败 ──
            // 必须放在“据都三年”成功判定之后，否则挑战者在最后一年
            // 已满足夺鼎条件时仍会被超时分支抢先判败。
            if (startYear > 0 && elapsed >= MANDATE_WAR_MAX_YEARS)
            {
                FinishMandateWarLoss(war, challenger, holder, year, "师老兵疲无功");
                return true;
            }

            return false; // 战争继续
        }

        /// <summary>
        /// 问鼎僵局议和：天命不转移，挑战者承担较轻的政治代价与 12 年冷却，
        /// 天命国只回复少量天命。这不是“白嫖”，也不会把议和误判为夺鼎成功。
        /// </summary>
        private static void FinishMandateWarStalemate(War war, Kingdom challenger, Kingdom holder,
            int year, int score, int challengerExhaustion, int holderExhaustion)
        {
            World.world.wars.endWar(war, WarWinner.Peace);
            try
            {
                float stability = KingdomDataExt.GetFloat(challenger, "stability", 50f);
                KingdomDataExt.SetFloat(challenger, "stability", Math.Max(0f, stability - 5f));
                long liZhi = KingdomDataExt.GetLong(challenger, "li_zhi_points", 0);
                KingdomDataExt.SetLong(challenger, "li_zhi_points", Math.Max(0, liZhi - 2));
                KingdomDataExt.SetLong(challenger, KEY_MANDATE_WAR_FAIL, year);
                KingdomDataExt.SetLong(challenger, KEY_MANDATE_WAR_COOLDOWN_YEARS, 12);
                AdjustTrackMandate(holder, 3f);
            }
            catch { }
            try
            {
                WorldTip.showNow($"【{challenger.name}】与【{holder.name}】争战多年，国中俱疲，约和罢兵；{TrackMandateName(holder)}暂不易主。",
                    false, "top", 9f, UI.ZhuluUIStyle.Hex.Azure);
            }
            catch { }
            ChronicleManager.Instance.AddEntry(year, ChronicleEventType.PeaceTreaty,
                challenger.id.ToString(), challenger.name ?? "", holder.name ?? "",
                $"争天议和：{challenger.name} 与 {holder.name} 久战俱疲，遂罢兵；{TrackMandateName(holder)}未移。", true);
            ZhuluContext.Instance.LogInfo($"[MandateWar] 问鼎僵局议和: {challenger.name} vs {holder.name}, score={score}, 厌战={challengerExhaustion}/{holderExhaustion}");
        }

        /// <summary>夺天命之战挑战失败收尾：终结战争 + 惩罚 + 冷却 + 天命国天命 +10。</summary>
        private static void FinishMandateWarLoss(War war, Kingdom challenger, Kingdom holder, int year, string reason)
        {
            World.world.wars.endWar(war, WarWinner.Defenders);
            try
            {
                // 惩罚：稳定 -15 / 礼治 -5（写祭祀累积分 key="li_zhi_points"——GetLiZhiScore 第②项） / 冷却 20 年
                string cId = challenger.id.ToString();
                float stab = KingdomDataExt.GetFloat(challenger, "stability", 50f);
                KingdomDataExt.SetFloat(challenger, "stability", Math.Max(0f, stab - 15f));
                long lzPoints = KingdomDataExt.GetLong(challenger, "li_zhi_points", 0);
                KingdomDataExt.SetLong(challenger, "li_zhi_points", Math.Max(0, lzPoints - 5));
                KingdomDataExt.SetLong(challenger, KEY_MANDATE_WAR_FAIL, year);
                KingdomDataExt.SetLong(challenger, KEY_MANDATE_WAR_COOLDOWN_YEARS, MANDATE_WAR_COOLDOWN);
                // 天命国天命 +10（挫败问鼎，天命愈固）
                AdjustTrackMandate(holder, 10f);
            }
            catch { }
            try { WorldTip.showNow($"【{challenger.name}】争夺{TrackMandateName(holder)}失败（{reason}），元气大伤！", false, "top", 7f, UI.ZhuluUIStyle.Hex.Negative); } catch { }
            ChronicleManager.Instance.AddEntry(year, ChronicleEventType.WarDeclared,
                challenger.id.ToString(), challenger.name ?? "", holder.name ?? "",
                $"争天失败：{challenger.name} 兵败（{reason}），威望扫地，二十年内不得再次挑战{TrackMandateName(holder)}。", true);
            ZhuluContext.Instance.LogInfo($"[MandateWar] {challenger.name} 问鼎失败（{reason}）");
        }
    }
}
