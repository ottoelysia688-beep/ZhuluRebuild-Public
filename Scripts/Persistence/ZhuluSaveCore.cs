using System;
using System.IO;
using ZhuluRebuild.Core;
using ZhuluRebuild.Domain;
using HarmonyLib;

namespace ZhuluRebuild.Persistence
{
    /// <summary>
    /// 逐鹿存档核心（v2 重构）：
    /// 快照文件为唯一权威源，读档同步恢复（零等待、零 custom_data 依赖）。
    /// 保存：收集各系统内存 → 序列化 → 原子写 .runtime 与存档目录（旧档 .bak 备份）。
    /// 读档：读存档目录快照 → world_id 校验（防串档）→ 分发恢复各系统。
    /// 旧档（无快照）：从 custom_data 迁移一次并立即生成快照。
    /// </summary>
    public class ZhuluSaveCore
    {
        public static ZhuluSaveCore Instance { get; } = new ZhuluSaveCore();

        private const string SnapshotFileName = "zhulu_snapshot.json";
        private const string WorldIdKey = "zlr_world_id";

        private string _runtimePath = "";
        private bool _initialized;

        /// <summary>当前世界 ID（新世界生成，读档恢复）。</summary>
        public string WorldId { get; private set; } = "";

        /// <summary>是否已完成一次读档/新世界初始化（防 custom_data 迟到恢复干扰）。</summary>
        public bool WorldInitialized { get; private set; }

        private ZhuluSaveCore() { }

        public void Initialize(string modFolder)
        {
            string runtimeDir = Path.Combine(modFolder, ".runtime");
            if (!Directory.Exists(runtimeDir)) Directory.CreateDirectory(runtimeDir);
            _runtimePath = Path.Combine(runtimeDir, SnapshotFileName);
            _initialized = true;
        }

        // ═══════════════ 保存 ═══════════════

        /// <summary>严格存档阻断异常：快照收集失败时抛出（Prefix 不吞 → 阻止本次游戏存档，防存出残缺档）。</summary>
        public class ZhuluSaveBlockedException : Exception
        {
            public ZhuluSaveBlockedException(string message) : base(message) { }
        }

        /// <summary>
        /// 保存前调用（SaveManager.saveWorldToDirectory Prefix）：收集各系统 → 原子写快照。
        /// 2026-08-20 严格模式（strict_save，默认开）：任一系统收集失败 → 抛 ZhuluSaveBlockedException
        /// 阻止本次存档（快照与游戏档不一致比存不了更糟——宁可阻断也不存残缺）；关闭则回退旧宽松模式（记日志继续）。
        /// </summary>
        public void Save(string saveFolder)
        {
            try
            {
                if (!_initialized || string.IsNullOrEmpty(saveFolder)) return;

                EnsureSchemaRegistered();   // 批次10-A1：v5 分区注册（幂等）
                ZhuluSnapshotV5 snap = CollectAllV5();
                string json = snap.Serialize();
                // 1) 原子写运行时工作副本
                AtomicWrite(_runtimePath, json);

                // 2) 原子写存档目录（旧档先轮转备份）
                string destDir = Directory.Exists(saveFolder) ? saveFolder : Path.GetDirectoryName(saveFolder);
                if (string.IsNullOrEmpty(destDir)) return;
                string dest = Path.Combine(destDir, SnapshotFileName);
                RotateBackups(dest);
                AtomicWrite(dest, json);

                ZhuluContext.Instance.LogInfo($"[ZhuluSaveCore] 快照已保存: world={snap.WorldId} 年={snap.SavedAtYear} ({dest})");
            }
            catch (ZhuluSaveBlockedException)
            {
                throw; // 严格模式：向上抛（Prefix 不吞 → 阻止游戏存档）
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[ZhuluSaveCore] 保存快照失败", ex);
                if (ZhuluConfig.Instance.StrictSave)
                    throw new ZhuluSaveBlockedException($"快照写入失败: {ex.Message}");
            }
        }

        /// <summary>原子写：先写 .tmp 再原子替换，避免写一半损坏。</summary>
        private static void AtomicWrite(string path, string content)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            string tmp = path + ".tmp";
            // 2026-10-23 [P1-3]：存储层 gzip（不动 Schema）。读取端按魔数自动识别，
            // 旧档纯 JSON 无需迁移；开关关闭则写出纯文本，行为回到修复前。
            byte[] bytes = ZhuluSnapshotCodec.Encode(content, ZhuluConfig.Instance.SnapshotGzip);
            File.WriteAllBytes(tmp, bytes);

            if (!File.Exists(path))
            {
                File.Move(tmp, path);
                return;
            }

            // 2026-10-23 [F4]：旧实现是 Delete→Move 两步，崩溃/断电落在中间会同时失去主文件与 tmp
            // （虽有三代备份兜底，但会退档一代）。File.Replace 在同一卷上为原子替换。
            // 失败（跨卷/只读/文件系统不支持）自动回落旧路径，行为不劣于修复前。
            try
            {
                File.Replace(tmp, path, null);
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning("[ZhuluSaveCore] File.Replace 不可用，回落删-移路径: " + ex.Message);
                try { File.Delete(path); } catch { }
                File.Move(tmp, path);
            }
        }

        // ── P0-5（2026-08-23）多代备份轮转 ──
        // 动机：SHToolkit 等自动保存 mod 会大幅提高存盘频率，单份 .bak 会被迅速覆盖；
        // 三代备份保证至少能回退到较早状态。轮转语义：bak.2←bak.1←当前。

        /// <summary>备份保留代数（.bak.1 ~ .bak.{BackupDepth-1}）。</summary>
        private const int BackupDepth = 3;

        /// <summary>
        /// 保存前轮转备份：.bak.1←主文件，.bak.2←.bak.1；旧版单份 .bak 首次迁移为 .bak.1。
        /// 失败仅告警不阻断保存（备份是尽力而为，主文件写入才是权威路径）。
        /// </summary>
        private static void RotateBackups(string path)
        {
            try
            {
                string legacy = path + ".bak";
                if (File.Exists(legacy))
                {
                    string b1 = path + ".bak.1";
                    if (!File.Exists(b1)) File.Move(legacy, b1);
                    else File.Delete(legacy); // 已有新代次备份，旧版残留作废
                }
                for (int i = BackupDepth - 1; i >= 1; i--)
                {
                    string dst = path + ".bak." + i;
                    string src = i == 1 ? path : path + ".bak." + (i - 1);
                    if (!File.Exists(src)) continue;
                    if (File.Exists(dst)) File.Delete(dst);
                    File.Copy(src, dst, true);
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning("[ZhuluSaveCore] 备份轮转失败（不影响本次保存）: " + ex.Message);
            }
        }


        // ═══════════════ 读档 ═══════════════

        /// <summary>读档后调用（SaveManager.loadWorld Postfix）：同步恢复，进入存档即原样。</summary>
        public void Load(string savePath)
        {
            try
            {
                if (!_initialized || string.IsNullOrEmpty(savePath)) return;

                string sourceDir = Directory.Exists(savePath) ? savePath : Path.GetDirectoryName(savePath);
                if (string.IsNullOrEmpty(sourceDir)) return;
                string source = Path.Combine(sourceDir, SnapshotFileName);

                var probe = TryReadRawSnapshot(source);   // 批次10-A1：先取原文判版本
                if (probe != null && SnapshotVersionOf(probe) < 5)
                {
                    ZhuluContext.Instance.LogError($"[ZhuluSaveCore] 检测到旧版快照(v{SnapshotVersionOf(probe)})——本版本不再支持旧格式读取。已按新世界初始化处理。");
                    ResetAllSystems();
                    WorldId = ReadWorldIdFromCustomData();
                    if (string.IsNullOrEmpty(WorldId)) WorldId = Guid.NewGuid().ToString("N");
                    WriteWorldIdToCustomData(WorldId);
                    WorldInitialized = true;
                    return;
                }

                ZhuluSnapshotV5 snap = TryReadSnapshotV5(source);

                if (snap == null)
                {
                    // 无任何可用快照（真新世界/全部损坏）→ 干净初始化
                    ZhuluContext.Instance.LogWarning("[ZhuluSaveCore] 无有效 v5 快照，按新世界初始化");
                    ResetAllSystems();
                    WorldId = ReadWorldIdFromCustomData();
                    if (string.IsNullOrEmpty(WorldId)) WorldId = Guid.NewGuid().ToString("N");
                    WriteWorldIdToCustomData(WorldId);
                    WorldInitialized = true;
                    return;
                }

                // world_id 校验（防串档：新世界/读错档）
                string storedWorldId = ReadWorldIdFromCustomData();
                if (!string.IsNullOrEmpty(storedWorldId) && storedWorldId != snap.WorldId)
                {
                    ZhuluContext.Instance.LogError($"[ZhuluSaveCore] 世界ID不匹配，拒绝恢复（存档={snap.WorldId} 世界={storedWorldId}）——防串档");
                    ResetAllSystems();
                    WorldId = storedWorldId;
                    WorldInitialized = true;
                    return;
                }

                // 兜底：0.51.2 主菜单「新世界」不触发 MapBox.generateNewMap → NewWorld() 从未执行，
                // 快照存在但 world_id 为空 = 该世界从未初始化（新档可加速玩到任意年数；真旧档无快照，
                // 走上方迁移路径会生成 id，永不进入此分支）→ 判定新世界初始化漏跑 → 无条件补初始化
                if (string.IsNullOrEmpty(snap.WorldId))
                {
                    ZhuluContext.Instance.LogInfo($"[ZhuluSaveCore] 快照无 world_id（{snap.SavedAtYear}年），判定新世界初始化漏跑 → 补初始化");
                    NewWorld();
                    // 立即为现存王国建 4 党空壳（不等年度，读档后打开党争窗口即有）
                    Domain.FactionManager.Instance.EnsureFreshFactionsForExistingWorld();
                    return;
                }

                // 分发恢复各系统（批次10-A1：注册表驱动）
                EnsureSchemaRegistered();
                ZhuluSaveSchema.RestoreAll(snap);
                WorldId = snap.WorldId;
                WorldInitialized = true;

                // 2026-10-24 LlmFix-1：读档即丢弃旧世界在途的 AI 评价队列与每局额度计数
                // （评语本体由 llm_comments 分区随 RestoreAll 恢复，两不误）
                Interop.LlmEmperorService.Instance.ResetForNewWorld();

                // 同步写运行时工作副本（下次"继续游戏"参考）
                ZhuluGuard.Run("SaveCore.Load.SyncRuntime", () => AtomicWrite(_runtimePath, snap.Serialize()));

                ZhuluContext.Instance.LogInfo($"[ZhuluSaveCore] 读档恢复完成: world={snap.WorldId} 年={snap.SavedAtYear}");
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[ZhuluSaveCore] 读档恢复异常", ex);
            }
        }

        /// <summary>新世界后调用（generateNewMap Postfix）：生成新世界 ID，清空所有系统。</summary>
        public void NewWorld()
        {
            try
            {
                WorldId = Guid.NewGuid().ToString("N");
                WriteWorldIdToCustomData(WorldId);
                ResetAllSystems();
                Domain.FactionManager.Instance.ResetForNewWorld(); // 新世界标志：首次年度建 4 固定党空壳（ResetRuntime 不置此标志）
                    Domain.FactionPoliticsManager.Instance.ResetForNewWorld();
                WorldInitialized = true;
                // 清理运行时快照（防残留串档）
                if (File.Exists(_runtimePath)) { ZhuluGuard.Run("SaveCore.NewWorld.CleanRuntime", () => File.Delete(_runtimePath)); }
                ZhuluContext.Instance.LogInfo($"[ZhuluSaveCore] 新世界初始化: world={WorldId}");
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[ZhuluSaveCore] 新世界初始化异常", ex);
            }
        }

        // ═══════════════ 收集/分发 ═══════════════

        // 批次10-A1：Schema v5 分区注册表（系统逐个接入；全部接完后的总切换提交会替换 Collect/Restore 主路径）
        private static bool _schemaRegistered;
        /// <summary>公开预热（CompatibilityReport 启动核对用）。</summary>
        public static void WarmSchema() => EnsureSchemaRegistered();
        private static void EnsureSchemaRegistered()
        {
            if (_schemaRegistered) return;
            _schemaRegistered = true;
            ZhuluSaveSchema.Register(Domain.TreasuryManager.Instance);
            ZhuluSaveSchema.Register(Domain.StabilityManager.Instance);
            ZhuluSaveSchema.Register(Domain.SuccessionManager.Instance);
            ZhuluSaveSchema.Register(Domain.VassalManager.Instance);
            ZhuluSaveSchema.Register(Domain.ClaimManager.Instance);
            ZhuluSaveSchema.Register(Domain.CorruptionManager.Instance);
            ZhuluSaveSchema.Register(Domain.KingdomCivilizationService.Instance);
            ZhuluSaveSchema.Register(new Domain.KingdomExt.KingdomExtSection());
            ZhuluSaveSchema.Register(new Domain.ZhuluWarService.ReparationsSection());   // T3
            ZhuluSaveSchema.Register(Domain.KejuManager.Instance);
            ZhuluSaveSchema.Register(Domain.FamilyRegistry.Instance);
            ZhuluSaveSchema.Register(Domain.RoyalRegistry.Instance);
            ZhuluSaveSchema.Register(Domain.FactionManager.Instance);
            ZhuluSaveSchema.Register(Domain.FactionPoliticsManager.Instance);
            ZhuluSaveSchema.Register(Domain.PersonHistoryManager.Instance);
            ZhuluSaveSchema.Register(Domain.IdentityClassifier.Instance);
            ZhuluSaveSchema.Register(Domain.PolicyManager.Instance);
            ZhuluSaveSchema.Register(Domain.OfficeManager.Instance);
            ZhuluSaveSchema.Register(Domain.OrthodoxMandateManager.Instance);
            ZhuluSaveSchema.Register(Domain.NomadicMandateManager.Instance);
            ZhuluSaveSchema.Register(Domain.ChronicleManager.Instance);
            ZhuluSaveSchema.Register(Domain.EraNameService.Instance);
            ZhuluSaveSchema.Register(new Adapters.WarfarePatches.WarfareSection());   // 22/22
            // 2026-10-24 历代天命接入 第5步：AI 评价结果（模型输出非确定，必须落快照，
            // 否则读档重请求会让历史评语翻变）
            ZhuluSaveSchema.Register(Interop.LlmCommentStore.Instance);
            // 2026-10-24 LlmV2-4：断代史论（同理必须落快照）
            ZhuluSaveSchema.Register(Interop.LlmChronicleStore.Instance);
            // ── 后续系统接入点（T1→T2→T3 逐行追加，与旧 CollectAll 清单一一对应防漏）──
        }

        private ZhuluSnapshotV5 CollectAllV5()
        {
            var snap = new ZhuluSnapshotV5
            {
                WorldId = WorldId,
                SavedAtYear = ZhuluTime.CurrentYear
            };
            try
            {
                ZhuluSaveSchema.CollectAll(snap);   // 任一分区异常 → 上抛 → StrictSave 阻断
            }
            catch (Exception ex)
            {
                if (ZhuluConfig.Instance.StrictSave)
                    throw new ZhuluSaveBlockedException($"快照收集失败（{ex.Message}）——已阻止本次存档；可在 Configs/FeatureFlags.json 设 strict_save=false 回退宽松模式");
                ZhuluContext.Instance.LogError("[ZhuluSaveCore] 宽松模式：收集失败继续保存(部分分区缺失): " + ex.Message);
            }
            return snap;
        }

        /// <summary>读快照回退链（原文）：主文件 → .bak.1 → .bak.2 → 旧版 .bak；全部不可用返回 null。</summary>
        private static string TryReadRawSnapshot(string source)
        {
            string[] candidates =
            {
                source,
                source + ".bak.1",
                source + ".bak.2",
                source + ".bak"
            };
            foreach (string c in candidates)
            {
                if (!File.Exists(c)) continue;
                try
                {
                    // 2026-10-23 [P1-3]：按魔数自动识别 gzip / 纯文本（旧档兼容）
                    string json = ZhuluSnapshotCodec.Decode(File.ReadAllBytes(c));
                    if (!string.IsNullOrEmpty(json)) return json;
                }
                catch (Exception ex)
                {
                    ZhuluContext.Instance.LogWarning($"[ZhuluSaveCore] 备份读取失败 {Path.GetFileName(c)}: {ex.Message}");
                }
            }
            return null;
        }

        private static int SnapshotVersionOf(string json)
        {
            try
            {
                var jobj = Newtonsoft.Json.Linq.JObject.Parse(json);
                return jobj.Value<int?>("Version") ?? 0;
            }
            catch { return -1; }
        }

        private static ZhuluSnapshotV5 TryReadSnapshotV5(string source)
        {
            string raw = TryReadRawSnapshot(source);
            if (raw == null) return null;
            if (SnapshotVersionOf(raw) < 5) return null;   // 旧版由 Load 上层给出明确提示
            return ZhuluSnapshotV5.Deserialize(raw);
        }


        /// <summary>新世界/串档拒绝时清空所有系统内存。</summary>
        private void ResetAllSystems()
        {
            Domain.KingdomCivilizationService.Instance.ResetRuntime();
            OrthodoxMandateManager.Instance.ResetRuntime();
            Domain.NomadicMandateManager.Instance.ResetRuntime();
            OfficeManager.Instance?.ResetRuntime();
            PolicyManager.Instance?.ResetRuntime();
            TreasuryManager.Instance?.ResetRuntime();
            StabilityManager.Instance?.ResetRuntime();
            Domain.KingdomExt.ResetRuntime();
            SuccessionManager.Instance?.ResetRuntime();
            VassalManager.Instance?.ResetRuntime();
            ClaimManager.Instance?.ResetRuntime();
            CorruptionManager.Instance?.ResetRuntime();
            ChronicleManager.Instance?.ResetRuntime();
            Domain.EraNameService.Instance?.ResetRuntime();
            Adapters.WarfarePatches.ResetRuntime();
            Domain.ZhuluWarService.ResetReparations(); // 2026-08-20：赔款分期计划
            Domain.PersonHistoryManager.Instance.ResetRuntime();
            Domain.IdentityClassifier.Instance.ResetRuntime();
            Domain.FactionManager.Instance.ResetRuntime();
                    Domain.FactionPoliticsManager.Instance.ResetRuntime();
            Domain.RoyalRegistry.Instance.ResetRuntime();
            Domain.KejuManager.Instance.ResetRuntime();
            Domain.FamilyRegistry.Instance.ResetRuntime(); // 谱牒（阶段3：防跨档残留串档）
            // 2026-10-24 LlmFix-1 H4：AI 评价两个运行时状态此前漏清——
            // 换世界后旧世界评语残留内存，还会被 Capture 进新世界的存档（串档）
            Interop.LlmCommentStore.Instance.Clear();
            Interop.LlmChronicleStore.Instance.Clear();   // LlmV2-4：断代史同规则
            Interop.LlmEmperorService.Instance.ResetForNewWorld();
        }

        // ═══════════════ 世界 ID 存取 ═══════════════

        /// <summary>反射获取 MapStats（World.world.map_stats 是 internal 字段，跨程序集需 AccessTools）。</summary>
        private static MapStats GetMapStats()
        {
            try
            {
                var f = HarmonyLib.AccessTools.Field(typeof(MapBox), "map_stats");
                if (f == null) return null;
                return f.GetValue(World.world) as MapStats;
            }
            catch { return null; }
        }

        private static string ReadWorldIdFromCustomData()
        {
            try
            {
                MapStats ms = GetMapStats();
                if (ms?.custom_data?.custom_data_string == null) return "";
                if (ms.custom_data.custom_data_string.TryGetValue(WorldIdKey, out string v) && !string.IsNullOrEmpty(v))
                    return v;
                return "";
            }
            catch { return ""; }
        }

        private static void WriteWorldIdToCustomData(string worldId)
        {
            // P0-3：world_id 写失败=防串档机制失效，必须留信号（限频30s）
            ZhuluGuard.Run("SaveCore.WriteWorldId", () =>
            {
                MapStats ms = GetMapStats();
                if (ms == null) return;
                if (ms.custom_data == null) ms.custom_data = new SaveCustomData();
                if (ms.custom_data.custom_data_string == null)
                    ms.custom_data.custom_data_string = new CustomDataContainer<string>();
                ms.custom_data.custom_data_string[WorldIdKey] = worldId;
            });
        }
    }
}
