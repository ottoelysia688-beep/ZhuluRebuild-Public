using System;
using System.Collections.Generic;
using ZhuluRebuild.Core;
using ZhuluRebuild.Gameplay;
using ZhuluRebuild.Adapters;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Domain
{
    public class OfficeAppointmentRecord
    {
        public string KingdomId { get; set; }
        public string OfficeId { get; set; }
        public string PersonId { get; set; }
        public string PersonName { get; set; }
        public int AppointmentYear { get; set; }
        /// <summary>玩家任命保护期截止年（含）。AI 在该年之前不可替换；玩家随时可任免。</summary>
        public int ProtectionUntilYear { get; set; } = 0;
        public bool IsPlayerIntervention { get; set; } = true;
        public string BuffDescription { get; set; }

        // ── 阶段2D 地方官扩展（2026-08-16；快照兼容：旧记录 OfficeType 为空=central）──
        /// <summary>官职类型：central（中央官）/ local（地方官）；空=central。</summary>
        public string OfficeType { get; set; } = "central";
        /// <summary>政绩分 0-100（升迁/罢黜用；年累积）。</summary>
        public int PerformanceScore { get; set; } = 0;
        /// <summary>辖城 id 列表（逗号分隔；地方官辖区——税收/忠诚效果范围）。</summary>
        public string JurisdictionCities { get; set; } = "";
    }

    /// <summary>
    /// 百官任免。全部状态持久化到 custom_data（key: zlr_offices），随存档保存。
    /// 匹配铁律：一律使用王国 ID 精确键 —— 历史上按名字模糊盲搜会导致跨国错配官员。
    /// </summary>
    public class OfficeManager : ISnapshotSection<OfficeManager.OfficesBlobState>
    {
        public static OfficeManager Instance { get; } = new OfficeManager();

        private const string KEY_OFFICES = "offices";
        private const int PlayerProtectionYears = 3;

        /// <summary>七席官职唯一权威清单（丞相 + 六部）。内部 ID 永不变更，保证存档兼容。</summary>
        public static readonly string[] OfficesList = { "prime_minister", "personnel", "revenue", "rites", "military", "justice", "works" };

        // ── 阶段2D 地方官（2026-08-16；参考 AW2 5 类地方官，简化 3 类）──
        public const string LOCAL_XIANCHENG = "local_xiancheng"; // 县丞（等级 1）
        public const string LOCAL_JUNSHOU = "local_junshou";     // 郡守（等级 2）
        public const string LOCAL_CISHI = "local_cishi";         // 刺史（等级 3）

        /// <summary>地方官三席清单（阶段2S-4 俸禄入账用；城主为食邑制不入此列）。</summary>
        public static readonly string[] LocalOffices = { LOCAL_XIANCHENG, LOCAL_JUNSHOU, LOCAL_CISHI };

        /// <summary>
        /// 官员任期上限（2026-08-18 用户需求）：地方官/城主/军队队长 45 年任期届满自动卸任
        /// （"任期届满"；中央朝官另有 25 年致仕——见 ProcessAutoSelectionAndAppointment）。
        /// </summary>
        public const int TermLimitYears = 45;

        /// <summary>地方官职级（升迁链/级差忠诚用：县丞1→郡守2→刺史3→尚书4→丞相5）。</summary>
        public static int GetOfficeRank(string officeId)
        {
            switch (officeId)
            {
                case LOCAL_XIANCHENG: return 1;
                case LOCAL_JUNSHOU: return 2;
                case LOCAL_CISHI: return 3;
                case "prime_minister": return 5;
                default: return 4; // 六部尚书
            }
        }

        /// <summary>地方官配置（按爵位 tierId：邑/子/伯/侯/公/天命 → 数量）。</summary>
        public static string[] GetLocalSeats(string tierId)
        {
            switch (tierId)
            {
                case ZhuluTierService.TIER_GONG:
                case ZhuluTierService.TIER_TIANZI:
                case NomadicTierService.TIER_DAHANTING:
                case NomadicTierService.TIER_WANGTING:
                    return new[] { LOCAL_CISHI, LOCAL_CISHI, LOCAL_CISHI, LOCAL_CISHI }; // 刺史 ×4
                case ZhuluTierService.TIER_HOU:
                case NomadicTierService.TIER_HANGUO:
                    return new[] { LOCAL_JUNSHOU, LOCAL_JUNSHOU, LOCAL_JUNSHOU }; // 郡守 ×3
                case ZhuluTierService.TIER_BO:
                case NomadicTierService.TIER_BUMENG:
                    return new[] { LOCAL_JUNSHOU, LOCAL_JUNSHOU }; // 郡守 ×2
                case ZhuluTierService.TIER_ZI:
                case NomadicTierService.TIER_DABU:
                    return new[] { LOCAL_XIANCHENG }; // 县丞 ×1
                default:
                    return new string[0]; // 邑国不设地方官（城主兼任）
            }
        }

        /// <summary>是否地方官。</summary>
        public static bool IsLocalOffice(string officeId)
        {
            return officeId == LOCAL_XIANCHENG || officeId == LOCAL_JUNSHOU || officeId == LOCAL_CISHI;
        }

        // ───────────────────────────── 四等爵制官席 ─────────────────────────────

        /// <summary>国家等级枚举（按城池数量判定，天命国独立于四等之外）。</summary>
        public enum KingdomTier { Zi, Bo, Hou, Gong, Tianming }

        /// <summary>根据有效城池数判定国家等级（不含天命国判定）。</summary>
        public static KingdomTier GetTier(int effCities)
        {
            if (effCities >= 12) return KingdomTier.Gong;
            if (effCities >= 6) return KingdomTier.Hou;
            if (effCities >= 3) return KingdomTier.Bo;
            return KingdomTier.Zi;
        }

        /// <summary>
        /// 按王国判定国家等级（2026-08-18 政策域口径统一）：爵位特质权威
        /// （ZhuluTierService.GetKingdomTierId：君主特质优先→国力点回退）——
        /// 与爵位称号/国名后缀同源，消除"国名已称公国、政策仍按伯国"错位。
        /// 邑/子共档（家宰一席）。
        /// </summary>
        public static KingdomTier GetTier(Kingdom k)
        {
            if (k == null) return KingdomTier.Zi;
            switch (ZhuluTierService.GetKingdomTierId(k))
            {
                case ZhuluTierService.TIER_GONG: return KingdomTier.Gong;
                case ZhuluTierService.TIER_HOU: return KingdomTier.Hou;
                case ZhuluTierService.TIER_BO: return KingdomTier.Bo;
                case NomadicTierService.TIER_DAHANTING: return KingdomTier.Gong;
                case NomadicTierService.TIER_HANGUO: return KingdomTier.Hou;
                case NomadicTierService.TIER_BUMENG: return KingdomTier.Bo;
                default: return KingdomTier.Zi;
            }
        }

        /// <summary>含天命国判定的等级获取（特质权威版）：先检查是否持有天命。</summary>
        public static KingdomTier GetTierWithMandate(Kingdom k, bool isMandateHolder)
        {
            if (isMandateHolder) return KingdomTier.Tianming;
            return GetTier(k);
        }

        /// <summary>含天命国判定的等级获取：先检查是否持有天命，是则返回 Tianming，否则按城池数走四等。</summary>
        public static KingdomTier GetTierWithMandate(int effCities, bool isMandateHolder)
        {
            if (isMandateHolder) return KingdomTier.Tianming;
            return GetTier(effCities);
        }

        /// <summary>按爵位等级返回应设官席（天命国和公国均为七席）。</summary>
        public static string[] GetExpectedSeats(int effCities)
        {
            switch (GetTier(effCities))
            {
                case KingdomTier.Gong: return OfficesList; // 七席
                case KingdomTier.Hou: return new[] { "prime_minister", "personnel", "revenue", "military", "justice" }; // 五席
                case KingdomTier.Bo: return new[] { "prime_minister", "revenue", "military" }; // 三席
                default: return new[] { "prime_minister" }; // 一席（家宰）
            }
        }

        /// <summary>含天命国判定的官席获取（天命国也是七席）。</summary>
        public static string[] GetExpectedSeatsWithMandate(int effCities, bool isMandateHolder)
        {
            if (isMandateHolder) return OfficesList; // 天命国七席
            return GetExpectedSeats(effCities);
        }

        /// <summary>按已锁定体制等级取官席；两套体制共用同一席位数量，只更换官名和身份特质。</summary>
        public static string[] GetExpectedSeatsWithMandate(Kingdom k, bool isMandateHolder)
        {
            switch (GetTierWithMandate(k, isMandateHolder))
            {
                case KingdomTier.Tianming:
                case KingdomTier.Gong: return OfficesList;
                case KingdomTier.Hou: return new[] { "prime_minister", "personnel", "revenue", "military", "justice" };
                case KingdomTier.Bo: return new[] { "prime_minister", "revenue", "military" };
                default: return new[] { "prime_minister" };
            }
        }

        private int _lastScanYear = -1;

        private OfficeManager() { }

        /// <summary>按国家等级返回官职全称。公国=周礼六官，天命国=丞相六部。</summary>
        public static string GetOfficeTitle(string officeId, int effCities = 12, bool isMandateHolder = false)
        {
            // 阶段2D：地方官固定名（不随爵位变——县丞/郡守/刺史）
            if (officeId == LOCAL_XIANCHENG) return "县丞";
            if (officeId == LOCAL_JUNSHOU) return "郡守";
            if (officeId == LOCAL_CISHI) return "刺史";
            var tier = GetTierWithMandate(effCities, isMandateHolder);
            switch (tier)
            {
                case KingdomTier.Zi:
                    return "家宰";
                case KingdomTier.Bo:
                    return officeId switch
                    {
                        "prime_minister" => "上大夫",
                        "revenue" => "司徒",
                        "military" => "司马",
                        _ => "大夫"
                    };
                case KingdomTier.Hou:
                    return officeId switch
                    {
                        "prime_minister" => "相邦",
                        "personnel" => "司徒",
                        "revenue" => "司空",
                        "military" => "司马",
                        "justice" => "司寇",
                        _ => "卿"
                    };
                case KingdomTier.Gong: // 公国：周礼六官（仿古）
                    return officeId switch
                    {
                        "prime_minister" => "太宰",
                        "personnel" => "天官冢宰",
                        "revenue" => "地官司徒",
                        "rites" => "春官宗伯",
                        "military" => "夏官司马",
                        "justice" => "秋官司寇",
                        "works" => "冬官司空",
                        _ => "卿"
                    };
                default: // Tianming：丞相六部
                    return officeId switch
                    {
                        "prime_minister" => "丞相",
                        "personnel" => "吏部尚书",
                        "revenue" => "户部尚书",
                        "rites" => "礼部尚书",
                        "military" => "兵部尚书",
                        "justice" => "刑部尚书",
                        "works" => "工部尚书",
                        _ => "尚书"
                    };
            }
        }

        /// <summary>无等级上下文的默认全称（天命国制）。</summary>
        public static string GetOfficeTitle(string officeId)
        {
            return GetOfficeTitle(officeId, 12, true);
        }

        /// <summary>
        /// 按王国对象动态取官职全称（天命=丞相六部/公=周礼六官/侯=卿/伯=大夫/子=家宰）。
        /// 用于史册/记录/管道 Reason——避免无参版本固定"天命国制"导致子国显示"丞相"。
        /// </summary>
        public static string GetOfficeTitleForKingdom(string officeId, Kingdom k)
        {
            if (k == null) return GetOfficeTitle(officeId);
            int raw = k.cities != null ? k.cities.Count : 1;
            int eff = KingdomExt.GetEffectiveCities(k.id.ToString(), raw);
            if (KingdomCivilizationService.Instance.IsNomadic(k))
            {
                bool nomadicMandate = NomadicMandateManager.Instance.IsMandateHolder(k.id.ToString());
                return GetNomadicOfficeTitle(officeId, GetTierWithMandate(k, nomadicMandate));
            }
            bool isMandate = OrthodoxMandateManager.Instance.IsMandateHolder(k.id.ToString());
            return GetOfficeTitle(officeId, eff, isMandate);
        }

        /// <summary>游牧官名；官席数量与对应东方等级完全相同，只替换称谓。</summary>
        public static string GetNomadicOfficeTitle(string officeId, KingdomTier tier)
        {
            if (officeId == LOCAL_XIANCHENG) return "百户长";
            if (officeId == LOCAL_JUNSHOU) return "千户长";
            if (officeId == LOCAL_CISHI) return "万户长";
            if (tier == KingdomTier.Zi) return "部族管事";
            if (tier == KingdomTier.Bo)
            {
                if (officeId == "prime_minister") return "盟府断事官";
                if (officeId == "revenue") return "牧政官";
                if (officeId == "military") return "兵马官";
                return "盟府官";
            }
            if (tier == KingdomTier.Hou)
            {
                if (officeId == "prime_minister") return "国相";
                if (officeId == "personnel") return "选贤官";
                if (officeId == "revenue") return "牧政官";
                if (officeId == "military") return "兵马官";
                if (officeId == "justice") return "断事官";
                return "汗国官";
            }
            switch (officeId)
            {
                case "prime_minister": return "大断事官";
                case "personnel": return "选贤官";
                case "revenue": return "大司牧";
                case "rites": return "祭天官";
                case "military": return "兵马大总管";
                case "justice": return "断狱官";
                case "works": return "营造官";
                default: return "汗庭官";
            }
        }

        /// <summary>按国家等级返回官职简称。公国=周礼六官简称，天命国=六部简称。</summary>
        public static string GetOfficeTitleShort(string officeId, int effCities = 12, bool isMandateHolder = false)
        {
            // 阶段2M：地方官固定名（与 GetOfficeTitle 一致——防"卿"误显示）
            if (officeId == LOCAL_XIANCHENG) return "县丞";
            if (officeId == LOCAL_JUNSHOU) return "郡守";
            if (officeId == LOCAL_CISHI) return "刺史";
            var tier = GetTierWithMandate(effCities, isMandateHolder);
            switch (tier)
            {
                case KingdomTier.Zi:
                    return "家宰";
                case KingdomTier.Bo:
                    return officeId switch
                    {
                        "prime_minister" => "上大夫",
                        "revenue" => "司徒",
                        "military" => "司马",
                        _ => "大夫"
                    };
                case KingdomTier.Hou:
                    return officeId switch
                    {
                        "prime_minister" => "相邦",
                        "personnel" => "司徒",
                        "revenue" => "司空",
                        "military" => "司马",
                        "justice" => "司寇",
                        _ => "卿"
                    };
                case KingdomTier.Gong: // 公国：周礼六官简称
                    return officeId switch
                    {
                        "prime_minister" => "太宰",
                        "personnel" => "冢宰",
                        "revenue" => "司徒",
                        "rites" => "宗伯",
                        "military" => "司马",
                        "justice" => "司寇",
                        "works" => "司空",
                        _ => "卿"
                    };
                default: // Tianming：丞相六部简称
                    return officeId switch
                    {
                        "prime_minister" => "丞相",
                        "personnel" => "吏部",
                        "revenue" => "户部",
                        "rites" => "礼部",
                        "military" => "兵部",
                        "justice" => "刑部",
                        "works" => "工部",
                        _ => "尚书"
                    };
            }
        }

        /// <summary>无等级上下文的默认简称（天命国制）。</summary>
        public static string GetOfficeTitleShort(string officeId)
        {
            return GetOfficeTitleShort(officeId, 12, true);
        }

        /// <summary>官职评分序号（0~5 六部，6 丞相），供 OfficialRatingManager 使用。</summary>
        public static int GetOfficeIndex(string officeId)
        {
            return officeId switch
            {
                "personnel" => 0,
                "revenue" => 1,
                "rites" => 2,
                "military" => 3,
                "justice" => 4,
                "works" => 5,
                "prime_minister" => 6,
                _ => 6
            };
        }

        // ───────────────────────────── 持久化读写 ─────────────────────────────

        /// <summary>公开接口：获取王国全部在任官员记录（供 CorruptionManager 等外部读取）。</summary>
        public List<OfficeAppointmentRecord> GetRecordsPublic(string kingdomId) => LoadRecords(kingdomId);

        private List<OfficeAppointmentRecord> LoadRecords(string kingdomId)
        {
            var result = new List<OfficeAppointmentRecord>();
            Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
            if (k == null) return result;

            string raw = KingdomDataExt.GetStr(k, KEY_OFFICES);
            if (string.IsNullOrEmpty(raw)) return result;

            foreach (var chunk in raw.Split('|'))
            {
                if (string.IsNullOrEmpty(chunk)) continue;
                var parts = chunk.Split(':');
                if (parts.Length < 6) continue;

                // 阶段2D 序列化升级：段 8/9/10 = 政绩/辖城/类型（旧 7 段兼容：政绩默认 40 防秒罢黜）
                int perf = 40;
                if (parts.Length > 7) int.TryParse(parts[7], out perf);
                result.Add(new OfficeAppointmentRecord
                {
                    KingdomId = kingdomId,
                    OfficeId = parts[0],
                    PersonId = parts[1],
                    PersonName = parts[2],
                    AppointmentYear = int.TryParse(parts[3], out int ay) ? ay : 1,
                    ProtectionUntilYear = int.TryParse(parts[4], out int py) ? py : 0,
                    IsPlayerIntervention = parts[5] == "1",
                    BuffDescription = parts.Length > 6 ? parts[6] : "",
                    PerformanceScore = perf,
                    JurisdictionCities = parts.Length > 8 ? parts[8] : "",
                    OfficeType = parts.Length > 9 ? parts[9] : (parts[0].StartsWith("local_") ? "local" : "central")
                });
            }
            return result;
        }

        private void SaveRecords(string kingdomId, List<OfficeAppointmentRecord> records)
        {
            Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
            if (k == null) return;

            var chunks = new List<string>();
            foreach (var r in records)
            {
                string safeName = (r.PersonName ?? "").Replace(":", "：").Replace("|", "¦");
                string safeBuff = (r.BuffDescription ?? "").Replace(":", "：").Replace("|", "¦");
                string safeJuris = (r.JurisdictionCities ?? "").Replace(":", "：").Replace("|", "¦");
                // 阶段2D：段 8/9/10 = 政绩/辖城/类型（旧存档 7 段兼容）
                chunks.Add($"{r.OfficeId}:{r.PersonId}:{safeName}:{r.AppointmentYear}:{r.ProtectionUntilYear}:{(r.IsPlayerIntervention ? "1" : "0")}:{safeBuff}:{r.PerformanceScore}:{safeJuris}:{(r.OfficeType ?? "central")}");
            }
            KingdomDataExt.SetStr(k, KEY_OFFICES, string.Join("|", chunks));
        }

        // ───────────────────────────── 查询与任免 ─────────────────────────────

        public OfficeAppointmentRecord GetAppointment(string kingdomId, string officeId)
        {
            if (string.IsNullOrEmpty(kingdomId) || string.IsNullOrEmpty(officeId)) return null;

            foreach (var r in LoadRecords(kingdomId))
            {
                if (r.OfficeId == officeId) return r;
            }
            return null;
        }

        public bool AppointOfficer(string kingdomId, string kingdomName, string officeId, string personId, string personName, int currentYear, bool isPlayer = true)
        {
            if (string.IsNullOrEmpty(kingdomId) || string.IsNullOrEmpty(officeId) || string.IsNullOrEmpty(personId)) return false;

            // 防御：国王不得兼任官职（入口拦截，防止任何路径把国王任命为官）
            try
            {
                Kingdom kk = KingdomDataExt.FindKingdomById(kingdomId);
                if (kk?.king != null && kk.king.isAlive() && kk.king.getID().ToString() == personId)
                {
                    ZhuluContext.Instance.LogWarning($"[OfficeManager] 拒绝任命国王 {personName} 兼任官职");
                    return false;
                }
            }
            catch { }

            var records = LoadRecords(kingdomId);
            var existing = records.Find(r => r.OfficeId == officeId);
            if (existing != null && !isPlayer && currentYear <= existing.ProtectionUntilYear)
            {
                return false; // 玩家任命保护期内，AI 不得替换
            }

            // 官职 Buff 定义（channel 一律走 ModifierChannels 常量，杜绝悬空通道）
            // 官职名按国家等级动态取（子=家宰/伯=大夫/侯=卿/公=周礼/天命=丞相六部），勿用无参默认（固定天命制）
            string officeTitle = GetOfficeTitleForKingdom(officeId, KingdomDataExt.FindKingdomById(kingdomId));
            var (channel, additive) = GetOfficeBuff(officeId);
            string buffDesc = officeId switch
            {
                "prime_minister" => "[丞相效果] 全国城市忠诚 +10，总揽朝政",
                "personnel" => "[吏部效果] 贤才拔擢 +20%，科举录取名额加成",
                "revenue" => "[户部效果] 国库进账 +15%，农田作物成熟加速",
                "rites" => "[礼部效果] 列国外交好感 +15，天命昭彰",
                "military" => "[兵部效果] 兵员上限 +20%，征兵动员加速",
                "justice" => "[刑部效果] 阴谋叛乱镇压 +25%，地方治安安定",
                "works" => "[工部效果] 建筑耐久 +20%，营造修缮提速",
                // 阶段2D 地方官（3 类——职责描述）
                "local_xiancheng" => "[县丞] 治理一县：辖城税收 +1%、忠诚 +1；政绩年积，可升郡守",
                "local_junshou" => "[郡守] 坐镇一郡：辖城税收 +1%、忠诚 +1；政绩年积，可升刺史",
                "local_cishi" => "[刺史] 巡按一方：辖城税收 +1%、忠诚 +1；政绩年积，可入朝为尚书",
                _ => ""
            };

            var newRecord = new OfficeAppointmentRecord
            {
                KingdomId = kingdomId,
                OfficeId = officeId,
                PersonId = personId,
                PersonName = personName,
                AppointmentYear = currentYear,
                ProtectionUntilYear = isPlayer ? currentYear + PlayerProtectionYears : 0,
                IsPlayerIntervention = isPlayer,
                BuffDescription = buffDesc,
                // 阶段2D ④：新官政绩起点 40；阶段2E ⑦：进士/状元起点 50/60（科举入仕直升）
                PerformanceScore = ComputeStartPerformance(personId)
            };

            records.RemoveAll(r => r.OfficeId == officeId);
            records.Add(newRecord);
            SaveRecords(kingdomId, records);

            // 阶段1 R9：乘算通道必须写 Multiplicative（防 Additive 语义被基值变化破坏）
            bool isMultChannel = Gameplay.ModifierChannels.MultiplierChannels.Contains(channel);
            ModifierPipeline.Instance.RegisterPlan(new ModifierPlan
            {
                SourceId = $"office_{kingdomId}_{officeId}",
                TargetKingdomId = kingdomId,
                Channel = channel,
                Additive = isMultChannel ? 0f : additive,
                Multiplicative = isMultChannel ? 1f + additive : 1.0f,
                Reason = $"{officeTitle} {personName} 登席履职"
            });

            ZhuluContext.Instance.LogInfo($"[OfficeManager] 任命官员: 王国={kingdomName}({kingdomId}), 官职={officeTitle}, 姓名={personName}");

            // 2026-08-19 中央官入朝：所属城市改为首都（京官定居王畿；地方官不动）
            if (!IsLocalOffice(officeId))
            {
                try
                {
                    Kingdom kApp = KingdomDataExt.FindKingdomById(kingdomId);
                    City capital = kApp?.capital;
                    if (capital != null && long.TryParse(personId, out long centralPid))
                    {
                        Actor centralActor = World.world?.units?.get(centralPid);
                        if (centralActor != null && centralActor.isAlive() && centralActor.city != capital)
                        {
                            if (Naming.ActorAccess.SetHomeCity(centralActor, capital))
                            {
                                ZhuluContext.Instance.LogInfo($"[OfficeManager] 中央官入朝定居: {personName} 迁居首都 {capital.data?.name}");
                            }
                        }
                    }
                }
                catch (Exception exCap)
                {
                    ZhuluContext.Instance.LogWarning($"[OfficeManager] 中央官迁都异常: {exCap.Message}");
                }
            }

            // 史册播报按国家等级区分措辞
            Kingdom chronicleK = KingdomDataExt.FindKingdomById(kingdomId);
            int chronicleCities = chronicleK != null && chronicleK.cities != null ? chronicleK.cities.Count : 1;
            var chronicleTier = GetTier(KingdomExt.GetEffectiveCities(kingdomId, chronicleCities));
            string chronicleText = chronicleTier switch
            {
                KingdomTier.Zi => $"<color=#6ca3fd>{personName}</color> 受命为【{officeTitle}】，掌理全族事务。",
                KingdomTier.Bo => $"<color=#6ca3fd>{personName}</color> 拜为【{officeTitle}】，总揽邦国政务。",
                KingdomTier.Hou => $"<color=#6ca3fd>{personName}</color> 受命为【{officeTitle}】，统领百官。",
                _ => $"<color=#6ca3fd>{personName}</color> 受命为【{officeTitle}】，开府仪同三司。"
            };

            ChronicleManager.Instance.AddEntry(
                currentYear,
                ChronicleEventType.OfficeAppointed,
                kingdomId,
                kingdomName,
                personName,
                chronicleText,
                false
            );

            // 地基联动：个人史册（机遇）+ 身份即时刷新
            try
            {
                if (long.TryParse(personId, out long personL))
                {
                    PersonHistoryManager.Instance.MarkDetailed(personL, currentYear);
                    PersonHistoryManager.Instance.RecordEvent(personL, PersonHistoryManager.CAT_SHITU,
                        $"受命为【{officeTitle}】（{kingdomName}）", currentYear);
                    Kingdom kk2 = KingdomDataExt.FindKingdomById(kingdomId);
                    Actor personActor = World.world?.units?.get(personL);
                    if (kk2 != null && personActor != null)
                    {
                        IdentityClassifier.Instance.RefreshActor(kk2, personActor, currentYear);
                    }
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[OfficeManager] 任命个人史册联动异常: {ex.Message}");
            }

            return true;
        }

        public bool DismissOfficer(string kingdomId, string kingdomName, string officeId, int currentYear, string reason = "罢免")
        {
            var records = LoadRecords(kingdomId);
            var record = records.Find(r => r.OfficeId == officeId);
            if (record == null) return false;

            string officeTitle = GetOfficeTitleForKingdom(officeId, KingdomDataExt.FindKingdomById(kingdomId));
            ModifierPipeline.Instance.RemovePlan($"office_{kingdomId}_{officeId}");
            CorruptionManager.Instance.ClearOfficerStats(kingdomId, officeId);
            records.Remove(record);
            SaveRecords(kingdomId, records);

            PolicyManager.Instance.OnOfficerVacated(kingdomId, kingdomName, officeId, currentYear);

            string chronicleText = reason switch
            {
                "薨" => $"【{officeTitle}】<color=#e0554a>{record.PersonName}</color> 薨于任上，官席恢复空缺。",
                "致仕" => $"【{officeTitle}】<color=#6ca3fd>{record.PersonName}</color> 任职期满，致仕归乡。官席恢复空缺。",
                _ => $"罢免 <color=#e0554a>{record.PersonName}</color> 的【{officeTitle}】官职，官席恢复空缺。"
            };

            ChronicleManager.Instance.AddEntry(
                currentYear,
                ChronicleEventType.OfficeAppointed,
                kingdomId,
                kingdomName,
                record.PersonName,
                chronicleText,
                false
            );

            // 地基联动：个人史册（挫折）+ 身份即时刷新
            try
            {
                if (long.TryParse(record.PersonId, out long personL))
                {
                    PersonHistoryManager.Instance.RecordEvent(personL, PersonHistoryManager.CAT_SHITU,
                        $"{reason}：【{officeTitle}】", currentYear);
                    Kingdom kk2 = KingdomDataExt.FindKingdomById(kingdomId);
                    Actor personActor = World.world?.units?.get(personL);
                    if (kk2 != null && personActor != null)
                    {
                        IdentityClassifier.Instance.RefreshActor(kk2, personActor, currentYear);
                    }
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[OfficeManager] 罢免个人史册联动异常: {ex.Message}");
            }

            return true;
        }

        /// <summary>世界载入后恢复全部官职 Buff 的管道注入（每年主 Tick 调用一次，RegisterPlan 幂等去重）。</summary>
        public void RehydratePipelineAll()
        {
            if (World.world?.kingdoms?.list == null) return;
            foreach (Kingdom k in World.world.kingdoms.list)
            {
                if (k == null || !k.isAlive()) continue;
                string kId = k.id.ToString();
                string kName = k.name ?? "本国";
                foreach (var r in LoadRecords(kId))
                {
                    // 重新走任命通道注册（不改动任职记录，只补管道）
                    var def = GetOfficeBuff(r.OfficeId);
                    if (def.channel == null) continue;
                    ModifierPipeline.Instance.RegisterPlan(new ModifierPlan
                    {
                        SourceId = $"office_{kId}_{r.OfficeId}",
                        TargetKingdomId = kId,
                        Channel = def.channel,
                        Additive = def.additive,
                        Multiplicative = 1.0f,
                        Reason = $"{GetOfficeTitleForKingdom(r.OfficeId, k)} {r.PersonName} 登席履职"
                    });
                }
            }
        }

        private (string channel, float additive) GetOfficeBuff(string officeId)
        {
            switch (officeId)
            {
                case "prime_minister": return (ModifierChannels.CityLoyaltyBonus, 10f);
                case "personnel": return (ModifierChannels.CivilExamEfficiency, 0.20f); // 科举录取名额加成（KejuManager 消费，阶段1 R3 接线）
                case "revenue": return (ModifierChannels.GoldIncomeMultiplier, 0.15f);
                case "rites": return (ModifierChannels.DiplomaticOpinionBonus, 15f);
                case "military": return (ModifierChannels.MilitaryReadiness, 0.20f);
                case "justice": return (ModifierChannels.CityLoyaltyBonus, 8f); // 阶段1 R3：原 RebellionSuppression 死通道→司法公正安民
                case "works": return (ModifierChannels.BuildingHealthMultiplier, 0.20f);
                default: return (null, 0f);
            }
        }

        /// <summary>
        /// 自动化百官推举：每个【游戏年】至多扫描一次（内部按年去重），
        /// 遍历各国国民，对空缺官席选拔最高分者补位，并清理已故官员。
        /// </summary>
        /// <summary>
        /// 2026-08-19 串联：门第速查表——actorId → 家声（每国每年一次构建，O(家族总成员)）。
        /// 范围与 FindFamilyOfActor 一致（主家+分支）；望族走 Notables 不在此列。
        /// </summary>
        private static Dictionary<long, int> BuildPedigreeMap(string kingdomId)
        {
            var map = new Dictionary<long, int>();
            try
            {
                var kf = FamilyRegistry.Instance.GetKingdomFamilies(kingdomId);
                if (kf == null) return map;
                var fams = new List<FamilyRegistry.FamilyData>();
                if (kf.Main != null) fams.Add(kf.Main);
                if (kf.Branches != null) fams.AddRange(kf.Branches);
                foreach (var f in fams)
                {
                    if (f?.Members == null) continue;
                    foreach (var m in f.Members)
                    {
                        if (m != null && m.ActorId != 0 && !map.ContainsKey(m.ActorId))
                            map[m.ActorId] = f.Prestige;
                    }
                }
            }
            catch { }
            return map;
        }

        public void ProcessAutoSelectionAndAppointment(int currentYear)
        {
            if (currentYear == _lastScanYear) return;
            _lastScanYear = currentYear;

            try
            {
                if (World.world?.kingdoms?.list == null) return;

                foreach (Kingdom kingdom in World.world.kingdoms.list)
                {
                    if (kingdom == null || !kingdom.isAlive() || kingdom.units == null || kingdom.units.Count == 0) continue;
                    if (Domain.ZhuluRebelService.IsRebel(kingdom)) continue; // 阶段2V：叛军无百官体制


                    string kingdomId = kingdom.id.ToString();
                    string kingdomName = kingdom.name ?? "本国";
                    bool isMandate = PolicyManager.IsMandateHolderForKingdom(kingdom);
                    string[] expectedSeats = GetExpectedSeatsWithMandate(kingdom, isMandate);

                    var citizens = WorldBoxReadOnlyAdapter.Instance.CaptureKingdomCitizens(kingdomId);
                    if (citizens == null || citizens.Count == 0) continue;

                    // 2026-08-19 串联：门第速查表（每国每年一次构建——避免每候选遍历家族成员链）
                    var pedigreeMap = BuildPedigreeMap(kingdomId);

                    // 收集已在任的国民 ID，兼任过滤
                    HashSet<string> busyPersonIds = new HashSet<string>();
                    foreach (var officeId in OfficesList)
                    {
                        var app = GetAppointment(kingdomId, officeId);
                        if (app != null && !string.IsNullOrEmpty(app.PersonId))
                        {
                            busyPersonIds.Add(app.PersonId);
                        }
                    }

                    foreach (var officeId in expectedSeats)
                    {
                        var app = GetAppointment(kingdomId, officeId);

                        // A. 已有官员：检查是否依旧在世
                        if (app != null)
                        {
                            bool isStillAlive = false;
                            foreach (var cit in citizens)
                            {
                                if (cit.ActorId == app.PersonId)
                                {
                                    isStillAlive = true;
                                    break;
                                }
                            }

                            if (!isStillAlive)
                            {
                                DismissOfficer(kingdomId, kingdomName, officeId, currentYear, "薨");
                                app = null;
                            }
                            else if (currentYear - app.AppointmentYear >= 25 && currentYear > app.ProtectionUntilYear)
                            {
                                // 任期超过 25 年自动致仕（保护期过后）
                                DismissOfficer(kingdomId, kingdomName, officeId, currentYear, "致仕");
                                app = null;
                            }
                        }

                        // B. 官席空缺：自动选拔最高分者上位
                        if (app == null)
                        {
                            int officeIdx = GetOfficeIndex(officeId);
                            ActorFactSnapshot bestCandidate = null;
                            int maxScore = -1;

                            foreach (var cit in citizens)
                            {
                                if (busyPersonIds.Contains(cit.ActorId)) continue;
                                // 国王不得兼任官职（国王属性通常最高，不排除会被自动选拔上位）
                                if (kingdom.king != null && kingdom.king.isAlive()
                                    && cit.ActorId == kingdom.king.getID().ToString()) continue;

                                var (score, _) = OfficialRatingManager.CalcOfficialScore(
                                    cit.Intelligence, cit.Stewardship, cit.Military, cit.Diplomacy, cit.Level, officeIdx, cit.Age);

                                // 2026-08-19 串联：门第提名修正——簪缨世家 +2 / 衣冠名族 +1 / 罪籍之家 -2
                                // （家族首次反向影响官制提名；次级修正不覆盖功名主导）
                                try
                                {
                                    if (long.TryParse(cit.ActorId, out long famAid)
                                        && pedigreeMap.TryGetValue(famAid, out int pres))
                                    {
                                        if (pres >= 80) score += 2;
                                        else if (pres >= 60) score += 1;
                                        else if (pres < 20) score -= 2;
                                    }
                                }
                                catch { }

                                if (score > maxScore)
                                {
                                    maxScore = score;
                                    bestCandidate = cit;
                                }
                            }

                            if (bestCandidate != null)
                            {
                                busyPersonIds.Add(bestCandidate.ActorId);
                                AppointOfficer(kingdomId, kingdomName, officeId, bestCandidate.ActorId, bestCandidate.Name, currentYear, isPlayer: false);
                                ZhuluContext.Instance.LogInfo($"[AutoAppointment] 为【{kingdomName}】选拔【{bestCandidate.Name}】(得分 {maxScore}分) 自动补位【{GetOfficeTitleForKingdom(officeId, KingdomDataExt.FindKingdomById(kingdomId))}】");
                            }
                        }
                    }

                    // ── 阶段2D ②：地方官补位（按爵位设缺；与中央官不同——不依赖官职权重表，用吏治评分）──
                    try
                    {
                        string tierId = ZhuluTierService.GetTierId(ZhuluTierService.GetPowerPoints(kingdom));
                        string[] localSeats = GetLocalSeats(tierId);
                        foreach (var localOfficeId in localSeats)
                        {
                            var localApp = GetAppointment(kingdomId, localOfficeId);
                            if (localApp != null && !string.IsNullOrEmpty(localApp.PersonId))
                            {
                                // 已有人在任：检查存活
                                bool alive = false;
                                try
                                {
                                    var p = long.TryParse(localApp.PersonId, out long pid) ? World.world?.units?.get(pid) : null;
                                    alive = p != null && p.isAlive();
                                }
                                catch { }
                                if (!alive)
                                {
                                    DismissOfficer(kingdomId, kingdomName, localOfficeId, currentYear, "亡故");
                                    localApp = null;
                                }
                                // 2026-08-18 任期限制：地方官 45 年任期届满 → 卸任（玩家干预保护期内不卸）
                                else if (currentYear - localApp.AppointmentYear >= TermLimitYears
                                    && currentYear > localApp.ProtectionUntilYear)
                                {
                                    DismissOfficer(kingdomId, kingdomName, localOfficeId, currentYear, "任期届满");
                                    ZhuluContext.Instance.LogInfo($"[AutoAppointment] 地方官 {localApp.PersonName} 任期届满（{TermLimitYears}年）卸任");
                                    localApp = null;
                                }
                            }
                            if (localApp != null && !string.IsNullOrEmpty(localApp.PersonId)) continue;

                            // 选人：非官员/非国王（地方官给寒门机会——科举衔接在 2D-④）
                            Actor bestLocal = null;
                            int bestLocalScore = -1;
                            if (kingdom.units != null)
                            {
                                foreach (Actor u in kingdom.units)
                                {
                                    if (u == null || !u.isAlive()) continue;
                                    if (u == kingdom.king) continue;
                                    if (busyPersonIds.Contains(u.getID().ToString())) continue;
                                    // 已任中央官或地方官
                                    bool already = false;
                                    foreach (var oid in OfficesList)
                                    {
                                        var oa = GetAppointment(kingdomId, oid);
                                        if (oa != null && oa.PersonId == u.getID().ToString()) { already = true; break; }
                                    }
                                    if (!already)
                                    {
                                        foreach (var oid in localSeats)
                                        {
                                            var oa = GetAppointment(kingdomId, oid);
                                            if (oa != null && oa.PersonId == u.getID().ToString()) { already = true; break; }
                                        }
                                    }
                                    if (already) continue;
                                    int s = u.stewardship + u.intelligence; // 吏治：管理+智力
                                    // 阶段2E ⑦：功名加成（科举入仕优先——进士>举人>秀才）
                                    try
                                    {
                                        if (u.hasTrait(TraitManager.TRAIT_ZHUANGYUAN)) s += 20;
                                        else if (u.hasTrait(TraitManager.TRAIT_JINSHI)) s += 15;
                                        else if (u.hasTrait(TraitManager.TRAIT_JUREN)) s += 10;
                                        else if (u.hasTrait(TraitManager.TRAIT_XIUCAI)) s += 5;
                                    }
                                    catch { }
                                    if (s > bestLocalScore) { bestLocalScore = s; bestLocal = u; }
                                }
                            }
                            if (bestLocal != null)
                            {
                                busyPersonIds.Add(bestLocal.getID().ToString());
                                AppointOfficer(kingdomId, kingdomName, localOfficeId, bestLocal.getID().ToString(), bestLocal.getName() ?? "官", currentYear, isPlayer: false);
                                // 辖城分配（除首都均分——简化：全部非首都城）
                                var cities = kingdom.cities;
                                var juris = new System.Collections.Generic.List<string>();
                                if (cities != null)
                                {
                                    foreach (var c in cities)
                                    {
                                        if (c == null) continue;
                                        if (kingdom.capital != null && c.id == kingdom.capital.id) continue;
                                        juris.Add(c.id.ToString());
                                    }
                                }
                                var rec = GetAppointment(kingdomId, localOfficeId);
                                if (rec != null) rec.JurisdictionCities = string.Join(",", juris);
                                ZhuluContext.Instance.LogInfo($"[AutoAppointment] 地方官补位: {bestLocal.getName()} → {GetOfficeTitleForKingdom(localOfficeId, kingdom)}（{tierId} 爵）");
                            }
                        }
                    }
                    catch (Exception ex2)
                    {
                        ZhuluContext.Instance.LogWarning($"[OfficeManager] 地方官补位异常: {ex2.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[OfficeManager] 自动化百官推举上位发生异常", ex);
            }
        }
    
        // ═══════════════ 阶段2D ③ 地方官效果 ═══════════════

        /// <summary>
        /// 地方官效果注册（年度调用，幂等）：
        /// 每个地方官 → 全国税收 +1%（GoldIncomeMultiplier）且 城市忠诚 +1（CityLoyaltyBonus）。
        /// 简化实现（王国级管道——逐鹿税收/忠诚均为全国聚合，无城市级）；
        /// 辖区限定（JurisdictionCities 精确效果）留待后续。
        /// </summary>
        public void RegisterLocalEffects(string kingdomId)
        {
            try
            {
                int localCount = 0;
                var all = GetAllAppointments(kingdomId);
                if (all != null)
                {
                    foreach (var rec in all)
                    {
                        if (rec == null) continue;
                        if (IsLocalOffice(rec.OfficeId) && !string.IsNullOrEmpty(rec.PersonId))
                            localCount++;
                    }
                }
                if (localCount <= 0) return;

                // 税收：每地方官 +1%（Multiplicative 语义）
                Gameplay.ModifierPipeline.Instance.RegisterPlan(new Gameplay.ModifierPlan
                {
                    SourceId = "office_local_tax_" + kingdomId,
                    TargetKingdomId = kingdomId,
                    Channel = Gameplay.ModifierChannels.GoldIncomeMultiplier,
                    Additive = 0f,
                    Multiplicative = 1f + localCount * 0.01f,
                    Reason = $"地方官治理（{localCount} 名）：全国税收 +{localCount}%"
                });
                // 忠诚：每地方官 +1
                Gameplay.ModifierPipeline.Instance.RegisterPlan(new Gameplay.ModifierPlan
                {
                    SourceId = "office_local_loyal_" + kingdomId,
                    TargetKingdomId = kingdomId,
                    Channel = Gameplay.ModifierChannels.CityLoyaltyBonus,
                    Additive = localCount,
                    Multiplicative = 1.0f,
                    Reason = $"地方官治理（{localCount} 名）：城市忠诚 +{localCount}"
                });
            }
            catch { }
        }

        /// <summary>
        /// 阶段2D ④：年度政绩结算与升迁/罢黜（YearDriver 按国调用）。
        /// 政绩年增：地方官 +2（治理）/尚书 +1（政务）；升迁：政绩≥60 且任期≥5 年；
        /// 罢黜：政绩<20 持续 3 年。升迁链：县丞→郡守→刺史→尚书→丞相。
        /// </summary>
        public void TickPerformanceAndPromotion(string kingdomId, string kingdomName, int year)
        {
            try
            {
                Kingdom kk = KingdomDataExt.FindKingdomById(kingdomId);
                var records = GetRecordsPublic(kingdomId);
                if (records == null) return;
                // 阶段2U 修复：GetRecordsPublic 返回解析副本——政绩修改必须显式落盘（原从不保存，
                // 政绩永远停在起点40，升迁体系整体失效——2N W9"官员活不到升迁"调参的真根因）。
                // 升迁/罢黜走 Dismiss/Appoint（内部自存），其席位本循环内跳过合并。
                var handledByAction = new HashSet<string>();
                // P2-30 串联①扩展（2026-08-23）：本年度占城折算基线——每国每年只结算一次，
                // 循环内多个武官共享同一份占城政绩（防同一年多官重复消费同一增量）。
                // 量纲校准：文档原拟"占城×5"，但逐鹿政绩年增基线仅 +1/+2（升迁门槛50），
                // 一役占2城即+10 会破坏升迁节奏——按自家量纲收敛为 占城×2 封顶10。
                int conquestMerit = 0;
                try
                {
                    int nowCities = kk.cities != null ? kk.cities.Count : 0;
                    int lastCities = (int)KingdomDataExt.GetLong(kk, "mil_conv_base", -1);
                    if (lastCities >= 0 && nowCities > lastCities)
                        conquestMerit = Math.Min(10, (nowCities - lastCities) * 2);
                    KingdomDataExt.SetLong(kk, "mil_conv_base", nowCities);
                }
                catch { }
                foreach (var rec in records)
                {
                    if (rec == null || string.IsNullOrEmpty(rec.PersonId)) continue;
                    // 政绩年增（地方官治理 +2；中央官政务 +1）− 腐败修正（贪官减政绩）
                    int gain = IsLocalOffice(rec.OfficeId) ? 2 : 1;
                    try
                    {
                        float corr = CorruptionManager.Instance.GetPower(kingdomId, rec.OfficeId);
                        if (corr >= 60f) gain -= 3;
                        else if (corr >= 40f) gain -= 1;
                    }
                    catch { }
                    // P2-30 串联①扩展（2026-08-23）：战功入仕途推广到全体武官——
                    // 判定 = 军事席官员 或 身份为武将（IdentityClassifier，含武举出身/队长城主）的官员。
                    // 折算 = 年度击杀差÷8 封顶5（原兵部尚书口径，每官每人独立基线防重复累计）
                    //        + 本年度王国占城折算（占城×2 封顶10，全国共享一份见循环外）。
                    // 旧"仅兵部尚书"窄规则被本块取代（其存储键 mil_kills_military 自然废弃不再读取）。
                    bool isMilitaryOfficer = rec.OfficeId == "military";
                    if (!isMilitaryOfficer && long.TryParse(rec.PersonId, out long gid0))
                        isMilitaryOfficer = IdentityClassifier.Instance.GetIdentity(gid0) == IdentityClassifier.IdentityType.Military;
                    if (isMilitaryOfficer)
                    {
                        try
                        {
                            if (long.TryParse(rec.PersonId, out long mid2))
                            {
                                var ma = World.world?.units?.get(mid2);
                                if (ma != null && ma.isAlive())
                                {
                                    int kills = OfficialRatingManager.GetActorKills(ma);
                                    string killKey = "mil_kills_" + rec.OfficeId + "_" + rec.PersonId;
                                    int lastKills = (int)KingdomDataExt.GetLong(kk, killKey, 0);
                                    int deltaKills = Math.Max(0, kills - lastKills);
                                    KingdomDataExt.SetLong(kk, killKey, kills);
                                    int merit = Math.Min(5, deltaKills / 8) + conquestMerit;
                                    if (merit > 0)
                                        gain += merit;
                                }
                            }
                        }
                        catch { }
                    }
                    rec.PerformanceScore = Math.Max(0, Math.Min(100, rec.PerformanceScore + gain));

                    int tenure = year - rec.AppointmentYear;
                    // 2026-08-16 修复：升迁排除高龄（≥60 不升——防"升迁后立即告老"矛盾）
                    int officerAge = 0;
                    try
                    {
                        if (long.TryParse(rec.PersonId, out long opid))
                        {
                            var op = World.world?.units?.get(opid);
                            if (op != null) officerAge = op.getAge();
                        }
                    }
                    catch { }
                    // 阶段2N W9 调参（候选A 定稿）：政绩≥50 + 任期≥3（原 60/5 在 20 倍速下官员活不到升迁——
                    // 寿命50-70，40起点+2/年爬到60需10年，再等5年任期大多已死）
                    if (tenure >= 3 && rec.PerformanceScore >= 50 && officerAge < 60 && IsLocalOffice(rec.OfficeId))
                    {
                        // 升迁：县丞→郡守→刺史→尚书（尚书以上停）
                        string next = null;
                        if (rec.OfficeId == LOCAL_XIANCHENG) next = LOCAL_JUNSHOU;
                        else if (rec.OfficeId == LOCAL_JUNSHOU) next = LOCAL_CISHI;
                        else if (rec.OfficeId == LOCAL_CISHI) next = "revenue"; // 刺史→户部尚书（理财入朝）
                        if (next != null)
                        {
                            // 目标席空位才升（有人的话暂缓）
                            var targetApp = GetAppointment(kingdomId, next);
                            if (targetApp == null || string.IsNullOrEmpty(targetApp.PersonId))
                            {
                                DismissOfficer(kingdomId, kingdomName, rec.OfficeId, year, "升迁");
                                AppointOfficer(kingdomId, kingdomName, next, rec.PersonId, rec.PersonName ?? "官", year, isPlayer: false);
                                // 阶段2U：升迁后政绩继承必须落盘（Dismiss/Appoint 内部各自 Load-Save，此后再改须重新取最新）
                                var latest = LoadRecords(kingdomId);
                                var newRec = latest.Find(r => r.OfficeId == next);
                                if (newRec != null)
                                {
                                    newRec.PerformanceScore = rec.PerformanceScore;
                                    SaveRecords(kingdomId, latest);
                                }
                                handledByAction.Add(rec.OfficeId);
                                ZhuluContext.Instance.LogInfo($"[OfficeManager] 升迁: {rec.PersonName} {GetOfficeTitle(rec.OfficeId)} → {GetOfficeTitle(next)}（政绩{rec.PerformanceScore}）");
                            }
                        }
                    }
                    else if (rec.PerformanceScore < 20)
                    {
                        // 阶段2D ④ 修正：政绩<20 **持续 3 年**才罢黜（低政绩起始年追踪）
                        string lowKey = "low_perf_" + rec.OfficeId;
                        long lowSince = KingdomDataExt.GetLong(kk, lowKey, 0);
                        if (lowSince == 0) KingdomDataExt.SetLong(kk, lowKey, year);
                        else if (year - (int)lowSince >= 3)
                        {
                            KingdomDataExt.SetLong(kk, lowKey, 0);
                            if (rec.ProtectionUntilYear == 0 || year > rec.ProtectionUntilYear)
                            {
                                DismissOfficer(kingdomId, kingdomName, rec.OfficeId, year, "政绩不彰罢黜");
                                handledByAction.Add(rec.OfficeId);
                                ZhuluContext.Instance.LogInfo($"[OfficeManager] 罢黜: {rec.PersonName} {GetOfficeTitle(rec.OfficeId)}（政绩{rec.PerformanceScore}）");
                            }
                        }
                    }
                    else
                    {
                        // 政绩恢复正常 → 清除低政绩计时
                        string lowKey = "low_perf_" + rec.OfficeId;
                        KingdomDataExt.SetLong(kk, lowKey, 0);
                    }
                }

                // 阶段2U：普通席政绩落盘（无任免动作时直接存；有动作席以 custom_data 最新状态为准合并政绩）
                if (handledByAction.Count == 0)
                {
                    SaveRecords(kingdomId, records);
                }
                else
                {
                    var latest = LoadRecords(kingdomId);
                    foreach (var nr in latest)
                    {
                        var src = records.Find(r => r.OfficeId == nr.OfficeId);
                        if (src != null && !handledByAction.Contains(nr.OfficeId)) nr.PerformanceScore = src.PerformanceScore;
                    }
                    SaveRecords(kingdomId, latest);
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[OfficeManager] 政绩升迁异常: {ex.Message}");
            }
        }

        /// <summary>阶段2E ⑦：任命政绩起点——状元 60 / 进士 50 / 举人+10 / 秀才+5 / 其他 40。</summary>
        private static int ComputeStartPerformance(string personId)
        {
            try
            {
                if (string.IsNullOrEmpty(personId) || !long.TryParse(personId, out long pid)) return 40;
                var actor = World.world?.units?.get(pid);
                if (actor == null) return 40;
                int perf = 40;
                try
                {
                    if (actor.hasTrait(TraitManager.TRAIT_ZHUANGYUAN)) perf = 60;
                    else if (actor.hasTrait(TraitManager.TRAIT_JINSHI)) perf = 50;
                    if (actor.hasTrait(TraitManager.TRAIT_JUREN)) perf += 10;
                    if (actor.hasTrait(TraitManager.TRAIT_XIUCAI)) perf += 5;
                }
                catch { }
                return Math.Min(100, perf);
            }
            catch { return 40; }
        }

        /// <summary>获取王国全部官职记录。</summary>
        public System.Collections.Generic.List<OfficeAppointmentRecord> GetAllAppointments(string kingdomId)
        {
            try
            {
                var records = GetRecordsPublic(kingdomId);
                return records != null ? new System.Collections.Generic.List<OfficeAppointmentRecord>(records) : new System.Collections.Generic.List<OfficeAppointmentRecord>();
            }
            catch { return new System.Collections.Generic.List<OfficeAppointmentRecord>(); }
        }

        // ═══════════ v2 快照存档接口 ═══════════
        // ---- batch10-A1: schema v5 section ----
        public sealed class OfficesBlobState { public string Blob; }
        public string SectionName => "offices";
        public Dictionary<string, OfficesBlobState> CaptureStates()
        {
            var d = new Dictionary<string, OfficesBlobState>();
            if (World.world?.kingdoms?.list == null) return d;
            foreach (Kingdom k in World.world.kingdoms.list)
            {
                if (k == null || !k.isAlive()) continue;
                string v = KingdomDataExt.GetStr(k, KEY_OFFICES, "");
                if (!string.IsNullOrEmpty(v)) d[k.id.ToString()] = new OfficesBlobState { Blob = v };
            }
            return d;
        }
        public void ApplyStates(Dictionary<string, OfficesBlobState> data)
        {
            if (data == null) return;
            foreach (var kv in data)
            {
                Kingdom k = KingdomDataExt.FindKingdomById(kv.Key);
                if (k == null || !k.isAlive() || kv.Value == null) continue;
                KingdomDataExt.SetStr(k, KEY_OFFICES, kv.Value.Blob ?? "");
            }
            try { RehydratePipelineAll(); } catch (Exception ex) { ZhuluContext.Instance.LogWarning($"[OfficeManager] snapshot rehydrate failed: {ex.Message}"); }
        }

        public void MigrateFromCustomData() { }
        public void ResetRuntime()
        {
            _lastScanYear = -1;
        }

}
}
