using System;
using UnityEngine;
using UnityEngine.UI;

namespace ZhuluRebuild.UI
{
    /// <summary>
    /// 逐鹿统一 UI 样式库（2026-08-20）：色板 + 贴图缓存 + 控件工厂集中定义。
    /// 动机：此前 12 个窗口各自持有 CreateText/SetSlicedSprite/色值字面量——
    /// 同一视觉语义有 N 份拷贝，换肤/调色要改遍全库且易漏（Mandate 与 Family 的
    /// SetSlicedSprite 曾完全重复）。
    /// 设计：窗口本地工具方法全部委托到此处（调用点不动），单一事实源。
    /// 基调：水墨底 + 鎏金强调 + 宣纸正文（水墨鎏金基调）。
    /// </summary>
    public static class ZhuluUIStyle
    {
        // ═══════════════ 色板（唯一事实源）═══════════════

        /// <summary>墨底：卡片/面板背景。</summary>
        public static readonly Color Ink = new Color(0.14f, 0.13f, 0.11f, 0.92f);
        /// <summary>墨底·卡片档（透明度 0.9，列表卡片通用底色）。</summary>
        public static readonly Color InkCard = new Color(0.14f, 0.13f, 0.11f, 0.9f);
        /// <summary>深墨：按钮底/输入底（比卡片更深一档）。</summary>
        public static readonly Color InkDeep = new Color(0.11f, 0.10f, 0.09f, 0.95f);
        /// <summary>鎏金：强调文字/选中态。</summary>
        public static readonly Color Gold = new Color(0.98f, 0.77f, 0.27f);
        /// <summary>暗金：节标题/标签文字。</summary>
        public static readonly Color GoldDim = new Color(0.78f, 0.73f, 0.54f);
        /// <summary>宣纸：正文白。</summary>
        public static readonly Color Paper = Color.white;
        /// <summary>灰纸：弱化文字（备注/提示）。</summary>
        public static readonly Color PaperDim = new Color(0.62f, 0.59f, 0.52f);
        /// <summary>朱砂：警示/危险/选中描边。</summary>
        public static readonly Color Vermilion = new Color(0.82f, 0.22f, 0.16f);
        /// <summary>青玉：正向数值（增益/太平）。</summary>
        public static readonly Color Jade = new Color(0.44f, 0.83f, 0.41f);

        /// <summary>富文本色（string 内嵌用，与上面色板一一对应）。</summary>
        public static class Hex
        {
            public const string Gold = "#fbc444";
            public const string GoldDim = "#c8b98a";
            public const string PaperDim = "#9a948c";
            public const string Vermilion = "#e0554a";
            public const string Jade = "#70d468";
            public const string Azure = "#6ca3fd";

            // ── P1-C1 语义令牌（2026-08-23）：按全库实际高频用值收编（散值众数，视觉零变化）──
            // 动机：435 处硬编码 hex 中六个语义色占 251 处；新代码一律引用令牌，改肤只动此处。
            // 注意取值 = 窗口现行用值（与上方 Jade/Vermilion 的旧常量值不同源——历史两套并存，
            // 本批不做视觉合并，避免无实机验证下的观感漂移）。
            /// <summary>正向/增益（绿）——全库 68 处的事实标准值。</summary>
            public const string Positive = "#70d468";
            /// <summary>负向/告急（红）——43 处事实标准。</summary>
            public const string Negative = "#f73939";
            /// <summary>提醒/暗金——30 处事实标准。</summary>
            public const string Warn = "#f0c060";
            /// <summary>弱化说明（灰）——20 处事实标准。</summary>
            public const string Muted = "#888888";
            /// <summary>天命金（大一统/受命级强调）——68 处事实标准。</summary>
            public const string Imperial = "#FFD700";
        }

        /// <summary>
        /// 文字层级令牌（2026-10-23 审美审计新增）。
        ///
        /// 背景：调色板只有「墨/金/纸/朱/玉」五个语义，**缺「次级文字」「三级文字」「分隔线」**。
        /// 开发者遇到"这段字要淡一点"时只能自己调一个 hex —— 于是全库散落 **69 种颜色**：
        /// 4 种金（#fbc444 / #FFD700 / #c8b98a / #f0c060）、3 种红（#e0554a / #F73939 / #e0554a / #e0554a）、
        /// 2 组几乎无法区分的灰（#8a8578 与 #8a8578）。
        ///
        /// 本批**只新增令牌，不改任何调用点**（零视觉变化），供后续批量替换与静态检查使用。
        /// 取值原则：优先采用全库实际高频值，替换时不产生观感漂移。
        /// </summary>
        public static class TextColor
        {
            /// <summary>正文：米白，比纯白柔和（Paper 是纯白，直接用于正文偏刺眼）。</summary>
            public const string Primary = "#e8e2d4";
            /// <summary>说明文字：全库 50 处事实标准（附庸说明/朝贡说明等）。</summary>
            public const string Secondary = "#8a8578";
            /// <summary>次要文字/占位/空态：全库 50 处事实标准（如"空缺"）。</summary>
            public const string Tertiary = "#6a6458";
            /// <summary>分隔线：比三级文字更淡，仅用于 1px 线。</summary>
            public const string Divider = "#3a3630";
        }

        /// <summary>
        /// 字号层级（2026-10-23 审美审计新增）。
        /// 此前全库 8 档字号并存（8/9/10/11/12/13/15/16），其中 8 与 9 相差 1px
        /// 却各用上百处——等于每次写 UI 都在即兴选值。收敛为 5 级，
        /// 11/13/15 无明确语义，后续归并。
        /// </summary>
        public static class FontSize
        {
            /// <summary>窗口主标题。</summary>
            public const int Display = 16;
            /// <summary>区块/卡片标题。</summary>
            public const int Title = 12;
            /// <summary>正文。</summary>
            public const int Body = 10;
            /// <summary>注释/表格/说明。</summary>
            public const int Caption = 9;
            /// <summary>角标/密集列表（仅限确实放不下时）。</summary>
            public const int Micro = 8;
        }

        // ═══════════════ 贴图（懒加载缓存）═══════════════

        private static Sprite _cardSprite;
        private static Sprite _buttonSprite;

        /// <summary>卡片九宫格贴图（逐鹿风格底纹，失败回退原版王国元素底）。</summary>
        public static Sprite CardSprite()
        {
            if (_cardSprite != null) return _cardSprite;
            _cardSprite = SafeSprite("ui/special/backgroundKingdomElement");
            return _cardSprite;
        }

        /// <summary>按钮九宫格贴图（失败回退卡片贴图）。</summary>
        public static Sprite ButtonSprite()
        {
            if (_buttonSprite != null) return _buttonSprite;
            _buttonSprite = SafeSprite("ui/special/button");
            if (_buttonSprite == null) _buttonSprite = CardSprite();
            return _buttonSprite;
        }

        private static Sprite SafeSprite(string path)
        {
            try { return SpriteTextureLoader.getSprite(path); }
            catch { return null; }
        }

        // ═══════════════ 应用方法 ═══════════════

        /// <summary>应用卡片底：九宫格 + 墨底 + 可点击。</summary>
        public static void ApplyCard(Image img, float alpha = -1f)
        {
            if (img == null) return;
            var sp = CardSprite();
            if (sp != null) { img.sprite = sp; img.type = Image.Type.Sliced; }
            img.color = alpha >= 0f ? new Color(Ink.r, Ink.g, Ink.b, alpha) : Ink;
            img.raycastTarget = true;
        }

        /// <summary>应用按钮底：九宫格 + 深墨。</summary>
        public static void ApplyButton(Image img)
        {
            if (img == null) return;
            var sp = ButtonSprite();
            if (sp != null) { img.sprite = sp; img.type = Image.Type.Sliced; }
            img.color = InkDeep;
            img.raycastTarget = true;
        }

        // ═══════════════ 控件工厂 ═══════════════

        /// <summary>
        /// 创建文本（中文安全：current_font + Overflow 防截断 + 富文本开 + 不挡射线）。
        /// </summary>
        public static Text CreateText(Transform parent, string content, int fontSize)
        {
            var obj = new GameObject("Text", typeof(RectTransform), typeof(Text));
            obj.transform.SetParent(parent, false);
            Text text = obj.GetComponent<Text>();
            text.font = LocalizedTextManager.current_font;
            text.fontSize = fontSize;
            text.text = content;
            text.color = Paper;
            text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.supportRichText = true;
            text.raycastTarget = false;
            return text;
        }

        /// <summary>W13 兜底底色：无 sprite 的 Image 渲染纯白矩形（顶部白横条根因），故贴图缺失时强制深灰底。</summary>
        private static readonly Color FallbackInk = new Color(0.14f, 0.14f, 0.17f, 0.95f);

        /// <summary>设置九宫格贴图（加载失败回退卡片贴图 + W13 深灰兜底色，避免白块）。
        /// 与各窗口原本地版逐行等价（含回退分支改色），为唯一实现。</summary>
        public static void SetSlicedSprite(Image img, string path)
        {
            if (img == null) return;
            try
            {
                var sp = SafeSprite(path);
                if (sp != null)
                {
                    img.sprite = sp;
                    img.type = Image.Type.Sliced;
                }
                else
                {
                    img.sprite = CardSprite();
                    img.type = Image.Type.Sliced;
                    img.color = FallbackInk;
                }
            }
            catch { }
        }

        /// <summary>左上锚定矩形（列表行通用布局）。</summary>
        public static void LayoutLeft(RectTransform rt, float x, float y, float w, float h)
        {
            if (rt == null) return;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(w, h);
        }

        /// <summary>拉平填充父级（铺满）。</summary>
        public static void Stretch(RectTransform rt)
        {
            if (rt == null) return;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = Vector2.zero;
        }

        /// <summary>
        /// 2026-08-19 用户回调决定：不再干预原版关闭按钮——红×与方框背景全部恢复原版原位原样。
        /// 保留方法签名（12 个窗口调用点不动），方法体置空；接管式方案代码见 git 历史（2026-08-19 接管式关闭钮提交）。
        /// </summary>
        public static void ReplaceCloseButton(Transform win)
        {
            // 回调：空操作——原版关闭按钮（红× + 方框）完全恢复
        }
    }
}
