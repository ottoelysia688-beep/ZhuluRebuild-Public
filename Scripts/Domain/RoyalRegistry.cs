using System;
using System.Collections.Generic;
using HarmonyLib;
using ZhuluRebuild.Core;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Domain
{
    /// <summary>
    /// 王室档案（地基 T0.2：KinshipTracker 扩展）。
    /// 记录每国王室三代内成员（君主/子女/孙/同父兄弟）+ 配偶 + 父母链。
    /// 供：宗藩判定数据源、世家总览（阶段3 UI）、联姻关系简版。
    /// 实现：每年在 EnforceRoyalClan 之后重建（数据量小，简单可靠）。
    /// 存储：内存 + 快照（王国ID → JSON）。
    /// </summary>
    public class RoyalRegistry : ISnapshotSection<RoyalRegistry.RoyalState>
    {
        public static RoyalRegistry Instance { get; } = new RoyalRegistry();

        public class RoyalMember
        {
            public long ActorId;
            public int Generation;      // 0=君主, 1=子女, 2=孙辈, 3=同父兄弟
            public long SpouseId;       // 0=无
            public List<long> ParentIds = new List<long>();
        }

        public class RoyalFamily
        {
            public string KingdomId;
            public long KingId;
            public List<RoyalMember> Members = new List<RoyalMember>();
            public int UpdatedYear;
        }

        private readonly Dictionary<string, RoyalFamily> _families = new Dictionary<string, RoyalFamily>();

        private RoyalRegistry() { }

        private bool Enabled => ZhuluConfig.Instance.GroundworkEnabled;

        // ═══════════════════════ 公开查询 ═══════════════════════

        /// <summary>取王室档案（无则 null）。</summary>
        public RoyalFamily GetFamily(string kingdomId)
        {
            _families.TryGetValue(kingdomId, out var f);
            return f;
        }

        /// <summary>某人是否在某国王室三代内（快查版，无档案时回退动态判定）。</summary>
        public bool IsRoyalMember(string kingdomId, long actorId)
        {
            var f = GetFamily(kingdomId);
            if (f == null) return false;
            foreach (var m in f.Members)
            {
                if (m.ActorId == actorId) return true;
            }
            return false;
        }

        /// <summary>某人辈分（-1=非王室成员）。</summary>
        public int GetGeneration(string kingdomId, long actorId)
        {
            var f = GetFamily(kingdomId);
            if (f == null) return -1;
            foreach (var m in f.Members)
            {
                if (m.ActorId == actorId) return m.Generation;
            }
            return -1;
        }

        // ═══════════════════════ 年度重建 ═══════════════════════

        /// <summary>重建某国王室档案（KinshipTracker.EnforceRoyalClan 之后调用）。</summary>
        public void OnKingdomYear(Kingdom k, int year)
        {
            if (!Enabled) return;
            if (k == null || !k.isAlive()) return;
            string kId = k.id.ToString();
            try
            {
                if (k.king == null || !k.king.isAlive())
                {
                    _families.Remove(kId); // 无君不建档
                    return;
                }

                long kingId = k.king.getID();
                var family = new RoyalFamily { KingdomId = kId, KingId = kingId, UpdatedYear = year };
                family.Members.Add(new RoyalMember { ActorId = kingId, Generation = 0, SpouseId = GetSpouseId(k.king) });

                // 子女（辈1）
                foreach (var child in KinshipTracker.GetAliveChildren(k, kingId.ToString()))
                {
                    if (child == null || child.getID() == kingId) continue;
                    var m = new RoyalMember
                    {
                        ActorId = child.getID(),
                        Generation = 1,
                        SpouseId = GetSpouseId(child),
                        ParentIds = GetParentIds(child)
                    };
                    family.Members.Add(m);

                    // 孙辈（辈2）
                    foreach (var g in KinshipTracker.GetAliveChildren(k, child.getID().ToString()))
                    {
                        if (g == null) continue;
                        var gm = new RoyalMember
                        {
                            ActorId = g.getID(),
                            Generation = 2,
                            SpouseId = GetSpouseId(g),
                            ParentIds = GetParentIds(g)
                        };
                        family.Members.Add(gm);
                    }
                }

                // 同父兄弟（辈3）
                foreach (var sibling in GetAliveSiblings(k, k.king))
                {
                    if (sibling == null) continue;
                    var sm = new RoyalMember
                    {
                        ActorId = sibling.getID(),
                        Generation = 3,
                        SpouseId = GetSpouseId(sibling),
                        ParentIds = GetParentIds(sibling)
                    };
                    // batch11#4: royal family hard cap 99 (registered size) — overflow not recorded
                    if (family.Members.Count < 99) family.Members.Add(sm);
                }

                _families[kId] = family;
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[RoyalRegistry] OnKingdomYear 异常: {ex.Message}");
            }
        }

        private static List<Actor> GetAliveSiblings(Kingdom k, Actor king)
        {
            var result = new List<Actor>();
            try
            {
                var kingParents = king.getParents();
                if (kingParents == null) return result;
                if (k.units == null) return result;
                foreach (var u in k.units)
                {
                    if (u == null || !u.isAlive() || u.getID() == king.getID()) continue;
                    var uParents = u.getParents();
                    if (uParents == null) continue;
                    foreach (var kp in kingParents)
                    {
                        if (kp == null) continue;
                        foreach (var up in uParents)
                        {
                            if (up != null && up.getID() == kp.getID())
                            {
                                result.Add(u);
                                break;
                            }
                        }
                        if (result.Count > 0 && result[result.Count - 1] == u) break;
                    }
                }
            }
            catch { }
            return result;
        }

        private static long GetSpouseId(Actor a)
        {
            try
            {
                var wife = Traverse.Create(a).Field("wife").GetValue<Actor>();
                if (wife != null) return wife.getID();
            }
            catch { }
            try
            {
                var husband = Traverse.Create(a).Field("husband").GetValue<Actor>();
                if (husband != null) return husband.getID();
            }
            catch { }
            return 0;
        }

        private static List<long> GetParentIds(Actor a)
        {
            var ids = new List<long>();
            try
            {
                var parents = a.getParents();
                if (parents != null)
                {
                    foreach (var p in parents)
                    {
                        if (p != null) ids.Add(p.getID());
                    }
                }
            }
            catch { }
            return ids;
        }

        // ═══════════════════════ 快照 ═══════════════════════

        // ---- batch10-A1: schema v5 section ----
        public sealed class RoyalState { public Dictionary<string, RoyalFamily> Families = new Dictionary<string, RoyalFamily>(); }
        public string SectionName => "royal_families";
        public Dictionary<string, RoyalState> CaptureStates()
        {
            return new Dictionary<string, RoyalState>
            {
                ["__global__"] = new RoyalState { Families = new Dictionary<string, RoyalFamily>(_families) }
            };
        }
        public void ApplyStates(Dictionary<string, RoyalState> data)
        {
            if (data == null || !data.TryGetValue("__global__", out var st) || st == null) return;
            _families.Clear();
            if (st.Families == null) return;
            foreach (var kv in st.Families) if (kv.Value != null) _families[kv.Key] = kv.Value;
        }



        public void MigrateFromCustomData() { }

        public void ResetForNewWorld() => _families.Clear();

        public void ResetRuntime() => _families.Clear();

        /// <summary>测试辅助。</summary>
        public void ClearForTest() => _families.Clear();

        public int FamilyCount => _families.Count;
    }
}
