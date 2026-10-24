using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using ZhuluRebuild.Core;
using ZhuluRebuild.Domain;
using ZhuluRebuild.Presentation.Routing;
using ZhuluRebuild.Adapters;

namespace ZhuluRebuild.UI.Windows
{
    /// <summary>
    /// 天下史册 v2：古风纪事长卷。
    /// 经验：Initialize 不构建（Canvas 未就绪会偏左）；首次 OnEnable 构建，之后只刷内容；
    /// 卡片用 Row 容器（背景 Image 锚全屏 + 纯色底 -36 防贴图留白偏移 + 文字 Wrap 防溢出黑块）。
    /// 批次8迁移（2026-08-23）：骨架收敛至 ZhuluScrollWindowBase（ContentTopBand=74 容纳 统计12+分类20；
    /// WantsScrollbar=true）——删除手写 RebuildUI 约190行与本地 返回钮/文本/贴图 工具；
    /// 动态牌匾题静态化，王国名由副状态行承载。
    /// </summary>
    public class ZhuluChronicleWindow : ZhuluScrollWindowBase
    {
        public static ZhuluChronicleWindow Instance { get; private set; }

        private Text _subStatText;
        private Text _emptyText;

        // 分类：0全部 1天命 2兵戈 3内政 4人事
        private static readonly string[] CatNames = { "全部", "天命/天眷", "兵戈", "邦交", "内政", "人事" }; // 双天命共用重大事件分类
        private int _currentCategory;
    // 阶段D：限量渲染（防长期世界开窗卡顿——首屏60页+加载更多）
    private const int InitialVisibleEntryCount = 60;
    private const int LoadMoreEntryCount = 60;
    private int _visibleEntryLimit = InitialVisibleEntryCount;
    private int _lastCategory = -1;
        private readonly List<GameObject> _catButtons = new List<GameObject>();
        private readonly List<Text> _catTexts = new List<Text>();

        // 2026-08-20 段头折叠状态（君主段/年段 key → 用户覆盖：true=展开 false=收起；无记录用默认值）
        // 任务13：前任君主段默认收起（当朝展开）——用户点击后记住选择
        private readonly Dictionary<string, bool> _eraState = new Dictionary<string, bool>();

        protected override string WindowTitle => "<b>天下史册 · 纪事长卷</b>";
        protected override float WindowWidth => 350f;
        protected override float WindowHeight => 400f;
        protected override float PlaqueWidth => 250f;
        protected override float ContentTopBand => 74f;   // 牌匾26 + 统计12 + 分类20 + 间距
        protected override bool WantsScrollbar => true;

        public void Initialize()
        {
            Instance = this;
            ZhuluContext.Instance.LogInfo("[ZhuluChronicleWindow] 初始化完成（首次打开时构建，防偏左）");
        }

        private void OnEnable()
        {
            if (World.world == null) return;
            EnsureBuilt();
            RefreshContent();
        }

        public void Show()
        {
            if (World.world != null && gameObject.activeInHierarchy)
            {
                EnsureBuilt();
                RefreshContent();
            }
        }

        public void Hide()
        {
            ZhuluContext.Instance.LogInfo("[ZhuluChronicleWindow] 隐藏史册窗口");
        }

        // ═══════════════════════ 结构（骨架由基类提供） ═══════════════════════

        protected override void BuildContent()
        {
            // 统计副标题（固定头部：挂 Bg 绝对定位不随滚动；x=-8 与原版避让位一致）
            _subStatText = CreateText(Bg, "", 9);
            _subStatText.alignment = TextAnchor.MiddleCenter;
            _subStatText.raycastTarget = false;
            var subRect = _subStatText.GetComponent<RectTransform>();
            subRect.anchorMin = new Vector2(0.5f, 1);
            subRect.anchorMax = new Vector2(0.5f, 1);
            subRect.pivot = new Vector2(0.5f, 1);
            subRect.anchoredPosition = new Vector2(-8, -38);
            subRect.sizeDelta = new Vector2(InnerWidth, 12f);

            // 分类栏（6 按钮均分，金色高亮当前）
            CreateCategoryBar();

            // 空态文本（常驻内容流，不参与清理）
            _emptyText = CreateText(ContentRoot, "<color=#6a6458>此分类暂无纪事。</color>", 10);
            _emptyText.alignment = TextAnchor.MiddleCenter;
            _emptyText.GetComponent<RectTransform>().sizeDelta = new Vector2(InnerWidth, 60f);
            _emptyText.gameObject.SetActive(false);

            RefreshContent();
        }


        private void CreateCategoryBar()
        {
            GameObject barObj = new GameObject("CatBar", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            barObj.transform.SetParent(Bg, false);
            var barRect = barObj.GetComponent<RectTransform>();
            barRect.anchorMin = new Vector2(0.5f, 1);
            barRect.anchorMax = new Vector2(0.5f, 1);
            barRect.pivot = new Vector2(0.5f, 1);
            barRect.anchoredPosition = new Vector2(-8, -52);
            barRect.sizeDelta = new Vector2(InnerWidth, 20f);

            var hg = barObj.GetComponent<HorizontalLayoutGroup>();
            hg.spacing = 4f;
            hg.childAlignment = TextAnchor.MiddleCenter;
            hg.childControlWidth = true;
            hg.childControlHeight = true;
            hg.childForceExpandWidth = true;
            hg.childForceExpandHeight = true;

            _catButtons.Clear();
            _catTexts.Clear();
            for (int i = 0; i < CatNames.Length; i++)
            {
                int idx = i;
                GameObject btnObj = new GameObject("CatBtn" + i, typeof(RectTransform), typeof(Image), typeof(Button));
                btnObj.transform.SetParent(barObj.transform, false);
                var img = btnObj.GetComponent<Image>();
                img.color = new Color(0.14f, 0.14f, 0.17f, 0.9f);
                img.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
                img.type = Image.Type.Sliced;
                var btn = btnObj.GetComponent<Button>();
                btn.onClick.AddListener(() =>
                {
                    _currentCategory = idx;
                    RefreshContent();
                });
                Text t = CreateText(btnObj.transform, CatNames[i], 8); // 阶段2R：分类按钮9→8（6键挤位，与个人史册统一）
                t.alignment = TextAnchor.MiddleCenter;
                Stretch(t.GetComponent<RectTransform>());
                t.raycastTarget = false;
                _catButtons.Add(btnObj);
                _catTexts.Add(t);
            }
        }

        // ═══════════════════════ 内容刷新 ═══════════════════════

        private void RefreshContent()
        {
            if (ContentRoot == null) return;

            string targetKingdomId = UiRouter.Instance.CurrentRoute.ActiveKingdomId;
            var kFact = WorldBoxReadOnlyAdapter.Instance.CaptureKingdomSnapshot(targetKingdomId);
            bool isKingdom = kFact != null && !string.IsNullOrEmpty(targetKingdomId);

            List<ChronicleEntry> entries;
            if (isKingdom)
            {
                // 批次8迁移：王国名由副状态行承载（牌匾静态化）
                entries = ChronicleManager.Instance.GetKingdomChronicle(kFact.KingdomId);
            }
            else
            {
                entries = ChronicleManager.Instance.GetWorldMajorChronicle();
            }
            // 2026-08-19 朝议去重：同国连续年份同结果的朝议条目合并为区间显示
            // （防"各执一词，相持不下"连年刷屏——数据保留，仅显示层归并）
            entries = MergeConsecutiveDebates(entries);

            // 分类计数 + 副标题
            int[] counts = new int[6]; // 阶段2J：6 分类（全部/天命/兵戈/邦交/内政/人事）
            int total = entries == null ? 0 : entries.Count;
            if (entries != null)
            {
                foreach (var e in entries)
                {
                    counts[0]++;
                    if (TypeInCategory(e.Type, 1)) counts[1]++;
                    if (TypeInCategory(e.Type, 2)) counts[2]++;
                    if (TypeInCategory(e.Type, 3)) counts[3]++;
                    if (TypeInCategory(e.Type, 4)) counts[4]++;
                if (TypeInCategory(e.Type, 5)) counts[5]++; // 阶段2J：邦交计数
                }
            }
            for (int i = 0; i < _catTexts.Count; i++)
            {
                if (_catTexts[i] != null) _catTexts[i].text = $"{CatNames[i]}·{counts[i]}";
            }
            for (int i = 0; i < _catButtons.Count; i++)
            {
                if (_catButtons[i] == null) continue;
                var img = _catButtons[i].GetComponent<Image>();
                if (img == null) continue;
                img.color = i == _currentCategory
                    ? new Color(0.20f, 0.17f, 0.09f, 0.95f)   // 选中：金色底
                    : new Color(0.14f, 0.14f, 0.17f, 0.9f);
            }

            int year = ZhuluTime.CurrentYear;
            if (isKingdom)
            {
                _subStatText.text = $"建元 <color=#ffd8d9>{year}</color> 年 · {kFact.KingdomName} · 纪事 <color={ZhuluUIStyle.Hex.Gold}>{total}</color> 则";
            }
            else
            {
                int kingdomCount = World.world?.kingdoms?.list == null ? 0 : World.world.kingdoms.list.Count;
                _subStatText.text = $"建元 <color=#ffd8d9>{year}</color> 年 · 全图 <color={ZhuluUIStyle.Hex.Gold}>{kingdomCount}</color> 国 · 纪事 <color={ZhuluUIStyle.Hex.Gold}>{total}</color> 则";
            }

            // 清空旧卡片（SetActive(false) 立即隐藏，Destroy 延迟释放）
            for (int i = ContentRoot.childCount - 1; i >= 0; i--)
            {
                var child = ContentRoot.GetChild(i);
                if (child == _emptyText.transform) continue;
                child.gameObject.SetActive(false);
                Destroy(child.gameObject);
            }

            // 阶段2M：王国摘要行（国祚/历任/大事/当前）——阶段2N W13 移到清空之后（原建完即毁）
            if (isKingdom)
            {
                CreateKingdomSummaryLine(targetKingdomId, entries);
            }

            // 过滤当前分类
            var filtered = new List<ChronicleEntry>();
            if (entries != null)
            {
                foreach (var e in entries)
                {
                    if (TypeInCategory(e.Type, _currentCategory)) filtered.Add(e);
                }
            }

            // 阶段D：切换分类时重置渲染上限为 60（加载更多只延续当前分类视图）
            if (_currentCategory != _lastCategory)
            {
                _lastCategory = _currentCategory;
                _visibleEntryLimit = InitialVisibleEntryCount;
            }

            if (filtered.Count == 0)
            {
                _emptyText.gameObject.SetActive(true);
                return;
            }
            _emptyText.gameObject.SetActive(false);
            int renderedCards = 0; // 阶段D：实际创建的事件卡总数（上限 _visibleEntryLimit）

            // 阶段2J：重要事件（IsWorldLevelMajor）置顶 + 年段分段（每 100 年分割线）+ 倒序逐条
            var majorList = new List<ChronicleEntry>();
            var normalList = new List<ChronicleEntry>();
            foreach (var e in filtered)
            {
                if (e.IsWorldLevelMajor) majorList.Add(e);
                else normalList.Add(e);
            }
            majorList.Sort((a, b) => b.Year.CompareTo(a.Year));
            normalList.Sort((a, b) => b.Year.CompareTo(a.Year));

            // 重要区标题
            if (majorList.Count > 0)
            {
                Text majorTitle = CreateText(ContentRoot, "<color=#f0c060>★ 天下要事</color>", 10);
                majorTitle.alignment = TextAnchor.MiddleLeft;
                majorTitle.GetComponent<RectTransform>().sizeDelta = new Vector2(InnerWidth, 18f);
                foreach (var e in majorList)
                {
                    if (renderedCards >= _visibleEntryLimit) break;
                    try { BuildChronicleCard(e, true); } catch (Exception ex) { ZhuluContext.Instance.LogError("[ChronicleWindow] 卡片构建异常", ex); }
                    renderedCards++;
                }
                // 分割线
                GameObject sep = new GameObject("MajorSep", typeof(RectTransform), typeof(Image));
                sep.transform.SetParent(ContentRoot, false);
                sep.GetComponent<RectTransform>().sizeDelta = new Vector2(InnerWidth, 2f);
                sep.GetComponent<Image>().color = new Color(0.3f, 0.25f, 0.1f, 0.6f);
            }

            // 阶段2M：普通区分段——王国视角=君主段（按 KingChange 切）；天下视角=年段
            // 2026-08-20 段头可折叠：点击段头收起/展开该段（含条数提示）
            // 任务13：前任君主段默认收起（只展开当朝），点击段头可切换
            bool isKingdomView = !string.IsNullOrEmpty(UiRouter.Instance.CurrentRoute.ActiveKingdomId);
            if (isKingdomView)
            {
                // 第一遍：预统计各君主段条数（折叠态显示"N条"）；末段 key = 当朝
                var secCounts = new Dictionary<string, int>();
                var secLabels = new Dictionary<string, string>();
                string scanMonarch = "";
                string scanKey = null;
                foreach (var e in normalList)
                {
                    bool isME = e.Type == ChronicleEventType.KingChange || e.Type == ChronicleEventType.DynastyFounding;
                    if (isME && !string.IsNullOrEmpty(e.PrimaryPersonName) && e.PrimaryPersonName != "邑")
                    {
                        string mn = System.Text.RegularExpressions.Regex.Replace(e.PrimaryPersonName, "[ ·-]*(皇帝|天王|天可汗|可汗|君|王)$", "");
                        if (mn != scanMonarch)
                        {
                            scanMonarch = mn;
                            scanKey = mn + "@" + e.Year;
                            secLabels[scanKey] = $"◆ {mn} 朝（建元 {e.Year} 起）";
                        }
                    }
                    if (scanKey != null)
                        secCounts[scanKey] = secCounts.TryGetValue(scanKey, out var c) ? c + 1 : 1;
                }
                string currentKey = scanKey;   // 最后一段 = 当朝（默认展开）

                // 第二遍：渲染（折叠段跳过条目；前任段默认收起）
                string lastMonarch = "";
                string activeKey = null;
                foreach (var e in normalList)
                {
                    bool isMonarchEvent = e.Type == ChronicleEventType.KingChange || e.Type == ChronicleEventType.DynastyFounding;
                    if (isMonarchEvent && !string.IsNullOrEmpty(e.PrimaryPersonName) && e.PrimaryPersonName != "邑")
                    {
                        string mName = System.Text.RegularExpressions.Regex.Replace(e.PrimaryPersonName, "[ ·-]*(皇帝|天王|天可汗|可汗|君|王)$", "");
                        if (mName != lastMonarch)
                        {
                            if (lastMonarch != "")
                            {
                                Text eraEnd = CreateText(ContentRoot, $"<color=#8a8578>—— {lastMonarch} 朝止于 {e.Year} 年 ——</color>", 7);
                                eraEnd.alignment = TextAnchor.MiddleCenter;
                                eraEnd.GetComponent<RectTransform>().sizeDelta = new Vector2(InnerWidth, 12f);
                            }
                            lastMonarch = mName;
                            activeKey = mName + "@" + e.Year;
                            CreateEraHeader(activeKey, secLabels[activeKey],
                                secCounts.TryGetValue(activeKey, out var kc) ? kc : 0,
                                defaultCollapsed: activeKey != currentKey);
                        }
                    }
                    if (activeKey != null && IsEraCollapsed(activeKey, activeKey != currentKey)) continue;
                    if (renderedCards >= _visibleEntryLimit) break;
                    try { BuildChronicleCard(e, false); } catch (Exception ex) { ZhuluContext.Instance.LogError("[ChronicleWindow] 卡片构建异常", ex); }
                    renderedCards++;
                }
            }
            else
            {
                // 天下视角：年段（每 100 年），可折叠
                var eraCounts = new Dictionary<string, int>();
                foreach (var e in normalList)
                {
                    string k = "era@" + ((e.Year / 100) * 100);
                    eraCounts[k] = eraCounts.TryGetValue(k, out var c) ? c + 1 : 1;
                }
                int lastEra = -1;
                string activeKey = null;
                foreach (var e in normalList)
                {
                    int eraStart = (e.Year / 100) * 100;
                    if (eraStart != lastEra)
                    {
                        lastEra = eraStart;
                        activeKey = "era@" + eraStart;
                        CreateEraHeader(activeKey, $"建元 {eraStart} 纪",
                            eraCounts.TryGetValue(activeKey, out var ec) ? ec : 0);
                    }
                    if (activeKey != null && IsEraCollapsed(activeKey, false)) continue;
                    if (renderedCards >= _visibleEntryLimit) break;
                    try { BuildChronicleCard(e, false); } catch (Exception ex) { ZhuluContext.Instance.LogError("[ChronicleWindow] 卡片构建异常", ex); }
                    renderedCards++;
                }
            }
            // 阶段D：计数 + 加载更多（只在还有未显示条目时创建）
            if (filtered.Count > 0)
            {
                int totalShown = renderedCards;
                Text infoT = CreateText(ContentRoot, totalShown < filtered.Count
                    ? $"<color=#8a8578>共 {filtered.Count} 条，当前显示 {totalShown} 条</color>"
                    : $"<color=#8a8578>共 {filtered.Count} 条</color>", 8);
                infoT.alignment = TextAnchor.MiddleCenter;
                infoT.GetComponent<RectTransform>().sizeDelta = new Vector2(InnerWidth - 16f, 16f);
                if (totalShown < filtered.Count && _visibleEntryLimit < filtered.Count)
                {
                    GameObject more = new GameObject("LoadMore", typeof(RectTransform), typeof(Image), typeof(Button));
                    more.transform.SetParent(ContentRoot, false);
                    var mRect = more.GetComponent<RectTransform>();
                    mRect.sizeDelta = new Vector2(InnerWidth - 16f, 26f);
                    ZhuluUIStyle.SetSlicedSprite(more.GetComponent<Image>(), "ui/special/backgroundKingdomElement");
                    Text mText = CreateText(more.transform, $"<color={UI.ZhuluUIStyle.Hex.Warn}>▼ 加载更多（尚有 {filtered.Count - totalShown} 条）</color>", 9);
                    mText.alignment = TextAnchor.MiddleCenter;
                    mText.GetComponent<RectTransform>().anchorMin = Vector2.zero;
                    mText.GetComponent<RectTransform>().anchorMax = Vector2.one;
                    mText.GetComponent<RectTransform>().sizeDelta = Vector2.zero;
                    var img0 = more.GetComponent<Image>();
                    img0.raycastTarget = true;
                    img0.color = new Color(1f, 1f, 1f, 0.02f);
                    more.GetComponent<Button>().onClick.AddListener(() => { _visibleEntryLimit += LoadMoreEntryCount; RefreshContent(); });
                }
            }
        }

        /// <summary>段折叠状态：用户点击覆盖优先，否则用默认（任务13：前任君主段默认收起）。
        /// 批次9d：持久化——跨会话记忆玩家选择。</summary>
        private bool IsEraCollapsed(string key, bool defaultCollapsed)
        {
            return !ZhuluRebuild.Persistence.ZhuluPreferences.GetFold("chronicle:" + key, !defaultCollapsed);
        }

        /// <summary>2026-08-20 段头行：可点击折叠/展开（▼/▶ + 段名 + 条数）。</summary>
        private void CreateEraHeader(string key, string label, int count, bool defaultCollapsed = false)
        {
            bool collapsed = IsEraCollapsed(key, defaultCollapsed);
            string arrow = collapsed ? "▶" : "▼";
            GameObject h = new GameObject("EraHdr", typeof(RectTransform), typeof(Image), typeof(Button));
            h.transform.SetParent(ContentRoot, false);
            var hRect = h.GetComponent<RectTransform>();
            hRect.sizeDelta = new Vector2(InnerWidth, 18f);
            var hImg = h.GetComponent<Image>();
            ZhuluUIStyle.SetSlicedSprite(hImg, "ui/special/backgroundKingdomElement");
            hImg.color = new Color(0.20f, 0.17f, 0.11f, 0.9f);

            Text t = CreateText(h.transform, $"{arrow} <color={ZhuluUIStyle.Hex.GoldDim}>{label}</color> <color=#8a8578>·{count}条</color>", 9);
            t.alignment = TextAnchor.MiddleLeft;
            var tRect = t.GetComponent<RectTransform>();
            tRect.anchorMin = new Vector2(0f, 0f);
            tRect.anchorMax = new Vector2(1f, 1f);
            tRect.offsetMin = new Vector2(8f, 0f);
            tRect.offsetMax = new Vector2(-6f, 0f);
            t.raycastTarget = false;

            h.GetComponent<Button>().onClick.AddListener(() =>
            {
                try
                {
                    bool nowExpanded = IsEraCollapsed(key, defaultCollapsed);   // 翻转
                    _eraState[key] = nowExpanded;
                    ZhuluRebuild.Persistence.ZhuluPreferences.SetFold("chronicle:" + key, nowExpanded);   // 批次9d 落盘
                    RefreshContent();
                }
                catch (Exception ex) { ZhuluContext.Instance.LogWarning($"[ChronicleWindow] 段头切换异常: {ex.Message}"); }
            });
        }

        /// <summary>阶段2M：王国史册摘要行（国祚/历任君主/大事数/当前君主——4 格 flex）。</summary>
        private void CreateKingdomSummaryLine(string kingdomId, List<ChronicleEntry> entries)
        {
            try
            {
                if (ContentRoot == null) return;
                Kingdom k = ZhuluRebuild.Persistence.KingdomDataExt.FindKingdomById(kingdomId);
                if (k == null) return;
                // 国祚（最早事件年）
                int startYear = 0;
                int monarchCount = 0;
                if (entries != null)
                {
                    foreach (var e in entries)
                    {
                        if (e == null) continue;
                        if (startYear == 0 || e.Year < startYear) startYear = e.Year;
                        if (e.Type == ChronicleEventType.KingChange || e.Type == ChronicleEventType.DynastyFounding)
                            monarchCount++;
                    }
                }
                int curYear = ZhuluTime.CurrentYear;
                int reign = startYear > 0 ? curYear - startYear : 0;
                string curKing = k.king != null ? (k.king.getName() ?? "?") : "虚位";
                int total = entries != null ? entries.Count : 0;

                GameObject bar = new GameObject("KingSummary", typeof(RectTransform), typeof(HorizontalLayoutGroup));
                bar.transform.SetParent(ContentRoot, false);
                bar.GetComponent<RectTransform>().sizeDelta = new Vector2(InnerWidth, 24f);
                HorizontalLayoutGroup hg = bar.GetComponent<HorizontalLayoutGroup>();
                hg.spacing = 4f; hg.padding = new RectOffset(0, 0, 2, 2);
                hg.childControlWidth = true; hg.childControlHeight = true; hg.childForceExpandWidth = true;
                CreateSummaryCell(bar.transform, $"<b>{reign}</b>", "国祚年");
                CreateSummaryCell(bar.transform, $"<b>{monarchCount}</b>", "历任君主");
                CreateSummaryCell(bar.transform, $"<b>{total}</b>", "大事则");
                CreateSummaryCell(bar.transform, $"<b>{curKing}</b>", "当前君主");
            }
            catch { }
        }

        private void CreateSummaryCell(Transform parent, string value, string label)
        {
            GameObject cell = new GameObject("SumCell", typeof(RectTransform), typeof(Image));
            cell.transform.SetParent(parent, false);
            Image img = cell.GetComponent<Image>();
            img.color = new Color(0.10f, 0.14f, 0.20f, 0.85f);
            img.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            img.type = Image.Type.Sliced;
            Text t = CreateText(cell.transform, $"<b><color={UI.ZhuluUIStyle.Hex.Warn}>{value}</color></b>\n<size=7><color=#8a8578>{label}</color></size>", 9);
            t.alignment = TextAnchor.MiddleCenter;
            RectTransform tr = t.GetComponent<RectTransform>();
            tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one;
            tr.offsetMin = Vector2.zero; tr.offsetMax = Vector2.zero;
        }

        /// <summary>分类映射：0全部 1天命 2兵戈 3内政 4人事</summary>
        private static bool TypeInCategory(ChronicleEventType t, int cat)
        {
            switch (cat)
            {
                case 1: return t == ChronicleEventType.MandateGranted || t == ChronicleEventType.MandateCollapsed || t == ChronicleEventType.DynasticChange;
                case 2: return t == ChronicleEventType.WarDeclared || t == ChronicleEventType.KingdomFallen;
                case 3: return t == ChronicleEventType.PeaceTreaty || t == ChronicleEventType.NonAggression || t == ChronicleEventType.VassalChange; // 阶段2J：邦交
                case 4: return t == ChronicleEventType.PolicyEnacted || t == ChronicleEventType.PolicyRevoked || t == ChronicleEventType.TierChange;
                case 5: return t == ChronicleEventType.KingChange || t == ChronicleEventType.OfficeAppointed || t == ChronicleEventType.DynastyFounding;
                default: return true;
            }
        }

        /// <summary>2026-08-19 事件类型 → 资源包贴图路径（贴图优先；null=无专属贴图，回退 TypeIcon emoji）。</summary>
        private static string TypeIconPath(ChronicleEventType t)
        {
            switch (t)
            {
                case ChronicleEventType.DynastyFounding: return "ui/zhulu/events/zlr_event_realm_founded_16";
                case ChronicleEventType.DynasticChange: return "ui/zhulu/events/zlr_event_throne_vacant_16";
                case ChronicleEventType.KingChange: return "ui/zhulu/events/zlr_event_ruler_changed_16";
                case ChronicleEventType.WarDeclared: return "ui/zhulu/events/zlr_event_war_started_16";
                case ChronicleEventType.KingdomFallen: return "ui/zhulu/events/zlr_event_realm_destroyed_16";
                case ChronicleEventType.PeaceTreaty: return "ui/zhulu/events/zlr_event_war_ended_16";
                case ChronicleEventType.NonAggression: return "ui/zhulu/events/zlr_event_war_ended_16";
                case ChronicleEventType.VassalChange: return "ui/zhulu/events/zlr_event_city_leader_16";
                case ChronicleEventType.OfficeAppointed: return "ui/zhulu/events/zlr_event_office_appointed_16";
                case ChronicleEventType.PolicyEnacted: return "ui/zhulu/events/zlr_event_policy_started_16";
                case ChronicleEventType.PolicyRevoked: return "ui/zhulu/events/zlr_event_policy_ended_16";
                case ChronicleEventType.MandateGranted: return "ui/zhulu/governance/zlr_mandate_blessed_24";
                case ChronicleEventType.MandateCollapsed: return "ui/zhulu/governance/zlr_mandate_lost_24";
                default: return null;   // TierChange/CourtDebate 等无专属贴图 → emoji
            }
        }

        /// <summary>阶段2M：事件类型 → 图标（emoji——游戏字体安全；贴图缺失时的回退）。</summary>
        private static string TypeIcon(ChronicleEventType t)
        {
            switch (t)
            {
                case ChronicleEventType.DynastyFounding: return "🏛";
                case ChronicleEventType.DynasticChange: return "♛";
                case ChronicleEventType.KingChange: return "👑";
                case ChronicleEventType.WarDeclared: return "⚔";
                case ChronicleEventType.KingdomFallen: return "💀";
                case ChronicleEventType.PeaceTreaty: return "🕊";
                case ChronicleEventType.NonAggression: return "🤝";
                case ChronicleEventType.VassalChange: return "🔗";
                case ChronicleEventType.OfficeAppointed: return "📜";
                case ChronicleEventType.PolicyEnacted: return "🏮";
                case ChronicleEventType.PolicyRevoked: return "🚫";
                case ChronicleEventType.MandateGranted: return "☯";
                case ChronicleEventType.MandateCollapsed: return "⛔";
                case ChronicleEventType.TierChange: return "🏅";
                default: return "📄";
            }
        }

        /// <summary>事件类型 → 中文标签 + 色板</summary>
        private static string TypeTag(ChronicleEventType t, out string colorHex)
        {
            switch (t)
            {
                case ChronicleEventType.MandateGranted: colorHex = ZhuluUIStyle.Hex.Gold; return "受命";
                case ChronicleEventType.MandateCollapsed: colorHex = "#e06a5a"; return "崩绝";
                case ChronicleEventType.WarDeclared: colorHex = "#f73939"; return "战争";
                case ChronicleEventType.KingdomFallen: colorHex = "#ff5f4f"; return "覆灭";
                case ChronicleEventType.PolicyEnacted: colorHex = "#70d468"; return "政令";
                case ChronicleEventType.PolicyRevoked: colorHex = "#9acf8a"; return "废令";
                case ChronicleEventType.KingChange: colorHex = "#6ca3fd"; return "继位";
                case ChronicleEventType.OfficeAppointed: colorHex = "#05fca6"; return "任免";
                case ChronicleEventType.DynastyFounding: colorHex = "#e8b84a"; return "开国";
                // 阶段2J：新类型标签
                case ChronicleEventType.PeaceTreaty: colorHex = "#6ca3fd"; return "和约";
                case ChronicleEventType.NonAggression: colorHex = "#6ca3fd"; return "互不侵犯";
                case ChronicleEventType.VassalChange: colorHex = "#70d468"; return "附庸";
                case ChronicleEventType.DynasticChange: colorHex = "#e8b84a"; return "改朝换代";
                case ChronicleEventType.TierChange: colorHex = ZhuluUIStyle.Hex.Gold; return "爵位";
                default: colorHex = "#8a8578"; return "纪事";
            }
        }

        /// <summary>2026-08-19 朝议归并：同国连续年份（相邻且年份差 1）同文本的朝议条目
        /// 合并为一条区间条目（Year=起始旧年，YearEnd=终止新年）。
        /// 输入按存入序（旧→新，与 ParseKingdomEntries 输出一致）；非朝议条目原样保留。</summary>
        private static List<ChronicleEntry> MergeConsecutiveDebates(List<ChronicleEntry> list)
        {
            if (list == null || list.Count < 2) return list;
            var result = new List<ChronicleEntry>(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (e == null || e.Type != ChronicleEventType.CourtDebate) { result.Add(e); continue; }
                int oldestYear = e.Year;
                int newestYear = e.Year;
                int j = i + 1;
                while (j < list.Count
                    && list[j] != null
                    && list[j].Type == ChronicleEventType.CourtDebate
                    && list[j].KingdomId == e.KingdomId
                    && list[j].ContentText == e.ContentText
                    && list[j].Year == newestYear + 1)
                {
                    newestYear = list[j].Year;
                    j++;
                }
                if (newestYear > oldestYear)
                {
                    result.Add(new ChronicleEntry
                    {
                        Year = oldestYear,
                        YearEnd = newestYear,
                        Type = e.Type,
                        KingdomId = e.KingdomId,
                        KingdomName = e.KingdomName,
                        PrimaryPersonName = e.PrimaryPersonName,
                        ContentText = e.ContentText,
                        IsWorldLevelMajor = e.IsWorldLevelMajor
                    });
                    i = j - 1; // 跳过已吞并的条目
                }
                else
                {
                    result.Add(e);
                }
            }
            return result;
        }

        /// <summary>史册卡片（Row 容器：纯色底 + 贴图装饰层同父级，文字 Wrap 防溢出黑块）。</summary>
        private void BuildChronicleCard(ChronicleEntry e, bool isMajor = false)
        {
            // Row 容器（VLG 直接子物体，宽 = InnerWidth；重要事件略高）
            GameObject row = new GameObject("Chronicle", typeof(RectTransform));
            row.transform.SetParent(ContentRoot, false);
            var rowRect = row.GetComponent<RectTransform>();
            rowRect.sizeDelta = new Vector2(InnerWidth, isMajor ? 22f : 20f);

            // 纯色底：offsetMin.x=-36 向左延长覆盖文字首字（防贴图左装饰留白导致视觉黑块偏右切字），遮罩裁齐列表左缘
            GameObject solidObj = new GameObject("SolidBg", typeof(RectTransform), typeof(Image));
            solidObj.transform.SetParent(row.transform, false);
            var solidRect = solidObj.GetComponent<RectTransform>();
            solidRect.anchorMin = Vector2.zero;
            solidRect.anchorMax = Vector2.one;
            solidRect.offsetMin = new Vector2(-36f, 0f);
            solidRect.offsetMax = Vector2.zero;
            var solidImg = solidObj.GetComponent<Image>();
            solidImg.color = isMajor ? new Color(0.20f, 0.16f, 0.08f, 0.95f) : new Color(0.13f, 0.14f, 0.18f, 0.92f);
            solidImg.raycastTarget = false;

            // 贴图装饰层（同样向左延长）
            GameObject bgObj = new GameObject("Bg", typeof(RectTransform), typeof(Image));
            bgObj.transform.SetParent(row.transform, false);
            var bgRect = bgObj.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = new Vector2(-36f, 0f);
            bgRect.offsetMax = Vector2.zero;
            var bgImg = bgObj.GetComponent<Image>();
            bgImg.color = new Color(0.16f, 0.17f, 0.21f, 0.9f);
            bgImg.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            bgImg.type = Image.Type.Sliced;
            bgImg.raycastTarget = false;

            // 阶段2M：事件图标（Type 映射——左列 16px 固定宽不挤文字）
            // 2026-08-19 资源包接入：优先事件贴图（ui/zhulu/events + governance），无贴图类型回退 emoji
            string iconPath = TypeIconPath(e.Type);
            Sprite iconSprite = null;
            if (!string.IsNullOrEmpty(iconPath))
            {
                try { iconSprite = SpriteTextureLoader.getSprite(iconPath); } catch { }
            }
            if (iconSprite != null)
            {
                GameObject iconImgObj = new GameObject("IconImg", typeof(RectTransform), typeof(Image));
                iconImgObj.transform.SetParent(row.transform, false);
                RectTransform iiRect = iconImgObj.GetComponent<RectTransform>();
                iiRect.anchorMin = new Vector2(0, 0.5f); iiRect.anchorMax = new Vector2(0, 0.5f);
                iiRect.pivot = new Vector2(0, 0.5f);
                iiRect.anchoredPosition = new Vector2(5f, 0f);
                iiRect.sizeDelta = new Vector2(16f, 16f);
                Image ii = iconImgObj.GetComponent<Image>();
                ii.sprite = iconSprite;
                ii.raycastTarget = false;
            }
            else
            {
                string icon = TypeIcon(e.Type);
                Text iconText = CreateText(row.transform, icon, 10);
                iconText.alignment = TextAnchor.MiddleLeft;
                RectTransform iconRect = iconText.GetComponent<RectTransform>();
                iconRect.anchorMin = new Vector2(0, 0.5f); iconRect.anchorMax = new Vector2(0, 0.5f);
                iconRect.pivot = new Vector2(0, 0.5f);
                iconRect.anchoredPosition = new Vector2(4f, 0f);
                iconRect.sizeDelta = new Vector2(18f, 16f);
                iconText.raycastTarget = false;
            }

            // 主行文字：标签 + 年份 + 完整文案（Wrap 换行防溢出黑块，垂直不裁剪）
            string tagColor;
            string tag = TypeTag(e.Type, out tagColor);
            string content = string.IsNullOrEmpty(e.ContentText) ? "" : e.ContentText;
            // 阶段2J：清洗残留颜色标签（<color=#xxxxxx> 完整去除）
            content = System.Text.RegularExpressions.Regex.Replace(content, "<color=#[0-9A-Fa-f]{6}>", "").Replace("</color>", "");
            // 2026-08-19 朝议归并：区间条目显示"建元X-Y年"（如连年相持不下合并后）
            string yearText = e.YearEnd > e.Year ? $"建元{e.Year}-{e.YearEnd}年" : $"建元{e.Year}年";
            string line = (isMajor ? "<color=#f0c060>★</color> " : "")
                + $"<color={tagColor}>【{tag}】</color> <color=#ffd8d9>{yearText}</color> {content}";

            Text mainText = CreateText(row.transform, line, 10);
            mainText.alignment = TextAnchor.MiddleLeft;
            mainText.horizontalOverflow = HorizontalWrapMode.Wrap;
            mainText.verticalOverflow = VerticalWrapMode.Overflow;
            var mainRect = mainText.GetComponent<RectTransform>();
            mainRect.anchorMin = new Vector2(0, 1);
            mainRect.anchorMax = new Vector2(0, 1);
            mainRect.pivot = new Vector2(0, 1);
            mainRect.anchoredPosition = new Vector2(26, -5); // 阶段2M：左移让图标位
            mainRect.sizeDelta = new Vector2(InnerWidth - 34, 16f);
            mainText.raycastTarget = false;

            // 阶段2M：点击条目 → 人物详情（有主要人物时）
            if (!string.IsNullOrEmpty(e.PrimaryPersonName) && e.PrimaryPersonName != "邑")
            {
                string personName = e.PrimaryPersonName;
                string kId = e.KingdomId;
                // 阶段2N W13 修复：row 无 raycastTarget 图形（底图/文字全 false）→ Button 永不触发（点击死区）。
                // 补透明点击层作 targetGraphic（同 FactionWindow ClickLayer 方案）。
                GameObject clickLayer = new GameObject("ClickLayer", typeof(RectTransform), typeof(Image));
                clickLayer.transform.SetParent(row.transform, false);
                var clRect = clickLayer.GetComponent<RectTransform>();
                clRect.anchorMin = Vector2.zero;
                clRect.anchorMax = Vector2.one;
                clRect.offsetMin = Vector2.zero;
                clRect.offsetMax = Vector2.zero;
                var clImg = clickLayer.GetComponent<Image>();
                clImg.color = new Color(1f, 1f, 1f, 0.01f);
                clImg.raycastTarget = true;
                var btn = row.AddComponent<Button>();
                btn.targetGraphic = clImg;
                btn.onClick.AddListener(() =>
                {
                    try
                    {
                        Actor person = null;
                        if (!string.IsNullOrEmpty(personName) && !string.IsNullOrEmpty(kId))
                        {
                            Kingdom kk = ZhuluRebuild.Persistence.KingdomDataExt.FindKingdomById(kId);
                            if (kk?.units != null)
                            {
                                foreach (var u in kk.units)
                                {
                                    if (u != null && u.isAlive() && (u.getName() ?? "").Contains(personName))
                                    { person = u; break; }
                                }
                            }
                        }
                        if (person != null)
                        {
                            MetaTypeLibrary.unit.selectAndInspect(person, false, true, true);
                            ScrollWindow.showWindow(MetaTypeLibrary.unit.window_name);
                        }
                    }
                    catch { }
                });
            }

            float textH = Mathf.Max(16f, mainText.preferredHeight);
            rowRect.sizeDelta = new Vector2(InnerWidth, textH + 10f);
        }

        // ═══════════════════════ 工具 ═══════════════════════

        /// <summary>设置 9宫格贴图（加载失败时回退到原版按钮贴图，避免白块）</summary>
        private static void SetSlicedSprite(Image img, string path)
        {
            ZhuluUIStyle.SetSlicedSprite(img, path);
        }
    }
}
