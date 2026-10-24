using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using ZhuluRebuild.Core;
using ZhuluRebuild.Domain;
using ZhuluRebuild.Persistence;
using ZhuluRebuild.Presentation.Routing;

namespace ZhuluRebuild.UI.Windows
{
    /// <summary>
    /// 天命窗口 v2（分层①核心）：字段 / 生命周期 / 结构骨架 RebuildUI / 面板路由 / 共享工具。
    /// 结构构建在 ZhuluMandateWindow.Build.cs；数据文本与刷新在 ZhuluMandateWindow.Data.cs。
    /// 三文件为同一 partial 类——运行时行为不变，仅源码分层。
    /// 经验要点：隐藏原版 Scroll View 用 SetActive(false)（不能 Destroy，resetScroll 会崩）；
    /// 滚动容器用 RectMask2D（透明 Mask 会把内容全裁掉）；字体统一 current_font；文字防溢出。
    /// </summary>
    public partial class ZhuluMandateWindow : MonoBehaviour
    {
        public static ZhuluMandateWindow Instance { get; private set; }

        private const float WindowWidth = 350f;
        private const float WindowHeight = 400f;

        private Transform _bg;
        private Transform _contentTransform;
        private ScrollRect _contentScroll;
        private float _innerWidth = 334f;
        private bool _structureBuilt = false;
        private bool _showNomadic = false;

        // 主面板元素
        private GameObject _mainPanel;
        private Text _mainValueText;
        private Text _mainGradeText;
        private Image _mandateIcon;          // 天命玉佩图标
        private Image[] _barSegments;          // 5 段进度条
        private Text _mainInfoText;             // 信息行（富文本）
        private Text _mainEffectText;
        // 2026-08-23 清理：_mainEventText 已随阶段3G 删除近期天命块一并移除（原字段从未赋值致 CS0649）
        private Text _challengerText;           // 2026-08-20 问鼎者行（可与当前朝代争天命的强国）
        private GameObject _warnBar;            // 易主预警条
        private Text _warnText;
        private GameObject _historyPanel;
        private Transform _historyList;
        private Text _historyEmptyText;
        private Text _historyStatText;      // 传承统计条
        // 2026-08-19 串联：历代天命折叠状态（朝代 key → 用户是否展开；无记录用默认：前朝收起、当朝展开）
        private readonly Dictionary<string, bool> _dynastyState = new Dictionary<string, bool>();

        // ═══════════════════════ 生命周期 ═══════════════════════

        public void Initialize()
        {
            Instance = this;
            // 不在此构建：窗口创建时 Canvas 布局未就绪（rect.width 旧值），锚点会偏左。
            // 首次 OnEnable（窗口真正打开、布局就绪）时构建，之后只刷新数据。
        }

        public void Show()
        {
            if (World.world != null && gameObject.activeInHierarchy)
            {
                if (!_structureBuilt)
                {
                    try { RebuildUI(); } catch (Exception ex) { ZhuluContext.Instance.LogError("[MandateWindow] 构建异常", ex); }
                    _structureBuilt = true;
                }
                else
                {
                    RefreshAll();
                }
            }
        }

        private void RefreshAll()
        {
            if (_mainPanel == null) return;
            ShowMain();
        }

        /// <summary>窗口打开期间定时刷新数值（天命值/在位年/趋势/疆域/兵力实时跟随游戏）。
        /// 批次9a：暂停时跳过（P1-17——暂停中数值不变，重算是纯浪费）；跨年由事件即时触发。</summary>
        private float _refreshTimer = 0f;
        private void Update()
        {
            if (_mainPanel == null) return;
            try { if (Config.paused) return; } catch { }
            _refreshTimer += Time.deltaTime;
            if (_refreshTimer < 3f) return;   // 阶段3H-13性能：1秒→3秒（问鼎者行含全王国军力统计）
            _refreshTimer = 0f;
            // RefreshMainPanel 内部有 !activeSelf 保护：主面板隐藏（如查看历史页）时自动跳过
            RefreshMainPanel();
        }

        private void OnEnable()
        {
            if (World.world == null) return;
            if (!_structureBuilt)
            {
                try { RebuildUI(); } catch (Exception ex) { ZhuluContext.Instance.LogError("[MandateWindow] 构建异常", ex); }
                _structureBuilt = true;
            }
            else
            {
                RefreshAll();
            }
            // 批次9a：跨年即时刷新（不等3秒轮询；订阅幂等防重复）
            ZhuluYearEvents.Subscribe(OnYearChanged);
        }

        private void OnDisable()
        {
            ZhuluYearEvents.Unsubscribe(OnYearChanged);
        }

        private void OnYearChanged(int newYear)
        {
            if (!gameObject.activeInHierarchy) return;
            RefreshAll();
        }

        // ═══════════════════════ 结构构建 ═══════════════════════

        private void RebuildUI()
        {
            Transform win = transform;
            if (win == null) return;

            // 1. 隐藏原版 Tab 按钮（不能隐藏 tabs.gameObject——它是内容大容器！）
            try
            {
                var scrollWin = GetComponent<ScrollWindow>();
                if (scrollWin != null && scrollWin.tabs != null)
                {
                    var tabs = scrollWin.tabs.getContentTabs();
                    if (tabs != null)
                    {
                        foreach (var tab in tabs)
                        {
                            if (tab != null && tab.gameObject != null && tab.gameObject.activeSelf)
                                tab.gameObject.SetActive(false);
                        }
                    }
                }
            }
            catch { }

            // 2. 隐藏原版标题
            try
            {
                var scrollWin = GetComponent<ScrollWindow>();
                if (scrollWin != null && scrollWin.titleText != null)
                    scrollWin.titleText.gameObject.SetActive(false);
            }
            catch { }

            // 3. 原版关闭按钮：红×保留原位，只隐藏后面包裹的方框/装饰
            try { HideCloseButtonFrame(win); } catch { }

            // 4. 找 Background 并设置尺寸
            _bg = win.Find("Background");
            if (_bg == null) _bg = win;
            var bgRect = _bg.GetComponent<RectTransform>();
            if (bgRect != null) bgRect.sizeDelta = new Vector2(WindowWidth, WindowHeight);

            // 5. 隐藏原版 Scroll View（不能 Destroy——resetScroll 会访问已销毁 Transform 崩溃）
            Transform oldSV = _bg.Find("Scroll View");
            if (oldSV != null && oldSV.gameObject.activeSelf)
                oldSV.gameObject.SetActive(false);

            // 6. 自建标题（标题牌匾背景 + 金色文字，右上角留给原版红×）
            GameObject plaque = new GameObject("TitlePlaque", typeof(RectTransform), typeof(Image));
            plaque.transform.SetParent(_bg, false);
            var plaqueRect = plaque.GetComponent<RectTransform>();
            plaqueRect.anchorMin = new Vector2(0.5f, 1);
            plaqueRect.anchorMax = new Vector2(0.5f, 1);
            plaqueRect.pivot = new Vector2(0.5f, 1);
            plaqueRect.anchoredPosition = new Vector2(-8, -4);
            plaqueRect.sizeDelta = new Vector2(250f, 26f);
            SetSlicedSprite(plaque.GetComponent<Image>(), "ui/zhulu/panels/zlr_title_plaque_9s");
            ZhuluWindowDrag.Attach(plaque.transform, plaque.transform.parent as RectTransform);   // 2026-08-20 拖牌匾移动窗口，双击复位

            Text titleText = CreateText(plaque.transform, "<b>天命 · 天眷编年</b>", 12);
            titleText.alignment = TextAnchor.MiddleCenter;
            titleText.color = new Color(1f, 0.84f, 0f);
            var titleRect = titleText.GetComponent<RectTransform>();
            titleRect.anchorMin = Vector2.zero;
            titleRect.anchorMax = Vector2.one;
            titleRect.sizeDelta = Vector2.zero;
            titleText.raycastTarget = false;

            CreateTrackSwitch(_bg, "东方天命", new Vector2(8f, -34f), false);
            CreateTrackSwitch(_bg, "长生天眷", new Vector2(88f, -34f), true);

            // 7. 自建滚动容器（RectMask2D——透明 Mask 会把内容全裁掉！）
            GameObject scrollObj = new GameObject("ContentScroll", typeof(RectTransform), typeof(ScrollRect), typeof(RectMask2D), typeof(Image)); // 2M-11：恢复拖拽 Image（全透明）
            scrollObj.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
            scrollObj.transform.SetParent(_bg, false);
            var sRect = scrollObj.GetComponent<RectTransform>();
            sRect.anchorMin = new Vector2(0, 0);
            sRect.anchorMax = new Vector2(1, 1);
            sRect.offsetMin = new Vector2(6, 4);
            sRect.offsetMax = new Vector2(-6, -60);   // 顶部留标题与双天命切换按钮
            var scroll = scrollObj.GetComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 24f;
            _contentScroll = scroll;

            GameObject viewportObj = new GameObject("Viewport", typeof(RectTransform));
            viewportObj.transform.SetParent(scrollObj.transform, false);
            var vpRect = viewportObj.GetComponent<RectTransform>();
            vpRect.anchorMin = Vector2.zero;
            vpRect.anchorMax = Vector2.one;
            vpRect.offsetMin = Vector2.zero;
            vpRect.offsetMax = new Vector2(-8f, 0f);   // 右侧让 8px 给滚动条

            // 可见滚动条（右侧窄条）：内容超高时可拖拽/指示可滚动（用户要求"试试加滚动条"）
            GameObject sbObj = new GameObject("Scrollbar", typeof(RectTransform), typeof(Image), typeof(Scrollbar));
            sbObj.transform.SetParent(scrollObj.transform, false);
            var sbRect = sbObj.GetComponent<RectTransform>();
            sbRect.anchorMin = new Vector2(1f, 0f);
            sbRect.anchorMax = new Vector2(1f, 1f);
            sbRect.pivot = new Vector2(1f, 0.5f);
            sbRect.anchoredPosition = new Vector2(-1f, 0f);
            sbRect.sizeDelta = new Vector2(6f, 0f);
            sbObj.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.3f);
            GameObject handleObj = new GameObject("Handle", typeof(RectTransform), typeof(Image));
            handleObj.transform.SetParent(sbObj.transform, false);
            var handleRect = handleObj.GetComponent<RectTransform>();
            handleRect.anchorMin = new Vector2(0f, 0f);
            handleRect.anchorMax = new Vector2(1f, 1f);
            handleRect.offsetMin = Vector2.zero;
            handleRect.offsetMax = Vector2.zero;
            handleObj.GetComponent<Image>().color = new Color(0.88f, 0.78f, 0.45f, 0.9f);
            var scrollbar = sbObj.GetComponent<Scrollbar>();
            scrollbar.handleRect = handleRect;
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            scroll.verticalScrollbar = scrollbar;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;

            GameObject contentObj = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            contentObj.transform.SetParent(viewportObj.transform, false);
            _contentTransform = contentObj.transform;
            var cRect = contentObj.GetComponent<RectTransform>();
            cRect.anchorMin = new Vector2(0.5f, 1f);
            cRect.anchorMax = new Vector2(0.5f, 1f);
            cRect.pivot = new Vector2(0.5f, 1f);
            cRect.anchoredPosition = Vector2.zero;
            cRect.sizeDelta = new Vector2(WindowWidth - 24f, 0f); // 阶段2N W13：334→330（原比 Viewport 宽 4px 右侧被裁）

            var fitter = contentObj.GetComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var vGroup = contentObj.GetComponent<VerticalLayoutGroup>();
            vGroup.childAlignment = TextAnchor.UpperCenter;
            vGroup.spacing = 4f;
            vGroup.padding = new RectOffset(2, 2, 4, 4);
            vGroup.childControlWidth = false;
            vGroup.childControlHeight = false;
            vGroup.childForceExpandWidth = false;
            vGroup.childForceExpandHeight = false;

            scroll.viewport = vpRect;
            scroll.content = cRect;
            _innerWidth = WindowWidth - 16f;

            // 8. 面板构建
            CreateMainPanel();
            CreateHistoryPanel();
            ShowMain();

            ZhuluContext.Instance.LogInfo("[MandateWindow] 天命窗口 v2 自建结构完成");
        }

        /// <summary>2026-08-19 接管式关闭钮：隐藏原版红×与方框，自建逐鹿关闭钮（随窗口拖动）</summary>
        private static void HideCloseButtonFrame(Transform root)
            => ZhuluUIStyle.ReplaceCloseButton(root);


        // ═══════════════════════ 面板切换 ═══════════════════════

        private void ShowMain()
        {
            RefreshMainPanel();
            _mainPanel.SetActive(true);
            if (_historyPanel != null) _historyPanel.SetActive(false);

            // 阶段2N W13 修复：切回主面板重算 Content 高度——原残留历史页高度，主面板下方一屏可滚空白
            try
            {
                if (_contentTransform != null)
                {
                    var contentRT = _contentTransform as RectTransform;
                    if (contentRT != null)
                    {
                        contentRT.sizeDelta = new Vector2(WindowWidth - 24f, 0f);
                        LayoutRebuilder.ForceRebuildLayoutImmediate(contentRT);
                    }
                }
            }
            catch { }
        }

        private void ShowHistory()
        {
            // 必须先激活面板再刷新——RefreshHistoryPanel 开头检查 activeSelf，先刷新会被跳过导致永远空白！
            _historyPanel.SetActive(true);
            _mainPanel.SetActive(false);
            RefreshHistoryPanel();
        }

        private void CreateTrackSwitch(Transform parent, string label, Vector2 pos, bool nomadic)
        {
            GameObject obj = new GameObject("Track_" + label, typeof(RectTransform), typeof(Image), typeof(Button));
            obj.transform.SetParent(parent, false);
            RectTransform rt = obj.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = pos; rt.sizeDelta = new Vector2(76f, 20f);
            Image image = obj.GetComponent<Image>();
            SetSlicedSprite(image, "ui/special/backgroundKingdomElement");
            image.color = nomadic ? new Color(0.32f, 0.22f, 0.10f, 0.95f) : new Color(0.18f, 0.28f, 0.40f, 0.95f);
            Text text = CreateText(obj.transform, label, 8); text.alignment = TextAnchor.MiddleCenter;
            RectTransform tr = text.rectTransform; tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one; tr.offsetMin = tr.offsetMax = Vector2.zero;
            obj.GetComponent<Button>().onClick.AddListener(() =>
            {
                _showNomadic = nomadic;
                ShowMain();
            });
        }

        private void OnOpenKingdomState()
        {
            string holderId = _showNomadic ? NomadicMandateManager.Instance.GetMandateHolderId() : OrthodoxMandateManager.Instance.GetMandateHolderId();
            if (string.IsNullOrEmpty(holderId)) return;
            // 路由到王国国势视图 + 打开天下总览窗口（它根据路由渲染对应视图）
            UiRouter.Instance.NavigateTo(RouteDestination.KingdomState, kingdomId: holderId);
            ScrollWindow.showWindow(Main.OverviewWindowId);
            // 关闭天命窗口
            var scrollWin = GetComponent<ScrollWindow>();
            if (scrollWin != null) scrollWin.clickCloseButton("right");
        }

        /// <summary>调整天命值（作弊/调试用，±5）</summary>
        private void AdjustMandate(int delta)
        {
            string holderId = _showNomadic ? NomadicMandateManager.Instance.GetMandateHolderId() : OrthodoxMandateManager.Instance.GetMandateHolderId();
            if (string.IsNullOrEmpty(holderId)) return;
            if (_showNomadic) NomadicMandateManager.Instance.AdjustMandateValue(holderId, delta);
            else OrthodoxMandateManager.Instance.AdjustMandateValue(holderId, delta);
            RefreshMainPanel();
        }

        // ═══════════════════════ 数据刷新 ═══════════════════════

        // 2026-08-20 委托共享样式库（与共享版语义一致：白字/居中/Overflow/富文本/不挡射线）
        private Text CreateText(Transform parent, string content, int fontSize)
            => ZhuluUIStyle.CreateText(parent, content, fontSize);

        private static Color ParseHexColor(string hex, Color fallback)
        {
            if (string.IsNullOrEmpty(hex) || !hex.StartsWith("#")) return fallback;
            try
            {
                Color c;
                if (ColorUtility.TryParseHtmlString(hex, out c)) return c;
            }
            catch { }
            return fallback;
        }

        /// <summary>设置 9宫格贴图（2026-08-20 委托共享样式库——回退路径一致）</summary>
        private static void SetSlicedSprite(Image img, string path)
            => ZhuluUIStyle.SetSlicedSprite(img, path);
    }
}
