using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using ZhuluRebuild.Adapters;
using ZhuluRebuild.Core;
using ZhuluRebuild.Domain;
using ZhuluRebuild.Persistence;

// ── 由 ZhuluChineseNameService.cs 拆出的 partial 部分：家族归族规则 ──
// 2026-10-23 [P1-1] 纯文件级拆分，编译后仍为同一个类型，无任何行为改动。
namespace ZhuluRebuild.Naming
{
    public sealed partial class ZhuluChineseNameService
    {
        /// 普通家庭按父系优先、母系兜底。Clan 与原版 Family 两层都同步。
        /// </summary>
        public void EnforceChildFamily(Actor child)
        {
            EnsureLoaded();
            if (child == null || !child.isAlive()) return;
            List<Actor> parents = new List<Actor>();
            try
            {
                var source = child.getParents();
                if (source != null) foreach (Actor p in source) if (p != null) parents.Add(p);
            }
            catch { }
            if (parents.Count == 0) return;

            var royalClanIds = GetRoyalClanIds();
            Actor royalMother = null, royalParent = null, father = null, first = null;
            Actor fatherWithFamily = null, motherWithFamily = null;
            foreach (Actor parent in parents)
            {
                if (first == null) first = parent;
                bool male = false;
                try { male = ActorAccess.GetSex(parent) == ActorSex.Male; } catch { }
                if (male && father == null) father = parent;
                bool hasFamily = false;
                try { hasFamily = parent.clan != null || FamilyAccess.GetFamily(parent) != null; } catch { }
                if (hasFamily)
                {
                    if (male && fatherWithFamily == null) fatherWithFamily = parent;
                    if (!male && motherWithFamily == null) motherWithFamily = parent;
                }
                long clanId = -1;
                try { if (parent.clan != null) clanId = parent.clan.getID(); } catch { }
                if (clanId > 0 && royalClanIds.Contains(clanId))
                {
                    if (!male && royalMother == null) royalMother = parent;
                    if (royalParent == null) royalParent = parent;
                }
            }

            Actor sourceParent = royalMother ?? royalParent ?? fatherWithFamily ?? motherWithFamily ?? father ?? first;
            if (sourceParent == null) return;

            // 氏族：王室亲生子女无论男女、无论父方身份，一律归王室氏族。
            try
            {
                Clan targetClan = sourceParent.clan;
                // 普通家庭按“父系优先、母系兜底”，王室母亲优先级更高；父母关系完整后，
                // 子女必须进入最终选中的家族，不能因为原版先临时分到另一氏族就跳过。
                if (targetClan != null && child.clan != targetClan) child.setClan(targetClan);
                if (targetClan != null) ApplySurname(child, GetClanSurname(targetClan));
                else
                {
                    string parentSurname = GetPreferredSurname(sourceParent);
                    if (string.IsNullOrEmpty(parentSurname)) parentSurname = ExtractSurname(ActorAccess.GetRawName(sourceParent));
                    ApplySurname(child, parentSurname);
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[ChineseName] 子女归氏族失败: {ex.Message}");
            }

            // 原版 Family：跟随同一个优先父母。反射失败时不阻断游戏出生。
            try
            {
                    object targetFamily = FamilyAccess.GetFamily(sourceParent);
                    if (targetFamily != null)
                    {
                        object currentFamily = FamilyAccess.GetFamily(child);
                        long currentId = FamilyAccess.GetObjectId(currentFamily);
                        long targetId = FamilyAccess.GetObjectId(targetFamily);
                        if (currentFamily == null || (!ReferenceEquals(currentFamily, targetFamily)
                            && (currentId <= 0 || targetId <= 0 || currentId != targetId)))
                            FamilyAccess.SetFamily(child, targetFamily);
                        WritePersistentValue(FamilyAccess.GetData(targetFamily), FamilySurnameKey, GetPreferredSurname(sourceParent));
                    }
            }
            catch { }
        }

        /// <summary>年度旧档修复：补齐王室母系归族、无家庭子女归家，并同步国内全部氏族姓。</summary>
        public void EnforceFamilyRulesForKingdom(Kingdom kingdom)
        {
            if (kingdom?.units == null) return;
            var clans = new HashSet<Clan>();
            foreach (Actor actor in kingdom.units)
            {
                try
                {
                    if (actor == null || !actor.isAlive()) continue;
                    EnforceChildFamily(actor);
                    if (actor.clan != null) clans.Add(actor.clan);
                }
                catch { }
            }
            foreach (Clan clan in clans) SynchronizeClanSurname(clan);
        }

        private static HashSet<long> GetRoyalClanIds()
        {
            var ids = new HashSet<long>();
            try
            {
                if (World.world?.kingdoms?.list == null) return ids;
                foreach (Kingdom kingdom in World.world.kingdoms.list)
                {
                    if (kingdom == null || !kingdom.isAlive()) continue;
                    long id = kingdom.data?.royal_clan_id ?? -1;
                    if (id > 0) ids.Add(id);
                    try { if (kingdom.king?.clan != null) ids.Add(kingdom.king.clan.getID()); } catch { }
                }
            }
            catch { }
            return ids;
        }

    }
}
