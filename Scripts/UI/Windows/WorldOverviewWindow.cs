using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UI;
using ZhuluRebuild.Core;
using ZhuluRebuild.Domain;
using ZhuluRebuild.Presentation.ViewModels;
using ZhuluRebuild.Presentation.Routing;
using ZhuluRebuild.Adapters;
using ZhuluRebuild.Gameplay;
using ZhuluRebuild.Persistence;


// 2026-10-23 [P1-1] 上帝类拆分：本文件保留「生命周期 / 框架 / 页签 / 改名」核心部分，
// 其余按 region 拆到同名 partial 文件（World / State / Offices / Policies / Diplomacy / Nomination / Components）。
// 纯文件级拆分，编译后仍为同一个类型，无任何行为改动。
namespace ZhuluRebuild.UI.Windows
{
    public partial class WorldOverviewWindow : MonoBehaviour
    {
        public static WorldOverviewWindow Instance { get; private set; }

        private const float WindowWidth = 320f;
        private const float WindowHeight = 380f;
        private float _innerWidth = 196f; // 内容宽度（按窗口实际宽度动态计算）
    // 阶段2J：国势榜排序状态（列索引：0档位/1国家/2君主/3城/4兵/5稳；-1=默认爵位排序）
    private int _rankSortColumn = -1;
    private bool _rankSortAsc = true;
    private bool _rankSortEnabled = false; // 阶段2J：今势榜启用列排序（其他榜关闭）
        private float InnerWidth => _innerWidth; // 兼容旧视图引用（国势/官制/国策/提名）

        /// <summary>国势等子页入口来源：kingdom_info=王国信息窗口（返回用原版clickBack窗口栈）/ overview=天下总览内部（返回切回总览路由）</summary>
        public static string StateEntrySource = "overview";

        // 自建 Header 固定元素（标题/统计/Tab/关闭按钮，锚点在 Background 上，不随内容滚动）
        private bool _headerBuilt = false;
        private Text _headerStatText;
        private Text _headerTitleText;
        private Action _headerBackOverride; // 运行时返回回调覆盖（国策二级页→大纲），null=默认路由返回
        private GameObject _headerRenameBtn; // 阶段3 改名功能：国号改名按钮（仅王国路由显示）
        private readonly Dictionary<string, GameObject> _tabButtons = new Dictionary<string, GameObject>();
        private WorldOverviewVm _worldVm;
        private KingdomStateVm _stateVm;
        private KingdomOfficesVm _officesVm;
        private KingdomPoliciesVm _policiesVm;
        private Transform _contentTransform;
        private Coroutine _talentBuildCoroutine;
        private GameObject _talentLoadingObject;

        public void Initialize()
        {
            Instance = this;
            _worldVm = new WorldOverviewVm { ActiveTab = "state" };
            ZhuluContext.Instance.LogInfo("[WorldOverviewWindow] 初始化完成，动态页眉与真机百官选拔已就绪");
        }

        public void Show()
        {
            ZhuluContext.Instance.LogInfo($"[WorldOverviewWindow] 呼出窗口，当前路由: {UiRouter.Instance.CurrentRoute.Destination}, 目标国家: {UiRouter.Instance.CurrentRoute.ActiveKingdomId ?? "全局"}");
            RebuildUI();
        }

        private void OnEnable()
        {
            // 窗口被 showWindow 激活时也要按当前路由渲染（否则显示上次内容或空白）
            if (World.world == null) return;
            RebuildUI();
        }

        private void OnDisable()
        {
            CancelTalentBuild();
        }

        public void Hide()
        {
            ZhuluContext.Instance.LogInfo("[WorldOverviewWindow] 隐藏窗口");
        }

        private void RebuildUI()
        {
            FindContentTransform();
            if (_contentTransform == null) return;

            CancelTalentBuild();

            // 加宽窗口：设置 Background 尺寸（原版 Scroll View 会随父级拉伸，Content 宽度同步变大）
            var bg = transform.Find("Background");
            if (bg != null)
            {
                var bgRect = bg.GetComponent<RectTransform>();
                if (bgRect != null) bgRect.sizeDelta = new Vector2(WindowWidth, WindowHeight);
                // 2M-12：背景恢复原版（不再擅自改背景——用户明确）
            }

            // 修正 Content 容器 X 轴居中定位，保证左右 1:1 对称
            RectTransform contentRect = _contentTransform.GetComponent<RectTransform>();
            if (contentRect != null)
            {
                contentRect.anchorMin = new Vector2(0.5f, 1f);
                contentRect.anchorMax = new Vector2(0.5f, 1f);
                contentRect.pivot = new Vector2(0.5f, 1f);
                contentRect.anchoredPosition = new Vector2(0f, 0f);

                float availW = contentRect.rect.width;
                _innerWidth = Mathf.Max(220f, availW > 0f ? availW - 16f : WindowWidth - 16f);
            }

            // 清空旧节点
            for (int i = _contentTransform.childCount - 1; i >= 0; i--)
            {
                Destroy(_contentTransform.GetChild(i).gameObject);
            }
            _buildGeneration++;   // 中断正在进行的贤才榜构建协程

            var fitter = _contentTransform.GetComponent<ContentSizeFitter>() ?? _contentTransform.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var vGroup = _contentTransform.GetComponent<VerticalLayoutGroup>() ?? _contentTransform.gameObject.AddComponent<VerticalLayoutGroup>();
            vGroup.childAlignment = TextAnchor.UpperCenter;
            vGroup.spacing = 6f;
            vGroup.padding = new RectOffset(0, 0, 4, 4);
            vGroup.childControlWidth = false;
            vGroup.childControlHeight = false;
            vGroup.childForceExpandWidth = false;

            var dest = UiRouter.Instance.CurrentRoute.Destination;
            string kingdomId = UiRouter.Instance.CurrentRoute.ActiveKingdomId;
            string officeId = UiRouter.Instance.CurrentRoute.ActiveOfficeId;

            // 阶段3 改名功能：国号按钮仅王国路由显示（国势/官制/国策/提名——天下总览隐藏）
            if (_headerRenameBtn != null)
                _headerRenameBtn.SetActive(!string.IsNullOrEmpty(kingdomId));

            // 滚动容器顶部留白按路由动态：天下总览有Tab栏留100px；国势/官制/国策/提名无Tab收紧到62px（消除标题与内容间大片留白）
            try
            {
                var bgT = transform.Find("Background");
                if (bgT != null)
                {
                    Transform cScroll = bgT.Find("ContentScroll");
                    if (cScroll != null)
                    {
                        var csRect = cScroll.GetComponent<RectTransform>();
                        if (csRect != null)
                        {
                            bool needTabs = dest == RouteDestination.WorldOverview;
                            csRect.offsetMax = new Vector2(-6, needTabs ? -100f : -52f);
                        }
                    }
                }
            }
            catch { }

            switch (dest)
            {
                case RouteDestination.KingdomState:
                    RenderKingdomStateUI(kingdomId);
                    break;
                case RouteDestination.KingdomOffices:
                    RenderKingdomOfficesUI(kingdomId);
                    break;
                case RouteDestination.KingdomPolicies:
                    RenderKingdomPoliciesUI(kingdomId);
                    break;
                case RouteDestination.KingdomDiplomacy:
                    RenderKingdomDiplomacyUI(kingdomId);
                    break;
                case RouteDestination.Nomination:
                    RenderNominationUI(kingdomId, officeId);
                    break;
                default:
                    RenderWorldOverviewUI();
                    break;
            }
        }

        private void FindContentTransform()
        {
            // 窗口根
            Transform win = transform;
            if (win == null) return;

            // 隐藏原版窗口 Tab 按钮（不能隐藏 tabs.gameObject——它是包含窗口内容的大容器，隐藏会导致整个内容区消失！）
            try
            {
                var scrollWin = GetComponent<ScrollWindow>();
                if (scrollWin != null && scrollWin.tabs != null)
                {
                    int hidden = 0;
                    var tabs = scrollWin.tabs.getContentTabs();
                    if (tabs != null)
                    {
                        foreach (var tab in tabs)
                        {
                            if (tab != null && tab.gameObject != null && tab.gameObject.activeSelf)
                            {
                                tab.gameObject.SetActive(false);
                                hidden++;
                            }
                        }
                    }
                    if (hidden > 0)
                    {
                        ZhuluContext.Instance.LogInfo($"[WorldOverviewWindow] 已隐藏 {hidden} 个原版窗口 Tab 按钮");
                    }
                }
            }
            catch { }

            // 隐藏原版窗口标题（原版 titleText 位置被自建布局影响会错乱），改为自建标题行
            try
            {
                var scrollWin2 = GetComponent<ScrollWindow>();
                if (scrollWin2 != null && scrollWin2.titleText != null)
                {
                    scrollWin2.titleText.gameObject.SetActive(false);
                }
            }
            catch { }

            Transform bg = win.Find("Background");
            if (bg == null) bg = win;

            // 原版关闭按钮：红×保留原位，只隐藏它后面包裹的方框/装饰
            try
            {
                HideCloseButtonFrame(transform);
            }
            catch { }

            // 隐藏原版 Scroll View（不能 Destroy——原版 ScrollWindow.resetScroll 会访问它导致打开崩溃），自建可控布局
            Transform oldSV = bg.Find("Scroll View");
            if (oldSV != null && oldSV.gameObject.activeSelf)
            {
                oldSV.gameObject.SetActive(false);
            }

            // 窗口尺寸
            var bgRect = bg.GetComponent<RectTransform>();
            if (bgRect != null) bgRect.sizeDelta = new Vector2(WindowWidth, WindowHeight);

            // 自建完整结构（Header + 关闭按钮 + 滚动容器，首次创建后缓存）
            if (!_headerBuilt)
            {
                _headerBuilt = true;

                // ── 0. 拖动条（2026-08-20）：标题/统计区拖动窗口（透明热区，双击复位；先建=垫底）──
                GameObject dragStrip = new GameObject("DragStrip", typeof(RectTransform), typeof(Image));
                dragStrip.transform.SetParent(bg, false);
                var dsRect = dragStrip.GetComponent<RectTransform>();
                dsRect.anchorMin = dsRect.anchorMax = new Vector2(0.5f, 1f);
                dsRect.pivot = new Vector2(0.5f, 1f);
                dsRect.anchoredPosition = new Vector2(0f, -2f);
                dsRect.sizeDelta = new Vector2(280f, 46f);
                var dsImg = dragStrip.GetComponent<Image>();
                dsImg.color = new Color(0f, 0f, 0f, 0f);
                dsImg.raycastTarget = true;
                ZhuluWindowDrag.Attach(dragStrip.transform, bg as RectTransform);

                // ── 0.5 标题牌匾（2026-08-19）：与史册/天命同款贴图，统一窗口视觉 ──
                GameObject plaque = new GameObject("TitlePlaque", typeof(RectTransform), typeof(Image));
                plaque.transform.SetParent(bg, false);
                var plaqueRect = plaque.GetComponent<RectTransform>();
                plaqueRect.anchorMin = new Vector2(0.5f, 1);
                plaqueRect.anchorMax = new Vector2(0.5f, 1);
                plaqueRect.pivot = new Vector2(0.5f, 1);
                plaqueRect.anchoredPosition = new Vector2(-8, -4);
                plaqueRect.sizeDelta = new Vector2(250f, 26f);
                ZhuluUIStyle.SetSlicedSprite(plaque.GetComponent<Image>(), "ui/zhulu/panels/zlr_title_plaque_9s");
                ZhuluWindowDrag.Attach(plaque.transform, plaque.transform.parent as RectTransform);

                // ── 1. 标题行（金色，顶部居中，右上角留给 ✕）──
                _headerTitleText = CreateText(bg, "<b>天下总览</b>", 12);
                _headerTitleText.alignment = TextAnchor.MiddleCenter;
                _headerTitleText.color = new Color(1f, 0.84f, 0f);
                var titleRect = _headerTitleText.GetComponent<RectTransform>();
                titleRect.anchorMin = new Vector2(0.5f, 1f);
                titleRect.anchorMax = new Vector2(0.5f, 1f);
                titleRect.pivot = new Vector2(0.5f, 1f);
                titleRect.anchoredPosition = new Vector2(-8, -6);
                titleRect.sizeDelta = new Vector2(200f, 22f);

                // ── 2. 统计行（动态文本，RebuildUI 更新）──
                _headerStatText = CreateText(bg, "", 9);
                _headerStatText.alignment = TextAnchor.MiddleCenter;
                var statRect = _headerStatText.GetComponent<RectTransform>();
                statRect.anchorMin = new Vector2(0.5f, 1f);
                statRect.anchorMax = new Vector2(0.5f, 1f);
                statRect.pivot = new Vector2(0.5f, 1f);
                statRect.anchoredPosition = new Vector2(0, -32);
                statRect.sizeDelta = new Vector2(320f, 18f);

                // ── 3. Tab 栏（今势/君主/贤才）──
                BuildHeaderTabs(bg);

                // ── 3.5 原版返回箭头（左上角；王国信息入口→原版clickBack窗口栈返回；总览内部入口→切回总览路由）──
                CreateHeaderBackButton(bg);

                // ── 3.6 阶段3 改名功能：国号改名按钮（仅王国路由显示——见 RebuildUI 里的显隐控制）──
                CreateHeaderRenameButton(bg);

                // ── 4. 滚动容器（中间填满，顶部留 Header 96px）──
                GameObject scrollObj = new GameObject("ContentScroll", typeof(RectTransform), typeof(ScrollRect), typeof(RectMask2D), typeof(Image)); // 2M-11：恢复拖拽 Image（全透明）
                scrollObj.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
                scrollObj.transform.SetParent(bg, false);
                var sRect = scrollObj.GetComponent<RectTransform>();
                sRect.anchorMin = new Vector2(0, 0);
                sRect.anchorMax = new Vector2(1, 1);
                sRect.offsetMin = new Vector2(6, 4);
                sRect.offsetMax = new Vector2(-6, -100);   // 顶部留 100px 给标题/统计/Tab
                var scroll = scrollObj.GetComponent<ScrollRect>();
                scroll.horizontal = false;
                scroll.vertical = true;
                scroll.movementType = ScrollRect.MovementType.Clamped;
                scroll.scrollSensitivity = 24f;

                // 阶段2N W14（白块根修）：滚动区全幅深色垫底层——卡片间隙（VLG spacing）原本直接露出
                // 原版 Background 贴图亮色区域，即"国势页卡片间隙白块"。垫底不拦射线（raycastTarget=false，
                // 拖拽仍由 scrollObj 自身透明 Image 接收）。左侧 -42 对齐卡片 -36 出血（scrollObj 自身左缩 6）。
                GameObject backing = new GameObject("DarkBacking", typeof(RectTransform), typeof(Image));
                backing.transform.SetParent(scrollObj.transform, false);
                var bkRect = backing.GetComponent<RectTransform>();
                bkRect.anchorMin = Vector2.zero;
                bkRect.anchorMax = Vector2.one;
                bkRect.offsetMin = new Vector2(-42f, 0f);
                bkRect.offsetMax = new Vector2(0f, 0f);
                var bkImg = backing.GetComponent<Image>();
                bkImg.color = new Color(0.10f, 0.11f, 0.15f, 0.92f);
                bkImg.raycastTarget = false;

                // Viewport（右侧让 16px 给滚动条）
                GameObject viewportObj = new GameObject("Viewport", typeof(RectTransform));
                viewportObj.transform.SetParent(scrollObj.transform, false);
                var vpRect = viewportObj.GetComponent<RectTransform>();
                vpRect.anchorMin = Vector2.zero;
                vpRect.anchorMax = Vector2.one;
                vpRect.offsetMin = Vector2.zero;
                vpRect.offsetMax = new Vector2(-16f, 0f);

                // Content（垂直布局容器，顶部居中）
                GameObject contentObj = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
                contentObj.transform.SetParent(viewportObj.transform, false);
                _contentTransform = contentObj.transform;
                var cRect = contentObj.GetComponent<RectTransform>();
                cRect.anchorMin = new Vector2(0.5f, 1f);
                cRect.anchorMax = new Vector2(0.5f, 1f);
                cRect.pivot = new Vector2(0.5f, 1f);
                cRect.anchoredPosition = Vector2.zero;
                cRect.sizeDelta = new Vector2(WindowWidth - 16f, 0f);

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

                // 阶段2N W13：可见滚动条（贤才榜/今势榜长列表无指示——同 Chronicle 方案）
                GameObject sbObj = new GameObject("Scrollbar", typeof(RectTransform), typeof(Image), typeof(Scrollbar));
                sbObj.transform.SetParent(scrollObj.transform, false);
                sbObj.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.3f);
                var sbRect = sbObj.GetComponent<RectTransform>();
                sbRect.anchorMin = new Vector2(1f, 0f);
                sbRect.anchorMax = new Vector2(1f, 1f);
                sbRect.pivot = new Vector2(1f, 0.5f);
                sbRect.offsetMin = new Vector2(-16f, 0f);
                sbRect.offsetMax = new Vector2(-2f, 0f);
                GameObject sbBg = new GameObject("Background", typeof(RectTransform), typeof(Image));
                sbBg.transform.SetParent(sbObj.transform, false);
                var sbBgRect = sbBg.GetComponent<RectTransform>();
                sbBgRect.anchorMin = Vector2.zero; sbBgRect.anchorMax = Vector2.one;
                sbBgRect.offsetMin = Vector2.zero; sbBgRect.offsetMax = Vector2.zero;
                sbBg.GetComponent<Image>().color = new Color(0.12f, 0.13f, 0.17f, 0.8f);
                GameObject sbHandle = new GameObject("Handle", typeof(RectTransform), typeof(Image));
                sbHandle.transform.SetParent(sbObj.transform, false);
                var sbHandleRect = sbHandle.GetComponent<RectTransform>();
                sbHandleRect.anchorMin = Vector2.zero; sbHandleRect.anchorMax = Vector2.one;
                sbHandleRect.offsetMin = Vector2.zero; sbHandleRect.offsetMax = Vector2.zero;
                sbHandle.GetComponent<Image>().color = new Color(0.45f, 0.42f, 0.3f, 0.9f);
                var sb = sbObj.GetComponent<Scrollbar>();
                sb.handleRect = sbHandleRect;
                sb.direction = Scrollbar.Direction.BottomToTop;
                scroll.verticalScrollbar = sb;
                scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;

                _innerWidth = WindowWidth - 16f;
                ZhuluContext.Instance.LogInfo("[WorldOverviewWindow] 自建完整窗口结构完成");
            }
        }

        /// <summary>自建 Tab 栏（挂在 Background，固定不滚动）</summary>
        private void BuildHeaderTabs(Transform bg)
        {
            // 2026-08-18：新增文臣榜/武将榜（在职官员限定——贤才榜为全图在野+在朝贤才）
            string[] tabs = new[] { "state", "monarch", "talent", "civil", "marshal" };
            string[] names = new[] { "今势", "君主", "贤才", "文臣", "武将" };
            float[] posX = new float[] { -124f, -62f, 0f, 62f, 124f }; // 窗宽320：5×58+间隙收拢防裁边

            _tabButtons.Clear();
            for (int i = 0; i < tabs.Length; i++)
            {
                string tabId = tabs[i];
                GameObject btnObj = new GameObject($"Tab_{tabId}", typeof(RectTransform), typeof(Image), typeof(Button));
                btnObj.transform.SetParent(bg, false);

                RectTransform btnRect = btnObj.GetComponent<RectTransform>();
                btnRect.anchorMin = new Vector2(0.5f, 1f);
                btnRect.anchorMax = new Vector2(0.5f, 1f);
                btnRect.pivot = new Vector2(0.5f, 1f);
                btnRect.anchoredPosition = new Vector2(posX[i], -56);
                btnRect.sizeDelta = new Vector2(58f, 24f);

                Image btnImg = btnObj.GetComponent<Image>();
                btnImg.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
                btnImg.type = Image.Type.Sliced;
                btnImg.color = new Color(0.2f, 0.22f, 0.28f, 0.85f);

                Button btn = btnObj.GetComponent<Button>();
                btn.targetGraphic = btnImg;
                btn.onClick.AddListener(() =>
                {
                    _worldVm.ActiveTab = tabId;
                    UpdateTabHighlight();
                    RebuildUI();
                });

                Text t = CreateText(btnObj.transform, names[i], 9);
                t.alignment = TextAnchor.MiddleCenter;
                t.color = new Color(0.8f, 0.8f, 0.8f);
                RectTransform tRect = t.GetComponent<RectTransform>();
                tRect.anchorMin = Vector2.zero;
                tRect.anchorMax = Vector2.one;
                tRect.sizeDelta = Vector2.zero;

                _tabButtons[tabId] = btnObj;
            }

            UpdateTabHighlight();
        }

        /// <summary>更新 Tab 高亮（选中红色底 + 金色下划线）</summary>
        private void UpdateTabHighlight()
        {
            string active = _worldVm != null ? _worldVm.ActiveTab : "state";
            foreach (var kvp in _tabButtons)
            {
                if (kvp.Value == null) continue;
                bool isActive = (kvp.Key == active);
                var img = kvp.Value.GetComponent<Image>();
                img.color = isActive ? new Color(0.7f, 0.2f, 0.2f, 0.95f) : new Color(0.2f, 0.22f, 0.28f, 0.85f);
                var t = kvp.Value.transform.Find("Text");
                if (t != null)
                {
                    var txt = t.GetComponent<Text>();
                    txt.text = isActive ? $"<b>{txt.text}</b>" : txt.text.Replace("<b>", "").Replace("</b>", "");
                    txt.color = isActive ? Color.white : new Color(0.8f, 0.8f, 0.8f);
                }
                // 金色下划线
                Transform ul = kvp.Value.transform.Find("Underline");
                if (isActive && ul == null)
                {
                    GameObject underline = new GameObject("Underline", typeof(RectTransform), typeof(Image));
                    underline.transform.SetParent(kvp.Value.transform, false);
                    RectTransform uRect = underline.GetComponent<RectTransform>();
                    uRect.anchorMin = new Vector2(0, 0);
                    uRect.anchorMax = new Vector2(1, 0);
                    uRect.pivot = new Vector2(0.5f, 0);
                    uRect.anchoredPosition = Vector2.zero;
                    uRect.sizeDelta = new Vector2(0, 2f);
                    underline.GetComponent<Image>().color = new Color(1f, 0.84f, 0f, 0.9f);
                    underline.GetComponent<Image>().raycastTarget = false;
                }
                if (!isActive && ul != null)
                {
                    Destroy(ul.gameObject);
                }
            }
        }

        private void UpdateHeaderTitle(string newTitle)
        {
            try
            {
                // 自建标题（主标题——原版 titleText 已隐藏，必须更新这个）
                if (_headerTitleText != null)
                {
                    _headerTitleText.text = $"<b>{newTitle}</b>";
                }
            }
            catch { }
        }

        /// <summary>原版返回箭头（Header 左上角，不占内容区）——原版 clickBack 窗口栈语义</summary>
        private void CreateHeaderBackButton(Transform bg)
        {
            GameObject btnObj = new GameObject("HeaderBackBtn", typeof(RectTransform), typeof(Image), typeof(Button), typeof(TipButton));
            btnObj.transform.SetParent(bg, false);
            var btnRect = btnObj.GetComponent<RectTransform>();
            btnRect.anchorMin = new Vector2(0.5f, 1f);
            btnRect.anchorMax = new Vector2(0.5f, 1f);
            btnRect.pivot = new Vector2(0.5f, 1f);
            btnRect.anchoredPosition = new Vector2(-122, -6);
            btnRect.sizeDelta = new Vector2(22f, 22f);
            var btnImg = btnObj.GetComponent<Image>();
            btnImg.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            btnImg.type = Image.Type.Sliced;
            btnImg.color = new Color(0.2f, 0.22f, 0.28f, 0.85f);

            Text arrow = CreateText(btnObj.transform, "<b>◄</b>", 12);
            arrow.alignment = TextAnchor.MiddleCenter;
            arrow.color = new Color(1f, 0.84f, 0f);
            var arRect = arrow.GetComponent<RectTransform>();
            arRect.anchorMin = Vector2.zero;
            arRect.anchorMax = Vector2.one;
            arRect.sizeDelta = Vector2.zero;
            arrow.raycastTarget = false;

            var tip = btnObj.GetComponent<TipButton>();
            tip.textOnClick = "";
            tip.hoverAction = () => Tooltip.show(btnObj, "tip", new TooltipData
            {
                tip_name = "返回",
                tip_description = "返回上一个界面"
            });

            var btn = btnObj.GetComponent<Button>();
            btn.onClick.AddListener(() =>
            {
                try
                {
                    if (_headerBackOverride != null)
                    {
                        // 运行时自定义返回（国策二级选择页 → 返回大纲）
                        var cb = _headerBackOverride;
                        _headerBackOverride = null;
                        cb();
                        return;
                    }
                    if (StateEntrySource == "kingdom_info")
                    {
                        // 原版窗口栈返回（从王国信息窗口进入 → 回王国信息窗口）
                        var scrollWin = GetComponent<ScrollWindow>();
                        if (scrollWin != null) scrollWin.clickBack();
                    }
                    else
                    {
                        // 天下总览内部进入 → 路由切回天下总览
                        UiRouter.Instance.NavigateTo(RouteDestination.WorldOverview);
                        RebuildUI();
                    }
                }
                catch (Exception ex)
                {
                    ZhuluContext.Instance.LogError("[WorldOverviewWindow] 返回操作异常", ex);
                }
            });
        }

        /// <summary>阶段3 改名功能：国号改名按钮（Header 左侧，返回箭头旁；点击弹出改名面板）。</summary>
        private void CreateHeaderRenameButton(Transform bg)
        {
            GameObject btnObj = new GameObject("HeaderRenameBtn", typeof(RectTransform), typeof(Image), typeof(Button), typeof(TipButton));
            btnObj.transform.SetParent(bg, false);
            var btnRect = btnObj.GetComponent<RectTransform>();
            btnRect.anchorMin = new Vector2(0.5f, 1f);
            btnRect.anchorMax = new Vector2(0.5f, 1f);
            btnRect.pivot = new Vector2(0.5f, 1f);
            btnRect.anchoredPosition = new Vector2(-96, -6);
            btnRect.sizeDelta = new Vector2(24f, 22f);
            var btnImg = btnObj.GetComponent<Image>();
            btnImg.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            btnImg.type = Image.Type.Sliced;
            btnImg.color = new Color(0.2f, 0.22f, 0.28f, 0.85f);

            Text icon = CreateText(btnObj.transform, "<b>✎</b>", 12);
            icon.alignment = TextAnchor.MiddleCenter;
            icon.color = new Color(0.75f, 0.85f, 1f);
            var icRect = icon.GetComponent<RectTransform>();
            icRect.anchorMin = Vector2.zero;
            icRect.anchorMax = Vector2.one;
            icRect.sizeDelta = Vector2.zero;
            icon.raycastTarget = false;

            var tip = btnObj.GetComponent<TipButton>();
            tip.textOnClick = "";
            tip.hoverAction = () => Tooltip.show(btnObj, "tip", new TooltipData
            {
                tip_name = "改国号",
                tip_description = "为当前国家改名（天命国亦可）。改名后地图、窗口与史册均使用新名。"
            });

            btnObj.GetComponent<Button>().onClick.AddListener(() => ShowRenamePanel());
            _headerRenameBtn = btnObj;
        }

        /// <summary>阶段3 改名功能：改名弹层（输入框 + 确定/取消；挂窗口根，遮挡点击防误触）。</summary>
        private void ShowRenamePanel()
        {
            try
            {
                string kId = UiRouter.Instance.CurrentRoute.ActiveKingdomId;
                if (string.IsNullOrEmpty(kId)) return;
                Kingdom k = ZhuluRebuild.Persistence.KingdomDataExt.FindKingdomById(kId);
                if (k == null) return;

                // 已开则关（防重复叠加）
                Transform old = transform.Find("ZuluRenamePanel");
                if (old != null) { Destroy(old.gameObject); return; }

                GameObject panel = new GameObject("ZuluRenamePanel", typeof(RectTransform), typeof(Image));
                panel.transform.SetParent(transform, false);
                var pRect = panel.GetComponent<RectTransform>();
                pRect.anchorMin = Vector2.zero;
                pRect.anchorMax = Vector2.one;
                pRect.offsetMin = Vector2.zero;
                pRect.offsetMax = Vector2.zero;
                panel.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.6f); // 全窗遮罩

                GameObject box = new GameObject("Box", typeof(RectTransform), typeof(Image));
                box.transform.SetParent(panel.transform, false);
                var bRect = box.GetComponent<RectTransform>();
                bRect.anchorMin = new Vector2(0.5f, 0.5f);
                bRect.anchorMax = new Vector2(0.5f, 0.5f);
                bRect.sizeDelta = new Vector2(240f, 96f);
                var bImg = box.GetComponent<Image>();
                bImg.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
                bImg.type = Image.Type.Sliced;
                bImg.color = new Color(0.13f, 0.14f, 0.18f, 0.98f);

                Text title = CreateText(box.transform, $"<color={UI.ZhuluUIStyle.Hex.Warn}>改国号 · {k.name}</color>", 10);
                title.alignment = TextAnchor.MiddleCenter;
                var tR = title.GetComponent<RectTransform>();
                tR.anchorMin = new Vector2(0, 1); tR.anchorMax = new Vector2(1, 1);
                tR.pivot = new Vector2(0.5f, 1); tR.anchoredPosition = Vector2.zero;
                tR.sizeDelta = new Vector2(0f, 20f);

                // 输入框
                GameObject inputObj = new GameObject("Input", typeof(RectTransform), typeof(Image), typeof(InputField));
                inputObj.transform.SetParent(box.transform, false);
                var iRect = inputObj.GetComponent<RectTransform>();
                iRect.anchorMin = new Vector2(0, 1); iRect.anchorMax = new Vector2(1, 1);
                iRect.pivot = new Vector2(0.5f, 1);
                iRect.anchoredPosition = new Vector2(0, -26);
                iRect.sizeDelta = new Vector2(-24f, 26f);
                var iImg = inputObj.GetComponent<Image>();
                iImg.color = new Color(0.08f, 0.09f, 0.12f, 1f);

                GameObject textObj = new GameObject("Text", typeof(RectTransform), typeof(Text));
                textObj.transform.SetParent(inputObj.transform, false);
                var txR = textObj.GetComponent<RectTransform>();
                txR.anchorMin = Vector2.zero; txR.anchorMax = Vector2.one;
                txR.offsetMin = new Vector2(6f, 2f); txR.offsetMax = new Vector2(-6f, -2f);
                Text tx = textObj.GetComponent<Text>();
                tx.font = LocalizedTextManager.current_font;
                tx.fontSize = 11;
                tx.color = new Color(0.93f, 0.9f, 0.82f);
                tx.alignment = TextAnchor.MiddleLeft;
                tx.horizontalOverflow = HorizontalWrapMode.Overflow;

                GameObject phObj = new GameObject("Placeholder", typeof(RectTransform), typeof(Text));
                phObj.transform.SetParent(inputObj.transform, false);
                var phR = phObj.GetComponent<RectTransform>();
                phR.anchorMin = Vector2.zero; phR.anchorMax = Vector2.one;
                phR.offsetMin = new Vector2(6f, 2f); phR.offsetMax = new Vector2(-6f, -2f);
                Text ph = phObj.GetComponent<Text>();
                ph.font = LocalizedTextManager.current_font;
                ph.fontSize = 11;
                ph.fontStyle = FontStyle.Italic;
                ph.color = new Color(0.5f, 0.5f, 0.5f, 0.6f);
                ph.text = "输入新国号…";

                InputField input = inputObj.GetComponent<InputField>();
                input.textComponent = tx;
                input.placeholder = ph;
                input.text = KingdomExt.GetManualName(kId);
                if (string.IsNullOrWhiteSpace(input.text)) input.text = k.data != null ? (k.data.name ?? "") : k.name;
                input.characterLimit = 16;
                input.ActivateInputField();

                // 确认 / 取消
                GameObject okBtn = CreateButton(box.transform, "确定", new Color(0.2f, 0.5f, 0.28f), () =>
                {
                    string name = (input.text ?? "").Trim();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        Naming.ZhuluChineseNameService.Instance.NotifyManualKingdomRename(k, name);
                        RebuildUI();
                    }
                    Destroy(panel);
                });
                var okR = okBtn.GetComponent<RectTransform>();
                okR.anchorMin = new Vector2(0.5f, 0); okR.anchorMax = new Vector2(0.5f, 0);
                okR.pivot = new Vector2(0.5f, 0);
                okR.anchoredPosition = new Vector2(-52f, 8f);
                okR.sizeDelta = new Vector2(64f, 20f);

                GameObject cancelBtn = CreateButton(box.transform, "取消", new Color(0.4f, 0.3f, 0.3f), () => Destroy(panel));
                var cR = cancelBtn.GetComponent<RectTransform>();
                cR.anchorMin = new Vector2(0.5f, 0); cR.anchorMax = new Vector2(0.5f, 0);
                cR.pivot = new Vector2(0.5f, 0);
                cR.anchoredPosition = new Vector2(52f, 8f);
                cR.sizeDelta = new Vector2(64f, 20f);
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[WorldOverviewWindow] 改名面板异常", ex);
            }
        }

        /// <summary>显示/隐藏自建 Tab 栏（今势/君主/贤才——仅天下总览需要；国势/官制/国策/提名页隐藏）</summary>
        private void SetHeaderTabsVisible(bool visible)
        {
            foreach (var kvp in _tabButtons)
            {
                if (kvp.Value != null && kvp.Value.gameObject != null && kvp.Value.activeSelf != visible)
                {
                    kvp.Value.SetActive(visible);
                }
            }
        }

    }
}
