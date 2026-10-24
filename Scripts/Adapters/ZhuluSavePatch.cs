using System;
using HarmonyLib;
using ZhuluRebuild.Core;
using ZhuluRebuild.Domain;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Adapters
{
    /// <summary>
    /// 逐鹿存档补丁（v2 重构）：
    /// - 保存世界时：ZhuluSaveCore 收集全系统 → 原子写快照（.runtime + 存档目录，旧档 .bak）
    /// - 读取世界时：同步读快照 → world_id 校验 → 分发恢复各系统（进入存档即原样，零等待）
    /// - 新建地图时：生成新 world_id + 清空所有系统
    /// 注意：必须用 Mount() 手动挂载（attribute 方式对 internal 方法匹配失败会静默跳过）。
    /// </summary>
    public static class ZhuluSavePatch
    {
        private static bool _mounted = false;

        /// <summary>
        /// 读档后待执行的国名同步标记。
        /// 2026-10-23 [P0-2]：探针实测 `loadWorld` 触发时 `World.world` 仍是**旧世界**
        /// （.runtime/save_hook_probe.log：loadWorld:before 显示 year/kingdoms 均属旧档），
        /// 此刻做国名同步等于拿旧世界的数据判断"玩家是否手改过国名"，必然误判。
        /// 因此改为在世界就绪（MapBox.finishMakingWorld 后置）再同步。
        /// 注意：finishMakingWorld 在**游戏启动时也会触发**（kingdoms=0），
        /// 故必须用本标记区分"刚读完档"与"启动/新世界"。
        /// </summary>
        private static bool _pendingNameSync = false;

        /// <summary>由 Main.cs 显式挂载存档补丁（attribute 方式对 internal 方法匹配失败会静默跳过，必须手动挂）</summary>
        public static void Mount(Harmony harmony)
        {
            if (_mounted) return;
            _mounted = true;

            // 保存：saveWorldToDirectory(string, bool, bool)
            var saveMethod = AccessTools.Method(typeof(SaveManager), nameof(SaveManager.saveWorldToDirectory), new[] { typeof(string), typeof(bool), typeof(bool) });
            if (saveMethod != null)
            {
                harmony.Patch(saveMethod,
                    prefix: new HarmonyMethod(typeof(ZhuluSavePatch), nameof(SaveWorld_Prefix)));
                ZhuluContext.Instance.LogInfo("[ZhuluSavePatch] 保存补丁挂载成功");
            }
            else
            {
                ZhuluContext.Instance.LogWarning("[ZhuluSavePatch] 未找到 saveWorldToDirectory，保存补丁不可用");
            }

            // 读档：loadWorld 是 internal 方法（attribute 匹配不到），手动 AccessTools 挂载
            var loadMethod = AccessTools.Method(typeof(SaveManager), "loadWorld", new[] { typeof(string), typeof(bool) });
            if (loadMethod == null)
            {
                // 兜底：不限定参数（internal + 可选参数时签名可能不同）
                loadMethod = AccessTools.Method(typeof(SaveManager), "loadWorld");
            }
            if (loadMethod != null)
            {
                harmony.Patch(loadMethod,
                    postfix: new HarmonyMethod(typeof(ZhuluSavePatch), nameof(LoadWorld_Postfix)));
                ZhuluContext.Instance.LogInfo("[ZhuluSavePatch] 读档补丁挂载成功");
            }
            else
            {
                ZhuluContext.Instance.LogWarning("[ZhuluSavePatch] 未找到 loadWorld，读档恢复不可用");
            }

            // 新世界：MapBox.generateNewMap()
            var newMapMethod = AccessTools.Method(typeof(MapBox), nameof(MapBox.generateNewMap));
            if (newMapMethod != null)
            {
                harmony.Patch(newMapMethod,
                    postfix: new HarmonyMethod(typeof(ZhuluSavePatch), nameof(NewMap_Postfix)));
                ZhuluContext.Instance.LogInfo("[ZhuluSavePatch] 新世界补丁挂载成功");
            }
            else
            {
                ZhuluContext.Instance.LogWarning("[ZhuluSavePatch] 未找到 generateNewMap，新世界重置不可用");
            }

            // 世界就绪：MapBox.finishMakingWorld()（读档后的国名同步在此执行）
            var finishMethod = AccessTools.Method(typeof(MapBox), nameof(MapBox.finishMakingWorld));
            if (finishMethod != null)
            {
                harmony.Patch(finishMethod,
                    postfix: new HarmonyMethod(typeof(ZhuluSavePatch), nameof(FinishMakingWorld_Postfix)));
                ZhuluContext.Instance.LogInfo("[ZhuluSavePatch] 世界就绪补丁挂载成功（读档国名同步）");
            }
            else
            {
                ZhuluContext.Instance.LogWarning("[ZhuluSavePatch] 未找到 finishMakingWorld，读档国名同步将退回周期同步");
            }
        }

        // ═══════════════ 保存世界 ═══════════════

        /// <summary>
        /// 保存前：收集全系统快照 → 原子写（.runtime + 存档目录 + .bak 备份）。
        /// 2026-08-20 严格模式（strict_save，默认开）：快照收集/写入失败 → 异常上抛阻止本次游戏存档
        /// （快照与游戏档不一致的残缺档比存不了更糟——借鉴外部存档屏障思路，自研实现）。
        /// </summary>
        public static void SaveWorld_Prefix(string pFolder, bool pCompress, bool pCheckFolder)
        {
            try
            {
                // 玩家可能改名后立刻保存，尚未等到 Main 的 3 秒命名观察 Tick。
                // 保存前先捕获人物/氏族/国名的外部改动，避免读档同步把刚改的名字恢复成旧权威名。
                Naming.ZhuluChineseNameService.Instance.TickAuthority();
                ZhuluSaveCore.Instance.Save(pFolder);
            }
            catch (ZhuluSaveCore.ZhuluSaveBlockedException ex)
            {
                // 严格模式阻断：不吞异常 → Harmony Prefix 异常上抛 → 游戏存档中止
                ZhuluContext.Instance.LogError($"[ZhuluSavePatch] ⛔ 严格存档阻断: {ex.Message}", ex);
                throw;
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[ZhuluSavePatch] 保存快照异常", ex);
            }
        }

        // ═══════════════ 读取世界 ═══════════════

        /// <summary>读档后：同步恢复快照（world_id 校验防串档）→ 名字立即稳定。</summary>
        public static void LoadWorld_Postfix(string pPath)
        {
            try
            {
                if (string.IsNullOrEmpty(pPath)) return;
                ZhuluSaveCore.Instance.Load(pPath);
                ZhuluYearDriverPatch.ResetYear();
                Naming.ZhuluChineseNameService.Instance.ResetPersonNameSeed(); // 阶段2N W7：人名去重种子按新世界重扫
                Domain.TraitManager.Instance.ResetFallenBaseline(); // 2026-08-20：亡国检测基线重置（防跨存档误判）
                // 2026-10-23 [P0-2]：国名同步推迟到世界就绪 —— 此刻 World.world 仍是旧世界。
                _pendingNameSync = true;
                ZhuluContext.Instance.LogInfo("[ZhuluSavePatch] 读档恢复完成（国名同步待世界就绪）");
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[ZhuluSavePatch] 读档恢复异常", ex);
            }
        }

        /// <summary>世界就绪后置（MapBox.finishMakingWorld）：消费读档标记，执行国名同步。</summary>
        public static void FinishMakingWorld_Postfix()
        {
            if (!_pendingNameSync) return;   // 启动 / 新世界不触发
            _pendingNameSync = false;
            try
            {
                SyncKingdomStoredNamesNow();
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning("[ZhuluSavePatch] 世界就绪国名同步异常: " + ex.Message);
            }
        }

        // ═══════════════ 新建地图 ═══════════════

        /// <summary>新世界后：生成新 world_id + 清空所有系统（防串档根）。</summary>
        public static void NewMap_Postfix()
        {
            try
            {
                _pendingNameSync = false;   // [P0-2] 新世界作废任何待执行的读档同步
                ZhuluSaveCore.Instance.NewWorld();
                ZhuluYearDriverPatch.ResetYear();
                Naming.ZhuluChineseNameService.Instance.ResetPersonNameSeed(); // 阶段2N W7：清人名集合防跨世界残留
                Domain.TraitManager.Instance.ResetFallenBaseline(); // 2026-08-20：亡国检测基线重置
                ZhuluContext.Instance.LogInfo("[ZhuluSavePatch] 新世界重置完成");
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[ZhuluSavePatch] 新世界重置异常", ex);
            }
        }

        // ═══════════════ 王国名同步（读档后立即稳定） ═══════════════

        /// <summary>读档后立即迁移并同步纯国名（爵位不写入存档名）。
        /// 阶段3 改名功能：存档里的 data.name 与系统名不一致且无手动名时，认定为玩家改名
        /// （玩家改名后未及 TickAuthority 检测就存档的场景）——保名而非回写。</summary>
        private static void SyncKingdomStoredNamesNow()
        {
            try
            {
                if (World.world?.kingdoms?.list == null) return;
                foreach (var k in World.world.kingdoms.list)
                {
                    if (k == null || !k.isAlive() || k.data == null) continue;
                    if (Persistence.KingdomDataExt.GetStr(k, "zlr_rebel_named") == "1") continue;
                    string kId = k.id.ToString();
                    string stored = KingdomExt.GetStoredName(kId, k.name);
                    if (k.data.name != stored)
                    {
                        // 读档瞬间：系统名格式均为 stored（受命/篡位/换代写入与 GetStoredName 同源），
                        // 不一致即玩家手改未被捕获 → 固化为手动名
                        if (string.IsNullOrWhiteSpace(KingdomExt.GetManualName(kId))
                            && !string.IsNullOrWhiteSpace(k.data.name))
                        {
                            KingdomExt.SetManualName(kId, k.data.name);
                        }
                        else
                        {
                            k.data.name = stored;
                        }
                    }
                }
                ZhuluContext.Instance.LogInfo("[ZhuluSavePatch] 读档后纯国名已立即同步");
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[ZhuluSavePatch] 读档后同步王国名异常", ex);
            }
        }
    }
}
