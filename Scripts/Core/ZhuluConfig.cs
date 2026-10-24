using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace ZhuluRebuild.Core
{
    /// <summary>
    /// 逐鹿全局配置（Configs/FeatureFlags.json 持久化）。
    /// 参考 Steam 版 mod（玄鉴仙族/PopControl）的 default_config.json 配置模式：
    /// SWITCH 布尔开关 + 数值项，带名称/描述/范围。修改即时生效，写回 json 重启保留。
    /// </summary>
    public class ZhuluConfig
    {
        public static ZhuluConfig Instance { get; } = new ZhuluConfig();

        private string _configPath;

        // ── 配置项 ──
        public bool DebugLogging { get; set; } = false;
    public bool PerformanceProfiling { get; set; } = false; // 2026-08-20：性能计时开关（JSON 控制，不改设置 UI）         // 调试日志（阶段3 性能：默认关——每年每国 LogInfo 刷控制台/Player.log 是稳定掉帧源；玩家可在 config.json 打开）
        public bool AiAutoPolicy { get; set; } = true;          // AI 自动施政
        public bool GroundworkEnabled { get; set; } = true;     // 地基（身份/个人史册/派系骨架）
        public bool KejuEnabled { get; set; } = true;           // 科举（三级：伯国县试/侯国乡试/公国殿试，5 年一届）
        public bool CityLeaderEnabled { get; set; } = true;      // 阶段2L：城主官职化（补位/撤职/退休/权势）
        public bool DiplomacyEnabled { get; set; } = true;       // 阶段2L：邦交体系（和约/互不侵犯/朝贡）
        public bool TierSystemEnabled { get; set; } = true;      // 阶段2L：爵位升降（国力五等）
        public bool LiZhiEnabled { get; set; } = true;           // 阶段2L：礼治体系（祭祖/祭天/受命）
        public int KejuIntervalYears { get; set; } = 5;          // 阶段2L：科举轮次间隔（5/10 年）
        public bool FactionPoliticsEnabled { get; set; } = true; // 党争（侯级以上，亲信度/五事件/世仇/政变）
        public int MandateTickInterval { get; set; } = 10;      // 天命结算间隔（年）
        public int MandateWarnThreshold { get; set; } = 30;     // 易主预警阈值
        public bool StrictSave { get; set; } = true;            // 2026-08-20：严格存档（快照收集失败阻止存档，防存出残缺档；关闭则回退旧宽松模式）
        public bool MapTierLabelsEnabled { get; set; } = true;  // 地图国家铭牌显示爵位（只影响显示，不改存档国名）
        public bool MapTierColorsEnabled { get; set; } = true;  // 地图爵位括号按等阶着色；字体不支持时自动回退纯文字
        public bool MapTierIconsEnabled { get; set; } = true;   // 批次20：地图铭牌显示爵位贴图（公九鼎/侯印绶…天子玉玺，双体制共用T25~T30）
        // 2026-10-13：禅让机制开关（设置窗口可关）。开启时 SuccessionManager.CheckAbdication 生效——
        // 君主国力<90 且在位≥30 年后，每年 10% 概率主动禅位给继承人；关闭则该路径完全停用
        // （君位只能靠病故继承或篡位更替）。默认 true = 保持原行为，零回归。
        public bool AbdicationEnabled { get; set; } = true;
        // 2026-10-23 [F1]：玩家改名捕获开关。build 549 的 Actor 无 setName 方法，改名走属性 name 的
        // setter（set_name，protected）。开启=挂载 set_name 后置把玩家改名写回权威名；
        // 关闭=完全不挂载该补丁，逐字回到 v1.14.0 旧行为（改完立刻被 getName 覆盖）。
        public bool NamingManualRenameCapture { get; set; } = true;
        // 2026-10-23 [F6]：数值管道求值缓存开关。true=按 (通道,国家) 预聚合 O(1) 求值；
        // false=回落修复前的线性扫描（逐字保留，用于随时回调与对照排查）。
        public bool ModifierCacheEnabled { get; set; } = true;
        // 2026-10-23 [P1-3]：快照存储层 gzip。true=压缩写盘（读取端按魔数自动识别）；
        // false=写纯 JSON（行为回到修复前）。Schema 与版本号不受影响。
        public bool SnapshotGzip { get; set; } = true;
        // 2026-10-23 [F9]：无嗣继承判定。true=按 中央权力/权臣权势/天命/稳定度 判定远亲·篡位·分裂；
        // false=完全回到原版（无嗣时只打日志、交给原版选王）。
        public bool NoHeirResolution { get; set; } = true;
        // 2026-10-24 [LlmFix-6]：在位帝王轨迹采样间隔（年）。LlmV2-5 默认 3=每 3 年记一笔
        // （城/民/库/天命/稳定/附庸/世界排名），供「极盛 N 年」与 AI 评语兴衰曲线；
        // 0=关闭采样（旧档无轨迹仍可用首尾两点兜底）。UI 为 −/+ 步进（最低 2）。
        public int EmperorSampleInterval { get; set; } = 3;
        // 2026-10-24 [LlmV2-4]：断代史摘要间隔（年）。默认 100=每一百年 AI 写一段史论
        // （挂历代天命窗口顶部）；0=关闭。
        public int ChronicleDigestInterval { get; set; } = 100;

        // 范围
        public const int MinTickInterval = 5;
        public const int MaxTickInterval = 20;
        public const int MinWarnThreshold = 10;
        public const int MaxWarnThreshold = 60;

        // 2026-10-24 [LlmV2-5]：采样间隔步进规则——开启态最低 2 年，2 再减=关闭(0)，关闭加=2，封顶 50。
        public static int StepSampleInterval(int current, int delta)
        {
            int v = current < 0 ? 0 : current;
            if (delta < 0)
            {
                if (v == 2) return 0;
                if (v > 2) return v - 1;
                return 0;   // 0/1 减到底都是关闭（1 为手改异常值，归 0）
            }
            if (v == 0) return 2;
            if (v >= 50) return 50;
            return v + 1;
        }

        private ZhuluConfig() { }

        public void Initialize(string modPath)
        {
            try
            {
                _configPath = Path.Combine(modPath, "Configs", "FeatureFlags.json");
                Load();

            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ZhuluConfig] 初始化配置失败，使用默认值: {ex.Message}");
            }
        }

        /// <summary>从 json 读取（逐行解析 "key": value；缺失字段保持默认）</summary>
        public void Load()
        {
            if (string.IsNullOrEmpty(_configPath) || !File.Exists(_configPath)) return;
            try
            {
                string[] lines = File.ReadAllLines(_configPath);
                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (!line.Contains(":")) continue;
                    string key = line.Substring(0, line.IndexOf(':')).Trim().Trim('"', ' ');
                    string val = line.Substring(line.IndexOf(':') + 1).Trim().Trim(',').Trim('"', ' ');
                    if (string.IsNullOrEmpty(key)) continue;
                    switch (key)
                    {
                        case "debug_logging": DebugLogging = ParseBool(val, DebugLogging); break;
                case "performance_profiling": PerformanceProfiling = ParseBool(val, PerformanceProfiling); break;
                        case "ai_auto_policy": AiAutoPolicy = ParseBool(val, AiAutoPolicy); break;
                        case "groundwork_enabled": GroundworkEnabled = ParseBool(val, GroundworkEnabled); break;
                        case "keju_enabled": KejuEnabled = ParseBool(val, KejuEnabled); break;
                case "city_leader_enabled": CityLeaderEnabled = ParseBool(val, CityLeaderEnabled); break;
                case "diplomacy_enabled": DiplomacyEnabled = ParseBool(val, DiplomacyEnabled); break;
                case "tier_system_enabled": TierSystemEnabled = ParseBool(val, TierSystemEnabled); break;
                case "li_zhi_enabled": LiZhiEnabled = ParseBool(val, LiZhiEnabled); break;
                case "keju_interval_years": KejuIntervalYears = int.TryParse(val, out int kiy) ? Math.Max(5, Math.Min(10, kiy)) : KejuIntervalYears; break;
                        case "faction_politics_enabled": FactionPoliticsEnabled = ParseBool(val, FactionPoliticsEnabled); break;
                        case "abdication_enabled": AbdicationEnabled = ParseBool(val, AbdicationEnabled); break;   // 2026-10-13：禅让开关
                        case "naming_manual_rename_capture": NamingManualRenameCapture = ParseBool(val, NamingManualRenameCapture); break;   // 2026-10-23 [F1]
                        case "modifier_cache_enabled": ModifierCacheEnabled = ParseBool(val, ModifierCacheEnabled); break;   // 2026-10-23 [F6]
                        case "snapshot_gzip": SnapshotGzip = ParseBool(val, SnapshotGzip); break;   // 2026-10-23 [P1-3]
                        case "no_heir_resolution": NoHeirResolution = ParseBool(val, NoHeirResolution); break;   // 2026-10-23 [F9]
                        case "emperor_sample_interval": EmperorSampleInterval = ParseInt(val, EmperorSampleInterval, 0, 50); break;   // 2026-10-24 [LlmFix-6]，0=关
                        case "chronicle_digest_interval": ChronicleDigestInterval = ParseInt(val, ChronicleDigestInterval, 0, 500); break;   // 2026-10-24 [LlmV2-4]，0=关
                        case "mandate_tick_interval": MandateTickInterval = ParseInt(val, MandateTickInterval, MinTickInterval, MaxTickInterval); break;
                        case "mandate_warn_threshold": MandateWarnThreshold = ParseInt(val, MandateWarnThreshold, MinWarnThreshold, MaxWarnThreshold); break;
                        case "strict_save": StrictSave = ParseBool(val, StrictSave); break;
                        case "map_tier_labels_enabled": MapTierLabelsEnabled = ParseBool(val, MapTierLabelsEnabled); break;
                        case "map_tier_colors_enabled": MapTierColorsEnabled = ParseBool(val, MapTierColorsEnabled); break;
                        case "map_tier_icons_enabled": MapTierIconsEnabled = ParseBool(val, MapTierIconsEnabled); break;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ZhuluConfig] 读取配置失败: {ex.Message}");
            }
        }

        /// <summary>写回 json（整体重写，保留全部字段）</summary>
        public void Save()
        {
            if (string.IsNullOrEmpty(_configPath)) return;
            try
            {
                string dir = Path.GetDirectoryName(_configPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"enable_world_overview\": true,");
                sb.AppendLine("  \"enable_kingdom_tabs\": true,");
                sb.AppendLine("  \"enable_dev_probes\": false,");
                sb.AppendLine("  \"enable_modifier_pipeline\": true,");
                sb.AppendLine($"  \"debug_logging\": {BoolStr(DebugLogging)},");
        sb.AppendLine($"  \"performance_profiling\": {BoolStr(PerformanceProfiling)},");
                sb.AppendLine($"  \"ai_auto_policy\": {BoolStr(AiAutoPolicy)},");
                sb.AppendLine($"  \"groundwork_enabled\": {BoolStr(GroundworkEnabled)},");
                sb.AppendLine($"  \"keju_enabled\": {BoolStr(KejuEnabled)},");
                sb.AppendLine($"  \"city_leader_enabled\": {BoolStr(CityLeaderEnabled)},");
                sb.AppendLine($"  \"diplomacy_enabled\": {BoolStr(DiplomacyEnabled)},");
                sb.AppendLine($"  \"tier_system_enabled\": {BoolStr(TierSystemEnabled)},");
                sb.AppendLine($"  \"li_zhi_enabled\": {BoolStr(LiZhiEnabled)},");
                sb.AppendLine($"  \"keju_interval_years\": {KejuIntervalYears},");
                sb.AppendLine($"  \"faction_politics_enabled\": {BoolStr(FactionPoliticsEnabled)},");
                sb.AppendLine($"  \"abdication_enabled\": {BoolStr(AbdicationEnabled)},");   // 2026-10-13：禅让开关
                sb.AppendLine($"  \"naming_manual_rename_capture\": {BoolStr(NamingManualRenameCapture)},");   // 2026-10-23 [F1]
                sb.AppendLine($"  \"modifier_cache_enabled\": {BoolStr(ModifierCacheEnabled)},");   // 2026-10-23 [F6]
                sb.AppendLine($"  \"snapshot_gzip\": {BoolStr(SnapshotGzip)},");   // 2026-10-23 [P1-3]
                sb.AppendLine($"  \"no_heir_resolution\": {BoolStr(NoHeirResolution)},");   // 2026-10-23 [F9]
                sb.AppendLine($"  \"emperor_sample_interval\": {EmperorSampleInterval},");   // 2026-10-24 [LlmFix-6]
                sb.AppendLine($"  \"chronicle_digest_interval\": {ChronicleDigestInterval},");   // 2026-10-24 [LlmV2-4]
                sb.AppendLine($"  \"mandate_tick_interval\": {MandateTickInterval},");
                sb.AppendLine($"  \"mandate_warn_threshold\": {MandateWarnThreshold},");
                sb.AppendLine($"  \"strict_save\": {BoolStr(StrictSave)},");
                sb.AppendLine($"  \"map_tier_labels_enabled\": {BoolStr(MapTierLabelsEnabled)},");
                sb.AppendLine($"  \"map_tier_colors_enabled\": {BoolStr(MapTierColorsEnabled)},");
                sb.AppendLine($"  \"map_tier_icons_enabled\": {BoolStr(MapTierIconsEnabled)}");
                sb.AppendLine("}");
                File.WriteAllText(_configPath, sb.ToString());
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ZhuluConfig] 保存配置失败: {ex.Message}");
            }
        }

        private static bool ParseBool(string s, bool fallback)
        {
            bool r;
            return bool.TryParse(s, out r) ? r : fallback;
        }

        private static int ParseInt(string s, int fallback, int min, int max)
        {
            int r;
            if (!int.TryParse(s, out r)) return fallback;
            return Mathf.Clamp(r, min, max);
        }

        private static string BoolStr(bool b) => b ? "true" : "false";
    }
}
