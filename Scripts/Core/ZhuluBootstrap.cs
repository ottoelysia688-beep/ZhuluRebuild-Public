using System;
using System.Collections.Generic;
using UnityEngine;
using NeoModLoader.General;
using NeoModLoader.General.UI.Tab;
using ZhuluRebuild.Core;
using ZhuluRebuild.Gameplay;
using ZhuluRebuild.Presentation.Routing;
using ZhuluRebuild.UI.Windows;
using ZhuluRebuild.Assets;

namespace ZhuluRebuild
{
    /// <summary>
    /// 启动注册器：负责本地化文本、窗口创建、神力栏Tab注册。
    /// 从 Main.cs 中分离出来，让 Main 只保留生命周期调度。
    /// </summary>
    public static class ZhuluBootstrap
    {
        // ── P0-6（2026-08-23）遗留文件检测 ──

        /// <summary>
        /// 启动时检测 mods 共享根目录的旧版遗留文件（PlayerFactionConfig.json）。
        /// 背景：旧版逐鹿曾把"玩家派系"状态错误落盘到 mods 根目录（全局共享、跨存档污染）；
        /// v2 快照架构已改走存档内持久化，该文件不再被任何代码读取——留着只会让用户困惑。
        /// 只读检测 + 告警日志，不做删除（删除属用户数据操作，交给用户自行处理）。
        /// </summary>
        public static void DetectLegacyRootFiles(string modDirectory)
        {
            try
            {
                if (string.IsNullOrEmpty(modDirectory)) return;
                // net48 无 Path.TrimEndingDirectorySeparator——手动去尾部分隔符
                string trimmed = modDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                string modsRoot = System.IO.Path.GetDirectoryName(trimmed);
                if (string.IsNullOrEmpty(modsRoot)) return;
                string legacy = System.IO.Path.Combine(modsRoot, "PlayerFactionConfig.json");
                if (System.IO.File.Exists(legacy))
                {
                    ZhuluContext.Instance.LogWarning(
                        "[Bootstrap] 检测到 mods 根目录遗留 PlayerFactionConfig.json（旧版逐鹿的落盘遗物，当前版本不读取）。" +
                        $"如无需要可手动删除: {legacy}");
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning("[Bootstrap] 遗留文件检测异常: " + ex.Message);
            }
        }

        /// <summary>注册悬停 Tooltip 与窗口标题的本地化文本。</summary>
        public static void RegisterLocaleTooltips()
        {
            try
            {
                // Tab 标题与描述（TabManager.CreateTab 的第 2、3 参数会被当作 locale key 查找）
                LM.AddToCurrentLocale("逐鹿", "逐鹿");
                LM.AddToCurrentLocale("架空古代政治朝政模拟", "架空古代政治朝政模拟");

                LM.AddToCurrentLocale("zl_world_overview", "天下总览");
                LM.AddToCurrentLocale("zl_world_overview_description", "查看各国国势、官制、政策与邦交");

                LM.AddToCurrentLocale("zl_world_wars", "天下战争");
                LM.AddToCurrentLocale("zl_world_wars_description", "查看战争、厌战、求和意愿与结算预测");

                LM.AddToCurrentLocale("zl_world_chronicle", "天下史册");
                LM.AddToCurrentLocale("zl_world_chronicle_description", "查看天命、天眷、兵戈、邦交、内政与人事");

                LM.AddToCurrentLocale("zl_settings", "逐鹿设置");
                LM.AddToCurrentLocale("zl_settings_description", "配置逐鹿功能、显示和模拟节奏");

                LM.AddToCurrentLocale("zl_mandate", "天命 / 天眷");
                LM.AddToCurrentLocale("zl_mandate_description", "切换查看东方天命与游牧长生天眷");
                LM.AddToCurrentLocale("zl_mandate_window", "天命 · 天眷编年");

                LM.AddToCurrentLocale("zl_world_overview_window", "天下总览 · 政治格局");
                LM.AddToCurrentLocale("zl_world_wars_window", "天下战争 · 战况观察");
                LM.AddToCurrentLocale("zl_world_chronicle_window", "天下史册 · 纪事长卷");
                LM.AddToCurrentLocale("zl_settings_window", "逐鹿设置 · 控制面板");

                LM.AddToCurrentLocale("zl_manual", "玩法速览");
                LM.AddToCurrentLocale("zl_manual_description", "查看东方与游牧双体系玩法说明");
                LM.AddToCurrentLocale("zl_manual_window", "玩法速览");
                LM.AddToCurrentLocale("zl_llm", "大模型接入");
                LM.AddToCurrentLocale("zl_llm_description", " 配置 AI 评价服务与连接测试");
                LM.AddToCurrentLocale("zl_llm_window", "大模型接入 · 史官评配置");
                LM.AddToCurrentLocale("zl_civilization", "种族体制");
                LM.AddToCurrentLocale("zl_civilization_description", "设置其他智慧种族未来新国家采用东方或游牧体制");
                LM.AddToCurrentLocale("zl_civilization_window", "种族体制");

                ZhuluContext.Instance.LogInfo("[Bootstrap] 本地化 Tooltip 与窗口标题注册成功");

                // 逐鹿特质本地化名称
                LM.AddToCurrentLocale("trait_zlr_benevolent_ruler", "仁君");
                LM.AddToCurrentLocale("trait_zlr_benevolent_ruler_info", "治国有方，百姓安乐");
                LM.AddToCurrentLocale("trait_zlr_wise_minister", "贤相");
                LM.AddToCurrentLocale("trait_zlr_wise_minister_info", "才德兼备，辅政有方");
                LM.AddToCurrentLocale("trait_zlr_loyal_subject", "忠臣");
                LM.AddToCurrentLocale("trait_zlr_loyal_subject_info", "忠心耿耿，矢志不渝");
                LM.AddToCurrentLocale("trait_zlr_genius", "天纵奇才");
                LM.AddToCurrentLocale("trait_zlr_genius_info", "天赋异禀，文武全才");
                LM.AddToCurrentLocale("trait_zlr_dynasty_founder", "开国之主");
                LM.AddToCurrentLocale("trait_zlr_dynasty_founder_info", "肇基立业，开创王朝");
                LM.AddToCurrentLocale("trait_zlr_tyrant", "暴君");
                LM.AddToCurrentLocale("trait_zlr_tyrant_info", "穷兵黩武，民不聊生");
                LM.AddToCurrentLocale("trait_zlr_corrupt_official", "贪官");
                LM.AddToCurrentLocale("trait_zlr_corrupt_official_info", "中饱私囊，贪得无厌");
                LM.AddToCurrentLocale("trait_zlr_mediocre", "庸才");
                LM.AddToCurrentLocale("trait_zlr_mediocre_info", "尸位素餐，碌碌无为");
                LM.AddToCurrentLocale("trait_zlr_ambitious", "野心家");
                LM.AddToCurrentLocale("trait_zlr_ambitious_info", "心怀叵测，觊觎大位");
                LM.AddToCurrentLocale("trait_zlr_foolish_ruler", "昏君");
                LM.AddToCurrentLocale("trait_zlr_foolish_ruler_info", "荒淫无道，国库空虚");

                // 阶段2A A1-8：前朝宗室
                LM.AddToCurrentLocale("trait_zlr_former_royal", "前朝宗室");
                LM.AddToCurrentLocale("trait_zlr_former_royal_info", "前朝王室遗族，历经改朝换代仍存于世");

                // 阶段2B 包1：爵位身份特质（显示名本地化——缺注册会显示英文 id）
                LM.AddToCurrentLocale("trait_zlr_tier_tianzi", "天子");
                LM.AddToCurrentLocale("trait_zlr_tier_tianzi_info", "受命于天，君临天下");
                LM.AddToCurrentLocale("trait_zlr_tier_gong", "公");
                LM.AddToCurrentLocale("trait_zlr_tier_gong_info", "大国之君，威震一方");
                LM.AddToCurrentLocale("trait_zlr_tier_hou", "侯");
                LM.AddToCurrentLocale("trait_zlr_tier_hou_info", "诸侯之长，一方屏藩");
                LM.AddToCurrentLocale("trait_zlr_tier_bo", "伯");
                LM.AddToCurrentLocale("trait_zlr_tier_bo_info", "一方伯爵，领有数城");
                LM.AddToCurrentLocale("trait_zlr_tier_zi", "子");
                LM.AddToCurrentLocale("trait_zlr_tier_zi_info", "子爵之君，偏安一隅");
                LM.AddToCurrentLocale("trait_zlr_tier_nan", "邑");
                LM.AddToCurrentLocale("trait_zlr_tier_nan_info", "邑国之君，城小民寡");

                // 阶段2E ⑦：功名特质显示名（缺注册会显示英文 id——爵位教训）
                LM.AddToCurrentLocale("trait_zlr_xiucai", "秀才");
                LM.AddToCurrentLocale("trait_zlr_xiucai_info", "县试及第，文墨可嘉");
                LM.AddToCurrentLocale("trait_zlr_juren", "举人");
                LM.AddToCurrentLocale("trait_zlr_juren_info", "乡试中选，才学出众");
                LM.AddToCurrentLocale("trait_zlr_jinshi", "进士");
                LM.AddToCurrentLocale("trait_zlr_jinshi_info", "殿试登科，国之栋梁");
                LM.AddToCurrentLocale("trait_zlr_zhuangyuan", "状元");
                LM.AddToCurrentLocale("trait_zlr_zhuangyuan_info", "殿试魁首，天下第一");
                LM.AddToCurrentLocale("trait_zlr_wuxiucai", "武秀才");
                LM.AddToCurrentLocale("trait_zlr_wuxiucai_info", "武试及第，弓马娴熟");
                LM.AddToCurrentLocale("trait_zlr_wujuren", "武举人");
                LM.AddToCurrentLocale("trait_zlr_wujuren_info", "武试中选，骁勇善战");
                LM.AddToCurrentLocale("trait_zlr_wujinshi", "武进士");
                LM.AddToCurrentLocale("trait_zlr_wujinshi_info", "武试登科，万人敌也");

                // 2026-08-20 逐鹿专属特质栏（分组标题）
                LM.AddToCurrentLocale("trait_group_zhulu", "逐鹿专属");

                // 2026-08-20 君主风评新增
                LM.AddToCurrentLocale("trait_zlr_sage_ruler", "明君");
                LM.AddToCurrentLocale("trait_zlr_sage_ruler_info", "励精图治，礼乐昌明");
                LM.AddToCurrentLocale("trait_zlr_steady_ruler", "守成之主");
                LM.AddToCurrentLocale("trait_zlr_steady_ruler_info", "萧规曹随，垂拱而治");

                // 2026-08-20 身份特质
                LM.AddToCurrentLocale("trait_zlr_crown_prince", "太子");
                LM.AddToCurrentLocale("trait_zlr_crown_prince_info", "天命储君，国之储贰");
                LM.AddToCurrentLocale("trait_zlr_heir_apparent", "世子");
                LM.AddToCurrentLocale("trait_zlr_heir_apparent_info", "诸侯储嗣，承嗣之家");
                LM.AddToCurrentLocale("trait_zlr_city_lord", "城主");
                LM.AddToCurrentLocale("trait_zlr_city_lord_info", "一城之主，食邑一方");
                LM.AddToCurrentLocale("trait_zlr_fallen_king", "亡国之君");
                LM.AddToCurrentLocale("trait_zlr_fallen_king_info", "社稷倾覆，宗庙为墟");
                LM.AddToCurrentLocale("trait_zlr_restored_king", "复国之君");
                LM.AddToCurrentLocale("trait_zlr_restored_king_info", "失国复起，中兴旧邦");

                // 2026-08-20 官职特质
                LM.AddToCurrentLocale("trait_zlr_office_prime_minister", "丞相");
                LM.AddToCurrentLocale("trait_zlr_office_prime_minister_info", "百官之首，总领朝纲");
                LM.AddToCurrentLocale("trait_zlr_office_personnel", "吏部尚书");
                LM.AddToCurrentLocale("trait_zlr_office_personnel_info", "铨选百官，掌天下人事");
                LM.AddToCurrentLocale("trait_zlr_office_revenue", "户部尚书");
                LM.AddToCurrentLocale("trait_zlr_office_revenue_info", "度支国用，掌天下钱粮");
                LM.AddToCurrentLocale("trait_zlr_office_rites", "礼部尚书");
                LM.AddToCurrentLocale("trait_zlr_office_rites_info", "礼乐典章，掌天下仪制");
                LM.AddToCurrentLocale("trait_zlr_office_military", "兵部尚书");
                LM.AddToCurrentLocale("trait_zlr_office_military_info", "武略韬钤，掌天下兵马");
                LM.AddToCurrentLocale("trait_zlr_office_justice", "刑部尚书");
                LM.AddToCurrentLocale("trait_zlr_office_justice_info", "明刑弼教，掌天下刑狱");
                LM.AddToCurrentLocale("trait_zlr_office_works", "工部尚书");
                LM.AddToCurrentLocale("trait_zlr_office_works_info", "营缮营造，掌天下百工");
                LM.AddToCurrentLocale("trait_zlr_office_local", "地方长吏");
                LM.AddToCurrentLocale("trait_zlr_office_local_info", "牧守一方，代王巡狩");
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[Bootstrap] 注册 Tooltip 文本发生异常", ex);
            }
        }

        /// <summary>使用 WindowCreator 初始化原生可呼出窗口。</summary>
        public static void RegisterWindows()
        {
            RegisterWindow(Main.OverviewWindowId, go => (go.GetComponent<WorldOverviewWindow>() ?? go.AddComponent<WorldOverviewWindow>()).Initialize());
            RegisterWindow(Main.WarsWindowId, go => (go.GetComponent<ZhuluWarsWindow>() ?? go.AddComponent<ZhuluWarsWindow>()).Initialize());
            RegisterWindow(Main.ChronicleWindowId, go => (go.GetComponent<ZhuluChronicleWindow>() ?? go.AddComponent<ZhuluChronicleWindow>()).Initialize());
            RegisterWindow(Main.SettingsWindowId, go => (go.GetComponent<ZhuluSettingsWindow>() ?? go.AddComponent<ZhuluSettingsWindow>()).Initialize());
            RegisterWindow(Main.MandateWindowId, go => (go.GetComponent<ZhuluMandateWindow>() ?? go.AddComponent<ZhuluMandateWindow>()).Initialize());
            RegisterWindow(Main.PersonHistoryWindowId, go => (go.GetComponent<ZhuluPersonHistoryWindow>() ?? go.AddComponent<ZhuluPersonHistoryWindow>()).Initialize());
                RegisterWindow(Main.LlmWindowId, go => (go.GetComponent<UI.Windows.ZhuluLlmWindow>() ?? go.AddComponent<UI.Windows.ZhuluLlmWindow>()).Initialize());
                // 启动时加载大模型配置并应用到服务单例（失败静默，不影响启动）
                try { Interop.LlmConfigStore.ReloadAndApply(); } catch { }
                // LlmFix-5：AI 评语落库后回写谥字/庙号到帝王记录（幂等订阅一次）
                SubscribeAiChoiceWriteback();
            RegisterWindow(Main.KejuWindowId, go => (go.GetComponent<ZhuluKejuWindow>() ?? go.AddComponent<ZhuluKejuWindow>()).Initialize());
            RegisterWindow(Main.FactionWindowId, go => (go.GetComponent<ZhuluFactionWindow>() ?? go.AddComponent<ZhuluFactionWindow>()).Initialize());
            RegisterWindow(Main.FamilyWindowId, go => (go.GetComponent<ZhuluFamilyWindow>() ?? go.AddComponent<ZhuluFamilyWindow>()).Initialize());      // 阶段3 F2
            RegisterWindow(Main.LineageWindowId, go => (go.GetComponent<ZhuluLineageWindow>() ?? go.AddComponent<ZhuluLineageWindow>()).Initialize()); // 阶段3 F4
            RegisterWindow(Main.ClanEventsWindowId, go => (go.GetComponent<ZhuluClanEventsWindow>() ?? go.AddComponent<ZhuluClanEventsWindow>()).Initialize()); // 阶段3 家族大事
            RegisterWindow(Main.ManualWindowId, go => (go.GetComponent<ZhuluManualWindow>() ?? go.AddComponent<ZhuluManualWindow>()).Initialize()); // 玩法速览
            RegisterWindow(Main.CivilizationWindowId, go => (go.GetComponent<ZhuluCivilizationWindow>() ?? go.AddComponent<ZhuluCivilizationWindow>()).Initialize());
            ZhuluContext.Instance.LogInfo("[Bootstrap] 完成逐鹿 13 个原生窗口注册流程（各窗口异常已隔离）");
        }

        private static bool _aiChoiceWritebackSubscribed;

        /// <summary>LlmFix-5：AI 选谥落库 → 回写帝王记录（事件由 Pump 在主线程触发）。</summary>
        private static void SubscribeAiChoiceWriteback()
        {
            if (_aiChoiceWritebackSubscribed) return;
            _aiChoiceWritebackSubscribed = true;
            try
            {
                Interop.LlmEmperorService.Instance.OnCommentApplied +=
                    Domain.OrthodoxMandateManager.Instance.HandleAiCommentApplied;
            }
            catch { }
        }

        private static void RegisterWindow(string windowId, Action<GameObject> initialize)
        {
            try
            {
                var window = WindowCreator.CreateEmptyWindow(windowId, windowId);
                if (window == null || window.gameObject == null)
                {
                    ZhuluContext.Instance.LogWarning($"[Bootstrap] 窗口 {windowId} 创建失败");
                    return;
                }
                initialize(window.gameObject);
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError($"[Bootstrap] 窗口 {windowId} 注册失败（已隔离）", ex);
            }
        }

        /// <summary>
        /// 全量注入 ui/zhulu 贴图到游戏贴图缓存（SpriteTextureLoader.addSprite，与特质/身份图标同机制）。
        /// 反编译实锤：SpriteTextureLoader.getSprite 只查 _cached_sprites + Unity Resources.Load——
        /// mod 目录贴图必须先 addSprite 注入才能被 getSprite 命中。
        /// 批次7美化根修（2026-08-23）：旧版只注入 navigation 子目录——decorations（云纹/竹节分隔）、
        /// events、governance、offices、policies 等 13 个子目录从未注入，组件库 Divider 长期静默回退细线。
        /// 键名与各系统既有约定一致：ui/zhulu/{子目录}/{文件名}；TraitManager/IdentityClassifier/
        /// RoyalClanTraitManager 的后续重复注入为同字节幂等覆盖，无害。
        /// </summary>
        public static void InjectAllUiTextures()
        {
            try
            {
                string root = System.IO.Path.Combine(
                    ZhuluContext.Instance.ModPath, "GameResources", "ui", "zhulu");
                if (!System.IO.Directory.Exists(root))
                {
                    ZhuluContext.Instance.LogWarning("[Bootstrap] 贴图根目录缺失，跳过注入: " + root);
                    return;
                }
                int total = 0;
                var summary = new System.Text.StringBuilder();
                foreach (string dir in System.IO.Directory.GetDirectories(root))
                {
                    string sub = System.IO.Path.GetFileName(dir);
                    int count = 0;
                    foreach (string file in System.IO.Directory.GetFiles(dir, "*.png"))
                    {
                        try
                        {
                            string key = "ui/zhulu/" + sub + "/" + System.IO.Path.GetFileNameWithoutExtension(file);
                            // 2026-10-23 [F8]：走统一注入器，按 key 去重（避免后续系统重复 addSprite 抛键冲突）
                            if (ZhuluRebuild.Assets.ZhuluSpriteInjector.TryInject(key, file)) count++;
                        }
                        catch (Exception ex)
                        {
                            ZhuluContext.Instance.LogWarning($"[Bootstrap] 贴图注入异常 {sub}/{System.IO.Path.GetFileName(file)}: {ex.Message}");
                        }
                    }
                    if (count > 0) summary.Append(sub).Append('=').Append(count).Append(' ');
                    total += count;
                }
                ZhuluContext.Instance.LogInfo($"[Bootstrap] UI 贴图全量注入完成：{total} 张 [{summary.ToString().TrimEnd()}]");
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[Bootstrap] UI 贴图注入整体异常", ex);
            }
        }

        /// <summary>注册游戏底部神力栏逐鹿 Tab 与入口按钮。</summary>
        public static void RegisterPowerBarTab()
        {
            try
            {
                Sprite tabIcon = ZhuluAssetLoader.Instance.GetSprite("brand/icon")
                                  ?? ZhuluAssetLoader.Instance.GetSprite("brand/zlr_entry_32")
                                  ?? SpriteTextureLoader.getSprite("ui/icons/iconCrown");

                var tab = TabManager.CreateTab("ZhuluRebuild", "逐鹿", "架空古代政治朝政模拟", tabIcon);
                // 注意：SetLayout 是**显式白名单**——AddPowerButton 加进去的按钮若不在这个列表里就不会显示。
                // 2026-10-24 新增 "llm"（大模型接入），漏加会表现为"代码没错但按钮不出现"。
                tab.SetLayout(new List<string> { "overview", "war", "history", "mandate", "civilization", "manual", "settings", "llm" });

                // 2026-08-20 专属导航贴图（32×32 古风水墨一套：神力栏 6 张）
                Sprite iconOverview = ZhuluAssetLoader.Instance.GetSprite("navigation/zlr_nav_world_32")
                                       ?? SpriteTextureLoader.getSprite("ui/icons/iconScroll");
                Sprite iconWars = ZhuluAssetLoader.Instance.GetSprite("navigation/zlr_nav_war_32")
                                   ?? SpriteTextureLoader.getSprite("ui/icons/iconSword");
                Sprite iconChronicle = ZhuluAssetLoader.Instance.GetSprite("navigation/zlr_nav_chronicle_32")
                                      ?? SpriteTextureLoader.getSprite("ui/icons/iconScroll");
                Sprite iconSettings = ZhuluAssetLoader.Instance.GetSprite("navigation/zlr_nav_settings_32")
                                      ?? SpriteTextureLoader.getSprite("ui/icons/iconOptions");

                var btnOverview = PowerButtonCreator.CreateSimpleButton("zl_world_overview", OnOverviewClick, iconOverview);
                var btnWars = PowerButtonCreator.CreateSimpleButton("zl_world_wars", OnWarsClick, iconWars);
                var btnChronicle = PowerButtonCreator.CreateSimpleButton("zl_world_chronicle", OnChronicleClick, iconChronicle);
                var btnSettings = PowerButtonCreator.CreateSimpleButton("zl_settings", OnSettingsClick, iconSettings);

                tab.AddPowerButton("overview", btnOverview);
                tab.AddPowerButton("war", btnWars);
                tab.AddPowerButton("history", btnChronicle);
                tab.AddPowerButton("settings", btnSettings);

                // 用户指定：种族体制按钮暂用设置贴图。
                var btnCivilization = PowerButtonCreator.CreateSimpleButton("zl_civilization", OnCivilizationClick, iconSettings);
                tab.AddPowerButton("civilization", btnCivilization);

                Sprite iconMandate = ZhuluAssetLoader.Instance.GetSprite("navigation/zlr_nav_mandate_32")
                                      ?? SpriteTextureLoader.getSprite("ui/icons/iconCrown");
                var btnMandate = PowerButtonCreator.CreateSimpleButton("zl_mandate", OnMandateClick, iconMandate);
                tab.AddPowerButton("mandate", btnMandate);

                // 第 6 按钮：玩法速览（手册——同源《逐鹿玩法速览.txt》）
                Sprite iconManual = ZhuluAssetLoader.Instance.GetSprite("navigation/zlr_nav_manual_32")
                                     ?? SpriteTextureLoader.getSprite("ui/icons/iconScroll");
                var btnManual = PowerButtonCreator.CreateSimpleButton("zl_manual", OnManualClick, iconManual);
                tab.AddPowerButton("manual", btnManual);

                // 2026-10-24 大模型接入：独立窗口，与天下总览等并列（用户指定）
                var btnLlm = PowerButtonCreator.CreateSimpleButton("zl_llm", OnLlmClick, iconSettings);
                tab.AddPowerButton("llm", btnLlm);

                tab.UpdateLayout();
                ZhuluContext.Instance.LogInfo("[Bootstrap] 成功向 WorldBox 底部神力栏绑定逐鹿 Tab 与古风贴图按钮！");
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[Bootstrap] 注册神力栏 Tab 发生异常", ex);
            }
        }

        // ───────────────────────────── 按钮点击回调 ─────────────────────────────

        private static void OnOverviewClick()
        {
            UiRouter.Instance.NavigateTo(RouteDestination.WorldOverview);
            if (WorldOverviewWindow.Instance != null)
            {
                WorldOverviewWindow.Instance.Show();
            }
            ScrollWindow.showWindow(Main.OverviewWindowId);
        }

        private static void OnWarsClick()
        {
            UiRouter.Instance.NavigateTo(RouteDestination.WorldWars);
            ScrollWindow.showWindow(Main.WarsWindowId);
        }

        private static void OnChronicleClick()
        {
            UiRouter.Instance.NavigateTo(RouteDestination.WorldChronicle);
            if (ZhuluChronicleWindow.Instance != null)
            {
                ZhuluChronicleWindow.Instance.Show();
            }
            ScrollWindow.showWindow(Main.ChronicleWindowId);
        }

        private static void OnSettingsClick()
        {
            UiRouter.Instance.NavigateTo(RouteDestination.Settings);
            if (ZhuluSettingsWindow.Instance != null)
            {
                ZhuluSettingsWindow.Instance.Show();
            }            ScrollWindow.showWindow(Main.SettingsWindowId);
        }

                /// <summary>2026-10-24：大模型接入窗口（独立，与天下总览并列）。</summary>
        private static void OnLlmClick()
        {
            UiRouter.Instance.NavigateTo(RouteDestination.LlmConfig);
            if (UI.Windows.ZhuluLlmWindow.Instance != null)
            {
                UI.Windows.ZhuluLlmWindow.Instance.Show();
            }
            // 关键：Show() 只负责"已激活时重建内容"，
            // 真正把窗口 GameObject 显示出来的是 showWindow —— 漏掉这句表现为"点按钮没反应"。
            ScrollWindow.showWindow(Main.LlmWindowId);
        }
private static void OnMandateClick()
        {
            if (ZhuluMandateWindow.Instance != null)
            {
                ZhuluMandateWindow.Instance.Show();
            }
            ScrollWindow.showWindow(Main.MandateWindowId);
        }

        private static void OnManualClick()
        {
            if (UI.Windows.ZhuluManualWindow.Instance != null)
            {
                UI.Windows.ZhuluManualWindow.Instance.Show();
            }
            ScrollWindow.showWindow(Main.ManualWindowId);
        }

        private static void OnCivilizationClick()
        {
            if (UI.Windows.ZhuluCivilizationWindow.Instance != null)
                UI.Windows.ZhuluCivilizationWindow.Instance.Show();
            ScrollWindow.showWindow(Main.CivilizationWindowId);
        }
    }
}
