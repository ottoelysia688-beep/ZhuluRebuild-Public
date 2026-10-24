using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using ZhuluRebuild.Adapters;
using ZhuluRebuild.Core;
using ZhuluRebuild.Domain;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Naming
{
    /// <summary>
    /// 逐鹿内置中文命名服务（性能优化版）。
    /// 热路径只做 O(1) 字典查找，反射全部缓存，Rehydrate 只跑一次。
    /// </summary>
    public sealed partial class ZhuluChineseNameService
    {
        public static ZhuluChineseNameService Instance { get; } = new ZhuluChineseNameService();

        private const string ActorNameKey = "zlr_cn_authoritative_name";
        private const string ActorSurnameKey = "zlr_cn_family_name";
        private const string SharedFamilyNameKey = "family_name";
        private const string ActorManualNameKey = "zlr_cn_manual_name";
        private const string ClanSurnameKey = "zlr_cn_clan_surname";
        private const string FamilySurnameKey = "zlr_cn_household_surname";
        private const string PersistentNameKey = "zlr_cn_authoritative_name";
        private const string KingdomBaseNameKey = "cn_base_name";

        // ── 游牧词池（阶段3C：蒙古/突厥/契丹/女真四系全覆盖，间隔号人名制）──
        private static readonly string[] NomadicSurnames =
        {
            "耶律", "孛儿只斤", "阿史那", "拓跋", "慕容", "宇文", "乞颜", "泰赤乌", "弘吉剌", "札答阑",
            "蔑儿乞", "塔塔尔", "克烈", "乃蛮", "汪古", "铁勒", "回纥", "薛延陀", "契苾", "贺兰",
            "独孤", "尉迟", "斛律", "仆固", "同罗", "都播", "骨利干", "黠戛斯", "处月", "处密",
            "突骑施", "葛逻禄", "拔野古", "浑", "奚", "室韦", "靺鞨", "完颜", "纥石烈", "徒单",
            "乌林答", "蒲察", "纳兰", "叶赫", "钮祜禄", "富察", "赫舍里", "兆佳", "郭尔罗斯", "扎赉特",
            "杜尔伯特", "鄂尔多斯", "土尔扈特", "和硕特", "准噶尔", "喀尔喀", "察哈尔", "乌梁海", "布里亚特", "巴尔虎",
            "科尔沁", "札萨克图", "苏尼特", "乌珠穆沁", "浩齐特", "奈曼", "敖汉", "巴林", "扎鲁特", "阿霸垓"
        };
        private static readonly string[] NomadicMaleGiven =
        {
            "铁木真", "旭烈兀", "忽必烈", "拖雷", "窝阔台", "察合台", "术赤", "蒙哥", "贵由", "拔都",
            "别儿哥", "海都", "忽都", "也孙", "阿速台", "昔班", "别勒古台", "合撒儿", "帖木格", "帖木儿",
            "脱脱", "脱火赤", "月思别", "俺答", "也先", "也先不花", "马哈木", "阿鲁台", "巴图蒙克", "达延汗",
            "土门", "室点密", "颉利", "启民", "始毕", "处罗", "咄苾", "骨力裴罗", "磨延啜", "药罗葛",
            "大石", "隆绪", "宗真", "洪基", "延禧", "乙辛", "仁先", "习泥烈", "古欲", "敖鲁斡",
            "巴特尔", "特木尔", "朝鲁", "那顺", "巴根", "苏和", "敖登", "阿古拉", "孟和", "巴音",
            "朝克图", "呼日勒", "那日苏", "额尔德尼", "占布拉", "道尔吉", "巴雅尔", "胡日查", "阿尔斯楞", "温都苏",
            "宝音", "甘珠尔", "丹毕", "嘎尔迪", "明安", "多尔济", "策伯克", "车凌", "色布腾", "刚噶"
        };
        private static readonly string[] NomadicFemaleGiven =
        {
            "孛儿帖", "也遂", "也速干", "唆鲁禾帖尼", "满都海", "阿剌海", "益里海雅", "合答安", "豁阿", "阿勒塔",
            "莎儿合", "别帖", "也里可", "火真", "阿必合", "忽答罕", "满都海斯琴", "其其格", "萨仁", "高娃",
            "娜仁", "乌云", "哈斯", "塔娜", "珊丹", "格日乐", "乌尤", "其木格", "阿茹娜", "乌仁",
            "塔木嘎", "昂嘎", "银花", "萨茹拉", "乌吉斯", "敖特罕", "玛西", "乌日娜", "阿迪亚", "额尔敦",
            "淑伦", "呼伦", "哈斯其其格", "乌云其其格", "萨仁高娃", "娜仁花", "其木德斯", "乌仁图雅", "萨仁花", "阿拉坦花",
            "金花", "梅花", "托娅", "乌罕", "塔娜花", "昂噶其其格", "哈斯高娃", "苏伦嘎", "乌兰其其格", "诺恩吉雅"
        };
        private static readonly string[] NomadicRealmNames =
        {
            "苍狼", "白鹿", "金帐", "白帐", "蓝帐", "黑纛", "朔风", "天山", "瀚海", "鹰扬",
            "铁勒", "九姓", "北庭", "龙城", "阴山", "河套", "漠北", "草海", "狼居", "祁连",
            "贺兰", "金山", "燕然", "弹汗", "柔然", "高车", "敕勒", "契丹", "室韦", "鞑靼",
            "瓦剌", "兀良哈", "斡难", "克鲁伦", "土拉", "鄂尔浑", "于都斤", "乌德鞬", "白霫", "契骨",
            "回鹘", "葛逻禄", "突骑施", "拔悉密"
        };
        private static readonly string[] NomadicSettlementNames =
        {
            "阿力麻里", "窝鲁朵八里", "怛逻斯", "别失八里", "哈拉和林", "兀鲁思八里", "撒马尔罕", "布哈拉", "忽毡", "讹答剌",
            "八剌沙衮", "碎叶", "热海", "裴罗城", "可敦城", "单于城", "郅居城", "窳浑", "浚稽山", "范夫人城",
            "斡难城", "克鲁伦城", "土拉城", "鄂尔浑城", "于都斤城", "乌德鞬城", "巴里坤城", "博格达城", "裕勒都斯城", "和林",
            "上都", "应昌", "兀剌海城", "拔野古城", "同罗马城", "白霫城", "居延海城", "狼居胥城", "燕然都护", "金山城"
        };
        private static readonly string[] NomadicEraNames =
        {
            "狼纛", "苍穹", "长生", "腾格里", "苏鲁锭", "白骏", "金戈", "大漠", "瀚海", "燕然",
            "于都斤", "斡难", "克鲁伦", "敕勒川", "白狼", "苍鹰", "天狼", "雪原", "风暴", "金帐",
            "龙城", "狼居胥", "金微", "阴山", "居延", "漠北", "铁勒", "九姓", "碎叶", "热海",
            "金山", "和林", "忽剌", "也克", "扎撒", "兀鲁思", "怯绿", "谷蠡", "屠耆", "单于",
            "白登", "漠南", "鹰旗", "马嘶"
        };
        private static readonly string[] NomadicCultureNames =
        {
            "苍狼部俗", "白鹿牧歌", "金帐汗俗", "白帐穹庐", "蓝帐游风", "长生天祭统", "敖包祭祀", "苏鲁锭军俗",
            "瀚海孤烟", "大漠牧歌", "天山雪部", "燕然勒俗", "狼纛武风", "铁勒九姓", "敕勒川歌", "克鲁伦牧歌",
            "斡难源俗", "于都斤祭地", "库里尔台风", "毡帐部俗", "马镫骑风", "走马牧俗", "接羔春俗", "那达慕欢俗"
        };
        private static readonly string[] NomadicLanguageNames =
        {
            "可汗语", "汗庭语", "穹庐语", "毡帐语", "马背语", "漠北语", "金山语", "苍狼语",
            "回鹘语", "敕勒语", "铁勒语", "室韦语", "克烈语", "乃蛮语", "斡难语", "瀚海语",
            "叶护语", "于都斤语"
        };
        private static readonly string[] NomadicReligionNames =
        {
            "长生天信仰", "腾格里大祭", "苍天祭祀", "祖灵信仰", "萨满教", "敖包祭祀", "苍狼祖灵", "白鹿图腾",
            "苏鲁锭战神祭", "火神祭祀", "狼神崇拜", "天鹅图腾", "雄鹰祭", "白马献祭", "金帐巫统", "汗庭萨满",
            "突厥狼祖祭", "敕勒天祭", "于都斤神山祭", "祖毡祭祀", "腾格里永生祭", "回鹘摩尼教"
        };
        private static readonly string[] NomadicSubspeciesNames =
        {
            "苍狼裔", "白鹿裔", "金帐裔", "白帐裔", "蓝帐裔", "苍穹裔", "大漠裔", "瀚海裔",
            "天山裔", "燕然裔", "阴山裔", "贺兰裔", "狼纛裔", "铁勒裔", "敕勒裔", "突厥裔",
            "回鹘裔", "黠戛斯裔", "克烈裔", "乃蛮裔", "室韦裔", "斡难裔", "克鲁伦裔", "于都斤裔"
        };
        private static readonly string[] NomadicFamilyHalls =
        {
            "金帐宗", "白帐宗", "苍狼部", "白鹿部", "狼纛帐", "苏鲁锭帐", "敖包祠", "祖毡帐",
            "穹庐帐", "大汗帐", "斡耳朵帐", "那颜帐", "巴特尔帐", "雄鹰帐", "白马帐", "火神帐",
            "长生天帐", "瀚海帐"
        };

        private readonly Dictionary<Actor, string> _actorNames = new Dictionary<Actor, string>();
        private readonly Dictionary<Kingdom, string> _kingdomBaseNames = new Dictionary<Kingdom, string>();
        private readonly Dictionary<Kingdom, string> _kingdomObservedNames = new Dictionary<Kingdom, string>();
        private readonly Dictionary<City, string> _cityNames = new Dictionary<City, string>();
        private readonly Dictionary<Clan, string> _clanNames = new Dictionary<Clan, string>();
        private readonly Dictionary<object, string> _familyNames = new Dictionary<object, string>();
        private readonly Dictionary<War, string> _warNames = new Dictionary<War, string>();
        private readonly Dictionary<Alliance, string> _allianceNames = new Dictionary<Alliance, string>();
        private readonly Dictionary<Culture, string> _cultureNames = new Dictionary<Culture, string>();
        private readonly Dictionary<Language, string> _languageNames = new Dictionary<Language, string>();
        private readonly Dictionary<Religion, string> _religionNames = new Dictionary<Religion, string>();

        private readonly HashSet<string> _sessionCityNames = new HashSet<string>();
        private readonly HashSet<string> _sessionCityAliasKeys = new HashSet<string>();
        private readonly HashSet<string> _sessionRealmNames = new HashSet<string>();
        private readonly HashSet<string> _sessionClanNames = new HashSet<string>();
        private readonly HashSet<string> _sessionWarNames = new HashSet<string>();
        // 2026-08-19 战名丰富：两国交兵计数（序战命名——齐燕再战/三战…）
        private readonly Dictionary<string, int> _warPairCount = new Dictionary<string, int>();
        // 阶段2N W7：人名去重（"樂卫"50+重名根修）——全图已用人名集合，生成时查重；只防新增重名，存量不迁移
        private readonly HashSet<string> _usedPersonNames = new HashSet<string>();
        private bool _personNamesSeeded;

        private ZhuluChineseNameConfig _config;
        private bool _loaded;
        private MapBox _world;
        private bool _rehydrated;
        private int _tickCounter;
        // P1-7（2026-08-23）TickAuthority 王国名同步轮转相位：每 tick 只处理 idx%3==phase 的条目
        private int _syncPhase;
        // P1-7（2026-08-23）姓氏首/尾字索引：ExtractSurname 从 O(431) 全池扫描降为 O(命中候选)
        // 懒构建一次；桶内保持池原顺序，跨条件取最小池序号——与旧线性扫描的"首个命中"语义完全一致
        private readonly Dictionary<char, List<SurnameEntry>> _compoundByFirst = new Dictionary<char, List<SurnameEntry>>();
        private readonly Dictionary<char, List<SurnameEntry>> _compoundByLast = new Dictionary<char, List<SurnameEntry>>();
        private readonly Dictionary<char, List<SurnameEntry>> _singleByFirst = new Dictionary<char, List<SurnameEntry>>();
        private readonly Dictionary<char, List<SurnameEntry>> _singleByLast = new Dictionary<char, List<SurnameEntry>>();
        private bool _surnameIndexesBuilt;

        private struct SurnameEntry
        {
            public string Name;
            public int Idx;
        }
        // 命名使用独立随机源，禁止消耗 UnityEngine.Random 全局状态，避免“起名”改变模拟结果。
        private readonly System.Random _nameRandom = new System.Random((int)(DateTime.Now.Ticks & 0x7FFFFFFF) ^ Environment.TickCount);
        private readonly object _nameRandomLock = new object();
        private readonly HashSet<long> _surnameSyncGuards = new HashSet<long>();

        private ZhuluChineseNameService() { }

        public void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                string path = Path.Combine(ZhuluContext.Instance.ModPath, "Configs", "ChineseNames.json");
                if (File.Exists(path))
                    _config = JsonUtility.FromJson<ZhuluChineseNameConfig>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[ChineseName] 配置加载失败，使用内置后备词库", ex);
            }
            if (_config == null) _config = new ZhuluChineseNameConfig();
            FillFallbacks();
            ZhuluContext.Instance.LogInfo("[ChineseName] 逐鹿权威中文命名服务已加载");
        }

        // ───────────────────────────── 公共接口 ─────────────────────────────

        public bool IsEligibleActor(Actor actor)
        {
            if (actor == null) return false;
            // 批次13修订：外部mod单位仍参与逐鹿命名（用户实锤——完全豁免导致英文原名裸奔）。
            // NamingExempt.json 的称号抑制消费点（原 TitleSuffixPatches.GetName_Postfix：
            // 外部mod单位不加逐鹿国王尊称后缀）已随 2026-10-06 后缀功能移出而暂停——
            // IsExternallyGoverned 保留备用（见 docs/移出代码存档-称号后缀.md）。
            // if (IsExternallyGoverned(actor)) return false;
            try
            {
                if (actor.asset != null && actor.asset.civ) return true;
                return actor.isSapient();
            }
            catch { return false; }
        }

        // ── 批次13：外部mod命名豁免（Configs/NamingExempt.json）──
        private static List<string> _exemptTraits;
        private static bool _exemptLoaded;

        /// <summary>单位是否由外部 mod 管辖命名（携带豁免名单中的任一特质）。</summary>
        /// <remarks>
        /// 批次18-B1 性能根修：旧实现对每次调用按 23 前缀×约62次编号枚举 hasTrait
        /// （≈1400次/调用+循环字符串分配），而本方法挂在引擎最热路径 Actor.getName 的
        /// Postfix 上——每帧每可见单位多次触发，构成卡顿主因。
        /// 现改为遍历单位自身少量特质逐条 StartsWith 前缀（一次遍历兼容点号/数字/加号后缀），
        /// 复杂度降约两个数量级；调用方亦已调整为仅国王命中后缀时才进入本判定。
        /// </remarks>
        public static bool IsExternallyGoverned(Actor actor)
        {
            if (!_exemptLoaded) LoadExemptList();
            if (_exemptTraits == null || _exemptTraits.Count == 0) return false;
            try
            {
                if (actor.traits == null) return false;
                foreach (var t in actor.traits)
                {
                    if (t == null || string.IsNullOrEmpty(t.id)) continue;
                    foreach (string prefix in _exemptTraits)
                    {
                        string p = prefix.EndsWith("*") ? prefix.Substring(0, prefix.Length - 1) : prefix;
                        if (p.Length > 0 && t.id.StartsWith(p, StringComparison.Ordinal)) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static void LoadExemptList()
        {
            _exemptLoaded = true;
            _exemptTraits = new List<string>();
            try
            {
                string path = System.IO.Path.Combine(ZhuluContext.Instance.ModPath, "Configs", "NamingExempt.json");
                if (!System.IO.File.Exists(path)) { ZhuluContext.Instance.LogInfo("[NamingExempt] 配置文件缺失，跳过外部豁免"); return; }
                var jobj = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(path));
                if (jobj.Value<bool?>("enabled") != true) { ZhuluContext.Instance.LogInfo("[NamingExempt] 豁免开关关闭"); return; }
                var arr = jobj["exempt_traits"] as Newtonsoft.Json.Linq.JArray;
                if (arr != null)
                    foreach (var t in arr)
                        if (!string.IsNullOrEmpty(t?.ToString())) _exemptTraits.Add(t.ToString());
                ZhuluContext.Instance.LogInfo($"[NamingExempt] 外部mod豁免名单加载完成: {_exemptTraits.Count} 条前缀");
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning("[NamingExempt] 豁免配置解析失败(降级为不豁免): " + ex.Message);
            }
        }

    }
}
