using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using ZhuluRebuild.Adapters;
using ZhuluRebuild.Core;
using ZhuluRebuild.Domain;
using ZhuluRebuild.Persistence;

// ── 由 ZhuluChineseNameService.cs 拆出的 partial 部分：亚种/装备/船只/叛军命名 ──
// 2026-10-23 [P1-1] 纯文件级拆分，编译后仍为同一个类型，无任何行为改动。
namespace ZhuluRebuild.Naming
{
    public sealed partial class ZhuluChineseNameService
    {
        /// <summary>为亚种生成中文名。智慧种族用完整族裔称呼，动物用古风兽名。</summary>
        public string SetSubspeciesName(Subspecies subspecies)
        {
            EnsureLoaded();
            if (subspecies == null || subspecies.data == null) return null;

            // 判断是否智慧生物
            bool isSapient = false;
            try
            {
                var field = typeof(Subspecies).GetField("_is_sapient",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (field != null) isSapient = (bool)field.GetValue(subspecies);
            }
            catch { }

            string name;
            if (isSapient)
            {
                // 阶段3C：游牧种（默认兽人系）亚种名走游牧池（苍狼裔/白鹿裔…），不出现"华夏族"
                try
                {
                    string spId = null;
                    try
                    {
                        // 阶段3E：species_id 非公有——反射直读（参考 ChineseName mod 实锤该字段存在）
                        const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                        spId = (typeof(Subspecies).GetField("species_id", F)?.GetValue(subspecies)
                            ?? typeof(Subspecies).GetProperty("species_id", F)?.GetValue(subspecies)) as string;
                    }
                    catch { }
                    if (string.IsNullOrEmpty(spId))
                    {
                        try
                        {
                            object assetObj = typeof(Subspecies).GetField("asset")?.GetValue(subspecies);
                            if (assetObj is ActorAsset aa2) spId = aa2.id;
                        }
                        catch { }
                    }
                    if (string.IsNullOrEmpty(spId))
                    {
                        // 阶段3C：asset 探测失败 → 找该亚种的存活成员按其体系路由
                        try
                        {
                            var units = World.world?.units?.getSimpleList();
                            if (units != null)
                            {
                                for (int ui = 0; ui < units.Count && ui < 3000; ui++)
                                {
                                    var u = units[ui];
                                    if (u == null || !u.isAlive()) continue;
                                    var udata = ActorAccess.GetData(u);
                                    if (udata == null) continue;
                                    object subRef = udata.GetType().GetField("subspecies")?.GetValue(udata);
                                    if (ReferenceEquals(subRef, subspecies))
                                    {
                                        if (IsNomadicActor(u)) spId = "orc";
                                        break;
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                    if (!string.IsNullOrEmpty(spId)
                        && Domain.KingdomCivilizationService.Instance.GetSpeciesDefault(spId) == Domain.CivilizationType.Nomadic)
                    {
                        string nn = GetNomadicSubspeciesName();
                        if (!string.IsNullOrEmpty(nn))
                        {
                            subspecies.data.name = nn;
                            WritePersistentName(subspecies.data, nn);
                            return nn;
                        }
                    }
                }
                catch { }
                // 智慧族裔使用经过审核的完整称呼；动物仍走独立动物名路径。
                if (!Empty(_config.sapientSubspeciesNames))
                    name = Pick(_config.sapientSubspeciesNames);
                else
                {
                    string root = !Empty(_config.subspeciesRoots) ? Pick(_config.subspeciesRoots) : "华";
                    string suffix = !Empty(_config.subspeciesSuffixes) ? Pick(_config.subspeciesSuffixes) : "族";
                    name = root + suffix;
                }
            }
            else
            {
                // 非智慧动物：古风前缀 + 中文动物名
                string prefix = _config.animalPrefixes != null && _config.animalPrefixes.Length > 0
                    ? Pick(_config.animalPrefixes) : "苍";
                string animalName = GetChineseAnimalName(subspecies);
                name = prefix + animalName;
            }
            subspecies.data.name = name;
            return name;
        }

        /// <summary>获取动物的中文名：优先用游戏翻译，翻译失败则用内置词库。</summary>
        private string GetChineseAnimalName(Subspecies subspecies)
        {
            // 尝试从游戏素材库获取翻译名
            try
            {
                string speciesId = subspecies.data.species_id;
                if (!string.IsNullOrEmpty(speciesId))
                {
                    var asset = AssetManager.actor_library.get(speciesId);
                    if (asset != null)
                    {
                        string translated = asset.getTranslatedName();
                        // 检查是否包含中文（如果包含则直接使用）
                        if (!string.IsNullOrEmpty(translated) && HasChineseChar(translated))
                            return translated;
                    }
                    // 翻译名不是中文，用内置词库查找
                    if (_config.animalNames != null)
                    {
                        foreach (var entry in _config.animalNames)
                        {
                            if (entry != null && speciesId.Contains(entry.Split('|')[0]))
                            {
                                var parts = entry.Split('|');
                                if (parts.Length > 1) return parts[1];
                            }
                        }
                    }
                }
            }
            catch { }
            return "兽";
        }

        private static bool HasChineseChar(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s)
                if (c >= 0x4E00 && c <= 0x9FFF) return true;
            return false;
        }

        // ═════════════ 家庭命名 ═════════════

        /// <summary>为家庭生成中文名：固定为“郡望+真实姓+氏”，不再随机换姓或拼身份标签。</summary>
        public string SetFamilyName(Actor actor)
        {
            EnsureLoaded();
            if (actor == null) return null;
            string surname = ExtractSurname(ActorAccess.GetRawName(actor));
            if (string.IsNullOrEmpty(surname)) surname = GetOrCreateSurname(actor);
            try
            {
                string clanName = actor.clan?.data?.name;
                if (!string.IsNullOrWhiteSpace(clanName)) return clanName;
            }
            catch { }
            return GenerateClanName(surname, GetFounderHome(actor));
        }

        // ═════════════ 书籍命名 ═════════════

        /// <summary>为书籍生成中文名（雅字+类型后缀）。</summary>
        public string GenerateBookName()
        {
            EnsureLoaded();
            string prefix = Pick(_config.cultureRoots);
            string suffix = _config.bookSuffixes != null && _config.bookSuffixes.Length > 0
                ? Pick(_config.bookSuffixes) : "传";
            return prefix + suffix;
        }

        // ═════════════ 装备命名（2026-08-18 重构：细分类+真实材质+品阶） ═════════════

        /// <summary>武器细分类关键词 → 后缀池（asset.id / equipment_subtype 匹配用）。
        /// 修复"木棒叫xx剑、银剑叫金锤"：原武器后缀全池随机，与实际武器外形无关。</summary>
        private static readonly (string[] keys, string[] suffixes)[] WeaponKindMap =
        {
            (new[] { "sword", "saber", "blade", "jian" }, new[] { "剑", "长剑", "佩剑" }),
            (new[] { "hammer", "mace", "maul" }, new[] { "锤", "战锤", "重锤" }),
            (new[] { "axe", "hatchet" }, new[] { "斧", "战斧", "板斧" }),
            (new[] { "spear", "lance", "pike" }, new[] { "枪", "长枪", "矛" }),
            (new[] { "halberd", "glave", "polearm" }, new[] { "戟", "长戟" }),
            (new[] { "bow" }, new[] { "弓", "长弓" }),
            (new[] { "crossbow" }, new[] { "弩", "强弩" }),
            (new[] { "dagger", "knife" }, new[] { "匕首", "短刃" }),
            (new[] { "staff", "wand", "rod" }, new[] { "杖", "法杖" }),
            (new[] { "club", "stick", "bat", "cudgel" }, new[] { "棒", "木棒", "棍" }),
            (new[] { "whip" }, new[] { "鞭" }),
            (new[] { "shield" }, new[] { "盾" })
        };

        /// <summary>品阶词（Rarity R0~R3）。R0 无前缀（凡品不注）；R3 走传奇名池。</summary>
        private static string QualityPrefix(int rarity)
        {
            switch (rarity)
            {
                case 1: return "良";
                case 2: return "宝";
                default: return "";
            }
        }

        /// <summary>
        /// 为装备生成中文名（2026-08-18 重构）：
        /// ① 传奇（R3）→ 历史名器池；
        /// ② 武器 → asset.id/equipment_subtype 关键词细分（剑归剑、锤归锤——修复武器种类错乱）；
        /// ③ 材质 → 实例材质（data.material，重铸后真实）优先，asset 默认材质兜底；
        /// ④ 品阶 → R1「良」/R2「宝」前缀（R0 不注，R3 传奇）。
        /// </summary>
        public string GenerateEquipmentName(EquipmentType equipType, int rarity, string materialId, string assetKey)
        {
            EnsureLoaded();

            // 传奇（R3 或旧参 isLegendary 情形）：武器用名剑/名器池，防具饰品用甲胄名器池
            if (rarity >= 3)
            {
                if (equipType == EquipmentType.Weapon && _config.legendaryWeapons != null && _config.legendaryWeapons.Length > 0)
                    return Pick(_config.legendaryWeapons);
                if (_config.legendaryArmors != null && _config.legendaryArmors.Length > 0)
                    return Pick(_config.legendaryArmors);
            }

            // 材质：实例材质优先（重铸换材后 asset.material 已失真），映射不到随机古风材质
            string mat = GetMaterialName(materialId);
            if (string.IsNullOrEmpty(mat))
            {
                mat = _config.weaponMaterials != null && _config.weaponMaterials.Length > 0
                    ? Pick(_config.weaponMaterials) : "精钢";
            }

            string suffix = PickSuffixForType(equipType, assetKey);
            return QualityPrefix(rarity) + mat + suffix;
        }

        /// <summary>按装备类型选择后缀池；武器按 asset 关键词细分（种类与外形一致）。</summary>
        private string PickSuffixForType(EquipmentType equipType, string assetKey)
        {
            string[] pool;
            switch (equipType)
            {
                case EquipmentType.Helmet:
                    pool = _config.helmetSuffixes;
                    break;
                case EquipmentType.Armor:
                    pool = _config.armorSuffixes;
                    break;
                case EquipmentType.Boots:
                    pool = _config.bootsSuffixes;
                    break;
                case EquipmentType.Ring:
                    pool = _config.ringSuffixes;
                    break;
                case EquipmentType.Amulet:
                    pool = _config.amuletSuffixes;
                    break;
                default:
                    // 武器细分：id/subtype 含关键词 → 对应种类后缀（木棒叫"木棒"而非"木剑"）
                    if (!string.IsNullOrEmpty(assetKey))
                    {
                        string key = assetKey.ToLowerInvariant();
                        foreach (var (keys, suffixes) in WeaponKindMap)
                        {
                            foreach (var k in keys)
                            {
                                if (key.Contains(k)) return Pick(suffixes);
                            }
                        }
                    }
                    pool = _config.weaponSuffixes;
                    break;
            }
            if (pool == null || pool.Length == 0) pool = new[] { "器" };
            return Pick(pool);
        }

        /// <summary>游戏真实材质 id → 中文名。未配置映射返回 null（调用方随机兜底）。</summary>
        public string GetMaterialName(string materialId)
        {
            if (string.IsNullOrEmpty(materialId) || _config.materialNames == null) return null;
            foreach (var entry in _config.materialNames)
            {
                if (entry == null) continue;
                var parts = entry.Split('|');
                if (parts.Length >= 2 && parts[0] == materialId)
                    return parts[1];
            }
            return null;
        }

        // ═════════════ 船只命名 ═════════════

        /// <summary>根据船型生成中文名。</summary>
        public string GenerateBoatName(string boatType)
        {
            EnsureLoaded();
            string[] pool;
            if (boatType != null && boatType.Contains("fish")) pool = _config.boatFishing;
            else if (boatType != null && boatType.Contains("trad")) pool = _config.boatTrading;
            else if (boatType != null && (boatType.Contains("battle") || boatType.Contains("war"))) pool = _config.boatBattle;
            else if (boatType != null && boatType.Contains("transport")) pool = _config.boatTransport;
            else pool = _config.boatFishing;
            if (pool == null || pool.Length == 0) pool = new[] { "渔舟" };
            return Pick(pool);
        }

        // ═════════════ 怪物命名 ═════════════

        /// <summary>为特殊怪物生成中文名（山海经风格）。</summary>
        public string GenerateMonsterName()
        {
            EnsureLoaded();
            if (_config.monsterNames != null && _config.monsterNames.Length > 0)
                return Pick(_config.monsterNames);
            return "异兽";
        }

        // ═════════════ 叛乱命名 ═════════════

        /// <summary>为叛乱王国生成名字：首都名+叛乱后缀（如"邯郸叛军""临淄义军"）。</summary>
        public string GenerateRebelName(Kingdom rebelKingdom)
        {
            EnsureLoaded();
            if (rebelKingdom == null) return "叛军";

            // 获取首都名（多种方式尝试）
            string cityName = null;
            try
            {
                // 方式1：直接读 capital.data.name
                if (rebelKingdom.capital != null && rebelKingdom.capital.data != null)
                    cityName = rebelKingdom.capital.data.name;

                // 方式2：如果capital为空，从城市列表取第一个
                if (string.IsNullOrEmpty(cityName) && rebelKingdom.cities != null && rebelKingdom.cities.Count > 0)
                {
                    var firstCity = rebelKingdom.cities[0];
                    if (firstCity?.data != null) cityName = firstCity.data.name;
                }
            }
            catch { }

            // 如果还是拿不到城市名，用王国基础名（去掉后缀）
            if (string.IsNullOrEmpty(cityName))
            {
                _kingdomBaseNames.TryGetValue(rebelKingdom, out cityName);
            }
            if (string.IsNullOrEmpty(cityName)) cityName = "无名";

            string suffix = _config.rebelSuffixes != null && _config.rebelSuffixes.Length > 0
                ? Pick(_config.rebelSuffixes) : "叛军";
            return cityName + suffix;
        }

    }
}
