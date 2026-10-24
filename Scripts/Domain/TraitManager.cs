using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using ZhuluRebuild.Core;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Domain
{
    /// <summary>
    /// 逐鹿人物特质管理器。
    /// 使用游戏原生特质系统（ActorTrait），特质自动显示在角色面板和特质总栏。
    /// 年度扫描自动赋予/移除特质，并接入腐败/政策/稳定度/考评系统。
    /// </summary>
    public class TraitManager
    {
        public static TraitManager Instance { get; } = new TraitManager();
        private bool _registered = false;

        /// <summary>2026-08-20 逐鹿专属分组：原版特质编辑器独立一栏（全部逐鹿特质归入）。</summary>
        public const string GROUP_ZHULU = "zhulu";

        // 特质 ID 常量
        public const string TRAIT_BENEVOLENT = "zlr_benevolent_ruler";
        public const string TRAIT_WISE_MINISTER = "zlr_wise_minister";
        public const string TRAIT_LOYAL = "zlr_loyal_subject";
        public const string TRAIT_GENIUS = "zlr_genius";
        public const string TRAIT_FOUNDER = "zlr_dynasty_founder";
        public const string TRAIT_TYRANT = "zlr_tyrant";
        public const string TRAIT_CORRUPT = "zlr_corrupt_official";
        public const string TRAIT_MEDIOCRE = "zlr_mediocre";
        public const string TRAIT_AMBITIOUS = "zlr_ambitious";
        public const string TRAIT_FOOLISH = "zlr_foolish_ruler";
    public const string TRAIT_FORMER_ROYAL = "zlr_former_royal"; // 阶段2A A1-8：前朝宗室（纯标识，无效果）

    // ── 2026-08-20 身份特质（纯标识：贴图先用默认王冠 iconCrown）──
    public const string TRAIT_CROWN_PRINCE = "zlr_crown_prince";   // 太子（天命国储君）
    public const string TRAIT_HEIR_APPARENT = "zlr_heir_apparent"; // 世子（诸侯储君）
    public const string TRAIT_NOMAD_HEIR = "zlr_nomad_heir";       // 游牧继承人
    public const string TRAIT_CITY_LORD = "zlr_city_lord";         // 城主
    public const string TRAIT_FALLEN_KING = "zlr_fallen_king";     // 亡国之君（灭国时最后的国王，终身）
    public const string TRAIT_RESTORED_KING = "zlr_restored_king"; // 复国之君（亡国之君再登王位，与亡国之君共存）

    // ── 2026-08-20 君主风评新增（与仁君/昏君/暴君同组互斥）──
    public const string TRAIT_SAGE_RULER = "zlr_sage_ruler";       // 明君
    public const string TRAIT_STEADY_RULER = "zlr_steady_ruler";   // 守成之主

    // ── 2026-08-20 官职特质（中央七席 + 地方官一档；前缀 + officeId）──
    /// <summary>官职 → 特质 id（地方官三席统一为 zlr_office_local）。</summary>
    public static string GetOfficeTraitId(string officeId)
    {
        if (OfficeManager.IsLocalOffice(officeId)) return "zlr_office_local";
        return "zlr_office_" + officeId;
    }

    public static string GetOfficeTraitId(string officeId, Kingdom kingdom)
    {
        if (kingdom != null && KingdomCivilizationService.Instance.IsNomadic(kingdom))
        {
            if (OfficeManager.IsLocalOffice(officeId)) return "zlr_nomad_office_local";
            return "zlr_nomad_office_" + officeId;
        }
        return GetOfficeTraitId(officeId);
    }

    /// <summary>全部官职特质 id 清单（互斥链/同步扫描用）。</summary>
    public static readonly string[] OfficeTraitIds =
    {
        "zlr_office_prime_minister",
        "zlr_office_personnel",
        "zlr_office_revenue",
        "zlr_office_rites",
        "zlr_office_military",
        "zlr_office_justice",
        "zlr_office_works",
        "zlr_office_local"
        ,"zlr_nomad_office_prime_minister"
        ,"zlr_nomad_office_personnel"
        ,"zlr_nomad_office_revenue"
        ,"zlr_nomad_office_rites"
        ,"zlr_nomad_office_military"
        ,"zlr_nomad_office_justice"
        ,"zlr_nomad_office_works"
        ,"zlr_nomad_office_local"
    };

    /// <summary>君主风评特质（年度互斥判定——一人一时一风评）。</summary>
    public static readonly string[] RulerVerdictTraits =
    {
        TRAIT_BENEVOLENT, TRAIT_SAGE_RULER, TRAIT_FOOLISH, TRAIT_TYRANT, TRAIT_STEADY_RULER
    };


    // 阶段2E ⑦ 功名特质（2026-08-16；id 不带 trait_ 前缀——显示名 key="trait_"+id 自动拼接；纯标识无效果）
    public const string TRAIT_XIUCAI = "zlr_xiucai";       // 秀才（县试）
    public const string TRAIT_JUREN = "zlr_juren";         // 举人（乡试）
    public const string TRAIT_JINSHI = "zlr_jinshi";       // 进士（殿试）
    public const string TRAIT_ZHUANGYUAN = "zlr_zhuangyuan"; // 状元（进士榜首）
    public const string TRAIT_WUXIUCAI = "zlr_wuxiucai";   // 武秀才
    public const string TRAIT_WUJUREN = "zlr_wujuren";     // 武举人
    public const string TRAIT_WUJINSHI = "zlr_wujinshi";   // 武进士

    // 阶段2B 包1：爵位身份特质（6 个，互为 traits_to_remove——升级自动替换）
    // 2026-08-15 修复：id 去 trait_ 前缀（显示名 key = "trait_"+id 自动拼接，防双前缀显示英文）
    public const string TIER_TIANZI = "zlr_tier_tianzi";
    public const string TIER_GONG = "zlr_tier_gong";
    public const string TIER_HOU = "zlr_tier_hou";
    public const string TIER_BO = "zlr_tier_bo";
    public const string TIER_ZI = "zlr_tier_zi";
    public const string TIER_NAN = "zlr_tier_nan";

    // 游牧等级身份特质（数值阈值对应东方六级）
    public const string NOMAD_WANGTING = NomadicTierService.TIER_WANGTING;
    public const string NOMAD_DAHANTING = NomadicTierService.TIER_DAHANTING;
    public const string NOMAD_HANGUO = NomadicTierService.TIER_HANGUO;
    public const string NOMAD_BUMENG = NomadicTierService.TIER_BUMENG;
    public const string NOMAD_DABU = NomadicTierService.TIER_DABU;
    public const string NOMAD_BUZU = NomadicTierService.TIER_BUZU;

    // 游牧选拔身份特质：与东方文武功名级别一一对应，数值不变。
    public const string NOMAD_TRIBE_SCHOLAR = "zlr_nomad_tribe_scholar";
    public const string NOMAD_LEAGUE_SCHOLAR = "zlr_nomad_league_scholar";
    public const string NOMAD_COURT_SCHOLAR = "zlr_nomad_court_scholar";
    public const string NOMAD_FIRST_SCHOLAR = "zlr_nomad_first_scholar";
    public const string NOMAD_WARRIOR = "zlr_nomad_warrior";
    public const string NOMAD_CENTURION = "zlr_nomad_centurion";
    public const string NOMAD_CHILIARCH = "zlr_nomad_chiliarch";

    public static readonly string[] NomadicExamTraitIds =
    {
        NOMAD_TRIBE_SCHOLAR, NOMAD_LEAGUE_SCHOLAR, NOMAD_COURT_SCHOLAR, NOMAD_FIRST_SCHOLAR,
        NOMAD_WARRIOR, NOMAD_CENTURION, NOMAD_CHILIARCH
    };


        /// <summary>特质 → 自定义图标 key（ZhuluAssetLoader 相对 GameResources/ui/zhulu/）。UI 显示用，游戏原生面板仍走 path_icon 占位。</summary>
        private static readonly Dictionary<string, string> TraitIcons = new Dictionary<string, string>
        {
            // ── 基础特质 T01-T10（2026-08-20 全套重绘贴图——文件名不变原位覆盖）──
            { TRAIT_BENEVOLENT, "traits/T01_仁君_祥云圣旨" },
            { TRAIT_WISE_MINISTER, "traits/T02_贤相_玉笏玉冠" },
            { TRAIT_LOYAL, "traits/T03_忠臣_盾剑" },
            { TRAIT_GENIUS, "traits/T04_天纵奇才_星辰" },
            { TRAIT_FOUNDER, "traits/T05_开国之主_龙旗城楼" },
            { TRAIT_TYRANT, "traits/T06_暴君_血刀" },
            { TRAIT_CORRUPT, "traits/T07_贪官_元宝铜钱" },
            { TRAIT_MEDIOCRE, "traits/T08_庸才_白板木架" },
            { TRAIT_AMBITIOUS, "traits/T09_野心家_权杖王冠" },
            { TRAIT_FOOLISH, "traits/T10_昏君_酒爵烛火" },
            // ── 2026-08-20 身份/风评特质 T11-T17（专属新画）──
            { TRAIT_SAGE_RULER, "traits/T11_明君_日月当空" },
            { TRAIT_STEADY_RULER, "traits/T12_守成之主_铜鼎" },
            { TRAIT_CROWN_PRINCE, "traits/T13_太子_东宫玉璧" },
            { TRAIT_HEIR_APPARENT, "traits/T14_世子_金印绶带" },
            { TRAIT_NOMAD_HEIR, "traits/T14_世子_金印绶带" },
            { TRAIT_CITY_LORD, "traits/T15_城主_城门钥匙" },
            { TRAIT_FALLEN_KING, "traits/T16_亡国之君_断旗残戈" },
            { TRAIT_RESTORED_KING, "traits/T17_复国之君_重铸之剑" },
            // ── 科举功名 T18-T24（专属新画——原借用其他系统贴图）──
            { TRAIT_XIUCAI, "traits/T18_秀才_笔简" },
            { TRAIT_JUREN, "traits/T19_举人_旗卷" },
            { TRAIT_JINSHI, "traits/T20_进士_乌纱金榜" },
            { TRAIT_ZHUANGYUAN, "traits/T21_状元_魁星点斗" },
            { TRAIT_WUXIUCAI, "traits/T22_武秀才_弓靶" },
            { TRAIT_WUJUREN, "traits/T23_武举人_枪旗" },
            { TRAIT_WUJINSHI, "traits/T24_武进士_虎符金甲" },
            // ── 爵位 T25-T30（专属新画——原借用国力条贴图）──
            { TIER_TIANZI, "traits/T25_天子_玉玺" },
            { TIER_GONG, "traits/T26_公_九鼎" },
            { TIER_HOU, "traits/T27_侯_印绶" },
            { TIER_BO, "traits/T28_伯_铜爵" },
            { TIER_ZI, "traits/T29_子_玄圭" },
            { TIER_NAN, "traits/T30_邑_土城" },
            { NOMAD_WANGTING, "traits/T25_天子_玉玺" },
            { NOMAD_DAHANTING, "traits/T26_公_九鼎" },
            { NOMAD_HANGUO, "traits/T27_侯_印绶" },
            { NOMAD_BUMENG, "traits/T28_伯_铜爵" },
            { NOMAD_DABU, "traits/T29_子_玄圭" },
            { NOMAD_BUZU, "traits/T30_邑_土城" },
            { NOMAD_TRIBE_SCHOLAR, "traits/T18_秀才_笔简" },
            { NOMAD_LEAGUE_SCHOLAR, "traits/T19_举人_旗卷" },
            { NOMAD_COURT_SCHOLAR, "traits/T20_进士_乌纱金榜" },
            { NOMAD_FIRST_SCHOLAR, "traits/T21_状元_魁星点斗" },
            { NOMAD_WARRIOR, "traits/T22_武秀才_弓靶" },
            { NOMAD_CENTURION, "traits/T23_武举人_枪旗" },
            { NOMAD_CHILIARCH, "traits/T24_武进士_虎符金甲" },
            // ── 前朝宗室 T33（专属新画——原借用角色贴图）──
            { TRAIT_FORMER_ROYAL, "traits/T33_前朝宗室_残玉" }
        };

        /// <summary>特质 → 属性加成表（科举评分/官方评分加成用；RegisterTrait 时写入）。</summary>
        private static readonly Dictionary<string, Dictionary<string, float>> TraitStats = new Dictionary<string, Dictionary<string, float>>();

        /// <summary>取特质属性加成总和（智力+组织+军事+外交）。科举/继位评分用。</summary>
        public static float GetTraitBonus(string traitId)
        {
            if (string.IsNullOrEmpty(traitId) || !TraitStats.TryGetValue(traitId, out var stats)) return 0f;
            float sum = 0f;
            foreach (var kv in stats)
            {
                if (kv.Key == "intelligence" || kv.Key == "stewardship" || kv.Key == "warfare" || kv.Key == "diplomacy")
                {
                    sum += kv.Value;
                }
            }
            return sum;
        }

        /// <summary>取特质图标 key；未映射返回兜底图。</summary>
        public static string GetTraitIconKey(string traitId)
        {
            if (!string.IsNullOrEmpty(traitId) && TraitIcons.TryGetValue(traitId, out var key)) return key;
            return "traits/zlr_trait_unknown_32";
        }

        /// <summary>取特质图标游戏资源 key（用于 path_icon + SpriteTextureLoader 注入 ID）。</summary>
        public static string GetTraitIconResourceKey(string traitId)
        {
            if (!string.IsNullOrEmpty(traitId) && TraitIcons.TryGetValue(traitId, out var key)) return "ui/zhulu/" + key;
            return "ui/Icons/iconCrown"; // 兜底原版
        }

        /// <summary>
        /// 运行时注入特质图标到游戏贴图缓存（SpriteTextureLoader.addSprite）。
        /// 注入 ID 与 path_icon 一致 → 游戏原生特质面板（Resources.Load 路径）也能显示 mod 自绘图标。
        /// 反编译依据：BaseUnlockableAsset.cached_sprite = SpriteTextureLoader.getSprite(path_icon)，
        /// getSprite 先查 _cached_sprites（addSprite 写入），命中即返回。
        /// </summary>
        private void InjectTraitIcons()
        {
            try
            {
                foreach (var kv in TraitIcons)
                {
                    try
                    {
                        string rel = kv.Value.Replace('/', System.IO.Path.DirectorySeparatorChar); // traits/T01_...
                        string file = System.IO.Path.Combine(ZhuluContext.Instance.ModPath, "GameResources", "ui", "zhulu", rel + ".png");
                        if (!File.Exists(file))
                        {
                            ZhuluContext.Instance.LogWarning($"[TraitManager] 特质图标文件缺失: {file}");
                            continue;
                        }
                        // 2026-10-23 [F8]：全量注入已注册过同名 key → 去重跳过，不再抛重复键异常
                        if (ZhuluRebuild.Assets.ZhuluSpriteInjector.TryInject("ui/zhulu/" + kv.Value, file))
                            ZhuluContext.Instance.LogInfo($"[TraitManager] 注入特质图标: {kv.Key} -> ui/zhulu/{kv.Value}");
                    }
                    catch (Exception ex)
                    {
                        ZhuluContext.Instance.LogWarning($"[TraitManager] 注入特质图标异常: {kv.Key}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[TraitManager] 特质图标注入整体异常", ex);
            }
        }

        private TraitManager() { }

        // ═══════════════════════ getSprite 覆盖补丁 ═══════════════════════

        /// <summary>
        /// 决定性方案：Harmony Postfix 覆盖 BaseUnlockableAsset.getSprite()。
        /// 反编译实锤 TraitButton.load → augmentation_asset.getSprite()（BaseUnlockableAsset 懒加载 cached_sprite）。
        /// 无论 SpriteTextureLoader 缓存时序如何，凡是我们 10 个特质一律返回自绘图标（ZhuluAssetLoader 直接读文件）。
        /// 顺带打印命中日志，方便验证。
        /// </summary>
        [HarmonyPatch(typeof(BaseUnlockableAsset), "getSprite")]
        public static class TraitIconOverridePatch
        {
            public static void Postfix(BaseUnlockableAsset __instance, ref Sprite __result)
            {
                try
                {
                    var trait = __instance as ActorTrait;
                    if (trait == null) return;
                    if (trait.id == null || !TraitIcons.TryGetValue(trait.id, out string key)) return; // 只覆盖逐鹿 10 特质
                    var custom = Assets.ZhuluAssetLoader.Instance.GetSprite(key);
                    if (custom != null)
                    {
                        __result = custom;
                        if (ZhuluContext.Instance.DebugLogging)
                        {
                            ZhuluContext.Instance.LogInfo($"[TraitManager] getSprite覆盖: {trait.id} -> {key}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    ZhuluContext.Instance.LogWarning($"[TraitManager] getSprite覆盖异常: {ex.Message}");
                }
            }
        }

        // ═══════════════════════ 注册 ═══════════════════════

        /// <summary>启动时注册所有逐鹿特质到游戏原生特质系统。</summary>
        public void RegisterAll()
        {
            if (_registered) return;
            _registered = true;

            try
            {
                // 先注入自绘图标到游戏贴图缓存（特质面板显示用）
                InjectTraitIcons();

                // 2026-08-20 逐鹿专属分组（特质编辑器独立一栏——先注册分组再注册特质）
                EnsureZhuluGroup();

                RegisterTrait(TRAIT_BENEVOLENT, "仁君", "治国有方，百姓安乐",
                    new Dictionary<string, float> { { "diplomacy", 5 }, { "stewardship", 3 } });
                RegisterTrait(TRAIT_WISE_MINISTER, "贤相", "才德兼备，辅政有方",
                    new Dictionary<string, float> { { "stewardship", 8 }, { "intelligence", 5 } });
                RegisterTrait(TRAIT_LOYAL, "忠臣", "忠心耿耿，矢志不渝",
                    new Dictionary<string, float> { { "diplomacy", 3 }, { "stewardship", 3 } });
                RegisterTrait(TRAIT_GENIUS, "天纵奇才", "天赋异禀，文武全才",
                    new Dictionary<string, float> { { "intelligence", 3 }, { "warfare", 3 }, { "diplomacy", 3 }, { "stewardship", 3 } });
                RegisterTrait(TRAIT_FOUNDER, "开国之主", "肇基立业，开创王朝",
                    new Dictionary<string, float> { { "warfare", 10 }, { "stewardship", 5 }, { "max_age", 15 } });
                RegisterTrait(TRAIT_TYRANT, "暴君", "穷兵黩武，民不聊生",
                    new Dictionary<string, float> { { "warfare", 5 }, { "diplomacy", -8 } });
                RegisterTrait(TRAIT_CORRUPT, "贪官", "中饱私囊，贪得无厌",
                    new Dictionary<string, float> { { "stewardship", -3 } });
                RegisterTrait(TRAIT_MEDIOCRE, "庸才", "尸位素餐，碌碌无为",
                    new Dictionary<string, float> { { "intelligence", -3 }, { "stewardship", -3 }, { "diplomacy", -3 }, { "warfare", -3 } });
                RegisterTrait(TRAIT_AMBITIOUS, "野心家", "心怀叵测，觊觎大位",
                    new Dictionary<string, float> { { "intelligence", 5 } });
                RegisterTrait(TRAIT_FOOLISH, "昏君", "荒淫无道，国库空虚",
                    new Dictionary<string, float> { { "stewardship", -5 }, { "intelligence", -3 } });
                // 阶段2A A1-8：前朝宗室（纯标识，无效果——效果预留：可加旧朝遗族相关事件判定）
                RegisterTrait(TRAIT_FORMER_ROYAL, "前朝宗室", "前朝王室遗族，历经改朝换代仍存于世",
                    new Dictionary<string, float>());

                // 2026-08-20 君主风评新增（与仁/昏/暴同链互斥）
                RegisterTrait(TRAIT_SAGE_RULER, "明君", "励精图治，礼乐昌明",
                    new Dictionary<string, float> { { "intelligence", 3 }, { "stewardship", 3 } });
                RegisterTrait(TRAIT_STEADY_RULER, "守成之主", "萧规曹随，垂拱而治",
                    new Dictionary<string, float>());

                // 2026-08-20 身份特质（纯标识——贴图默认王冠）
                RegisterTrait(TRAIT_CROWN_PRINCE, "太子", "天命储君，国之储贰",
                    new Dictionary<string, float>());
                RegisterTrait(TRAIT_HEIR_APPARENT, "世子", "诸侯储嗣，承嗣之家",
                    new Dictionary<string, float>());
                RegisterTrait(TRAIT_NOMAD_HEIR, "游牧继承人", "经部族认可的汗位继承人",
                    new Dictionary<string, float>());
                RegisterTrait(TRAIT_CITY_LORD, "城主", "一城之主，食邑一方",
                    new Dictionary<string, float>());
                RegisterTrait(TRAIT_FALLEN_KING, "亡国之君", "社稷倾覆，宗庙为墟",
                    new Dictionary<string, float> { { "diplomacy", -3 } });
                RegisterTrait(TRAIT_RESTORED_KING, "复国之君", "失国复起，中兴旧邦",
                    new Dictionary<string, float> { { "warfare", 5 }, { "diplomacy", 3 } });

                // 2026-08-20 官职特质（各席小加成——拜官任职之能）
                RegisterTrait("zlr_office_prime_minister", "丞相", "百官之首，总领朝纲",
                    new Dictionary<string, float> { { "stewardship", 3 } });
                RegisterTrait("zlr_office_personnel", "吏部尚书", "铨选百官，掌天下人事",
                    new Dictionary<string, float> { { "diplomacy", 2 } });
                RegisterTrait("zlr_office_revenue", "户部尚书", "度支国用，掌天下钱粮",
                    new Dictionary<string, float> { { "intelligence", 3 } });
                RegisterTrait("zlr_office_rites", "礼部尚书", "礼乐典章，掌天下仪制",
                    new Dictionary<string, float> { { "diplomacy", 3 } });
                RegisterTrait("zlr_office_military", "兵部尚书", "武略韬钤，掌天下兵马",
                    new Dictionary<string, float> { { "warfare", 3 } });
                RegisterTrait("zlr_office_justice", "刑部尚书", "明刑弼教，掌天下刑狱",
                    new Dictionary<string, float> { { "intelligence", 2 } });
                RegisterTrait("zlr_office_works", "工部尚书", "营缮营造，掌天下百工",
                    new Dictionary<string, float> { { "stewardship", 2 } });
                RegisterTrait("zlr_office_local", "地方长吏", "牧守一方，代王巡狩",
                    new Dictionary<string, float> { { "stewardship", 1 }, { "intelligence", 1 } });

                // 游牧官职：属性加成逐席复制东方对应官职，只更换身份名称。
                RegisterTrait("zlr_nomad_office_prime_minister", "大断事官", "总理汗庭政务，调停诸部",
                    new Dictionary<string, float> { { "stewardship", 3 } });
                RegisterTrait("zlr_nomad_office_personnel", "选贤官", "考察部众，选任各部首领",
                    new Dictionary<string, float> { { "diplomacy", 2 } });
                RegisterTrait("zlr_nomad_office_revenue", "大司牧", "清点牲畜人口，掌管汗庭财赋",
                    new Dictionary<string, float> { { "intelligence", 3 } });
                RegisterTrait("zlr_nomad_office_rites", "祭天官", "主持祭天盟誓与部族礼仪",
                    new Dictionary<string, float> { { "diplomacy", 3 } });
                RegisterTrait("zlr_nomad_office_military", "兵马大总管", "统领诸部兵马",
                    new Dictionary<string, float> { { "warfare", 3 } });
                RegisterTrait("zlr_nomad_office_justice", "断狱官", "裁断部族争讼",
                    new Dictionary<string, float> { { "intelligence", 2 } });
                RegisterTrait("zlr_nomad_office_works", "营造官", "营建营地城寨，调度工匠",
                    new Dictionary<string, float> { { "stewardship", 2 } });
                RegisterTrait("zlr_nomad_office_local", "部族管事", "管理一地部众与牧地",
                    new Dictionary<string, float> { { "stewardship", 1 }, { "intelligence", 1 } });

                // 阶段2B 包1：爵位身份特质（纯显示，效果预留注释；互斥链在下方 BuildZhuluExclusivity）
                RegisterTrait(TIER_TIANZI, "天子", "受命于天，君临天下", new Dictionary<string, float>());
                RegisterTrait(TIER_GONG, "公", "大国之君，威震一方", new Dictionary<string, float>());
                RegisterTrait(TIER_HOU, "侯", "诸侯之长，一方屏藩", new Dictionary<string, float>());
                RegisterTrait(TIER_BO, "伯", "一方伯爵，领有数城", new Dictionary<string, float>());
                RegisterTrait(TIER_ZI, "子", "子爵之君，偏安一隅", new Dictionary<string, float>());
                RegisterTrait(TIER_NAN, "邑", "邑国之君，城小民寡", new Dictionary<string, float>());

                RegisterTrait(NOMAD_WANGTING, "王庭", "受长生天眷，天可汗统御诸部", new Dictionary<string, float>());
                RegisterTrait(NOMAD_DAHANTING, "大汗庭", "强部拱卫，大汗会盟诸部", new Dictionary<string, float>());
                RegisterTrait(NOMAD_HANGUO, "汗国", "可汗统众，据地称雄", new Dictionary<string, float>());
                RegisterTrait(NOMAD_BUMENG, "部盟", "诸部会盟，共推盟主", new Dictionary<string, float>());
                RegisterTrait(NOMAD_DABU, "大部", "部众渐盛，统合邻部", new Dictionary<string, float>());
                RegisterTrait(NOMAD_BUZU, "部族", "一部之众，逐水草而居", new Dictionary<string, float>());

                // 阶段2E ⑦：功名特质（纯标识，无效果——科举录取标记）
                RegisterTrait(TRAIT_XIUCAI, "秀才", "县试及第，文墨可嘉", new Dictionary<string, float>());
                RegisterTrait(TRAIT_JUREN, "举人", "乡试中选，才学出众", new Dictionary<string, float>());
                RegisterTrait(TRAIT_JINSHI, "进士", "殿试登科，国之栋梁", new Dictionary<string, float>());
                RegisterTrait(TRAIT_ZHUANGYUAN, "状元", "殿试魁首，天下第一", new Dictionary<string, float>());
                RegisterTrait(TRAIT_WUXIUCAI, "武秀才", "武试及第，弓马娴熟", new Dictionary<string, float>());
                RegisterTrait(TRAIT_WUJUREN, "武举人", "武试中选，骁勇善战", new Dictionary<string, float>());
                RegisterTrait(TRAIT_WUJINSHI, "武进士", "武试登科，万人敌也", new Dictionary<string, float>());

                RegisterTrait(NOMAD_TRIBE_SCHOLAR, "部族贤士", "部族选拔，善理民事", new Dictionary<string, float>());
                RegisterTrait(NOMAD_LEAGUE_SCHOLAR, "部盟贤士", "诸部共举，才略出众", new Dictionary<string, float>());
                RegisterTrait(NOMAD_COURT_SCHOLAR, "汗庭贤臣", "汗庭选拔，辅弼可汗", new Dictionary<string, float>());
                RegisterTrait(NOMAD_FIRST_SCHOLAR, "王庭首贤", "诸贤之首，名冠王庭", new Dictionary<string, float>());
                RegisterTrait(NOMAD_WARRIOR, "勇士", "弓马娴熟，勇冠一部", new Dictionary<string, float>());
                RegisterTrait(NOMAD_CENTURION, "百夫长", "统率百骑，战功卓著", new Dictionary<string, float>());
                RegisterTrait(NOMAD_CHILIARCH, "千夫长", "统率千骑，为汗庭宿将", new Dictionary<string, float>());

                // 2026-08-20 全量互斥关系（爵位/官职/储君/风评/文武/亡国清理链）
                BuildZhuluExclusivity();

                ZhuluContext.Instance.LogInfo("[TraitManager] 逐鹿特质注册完成（逐鹿专属栏）");
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[TraitManager] 特质注册异常", ex);
            }
        }

        /// <summary>
        /// 2026-08-20 注册"逐鹿专属"特质分组到原版特质编辑器。
        /// 反编译依据：AugmentationsEditor.groupsBuilder 遍历 AssetManager.trait_groups.list
        /// 建组面板，再按每个特质的 group_id 归组（dict_groups[group_id]）——
        /// 分组缺失会 KeyError，故特质注册前必须先建组。
        /// name 即 locale key（BaseCategoryAsset.getLocaleID 返回 name）。
        /// </summary>
        private void EnsureZhuluGroup()
        {
            try
            {
                if (AssetManager.trait_groups.get(GROUP_ZHULU) != null) return;
                AssetManager.trait_groups.add(new ActorTraitGroupAsset
                {
                    id = GROUP_ZHULU,
                    name = "trait_group_zhulu", // locale key → 逐鹿专属
                    color = "#C8A24B"            // 组标题色（Toolbox.makeColor 解析 hex）
                });
                NeoModLoader.General.LM.AddToCurrentLocale("trait_group_zhulu", "逐鹿专属");
                ZhuluContext.Instance.LogInfo("[TraitManager] 特质编辑器「逐鹿专属」分组注册成功");
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[TraitManager] 逐鹿专属分组注册失败: {ex.Message}");
            }
        }

        private void RegisterTrait(string id, string name, string description, Dictionary<string, float> stats)
        {
            // 运行时特质必须同步注册本地化；仅 AssetManager.add 不会使用传入的 name/description。
            // 统一在这里注册，确保游牧等级、官职、选拔与继承身份不会显示内部英文 ID。
            try
            {
                NeoModLoader.General.LM.AddToCurrentLocale("trait_" + id, name);
                NeoModLoader.General.LM.AddToCurrentLocale("trait_" + id + "_info", description);
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[TraitManager] 特质本地化注册失败 {id}: {ex.Message}");
            }

            // 先注册特质（游戏内部会初始化 base_stats）
            var trait = AssetManager.traits.add(new ActorTrait
            {
                id = id,
                path_icon = GetTraitIconResourceKey(id),
                group_id = GROUP_ZHULU, // 2026-08-20：逐鹿专属栏（原 miscellaneous）
                spawn_random_trait_allowed = false, // 2026-08-20：逐鹿特质只随系统赋予，不入随机池
                needs_to_be_explored = false, // 运行时注册绕过 autoSetRarity——免探索解锁，编辑器直接可用
                rarity = Rarity.R0_Normal // 常规档边框（身份/风评非奇观）
            });

            // 注册后再写属性加成（此时 base_stats 已由游戏初始化）
            // 判空保护：0.51.2 部分环境 add() 返回的 base_stats 内部字典可能为空，
            // 属性效果已由 mod 自身逻辑（GetPolicyMultiplier/GetCorruptionMultiplier 等）兜底，写失败不影响功能
            TraitStats[id] = new Dictionary<string, float>(stats); // 副本存静态表（科举评分用）
            if (trait == null || trait.base_stats == null) return;
            foreach (var kv in stats)
            {
                try
                {
                    trait.base_stats[kv.Key] = kv.Value;
                }
                catch
                {
                    // 单个属性写入失败不中断其余特质注册
                }
            }
        }

        /// <summary>
        /// 2026-08-20 逐鹿特质全量互斥链（traits_to_remove：添加该特质时自动移除清单内特质）。
        /// 反编译依据：Actor.addTrait → pTrait.traits_to_remove != null 时 removeTraits（无条件移除）；
        /// 运行时注册晚于 linkAssets，故直接填 traits_to_remove 数组（不依赖 traits_to_remove_ids 解析）。
        /// ═══ 链规则 ═══
        /// ① 爵位（6）：其余 5 爵位 + 全部官职 + 城主 + 储君（君王不任职储）
        /// ② 官职（8）：其余 7 官职 + 城主（一人一职）
        /// ③ 城主：全部官职（食邑封地不任朝职）
        /// ④ 太子/世子：互相 + 全部官职 + 城主（储君专注国本）
        /// ⑤ 君主风评（5）：其余 4 风评（一人一时一风评）
        /// ⑥ 文武功名：文 4 ↔ 武 3（文武不同科）
        /// ⑦ 亡国之君：全部身份/风评/官员特质（灭国之君只留亡国之君；开国之主/功名/奇才/前朝宗室保留）
        /// ⑧ 复国之君：官职 + 城主 + 储君（重登大位清理旧身份；亡国之君保留配合）
        /// </summary>
        private void BuildZhuluExclusivity()
        {
            try
            {
                string[] tiers = Combine(new[] { TIER_TIANZI, TIER_GONG, TIER_HOU, TIER_BO, TIER_ZI, TIER_NAN }, NomadicTierService.AllTiers).ToArray();
                string[] civilExam = { TRAIT_XIUCAI, TRAIT_JUREN, TRAIT_JINSHI, TRAIT_ZHUANGYUAN,
                    NOMAD_TRIBE_SCHOLAR, NOMAD_LEAGUE_SCHOLAR, NOMAD_COURT_SCHOLAR, NOMAD_FIRST_SCHOLAR };
                string[] militaryExam = { TRAIT_WUXIUCAI, TRAIT_WUJUREN, TRAIT_WUJINSHI,
                    NOMAD_WARRIOR, NOMAD_CENTURION, NOMAD_CHILIARCH };
                string[] heirTraits = { TRAIT_CROWN_PRINCE, TRAIT_HEIR_APPARENT, TRAIT_NOMAD_HEIR };
                string[] officeVerdicts = { TRAIT_WISE_MINISTER, TRAIT_LOYAL, TRAIT_CORRUPT, TRAIT_MEDIOCRE, TRAIT_AMBITIOUS };

                // ① 爵位链：其余爵位 + 官职 + 城主 + 储君
                foreach (var t in tiers)
                {
                    SetRemoveList(t, Combine(Except(tiers, t), OfficeTraitIds, new[] { TRAIT_CITY_LORD }, heirTraits));
                }

                // ② 官职链：其余官职 + 城主
                foreach (var o in OfficeTraitIds)
                {
                    SetRemoveList(o, Combine(Except(OfficeTraitIds, o), new[] { TRAIT_CITY_LORD }));
                }

                // ③ 城主 → 全部官职
                SetRemoveList(TRAIT_CITY_LORD, OfficeTraitIds);

                // ④ 储君链：互相 + 官职 + 城主
                SetRemoveList(TRAIT_CROWN_PRINCE, Combine(new[] { TRAIT_HEIR_APPARENT, TRAIT_NOMAD_HEIR }, OfficeTraitIds, new[] { TRAIT_CITY_LORD }));
                SetRemoveList(TRAIT_HEIR_APPARENT, Combine(new[] { TRAIT_CROWN_PRINCE, TRAIT_NOMAD_HEIR }, OfficeTraitIds, new[] { TRAIT_CITY_LORD }));
                SetRemoveList(TRAIT_NOMAD_HEIR, Combine(new[] { TRAIT_CROWN_PRINCE, TRAIT_HEIR_APPARENT }, OfficeTraitIds, new[] { TRAIT_CITY_LORD }));

                // ⑤ 君主风评链：其余 4 风评
                foreach (var v in RulerVerdictTraits)
                {
                    SetRemoveList(v, Except(RulerVerdictTraits, v));
                }

                // ⑥ 文武功名互斥
                foreach (var c in civilExam) SetRemoveList(c, militaryExam);
                foreach (var m in militaryExam) SetRemoveList(m, civilExam);

                // ⑦ 亡国之君：只留下亡国之君（爵位/官职/城主/储君/风评/官员风评全清）
                SetRemoveList(TRAIT_FALLEN_KING,
                    Combine(tiers, OfficeTraitIds, new[] { TRAIT_CITY_LORD }, heirTraits, RulerVerdictTraits, officeVerdicts));

                // ⑧ 复国之君：官职 + 城主 + 储君（重登大位）
                SetRemoveList(TRAIT_RESTORED_KING, Combine(OfficeTraitIds, new[] { TRAIT_CITY_LORD }, heirTraits));

                ZhuluContext.Instance.LogInfo("[TraitManager] 逐鹿特质互斥链已设置（爵位/官职/储君/风评/文武/亡国）");
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[TraitManager] 互斥链设置失败: {ex.Message}");
            }
        }

        /// <summary>给特质设置 traits_to_remove（跳过未注册/自身；直接填数组——运行时注册绕过 linkAssets 解析）。</summary>
        private static void SetRemoveList(string traitId, IEnumerable<string> removeIds)
        {
            try
            {
                ActorTrait trait = AssetManager.traits.get(traitId);
                if (trait == null) return;
                var remove = new List<ActorTrait>();
                foreach (var rid in removeIds)
                {
                    if (rid == traitId) continue;
                    var rt = AssetManager.traits.get(rid);
                    if (rt != null) remove.Add(rt);
                }
                trait.traits_to_remove = remove.ToArray();
            }
            catch { }
        }

        /// <summary>拼接多个 id 数组。</summary>
        private static IEnumerable<string> Combine(params IEnumerable<string>[] lists)
        {
            foreach (var list in lists)
                foreach (var id in list)
                    yield return id;
        }

        /// <summary>数组排除指定元素。</summary>
        private static IEnumerable<string> Except(string[] arr, string exclude)
        {
            foreach (var id in arr)
                if (id != exclude) yield return id;
        }

        // ═══════════════════════ 退位清理（2026-10-06 D批bug修复）═══════════════════════
        // 用户实锤：退位皇帝仍带皇帝/风评特质。根因：TickKingTraits 只扫在位君主，
        // 禅让/篡位/政变换君后旧君脱离年度扫描，风评五特质与爵位特质永久残留。

        /// <summary>在位专属特质：君主风评五特质 + 东方天子/五等爵 + 游牧六级（失去君位即应移除）。</summary>
        public static readonly string[] ReignOnlyTraitIds = BuildReignOnlyTraitIds();

        private static string[] BuildReignOnlyTraitIds()
        {
            var list = new List<string>(RulerVerdictTraits)
            {
                TIER_TIANZI, TIER_GONG, TIER_HOU, TIER_BO, TIER_ZI, TIER_NAN
            };
            list.AddRange(NomadicTierService.AllTiers);
            return list.ToArray();
        }

        /// <summary>
        /// 退位/被废旧君清理：移除在位专属特质（风评/爵位/游牧等级）。
        /// 由 KingTraitPatch.setKing 钩子即时调用，OnKingdomYear 年度扫描兜底（修复历史存档残留）。
        /// 开国之主/亡国之君/功名/天纵奇才为终身或独立特质，不在清理范围。
        /// </summary>
        public static void ClearReignOnlyTraits(Actor exKing)
        {
            if (exKing == null || !exKing.isAlive()) return;
            foreach (var tid in ReignOnlyTraitIds)
            {
                try { if (exKing.hasTrait(tid)) exKing.removeTrait(tid); } catch { }
            }
        }

        /// <summary>年度兜底：非在位单位持有在位专属特质即清除（历史档已退位旧君不经 setKing 钩子）。</summary>
        private void TickExKingSweep(Kingdom k)
        {
            try
            {
                if (k?.units == null) return;
                foreach (var u in k.units)
                {
                    if (u == null || !u.isAlive() || u.isKing()) continue;
                    ClearReignOnlyTraits(u);
                }
            }
            catch { }
        }

        // ═══════════════════════ 年度扫描 ═══════════════════════

        /// <summary>按国年度处理（由 YearDriver 每国每年调用一次）。</summary>
        public void OnKingdomYear(Kingdom k, int year)
        {
            if (k == null || !k.isAlive()) return;
            string kId = k.id.ToString();

            if (k.king != null && k.king.isAlive())
                TickKingTraits(k, kId, k.king, year);

            // 2026-10-06 退位残留自愈：历史档已退位旧君脱离在位扫描，年度兜底清理
            TickExKingSweep(k);

            // 2026-08-20 身份特质同步（储君/官职/城主——units 单次遍历）
            TickIdentityTraits(k, kId);

            TickOfficialVerdicts(k, kId, year);

            // 天纵奇才：每5年全图扫一次
            if (year % 5 == 0)
                TickGeniusTrait();
        }

        private void TickKingTraits(Kingdom k, string kId, Actor king, int year)
        {
            float stability = StabilityManager.Instance.GetStability(kId);
            var report = TreasuryManager.Instance.GetReport(kId);
            bool isFounder = KingdomExt.GetDynastyAge(kId, year) <= 1;
            int liZhi = 0;
            try { liZhi = ZhuluLiZhiService.GetLiZhiScore(kId); } catch { }

            // ═══ 2026-08-20 君主风评强化（多维度综合——一人一时一风评，互斥链自动清其他）═══
            // 暴君：稳定<25（苛政汹汹，民不聊生）
            // 昏君：连亏≥3年 且 稳定<45（荒于政事，国库空虚）
            // 仁君：稳定≥75 且 财政盈余（治国有方，百姓安乐）
            // 明君：稳定≥60 且 礼治≥15（励精图治，礼乐昌明）
            // 守成之主：稳定 40-75 且 无连亏（萧规曹随，垂拱而治）
            bool deficit = report.DeficitYears >= 3;
            bool surplus = report.DeficitYears == 0 && report.Balance > 0;
            string verdict = null;
            if (stability < 25f) verdict = TRAIT_TYRANT;
            else if (deficit && stability < 45f) verdict = TRAIT_FOOLISH;
            else if (stability >= 75f && surplus) verdict = TRAIT_BENEVOLENT;
            else if (stability >= 60f && liZhi >= 15) verdict = TRAIT_SAGE_RULER;
            else if (stability >= 40f && !deficit) verdict = TRAIT_STEADY_RULER;

            foreach (var t in RulerVerdictTraits)
                SetTraitByCondition(king, t, t == verdict);

            // 开国之主：建国第一年（终身特质）
            if (isFounder && !king.hasTrait(TRAIT_FOUNDER))
            {
                king.addTrait(TRAIT_FOUNDER);
                ZhuluContext.Instance.LogInfo($"[TraitManager] 赋予【开国之主】: {king.getName()}（{k.name}）");
            }

            // 2026-08-20 复国之君：亡国之君再登王位（两者共存——失国之耻与中兴之荣）
            if (king.hasTrait(TRAIT_FALLEN_KING) && !king.hasTrait(TRAIT_RESTORED_KING))
            {
                try
                {
                    if (king.addTrait(TRAIT_RESTORED_KING))
                    {
                        ChronicleManager.Instance.AddEntry(year, ChronicleEventType.KingChange, kId,
                            k.name ?? "", king.getName() ?? "",
                            $"<color={UI.ZhuluUIStyle.Hex.Imperial}>复国之君</color>：{king.getName()}曾失社稷，今重登大位，中兴旧邦！", true);
                        ZhuluContext.Instance.LogInfo($"[TraitManager] 复国之君: {king.getName()}（{k.name}）");
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// 2026-08-20 身份特质年度同步：储君（太子/世子）+ 官职 + 城主。
        /// units 单次遍历（每人对特质清单做条件同步——数据源为官职记录/储君/城 leader 快照）。
        /// </summary>
        private void TickIdentityTraits(Kingdom k, string kId)
        {
            try
            {
                // ── 数据快照 ──
                string heirId = null;
                try { heirId = SuccessionManager.Instance.GetHeirId(kId); } catch { }
                bool isMandate = false;
                try { isMandate = PolicyManager.IsMandateHolderForKingdom(k); } catch { }
                string kingId = null;
                try { kingId = k.king?.getID().ToString(); } catch { }

                var officeByPerson = new Dictionary<string, string>();
                try
                {
                    var records = OfficeManager.Instance.GetRecordsPublic(kId);
                    if (records != null)
                        foreach (var r in records)
                            if (!string.IsNullOrEmpty(r?.PersonId)) officeByPerson[r.PersonId] = r.OfficeId;
                }
                catch { }

                var lordIds = new HashSet<string>();
                try
                {
                    if (k.cities != null)
                        foreach (var c in k.cities)
                        {
                            if (c?.leader == null) continue;
                            lordIds.Add(c.leader.getID().ToString());
                        }
                }
                catch { }

                if (k.units == null) return;
                foreach (Actor u in k.units)
                {
                    if (u == null || !u.isAlive()) continue;
                    try
                    {
                        string uid = u.getID().ToString();
                        bool isKing = uid == kingId;

                        // ① 储君特质（国王除外——继位即除储）
                        bool isHeir = !isKing && uid == heirId;
                        bool nomadic = KingdomCivilizationService.Instance.IsNomadic(k);
                        SetTraitByCondition(u, TRAIT_CROWN_PRINCE, isHeir && isMandate && !nomadic);
                        SetTraitByCondition(u, TRAIT_HEIR_APPARENT, isHeir && !isMandate && !nomadic);
                        SetTraitByCondition(u, TRAIT_NOMAD_HEIR, isHeir && nomadic);

                        // ② 官职特质（国王不任职）
                        if (isKing)
                        {
                            RemoveOfficeTraits(u);
                        }
                        else
                        {
                            string myOffice = officeByPerson.TryGetValue(uid, out var o) ? o : null;
                            string wantTrait = myOffice != null ? GetOfficeTraitId(myOffice, k) : null;
                            foreach (var tid in OfficeTraitIds)
                                SetTraitByCondition(u, tid, tid == wantTrait);
                        }

                        // ③ 城主特质（国王除外）
                        SetTraitByCondition(u, TRAIT_CITY_LORD, !isKing && lordIds.Contains(uid));
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[TraitManager] 身份特质同步异常: {ex.Message}");
            }
        }

        /// <summary>移除一人的全部官职特质（国王/卸任兜底）。</summary>
        private static void RemoveOfficeTraits(Actor actor)
        {
            foreach (var tid in OfficeTraitIds)
            {
                try { if (actor.hasTrait(tid)) actor.removeTrait(tid); } catch { }
            }
        }

        /// <summary>官员风评年度判定（贤相/忠臣/贪官/庸才/野心家——遍历官职记录，人数少）。</summary>
        private void TickOfficialVerdicts(Kingdom k, string kId, int year)
        {
            var records = OfficeManager.Instance.GetRecordsPublic(kId);
            if (records == null || records.Count == 0) return;

            // D3 评分分布插桩（2026-10-06）：收集本国在任官员考评分，循环末输出一行汇总
            var scoreList = new System.Collections.Generic.List<int>();

            foreach (var rec in records)
            {
                if (string.IsNullOrEmpty(rec.PersonId)) continue;
                Actor official = FindActorInKingdom(k, rec.PersonId);
                if (official == null || !official.isAlive()) continue;

                // 如果是国王本人，跳过（国王特质已单独处理）
                if (k.king != null && official.getID() == k.king.getID()) continue;

                float corruption = CorruptionManager.Instance.GetCorruption(kId, rec.OfficeId);
                float power = CorruptionManager.Instance.GetPower(kId, rec.OfficeId);

                // 计算考评分
                int score = 0;
                try
                {
                    var (s, _) = OfficialRatingManager.CalcOfficialScore(official, OfficeManager.GetOfficeIndex(rec.OfficeId), 0); // 阶段2B 方案3
                    score = s;
                    scoreList.Add(s);
                }
                catch { }

                // 贤相：丞相且考评>=85
                bool isPrimeMinister = rec.OfficeId == "prime_minister";
                SetTraitByCondition(official, TRAIT_WISE_MINISTER, isPrimeMinister && score >= 85);

                // 忠臣：腐败<10
                SetTraitByCondition(official, TRAIT_LOYAL, corruption < 10f);

                // 贪官：腐败>=60
                SetTraitByCondition(official, TRAIT_CORRUPT, corruption >= 60f);

                // 庸才：考评<30
                SetTraitByCondition(official, TRAIT_MEDIOCRE, score < 30 && score > 0);

            // 野心家：权势>=65且非国王
            SetTraitByCondition(official, TRAIT_AMBITIOUS, power >= 65f); // 阶段2U：80→65 对齐权势增速
        }

        // D3 评分分布插桩（2026-10-06）：一行/国/年，对照 v6 定稿目标（mean≈40 / B+≈6% / S 极罕见）
        try
        {
            int n0 = scoreList.Count;
            if (n0 > 0)
            {
                var sorted = new System.Collections.Generic.List<int>(scoreList);
                sorted.Sort((a, b) => a.CompareTo(b));
                double sum0 = 0;
                foreach (int s0 in sorted) sum0 += s0;
                int p50 = sorted[Math.Min(n0 - 1, 50 * n0 / 100)];
                int p90 = sorted[Math.Min(n0 - 1, 90 * n0 / 100)];
                int p99 = sorted[Math.Min(n0 - 1, 99 * n0 / 100)];
                int sN = 0, aN = 0, bN = 0, cN = 0, dN = 0, fN = 0;
                foreach (int s0 in sorted)
                {
                    if (s0 >= 85) sN++;
                    else if (s0 >= 70) aN++;
                    else if (s0 >= 55) bN++;
                    else if (s0 >= 50) cN++;
                    else if (s0 >= 35) dN++;
                    else fN++;
                }
                ZhuluTrace.Write("rating_trace.csv",
                    $"rating_year,{year},{kId},{n0},{(sum0 / n0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)},{p50},{p90},{p99},{sN},{aN},{bN},{cN},{dN},{fN}");
            }
        }
        catch { }
    }

        // ═══════════════════════ 全局年度（亡国之君检测） ═══════════════════════

        /// <summary>上年度各国国王快照（kId → kingActorId；灭国检测基线）。</summary>
        private readonly Dictionary<string, long> _lastKings = new Dictionary<string, long>();
        private int _lastFallenScanYear = -1;

        /// <summary>
        /// 2026-08-20 全局年度：亡国之君检测。
        /// 上年度在册王国本年度灭亡（isAlive=false）→ 最后的国王获【亡国之君】
        /// （traits_to_remove 自动清爵位/官职/城主/储君/风评——只留下亡国之君）。
        /// 读档/新世界后首年重建基线（不误判）。
        /// </summary>
        public void OnGlobalYear(int year)
        {
            if (year == _lastFallenScanYear) return; // 同年幂等
            _lastFallenScanYear = year;
            try
            {
                var currentKings = new Dictionary<string, long>();
                if (World.world?.kingdoms?.list != null)
                {
                    foreach (Kingdom k in World.world.kingdoms.list)
                    {
                        try
                        {
                            if (k == null || !k.isAlive() || k.king == null) continue;
                            currentKings[k.id.ToString()] = k.king.getID();
                        }
                        catch { }
                    }
                }

                // 灭国检测：上年在册、本年不在 → 亡国之君
                foreach (var kv in _lastKings)
                {
                    if (currentKings.ContainsKey(kv.Key)) continue;
                    try
                    {
                        var lastKing = World.world?.units?.get(kv.Value);
                        if (lastKing == null || !lastKing.isAlive()) continue; // 与国同殉者无特质
                        if (lastKing.hasTrait(TRAIT_FALLEN_KING)) continue;
                        if (lastKing.addTrait(TRAIT_FALLEN_KING))
                        {
                            string name = lastKing.getName() ?? "末代之君";
                            // 阶段3H-10：灭亡者清单写入 Details（攻方史册回溯"破敌天命/覆灭王庭"评语的数据源）
                            string killers = BuildFallenByList(kv.Key, year);
                            string killerNote = string.IsNullOrEmpty(killers) ? "" : "\n<color=#8a8578>亡于：" + killers + "</color>";
                            ChronicleManager.Instance.AddEntry(year, ChronicleEventType.KingdomFallen, kv.Key,
                                name, name,
                                $"<color=#FF4444>亡国之君</color>：社稷倾覆，{name}沦为亡国之君{killerNote}", true);
                            ZhuluContext.Instance.LogInfo($"[TraitManager] 亡国之君: {name}（王国 {kv.Key} 覆灭）");
                        }
                    }
                    catch { }
                }

                // 重建基线
                _lastKings.Clear();
                foreach (var kv in currentKings) _lastKings[kv.Key] = kv.Value;
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[TraitManager] 亡国之君检测异常: {ex.Message}");
            }
        }

        /// <summary>阶段3H-10：灭亡某国的交战对方清单（王国当年 wars 里对手存活者；无战争记录则空）。</summary>
        private static string BuildFallenByList(string fallenKingdomId, int year)
        {
            try
            {
                var parts = new List<string>();
                var fallen = KingdomDataExt.FindKingdomById(fallenKingdomId);
                if (fallen != null)
                {
                    var wars = fallen.getWars();
                    if (wars != null)
                    {
                        foreach (var w in wars)
                        {
                            try
                            {
                                if (w == null) continue;
                                var aK = w.getMainAttacker();
                                var dK = w.getMainDefender();
                                var other = aK != null && aK.id.ToString() == fallenKingdomId ? dK
                                    : dK != null && dK.id.ToString() == fallenKingdomId ? aK
                                    : null;
                                if (other == null || !other.isAlive()) continue;
                                string on = other.name ?? "";
                                if (!string.IsNullOrEmpty(on) && !parts.Contains(on)) parts.Add(on);
                            }
                            catch { }
                        }
                    }
                }
                return parts.Count > 0 ? string.Join("、", parts) : "";
            }
            catch { return ""; }
        }

        /// <summary>读档/新世界：清空亡国检测基线（防跨存档误判）。</summary>
        public void ResetFallenBaseline()
        {
            _lastKings.Clear();
            _lastFallenScanYear = -1;
        }

        private void TickGeniusTrait()
        {
            try
            {
                if (World.world?.kingdoms?.list == null) return;
                foreach (Kingdom k in World.world.kingdoms.list)
                {
                    if (k == null || !k.isAlive() || k.units == null) continue;
                    foreach (Actor unit in k.units)
                    {
                        if (unit == null || !unit.isAlive()) continue;
                        // 阶段1 A4：天纵奇才改与考课同源判定（原 total>=280 理论极限约80永不可达）
                        int geniusScore = 0;
                        try
                        {
                            int gAge = 0;
                            try { gAge = unit.getAge(); } catch { }
                            geniusScore = OfficialRatingManager.CalcOfficialScore(unit, 6, gAge).score; // 阶段2B 方案3
                        }
                        catch { continue; }
                        SetTraitByCondition(unit, TRAIT_GENIUS, geniusScore >= 90);
                    }
                }
            }
            catch { }
        }

        // ═══════════════════════ 查询接口（供其他系统使用） ═══════════════════════

        /// <summary>查询某角色是否有指定特质。</summary>
        public bool HasTrait(Actor actor, string traitId)
        {
            if (actor == null) return false;
            try { return actor.hasTrait(traitId); } catch { return false; }
        }

        /// <summary>返回政策效果倍率（贤相1.15 / 庸才0.7 / 默认1.0）。</summary>
        public float GetPolicyMultiplier(Actor actor)
        {
            if (actor == null) return 1.0f;
            try
            {
                if (actor.hasTrait(TRAIT_WISE_MINISTER)) return 1.15f;
                if (actor.hasTrait(TRAIT_MEDIOCRE)) return 0.7f;
            }
            catch { }
            return 1.0f;
        }

        /// <summary>返回腐败速度倍率（贪官1.5 / 默认1.0）。</summary>
        public float GetCorruptionMultiplier(Actor actor)
        {
            if (actor == null) return 1.0f;
            try
            {
                if (actor.hasTrait(TRAIT_CORRUPT)) return 1.5f;
            }
            catch { }
            return 1.0f;
        }

        /// <summary>返回稳定度目标加成（仁君+5 / 暴君-10 / 昏君-5 / 默认0）。</summary>
        public float GetStabilityBonus(Actor king)
        {
            if (king == null) return 0f;
            float bonus = 0f;
            try
            {
                if (king.hasTrait(TRAIT_BENEVOLENT)) bonus += 5f;
                if (king.hasTrait(TRAIT_TYRANT)) bonus -= 10f;
                if (king.hasTrait(TRAIT_FOOLISH)) bonus -= 5f;
            }
            catch { }
            return bonus;
        }

        /// <summary>返回考评额外加分（天纵奇才+10 / 默认0）。</summary>
        public int GetRatingBonus(Actor actor)
        {
            if (actor == null) return 0;
            try
            {
                if (actor.hasTrait(TRAIT_GENIUS)) return 10;
            }
            catch { }
            return 0;
        }

        // ═══════════════════════ 内部辅助 ═══════════════════════

        private void SetTraitByCondition(Actor actor, string traitId, bool shouldHave)
        {
            try
            {
                bool has = actor.hasTrait(traitId);
                if (shouldHave && !has)
                {
                    actor.addTrait(traitId);
                }
                else if (!shouldHave && has)
                {
                    actor.removeTrait(traitId);
                }
            }
            catch { }
        }

        private Actor FindActorInKingdom(Kingdom k, string actorId)
        {
            if (k?.units == null || string.IsNullOrEmpty(actorId)) return null;
            try
            {
                foreach (Actor unit in k.units)
                {
                    if (unit == null || !unit.isAlive()) continue;
                    if (unit.getID().ToString() == actorId) return unit;
                }
            }
            catch { }
            return null;
        }
    }
}
