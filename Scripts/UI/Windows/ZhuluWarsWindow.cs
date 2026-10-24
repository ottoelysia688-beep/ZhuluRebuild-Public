using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using ZhuluRebuild.Core;
using ZhuluRebuild.Domain;

namespace ZhuluRebuild.UI.Windows
{
    /// <summary>
    /// 天下战争窗口 v2：表格化布局（表头+斑马纹+进度条+最激烈高亮）+ 筛选排序 + 虚拟滚动。
    /// 设计规则（外部思路借鉴）：虚拟滚动/生命周期钩子/性能缓存、表头/斑马纹/金银铜、分帧与缓存。
    /// v2.1 修复：布局根改为窗口 Background（原版 Content 被 Mask 裁剪导致底栏/进度列丢失），列宽按窗口实际宽度动态计算。
    /// </summary>
    public class ZhuluWarsWindow : MonoBehaviour
    {
        public static ZhuluWarsWindow Instance { get; private set; }

        #region 布局常量
        private const float WindowWidth = 320f;
        private const float WindowHeight = 366f;
        private const float RowHeight = 30f;              // 行高（阶段2K：含目标小字第二行）
        private const float HeaderHeight = 18f;           // 表头高
        private const float ScrollTopInset = 102f;        // 顶部(牌匾+统计条+控制栏+表头)占用
        private const float ScrollBottomInset = 34f;      // 底部按钮占用
        private const float ScrollSideInset = 6f;
        private const float RefreshInterval = 5f;         // 定时刷新秒

        // 列宽（进度列右侧起点 = 动态剩余）
        private const float ColType = 50f;
        private const float ColDur = 36f;
        private const float ColArmy = 48f;
        private const float ColProg = 56f;
        #endregion

        #region 颜色
        private static readonly Color ColorHeaderBg = new Color(0.15f, 0.15f, 0.22f, 0.95f);
        private static readonly Color ColorRowOdd = new Color(0.12f, 0.12f, 0.16f, 0.5f);
        private static readonly Color ColorRowEven = new Color(0.08f, 0.08f, 0.12f, 0.5f);
        private static readonly Color ColorRowHot = new Color(0.22f, 0.16f, 0.07f, 0.6f);
        private static readonly Color ColorTextNormal = Color.white;
        private static readonly Color ColorTextGray = new Color(0.55f, 0.55f, 0.55f);
        private static readonly Color ColorArmy = new Color(0.53f, 1f, 0.53f);
        private static readonly Color ColorProgressClaim = new Color(1f, 0.53f, 0f);
        private static readonly Color ColorProgressVassal = new Color(0.27f, 0.53f, 1f);
        private static readonly Color ColorProgressIndep = new Color(0.67f, 0.27f, 1f);
        private static readonly Color ColorProgressNormal = new Color(0.6f, 0.6f, 0.6f);
        private static readonly Color ColorProgressTotal = new Color(1f, 0.25f, 0.2f); // 阶段2B 包3：全面战争（血红）
        private static readonly Color ColorBarBg = new Color(0.16f, 0.18f, 0.24f, 0.9f);
        // ── 详情页构成条（2026-08-19：正值绿 #70d468 / 负值红 #f73939）──
        private static readonly Color ColorDetailPos = new Color(0.52f, 0.95f, 0.41f, 1f);
        private static readonly Color ColorDetailNeg = new Color(0.97f, 0.22f, 0.22f, 1f);
        #endregion

        #region 字段
        private Transform _root;                  // 布局根 = 窗口 Background
        private RectTransform _rootRect;
        private Transform _viewportTransform;     // 列表可视区（EmptyText 挂这里）
        private RectTransform _contentRect;
        private Transform _contentTransform;
        private RectTransform _scrollViewRect;
        private GameObject _rowPrefab;
        private float _rowWidth = 300f;
        private Text _emptyText;
        private Text _statsText;

        private readonly List<War> _sortedWars = new List<War>();
        // 批次9b：虚拟回收器泛型化（原 _rowByIndex/_rowInstances/RenderVisibleRows/CreateRow/ClearRows 收编）
        private ZhuluVirtualList<War> _warList;
        private float _lastRefreshTime = 0f;
        private int _currentFilter = 0;   // 0全部 1附庸 2宣称 3独立 4其他
        private int _currentSort = 0;     // 0按兵力 1按持续 2按类型
        private bool _structureBuilt = false;  // 窗口结构只构建一次，避免反复 Destroy/重建导致卡顿

        // ── 战争详情页（2026-08-19：_detailWar 为 null=列表模式，非 null=详情模式）──
        private War _detailWar;                                    // 详情页当前战争
        private GameObject _detailRoot;                            // 详情视图根（返回列表时整体销毁）
        private readonly List<GameObject> _detailCards = new List<GameObject>(); // 详情卡集合（随根销毁）
        private RectTransform _detailContentRect;                  // 详情滚动内容容器
        private GameObject _listStatsBar;                          // 列表模式专属控件（详情模式隐藏）
        private GameObject _listCtrlBar;
        private GameObject _listTableHeader;
        private GameObject _listFooter;
        #endregion

        #region 初始化
        public void Initialize()
        {
            Instance = this;
            // 不在此构建结构：窗口创建时 Canvas 布局未就绪（rect.width 旧值），锚点会偏左。
            // 改为首次 OnEnable（窗口真正打开、布局就绪）时构建，之后只刷新数据。
            ZhuluContext.Instance.LogInfo("[ZhuluWarsWindow] 天下战争窗口v2初始化完成");
        }

        private void OnEnable()
        {
            if (World.world == null) return;
            if (!_structureBuilt)
            {
                RebuildUI();
                if (_root != null) _structureBuilt = true; // 阶段2N：FindRoot 失败不置位——否则永久空白且不再重试
            }
            else
            {
                RefreshView(); // 2026-08-19：详情模式下重开窗口需重建详情取新数据
            }
        }

        public void Show()
        {
            if (World.world == null) return;
            if (!_structureBuilt)
            {
                RebuildUI();
                if (_root != null) _structureBuilt = true;
            }
            else
            {
                RefreshView();
            }
        }

        public void Hide() { }

        private void Update()
        {
            if (!gameObject.activeInHierarchy) return;
            if (_detailWar != null) return; // 2026-08-19：详情模式为静态读档视图，暂停列表轮询与虚拟滚动
            if (_warList == null || _warList.Count == 0) return;

            if (Time.time - _lastRefreshTime > RefreshInterval)
            {
                _lastRefreshTime = Time.time;
                RefreshList();
                return;
            }

            _warList.Refresh();
        }
        #endregion

        #region 窗口构建
        private void RebuildUI()
        {
            FindRoot();
            if (_root == null) return;
            try { HideCloseButtonFrame(transform); } catch { } // 阶段2N W13：关闭按钮红×保留、隐藏原版方框（传窗口根）

            // 清空旧布局
            for (int i = _root.childCount - 1; i >= 0; i--)
            {
                var child = _root.GetChild(i);
                if (child.name == "Scroll View") continue; // 原版空壳保留也无妨，但销毁更干净
                Destroy(child.gameObject);
            }
            _warList = null; // 批次9b：结构重建时列表实例随行销毁一并作废（prefab 也重建）
            DestroyDetailView(); // 2026-08-19：详情视图随结构重建一并清理，回到列表模式
            _detailWar = null;

            // 可用宽度（窗口宽 - 边框留白；rect 未就绪时用常量兜底）
            float availW = _rootRect.rect.width > 0f ? _rootRect.rect.width : WindowWidth;
            _rowWidth = Mathf.Max(200f, availW - 12f);

            // 2026-08-19 隐藏原版标题（牌匾接管，与其他窗口统一）
            try
            {
                var scrollWin = GetComponent<ScrollWindow>();
                if (scrollWin != null && scrollWin.titleText != null)
                    scrollWin.titleText.gameObject.SetActive(false);
            }
            catch { }

            // 2026-08-19 标题牌匾：与史册/天命同款贴图，统一窗口视觉
            GameObject plaque = new GameObject("TitlePlaque", typeof(RectTransform), typeof(Image));
            plaque.transform.SetParent(_root, false);
            var plaqueRect = plaque.GetComponent<RectTransform>();
            plaqueRect.anchorMin = new Vector2(0.5f, 1);
            plaqueRect.anchorMax = new Vector2(0.5f, 1);
            plaqueRect.pivot = new Vector2(0.5f, 1);
            plaqueRect.anchoredPosition = new Vector2(-8, -4);
            plaqueRect.sizeDelta = new Vector2(250f, 26f);
            ZhuluUIStyle.SetSlicedSprite(plaque.GetComponent<Image>(), "ui/zhulu/panels/zlr_title_plaque_9s");
            ZhuluWindowDrag.Attach(plaque.transform, plaque.transform.parent as RectTransform);
            Text plaqueTitle = CreateLabel(plaque.transform, "PlaqueTitle", "<b>天下战争 · 战况观察</b>", 12, TextAnchor.MiddleCenter, new Color(1f, 0.84f, 0f));
            var ptRect = plaqueTitle.GetComponent<RectTransform>();
            ptRect.anchorMin = Vector2.zero;
            ptRect.anchorMax = Vector2.one;
            ptRect.sizeDelta = Vector2.zero;
            plaqueTitle.raycastTarget = false;

            BuildStatsBar();
            BuildCtrlBar();
            BuildTableHeader();
            BuildListView();
            BuildFooter();

            if (_rowPrefab == null)
            {
                _rowPrefab = BuildRowPrefab();
            }
            // 批次9b：泛型虚拟列表实例（结构重建时上方已置 null 作废）
            if (_warList == null)
            {
                _warList = new ZhuluVirtualList<War>(_scrollViewRect, _contentRect, _contentTransform,
                    _rowPrefab, RowHeight, (rowObj, war, idx) => SetupRow(rowObj, war, idx));
            }

            RefreshList();
        }

        /// <summary>查找窗口根节点并清掉原版 Scroll View 空壳，使用 Background 作为布局根。
        /// 阶段2N W13：主路径改 transform（组件挂在窗口 GO 上——与其余七窗口一致，不依赖场景绝对路径），
        /// GameObject.Find 绝对路径仅作兜底（路径任一级改名/未激活即失败）。</summary>
        private void FindRoot()
        {
            string windowId = Main.WarsWindowId;
            // 主路径：本组件就挂在窗口 GameObject 上
            Transform win = transform;
            // 兜底：绝对路径查找（历史方案）
            if (win == null || win.gameObject == null)
            {
                var windowsRoot = GameObject.Find($"/Canvas Container Main/Canvas - Windows/windows/{windowId}");
                if (windowsRoot == null)
                {
                    ZhuluContext.Instance.LogWarning($"[ZhuluWarsWindow] 未找到窗口根节点: {windowId}");
                    return;
                }
                win = windowsRoot.transform;
            }

            var bg = win.Find("Background");
            if (bg == null) bg = win;
            _root = bg;
            _rootRect = bg.GetComponent<RectTransform>();
            if (_rootRect == null) _rootRect = bg.gameObject.AddComponent<RectTransform>();
            _rootRect.sizeDelta = new Vector2(WindowWidth, WindowHeight);

            // 隐藏原版空壳 Scroll View（不能 Destroy——原版 ScrollWindow.resetScroll 会访问它，销毁导致窗口打开崩溃）
            var oldScroll = bg.Find("Scroll View");
            if (oldScroll != null)
            {
                oldScroll.gameObject.SetActive(false);
            }

            // 2026-08-20 顶部拖动条：拖动统计条区域移动窗口（透明热区，双击复位）
            GameObject dragStrip = new GameObject("DragStrip", typeof(RectTransform), typeof(Image));
            dragStrip.transform.SetParent(bg, false);
            var dsRect = dragStrip.GetComponent<RectTransform>();
            dsRect.anchorMin = dsRect.anchorMax = new Vector2(0.5f, 1f);
            dsRect.pivot = new Vector2(0.5f, 1f);
            dsRect.anchoredPosition = new Vector2(0f, -2f);
            dsRect.sizeDelta = new Vector2(WindowWidth - 80f, 22f);
            var dsImg = dragStrip.GetComponent<Image>();
            dsImg.color = new Color(0f, 0f, 0f, 0f);
            dsImg.raycastTarget = true;
            ZhuluWindowDrag.Attach(dragStrip.transform, bg as RectTransform);
        }

        private Text CreateLabel(Transform parent, string name, string text, int size, TextAnchor align, Color color)
        {
            GameObject obj = new GameObject(name, typeof(RectTransform));
            obj.transform.SetParent(parent, false);
            Text txt = obj.AddComponent<Text>();
            txt.font = LocalizedTextManager.current_font;
            txt.fontSize = size;
            txt.alignment = align;
            txt.color = color;
            txt.text = text;
            txt.horizontalOverflow = HorizontalWrapMode.Wrap;
            txt.verticalOverflow = VerticalWrapMode.Truncate;
            txt.raycastTarget = false;
            return txt;
        }

        /// <summary>2026-08-19 接管式关闭钮：隐藏原版红×与方框，自建逐鹿关闭钮（随窗口拖动）</summary>
        private static void HideCloseButtonFrame(Transform root)
        {
            ZhuluUIStyle.ReplaceCloseButton(root);
        }

        private void BuildStatsBar()
        {
            var stats = CreateLabel(_root, "StatsBar", "", 9, TextAnchor.MiddleCenter, ColorTextGray);
            var rect = stats.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0, 1);
            rect.anchorMax = new Vector2(1, 1);
            rect.pivot = new Vector2(0.5f, 1);
            rect.anchoredPosition = new Vector2(0, -30);
            rect.sizeDelta = new Vector2(-12, 18);
            _statsText = stats;
            _listStatsBar = stats.gameObject; // 2026-08-19：详情模式隐藏
        }

        private void BuildCtrlBar()
        {
            GameObject ctrl = new GameObject("CtrlBar", typeof(RectTransform));
            ctrl.transform.SetParent(_root, false);
            _listCtrlBar = ctrl; // 2026-08-19：详情模式隐藏
            var ctrlRect = ctrl.GetComponent<RectTransform>();
            ctrlRect.anchorMin = new Vector2(0, 1);
            ctrlRect.anchorMax = new Vector2(1, 1);
            ctrlRect.pivot = new Vector2(0.5f, 1);
            ctrlRect.anchoredPosition = new Vector2(0, -52);
            ctrlRect.sizeDelta = new Vector2(-12, 22);

            var filter = CreateDropdown(ctrl.transform, "FilterDropdown", new string[] { "全部类型", "附庸战争", "宣称战争", "独立战争", "全面战争", "叛乱", "其他", "☀争天命/天眷" });
            var fRect = filter.GetComponent<RectTransform>();
            fRect.anchorMin = new Vector2(0, 0.5f);
            fRect.anchorMax = new Vector2(0, 0.5f);
            fRect.pivot = new Vector2(0, 0.5f);
            fRect.anchoredPosition = new Vector2(0, 0);
            fRect.sizeDelta = new Vector2(110, 20);
            filter.onValueChanged.AddListener(i => { _currentFilter = i; RefreshList(); });

            var sort = CreateDropdown(ctrl.transform, "SortDropdown", new string[] { "按兵力排序", "按持续年数", "按类型分组" });
            var sRect = sort.GetComponent<RectTransform>();
            sRect.anchorMin = new Vector2(1, 0.5f);
            sRect.anchorMax = new Vector2(1, 0.5f);
            sRect.pivot = new Vector2(1, 0.5f);
            sRect.anchoredPosition = new Vector2(0, 0);
            sRect.sizeDelta = new Vector2(110, 20);
            sort.onValueChanged.AddListener(i => { _currentSort = i; RefreshList(); });
        }

        private void BuildTableHeader()
        {
            GameObject header = new GameObject("TableHeader", typeof(RectTransform), typeof(Image));
            header.transform.SetParent(_root, false);
            _listTableHeader = header; // 2026-08-19：详情模式隐藏
            var rect = header.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0, 1);
            rect.anchorMax = new Vector2(1, 1);
            rect.pivot = new Vector2(0.5f, 1);
            rect.anchoredPosition = new Vector2(0, -78);
            rect.sizeDelta = new Vector2(-12, HeaderHeight);
            header.GetComponent<Image>().color = ColorHeaderBg;

            var tr = header.transform;
            float warW = _rowWidth - ColType - ColDur - ColArmy - ColProg; // 交战双方列=剩余宽度
            var t1 = CreateLabel(tr, "HType", "类型", 8, TextAnchor.MiddleCenter, ColorTextGray);
            SetColRect(t1, 0, ColType);
            var t2 = CreateLabel(tr, "HWar", "交战双方", 8, TextAnchor.MiddleCenter, ColorTextGray);
            SetColRect(t2, ColType, warW);
            var t3 = CreateLabel(tr, "HDur", "持续", 8, TextAnchor.MiddleCenter, ColorTextGray);
            SetColRect(t3, ColType + warW, ColDur);
            var t4 = CreateLabel(tr, "HArmy", "兵力", 8, TextAnchor.MiddleCenter, ColorTextGray);
            SetColRect(t4, ColType + warW + ColDur, ColArmy);
            var t5 = CreateLabel(tr, "HProg", "进度", 8, TextAnchor.MiddleCenter, ColorTextGray);
            SetColRect(t5, ColType + warW + ColDur + ColArmy, ColProg);
        }

        /// <summary>按左偏移定位表头列（左起）</summary>
        private void SetColRect(Text txt, float leftOffset, float width)
        {
            var r = txt.GetComponent<RectTransform>();
            r.anchorMin = new Vector2(0, 0.5f);
            r.anchorMax = new Vector2(0, 0.5f);
            r.pivot = new Vector2(0, 0.5f);
            r.anchoredPosition = new Vector2(leftOffset, 0);
            r.sizeDelta = new Vector2(width, HeaderHeight);
        }

        private void BuildListView()
        {
            GameObject scrollObj = new GameObject("ListView", typeof(RectTransform), typeof(ScrollRect), typeof(Image), typeof(Mask));
            scrollObj.transform.SetParent(_root, false);
            _scrollViewRect = scrollObj.GetComponent<RectTransform>();
            _scrollViewRect.anchorMin = new Vector2(0, 0);
            _scrollViewRect.anchorMax = new Vector2(1, 1);
            _scrollViewRect.offsetMin = new Vector2(ScrollSideInset, ScrollBottomInset);
            _scrollViewRect.offsetMax = new Vector2(-ScrollSideInset, -ScrollTopInset);

            scrollObj.GetComponent<Image>().color = new Color(0, 0, 0, 0.25f);

            var scroll = scrollObj.GetComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 24f;
            scrollObj.GetComponent<Mask>().showMaskGraphic = true;

            // Viewport
            GameObject viewportObj = new GameObject("Viewport", typeof(RectTransform));
            viewportObj.transform.SetParent(scrollObj.transform, false);
            var vpRect = viewportObj.GetComponent<RectTransform>();
            vpRect.anchorMin = Vector2.zero;
            vpRect.anchorMax = Vector2.one;
            vpRect.offsetMin = Vector2.zero;
            vpRect.offsetMax = Vector2.zero;
            _viewportTransform = viewportObj.transform;

            // Content（虚拟滚动容器）
            GameObject contentObj = new GameObject("Content", typeof(RectTransform));
            contentObj.transform.SetParent(viewportObj.transform, false);
            _contentRect = contentObj.GetComponent<RectTransform>();
            _contentRect.anchorMin = new Vector2(0, 1);
            _contentRect.anchorMax = new Vector2(1, 1);
            _contentRect.pivot = new Vector2(0.5f, 1);
            _contentRect.anchoredPosition = Vector2.zero;
            _contentRect.sizeDelta = Vector2.zero;
            _contentTransform = contentObj.transform;

            scroll.viewport = vpRect;
            scroll.content = _contentRect;

            // 空提示（挂 Viewport，不受 Content 高度影响）
            _emptyText = CreateLabel(_viewportTransform, "EmptyText", "天下太平，四海无战事。", 10, TextAnchor.MiddleCenter, ColorTextGray);
            var eRect = _emptyText.GetComponent<RectTransform>();
            eRect.anchorMin = new Vector2(0.5f, 0.5f);
            eRect.anchorMax = new Vector2(0.5f, 0.5f);
            eRect.pivot = new Vector2(0.5f, 0.5f);
            eRect.anchoredPosition = Vector2.zero;
            eRect.sizeDelta = new Vector2(200, 24);
        }

        private void BuildFooter()
        {
            GameObject footer = new GameObject("Footer", typeof(RectTransform));
            footer.transform.SetParent(_root, false);
            _listFooter = footer; // 2026-08-19：详情模式隐藏
            var rect = footer.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0, 0);
            rect.anchorMax = new Vector2(1, 0);
            rect.pivot = new Vector2(0.5f, 0);
            rect.anchoredPosition = new Vector2(0, 5);
            rect.sizeDelta = new Vector2(-12, 26);

            CreateFooterButton(footer.transform, "ToTop", "⬆ 到顶", 0f, () => { if (_warList != null) _warList.ScrollToTop(); });
            CreateFooterButton(footer.transform, "Refresh", "⟳ 刷新", 0.5f, () => { _lastRefreshTime = 0; RefreshList(); });
            CreateFooterButton(footer.transform, "ToBottom", "⬇ 到底", 1f, () => { if (_warList != null) _warList.ScrollToBottom(); });
        }

        private void CreateFooterButton(Transform parent, string name, string text, float xAnchor, Action onClick)
        {
            GameObject obj = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            obj.transform.SetParent(parent, false);
            var rect = obj.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(xAnchor, 0.5f);
            rect.anchorMax = new Vector2(xAnchor, 0.5f);
            rect.pivot = new Vector2(xAnchor, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(64, 20);
            obj.GetComponent<Image>().sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            obj.GetComponent<Image>().type = Image.Type.Sliced;
            var btn = obj.GetComponent<Button>();
            btn.onClick.AddListener(() => onClick?.Invoke());
            btn.targetGraphic = obj.GetComponent<Image>();

            var label = CreateLabel(obj.transform, "Text", text, 9, TextAnchor.MiddleCenter, ColorTextGray);
            var lr = label.GetComponent<RectTransform>();
            lr.anchorMin = Vector2.zero;
            lr.anchorMax = Vector2.one;
            lr.offsetMin = Vector2.zero;
            lr.offsetMax = Vector2.zero;
        }
        #endregion

        #region 下拉框
        private Dropdown CreateDropdown(Transform parent, string name, string[] options)
        {
            GameObject obj = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Dropdown));
            obj.transform.SetParent(parent, false);
            obj.GetComponent<Image>().sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            obj.GetComponent<Image>().type = Image.Type.Sliced;
            // 阶段2N W13：补深色 tint（原默认纯白——筛选/排序下拉是"白块"）
            obj.GetComponent<Image>().color = new Color(0.14f, 0.14f, 0.17f, 0.95f);

            Dropdown dropdown = obj.GetComponent<Dropdown>();
            dropdown.options.Clear();
            foreach (var opt in options) dropdown.options.Add(new Dropdown.OptionData(opt));

            // Label
            GameObject labelObj = new GameObject("Label", typeof(RectTransform), typeof(Text));
            labelObj.transform.SetParent(obj.transform, false);
            var labelRect = labelObj.GetComponent<RectTransform>();
            labelRect.anchorMin = new Vector2(0, 0);
            labelRect.anchorMax = new Vector2(1, 1);
            labelRect.offsetMin = new Vector2(5, 0);
            labelRect.offsetMax = new Vector2(-16, 0);
            Text labelText = labelObj.GetComponent<Text>();
            labelText.font = LocalizedTextManager.current_font;
            labelText.fontSize = 9;
            labelText.alignment = TextAnchor.MiddleCenter;   // 居中，与表头/底部按钮文字对齐
            labelText.color = Color.white;
            dropdown.captionText = labelText;

            // Template
            GameObject templateObj = new GameObject("Template", typeof(RectTransform), typeof(Image), typeof(ScrollRect));
            templateObj.transform.SetParent(obj.transform, false);
            var templateRect = templateObj.GetComponent<RectTransform>();
            templateRect.anchorMin = new Vector2(0, 0);
            templateRect.anchorMax = new Vector2(1, 0);
            templateRect.pivot = new Vector2(0.5f, 1);
            templateRect.anchoredPosition = Vector2.zero;
            templateRect.sizeDelta = new Vector2(0, 80);
            templateObj.GetComponent<Image>().sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            templateObj.GetComponent<Image>().type = Image.Type.Sliced;
            templateObj.GetComponent<Image>().color = new Color(0.14f, 0.14f, 0.17f, 0.95f); // 阶段2N W13：展开面板白块同修
            templateObj.GetComponent<ScrollRect>().movementType = ScrollRect.MovementType.Clamped;

            // Item
            GameObject itemObj = new GameObject("Item", typeof(RectTransform), typeof(Toggle));
            itemObj.transform.SetParent(templateObj.transform, false);
            var itemRect = itemObj.GetComponent<RectTransform>();
            itemRect.anchorMin = new Vector2(0, 0.5f);
            itemRect.anchorMax = new Vector2(1, 0.5f);
            itemRect.sizeDelta = new Vector2(0, 18);
            var itemBg = itemObj.gameObject.AddComponent<Image>();
            itemBg.color = new Color(1f, 1f, 1f, 0.1f);
            itemObj.GetComponent<Toggle>().targetGraphic = itemBg;

            GameObject itemLabelObj = new GameObject("Item Label", typeof(RectTransform), typeof(Text));
            itemLabelObj.transform.SetParent(itemObj.transform, false);
            var itemLabelRect = itemLabelObj.GetComponent<RectTransform>();
            itemLabelRect.anchorMin = Vector2.zero;
            itemLabelRect.anchorMax = Vector2.one;
            itemLabelRect.offsetMin = new Vector2(5, 0);
            itemLabelRect.offsetMax = new Vector2(-5, 0);
            Text itemText = itemLabelObj.GetComponent<Text>();
            itemText.font = LocalizedTextManager.current_font;
            itemText.fontSize = 9;
            itemText.alignment = TextAnchor.MiddleCenter;   // 选项文字也居中
            itemText.color = Color.white;
            dropdown.itemText = itemText;

            dropdown.template = templateRect;
            templateObj.SetActive(false);
            dropdown.RefreshShownValue();
            return dropdown;
        }
        #endregion

        #region 数据刷新
        private void RefreshList()
        {
            // 批次9b：行清空/高度重置/可见区首建 全部由 ZhuluVirtualList.SetItems 承担
            _sortedWars.Clear();

            if (World.world?.wars != null)
            {
                foreach (War war in World.world.wars)
                {
                    if (war == null || war.hasEnded()) continue;
                    if (!PassFilter(war)) continue;
                    _sortedWars.Add(war);
                }
            }

            ApplySort();

            if (_emptyText != null) _emptyText.gameObject.SetActive(_sortedWars.Count == 0);

            // 更新统计条
            if (_statsText != null)
            {
                int vassal = 0, claim = 0, indep = 0, total = 0, rebel = 0, other = 0, mandate = 0;
                foreach (var w in _sortedWars)
                {
                    string t = GetWarTypeId(w);
                    if (t == "zlr_vassal_war") vassal++;
                    else if (t == "zlr_claim_war") claim++;
                    else if (t == "zlr_independence_war") indep++;
                    else if (t == "zlr_total_war") total++; // 阶段2B 包3：全面战争
                    else if (t == ZhuluWarService.MANDATE_WAR_ID) mandate++; // 问鼎
                    else if (t == "inspire") rebel++; // 阶段2K：叛乱
                    else other++;
                }
                // 阶段2K：和约数（扫描 pairwise——国家数少可接受）
                int truceCount = 0;
                try
                {
                    // 阶段3H-16性能：O(K²) 配对扫描按年缓存（和约数一年内不变，仅跨年首刷重算）
                    int year = ZhuluTime.CurrentYear;
                    if (_truceCountYear != year)
                    {
                        _truceCountYear = year;
                        _truceCount = 0;
                        var list = World.world?.kingdoms?.list;
                        if (list != null)
                        {
                            for (int i = 0; i < list.Count; i++)
                            {
                                var a = list[i];
                                if (a == null || !a.isAlive()) continue;
                                for (int j = i + 1; j < list.Count; j++)
                                {
                                    var b = list[j];
                                    if (b == null || !b.isAlive()) continue;
                                    if (ZhuluDiplomacyService.IsTruceActive(a.id.ToString(), b.id.ToString(), year)) _truceCount++;
                                }
                            }
                        }
                    }
                    truceCount = _truceCount;
                }
                catch { }
                _statsText.text = $"当世交兵 <color=#e0554a>{_sortedWars.Count}处</color> | 和约 <color=#6ca3fd>{truceCount}</color> | " +
                    $"<color={UI.ZhuluUIStyle.Hex.Imperial}>☀问鼎:{mandate}</color> <color=#4488FF>附庸:{vassal}</color> <color=#FF8800>宣称:{claim}</color> " +
                    $"<color=#AA44FF>独立:{indep}</color> <color=#FF4444>全面:{total}</color> <color=#FF8A7A>叛乱:{rebel}</color> 其他:{other}";
            }

            _lastRefreshTime = Time.time;
            _warList.SetItems(_sortedWars);
        }

        // 阶段3H-16性能：和约统计按年缓存
        private int _truceCountYear = -1;
        private int _truceCount;

        private bool PassFilter(War war)
        {
            string t = GetWarTypeId(war);
            switch (_currentFilter)
            {
                case 1: return t == "zlr_vassal_war";
                case 2: return t == "zlr_claim_war";
                case 3: return t == "zlr_independence_war";
                case 4: return t == "zlr_total_war"; // 阶段2B 包3：全面战争
                // 阶段2N W13 修复：原 case5 匹配"四大类之外"（叛乱+其他混杂）、case6 落入 default 显示全部——选项与逻辑错位
                case 5: return t == "rebellion" || t == "inspire"; // 叛乱（城主兵变/叛军）
                case 6: return t != "zlr_vassal_war" && t != "zlr_claim_war" && t != "zlr_independence_war"
                    && t != "zlr_total_war" && t != "rebellion" && t != "inspire" && t != ZhuluWarService.MANDATE_WAR_ID; // 其他（spite 等原版类型）
                case 7: return t == ZhuluWarService.MANDATE_WAR_ID; // 夺天命
                default: return true;
            }
        }

        private void ApplySort()
        {
            switch (_currentSort)
            {
                case 1:
                    _sortedWars.Sort((a, b) => GetDuration(b).CompareTo(GetDuration(a)));
                    break;
                case 2:
                    _sortedWars.Sort((a, b) => GetTypeOrder(GetWarTypeId(a)).CompareTo(GetTypeOrder(GetWarTypeId(b))));
                    break;
                default:
                    _sortedWars.Sort((a, b) =>
                    {
                        // 2026-08-20 问鼎置顶：夺天命之战在兵力排序下也排最前
                        bool am = GetWarTypeId(a) == ZhuluWarService.MANDATE_WAR_ID;
                        bool bm = GetWarTypeId(b) == ZhuluWarService.MANDATE_WAR_ID;
                        if (am != bm) return am ? -1 : 1;
                        return GetTotalArmy(b).CompareTo(GetTotalArmy(a));
                    });
                    break;
            }
        }

        private static int GetTypeOrder(string typeId)
        {
            if (typeId == ZhuluWarService.MANDATE_WAR_ID) return -1; // 问鼎置顶
            if (typeId == "zlr_vassal_war") return 0;
            if (typeId == "zlr_claim_war") return 1;
            if (typeId == "zlr_independence_war") return 2;
            return 3;
        }

        private static int GetDuration(War war)
        {
            try { return (int)war.getDuration(); } catch { return 0; }
        }

        private static int GetTotalArmy(War war)
        {
            try
            {
                Kingdom a = war.getMainAttacker();
                Kingdom d = war.getMainDefender();
                return (a != null ? a.countTotalWarriors() : 0) + (d != null ? d.countTotalWarriors() : 0);
            }
            catch { return 0; }
        }
        #endregion

        #region 战争详情页（2026-08-19 串联·方案2.1：分数构成/厌战/求和意愿/结算预测——纯读档不改逻辑）

        /// <summary>视图刷新总入口：详情模式建详情、列表模式刷列表（OnEnable/Show/行点击共用）。</summary>
        private void RefreshView()
        {
            try
            {
                if (_detailWar != null)
                {
                    SetListControlsActive(false);
                    DestroyDetailView();
                    BuildDetailView();
                }
                else
                {
                    SetListControlsActive(true);
                    DestroyDetailView();
                    RefreshList();
                }
            }
            catch (Exception ex) { ZhuluContext.Instance.LogWarning($"[WarsWindow] 视图切换异常: {ex.Message}"); }
        }

        /// <summary>列表控件显隐（详情模式隐藏统计条/筛选栏/表头/底部按钮/列表滚动区）。</summary>
        private void SetListControlsActive(bool active)
        {
            if (_listStatsBar != null) _listStatsBar.SetActive(active);
            if (_listCtrlBar != null) _listCtrlBar.SetActive(active);
            if (_listTableHeader != null) _listTableHeader.SetActive(active);
            if (_listFooter != null) _listFooter.SetActive(active);
            if (_scrollViewRect != null) _scrollViewRect.gameObject.SetActive(active);
        }

        /// <summary>销毁详情视图（返回列表/结构重建时调用）。</summary>
        private void DestroyDetailView()
        {
            if (_detailRoot != null) { try { Destroy(_detailRoot); } catch { } }
            _detailRoot = null;
            _detailContentRect = null;
            _detailCards.Clear();
        }

        /// <summary>构建详情视图：头部/分数构成/厌战/求和意愿/结算预测/夺天命特殊卡。</summary>
        private void BuildDetailView()
        {
            War war = _detailWar;
            if (war == null) return;
            Kingdom atk = null, def = null;
            try { atk = war.getMainAttacker(); def = war.getMainDefender(); } catch { }
            if (atk == null || def == null || !atk.isAlive() || !def.isAlive())
            {
                _detailWar = null;   // 战争已结束/数据失效——退回列表
                SetListControlsActive(true);
                RefreshList();
                return;
            }

            // ── 滚动容器（RectMask2D 透明遮罩 + VLG/CSF 自动排布）──
            GameObject scrollObj = new GameObject("DetailView", typeof(RectTransform), typeof(ScrollRect), typeof(RectMask2D), typeof(Image));
            scrollObj.transform.SetParent(_root, false);
            _detailRoot = scrollObj;
            var svRect = scrollObj.GetComponent<RectTransform>();
            svRect.anchorMin = new Vector2(0, 0);
            svRect.anchorMax = new Vector2(1, 1);
            svRect.offsetMin = new Vector2(ScrollSideInset, ScrollBottomInset);
            svRect.offsetMax = new Vector2(-ScrollSideInset, -ScrollTopInset);
            scrollObj.GetComponent<Image>().color = new Color(0, 0, 0, 0.25f);
            var sv = scrollObj.GetComponent<ScrollRect>();
            sv.horizontal = false;
            sv.vertical = true;
            sv.movementType = ScrollRect.MovementType.Clamped;
            sv.scrollSensitivity = 24f;

            GameObject viewportObj = new GameObject("Viewport", typeof(RectTransform));
            viewportObj.transform.SetParent(scrollObj.transform, false);
            var vpRect = viewportObj.GetComponent<RectTransform>();
            vpRect.anchorMin = Vector2.zero; vpRect.anchorMax = Vector2.one;
            vpRect.offsetMin = Vector2.zero; vpRect.offsetMax = Vector2.zero;

            GameObject contentObj = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            contentObj.transform.SetParent(viewportObj.transform, false);
            _detailContentRect = contentObj.GetComponent<RectTransform>();
            _detailContentRect.anchorMin = new Vector2(0.5f, 1f);
            _detailContentRect.anchorMax = new Vector2(0.5f, 1f);
            _detailContentRect.pivot = new Vector2(0.5f, 1f);
            _detailContentRect.anchoredPosition = Vector2.zero;
            _detailContentRect.sizeDelta = new Vector2(_rowWidth, 0f);
            contentObj.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var vlg = contentObj.GetComponent<VerticalLayoutGroup>();
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.spacing = 4f;
            vlg.padding = new RectOffset(0, 0, 4, 4);
            vlg.childControlWidth = false;
            vlg.childControlHeight = false;
            vlg.childForceExpandWidth = false;
            vlg.childForceExpandHeight = false;
            sv.viewport = vpRect;
            sv.content = _detailContentRect;

            // ── 卡0：返回按钮 + 头部（攻守/类型/年限）──
            var headCard = CreateDetailCard(22f);
            var backBtn = new GameObject("BackBtn", typeof(RectTransform), typeof(Image), typeof(Button));
            backBtn.transform.SetParent(headCard.transform, false);
            var bbRect = backBtn.GetComponent<RectTransform>();
            bbRect.anchorMin = new Vector2(0, 0.5f); bbRect.anchorMax = new Vector2(0, 0.5f);
            bbRect.pivot = new Vector2(0, 0.5f);
            bbRect.anchoredPosition = new Vector2(6, 0);
            bbRect.sizeDelta = new Vector2(60, 16);
            backBtn.GetComponent<Image>().color = new Color(0.18f, 0.2f, 0.28f, 0.95f);
            var backTxt = CreateLabel(backBtn.transform, "Bk", "← 返回列表", 8, TextAnchor.MiddleCenter, new Color(0.85f, 0.86f, 0.94f));
            StretchRect(backTxt.GetComponent<RectTransform>());
            backBtn.GetComponent<Button>().onClick.AddListener(() => { _detailWar = null; RefreshView(); });

            // 2026-08-20 原版战报跳转：设置 SelectedMetas.selected_war 并打开原版 war 窗口（同 MetaTypeLibrary.stat_click 路径）
            var vanillaBtn = new GameObject("VanillaBtn", typeof(RectTransform), typeof(Image), typeof(Button));
            vanillaBtn.transform.SetParent(headCard.transform, false);
            var vbRect = vanillaBtn.GetComponent<RectTransform>();
            vbRect.anchorMin = new Vector2(1, 0.5f); vbRect.anchorMax = new Vector2(1, 0.5f);
            vbRect.pivot = new Vector2(1, 0.5f);
            vbRect.anchoredPosition = new Vector2(-6, 0);
            vbRect.sizeDelta = new Vector2(66, 16);
            vanillaBtn.GetComponent<Image>().color = new Color(0.28f, 0.24f, 0.1f, 0.95f);
            var vTxt = CreateLabel(vanillaBtn.transform, "Vb", "交战详情 →", 8, TextAnchor.MiddleCenter, new Color(1f, 0.93f, 0.55f));
            StretchRect(vTxt.GetComponent<RectTransform>());
            War warForJump = war; // 闭包捕获
            vanillaBtn.GetComponent<Button>().onClick.AddListener(() =>
            {
                try
                {
                    if (warForJump == null || warForJump.isRekt()) return;
                    SelectedMetas.selected_war = warForJump;
                    ScrollWindow.showWindow("war");
                }
                catch (Exception ex) { ZhuluContext.Instance.LogWarning($"[WarsWindow] 原版战报跳转失败: {ex.Message}"); }
            });

            string detailTypeId = GetWarTypeId(war);
            string typeName = GetWarTypeName(detailTypeId);
            int years = 0;
            try { years = Mathf.Max(0, (int)war.getDuration()); } catch { }
            var headTxt = CreateLabel(headCard.transform, "Head",
                $"<color=#ff8a7a>{atk.name}</color> <color=#8a8578>→</color> <color=#7ab8e8>{def.name}</color>  " +
                $"<color={UI.ZhuluUIStyle.Hex.Warn}>{typeName}</color> <color=#8a8578>· 已战{years}年</color>", 10, TextAnchor.MiddleLeft, ColorTextNormal);
            LayoutAt(headTxt, 72, 4, _rowWidth - 148, 14); // 2026-08-20：右侧让位"交战详情"按钮

            // ── 数据读取 ──
            int score = 0;
            int.TryParse(ClaimManager.GetWarCustomStr(war, ZhuluWarService.WAR_SCORE) ?? "", out score);
            string detail = ClaimManager.GetWarCustomStr(war, ZhuluWarService.WAR_SCORE_DETAIL) ?? "";
            int dCity = 0, dLost = 0, dKill = 0, dCap = 0;
            bool hasDetail = false;
            try
            {
                var parts = detail.Split('|');
                if (parts.Length >= 4)
                {
                    hasDetail = int.TryParse(parts[0], out dCity) && int.TryParse(parts[1], out dLost)
                        && int.TryParse(parts[2], out dKill) && int.TryParse(parts[3], out dCap);
                }
            }
            catch { }

            // ── 卡1：分数构成（四段横条——占城/失城/击杀/首都）──
            var scoreCard = CreateDetailCard(hasDetail ? 96f : 44f);
            var scoreTitle = CreateLabel(scoreCard.transform, "T", $"<b>战争分数</b>  <color={(score >= 0 ? UI.ZhuluUIStyle.Hex.Positive : UI.ZhuluUIStyle.Hex.Negative)}>{score}</color> <color=#8a8578>（正=攻方占优）</color>", 9, TextAnchor.MiddleLeft, ColorTextNormal);
            LayoutAt(scoreTitle, 8, 22, _rowWidth - 16, 14);
            if (hasDetail)
            {
                CreateScoreRow(scoreCard, 40, "占城", dCity);
                CreateScoreRow(scoreCard, 58, "失城", dLost);
                CreateScoreRow(scoreCard, 76, "击杀", dKill);
                CreateScoreRow(scoreCard, 94, "首都", dCap);
            }
            else
            {
                var hint = CreateLabel(scoreCard.transform, "H", "<color=#8a8578>逐年构成未采集（新战争开战后自动记录）</color>", 8, TextAnchor.MiddleLeft, ColorTextGray);
                LayoutAt(hint, 8, 40, _rowWidth - 16, 12);
            }

            // ── 卡2：双方厌战（0-100 水平条）──
            int exA = 0, exD = 0;
            try { exA = ZhuluWarService.GetWarExhaustion(atk.id.ToString()); } catch { }
            try { exD = ZhuluWarService.GetWarExhaustion(def.id.ToString()); } catch { }
            var warExCard = CreateDetailCard(64f);
            var exTitle = CreateLabel(warExCard.transform, "T", "<b>双方厌战</b>", 9, TextAnchor.MiddleLeft, ColorTextNormal);
            LayoutAt(exTitle, 8, 22, _rowWidth - 16, 14);
            CreateExhaustionRow(warExCard, 40, atk.name, exA);
            CreateExhaustionRow(warExCard, 52, def.name, exD);

            // ── 卡3：求和意愿（特殊战争有更高门槛）──
            int pwA = 0, pwD = 0;
            try { pwA = ZhuluWarService.GetPeaceWillingness(atk, def, war, -score, ZhuluTime.CurrentYear); } catch { }
            try { pwD = ZhuluWarService.GetPeaceWillingness(def, atk, war, score, ZhuluTime.CurrentYear); } catch { }
            var peaceCard = CreateDetailCard(64f);
            string peaceHint = detailTypeId == ZhuluWarService.MANDATE_WAR_ID
                ? $"（{(IsNomadicMandateWar(war) ? "争夺天眷" : "问鼎")}议和：七年后双方高厌战且僵持）"
                : detailTypeId == "rebellion" || detailTypeId == "inspire"
                    ? "（叛乱议和：通常需十二年以上且俱疲）"
                    : "（≥60 时可能主动求和）";
            var pTitle = CreateLabel(peaceCard.transform, "T", $"<b>求和意愿</b>  <color=#8a8578>{peaceHint}</color>", 9, TextAnchor.MiddleLeft, ColorTextNormal);
            LayoutAt(pTitle, 8, 22, _rowWidth - 16, 14);
            CreateWillingnessRow(peaceCard, 40, atk.name, pwA);
            CreateWillingnessRow(peaceCard, 52, def.name, pwD);

            // ── 卡4：结算预测（纯文本推演——绝不调用真实结算）──
            string forecast;
            if (detailTypeId == ZhuluWarService.MANDATE_WAR_ID)
                forecast = score >= 30 ? $"攻方占优，仍需据持有国首都三年方可夺取{(IsNomadicMandateWar(war) ? "长生天眷" : "天命")}" 
                    : score <= -30 ? "挑战者局势不利，可能撤军认输或超时失败"
                    : $"{(IsNomadicMandateWar(war) ? "争夺天眷" : "问鼎")}僵持；七年后双方高厌战且军力枯竭方可议和";
            else if (detailTypeId == "rebellion" || detailTypeId == "inspire")
                forecast = "叛乱不轻和；若十余年俱疲而罢战，叛军将获承认并转为正式国家";
            else if (!detailTypeId.StartsWith("zlr_"))
                forecast = "通用战争：大败可认输，长期双方厌战可议和；逐鹿不额外强制割地";
            else if (score >= 60) forecast = "攻方大胜态势：若今岁停战，守方将割让城池并赔款";
            else if (score >= 30) forecast = "攻方占优：可望割城止战";
            else if (score >= -29) forecast = "相持不下：大概率白和";
            else if (score >= -59) forecast = "守方占优：攻方恐需赔款脱身";
            else forecast = "攻方溃败态势：割地赔款在所难免";
            var fcCard = CreateDetailCard(46f);
            var fcTitle = CreateLabel(fcCard.transform, "T", "<b>结算预测</b>  <color=#8a8578>（按当前分数推演）</color>", 9, TextAnchor.MiddleLeft, ColorTextNormal);
            LayoutAt(fcTitle, 8, 22, _rowWidth - 16, 14);
            var fcText = CreateLabel(fcCard.transform, "F", $"<color={UI.ZhuluUIStyle.Hex.Warn}>{forecast}</color>", 9, TextAnchor.MiddleLeft, ColorTextNormal);
            LayoutAt(fcText, 8, 36, _rowWidth - 16, 12);

            // ── 卡5：夺天命战特殊说明（仅夺天命战显示）──
            if (detailTypeId == ZhuluWarService.MANDATE_WAR_ID)
            {
                bool nomadicMandateWar = IsNomadicMandateWar(war);
                var mwCard = CreateDetailCard(66f);
                var mwTitle = CreateLabel(mwCard.transform, "T", $"<b><color={UI.ZhuluUIStyle.Hex.Imperial}>☀ {(nomadicMandateWar ? "夺长生天眷之战" : "夺天命之战")}</color></b>", 9, TextAnchor.MiddleLeft, ColorTextNormal);
                LayoutAt(mwTitle, 8, 22, _rowWidth - 16, 14);
                var mwText = CreateLabel(mwCard.transform, "F",
                    $"<color=#ffd8a0>据守方首都满3年→{(nomadicMandateWar ? "天眷" : "天命")}易主；高厌战僵局可议和；超过12年无功→挑战失败且冷却20年</color>", 8, TextAnchor.MiddleLeft, ColorTextGray);
                mwText.verticalOverflow = VerticalWrapMode.Overflow;
                LayoutAt(mwText, 8, 36, _rowWidth - 16, 28);
            }
        }

        /// <summary>详情卡容器（统一深底 + 无标题——标题由调用方自建）。</summary>
        private GameObject CreateDetailCard(float height)
        {
            var card = new GameObject("DetailCard", typeof(RectTransform));
            card.transform.SetParent(_detailContentRect, false);
            _detailCards.Add(card);
            var rt = card.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(_rowWidth, height);
            var bg = new GameObject("Bg", typeof(RectTransform), typeof(Image));
            bg.transform.SetParent(card.transform, false);
            var bgRect = bg.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero; bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = Vector2.zero; bgRect.offsetMax = Vector2.zero;
            var bgImg = bg.GetComponent<Image>();
            bgImg.color = new Color(0.13f, 0.13f, 0.17f, 0.95f);
            bgImg.raycastTarget = false;
            return card;
        }

        /// <summary>分数构成行：标签 + 归一化水平条（正绿/负红）+ 数值。</summary>
        private void CreateScoreRow(GameObject card, float y, string label, int value)
        {
            var lab = CreateLabel(card.transform, "L", label, 8, TextAnchor.MiddleLeft, ColorTextGray);
            LayoutAt(lab, 8, y, 30, 12);
            float barX = 44, barW = Mathf.Max(60f, _rowWidth - 130);
            var barBg = new GameObject("BarBg", typeof(RectTransform), typeof(Image));
            barBg.transform.SetParent(card.transform, false);
            var bgRect = barBg.GetComponent<RectTransform>();
            LayoutAtRect(bgRect, barX, y + 2, barW, 8);
            barBg.GetComponent<Image>().color = new Color(0.08f, 0.08f, 0.1f, 0.95f);
            var fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            fill.transform.SetParent(barBg.transform, false);
            var fillRect = fill.GetComponent<RectTransform>();
            fillRect.anchorMin = new Vector2(0, 0); fillRect.anchorMax = new Vector2(0, 1);
            fillRect.pivot = new Vector2(0, 0.5f);
            fillRect.anchoredPosition = Vector2.zero;
            fillRect.sizeDelta = new Vector2(barW * Mathf.Clamp01(Mathf.Abs(value) / 100f), 0);
            var fillImg = fill.GetComponent<Image>();
            fillImg.color = value >= 0 ? new Color(0.52f, 0.95f, 0.41f) : new Color(0.97f, 0.22f, 0.22f);
            fillImg.raycastTarget = false;
            var val = CreateLabel(card.transform, "V", $"{value:+0;-0;0}", 9, TextAnchor.MiddleLeft, value >= 0 ? new Color(0.52f, 0.95f, 0.41f) : new Color(0.97f, 0.22f, 0.22f));
            LayoutAt(val, barX + barW + 6, y, 40, 12);
        }

        /// <summary>厌战行：国名 + 0-100 条（≥60 红）+ 数值。</summary>
        private void CreateExhaustionRow(GameObject card, float y, string name, int value)
        {
            var lab = CreateLabel(card.transform, "L", name, 8, TextAnchor.MiddleLeft, ColorTextGray);
            LayoutAt(lab, 8, y, 60, 12);
            float barX = 76, barW = Mathf.Max(60f, _rowWidth - 160);
            var barBg = new GameObject("BarBg", typeof(RectTransform), typeof(Image));
            barBg.transform.SetParent(card.transform, false);
            LayoutAtRect(barBg.GetComponent<RectTransform>(), barX, y + 2, barW, 8);
            barBg.GetComponent<Image>().color = new Color(0.08f, 0.08f, 0.1f, 0.95f);
            var fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            fill.transform.SetParent(barBg.transform, false);
            var fillRect = fill.GetComponent<RectTransform>();
            fillRect.anchorMin = new Vector2(0, 0); fillRect.anchorMax = new Vector2(0, 1);
            fillRect.pivot = new Vector2(0, 0.5f);
            fillRect.anchoredPosition = Vector2.zero;
            fillRect.sizeDelta = new Vector2(barW * Mathf.Clamp01(value / 100f), 0);
            var fillImg = fill.GetComponent<Image>();
            fillImg.color = value >= 60 ? new Color(1f, 0.4f, 0.3f) : new Color(0.9f, 0.72f, 0.35f);
            fillImg.raycastTarget = false;
            string warn = value >= 60 ? " <color=#ff8a7a>⚠厌战高涨</color>" : "";
            var val = CreateLabel(card.transform, "V", $"{value}{warn}", 9, TextAnchor.MiddleLeft, ColorTextNormal);
            LayoutAt(val, barX + barW + 6, y, 70, 12);
        }

        /// <summary>求和意愿行：国名 + 0-100 条 + 数值。</summary>
        private void CreateWillingnessRow(GameObject card, float y, string name, int value)
        {
            var lab = CreateLabel(card.transform, "L", name, 8, TextAnchor.MiddleLeft, ColorTextGray);
            LayoutAt(lab, 8, y, 60, 12);
            float barX = 76, barW = Mathf.Max(60f, _rowWidth - 130);
            var barBg = new GameObject("BarBg", typeof(RectTransform), typeof(Image));
            barBg.transform.SetParent(card.transform, false);
            LayoutAtRect(barBg.GetComponent<RectTransform>(), barX, y + 2, barW, 8);
            barBg.GetComponent<Image>().color = new Color(0.08f, 0.08f, 0.1f, 0.95f);
            var fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            fill.transform.SetParent(barBg.transform, false);
            var fillRect = fill.GetComponent<RectTransform>();
            fillRect.anchorMin = new Vector2(0, 0); fillRect.anchorMax = new Vector2(0, 1);
            fillRect.pivot = new Vector2(0, 0.5f);
            fillRect.anchoredPosition = Vector2.zero;
            fillRect.sizeDelta = new Vector2(barW * Mathf.Clamp01(value / 100f), 0);
            var fillImg = fill.GetComponent<Image>();
            fillImg.color = value >= 60 ? new Color(0.52f, 0.95f, 0.41f) : new Color(0.42f, 0.66f, 0.9f);
            fillImg.raycastTarget = false;
            var val = CreateLabel(card.transform, "V", $"{value}/100", 9, TextAnchor.MiddleLeft, ColorTextNormal);
            LayoutAt(val, barX + barW + 6, y, 46, 12);
        }

        /// <summary>文本绝对定位（卡片内左上原点系）。</summary>
        private static void LayoutAt(Text t, float x, float y, float w, float h)
        {
            LayoutAtRect(t.GetComponent<RectTransform>(), x, y, w, h);
        }

        /// <summary>Rect 绝对定位（卡片内左上原点系）。</summary>
        private static void LayoutAtRect(RectTransform r, float x, float y, float w, float h)
        {
            r.anchorMin = new Vector2(0, 1);
            r.anchorMax = new Vector2(0, 1);
            r.pivot = new Vector2(0, 1);
            r.anchoredPosition = new Vector2(x, -y);
            r.sizeDelta = new Vector2(w, h);
        }

        /// <summary>Rect 拉满父级。</summary>
        private static void StretchRect(RectTransform r)
        {
            r.anchorMin = Vector2.zero;
            r.anchorMax = Vector2.one;
            r.sizeDelta = Vector2.zero;
            r.offsetMin = Vector2.zero;
            r.offsetMax = Vector2.zero;
        }

        /// <summary>战争类型中文名（详情页头部）。</summary>
        private static string GetWarTypeName(string typeId)
        {
            switch (typeId)
            {
                case "zlr_claim_war": return "宣称战争";
                case "zlr_vassal_war": return "附庸战争";
                case "zlr_independence_war": return "独立战争";
                case "zlr_total_war": return "全面战争";
                case "zlr_mandate_war": return "争夺天命/天眷";
                case "rebellion": case "inspire": return "叛乱";
                default: return "刀兵之争";
            }
        }

        private static bool IsNomadicMandateWar(War war)
        {
            try
            {
                Kingdom attacker = war?.getMainAttacker();
                return attacker != null && KingdomCivilizationService.Instance.IsNomadic(attacker);
            }
            catch { return false; }
        }
        #endregion

        #region 虚拟滚动（批次9b：核心收编至 ZhuluVirtualList<War>，此处仅保留调用）
        #endregion

        #region 行预制体
        private GameObject BuildRowPrefab()
        {
            float w = _rowWidth;
            GameObject rowObj = new GameObject("WarRow", typeof(RectTransform), typeof(Image));
            var rect = rowObj.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(w, RowHeight);
            rowObj.GetComponent<Image>().color = ColorRowEven;

            // 类型标签
            GameObject tagObj = new GameObject("TypeTag", typeof(RectTransform), typeof(Image));
            tagObj.transform.SetParent(rowObj.transform, false);
            var tagRect = tagObj.GetComponent<RectTransform>();
            tagRect.anchorMin = new Vector2(0, 0.5f);
            tagRect.anchorMax = new Vector2(0, 0.5f);
            tagRect.pivot = new Vector2(0, 0.5f);
            tagRect.anchoredPosition = new Vector2(4, 0);
            tagRect.sizeDelta = new Vector2(42, 15);
            var tagImg = tagObj.GetComponent<Image>();
            tagImg.type = Image.Type.Sliced;
            var tagText = CreateLabel(tagObj.transform, "Text", "", 8, TextAnchor.MiddleCenter, Color.white);
            var ttRect = tagText.GetComponent<RectTransform>();
            ttRect.anchorMin = Vector2.zero;
            ttRect.anchorMax = Vector2.one;
            ttRect.offsetMin = Vector2.zero;
            ttRect.offsetMax = Vector2.zero;
            tagText.raycastTarget = false;

            // 交战双方（左起 54，占剩余）
            float warW = w - ColType - ColDur - ColArmy - ColProg;
            var warText = CreateLabel(rowObj.transform, "WarText", "", 9, TextAnchor.MiddleLeft, ColorTextNormal);
            var wRect = warText.GetComponent<RectTransform>();
            wRect.anchorMin = new Vector2(0, 0.5f);
            wRect.anchorMax = new Vector2(0, 0.5f);
            wRect.pivot = new Vector2(0, 0.5f);
            wRect.anchoredPosition = new Vector2(ColType, 0);
            wRect.sizeDelta = new Vector2(warW, 18);

            // 持续
            var durText = CreateLabel(rowObj.transform, "DurText", "", 8, TextAnchor.MiddleCenter, ColorTextGray);
            var dRect = durText.GetComponent<RectTransform>();
            dRect.anchorMin = new Vector2(0, 0.5f);
            dRect.anchorMax = new Vector2(0, 0.5f);
            dRect.pivot = new Vector2(0, 0.5f);
            dRect.anchoredPosition = new Vector2(ColType + warW, 0);
            dRect.sizeDelta = new Vector2(ColDur, 18);

            // 兵力
            var armyText = CreateLabel(rowObj.transform, "ArmyText", "", 8, TextAnchor.MiddleCenter, ColorArmy);
            var aRect = armyText.GetComponent<RectTransform>();
            aRect.anchorMin = new Vector2(0, 0.5f);
            aRect.anchorMax = new Vector2(0, 0.5f);
            aRect.pivot = new Vector2(0, 0.5f);
            aRect.anchoredPosition = new Vector2(ColType + warW + ColDur, 0);
            aRect.sizeDelta = new Vector2(ColArmy, 18);

            // 进度条容器
            GameObject progObj = new GameObject("Progress", typeof(RectTransform));
            progObj.transform.SetParent(rowObj.transform, false);
            var pRect = progObj.GetComponent<RectTransform>();
            pRect.anchorMin = new Vector2(0, 0.5f);
            pRect.anchorMax = new Vector2(0, 0.5f);
            pRect.pivot = new Vector2(0, 0.5f);
            pRect.anchoredPosition = new Vector2(ColType + warW + ColDur + ColArmy, 0);
            pRect.sizeDelta = new Vector2(ColProg, 18);

            // 条底
            GameObject barBg = new GameObject("BarBg", typeof(RectTransform), typeof(Image));
            barBg.transform.SetParent(progObj.transform, false);
            var bRect = barBg.GetComponent<RectTransform>();
            bRect.anchorMin = new Vector2(0, 0.5f);
            bRect.anchorMax = new Vector2(1, 0.5f);
            bRect.pivot = new Vector2(0, 0.5f);
            bRect.anchoredPosition = new Vector2(0, 2);
            bRect.sizeDelta = new Vector2(-18, 5);
            barBg.GetComponent<Image>().color = ColorBarBg;

            // 条填充
            GameObject barFill = new GameObject("BarFill", typeof(RectTransform), typeof(Image));
            barFill.transform.SetParent(barBg.transform, false);
            var fRect = barFill.GetComponent<RectTransform>();
            fRect.anchorMin = new Vector2(0, 0.5f);
            fRect.anchorMax = new Vector2(0, 0.5f);
            fRect.pivot = new Vector2(0, 0.5f);
            fRect.anchoredPosition = Vector2.zero;
            fRect.sizeDelta = new Vector2(0, 5);
            barFill.GetComponent<Image>().color = ColorProgressClaim;

            // 数字
            var numText = CreateLabel(progObj.transform, "NumText", "", 8, TextAnchor.MiddleRight, ColorTextGray);
            var nRect = numText.GetComponent<RectTransform>();
            nRect.anchorMin = new Vector2(1, 0.5f);
            nRect.anchorMax = new Vector2(1, 0.5f);
            nRect.pivot = new Vector2(1, 0.5f);
            nRect.anchoredPosition = new Vector2(0, 0);
            nRect.sizeDelta = new Vector2(18, 18);

            rowObj.SetActive(false);
            return rowObj;
        }

        private void SetupRow(GameObject rowObj, War war, int index)
        {
            string typeId = GetWarTypeId(war);
            Image bg = rowObj.GetComponent<Image>();
            bool isHot = (_currentSort == 0 && index == 0 && _sortedWars.Count > 0);
            if (isHot) bg.color = ColorRowHot;
            else bg.color = (index % 2 == 0) ? ColorRowEven : ColorRowOdd;

            bool isMandate = typeId == ZhuluWarService.MANDATE_WAR_ID;
            // 2026-08-20 问鼎显眼化：夺天命之战行底金色微光压过其他所有行（含"最激烈"高亮）
            if (isMandate) bg.color = new Color(0.32f, 0.25f, 0.06f, 0.85f);

            var tagObj = rowObj.transform.Find("TypeTag");
            var tagImg = tagObj.GetComponent<Image>();
            var tagText = tagObj.Find("Text").GetComponent<Text>();
            string typeName = WarfareManager.GetWarTypeName(typeId);
            Color tagColor;
            if (isMandate) { tagColor = new Color(0.78f, 0.6f, 0.08f, 1f); tagText.color = new Color(1f, 0.93f, 0.55f); } // 问鼎：金底亮字
            else if (typeId == "zlr_vassal_war") { tagColor = new Color(0.09f, 0.19f, 0.31f, 1f); tagText.color = ColorProgressVassal; }
            else if (typeId == "zlr_claim_war") { tagColor = new Color(0.29f, 0.17f, 0.04f, 1f); tagText.color = ColorProgressClaim; }
            else if (typeId == "zlr_independence_war") { tagColor = new Color(0.2f, 0.1f, 0.29f, 1f); tagText.color = ColorProgressIndep; }
            else if (typeId == "zlr_total_war") { tagColor = new Color(0.45f, 0.1f, 0.1f, 1f); tagText.color = ColorProgressTotal; } // 阶段2B 包3
            else if (typeId == "inspire") { tagColor = new Color(0.35f, 0.12f, 0.12f, 1f); tagText.color = new Color(1f, 0.54f, 0.44f); } // 阶段2K：叛乱（城主兵变）
            else { tagColor = new Color(0.2f, 0.2f, 0.2f, 1f); tagText.color = ColorTextGray; }
            tagImg.color = tagColor;
            tagText.text = isMandate ? (IsNomadicMandateWar(war) ? "夺天眷" : "夺天命") : typeName;

            Kingdom attacker = null, defender = null;
            try { attacker = war.getMainAttacker(); defender = war.getMainDefender(); } catch { }
            string aName = attacker != null ? attacker.name : "未知";
            string dName = defender != null ? defender.name : "未知";
            // 阶段2K：和约期徽章（停战中的双方——🕊）
            string truceMark = "";
            try
            {
                int year = ZhuluTime.CurrentYear;
                if (attacker != null && defender != null)
                {
                    bool truce = ZhuluDiplomacyService.IsTruceActive(attacker.id.ToString(), defender.id.ToString(), year)
                        || ZhuluDiplomacyService.IsNonAggressionActive(attacker.id.ToString(), defender.id.ToString(), year);
                    if (truce) truceMark = " <size=8><color=#6ca3fd>🕊和约期</color></size>";
                }
            }
            catch { }
            Text warText = rowObj.transform.Find("WarText").GetComponent<Text>();
            string mandateMark = isMandate ? "<color=#FFD700>☀问鼎</color> " : ""; // 问鼎战名前置金标
            warText.text = $"{mandateMark}{aName} <color=#666666>vs</color> {dName}{truceMark}";
            warText.horizontalOverflow = HorizontalWrapMode.Wrap;
            warText.verticalOverflow = VerticalWrapMode.Truncate;
            // 阶段2K：战争目标小字（为何而战）
            try
            {
                string goal = GetWarGoalText(war, typeId);
                if (!string.IsNullOrEmpty(goal))
                    warText.text += $"\n<size=7><color=#5a5f6e>{goal}</color></size>";
            }
            catch { }

            rowObj.transform.Find("DurText").GetComponent<Text>().text = $"{GetDuration(war)}年";

            // 阶段2K：兵力双色（攻蓝守红）
            int aArmy = attacker != null ? attacker.countTotalWarriors() : 0;
            int dArmy = defender != null ? defender.countTotalWarriors() : 0;
            rowObj.transform.Find("ArmyText").GetComponent<Text>().text =
                $"<color=#6ca3fd>{aArmy}</color>:<color=#ff6a5a>{dArmy}</color>";

            SetupProgress(rowObj, war, typeId);
            // 阶段2K：厌战徽章（进度列尾——≥60 红标）
            try
            {
                if (attacker != null)
                {
                    int ex = ZhuluWarService.GetWarExhaustion(attacker.id.ToString());
                    if (ex >= 60)
                    {
                        var progText = rowObj.transform.Find("ProgText");
                        if (progText != null)
                        {
                            Text pt = progText.GetComponent<Text>();
                            pt.text += " <size=7><color=#e0554a>厌战</color></size>";
                        }
                    }
                }
            }
            catch { }

            // 2026-08-19 详情入口：整行可点 → 进入该战争详情页（Button 无则补加，有则复用）
            Button rowBtn = rowObj.GetComponent<Button>();
            if (rowBtn == null) rowBtn = rowObj.AddComponent<Button>();
            rowBtn.targetGraphic = bg;
            rowBtn.onClick.RemoveAllListeners();
            War clickedWar = war; // 闭包捕获当前行战争
            rowBtn.onClick.AddListener(() => { _detailWar = clickedWar; RefreshView(); });
        }

        /// <summary>阶段2K：战争目标文本（宣称城/附庸对象/独立条件）。</summary>
        private static string GetWarGoalText(War war, string typeId)
        {
            try
            {
                if (typeId == "zlr_claim_war")
                {
                    string targetId = ClaimManager.GetWarCustomStr(war, ZhuluWarService.WAR_TARGET);
                    City tc = !string.IsNullOrEmpty(targetId) ? ClaimManager.FindCityById(targetId) : null;
                    return tc != null ? $"目标：{tc.name}（宣称之城）" : "";
                }
                if (typeId == "zlr_independence_war")
                    return "坚持五载可成 · 或破宗主首都";
                if (typeId == "zlr_vassal_war")
                    return "首都沦陷过半即臣服";
                if (typeId == "zlr_total_war")
                    return "倾国之战 · 不死不休";
                if (typeId == ZhuluWarService.MANDATE_WAR_ID)
                    return $"据其首都满{ZhuluWarService.MANDATE_WAR_HOLD_YEARS}年 → {(IsNomadicMandateWar(war) ? "天眷" : "天命")}易主";
                if (typeId == "inspire")
                    return "城主兵变 · 不服夺职";
                return "";
            }
            catch { return ""; }
        }

        private void SetupProgress(GameObject rowObj, War war, string typeId)
        {
            float progress = 0f;
            Color fillColor = ColorProgressNormal;
            string numText = "";

            try
            {
                if (typeId == "zlr_claim_war")
                {
                    string targetCityId = ClaimManager.GetWarCustomStr(war, "zlr_claim_city");
                    if (!string.IsNullOrEmpty(targetCityId))
                    {
                        City city = ClaimManager.FindCityById(targetCityId);
                        if (city != null)
                        {
                            Kingdom attacker = war.getMainAttacker();
                            if (attacker != null && city.isGettingCapturedBy(attacker))
                                progress = city.last_visual_capture_ticks;
                            fillColor = ColorProgressClaim;
                            numText = $"{Mathf.RoundToInt(progress)}/100";
                        }
                    }
                }
                else if (typeId == "zlr_vassal_war")
                {
                    Kingdom defender = war.getMainDefender();
                    Kingdom attacker = war.getMainAttacker();
                    if (defender?.capital != null && attacker != null)
                    {
                        if (defender.capital.isGettingCapturedBy(attacker))
                            progress = defender.capital.last_visual_capture_ticks;
                        progress = Mathf.Min(progress, 100f);
                        fillColor = ColorProgressVassal;
                        numText = $"{Mathf.RoundToInt(progress)}/100";
                    }
                }
                else if (typeId == "zlr_independence_war")
                {
                    string startYearStr = ClaimManager.GetWarCustomStr(war, "zlr_war_start_year");
                    if (!string.IsNullOrEmpty(startYearStr) && int.TryParse(startYearStr, out int startYear))
                    {
                        int elapsed = ZhuluTime.CurrentYear - startYear;
                        progress = Mathf.Clamp(elapsed * 20f, 0f, 100f);
                        fillColor = ColorProgressIndep;
                        numText = $"{elapsed}/5年";
                    }
                }
                else if (typeId == "zlr_total_war")
                {
                    // 阶段2B 包3：全面战争进度 = 战争分数映射（0-100：30=小胜/50=胜/75=大胜）
                    string scoreStr = ClaimManager.GetWarCustomStr(war, ZhuluWarService.WAR_SCORE);
                    int score = 0;
                    int.TryParse(scoreStr ?? "", out score);
                    progress = Mathf.Clamp(50f + score * 0.5f, 0f, 100f);
                    fillColor = ColorProgressTotal;
                    numText = $"势{score}";
                }
                else if (typeId == ZhuluWarService.MANDATE_WAR_ID)
                {
                    // 2026-08-20 问鼎进度：据天命国都年数 / 3（金色条）
                    string hsStr = ClaimManager.GetWarCustomStr(war, ZhuluWarService.WAR_MANDATE_HOLD_SINCE) ?? "0";
                    long.TryParse(hsStr, out long holdSince);
                    if (holdSince > 0)
                    {
                        int held = ZhuluTime.CurrentYear - (int)holdSince;
                        progress = Mathf.Clamp(held * 100f / ZhuluWarService.MANDATE_WAR_HOLD_YEARS, 0f, 100f);
                        numText = $"{Mathf.Max(0, held)}/{ZhuluWarService.MANDATE_WAR_HOLD_YEARS}年";
                    }
                    else numText = "未据都";
                    fillColor = new Color(1f, 0.84f, 0f); // 金
                }
            }
            catch { }

            var progObj = rowObj.transform.Find("Progress");
            var barBg = progObj.Find("BarBg");
            var fill = barBg.Find("BarFill").GetComponent<RectTransform>();
            float barWidth = barBg.GetComponent<RectTransform>().rect.width;
            fill.sizeDelta = new Vector2(barWidth * Mathf.Clamp01(progress / 100f), 5f);
            fill.GetComponent<Image>().color = fillColor;
            progObj.Find("NumText").GetComponent<Text>().text = numText;
        }

        private static string GetWarTypeId(War war)
        {
            string typeId = war.data?.war_type ?? "";
            if (string.IsNullOrEmpty(typeId))
            {
                var asset = war.getAsset();
                typeId = asset != null ? asset.id : "normal";
            }
            return typeId;
        }
        #endregion
    }
}
