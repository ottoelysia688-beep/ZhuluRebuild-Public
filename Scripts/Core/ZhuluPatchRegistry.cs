using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ZhuluRebuild.Core
{
    /// <summary>
    /// 逐鹿 Harmony 补丁登记表 —— 全工程补丁的唯一清单与唯一安装入口。
    ///
    /// 为什么需要它（2026-10-23 自检结论）：
    ///   旧实现是"反射扫描 + 5 处散落的手工 Mount"。能装上，但回答不了三个问题：
    ///     ① 一共装了多少补丁？② 哪个失败了？③ 线上某功能失效，能不能只关它来定位？
    ///   F1（改名补丁因参数个数写错而静默失效）潜伏一年，正是缺这层可观测性。
    ///
    /// 设计取舍（尽量不动存量代码）：
    ///   · 属性式补丁仍交给 Harmony 的 CreateClassProcessor，这里只做"登记 + 隔离 + 统计 + 可屏蔽"。
    ///   · 用途元数据集中在下方 Purposes 表；新增补丁类必须在此登记，
    ///     由 Tests 的「补丁清单完整性」用例强制（漏登记 = 测试失败）。
    ///   · 屏蔽：<mod>/.runtime/patch_mask.txt 每行一个补丁 Id（# 开头为注释）。
    ///     命中的补丁本次不安装 —— 线上二分定位用，无需改代码重编译。
    /// </summary>
    public static class ZhuluPatchRegistry
    {
        public const string MaskFileName = "patch_mask.txt";

        /// <summary>一条补丁登记项。</summary>
        public sealed class PatchEntry
        {
            /// <summary>稳定标识（属性式补丁取类简名；手工项用显式 Id）。</summary>
            public string Id;
            /// <summary>一句话用途。</summary>
            public string Purpose;
            /// <summary>属性式补丁类；手工项为 null。</summary>
            public Type ClassType;
            /// <summary>手工安装委托；属性式补丁为 null。</summary>
            public Action<Harmony> Install;
            public bool IsManual => Install != null;
        }

        /// <summary>安装结果汇总。</summary>
        public sealed class InstallReport
        {
            public int Total, Installed, Failed, Skipped;
            public readonly List<string> Failures = new List<string>();
            public readonly List<string> SkippedIds = new List<string>();
            public string Summary => $"共 {Total} 项：成功 {Installed}，失败 {Failed}，屏蔽 {Skipped}";
        }

        /// <summary>
        /// 用途登记表。键 = 补丁类简名（嵌套类取内层类名）或手工项 Id。
        /// 新增补丁必须在此追加一行，否则 PatchRegistryTests 会失败。
        /// </summary>
        private static readonly Dictionary<string, string> Purposes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // ── 原生数值修正（ModifierPipeline 消费端）──
            ["TaxModifierPatch"] = "税收效率修正（Kingdom.getTaxRateLocal）",
            ["LoyaltyModifierPatch"] = "城市忠诚度修正（LoyaltyCalculator.calculate）",
            ["OpinionModifierPatch"] = "外交好感修正（DiplomacyManager.getOpinion）",
            ["MaxWarriorsModifierPatch"] = "兵员上限修正（City.getMaxWarriors）",
            ["PopulationCapModifierPatch"] = "人口上限修正（City.getPopulationMaximum）",
            ["BuildingHealthModifierPatch"] = "建筑耐久修正（BaseSimObject.getMaxHealth）",
            ["MandateCityCapPatch"] = "天命国城数上限（Kingdom.getMaxCities）",
            ["StabilityChannelPatch+GetStabilityPostfix"] = "稳定度通道修正",

            // ── 战斗与立王 ──
            ["CombatChannelPatches+ActorUpdateStatsPatch"] = "战斗四通道：攻/血/速/防（Actor.updateStats）",
            ["KingSapientGatePatch"] = "立王拦截：非智慧生物/载具不得为王",
            ["KingTraitPatch"] = "换君即时特质处理（旧君清理 + 新君修正）",
            ["RoyalClanTraitPatch"] = "王室/普通氏族特质（Kingdom.trySetRoyalClan）",
            ["TraitManager+TraitIconOverridePatch"] = "特质图标覆盖（BaseUnlockableAsset.getSprite）",

            // ── 家族与继承 ──
            ["FamilyInheritanceParent1Patch"] = "子女归族·父母位1（Actor.setParent1）",
            ["FamilyInheritanceParent2Patch"] = "子女归族·父母位2（Actor.setParent2）",
            ["ClanWindowFamilyPatch"] = "氏族窗口注入家族入口",

            // ── 建国与分裂 ──
            ["CityMakeOwnKingdomPatch"] = "城市自立建国拦截（City.makeOwnKingdom）",
            ["KingdomChaosGatePatch"] = "分崩离析事件开关（checkKingdomChaos）",
            ["ShatteredCrownGatePatch"] = "碎冠事件开关（checkShatteredCrownEvent）",

            // ── 原版退化路径守卫（防止 AI/渲染在异常态下崩溃或刷屏）──
            ["DegenerateCityGuardPatch+DocksBuildBoatGuard"] = "船坞造船守卫",
            ["DegenerateCityGuardPatch+RandomBoatAssetGuard"] = "随机船只资产守卫",
            ["DegenerateCityGuardPatch+LoadSpriteFromAssetGuard"] = "贴图加载守卫",
            ["DegenerateCityGuardPatch+KingdomElementIconGuard"] = "王国元素图标守卫",
            ["DegenerateCityGuardPatch+KingdomElementBackgroundGuard"] = "王国元素底纹守卫",
            ["DegenerateCityGuardPatch+CityBannerSetupGuard"] = "城市旗帜初始化守卫",
            ["DegenerateCityGuardPatch+KingdomBannerSetupGuard"] = "王国旗帜初始化守卫",
            ["DegenerateCityGuardPatch+CityBehBuildGuard"] = "城市建造候选守卫",
            ["DegenerateCityGuardPatch+CheckSettleBiomesGuard"] = "定居地形校验守卫",

            // ── UI 注入 ──
            ["KingdomWindowTabsPatch"] = "王国窗口页签注入（逐鹿四页）",
            ["UnitWindowPersonHistoryPatch"] = "单位窗口「个人史册」侧边入口",
            ["NameplateTierPatch"] = "地图铭牌爵位显示（NameplateText.showTextKingdom）",

            // ── 手工挂载项 ──
            ["warfare"] = "战争分类补丁组（WarManager / War / DiplomacyManager）",
            ["year_driver"] = "年度驱动（Kingdom.updateAge，替代轮询）",
            ["save_sync"] = "存档同步（保存 / 读档 / 新世界 / 世界就绪后国名同步）",
            ["chinese_name"] = "中文命名（getName 拦截 + 各生成后置 + 玩家改名捕获）",
            ["no_heir"] = "无嗣继承判定接入（KingdomBehCheckKing.execute：远亲/篡位/分裂）",
            // 2026-10-24：save_probe / succession_probe 两探针随使命完成移除
            // （P0-2 已落地验证；F9 继承链已实测结案——.runtime/no_heir.log 三条判定为证）
        };

        /// <summary>
        /// 补丁 Id：嵌套类取「外层+内层」（如 StabilityChannelPatch+GetStabilityPostfix），
        /// 否则 Type.Name 只给内层名，多个 Postfix 类会撞车。
        /// </summary>
        public static string IdOf(Type t)
        {
            if (t == null) return "";
            return t.IsNested && t.DeclaringType != null ? t.DeclaringType.Name + "+" + t.Name : t.Name;
        }

        /// <summary>取用途描述；未登记返回占位串（配合测试报错）。</summary>
        public static string PurposeOf(string id)
        {
            return Purposes.TryGetValue(id, out var p) ? p : "(未登记用途)";
        }

        /// <summary>是否已在用途表登记（Tests 用）。</summary>
        public static bool IsDocumented(string id) => Purposes.ContainsKey(id);

        /// <summary>用途表全量键（Tests / 文档生成用）。</summary>
        public static IEnumerable<string> DocumentedIds => Purposes.Keys;

        /// <summary>
        /// 构建全部登记项：属性式补丁（按全名排序，保持与旧行为一致的确定性顺序）+ 手工挂载项。
        /// </summary>
        public static List<PatchEntry> BuildEntries()
        {
            var list = new List<PatchEntry>();

            var patchTypes = Assembly.GetExecutingAssembly().GetTypes()
                .Where(t => t.GetCustomAttributes(typeof(HarmonyPatch), true).Length > 0
                         || t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                            BindingFlags.Static | BindingFlags.Instance)
                            .Any(m => m.GetCustomAttributes(typeof(HarmonyPatch), true).Length > 0))
                .OrderBy(t => t.FullName)
                .ToList();

            foreach (var t in patchTypes)
            {
                string id = IdOf(t);
                list.Add(new PatchEntry
                {
                    Id = id,
                    Purpose = PurposeOf(id),
                    ClassType = t
                });
            }

            // 手工挂载项：顺序与旧 Main.OnModLoad 保持一致（属性补丁 → 战争 → 年度 → 存档 → 命名）
            // 注：SplitEventsPatch.Apply() 仍在配置初始化阶段调用（早于补丁安装），不纳入本表。
            list.Add(new PatchEntry { Id = "warfare", Purpose = PurposeOf("warfare"), Install = h => Adapters.WarfarePatches.Mount(h) });
            list.Add(new PatchEntry { Id = "year_driver", Purpose = PurposeOf("year_driver"), Install = h => Adapters.ZhuluYearDriverPatch.Mount(h) });
            list.Add(new PatchEntry { Id = "save_sync", Purpose = PurposeOf("save_sync"), Install = h => Adapters.ZhuluSavePatch.Mount(h) });
            list.Add(new PatchEntry
            {
                Id = "chinese_name",
                Purpose = PurposeOf("chinese_name"),
                Install = h =>
                {
                    // 先加载姓名池，再挂补丁（否则后置里取不到生成器）
                    Naming.ZhuluChineseNameService.Instance.EnsureLoaded();
                    Naming.ZhuluChineseNamePatches.Mount(h);
                }
            });
            // F9：无嗣继承判定（反射挂载，目标类型可能非 public）
            list.Add(new PatchEntry { Id = "no_heir", Purpose = PurposeOf("no_heir"), Install = h => Adapters.NoHeirSuccessionPatch.Mount(h) });
            // 2026-10-24：save_probe / succession_probe 探针登记已随文件删除一并移除

            return list;
        }

        /// <summary>读取屏蔽清单（不存在或异常 → 空集，不阻断启动）。</summary>
        public static HashSet<string> LoadMask(string runtimeDir)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (string.IsNullOrEmpty(runtimeDir)) return set;
                string path = Path.Combine(runtimeDir, MaskFileName);
                if (!File.Exists(path)) return set;
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    set.Add(line);
                }
                if (set.Count > 0)
                    // 有效的屏蔽是**人为诊断操作**，属正常配置，用 Info（debug_logging 关闭时不输出）；
                    // 只有"写了但登记表里没有"的 Id 才是异常，由 BuildPlan 单独告警。
                    ZhuluContext.Instance.LogInfo($"[PatchRegistry] 屏蔽清单生效（{set.Count} 项）：{string.Join(", ", set)}");
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning("[PatchRegistry] 读取屏蔽清单失败（忽略）: " + ex.Message);
            }
            return set;
        }

        /// <summary>
        /// 安装计划（不触碰 Harmony）——供启动前诊断与单测使用。
        /// 把「登记 → 屏蔽过滤 → 待装清单」从实际挂载里拆出来，
        /// 这样治理层本身可以在没有游戏运行时的环境下被验证。
        /// </summary>
        public sealed class InstallPlan
        {
            public readonly List<PatchEntry> Entries = new List<PatchEntry>();
            public readonly HashSet<string> Masked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            /// <summary>屏蔽清单里写了、但登记表中不存在的 Id（多半是拼错或补丁已删）——属异常，需告警。</summary>
            public readonly List<string> UnknownMaskIds = new List<string>();

            public List<PatchEntry> Pending
            {
                get
                {
                    var list = new List<PatchEntry>(Entries.Count);
                    foreach (var e in Entries) if (!Masked.Contains(e.Id)) list.Add(e);
                    return list;
                }
            }

            public int AutoCount => Entries.Count(e => !e.IsManual);
            public int ManualCount => Entries.Count(e => e.IsManual);
        }

        /// <summary>构建安装计划：登记项 + 屏蔽过滤。不安装任何补丁。</summary>
        public static InstallPlan BuildPlan(string runtimeDir)
        {
            var plan = new InstallPlan();
            plan.Entries.AddRange(BuildEntries());

            var ids = new HashSet<string>(plan.Entries.Select(e => e.Id), StringComparer.Ordinal);
            foreach (var id in LoadMask(runtimeDir))
            {
                if (ids.Contains(id)) plan.Masked.Add(id);
                else plan.UnknownMaskIds.Add(id);
            }

            if (plan.UnknownMaskIds.Count > 0)
                ZhuluContext.Instance.LogWarning(
                    $"[PatchRegistry] 屏蔽清单含未知 Id（已忽略）：{string.Join(", ", plan.UnknownMaskIds)}");

            return plan;
        }

        /// <summary>
        /// 安装全部补丁。逐项隔离：单项失败不牵连其余，最终返回汇总供启动日志与自检使用。
        /// </summary>
        public static InstallReport InstallAll(Harmony harmony, string runtimeDir)
        {
            var report = new InstallReport();
            var plan = BuildPlan(runtimeDir);

            foreach (var e in plan.Entries)
            {
                report.Total++;
                if (plan.Masked.Contains(e.Id))
                {
                    report.Skipped++;
                    report.SkippedIds.Add(e.Id);
                    continue;
                }
                try
                {
                    if (e.IsManual) e.Install(harmony);
                    else harmony.CreateClassProcessor(e.ClassType).Patch();
                    report.Installed++;
                }
                catch (Exception ex)
                {
                    report.Failed++;
                    report.Failures.Add($"{e.Id}（{e.Purpose}）: {ex.Message}");
                    ZhuluContext.Instance.LogError($"[PatchRegistry] 补丁安装失败：{e.Id} —— {e.Purpose}", ex);
                }
            }

            if (report.Failed > 0)
            {
                var sb = new System.Text.StringBuilder();
                sb.Append($"[PatchRegistry] {report.Summary}");
                foreach (var f in report.Failures) sb.Append("\n  ✗ ").Append(f);
                ZhuluContext.Instance.LogError(sb.ToString());
            }
            else
            {
                ZhuluContext.Instance.LogInfo($"[PatchRegistry] {report.Summary}");
            }
            return report;
        }
    }
}
