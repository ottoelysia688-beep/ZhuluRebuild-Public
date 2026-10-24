using System.Collections.Generic;
using ZhuluRebuild.Core;

namespace ZhuluRebuild.Domain
{
    /// <summary>
    /// 城市崩塌看门狗（诊断工具，零行为影响）。
    /// 用户报告：几百年后人口正常，但某一年城市人口单位突然消失、城市毁灭——渐进理论解释不了
    /// 突发型，需要当场取证。年度对比各城快照，捕获两类事件并打出完整现场：
    ///   ① 城市从活城列表消失（被移除/并吞）——打印消失前状态；
    ///   ② 一年内人口骤降 ≥50%（屠杀/迁出/饥饿）——打印前后对比。
    /// 现场字段：城名/王国/前后人口/无族市民数/王国城数与上限/是否交战/厌战——
    /// 足以区分四大嫌疑：战争屠城、王国超限拆城、无族者迁离（批次15旧伤）、饥饿/灾难。
    /// </summary>
    public static class CityCrashWatchdog
    {
        private sealed class Snap
        {
            public string Name = "";
            public string KingdomId = "";
            public string KingdomName = "";
            public int Pop;
            public int Clanless;
            public bool AtWar;
            public int Exhaustion;
            public int KingdomCities;
            public int KingdomMaxCities;
        }

        private static readonly Dictionary<long, Snap> _last = new Dictionary<long, Snap>();
        private static readonly Dictionary<long, int> _zeroPopYears = new Dictionary<long, int>();
        private static int _lastYear = -1;

        public static void OnYear(int year)
        {
            try
            {
                if (year == _lastYear) return;
                _lastYear = year;
                var cities = World.world?.cities?.list;
                if (cities == null) return;

                var seen = new HashSet<long>();
                foreach (City c in cities)
                {
                    try
                    {
                        if (c == null || !c.isAlive()) continue;
                        long cid = c.id;
                        seen.Add(cid);
                        Snap cur = BuildSnap(c);
                        _last.TryGetValue(cid, out Snap prev);
                        if (prev != null)
                        {
                            if (cur.Pop + 4 < prev.Pop / 2 && prev.Pop >= 10)
                                ZhuluContext.Instance.LogWarning(FormatCrash(year, prev, cur, false));
                        }
                        // 阶段3H-21 验尸修复：零人口城治理——人口清零的城会让原版城建AI
                        // (CityBehBuild.calcPossibleBuildings) 每帧 NRE（getActorAsset 采不到样本），
                        // 炸断 CityManager.update 队列=邻城集体冻结饿死（3000→几十雪崩的执行器）。
                        // 连续两年零人口 → 销毁（原版 destroyCity，正常触发'城市已毁灭'事件）
                        if (cur.Pop == 0)
                        {
                            int zeroYears = _zeroPopYears.TryGetValue(cid, out int z) ? z + 1 : 1;
                            _zeroPopYears[cid] = zeroYears;
                            if (zeroYears >= 2)
                            {
                                try
                                {
                                    string nm = cur.KingdomName + "·" + cur.Name;
                                    c.destroyCity();
                                    ZhuluContext.Instance.LogWarning($"[CityCrash] {year}年 零人口城治理: {nm} 连续{zeroYears}年无人，已销毁（防城建AI每帧NRE级联）");
                                }
                                catch { }
                                _zeroPopYears[cid] = 0;
                            }
                        }
                        else _zeroPopYears[cid] = 0;
                        _last[cid] = cur;
                    }
                    catch { }
                }

                // 消失的城市（上年活着、本年不在活城列表）
                foreach (var kv in _last)
                {
                    if (seen.Contains(kv.Key)) continue;
                    ZhuluContext.Instance.LogWarning(FormatCrash(year, kv.Value, null, true));
                }
                _last.Clear();
                _zeroPopYears.Clear();
                // 重建（只留活城）
                foreach (City c in cities)
                {
                    try
                    {
                        if (c == null || !c.isAlive()) continue;
                        _last[c.id] = BuildSnap(c);
                    }
                    catch { }
                }
            }
            catch (System.Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[CityCrash] 看门狗异常: {ex.Message}");
            }
        }

        private static Snap BuildSnap(City c)
        {
            var s = new Snap();
            try { s.Name = c.name ?? "?"; } catch { }
            try
            {
                Kingdom k = Adapters.NativePatchHelpers.GetKingdomFromCity(c);
                if (k != null && k.isAlive())
                {
                    s.KingdomId = k.id.ToString();
                    s.KingdomName = k.name ?? "";
                    try { var wars = k.getWars(); if (wars != null) foreach (var w in wars) { s.AtWar = true; break; } } catch { }
                    s.Exhaustion = ZhuluWarService.GetWarExhaustion(k.id.ToString());
                    try { s.KingdomCities = k.cities?.Count ?? 0; } catch { }
                    try { s.KingdomMaxCities = k.getMaxCities(); } catch { }
                }
            }
            catch { }
            try
            {
                int pop = 0, clanless = 0;
                if (c.units != null)
                    foreach (Actor u in c.units)
                    {
                        if (u == null || !u.isAlive()) continue;
                        pop++;
                        if (u.clan == null || u.clan.data == null) clanless++;
                    }
                s.Pop = pop;
                s.Clanless = clanless;
            }
            catch { }
            return s;
        }

        private static string FormatCrash(int year, Snap prev, Snap cur, bool removed)
        {
            string now = cur == null
                ? "已从活城列表消失"
                : $"现人口{cur.Pop}（无族{cur.Clanless}）";
            return $"[CityCrash] {year}年 城市异常事件：{prev.KingdomName}·{prev.Name} " +
                   $"上一年人口{prev.Pop}（无族{prev.Clanless}｜交战={prev.AtWar}｜厌战={prev.Exhaustion}｜" +
                   $"王国城数{prev.KingdomCities}/{prev.KingdomMaxCities}）→ {now}";
        }
    }
}
