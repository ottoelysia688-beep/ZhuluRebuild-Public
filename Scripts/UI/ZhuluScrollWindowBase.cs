using System;
using UnityEngine;
using UnityEngine.UI;
using ZhuluRebuild.Core;

namespace ZhuluRebuild.UI
{
    /// <summary>
    /// 逐鹿滚动窗口骨架基类（P1-C2，2026-08-23）。
    /// 动机：13 个窗口各持约百行相同初始化（隐藏原版 tabs/标题 → 定尺寸背景 → 牌匾+拖拽 →
    /// 返回钮 → RectMask2D 滚动区 → VLG+CSF 内容流），复制粘贴税约 1300 行且改一处要遍改十三处。
    /// 本基类把骨架收敛为一次实现；子类只提供 标题/尺寸/BuildContent 三件事。
    /// 迁移策略：逐窗迁移（先小后大），每迁一窗实机验证一窗；未迁移窗口不受任何影响。
    /// 骨架步骤与 ZhuluManualWindow 原实现逐行等价（含 W13 兜底、旧 ScrollView 停用等踩坑结论）。
    /// </summary>
    public abstract class ZhuluScrollWindowBase : MonoBehaviour
    {
        protected const float ContentSideInset = 6f;   // 滚动区左右内缩（防边框裁字）
        protected const float PlaqueBandHeight = 36f;  // 牌匾带高（滚动区顶部让位）
        protected const float ContentSpacing = 6f;     // 卡片间距

        protected Transform Bg { get; private set; }
        protected Transform ContentRoot { get; private set; }
        protected float InnerWidth { get; private set; }

        /// <summary>牌匾标题（富文本可用，如 "&lt;b&gt;逐鹿 · 玩法速览&lt;/b&gt;"）。</summary>
        protected abstract string WindowTitle { get; }
        protected virtual float WindowWidth => 320f;
        protected virtual float WindowHeight => 400f;
        /// <summary>牌匾标题字色。</summary>
        protected virtual Color TitleColor => new Color(1f, 0.84f, 0f);
        /// <summary>牌匾宽度。</summary>
        protected virtual float PlaqueWidth => 230f;
        /// <summary>
        /// 滚动区顶部让位总高（默认=牌匾带36）。带固定副标题/分类栏的窗口覆写更大值，
        /// 并在 BuildContent 里把固定元素挂到 Bg（绝对定位），内容仍走 ContentRoot 流式。
        /// </summary>
        protected virtual float ContentTopBand => PlaqueBandHeight;
        /// <summary>是否创建右侧常驻竖直滚动条（长内容窗口覆写 true——批次8自设置窗收编）。</summary>
        protected virtual bool WantsScrollbar => false;
        /// <summary>滚动区底部让位高度（默认4；带底部页签/按钮条的窗口覆写更大值——批次8自家族窗收编）。</summary>
        protected virtual float ContentBottomBand => 4f;

        private bool _structureBuilt;
        private Text _plaqueText;
        private ScrollRect _contentScroll;

        /// <summary>牌匾题文本（需动态改题的窗口在 BuildContent 后直接写此字段）。</summary>
        protected Text PlaqueText => _plaqueText;
        /// <summary>基类创建的滚动区 ScrollRect（需启停滚动/联动滚轮的窗口用，如世系平移页）。</summary>
        protected ScrollRect ContentScroll => _contentScroll;

        /// <summary>子类在 OnEnable/Show 中调用：首次进入时构建骨架+内容（幂等）。</summary>
        protected void EnsureBuilt()
        {
            if (_structureBuilt) return;
            try
            {
                RebuildUI();
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[" + GetType().Name + "] 构建异常", ex);
            }
            _structureBuilt = true;
        }

        // ═══════════════ 骨架（与各窗原本地实现逐步骤等价） ═══════════════

        private void RebuildUI()
        {
            Transform win = transform;
            if (win == null) return;

            // ① 隐藏原版 tabs 与标题（旧 ScrollView 不能 Destroy——resetScroll 会崩，只停用）
            try
            {
                var scrollWin = GetComponent<ScrollWindow>();
                if (scrollWin != null && scrollWin.tabs != null)
                {
                    var tabs = scrollWin.tabs.getContentTabs();
                    if (tabs != null)
                        foreach (var tab in tabs)
                            if (tab != null && tab.gameObject != null && tab.gameObject.activeSelf)
                                tab.gameObject.SetActive(false);
                }
                if (scrollWin != null && scrollWin.titleText != null)
                    scrollWin.titleText.gameObject.SetActive(false);
            }
            catch { }

            // ② 背景定尺寸
            Bg = win.Find("Background");
            if (Bg == null) Bg = win;
            var bgRect = Bg.GetComponent<RectTransform>();
            if (bgRect != null) bgRect.sizeDelta = new Vector2(WindowWidth, WindowHeight);

            // ③ 停用原生滚动视图
            Transform oldSV = Bg.Find("Scroll View");
            if (oldSV != null && oldSV.gameObject.activeSelf) oldSV.gameObject.SetActive(false);

            // ④ 关闭钮策略位（当前为回调空操作）+ 返回钮
            ZhuluUIStyle.ReplaceCloseButton(win);
            CreateBackButton(Bg);

            // ⑤ 牌匾（9-slice + 拖拽双击复位）
            var plaque = new GameObject("TitlePlaque", typeof(RectTransform), typeof(Image));
            plaque.transform.SetParent(Bg, false);
            var pr = plaque.GetComponent<RectTransform>();
            pr.anchorMin = new Vector2(0.5f, 1f);
            pr.anchorMax = new Vector2(0.5f, 1f);
            pr.pivot = new Vector2(0.5f, 1f);
            pr.anchoredPosition = new Vector2(0, -4);
            pr.sizeDelta = new Vector2(PlaqueWidth, 26f);
            ZhuluUIStyle.SetSlicedSprite(plaque.GetComponent<Image>(), "ui/zhulu/panels/zlr_title_plaque_9s");
            ZhuluWindowDrag.Attach(plaque.transform, plaque.transform.parent as RectTransform);

            var titleT = ZhuluUIStyle.CreateText(plaque.transform, WindowTitle, 12);
            _plaqueText = titleT;
            titleT.alignment = TextAnchor.MiddleCenter;
            titleT.color = TitleColor;
            Stretch(titleT.GetComponent<RectTransform>());

            // ⑥ 滚动区：RectMask2D + 透明底（透明 Mask 会裁光内容是历史踩坑——此处透明仅作遮罩载体）
            var scrollObj = new GameObject("ContentScroll", typeof(RectTransform), typeof(ScrollRect), typeof(RectMask2D), typeof(Image));
            scrollObj.GetComponent<Image>().color = new Color(0, 0, 0, 0);
            scrollObj.transform.SetParent(Bg, false);
            var sRect = scrollObj.GetComponent<RectTransform>();
            sRect.anchorMin = new Vector2(0, 0);
            sRect.anchorMax = new Vector2(1, 1);
            sRect.offsetMin = new Vector2(ContentSideInset, ContentBottomBand);
            sRect.offsetMax = new Vector2(-ContentSideInset, -ContentTopBand);
            var scroll = scrollObj.GetComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 24f;

            // ⑦ 视口 + 内容流（VLG+CSF，顶中锚，宽=窗宽-16）
            var viewportObj = new GameObject("Viewport", typeof(RectTransform));
            viewportObj.transform.SetParent(scrollObj.transform, false);
            var vp = viewportObj.GetComponent<RectTransform>();
            vp.anchorMin = Vector2.zero; vp.anchorMax = Vector2.one;
            vp.offsetMin = Vector2.zero; vp.offsetMax = Vector2.zero;

            var contentObj = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            contentObj.transform.SetParent(viewportObj.transform, false);
            var cRect = contentObj.GetComponent<RectTransform>();
            cRect.anchorMin = new Vector2(0.5f, 1f);
            cRect.anchorMax = new Vector2(0.5f, 1f);
            cRect.pivot = new Vector2(0.5f, 1f);
            cRect.anchoredPosition = Vector2.zero;
            cRect.sizeDelta = new Vector2(WindowWidth - 16f, 0f);
            contentObj.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var vlg = contentObj.GetComponent<VerticalLayoutGroup>();
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.spacing = ContentSpacing;
            vlg.padding = new RectOffset(2, 2, 4, 4);
            vlg.childControlWidth = false;
            vlg.childControlHeight = false;
            vlg.childForceExpandWidth = false;
            vlg.childForceExpandHeight = false;
            scroll.viewport = vp;
            scroll.content = cRect;
            _contentScroll = scroll;

            // ⑦.5 竖直滚动条（阶段2L 定式收编：根 Image 补 tint 防白条 + 暗金把手）
            if (WantsScrollbar)
            {
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
                sb.direction = Scrollbar.Direction.BottomToTop;
                sb.handleRect = sbHandleRect;
                sb.targetGraphic = sbHandle.GetComponent<Image>();
                scroll.verticalScrollbar = sb;
                scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
            }

            InnerWidth = WindowWidth - 16f;
            ContentRoot = contentObj.transform;

            // ⑧ 子类内容
            BuildContent();
        }

        /// <summary>子类填充内容（ContentRoot 已就绪）。</summary>
        protected abstract void BuildContent();

        // ═══════════════ 子类工具 ═══════════════

        /// <summary>统一文本创建（委托共享样式库——中文字体/防溢出/富文本）。</summary>
        protected static Text CreateText(Transform parent, string content, int fontSize)
            => ZhuluUIStyle.CreateText(parent, content, fontSize);

        /// <summary>返回钮（点击回原版导航栈；无栈则隐藏自身）。</summary>
        protected void CreateBackButton(Transform parent)
        {
            try
            {
                var backObj = new GameObject("BackBtn", typeof(RectTransform), typeof(Image), typeof(Button));
                backObj.transform.SetParent(parent, false);
                var backRt = backObj.GetComponent<RectTransform>();
                backRt.anchorMin = new Vector2(0f, 1f);
                backRt.anchorMax = new Vector2(0f, 1f);
                backRt.pivot = new Vector2(0f, 1f);
                backRt.anchoredPosition = new Vector2(8f, -8f);
                backRt.sizeDelta = new Vector2(44f, 22f);
                var img = backObj.GetComponent<Image>();
                img.color = new Color(0.14f, 0.14f, 0.17f, 0.92f);
                img.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
                img.type = Image.Type.Sliced;

                var t = ZhuluUIStyle.CreateText(backObj.transform, "← 返回", 9);
                t.alignment = TextAnchor.MiddleCenter;
                t.color = new Color(0.85f, 0.84f, 0.92f, 1f);
                Stretch(t.GetComponent<RectTransform>());

                backObj.GetComponent<Button>().onClick.AddListener(() =>
                {
                    try
                    {
                        HandleBackClick();
                    }
                    catch { gameObject.SetActive(false); }
                });
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[{GetType().Name}] 返回按钮异常: {ex.Message}");
            }
        }

        /// <summary>返回钮点击行为（默认回原版导航栈；多页窗口覆写实现页内回退——批次8自党争窗收编）。</summary>
        protected virtual void HandleBackClick()
        {
            var sw = GetComponent<ScrollWindow>();
            if (sw != null) sw.clickBack();
            else gameObject.SetActive(false);
        }

        protected static void Stretch(RectTransform r)
        {
            r.anchorMin = Vector2.zero;
            r.anchorMax = Vector2.one;
            r.sizeDelta = Vector2.zero;
            r.offsetMin = Vector2.zero;
            r.offsetMax = Vector2.zero;
        }

        /// <summary>左上锚定布局（y 为正的"向下"语义——与各窗本地版习惯一致）。</summary>
        protected static void LayoutLeft(RectTransform r, float x, float y, float w, float h)
        {
            r.anchorMin = new Vector2(0, 1);
            r.anchorMax = new Vector2(0, 1);
            r.pivot = new Vector2(0, 1);
            r.anchoredPosition = new Vector2(x, -y);
            r.sizeDelta = new Vector2(w, h);
        }
    }
}
