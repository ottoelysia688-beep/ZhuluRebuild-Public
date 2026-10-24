using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using ZhuluRebuild.Core;
using ZhuluRebuild.Domain;
using ZhuluRebuild.Interop;

namespace ZhuluRebuild.UI.Windows
{
    /// <summary>
    /// 历代天命窗口 · 帝王详情页 + 断代史卡（LlmV2-2 → V2-7/8 重写）。
    ///
    /// V2-7 重写教训：首版把所有内容塞进**单张卡里手工累加 y**——任一文本测高偏差就级联重叠
    /// （实机截图证实：柱状图压字、表格错位）。本版改为**分区卡片制**：
    /// 返回条/帝号卡/评语卡/图表卡/明细卡各是 _historyList 的直接子物体，VLG 自动排布，
    /// 一张卡测高错了不再连累其它卡。卡内文本高度一律 preferredHeight 与字符估行取大。
    ///
    /// 用户确认项：列表 AI 长评全文换行；详情页实录/野史双 tab（大页签）；史论卡默认折叠。
    /// </summary>
    public partial class ZhuluMandateWindow
    {
        private DynastyRecord _detailDynasty;
        private EmperorRecord _detailEmperor;   // null = 列表态
        private int _detailIdx;                 // 1 基帝序（与 AI 键同口径）
        private int _detailTab;                 // 0=实录 1=野史
        // 断代史卡展开态（会话级；默认全折叠）
        private static readonly HashSet<string> _chronExpanded = new HashSet<string>();

        // ═══════════════ 纯函数（可单测）═══════════════

        /// <summary>柱状图抽稀：保首尾与极盛，其余均匀取样至 ≤max 根。</summary>
        public static List<EmperorSample> ThinTrack(IList<EmperorSample> track, int max)
        {
            var result = new List<EmperorSample>();
            if (track == null || track.Count == 0 || max < 2) return result;
            if (track.Count <= max)
            {
                result.AddRange(track);
                return result;
            }
            var peak = EmperorTrackSampler.Peak(track);
            var picked = new HashSet<int> { 0, track.Count - 1 };
            for (int i = 0; i < track.Count; i++)
                if (ReferenceEquals(track[i], peak)) picked.Add(i);
            int remain = max - picked.Count;
            if (remain > 0)
            {
                double step = (track.Count - 1) / (double)(remain + 1);
                for (int k = 1; k <= remain; k++)
                {
                    int idx = (int)Math.Round(k * step);
                    if (idx >= 0 && idx < track.Count) picked.Add(idx);
                }
            }
            var sorted = new List<int>(picked);
            sorted.Sort();
            foreach (var i in sorted) result.Add(track[i]);
            return result;
        }

        /// <summary>柱状图归一化：返回每根柱高 0~1（峰值=1），并给出峰值下标。</summary>
        public static float[] NormalizeBars(List<EmperorSample> samples, out int peakIndex)
        {
            peakIndex = -1;
            if (samples == null || samples.Count == 0) return new float[0];
            int maxScore = 0;
            for (int i = 0; i < samples.Count; i++)
            {
                int s = samples[i].PowerScore;
                if (s > maxScore) { maxScore = s; peakIndex = i; }
            }
            var bars = new float[samples.Count];
            if (maxScore <= 0) return bars;
            for (int i = 0; i < samples.Count; i++)
                bars[i] = Mathf.Clamp01((float)samples[i].PowerScore / maxScore);
            bars[peakIndex] = 1f;
            return bars;
        }

        // ═══════════════ 进入/退出详情 ═══════════════

        private void OpenEmperorDetail(DynastyRecord d, EmperorRecord e, int idx)
        {
            _detailDynasty = d;
            _detailEmperor = e;
            _detailIdx = idx;
            _detailTab = 0;
            RefreshHistoryPanel();
        }

        private void CloseEmperorDetail()
        {
            _detailEmperor = null;
            _detailTab = 0;
            RefreshHistoryPanel();
        }

        /// <summary>
        /// 帝王块透明点击层（列表态）：盖住本帝三行，点进详情页。
        /// 放到最底层（SetAsFirstSibling）——「本纪/AI 重评」按钮在上层先行吃点击。
        /// </summary>
        private void CreateEmperorHitZone(Transform parent, DynastyRecord d, EmperorRecord e,
            int idx, float blockTop, float blockUsed)
        {
            GameObject hit = new GameObject("EmpHit", typeof(RectTransform), typeof(Image), typeof(Button));
            hit.transform.SetParent(parent, false);
            hit.transform.SetAsFirstSibling();   // 最底层：不挡按钮
            var hr = hit.GetComponent<RectTransform>();
            hr.anchorMin = new Vector2(0, 1);
            hr.anchorMax = new Vector2(1, 1);
            hr.pivot = new Vector2(0.5f, 1);
            hr.anchoredPosition = new Vector2(0, blockTop);
            hr.sizeDelta = new Vector2(0, Mathf.Max(12f, blockUsed + 4f));
            var img = hit.GetComponent<Image>();
            img.color = new Color(1f, 1f, 1f, 0.005f);
            img.raycastTarget = true;
            var btn = hit.GetComponent<Button>();
            btn.targetGraphic = img;
            DynastyRecord dd = d; EmperorRecord ee = e; int ii = idx;
            btn.onClick.AddListener(() => { try { OpenEmperorDetail(dd, ee, ii); } catch { } });
        }

        /// <summary>行尾「本纪 ▸」入口按钮（LlmV2-8）：与「AI 重评」组成右对齐按钮组。</summary>
        private void CreateDetailEntryButton(Transform parent, DynastyRecord d, EmperorRecord e, int idx, float y)
        {
            CreateSmallButton(parent, "本纪 ▸", _innerWidth - 56f, y + 2f, 48f, 14f,
                lbl => { try { OpenEmperorDetail(d, e, idx); } catch { } });
        }

        // ═══════════════ 通用构建件 ═══════════════

        /// <summary>详情分区卡：独立挂在 _historyList 上由 VLG 排布（级联错位根除）。</summary>
        private RectTransform CreateDetailCard(string goName, float height)
        {
            GameObject row = new GameObject(goName, typeof(RectTransform));
            row.transform.SetParent(_historyList, false);
            var rowRect = row.GetComponent<RectTransform>();
            rowRect.sizeDelta = new Vector2(_innerWidth, height);

            GameObject bgObj = new GameObject("Bg", typeof(RectTransform), typeof(Image));
            bgObj.transform.SetParent(row.transform, false);
            var bgRect = bgObj.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = new Vector2(-36f, 0f);
            bgRect.offsetMax = Vector2.zero;
            var bgImg = bgObj.GetComponent<Image>();
            bgImg.color = new Color(0.13f, 0.12f, 0.10f, 0.95f);
            bgImg.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            bgImg.type = Image.Type.Sliced;
            bgImg.raycastTarget = false;
            return rowRect;
        }

        /// <summary>卡内文本：创建+布局+返回实测高度（preferred 与估行取大，杜绝手拍行高）。
        /// V2-10 关键：alignment 必须是 UpperLeft——MiddleLeft+Overflow 下多行文字以 rect 中心
        /// 居中、**向上溢出压住上一行**（实机截图：史官评压正文/史论标题压正文的根因）。</summary>
        private float DetailText(Transform card, string content, int fontSize,
            float x, float y, float width)
        {
            Text t = CreateText(card, content, fontSize);
            t.alignment = TextAnchor.UpperLeft;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            var r = t.GetComponent<RectTransform>();
            r.anchorMin = new Vector2(0, 1);
            r.anchorMax = new Vector2(0, 1);
            r.pivot = new Vector2(0, 1);
            r.anchoredPosition = new Vector2(x, y);
            r.sizeDelta = new Vector2(width, 14f);
            t.raycastTarget = false;
            return MeasureWrappedHeight(t, width, fontSize + 5f);
        }

        /// <summary>实心大按钮（返回条/页签/通栏操作——显眼：底色对比+金字）。</summary>
        private void CreateBigButton(Transform card, string label, float x, float y, float w, float h,
            Color bg, Color fg, Action<Text> onClick)
        {
            GameObject obj = new GameObject("BigBtn", typeof(RectTransform), typeof(Image), typeof(Button));
            obj.transform.SetParent(card, false);
            var rt = obj.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(w, h);
            var img = obj.GetComponent<Image>();
            img.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            img.type = Image.Type.Sliced;
            img.color = bg;
            img.raycastTarget = true;
            var btn = obj.GetComponent<Button>();
            btn.targetGraphic = img;
            Text lblRef = null;
            btn.onClick.AddListener(() => { try { onClick(lblRef); } catch { } });

            Text t = CreateText(obj.transform, label, 10);
            t.alignment = TextAnchor.MiddleCenter;
            t.color = fg;
            var tr = t.GetComponent<RectTransform>();
            tr.anchorMin = Vector2.zero;
            tr.anchorMax = Vector2.one;
            tr.offsetMin = Vector2.zero;
            tr.offsetMax = Vector2.zero;
            t.raycastTarget = false;
            lblRef = t;
        }

        // ═══════════════ 断代史卡（默认折叠；展开态留足 padding 防重叠）═══════════════

        private void BuildChronicleCards()
        {
            var all = LlmChronicleStore.Instance.All();
            if (all.Count == 0) return;
            for (int i = all.Count - 1; i >= 0; i--)   // 最新在上
            {
                var c = all[i];
                if (c == null) continue;
                try { BuildChronicleCard(c); } catch { }
            }
        }

        private void BuildChronicleCard(LlmChronicleEntry c)
        {
            string key = LlmChronicleStore.BuildKey(c.SpanStart, c.SpanEnd);
            bool expanded = _chronExpanded.Contains(key);
            bool fromLlm = c.Source == "llm";

            GameObject row = new GameObject("Chronicle", typeof(RectTransform));
            row.transform.SetParent(_historyList, false);
            var rowRect = row.GetComponent<RectTransform>();

            GameObject bgObj = new GameObject("Bg", typeof(RectTransform), typeof(Image));
            bgObj.transform.SetParent(row.transform, false);
            var bgRect = bgObj.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = new Vector2(-36f, 0f);
            bgRect.offsetMax = Vector2.zero;
            var bgImg = bgObj.GetComponent<Image>();
            bgImg.color = new Color(0.19f, 0.16f, 0.10f, 0.95f);   // 暗金底（史论卡）
            bgImg.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            bgImg.type = Image.Type.Sliced;
            bgImg.raycastTarget = false;

            float h;
            if (!expanded)
            {
                // 折叠态：一行窄条
                Text line = CreateText(row.transform,
                    $"<color={ZhuluUIStyle.Hex.GoldDim}>◆ 史论 · {c.SpanStart}~{c.SpanEnd}</color>" +
                    (fromLlm ? $" <color={ZhuluUIStyle.Hex.Jade}>●史官评</color>" : "") +
                    $" <color={ZhuluUIStyle.TextColor.Tertiary}>▸ 点击展开</color>", 9);
                line.alignment = TextAnchor.MiddleLeft;
                var lr = line.GetComponent<RectTransform>();
                lr.anchorMin = new Vector2(0, 1);
                lr.anchorMax = new Vector2(0, 1);
                lr.pivot = new Vector2(0, 1);
                lr.anchoredPosition = new Vector2(8, -4);
                lr.sizeDelta = new Vector2(_innerWidth - 16, 14f);
                line.raycastTarget = false;
                h = 22f;
            }
            else
            {
                // 展开态（V2-7 修复：head 区 22px + 正文行高 16 + 底部 10px——原版 padding 不足文字溢出到下一卡）
                Text head = CreateText(row.transform,
                    $"<color={ZhuluUIStyle.Hex.Gold}>史 论</color>" +
                    $" <color={ZhuluUIStyle.TextColor.Tertiary}>{c.SpanStart}~{c.SpanEnd} · 点击收起</color>", 10);
                head.alignment = TextAnchor.MiddleLeft;
                var hr2 = head.GetComponent<RectTransform>();
                hr2.anchorMin = new Vector2(0, 1);
                hr2.anchorMax = new Vector2(0, 1);
                hr2.pivot = new Vector2(0, 1);
                hr2.anchoredPosition = new Vector2(10, -4);
                hr2.sizeDelta = new Vector2(_innerWidth - 20, 16f);
                head.raycastTarget = false;

                Text body = CreateText(row.transform,
                    $"<color={ZhuluUIStyle.TextColor.Primary}>{c.Digest}</color>", 10);
                body.alignment = TextAnchor.UpperLeft;   // V2-10：同因——多行居中上溢压住史论标题
                body.horizontalOverflow = HorizontalWrapMode.Wrap;
                body.verticalOverflow = VerticalWrapMode.Overflow;
                var br = body.GetComponent<RectTransform>();
                br.anchorMin = new Vector2(0, 1);
                br.anchorMax = new Vector2(0, 1);
                br.pivot = new Vector2(0, 1);
                br.anchoredPosition = new Vector2(10, -28);
                br.sizeDelta = new Vector2(_innerWidth - 20, 14f);
                body.raycastTarget = false;
                float bodyH = MeasureWrappedHeight(body, _innerWidth - 20, 16f);
                h = 28f + bodyH + 10f;
            }
            rowRect.sizeDelta = new Vector2(_innerWidth, h);

            // 整卡可点：展开/收起
            GameObject click = new GameObject("Click", typeof(RectTransform), typeof(Image), typeof(Button));
            click.transform.SetParent(row.transform, false);
            var cr = click.GetComponent<RectTransform>();
            cr.anchorMin = Vector2.zero;
            cr.anchorMax = Vector2.one;
            cr.offsetMin = Vector2.zero;
            cr.offsetMax = Vector2.zero;
            var cImg = click.GetComponent<Image>();
            cImg.color = new Color(1f, 1f, 1f, 0.005f);
            cImg.raycastTarget = true;
            var cBtn = click.GetComponent<Button>();
            cBtn.targetGraphic = cImg;
            string k = key;
            cBtn.onClick.AddListener(() =>
            {
                try
                {
                    if (_chronExpanded.Contains(k)) _chronExpanded.Remove(k);
                    else _chronExpanded.Add(k);
                    RefreshHistoryPanel();
                }
                catch { }
            });
        }

        // ═══════════════ 帝王详情页（分区卡片制）═══════════════

        private void BuildEmperorDetailCard()
        {
            var d = _detailDynasty;
            var e = _detailEmperor;
            if (d == null || e == null) { CloseEmperorDetail(); return; }

            // ── 卡0：返回条（V2-8：通栏大按钮，一眼可见）──
            var back = CreateDetailCard("DetailBack", 26f);
            CreateBigButton(back, "‹ 返回列表（历代天命）", 8, -4, _innerWidth - 16, 18f,
                new Color(0.30f, 0.26f, 0.18f, 0.95f), new Color(0.98f, 0.77f, 0.27f),
                _ => CloseEmperorDetail());

            // ── 卡1：帝号 ──
            BuildDetailHeaderCard(d, e);

            // ── 卡2：评语（实录/野史大页签）──
            BuildDetailVerdictCard(d, e);

            // ── 卡3：轨迹图表 ──
            BuildDetailTrackCard(e);

            // ── 卡4：轨迹明细 ──
            BuildDetailTableCard(e);

            // ── 卡5：功过书 + 重新生成 ──
            BuildDetailDeedsCard(d, e);
        }

        private void BuildDetailHeaderCard(DynastyRecord d, EmperorRecord e)
        {
            bool closed = e.EndYear > 0;
            string shi = e.PosthumousName ?? "";
            string temple = e.TempleName ?? "";
            string dihao;
            if (!closed) dihao = "◆（今上）";
            else if (!string.IsNullOrEmpty(temple) && !string.IsNullOrEmpty(shi))
                dihao = $"◆{temple}{shi}皇帝";
            else if (!string.IsNullOrEmpty(shi))
                dihao = $"◆{shi}帝 <color={ZhuluUIStyle.TextColor.Tertiary}>（不祔庙）</color>";
            else dihao = $"◆ <color={ZhuluUIStyle.TextColor.Tertiary}>（未定谥）</color>";
            string badges = "";
            if (e.Unified == 1) badges += $" <color={ZhuluUIStyle.Hex.Imperial}>☀大一统</color>";
            if (e.SlewHolder == 1) badges += $" <color={ZhuluUIStyle.Hex.Imperial}>⚔破敌天命</color>";

            int reignYears = closed ? e.EndYear - e.StartYear : Mathf.Max(0, ZhuluTime.CurrentYear - e.StartYear);
            string ageText = e.EndAge > 0 ? $" · 寿{PosthumousNameGenerator.ToChinese(e.EndAge)}" : "";
            string info = $"<color={ZhuluUIStyle.TextColor.Secondary}>{e.Name} · 年号{e.Era} · {d.Dynasty}朝第{_detailIdx}帝 · " +
                $"在位 {e.StartYear}~{(closed ? e.EndYear.ToString() : "今")}（{PosthumousNameGenerator.ToChinese(reignYears)}年）{ageText}</color>";

            // 先建卡再按两行实测高度回写卡高（首版固定行高在富文本换行时低估 → 重叠根因之一）
            float w = _innerWidth - 20;
            var card = CreateDetailCard("DetailHeader", 10f);
            float h1 = DetailText(card, $"<color={ZhuluUIStyle.Hex.Imperial}><b>{dihao}</b></color>{badges}", 12, 10, -8, w);
            float h2 = DetailText(card, info, 9, 10, -8 - h1 - 2, w);
            SetCardHeight(card, 8 + h1 + 2 + h2 + 10);
        }

        private void BuildDetailVerdictCard(DynastyRecord d, EmperorRecord e)
        {
            bool closed = e.EndYear > 0;
            bool lastFell = closed && _detailIdx == d.Emperors.Count
                && d.EndYear != -1 && d.EndReason != "active";
            var ai = LlmCommentStore.Instance.Get(AiKey(d, e, _detailIdx));

            string verdictBody;
            string verdictSrc;
            if (_detailTab == 0)
            {
                if (ai != null && !string.IsNullOrEmpty(ai.Comment))
                {
                    verdictBody = ai.Comment;
                    verdictSrc = $"<color={ZhuluUIStyle.Hex.Jade}>● 史官评（AI 生成 · 全文 {ai.Comment.Length} 字）</color>";
                }
                else
                {
                    var (_, _, v) = PosthumousNameGenerator.BuildVerdict(e, lastFell);
                    verdictBody = v;
                    verdictSrc = $"<color={ZhuluUIStyle.TextColor.Tertiary}>◇ 本地模板（无 AI 评语）</color>";
                }
            }
            else
            {
                if (ai != null && !string.IsNullOrEmpty(ai.WildComment))
                {
                    verdictBody = ai.WildComment;
                    verdictSrc = $"<color={ZhuluUIStyle.TextColor.Secondary}>◇ 野史 · 里巷之说</color>";
                }
                else
                {
                    verdictBody = "野史未修。";
                    verdictSrc = $"<color={ZhuluUIStyle.TextColor.Tertiary}>◇ 野史未修——点下方「重新生成」将一并补写</color>";
                }
            }

            float w = _innerWidth - 20;
            var card = CreateDetailCard("DetailVerdict", 10f);

            // 卡头：实录/野史大页签（V2-8：等宽 20px 高，激活金底/非激活墨底）
            float tabW = (w - 6f) / 2f;
            CreateBigButton(card, _detailTab == 0 ? "◆ 实 录" : "实 录", 10, -6, tabW, 20f,
                _detailTab == 0
                    ? new Color(0.55f, 0.42f, 0.16f, 0.95f)
                    : new Color(0.16f, 0.15f, 0.13f, 0.9f),
                _detailTab == 0 ? new Color(0.13f, 0.11f, 0.06f) : new Color(0.6f, 0.57f, 0.5f),
                _ => { _detailTab = 0; RefreshHistoryPanel(); });
            CreateBigButton(card, _detailTab == 1 ? "◆ 野 史" : "野 史", 10 + tabW + 6f, -6, tabW, 20f,
                _detailTab == 1
                    ? new Color(0.42f, 0.36f, 0.20f, 0.95f)
                    : new Color(0.16f, 0.15f, 0.13f, 0.9f),
                _detailTab == 1 ? new Color(0.13f, 0.11f, 0.06f) : new Color(0.6f, 0.57f, 0.5f),
                _ => { _detailTab = 1; RefreshHistoryPanel(); });

            float y = -32f;
            float hSrc = DetailText(card, verdictSrc, 8, 10, y, w);
            y -= hSrc + 3f;
            Text body = CreateText(card, $"<color={ZhuluUIStyle.Hex.GoldDim}>「{verdictBody}」</color>", 10);
            body.alignment = TextAnchor.UpperLeft;   // V2-10：MiddleLeft 多行居中上溢会压住源标记行
            body.horizontalOverflow = HorizontalWrapMode.Wrap;
            body.verticalOverflow = VerticalWrapMode.Overflow;
            var br = body.GetComponent<RectTransform>();
            br.anchorMin = new Vector2(0, 1);
            br.anchorMax = new Vector2(0, 1);
            br.pivot = new Vector2(0, 1);
            br.anchoredPosition = new Vector2(10, y);
            br.sizeDelta = new Vector2(w, 14f);
            body.raycastTarget = false;
            float hBody = MeasureWrappedHeight(body, w, 16f);
            y -= hBody + 4f;

            if (ai != null && !string.IsNullOrEmpty(ai.ShiftReason))
            {
                float hShift = DetailText(card,
                    $"<color={ZhuluUIStyle.TextColor.Tertiary}>史官移评 {ShiftText(ai.Shift)}：{ai.ShiftReason}</color>",
                    8, 10, y, w);
                y -= hShift + 4f;
            }
            SetCardHeight(card, -y + 6f);
        }

        private static string ShiftText(int shift)
        {
            if (shift > 0) return "+" + shift;
            if (shift < 0) return shift.ToString();
            return "0";
        }

        /// <summary>轨迹图表卡：固定 72px 柱区 + 极盛行——独立成卡，不再与文字共享 y 累计。</summary>
        private void BuildDetailTrackCard(EmperorRecord e)
        {
            var track = e.Track;
            float w = _innerWidth - 20;

            if (track == null || track.Count < 2)
            {
                var card0 = CreateDetailCard("DetailTrack", 34f);
                float h0 = DetailText(card0,
                    $"<color={ZhuluUIStyle.Hex.GoldDim}>◈ 在位国力轨迹</color>", 9, 10, -6, w);
                DetailText(card0,
                    $"<color={ZhuluUIStyle.TextColor.Tertiary}>轨迹未采（旧档或采样已关闭）——评语依据首尾快照。</color>",
                    8, 10, -6 - h0 - 2, w);
                SetCardHeight(card0, 6 + h0 + 2 + 13f + 8f);
                return;
            }

            var samples = ThinTrack(track, 40);
            int peakIdx;
            float[] bars = NormalizeBars(samples, out peakIdx);

            var card = CreateDetailCard("DetailTrack", 10f);
            float hHead = DetailText(card,
                $"<color={ZhuluUIStyle.Hex.GoldDim}>◈ 在位国力轨迹</color>" +
                $" <color={ZhuluUIStyle.TextColor.Tertiary}>（柱高=国力点 · 金柱=极盛）</color>", 9, 10, -6, w);

            float chartTop = -6 - hHead - 4f;
            float chartH = 72f;
            float baseLine = chartTop - chartH;   // V2-10：柱子底对齐基线（原顶对齐向下挂=视觉颠倒）
            float barW = Mathf.Max(2.5f, w / samples.Count - 1.5f);
            for (int i = 0; i < samples.Count; i++)
            {
                GameObject bar = new GameObject("Bar", typeof(RectTransform), typeof(Image));
                bar.transform.SetParent(card, false);
                var bRect = bar.GetComponent<RectTransform>();
                bRect.anchorMin = new Vector2(0, 1);
                bRect.anchorMax = new Vector2(0, 1);
                bRect.pivot = new Vector2(0, 0);          // 左下角锚定：从基线向上长
                bRect.anchoredPosition = new Vector2(10 + i * (w / samples.Count), baseLine);
                bRect.sizeDelta = new Vector2(barW, 4f + (chartH - 8f) * bars[i]);
                var bi = bar.GetComponent<Image>();
                bi.color = i == peakIdx
                    ? new Color(0.98f, 0.77f, 0.27f, 0.95f)     // 金柱（极盛）
                    : new Color(0.60f, 0.57f, 0.49f, 0.55f);     // 宣纸灰柱
                bi.raycastTarget = false;
            }

            float capY = chartTop - chartH - 3f;
            int peakYear = e.PeakYear > 0 ? e.PeakYear
                : (peakIdx >= 0 && peakIdx < samples.Count ? samples[peakIdx].Year : 0);
            float hCap = DetailText(card,
                $"<color={ZhuluUIStyle.TextColor.Tertiary}>{track[0].Year} … {track[track.Count - 1].Year}年</color>" +
                (peakYear > 0 ? $"　<color={ZhuluUIStyle.Hex.Gold}>▲ 极盛 {peakYear} 年</color>" : ""),
                8, 10, capY, w);
            SetCardHeight(card, 6 + hHead + 4 + chartH + 3 + hCap + 8);
        }

        /// <summary>明细表卡：8 列定宽（392px 内宽实测分配），全部采样年份，极盛行金色。</summary>
        private void BuildDetailTableCard(EmperorRecord e)
        {
            float w = _innerWidth - 20;
            var track = e.Track;

            if (track == null || track.Count == 0)
            {
                var card0 = CreateDetailCard("DetailTable", 34f);
                float h0 = DetailText(card0,
                    $"<color={ZhuluUIStyle.Hex.GoldDim}>◈ 轨迹明细</color>", 9, 10, -6, w);
                DetailText(card0,
                    $"<color={ZhuluUIStyle.TextColor.Tertiary}>无采样数据。</color>", 8, 10, -6 - h0 - 2, w);
                SetCardHeight(card0, 6 + h0 + 2 + 13f + 8f);
                return;
            }

            var card = CreateDetailCard("DetailTable", 10f);
            float hHead = DetailText(card,
                $"<color={ZhuluUIStyle.Hex.GoldDim}>◈ 轨迹明细</color>" +
                $" <color={ZhuluUIStyle.TextColor.Tertiary}>（{track.Count} 个采样年 · 极盛行金色）</color>", 9, 10, -6, w);

            float y = -6 - hHead - 4f;
            float[] xs = { 10, 54, 86, 130, 182, 226, 266, 304 };
            float[] cw = { 44, 32, 44, 52, 44, 40, 38, 52 };
            string[] cols = { "年", "城", "民", "库", "天命", "稳", "附庸", "排名" };
            for (int c = 0; c < 8; c++)
            {
                Text hc = CreateText(card, $"<color={ZhuluUIStyle.TextColor.Tertiary}>{cols[c]}</color>", 8);
                hc.alignment = c == 0 ? TextAnchor.MiddleLeft : TextAnchor.MiddleRight;
                PlaceCell(hc, xs[c], y, cw[c]);
            }
            y -= 13f;

            foreach (var s in track)
            {
                if (s == null) continue;
                bool isPeak = s.Year == e.PeakYear;
                string color = isPeak ? ZhuluUIStyle.Hex.Gold : ZhuluUIStyle.TextColor.Secondary;
                string[] vals =
                {
                    s.Year + (isPeak ? " ◆" : ""),
                    s.Cities.ToString(),
                    s.Population.ToString(),
                    s.Treasury.ToString(),
                    s.Mandate.ToString("F0"),
                    s.Stability.ToString("F0"),
                    s.Vassals.ToString(),
                    s.Rank > 0 ? s.Rank.ToString() : "-"
                };
                for (int c = 0; c < 8; c++)
                {
                    Text cell = CreateText(card, $"<color={color}>{vals[c]}</color>", 8);
                    cell.alignment = c == 0 ? TextAnchor.MiddleLeft : TextAnchor.MiddleRight;
                    PlaceCell(cell, xs[c], y, cw[c]);
                }
                y -= 12f;
            }
            SetCardHeight(card, -y + 8f);
        }

        private static void PlaceCell(Text cell, float x, float y, float w)
        {
            var r = cell.GetComponent<RectTransform>();
            r.anchorMin = new Vector2(0, 1);
            r.anchorMax = new Vector2(0, 1);
            r.pivot = new Vector2(0, 1);
            r.anchoredPosition = new Vector2(x, y);
            r.sizeDelta = new Vector2(w, 12f);
            cell.raycastTarget = false;
        }

        /// <summary>功过书 + 重新生成（通栏按钮）。</summary>
        private void BuildDetailDeedsCard(DynastyRecord d, EmperorRecord e)
        {
            float w = _innerWidth - 20;
            var card = CreateDetailCard("DetailDeeds", 10f);
            float h1 = DetailText(card, BuildDeedsLine(e), 8, 10, -6, w);
            float y = -6 - h1 - 6f;

            CreateBigButton(card, "重新生成（长评 + 野史）", 10, y, w, 18f,
                new Color(0.30f, 0.26f, 0.18f, 0.95f), new Color(0.98f, 0.77f, 0.27f), lbl =>
                {
                    try
                    {
                        bool ok = EnqueueOne(d, e, _detailIdx);
                        ZhuluContext.Instance.LogRateLimited("AiReevalDetail",
                            "[Llm] 详情页重评 " + e.Name + "：" + (ok ? "已入队" :
                            LlmEmperorService.Instance.LastRejectReason), 2f);
                    }
                    catch { }
                });
            SetCardHeight(card, 6 + h1 + 6 + 18 + 8);
        }

        /// <summary>把分区卡高度回写（文本实测后再定卡高）。</summary>
        private static void SetCardHeight(RectTransform card, float h)
        {
            if (card != null) card.sizeDelta = new Vector2(card.sizeDelta.x, Mathf.Max(20f, h));
        }
    }
}
