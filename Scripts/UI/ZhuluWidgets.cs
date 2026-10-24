using System;
using UnityEngine;
using UnityEngine.UI;
using ZhuluRebuild.Core;

namespace ZhuluRebuild.UI
{
    /// <summary>
    /// 逐鹿 UI 美化组件库（批次7，2026-08-23）。
    /// 借鉴：AW3 SchoolListItem 行单元与影响条分解 / Sandbox ButtonBuilder 流式构建 /
    /// WWB PixelIcons 程序化生成图标（零美术资源）/ 党争重构方案的状态边条规范。
    /// 核心：
    ///   1. 程序生成圆角九宫格卡底（普通/鎏金强调两档）——无需美术资源即可摆脱直角原版底；
    ///   2. Card 流式构建器——一张状态边条卡=一行链式声明；
    ///   3. SectionHeader / Divider —— 竹节·云纹分隔（装饰 PNG 此前从未注入，本批随全量注入生效）；
    ///   4. StatBar / Badge / HoverButton —— 进度条、徽章、悬停态按钮的统一实现。
    /// 所有方法防御性判空，异常不外泄。
    /// </summary>
    public static class ZhuluWidgets
    {
        // ═══════════════ 程序生成圆角九宫格 ═══════════════

        private const string KeyRound = "ui/zhulu/gen/card_round";
        private const string KeyRoundGold = "ui/zhulu/gen/card_round_gold";
        private const string KeyPill = "ui/zhulu/gen/pill";
        private static bool _genDone;

        private static readonly System.Collections.Generic.Dictionary<string, Sprite> _genSprites =
            new System.Collections.Generic.Dictionary<string, Sprite>();

        /// <summary>懒生成三枚程序化圆角贴图（自建 Sprite 缓存——SpriteTextureLoader 无法携带九宫格 border）。幂等；失败回退旧卡底。</summary>
        public static void EnsureGenerated()
        {
            if (_genDone) return;
            _genDone = true;
            try
            {
                _genSprites[KeyRound] = Make(KeyRound, GenRounded(48, 48, 12,
                    new Color32(30, 27, 23, 235), new Color32(106, 91, 63, 200), 2));   // 墨底+暗金描边
                _genSprites[KeyRoundGold] = Make(KeyRoundGold, GenRounded(48, 48, 12,
                    new Color32(38, 32, 24, 240), new Color32(200, 160, 74, 230), 2)); // 深墨+鎏金描边
                _genSprites[KeyPill] = Make(KeyPill, GenRounded(36, 20, 9,
                    new Color32(255, 255, 255, 46), new Color32(0, 0, 0, 0), 0));      // 徽章药丸（白低透明，染色用）
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogRateLimited("Widgets.Gen", "[Widgets] 圆角贴图生成失败（回退旧卡底）: " + ex.Message, 60f);
            }
        }

        private static Sprite Make(string key, Texture2D tex)
        {
            if (tex == null) return null;
            var sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect,
                new Vector4(14, 14, 14, 14));
            sp.name = key;
            return sp;
        }

        /// <summary>程序生成圆角矩形（含可选描边），返回带九宫格边界的 Texture。</summary>
        private static Texture2D GenRounded(int w, int h, int radius, Color32 fill, Color32 border, int borderWidth)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            var px = new Color32[w * h];
            float rOut = radius, rIn = radius - borderWidth;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    // 到圆角矩形的符号距离（简化：四角圆心距离）
                    float cx = Mathf.Min(Math.Max(x + 0.5f, rOut), w - rOut);
                    float cy = Mathf.Min(Math.Max(y + 0.5f, rOut), h - rOut);
                    float d = Mathf.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy)) ;
                    float inside = rOut - d;                       // >0 在形内
                    if (inside <= -0.5f) { px[y * w + x] = new Color32(0, 0, 0, 0); continue; }

                    Color32 c = fill;
                    if (borderWidth > 0 && rIn > 0)
                    {
                        float cinx = Mathf.Min(Math.Max(x + 0.5f, rIn), w - rIn);
                        float ciny = Mathf.Min(Math.Max(y + 0.5f, rIn), h - rIn);
                        float din = Mathf.Sqrt((x + 0.5f - cinx) * (x + 0.5f - cinx) + (y + 0.5f - ciny) * (y + 0.5f - ciny));
                        if (rIn - din < borderWidth - 0.5f + 1f) c = border; // 近边界 → 描边色
                    }
                    // 边缘 1px 抗锯齿
                    byte a = (byte)Mathf.Clamp((inside + 0.5f) * 255f, 0, 255);
                    c.a = (byte)Mathf.Min(c.a, a);
                    px[y * w + x] = c;
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }

        private static Sprite Sliced(string key, Sprite fallback)
        {
            EnsureGenerated();
            Sprite sp;
            if (_genSprites.TryGetValue(key, out sp) && sp != null) return sp;
            return fallback;
        }

        private static Sprite CardBg() => Sliced(KeyRound, ZhuluUIStyle.CardSprite());
        private static Sprite CardBgGold() => Sliced(KeyRoundGold, ZhuluUIStyle.CardSprite());
        private static Sprite PillBg() => Sliced(KeyPill, null);

        // ═══════════════ 卡片构建器 ═══════════════

        /// <summary>状态语义色（对应 Hex 令牌）。</summary>
        public enum Edge { None, Positive, Negative, Warn, Muted, Imperial, Azure }

        private static string EdgeHex(Edge e)
        {
            switch (e)
            {
                case Edge.Positive: return ZhuluUIStyle.Hex.Positive;
                case Edge.Negative: return ZhuluUIStyle.Hex.Negative;
                case Edge.Warn: return ZhuluUIStyle.Hex.Warn;
                case Edge.Muted: return ZhuluUIStyle.Hex.Muted;
                case Edge.Imperial: return ZhuluUIStyle.Hex.Imperial;
                case Edge.Azure: return ZhuluUIStyle.Hex.Azure;
                default: return null;
            }
        }

        /// <summary>
        /// 构建一张圆角卡片（墨底+暗金描边+左侧状态边条+可选金色标题行）。
        /// 返回内容挂载点（已含标题占位高度）；配合 VLG 父级使用。
        /// </summary>
        public static RectTransform Card(Transform parent, float width, float height,
            string title = null, Edge edge = Edge.None, bool emphasis = false)
        {
            EnsureGenerated();
            var card = new GameObject("Card", typeof(RectTransform), typeof(Image));
            card.transform.SetParent(parent, false);
            var rt = card.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(width, height);
            var img = card.GetComponent<Image>();
            img.sprite = emphasis ? CardBgGold() : CardBg();
            img.type = Image.Type.Sliced;
            img.color = Color.white;

            const float pad = 4f;
            // 左侧状态边条
            string hex = EdgeHex(edge);
            if (hex != null)
            {
                var bar = new GameObject("Edge", typeof(RectTransform), typeof(Image));
                bar.transform.SetParent(card.transform, false);
                var brt = bar.GetComponent<RectTransform>();
                brt.anchorMin = new Vector2(0f, 0f); brt.anchorMax = new Vector2(0f, 1f);
                brt.pivot = new Vector2(0f, 0.5f);
                brt.anchoredPosition = new Vector2(pad + 1f, 0f);
                brt.sizeDelta = new Vector2(3f, -2f * pad);
                Color c; ColorUtility.TryParseHtmlString(hex, out c); c.a = 0.9f;
                bar.GetComponent<Image>().color = c;
            }

            float topUsed = 0f;
            float padX = pad + 2f;                       // 无边条时水平内缩
            if (!string.IsNullOrEmpty(title))
            {
                var tt = ZhuluUIStyle.CreateText(card.transform, $"<b><color={ZhuluUIStyle.Hex.Gold}>{title}</color></b>", 10);
                tt.alignment = TextAnchor.MiddleLeft;
                var trt = tt.GetComponent<RectTransform>();
                trt.anchorMin = new Vector2(0f, 1f); trt.anchorMax = new Vector2(1f, 1f);
                trt.pivot = new Vector2(0f, 1f);
                trt.anchoredPosition = new Vector2(padX, -3f);
                trt.sizeDelta = new Vector2(-2f * padX, 18f);
                topUsed = 20f;
            }
            if (hex != null) padX += 5f;                 // 有状态边条再让 5px

            var body = new GameObject("Body", typeof(RectTransform));
            body.transform.SetParent(card.transform, false);
            var brt2 = body.GetComponent<RectTransform>();
            // 2026-08-23 重叠修复：stretch 锚点下 anchoredPosition.y 抬的是"底边"，顶边仍在
            // 卡顶-8px 处、压进标题带(标题延伸到-21px)。改显式 offsets——顶边完整让位标题带。
            brt2.anchorMin = new Vector2(0f, 0f); brt2.anchorMax = new Vector2(1f, 1f);
            brt2.offsetMin = new Vector2(padX, pad);                 // 左/下内缩（底部不再上提）
            brt2.offsetMax = new Vector2(-padX, -(topUsed + 2f));    // 右/上：上沿让位标题带+2px 呼吸
            return brt2;
        }

        // ═══════════════ 节标题 + 分隔线 ═══════════════

        /// <summary>节标题：金字 + 云纹分隔线（贴图缺失回退细线）。</summary>
        public static float SectionHeader(Transform parent, float width, string title, float y, bool useCloud = true)
        {
            var tt = ZhuluUIStyle.CreateText(parent, $"<b><color={ZhuluUIStyle.Hex.GoldDim}>{title}</color></b>", 10);
            tt.alignment = TextAnchor.MiddleLeft;
            var trt = tt.GetComponent<RectTransform>();
            LayoutLeft(trt, 0f, y, width, 16f);

            Divider(parent, width, y + 18f, useCloud);
            return y + 28f;
        }

        /// <summary>分隔线：云纹/竹节贴图（未注入则回退 1px 暗金线）。</summary>
        public static void Divider(Transform parent, float width, float y, bool cloud = true)
        {
            var go = new GameObject("Divider", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            LayoutLeft(rt, 2f, y, width - 4f, 8f);
            var img = go.GetComponent<Image>();
            string path = cloud ? "ui/zhulu/decorations/zlr_divider_cloud_64x8" : "ui/zhulu/decorations/zlr_divider_bamboo_64x8";
            try
            {
                var sp = SpriteTextureLoader.getSprite(path);
                if (sp != null) { img.sprite = sp; img.type = Image.Type.Sliced; img.color = new Color(1f, 1f, 1f, 0.55f); }
                else { img.color = new Color(0.78f, 0.73f, 0.54f, 0.35f); } // 回退细线观感
            }
            catch { img.color = new Color(0.78f, 0.73f, 0.54f, 0.35f); }
            img.raycastTarget = false;
        }

        // ═══════════════ 行单元（借鉴 AW3 SchoolListItem） ═══════════════

        /// <summary>双行行单元：[4px 边条][主行 9pt][副行 7pt 灰][右值]。返回根 RectTransform（可挂点击）。</summary>
        public static RectTransform RowItem(Transform parent, float width, float rowH,
            string mainText, string subText, string rightValue = null, Edge edge = Edge.None)
        {
            var root = new GameObject("RowItem", typeof(RectTransform));
            root.transform.SetParent(parent, false);
            var rt = root.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(width, rowH);

            string hex = EdgeHex(edge);
            if (hex != null)
            {
                var bar = new GameObject("Edge", typeof(RectTransform), typeof(Image));
                bar.transform.SetParent(root.transform, false);
                var brt = bar.GetComponent<RectTransform>();
                brt.anchorMin = new Vector2(0f, 0f); brt.anchorMax = new Vector2(0f, 1f);
                brt.pivot = new Vector2(0f, 0.5f);
                brt.anchoredPosition = new Vector2(2f, 0f);
                brt.sizeDelta = new Vector2(3f, -4f);
                Color c; ColorUtility.TryParseHtmlString(hex, out c); c.a = 0.85f;
                bar.GetComponent<Image>().color = c;
            }

            float textX = hex != null ? 10f : 4f;
            var main = ZhuluUIStyle.CreateText(root.transform, mainText, 9);
            main.alignment = TextAnchor.LowerLeft;
            var mrt = main.GetComponent<RectTransform>();
            mrt.anchorMin = new Vector2(0f, 1f); mrt.anchorMax = new Vector2(1f, 1f);
            mrt.pivot = new Vector2(0f, 1f);
            mrt.anchoredPosition = new Vector2(textX, -1f);
            mrt.sizeDelta = new Vector2(-(textX + 4f) - (string.IsNullOrEmpty(rightValue) ? 0f : 56f), rowH * 0.55f);

            if (!string.IsNullOrEmpty(subText))
            {
                var sub = ZhuluUIStyle.CreateText(root.transform, $"<color={ZhuluUIStyle.Hex.Muted}>{subText}</color>", 7);
                sub.alignment = TextAnchor.UpperLeft;
                var srt = sub.GetComponent<RectTransform>();
                srt.anchorMin = new Vector2(0f, 0f); srt.anchorMax = new Vector2(1f, 0f);
                srt.pivot = new Vector2(0f, 0f);
                srt.anchoredPosition = new Vector2(textX, 2f);
                srt.sizeDelta = new Vector2(-(textX + 4f), rowH * 0.42f);
            }

            if (!string.IsNullOrEmpty(rightValue))
            {
                var rv = ZhuluUIStyle.CreateText(root.transform, rightValue, 9);
                rv.alignment = TextAnchor.MiddleRight;
                var rrt = rv.GetComponent<RectTransform>();
                rrt.anchorMin = new Vector2(1f, 0f); rrt.anchorMax = new Vector2(1f, 1f);
                rrt.pivot = new Vector2(1f, 0.5f);
                rrt.anchoredPosition = new Vector2(-4f, 0f);
                rrt.sizeDelta = new Vector2(52f, -2f);
            }
            return rt;
        }

        // ═══════════════ 进度条 / 徽章 / 按钮 ═══════════════

        /// <summary>统一进度条：深底轨 + 语义色填充（ratio 0~1）。</summary>
        public static void StatBar(Transform parent, float x, float y, float w, float h, float ratio, string hexFill)
        {
            ratio = Mathf.Clamp01(ratio);
            var track = new GameObject("BarTrack", typeof(RectTransform), typeof(Image));
            track.transform.SetParent(parent, false);
            LayoutLeft(track.GetComponent<RectTransform>(), x, y, w, h);
            var ti = track.GetComponent<Image>();
            ti.color = new Color32(16, 15, 13, 220);
            ti.sprite = PillBg(); if (ti.sprite != null) ti.type = Image.Type.Sliced;

            if (ratio <= 0f) return;
            var fill = new GameObject("BarFill", typeof(RectTransform), typeof(Image));
            fill.transform.SetParent(track.transform, false);
            var frt = fill.GetComponent<RectTransform>();
            frt.anchorMin = new Vector2(0f, 0f); frt.anchorMax = new Vector2(0f, 1f);
            frt.pivot = new Vector2(0f, 0.5f);
            frt.anchoredPosition = Vector2.zero;
            frt.sizeDelta = new Vector2(Mathf.Max(2f, (w - 2f) * ratio), -2f);
            var fi = fill.GetComponent<Image>();
            Color c; ColorUtility.TryParseHtmlString(hexFill, out c);
            fi.color = c; fi.sprite = PillBg(); if (fi.sprite != null) fi.type = Image.Type.Sliced;
        }

        /// <summary>徽章药丸：低透明底 + 语义色字。</summary>
        public static RectTransform Badge(Transform parent, float x, float y, string text, string hex)
        {
            var go = new GameObject("Badge", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            LayoutLeft(rt, x, y, 44f, 14f);
            var img = go.GetComponent<Image>();
            img.sprite = PillBg(); if (img.sprite != null) img.type = Image.Type.Sliced;
            Color bg = Color.white * 0.25f; bg.a = 0.35f;
            img.color = bg;

            var t = ZhuluUIStyle.CreateText(go.transform, $"<color={hex}>{text}</color>", 7);
            t.alignment = TextAnchor.MiddleCenter;
            Stretch(t.GetComponent<RectTransform>());
            return rt;
        }

        /// <summary>悬停态按钮：原生 ColorTint 过渡（Normal/Highlighted/Pressed 三态统一）。</summary>
        public static Button HoverButton(Transform parent, string label, float x, float y, float w, float h, Action onClick)
        {
            var go = new GameObject("Btn", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            LayoutLeft(rt, x, y, w, h);
            var img = go.GetComponent<Image>();
            img.sprite = ZhuluUIStyle.ButtonSprite(); if (img.sprite != null) img.type = Image.Type.Sliced;
            img.color = new Color(0.16f, 0.15f, 0.13f, 0.96f);

            var btn = go.GetComponent<Button>();
            var cb = btn.colors;
            cb.normalColor = new Color(1f, 1f, 1f, 1f);
            cb.highlightedColor = new Color(1.35f, 1.25f, 1.05f, 1f); // 悬停泛暖金
            cb.pressedColor = new Color(0.75f, 0.72f, 0.68f, 1f);
            cb.fadeDuration = 0.08f;
            btn.colors = cb;

            var t = ZhuluUIStyle.CreateText(go.transform, label, 9);
            t.alignment = TextAnchor.MiddleCenter;
            Stretch(t.GetComponent<RectTransform>());

            btn.onClick.AddListener(() => { try { onClick?.Invoke(); } catch (Exception ex) { ZhuluContext.Instance.LogWarning("[Widgets] 按钮回调异常: " + ex.Message); } });
            return btn;
        }

        private static void Stretch(RectTransform r)
        {
            r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one;
            r.offsetMin = Vector2.zero; r.offsetMax = Vector2.zero;
        }

        private static void LayoutLeft(RectTransform r, float x, float y, float w, float h)
        {
            r.anchorMin = new Vector2(0, 1); r.anchorMax = new Vector2(0, 1);
            r.pivot = new Vector2(0, 1);
            r.anchoredPosition = new Vector2(x, -y);
            r.sizeDelta = new Vector2(w, h);
        }
    }
}
