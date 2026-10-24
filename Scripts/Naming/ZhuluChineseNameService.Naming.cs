using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using ZhuluRebuild.Adapters;
using ZhuluRebuild.Core;
using ZhuluRebuild.Domain;
using ZhuluRebuild.Persistence;

// ── 由 ZhuluChineseNameService.cs 拆出的 partial 部分：对象命名（城/国/氏族/战争/文化…） ──
// 2026-10-23 [P1-1] 纯文件级拆分，编译后仍为同一个类型，无任何行为改动。
namespace ZhuluRebuild.Naming
{
    public sealed partial class ZhuluChineseNameService
    {
        // ───────────────────────────── 对象命名 ─────────────────────────────

        public string SetCityName(City city, Actor founder = null)
        {
            // 阶段3C：空创建者（扩张建城等）按城市归属国补判体系
            bool ownerNomadic = false;
            if (founder == null)
            {
                try
                {
                    var k = ClaimManager.GetCityKingdom(city);
                    if (k != null) ownerNomadic = KingdomCivilizationService.Instance.IsNomadic(k);
                }
                catch { }
            }

            EnsureLoaded();
            if (city == null || city.data == null) return null;
            bool nomadic = founder != null ? IsNomadicActor(founder) : (ownerNomadic || WorldAllNomadicFallback());
            string generated = nomadic ? GenerateNomadicCityName() : GenerateUniqueCityName();
            _cityNames[city] = generated;
            string aliasKey = GetCityAliasKey(generated);
            if (!string.IsNullOrEmpty(aliasKey)) _sessionCityAliasKeys.Add(aliasKey);
            city.data.name = generated;
            WritePersistentName(city.data, generated);
            return generated;
        }

        public string SetKingdomName(Kingdom kingdom)
        {
            EnsureLoaded();
            if (kingdom == null || kingdom.data == null) return null;

            // 已命名保护（2026-08-03）：已有国名不重新生成。
            // 根因：WorldBox 城邦升级/王国重建时会对已有王国再次调用 newCivKingdom，
            // 补丁无条件 SetKingdomName → 从词库重新生成新名（如"平原"→"交趾"）→
            // TickAuthority 每 3 秒覆盖地图名 → 读档后天命国被改名。
            // 新王国（键为空）/叛乱转正（调用方先清键）仍走正常生成。
            string existing = KingdomDataExt.GetStr(kingdom, KingdomBaseNameKey);
            if (!string.IsNullOrWhiteSpace(existing))
            {
                // 叛乱转正是特例：当前 data.name 仍是“××义军”，但新国创建时
                // 往往已经写过 cn_base_name。旧代码在此直接 return，导致转正后仍短暂保留叛军名。
                if (Persistence.KingdomDataExt.GetStr(kingdom, "zlr_rebel_named") == "1")
                {
                    KingdomExt.SetBaseName(kingdom.id.ToString(), existing);
                    kingdom.data.name = KingdomExt.GetStoredName(kingdom.id.ToString(), existing);
                    _kingdomBaseNames[kingdom] = existing;
                    _kingdomObservedNames[kingdom] = kingdom.data.name;
                }
                return existing;
            }

            string generated = KingdomCivilizationService.Instance.IsNomadic(kingdom)
                ? GenerateUniqueNomadicRealmName(kingdom) : GenerateUniqueRealmBaseName(kingdom);
            PersistKingdomBaseName(kingdom, generated);
            kingdom.data.name = KingdomExt.GetStoredName(kingdom.id.ToString(), generated);
            _kingdomObservedNames[kingdom] = kingdom.data.name;
            return generated;
        }

        /// <summary>阶段3H-17：氏族迁移器（ClanMemberCapEnforcer）并人后立即按目标氏族权威姓
        /// 改其姓名——不等年度同步，消除过渡期同族异姓（用户实锤家族统一姓氏没有了）。</summary>
        public void ApplyClanSurnameTo(Actor actor, Clan clan)
        {
            EnsureLoaded();
            if (actor == null || clan == null) return;
            try
            {
                string sn = GetClanSurname(clan);
                if (string.IsNullOrEmpty(sn)) return;
                ApplySurname(actor, sn);
            }
            catch { }
        }

        public string SetClanName(Clan clan, Actor founder)
        {
            EnsureLoaded();
            if (clan == null || clan.data == null) return null;
            // 创族时以创始人的真实数据名为第一权威，不能继续使用可能被旧档污染的个人姓字段。
            // 例如创始人“杨宣曜”必须创建“××杨氏”，不能因 zlr_cn_family_name 残留“常”而成为“滁常氏”。
            string surname = ExtractSurname(ActorAccess.GetRawName(founder));
            if (string.IsNullOrEmpty(surname)) surname = GetOrCreateSurname(founder);
            if (founder != null && !string.IsNullOrEmpty(surname))
            {
                ActorAccess.SetString(founder, ActorSurnameKey, surname);
                ActorAccess.SetString(founder, SharedFamilyNameKey, surname);
            }
            string home = GetFounderHome(founder);
            bool nomadClan = IsNomadicActor(founder);
            string generated = nomadClan ? GenerateNomadicClanName(surname) : GenerateClanName(surname, home);
            if (_sessionClanNames.Contains(generated))
            {
                // 同郡望同姓分支用堂号区分，社会身份不混进正式氏族名（游牧用草原堂名池）。
                string[] hallPool = nomadClan ? NomadicFamilyHalls : (_config.familyHallNames ?? new string[0]);
                foreach (string hall in hallPool)
                {
                    string candidate = generated + "·" + hall;
                    if (_sessionClanNames.Contains(candidate)) continue;
                    generated = candidate;
                    break;
                }
                // 极端长局同郡同姓分支多于堂号池时，以传统支派方位继续区分（游牧用翼帐支脉）。
                if (_sessionClanNames.Contains(generated))
                {
                    string[] branches = nomadClan
                        ? new string[] { "左翼", "右翼", "中帐", "别部", "长子帐", "幼子帐" }
                        : new string[] { "东支", "西支", "南支", "北支", "长房", "二房", "三房", "季房" };
                    foreach (string branch in branches)
                    {
                        string candidate = generated + "·" + branch;
                        if (_sessionClanNames.Contains(candidate)) continue;
                        generated = candidate;
                        break;
                    }
                }
            }
            _sessionClanNames.Add(generated);
            _clanNames[clan] = generated;
            clan.data.name = generated;
            WritePersistentName(clan.data, generated);
            WritePersistentValue(clan.data, ClanSurnameKey, surname);
            SynchronizeClanSurname(clan, surname);
            return generated;
        }

        /// <summary>为原版 Family 对象命名。Family 与 Clan 分离，故通过反射数据访问层写入。</summary>
        public string SetFamilyObjectName(object family, Actor founder)
        {
            EnsureLoaded();
            if (family == null || founder == null) return null;
            string surname = ExtractSurname(ActorAccess.GetRawName(founder));
            if (string.IsNullOrEmpty(surname)) surname = GetOrCreateSurname(founder);
            string home = GetFounderHome(founder);
            string generated = IsNomadicActor(founder) ? GenerateNomadicClanName(surname) : GenerateClanName(surname, home);
            FamilyAccess.SetName(family, generated);
            object data = FamilyAccess.GetData(family);
            WritePersistentName(data, generated);
            WritePersistentValue(data, FamilySurnameKey, surname);
            _familyNames[family] = generated;
            return generated;
        }

        public string SetWarName(War war, Kingdom attacker, Kingdom defender, WarTypeAsset type)
        {
            EnsureLoaded();
            if (war == null || war.data == null) return null;
            // 2026-08-19 战名丰富（防"无名之战"）：主词库 → 地点命名（桂陵之战式）→ 序战命名（再战/三战）→ 方位修饰兜底
            string a = GetRealmShortName(attacker);
            string d = GetRealmShortName(defender);
            string pairKey = BuildWarPairKey(attacker, defender);
            _warPairCount.TryGetValue(pairKey, out int warIndex);
            _warPairCount[pairKey] = warIndex + 1;

            string generated = UniqueWarName(a, d, type?.id ?? "normal", warIndex, attacker, defender);
            _warNames[war] = generated;
            war.data.name = generated;
            WritePersistentName(war.data, generated);
            return generated;
        }

        /// <summary>2026-08-19：两国战争对键（id 小者在前——攻守互换也累计）。</summary>
        private static string BuildWarPairKey(Kingdom attacker, Kingdom defender)
        {
            try
            {
                string aId = attacker != null ? attacker.id.ToString() : "0";
                string dId = defender != null ? defender.id.ToString() : "0";
                return string.CompareOrdinal(aId, dId) <= 0 ? aId + "|" + dId : dId + "|" + aId;
            }
            catch { return "0|0"; }
        }

        /// <summary>
        /// 2026-08-19 战名四级生成（杜绝"无名之战"）：
        /// ① 主词库（类型化扩充）② 地点命名（守方都城/边城——桂陵之战式）
        /// ③ 序战命名（同国再战/三战/四战…）④ 方位修饰兜底（修饰基名而非空泛 fallback）。
        /// </summary>
        private string UniqueWarName(string a, string d, string typeId, int warIndex, Kingdom attacker, Kingdom defender)
        {
            // ① 主词库
            for (int i = 0; i < 30; i++)
            {
                string candidate = GenerateWarName(a, d, typeId);
                if (!string.IsNullOrWhiteSpace(candidate) && !_sessionWarNames.Contains(candidate))
                { _sessionWarNames.Add(candidate); return candidate; }
            }

            // ② 地点命名（守方都城 → 守方任意城 → 攻方边城）
            foreach (string place in GetBattlePlaces(attacker, defender))
            {
                if (string.IsNullOrEmpty(place)) continue;
                string[] placePatterns =
                {
                    place + "之战", place + "之役", place + "鏖兵之役",
                    place + "兵燹之祸", place + "烽火之役"
                };
                string candidate = Pick(placePatterns);
                if (!_sessionWarNames.Contains(candidate)) { _sessionWarNames.Add(candidate); return candidate; }
            }

            // ③ 序战命名（同一对国家第 N 次交兵：初战之后称再战/三战…）
            if (warIndex >= 1)
            {
                string[] ordinals = { "再", "三", "四", "五", "六", "七", "八", "九" };
                string ord = warIndex - 1 < ordinals.Length ? ordinals[warIndex - 1] : "屡";
                string[] seqPatterns =
                {
                    $"{a}{d}{ord}启兵衅", $"{a}{ord}举伐{d}", $"{a}{d}{ord}构兵",
                    $"{a}{ord}犯{d}之役", $"{d}{ord}却{a}之师"
                };
                string candidate = Pick(seqPatterns);
                if (!_sessionWarNames.Contains(candidate)) { _sessionWarNames.Add(candidate); return candidate; }
                // 序战 + 地点复合
                foreach (string place in GetBattlePlaces(attacker, defender))
                {
                    if (string.IsNullOrEmpty(place)) continue;
                    string combo = $"{a}{d}{ord}战·{place}之役";
                    if (!_sessionWarNames.Contains(combo)) { _sessionWarNames.Add(combo); return combo; }
                }
            }

            // ④ 方位/新旧修饰兜底（修饰基名——不再退化为"无名之战"）
            string baseName = GenerateWarName(a, d, typeId);
            if (string.IsNullOrWhiteSpace(baseName)) baseName = a + d + "之战";
            string[] modifiers = { "东", "西", "南", "北", "新", "故", "大", "小", "复", "重" };
            foreach (string modifier in modifiers)
            {
                string candidate = modifier + baseName;
                if (!_sessionWarNames.Contains(candidate)) { _sessionWarNames.Add(candidate); return candidate; }
            }
            _sessionWarNames.Add(baseName);
            return baseName;
        }

        /// <summary>2026-08-19：战场地点候选（守方都城 → 守方诸城 → 攻方都城；去重、限 6 个）。</summary>
        private static List<string> GetBattlePlaces(Kingdom attacker, Kingdom defender)
        {
            var places = new List<string>();
            try
            {
                if (defender != null)
                {
                    if (defender.capital != null && !string.IsNullOrEmpty(defender.capital.name))
                        places.Add(CleanPlaceName(defender.capital.name));
                    if (defender.cities != null)
                        foreach (var c in defender.cities)
                        {
                            if (c == null || string.IsNullOrEmpty(c.name)) continue;
                            string n = CleanPlaceName(c.name);
                            if (!string.IsNullOrEmpty(n) && !places.Contains(n) && places.Count < 4) places.Add(n);
                        }
                }
                if (attacker != null && attacker.capital != null && !string.IsNullOrEmpty(attacker.capital.name))
                {
                    string n = CleanPlaceName(attacker.capital.name);
                    if (!places.Contains(n)) places.Add(n);
                }
            }
            catch { }
            return places;
        }

        public string SetAllianceName(Alliance alliance)
        {
            EnsureLoaded();
            if (alliance == null || alliance.data == null) return null;
            var members = new List<Kingdom>();
            try
            {
                if (alliance.kingdoms_hashset != null)
                    foreach (Kingdom k in alliance.kingdoms_hashset)
                        if (k != null) members.Add(k);
            }
            catch { }
            string first = members.Count > 0 ? GetRealmShortName(members[0]) : "诸邦";
            string second = members.Count > 1 ? GetRealmShortName(members[1]) : "同道";
            string suffix = Pick(_config.allianceSuffixes);
            string generated = NextDouble() < 0.55d ? first + second + suffix : first + suffix;
            _allianceNames[alliance] = generated;
            alliance.data.name = generated;
            WritePersistentName(alliance.data, generated);
            return generated;
        }

        public string SetCultureName(Culture culture, Actor founder = null)
        {
            EnsureLoaded();
            if (culture == null || culture.data == null) return null;
            string name;
            if (IsNomadicActor(founder))
                name = PickUniqueNomad(NomadicCultureNames, _cultureNames.Values);
            else if (!Empty(_config.cultureNames))
                name = Pick(_config.cultureNames);
            else
            {
                string root = Pick(_config.cultureRoots);
                string suffix = !Empty(_config.cultureSuffixes)
                    ? Pick(_config.cultureSuffixes)
                    : Pick(new[] { "礼俗", "文脉", "风华", "遗风" });
                name = root + suffix;
            }
            _cultureNames[culture] = name;
            culture.data.name = name;
            WritePersistentName(culture.data, name);
            return name;
        }

        public string SetLanguageName(Language language, Actor founder = null)
        {
            EnsureLoaded();
            if (language == null || language.data == null) return null;
            // 30%概率用朝代语言（如"周语""殷语"），70%用词根+后缀
            string name;
            if (IsNomadicActor(founder))
            {
                name = PickUniqueNomad(NomadicLanguageNames, _languageNames.Values);
            }
            else if (_config.dynastyLanguages != null && _config.dynastyLanguages.Length > 0 && NextDouble() < 0.3d)
            {
                name = Pick(_config.dynastyLanguages);
            }
            else
            {
                string root = Pick(_config.cultureRoots);
                string suffix = _config.languageSuffixes != null && _config.languageSuffixes.Length > 0
                    ? Pick(_config.languageSuffixes)
                    : Pick(new[] { "雅言", "官话", "方言", "古语" });
                name = root + suffix;
            }
            _languageNames[language] = name;
            language.data.name = name;
            WritePersistentName(language.data, name);
            return name;
        }

        public string SetReligionName(Religion religion, Actor founder = null)
        {
            EnsureLoaded();
            if (religion == null || religion.data == null) return null;
            string name;
            double roll = NextDouble();
            if (IsNomadicActor(founder))
            {
                name = PickUniqueNomad(NomadicReligionNames, _religionNames.Values);
            }
            else if (!Empty(_config.religionCompleteNames) && roll < 0.22d)
            {
                // 禅宗、净土宗等已经是完整名称，禁止再拼成“禅宗道/净土宗寺”。
                name = Pick(_config.religionCompleteNames);
            }
            else if (!Empty(_config.religionThoughtRoots) && roll < 0.52d)
            {
                string root = Pick(_config.religionThoughtRoots);
                name = root + Pick(new[] { "学", "派", "道", "门" });
            }
            else if (!Empty(_config.religionDeityRoots))
            {
                string root = Pick(_config.religionDeityRoots);
                name = root + Pick(new[] { "祀", "庙", "宫", "教" });
            }
            else
            {
                string root = Pick(_config.religionRoots);
                string suffix = !Empty(_config.religionSuffixes)
                    ? Pick(_config.religionSuffixes)
                    : Pick(new[] { "道", "教", "祀", "法门" });
                name = root + suffix;
            }
            _religionNames[religion] = name;
            religion.data.name = name;
            WritePersistentName(religion.data, name);
            return name;
        }

        public string EnsureKingdomMotto(Kingdom kingdom, bool wasBlank)
        {
            EnsureLoaded();
            if (kingdom == null || kingdom.data == null) return null;
            if (!wasBlank && !string.IsNullOrWhiteSpace(kingdom.data.motto)) return kingdom.data.motto;
            kingdom.data.motto = Pick(_config.kingdomMottos);
            return kingdom.data.motto;
        }

        public string EnsureClanMotto(Clan clan, bool wasBlank)
        {
            EnsureLoaded();
            if (clan == null || clan.data == null) return null;
            if (!wasBlank && !string.IsNullOrWhiteSpace(clan.data.motto)) return clan.data.motto;
            clan.data.motto = Pick(_config.clanMottos);
            return clan.data.motto;
        }

        public string EnsureAllianceMotto(Alliance alliance, bool wasBlank)
        {
            EnsureLoaded();
            if (alliance == null || alliance.data == null) return null;
            if (!wasBlank && !string.IsNullOrWhiteSpace(alliance.data.motto)) return alliance.data.motto;
            alliance.data.motto = Pick(_config.allianceMottos);
            return alliance.data.motto;
        }

        // ───────────────────────────── Tick（轻量） ─────────────────────────────

        /// <summary>
        /// 阶段3 改名功能：单王国名同步（带玩家改名检测）——从 TickAuthority 循环体抽出，
        /// 供 Main.SyncKingdomStoredNames 对【未跟踪王国】复用（原盲回写会吃掉玩家的改名）。
    }
}
