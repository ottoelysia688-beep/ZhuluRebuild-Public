using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using HarmonyLib;
using NeoModLoader.api;
using NeoModLoader.General;
using NeoModLoader.General.UI.Tab;
using ZhuluRebuild.Core;
using ZhuluRebuild.Gameplay;
using ZhuluRebuild.Presentation.Routing;
using ZhuluRebuild.UI.Windows;
using ZhuluRebuild.Adapters;
using ZhuluRebuild.Assets;
using ZhuluRebuild.Domain;
using ZhuluRebuild.Naming;

namespace ZhuluRebuild
{
    public class Main : BasicMod<Main>
    {
        public const string OverviewWindowId = "zl_world_overview_window";
        public const string WarsWindowId = "zl_world_wars_window";
        public const string ChronicleWindowId = "zl_world_chronicle_window";
        public const string SettingsWindowId = "zl_settings_window";
        public const string MandateWindowId = "zl_mandate_window";
        public const string PersonHistoryWindowId = "zl_person_history_window";
        public const string KejuWindowId = "zl_keju_window";
        public const string FactionWindowId = "zl_faction_window";
        public const string FamilyWindowId = "zl_family_window";       // 阶段3 F2：家族深度页
        public const string LineageWindowId = "zl_lineage_window";     // 阶段3 F4：宗族谱牒总览
        public const string LlmWindowId = "zl_llm_window";             // 2026-10-24：大模型接入（独立窗口）
        public const string ClanEventsWindowId = "zl_clan_events_window"; // 阶段3：家族大事（原版家族窗口入口）
        public const string ManualWindowId = "zl_manual_window";       // 逐鹿玩法速览（神力栏手册按钮）
        public const string CivilizationWindowId = "zl_civilization_window";

        private float _autoTickTimer = 0f;
        private int _lastSeenYear = -1;
        private int _lastPipelineYear = -1;
        private readonly Dictionary<string, string> _kingIdByKingdom = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _kingNameByKingdom = new Dictionary<string, string>();

        protected override void OnModLoad()
        {
            string modDirectory = GetDeclaration()?.FolderPath ?? Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            ZhuluContext.Instance.Initialize(modDirectory);

            // P0-6：启动检测 mods 根目录旧版遗留文件（只读告警，不删除）
            ZhuluBootstrap.DetectLegacyRootFiles(modDirectory);

            // 阶段2B：NML ModConfig 配置同步（分崩离析开关等，参考鬼谷模式）
            try
            {
                Core.ZhuluModConfig.RegisterLocalization();
                Core.ZhuluModConfig.Initialize(GetConfig());
                Adapters.SplitEventsPatch.Apply();
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[Main Engine] 配置初始化异常: {ex.Message}");
            }

            ZhuluContext.Instance.LogInfo("=========================================");
            ZhuluContext.Instance.LogInfo("  逐鹿 Mod 从零重制版 (ZhuluRebuild) 启动  ");
            ZhuluContext.Instance.LogInfo("=========================================");

            // 0. 初始化快照存档核心（v2：全量快照，唯一权威持久化源）
            Persistence.ZhuluSaveCore.Instance.Initialize(modDirectory);

            // 1. 加载悬停 Tooltip 本地化语言文本
            ZhuluBootstrap.RegisterLocaleTooltips();

            // 2. 使用 WindowCreator 初始化原生可呼出窗口
            ZhuluBootstrap.RegisterWindows();

            // 3. 注册游戏底部神力栏逐鹿 Tab 与四入口按钮
            // 3a. 先全量注入 ui/zhulu 贴图到 SpriteTextureLoader 缓存（导航/装饰/事件/官制等全部子目录，
            //     王国侧边栏/家族/单位窗口/组件库 getSprite 均依赖此缓存）
            ZhuluBootstrap.InjectAllUiTextures();
            ZhuluBootstrap.RegisterPowerBarTab();

            // 4. 安装 Harmony 补丁：统一经 ZhuluPatchRegistry（登记 / 逐项隔离 / 统计 / 可屏蔽）
            //    旧实现是"反射扫描 + 5 处散落手工 Mount"，能装但不可审计（F1 静默失效即由此潜伏）。
            try
            {
                var harmony = new Harmony("zhulu.rebuild.kingdom_tabs");
                string runtimeDir = System.IO.Path.Combine(ZhuluContext.Instance.ModPath ?? "", ".runtime");
                Core.ZhuluPatchRegistry.InstallAll(harmony, runtimeDir);

                // 启动 Hook 自检必须在所有补丁安装完成之后；只读、失败不阻止启动。
                try { Core.ZhuluCompatibilityReport.Run(); }
                catch (Exception czEx) { ZhuluContext.Instance.LogWarning($"[Main Engine] 兼容性报告调用异常: {czEx.Message}"); }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[Main Engine] 挂载 Harmony 补丁发生异常", ex);
            }

            // 5. 注册逐鹿特质到游戏原生特质系统
            TraitManager.Instance.RegisterAll();

            // 5a. 注入身份图标（4 张：文官/武将/世家/寒门，SpriteTextureLoader 缓存）
            IdentityClassifier.InjectIdentityIcons();

            // 5b. 注册自定义战争类型
            WarfareManager.Instance.RegisterWarTypes();

            // 5c. 注册宣称谋划和附庸战争谋划到原版谋划系统
            ClaimManager.Instance.RegisterPlot();
            VassalManager.Instance.RegisterPlot();
            // 2026-08-20 任务5：争夺天命谋划（外交栏——与附庸/独立/宣称/全面战争同栏）
            ZhuluWarService.RegisterMandateWarPlot();

            // 5e. 2026-08-19 王室氏族特质：和谐栏注册王室家族(99)/普通氏族(18)——防王族断代
            Domain.RoyalClanTraitManager.Instance.Init();

            // 5d. 注册逐鹿原生文本 tooltip（动态人名/年号等不刷 missing text 日志——2026-08-20）
            UI.ZhuluRawTooltip.Init();

            // 6. 监听路由状态变化
            UiRouter.Instance.OnRouteChanged += HandleRouteChanged;
        }

        public void Update()
        {
            if (World.world == null) return;

            // 按国年度结算分帧：每帧限时 ~4ms 处理王国队列（跨年帧只入队，彻底消除跨年单帧尖峰）
            ZhuluYearDriverPatch.ProcessPendingKingdoms(4f);

            // 2026-10-24 历代天命接入：把后台线程拿到的 AI 评价结果写库（只在主线程写）
            try { Interop.LlmEmperorService.Instance.Pump(); }
            catch (Exception ex) { ZhuluContext.Instance.LogRateLimited("LlmPump", "[Llm] 结果写库异常: " + ex.Message, 30f); }

            // 低频全局检查（每3秒一次，只做全局性事务）
            _autoTickTimer += Time.deltaTime;
            if (_autoTickTimer < 3.0f) return;
            _autoTickTimer = 0f;

            int currentYear = ZhuluTime.CurrentYear;

            // 年份回退 => 检测到新读档/新世界：恢复被软回滚挂起的管道，并重建观察基线
            if (_lastSeenYear >= 0 && currentYear < _lastSeenYear)
            {
                ModifierPipeline.Instance.Resume();
                _kingIdByKingdom.Clear();
                _kingNameByKingdom.Clear();
                OrthodoxMandateManager.Instance.ResetCache();
                // ⚠️ 不再 RehydrateHistory——历史恢复只由 LoadWorld_Postfix/NewMap_Postfix 负责：
                // 新世界第一帧 World.world 可能仍是旧世界引用，这里恢复会把老存档历史串进新地图
                OrthodoxMandateManager.Instance.RehydrateEraNames();
                ZhuluBenchmark.Reset();
                ZhuluYearDriverPatch.ResetYear();
                Persistence.KingdomDataExt.InvalidateKingdomCache();
                ZhuluContext.Instance.LogInfo($"[Main Engine] 检测到世界年份回退 ({_lastSeenYear} -> {currentYear})，按新世界重建观察基线");
            }
            _lastSeenYear = currentYear;

            // 管道重建（幂等）——批次15性能：从每3秒降为每30秒（只在年份变化时才需要重建）
            if (currentYear != _lastPipelineYear)
            {
                _lastPipelineYear = currentYear;
                OfficeManager.Instance.RehydratePipelineAll();
                PolicyManager.Instance.RehydratePipelineAll();
            }

            // 全局年度结算分帧：每 3 秒执行一组（每年 5 组，跨年后约 15 秒摊完，消除跨年单帧全局尖峰）
            ZhuluYearDriverPatch.RunGlobalYearIfDue(currentYear);

            // 换君观察（F6：只读观察原版自然换君并入史册）
            ObserveKingChanges(currentYear);

            // 重申逐鹿已接管的中文名字，压过其他命名 Mod 的后续改写
            ZhuluChineseNameService.Instance.TickAuthority();

            // 2026-08-18 叛军命名高频兜底（3s 节流）：开局/新叛军窗口期未及年度扫描即命名（幂等）
            // 批次18-B3：静默失败=叛军永远无名且无从排查——限频留痕
            try { Adapters.ZhuluYearDriverPatch.CheckRebelKingdomRename(); }
            catch (Exception ex) { ZhuluContext.Instance.LogRateLimited("RebelRename", "[RebelRename] 叛军命名兜底异常(限频): " + ex.Message, 60f); }

            // 同步纯国名（爵位只由地图铭牌补丁展示）与国王尊称
            SyncKingdomStoredNames();
        }

        private void ObserveKingChanges(int currentYear)
        {
            try
            {
                if (World.world?.kingdoms?.list == null) return;

                foreach (var k in World.world.kingdoms.list)
                {
                    if (k == null || !k.isAlive()) continue;
                    string kId = k.id.ToString();
                    string newKingId = (k.king != null && k.king.isAlive()) ? k.king.getID().ToString() : null;

                    if (_kingIdByKingdom.TryGetValue(kId, out string oldKingId))
                    {
                        if (newKingId != oldKingId && newKingId != null)
                        {
                            _kingNameByKingdom.TryGetValue(kId, out string oldName);
                            if (string.IsNullOrEmpty(oldName)) oldName = "先君";
                            string newName = k.king.getName() ?? "新君";
                            // 阶段2A A1-6：传新旧君 id 供换君分类（直系/旁系/异宗/禅让/篡位）
                            DynastyObserver.Instance.ObserveKingChange(kId, k.name, oldName, newName, oldKingId, newKingId, currentYear);
                            // 阶段2O：传旧君 id——天命史关闭皇帝记录时取德行特质与寿数
                            if (KingdomCivilizationService.Instance.IsEastern(k))
                                OrthodoxMandateManager.Instance.OnKingChange(kId, newName, currentYear, oldKingId);
                            SuccessionManager.Instance.OnKingChange(kId, newName, currentYear);
                            // 阶段2A A2-4：新君即时入皇权党（党争生命周期）
                            try { FactionManager.Instance.EnsureKingInFaction(kId, k.king.getID()); } catch { }

                            // 地基联动：新君个人史册（机遇）+ 身份即时刷新
                            try
                            {
                                long kingId = k.king.getID();
                                PersonHistoryManager.Instance.MarkDetailed(kingId, currentYear);
                                PersonHistoryManager.Instance.RecordEvent(kingId, PersonHistoryManager.CAT_JUNLIN,
                                    $"即位为【{k.name}】之君", currentYear);
                                IdentityClassifier.Instance.RefreshActor(k, k.king, currentYear);
                            }
                            catch (Exception ex2)
                            {
                                ZhuluContext.Instance.LogWarning($"[Main Engine] 新君个人史册联动异常: {ex2.Message}");
                            }
                        }
                    }
                    _kingIdByKingdom[kId] = newKingId;
                    _kingNameByKingdom[kId] = (k.king != null && k.king.isAlive()) ? (k.king.getName() ?? "") : "";
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[Main Engine] 换君观察发生异常", ex);
            }
        }

        private void SyncKingdomStoredNames()
        {
            try
            {
                // 2026-10-06 移出：TitleSuffixPatches.RebuildSuffixes()（君主名后缀重建）——见 docs/移出代码存档-称号后缀.md
                if (World.world?.kingdoms?.list == null) return;

                foreach (var k in World.world.kingdoms.list)
                {
                    if (k == null || !k.isAlive()) continue;
                    // 阶段3 改名功能：改走带玩家改名检测的统一同步（原盲回写会吃掉玩家改名——
                    // "一改就恢复"根因之一；未跟踪王国也纳入检测面）
                    Naming.ZhuluChineseNameService.Instance.SyncKingdomDataName(k, null);
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[Main Engine] 同步纯国名与尊称发生异常", ex);
            }
        }

        private void HandleRouteChanged(RouteState state)
        {
            ZhuluContext.Instance.LogInfo($"[Main Engine] 响应路由改变 -> 目标: {state.Destination}, 国家: {state.ActiveKingdomId ?? "N/A"}");
        }

        public void OnDestroy()
        {
            UiRouter.Instance.OnRouteChanged -= HandleRouteChanged;
        }
    }
}
