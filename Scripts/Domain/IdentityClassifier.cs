using System;
using System.Collections.Generic;
using HarmonyLib;
using ZhuluRebuild.Core;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Domain
{
    /// <summary>
    /// 身份分类器（地基·用户定案）。
    /// 优先级：君主 > 宗藩 > 外戚 > 武将 > 文官 > 平民。
    /// 出身独立两轴：世家（有家族 Clan）/ 寒门（无家族）。
    /// 刷新：年度为主 + 关键事件即时补刷（入仕/封城主/换帝）。
    /// 存储：内存字典 + 快照（ActorId → "身份|出身|更新年"）。
    /// </summary>
    public class IdentityClassifier : ISnapshotSection<IdentityClassifier.IdentityCacheState>
    {
        public static IdentityClassifier Instance { get; } = new IdentityClassifier();

        public enum IdentityType
        {
            Monarch,    // 君主
            Imperial,   // 宗藩（王室三代内）
            ConsortKin, // 外戚（国王配偶及父母）
            Military,   // 武将（城主/队长）
            Civil,      // 文官（OfficeManager 官员）
            Commoner    // 平民
        }

        public class IdentityInfo
        {
            public IdentityType Type;
            public string Origin;      // 世家/寒门
            public int UpdatedYear;
            public string KingdomId;   // 归属国（平民可为空）
        }

        private readonly Dictionary<long, IdentityInfo> _cache = new Dictionary<long, IdentityInfo>();

        private IdentityClassifier() { }

        private bool Enabled => ZhuluConfig.Instance.GroundworkEnabled;

        // ═══════════════════════ 公开查询 ═══════════════════════

        /// <summary>取某人身份（未分类返回平民）。</summary>
        public IdentityType GetIdentity(long actorId)
        {
            return _cache.TryGetValue(actorId, out var info) ? info.Type : IdentityType.Commoner;
        }

        public IdentityInfo GetInfo(long actorId)
        {
            _cache.TryGetValue(actorId, out var info);
            return info;
        }

        public static string GetIdentityName(IdentityType type)
        {
            return type switch
            {
                IdentityType.Monarch => "君主",
                IdentityType.Imperial => "宗藩",
                IdentityType.ConsortKin => "外戚",
                IdentityType.Military => "武将",
                IdentityType.Civil => "文官",
                _ => "平民"
            };
        }

        /// <summary>身份图标 key（ZhuluAssetLoader 相对路径）。</summary>
        public static string GetIdentityIconKey(IdentityType type)
        {
            return type switch
            {
                IdentityType.Monarch => "identities/ID01_文官_简册毛笔", // 君主暂用文官图占位（后续补帝王图）
                IdentityType.Imperial => "identities/ID03_世家_牌坊匾",
                IdentityType.ConsortKin => "identities/ID03_世家_牌坊匾",
                IdentityType.Military => "identities/ID02_武将_头盔双剑",
                IdentityType.Civil => "identities/ID01_文官_简册毛笔",
                _ => "identities/ID04_寒门_茅屋柴堆"
            };
        }

        /// <summary>身份图标游戏资源 key（ui/zhulu/ 前缀，与 SpriteTextureLoader 注入 ID 一致）。</summary>
        public static string GetIdentityIconResourceKey(IdentityType type)
        {
            return "ui/zhulu/" + GetIdentityIconKey(type);
        }

        /// <summary>
        /// 注入身份图标到游戏贴图缓存（SpriteTextureLoader.addSprite，与特质图标同机制）。
        /// 注入后任何按 "ui/zhulu/identities/..." Resources.Load 的路径都能命中。
        /// </summary>
        public static void InjectIdentityIcons()
        {
            try
            {
                var icons = new Dictionary<IdentityType, string>
                {
                    { IdentityType.Monarch, GetIdentityIconKey(IdentityType.Monarch) },
                    { IdentityType.Imperial, GetIdentityIconKey(IdentityType.Imperial) },
                    { IdentityType.ConsortKin, GetIdentityIconKey(IdentityType.ConsortKin) },
                    { IdentityType.Military, GetIdentityIconKey(IdentityType.Military) },
                    { IdentityType.Civil, GetIdentityIconKey(IdentityType.Civil) },
                    { IdentityType.Commoner, GetIdentityIconKey(IdentityType.Commoner) }
                };
                var done = new HashSet<string>();
                foreach (var kv in icons)
                {
                    string rel = kv.Value;
                    if (!done.Add(rel)) continue; // 君主/宗藩/外戚共用图去重
                    try
                    {
                        string file = System.IO.Path.Combine(ZhuluContext.Instance.ModPath,
                            "GameResources", "ui", "zhulu",
                            rel.Replace('/', System.IO.Path.DirectorySeparatorChar) + ".png");
                        if (!System.IO.File.Exists(file))
                        {
                            ZhuluContext.Instance.LogWarning($"[Identity] 身份图标文件缺失: {file}");
                            continue;
                        }
                        // 2026-10-23 [F8]：全量注入已注册过同名 key → 去重跳过
                        if (ZhuluRebuild.Assets.ZhuluSpriteInjector.TryInject("ui/zhulu/" + rel, file))
                            ZhuluContext.Instance.LogInfo($"[Identity] 注入身份图标: ui/zhulu/{rel}");
                    }
                    catch (Exception ex)
                    {
                        ZhuluContext.Instance.LogWarning($"[Identity] 注入身份图标异常: {kv.Value}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[Identity] 身份图标注入整体异常", ex);
            }
        }

        public static string GetOriginName(string origin) => origin == "世家" ? "世家" : "寒门";

        // ═══════════════════════ 分类核心 ═══════════════════════

        /// <summary>按国年度刷新：遍历该国存活单位更新身份缓存。</summary>
        public void OnKingdomYear(Kingdom k, int year)
        {
            if (!Enabled) return;
            if (k == null || !k.isAlive()) return;
            try
            {
                if (k.units == null) return;
                for (int i = 0; i < k.units.Count; i++)
                {
                    var u = k.units[i];
                    if (u == null || !u.isAlive()) continue;
                    RefreshActor(k, u, year);
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[Identity] OnKingdomYear 异常: {ex.Message}");
            }
        }

        /// <summary>刷新某人身份（关键事件即时调用：入仕/封城主/换帝）。</summary>
        public void RefreshActor(Kingdom k, Actor a, int year)
        {
            try
            {
                long aid = a.getID();
                if (aid <= 0) return;

                var info = new IdentityInfo
                {
                    Type = Classify(k, a, year),
                    Origin = GetOrigin(a),
                    UpdatedYear = year,
                    KingdomId = k?.id.ToString() ?? ""
                };
                _cache[aid] = info;
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[Identity] RefreshActor 异常: {ex.Message}");
            }
        }

        /// <summary>身份判定（优先级：君主>宗藩>外戚>武将>文官>平民）。</summary>
        private IdentityType Classify(Kingdom k, Actor a, int year)
        {
            // 1. 君主
            if (k != null && k.king == a) return IdentityType.Monarch;

            // 2. 宗藩：同 Clan 且三代内（国王 + 子女 + 孙 + 同父兄弟）
            if (k != null && IsImperialKin(k, a)) return IdentityType.Imperial;

            // 3. 外戚：国王配偶及其父母
            if (k != null && IsConsortKin(k, a)) return IdentityType.ConsortKin;

            // 4. 武将：城主
            if (a.isCityLeader()) return IdentityType.Military;

            // 5. 文官：OfficeManager 官员
            if (k != null && IsCivilOfficial(k, a)) return IdentityType.Civil;

            // 6. 平民
            return IdentityType.Commoner;
        }

        /// <summary>宗藩判定：与国王同 Clan，三代内（国王/子女/孙/同父兄弟）。</summary>
        private static bool IsImperialKin(Kingdom k, Actor a)
        {
            try
            {
                if (k.king == null || !k.king.isAlive()) return false;
                if (a.clan == null || k.king.clan == null) return false;
                if (a.clan != k.king.clan) return false;
                if (a == k.king) return false; // 君主已单独处理

                // 三代内：国王的子女 / 孙 / 同父兄弟
                if (KinshipTracker.IsParent(k.king.getID().ToString(), a.getID().ToString())) return true; // 子女
                if (IsGrandchildOf(k.king, a)) return true;                                              // 孙
                if (IsSibling(k.king, a)) return true;                                                   // 同父兄弟

                return false;
            }
            catch { return false; }
        }

        private static bool IsGrandchildOf(Actor grandparent, Actor a)
        {
            if (grandparent == null || a == null) return false;
            // a 的父/母是 grandparent 的子女
            var parents = a.getParents();
            if (parents == null) return false;
            foreach (var p in parents)
            {
                if (p == null) continue;
                if (KinshipTracker.IsParent(grandparent.getID().ToString(), p.getID().ToString())) return true;
            }
            return false;
        }

        private static bool IsSibling(Actor king, Actor a)
        {
            if (king == null || a == null) return false;
            var kingParents = king.getParents();
            var aParents = a.getParents();
            if (kingParents == null || aParents == null) return false;
            foreach (var kp in kingParents)
            {
                if (kp == null) continue;
                foreach (var ap in aParents)
                {
                    if (ap == null) continue;
                    if (kp.getID() == ap.getID()) return true;
                }
            }
            return false;
        }

        /// <summary>外戚判定：国王配偶 + 其父母。</summary>
        private static bool IsConsortKin(Kingdom k, Actor a)
        {
            try
            {
                if (k.king == null || !k.king.isAlive()) return false;
                var consort = GetSpouse(k.king);
                if (consort == null) return false;
                if (consort.getID() == a.getID()) return true;

                // a 是配偶的父母（外戚直系）
                if (KinshipTracker.IsParent(consort.getID().ToString(), a.getID().ToString())) return true;
                var consortParents = consort.getParents();
                if (consortParents != null)
                {
                    foreach (var p in consortParents)
                    {
                        if (p != null && p.getID() == a.getID()) return true;
                    }
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>读取国王配偶（原版字段 wife/husband，反射安全读取）。</summary>
        private static Actor GetSpouse(Actor king)
        {
            try
            {
                var wife = Traverse.Create(king).Field("wife").GetValue<Actor>();
                if (wife != null) return wife;
            }
            catch { }
            try
            {
                var husband = Traverse.Create(king).Field("husband").GetValue<Actor>();
                if (husband != null) return husband;
            }
            catch { }
            return null;
        }

        /// <summary>文官判定：OfficeManager 任免记录中有此人。</summary>
        private static bool IsCivilOfficial(Kingdom k, Actor a)
        {
            try
            {
                var records = OfficeManager.Instance.GetRecordsPublic(k.id.ToString());
                if (records == null) return false;
                long aid = a.getID();
                foreach (var r in records)
                {
                    if (r.PersonId == aid.ToString()) return true;
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>出身判定：世家=有家族（Clan），寒门=无家族。</summary>
        public static string GetOrigin(Actor a)
        {
            try
            {
                return (a != null && a.clan != null) ? "世家" : "寒门";
            }
            catch { return "寒门"; }
        }

        // ═══════════════════════ 快照 ═══════════════════════

        // ---- batch10-A1: schema v5 section ----
        public sealed class IdentityCacheState { public Dictionary<long, IdentityInfo> Cache = new Dictionary<long, IdentityInfo>(); }
        public string SectionName => "identities";
        public Dictionary<string, IdentityCacheState> CaptureStates()
        {
            return new Dictionary<string, IdentityCacheState>
            {
                ["__global__"] = new IdentityCacheState { Cache = new Dictionary<long, IdentityInfo>(_cache) }
            };
        }
        public void ApplyStates(Dictionary<string, IdentityCacheState> data)
        {
            if (data == null || !data.TryGetValue("__global__", out var st) || st == null) return;
            _cache.Clear();
            if (st.Cache == null) return;
            foreach (var kv in st.Cache) if (kv.Value != null) _cache[kv.Key] = kv.Value;
        }



        public void MigrateFromCustomData() { }

        public void ResetForNewWorld()
        {
            _cache.Clear();
        }

        public void ResetRuntime()
        {
            _cache.Clear();
        }

        /// <summary>测试辅助：清空。</summary>
        public void ClearForTest() => _cache.Clear();

        public int CacheCount => _cache.Count;
    }
}
