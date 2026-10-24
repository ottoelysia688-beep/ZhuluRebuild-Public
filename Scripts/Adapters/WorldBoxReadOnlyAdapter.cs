using System;
using System.Collections.Generic;
using UnityEngine;
using HarmonyLib;
using ZhuluRebuild.Core;
using ZhuluRebuild.Domain;

namespace ZhuluRebuild.Adapters
{
    public enum FactAvailability
    {
        Known,
        Unknown,
        Unavailable
    }

    public class ActorFactSnapshot
    {
        public string ActorId { get; set; }
        public string Name { get; set; }
        public bool IsAlive { get; set; }
        public int Age { get; set; }
        public int Level { get; set; } = 1;
        public int Military { get; set; }
        public int Stewardship { get; set; }
        public int Diplomacy { get; set; }
        public int Intelligence { get; set; }
        public int Renown { get; set; }
        public FactAvailability RenownAvailability { get; set; } = FactAvailability.Known;
    }

    public class KingdomFactSnapshot
    {
        public string KingdomId { get; set; }
        public string KingdomName { get; set; }
        public string RulerId { get; set; }
        public string RulerName { get; set; }
        public int RulerLevel { get; set; } = 5;
        public int FoundedYear { get; set; } = 1;
        public int CityCount { get; set; }
        public int TotalPopulation { get; set; }
        public int TotalMilitary { get; set; }
        public int ActiveWarsCount { get; set; }
        public FactAvailability FactStatus { get; set; } = FactAvailability.Known;
    }

    public class WorldFactSnapshot
    {
        public int CurrentYear { get; set; }
        public int KingdomCount { get; set; }
        public int ActiveWarsCount { get; set; }
        public List<KingdomFactSnapshot> Kingdoms { get; set; } = new List<KingdomFactSnapshot>();
    }

    public class WorldBoxReadOnlyAdapter
    {
        public static WorldBoxReadOnlyAdapter Instance { get; } = new WorldBoxReadOnlyAdapter();

        private WorldBoxReadOnlyAdapter() { }

        private bool IsValidCitizenUnit(Actor unit)
        {
            if (unit == null || !unit.isAlive()) return false;

            try
            {
                if (unit.asset != null)
                {
                    string assetId = unit.asset.id != null ? unit.asset.id.ToLower() : "";
                    if (assetId.Contains("boat") || assetId.Contains("ship") || assetId.Contains("cart") || assetId.Contains("vehicle"))
                    {
                        return false;
                    }
                }
            }
            catch { }

            return true;
        }

        private WorldFactSnapshot CreateFallbackSnapshot()
        {
            return new WorldFactSnapshot
            {
                CurrentYear = 1,
                KingdomCount = 1,
                ActiveWarsCount = 0,
                Kingdoms = new List<KingdomFactSnapshot>
                {
                    new KingdomFactSnapshot
                    {
                        KingdomId = "k_1",
                        KingdomName = "齐国",
                        RulerId = "r_1",
                        RulerName = "姜子牙",
                        RulerLevel = 8,
                        FoundedYear = 1,
                        CityCount = 6,
                        TotalPopulation = 280,
                        TotalMilitary = 75,
                        FactStatus = FactAvailability.Unknown
                    }
                }
            };
        }

        /// <summary>
        /// 读取人物四维属性。0.51.2 反编译验证：Actor 提供 public 属性
        /// intelligence / diplomacy / warfare / stewardship / renown / level。
        /// 历史教训：对 stats 对象做 Property/Field 反射永远命中失败，会静默退化成哈希伪随机分。
        /// </summary>
        /// <summary>读基础值（共享实现见 OfficialRatingManager.GetBaseStat；0.51.2 stats 合并源码确认 data 是基础层）。</summary>
        private int GetBaseStat(Actor unit, string statId)
        {
            return Domain.OfficialRatingManager.GetBaseStat(unit, statId);
        }

        /// <summary>
        /// 读四维（阶段2B 方案3，2026-08-15 定稿）：基础×0.5 + 最终×0.5 混合。
        /// 依据：纯最终值（stats 合并含遗传/亚种随机）跨图波动 2.5 倍致官评通胀（新图 B+ 34.9%），
        /// 基础值稳定（存档 mean 2.2）；混合 = 稳定为底 + 捕捉特质成长。
        /// 注意：权重与 OfficialRatingManager 保持一致（0.5/0.5），改动须两处同步。
        /// </summary>
        private int GetActorStat(Actor unit, string statId)
        {
            if (unit == null) return 0;
            try
            {
                switch (statId)
                {
                    // 0.5/0.5（与 OfficialRatingManager 一致，2026-08-15 权重实测定稿）
                    case "warfare": return Mathf.RoundToInt(GetBaseStat(unit, "warfare") * 0.5f + unit.warfare * 0.5f);
                    case "stewardship": return Mathf.RoundToInt(GetBaseStat(unit, "stewardship") * 0.5f + unit.stewardship * 0.5f);
                    case "diplomacy": return Mathf.RoundToInt(GetBaseStat(unit, "diplomacy") * 0.5f + unit.diplomacy * 0.5f);
                    case "intelligence": return Mathf.RoundToInt(GetBaseStat(unit, "intelligence") * 0.5f + unit.intelligence * 0.5f);
                    case "renown": return unit.renown;
                    default: return 0;
                }
            }
            catch
            {
                return 0;
            }
        }

        private int GetActorLevel(Actor unit)
        {
            try
            {
                if (unit != null) return unit.level;
            }
            catch { }
            return 1;
        }

        public WorldFactSnapshot CaptureWorldSnapshot()
        {
            var snapshot = new WorldFactSnapshot();

            try
            {
                MapBox world = World.world ?? MapBox.instance;
                if (world != null)
                {
                    snapshot.CurrentYear = ZhuluTime.CurrentYear;

                    if (world.kingdoms != null && world.kingdoms.list != null)
                    {
                        foreach (Kingdom kingdom in world.kingdoms.list)
                        {
                            if (kingdom == null || !kingdom.isAlive()) continue;

                            int cityCount = kingdom.cities != null ? kingdom.cities.Count : 0;
                            int population = kingdom.units != null ? kingdom.units.Count : 0;

                            var kFact = new KingdomFactSnapshot
                            {
                                KingdomId = kingdom.id.ToString(),
                                KingdomName = kingdom.name ?? "无名之国",
                                CityCount = cityCount,
                                TotalPopulation = population,
                                TotalMilitary = population,
                                FoundedYear = KingdomExt.GetDynastyBirthYear(kingdom.id.ToString(), snapshot.CurrentYear),
                                FactStatus = FactAvailability.Known
                            };

                            if (kingdom.king != null)
                            {
                                try
                                {
                                    kFact.RulerId = kingdom.king.getID().ToString();
                                }
                                catch
                                {
                                    kFact.RulerId = "k_ruler";
                                }
                                kFact.RulerName = kingdom.king.getName() ?? kingdom.king.name ?? "无名君";
                                kFact.RulerLevel = GetActorLevel(kingdom.king);
                            }
                            else
                            {
                                kFact.RulerName = "国君空缺";
                                kFact.RulerLevel = 0;
                            }

                            snapshot.Kingdoms.Add(kFact);
                        }

                        snapshot.KingdomCount = snapshot.Kingdoms.Count;
                    }

                    if (world.wars != null)
                    {
                        int activeWars = 0;
                        foreach (War war in world.wars)
                        {
                            if (war != null)
                            {
                                activeWars++;
                            }
                        }
                        snapshot.ActiveWarsCount = activeWars;
                    }
                }
                else
                {
                    return CreateFallbackSnapshot();
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[WorldBoxReadOnlyAdapter] 读取原生世界事实发生异常，使用降级只读数据", ex);
                return CreateFallbackSnapshot();
            }

            return snapshot;
        }

        /// <summary>按 ID 精确解析王国快照；仅当输入不是 ID 时才按名字匹配兜底。绝不静默回退到“第一个国家”。</summary>
        public KingdomFactSnapshot CaptureKingdomSnapshot(string kingdomId)
        {
            var worldSnap = CaptureWorldSnapshot();
            if (worldSnap?.Kingdoms == null || worldSnap.Kingdoms.Count == 0) return null;
            if (string.IsNullOrEmpty(kingdomId)) return null;

            // 1. 精确匹配 KingdomId
            foreach (var k in worldSnap.Kingdoms)
            {
                if (k.KingdomId == kingdomId) return k;
            }

            // 2. 名字兜底匹配（历史调用方可能传国名）
            string searchBase = TitleSuffixPatches.StripKingSuffix(kingdomId);
            if (!string.IsNullOrEmpty(searchBase))
            {
                foreach (var k in worldSnap.Kingdoms)
                {
                    string kBase = TitleSuffixPatches.StripKingSuffix(k.KingdomName);
                    if (k.KingdomName == kingdomId || kBase == searchBase)
                    {
                        return k;
                    }
                }
            }

            return null;
        }

        public List<ActorFactSnapshot> CaptureKingdomCitizens(string kingdomId)
        {
            List<ActorFactSnapshot> result = new List<ActorFactSnapshot>();
            try
            {
                Kingdom targetKingdom = Persistence.KingdomDataExt.FindKingdomById(kingdomId);

                // 名字兜底：仅当按 ID 找不到时才按名字解析
                if (targetKingdom == null && !string.IsNullOrEmpty(kingdomId) && World.world?.kingdoms?.list != null)
                {
                    string searchBase = TitleSuffixPatches.StripKingSuffix(kingdomId);
                    foreach (Kingdom k in World.world.kingdoms.list)
                    {
                        if (k == null || !k.isAlive()) continue;
                        string kName = k.name ?? "";
                        if (kName == kingdomId || TitleSuffixPatches.StripKingSuffix(kName) == searchBase)
                        {
                            targetKingdom = k;
                            break;
                        }
                    }
                }

                if (targetKingdom == null || targetKingdom.units == null) return result;

                foreach (Actor unit in targetKingdom.units)
                {
                    if (!IsValidCitizenUnit(unit)) continue;

                    string name = "国民";
                    try { name = TitleSuffixPatches.StripKingSuffix(unit.getName() ?? unit.name ?? "国民"); } catch { }

                    int age = 20;
                    try { age = unit.getAge(); } catch { }

                    result.Add(new ActorFactSnapshot
                    {
                        ActorId = unit.getID().ToString(),
                        Name = name,
                        IsAlive = true,
                        Age = age,
                        Level = GetActorLevel(unit),
                        Military = GetActorStat(unit, "warfare"),
                        Stewardship = GetActorStat(unit, "stewardship"),
                        Diplomacy = GetActorStat(unit, "diplomacy"),
                        Intelligence = GetActorStat(unit, "intelligence"),
                        Renown = GetActorStat(unit, "renown")
                    });
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError($"[WorldBoxReadOnlyAdapter] 抓取 {kingdomId} 本国国民事实发生异常", ex);
            }

            return result;
        }
    }
}
