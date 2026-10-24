using System.Collections.Generic;
using ZhuluRebuild.Core;

namespace ZhuluRebuild.Domain
{
    /// <summary>
    /// 氏族成员上限执行器 v3.1（性能根修版）。
    ///
    /// v3.0 的致命伤（用户实锤"50年地图卡住但时间在动"）：FindUnderLimitClan 每迁一人
    /// 全天下氏族成员表重扫一遍——受害者数×氏族数×单位数平方级爆炸，世界有大量无族者
    /// （批次15旧伤）时年度结算一卡数分钟。
    ///
    /// v3.1：单遍 O(U) 建索引（氏族→上限/人数/王国/剩余容量），迁移与收编改为
    /// O(1) 队列取目标；另设年度动作上限封顶单帧开销。
    /// 行为与 v3.0 一致：永不禁族、迁移原城市不动、同国未满收编→裂植新族、
    /// 豁免族长/国王直系（本人+父母+子女）、上限确定值王99普18、旧档无族者收编。
    /// </summary>
    public static class ClanMemberCapEnforcer
    {
        private const int ROYAL_LIMIT = 99;
        private const int COMMON_LIMIT = 18;
        private const int MinSplinterSize = 4;
        /// <summary>年度动作上限（迁移+裂植+收编合计）——封顶单年结算耗时。</summary>
        private const int MaxActionsPerYear = 4000;

        /// <summary>氏族索引条目（单遍构建，全程复用——不再逐受害者重扫）。</summary>
        private sealed class ClanStat
        {
            public Clan Clan;
            public int Limit;
            public int Count;
            public Kingdom Kingdom;
            public int Room => Limit - Count;
        }

        public static void EnforceAll(int year)
        {
            var all = World.world?.units?.getSimpleList();
            var clans = World.world?.clans?.list;
            if (all == null || clans == null) return;

            try
            {
                // ── ① 王室氏族集合 ──
                var royalIds = new HashSet<long>();
                if (World.world?.kingdoms?.list != null)
                    foreach (Kingdom k in World.world.kingdoms.list)
                    {
                        try
                        {
                            if (k == null || !k.isAlive() || !k.hasKing()) continue;
                            var kc = k.getKingClan();
                            if (kc != null && kc.isAlive()) royalIds.Add(kc.id);
                        }
                        catch { }
                    }

                // ── ② 单遍 O(U)：氏族索引 + 无族市民收集 ──
                var stats = new Dictionary<long, ClanStat>();
                var clanless = new List<Actor>();
                foreach (Actor u in all)
                {
                    if (u == null || !u.isAlive()) continue;
                    if (u.clan != null && u.clan.data != null)
                    {
                        long cid = u.clan.getID();
                        if (!stats.TryGetValue(cid, out var st))
                        {
                            st = new ClanStat
                            {
                                Clan = u.clan,
                                Limit = royalIds.Contains(cid) ? ROYAL_LIMIT : COMMON_LIMIT,
                                Kingdom = u.kingdom
                            };
                            stats[cid] = st;
                        }
                        st.Count++;
                    }
                    else if (u.kingdom != null && u.kingdom.isAlive() && u.city != null)
                    {
                        clanless.Add(u);
                    }
                }

                // ── ③ 王国 → 有容量的氏族队列（容量降序；一次构建，全程 O(1) 取目标）──
                var kingdomQueues = new Dictionary<string, List<ClanStat>>();
                foreach (var st in stats.Values)
                {
                    if (st.Kingdom == null || !st.Kingdom.isAlive() || st.Room <= 0) continue;
                    string key = st.Kingdom.id.ToString();
                    if (!kingdomQueues.TryGetValue(key, out var q)) { q = new List<ClanStat>(); kingdomQueues[key] = q; }
                    q.Add(st);
                }
                foreach (var q in kingdomQueues.Values) q.Sort((a, b) => b.Room.CompareTo(a.Room));

                int actions = 0;
                int migrated = 0, splintered = 0, adopted = 0;

                // ── ④ 超限氏族处理（O(超额)——索引现成；先物化清单——裂植会往 stats 加新条目，
                //    v3.1 曾因边枚举边改字典抛 Collection modified 当年中止，收编自愈从未执行）──
                var overLimit = new List<ClanStat>();
                foreach (var st in stats.Values)
                    if (st.Count > st.Limit) overLimit.Add(st);
                foreach (var st in overLimit)
                {
                    if (actions >= MaxActionsPerYear) break;
                    if (st.Count <= st.Limit || st.Clan == null || !st.Clan.isAlive()) continue;
                    int excess = st.Count - st.Limit;

                    var protectedIds = BuildProtectedIds(st.Clan, st.Kingdom);
                    var members = st.Clan.units;
                    if (members == null) continue;

                    var candidates = new List<Actor>();
                    foreach (Actor m in members)
                    {
                        if (m == null || !m.isAlive()) continue;
                        if (protectedIds.Contains(m.getID())) continue;
                        candidates.Add(m);
                    }
                    candidates.Sort((a, b) => GetAge(a).CompareTo(GetAge(b)));
                    if (candidates.Count < excess) excess = candidates.Count;

                    var splinterPool = new List<Actor>();
                    string kKey = st.Kingdom != null && st.Kingdom.isAlive() ? st.Kingdom.id.ToString() : "";
                    for (int i = 0; i < excess && actions < MaxActionsPerYear; i++)
                    {
                        Actor victim = candidates[i];
                        ClanStat target = TakeTarget(kingdomQueues, kKey);
                        if (target != null)
                        {
                            try
                            {
                                victim.setClan(target.Clan);
                                // 阶段3H-17：立即按目标氏族姓改姓名（不等年度同步——消除同族异姓过渡期）
                                Naming.ZhuluChineseNameService.Instance.ApplyClanSurnameTo(victim, target.Clan);
                                target.Count++;
                                migrated++;
                                actions++;
                            }
                            catch { }
                        }
                        else splinterPool.Add(victim);
                    }

                    if (splinterPool.Count >= MinSplinterSize && actions < MaxActionsPerYear)
                    {
                        if (SplinterNewClan(splinterPool, royalIds, kingdomQueues, stats))
                        {
                            splintered++;
                            actions += splinterPool.Count;
                        }
                    }
                }

                // ── ⑤ 旧档自愈：无族市民收编（同队列 O(1)）──
                foreach (Actor u in clanless)
                {
                    if (actions >= MaxActionsPerYear) break;
                    try
                    {
                        Kingdom k = u.kingdom;
                        if (k == null || !k.isAlive()) continue;
                        ClanStat target = TakeTarget(kingdomQueues, k.id.ToString());
                        if (target == null) continue;
                        u.setClan(target.Clan);
                        Naming.ZhuluChineseNameService.Instance.ApplyClanSurnameTo(u, target.Clan);
                        target.Count++;
                        adopted++;
                        actions++;
                    }
                    catch { }
                }

                if (migrated > 0 || splintered > 0 || adopted > 0)
                    ZhuluContext.Instance.LogInfo(
                        $"[ClanCap] year={year} 迁移={migrated} 裂植={splintered}族 收编无族者={adopted}" +
                        (actions >= MaxActionsPerYear ? $"（达年度上限{MaxActionsPerYear}，余量下年续）" : ""));
            }
            catch (System.Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[ClanCap] 执行异常: {ex.Message}");
            }
        }

        /// <summary>从王国队列取一个有容量的目标（O(1) 弹出；容量耗尽的条目丢弃）。</summary>
        private static ClanStat TakeTarget(Dictionary<string, List<ClanStat>> queues, string kingdomKey)
        {
            if (string.IsNullOrEmpty(kingdomKey)) return null;
            if (!queues.TryGetValue(kingdomKey, out var q)) return null;
            while (q.Count > 0)
            {
                var st = q[0];
                if (st.Room > 0) return st;
                q.RemoveAt(0);   // 容量已满——出队
            }
            return null;
        }

        /// <summary>豁免集合：族长本人+父母+子女；国王本人+父母+子女（直系血脉——用户定稿）。</summary>
        private static HashSet<long> BuildProtectedIds(Clan clan, Kingdom kingdom)
        {
            var set = new HashSet<long>();
            try
            {
                Actor chief = null;
                try { chief = clan.getChief(); } catch { }
                CollectDirectLine(chief, kingdom, set);
            }
            catch { }
            try
            {
                if (kingdom != null && kingdom.hasKing()) CollectDirectLine(kingdom.king, kingdom, set);
            }
            catch { }
            return set;
        }

        private static void CollectDirectLine(Actor person, Kingdom kingdom, HashSet<long> set)
        {
            if (person == null || !person.isAlive()) return;
            set.Add(person.getID());
            try
            {
                var parents = person.getParents();
                if (parents != null)
                    foreach (Actor p in parents)
                        if (p != null && p.isAlive()) set.Add(p.getID());
            }
            catch { }
            try
            {
                var children = KinshipTracker.GetAliveChildren(kingdom, person.getID().ToString());
                if (children != null)
                    foreach (Actor c in children)
                        if (c != null && c.isAlive()) set.Add(c.getID());
            }
            catch { }
        }

        /// <summary>裂植新氏族：最年长者任首领，原生 newClan API；新族入索引并可继续当收编目标。</summary>
        private static bool SplinterNewClan(List<Actor> members, HashSet<long> royalIds,
            Dictionary<string, List<ClanStat>> queues, Dictionary<long, ClanStat> stats)
        {
            if (members == null || members.Count == 0) return false;
            try
            {
                members.Sort((a, b) => GetAge(b).CompareTo(GetAge(a)));
                Actor leader = null;
                Kingdom kingdom = null;
                foreach (Actor m in members)
                {
                    if (m == null || !m.isAlive()) continue;
                    if (leader == null) leader = m;
                    if (kingdom == null && m.kingdom != null && m.kingdom.isAlive()) kingdom = m.kingdom;
                    if (leader != null && kingdom != null) break;
                }
                if (leader == null) return false;

                Clan newClan = World.world.clans.newClan(leader, true);
                if (newClan == null) return false;

                foreach (Actor m in members)
                {
                    if (m == null || !m.isAlive() || m == leader) continue;
                    try { m.setClan(newClan); } catch { }
                }
                // 阶段3H-17：新族立即中文名（SetClanName 末尾自带全族姓氏统一）——
                // 不走命名管线的话氏族名是原版随机名，族内姓氏永不统一（用户实锤）
                try { Naming.ZhuluChineseNameService.Instance.SetClanName(newClan, leader); } catch { }

                var st = new ClanStat { Clan = newClan, Limit = COMMON_LIMIT, Kingdom = kingdom, Count = 1 };
                stats[newClan.id] = st;
                if (kingdom != null && st.Room > 0)
                {
                    string key = kingdom.id.ToString();
                    if (!queues.TryGetValue(key, out var q)) { q = new List<ClanStat>(); queues[key] = q; }
                    q.Add(st);
                }

                return true;
            }
            catch { return false; }
        }

        private static int GetAge(Actor a)
        {
            try { return a.getAge(); } catch { return 0; }
        }
    }
}
