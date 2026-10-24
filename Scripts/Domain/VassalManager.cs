using System;
using System.Collections.Generic;
using UnityEngine;
using NeoModLoader.General;
using ZhuluRebuild.Core;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Domain
{
    /// <summary>
    /// 附庸管理器：管理附庸关系、朝贡、独立战争触发、宗主被灭自动独立。
    /// 数据存储在 kingdom.data.custom_data_string 中（随存档自动序列化）。
    /// </summary>
    public class VassalManager : ISnapshotSection<VassalManager.VassalState>
    {
        public static VassalManager Instance { get; } = new VassalManager();

        private const string KEY_SUZERAIN = "suzerain";
        private const string KEY_VASSAL_SINCE = "vassal_since";
        private const string KEY_VASSALS = "vassals";
        private const string KEY_INDEPENDENCE_COOLDOWN = "independence_cooldown";
        // ── 阶段3 扶贫附庸机制：宗主赈济穷附庸（贡不起者），受赈者不易独立 ──
        private const string KEY_AID_YEARS = "aid_years";         // 附庸累计受赈年数（独立判定减免用）
        private const long SuzerainAidMinTreasury = 100;          // 宗主赈济门槛：国库 ≥100（自身充裕才施援）
        private const long AidMinGold = 20;                       // 赈济金下限（≈2 年贡金——够喘息）


        private VassalManager() { }

        // ───────────────────────────── 附庸关系操作 ─────────────────────────────

        /// <summary>建立附庸关系：vassal 成为 suzerain 的附庸。</summary>
        /// <summary>建立附庸关系（公共入口：附庸战争收编/UI 收编）。</summary>
        public bool MakeVassal(Kingdom suzerain, Kingdom vassal)
        {
            if (suzerain == null || vassal == null) return false;
            if (suzerain == vassal) return false;

            // C1：防止链式附庸——
            // 宗主要是附庸 → 不能收附庸（附庸无权拥有附庸）
            if (IsVassal(suzerain))
            {
                ZhuluContext.Instance.LogWarning($"[VassalManager] 拒绝收编：宗主国 {suzerain.name} 自身是附庸，不能拥有附庸（防链式附庸）");
                return false;
            }
            // 有附庸的国 → 不能被收编（防间接附庸链）
            if (GetVassalIds(vassal).Count > 0)
            {
                ZhuluContext.Instance.LogWarning($"[VassalManager] 拒绝收编：{vassal.name} 已有附庸，不能成为附庸（防链式附庸）");
                return false;
            }

            ForceMakeVassal(suzerain, vassal);
            return true;
        }

        /// <summary>建立附庸关系的核心实现（跳过防链检查——供年度自愈转属调用）。</summary>
        private void ForceMakeVassal(Kingdom suzerain, Kingdom vassal)
        {
            if (suzerain == null || vassal == null) return;
            if (suzerain == vassal) return;

            string suzerainId = suzerain.id.ToString();
            string vassalId = vassal.id.ToString();

            // 先清除旧的宗主关系（防止被别国抢走附庸时旧宗主列表残留）
            string oldSuzerainId = KingdomDataExt.GetStr(vassal, KEY_SUZERAIN);
            if (!string.IsNullOrEmpty(oldSuzerainId) && oldSuzerainId != suzerainId)
            {
                Kingdom oldSuzerain = KingdomDataExt.FindKingdomById(oldSuzerainId);
                if (oldSuzerain != null)
                {
                    var oldVassals = GetVassalIds(oldSuzerain);
                    oldVassals.Remove(vassalId);
                    SaveVassalIds(oldSuzerain, oldVassals);
                }
            }

            // 阶段3：个人史册（君临类——称臣大事；仅首次称臣记，转宗主不重复刷）
            if (string.IsNullOrEmpty(oldSuzerainId) && vassal.king != null && vassal.king.isAlive())
            {
                PersonHistoryManager.Instance.RecordEvent(vassal.king.getID(), PersonHistoryManager.CAT_JUNLIN,
                    $"向{suzerain.name}称臣，去国号奉正朔", ZhuluTime.CurrentYear);
            }

            // 设置附庸国数据
            KingdomDataExt.SetStr(vassal, KEY_SUZERAIN, suzerainId);
            KingdomDataExt.SetLong(vassal, KEY_VASSAL_SINCE, ZhuluTime.CurrentYear);

            // 更新宗主国的附庸列表
            var vassals = GetVassalIds(suzerain);
            if (!vassals.Contains(vassalId))
            {
                vassals.Add(vassalId);
                SaveVassalIds(suzerain, vassals);
            }

            // 附庸国颜色改为宗主国颜色
            try
            {
                ColorAsset suzerainColor = suzerain.getColor();
                if (suzerainColor != null)
                {
                    vassal.updateColor(suzerainColor);
                }
            }
            catch { }

            // 录入史册
            int year = ZhuluTime.CurrentYear;
            string text = $"<color=#4488FF>{vassal.name}</color> 臣服于 <color={UI.ZhuluUIStyle.Hex.Imperial}>{suzerain.name}</color>，成为其附庸国。";
            ChronicleManager.Instance.AddEntry(year, ChronicleEventType.WarDeclared,
                suzerainId, suzerain.name ?? "", "", text, true);

            ZhuluContext.Instance.LogInfo($"[VassalManager] 附庸建立: {vassal.name} → {suzerain.name}");

            // 收编完成：附庸补入宗主现存战争；宗主补入附庸现存战争
            try { JoinExistingWars(suzerain, vassal); } catch (Exception ex) { ZhuluContext.Instance.LogError("[VassalManager] JoinExistingWars 异常", ex); }
        }

        /// <summary>
        /// 收编后补入现存战争：
        /// - 附庸补入宗主的战争（宗主攻→joinAttackers，宗主守→joinDefenders）
        /// - 宗主补入附庸的战争（附庸攻→joinAttackers，附庸守→joinDefenders）
        /// 天然排除宗主 vs 附庸的独立战争（main_attacker/main_defender 互指时两边条件都跳过）。
        /// </summary>
        private void JoinExistingWars(Kingdom suzerain, Kingdom vassal)
        {
            if (suzerain == null || vassal == null || World.world?.wars == null) return;

            // 附庸补入宗主现存战争
            var suzerainWars = suzerain.getWars();
            if (suzerainWars != null)
            {
                foreach (War war in suzerainWars)
                {
                    if (war == null || war.hasEnded() || war.hasKingdom(vassal)) continue;
                    if (war.isAttacker(suzerain) && war.main_defender != vassal)
                    {
                        try { war.joinAttackers(vassal); } catch { }
                    }
                    else if (war.isDefender(suzerain) && war.main_attacker != vassal)
                    {
                        try { war.joinDefenders(vassal); } catch { }
                    }
                }
            }

            // 宗主补入附庸现存战争
            var vassalWars = vassal.getWars();
            if (vassalWars != null)
            {
                foreach (War war in vassalWars)
                {
                    if (war == null || war.hasEnded() || war.hasKingdom(suzerain)) continue;
                    if (war.isAttacker(vassal) && war.main_defender != suzerain)
                    {
                        try { war.joinAttackers(suzerain); } catch { }
                    }
                    else if (war.isDefender(vassal) && war.main_attacker != suzerain)
                    {
                        try { war.joinDefenders(suzerain); } catch { }
                    }
                }
            }
        }

        /// <summary>解除附庸关系。</summary>
        public void ReleaseVassal(Kingdom suzerain, Kingdom vassal)
        {
            if (suzerain == null || vassal == null) return;

            string vassalId = vassal.id.ToString();

            // 阶段2R：恢复本国颜色（收编时 updateColor 成宗主色——独立后地图上仍是宗主色的 bug）
            try
            {
                var suzColor = suzerain.getColor();
                var curColor = vassal.getColor();
                if (suzColor != null && curColor != null && curColor.id == suzColor.id)
                {
                    var ownColor = vassal.culture?.getColor();
                    if (ownColor != null) vassal.updateColor(ownColor);
                }
            }
            catch { }

            // 清除附庸国数据
            KingdomDataExt.SetStr(vassal, KEY_SUZERAIN, "");
            KingdomDataExt.SetLong(vassal, KEY_VASSAL_SINCE, 0);

            // 从宗主国附庸列表移除
            var vassals = GetVassalIds(suzerain);
            vassals.Remove(vassalId);
            SaveVassalIds(suzerain, vassals);

            // 录入史册
            int year = ZhuluTime.CurrentYear;
            string text = $"<color=#88FF88>{vassal.name}</color> 脱离 <color={UI.ZhuluUIStyle.Hex.Imperial}>{suzerain.name}</color> 的附庸关系，恢复独立。";
            ChronicleManager.Instance.AddEntry(year, ChronicleEventType.WarDeclared,
                vassalId, vassal.name ?? "", "", text, true);

            ZhuluContext.Instance.LogInfo($"[VassalManager] 附庸解除: {vassal.name} 脱离 {suzerain.name}");
        }

        /// <summary>按ID解除附庸关系（用于宗主被灭的情况）。</summary>
        public void ReleaseVassalById(string suzerainId, Kingdom vassal)
        {
            if (vassal == null) return;

            // 阶段3：补宗主被灭路径的换色（与 ReleaseVassal 同款——宗主已亡不能取其色，
            // 直接恢复本国文化色，地图上立刻脱离宗主色系）
            try
            {
                var ownColor = vassal.culture?.getColor();
                if (ownColor != null) vassal.updateColor(ownColor);
            }
            catch { }

            KingdomDataExt.SetStr(vassal, KEY_SUZERAIN, "");
            KingdomDataExt.SetLong(vassal, KEY_VASSAL_SINCE, 0);

            int year = ZhuluTime.CurrentYear;
            string text = $"<color=#88FF88>{vassal.name}</color> 因宗主国覆灭而恢复独立。";
            ChronicleManager.Instance.AddEntry(year, ChronicleEventType.WarDeclared,
                vassal.id.ToString(), vassal.name ?? "", "", text, true);
        }

        // ───────────────────────────── 查询方法 ─────────────────────────────

        /// <summary>是否是附庸国（双向验证：自己的suzerain字段+宗主的vassals列表必须同时存在）。</summary>
        public bool IsVassal(Kingdom k)
        {
            if (k == null) return false;
            string suzerainId = KingdomDataExt.GetStr(k, KEY_SUZERAIN);
            if (string.IsNullOrEmpty(suzerainId)) return false;
            // 双向验证：宗主的附庸列表中必须包含自己（防止叛乱继承残留数据）
            Kingdom suzerain = KingdomDataExt.FindKingdomById(suzerainId);
            if (suzerain == null || !suzerain.isAlive()) return false;
            var vassalIds = GetVassalIds(suzerain);
            return vassalIds.Contains(k.id.ToString());
        }

        /// <summary>获取宗主国（返回null表示不是附庸）。</summary>
        public Kingdom GetSuzerain(Kingdom k)
        {
            if (k == null) return null;
            string suzerainId = KingdomDataExt.GetStr(k, KEY_SUZERAIN);
            if (string.IsNullOrEmpty(suzerainId)) return null;
            return KingdomDataExt.FindKingdomById(suzerainId);
        }

        /// <summary>获取宗主国ID。</summary>
        public string GetSuzerainId(Kingdom k)
        {
            if (k == null) return "";
            return KingdomDataExt.GetStr(k, KEY_SUZERAIN);
        }

        /// <summary>获取所有附庸国。</summary>
        public List<Kingdom> GetVassals(Kingdom k)
        {
            var result = new List<Kingdom>();
            if (k == null) return result;
            var ids = GetVassalIds(k);
            foreach (string id in ids)
            {
                Kingdom v = KingdomDataExt.FindKingdomById(id);
                if (v != null && v.isAlive()) result.Add(v);
            }
            return result;
        }

        /// <summary>判断两个国家是否是附庸关系对（一个是另一个的宗主）。</summary>
        public bool IsVassalPair(string kingdomId1, string kingdomId2)
        {
            Kingdom k1 = KingdomDataExt.FindKingdomById(kingdomId1);
            if (k1 == null) return false;
            string suzerain = KingdomDataExt.GetStr(k1, KEY_SUZERAIN);
            if (suzerain == kingdomId2) return true;

            Kingdom k2 = KingdomDataExt.FindKingdomById(kingdomId2);
            if (k2 == null) return false;
            string suzerain2 = KingdomDataExt.GetStr(k2, KEY_SUZERAIN);
            return suzerain2 == kingdomId1;
        }

        /// <summary>设置独立战争冷却（失败后20年内不得再独立）。</summary>
        public void SetIndependenceCooldown(Kingdom vassal)
        {
            if (vassal == null) return;
            int cooldownUntil = ZhuluTime.CurrentYear + VassalRules.IndependenceCooldownYears;
            KingdomDataExt.SetLong(vassal, KEY_INDEPENDENCE_COOLDOWN, cooldownUntil);
        }

        /// <summary>检查是否在独立冷却期内。</summary>
        public bool IsInIndependenceCooldown(Kingdom k)
        {
            if (k == null) return false;
            long cooldownUntil = KingdomDataExt.GetLong(k, KEY_INDEPENDENCE_COOLDOWN);
            return ZhuluTime.CurrentYear < cooldownUntil;
        }

        // ───────────────────────────── 年度 Tick ─────────────────────────────

        private int _lastIndirectFixYear = -1;
        private int _lastIndependenceCheckYear = -1;

        /// <summary>按国年度处理（由 YearDriver 每国每年调用一次）。</summary>
        // ── 批次11③：随宗主作战的功勋独立通道（条件苛刻）──
        private const string KEY_ALLY_WAR_SINCE = "zlr_ally_war_since";
        private const string KEY_MERIT_COOLDOWN = "zlr_merit_cd";
        private const float MERIT_ARMY_RATIO = 0.60f;   // 军力 ≥ 宗主60%
        private const int MERIT_MIN_YEARS = 2;          // 并肩作战满2年
        private const float MERIT_DICE_P = 0.20f;       // 年度概率20%（确定性骰）

        /// <summary>年度检定：与宗主并肩作战中的附庸，军力达标+并肩满2年+骰过 → 和平解约独立。</summary>
        private void TickMeritIndependence(Kingdom suzerain, int year)
        {
            if (suzerain == null) return;
            foreach (var vidRaw in GetVassalIds(suzerain).ToArray())
            {
                string vid = vidRaw;
                Kingdom v = KingdomDataExt.FindKingdomById(vid);
                if (v == null || !v.isAlive()) continue;
                if (!IsVassal(v)) continue;

                // 并肩作战中？（宗主与附庸同时出现在同一场进行中的战争）
                bool fightingTogether = false;
                try
                {
                    foreach (War sw in suzerain.getWars())
                    {
                        if (sw == null || sw.hasEnded()) continue;
                        foreach (War vw in v.getWars())
                        {
                            if (vw == sw) { fightingTogether = true; break; }
                        }
                        if (fightingTogether) break;
                    }
                }
                catch { }

                string sinceKey = KEY_ALLY_WAR_SINCE + ":" + vid;
                if (fightingTogether)
                {
                    if (string.IsNullOrEmpty(KingdomDataExt.GetStr(suzerain, sinceKey)))
                        KingdomDataExt.SetStr(suzerain, sinceKey, year.ToString());
                }
                else
                {
                    KingdomDataExt.SetStr(suzerain, sinceKey, "");   // 停止并肩 → 计时清零
                    continue;
                }

                int sinceYear = 0;
                int.TryParse(KingdomDataExt.GetStr(suzerain, sinceKey), out sinceYear);
                if (sinceYear <= 0 || year - sinceYear < MERIT_MIN_YEARS) continue;

                // 军力 ≥ 60% 宗主
                int armyV = 0, armyS = 0;
                try { armyV = v.countTotalWarriors(); } catch { }
                try { armyS = suzerain.countTotalWarriors(); } catch { }
                if (armyS <= 0 || armyV < armyS * MERIT_ARMY_RATIO) continue;

                // 冷却10年（失败/成功都进冷却——成功即解约无所谓，失败防年年纠缠）
                int cd = (int)KingdomDataExt.GetLong(v, KEY_MERIT_COOLDOWN, 0);
                if (cd > 0 && year - cd < 10) continue;

                // 确定性骰（种子=附庸ID+年份）
                int dice = Core.DeterministicRandom.RollInt(v.getID(), year, "merit_independence", 0, 100);
                KingdomDataExt.SetLong(v, KEY_MERIT_COOLDOWN, year);
                if (dice < (int)(MERIT_DICE_P * 100))
                {
                    ReleaseVassal(suzerain, v);
                    ChronicleManager.Instance.AddEntry(year, ChronicleEventType.PeaceTreaty,
                        suzerain.id.ToString(), suzerain.name ?? "", v.name ?? "",
                        "<color=#6ca3fd>【以功请封】</color>" + v.name + " 随征有功、兵甲日盛，" + suzerain.name + " 许其自立。", false);
                    ZhuluContext.Instance.LogInfo($"[VassalManager] 功勋独立(和平解约): {v.name} ← {suzerain.name}");
                    try { WorldTip.showNow($"【{v.name}】随征战功赫赫，受许自立！", false, "top", 6f, UI.ZhuluUIStyle.Hex.Warn); } catch { }
                }
            }
        }

        public void OnKingdomYear(Kingdom k, int year)
        {
            if (k == null || !k.isAlive()) return;

            // 批次11③：功勋独立年度检定（先于其他逻辑——解约会改变后续遍历基面）
            TickMeritIndependence(k, year);

            // 年度一次：修复历史遗留的链式附庸（附庸的宗主也是附庸 → 转属到最上层宗主）
            if (year != _lastIndirectFixYear)
            {
                _lastIndirectFixYear = year;
                FixIndirectVassalage();
            }

            TickTribute(k, year);
            TickSuzerainAlive(k);

            // 独立战争触发（每5年判定一次，全局性但挂在按国调用上）
            if (year % VassalRules.IndependenceCheckInterval == 0 && year != _lastIndependenceCheckYear)
            {
                _lastIndependenceCheckYear = year;
                TickIndependenceCheck(year);
            }
        }

        /// <summary>
        /// 自愈：把"附庸的宗主也是附庸"的链式结构转属到最上层宗主。
        /// 旧档/原版机制可能产生宗主易主（A 被 C 收编，A 的附庸 B 变间接附庸）——
        /// 转属后 B 直接臣服于 C，消除链式附庸（用户反馈：宗主国成为别人的附庸那他原本的附庸怎么办）。
        /// </summary>
        private void FixIndirectVassalage()
        {
            try
            {
                if (World.world?.kingdoms?.list == null) return;
                foreach (Kingdom vassal in World.world.kingdoms.list)
                {
                    if (vassal == null || !vassal.isAlive()) continue;
                    if (!IsVassal(vassal)) continue;
                    Kingdom suz = GetSuzerain(vassal);
                    if (suz == null || !suz.isAlive())
                    {
                        // 批次15：宗主已亡 → 解除附庸关系（不再跳过导致铭牌残留）
                        ReleaseVassalById(KingdomDataExt.GetStr(vassal, KEY_SUZERAIN), vassal);
                        ZhuluContext.Instance.LogInfo($"[VassalManager] 宗主已亡，自动解除 {vassal.name} 的附庸关系");
                        continue;
                    }
                    if (!IsVassal(suz)) continue; // 宗主不是附庸 → 正常

                    // 宗主也是附庸 → 找最上层宗主并转属
                    Kingdom top = GetSuzerain(suz);
                    if (top == null || !top.isAlive() || top == vassal) continue;
                    if (top == suz) continue;

                    ForceMakeVassal(top, vassal);
                    ZhuluContext.Instance.LogInfo($"[VassalManager] 自愈转属: {vassal.name} 从 {suz.name} 转属到上层宗主 {top.name}");
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[VassalManager] 链式附庸修复异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 朝贡（阶段2C 金币化，2026-08-16）：附庸每年向宗主贡金（人口÷2 金币，长明火模式）
        /// + 稳定度小加成；附庸国库不足 → 断贡（记欠贡年数，欠贡 ≥2 年附庸独立倾向增加）。
        /// 阶段3 扶贫附庸（2026-08-17）：贡不起且宗主充裕 → 宗主赈济（免当年贡+记受赈年数），
        /// 受赈附庸独立判定减免——治"穷附庸年年断贡弹窗震怒"的恶性循环。
        /// </summary>
        private void TickTribute(Kingdom vassal, int currentYear)
        {
            if (!IsVassal(vassal)) return;

            Kingdom suzerain = GetSuzerain(vassal);
            if (suzerain == null) return;

            // 独立战争期间停止朝贡
            if (World.world.wars.isInWarWith(vassal, suzerain)) return;

            try
            {
                long collected = ZhuluDiplomacyService.CollectTribute(vassal, suzerain);
                // 阶段3H-19：朝贡收入通道（天朝贡制/驰道直道——宗主侧乘算）
                try
                {
                    float tr = Gameplay.ModifierPipeline.Instance.EvaluateChannelMultiplier(Gameplay.ModifierChannels.TributeIncome, suzerain.id.ToString(), 1f);
                    if (tr != 1f) collected = (long)Math.Round(collected * tr);
                }
                catch { }
                if (collected > 0)
                {
                    // P2-30：补缴成功 → 欠贡清零（旧账随朝贡恢复而消解；此前不清是漏计）
                    KingdomDataExt.SetLong(suzerain, "tribute_deficit", 0);
                    // 宗主稳定度小加成（朝贡安定民心）
                    StabilityManager.Instance.AddExternalBonus(suzerain.id.ToString(), 1f);
                    // 记贡金（快照/日志）
                    KingdomDataExt.SetLong(suzerain, "tribute_last", collected);
                    ZhuluContext.Instance.LogInfo($"[Diplomacy] {vassal.name} 向宗主 {suzerain.name} 朝贡 {collected} 金币");
                    return;
                }

                // ── 断贡分支：先试扶贫，宗主也穷才走震怒线 ──
                long suzTre = KingdomDataExt.GetLong(suzerain, "tre", 0);
                if (suzTre >= SuzerainAidMinTreasury)
                {
                    // 宗主赈济：转移 ≈2 年贡金（下限 20），受赈年数累计（独立减免）；
                    // 赈济已超贡金——当年视作已尽藩臣之礼（欠贡计数清零，不再震怒弹窗）
                    long aid = Math.Max(AidMinGold, ZhuluDiplomacyService.ComputeTribute(vassal) * 2L);
                    aid = Math.Min(aid, suzTre / 2);   // 单次赈济不超宗主国库半数（防宗主被拖垮）
                    if (aid > 0)
                    {
                        KingdomDataExt.SetLong(suzerain, "tre", suzTre - aid);
                        long vassalTre = KingdomDataExt.GetLong(vassal, "tre", 0);
                        KingdomDataExt.SetLong(vassal, "tre", vassalTre + aid);
                        KingdomDataExt.SetLong(vassal, KEY_AID_YEARS, KingdomDataExt.GetLong(vassal, KEY_AID_YEARS, 0) + 1);
                        KingdomDataExt.SetLong(suzerain, "tribute_deficit", 0);
                        StabilityManager.Instance.AddExternalBonus(vassal.id.ToString(), 1f); // 赈济安附庸之心
                        // 阶段3：个人史册（恩遇类——受宗主赈济）
                        if (vassal.king != null && vassal.king.isAlive())
                            PersonHistoryManager.Instance.RecordEvent(vassal.king.getID(), PersonHistoryManager.CAT_ENYU,
                                $"国用匮乏，受宗主{suzerain.name}赈济{aid}金", currentYear);
                        try { ZhuluWorldTip.ShowPair(suzerain, vassal, $"【{suzerain.name}】念 {vassal.name} 贫弱，赐金 {aid} 以示抚绥", 5f, UI.ZhuluUIStyle.Hex.Azure); } catch { }
                        ZhuluContext.Instance.LogInfo($"[Diplomacy] {suzerain.name} 赈济附庸 {vassal.name} {aid} 金（扶贫附庸，受赈 {KingdomDataExt.GetLong(vassal, KEY_AID_YEARS, 0)} 年）");
                    }
                    return;
                }

                // 宗主无力赈济：累计欠贡年数（P2-30 改版：持续累计，不再"播报即清零"）
                long deficit = KingdomDataExt.GetLong(suzerain, "tribute_deficit", 0) + 1;
                KingdomDataExt.SetLong(suzerain, "tribute_deficit", deficit);

                // 阶段3H-21：代贡三式——附庸真没钱时以贤才/质子抵贡，宗主受之则欠贡清零，
                // 问罪失去正当性（用户定稿：朝贡不止金钱，讨伐要考虑附庸真的没钱）
                if (TryPayTributeInKind(suzerain, vassal, currentYear, deficit))
                {
                    KingdomDataExt.SetLong(suzerain, "tribute_deficit", 0);
                    return;
                }

                // 贫困豁免：附庸国库归零且无人才可纳——天灾饥馑非悖逆，宗主不征（用户定稿）
                long vTre = KingdomDataExt.GetLong(vassal, "tre", 0);
                if (vTre <= 0 && deficit <= 6)
                {
                    if (deficit == 3)
                        ZhuluContext.Instance.LogInfo($"[Diplomacy] {vassal.name} 岁饥民贫（库{vTre}），{suzerain.name} 暂免其贡");
                    return;
                }

                // 震怒/告庙按档各播一次（2 年首怒、4 年告庙檄征）
                if (deficit == 2 || deficit == 4)
                {
                    ZhuluContext.Instance.LogInfo($"[Diplomacy] {vassal.name} 连续欠贡{deficit}年，宗主 {suzerain.name} {(deficit >= 4 ? "告庙檄征" : "震怒")}");
                    try { ZhuluWorldTip.ShowPair(suzerain, vassal, $"【{suzerain.name}】{vassal.name} 久不朝贡，宗主{(deficit >= 4 ? "告庙将征" : "震怒")}！", 5f, UI.ZhuluUIStyle.Hex.Warn); } catch { }
                }

                // P2-30 断贡问罪（3H-21 降频）：欠贡≥5 年起，p = min(0.20, 0.07×(欠贡年数−4))
                // 且同一次欠贡期只兴一次师（问罪后清欠贡计数——战后需重新累积）；刚问罪过有 8 年休兵
                if (deficit >= 5)
                {
                    long lastPunish = KingdomDataExt.GetLong(vassal, "punitive_last_year", -999);
                    if (currentYear - lastPunish < 8) return;
                    float pWenjie = System.Math.Min(0.20f, 0.07f * (deficit - 4));
                    if (DeterministicRandom.Roll01(suzerain.id, currentYear, "wenjie") < pWenjie)
                    {
                        KingdomDataExt.SetLong(vassal, "punitive_last_year", currentYear);
                        War punitiveWar = WarGateway.DeclareWar(suzerain, vassal, WarfareManager.PunitiveWar, "问罪讨贡（久不朝贡）");
                        if (punitiveWar != null)
                        {
                            try { ZhuluWorldTip.ShowPair(suzerain, vassal, $"【{suzerain.name}】兴问罪之师！{vassal.name} 久废朝贡，天讨至矣", 6f, UI.ZhuluUIStyle.Hex.Negative); } catch { }
                            ChronicleManager.Instance.AddEntry(currentYear, ChronicleEventType.WarDeclared,
                                suzerain.id.ToString(), suzerain.name ?? "", vassal.name ?? "",
                                $"<color=#FFD700>【问罪】</color>{suzerain.name} 以 {vassal.name} 久废朝贡，兴问罪之师。", true);
                            ZhuluContext.Instance.LogInfo($"[Diplomacy] 问罪之战爆发: {suzerain.name} → {vassal.name}（欠贡{deficit}年）");
                            // 3H-21：问罪即清算——欠贡计数清零（一罪一罚，战后重新累积）
                            KingdomDataExt.SetLong(suzerain, "tribute_deficit", 0);
                        }
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// 阶段3H-21：代贡三式（用户定稿——朝贡不止金钱，附庸真没钱时以人抵贡）：
        /// ① 纳贤：附庸人才库最高分贤士送入宗主人才库（score×1.1 旅外历练加成）；
        /// ② 质子：王室子弟（国王子女）送宗主都城为质（附庸独立倾向大减——人质在宗主手）；
        /// ③ 岁币缓期：有少量存钱（<贡金但>0）→ 半贡折纳、欠贡减半计。
        /// 任一成行 → 返回 true（当年视作已贡，欠贡清零由调用方处理）。
        /// 每年至多一行（确定性骰防刷）。
        /// </summary>
        private bool TryPayTributeInKind(Kingdom suzerain, Kingdom vassal, int currentYear, long deficit)
        {
            try
            {
                string vId = vassal.id.ToString();

                // ① 纳贤：人才库最高分（文武皆可）送宗主
                var pool = KejuManager.Instance.GetTalentPool(vId);
                KejuManager.TalentEntry best = null;
                foreach (var t in pool)
                    if (best == null || t.Score > best.Score) best = t;
                if (best != null && best.Score >= 40)   // 门槛：中才有资格作贡贤
                {
                    Actor scholar = World.world?.units?.get(best.ActorId);
                    if (scholar != null && scholar.isAlive())
                    {
                        // 迁徙：入宗主国籍
                        try
                        {
                            scholar.joinKingdom(suzerain);
                            if (suzerain.cities != null && suzerain.cities.Count > 0)
                                scholar.joinCity(suzerain.cities[0]);
                        }
                        catch { }
                        // 从附庸人才库移除，入宗主人才库（旅外历练 +10% 评分）
                        try
                        {
                            var m = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                            var poolField = typeof(KejuManager).GetField("_talentPool", m);
                            var addMethod = typeof(KejuManager).GetMethod("AddToTalentPool", m);
                            if (poolField != null && addMethod != null)
                            {
                                var pools = (System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<KejuManager.TalentEntry>>)poolField.GetValue(KejuManager.Instance);
                                System.Collections.Generic.List<KejuManager.TalentEntry> vl = null;
                                if (pools != null) pools.TryGetValue(vId, out vl);
                                vl?.RemoveAll(e => e.ActorId == best.ActorId);
                                addMethod.Invoke(KejuManager.Instance, new object[] { suzerain.id.ToString(), scholar, (int)(best.Score * 1.1f), currentYear, best.BranchWu });
                            }
                        }
                        catch { }
                        PersonHistoryManager.Instance.RecordEvent(best.ActorId, PersonHistoryManager.CAT_ENYU,
                            $"以贤才入贡，事{vassal.name}之主 {suzerain.name}（旅外历练）", currentYear);
                        try { ZhuluWorldTip.ShowPair(suzerain, vassal, $"【{vassal.name}】献贤才 {best.Name} 代贡——{suzerain.name} 受之，怒意稍解", 6f, UI.ZhuluUIStyle.Hex.Azure); } catch { }
                        ChronicleManager.Instance.AddEntry(currentYear, ChronicleEventType.VassalChange,
                            vId, vassal.name ?? "", best.Name,
                            $"{vassal.name} 岁饥乏金，献贤才 {best.Name} 于 {suzerain.name} 以代贡。", false);
                        ZhuluContext.Instance.LogInfo($"[Diplomacy] 代贡·纳贤: {vassal.name} 献 {best.Name}(评分{best.Score}) → {suzerain.name}");
                        return true;
                    }
                }

                // ② 质子：国王子女送宗主都城（迁籍；附庸独立倾向大减——记质子标记）
                try
                {
                    if (vassal.king != null && vassal.king.isAlive())
                    {
                        var kids = KinshipTracker.GetAliveChildren(vassal, vassal.king.getID().ToString());
                        Actor hostage = null;
                        if (kids != null)
                            foreach (var c in kids)
                            {
                                if (c == null || !c.isAlive()) continue;
                                try { if (c.kingdom == vassal) { hostage = c; break; } } catch { }
                            }
                        if (hostage != null)
                        {
                            try
                            {
                                hostage.joinKingdom(suzerain);
                                if (suzerain.capital != null) hostage.joinCity(suzerain.capital);
                            }
                            catch { }
                            KingdomDataExt.SetLong(vassal, "hostage_at_suzerain", currentYear);
                            // 质子在宗主手 → 附庸独立倾向大减（独立谋划门槛实质抬高）
                            StabilityManager.Instance.AddExternalBonus(vId, 2f);
                            PersonHistoryManager.Instance.RecordEvent(hostage.getID(), PersonHistoryManager.CAT_ENYU,
                                $"入质{vassal.name}之主 {suzerain.name}（代贡为质）", currentYear);
                            try { ZhuluWorldTip.ShowPair(suzerain, vassal, $"【{vassal.name}】遣王子为质以代贡——{suzerain.name} 勉纳之", 6f, UI.ZhuluUIStyle.Hex.Azure); } catch { }
                            ChronicleManager.Instance.AddEntry(currentYear, ChronicleEventType.VassalChange,
                                vId, vassal.name ?? "", hostage.getName() ?? "王子",
                                $"{vassal.name} 乏金，遣王族 {hostage.getName() ?? "王子"} 入质 {suzerain.name}。", false);
                            ZhuluContext.Instance.LogInfo($"[Diplomacy] 代贡·质子: {vassal.name} 遣质 → {suzerain.name}");
                            return true;
                        }
                    }
                }
                catch { }

                // ③ 岁币缓期：国库有点小钱（>0）但不足额 → 半贡折纳 + 欠贡减半计（诚意抵怒）
                long vTre = KingdomDataExt.GetLong(vassal, "tre", 0);
                if (vTre > 0)
                {
                    long half = ZhuluDiplomacyService.ComputeTribute(vassal) / 2;
                    if (half > 0)
                    {
                        long pay = System.Math.Min(half, vTre);
                        KingdomDataExt.SetLong(vassal, "tre", vTre - pay);
                        long sTre = KingdomDataExt.GetLong(suzerain, "tre", 0);
                        KingdomDataExt.SetLong(suzerain, "tre", sTre + pay);
                        // 欠贡减半计（诚意折怒）
                        KingdomDataExt.SetLong(suzerain, "tribute_deficit", System.Math.Max(0, deficit / 2));
                        try { ZhuluWorldTip.ShowPair(suzerain, vassal, $"【{vassal.name}】岁币折半以进——{suzerain.name} 姑念其乏，许之", 5f, UI.ZhuluUIStyle.Hex.Azure); } catch { }
                        ZhuluContext.Instance.LogInfo($"[Diplomacy] 代贡·岁币缓期: {vassal.name} 折纳 {pay} 金（欠贡减半计）");
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>宗主被灭检查：宗主国不存在了则附庸自动独立。</summary>
        private void TickSuzerainAlive(Kingdom vassal)
        {
            if (!IsVassal(vassal)) return;

            string suzerainId = KingdomDataExt.GetStr(vassal, KEY_SUZERAIN);
            if (string.IsNullOrEmpty(suzerainId)) return;

            Kingdom suzerain = KingdomDataExt.FindKingdomById(suzerainId);
            if (suzerain == null || !suzerain.isAlive())
            {
                ReleaseVassalById(suzerainId, vassal);
                ZhuluContext.Instance.LogInfo($"[VassalManager] 宗主国覆灭，{vassal.name} 自动独立");
            }
        }

        /// <summary>独立战争触发判定（每5年一次）。</summary>
        private void TickIndependenceCheck(int currentYear)
        {
            if (World.world?.kingdoms?.list == null) return;

            // 收集所有宗主国及其附庸
            var suzerainMap = new Dictionary<string, List<Kingdom>>();

            foreach (Kingdom k in World.world.kingdoms.list)
            {
                if (k == null || !k.isAlive()) continue;
                if (!IsVassal(k)) continue;
                if (IsInIndependenceCooldown(k)) continue;

                string suzerainId = KingdomDataExt.GetStr(k, KEY_SUZERAIN);
                if (string.IsNullOrEmpty(suzerainId)) continue;

                if (!suzerainMap.ContainsKey(suzerainId))
                    suzerainMap[suzerainId] = new List<Kingdom>();
                suzerainMap[suzerainId].Add(k);
            }

            foreach (var kvp in suzerainMap)
            {
                Kingdom suzerain = KingdomDataExt.FindKingdomById(kvp.Key);
                if (suzerain == null || !suzerain.isAlive()) continue;

                int suzerainPop = suzerain.getPopulationPeople();
                if (suzerainPop <= 0) continue;

                var vassalsList = kvp.Value;

                // 单个附庸判定（委托给 VassalRules 纯判定；阶段2B ⑥ 加兵力比）
                // 阶段2B C（2026-08-15 推荐 B）：天命国宗主 → 附庸独立概率减半（天子威慑）
                bool suzerainIsMandate = PolicyManager.IsMandateHolderForKingdom(suzerain);
                foreach (Kingdom vassal in vassalsList)
                {
                    if (!vassal.isAlive()) continue;
                    int vassalPop = vassal.getPopulationPeople();
                    int vassalArmy = 0, suzerainArmy = 0;
                    try { vassalArmy = vassal.countTotalWarriors(); suzerainArmy = suzerain.countTotalWarriors(); } catch { }

                    bool should = VassalRules.ShouldTriggerIndependence(vassalPop, suzerainPop, vassalArmy, suzerainArmy);
                    if (should && suzerainIsMandate)
                    {
                        // 天命国威慑：80%-100% 区间概率减半；100%+ 必定独立不受限（实力碾压仍可反）
                        float ratio = suzerainPop > 0 ? (float)vassalPop / suzerainPop : 0f;
                        if (ratio < 1.0f && UnityEngine.Random.value >= 0.5f) should = false;
                    }
                    // 阶段3 扶贫安抚：受赈年数越长越不易反（每受赈年 -15% 独立倾向，封顶 -50%——久蒙恩泽不忍背）
                    if (should)
                    {
                        long aidYears = KingdomDataExt.GetLong(vassal, KEY_AID_YEARS, 0);
                        if (aidYears > 0)
                        {
                            float relief = Math.Min(0.5f, aidYears * 0.15f);
                            if (UnityEngine.Random.value < relief) should = false;
                        }
                    }
                    // 2026-08-19 串联：执政党战争轴修正独立倾向（主战党 ×1.3 倾向 / 主和党 ×0.7）
                    if (should && FactionPoliticsManager.GetWarAppetiteFactor(vassal) < 1f
                        && UnityEngine.Random.value < 0.3f)
                    {
                        should = false; // 主和党执政：30% 概率按下举兵之议
                    }
                    if (should)
                        StartIndependenceWar(vassal, suzerain, currentYear);
                }

                // 联合独立判定（委托给 VassalRules 纯判定）
                int totalVassalPop = 0;
                var aliveVassals = new List<Kingdom>();
                foreach (Kingdom v in vassalsList)
                {
                    if (!v.isAlive()) continue;
                    if (IsInIndependenceCooldown(v)) continue;
                    totalVassalPop += v.getPopulationPeople();
                    aliveVassals.Add(v);
                }

                if (VassalRules.ShouldTriggerJointIndependence(totalVassalPop, suzerainPop, aliveVassals.Count))
                {
                    // 联合独立：所有附庸同时对宗主宣战
                    foreach (Kingdom v in aliveVassals)
                    {
                        StartIndependenceWar(v, suzerain, currentYear);
                    }
                    ZhuluContext.Instance.LogInfo($"[VassalManager] 联合独立战争爆发！{aliveVassals.Count}个附庸国联合反抗 {suzerain.name}");
                }
            }
        }

        /// <summary>发动独立战争。</summary>
        private bool StartIndependenceWar(Kingdom vassal, Kingdom suzerain, int currentYear)
        {
            try
            {
                // 阶段2B ⑥：附庸与宗主互战时——若宗主正发动附庸战争（收编），允许独立反抗（并存）；
                // 其他互战（如镇压叛乱）仍拒绝
                bool inWar = World.world.wars.isInWarWith(vassal, suzerain);
                if (inWar)
                {
                    bool vassalWarWithSuz = false;
                    try
                    {
                        foreach (War w in vassal.getWars())
                        {
                            if (w == null || w.hasEnded()) continue;
                            string wt = w.data?.war_type ?? "";
                            if (wt == "zlr_vassal_war") { vassalWarWithSuz = true; break; }
                        }
                    }
                    catch { }
                    if (!vassalWarWithSuz) return false; // 非附庸战争互战：拒绝
                }

                War war = WarGateway.DeclareWar(vassal, suzerain, WarfareManager.IndependenceWar, "独立战争");
                if (war != null)
                {
                    // 双保险：静态字典 + custom_data_string
                    Adapters.WarfarePatches.IndependenceWarStartYears[war.getID()] = currentYear;
                    ClaimManager.SetWarCustomStr(war, "zlr_war_start_year", currentYear.ToString());
                    // 记录开战时附庸首都城市 id（首都沦陷判负）
                    if (vassal.capital != null)
                        ClaimManager.SetWarCustomStr(war, "zlr_ind_war_start_capital", vassal.capital.id.ToString());

                    string text = $"<color=#AA44FF>{vassal.name}</color> 对宗主 <color={UI.ZhuluUIStyle.Hex.Imperial}>{suzerain.name}</color> 发动<color=#AA44FF>独立战争</color>！";
                    ChronicleManager.Instance.AddEntry(currentYear, ChronicleEventType.WarDeclared,
                        vassal.id.ToString(), vassal.name ?? "", "", text, true);

                    // 阶段3：个人史册（君临类——附庸君主举兵大事）
                    if (vassal.king != null && vassal.king.isAlive())
                        PersonHistoryManager.Instance.RecordEvent(vassal.king.getID(), PersonHistoryManager.CAT_JUNLIN,
                            $"举兵反抗宗主{suzerain.name}，独立战争爆发", currentYear);

                    ZhuluContext.Instance.LogInfo($"[VassalManager] 独立战争爆发: {vassal.name} vs {suzerain.name}");
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[VassalManager] 发动独立战争失败", ex);
                return false;
            }
        }

        /// <summary>邦交页按钮入口：直接对宗主发动独立战争（条件校验后调用）。</summary>
        public bool StartIndependenceWarFromUI(Kingdom vassal)
        {
            try
            {
                if (vassal == null) return false;
                Kingdom suzerain = GetSuzerain(vassal);
                if (suzerain == null || !suzerain.isAlive()) return false;
                if (!VassalRules.CanStartIndependence(vassal, suzerain, ZhuluTime.CurrentYear)) return false;
                return StartIndependenceWar(vassal, suzerain, ZhuluTime.CurrentYear);
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[VassalManager] UI发动独立战争失败", ex);
                return false;
            }
        }

        // ───────────────────────────── 附庸战争谋划注册 ─────────────────────────────

        /// <summary>注册"发动附庸战争"谋划到原版谋划系统。</summary>
        public void RegisterPlot()
        {
            try
            {
                var vassalWarPlot = new PlotAsset
                {
                    id = "zlr_vassal_war_plot",
                    is_basic_plot = true,
                    path_icon = "plots/icons/plot_new_war",
                    group_id = "diplomacy",
                    can_be_done_by_king = true,
                    check_target_kingdom = true,
                    progress_needed = 40f,
                    min_level = 5,
                    money_cost = 15,
                    check_is_possible = (Actor pActor) =>
                    {
                        if (pActor == null || !pActor.hasKingdom()) return false;
                        // 外交关闭时不允许发动战争
                        if (!WorldLawLibrary.world_law_diplomacy.isEnabled()) return false;
                        // 已在战争中不允许再发动
                        if (pActor.kingdom.getWars().GetEnumerator().MoveNext()) return false;
                        // 附庸国不能发动附庸战争
                        if (IsVassal(pActor.kingdom)) return false;
                        return HasWeakerNeighbor(pActor.kingdom);
                    },
                    try_to_start_advanced = (Actor pActor, PlotAsset pAsset, bool pForced) =>
                    {
                        Kingdom target = GetWeakerNeighbor(pActor.kingdom);
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

                        // 再次验证条件
                        if (!CanStartVassalWar(pActor.kingdom, target)) return false;

                        War war = WarGateway.DeclareWar(pActor.kingdom, target, WarfareManager.VassalWar, "附庸战争");
                        if (war != null)
                        {
                            int year = ZhuluTime.CurrentYear;
                            string text = $"<color=#4488FF>{pActor.kingdom.name}</color> 对 <color=#6ca3fd>{target.name}</color> 发动<color=#4488FF>附庸战争</color>！";
                            ChronicleManager.Instance.AddEntry(year, ChronicleEventType.WarDeclared,
                                pActor.kingdom.id.ToString(), pActor.kingdom.name ?? "", "", text, true);
                            return true;
                        }
                        return false;
                    }
                };
                vassalWarPlot.check_can_be_forced = vassalWarPlot.check_is_possible;
                vassalWarPlot.get_formatted_description = FormatPlotDescription;

                AssetManager.plots_library.add(vassalWarPlot);
                AssetManager.plots_library.basic_plots.Add(vassalWarPlot);

                // 注册谋划本地化文本
                LM.AddToCurrentLocale("plot_zlr_vassal_war_plot", "发动附庸战争");
                LM.AddToCurrentLocale("plot_zlr_vassal_war_plot_info", "$initiator_actor$ 正谋划对 $target_kingdom$ 发动附庸战争");
                LM.AddToCurrentLocale("plot_zlr_vassal_war_plot_info_base", "谋划对弱邻国发动附庸战争，占领其首都后可将其收为附庸");

                ZhuluContext.Instance.LogInfo("[VassalManager] 附庸战争谋划注册成功");
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[VassalManager] 注册附庸战争谋划失败", ex);
            }

            // ═══ 独立战争谋划（附庸对宗主）═══
            // 图标用原版"叛乱"图标（plots/icons/plot_rebellion），区别于"发动战争"（plot_new_war）
            try
            {
                var independencePlot = new PlotAsset
                {
                    id = "zlr_independence_war_plot",
                    is_basic_plot = true,
                    path_icon = "plots/icons/plot_rebellion",
                    group_id = "diplomacy",
                    can_be_done_by_king = true,
                    check_target_kingdom = true,
                    progress_needed = 60f,   // 独立谋划需要更长时间（酝酿反心）
                    min_level = 3,
                    money_cost = 10,
                    check_is_possible = (Actor pActor) =>
                    {
                        if (pActor == null || !pActor.hasKingdom()) return false;
                        Kingdom vassal = pActor.kingdom;
                        // 必须是附庸国
                        if (!IsVassal(vassal)) return false;
                        // 外交关闭时不允许
                        if (!WorldLawLibrary.world_law_diplomacy.isEnabled()) return false;
                        // 宗主必须存活
                        Kingdom suzerain = GetSuzerain(vassal);
                        if (suzerain == null || !suzerain.isAlive()) return false;
                        // 已在战争中不允许
                        if (vassal.getWars().GetEnumerator().MoveNext()) return false;
                        // 复用独立战争纯判定（存活/未交战/冷却期外）
                        return VassalRules.CanStartIndependence(vassal, suzerain, ZhuluTime.CurrentYear);
                    },
                    try_to_start_advanced = (Actor pActor, PlotAsset pAsset, bool pForced) =>
                    {
                        Kingdom vassal = pActor.kingdom;
                        Kingdom suzerain = GetSuzerain(vassal);
                        if (suzerain == null) return false;
                        Plot plot = World.world.plots.newPlot(pActor, pAsset, pForced);
                        plot.target_kingdom = suzerain;
                        return true;
                    },
                    check_should_continue = (Actor pActor) =>
                    {
                        if (pActor == null || pActor.plot == null) return false;
                        // 仍为附庸且宗主存活才继续谋划
                        if (!IsVassal(pActor.kingdom)) return false;
                        Kingdom suzerain = pActor.plot.target_kingdom;
                        return suzerain != null && suzerain.isAlive();
                    },
                    action = (Actor pActor) =>
                    {
                        Kingdom vassal = pActor.kingdom;
                        Kingdom suzerain = pActor.plot != null ? pActor.plot.target_kingdom : GetSuzerain(vassal);
                        if (vassal == null || suzerain == null) return false;
                        // 发动前再次验证条件；失败时给出明确原因提示（机制透明化——用户反馈"谋划失败不知道是机制还是bug"）
                        if (!VassalRules.CanStartIndependence(vassal, suzerain, ZhuluTime.CurrentYear))
                        {
                            string reason = (!vassal.isAlive() || !suzerain.isAlive())
                                ? "宗主已亡或自身覆灭"
                                : World.world.wars.isInWarWith(vassal, suzerain)
                                    ? "已处于交战中"
                                    : VassalManager.Instance.IsInIndependenceCooldown(vassal)
                                        ? "独立冷却期内（失败后20年不得再谋）"
                                        : "条件不满足";
                            ZhuluContext.Instance.LogInfo($"[VassalManager] 独立战争谋划发动失败：{vassal.name}（{reason}）");
                            try { WorldTip.showNow($"独立谋划失败：{reason}", false, "top", 3f, UI.ZhuluUIStyle.Hex.Negative); } catch { }
                            return false;
                        }
                        // 附庸正与第三方交战 → 也拒绝（谋划条件一致性）
                        if (vassal.getWars().GetEnumerator().MoveNext())
                        {
                            ZhuluContext.Instance.LogInfo($"[VassalManager] 独立战争谋划发动失败：{vassal.name}（正与他国交战）");
                            try { WorldTip.showNow("独立谋划失败：正与他国交战", false, "top", 3f, "#f73939"); } catch { }
                            return false;
                        }
                        return StartIndependenceWar(vassal, suzerain, ZhuluTime.CurrentYear);
                    }
                };
                independencePlot.check_can_be_forced = independencePlot.check_is_possible;
                independencePlot.get_formatted_description = FormatPlotDescription;

                AssetManager.plots_library.add(independencePlot);
                AssetManager.plots_library.basic_plots.Add(independencePlot);

                // 注册谋划本地化文本
                LM.AddToCurrentLocale("plot_zlr_independence_war_plot", "发动独立战争");
                LM.AddToCurrentLocale("plot_zlr_independence_war_plot_info", "$initiator_actor$ 正谋划脱离宗主 $target_kingdom$，重建邦国");
                LM.AddToCurrentLocale("plot_zlr_independence_war_plot_info_base", "谋划脱离宗主独立建国：打下宗主首都或坚持5年即可独立成功，失败后20年内不得再谋");

                ZhuluContext.Instance.LogInfo("[VassalManager] 独立战争谋划注册成功（附庸对宗主）");
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[VassalManager] 注册独立战争谋划失败", ex);
            }
        }

        /// <summary>谋划描述格式化：在进度条下方显示"某人对某国发动附庸战争"。</summary>
        private static string FormatPlotDescription(Plot pPlot)
        {
            try
            {
                if (pPlot == null) return "";
                string key = pPlot.getAsset()?.getDescriptionID() ?? "";
                string text = key.Localize();
                if (string.IsNullOrEmpty(text) || text == key) return "";
                Actor author = pPlot.getAuthor();
                text = text.Replace("$initiator_actor$", author?.getName() ?? pPlot.data?.founder_name ?? "");
                text = text.Replace("$initiator_kingdom$", author?.kingdom?.name ?? "");
                text = text.Replace("$target_kingdom$", pPlot.target_kingdom?.name ?? "");
                return text;
            }
            catch { return ""; }
        }

        // ───────────────────────────── 附庸战争条件判定 ─────────────────────────────

        /// <summary>检查是否可以发动附庸战争（委托给 VassalRules 纯判定）。</summary>
        public bool CanStartVassalWar(Kingdom attacker, Kingdom defender)
        {
            return VassalRules.CanStartVassalWar(attacker, defender);
        }

        /// <summary>是否有可附庸的相邻弱国。</summary>
        private bool HasWeakerNeighbor(Kingdom kingdom)
        {
            return GetWeakerNeighbor(kingdom) != null;
        }

        /// <summary>获取一个可附庸的相邻弱国。</summary>
        private Kingdom GetWeakerNeighbor(Kingdom kingdom)
        {
            if (kingdom == null || kingdom.cities == null || kingdom.cities.Count == 0) return null;

            var candidates = new List<Kingdom>();
            var checkedKingdoms = new HashSet<string>();

            // 遍历所有存活王国，找出符合条件的弱国
            if (World.world?.kingdoms?.list == null) return null;
            foreach (Kingdom other in World.world.kingdoms.list)
            {
                if (other == null || !other.isAlive() || other == kingdom) continue;
                string nId = other.id.ToString();
                if (checkedKingdoms.Contains(nId)) continue;
                checkedKingdoms.Add(nId);

                if (CanStartVassalWar(kingdom, other))
                    candidates.Add(other);
            }

            if (candidates.Count == 0) return null;

            // 城市达上限时，附庸战争概率增大（重复加入候选列表提高权重）
            int cityCount = kingdom.cities != null ? kingdom.cities.Count : 0;
            int cityCap = kingdom.getMaxCities();
            if (cityCount >= cityCap)
            {
                // 达到上限：候选列表翻倍（相当于概率翻倍）
                var boosted = new List<Kingdom>(candidates);
                boosted.AddRange(candidates);
                return boosted[UnityEngine.Random.Range(0, boosted.Count)];
            }

            return candidates[UnityEngine.Random.Range(0, candidates.Count)];
        }

        // ───────────────────────────── 序列化辅助 ─────────────────────────────

        public List<string> GetVassalIds(Kingdom suzerain)
        {
            string raw = KingdomDataExt.GetStr(suzerain, KEY_VASSALS);
            var result = new List<string>();
            if (string.IsNullOrEmpty(raw)) return result;
            // 简单逗号分隔存储
            string[] parts = raw.Split(',');
            foreach (string p in parts)
            {
                string trimmed = p.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                // 批次15：过滤已灭亡的附庸——防"失去附庸但宗主仍显示N附"
                try
                {
                    var kd = World.world?.kingdoms?.get(int.Parse(trimmed));
                    if (kd == null || !kd.isAlive()) continue;
                }
                catch { }
                result.Add(trimmed);
            }
            return result;
        }

        private void SaveVassalIds(Kingdom suzerain, List<string> ids)
        {
            KingdomDataExt.SetStr(suzerain, KEY_VASSALS, string.Join(",", ids));
        }
    
        // ═══════════ v2 快照存档接口 ═══════════
        // ---- batch10-A1: schema v5 section ----
        public sealed class VassalState
        {
            public string Suzerain;
            public List<string> Vassals = new List<string>();
            public long Since;
            public long IndependenceCooldown;
        }
        public string SectionName => "vassal";
        public Dictionary<string, VassalState> CaptureStates()
        {
            var d = new Dictionary<string, VassalState>();
            if (World.world?.kingdoms?.list == null) return d;
            foreach (Kingdom k in World.world.kingdoms.list)
            {
                if (k == null || !k.isAlive()) continue;
                d[k.id.ToString()] = new VassalState
                {
                    Suzerain = KingdomDataExt.GetStr(k, KEY_SUZERAIN, ""),
                    Vassals = GetVassalIds(k),
                    Since = KingdomDataExt.GetLong(k, KEY_VASSAL_SINCE, 0),
                    IndependenceCooldown = KingdomDataExt.GetLong(k, KEY_INDEPENDENCE_COOLDOWN, 0),
                };
            }
            return d;
        }
        public void ApplyStates(Dictionary<string, VassalState> data)
        {
            if (data == null) return;
            foreach (var kv in data)
            {
                Kingdom k = KingdomDataExt.FindKingdomById(kv.Key);
                if (k == null || !k.isAlive() || kv.Value == null) continue;
                KingdomDataExt.SetStr(k, KEY_SUZERAIN, kv.Value.Suzerain ?? "");
                SaveVassalIds(k, kv.Value.Vassals != null ? kv.Value.Vassals : new List<string>());
                KingdomDataExt.SetLong(k, KEY_VASSAL_SINCE, kv.Value.Since);
                KingdomDataExt.SetLong(k, KEY_INDEPENDENCE_COOLDOWN, kv.Value.IndependenceCooldown);
            }
        }

        public void MigrateFromCustomData() { }
        public void ResetRuntime()
        {
            _lastIndirectFixYear = -1;
            _lastIndependenceCheckYear = -1;
        }

}
}
