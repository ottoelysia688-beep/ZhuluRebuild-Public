using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ZhuluRebuild.Core;
using ZhuluRebuild.Gameplay;
using ZhuluRebuild.Adapters;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Domain
{
    public class PolicyDefinition
    {
        public string PolicyId { get; set; }
        public string Category { get; set; } // governance, military, economy, social
        public string CategoryTitle { get; set; }
        public string OfficeId { get; set; }
        public string OfficeTitle { get; set; }
        public string Title { get; set; }
        public string SummaryDesc { get; set; }
        public string BuffDesc { get; set; }
        public string TradeoffDesc { get; set; }
        public string Channel { get; set; }
        /// <summary>
        /// 基准数值。加算通道 = 点数（如忠诚 +20）；乘算通道 = 目标倍率（如 1.30 = +30%）。
        /// </summary>
        public float BaseValue { get; set; }
        /// <summary>代价通道（与主效果同时生效，废止时同时移除）。</summary>
        public string TradeoffChannel { get; set; }
        /// <summary>代价数值。加算通道填负数（如 -10）；乘算通道填小于1的倍率（如 0.90 = -10%）。</summary>
        public float TradeoffValue { get; set; }
        /// <summary>是否仅限天命国颁布。</summary>
        public bool RequiresMandate { get; set; } = false;
        /// <summary>天命值门槛（RequiresMandate=true 时有效）。特殊：-1 表示“天命<40”（罪己诏）。</summary>
        public int MandateThreshold { get; set; } = 0;
        /// <summary>最低国家等级要求（zi/bo/hou/gong）。空或null表示不限制。</summary>
        public string MinTier { get; set; } = null;
        // P2-30 协同/互斥（2026-08-23）：协同=同时在颁各自效果+5%；互斥=禁止同颁。
        // 仅数据声明+颁布口拦截+协同重算，展示徽章留待窗口迁移批次。
        public string[] Synergies { get; set; }
        public string[] Conflicts { get; set; }
    /// <summary>持续开销（阶段2D ⑤）：每年扣岁入百分比（0.02 = -2%岁入/年）。0 = 用固定维护费。</summary>
    public float UpkeepPct { get; set; } = 0f;
    /// <summary>礼治加成（2026-08-18：礼治政策体系字段化——GetLiZhiScore 遍历活跃政策按此求和，
    /// 替代硬编码 ID 白名单；爵位等级越高礼制规格越高：邑1/子1/伯2/侯2/公3/天命2~3）。</summary>
    public int LiZhiBonus { get; set; } = 0;
    /// <summary>阶段3H-18：体制专属标注（"east"/"nomad"/null=通用）。权威数据在 PolicyManager.CivFilterMap
    /// （id→值，InitDefinitions 末尾统一回填，JSON 覆盖后同样生效）；颁布口提示文案取用。</summary>
    public string CivilizationFilter { get; set; }
    }

    public class ActivePolicyRecord
    {
        public string KingdomId { get; set; }
        public string PolicyId { get; set; }
        public string Category { get; set; }
        public string HostPersonName { get; set; }
        public string HostGrade { get; set; }
        public float ScaledValue { get; set; }
        public int IssueYear { get; set; }
    }

    /// <summary>
    /// 国策政令。激活政策持久化到 custom_data（key: zlr_policies），随存档保存；
    /// 世界载入后由 RehydratePipeline 重新注入 ModifierPipeline。
    /// </summary>
    public class PolicyManager : ISnapshotSection<PolicyManager.PoliciesBlobState>
    {
        public static PolicyManager Instance { get; } = new PolicyManager();

        private const string KEY_POLICIES = "policies";

        private readonly Dictionary<string, PolicyDefinition> _definitions = new Dictionary<string, PolicyDefinition>();

        /// <summary>乘算通道集合：这些通道的 BaseValue 是目标倍率（1.30 = +30%），经评级放缩后写入 Multiplicative。</summary>
        private static readonly HashSet<string> MultiplierChannels = new HashSet<string>
        {
            ModifierChannels.GoldIncomeMultiplier,
            ModifierChannels.MilitaryReadiness,
            ModifierChannels.BuildingHealthMultiplier,
            ModifierChannels.PopulationGrowthSpeed,
            // 阶段3H-19 验尸修复：六新乘算通道（缺位则走加算放缩公式——攻击+8%会被算成-46%）
            ModifierChannels.AttackPower,
            ModifierChannels.UnitHealth,
            ModifierChannels.ArmySpeed,
            ModifierChannels.WarExhaustionRate,
            ModifierChannels.CorruptionRate,
            ModifierChannels.TributeIncome
        };

        private PolicyManager()
        {
            InitDefinitions();
        }

        private void InitDefinitions()
        {
            // 1) 内置硬编码定义全量注册（运行时基线）。
            //    修复（2026-08-17）：原实现「JSON 存在即 return」完全压制内置定义，而 Policies.json 停在
            //    25 条旧版——礼制政策/档位化三档/持续开销字段在运行时静默缺失。改合并语义：内置先注册，
            //    JSON 只按 id 覆盖同名项（数值/文案热改通道），不删减内置项。
            InitDefinitionsFallback();
            int builtinCount = _definitions.Count;

            // 2) Configs/Policies.json 按 id 覆盖
            // 2026-08-19 修复：上下文未初始化（ModPath 空——单测环境早期触发 cctor）时跳过 JSON 加载。
            // 原 Path.Combine(null,...) 抛 ArgumentNullException → catch 内 LogWarning 无条件调
            // Debug.LogWarning（ECall）→ 纯 .NET 测试环境 TypeInitializationException 且被 CLR 缓存。
            if (string.IsNullOrEmpty(ZhuluContext.Instance.ModPath)) return;
            try
            {
                string path = System.IO.Path.Combine(ZhuluContext.Instance.ModPath, "Configs", "Policies.json");
                if (System.IO.File.Exists(path))
                {
                    string json = System.IO.File.ReadAllText(path);
                    var items = JsonUtility.FromJson<PolicyJsonWrapper>($"{{\"items\":{json}}}");
                    if (items != null && items.items != null)
                    {
                        int overridden = 0;
                        foreach (var item in items.items)
                        {
                            if (string.IsNullOrEmpty(item.id)) continue;
                            // 2026-08-18：JSON 覆盖不回退内置 LiZhiBonus——Policies.json 停在旧版
                            // 无 liZhiBonus 字段，整项替换会把礼治政策加成清零（字段化体系静默失效）。
                            int keepLiZhi = item.liZhiBonus;
                            if (keepLiZhi == 0 && _definitions.TryGetValue(item.id, out var builtin)
                                && builtin != null && builtin.LiZhiBonus > 0)
                            {
                                keepLiZhi = builtin.LiZhiBonus;
                            }
                            _definitions[item.id] = new PolicyDefinition
                            {
                                PolicyId = item.id,
                                Category = item.category,
                                CategoryTitle = item.categoryTitle,
                                OfficeId = item.officeId,
                                OfficeTitle = item.officeTitle,
                                Title = item.title,
                                UpkeepPct = item.upkeepPct,
                                LiZhiBonus = keepLiZhi,
                                SummaryDesc = item.summary,
                                BuffDesc = item.buff,
                                TradeoffDesc = item.tradeoff,
                                Channel = item.channel,
                                BaseValue = item.baseValue,
                                TradeoffChannel = item.tradeoffChannel,
                                TradeoffValue = item.tradeoffValue,
                                RequiresMandate = item.requiresMandate,
                                MandateThreshold = item.mandateThreshold,
                                MinTier = item.minTier
                            };
                            overridden++;
                        }
                        ZhuluContext.Instance.LogInfo($"[PolicyManager] 政策定义：内置 {builtinCount} 项 + JSON 覆盖 {overridden} 项（合并语义）");
                    }
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[PolicyManager] JSON 覆盖加载失败（保留内置定义）: {ex.Message}");
            }

            // 天朝贡制：天命国专属（正统朝贡体系）
            if (_definitions.TryGetValue("gov_tribute_court", out var tc)) tc.RequiresMandate = true;

            // 3) 阶段3H-18：体制专属标注统一回填（内置+JSON 覆盖后同源——CivFilterMap 按 id 权威）。
            //    注意静态初始化顺序：Instance(行首)先于本类后段静态表构造——ctor 期间 map 尚为 null，
            //    跳过回填即可（MeetsCivGate/颁布口直读映射，不受影响；字段仅提示文案冗余用）。
            if (CivFilterMap == null) return;
            foreach (var kv in CivFilterMap)
                if (_definitions.TryGetValue(kv.Key, out var d) && d != null)
                    d.CivilizationFilter = kv.Value;
        }

        // JSON 反序列化 DTO：字段由 JsonUtility 运行时赋值，CS0649 属误报
#pragma warning disable CS0649
        [Serializable]
        private class PolicyJsonWrapper
        {
            public PolicyJsonItem[] items;
        }

        [Serializable]
        private class PolicyJsonItem
        {
            public string id;
            public string category;
            public string categoryTitle;
            public string officeId;
            public string officeTitle;
            public string title;
            public string summary;
            public string buff;
            public string tradeoff;
            public string channel;
            public float baseValue;
            public string tradeoffChannel;
            public float tradeoffValue;
            public bool requiresMandate;
            public int mandateThreshold;
            public string minTier;
            public float upkeepPct; // 阶段2D ⑤：持续开销（岁入比例）——原 DTO 缺此字段，JSON 政策恒 0
            public int liZhiBonus;  // 2026-08-18：礼治加成——旧版 JSON 无此字段，覆盖时保留内置值
        }
#pragma warning restore CS0649

        private void InitDefinitionsFallback()
        {
            // ═══════════ 0. 子国专属政策 (主管: 家宰, 无等级限制) ═══════════
            RegisterDef("zi_clan_aid", "governance", "族政", "prime_minister", "家宰", "宗族互助",
                "宗族子弟守望相助，共渡时艰",
                "城市忠诚度 +10",
                "金币税收效率 -5%",
                ModifierChannels.CityLoyaltyBonus, 10.0f,
                ModifierChannels.GoldIncomeMultiplier, 0.95f, null);

            // 阶段2D ⑥：全民皆兵档位化（轻/重——动员度两档）
            RegisterDef("zi_all_soldiers", "military", "族政", "prime_minister", "家宰", "全民皆兵·中",
                "举族皆战，老幼皆守，以弱抗强",
                "军队兵员上限 +20%",
                "城市人口上限 -10%",
                ModifierChannels.MilitaryReadiness, 1.20f,
                ModifierChannels.PopulationGrowthSpeed, 0.90f, null);
            RegisterDef("zi_all_soldiers_light", "military", "族政", "prime_minister", "家宰", "全民皆兵·轻",
                "农闲操练，战时集结",
                "军队兵员上限 +10%",
                "城市人口上限 -3%",
                ModifierChannels.MilitaryReadiness, 1.10f,
                ModifierChannels.PopulationGrowthSpeed, 0.97f, null);
            RegisterDef("zi_all_soldiers_heavy", "military", "族政", "prime_minister", "家宰", "全民皆兵·重",
                "尽发丁壮，倾国为战",
                "军队兵员上限 +35%",
                "城市人口上限 -20%",
                ModifierChannels.MilitaryReadiness, 1.35f,
                ModifierChannels.PopulationGrowthSpeed, 0.80f, null);

            // ═══════════ 0b. 伯国专属政策 (主管: 上大夫/司马/司徒, minTier=bo) ═══════════
            // 阶段2B 包2：诸侯礼政策（礼治积累用——受命门槛礼治≥5 的来源，2026-08-15 设计定稿）
            // 2026-08-18 礼治政策体系字段化：LiZhiBonus 为礼治加成权威来源（GetLiZhiScore 遍历活跃政策求和），
            // BuffDesc 首段明示"礼治 +X"（用户反馈：礼治加成内置但不显示）。
            RegisterDef(ZhuluLiZhiService.POLICY_ZONG_MIAO, "governance", "礼制", "prime_minister", "上大夫", "宗庙血食",
                "立宗庙，奉血食，追远慎终，礼教以兴",
                "礼治 +2",
                "金币税收效率 -5%，城市忠诚 -2（祭祀靡费）",
                ModifierChannels.GoldIncomeMultiplier, 0.95f,
                ModifierChannels.CityLoyaltyBonus, -2.0f, "bo", 0f, 2);
            RegisterDef(ZhuluLiZhiService.POLICY_MING_TANG, "governance", "礼制", "prime_minister", "上大夫", "明堂修礼",
                "修明堂，行朝聘，序爵齿，明贵贱",
                "礼治 +2，城市忠诚 +8",
                "金币税收效率 -3%（营建耗财）",
                ModifierChannels.CityLoyaltyBonus, 8.0f,
                ModifierChannels.GoldIncomeMultiplier, 0.97f, "bo", 0f, 2);

            // ═══════════ 0c. 礼治政策体系（2026-08-18 五等爵全覆盖：邑/子/侯/公 各配礼制，
            // 名目规格随爵位递进；礼治加成走 LiZhiBonus 字段，与伯国二礼/天子三礼同源）═══════════
            // 邑国（不限等级）：里社同祀——乡里共祭社神，礼之始也
            RegisterDef("zlr_yi_lishe", "governance", "礼制", "prime_minister", "家宰", "里社同祀",
                "封土立社，乡里同祀，春祈秋报，礼萌于野",
                "礼治 +1",
                "金币税收效率 -3%（祭祀靡费）",
                ModifierChannels.GoldIncomeMultiplier, 0.97f,
                null, 0f, null, 0f, 1);
            // P2-30 串联⑤（2026-08-23）：兴学立教——政策→科举正式挂钩（侯级）。
            // 名额通道走 civil_exam_efficiency ×1.15（默认名额4 → +1，与吏部官效同源叠加）；
            // 开科费七折不走通道，由 KejuManager.TryPayExamFee 直接查 IsPolicyActive 落地。
            // ═════ 阶段3H-18 A3：体制专属新政策 ═════
            // 东方（农耕/文教）
            RegisterDef("eco_granary", "economy", "经济政策", "revenue", "户部尚书", "常平仓",
                "设仓平粜，谷价不惊，凶年有备",
                "城市忠诚度 +6",
                "金币税收效率 -4%",
                ModifierChannels.CityLoyaltyBonus, 6.0f,
                ModifierChannels.GoldIncomeMultiplier, 0.96f, "hou");
            RegisterDef("eco_farming", "economy", "经济政策", "revenue", "户部尚书", "劝课农桑",
                "劝课农桑，深耕易耨，生齿日繁",
                "城市人口上限 +8%",
                "军队兵员上限 -3%",
                ModifierChannels.PopulationGrowthSpeed, 1.08f,
                ModifierChannels.MilitaryReadiness, 0.97f, "bo");
            // 草原（游牧专属）
            // ═════ 阶段3H-19：战斗/内政新通道政策（全部有利有弊）═════
            RegisterDef("mil_valour", "military", "军事政策", "military", "兵部尚书", "尚武之风",
                "举国崇武，校尉比武，刀锋所向",
                "攻击力 +8%",
                "城市忠诚度 -3（穷兵黩武）",
                ModifierChannels.AttackPower, 1.08f,
                ModifierChannels.CityLoyaltyBonus, -3.0f, "hou");
            RegisterDef("mil_fortress", "military", "军事政策", "military", "兵部尚书", "坚壁清野",
                "深沟高垒，坚壁清野，来敌无所掠",
                "防御力 +10（受伤 -10%）",
                "金币税收效率 -6%（市集萧条）",
                ModifierChannels.DefensePower, 10.0f,
                ModifierChannels.GoldIncomeMultiplier, 0.94f, "hou");
            RegisterDef("eco_post_road", "economy", "经济政策", "revenue", "户部尚书", "驰道直道",
                "修驰道，通直道，政令军情朝发夕至",
                "移速 +10%，朝贡收入 +10%",
                "金币税收效率 -5%（工役靡费）",
                ModifierChannels.ArmySpeed, 1.10f,
                ModifierChannels.GoldIncomeMultiplier, 0.95f, "gong");
            RegisterDef("mil_arsenal", "military", "军事政策", "military", "兵部尚书", "武库常备",
                "武库充备，甲胄鲜明，士卒披坚",
                "血量 +10%",
                "金币税收效率 -5% 且持续军饷（岁入 -2%/年）",
                ModifierChannels.UnitHealth, 1.10f,
                ModifierChannels.GoldIncomeMultiplier, 0.95f, "hou", 0.02f);
            RegisterDef("gov_xibing", "governance", "治政", "prime_minister", "丞相", "息兵罢战",
                "罢兵息民，与民更始，厌战自消",
                "厌战累积 -25%，消退 +33%",
                "军队兵员上限 -10%",
                ModifierChannels.WarExhaustionRate, 0.75f,
                ModifierChannels.MilitaryReadiness, 0.90f, null);
            RegisterDef("gov_chayan", "governance", "治政", "justice", "御史大夫", "察奸律",
                "设察奸之律，遣御史巡郡，贪墨敛迹",
                "腐败累积 -20%",
                "城市忠诚度 -3（苛察伤和）",
                ModifierChannels.CorruptionRate, 0.80f,
                ModifierChannels.CityLoyaltyBonus, -3.0f, "hou");
            RegisterDef("gov_salary", "governance", "治政", "justice", "御史大夫", "高俸养廉",
                "厚俸以养廉，禄厚则吏不忍贪",
                "腐败累积 -35%",
                "金币税收效率 -7% 且持续俸禄（岁入 -2%/年）",
                ModifierChannels.CorruptionRate, 0.65f,
                ModifierChannels.GoldIncomeMultiplier, 0.93f, "gong", 0.02f);
            RegisterDef("gov_tribute_court", "governance", "治政", "prime_minister", "丞相", "天朝贡制",
                "定朝贡之制，四夷奉贡，厚往薄来",
                "朝贡收入 +25%",
                "外交好感 -5（厚贡薄义）",
                ModifierChannels.TributeIncome, 1.25f,
                ModifierChannels.DiplomaticOpinionBonus, -5.0f, "gong");

            RegisterDef("nomad_salt_oath", "social", "族政", "prime_minister", "家宰", "撒盐盟誓",
                "诸部会盟，饮盐血为誓，背誓者长生天弃之",
                "城市忠诚度 +12",
                "金币税收效率 -3%",
                ModifierChannels.CityLoyaltyBonus, 12.0f,
                ModifierChannels.GoldIncomeMultiplier, 0.97f, null);
            RegisterDef("nomad_yam", "economy", "经济政策", "revenue", "户部尚书", "驿站传讯",
                "站赤通传，商旅四达，赋税倍输",
                "金币税收效率 +10%",
                "城市忠诚度 -2（驿役占丁）",
                ModifierChannels.GoldIncomeMultiplier, 1.10f,
                ModifierChannels.CityLoyaltyBonus, -2.0f, "bo");
            RegisterDef("nomad_rotation", "economy", "经济政策", "revenue", "户部尚书", "冬夏转场",
                "冬夏逐水草，牧地轮休，畜群繁息",
                "城市人口上限 +10%",
                "金币税收效率 -4%（转场靡费）",
                ModifierChannels.PopulationGrowthSpeed, 1.10f,
                ModifierChannels.GoldIncomeMultiplier, 0.96f, "bo");
            RegisterDef("nomad_hunt", "military", "军事", "military", "司马", "搜围演武",
                "岁行大搜围，骑射娴熟，寓训于猎",
                "军队兵员上限 +15%",
                "城市人口上限 -4%",
                ModifierChannels.MilitaryReadiness, 1.15f,
                ModifierChannels.PopulationGrowthSpeed, 0.96f, "hou");

            RegisterDef("zlr_hou_xingxue", "governance", "文教", "personnel", "吏部", "兴学立教",
                "广设庠序，劝学兴贤，郡县皆有其学",
                "开科费 -30%，取士名额 +1",
                "维持费 1% 岁入（学廪之费）",
                ModifierChannels.CivilExamEfficiency, 1.15f,
                null, 0f, "hou", 0.01f, 0);
            // 2026-08-18 各等级集权政策（中央权力过低难救——每级配加权力政策，AI 低权时优先颁）：
            // 邑/子（家宰）：集权于宗 +4
            RegisterDef("zi_jiquan", "governance", "族政", "prime_minister", "家宰", "集权于宗",
                "政出一门，族长总揽，事无大小皆决于宗庙",
                "中央权力 +4，城市忠诚 +5",
                "军队兵员上限 -5%",
                ModifierChannels.CityLoyaltyBonus, 5.0f,
                ModifierChannels.MilitaryReadiness, 0.95f, null);
            // 子国（minTier=zi）：家庙四时——嫡庶有序，四时祭享不辍
            RegisterDef("zlr_zi_sishi", "governance", "礼制", "prime_minister", "家宰", "家庙四时",
                "立家庙，序昭穆，四时祭享，孝道以敦",
                "礼治 +1，城市忠诚 +3",
                "金币税收效率 -4%（祭享靡费）",
                ModifierChannels.CityLoyaltyBonus, 3.0f,
                ModifierChannels.GoldIncomeMultiplier, 0.96f, "zi", 0f, 1);
            // 侯国（minTier=hou）：朝觐述职——比年小聘，三年一朝，藩屏之礼
            RegisterDef("zlr_hou_chaojin", "governance", "礼制", "prime_minister", "丞相", "朝觐述职",
                "比年小聘，三年一朝，述职于王，藩礼以修",
                "礼治 +2，列国外交好感度 +5",
                "金币税收效率 -6%（朝觐资费）",
                ModifierChannels.DiplomaticOpinionBonus, 5.0f,
                ModifierChannels.GoldIncomeMultiplier, 0.94f, "hou", 0f, 2);
            // 侯国（minTier=hou）：时享荐新——四时荐新于庙，视朔听政
            RegisterDef("zlr_hou_shixin", "governance", "礼制", "prime_minister", "丞相", "时享荐新",
                "四时荐新，视朔听政，庙制以隆",
                "礼治 +2，城市忠诚 +5",
                "金币税收效率 -5%（庙享靡费）",
                ModifierChannels.CityLoyaltyBonus, 5.0f,
                ModifierChannels.GoldIncomeMultiplier, 0.95f, "hou", 0f, 2);
            // 公国（minTier=gong）：大飨宾仪——飨宴宾旅，礼绝百僚
            RegisterDef("zlr_gong_binyi", "governance", "礼制", "prime_minister", "丞相", "大飨宾仪",
                "大飨宾旅，庭实旅百，执豆执笾，礼成于朝",
                "礼治 +3，列国外交好感度 +12",
                "金币税收效率 -8%（飨宴靡费）",
                ModifierChannels.DiplomaticOpinionBonus, 12.0f,
                ModifierChannels.GoldIncomeMultiplier, 0.92f, "gong", 0f, 3);
            // 公国（minTier=gong）：雅乐正声——金声玉振，颂声并作
            RegisterDef("zlr_gong_yayue", "governance", "礼制", "prime_minister", "丞相", "雅乐正声",
                "正雅颂，和钟磬，金声玉振，移风易俗",
                "礼治 +3，城市忠诚 +8",
                "金币税收效率 -6%（乐府靡费）",
                ModifierChannels.CityLoyaltyBonus, 8.0f,
                ModifierChannels.GoldIncomeMultiplier, 0.94f, "gong", 0f, 3);

            RegisterDef("bo_minister_gov", "governance", "邦政", "prime_minister", "上大夫", "大夫理政",
                "大夫总揽政务，井井有条，赋税充盈",
                "金币税收效率 +10%",
                "城市忠诚度 -5",
                ModifierChannels.GoldIncomeMultiplier, 1.10f,
                ModifierChannels.CityLoyaltyBonus, -5.0f, "bo");
            // 2026-08-18 伯国集权：大夫集权 +5
            RegisterDef("bo_jiquan", "governance", "邦政", "prime_minister", "上大夫", "大夫集权",
                "收邑人之权归于邦府，号令自大夫出",
                "中央权力 +5，金币税收效率 +5%",
                "城市忠诚度 -5",
                ModifierChannels.GoldIncomeMultiplier, 1.05f,
                ModifierChannels.CityLoyaltyBonus, -5.0f, "bo");

            RegisterDef("bo_militia", "military", "邦政", "military", "司马", "乡兵制",
                "征召乡勇，农闲操练，守卫邦土",
                "军队兵员上限 +25%",
                "金币税收效率 -10%",
                ModifierChannels.MilitaryReadiness, 1.25f,
                ModifierChannels.GoldIncomeMultiplier, 0.90f, "bo");

            RegisterDef("bo_recruit_talent", "economy", "邦政", "revenue", "司徒", "礼贤下士",
                "广纳贤才，招徕流民，充实邦国人口",
                "城市人口上限 +15%",
                "军队兵员上限 -10%",
                ModifierChannels.PopulationGrowthSpeed, 1.15f,
                ModifierChannels.MilitaryReadiness, 0.90f, "bo");

            // ═══════════ 1. 朝政政策 (主管: 丞相, minTier=hou) ═══════════
            // 2026-08-18 集权政策体系：BuffDesc 明示中央权力加成（RegimeManager 年度目标值 +N）
            RegisterDef("gov_central", "governance", "朝政政策", "prime_minister", "丞相", "中央集权",
                "强干弱枝，巩固朝廷权威，削弱地方割据势力",
                "中央权力 +10，城市忠诚度 +20",
                "军队兵员上限 -10%",
                ModifierChannels.CityLoyaltyBonus, 20.0f,
                ModifierChannels.MilitaryReadiness, 0.90f, "hou");

            RegisterDef("gov_feudal", "governance", "朝政政策", "prime_minister", "丞相", "郡国并行",
                "分封宗室功臣，犬牙相制，拱卫京师朝堂",
                "城市忠诚度 +15",
                "金币税收效率 -10%",
                ModifierChannels.CityLoyaltyBonus, 15.0f,
                ModifierChannels.GoldIncomeMultiplier, 0.90f, "hou");

            RegisterDef("gov_purge", "governance", "朝政政策", "prime_minister", "丞相", "朝堂整肃",
                "严加考课百官，整顿朝纲，肃清吏治",
                "金币税收效率 +15%",
                "城市忠诚度 -10",
                ModifierChannels.GoldIncomeMultiplier, 1.15f,
                ModifierChannels.CityLoyaltyBonus, -10.0f, "hou");

            RegisterDef("gov_taoist", "governance", "朝政政策", "prime_minister", "丞相", "无为而治",
                "与民休息，宽刑省罚，顺应自然无为",
                "城市人口上限 +30%",
                "军队兵员上限 -15%",
                ModifierChannels.PopulationGrowthSpeed, 1.30f,
                ModifierChannels.MilitaryReadiness, 0.85f, "hou");
            // 2026-08-18 公国集权：强干弱枝 +8
            RegisterDef("gong_qianggan", "governance", "朝政政策", "prime_minister", "丞相", "强干弱枝",
                "削支封之权，大臣奉法仰成，政柄独揽于公廷",
                "中央权力 +8，城市忠诚 +15",
                "军队兵员上限 -10%",
                ModifierChannels.CityLoyaltyBonus, 15.0f,
                ModifierChannels.MilitaryReadiness, 0.90f, "gong");
            // 2026-08-18 天命集权：乾纲独断 +12（修复：RegimeManager/AI 引用但未注册——天命国无集权政策）
            RegisterDef("mand_qiangan", "governance", "朝政政策", "prime_minister", "丞相", "乾纲独断",
                "威福自上，事决宸衷，百官奉行而已",
                "中央权力 +12，城市忠诚 +10",
                "军队兵员上限 -10%（内重外轻）",
                ModifierChannels.CityLoyaltyBonus, 10.0f,
                ModifierChannels.MilitaryReadiness, 0.90f, "tianzi");

            // ═══════════ 2. 军事政策 (主管: 兵部尚书) ═══════════
            RegisterDef("mil_fubing", "military", "军事政策", "military", "兵部尚书", "府兵制",
                "寓兵于农，战时为兵，平时为民耕种",
                "军队兵员上限 +30%",
                "金币税收效率 -10%",
                ModifierChannels.MilitaryReadiness, 1.30f,
                ModifierChannels.GoldIncomeMultiplier, 0.90f, "hou", 0.015f); // 阶段2D ⑤：持续开销 -1.5%岁入/年

            RegisterDef("mil_mercenary", "military", "军事政策", "military", "兵部尚书", "募兵制",
                "招募职业精兵，重赏勇士，专事对外征伐",
                "军队兵员上限 +20%",
                "城市忠诚度 -10 且持续耗军饷（岁入 -2%/年）",
                ModifierChannels.MilitaryReadiness, 1.20f,
                ModifierChannels.CityLoyaltyBonus, -10.0f, "hou", 0.02f); // 阶段2D ⑤：持续开销 -2%岁入/年

            RegisterDef("mil_civilian", "military", "军事政策", "military", "兵部尚书", "重文轻武",
                "抑将重文，交出兵权，防止藩镇割据自立",
                "城市忠诚度 +25",
                "军队兵员上限 -20%",
                ModifierChannels.CityLoyaltyBonus, 25.0f,
                ModifierChannels.MilitaryReadiness, 0.80f, "gong");

            RegisterDef("mil_elite", "military", "军事政策", "military", "兵部尚书", "精兵简政",
                "裁撤冗兵，精选健儿，坚城固守",
                "建筑耐久 +25%",
                "军队兵员上限 -10% 且持续军饷（岁入 -1%/年）",
                ModifierChannels.BuildingHealthMultiplier, 1.25f,
                ModifierChannels.MilitaryReadiness, 0.90f, "hou", 0.01f); // 阶段2D ⑤：持续开销 -1%岁入/年

            // ═══════════ 3. 经济政策 (主管: 户部尚书) ═══════════
            RegisterDef("eco_salt_iron", "economy", "经济政策", "revenue", "户部尚书", "盐铁专卖",
                "国家垄断盐铁手工业与矿产商品贸易",
                "金币税收效率 +30%",
                "城市忠诚度 -5",
                ModifierChannels.GoldIncomeMultiplier, 1.30f,
                ModifierChannels.CityLoyaltyBonus, -5.0f, "hou");

            RegisterDef("eco_rest", "economy", "经济政策", "revenue", "户部尚书", "与民休息",
                "薄敛轻徭，不夺农时，充实民间粮仓",
                "城市忠诚度 +15",
                "金币税收效率 -20%",
                ModifierChannels.CityLoyaltyBonus, 15.0f,
                ModifierChannels.GoldIncomeMultiplier, 0.80f, "hou");

            RegisterDef("eco_land", "economy", "经济政策", "revenue", "户部尚书", "均田制",
                "土地官办再分配，防止地方豪强兼并土地",
                "城市人口上限 +25%",
                "金币税收效率 -15%",
                ModifierChannels.PopulationGrowthSpeed, 1.25f,
                ModifierChannels.GoldIncomeMultiplier, 0.85f, "hou");

            // ── P2-30 政策协同/互斥数据（2026-08-23，设计文档 2.5 原案落地）──
            // 协同：府兵制↔精兵简政、均田制↔与民休息——同时在颁各自 ScaledValue ×1.05；
            // 互斥：府兵制↔募兵制——后颁者被拒。双向声明便于任意侧先颁。
            _definitions["mil_fubing"].Synergies = new[] { "mil_elite" };
            _definitions["mil_fubing"].Conflicts = new[] { "mil_mercenary" };
            _definitions["mil_mercenary"].Conflicts = new[] { "mil_fubing" };
            _definitions["mil_elite"].Synergies = new[] { "mil_fubing" };
            _definitions["eco_land"].Synergies = new[] { "eco_rest" };
            _definitions["eco_rest"].Synergies = new[] { "eco_land" };

            // ── 阶段3H-19：互斥扩容（跨类别的理念冲突——同槽位互斥由单槽机制天然保证）──
            void Conf2(string a, string b)
            {
                var da = _definitions.TryGetValue(a, out var x) ? x : null;
                var db = _definitions.TryGetValue(b, out var y) ? y : null;
                if (da != null)
                {
                    da.Conflicts = da.Conflicts ?? new string[0];
                    var l = new List<string>(da.Conflicts); if (!l.Contains(b)) l.Add(b); da.Conflicts = l.ToArray();
                }
                if (db != null)
                {
                    db.Conflicts = db.Conflicts ?? new string[0];
                    var l2 = new List<string>(db.Conflicts); if (!l2.Contains(a)) l2.Add(a); db.Conflicts = l2.ToArray();
                }
            }
            Conf2("gov_xibing", "mil_fubing");          // 息兵罢战 ↔ 府兵制
            Conf2("gov_xibing", "mil_mercenary");       // 息兵罢战 ↔ 募兵制
            Conf2("gov_xibing", "zi_all_soldiers");     // 息兵罢战 ↔ 全民皆兵·中
            Conf2("gov_xibing", "zi_all_soldiers_heavy");
            Conf2("mil_fortress", "soc_peace");         // 坚壁清野 ↔ 修好周边（防守 vs 修好）
            Conf2("mil_fortress", "zlr_hou_chaojin");   // 坚壁清野 ↔ 朝觐述职
            Conf2("soc_isolation", "nomad_yam");        // 闭关锁国 ↔ 驿站传讯（锁国 vs 通驿）
            Conf2("soc_isolation", "eco_post_road");    // 闭关锁国 ↔ 驰道直道
            Conf2("eco_head_tax_heavy", "gov_salary");  // 重税薄俸 ↔ 高俸养廉
            Conf2("mil_valour", "mil_civilian");        // 尚武之风 ↔ 重文轻武
            Conf2("gov_chayan", "zlr_zi_clan_aid");     // 察奸律 ↔ 宗族互助（法不容私）
            Conf2("eco_post_road", "soc_isolation");

            // 阶段2D ⑥：人均制税档位化（轻/中/重——税率三档，代价递增；AI 按国力选档）
            RegisterDef("eco_head_tax", "economy", "经济政策", "revenue", "户部尚书", "人均制税·中",
                "按人口头钱征税，快速充实中央国库",
                "金币税收效率 +40%",
                "城市忠诚度 -10",
                ModifierChannels.GoldIncomeMultiplier, 1.40f,
                ModifierChannels.CityLoyaltyBonus, -10.0f, "gong");
            RegisterDef("eco_head_tax_light", "economy", "经济政策", "revenue", "户部尚书", "人均制税·轻",
                "轻征头钱，百姓尚可承受",
                "金币税收效率 +20%",
                "城市忠诚度 -3",
                ModifierChannels.GoldIncomeMultiplier, 1.20f,
                ModifierChannels.CityLoyaltyBonus, -3.0f, "gong");
            RegisterDef("eco_head_tax_heavy", "economy", "经济政策", "revenue", "户部尚书", "人均制税·重",
                "重征头钱，竭泽而渔以充国库",
                "金币税收效率 +60%",
                "城市忠诚度 -18",
                ModifierChannels.GoldIncomeMultiplier, 1.60f,
                ModifierChannels.CityLoyaltyBonus, -18.0f, "gong");

            // ═══════════ 4. 社会政策 (主管: 礼部尚书, minTier=hou) ═══════════
            RegisterDef("soc_peace", "social", "社会政策", "rites", "礼部尚书", "修好周边",
                "怀柔远人，厚往薄来，修睦周边列国",
                "列国外交好感度 +30",
                "军队兵员上限 -10%",
                ModifierChannels.DiplomaticOpinionBonus, 30.0f,
                ModifierChannels.MilitaryReadiness, 0.90f, "hou");

            RegisterDef("soc_hegemony", "social", "社会政策", "rites", "礼部尚书", "尊王攘夷",
                "奉天子以令诸侯，号令天下，抵御异族侵扰",
                "列国外交好感度 +20",
                "金币税收效率 -10%",
                ModifierChannels.DiplomaticOpinionBonus, 20.0f,
                ModifierChannels.GoldIncomeMultiplier, 0.90f, "hou");

            RegisterDef("soc_isolation", "social", "社会政策", "rites", "礼部尚书", "闭关锁国",
                "封锁关隘，严禁边贸，维持传统防守",
                "建筑耐久 +25%",
                "列国外交好感度 -15",
                ModifierChannels.BuildingHealthMultiplier, 1.25f,
                ModifierChannels.DiplomaticOpinionBonus, -15.0f, "hou");

            RegisterDef("soc_ritual", "social", "社会政策", "rites", "礼部尚书", "礼乐教化",
                "修明礼乐，崇尚圣贤，教化天下百姓",
                "城市人口上限 +20%",
                "军队兵员上限 -10%",
                ModifierChannels.PopulationGrowthSpeed, 1.20f,
                ModifierChannels.MilitaryReadiness, 0.90f, "hou");

            // ═══════════ 5. 天命专属政策 (主管: 丞相，仅天命国) ═══════════
            RegisterMandateDef("mand_fengshan", "封禅泰山",
                "登封泰山，告成功于天地，彰显天命正统",
                "礼治 +2，社稷稳定度 +15 且城市忠诚 +10",
                "金币税收效率 -15%",
                ModifierChannels.GoldIncomeMultiplier, 0.85f, 70, 2);

            RegisterMandateDef("mand_zhengshuo", "颁布正朔",
                "颁正朔于天下，令列国奉我朝历法，确立宗主之尊",
                "礼治 +2，列国外交好感 +40",
                "军队兵员上限 -15%",
                ModifierChannels.MilitaryReadiness, 0.85f, 50, 2);

            RegisterMandateDef("mand_jitian", "祭天大典",
                "举国祭天，大赦天下，万民同庆",
                "礼治 +2，城市忠诚度 +25",
                "金币税收效率 -20%",
                ModifierChannels.GoldIncomeMultiplier, 0.80f, 60, 2);

            // 2026-08-18 天命集权：乾纲独断 +12（天命值≥40 方颁——罪己诏的对冲）
            RegisterMandateDef("mand_qiangan", "乾纲独断",
                "天子亲揽万机，政不由下，威福自上出之",
                "中央权力 +12，城市忠诚 +12",
                "金币税收效率 -10%",
                ModifierChannels.GoldIncomeMultiplier, 0.90f, 40);

            RegisterMandateDef("mand_zuiji", "罪己诏",
                "天子下诏罪己，引咎自责，以安天下人心",
                "稳定度 +20 且城市忠诚 +10",
                "天命值 -5（一次性）",
                ModifierChannels.CityLoyaltyBonus, 0.0f, -1); // A3-4：tradeoff 对齐 Policies.json（0=无代价通道）
        }

        private void RegisterDef(string id, string category, string categoryTitle, string officeId, string officeTitle, string title,
            string summary, string buff, string tradeoff, string channel, float baseVal,
            string tradeoffChannel, float tradeoffVal, string minTier = null, float upkeepPct = 0f, int liZhiBonus = 0)
        {
            _definitions[id] = new PolicyDefinition
            {
                PolicyId = id,
                Category = category,
                CategoryTitle = categoryTitle,
                OfficeId = officeId,
                OfficeTitle = officeTitle,
                Title = title,
                UpkeepPct = upkeepPct, // 阶段2D ⑤：持续开销（岁入比例）
                LiZhiBonus = liZhiBonus, // 2026-08-18：礼治加成（GetLiZhiScore 字段化求和）
                SummaryDesc = summary,
                BuffDesc = buff,
                TradeoffDesc = tradeoff,
                Channel = channel,
                BaseValue = baseVal,
                TradeoffChannel = tradeoffChannel,
                TradeoffValue = tradeoffVal,
                MinTier = minTier
            };
        }

        /// <summary>注册天命专属政策（分类=mandate，丞相主管，RequiresMandate=true）。
        /// liZhiBonus：礼治加成（祭天/封禅/正朔 +2——天子礼制）。</summary>
        private void RegisterMandateDef(string id, string title, string summary, string buff, string tradeoff,
            string tradeoffChannel, float tradeoffVal, int mandateThreshold, int liZhiBonus = 0)
        {
            // 主效果统一为稳定度注入（通过 StabilityManager.AddExternalBonus）
            // 但为了管道兼容性，封禅/祭天/正朔的主效果用已有通道表达
            string mainChannel;
            float mainVal;
            switch (id)
            {
                case "mand_fengshan": mainChannel = ModifierChannels.CityLoyaltyBonus; mainVal = 10f; break;
                case "mand_zhengshuo": mainChannel = ModifierChannels.DiplomaticOpinionBonus; mainVal = 40f; break;
                case "mand_jitian": mainChannel = ModifierChannels.CityLoyaltyBonus; mainVal = 25f; break;
                case "mand_zuiji": mainChannel = ModifierChannels.CityLoyaltyBonus; mainVal = 10f; break;
                default: mainChannel = ModifierChannels.CityLoyaltyBonus; mainVal = 10f; break;
            }

            _definitions[id] = new PolicyDefinition
            {
                PolicyId = id,
                Category = "mandate",
                CategoryTitle = "天命专属",
                OfficeId = "prime_minister",
                OfficeTitle = "丞相",
                Title = title,
                SummaryDesc = summary,
                BuffDesc = buff,
                TradeoffDesc = tradeoff,
                Channel = mainChannel,
                BaseValue = mainVal,
                TradeoffChannel = tradeoffChannel,
                TradeoffValue = tradeoffVal,
                RequiresMandate = true,
                MandateThreshold = mandateThreshold,
                LiZhiBonus = liZhiBonus
            };
        }

        public IEnumerable<PolicyDefinition> GetAllDefinitions() => _definitions.Values;

        // ───────────────────────────── 等级门槛统一判定（2026-08-18）─────────────────────────────
        // 语义（四处同口径：CanIssue / ValidateAndRevoke / GetAvailablePoliciesForTier / KingdomPoliciesVm）：
        // null/""/zi = 邑·子专属（rank 0 精确）；bo/hou/gong = 最低门槛递进（伯/侯/公及以上）。
        // 修复：①原显示层 bo=精确伯国而 AI 池 bo=伯及以上，AI 颁的礼制政策玩家看不见；
        //      ②"zi" 无分支——子国政策漏给所有国；③颁布层 null=不限与显示层 null=子国专属矛盾。

        /// <summary>国家等级 rank（特质权威版：0 邑/子 · 1 伯 · 2 侯 · 3 公 · 4 天命）。</summary>
        public static int GetKingdomRank(Kingdom k, bool isMandate)
        {
            var tier = OfficeManager.GetTierWithMandate(k, isMandate);
            return tier == OfficeManager.KingdomTier.Tianming ? 4
                : tier == OfficeManager.KingdomTier.Gong ? 3
                : tier == OfficeManager.KingdomTier.Hou ? 2
                : tier == OfficeManager.KingdomTier.Bo ? 1 : 0;
        }

        /// <summary>政策等级门槛判定（统一口径）。</summary>
        public static bool MeetsTierGate(string minTier, int currentRank)
        {
            if (string.IsNullOrEmpty(minTier) || minTier == "zi") return currentRank == 0;
            if (minTier == "bo") return currentRank >= 1;
            if (minTier == "hou") return currentRank >= 2;
            if (minTier == "gong") return currentRank >= 3;
            if (minTier == "tianzi") return currentRank >= 4; // 2026-08-18 天命专属档
            return true;
        }

        public IEnumerable<PolicyDefinition> GetDefinitionsByCategory(string category)
        {
            foreach (var def in _definitions.Values)
            {
                if (def.Category == category) yield return def;
            }
        }

        public PolicyDefinition GetDefinition(string policyId)
        {
            return _definitions.TryGetValue(policyId, out var def) ? def : null;
        }

        private static readonly Dictionary<string, string> NomadicPolicyTitles = new Dictionary<string, string>
        {
            { "zi_clan_aid", "部族互助" }, { "zi_all_soldiers", "全民骑战·中" },
            { "zi_all_soldiers_light", "全民骑战·轻" }, { "zi_all_soldiers_heavy", "全民骑战·重" },
            { ZhuluLiZhiService.POLICY_ZONG_MIAO, "祖灵祭祀" }, { ZhuluLiZhiService.POLICY_MING_TANG, "库里尔台议政" },
            { "zlr_yi_lishe", "敖包同祭" }, { "zi_jiquan", "汗权归一" }, { "zlr_zi_sishi", "四时祭天" },
            { "zlr_hou_chaojin", "诸部朝会" }, { "zlr_hou_shixin", "时节荐牲" },
            { "zlr_gong_binyi", "会盟宾仪" }, { "zlr_gong_yayue", "草原长调" },
            { "bo_minister_gov", "断事官理政" }, { "bo_jiquan", "盟主集权" },
            { "bo_militia", "部族兵制" }, { "bo_recruit_talent", "招纳贤勇" },
            { "gov_central", "汗权集中" }, { "gov_feudal", "分封诸部" },
            { "gov_purge", "整肃汗庭" }, { "gov_taoist", "宽简治部" }, { "gong_qianggan", "强庭弱部" },
            { "mand_qiangan", "天可汗独断" }, { "mil_fubing", "千户兵制" },
            { "mil_mercenary", "募勇制" }, { "mil_civilian", "重牧轻战" }, { "mil_elite", "精骑简政" },
            { "eco_salt_iron", "牲畜官市" }, { "eco_rest", "休养部众" }, { "eco_land", "划分牧地" },
            { "eco_head_tax", "按帐征税·中" }, { "eco_head_tax_light", "按帐征税·轻" },
            { "eco_head_tax_heavy", "按帐征税·重" }, { "soc_peace", "结好邻部" },
            { "soc_hegemony", "会盟诸部" }, { "soc_isolation", "封闭牧场" }, { "soc_ritual", "祖训教化" },
            { "mand_fengshan", "祭告长生天" }, { "mand_zhengshuo", "颁布汗历" },
            { "mand_jitian", "大祭长生天" }, { "mand_zuiji", "天可汗谢罪" }
        };

        // ── 阶段3D 游牧政策数值覆盖层（D2 定稿：明显幅度）——机动/掠夺/部族动员 与东方税制/农耕/礼法错开 ──
        private static readonly Dictionary<string, float> NomadicValueMult = new Dictionary<string, float>
        {
            // 全民骑战：军力增益×1.30（骑兵全民化），代价=持续开销×1.25（战马靡费——见 upkeep 表）
            { "zi_all_soldiers", 1.30f }, { "zi_all_soldiers_light", 1.30f }, { "zi_all_soldiers_heavy", 1.30f },
            // 牲畜官市：互市厚利 收入×1.25（逐利而礼弛——东方盐铁无此增幅）
            { "eco_salt_iron", 1.25f },
            // 划分牧地：牧地广辟 效果×1.20
            { "eco_land", 1.20f },
            // 千户兵制：军事×1.15 且开销×0.70（诸部自带兵器战马——低于府兵）
            { "mil_fubing", 1.15f },
            // 库里尔台议政：分权制衡增效×1.20（与东方中央集权反向）
            { ZhuluLiZhiService.POLICY_MING_TANG, 1.20f },
            // 祭告长生天：杀牲大祭 效果×1.25
            { "mand_fengshan", 1.25f }
        };
        private static readonly Dictionary<string, float> NomadicUpkeepMult = new Dictionary<string, float>
        {
            { "zi_all_soldiers", 1.25f }, { "zi_all_soldiers_light", 1.25f }, { "zi_all_soldiers_heavy", 1.25f },
            { "mil_fubing", 0.70f }
        };

        // ═════ 阶段3H-18：政策分开化 ═════
        // A. 体制专属（east=农耕礼制专属 / nomad=草原专属；不在表=通用）——
        //    AI 施政池、玩家颁布口、政策页三处同口径过滤。
        //    注意：全民骑战/牲畜官市/划牧地/千户兵制/库里尔台(明堂)/祭告长生天六条为
        //    "共用但改名换数值"设计（NomadicPolicyTitles+Mult 表），不入专属表。
        private static readonly Dictionary<string, string> CivFilterMap = new Dictionary<string, string>
        {
            // 东方农耕/礼制专属
            { "zlr_yi_lishe", "east" },            // 里社同祀（乡里社神——游牧对应敖包同祭为改名共享？否：独立条目归东）
            { "zlr_zongmiao", "east" },                     // 宗庙血食（字面量——避免 cctor 级联 ZhuluLiZhiService，纯测试环境 TypeInit 崩）
            { "zlr_hou_shixin", "east" },          // 时享荐新
            { "zlr_hou_xingxue", "east" },         // 兴学立教（科举文教）
            { "eco_granary", "east" },             // 常平仓（新增）
            { "eco_farming", "east" },             // 劝课农桑（新增）
            // 草原专属（新增）
            { "nomad_salt_oath", "nomad" },        // 撒盐盟誓
            { "nomad_yam", "nomad" },              // 驿站传讯
            { "nomad_rotation", "nomad" },         // 冬夏转场
            { "nomad_hunt", "nomad" },             // 搜围演武
        };

        // B. 类别分轨（游牧 id 表未覆盖的分类兜底倍率——骑战利/贸易钝/集权弱/部族凝聚力强）
        private static readonly Dictionary<string, float> NomadicCategoryValueMult = new Dictionary<string, float>
        {
            { "military", 1.10f }, { "social", 1.05f }, { "economy", 0.92f }, { "governance", 0.95f }
        };
        private static readonly Dictionary<string, float> NomadicCategoryUpkeepMult = new Dictionary<string, float>
        {
            { "military", 1.20f }, { "economy", 1.05f }
        };

        // ═══ 阶段3H-19：花费个体系数（叠在类别系数上——中央集权靡费、罪己诏轻取）═══
        private static readonly Dictionary<string, float> IssueCostMult = new Dictionary<string, float>
        {
            { "gov_central", 2.0f }, { "gov_qianang", 2.0f }, { "gov_junguo", 1.6f }, { "gov_ganshuo", 1.6f },
            { "eco_salt_iron", 1.5f }, { "mand_zui_zhao", 0.5f }, { "zlr_zi_clan_aid", 0.7f }
        };

        /// <summary>政策个体颁布花费系数（无覆盖=1）。</summary>
        public static float GetIssueCostMult(string policyId)
            => IssueCostMult.TryGetValue(policyId ?? "", out float m) ? m : 1f;

        /// <summary>政策对指定王国是否可用（体制门；null/未知=通用放行）。</summary>
        public static bool MeetsCivGate(PolicyDefinition def, Kingdom k)
        {
            if (def == null || k == null) return true;
            if (!CivFilterMap.TryGetValue(def.PolicyId ?? "", out string f)) return true;
            bool nomad = KingdomCivilizationService.Instance.IsNomadic(k);
            return f == "nomad" ? nomad : !nomad;
        }

        public static float GetNomadicCategoryValueMult(string category)
            => NomadicCategoryValueMult.TryGetValue(category ?? "", out float m) ? m : 1f;

        public static float GetNomadicCategoryUpkeepMult(string category)
            => NomadicCategoryUpkeepMult.TryGetValue(category ?? "", out float m) ? m : 1f;

        /// <summary>阶段3D：游牧差异标注（效果与描述一致）。
        /// 阶段3H-19 审计修正：实际效果=id表×类别表双层——标注必须显示合并值，否则描述撒谎。</summary>
        private static string NomadicMultNote(string policyId, bool isTradeoff, string category = null)
        {
            if (isTradeoff)
            {
                float u = GetNomadicUpkeepMult(policyId) * GetNomadicCategoryUpkeepMult(category);
                return u != 1f ? $"（军费×{u:F2}）" : "";
            }
            float m = GetNomadicValueMult(policyId) * GetNomadicCategoryValueMult(category);
            return m != 1f ? $"（游牧×{m:F2}）" : "";
        }

        /// <summary>游牧国政策效果倍率（无覆盖=1）。</summary>
        public static float GetNomadicValueMult(string policyId)
        {
            return NomadicValueMult.TryGetValue(policyId ?? "", out float m) ? m : 1f;
        }

        /// <summary>游牧国政策持续开销倍率（无覆盖=1）。</summary>
        public static float GetNomadicUpkeepMult(string policyId)
        {
            return NomadicUpkeepMult.TryGetValue(policyId ?? "", out float m) ? m : 1f;
        }

        /// <summary>返回只用于显示的副本；政策 ID、效果通道、数值和门槛保持原定义不变。</summary>
        public PolicyDefinition GetDisplayDefinition(PolicyDefinition source, Kingdom kingdom)
        {
            if (source == null || kingdom == null || !KingdomCivilizationService.Instance.IsNomadic(kingdom)) return source;
            string title = NomadicPolicyTitles.TryGetValue(source.PolicyId, out string mapped) ? mapped : source.Title;
            return new PolicyDefinition
            {
                PolicyId = source.PolicyId, Category = source.Category,
                CategoryTitle = NomadizeText(source.CategoryTitle), OfficeId = source.OfficeId,
                OfficeTitle = OfficeManager.GetOfficeTitleForKingdom(source.OfficeId, kingdom), Title = title,
                SummaryDesc = NomadizeText(source.SummaryDesc),
                BuffDesc = NomadizeText(source.BuffDesc) + NomadicMultNote(source.PolicyId, false, source.Category),
                TradeoffDesc = NomadizeText(source.TradeoffDesc) + NomadicMultNote(source.PolicyId, true, source.Category),
                Channel = source.Channel, BaseValue = source.BaseValue,
                TradeoffChannel = source.TradeoffChannel, TradeoffValue = source.TradeoffValue,
                RequiresMandate = source.RequiresMandate, MandateThreshold = source.MandateThreshold,
                MinTier = source.MinTier, UpkeepPct = source.UpkeepPct, LiZhiBonus = source.LiZhiBonus
            };
        }

        private static string NomadizeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return text.Replace("天命专属", "天眷专属").Replace("朝政政策", "汗庭政策")
                .Replace("军事政策", "军务政策").Replace("经济政策", "牧政政策")
                .Replace("社会政策", "部族政策").Replace("族政", "部政").Replace("邦政", "盟政")
                .Replace("礼制", "祭礼").Replace("礼治", "汗威").Replace("天命", "天眷")
                .Replace("天子", "天可汗").Replace("朝廷", "汗庭").Replace("百姓", "部众");
        }

        public static bool IsMandateHolderForKingdom(Kingdom k)
        {
            if (k == null) return false;
            string id = k.id.ToString();
            return KingdomCivilizationService.Instance.IsNomadic(k)
                ? NomadicMandateManager.Instance.IsMandateHolder(id)
                : OrthodoxMandateManager.Instance.IsMandateHolder(id);
        }

        public static float GetMandateValueForKingdom(Kingdom k)
        {
            if (k == null) return 0f;
            string id = k.id.ToString();
            return KingdomCivilizationService.Instance.IsNomadic(k)
                ? NomadicMandateManager.Instance.GetMandateValue(id)
                : OrthodoxMandateManager.Instance.GetMandateValue(id);
        }

        // ───────────────────────────── 持久化读写 ─────────────────────────────

        public List<ActivePolicyRecord> GetActivePolicies(string kingdomId)
        {
            var result = new List<ActivePolicyRecord>();
            Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
            if (k == null) return result;

            string raw = KingdomDataExt.GetStr(k, KEY_POLICIES);
            if (string.IsNullOrEmpty(raw)) return result;

            foreach (var chunk in raw.Split('|'))
            {
                if (string.IsNullOrEmpty(chunk)) continue;
                var parts = chunk.Split(':');
                if (parts.Length < 6) continue;

                var def = GetDefinition(parts[0]);
                result.Add(new ActivePolicyRecord
                {
                    KingdomId = kingdomId,
                    PolicyId = parts[0],
                    Category = def != null ? def.Category : "",
                    HostPersonName = parts[1],
                    HostGrade = parts[2],
                    ScaledValue = float.TryParse(parts[3], out float sv) ? sv : 0f,
                    IssueYear = int.TryParse(parts[4], out int iy) ? iy : 1
                });
            }
            return result;
        }

        private void SaveActivePolicies(string kingdomId, List<ActivePolicyRecord> records)
        {
            Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
            if (k == null) return;

            var chunks = new List<string>();
            foreach (var r in records)
            {
                string safeHost = (r.HostPersonName ?? "").Replace(":", "：").Replace("|", "¦");
                chunks.Add($"{r.PolicyId}:{safeHost}:{r.HostGrade}:{r.ScaledValue:F4}:{r.IssueYear}:1");
            }
            KingdomDataExt.SetStr(k, KEY_POLICIES, string.Join("|", chunks));
        }

        public ActivePolicyRecord GetActivePolicyInCategory(string kingdomId, string category)
        {
            return GetActivePolicies(kingdomId).Find(p => p.Category == category);
        }

        public bool IsPolicyActive(string kingdomId, string policyId)
        {
            return GetActivePolicies(kingdomId).Exists(p => p.PolicyId == policyId);
        }

        // ───────────────────────────── 数值放缩与管道注入 ─────────────────────────────

        /// <summary>
        /// 评级放缩。加算通道：值 × 评级系数；乘算通道：1 + (基准倍率 - 1) × 评级系数
        /// （保证 B 级主持恰好得到设计基准值，S 级增强而非翻倍爆炸）。
        /// </summary>
        public float CalculateScaledEffect(PolicyDefinition def, float gradeFactor)
        {
            if (MultiplierChannels.Contains(def.Channel))
            {
                return 1f + (def.BaseValue - 1f) * gradeFactor;
            }
            return def.BaseValue * gradeFactor;
        }

        /// <summary>按主持官员的纯数值考评分映射放缩系数与等级文本（唯一评分出口：OfficialRatingManager）。</summary>
        public static (float factor, string gradeLabel) GetGradeFactorFromScore(int score)
        {
            // 阶段1 A3：统一引用 OfficialRatingManager.GetGradeFactor（消除双系数表分裂）
            float factor = OfficialRatingManager.GetGradeFactor(score);
            string label = score >= 85 ? "极高 (S)" : score >= 70 ? "上等 (A)" : score >= 50 ? "适中 (B)"
                         : score >= 35 ? "较差 (C)" : "不推荐 (D)";
            return (factor, label);
        }

        /// <summary>
        /// P2-30 政策协同（2026-08-23）：justIssued 的 Synergies 对方已在颁时，
        /// 对方 ScaledValue ×1.05 并即时重写管道计划 + 落盘（RegisterPlan 幂等按 SourceId 替换）。
        /// </summary>
        private void ApplySynergyBoost(string kingdomId, PolicyDefinition justIssued)
        {
            try
            {
                if (justIssued?.Synergies == null || justIssued.Synergies.Length == 0) return;
                var records = GetActivePolicies(kingdomId);
                bool dirty = false;
                foreach (var sid in justIssued.Synergies)
                {
                    if (string.IsNullOrEmpty(sid)) continue;
                    var rec = records.Find(x => x.PolicyId == sid);
                    if (rec == null) continue;
                    rec.ScaledValue *= 1.05f;
                    var sdef = GetDefinition(sid);
                    RegisterPolicyPlan(kingdomId, rec);
                    dirty = true;
                    ZhuluContext.Instance.LogInfo($"[PolicyManager] 国策协同：《{sdef?.Title ?? sid}》因《{justIssued.Title}》并行，效果提升至 x{rec.ScaledValue / GetBaseScaledForLog(rec):F2}");
                }
                if (dirty) SaveActivePolicies(kingdomId, records);
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogRateLimited("Policy.Synergy", "[PolicyManager] 协同加成异常: " + ex.Message, 30f);
            }
        }

        /// <summary>协同日志用：取该政策定义基准值（避免除零，仅日志展示）。</summary>
        private float GetBaseScaledForLog(ActivePolicyRecord record)
        {
            var d = GetDefinition(record.PolicyId);
            float baseVal = d != null && d.BaseValue != 0f ? Math.Abs(d.BaseValue) : 1f;
            return baseVal;
        }

        private void RegisterPolicyPlan(string kingdomId, ActivePolicyRecord record)
        {
            var def = GetDefinition(record.PolicyId);
            if (def == null) return;

            // 主效果
            bool isMult = MultiplierChannels.Contains(def.Channel);
            ModifierPipeline.Instance.RegisterPlan(new ModifierPlan
            {
                SourceId = $"policy_{kingdomId}_{def.Category}",
                TargetKingdomId = kingdomId,
                Channel = def.Channel,
                Additive = isMult ? 0f : record.ScaledValue,
                Multiplicative = isMult ? record.ScaledValue : 1.0f,
                Reason = $"颁布【{def.CategoryTitle}】国策《{def.Title}》({record.HostPersonName} 主持)"
            });

            // 代价效果（双效果第二通道）
            if (!string.IsNullOrEmpty(def.TradeoffChannel) && def.TradeoffValue != 0f)
            {
                bool isTradeoffMult = MultiplierChannels.Contains(def.TradeoffChannel);
                float tradeoffScaled = isTradeoffMult
                    ? 1f + (def.TradeoffValue - 1f) * GetGradeFactorFromScore(50).factor  // B级基准
                    : def.TradeoffValue;
                ModifierPipeline.Instance.RegisterPlan(new ModifierPlan
                {
                    SourceId = $"policy_{kingdomId}_{def.Category}_tradeoff",
                    TargetKingdomId = kingdomId,
                    Channel = def.TradeoffChannel,
                    Additive = isTradeoffMult ? 0f : tradeoffScaled,
                    Multiplicative = isTradeoffMult ? def.TradeoffValue : 1.0f,
                    Reason = $"《{def.Title}》代价：{def.TradeoffDesc}"
                });
            }
        }

        /// <summary>世界载入后恢复管道注入（每年主 Tick 调用一次即可，RegisterPlan 幂等去重）。
        /// 注意：天命政策稳定度重放（AddExternalBonus 非幂等累加）只在快照恢复后执行一次——
        /// 阶段2N W11 修复：Main.Update 每 3 秒调本方法，原实现会反复累加 +15/+20 稳定度缓存（爆表漏洞）。</summary>
        public void RehydratePipelineAll(bool replayMandateStability = false)
        {
            if (World.world?.kingdoms?.list == null) return;
            foreach (Kingdom k in World.world.kingdoms.list)
            {
                if (k == null || !k.isAlive()) continue;
                string kId = k.id.ToString();
                foreach (var record in GetActivePolicies(kId))
                {
                    RegisterPolicyPlan(kId, record);
                    // 阶段3H-19 验尸修复：读档后战斗通道重放完毕→全军重算（否则读档单位无政策加成）
                    try { Adapters.CombatChannelPatches.RefreshKingdom(k); } catch { }
                    // 阶段2A A3-2：读档重放天命政策的稳定度外部加成（原为一次性注入，读档后丢失）——仅恢复路径
                    if (replayMandateStability)
                    {
                        var def = GetDefinition(record.PolicyId);
                        if (def != null && def.RequiresMandate)
                        {
                            if (def.PolicyId == "mand_fengshan") StabilityManager.Instance.AddExternalBonus(kId, 15f);
                            else if (def.PolicyId == "mand_zuiji") StabilityManager.Instance.AddExternalBonus(kId, 20f);
                        }
                    }
                }
            }
        }

        public bool IssuePolicy(string kingdomId, string kingdomName, string policyId, int currentYear, bool isPlayer = true)
        {
            var def = GetDefinition(policyId);
            if (def == null) return false;

            // 天命专属政策门槛检查
            if (def.RequiresMandate)
            {
                Kingdom mandateK = KingdomDataExt.FindKingdomById(kingdomId);
                if (!IsMandateHolderForKingdom(mandateK)) return false;
                float mVal = GetMandateValueForKingdom(mandateK);
                if (def.MandateThreshold == -1)
                { if (mVal >= 40f) return false; } // 罪己诏：仅天命<40
                else if (mVal < def.MandateThreshold) return false;
            }

            // 国家等级门槛检查（2026-08-18 特质权威 + 统一语义 MeetsTierGate）
            if (!string.IsNullOrEmpty(def.MinTier))
            {
                Kingdom tierK = KingdomDataExt.FindKingdomById(kingdomId);
                if (tierK != null)
                {
                    bool tierIsMandate = IsMandateHolderForKingdom(tierK);
                    if (!MeetsTierGate(def.MinTier, GetKingdomRank(tierK, tierIsMandate))) return false;
                }
            }

            // P2-30 互斥门：与在颁政策冲突 → 拒绝颁布（玩家给提示；AI 路径静默计分自然规避）
            if (def.Conflicts != null)
            {
                foreach (var cid in def.Conflicts)
                {
                    if (string.IsNullOrEmpty(cid) || !IsPolicyActive(kingdomId, cid)) continue;
                    var cdef = GetDefinition(cid);
                    ZhuluContext.Instance.LogInfo($"[PolicyManager] 《{def.Title}》与在颁《{cdef?.Title ?? cid}》互斥，拒绝颁行");
                    if (isPlayer)
                    {
                        try { WorldTip.showNow($"《{def.Title}》与《{cdef?.Title ?? cid}》不可并行颁行", false, "top", 4f, "#FF9A76"); } catch { }
                    }
                    return false;
                }
            }

            // 阶段3H-18：体制专属门（玩家与 AI 同口径——农耕政策草原颁不得，反之亦然）
            try
            {
                Kingdom civGateK = KingdomDataExt.FindKingdomById(kingdomId);
                if (civGateK != null && !MeetsCivGate(def, civGateK))
                {
                    bool gateNomad = KingdomCivilizationService.Instance.IsNomadic(civGateK);
                    string cf = CivFilterMap != null && CivFilterMap.TryGetValue(policyId, out string cfv) ? cfv : null;
                    string gateMsg = cf == "nomad"
                        ? $"《{def.Title}》乃草原体制之政，农耕国家不可颁行"
                        : $"《{def.Title}》乃农耕体制之政，草原国家不可颁行";
                    ZhuluContext.Instance.LogWarning($"[PolicyManager] {gateMsg}（{civGateK.name}，{(gateNomad ? "游牧" : "东方")}）");
                    if (isPlayer)
                        try { WorldTip.showNow(gateMsg, false, "top", 5f, "#f73939"); } catch { }
                    return false;
                }
            }
            catch { }

            // 1. 对应官席必须在任主持
            var app = OfficeManager.Instance.GetAppointment(kingdomId, def.OfficeId);
            // 动态获取当前国家等级对应的官职名称
            Kingdom issueK = KingdomDataExt.FindKingdomById(kingdomId);
            int issueRawCities = (issueK != null && issueK.cities != null) ? issueK.cities.Count : 1;
            int issueEffCities = KingdomExt.GetEffectiveCities(kingdomId, issueRawCities);
            bool issueIsMandate = IsMandateHolderForKingdom(issueK);
            string dynamicOfficeTitle = OfficeManager.GetOfficeTitleForKingdom(def.OfficeId, issueK);
            PolicyDefinition displayDef = GetDisplayDefinition(def, issueK);

            if (app == null || string.IsNullOrEmpty(app.PersonId))
            {
                string warnKey = $"{kingdomId}:{policyId}";
                if (_warnedPolicyFailures.Add(warnKey))
                    ZhuluContext.Instance.LogWarning($"[PolicyManager] 颁行《{def.Title}》失败：需要【{dynamicOfficeTitle}】在任主持（后续不再重复提示）");
                return false;
            }

            if (IsPolicyActive(kingdomId, policyId)) return true;

            // 阶段2S-1：颁布一次性费用（= 2 年维护费；国库不足 → 颁不行，玩家即时提示）
            long issueCost = TreasuryManager.Instance.CalcIssueCost(kingdomId, policyId);
            Kingdom costK = KingdomDataExt.FindKingdomById(kingdomId);
            if (!TreasuryManager.Instance.TryPayOneTime(costK, issueCost, $"颁行《{def.Title}》"))
            {
                // 2026-10-23 [F3]：与上方「缺主持官员」分支对齐，按 国:政策 去重。
                // 实测单局 53 年产生 570+ 条同义告警（里社同祀 173 / 全民皆兵·轻 161 …），
                // 淹没真实故障且拖慢 IO。_warnedPolicyFailures 在 ResetRuntime() 统一 Clear()。
                string costWarnKey = $"{kingdomId}:{policyId}:cost";
                if (_warnedPolicyFailures.Add(costWarnKey))
                    ZhuluContext.Instance.LogWarning($"[PolicyManager] 颁行《{def.Title}》失败：国用不足（需 {issueCost} 金，后续不再重复提示）");
                if (isPlayer)
                {
                    // 阶段3H-13：国用不足缓颁属内部行政——不弹窗（史册已记）
                }
                return false;
            }

            // 2. 同分类只保留一项政策
            var currentActiveInCat = GetActivePolicyInCategory(kingdomId, def.Category);
            if (currentActiveInCat != null)
            {
                RevokePolicy(kingdomId, kingdomName, currentActiveInCat.PolicyId, currentYear);
                ZhuluContext.Instance.LogInfo($"[PolicyManager] 颁行《{def.Title}》时自动替换【{def.CategoryTitle}】旧政策《{GetDefinition(currentActiveInCat.PolicyId)?.Title}》");
            }

            // 3. 主持官员纯数值考评分 -> 评级放缩
            float gradeFactor = 1.0f;
            string hostGrade = "适中 (B)";
            var citSnap = WorldBoxReadOnlyAdapter.Instance.CaptureKingdomCitizens(kingdomId);
            var hostCit = citSnap?.Find(c => c.ActorId == app.PersonId);
            if (hostCit != null)
            {
                var (score, _) = OfficialRatingManager.CalcOfficialScore(
                    hostCit.Intelligence, hostCit.Stewardship, hostCit.Military, hostCit.Diplomacy, hostCit.Level,
                    OfficeManager.GetOfficeIndex(def.OfficeId), hostCit.Age);
                (gradeFactor, hostGrade) = GetGradeFactorFromScore(score);
            }

            float scaledValue = CalculateScaledEffect(def, gradeFactor);

            // 逐鹿特质修正：贤相x1.15 / 庸才x0.7
            try
            {
                long hostId;
                if (long.TryParse(app.PersonId, out hostId))
                {
                    Actor hostActor = World.world.units.get(hostId);
                    if (hostActor != null && hostActor.isAlive())
                    {
                        scaledValue *= TraitManager.Instance.GetPolicyMultiplier(hostActor);
                    }
                }
            }
            catch { }

            // 体制被动：伯国「大夫辅政」政策效果x1.1（伯国爵位且三席满员；2026-08-18 特质权威口径）
            try
            {
                Kingdom policyK = KingdomDataExt.FindKingdomById(kingdomId);
                if (policyK != null && OfficeManager.GetTier(policyK) == OfficeManager.KingdomTier.Bo)
                {
                    string[] boSeats = { "prime_minister", "revenue", "military" }; // 伯国三席
                    bool allFilled = true;
                    foreach (var seat in boSeats)
                    {
                        var seatApp = OfficeManager.Instance.GetAppointment(kingdomId, seat);
                        if (seatApp == null || string.IsNullOrEmpty(seatApp.PersonId)) { allFilled = false; break; }
                    }
                    if (allFilled) scaledValue *= 1.1f;
                }
            }
            catch { }

            // 政体效能（阶段2N W6 实装）：政体五档温和映射 ±15%——原政体加成仅 UI 展示，现接入放缩主路径。
            // scaledValue 随 ActivePolicyRecord 持久化，读档重放（RegisterPolicyPlan）用存储值，同源不漂移。
            try
            {
                scaledValue *= RegimeManager.GetPolicyEfficiencyFactor(KingdomExt.GetPowerValue(kingdomId));
            }
            catch { }

            // 阶段3D 游牧数值覆盖层：机动/掠夺/部族动员 与东方结构错开（D2 定稿）
            try
            {
                Kingdom civK = KingdomDataExt.FindKingdomById(kingdomId);
                if (civK != null && KingdomCivilizationService.Instance.IsNomadic(civK))
                {
                    // 阶段3H-18 B：id 表（骑战/官市等专属错位）×类别表（军利/商钝/集权弱）双层分轨
                    float nm = GetNomadicValueMult(def.PolicyId) * GetNomadicCategoryValueMult(def.Category);
                    if (nm != 1f) scaledValue *= nm;
                }
            }
            catch { }

            // 阶段3H-19：战斗通道（攻防移血）随政策变动重算全军
            var record = new ActivePolicyRecord
            {
                KingdomId = kingdomId,
                PolicyId = policyId,
                Category = def.Category,
                HostPersonName = app.PersonName,
                HostGrade = hostGrade,
                ScaledValue = scaledValue,
                IssueYear = currentYear
            };

            var list = GetActivePolicies(kingdomId);
            list.Add(record);
            SaveActivePolicies(kingdomId, list);

            // 4. 注入 ModifierPipeline
            RegisterPolicyPlan(kingdomId, record);

            // 4a. P2-30 协同加成： synergy 对方已在颁 → 对方效果 ×1.05 重算刷新
            //（自身由未来对称触发覆盖；双方先后颁行各触发一次 = 文档"各+5%"）
            ApplySynergyBoost(kingdomId, def);

            // 4b. 天命专属政策的稳定度注入
            if (def.RequiresMandate)
            {
                if (def.PolicyId == "mand_fengshan") StabilityManager.Instance.AddExternalBonus(kingdomId, 15f);
                else if (def.PolicyId == "mand_zuiji")
                {
                    StabilityManager.Instance.AddExternalBonus(kingdomId, 20f);
                    // 天命值 -5（一次性）
                    if (issueK != null && KingdomCivilizationService.Instance.IsNomadic(issueK))
                        NomadicMandateManager.Instance.AdjustMandateValue(kingdomId, -5f);
                    else OrthodoxMandateManager.Instance.AdjustMandateValue(kingdomId, -5f);
                }
            }

            // 5. 录入史册（官职名按国家等级动态显示）
            ChronicleManager.Instance.AddEntry(
                currentYear,
                ChronicleEventType.PolicyEnacted,
                kingdomId,
                kingdomName,
                app.PersonName,
                $"由【{dynamicOfficeTitle}】<color=#6ca3fd>{app.PersonName}</color> 主持，正式颁行【{displayDef.CategoryTitle}】国策 <color={UI.ZhuluUIStyle.Hex.Imperial}>《{displayDef.Title}》</color>！",
                false
            );

            ZhuluContext.Instance.LogInfo($"[PolicyManager] 颁布国策: 王国={kingdomName}, 国策=《{def.Title}》, 主持={app.PersonName}({hostGrade}), 数值={scaledValue:F2}, 通道={def.Channel}");

            // 阶段3H-19 验尸修复：战斗通道刷新必须在 RegisterPolicyPlan 之后（原在注册前=旧管道状态，新战斗政策当晚不生效）
            try { Adapters.CombatChannelPatches.RefreshKingdom(KingdomDataExt.FindKingdomById(kingdomId)); } catch { }

            // 阶段3：个人史册（主持官员仕途高光 + 君主治迹）
            long hostActorId;
            if (long.TryParse(app.PersonId, out hostActorId))
                PersonHistoryManager.Instance.RecordEvent(hostActorId, PersonHistoryManager.CAT_SHITU,
                    $"主持颁行【{def.CategoryTitle}】国策《{def.Title}》", currentYear);
            try
            {
                Kingdom policyK = KingdomDataExt.FindKingdomById(kingdomId);
                if (policyK != null && policyK.king != null && policyK.king.isAlive())
                    PersonHistoryManager.Instance.RecordEvent(policyK.king.getID(), PersonHistoryManager.CAT_JUNLIN,
                        $"颁行国策《{def.Title}》", currentYear);
            }
            catch { }

            return true;
        }

        public bool RevokePolicy(string kingdomId, string kingdomName, string policyId, int currentYear)
        {
            bool revoked = RevokePolicyInner(kingdomId, kingdomName, policyId, currentYear);
            try { Adapters.CombatChannelPatches.RefreshKingdom(KingdomDataExt.FindKingdomById(kingdomId)); } catch { }
            return revoked;
        }

        private bool RevokePolicyInner(string kingdomId, string kingdomName, string policyId, int currentYear)
        {
            var def = GetDefinition(policyId);
            if (def == null) return false;

            var list = GetActivePolicies(kingdomId);
            int removed = list.RemoveAll(p => p.PolicyId == policyId);
            if (removed <= 0) return false;

            SaveActivePolicies(kingdomId, list);

            // C4 方案甲（2026-10-01）：年中废止统一退还当年已付维护费——
            // 覆盖全部调用点（等级校验/AI换政/官席空缺/党争政变/付不起废止）；
            // 付不起废止的项当年未付款、不在台账，天然退 0
            try
            {
                long refund = TreasuryManager.Instance.ConsumePaidUpkeep(kingdomId, policyId);
                if (refund > 0) TreasuryManager.Instance.RefundUpkeep(kingdomId, refund);
            }
            catch { }

            ModifierPipeline.Instance.RemovePlan($"policy_{kingdomId}_{def.Category}");
            ModifierPipeline.Instance.RemovePlan($"policy_{kingdomId}_{def.Category}_tradeoff");

            ChronicleManager.Instance.AddEntry(
                currentYear,
                ChronicleEventType.PolicyRevoked,
                kingdomId,
                kingdomName,
                "朝廷",
                $"朝廷正式废止并撤销【{def.CategoryTitle}】国策 <color=#e0554a>《{def.Title}》</color>。",
                false
            );

            ZhuluContext.Instance.LogInfo($"[PolicyManager] 废止国策: 王国={kingdomName}, 国策=《{def.Title}》");
            return true;
        }

        public void OnOfficerVacated(string kingdomId, string kingdomName, string officeId, int currentYear)
        {
            var toRevoke = new List<string>();
            foreach (var p in GetActivePolicies(kingdomId))
            {
                var def = GetDefinition(p.PolicyId);
                if (def != null && def.OfficeId == officeId)
                {
                    toRevoke.Add(p.PolicyId);
                }
            }

            foreach (var pid in toRevoke)
            {
                RevokePolicy(kingdomId, kingdomName, pid, currentYear);
            }
        }

        // ───────────────────────────── AI 自动颁布 ─────────────────────────────

        private int _lastAutoEnactYear = -1;
        private readonly HashSet<string> _warnedPolicyFailures = new HashSet<string>();

        /// <summary>
        /// 年度自动颁布入口。先校验清除违规政策，再按国家等级从可用政策池中选取颁布。
        /// </summary>
        public void TickAutoEnact(int currentYear)
        {
            // 可配置开关：AI 自动施政（关闭时重置年度标记，重开立即生效）
            if (!ZhuluConfig.Instance.AiAutoPolicy)
            {
                _lastAutoEnactYear = -1;
                return;
            }
            if (currentYear == _lastAutoEnactYear) return;
            _lastAutoEnactYear = currentYear;

            try
            {
                if (World.world?.kingdoms?.list == null) return;
                foreach (Kingdom k in World.world.kingdoms.list)
                {
                    if (k == null || !k.isAlive()) continue;
                    if (ZhuluRebelService.IsRebel(k)) continue; // 阶段2W：叛军无朝堂不施政（叛军体制全局口径）
                    string kId = k.id.ToString();
                    string kName = k.name ?? "本国";

                    // ── 年度校验：清除不符合当前等级的已生效政策 ──
                    ValidateAndRevokeInvalidPolicies(kId, kName, k, currentYear);

                    // ── 按等级自动颁布 ──
                    AutoEnactForKingdom(kId, kName, k, currentYear);
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[PolicyManager] AI 自动颁布异常", ex);
            }
        }

        /// <summary>年度校验：清除不符合当前国家等级的已生效政策。</summary>
        private void ValidateAndRevokeInvalidPolicies(string kId, string kName, Kingdom k, int year)
        {
            bool isMandate = IsMandateHolderForKingdom(k);
            // 2026-08-18 特质权威版 rank（与爵位称号同源——原城数口径致"国名公国、政策按伯国废止"错位）
            int currentRank = GetKingdomRank(k, isMandate);

            var activePolicies = GetActivePolicies(kId);
            var toRevoke = new List<string>();

            foreach (var active in activePolicies)
            {
                var def = GetDefinition(active.PolicyId);
                if (def == null) continue;

                // 天命专属政策：非天命国必须废止，天命国直接放行不走minTier
                if (def.RequiresMandate)
                {
                    if (!isMandate) toRevoke.Add(active.PolicyId);
                    continue;
                }

                // 混合过滤（2026-08-18 统一口径 MeetsTierGate：null/zi=邑子专属，bo/hou/gong 递进）
                if (!MeetsTierGate(def.MinTier, currentRank))
                {
                    toRevoke.Add(active.PolicyId);
                }
            }

            foreach (var policyId in toRevoke)
            {
                RevokePolicy(kId, kName, policyId, year);
                ZhuluContext.Instance.LogInfo($"[PolicyManager] 年度校验废止违规政策: {kName} 《{GetDefinition(policyId)?.Title ?? policyId}》");
            }
        }

        /// <summary>AI 国情快照（阶段2Q——施政决策的统一输入）。</summary>
        private class AiSituation
        {
            public bool AtWar;
            public bool Deficit;      // 国库亏空
            public bool Rich;         // 库 ≥ 5 年岁入
            public float Stability;
            public float WarExhaustion;
            public float FactionIntensity;
            public bool MandateVacant; // 天命空悬（礼治竞争期）
            public float NetLiZhi;
            public int Power;         // 中央权力值（2026-08-18：低权时 AI 优先集权政策）
            // 2026-08-19 串联：执政党立场（-1=无党争；否则 0~1 五轴快照——每国每年只算一次）
            public float[] RulingStance = null;
        }

        /// <summary>集权政策集合（2026-08-18：AI 低权救急优先 + RegimeManager 目标值加成同源）。</summary>
        private static readonly HashSet<string> PowerBoostPolicies = new HashSet<string>
        {
            "gov_central", "gong_qianggan", "bo_jiquan", "zi_jiquan", "mand_qiangan", "gov_purge"
        };

        /// <summary>
        /// 按国家等级自动施政。阶段2Q 完整重构：
        /// ① 国情快照（战争/财政/稳定/厌战/党争/礼治竞争）② 效果感知评分（读政策通道数值）
        /// ③ 换政机制——活跃政策满 8 年且新候选分差 ≥3 方改弦更张（原"有活跃就跳过"= 永不换政）
        /// ④ 亏空国不再一刀切禁策：只颁经济恢复/安民类自救，其余停颁 ⑤ 每年每国最多颁/换 2 条。</summary>
        private void AutoEnactForKingdom(string kId, string kName, Kingdom k, int year)
        {
            bool isMandate = IsMandateHolderForKingdom(k);
            int rawCities = k.cities != null ? k.cities.Count : 1;
            int effCities = KingdomExt.GetEffectiveCities(kId, rawCities);

            // ── 国情快照 ──
            var sit = new AiSituation();
            try { var wars = k.getWars(); if (wars != null) { foreach (var _ in wars) { sit.AtWar = true; break; } } } catch { }
            try
            {
                var tre = TreasuryManager.Instance.GetReport(kId);
                sit.Deficit = tre.Treasury < 0;
                sit.Rich = tre.Income > 0 && tre.Treasury >= tre.Income * 5;
            }
            catch { }
            try { sit.Stability = StabilityManager.Instance.GetStability(kId); } catch { }
            try { sit.WarExhaustion = ZhuluWarService.GetWarExhaustion(kId); } catch { }
            try { sit.FactionIntensity = FactionPoliticsManager.Instance.GetIntensity(k, kId); } catch { }
            // 2026-08-19 串联：执政党立场快照（每国每年一次；AiPolicyScore 据此做政策偏好）
            try
            {
                var rulingF = FactionPoliticsManager.Instance.GetRulingFaction(k, kId);
                if (rulingF != null) sit.RulingStance = FactionManager.GetStance(rulingF);
            }
            catch { }
            try { sit.Power = KingdomExt.GetPowerValue(kId); } catch { }
            try
            {
                sit.MandateVacant = KingdomCivilizationService.Instance.IsNomadic(k)
                    ? NomadicMandateManager.Instance.GetMandateHolderId() == null
                    : OrthodoxMandateManager.Instance.GetMandateHolderId() == null;
                sit.NetLiZhi = ZhuluLiZhiService.GetNetLiZhi(kId);
            }
            catch { }

            // ── 天命专属（仅天命国，按天命值选）──
            if (isMandate)
            {
                string mandateChoice = ChooseMandatePolicy(kId);
                if (mandateChoice != null)
                    TryAutoEnactCategory(kId, kName, "mandate", mandateChoice, year);
            }

            var availableDefs = GetAvailablePoliciesForTier(k, isMandate);
            var byCategory = new Dictionary<string, List<PolicyDefinition>>();
            foreach (var def in availableDefs)
            {
                if (def.RequiresMandate) continue;
                if (!byCategory.TryGetValue(def.Category, out var list))
                {
                    list = new List<PolicyDefinition>();
                    byCategory[def.Category] = list;
                }
                list.Add(def);
            }

            int actions = 0; // 每年每国最多颁/换 2 条（防整肃式大换血）
            foreach (var kvp in byCategory)
            {
                if (actions >= 2) break;
                string category = kvp.Key;
                var candidates = kvp.Value;
                if (candidates.Count == 0) continue;

                // 最佳候选（效果感知评分）
                PolicyDefinition best = null;
                float bestScore = float.MinValue;
                foreach (var c in candidates)
                {
                    float s = AiPolicyScore(c, sit, kId, year);
                    if (s > bestScore) { bestScore = s; best = c; }
                }
                if (best == null) continue;

                var active = GetActivePolicyInCategory(kId, category);
                if (active == null)
                {
                    // 亏空自救：只开经济恢复 / 社会安民（原直接 return 连自救都不做）
                    if (sit.Deficit && category != "economy" && category != "social") continue;
                    TryAutoEnactCategory(kId, kName, category, best.PolicyId, year);
                    if (IsPolicyActive(kId, best.PolicyId)) actions++;
                }
                else
                {
                    // 换政：满 8 年 + 分差 ≥ 3（防抖动）
                    var activeDef = GetDefinition(active.PolicyId);
                    float curScore = activeDef != null ? AiPolicyScore(activeDef, sit, kId, year) : -99f;
                    // 2026-08-18 放宽：满 5 年 + 分差 ≥2 即改弦更张（原 8 年/3 分过保守——AI 施政固化）
                    if (year - active.IssueYear >= 5 && bestScore - curScore >= 2f)
                    {
                        TryAutoEnactCategory(kId, kName, category, best.PolicyId, year);
                        if (IsPolicyActive(kId, best.PolicyId))
                        {
                            actions++;
                            ZhuluContext.Instance.LogInfo($"[PolicyManager] AI改弦更张: {kName} 【{category}】《{activeDef?.Title}》→《{best.Title}》（{curScore:F0}→{bestScore:F0}分）");
                        }
                    }
                }
            }
        }

        /// <summary>阶段2Q：效果感知评分——按国情与政策实际通道/数值打分（替代原 3 项粗加权）。
        /// 2026-08-18 重构：① 政策强度基础分（档位/效果幅度）② 每国年度确定性伪随机偏好
        /// （同国同年稳定、跨年跨国有差异——防全图 AI 固定颁第一条政策永不更换）。</summary>
        private float AiPolicyScore(PolicyDefinition def, AiSituation s, string kId = null, int year = 0)
        {
            float score = 0f;
            bool isTaxRaiser = def.Channel == ModifierChannels.GoldIncomeMultiplier && def.BaseValue > 1f;
            bool isArmyBooster = def.Category == "military" && def.Channel == ModifierChannels.MilitaryReadiness && def.BaseValue > 1f;
            bool isAppease = def.PolicyId == "eco_rest" || def.PolicyId == "soc_peace" || def.PolicyId == "gov_taoist";

            // ── 基础适配分（0~2）：效果幅度大/档位匹配国力者略优先 ──
            float magnitude = Mathf.Abs(def.BaseValue - 1f);
            if (def.Channel == ModifierChannels.GoldIncomeMultiplier || def.Channel == ModifierChannels.MilitaryReadiness
                || def.Channel == ModifierChannels.PopulationGrowthSpeed) magnitude = Mathf.Abs(def.BaseValue - 1f) * 20f;
            else if (def.Channel == ModifierChannels.CityLoyaltyBonus || def.Channel == ModifierChannels.DiplomaticOpinionBonus) magnitude = def.BaseValue / 15f;
            score += Mathf.Clamp(magnitude, 0f, 1.5f);

            // ── 每国年度伪随机偏好（0~1.5）：hash(kId+year+policyId) 确定性稳定 ──
            if (!string.IsNullOrEmpty(kId))
            {
                int h = 17;
                string seed = kId + "|" + year + "|" + def.PolicyId;
                unchecked { foreach (char c in seed) h = h * 31 + c; }
                float r = (h & 0xFFFF) / 65535f;
                score += r * 1.5f;
            }

            // 危机项
            if (s.Deficit)
            {
                if (isTaxRaiser && def.Category == "economy") score += 2f;      // 增收自救
                if (def.UpkeepPct > 0f) score -= 3f;                              // 持续开销雪上加霜
                if (def.Category == "military") score -= 1f;
                if (isAppease) score += 1f;                                        // 安民止损
            }
            if (s.AtWar)
            {
                if (isArmyBooster) score += 2f;
                if (def.PolicyId == "mil_civilian") score -= 3f;                  // 战时重文轻武
            }
            if (s.WarExhaustion >= 40f && isArmyBooster) score -= 2f;             // 厌战仍扩军
            if (s.Stability < 40f)
            {
                if (isAppease) score += 2f;
                if (def.PolicyId.EndsWith("_heavy")) score -= 2f;                 // 低稳横征
            }
            // 机会项
            if (s.Rich && def.PolicyId.EndsWith("_heavy")) score += 1f;
            if (s.Rich && isArmyBooster) score += 1f;
            if (!s.Rich && def.PolicyId.EndsWith("_light") && isTaxRaiser) score += 1f; // 穷国轻税
            // 低权救急（2026-08-18）：中央权力 <40 → 集权政策优先（大权旁落先收权）
            if (s.Power < 40f && PowerBoostPolicies.Contains(def.PolicyId)) score += 2f;
            // 礼治竞争（天命空悬 + 本国礼治未达：礼制政策抢先机；2026-08-18 字段化——
            // def.LiZhiBonus>0 覆盖五等爵全部礼治政策，不再硬编码伯国二礼）
            if (s.MandateVacant && s.NetLiZhi < 8f && def.LiZhiBonus > 0)
                score += 2f;
            // 党争倾轧 → 整肃/察奸/养廉（阶段3H-19：党争-廉政联动）
            if (s.FactionIntensity >= 0.5f && def.PolicyId == "gov_purge") score += 1f;
            if (s.FactionIntensity >= 0.5f && (def.PolicyId == "gov_chayan" || def.PolicyId == "gov_salary")) score += 1.5f;
            // 富国修武（阶段3H-19）：库充 → 武库/驰道
            if (s.Rich && (def.PolicyId == "mil_arsenal" || def.PolicyId == "eco_post_road")) score += 1f;
            // 厌战高 → 息兵罢战
            if (s.WarExhaustion >= 50f && def.PolicyId == "gov_xibing") score += 2f;
            // 2026-08-19 串联：执政党立场偏好——军事党（战争轴≥0.7）→military +20%；
            // 民生党（≥0.7）→social/economy +20%；变法党（≥0.7）→高维护政策容忍 +
            score *= RulingPartyPolicyFactor(s, def);
            return score;
        }

        /// <summary>
        /// 2026-08-19 串联：执政党立场 → 政策偏好系数（乘在总分上）。
        /// 军事党（战争轴≥0.7）→ military 类 ×1.2；民生党（≥0.7）→ social/economy ×1.2；
        /// 变法党（≥0.7）→ 高维护政策 ×1.1（改革不惜费）；无党争 → 1.0。
        /// </summary>
        private static float RulingPartyPolicyFactor(AiSituation s, PolicyDefinition def)
        {
            try
            {
                var st = s?.RulingStance;
                if (st == null || st.Length < FactionManager.STANCE_COUNT) return 1f;
                if (st[FactionManager.ST_WAR] >= 0.7f && def.Category == "military") return 1.2f;
                if (st[FactionManager.ST_LIVELIHOOD] >= 0.7f
                    && (def.Category == "social" || def.Category == "economy")) return 1.2f;
                if (st[FactionManager.ST_REFORM] >= 0.7f && def.UpkeepPct > 0f) return 1.1f;
                return 1f;
            }
            catch { return 1f; }
        }

        /// <summary>获取当前等级可用的政策列表（排除天命专属；2026-08-18 特质权威 rank + 统一 MeetsTierGate）。</summary>
        private List<PolicyDefinition> GetAvailablePoliciesForTier(Kingdom k, bool isMandateHolder)
        {
            int currentRank = GetKingdomRank(k, isMandateHolder);

            var result = new List<PolicyDefinition>();
            foreach (var def in _definitions.Values)
            {
                // 天命专属单独处理，不纳入通用池
                if (def.RequiresMandate) continue;

                // 混合过滤（2026-08-18 修复：原"bo"精确匹配致侯国/公国/天命国无礼制政策 →
                // 礼治永不动 → 天命不可得；null/zi=邑子专属，bo/hou/gong 递进）
                if (!MeetsTierGate(def.MinTier, currentRank)) continue;

                // 阶段3H-18：体制专属过滤（东方不颁草原政策，游牧不颁农耕政策）
                if (!MeetsCivGate(def, k)) continue;

                result.Add(def);
            }
            return result;
        }

        /// <summary>若该分类无活跃政策且主管官席在任，则颁布指定政策。</summary>
        private void TryAutoEnactCategory(string kId, string kName, string category, string policyId, int year)
        {
            if (GetActivePolicyInCategory(kId, category) != null) return;
            IssuePolicy(kId, kName, policyId, year, false);
        }

        private string ChooseMandatePolicy(string kId)
        {
            Kingdom mandateK = KingdomDataExt.FindKingdomById(kId);
            float mVal = GetMandateValueForKingdom(mandateK);
            if (mVal >= 70f) return "mand_fengshan";   // 封禅泰山
            if (mVal >= 60f) return "mand_jitian";     // 祭天大典
            if (mVal >= 50f) return "mand_zhengshuo";  // 颁布正朔
            if (mVal < 40f) return "mand_zuiji";       // 罪己诏
            return null; // 40-49 不颁布
        }

    
        // ═══════════ v2 快照存档接口 ═══════════
        // ---- batch10-A1: schema v5 section ----
        public sealed class PoliciesBlobState { public string Blob; }
        public string SectionName => "policies";
        public Dictionary<string, PoliciesBlobState> CaptureStates()
        {
            var d = new Dictionary<string, PoliciesBlobState>();
            if (World.world?.kingdoms?.list == null) return d;
            foreach (Kingdom k in World.world.kingdoms.list)
            {
                if (k == null || !k.isAlive()) continue;
                string v = KingdomDataExt.GetStr(k, KEY_POLICIES, "");
                if (!string.IsNullOrEmpty(v)) d[k.id.ToString()] = new PoliciesBlobState { Blob = v };
            }
            return d;
        }
        public void ApplyStates(Dictionary<string, PoliciesBlobState> data)
        {
            if (data == null) return;
            foreach (var kv in data)
            {
                Kingdom k = KingdomDataExt.FindKingdomById(kv.Key);
                if (k == null || !k.isAlive() || kv.Value == null) continue;
                KingdomDataExt.SetStr(k, KEY_POLICIES, kv.Value.Blob ?? "");
            }
            try { RehydratePipelineAll(replayMandateStability: true); } catch (Exception ex) { ZhuluContext.Instance.LogWarning($"[PolicyManager] snapshot rehydrate failed: {ex.Message}"); }
        }

        public void MigrateFromCustomData() { }
        public void ResetRuntime()
        {
            _lastAutoEnactYear = -1;
            _warnedPolicyFailures.Clear();
        }

}
}
