using System;
using HarmonyLib;
using ZhuluRebuild.Core;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Domain
{
    /// <summary>
    /// 城主官职化服务（2026-08-16 A 方案定稿）：
    /// ① 城主权势 = 10 + 任期×2 + min(10, 城人口/100) + 武将/宗室+3
    /// ② 替换：国王权力值 ≥ 城主权势 → 安全撤职；城主>国王 → 尾大不掉（撤职触发叛乱）
    /// ③ 撤职叛乱：被撤高权城主 → inspire 式叛乱（复用原版 useInspire）
    /// ④ 退休：城主 ≥60 岁 → 告老让位
    /// ⑤ 首都城主豁免撤职（原版叛乱豁免首都）
    /// ⑥ 补位：武将/宗室优先
    /// </summary>
    public static class CityLeaderService
    {
        private const string KEY_LEADER_POWER = "leader_power_"; // + 城id → 城主权势

        // ═══════════ ① 城主权势 ═══════════

        public static int GetLeaderPower(City city, Kingdom kingdom)
        {
            try
            {
                if (city?.leader == null) return 0;
                string cid = city.id.ToString();
                // 持久化权势（任期累积）
                long stored = KingdomDataExt.GetLong(kingdom, KEY_LEADER_POWER + cid, 0);
                int power = 10 + (int)stored;
                // 城人口加成
                int pop = city.units != null ? city.units.Count : 0;
                power += Math.Min(10, pop / 100);
                // 身份加成（武将/宗室）
                var idType = IdentityClassifier.Instance.GetIdentity(city.leader.getID());
                if (idType == IdentityClassifier.IdentityType.Military) power += 3;
                else if (idType == IdentityClassifier.IdentityType.Imperial) power += 3;
                return Math.Min(100, power);
            }
            catch { return 0; }
        }

        /// <summary>年度累积城主权势（任期 +2/年；中央集权政策压制 -30%）。</summary>
        public static void TickLeaderPower(Kingdom kingdom, int year)
        {
            try
            {
                if (kingdom?.cities == null) return;
                string kId = kingdom.id.ToString();
                // 中央集权压制
                float mult = 1.0f;
                try
                {
                    var actives = PolicyManager.Instance.GetActivePolicies(kId);
                if (actives != null)
                {
                    foreach (var ap in actives)
                    {
                        if (ap != null && ap.PolicyId == "gov_central") { mult = 0.7f; break; }
                    }
                }
                }
                catch { }
                foreach (var c in kingdom.cities)
                {
                    if (c == null || c.leader == null) continue;
                    string cid = c.id.ToString();
                    long stored = KingdomDataExt.GetLong(kingdom, KEY_LEADER_POWER + cid, 0);
                    KingdomDataExt.SetLong(kingdom, KEY_LEADER_POWER + cid, Math.Min(40, (long)((stored + 2) * mult)));
                }
            }
            catch { }
        }

        // ═══════════ ② 替换判定 ═══════════

        /// <summary>能否安全撤职（国王权力值 ≥ 城主权势+10 才安全；否则尾大不掉）。</summary>
        public static bool CanSafelyRemove(Kingdom kingdom, City city)
        {
            try
            {
                int kingPower = KingdomExt.GetPowerValue(kingdom.id.ToString());
                int leaderPower = GetLeaderPower(city, kingdom);
                return kingPower >= leaderPower + 10;
            }
            catch { return false; }
        }

        /// <summary>撤职（返回是否成功；高权势城主【因撤职】→ 触发叛乱；退休不叛乱）。
        /// 2026-08-16 修复：退休（reason=retire）安静让位，不播"被夺职起兵"。</summary>
        public static bool RemoveCityLeader(Kingdom kingdom, City city, bool isPlayerAction, string reason = "dismiss")
        {
            try
            {
                if (city?.leader == null) return false;
                // 首都豁免
                if (kingdom.capital != null && city.id == kingdom.capital.id) return false;
                bool safe = CanSafelyRemove(kingdom, city);
                if (!safe && !isPlayerAction) return false; // AI 不冒险撤

                int leaderPower = GetLeaderPower(city, kingdom);
                Actor oldLeader = city.leader;
                string oldName = oldLeader.getName() ?? "城主";
                // 清权势记录
                KingdomDataExt.SetLong(kingdom, KEY_LEADER_POWER + city.id.ToString(), 0);
                // 撤职（原版移除 leader——Traverse 调用 removeLeader？用 setLeader(null)?——原版无直接"解职"：
                // 用 Traverse 清 leader 字段 + 职业还原）
                try
                {
                    // 2026-08-16 修复：City.leader 是 public 字段、data.leaderID 是 public 属性——直接赋值（Traverse 曾致撤职失败）
                    city.leader = null;
                    city.data.leaderID = -1L;
                    // 职业还原（setProfession 是 internal——Traverse 调用）
                    try { Traverse.Create(oldLeader).Method("setProfession", UnitProfession.Unit, true).GetValue(); } catch { }
                }
                catch { return false; }

                // 高权势被【撤职】→ 叛乱（复用原版 useInspire）；退休（retire）/任期届满（term）安静让位
                if (reason != "retire" && reason != "term" && leaderPower >= 40)
                {
                    try
                    {
                        Traverse.Create(oldLeader).Method("addStatusEffect", "voices_in_my_head", 0f, true).GetValue();
                        // useInspire 是 internal——用 Traverse 调用
                        Traverse.Create(city).Method("useInspire", oldLeader).GetValue();
                        ZhuluContext.Instance.LogWarning($"[CityLeader] {oldName} 被夺职，起兵反叛！（权势{leaderPower}）");
                        try { ZhuluWorldTip.Show(null, $"【{oldName}】不服夺职，起兵反叛！", 8f); } catch { }
                    }
                    catch
                    {
                        // useInspire 失败兜底：直接记叛乱倾向（inspire 战争由原版后续触发）
                        ZhuluContext.Instance.LogWarning($"[CityLeader] {oldName} 被夺职，叛乱倾向↑（权势{leaderPower}）");
                    }
                }
                else if (reason != "retire" && reason != "term")
                {
                    ZhuluContext.Instance.LogInfo($"[CityLeader] {oldName} 被撤职（安全）");
                }
                return true;
            }
            catch { return false; }
        }

        // ═══════════ ④ 退休 ═══════════

        /// <summary>城主年龄 ≥60 → 告老让位（年度检查）。
        /// 2026-08-16 修复：跳过国王（登基后城主身份残留——国王不退休）。</summary>
        public static void TickRetirement(Kingdom kingdom, City city)
        {
            try
            {
                if (city?.leader == null) return;
                // 国王不当城主——登基后残留身份不清（跳过，防"皇帝告老"误伤）
                try
                {
                    if (kingdom?.king != null && city.leader.getID() == kingdom.king.getID()) return;
                }
                catch { }
                try
                {
                    if (city.leader.getAge() >= 60)
                    {
                        string name = city.leader.getName() ?? "城主";
                        RemoveCityLeader(kingdom, city, true, "retire");
                        ZhuluContext.Instance.LogInfo($"[CityLeader] {name} 告老让位");
                    }
                }
                catch { }
            }
            catch { }
        }

        // ═══════════ ④b 任期限制（2026-08-18 用户需求：45 年）═══════════

        /// <summary>任期起始标记 key（actor custom_data_string："{actorId}|{year}"——换人自愈重置）。</summary>
        private const string KEY_TERM_SINCE = "zlr_term_since";

        /// <summary>
        /// 城主任期检查（年度）：同一位城主在职 ≥ OfficeManager.TermLimitYears（45 年）→ 任期届满卸任。
        /// 起始年存于城主本人 custom_data（"actorId|year"）——武举任命/自动补位/原版任命全覆盖，
        /// 换人后标记随新 actor 重建（首次观察年即起始年，旧档兼容）。届满走安静卸任（不触发夺职叛乱）。
        /// </summary>
        public static void TickTermLimit(Kingdom kingdom, City city, int year)
        {
            try
            {
                if (city?.leader == null) return;
                try
                {
                    if (kingdom?.king != null && city.leader.getID() == kingdom.king.getID()) return;
                }
                catch { }
                Actor leader = city.leader;
                string mark = Naming.ActorAccess.GetString(leader, KEY_TERM_SINCE);
                string aid = leader.getID().ToString();
                if (string.IsNullOrEmpty(mark) || !mark.StartsWith(aid + "|"))
                {
                    // 新任城主（或旧档首次观察）——记录起始年
                    Naming.ActorAccess.SetString(leader, KEY_TERM_SINCE, aid + "|" + year);
                    return;
                }
                int start = 0;
                int.TryParse(mark.Substring(mark.IndexOf('|') + 1), out start);
                if (start > 0 && year - start >= Domain.OfficeManager.TermLimitYears)
                {
                    string name = leader.getName() ?? "城主";
                    bool ok = RemoveCityLeader(kingdom, city, true, "term");
                    if (ok)
                    {
                        Naming.ActorAccess.SetString(leader, KEY_TERM_SINCE, ""); // 清标记
                        try { PersonHistoryManager.Instance.RecordEvent(leader.getID(),
                            PersonHistoryManager.CAT_SHITU,
                            $"城主任职 {Domain.OfficeManager.TermLimitYears} 年届满，荣升荣休", year); } catch { }
                        ZhuluContext.Instance.LogInfo($"[CityLeader] {name} 任期届满（{Domain.OfficeManager.TermLimitYears}年）卸任");
                    }
                }
            }
            catch { }
        }

        // ═══════════ ⑥ 补位（空缺城主 → 武将/宗室优先） ═══════════

        /// <summary>补位城主：武将/宗室优先；无则国民吏治最高者。返回补位 actor id（null=未补）。
        /// 2026-08-16 修复：setLeader 不更新 actor.city → isCityLeader 失效 → 同一人补多城；
        /// usedThisTick 去重（本次年度每 actor 只补一次）。</summary>
        public static long? FillCityLeader(Kingdom kingdom, City city, System.Collections.Generic.HashSet<long> usedThisTick = null)
        {
            try
            {
                if (city == null || city.leader != null) return null;
                if (kingdom?.units == null) return null;
                // 国王不能当城主
                Actor king = kingdom.king;
                Actor bestMilitary = null, bestImperial = null, bestCivil = null;
                int bestMilScore = -1, bestImpScore = -1, bestCivScore = -1;
                foreach (var u in kingdom.units)
                {
                    if (u == null || !u.isAlive()) continue;
                    if (king != null && u.getID() == king.getID()) continue;
                    if (u.isCityLeader() || u.isKing()) continue;
                    // 本次年度已补位过 → 跳过（防同一人补多城）
                    if (usedThisTick != null && usedThisTick.Contains(u.getID())) continue;
                    // 2026-08-16 修复：补位筛年龄——<50 优先（避免补到 60+ 老人立即告老循环）
                    int age = 0;
                    try { age = u.getAge(); } catch { }
                    if (age >= 55) continue;
                    var idType = IdentityClassifier.Instance.GetIdentity(u.getID());
                    int s = u.warfare + u.stewardship;
                    if (idType == IdentityClassifier.IdentityType.Military && s > bestMilScore)
                    { bestMilScore = s; bestMilitary = u; }
                    else if (idType == IdentityClassifier.IdentityType.Imperial && s > bestImpScore)
                    { bestImpScore = s; bestImperial = u; }
                    else if (s > bestCivScore) { bestCivScore = s; bestCivil = u; }
                }
                Actor pick;
                // 2026-08-18 郡国并行：颁行「郡国并行」时城市领主优先宗室（分封宗室拱卫——设计文案落实）
                bool feudalActive = false;
                try { feudalActive = PolicyManager.Instance.IsPolicyActive(kingdom.id.ToString(), "gov_feudal"); } catch { }
                if (feudalActive && bestImperial != null) pick = bestImperial;
                else pick = bestMilitary ?? bestImperial ?? bestCivil;
                if (pick == null) return null; // 无人可选
                // 原版 setLeader（公开 API）
                try
                {
                    city.setLeader(pick, false);
                    ZhuluContext.Instance.LogInfo($"[CityLeader] 城主补位: {pick.getName()} → {city.data?.name ?? "城"}（{(feudalActive && bestImperial == pick ? "宗室(郡国并行)" : bestMilitary == pick ? "武将" : bestImperial == pick ? "宗室" : "国民")}）");
                    return pick.getID();
                }
                catch { return null; }
            }
            catch { return null; }
        }
    }
}
