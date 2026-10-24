using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using ZhuluRebuild.Adapters;
using ZhuluRebuild.Core;
using ZhuluRebuild.Domain;
using ZhuluRebuild.Persistence;

// ── 由 ZhuluChineseNameService.cs 拆出的 partial 部分：名字生成器（人物/城池/王号） ──
// 2026-10-23 [P1-1] 纯文件级拆分，编译后仍为同一个类型，无任何行为改动。
namespace ZhuluRebuild.Naming
{
    public sealed partial class ZhuluChineseNameService
    {
        private void CleanupDeadEntries()
        {
            try
            {
                var deadActors = new List<Actor>();
                foreach (var pair in _actorNames)
                {
                    try { if (pair.Key == null || !pair.Key.isAlive()) deadActors.Add(pair.Key); }
                    catch { deadActors.Add(pair.Key); }
                }
                // P1-10（2026-08-23）：亡者名字回收候选——只防新增重名的去重集合此前只增不减，
                // 千年长局理论可耗尽人名空间。回收条件：该名字不再被任何"存活"被跟踪者持有
                // （防同名存活者在世时名字被新生儿复用造成活人重名）。
                var recycleCandidates = new List<string>(deadActors.Count);
                foreach (var a in deadActors)
                {
                    string n;
                    if (_actorNames.TryGetValue(a, out n) && !string.IsNullOrEmpty(n))
                        recycleCandidates.Add(n);
                    _actorNames.Remove(a);
                }

                var deadKingdoms = new List<Kingdom>();
                foreach (var pair in _kingdomBaseNames)
                {
                    try { if (pair.Key == null || !pair.Key.isAlive()) deadKingdoms.Add(pair.Key); }
                    catch { deadKingdoms.Add(pair.Key); }
                }
                foreach (var k in deadKingdoms) _kingdomBaseNames.Remove(k);

                var deadWars = new List<War>();
                foreach (var pair in _warNames)
                {
                    try { if (pair.Key == null || pair.Key.hasEnded()) deadWars.Add(pair.Key); }
                    catch { deadWars.Add(pair.Key); }
                }
                foreach (var w in deadWars) _warNames.Remove(w);

                // P1-7c 自愈扩面：全图氏族扫描——旧版生成器产出的"氏氏/部部"坏名可能存在于
                // 任意 Clan（含未跟踪者）；仅扫跟踪表会漏。每 10 tick 一轮，O(全国单位) 可忽略。
                int healedGlobal = 0;
                try
                {
                    var seenClans = new HashSet<long>();
                    if (World.world?.kingdoms?.list != null)
                    {
                        foreach (var kd in World.world.kingdoms.list)
                        {
                            if (kd == null || kd.units == null) continue;
                            foreach (var u in kd.units)
                            {
                                try
                                {
                                    var cl2 = u?.clan;
                                    if (cl2 == null || cl2.data == null || !cl2.isAlive()) continue;
                                    long cid = cl2.getID();
                                    if (!seenClans.Add(cid)) continue;
                                    string nm = cl2.data.name ?? "";
                                    if (nm.Contains("氏氏") || nm.Contains("部部"))
                                    {
                                        string fixedNm = nm.Replace("氏氏", "氏").Replace("部部", "部");
                                        cl2.data.name = fixedNm;
                                        WritePersistentName(cl2.data, fixedNm);
                                        if (_clanNames.ContainsKey(cl2)) _clanNames[cl2] = fixedNm;
                                        if (++healedGlobal <= 3)
                                            ZhuluContext.Instance.LogInfo($"[ChineseName] 双后缀自愈(全图): {nm} → {fixedNm}");
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    if (healedGlobal > 3)
                        ZhuluContext.Instance.LogInfo($"[ChineseName] 双后缀自愈(全图): 本轮共 {healedGlobal} 个");
                }
                catch { }

                // P1-10：执行名字回收（在 _actorNames 清理之后——存活持有检查才准确）
                if (recycleCandidates.Count > 0)
                {
                    foreach (string n in recycleCandidates)
                    {
                        bool stillHeld = false;
                        foreach (var kv in _actorNames)
                        {
                            if (kv.Value == n) { stillHeld = true; break; }
                        }
                        if (!stillHeld) _usedPersonNames.Remove(n);
                    }
                }
            }
            catch { }
        }

        // ───────────────────────────── 名字生成 ─────────────────────────────

        private string GenerateActorName(Actor actor)
        {
            string surname = GetOrCreateSurname(actor);
            if (IsNomadicActor(actor))
            {
                // 阶段3C：游牧人名制 = 氏族姓·本名（耶律·大石 / 孛儿只斤·旭烈兀），男女分池
                bool nm = true;
                try { nm = ActorAccess.GetSex(actor) == ActorSex.Male; } catch { }
                return surname + "·" + Pick(nm ? NomadicMaleGiven : NomadicFemaleGiven);
            }
            bool male = true;
            try { male = ActorAccess.GetSex(actor) == ActorSex.Male; }
            catch { }

            string[] firstPool = male ? _config.maleNameFirst : _config.femaleNameFirst;
            string[] secondPool = male ? _config.maleNameSecond : _config.femaleNameSecond;
            if (Empty(firstPool)) firstPool = male ? _config.maleGiven : _config.femaleGiven;
            if (Empty(secondPool)) secondPool = male ? _config.maleGiven : _config.femaleGiven;

            // 约 30% 单字名；其余双字名。双字名按“气质/辈分字 + 个人含义字”组合，
            // 少量首字取中性池，避免所有名字都长成同一种模板。
            if (NextDouble() < 0.30d)
                return surname + Pick(secondPool);

            string first = GetGenerationAwareFirst(actor, firstPool);
            if (NextDouble() < 0.14d) first = Pick(_config.neutralGiven);
            string second = PickDifferent(secondPool, first);
            for (int i = 0; i < 8 && IsAwkwardGivenPair(first, second); i++)
            {
                first = GetGenerationAwareFirst(actor, firstPool);
                second = PickDifferent(secondPool, first);
            }
            return surname + first + second;
        }

        /// <summary>生成不重复全名；同名用更换名而不是更换已继承的姓氏解决。</summary>
        private string GenerateUniqueActorName(Actor actor)
        {
            for (int i = 0; i < 32; i++)
            {
                string candidate = GenerateActorName(actor);
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                if (_usedPersonNames.Contains(candidate)) continue;
                _usedPersonNames.Add(candidate);
                return candidate;
            }
            // 阶段3C：游牧兜底（本名池组合，保持·格式）
            string fb = GetOrCreateSurname(actor);
            if (IsNomadicActor(actor))
            {
                bool nm = true;
                try { nm = ActorAccess.GetSex(actor) == ActorSex.Male; } catch { }
                var pool = nm ? NomadicMaleGiven : NomadicFemaleGiven;
                for (int i = 0; i < pool.Length; i++)
                {
                    string cand = fb + "·" + pool[(i + StableIndex(actor.getID() + "|" + i, pool.Length)) % pool.Length];
                    if (_usedPersonNames.Add(cand)) return cand;
                }
                return fb + "·" + Pick(pool);
            }
            // 极端长局兜底：系统遍历组合，仍保持纯中文且不附加数字。
            string fallbackSurname = GetOrCreateSurname(actor);
            bool male = true;
            try { male = ActorAccess.GetSex(actor) == ActorSex.Male; } catch { }
            string[] secondPool = male ? _config.maleNameSecond : _config.femaleNameSecond;
            if (Empty(secondPool)) secondPool = male ? _config.maleGiven : _config.femaleGiven;
            foreach (string first in _config.neutralGiven)
                foreach (string second in secondPool)
                {
                    if (IsAwkwardGivenPair(first, second)) continue;
                    string forced = fallbackSurname + first + second;
                    if (_usedPersonNames.Add(forced)) return forced;
                }
            string last = fallbackSurname + Pick(_config.neutralGiven) + Pick(secondPool);
            _usedPersonNames.Add(last);
            return last;
        }

        private string GetGenerationAwareFirst(Actor actor, string[] pool)
        {
            if (Empty(pool)) return "安";
            try
            {
                if (actor?.clan != null && NextDouble() < 0.24d)
                {
                    int birthYear = ZhuluTime.CurrentYear - actor.getAge();
                    int generationBand = (int)Math.Floor(birthYear / 24d);
                    int index = StableIndex(actor.clan.id + "|" + generationBand, pool.Length);
                    return pool[index] ?? "安";
                }
            }
            catch { }
            return Pick(pool);
        }

        private static bool IsAwkwardGivenPair(string first, string second)
        {
            if (string.IsNullOrEmpty(first) || string.IsNullOrEmpty(second)) return true;
            if (first == second) return true;
            char a = first[first.Length - 1];
            char b = second[0];
            return a == b;
        }

        /// <summary>全图人名种子（懒加载，每世界一次；读档/新世界后由 ZhuluSavePatch.ResetPersonNameSeed 重置）。</summary>
        private void SeedUsedPersonNamesOnce()
        {
            if (_personNamesSeeded) return;
            _personNamesSeeded = true;
            try
            {
                if (World.world?.units == null) return;
                var units = World.world.units.getSimpleList();
                int count = 0;
                for (int i = 0; i < units.Count; i++)
                {
                    var u = units[i];
                    if (u == null || !u.isAlive()) continue;
                    string n = u.getName();
                    if (!string.IsNullOrWhiteSpace(n)) { _usedPersonNames.Add(n); count++; }
                }
                ZhuluContext.Instance.LogInfo($"[ChineseName] 人名去重种子就绪：全图 {count} 名");
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[ChineseName] 人名种子扫描异常: {ex.Message}");
            }
        }

        /// <summary>读档/新世界后重置人名种子（由 ZhuluSavePatch 调用——防止跨世界名集合残留）。</summary>
        public void ResetPersonNameSeed()
        {
            _usedPersonNames.Clear();
            _personNamesSeeded = false;
        }

        private string GetOrCreateSurname(Actor actor)
        {
            if (actor == null) return PickSurname(null);
            // 已入氏族者必须以氏族正式名的姓为最高权威，修复“晋戚氏十人只有三人姓戚”。
            string surname = "";
            try { if (actor.clan != null) surname = GetClanSurname(actor.clan); } catch { }
            if (string.IsNullOrEmpty(surname)) surname = ActorAccess.GetString(actor, ActorSurnameKey);
            if (string.IsNullOrEmpty(surname)) surname = ActorAccess.GetString(actor, SharedFamilyNameKey);

            if (string.IsNullOrEmpty(surname))
            {
                try
                {
                    var parents = new List<Actor>();
                    foreach (Actor parent in actor.getParents()) if (parent != null) parents.Add(parent);
                    var royalIds = GetRoyalClanIds();
                    // 王室母亲优先，确保女皇/王室女性子女不被父方姓覆盖。
                    parents.Sort((a, b) => ParentSurnamePriority(a, royalIds).CompareTo(ParentSurnamePriority(b, royalIds)));
                    foreach (Actor parent in parents)
                    {
                        string inherited = "";
                        try { if (parent.clan != null) inherited = GetClanSurname(parent.clan); } catch { }
                        if (string.IsNullOrEmpty(inherited)) inherited = ActorAccess.GetString(parent, ActorSurnameKey);
                        if (string.IsNullOrEmpty(inherited)) inherited = ActorAccess.GetString(parent, SharedFamilyNameKey);
                        if (string.IsNullOrEmpty(inherited)) inherited = ExtractSurname(ActorAccess.GetRawName(parent));
                        if (!string.IsNullOrEmpty(inherited))
                        {
                            surname = inherited;
                            break;
                        }
                    }
                }
                catch { }
            }
            if (string.IsNullOrEmpty(surname)) surname = PickSurname(actor);

            ActorAccess.SetString(actor, ActorSurnameKey, surname);
            ActorAccess.SetString(actor, SharedFamilyNameKey, surname);
            return surname;
        }

        private static int ParentSurnamePriority(Actor parent, HashSet<long> royalClanIds)
        {
            if (parent == null) return 99;
            bool male = true;
            try { male = ActorAccess.GetSex(parent) == ActorSex.Male; } catch { }
            bool royal = false;
            try { royal = parent.clan != null && royalClanIds.Contains(parent.clan.getID()); } catch { }
            if (royal && !male) return 0;
            if (royal) return 1;
            return male ? 2 : 3;
        }

        private string PickSurname(Actor actor)
        {
            if (IsNomadicActor(actor)) return Pick(NomadicSurnames);
            if (NextDouble() < 0.08d && _config.compoundSurnames.Length > 0)
                return Pick(_config.compoundSurnames);
            return Pick(_config.surnames);
        }

        private string GenerateUniqueCityName()
        {
            if (!Empty(_config.ancientCityNames))
            {
                for (int i = 0; i < Math.Max(120, _config.ancientCityNames.Length * 2); i++)
                {
                    string candidate = Pick(_config.ancientCityNames);
                    if (string.IsNullOrWhiteSpace(candidate) || IsCityNameUsed(candidate)) continue;
                    string aliasKey = GetCityAliasKey(candidate);
                    if (!string.IsNullOrEmpty(aliasKey) && _sessionCityAliasKeys.Contains(aliasKey)) continue;
                    _sessionCityNames.Add(candidate);
                    if (!string.IsNullOrEmpty(aliasKey)) _sessionCityAliasKeys.Add(aliasKey);
                    return candidate;
                }
            }
            // 历史名池耗尽才启用受控组合回退。
            return UniqueName(GenerateCityName, IsCityNameUsed, _sessionCityNames, "安邑");
        }

        private string GenerateCityName()
        {
            double roll = NextDouble();
            if (roll < 0.55d) return Pick(_config.cityPrefixes) + Pick(_config.cityStems);
            if (roll < 0.88d) return Pick(_config.cityStems) + Pick(_config.citySuffixes);
            return Pick(_config.cityPrefixes) + Pick(_config.cityStems) + Pick(_config.citySuffixes);
        }

        private string GenerateUniqueRealmBaseName(Kingdom exceptKingdom)
        {
            for (int i = 0; i < 160; i++)
            {
                string candidate = Pick(_config.realmNames);
                if (!string.IsNullOrWhiteSpace(candidate) && !IsRealmNameUsed(candidate, exceptKingdom))
                {
                    _sessionRealmNames.Add(candidate);
                    return candidate;
                }
            }
            // 同一国号发生碰撞时，优先采用史书中最常见的方位区分；
            // 只有方位组合也耗尽后，才使用前/后等时序或修饰字。
            string[] directionModifiers = { "东", "西", "南", "北" };
            string[] fallbackModifiers = { "前", "后", "上", "下", "中", "大", "新", "兴", "靖", "定", "永", "盛" };
            string directional = GenerateModifiedRealmName(directionModifiers, exceptKingdom);
            if (!string.IsNullOrEmpty(directional)) return directional;
            string fallbackModified = GenerateModifiedRealmName(fallbackModifiers, exceptKingdom);
            if (!string.IsNullOrEmpty(fallbackModified)) return fallbackModified;

            string fallback = Pick(_config.realmNames);
            if (string.IsNullOrWhiteSpace(fallback)) fallback = "宁";
            _sessionRealmNames.Add(fallback);
            return fallback;
        }

        private string GenerateUniqueNomadicRealmName(Kingdom exceptKingdom)
        {
            for (int i = 0; i < NomadicRealmNames.Length * 3; i++)
            {
                string candidate = Pick(NomadicRealmNames);
                if (!IsRealmNameUsed(candidate, exceptKingdom))
                {
                    _sessionRealmNames.Add(candidate);
                    return candidate;
                }
            }
            string fallback = "新" + Pick(NomadicRealmNames);
            _sessionRealmNames.Add(fallback);
            return fallback;
        }

        /// <summary>阶段3H-14：名制强制跟种族——人类固定东方名制（姓+名，无·），
        /// 兽人固定游牧名制（氏族姓·本名）。其他智慧种族按王国/物种默认兜底。
        /// 旧判定"本人kingdom体制优先"是跨族名串格的根因（兽人在东方国被写成东方名、
        /// 人类游牧子民被写成·格式），种族是出生即定的硬属性，名制必须锚定它。</summary>
        private static bool IsNomadicActor(Actor actor)
        {
            if (actor == null) return WorldAllNomadicFallback();
            string species = KingdomCivilizationService.GetActorSpeciesId(actor);
            if (species == "human") return false;                 // 人类：永远东方名制
            if (species == "orc") return true;                    // 兽人：永远游牧名制
            try
            {
                if (actor.kingdom != null)
                    return KingdomCivilizationService.Instance.IsNomadic(actor.kingdom);
            }
            catch { }
            return KingdomCivilizationService.Instance.GetSpeciesDefault(species) == CivilizationType.Nomadic;
        }

        /// <summary>阶段3C：空创建者兜底——世界只有游牧国（典型：纯兽人世界 worldgen）时按游牧命名。</summary>
        /// <summary>阶段3C：游牧氏族名（查重——千年同名氏族加山河缀区分：克烈部→克烈·燕然部）。</summary>
        private string GenerateNomadicClanName(string surname)
        {
            // P1-7b 防双"部"——同上，传入姓可能已带后缀
            if (surname.EndsWith("部")) surname = surname.Substring(0, surname.Length - 1);
            string baseName = surname + "部";
            // 阶段3H-2：注册统一交还 SetClanName（此处自注册会让外层查重恒真→每个游牧氏族都误拼东方堂号）
            if (!_sessionClanNames.Contains(baseName)) return baseName;
            for (int i = 0; i < 48; i++)
            {
                string cand = surname + "·" + Pick(NomadicRealmNames) + "部";
                if (!_sessionClanNames.Contains(cand)) { _sessionClanNames.Add(cand); return cand; }
            }
            return baseName;
        }

        /// <summary>阶段3C：游牧城名（查重——词池耗尽加方位缀）。</summary>
        private string GenerateNomadicCityName()
        {
            string n = UniqueName(() => Pick(NomadicSettlementNames), IsCityNameUsed, _sessionCityNames, "北庭");
            if (n == "北庭" && IsCityNameUsed(n))
            {
                string[] dir = { "东", "西", "南", "北", "上", "下", "前", "后" };
                for (int i = 0; i < dir.Length; i++)
                {
                    string cand = dir[i] + "北庭";
                    if (!IsCityNameUsed(cand)) { _sessionCityNames.Add(cand); return cand; }
                }
            }
            return n;
        }

        private static bool WorldAllNomadicFallback()
        {
            try
            {
                if (World.world?.kingdoms?.list == null || World.world.kingdoms.list.Count == 0) return false;
                bool anyNomad = false;
                foreach (var k in World.world.kingdoms.list)
                {
                    if (k == null || !k.isAlive()) continue;
                    if (KingdomCivilizationService.Instance.IsNomadic(k)) { anyNomad = true; continue; }
                    return false; // 存在任一东方国 → 空创建者按东方
                }
                return anyNomad;
            }
            catch { return false; }
        }

        private string GenerateModifiedRealmName(string[] modifiers, Kingdom exceptKingdom)
        {
            for (int i = 0; i < 240; i++)
            {
                string modifier = Pick(modifiers);
                string root = Pick(_config.realmNames);
                if (string.IsNullOrWhiteSpace(root) || root.StartsWith(modifier)) continue;
                string candidate = modifier + root;
                if (!IsRealmNameUsed(candidate, exceptKingdom))
                {
                    _sessionRealmNames.Add(candidate);
                    return candidate;
                }
            }
            foreach (string modifier in modifiers)
                foreach (string root in _config.realmNames)
                {
                    if (string.IsNullOrWhiteSpace(root) || root.StartsWith(modifier)) continue;
                    string candidate = modifier + root;
                    if (!IsRealmNameUsed(candidate, exceptKingdom))
                    {
                        _sessionRealmNames.Add(candidate);
                        return candidate;
                    }
                }
            return "";
        }

        private string GenerateClanName(string surname, string home)
        {
            if (string.IsNullOrEmpty(surname)) surname = "无名";
            // P1-7b 防双"氏"（实锤：武清柯氏氏）——改姓链路可能传入已带"氏"的姓
            if (surname.EndsWith("氏")) surname = surname.Substring(0, surname.Length - 1);
            string seat = CleanPlaceName(home);
            if (string.IsNullOrEmpty(seat) && !Empty(_config.clanJunwang)) seat = Pick(_config.clanJunwang);
            return string.IsNullOrEmpty(seat) ? surname + "氏" : seat + surname + "氏";
        }

        // ═════════════ 亚种命名 ═════════════

    }
}
