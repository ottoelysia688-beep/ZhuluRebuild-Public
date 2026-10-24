using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace ZhuluRebuild.Persistence
{
    /// <summary>
/// 【批次10-A1 起降级为内部中转载体】仅供 Orthodox/Nomadic/Chronicle 三个委托适配器
/// 在内存中调用遗留协议实现时传递载荷；磁盘格式已是 Schema v5（ZhuluSnapshotV5），
/// 本类型不再参与任何序列化落盘。B5 Orthodox 拆分时将随卷轴协议一并退役。
/// </summary>
    /// <summary>
    /// 【已废弃落盘用途】逐鹿全量存档快照（v2 时代容器）。
    /// 设计：所有系统数据在运行中存内存（Dictionary 等），保存时 Collect 进快照，
    /// 读档时从快照 Restore 回内存。不再依赖王国 custom_data（其读档时序不可靠——曾致天命消失/串档）。
    /// 快照文件：存档目录 zhulu_snapshot.json（权威）+ .runtime/zhulu_snapshot.json（工作副本）。
    /// </summary>
    public class ZhuluSnapshot
    {
        /// <summary>快照格式版本。</summary>
        public int Version { get; set; } = 4;

        /// <summary>世界唯一 ID（新世界生成 GUID；读档校验防串档）。</summary>
        public string WorldId { get; set; } = "";

        /// <summary>保存时世界年。</summary>
        public int SavedAtYear { get; set; } = 0;

        // ═══════════════ 天命系统 ═══════════════

        /// <summary>朝代历史序列化（沿用朝代|王国|起|止|因|皇帝... 格式）。</summary>
        public string MandateHistory { get; set; } = "";

        /// <summary>天命国数据：王国ID → has_mandate|mandate_value|below6_since|dynasty|era|emperor|trend0|trend1|trend2</summary>
        public Dictionary<string, string> MandateData { get; set; } = new Dictionary<string, string>();

        /// <summary>长生天眷历史（与东方天命完全分轨）。</summary>
        public string NomadicMandateHistory { get; set; } = "";

        /// <summary>阶段3H：游牧王庭/可汗结构化记录（court|kid|s|e|r⟂khan;... 以\n分庭）。</summary>
        public string NomadicCourtHistory { get; set; } = "";

        /// <summary>长生天眷国数据：王国ID → 独立序列化串。</summary>
        public Dictionary<string, string> NomadicMandateData { get; set; } = new Dictionary<string, string>();

        /// <summary>国家体制锁：王国ID → 体制枚举值|建国/转制种族ID。</summary>
        public Dictionary<string, string> KingdomCivilizations { get; set; } = new Dictionary<string, string>();

        /// <summary>其他智慧种族未来新国家的体制默认。</summary>
        public Dictionary<string, int> SpeciesCivilizationDefaults { get; set; } = new Dictionary<string, int>();

        // ═══════════════ 各王国系统（王国ID → 序列化串，格式沿用各系统现有实现） ═══════════════

        /// <summary>官制：王国ID → offices 串。</summary>
        public Dictionary<string, string> Offices { get; set; } = new Dictionary<string, string>();

        /// <summary>国策：王国ID → policies 串。</summary>
        public Dictionary<string, string> Policies { get; set; } = new Dictionary<string, string>();

        /// <summary>财政：王国ID → tre|tre_in|tre_sal|tre_upk|tre_def|tre_reveff。</summary>
        public Dictionary<string, string> Treasury { get; set; } = new Dictionary<string, string>();

        /// <summary>稳定度：王国ID → 数值。</summary>
        public Dictionary<string, float> Stability { get; set; } = new Dictionary<string, float>();

        /// <summary>王国扩展数据：王国ID → basename|power|birthyear|everking|everemp|overlord。</summary>
        public Dictionary<string, string> KingdomExt { get; set; } = new Dictionary<string, string>();

        /// <summary>继承：王国ID → heir_id|reign_start|regency。</summary>
        public Dictionary<string, string> Succession { get; set; } = new Dictionary<string, string>();

        /// <summary>附庸：王国ID → suzerain|vassals|vassal_since|independence_cooldown。</summary>
        public Dictionary<string, string> Vassal { get; set; } = new Dictionary<string, string>();

        /// <summary>宣称：王国ID → claims 串。</summary>
        public Dictionary<string, string> Claims { get; set; } = new Dictionary<string, string>();

        /// <summary>清廉统计：王国ID → officials_stats 串。</summary>
        public Dictionary<string, string> Corruption { get; set; } = new Dictionary<string, string>();

        /// <summary>王国史册：王国ID → chronicle 串。</summary>
        public Dictionary<string, string> Chronicle { get; set; } = new Dictionary<string, string>();

        /// <summary>叛乱标记：王国ID → "1"。</summary>
        public Dictionary<string, string> RebelNamed { get; set; } = new Dictionary<string, string>();

        // ═══════════════ 全局数据 ═══════════════

        /// <summary>进行中逐鹿战争附加数据：warId → 目标城市ID（静态字典落盘，防丢失）。</summary>
        public Dictionary<long, string> ActiveClaimWarTargets { get; set; } = new Dictionary<long, string>();
        public Dictionary<long, int> ActiveClaimWarStartYears { get; set; } = new Dictionary<long, int>();
        public Dictionary<long, int> ActiveIndependenceWarStartYears { get; set; } = new Dictionary<long, int>();

        /// <summary>赔款分期偿付计划：付款国ID → receiverId|付款国名|收款国名|年额|剩余|下次偿付年（2026-08-20）。</summary>
        public Dictionary<string, string> Reparations { get; set; } = new Dictionary<string, string>();

        /// <summary>已用年号列表。</summary>
        public List<string> EraUsed { get; set; } = new List<string>();

        // ═══════════════ 地基（身份/个人史册/派系骨架） ═══════════════

        /// <summary>个人史册：ActorId → JSON（PersonHistoryManager）。</summary>
        public Dictionary<long, string> PersonHistories { get; set; } = new Dictionary<long, string>();

        /// <summary>身份缓存：ActorId → "身份|出身|更新年"（IdentityClassifier）。</summary>
        public Dictionary<long, string> Identities { get; set; } = new Dictionary<long, string>();

        /// <summary>派系状态：王国ID → JSON（FactionManager）。</summary>
        public Dictionary<string, string> Factions { get; set; } = new Dictionary<string, string>();

        /// <summary>王室档案：王国ID → JSON（RoyalRegistry）。</summary>
        public Dictionary<string, string> RoyalFamilies { get; set; } = new Dictionary<string, string>();

        /// <summary>家族谱牒（主家+分支）：王国ID → JSON（FamilyRegistry，阶段3 F1）。</summary>
        public Dictionary<string, string> Families { get; set; } = new Dictionary<string, string>();

        // ═══════════════ 科举（KejuManager） ═══════════════

        /// <summary>科举设置：王国ID → JSON（KejuSettings）。</summary>
        public Dictionary<string, string> KejuSettings { get; set; } = new Dictionary<string, string>();

        /// <summary>人才库：王国ID → JSON（List&lt;TalentEntry&gt;）。</summary>
        public Dictionary<string, string> KejuTalentPool { get; set; } = new Dictionary<string, string>();

        /// <summary>武举队长记录：王国ID → JSON（List&lt;CaptainMark&gt;）。</summary>
        public Dictionary<string, string> KejuCaptains { get; set; } = new Dictionary<string, string>();

        /// <summary>武举替换冷却：王国ID → JSON（cityId → 截止年）。</summary>
        public Dictionary<string, string> KejuCityCool { get; set; } = new Dictionary<string, string>();

        /// <summary>新世界派系初始化标志（旧档=false 不生成派系）。</summary>
        public bool FactionFreshWorld { get; set; } = false;

        public Dictionary<string, string> FactionPolitics { get; set; } = new Dictionary<string, string>();
        public bool FactionPoliticsFreshWorld { get; set; } = false;

        // ═══════════════ 序列化 ═══════════════

        public string Serialize()
        {
            return JsonConvert.SerializeObject(this, Formatting.Indented);
        }

        public static ZhuluSnapshot Deserialize(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                return JsonConvert.DeserializeObject<ZhuluSnapshot>(json);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
