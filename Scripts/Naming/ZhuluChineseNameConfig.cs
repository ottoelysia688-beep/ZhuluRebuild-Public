using System;

namespace ZhuluRebuild.Naming
{
    [Serializable]
    public class ZhuluChineseNameConfig
    {
        public string[] surnames;
        public string[] compoundSurnames;
        public string[] maleGiven;
        public string[] femaleGiven;
        public string[] neutralGiven;
        // v8 人名语义池：前字负责辈分/气质，后字负责个人含义；旧字段保留作兼容回退
        public string[] maleNameFirst;
        public string[] maleNameSecond;
        public string[] femaleNameFirst;
        public string[] femaleNameSecond;
        public string[] cityPrefixes;
        public string[] cityStems;
        public string[] citySuffixes;
        public string[] ancientCityNames;
        public string[] realmNames;
        public string[] clanSuffixes;
        public string[] clanJunwang;
        public string[] allianceSuffixes;
        public string[] cultureRoots;
        public string[] cultureSuffixes;
        public string[] cultureNames;
        public string[] religionRoots;
        public string[] religionSuffixes;
        public string[] religionDeityRoots;
        public string[] religionThoughtRoots;
        public string[] religionCompleteNames;
        public string[] languageSuffixes;
        public string[] dynastyLanguages;
        public string[] subspeciesRoots;
        public string[] subspeciesSuffixes;
        public string[] sapientSubspeciesNames;
        public string[] familySuffixes;
        // 2026-08-18 家族名美化：堂号池（"赵氏·积善堂"式——中古世家堂号）
        public string[] familyHallNames;
        // 2026-08-18 朝代名美化：经典朝代字池（受命国号从中择字，"大淮朝"→"淮"在池则用，
        // 否则换经典字——"宁朝""平朝"类生僻组合不再出现）
        public string[] dynastyNames;
        public string[] animalPrefixes;
        public string[] animalNames;
        public string[] boatFishing;
        public string[] boatTrading;
        public string[] boatBattle;
        public string[] boatTransport;
        public string[] legendaryWeapons;
        public string[] weaponMaterials;
        public string[] weaponSuffixes;
        // 装备按类型分池（0.51.2 EquipmentType: Weapon/Helmet/Armor/Boots/Ring/Amulet）
        public string[] legendaryArmors;
        public string[] helmetSuffixes;
        public string[] armorSuffixes;
        public string[] bootsSuffixes;
        public string[] ringSuffixes;
        public string[] amuletSuffixes;
        // 游戏真实材质 id → 中文名（格式 "wood|木"，与 animalNames 一致）
        public string[] materialNames;
        public string[] bookSuffixes;
        public string[] monsterNames;
        public string[] kingdomMottos;
        public string[] clanMottos;
        public string[] allianceMottos;
        public string[] rebelSuffixes;
    }
}
