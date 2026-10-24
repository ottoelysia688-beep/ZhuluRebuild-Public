using System;
using System.Collections.Generic;
using UnityEngine;
using NeoModLoader.General;
using ZhuluRebuild.Core;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Domain
{
    /// <summary>
    /// 宣称记录：某国对某城市的领土宣称。
    /// </summary>
    [Serializable]
    public class ClaimRecord
    {
        public string ClaimantKingdomId;
        public string CityId;
        public string OriginalKingdomId;
        public int StartYear;
        public int ExpireYear;
    }

    /// <summary>
    /// 宣称管理器：注册"谋取宣称"谋划到原版谋划系统，管理宣称数据，AI年度判定是否发动宣称战争。
    /// </summary>
    public class ClaimManager : ISnapshotSection<ClaimManager.ClaimState>
    {
        public static ClaimManager Instance { get; } = new ClaimManager();

        private const string KEY_CLAIMS = "claims";
        private const string KEY_LAST_CLAIM_YEAR = "last_claim_year";
        private const string KEY_LAST_CLAIM_WAR_YEAR = "last_claim_war_year"; // 阶段2B 4：宣称战争冷却记录
        private const int CLAIM_DURATION = 50;
        private const int CLAIM_COOLDOWN = 10; // 10年只能谋划1次宣称
        private const int CLAIM_WAR_COOLDOWN = 3; // 阶段2B 4：宣称战后 3 年冷却
        private ClaimManager() { }

        // ───────────────────────────── 谋划注册 ─────────────────────────────

        /// <summary>注册"谋取宣称"和"宣称战争"谋划到原版谋划系统。</summary>
        public void RegisterPlot()
        {
            try
            {
                // ═══ 谋划1：谋取宣称 ═══
                var claimPlot = new PlotAsset
                {
                    id = "zlr_fake_claim",
                    is_basic_plot = true,
                    path_icon = "plots/icons/plot_new_war",
                    group_id = "diplomacy",
                    can_be_done_by_king = true,
                    check_target_city = true,
                    progress_needed = 80f,   // 进度条较长，避免初期频繁谋划
                    min_level = 5,           // 国王等级至少5
                    money_cost = 20,
                    check_is_possible = (Actor pActor) =>
                    {
                        if (pActor == null || !pActor.hasKingdom()) return false;
                        // 纯判定委托给 ClaimRules
                        if (!ClaimRules.CanPlotClaim(pActor.kingdom, ZhuluTime.CurrentYear)) return false;
                        // 兵力检查：己方兵力 >= 双方平均值
                        return HasWeakerEnemyCity(pActor.kingdom);
                    },
                    try_to_start_advanced = (Actor pActor, PlotAsset pAsset, bool pForced) =>
                    {
                        City target = GetWeakerEnemyCity(pActor.kingdom);
                        if (target == null) return false;
                        Plot plot = World.world.plots.newPlot(pActor, pAsset, pForced);
                        plot.target_city = target;
                        // 同时设置 target_kingdom 以便谋划描述显示目标国名
                        plot.target_kingdom = GetCityKingdom(target);
                        return true;
                    },
                    check_should_continue = (Actor pActor) =>
                    {
                        if (pActor == null || pActor.plot == null) return false;
                        return pActor.plot.target_city != null;
                    },
                    action = (Actor pActor) =>
                    {
                        City city = pActor.plot.target_city;
                        if (city == null || pActor.kingdom == null) return false;
                        AddClaim(pActor.kingdom, city);
                        // 记录本次宣称年份（用于冷却判定）
                        KingdomDataExt.SetStr(pActor.kingdom, KEY_LAST_CLAIM_YEAR, ZhuluTime.CurrentYear.ToString());
                        return true;
                    }
                };
                claimPlot.check_can_be_forced = claimPlot.check_is_possible;
                // 设置谋划描述格式化（游戏用此委托在进度条下方显示"某人对某国谋划..."）
                claimPlot.get_formatted_description = FormatPlotDescription;

                AssetManager.plots_library.add(claimPlot);
                AssetManager.plots_library.basic_plots.Add(claimPlot);

                LM.AddToCurrentLocale("plot_zlr_fake_claim", "谋取宣称");
                LM.AddToCurrentLocale("plot_zlr_fake_claim_info", "$initiator_actor$ 正谋划对 $target_kingdom$ 谋取宣称");
                LM.AddToCurrentLocale("plot_zlr_fake_claim_info_base", "谋划夺取邻国城市的领土宣称，完成后可发动宣称战争");

                // ═══ 谋划2：宣称战争（有宣称后AI自动发动） ═══
                var claimWarPlot = new PlotAsset
                {
                    id = "zlr_claim_war_plot",
                    is_basic_plot = true,
                    path_icon = "plots/icons/plot_new_war",
                    group_id = "diplomacy",
                    can_be_done_by_king = true,
                    check_target_kingdom = true,
                    progress_needed = 30f,
                    min_level = 5,
                    money_cost = 15,
                    check_is_possible = (Actor pActor) =>
                    {
                        if (pActor == null || !pActor.hasKingdom()) return false;
                        // 外交关闭时不允许发动战争
                        if (!WorldLawLibrary.world_law_diplomacy.isEnabled()) return false;
                        // 已在战争中不允许再发动
                        if (pActor.kingdom.getWars().GetEnumerator().MoveNext()) return false;
                        // 必须有宣称才能发动
                        var claims = GetClaims(pActor.kingdom);
                        if (claims.Count <= 0) return false;
                        // 2026-08-15 调参：兵力检查（防 AI 反复宣战被拒——南郡子国 8 次被拒现象）
                        try
                        {
                            Kingdom target = pActor.plot != null ? pActor.plot.target_kingdom : null;
                            if (target != null && target.isAlive())
                            {
                                if (pActor.kingdom.countTotalWarriors() < target.countTotalWarriors()) return false;
                            }
                        }
                        catch { }
                        return true;
                    },
                    try_to_start_advanced = (Actor pActor, PlotAsset pAsset, bool pForced) =>
                    {
                        var claims = GetClaims(pActor.kingdom);
                        if (claims.Count == 0) return false;
                        // 找到第一个有效宣称的目标国
                        foreach (var claim in claims)
                        {
                            City city = FindCityById(claim.CityId);
                            if (city == null) continue;
                            Kingdom target = GetCityKingdom(city);
                            if (target == null) continue;
                            // 只排除自己/自己的宗主/自己的附庸/已互战；其他人的附庸允许成为目标。
                            if (ClaimRules.IsForbiddenTarget(pActor.kingdom, target)) continue;
                            // 2026-08-15 调参 2b：兵力检查在此处（target 已定——check 时 plot 未建拿不到目标）
                            try
                            {
                                if (pActor.kingdom.countTotalWarriors() < target.countTotalWarriors()) continue;
                            }
                            catch { }
                            Plot plot = World.world.plots.newPlot(pActor, pAsset, pForced);
                            plot.target_kingdom = target;
                            return true;
                        }
                        return false;
                    },
                    check_should_continue = (Actor pActor) =>
                    {
                        if (pActor == null || pActor.plot == null) return false;
                        Kingdom target = pActor.plot.target_kingdom;
                        return target != null && target.isAlive();
                    },
                    action = (Actor pActor) =>
                    {
                        Kingdom target = pActor.plot.target_kingdom;
                        if (target == null || pActor.kingdom == null) return false;
                        // 找到对应宣称
                        var claims = GetClaims(pActor.kingdom);
                        foreach (var claim in claims)
                        {
                            City city = FindCityById(claim.CityId);
                            if (city == null) continue;
                            if (GetCityKingdom(city) == target)
                            {
                                return StartClaimWar(pActor.kingdom, target, claim);
                            }
                        }
                        return false;
                    }
                };
                claimWarPlot.check_can_be_forced = claimWarPlot.check_is_possible;
                claimWarPlot.get_formatted_description = FormatPlotDescription;

                AssetManager.plots_library.add(claimWarPlot);
                AssetManager.plots_library.basic_plots.Add(claimWarPlot);

                // ═══ 阶段2B 包3：全面战争谋划（五重门槛：国力碾压/宣称≥2/厌战<30/世仇/20年冷却）═══
                var totalWarPlot = new PlotAsset
                {
                    id = "zlr_total_war_plot",
                    is_basic_plot = true,
                    path_icon = "plots/icons/plot_new_war",
                    group_id = "diplomacy",
                    can_be_done_by_king = true,
                    check_target_kingdom = true,
                    progress_needed = 60f, // 全面战争谋划更久（灭国大事，蓄谋更深）
                    min_level = 10,
                    money_cost = 40,
                    check_is_possible = (Actor pActor) =>
                    {
                        if (pActor == null || !pActor.hasKingdom()) return false;
                        if (!WorldLawLibrary.world_law_diplomacy.isEnabled()) return false;
                        if (pActor.kingdom.getWars().GetEnumerator().MoveNext()) return false;
                        // check 阶段通常尚未创建 Plot；只做确定性“是否存在目标”检查，不能在 UI/AI 探测时消耗随机数。
                        Kingdom target = pActor.plot != null ? pActor.plot.target_kingdom : null;
                        return target != null
                            ? ZhuluWarService.CanTotalWar(pActor.kingdom, target, ZhuluTime.CurrentYear)
                            : HasTotalWarTarget(pActor.kingdom);
                    },
                    try_to_start_advanced = (Actor pActor, PlotAsset pAsset, bool pForced) =>
                    {
                        Kingdom target = GetTotalWarTarget(pActor.kingdom);
                        if (target == null) return false;
                        Plot plot = World.world.plots.newPlot(pActor, pAsset, pForced);
                        plot.target_kingdom = target;
                        return true;
                    },
                    check_should_continue = (Actor pActor) =>
                    {
                        if (pActor == null || pActor.plot == null) return false;
                        Kingdom target = pActor.plot.target_kingdom;
                        return target != null && target.isAlive();
                    },
                    action = (Actor pActor) =>
                    {
                        Kingdom target = pActor.plot.target_kingdom;
                        if (target == null || pActor.kingdom == null) return false;
                        if (!ZhuluWarService.CanTotalWar(pActor.kingdom, target, ZhuluTime.CurrentYear)) return false;
                        War war = WarGateway.DeclareWar(pActor.kingdom, target, WarfareManager.TotalWar, "全面战争");
                        if (war != null)
                        {
                            KingdomDataExt.SetLong(pActor.kingdom, "last_total_war", ZhuluTime.CurrentYear);
                            // 天下震惊：好感惩罚
                            try
                            {
                                Gameplay.ModifierPipeline.Instance.RegisterPlan(new Gameplay.ModifierPlan
                                {
                                    SourceId = "total_war_notoriety_" + pActor.kingdom.id + "_" + ZhuluTime.CurrentYear,
                                    TargetKingdomId = pActor.kingdom.id.ToString(),
                                    Channel = Gameplay.ModifierChannels.DiplomaticOpinionBonus,
                                    Additive = -15f,
                                    Multiplicative = 1.0f,
                                    Reason = "穷兵黩武，天下侧目（全面战争）"
                                });
                            }
                            catch { }
                            ZhuluContext.Instance.LogInfo($"[ClaimManager] {pActor.kingdom.name} 发动全面战争！目标 {target.name}");
                            try { WorldTip.showNow($"【{pActor.kingdom.name}】兴兵伐国，发动全面战争！天下震动！", false, "top", 8f, UI.ZhuluUIStyle.Hex.Negative); } catch { }
                            return true;
                        }
                        return false;
                    }
                };
                totalWarPlot.check_can_be_forced = totalWarPlot.check_is_possible;
                totalWarPlot.get_formatted_description = FormatPlotDescription;
                AssetManager.plots_library.add(totalWarPlot);
                AssetManager.plots_library.basic_plots.Add(totalWarPlot);

                LM.AddToCurrentLocale("plot_zlr_total_war_plot", "全面战争");
                LM.AddToCurrentLocale("plot_zlr_total_war_plot_info", "$initiator_actor$ 正谋划对 $target_kingdom$ 发动全面战争");
                LM.AddToCurrentLocale("plot_zlr_total_war_plot_info_base", "举国伐之，意欲灭国（需国力碾压/宣称积累/民心可用/世仇正当）");

                LM.AddToCurrentLocale("plot_zlr_claim_war_plot", "宣称战争");
                LM.AddToCurrentLocale("plot_zlr_claim_war_plot_info", "$initiator_actor$ 正谋划对 $target_kingdom$ 发动宣称战争");
                LM.AddToCurrentLocale("plot_zlr_claim_war_plot_info_base", "以领土宣称为由对目标国发动战争，攻下宣称城市后自动停战");

                ZhuluContext.Instance.LogInfo("[ClaimManager] 谋取宣称+宣称战争谋划注册成功");
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[ClaimManager] 注册谋划失败", ex);
            }
        }

        // ───────────────────────────── 谋划描述格式化 ─────────────────────────────

        /// <summary>
        /// 谋划描述格式化委托：游戏在谋划进度条下方显示此文本。
        /// 模仿原版 getDescriptionGeneric 的占位符替换逻辑。
        /// </summary>
        private static string FormatPlotDescription(Plot pPlot)
        {
            try
            {
                if (pPlot == null) return "";
                string key = pPlot.getAsset()?.getDescriptionID() ?? "";
                string text = key.Localize();
                if (string.IsNullOrEmpty(text) || text == key) return "";

                // 替换占位符（Plot 用 getAuthor() 获取发起者）
                Actor author = pPlot.getAuthor();
                string initiatorName = author?.getName() ?? pPlot.data?.founder_name ?? "";

                // target_kingdom 可能为 null（游戏只维护 target_city），从城市反推王国
                string targetKingdomName = pPlot.target_kingdom?.name ?? "";
                if (string.IsNullOrEmpty(targetKingdomName) && pPlot.target_city != null)
                {
                    Kingdom cityOwner = GetCityKingdom(pPlot.target_city);
                    targetKingdomName = cityOwner?.name ?? "";
                }
                string targetCityName = pPlot.target_city?.data?.name ?? "";

                text = text.Replace("$initiator_actor$", initiatorName);
                text = text.Replace("$initiator_kingdom$", author?.kingdom?.name ?? "");
                text = text.Replace("$target_kingdom$", targetKingdomName);
                text = text.Replace("$target_city$", targetCityName);
                return text;
            }
            catch { return ""; }
        }

        // ───────────────────────────── 宣称数据操作 ─────────────────────────────

        /// <summary>给王国添加一个城市宣称。</summary>
        public void AddClaim(Kingdom kingdom, City city)
        {
            if (kingdom == null || city == null) return;

            var claims = GetClaims(kingdom);
            // 去重：同一城市不重复宣称
            string cityId = city.data.id.ToString();
            foreach (var c in claims)
            {
                if (c.CityId == cityId) return;
            }

            Kingdom cityKingdom = GetCityKingdom(city);
            int year = ZhuluTime.CurrentYear;
            claims.Add(new ClaimRecord
            {
                ClaimantKingdomId = kingdom.id.ToString(),
                CityId = cityId,
                OriginalKingdomId = cityKingdom != null ? cityKingdom.id.ToString() : "",
                StartYear = year,
                ExpireYear = year + CLAIM_DURATION
            });

            SaveClaims(kingdom, claims);

            string cityName = city.data.name ?? "未知城";
            string targetName = cityKingdom != null ? cityKingdom.name : "未知国";
            ZhuluContext.Instance.LogInfo($"[ClaimManager] {kingdom.name} 获得对 {targetName}·{cityName} 的宣称（{CLAIM_DURATION}年有效）");
        }

        /// <summary>获取王国的所有宣称。</summary>
        public List<ClaimRecord> GetClaims(Kingdom kingdom)
        {
            if (kingdom == null) return new List<ClaimRecord>();
            string raw = KingdomDataExt.GetStr(kingdom, KEY_CLAIMS);
            if (string.IsNullOrEmpty(raw)) return new List<ClaimRecord>();
            var result = new List<ClaimRecord>();
            try
            {
                // 格式: cityId|origKingdomId|startYear|expireYear;cityId|...
                string[] entries = raw.Split(';');
                foreach (string entry in entries)
                {
                    if (string.IsNullOrEmpty(entry)) continue;
                    string[] parts = entry.Split('|');
                    if (parts.Length < 4) continue;
                    result.Add(new ClaimRecord
                    {
                        ClaimantKingdomId = kingdom.id.ToString(),
                        CityId = parts[0],
                        OriginalKingdomId = parts[1],
                        StartYear = int.TryParse(parts[2], out int sy) ? sy : 0,
                        ExpireYear = int.TryParse(parts[3], out int ey) ? ey : 0
                    });
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[ClaimManager] GetClaims解析失败", ex);
            }
            return result;
        }

        /// <summary>获取王国的所有宣称（按ID查找）。</summary>
        public List<ClaimRecord> GetClaims(string kingdomId)
        {
            Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
            return GetClaims(k);
        }

        /// <summary>阶段2B 包3：对目标国的宣称数（全面战争门槛②）。</summary>
        public int CountClaimsAgainst(Kingdom attacker, Kingdom target)
        {
            try
            {
                if (attacker == null || target == null) return 0;
                var claims = GetClaims(attacker);
                if (claims == null) return 0;
                string targetId = target.id.ToString();
                int count = 0;
                foreach (var c in claims)
                {
                    if (c == null) continue;
                    // ClaimRecord 有 TargetKingdomId 或 CityId（城市归属判定）——按城市归属数
                    string cityId = c.CityId;
                    if (!string.IsNullOrEmpty(cityId) && long.TryParse(cityId, out long cid))
                    {
                        City city = FindCityById(cityId);
                        if (city != null && city.data != null && city.data.kingdomID.ToString() == targetId)
                            count++;
                    }
                    else if (c.OriginalKingdomId == targetId)
                    {
                        count++;
                    }
                }
                return count;
            }
            catch { return 0; }
        }

        private void SaveClaims(Kingdom kingdom, List<ClaimRecord> claims)
        {
            if (kingdom == null) return;
            // 简单格式: cityId|origKingdomId|startYear|expireYear;...
            var parts = new List<string>();
            foreach (var c in claims)
            {
                parts.Add($"{c.CityId}|{c.OriginalKingdomId}|{c.StartYear}|{c.ExpireYear}");
            }
            string raw = string.Join(";", parts);
            KingdomDataExt.SetStr(kingdom, KEY_CLAIMS, raw);
        }

        // ───────────────────────────── 年度 Tick ─────────────────────────────

        /// <summary>年度Tick：清理过期宣称，每3年判定是否发动宣称战争。</summary>
        /// <summary>按国年度处理（由 YearDriver 每国每年调用一次）。</summary>
        public void OnKingdomYear(Kingdom k, int year)
        {
            if (k == null || !k.isAlive()) return;
            TickKingdomClaims(k, year);
        }

        private void TickKingdomClaims(Kingdom kingdom, int currentYear)
        {
            var claims = GetClaims(kingdom);
            if (claims.Count == 0) return;

            bool modified = false;

            // 清理过期/无效宣称
            for (int i = claims.Count - 1; i >= 0; i--)
            {
                var claim = claims[i];
                if (currentYear >= claim.ExpireYear)
                {
                    claims.RemoveAt(i);
                    modified = true;
                    continue;
                }
                // 目标城市不存在了
                City city = FindCityById(claim.CityId);
                if (city == null)
                {
                    claims.RemoveAt(i);
                    modified = true;
                    continue;
                }
                // 城市已经属于自己了
                Kingdom cityK = GetCityKingdom(city);
                if (cityK == kingdom)
                {
                    claims.RemoveAt(i);
                    modified = true;
                }
            }

            if (modified) SaveClaims(kingdom, claims);
        }


        /// <summary>发动宣称战争（enforceStrength=true 时强制兵力判定——AI/谋划路径；UI 玩家自由）。</summary>
        private bool StartClaimWar(Kingdom attacker, Kingdom defender, ClaimRecord claim, bool enforceStrength = true)
        {
            try
            {
                // 兵力判定（防子国拿着老宣称打数倍强侯国——只有谋取宣称环节有判定，宣战环节曾缺失）
                if (enforceStrength)
                {
                    int myArmy = attacker.countTotalWarriors();
                    int enemyArmy = defender.countTotalWarriors();
                    if (myArmy < enemyArmy)
                    {
                        ZhuluContext.Instance.LogInfo($"[ClaimManager] 拒绝宣战（兵力不足）: {attacker.name}({myArmy}) vs {defender.name}({enemyArmy})");
                        return false;
                    }
                }

                War war = WarGateway.DeclareWar(attacker, defender, WarfareManager.ClaimWar, "宣称战争");
                if (war != null)
                {
                    // 阶段2B ④：记录宣称战争开战年份（战后 3 年冷却，防连环开战）
                    KingdomDataExt.SetStr(attacker, KEY_LAST_CLAIM_WAR_YEAR, ZhuluTime.CurrentYear.ToString());
                    // 在战争数据中记录目标城市ID（双保险：custom_data + 静态字典）
                    SetWarCustomStr(war, "zlr_claim_city", claim.CityId);
                    Adapters.WarfarePatches.ClaimWarTargets[war.getID()] = claim.CityId;
                    Adapters.WarfarePatches.ClaimWarStartYears[war.getID()] = ZhuluTime.CurrentYear;

                    string aName = attacker.name ?? "?";
                    string dName = defender.name ?? "?";
                    string cityName = "";
                    City city = FindCityById(claim.CityId);
                    if (city != null) cityName = city.data.name ?? "";

                    // 录入史册
                    int year = ZhuluTime.CurrentYear;
                    string text = $"<color=#FF8800>{aName}</color> 以领土宣称对 <color=#6ca3fd>{dName}</color> 发动<color=#FF8800>宣称战争</color>，目标：{cityName}！";
                    ChronicleManager.Instance.AddEntry(year, ChronicleEventType.WarDeclared,
                        attacker.id.ToString(), aName, "", text, true);

                    ZhuluContext.Instance.LogInfo($"[ClaimManager] 宣称战争爆发: {aName} vs {dName}, 目标城市={cityName}");
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[ClaimManager] 发动宣称战争失败", ex);
                return false;
            }
        }

        /// <summary>邦交页按钮入口：直接对宣称目标国发动宣称战争（条件校验后调用）。</summary>
        public bool StartClaimWarFromUI(Kingdom attacker, ClaimRecord claim)
        {
            try
            {
                if (attacker == null || claim == null) return false;
                City city = FindCityById(claim.CityId);
                if (city == null) return false;
                Kingdom defender = GetCityKingdom(city);
                if (defender == null || defender == attacker || !defender.isAlive()) return false;
                if (World.world.wars.isInWarWith(attacker, defender)) return false;
                // 只排除自己的宗主和自己的附庸；其他宗主的附庸仍可宣称。
                if (ClaimRules.IsForbiddenTarget(attacker, defender)) return false;
                // UI 玩家手动宣战：不强制兵力判定（玩家自主选择，与 AI 策略区分）
                return StartClaimWar(attacker, defender, claim, false);
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[ClaimManager] UI发动宣称战争失败", ex);
                return false;
            }
        }

        // ───────────────────────────── 辅助方法 ─────────────────────────────

        /// <summary>获取城市所属王国（City.kingdom 是 internal，需要通过 data.kingdomID 查找）。</summary>
        internal static Kingdom GetCityKingdom(City city)
        {
            if (city == null || city.data == null) return null;
            long kId = city.data.kingdomID;
            if (kId <= 0) return null;
            return World.world.kingdoms.get(kId);
        }

        /// <summary>按ID查找城市。</summary>
        internal static City FindCityById(string cityId)
        {
            if (string.IsNullOrEmpty(cityId)) return null;
            if (long.TryParse(cityId, out long id))
                return World.world.cities.get(id);
            return null;
        }

        /// <summary>在战争数据中存储字符串。</summary>
        internal static void SetWarCustomStr(War war, string key, string value)
        {
            if (war?.data == null) return;
            if (war.data.custom_data_string == null)
                war.data.custom_data_string = new CustomDataContainer<string>();
            war.data.custom_data_string[key] = value ?? "";
        }

        /// <summary>从战争数据中读取字符串。</summary>
        internal static string GetWarCustomStr(War war, string key)
        {
            if (war?.data?.custom_data_string == null) return null;
            if (war.data.custom_data_string.TryGetValue(key, out var v)) return v;
            return null;
        }

        /// <summary>检查王国宣称冷却（10年只能谋划1次）。</summary>
        private bool CheckClaimCooldown(Kingdom kingdom)
        {
            string lastYearStr = KingdomDataExt.GetStr(kingdom, KEY_LAST_CLAIM_YEAR);
            if (string.IsNullOrEmpty(lastYearStr)) return true; // 从未宣称过
            if (int.TryParse(lastYearStr, out int lastYear))
            {
                int currentYear = ZhuluTime.CurrentYear;
                return (currentYear - lastYear) >= CLAIM_COOLDOWN;
            }
            return true;
        }

        /// <summary>公开冷却检查（供 ClaimRules 调用）。</summary>
        public bool CheckClaimCooldownPublic(Kingdom kingdom) => CheckClaimCooldown(kingdom);

        /// <summary>阶段2B ④：宣称战争冷却（战后 3 年内不能再开宣称战）。</summary>
        public bool CheckClaimWarCooldownPublic(Kingdom kingdom)
        {
            if (kingdom == null) return false;
            string lastWarStr = KingdomDataExt.GetStr(kingdom, KEY_LAST_CLAIM_WAR_YEAR);
            if (string.IsNullOrEmpty(lastWarStr)) return true; // 从未打过宣称战
            if (int.TryParse(lastWarStr, out int lastWarYear))
            {
                return (ZhuluTime.CurrentYear - lastWarYear) >= CLAIM_WAR_COOLDOWN;
            }
            return true;
        }

        /// <summary>判断王国是否有可谋划的较弱敌国城市。</summary>
        private bool HasWeakerEnemyCity(Kingdom kingdom)
        {
            return GetWeakerEnemyCity(kingdom) != null;
        }

        /// <summary>获取一个可谋划的较弱敌国城市（己方兵力>=双方平均值）。</summary>
        private City GetWeakerEnemyCity(Kingdom kingdom)
        {
            if (kingdom == null || kingdom.cities == null || kingdom.cities.Count == 0) return null;

            int myArmy = kingdom.countTotalWarriors();
            var candidates = new List<City>();
            if (World.world?.kingdoms?.list == null) return null;

            foreach (Kingdom other in World.world.kingdoms.list)
            {
                if (other == null || !other.isAlive() || other == kingdom) continue;
                if (other.cities == null || other.cities.Count == 0) continue;

                // 禁止谋取自己的宗主/自己的附庸城市；其他人的附庸仍属合法目标。
                if (ClaimRules.IsForbiddenTarget(kingdom, other)) continue;

                // 设计规则：己方兵力 >= 双方平均值
                int enemyArmy = other.countTotalWarriors();
                int average = (myArmy + enemyArmy) / 2;
                if (myArmy < average) continue;

                foreach (City c in other.cities)
                {
                    if (c != null) candidates.Add(c);
                }
            }

            if (candidates.Count == 0) return null;
            // 2026-08-20 阶段E：宣称城市按距本国领土分层加权——近35%(x8)/中35%(x3)/远30%(x1)，只掷一次随机
            candidates.Sort((a, b) =>
            {
                float da = DistanceSqToOwnTerritory(a, kingdom);
                float db = DistanceSqToOwnTerritory(b, kingdom);
                int byD = da.CompareTo(db);
                if (byD != 0) return byD;
                return string.CompareOrdinal(a.id.ToString(), b.id.ToString()); // 稳定第二键
            });
            int cnt = candidates.Count;
            int nearEnd = Mathf.Max(1, Mathf.CeilToInt(cnt * 0.35f));
            int middleEnd = Mathf.Max(nearEnd, Mathf.CeilToInt(cnt * 0.70f));
            float totalW = 0f;
            for (int i = 0; i < cnt; i++) totalW += (i < nearEnd ? 8f : i < middleEnd ? 3f : 1f);
            float roll = UnityEngine.Random.Range(0f, totalW);
            for (int i = 0; i < cnt; i++)
            {
                roll -= (i < nearEnd ? 8f : i < middleEnd ? 3f : 1f);
                if (roll <= 0f) return candidates[i];
            }
            return candidates[cnt - 1];
        }

        /// <summary>寻找可发动全面战争的目标；按较弱且较近者优先，保留远方目标的低概率。</summary>
        private Kingdom GetTotalWarTarget(Kingdom attacker)
        {
            if (attacker == null || World.world?.kingdoms?.list == null) return null;
            var candidates = new List<Kingdom>();
            foreach (Kingdom target in World.world.kingdoms.list)
            {
                if (target == null || target == attacker || !target.isAlive()) continue;
                if (ClaimRules.IsForbiddenTarget(attacker, target)) continue;
                if (!ZhuluWarService.CanTotalWar(attacker, target, ZhuluTime.CurrentYear)) continue;
                candidates.Add(target);
            }
            if (candidates.Count == 0) return null;
            candidates.Sort((a, b) =>
            {
                int byArmy = a.countTotalWarriors().CompareTo(b.countTotalWarriors());
                if (byArmy != 0) return byArmy;
                return a.id.CompareTo(b.id);
            });
            int preferred = Mathf.Max(1, Mathf.CeilToInt(candidates.Count * 0.7f));
            return UnityEngine.Random.value < 0.85f
                ? candidates[UnityEngine.Random.Range(0, preferred)]
                : candidates[UnityEngine.Random.Range(0, candidates.Count)];
        }

        private bool HasTotalWarTarget(Kingdom attacker)
        {
            if (attacker == null || World.world?.kingdoms?.list == null) return false;
            foreach (Kingdom target in World.world.kingdoms.list)
            {
                if (target == null || target == attacker || !target.isAlive()) continue;
                if (ClaimRules.IsForbiddenTarget(attacker, target)) continue;
                if (ZhuluWarService.CanTotalWar(attacker, target, ZhuluTime.CurrentYear)) return true;
            }
            return false;
        }

        /// <summary>候选城市到发起国任意城市的最小瓦片平方距离（靠近领土非靠近首都）。</summary>
        private static float DistanceSqToOwnTerritory(City candidate, Kingdom claimant)
        {
            if (candidate == null || claimant?.cities == null) return float.MaxValue;
            var t = candidate.getTile();
            if (t == null) return float.MaxValue;
            float best = float.MaxValue;
            foreach (City own in claimant.cities)
            {
                var ot = own?.getTile();
                if (ot == null) continue;
                float dx = t.x - ot.x, dy = t.y - ot.y;
                float d = dx * dx + dy * dy;
                if (d < best) best = d;
            }
            return best;
        }

    
        // ═══════════ v2 快照存档接口 ═══════════
        // ---- batch10-A1: schema v5 section ----
        public sealed class ClaimState
        {
            public string Claims;
            public long LastClaimYear;
            public long LastClaimWarYear;
        }
        public string SectionName => "claims";
        public Dictionary<string, ClaimState> CaptureStates()
        {
            var d = new Dictionary<string, ClaimState>();
            if (World.world?.kingdoms?.list == null) return d;
            foreach (Kingdom k in World.world.kingdoms.list)
            {
                if (k == null || !k.isAlive()) continue;
                d[k.id.ToString()] = new ClaimState
                {
                    Claims = KingdomDataExt.GetStr(k, KEY_CLAIMS, ""),
                    LastClaimYear = KingdomDataExt.GetLong(k, KEY_LAST_CLAIM_YEAR, 0),
                    LastClaimWarYear = KingdomDataExt.GetLong(k, KEY_LAST_CLAIM_WAR_YEAR, 0),
                };
            }
            return d;
        }
        public void ApplyStates(Dictionary<string, ClaimState> data)
        {
            if (data == null) return;
            foreach (var kv in data)
            {
                Kingdom k = KingdomDataExt.FindKingdomById(kv.Key);
                if (k == null || !k.isAlive() || kv.Value == null) continue;
                KingdomDataExt.SetStr(k, KEY_CLAIMS, kv.Value.Claims ?? "");
                KingdomDataExt.SetLong(k, KEY_LAST_CLAIM_YEAR, kv.Value.LastClaimYear);
                KingdomDataExt.SetLong(k, KEY_LAST_CLAIM_WAR_YEAR, kv.Value.LastClaimWarYear);
            }
        }

        public void MigrateFromCustomData() { }
        public void ResetRuntime() { }

}
}
