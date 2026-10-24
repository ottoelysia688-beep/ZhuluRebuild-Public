using System;
using System.Collections.Generic;
using HarmonyLib;
using ai.behaviours;
using ZhuluRebuild.Core;
using ZhuluRebuild.Domain;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Adapters
{
    /// <summary>
    /// 战争分类 Harmony 补丁：拦截战争创建（重新分类）、战争每帧更新（自动停战判定）和战争结束（录入史册）。
    /// 必须用显式 harmony.Patch 挂载，不能用 PatchAll（避免静默失效）。
    /// </summary>
    public static class WarfarePatches
    {
        // 用静态字典存储宣称战争的目标城市和开始年份（比 custom_data_string 更可靠）
        internal static readonly Dictionary<long, string> ClaimWarTargets = new Dictionary<long, string>();
        internal static readonly Dictionary<long, int> ClaimWarStartYears = new Dictionary<long, int>();
        // 用静态字典存储独立战争的开始年份
        internal static readonly Dictionary<long, int> IndependenceWarStartYears = new Dictionary<long, int>();


        /// <summary>由 Main.cs 调用，显式挂载所有战争相关补丁。</summary>
        public static void Mount(Harmony harmony)
        {
            // 拦截 WarManager.newWar(Kingdom, Kingdom, WarTypeAsset) 的前置（战争网关）+ 后置
            var newWarMethod = AccessTools.Method(typeof(WarManager), "newWar",
                new[] { typeof(Kingdom), typeof(Kingdom), typeof(WarTypeAsset) });
            if (newWarMethod != null)
            {
                harmony.Patch(newWarMethod,
                    prefix: new HarmonyMethod(typeof(WarfarePatches), nameof(NewWarPrefix)),
                    postfix: new HarmonyMethod(typeof(WarfarePatches), nameof(NewWarPostfix)));
                ZhuluContext.Instance.LogInfo("[WarfarePatches] newWar 前置+后置补丁挂载成功");
            }
            else
            {
                ZhuluContext.Instance.LogWarning("[WarfarePatches] 未找到 WarManager.newWar 方法，补丁跳过");
            }

            // 拦截 DiplomacyManager.startWar 的前置（战争网关双保险）
            var startWarMethod = AccessTools.Method(typeof(DiplomacyManager), "startWar",
                new[] { typeof(Kingdom), typeof(Kingdom), typeof(WarTypeAsset), typeof(bool) });
            if (startWarMethod != null)
            {
                harmony.Patch(startWarMethod,
                    prefix: new HarmonyMethod(typeof(WarfarePatches), nameof(StartWarPrefix)));
                ZhuluContext.Instance.LogInfo("[WarfarePatches] startWar 前置补丁挂载成功");
            }

            // 拦截 WarManager.endWar(War, WarWinner) 的后置
            var endWarMethod = AccessTools.Method(typeof(WarManager), "endWar",
                new[] { typeof(War), typeof(WarWinner) });
            if (endWarMethod != null)
            {
                harmony.Patch(endWarMethod,
                    postfix: new HarmonyMethod(typeof(WarfarePatches), nameof(EndWarPostfix)));
                ZhuluContext.Instance.LogInfo("[WarfarePatches] endWar 后置补丁挂载成功");
            }
            else
            {
                ZhuluContext.Instance.LogWarning("[WarfarePatches] 未找到 WarManager.endWar 方法，补丁跳过");
            }

            // 拦截 War.update() 的后置（无参数版本）—— 用于宣称战争自动停战、附庸战争占领判定、独立战争结束判定
            var warUpdateMethod = AccessTools.Method(typeof(War), "update", Type.EmptyTypes);
            if (warUpdateMethod != null)
            {
                harmony.Patch(warUpdateMethod,
                    postfix: new HarmonyMethod(typeof(WarfarePatches), nameof(WarUpdatePostfix)));
                ZhuluContext.Instance.LogInfo("[WarfarePatches] War.update 后置补丁挂载成功");
            }
            else
            {
                ZhuluContext.Instance.LogWarning("[WarfarePatches] 未找到 War.update 方法，补丁跳过");
            }

            // 禁止联盟：拦截 AllianceManager.forceAlliance 的前置，直接返回 false 阻止联盟形成
            var forceAllianceMethod = AccessTools.Method(typeof(AllianceManager), "forceAlliance",
                new[] { typeof(Kingdom), typeof(Kingdom) });
            if (forceAllianceMethod != null)
            {
                harmony.Patch(forceAllianceMethod,
                    prefix: new HarmonyMethod(typeof(WarfarePatches), nameof(BlockAlliancePrefix)));
                ZhuluContext.Instance.LogInfo("[WarfarePatches] 联盟禁止补丁挂载成功");
            }

            // 禁止结盟谋划出现：直接覆盖 alliance_create 谋划的 check_is_possible，让AI永远不会发起结盟谋划
            DisableAlliancePlots();

            // 禁止原版战争谋划：双重保障
            // 1. 尝试直接覆盖（如果 PlotsLibrary 已初始化）
            DisableWarPlots();

            // 2. 拦截谋划进度检查行为：即使谋划已经开始，也在每次进度检查时取消它
            //    （解决 PlotsLibrary.new_war 在 mod 加载时为 null 的时机问题）
            var checkPlotProgressMethod = AccessTools.Method(typeof(BehCheckPlotProgress), "execute");
            if (checkPlotProgressMethod != null)
            {
                harmony.Patch(checkPlotProgressMethod,
                    prefix: new HarmonyMethod(typeof(WarfarePatches), nameof(CheckPlotProgressPrefix)));
                ZhuluContext.Instance.LogInfo("[WarfarePatches] 谋划进度拦截补丁挂载成功（BehCheckPlotProgress）");
            }
        }

        /// <summary>战争网关前置：拦截原版文明间随机战争（返回 false 阻止原方法执行）。</summary>
        static bool NewWarPrefix(Kingdom pAttacker, Kingdom pDefender, WarTypeAsset pType, ref War __result)
        {
            if (WarGateway.ShouldBlockWar(pAttacker, pDefender, pType))
            {
                __result = null;
                return false;
            }
            return true;
        }

        /// <summary>战争网关前置（DiplomacyManager.startWar 双保险）。</summary>
        static bool StartWarPrefix(Kingdom pAttacker, Kingdom pDefender, WarTypeAsset pAsset, ref War __result)
        {
            if (WarGateway.ShouldBlockWar(pAttacker, pDefender, pAsset))
            {
                __result = null;
                return false;
            }
            return true;
        }

        /// <summary>禁止联盟形成（前置补丁返回 false 阻止原方法执行）。</summary>
        static bool BlockAlliancePrefix()
        {
            return false;
        }

        /// <summary>
        /// 拦截谋划进度检查：如果当前谋划是原版战争谋划（new_war/whisper_of_war），立即取消。
        /// 参考外部重定向补丁方案，解决 PlotsLibrary 在 mod 加载时可能未初始化的时机问题。
        /// </summary>
        static bool CheckPlotProgressPrefix(Actor pActor, ref BehResult __result)
        {
            try
            {
                if (pActor?.plot == null) return true; // 不是我们的目标，放行

                string plotId = "";
                try { plotId = pActor.plot.getAsset()?.id ?? pActor.plot.data?.plot_type_id ?? ""; }
                catch { return true; }

                if (plotId == "new_war" || plotId == "whisper_of_war")
                {
                    // 取消这个谋划
                    try { World.world?.plots?.cancelPlot(pActor.plot); }
                    catch { try { pActor.leavePlot(); } catch { } }

                    __result = BehResult.Stop;
                    return false; // 不执行原方法
                }
            }
            catch { }
            return true; // 放行
        }

        /// <summary>禁止原版战争谋划出现：覆盖 new_war / whisper_of_war 谋划的条件检查，AI不再发起原版战争谋划。</summary>
        private static void DisableWarPlots()
        {
            try
            {
                // 禁止原版"发动战争"谋划（阶段2B 战争修复 ②③：用户反馈"小人头顶谋划×"观感差——
                // check_is_possible=false 让 AI 不再发起；check_should_continue=false 让已开始的存量谋划
                // 静默中断（不再执行到 action 时失败显示 ×））
                var newWarPlot = PlotsLibrary.new_war;
                if (newWarPlot != null)
                {
                    newWarPlot.check_is_possible = (Actor pActor) => false;
                    newWarPlot.check_can_be_forced = (Actor pActor) => false;
                    newWarPlot.check_should_continue = (Actor pActor) => false;
                    ZhuluContext.Instance.LogInfo("[WarfarePatches] 原版战争谋划已禁止（new_war，含存量中断）");
                }
                else
                {
                    ZhuluContext.Instance.LogWarning("[WarfarePatches] PlotsLibrary.new_war 为 null（将由 BehCheckPlotProgress 拦截兜底）");
                }

                // 禁止"低语战争"谋划（如果存在；含存量中断）
                var whisperPlot = AssetManager.plots_library.get("whisper_of_war");
                if (whisperPlot != null)
                {
                    whisperPlot.check_is_possible = (Actor pActor) => false;
                    whisperPlot.check_can_be_forced = (Actor pActor) => false;
                    whisperPlot.check_should_continue = (Actor pActor) => false;
                    ZhuluContext.Instance.LogInfo("[WarfarePatches] 低语战争谋划已禁止（whisper_of_war，含存量中断）");
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[WarfarePatches] 禁止原版战争谋划失败", ex);
            }
        }

        /// <summary>禁止结盟谋划出现：覆盖原版 alliance_create / alliance_join 谋划的条件检查，让AI永远不会发起结盟谋划。</summary>
        private static void DisableAlliancePlots()
        {
            try
            {
                var alliancePlot = PlotsLibrary.alliance_create;
                if (alliancePlot != null)
                {
                    alliancePlot.check_is_possible = (Actor pActor) => false;
                    alliancePlot.check_can_be_forced = (Actor pActor) => false;
                    ZhuluContext.Instance.LogInfo("[WarfarePatches] 结盟谋划已禁止（alliance_create）");
                }

                // 同时禁止加入联盟谋划
                var joinPlot = AssetManager.plots_library.get("alliance_join");
                if (joinPlot != null)
                {
                    joinPlot.check_is_possible = (Actor pActor) => false;
                    joinPlot.check_can_be_forced = (Actor pActor) => false;
                    ZhuluContext.Instance.LogInfo("[WarfarePatches] 加入联盟谋划已禁止（alliance_join）");
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[WarfarePatches] 禁止结盟谋划失败", ex);
            }
        }

        /// <summary>战争创建后：根据上下文重新分类战争类型 + 附庸国战争参与规则。</summary>
        static void NewWarPostfix(WarManager __instance, War __result,
            Kingdom pAttacker, Kingdom pDefender, WarTypeAsset pType)
        {
            try
            {
                if (__result == null || pAttacker == null || pDefender == null) return;

                // 所有文明战争都需要开战年、城数与军力基线，供通用厌战/求和评估使用。
                // WarGateway 创建的逐鹿战争稍后会再调 InitializeWarData；本方法只补空值，幂等。
                ZhuluWarService.EnsureWarTrackingData(__result, pAttacker, pDefender, ZhuluTime.CurrentYear);

                // 独立战争不触发附庸参战（宗主 vs 附庸单挑）；宣称战争/附庸战争/叛乱附庸都参战
                string typeId = pType?.id ?? "";
                bool skipVassalJoin = (typeId == "zlr_independence_war");

                // 同一宗主的附庸国之间不开战（直接跳过附庸参战逻辑）
                if (!skipVassalJoin)
                {
                    var vassalMgr = VassalManager.Instance;
                    bool sameSuzerain = vassalMgr.IsVassal(pAttacker) && vassalMgr.IsVassal(pDefender)
                        && vassalMgr.GetSuzerainId(pAttacker) == vassalMgr.GetSuzerainId(pDefender);
                    if (!sameSuzerain)
                        HandleVassalWarParticipation(__result, pAttacker, pDefender);
                }

                // 叛乱战争：原版 startRebellion 中 startWar(原国家, 叛军新国家, rebellion)
                // 所以 pAttacker = 原国家（保持原名），pDefender = 叛军新国家（得"首都+叛军"名）
                if (typeId == "rebellion" || typeId == "inspire") // 阶段2E：inspire（城主叛乱）叛军同样命名
                {
                    try
                    {
                        // 阶段2B ⑤（用户 2026-08-15 报告"宋朝变起义又变回"）：
                        // 叛乱命名排除天命国——原版某些路径（inspire/多叛军）攻守方向会反转，
                        // 若 pDefender 是天命国（原国）则跳过命名，防王朝名被覆盖
                        bool defenderIsMandate = false;
                        try { defenderIsMandate = pDefender != null && PolicyManager.IsMandateHolderForKingdom(pDefender); } catch { }
                        if (defenderIsMandate)
                        {
                            ZhuluContext.Instance.LogInfo("[WarfarePatches] 叛乱命名跳过（守方为天命国）");
                        }
                        else
                        {
                        // 叛军新国家（守方）：改名为"首都+义军"，标记为叛乱命名
                        string defenderName = Naming.ZhuluChineseNameService.Instance.GenerateRebelName(pDefender);
                        if (!string.IsNullOrEmpty(defenderName) && pDefender.data != null)
                        {
                            pDefender.data.name = defenderName;
                            Persistence.KingdomDataExt.SetStr(pDefender, "zlr_rebel_named", "1");
                        }
                        // 原国家（攻方）：保持原名，不做任何修改
                        ZhuluContext.Instance.LogInfo($"[WarfarePatches] 叛乱命名: 叛军={defenderName}, 原国={pAttacker.name}（保持原名）");
                        }
                    }
                    catch { }
                }

                // 只重新分类原版 normal 类型的战争，不干扰 rebellion/spite/inspire/自定义类型
                if (pType == null || pType.id != "normal") return;

                var classified = WarfareManager.Instance.ClassifyWar(pAttacker, pDefender);
                if (classified != null && classified.id != pType.id)
                {
                    __result.data.war_type = classified.id;
                    WarfareManager.Instance.LogWarStart(__result, pAttacker, pDefender, classified);
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[WarfarePatches] NewWarPostfix 异常", ex);
            }
        }

        /// <summary>
        /// 附庸国战争参与规则（spec 2.6）：
        /// - 宗主国发动战争：附庸国必须加入
        /// - 附庸国主动发动战争：宗主国有50%概率加入
        /// </summary>
        private static void HandleVassalWarParticipation(War war, Kingdom attacker, Kingdom defender)
        {
            var vassalMgr = VassalManager.Instance;

            // 攻方是宗主国 → 其附庸国必须加入攻方
            if (vassalMgr.GetVassals(attacker) is var attackerVassals && attackerVassals.Count > 0)
            {
                foreach (Kingdom vassal in attackerVassals)
                {
                    if (vassal == null || !vassal.isAlive()) continue;
                    if (vassal == defender) continue;
                    // 附庸正在独立战争（vs 宗主）→ 不参与宗主战争（一边独立一边帮宗主打仗 = 逻辑矛盾）
                    if (World.world.wars != null && World.world.wars.isInWarWith(vassal, attacker)) continue;
                    try { war.joinAttackers(vassal); } catch { }
                }
            }

            // 攻方是附庸国 → 宗主国必须加入攻方（100%）
            if (vassalMgr.IsVassal(attacker))
            {
                Kingdom suzerain = vassalMgr.GetSuzerain(attacker);
                if (suzerain != null && suzerain.isAlive() && suzerain != defender)
                {
                    try { war.joinAttackers(suzerain); } catch { }
                }
            }

            // 守方是宗主国 → 其附庸国必须加入守方
            if (vassalMgr.GetVassals(defender) is var defenderVassals && defenderVassals.Count > 0)
            {
                foreach (Kingdom vassal in defenderVassals)
                {
                    if (vassal == null || !vassal.isAlive()) continue;
                    if (vassal == attacker) continue;
                    // 附庸正在独立战争（vs 宗主）→ 不参与宗主战争
                    if (World.world.wars != null && World.world.wars.isInWarWith(vassal, defender)) continue;
                    try { war.joinDefenders(vassal); } catch { }
                }
            }

            // 守方是附庸国 → 宗主国必须加入守方（100%）
            if (vassalMgr.IsVassal(defender))
            {
                Kingdom suzerain = vassalMgr.GetSuzerain(defender);
                if (suzerain != null && suzerain.isAlive() && suzerain != attacker)
                {
                    try { war.joinDefenders(suzerain); } catch { }
                }
            }
        }

        /// <summary>
        /// 战争每帧更新后：调用**完整版** Check（2026-08-16 用户强烈反馈——架构重构引入大量回归，
        /// 恢复原每帧完整判定逻辑；年度评估 TickAllWars 保留为增量（分数/求和/白和/厌战），不替代即时判定）。
        /// </summary>
        static void WarUpdatePostfix(War __instance)
        {
            try
            {
                if (__instance == null || __instance.hasEnded()) return;
                string warTypeId = __instance.data?.war_type ?? "";
                if (string.IsNullOrEmpty(warTypeId))
                {
                    var asset = __instance.getAsset();
                    if (asset != null) warTypeId = asset.id;
                }

                if (warTypeId == "zlr_claim_war")
                    CheckClaimWarEnd(__instance);
                else if (warTypeId == "zlr_vassal_war")
                    CheckVassalWarEnd(__instance);
                else if (warTypeId == "zlr_independence_war")
                    CheckIndependenceWarEnd(__instance);
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[WarfarePatches] WarUpdatePostfix 异常", ex);
            }
        }

        /// <summary>宣称战轻量即时判定：宣称城离开守方（攻方/第三方控制）→ 立即停战（攻方胜/白和）。</summary>
        private static void CheckClaimWarEndLight(War war)
        {
            try
            {
                Kingdom attacker = war.getMainAttacker();
                Kingdom defender = war.getMainDefender();
                if (attacker == null || defender == null) return;
                if (!defender.isAlive())
                {
                    if (!war.hasEnded()) World.world.wars.endWar(war, WarWinner.Attackers);
                    return;
                }
                // 读宣称城（城易主判定天然安全——归属变化需要真打下来，无瞬判风险）
                string targetCityId = ClaimManager.GetWarCustomStr(war, ZhuluWarService.WAR_TARGET);
                City tc = !string.IsNullOrEmpty(targetCityId) ? ClaimManager.FindCityById(targetCityId) : null;
                if (tc == null) return;
                Kingdom cityOwner = ClaimManager.GetCityKingdom(tc);
                if (cityOwner == attacker)
                {
                    if (!war.hasEnded()) World.world.wars.endWar(war, WarWinner.Attackers);
                    ZhuluContext.Instance.LogInfo($"[WarfarePatches] 宣称战即时停战: {attacker.name} 拿下宣称城");
                    try { WorldTip.showNow($"【{attacker.name}】攻陷宣称城，{defender.name} 退让！", false, "top", 5f, UI.ZhuluUIStyle.Hex.Vermilion); } catch { }
                }
                else if (cityOwner != null && cityOwner != defender)
                {
                    // 第三方夺城 → 白和
                    if (!war.hasEnded()) World.world.wars.endWar(war, WarWinner.Nobody);
                    ZhuluContext.Instance.LogInfo($"[WarfarePatches] 宣称战白和: {attacker.name} vs {defender.name}（第三方夺城）");
                }
            }
            catch { }
        }

        /// <summary>独立战轻量即时判定：宗主首都占领度≥50% → 独立成功；附庸首都占领度≥50% → 失败。
        /// 2026-08-16：capture_ticks ≥50 阈值（不加年限门槛——快战争也应收编/独立）。</summary>
        private static void CheckIndependenceWarEndLight(War war)
        {
            try
            {
                Kingdom vassal = war.getMainAttacker();   // 附庸=攻方
                Kingdom suzerain = war.getMainDefender(); // 宗主=守方
                if (vassal == null || suzerain == null) return;

                // ── 诊断（2026-08-16 用户报告：独立战附庸被第三方吞并→B vs C 战争）──
                try
                {
                    int vCities = vassal.cities != null ? vassal.cities.Count : 0;
                    string vCapOwner = "无";
                    try
                    {
                        if (vassal.capital != null)
                        {
                            var capK = ClaimManager.GetCityKingdom(vassal.capital);
                            if (capK != null) vCapOwner = capK.name ?? "?";
                        }
                    }
                    catch { }
                    // 谁在打 vassal（除宗主外）
                    var others = new System.Collections.Generic.List<string>();
                    try
                    {
                        foreach (War w in vassal.getWars())
                        {
                            if (w == null || w.hasEnded()) continue;
                            foreach (Kingdom a in w.getAttackers())
                            {
                                if (a != null && a != vassal && a != suzerain && !others.Contains(a.name ?? ""))
                                    others.Add(a.name ?? "");
                            }
                            foreach (Kingdom d in w.getDefenders())
                            {
                                if (d != null && d != vassal && d != suzerain && !others.Contains(d.name ?? ""))
                                    others.Add(d.name ?? "");
                            }
                        }
                    }
                    catch { }
                    ZhuluContext.Instance.LogInfo($"[WarEval][诊断] 独立战: {vassal.name}(城{vCities},首都属{vCapOwner}) vs {suzerain.name}；第三方涉战:{string.Join("/", others)}==");
                }
                catch { }

                // 2026-08-16 补回（用户指正）：坚持 5 年 → 独立成功（原 CheckIndependenceWarEnd 双路径之二）
                string sy = ClaimManager.GetWarCustomStr(war, ZhuluWarService.WAR_START);
                int startYear = 0; int.TryParse(sy ?? "", out startYear);
                if (startYear > 0 && ZhuluTime.CurrentYear - startYear >= 5)
                {
                    if (!VassalManager.Instance.IsVassal(vassal)) return; // 已非附庸（已独立）
                    VassalManager.Instance.ReleaseVassal(suzerain, vassal);
                    if (!war.hasEnded()) World.world.wars.endWar(war, WarWinner.Attackers);
                    ZhuluContext.Instance.LogInfo($"[WarfarePatches] 独立战坚持5年成功: {vassal.name} 独立！");
                    try { WorldTip.showNow($"【{vassal.name}】坚持五载，独立成功！", false, "top", 6f, UI.ZhuluUIStyle.Hex.Positive); } catch { }
                    return;
                }

                // 宗主首都沦陷过半 → 独立成功
                if (suzerain.capital != null && suzerain.capital.isGettingCapturedBy(vassal)
                    && suzerain.capital.last_visual_capture_ticks >= 50f)
                {
                    VassalManager.Instance.ReleaseVassal(suzerain, vassal);
                    if (!war.hasEnded()) World.world.wars.endWar(war, WarWinner.Attackers);
                    ZhuluContext.Instance.LogInfo($"[WarfarePatches] 独立战即时成功: {vassal.name} 攻破宗主首都，独立！");
                    try { WorldTip.showNow($"【{vassal.name}】攻破宗主首都，独立成功！", false, "top", 6f, UI.ZhuluUIStyle.Hex.Positive); } catch { }
                }
                else if (vassal.capital != null && vassal.capital.isGettingCapturedBy(suzerain)
                    && vassal.capital.last_visual_capture_ticks >= 50f)
                {
                    // 附庸首都沦陷过半 → 独立失败（继续附庸）
                    if (!war.hasEnded()) World.world.wars.endWar(war, WarWinner.Defenders);
                    ZhuluContext.Instance.LogInfo($"[WarfarePatches] 独立战即时失败: {vassal.name} 首都沦陷");
                }
            }
            catch { }
        }

        /// <summary>
        /// 附庸战轻量即时判定：守方首都占领度≥50% → 立即收编为附庸。
        /// 2026-08-16 修复链：isGettingCapturedBy 无阈值（1 个攻城单位即 true）→ 必须看
        /// last_visual_capture_ticks ≥50（占领度本身需要时间/兵力优势，天然防瞬判；
        /// 不加"开战年限"门槛——20 倍速下快战争 1 年内完成也应收编，年限门槛会误伤）。
        /// </summary>
        private static void CheckVassalWarEndLight(War war)
        {
            try
            {
                Kingdom attacker = war.getMainAttacker();
                Kingdom defender = war.getMainDefender();
                if (attacker == null || defender == null) return;
                if (!defender.isAlive()) return;
                if (defender.capital == null) return;
                // 首都占领度 ≥50%（isGettingCapturedBy 无阈值，必须看进度）
                if (!defender.capital.isGettingCapturedBy(attacker)) return;
                if (defender.capital.last_visual_capture_ticks < 50f) return;
                // 收编
                VassalManager.Instance.MakeVassal(attacker, defender);
                if (!war.hasEnded()) World.world.wars.endWar(war, WarWinner.Attackers);
                ZhuluContext.Instance.LogInfo($"[WarfarePatches] 附庸战即时收编: {defender.name} → {attacker.name} 附庸");
                try { WorldTip.showNow($"【{defender.name}】首都沦陷过半，臣服于【{attacker.name}】！", false, "top", 6f, UI.ZhuluUIStyle.Hex.Vermilion); } catch { }
            }
            catch { }
        }

        /// <summary>宣称战争：目标城市离开守方即停战；超时5年也停战。防止灭国。</summary>
        private static void CheckClaimWarEnd(War war)
        {
            Kingdom attacker = war.getMainAttacker();
            Kingdom defender = war.getMainDefender();
            if (attacker == null || defender == null) return;

            long warId = war.getID();

            // 尝试读取目标城市
            string targetCityId = null;
            if (ClaimWarTargets.ContainsKey(warId))
                targetCityId = ClaimWarTargets[warId];
            if (string.IsNullOrEmpty(targetCityId))
                targetCityId = ClaimManager.GetWarCustomStr(war, Domain.ZhuluWarService.WAR_TARGET); // 2026-08-19 修复：原误写 "zlr_claim_city"（从未写入的 key）致首选宣称城恒失效

            City targetCity = !string.IsNullOrEmpty(targetCityId) ? ClaimManager.FindCityById(targetCityId) : null;

            // 读取开始年份
            int startYear = 0;
            if (ClaimWarStartYears.ContainsKey(warId))
                startYear = ClaimWarStartYears[warId];

            // 使用 ClaimRules 纯判定
            if (ClaimRules.ShouldEndClaimWar(targetCity, defender, startYear, ZhuluTime.CurrentYear))
            {
                bool timeout = startYear > 0 && ZhuluTime.CurrentYear - startYear >= ClaimRules.ClaimWarMaxYears;
                bool defenderDead = defender == null || !defender.isAlive();

                // 阶段2B ①：胜负按宣称城归属判定（2026-08-15 修复第三方夺城误判攻方胜）
                // a. 宣称城易主：攻方控制→攻方胜；第三方夺城/其他原因→白和 Nobody
                // c. 超时→守方胜
                WarWinner winner;
                Kingdom cityOwner = targetCity != null ? ClaimManager.GetCityKingdom(targetCity) : null;
                if (cityOwner == attacker) winner = WarWinner.Attackers;
                else if (cityOwner != null && cityOwner != defender) winner = WarWinner.Nobody;
                else winner = WarWinner.Defenders;

                World.world.wars.endWar(war, winner);
                ClaimWarTargets.Remove(warId);
                ClaimWarStartYears.Remove(warId);
                ZhuluContext.Instance.LogInfo($"[WarfarePatches] 宣称战争结束: 胜者={winner} (超时={timeout})");
                return;
            }

            // b. 守方求和（2026-08-19 意愿权重制）：战力/战况/厌战/年限四维意愿 ≥60 → 真结算
            //    （割宣称城 + 按优势度分级赔款——原版只 endWar 弹"割让"文案，城从未转手——用户实锤）
            //    1 城国家不求和（割让=灭国，死战到底或超时停战）
            bool defDead = defender == null || !defender.isAlive();
            if (!defDead)
            {
                try
                {
                    int defCityCount = defender.cities != null ? defender.cities.Count : 0;
                    if (defCityCount <= 1) return; // 1城国家不求和（割让即灭国）
                    // 意愿评估用最近年度评分（不在此处重算——EvaluateWarScore 有累加副作用）
                    int storedScore = 0;
                    int.TryParse(ClaimManager.GetWarCustomStr(war, Domain.ZhuluWarService.WAR_SCORE) ?? "", out storedScore);
                    int will = Domain.ZhuluWarService.GetPeaceWillingness(defender, attacker, war, storedScore, ZhuluTime.CurrentYear);
                    if (will >= Domain.ZhuluWarService.PEACE_WILLINGNESS_DEFENDER)
                    {
                        string terms = Domain.ZhuluWarService.ApplyWarSettlement(war, attacker, defender, storedScore, targetCity);
                        World.world.wars.endWar(war, WarWinner.Attackers);
                        ClaimWarTargets.Remove(warId);
                        ClaimWarStartYears.Remove(warId);
                        ZhuluContext.Instance.LogInfo($"[WarfarePatches] 宣称战争守方求和: {defender.name} {terms}（意愿 {will}）");
                        try { WorldTip.showNow($"【{defender.name}】力不能支，{terms}！", false, "top", 6f, UI.ZhuluUIStyle.Hex.Azure); } catch { }
                    }
                }
                catch { }
            }
        }

        /// <summary>附庸战争：守方首都占领度>=50%则附庸成功；攻方首都被破则战争结束。防止完全占领。</summary>
        private static void CheckVassalWarEnd(War war)
        {
            Kingdom attacker = war.getMainAttacker();
            Kingdom defender = war.getMainDefender();
            if (attacker == null || defender == null) return;

            // 使用 ClaimRules 纯判定
            var outcome = ClaimRules.CheckVassalWarOutcome(attacker, defender);
            switch (outcome)
            {
                case VassalWarOutcome.AttackerCapitalFallen:
                    World.world.wars.endWar(war, WarWinner.Defenders);
                    break;
                case VassalWarOutcome.DefenderCapitalCaptured:
                case VassalWarOutcome.DefenderCapitalProgress:
                    VassalManager.Instance.MakeVassal(attacker, defender);
                    World.world.wars.endWar(war, WarWinner.Attackers);
                    break;
            }
        }

        /// <summary>独立战争：附庸国打下宗主首都或坚持5年则独立成功；附庸首都被破则失败（不被吞并）。</summary>
        private static void CheckIndependenceWarEnd(War war)
        {
            Kingdom vassal = war.getMainAttacker();   // 附庸国是攻方
            Kingdom suzerain = war.getMainDefender(); // 宗主国是守方
            if (vassal == null || suzerain == null) return;

            long warId = war.getID();

            // 读取开始年份
            int warStartYear = 0;
            if (IndependenceWarStartYears.ContainsKey(warId))
                warStartYear = IndependenceWarStartYears[warId];
            else
            {
                string startYearStr = ClaimManager.GetWarCustomStr(war, "zlr_war_start_year");
                if (!string.IsNullOrEmpty(startYearStr) && int.TryParse(startYearStr, out int parsed))
                    warStartYear = parsed;
            }

            // 读取开战时附庸首都城市 id（首都沦陷判负）
            int vassalStartCapitalId = 0;
            string capitalStr = ClaimManager.GetWarCustomStr(war, "zlr_ind_war_start_capital");
            if (!string.IsNullOrEmpty(capitalStr) && int.TryParse(capitalStr, out int parsedCapital))
                vassalStartCapitalId = parsedCapital;

            // 使用 ClaimRules 纯判定
            var outcome = ClaimRules.CheckIndependenceWarOutcome(vassal, suzerain, warStartYear, ZhuluTime.CurrentYear, vassalStartCapitalId);
            switch (outcome)
            {
                case IndependenceWarOutcome.VassalCapitalFallen:
                    // 独立失败（不被吞并，只设冷却）
                    ZhuluContext.Instance.LogInfo($"[WarfarePatches] 独立战争失败：{vassal.name} 首都沦陷（{ZhuluTime.CurrentYear}年）");
                    VassalManager.Instance.SetIndependenceCooldown(vassal);
                    World.world.wars.endWar(war, WarWinner.Defenders);
                    IndependenceWarStartYears.Remove(warId);
                    break;
                case IndependenceWarOutcome.SuzerainCapitalFallen:
                case IndependenceWarOutcome.TimeExpired:
                    // 独立成功
                    VassalManager.Instance.ReleaseVassal(suzerain, vassal);
                    World.world.wars.endWar(war, WarWinner.Attackers);
                    IndependenceWarStartYears.Remove(warId);
                    break;
            }
        }

        /// <summary>战争结束后：录入史册 + 战争赔款（阶段1 F5，仅文明国间战争）。</summary>
        static void EndWarPostfix(WarManager __instance, War pWar, WarWinner pWinner)
        {
            try
            {
                if (pWar == null) return;
                // 2026-08-16 兜底：附庸战任何方式结束——若攻方胜且守方仍有存活城市 → 转收编（防吞并）
                // P0-3：静默失败=附庸战白打，留限频信号
                ZhuluGuard.Run("Warfare.EndWar.附庸兜底收编", () =>
                {
                    string wt = pWar.data?.war_type ?? "";
                    if (wt == "zlr_vassal_war" && pWinner == WarWinner.Attackers)
                    {
                        var atkK = pWar.getMainAttacker();
                        var defK = pWar.getMainDefender();
                        if (atkK != null && defK != null && defK.isAlive()
                            && defK.cities != null && defK.cities.Count > 0
                            && !VassalManager.Instance.IsVassal(defK))
                        {
                            VassalManager.Instance.MakeVassal(atkK, defK);
                            ZhuluContext.Instance.LogInfo($"[WarfarePatches] 附庸战结算兜底收编: {defK.name} → {atkK.name}");
                        }
                    }
                });
                // D1 战争终结插桩（2026-10-06）：全部 endWar 出口（39 处调用）流经本 Postfix，一战一行
                try
                {
                    string wsEnd = ClaimManager.GetWarCustomStr(pWar, ZhuluWarService.WAR_START);
                    int.TryParse(wsEnd ?? "", out int endStartYear);
                    var endAtk = pWar.getMainAttacker();
                    var endDef = pWar.getMainDefender();
                    ZhuluTrace.Write("war_trace.csv",
                        $"war_end,{ZhuluTime.CurrentYear},{pWar.getID()},{pWar.data?.war_type ?? ""},{pWinner},{endAtk?.name ?? "?"},{endDef?.name ?? "?"},{endStartYear},{(endStartYear > 0 ? ZhuluTime.CurrentYear - endStartYear : -1)}");
                }
                catch { }
                WarfareManager.Instance.LogWarEnd(pWar, pWinner);
                HandleRebellionWarEnd(pWar, pWinner);
                HandleWarReparation(pWar, pWinner);
                // 阶段2B 包3：厌战胜负修正（战败 +15 / 胜利 −5）——补齐设计缺口（自检发现）
                // P0-3：静默失败=厌战失真，留限频信号
                ZhuluGuard.Run("Warfare.EndWar.厌战修正", () =>
                {
                    if (pWinner == WarWinner.Attackers)
                    {
                        var atkK = pWar.getMainAttacker();
                        var defK = pWar.getMainDefender();
                        if (atkK != null) ZhuluWarService.AddWarExhaustion(atkK.id.ToString(), -5);
                        if (defK != null) ZhuluWarService.AddWarExhaustion(defK.id.ToString(), 15);
                    }
                    else if (pWinner == WarWinner.Defenders)
                    {
                        var atkK = pWar.getMainAttacker();
                        var defK = pWar.getMainDefender();
                        if (atkK != null) ZhuluWarService.AddWarExhaustion(atkK.id.ToString(), 15);
                        if (defK != null) ZhuluWarService.AddWarExhaustion(defK.id.ToString(), -5);
                    }
                });
                // P2-30 串联②（2026-08-23）：战争结算回写朝廷——胜方执政党党势+3、败方执政党−5。
                // 主和党在败国抬头、主战党在胜国坐大——战争胜负第一次直接改写朝局力量对比。
                ZhuluGuard.Run("Warfare.EndWar.党势回写", () =>
                {
                    var wAtk = pWar.getMainAttacker();
                    var wDef = pWar.getMainDefender();
                    if (pWinner == WarWinner.Attackers)
                    {
                        if (wAtk != null) FactionPoliticsManager.Instance.AdjustRulingFactionPower(wAtk, wAtk.id.ToString(), 3f);
                        if (wDef != null) FactionPoliticsManager.Instance.AdjustRulingFactionPower(wDef, wDef.id.ToString(), -5f);
                    }
                    else if (pWinner == WarWinner.Defenders)
                    {
                        if (wDef != null) FactionPoliticsManager.Instance.AdjustRulingFactionPower(wDef, wDef.id.ToString(), 3f);
                        if (wAtk != null) FactionPoliticsManager.Instance.AdjustRulingFactionPower(wAtk, wAtk.id.ToString(), -5f);
                    }
                });
                // P2-30 断贡问罪收尾：无论胜负两清欠贡；宗主胜另惩附庸稳定并昭告
                ZhuluGuard.Run("Warfare.EndWar.问罪收尾", () =>
                {
                    if ((pWar.data?.war_type ?? "") != "zlr_punitive_war") return;
                    var sK = pWar.getMainAttacker();
                    var vK = pWar.getMainDefender();
                    if (sK != null) Persistence.KingdomDataExt.SetLong(sK, "tribute_deficit", 0);
                    if (vK != null) Persistence.KingdomDataExt.SetLong(vK, "tribute_deficit", 0);
                    if (pWinner == WarWinner.Attackers && vK != null && vK.isAlive())
                    {
                        StabilityManager.Instance.AddExternalBonus(vK.id.ToString(), -5f);
                        try { WorldTip.showNow($"【{vK.name}】请罪纳贡，国势受挫", false, "top", 5f, "#FF9A76"); } catch { }
                    }
                });

                // P2-30 串联②b：败国君主角标"败绩"入个人史册（盖棺评语与朝争叙事的素材源）
                ZhuluGuard.Run("Warfare.EndWar.君主败绩史册", () =>
                {
                    Kingdom loserK = pWinner == WarWinner.Attackers ? pWar.getMainDefender()
                                   : pWinner == WarWinner.Defenders ? pWar.getMainAttacker() : null;
                    Kingdom winnerK = pWinner == WarWinner.Attackers ? pWar.getMainAttacker()
                                    : pWinner == WarWinner.Defenders ? pWar.getMainDefender() : null;
                    if (loserK == null || !loserK.isAlive()) return;
                    if (loserK.king == null || !loserK.king.isAlive()) return;
                    string winnerName = winnerK != null ? (winnerK.name ?? "敌国") : "敌国";
                    PersonHistoryManager.Instance.RecordEvent(loserK.king.getID(), PersonHistoryManager.CAT_JUNLIN,
                        $"督师失利，败于【{winnerName}】——丧师辱国，朝廷震动", ZhuluTime.CurrentYear);
                });
                // 阶段2C 邦交①：和平条约/停战期（文明国间战争结束后 5 年停战，期内禁互相宣战）
                // P0-3：静默失败=停战期丢失，留限频信号
                ZhuluGuard.Run("Warfare.EndWar.停战条约", () =>
                {
                    if (pWinner != WarWinner.Nobody && pWinner != WarWinner.Merged)
                    {
                        var atkK = pWar.getMainAttacker();
                        var defK = pWar.getMainDefender();
                        if (atkK != null && defK != null && atkK.isAlive() && defK.isAlive())
                        {
                            int truceUntil = ZhuluTime.CurrentYear + ZhuluDiplomacyService.TRUCE_YEARS;
                            ZhuluDiplomacyService.SetTruce(atkK.id.ToString(), defK.id.ToString(), truceUntil);
                            ZhuluDiplomacyService.SetTruce(defK.id.ToString(), atkK.id.ToString(), truceUntil);
                            ZhuluContext.Instance.LogInfo($"[Diplomacy] {atkK.name} 与 {defK.name} 签订和约，停战 {ZhuluDiplomacyService.TRUCE_YEARS} 年");
                            ZhuluGuard.Run("Warfare.EndWar.和约史册", () =>
                                ChronicleManager.Instance.AddEntry(ZhuluTime.CurrentYear, ChronicleEventType.PeaceTreaty,
                                    atkK.id.ToString(), atkK.name ?? "", defK.name ?? "",
                                    $"<color={UI.ZhuluUIStyle.Hex.Azure}>{atkK.name}</color> 与 <color=#6ca3fd>{defK.name}</color> 签订和约，休战 {ZhuluDiplomacyService.TRUCE_YEARS} 年。", false));
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[WarfarePatches] EndWarPostfix 异常", ex);
            }
        }

        /// <summary>
        /// 战争赔款（阶段1 F5）：仅 zlr 三种战争 + rebellion 结算（正常文明战争）；
        /// 赔款 = 20 + (胜方国王智+外)×2 + 败方死亡×2，从败方国库扣，不足记欠款年追缴10%。
        /// </summary>
        private static void HandleWarReparation(War war, WarWinner winner)
        {
            try
            {
                if (war == null) return;
                // 白和/ 议和没有战胜方。旧代码把 Peace 落入 else 分支，
                // 误将守方当战胜方并向攻方追赔，会让“和谈”变成隐形战败。
                if (winner != WarWinner.Attackers && winner != WarWinner.Defenders) return;
                // 2026-08-19 修复双重赔款：求和条款结算（ApplyWarSettlement）已执行割城/赔款，
                // 此处再扣即"条款外暗账"（文本写赔30金实际扣两笔）——已结算战争跳过
                // P0-3：此检查失败若继续走会双扣赔款，故失败时直接放弃本函数并留信号
                try
                {
                    if (ClaimManager.GetWarCustomStr(war, Domain.ZhuluWarService.WAR_SETTLED) == "1") return;
                }
                catch (Exception exSettled)
                {
                    ZhuluContext.Instance.LogRateLimited("Warfare.Reparation.SettledCheck",
                        "[WarfarePatches] 赔款防双扣检查异常，本次跳过赔款结算: " + exSettled.Message, 30f);
                    return;
                }
                // 排除叛乱战争（叛军无国库概念，由叛乱结算处理）
                string wt = war.data?.war_type ?? "";
                if (string.IsNullOrEmpty(wt))
                {
                    var asset = war.getAsset();
                    if (asset != null) wt = asset.id;
                }
                if (wt == "rebellion" || wt == "inspire") return;
                // 阶段3H-20：附庸战臣服=今后朝贡（不再另收旧公式赔款——用户实锤"臣服还赔款"双收）；
                // 问鼎战失败已有专门惩罚（稳定/礼治/20年冷却），不叠加金币暗账
                if (wt == "zlr_vassal_war" || wt == "zlr_mandate_war") return;

                Kingdom winnerK = null, loserK = null;
                int winnerIntDip = 0, loserDeaths = 0;
                // getAttackers()/getDefenders() 返回 IEnumerable，手动取第一个
                if (winner == WarWinner.Attackers)
                {
                    winnerK = war.main_attacker;
                    if (winnerK == null) { foreach (Kingdom k in war.getAttackers()) { winnerK = k; break; } }
                    loserK = war.main_defender;
                    if (loserK == null) { foreach (Kingdom k in war.getDefenders()) { loserK = k; break; } }
                }
                else
                {
                    winnerK = war.main_defender;
                    if (winnerK == null) { foreach (Kingdom k in war.getDefenders()) { winnerK = k; break; } }
                    loserK = war.main_attacker;
                    if (loserK == null) { foreach (Kingdom k in war.getAttackers()) { loserK = k; break; } }
                }
                if (winnerK == null || loserK == null || !winnerK.isAlive() || !loserK.isAlive()) return;

                if (winnerK.king != null)
                {
                    try { winnerIntDip = winnerK.king.intelligence + winnerK.king.diplomacy; } catch { }
                }
                // 败方死亡数：攻方败→dead_attackers；守方败→dead_defenders
                // 2026-08-19 修复：原反射 War.died_attackers 字段不存在（恒 null → 死亡项恒 0），
                // 改用 war.data 计数器（与 ZhuluWarService.EvaluateWarScore 同一已验证 API）
                try
                {
                    if (war.data != null)
                        loserDeaths = winner == WarWinner.Defenders ? war.data.dead_attackers : war.data.dead_defenders;
                }
                catch { }

                Domain.TreasuryManager.ApplyWarReparation(winnerK, loserK, winnerIntDip, loserDeaths);
            }
            catch (Exception exRep)
            {
                // P0-3：赔款结算整体失败=经济后果丢失，必须留信号（限频30s防刷屏）
                ZhuluContext.Instance.LogRateLimited("Warfare.Reparation.Settle",
                    "[WarfarePatches] 战争赔款结算异常: " + exRep.Message, 30f);
            }
        }

        /// <summary>
        /// 叛乱战争结算：叛军战败（原国胜）→ 叛军直接被灭；叛军胜利或战争结束（含和局）→ 转正为正式国号。
        /// 叛乱方按 zlr_rebel_named 标记识别（不依赖攻守方向，防原版路径方向不一致）。
        /// </summary>
        private static void HandleRebellionWarEnd(War war, WarWinner winner)
        {
            try
            {
                string warTypeId = war.data?.war_type ?? "";
                if (string.IsNullOrEmpty(warTypeId))
                {
                    var asset = war.getAsset();
                    if (asset != null) warTypeId = asset.id;
                }
                if (warTypeId != "rebellion" && warTypeId != "inspire") return;

                Kingdom attacker = war.getMainAttacker();
                Kingdom defender = war.getMainDefender();

                // 叛军 = 带标记的一方（方向自适应）
                Kingdom rebel = null;
                if (attacker != null && Persistence.KingdomDataExt.GetStr(attacker, "zlr_rebel_named") == "1") rebel = attacker;
                else if (defender != null && Persistence.KingdomDataExt.GetStr(defender, "zlr_rebel_named") == "1") rebel = defender;
                if (rebel == null || !rebel.isAlive()) return;

                // 叛军是否获胜（按实际方向换算）
                bool rebelWon = (rebel == attacker && winner == WarWinner.Attackers)
                             || (rebel == defender && winner == WarWinner.Defenders);

                bool negotiatedPeace = winner == WarWinner.Peace || winner == WarWinner.Nobody;

                if (rebelWon || negotiatedPeace)
                {
                    // 叛军胜利或长期和谈：转正为正式国号。
                    // 和谈代表原国默认其事实独立，不能再走“叛军战败直接删国”分支。
                    ZhuluContext.Instance.LogInfo(negotiatedPeace
                        ? $"[WarfarePatches] 叛乱议和：{rebel.name} 获承认并转正为正式国号"
                        : $"[WarfarePatches] 叛乱成功：{rebel.name} 转正为正式国号");
                    Naming.ZhuluChineseNameService.Instance.SetKingdomName(rebel);
                    Persistence.KingdomDataExt.SetStr(rebel, "zlr_rebel_named", "");
                }
                else
                {
                    // 叛军战败（含和局外的一切败局）：直接被灭
                    ZhuluContext.Instance.LogInfo($"[WarfarePatches] 叛乱镇压：{rebel.name} 战败被灭");
                    Persistence.KingdomDataExt.SetStr(rebel, "zlr_rebel_named", "");
                    try { World.world.kingdoms.removeObject(rebel); }
                    catch (Exception ex2) { ZhuluContext.Instance.LogError("[WarfarePatches] 灭叛军异常", ex2); }
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[WarfarePatches] 叛乱结算异常", ex);
            }
        }
    
        // ═══════════ v2 快照存档接口（静态战争附加数据落盘，防丢失） ═══════════
        // ---- batch10-A1: schema v5 section ----
        public sealed class WarfareState
        {
            public Dictionary<long, string> ClaimTargets = new Dictionary<long, string>();
            public Dictionary<long, int> ClaimStartYears = new Dictionary<long, int>();
            public Dictionary<long, int> IndependenceStartYears = new Dictionary<long, int>();
        }
        public sealed class WarfareSection : ISnapshotSection<WarfareState>
        {
            public string SectionName => "warfare";
            public Dictionary<string, WarfareState> CaptureStates()
            {
                var st = new WarfareState();
                foreach (var kv in ClaimWarTargets) st.ClaimTargets[kv.Key] = kv.Value;
                foreach (var kv in ClaimWarStartYears) st.ClaimStartYears[kv.Key] = kv.Value;
                foreach (var kv in IndependenceWarStartYears) st.IndependenceStartYears[kv.Key] = kv.Value;
                return new Dictionary<string, WarfareState> { ["__global__"] = st };
            }
            public void ApplyStates(Dictionary<string, WarfareState> data)
            {
                if (data == null || !data.TryGetValue("__global__", out var st) || st == null) return;
                ClaimWarTargets.Clear();
                ClaimWarStartYears.Clear();
                IndependenceWarStartYears.Clear();
                if (st.ClaimTargets != null) foreach (var kv in st.ClaimTargets) ClaimWarTargets[kv.Key] = kv.Value;
                if (st.ClaimStartYears != null) foreach (var kv in st.ClaimStartYears) ClaimWarStartYears[kv.Key] = kv.Value;
                if (st.IndependenceStartYears != null) foreach (var kv in st.IndependenceStartYears) IndependenceWarStartYears[kv.Key] = kv.Value;
            }
        }

        public static void ResetRuntime()
        {
            ClaimWarTargets.Clear();
            ClaimWarStartYears.Clear();
            IndependenceWarStartYears.Clear();
        }

}
}
