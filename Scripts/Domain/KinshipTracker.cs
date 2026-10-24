using System;
using System.Collections.Generic;
using HarmonyLib;
using ZhuluRebuild.Core;

namespace ZhuluRebuild.Domain
{
    /// <summary>
    /// 亲子关系追踪器。
    /// 通过游戏公开的 getParents() 接口读取亲子关系；不拦截任何原版函数、不持久化。
    /// P1-8（2026-08-23）：增加同国同年的脏标记缓存——失效信号来自 setParent1/2 后置补丁
    /// （出生/过继必经），死亡靠返回时存活过滤惰性剔除。缓存只加速查询，权威数据始终在原版。
    /// 王室子女归族坚持调用原版 child.setClan()，绝不反射写内部字段
    /// （2026-08-15 手写 Traverse 曾致氏族 100+ 重复人——见 EnforceRoyalClan 内注释）。
    /// </summary>
    public static class KinshipTracker
    {
        // ═══════════════════════ P1-8 脏标记缓存（2026-08-23） ═══════════════════════
        // 动机：GetAliveChildren 每次全王国扫描 + 逐单位 getParents()；GetAliveGrandchildren 是
        // O(子女数×全国人口) 的嵌套扫描。战乱年代频繁换君 → SuccessionManager 反复重算同一批亲子。
        // 缓存策略（保持只读哲学——不拦截任何原版函数）：
        //   • 失效信号 = setParent1/setParent2 后置补丁调用 MarkDirty()（出生/过继必经此口）；
        //   • 同一王国 + 同一年份内复用；跨年/换王国/置脏即重建；
        //   • 死亡不主动失效——返回时逐个过滤存活并惰性剔除死条目（O(子女数) 级，可忽略）。
        private static bool _mapDirty = true;
        private static int _mapYear = -1;
        private static long _mapKingdomId = -1;
        private static readonly Dictionary<string, List<Actor>> _childrenMap = new Dictionary<string, List<Actor>>();

        /// <summary>亲子关系写入后置脏（FamilyInheritancePatches 的 setParent1/2 Postfix 调用）。</summary>
        public static void MarkDirty()
        {
            _mapDirty = true;
        }

        /// <summary>缓存是否可用（同国同年且未置脏）。</summary>
        private static bool IsCacheFresh(Kingdom k)
        {
            return !_mapDirty
                && _mapYear == ZhuluTime.CurrentYear
                && k != null && k.units != null
                && _mapKingdomId == k.id;
        }

        /// <summary>重建指定王国的亲子映射（一次全量扫描）。</summary>
        private static void RebuildChildrenMap(Kingdom k)
        {
            _childrenMap.Clear();
            foreach (Actor unit in k.units)
            {
                if (unit == null || !unit.isAlive()) continue;
                var parents = SafeGetParents(unit);
                if (parents == null) continue;
                foreach (Actor parent in parents)
                {
                    if (parent == null) continue;
                    string pid = parent.getID().ToString();
                    if (!_childrenMap.TryGetValue(pid, out var list))
                    {
                        list = new List<Actor>();
                        _childrenMap[pid] = list;
                    }
                    list.Add(unit);
                }
            }
            _mapKingdomId = k.id;
            _mapYear = ZhuluTime.CurrentYear;
            _mapDirty = false;
        }

        /// <summary>从缓存取存活子女列表；死条目惰性剔除。</summary>
        private static List<Actor> CollectAliveFromCache(string actorId, List<Actor> result)
        {
            if (_childrenMap.TryGetValue(actorId, out var cached))
            {
                for (int i = cached.Count - 1; i >= 0; i--)
                {
                    Actor c = cached[i];
                    bool alive = false;
                    try { alive = c != null && c.isAlive(); } catch { alive = false; }
                    if (!alive) { cached.RemoveAt(i); continue; } // 死条目惰性剔除
                    result.Add(c);
                }
            }
            return result;
        }

        /// <summary>安全读 getParents()（原公开接口，异常返回 null）。</summary>
        private static System.Collections.Generic.IEnumerable<Actor> SafeGetParents(Actor unit)
        {
            try { return unit.getParents(); }
            catch { return null; }
        }

        // ═══════════════════════ 公开查询接口 ═══════════════════════

        /// <summary>获取某人在指定王国内的存活子女（P1-8：同国同年内走脏标记缓存，否则全量重建）。</summary>
        public static List<Actor> GetAliveChildren(Kingdom k, string actorId)
        {
            var result = new List<Actor>();
            if (k == null || string.IsNullOrEmpty(actorId)) return result;

            try
            {
                if (k.units == null) return result;

                if (!IsCacheFresh(k))
                {
                    RebuildChildrenMap(k);
                }
                return CollectAliveFromCache(actorId, result);
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[KinshipTracker] GetAliveChildren 异常: {ex.Message}");
            }

            return result;
        }

        /// <summary>获取某人在指定王国内的存活孙子。</summary>
        public static List<Actor> GetAliveGrandchildren(Kingdom k, string actorId)
        {
            var result = new List<Actor>();
            if (k == null || string.IsNullOrEmpty(actorId)) return result;

            var children = GetAliveChildren(k, actorId);
            foreach (var child in children)
            {
                if (child == null) continue;
                string childId = child.getID().ToString();
                var grandChildren = GetAliveChildren(k, childId);
                result.AddRange(grandChildren);
            }

            return result;
        }

        /// <summary>
        /// 全球反查指定角色的存活子女（阶段2A A1-1：子嗣移居别国时本国查询会漏，
        /// 用于 SelectHeir 的子嗣路径兜底；O(全图)，仅在继承选定罕见路径调用）。
        /// </summary>
        public static List<Actor> GetAllChildrenWorldwide(string actorId)
        {
            var result = new List<Actor>();
            if (string.IsNullOrEmpty(actorId)) return result;
            try
            {
                var allUnits = World.world?.units?.getSimpleList();
                if (allUnits == null) return result;
                foreach (var u in allUnits)
                {
                    if (u == null || !u.isAlive()) continue;
                    if (HasParent(u, actorId)) result.Add(u);
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[KinshipTracker] GetAllChildrenWorldwide 异常: {ex.Message}");
            }
            return result;
        }

        /// <summary>判断 parentId 是否是 childId 的父/母亲。</summary>
        public static bool IsParent(string parentId, string childId)
        {
            if (string.IsNullOrEmpty(parentId) || string.IsNullOrEmpty(childId)) return false;

            try
            {
                long cid;
                if (!long.TryParse(childId, out cid)) return false;
                Actor child = World.world.units.get(cid);
                if (child == null || !child.isAlive()) return false;
                return HasParent(child, parentId);
            }
            catch { return false; }
        }

        // ═══════════════════════ 王室子女归族 ═══════════════════════

        /// <summary>
        /// 每年检查：国王的亲生子是否在国王氏族里，不在就移入。
        /// 解决女国王子女跟父姓/不入母族的问题。
        /// 移入后，现有 TitleSuffixPatches 的 clan 姓氏统一逻辑会自动生效。
        /// </summary>
        public static void EnforceRoyalClan(Kingdom k)
        {
            try
            {
                if (k == null || !k.isAlive()) return;
                if (k.king == null || !k.king.isAlive()) return;

                Clan kingClan = k.king.clan;
                if (kingClan == null) return;

                string kingId = k.king.getID().ToString();
                var children = GetAliveChildren(k, kingId);

                foreach (var child in children)
                {
                    if (child == null || !child.isAlive()) continue;
                    if (child.clan == kingClan) continue; // 已在国王氏族，无需操作

                    // ⚠️ 2026-08-15 血泪教训复现（2A 排查）：手写版 units.Add + Traverse 写 data.clan
                    // 被 8b4b89c 族谱重写时回退，导致内存 actor.clan 引用不同步：
                    //   Bug1：归族守卫 child.clan==kingClan 失效 → 孩子看似"未归族"（保持旧族姓）
                    //         继位时 setKing→trySetRoyalClan → 王室家族被新王旧族替换
                    //   Bug2：同一孩子每年重复 Add → 氏族 100+ 重复人（用户实锤）
                    // 必须用原版 child.setClan(kingClan)（27deda9 验证）：同步内存引用 + 标 dirty，
                    // 原版 ClanManager 脏检查自动 clear+refill 保持 units 干净。
                    try
                    {
                        child.setClan(kingClan);
                        // 阶段2A A1-5：女皇子女入赘姓——归族同步强制王室姓（王夫入赘语义，
                        // 子女从君主姓，王室血脉名义延续；避免女皇儿子随父姓导致下一代"改朝换代"）
                        try
                        {
                            Naming.ZhuluChineseNameService.Instance.AdoptSurname(child, k.king);
                        }
                        catch { }
                        ZhuluContext.Instance.LogInfo(
                            $"[KinshipTracker] 王室子女归族(setClan): {child.getName()} → 国王 {k.king.getName()} 的氏族");
                    }
                    catch (Exception ex)
                    {
                        ZhuluContext.Instance.LogWarning($"[KinshipTracker] 归族异常: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[KinshipTracker] EnforceRoyalClan 异常: {ex.Message}");
            }
        }

        // ═══════════════════════ 内部辅助 ═══════════════════════

        /// <summary>通过游戏公开的 getParents() 接口判断某角色的父/母亲是否为目标。</summary>
        private static bool HasParent(Actor unit, string targetParentId)
        {
            try
            {
                var parents = unit.getParents();
                if (parents == null) return false;
                foreach (Actor parent in parents)
                {
                    if (parent != null && parent.getID().ToString() == targetParentId)
                        return true;
                }
            }
            catch { }
            return false;
        }
    }
}
