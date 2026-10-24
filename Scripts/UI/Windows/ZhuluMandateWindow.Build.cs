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
    /// 天命窗口 v2（分层②构建）：主面板 / 历史面板 / 朝代卡片 / 按钮工厂 / 进度条等 UI 结构创建。
    /// 与核心文件同属 partial 类（ZhuluMandateWindow）。
    /// </summary>
    public partial class ZhuluMandateWindow : MonoBehaviour
    {
        // ═══════════════════════ 结构构建（分层②）═══════════════════════


        private void CreateMainPanel()
        {
            // 绝对定位布局（不依赖 VLG 子布局——之前 VLG 布局导致核心卡内容"漂浮"错位）
            _mainPanel = new GameObject("MainPanel", typeof(RectTransform));
            _mainPanel.transform.SetParent(_contentTransform, false);
            var mainRect = _mainPanel.GetComponent<RectTransform>();
            mainRect.sizeDelta = new Vector2(_innerWidth, 354f);   // 2026-08-20 312→340 问鼎者行；2026-08-19 340→354 天人感应明细行

            float barWidth = _innerWidth - 84f;   // 进度条宽度（右侧留 74px 给 ±按钮）
            float startX = 10f;

            // ── 天命核心卡（淡青玉九宫格面板）──
            GameObject core = new GameObject("CoreCard", typeof(RectTransform), typeof(Image));
            core.transform.SetParent(_mainPanel.transform, false);
            var coreRect = core.GetComponent<RectTransform>();
            coreRect.anchorMin = new Vector2(0.5f, 1f);
            coreRect.anchorMax = new Vector2(0.5f, 1f);
            coreRect.pivot = new Vector2(0.5f, 1f);
            coreRect.anchoredPosition = new Vector2(0, -2);
            coreRect.sizeDelta = new Vector2(_innerWidth, 66f);
            var coreImg = core.GetComponent<Image>();
            coreImg.color = Color.white;
            SetSlicedSprite(coreImg, "ui/zhulu/panels/zlr_panel_jade_9s");

            // 天命玉佩图标（徽章左侧）
            GameObject iconObj = new GameObject("MandateIcon", typeof(RectTransform), typeof(Image));
            iconObj.transform.SetParent(core.transform, false);
            _mandateIcon = iconObj.GetComponent<Image>();
            var iconRect = _mandateIcon.GetComponent<RectTransform>();
            iconRect.anchorMin = new Vector2(1, 1);
            iconRect.anchorMax = new Vector2(1, 1);
            iconRect.pivot = new Vector2(1, 1);
            iconRect.anchoredPosition = new Vector2(-118, -13);
            iconRect.sizeDelta = new Vector2(24, 24);
            _mandateIcon.raycastTarget = false;

            // 大数字（顶部左侧）
            _mainValueText = CreateText(core.transform, "—", 30);
            _mainValueText.alignment = TextAnchor.MiddleLeft;
            _mainValueText.color = new Color(1f, 0.84f, 0f);
            var valRect = _mainValueText.GetComponent<RectTransform>();
            valRect.anchorMin = new Vector2(0, 1);
            valRect.anchorMax = new Vector2(0, 1);
            valRect.pivot = new Vector2(0, 1);
            valRect.anchoredPosition = new Vector2(10, -8);
            valRect.sizeDelta = new Vector2(90f, 30f);

            // 等级徽章（顶部右侧）
            _mainGradeText = CreateText(core.transform, "天命—", 10);
            _mainGradeText.alignment = TextAnchor.MiddleCenter;
            var gradeRect = _mainGradeText.GetComponent<RectTransform>();
            gradeRect.anchorMin = new Vector2(1, 1);
            gradeRect.anchorMax = new Vector2(1, 1);
            gradeRect.pivot = new Vector2(1, 1);
            gradeRect.anchoredPosition = new Vector2(-10, -10);
            gradeRect.sizeDelta = new Vector2(100f, 20f);

            // 5 段进度条（红/橙/金/绿/青，值覆盖段亮、超出段暗）——第二行
            _barSegments = new Image[5];
            float[] segW = new float[] { 12f, 16f, 22f, 22f, 28f };   // 总 100%
            Color[] segColor = new Color[]
            {
                new Color(1f, 0.2f, 0.2f), new Color(1f, 0.55f, 0.1f), new Color(1f, 0.84f, 0f),
                new Color(0.62f, 0.9f, 0.29f), new Color(0.18f, 0.77f, 0.42f)
            };
            float segStart = startX;
            for (int i = 0; i < 5; i++)
            {
                GameObject seg = new GameObject($"Seg{i}", typeof(RectTransform), typeof(Image));
                seg.transform.SetParent(core.transform, false);
                var segRect = seg.GetComponent<RectTransform>();
                segRect.anchorMin = new Vector2(0, 1);
                segRect.anchorMax = new Vector2(0, 1);
                segRect.pivot = new Vector2(0, 1);
                segRect.anchoredPosition = new Vector2(segStart, -42);
                segRect.sizeDelta = new Vector2(barWidth * segW[i] / 100f, 8f);
                segStart += barWidth * segW[i] / 100f;
                var segImg = seg.GetComponent<Image>();
                segImg.color = segColor[i];
                segImg.raycastTarget = false;
                _barSegments[i] = segImg;
            }

            // 刻度文字（7 个独立 Text，按段边界精确定位——第三行）
            string[] tickLabels = new string[] { "0", "欲绝", "将尽", "衰微", "平稳", "昌盛", "隆盛" };
            float[] tickPcts = new float[] { 0f, 6f, 20f, 39f, 61f, 86f, 100f };   // 各段中心 + 端点
            for (int i = 0; i < tickLabels.Length; i++)
            {
                Text tick = CreateText(core.transform, tickLabels[i], 7);
                tick.alignment = TextAnchor.MiddleCenter;
                tick.color = new Color(0.55f, 0.5f, 0.4f);
                var tickRect = tick.GetComponent<RectTransform>();
                tickRect.anchorMin = new Vector2(0, 1);
                tickRect.anchorMax = new Vector2(0, 1);
                tickRect.pivot = new Vector2(0.5f, 1);
                tickRect.anchoredPosition = new Vector2(startX + barWidth * tickPcts[i] / 100f, -56);
                tickRect.sizeDelta = new Vector2(24f, 10f);
                tick.raycastTarget = false;
            }

            // ± 调整按钮（进度条右侧，小巧美观：深底金边 + 金色符号）
            CreateTinyButton(core.transform, "+", new Vector2(-38, -42), () => AdjustMandate(5));
            CreateTinyButton(core.transform, "−", new Vector2(-14, -42), () => AdjustMandate(-5));

            // ── 云纹分隔线 ──
            GameObject divider = new GameObject("Divider", typeof(RectTransform), typeof(Image));
            divider.transform.SetParent(_mainPanel.transform, false);
            var dRect = divider.GetComponent<RectTransform>();
            dRect.anchorMin = new Vector2(0.5f, 1f);
            dRect.anchorMax = new Vector2(0.5f, 1f);
            dRect.pivot = new Vector2(0.5f, 1f);
            dRect.anchoredPosition = new Vector2(0, -72);
            dRect.sizeDelta = new Vector2(_innerWidth - 20f, 8f);
            SetSlicedSprite(divider.GetComponent<Image>(), "ui/zhulu/decorations/zlr_divider_cloud_64x8");
            divider.GetComponent<Image>().raycastTarget = false;

            // ── 信息行（富文本）──
            _mainInfoText = CreateText(_mainPanel.transform, "", 10);
            _mainInfoText.alignment = TextAnchor.UpperLeft;
            _mainInfoText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _mainInfoText.verticalOverflow = VerticalWrapMode.Truncate;
            var infoRect = _mainInfoText.GetComponent<RectTransform>();
            infoRect.anchorMin = new Vector2(0.5f, 1f);
            infoRect.anchorMax = new Vector2(0.5f, 1f);
            infoRect.pivot = new Vector2(0.5f, 1f);
            infoRect.anchoredPosition = new Vector2(0, -84);
            infoRect.sizeDelta = new Vector2(_innerWidth - 20f, 124f); // 阶段2V 96→110 大一统行；2026-08-19 110→124 天人感应明细行

            // ── 效果条 ──
            _mainEffectText = CreateText(_mainPanel.transform, "", 10);
            _mainEffectText.alignment = TextAnchor.MiddleLeft;
            _mainEffectText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _mainEffectText.verticalOverflow = VerticalWrapMode.Truncate;
            var effRect = _mainEffectText.GetComponent<RectTransform>();
            effRect.anchorMin = new Vector2(0.5f, 1f);
            effRect.anchorMax = new Vector2(0.5f, 1f);
            effRect.pivot = new Vector2(0.5f, 1f);
            effRect.anchoredPosition = new Vector2(0, -212);
            effRect.sizeDelta = new Vector2(_innerWidth - 20f, 22f);

            // 阶段3G：近期天命块已删（用户要求）——_mainEventText 不再创建，Data 侧空守卫

            // ── 易主预警条（默认隐藏，天命<30 时显示）──
            // ── 问鼎者行（2026-08-20：可与当前朝代争天命的强国——夺天命之战资格可视化）──
            _challengerText = CreateText(_mainPanel.transform, "", 9);
            _challengerText.alignment = TextAnchor.UpperLeft;
            _challengerText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _challengerText.verticalOverflow = VerticalWrapMode.Truncate;
            var chRect = _challengerText.GetComponent<RectTransform>();
            chRect.anchorMin = new Vector2(0.5f, 1f);
            chRect.anchorMax = new Vector2(0.5f, 1f);
            chRect.pivot = new Vector2(0.5f, 1f);
            chRect.anchoredPosition = new Vector2(0, -238); // 阶段3H2：上移补近期天命空缺
            chRect.sizeDelta = new Vector2(_innerWidth - 20f, 26f);

            _warnBar = new GameObject("WarnBar", typeof(RectTransform), typeof(Image));
            _warnBar.transform.SetParent(_mainPanel.transform, false);
            var warnRect = _warnBar.GetComponent<RectTransform>();
            warnRect.anchorMin = new Vector2(0.5f, 1f);
            warnRect.anchorMax = new Vector2(0.5f, 1f);
            warnRect.pivot = new Vector2(0.5f, 1f);
            warnRect.anchoredPosition = new Vector2(0, -268);
            warnRect.sizeDelta = new Vector2(_innerWidth - 20f, 16f);
            var warnImg = _warnBar.GetComponent<Image>();
            warnImg.color = new Color(0.55f, 0.1f, 0.1f, 0.35f);
            warnImg.raycastTarget = false;
            _warnText = CreateText(_warnBar.transform, "", 10);
            _warnText.alignment = TextAnchor.MiddleCenter;
            _warnText.color = new Color(1f, 0.5f, 0.5f);
            var wtRect = _warnText.GetComponent<RectTransform>();
            wtRect.anchorMin = Vector2.zero;
            wtRect.anchorMax = Vector2.one;
            wtRect.sizeDelta = Vector2.zero;
            _warnText.raycastTarget = false;
            _warnBar.SetActive(false);

            // ── 按钮行 ──
            CreateButton(_mainPanel.transform, "历代天命/天眷", new Color(0.55f, 0.42f, 0.06f, 1f), new Color(1f, 0.93f, 0.55f), new Vector2(-70f, -292f), ShowHistory); // 阶段3H2：随空缺上移
            CreateButton(_mainPanel.transform, "查看国势", new Color(0.16f, 0.18f, 0.22f, 1f), new Color(0.8f, 0.8f, 0.8f), new Vector2(70f, -292f), OnOpenKingdomState);
        }

        /// <summary>创建小巧的调整按钮（深底金边 + 金色符号）</summary>
        private void CreateTinyButton(Transform parent, string label, Vector2 pos, Action onClick)
        {
            GameObject btnObj = new GameObject($"Tiny_{label}", typeof(RectTransform), typeof(Image), typeof(Button));
            btnObj.transform.SetParent(parent, false);
            var btnRect = btnObj.GetComponent<RectTransform>();
            btnRect.anchorMin = new Vector2(1, 1);
            btnRect.anchorMax = new Vector2(1, 1);
            btnRect.pivot = new Vector2(1, 1);
            btnRect.anchoredPosition = pos;
            btnRect.sizeDelta = new Vector2(20f, 16f);
            var img = btnObj.GetComponent<Image>();
            SetSlicedSprite(img, "ui/special/backgroundKingdomElement");
            img.color = new Color(0.15f, 0.13f, 0.1f, 0.9f);
            var btn = btnObj.GetComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(() => onClick());

            Text t = CreateText(btnObj.transform, label, 10);
            t.alignment = TextAnchor.MiddleCenter;
            t.color = new Color(1f, 0.84f, 0f);
            var tRect = t.GetComponent<RectTransform>();
            tRect.anchorMin = Vector2.zero;
            tRect.anchorMax = Vector2.one;
            tRect.sizeDelta = Vector2.zero;
            t.raycastTarget = false;
        }

        /// <summary>创建通用按钮（pos=锚 (0.5,1) 下的位置）</summary>
        private void CreateButton(Transform parent, string label, Color bgColor, Color textColor, Vector2 pos, Action onClick)
        {
            GameObject btnObj = new GameObject($"Btn_{label}", typeof(RectTransform), typeof(Image), typeof(Button));
            btnObj.transform.SetParent(parent, false);
            var btnRect = btnObj.GetComponent<RectTransform>();
            btnRect.anchorMin = new Vector2(0.5f, 1f);
            btnRect.anchorMax = new Vector2(0.5f, 1f);
            btnRect.pivot = new Vector2(0.5f, 0.5f);
            btnRect.anchoredPosition = pos;
            btnRect.sizeDelta = new Vector2(118f, 24f);
            var img = btnObj.GetComponent<Image>();
            img.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            img.type = Image.Type.Sliced;
            img.color = bgColor;
            var btn = btnObj.GetComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(() => onClick());

            Text t = CreateText(btnObj.transform, label, 10);
            t.alignment = TextAnchor.MiddleCenter;
            t.color = textColor;
            var tRect = t.GetComponent<RectTransform>();
            tRect.anchorMin = Vector2.zero;
            tRect.anchorMax = Vector2.one;
            tRect.sizeDelta = Vector2.zero;
            t.raycastTarget = false;
        }

        // ═══════════════════════ 历史面板 ═══════════════════════

        private void CreateHistoryPanel()
        {
            // 历史面板：顶部返回按钮 + 朝代卡片列表（VLG）
            _historyPanel = new GameObject("HistoryPanel", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            _historyPanel.transform.SetParent(_contentTransform, false);
            _historyPanel.GetComponent<RectTransform>().sizeDelta = new Vector2(_innerWidth, 0f);
            var histV = _historyPanel.GetComponent<VerticalLayoutGroup>();
            histV.childAlignment = TextAnchor.UpperCenter;
            histV.spacing = 6f;
            histV.padding = new RectOffset(0, 0, 4, 4);
            histV.childControlWidth = false;
            histV.childControlHeight = false;
            histV.childForceExpandWidth = false;
            histV.childForceExpandHeight = false;
            var histFitter = _historyPanel.GetComponent<ContentSizeFitter>();
            histFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // 返回按钮（历史面板顶部）
            CreateButton(_historyPanel.transform, "← 返回主面板", new Color(0.3f, 0.24f, 0.18f, 1f), new Color(1f, 0.75f, 0.6f), new Vector2(0f, 0f), ShowMain);
            // 调整返回按钮位置（VLG 内居中）
            var backRect = _historyPanel.transform.Find("Btn_← 返回主面板");
            if (backRect != null)
            {
                var rt = backRect.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = Vector2.zero;
                rt.sizeDelta = new Vector2(118f, 24f);
            }

            // 传承统计条
            _historyStatText = CreateText(_historyPanel.transform, "", 9);
            _historyStatText.alignment = TextAnchor.MiddleCenter;
            _historyStatText.color = new Color(0.85f, 0.8f, 0.7f);
            var statRect = _historyStatText.GetComponent<RectTransform>();
            statRect.sizeDelta = new Vector2(_innerWidth, 16f);

            _historyList = new GameObject("DynastyList", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter)).transform;
            _historyList.SetParent(_historyPanel.transform, false);
            var listRect = _historyList.GetComponent<RectTransform>();
            listRect.sizeDelta = new Vector2(_innerWidth, 0f);
            var listV = _historyList.GetComponent<VerticalLayoutGroup>();
            listV.childAlignment = TextAnchor.UpperCenter;
            listV.spacing = 6f;
            listV.childControlWidth = false;
            listV.childControlHeight = false;
            listV.childForceExpandWidth = false;
            listV.childForceExpandHeight = false;
            // 关键：DynastyList 必须挂 ContentSizeFitter 撑起实际高度——
            // 否则卡片高度不传播，HistoryPanel→Content 高度恒 ~50px，ScrollRect 无内容可滚（历代天命不能滚动）
            var listFitter = _historyList.GetComponent<ContentSizeFitter>();
            listFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _historyEmptyText = CreateText(_historyPanel.transform, "尚无天命或天眷传承记录。", 10);
            _historyEmptyText.alignment = TextAnchor.MiddleCenter;
            _historyEmptyText.color = new Color(0.6f, 0.58f, 0.52f);
            var emptyRect = _historyEmptyText.GetComponent<RectTransform>();
            emptyRect.sizeDelta = new Vector2(_innerWidth, 80f);

            _historyPanel.SetActive(false);
        }

        /// <summary>构建朝代卡片（Row容器：背景Image全屏填满 + 文字内缩8px——Image和Text同父级绝对对齐）</summary>
        // ── 2026-08-19 串联：前朝默认收起辅助 ──

        private static string DynastyStateKey(DynastyRecord d)
            => "dyn@" + d.StartYear + "_" + (d.Dynasty ?? "?");

        /// <summary>朝代展开态：用户点击覆盖优先；否则默认当朝展开、前朝收起。</summary>
        private bool IsDynastyExpanded(DynastyRecord d, bool isActive)
        {
            return _dynastyState.TryGetValue(DynastyStateKey(d), out bool expanded) ? expanded : isActive;
        }

        private void BuildDynastyCard(DynastyRecord d)
        {
            bool isVoid = d.Dynasty == "空悬";
            bool isActive = d.EndYear == -1 && !isVoid;

            // Row 容器（VLG 直接子物体，宽 = _innerWidth）
            GameObject row = new GameObject("Dynasty", typeof(RectTransform));
            row.transform.SetParent(_historyList, false);
            var rowRect = row.GetComponent<RectTransform>();
            rowRect.sizeDelta = new Vector2(_innerWidth, 20f);

            // 纯色底：从 Row 左边缘开始（offsetMin.x=-36 向左延长覆盖文字首字，被遮罩裁齐到列表左边缘）
            GameObject solidObj = new GameObject("SolidBg", typeof(RectTransform), typeof(Image));
            solidObj.transform.SetParent(row.transform, false);
            var solidRect = solidObj.GetComponent<RectTransform>();
            solidRect.anchorMin = Vector2.zero;
            solidRect.anchorMax = Vector2.one;
            solidRect.offsetMin = new Vector2(-36f, 0f);
            solidRect.offsetMax = Vector2.zero;
            var solidImg = solidObj.GetComponent<Image>();
            solidImg.color = isActive
                ? new Color(0.20f, 0.17f, 0.09f, 0.95f)      // 当前朝代：金色底
                : new Color(0.12f, 0.12f, 0.15f, 0.92f);
            solidImg.raycastTarget = false;

            // LlmV2-9：左缘饰条——当朝金、前朝墨灰（预览定稿的一眼辨识）
            GameObject accentObj = new GameObject("Accent", typeof(RectTransform), typeof(Image));
            accentObj.transform.SetParent(row.transform, false);
            var acRect = accentObj.GetComponent<RectTransform>();
            acRect.anchorMin = new Vector2(0, 0);
            acRect.anchorMax = new Vector2(0, 1);
            acRect.pivot = new Vector2(0, 0.5f);
            acRect.anchoredPosition = new Vector2(0, 0);
            acRect.sizeDelta = new Vector2(3f, 0f);
            var acImg = accentObj.GetComponent<Image>();
            acImg.color = isActive
                ? new Color(0.98f, 0.77f, 0.27f, 0.90f)
                : new Color(0.32f, 0.29f, 0.23f, 0.80f);
            acImg.raycastTarget = false;

            // 贴图装饰层（同样向左延长——贴图左边有装饰留白，视觉黑块会偏右，纯色层兜底对齐）
            GameObject bgObj = new GameObject("Bg", typeof(RectTransform), typeof(Image));
            bgObj.transform.SetParent(row.transform, false);
            var bgRect = bgObj.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = new Vector2(-36f, 0f);
            bgRect.offsetMax = Vector2.zero;
            var bgImg = bgObj.GetComponent<Image>();
            bgImg.color = isActive
                ? new Color(0.20f, 0.17f, 0.09f, 0.95f)      // 当前朝代：金色底
                : new Color(0.14f, 0.14f, 0.17f, 0.9f);
            bgImg.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            bgImg.type = Image.Type.Sliced;
            bgImg.raycastTarget = false;

            // 头部行（Row 内 x=8 起）
            string period = d.EndYear == -1 ? $"天命{d.StartYear}年~今" : $"天命{d.StartYear}年~{d.EndYear}年";
            string headText;
            if (isVoid)
            {
                // 空悬期（未结束的显示"至今"，避免 EndYear=-1 算出负数年份）
                string voidLen = d.EndYear == -1
                    ? "<color=#ac8b7f>（至今）</color>"
                    : $"<color=#ac8b7f>（{d.EndYear - d.StartYear}年）</color>";
                headText = $"<color=#ac8b7f>【空悬期】</color> <color=#ffd8d9>{period}</color> {voidLen}";
            }
            else
            {
                string reasonColor = isActive ? ZhuluUIStyle.Hex.Gold : "#f73939";
                string reason = isActive ? "（今上）" : $"（{DynastyEndReasonText(d.EndReason)}）";
                string dynastyColor = isActive ? ZhuluUIStyle.Hex.Gold : "#FF9A8A";
                // 朝代名防御：空名/脏数据不显示"【朝】"（历史记录错位时避免空卡）
                string dynName = string.IsNullOrEmpty(d.Dynasty) ? "未知" : d.Dynasty;
                // 阶段2M-29：受命序号（同国失而复得——古称"再受命/三受命"；篡位改朝天命未断不计）
                string reignTag = d.ReignIndex > 1 ? $" · {(d.ReignIndex == 2 ? "再" : CnNum(d.ReignIndex))}受命" : "";
                // 2026-08-19 串联：前朝默认收起（当朝展开）——头部带 ▼/▶ 箭头与折叠条数提示
                bool expanded = IsDynastyExpanded(d, isActive);
                string arrow = expanded ? "▼" : "▶";
                // 2026-08-19 串联·方案6.5：朝代级结局徽章——任一帝达成大一统 → ☀（金色；一眼识别盛世朝代）
                string unifiedBadge = "";
                try
                {
                    if (d.Emperors != null)
                        foreach (var e in d.Emperors)
                            if (e != null && e.Unified == 1) { unifiedBadge = " <color=#FFD700>☀</color>"; break; }
                }
                catch { }
                headText = $"<b>{arrow}<color={dynastyColor}>【{dynName}朝{reignTag}】</color>{unifiedBadge}</b> 历{d.Emperors.Count}帝　" +
                           $"<color=#ffd8d9>{period}</color> <color={reasonColor}>{reason}</color>" +
                           (expanded ? "" : " <color=#8a8578>·点击展开诸帝</color>");
            }
            Text head = CreateText(row.transform, headText, 10);
            head.alignment = TextAnchor.MiddleLeft;
            head.horizontalOverflow = HorizontalWrapMode.Wrap;   // 超宽换行，绝不溢出背景块
            head.verticalOverflow = VerticalWrapMode.Overflow;   // 换行后多行不被裁剪
            var headRect = head.GetComponent<RectTransform>();
            headRect.anchorMin = new Vector2(0, 1);
            headRect.anchorMax = new Vector2(0, 1);
            headRect.pivot = new Vector2(0, 1);
            headRect.anchoredPosition = new Vector2(8, -8);
            headRect.sizeDelta = new Vector2(_innerWidth - 16, 16f);
            head.raycastTarget = false;

            // 2026-08-19 串联：前朝收起态——头部行即卡（点击头部展开/收起，_dynastyState 记住用户选择）
            if (!isVoid && !IsDynastyExpanded(d, isActive))
            {
                // 透明点击层 + Button（头部不可点射线，补点击层——史册段头同法）
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
                var headBtn = row.AddComponent<Button>();
                headBtn.targetGraphic = clImg;
                string stateKey = DynastyStateKey(d);
                headBtn.onClick.AddListener(() =>
                {
                    try
                    {
                        _dynastyState[stateKey] = true;   // 收起态点击=展开
                        RefreshHistoryPanel();
                    }
                    catch { }
                });
                // 收起态高度：头部一行（可能换行——按实际行高）
                float headH = Mathf.Max(16f, head.preferredHeight);
                rowRect.sizeDelta = new Vector2(_innerWidth, 12f + headH + 8f);
                return;
            }
            // 展开态头部也可点击收起（当朝/用户展开的前朝）
            if (!isVoid)
            {
                GameObject clickLayer2 = new GameObject("ClickLayer", typeof(RectTransform), typeof(Image));
                clickLayer2.transform.SetParent(row.transform, false);
                var cl2Rect = clickLayer2.GetComponent<RectTransform>();
                cl2Rect.anchorMin = new Vector2(0f, 1f);
                cl2Rect.anchorMax = new Vector2(1f, 1f);
                cl2Rect.pivot = new Vector2(0.5f, 1f);
                cl2Rect.anchoredPosition = Vector2.zero;
                cl2Rect.sizeDelta = new Vector2(0f, 22f);   // 仅头部行区域可点（不挡诸帝行点击）
                var cl2Img = clickLayer2.GetComponent<Image>();
                cl2Img.color = new Color(1f, 1f, 1f, 0.01f);
                cl2Img.raycastTarget = true;
                var headBtn2 = row.AddComponent<Button>();
                headBtn2.targetGraphic = cl2Img;
                string stateKey2 = DynastyStateKey(d);
                headBtn2.onClick.AddListener(() =>
                {
                    try
                    {
                        _dynastyState[stateKey2] = false;   // 展开态点击头部=收起
                        RefreshHistoryPanel();
                    }
                    catch { }
                });
            }

            if (isVoid)
            {
                // 空悬期描述（阶段2O：受命之阶同主面板口径——国力点≥40）
                Text voidDesc = CreateText(row.transform,
                    "<color=#8a8578>天下无主，列国争雄，神器虚悬以待有德。受命之阶：国力点≥40 · 立国≥15年 · 净礼治≥8 · 无宗主。</color>", 9);
                voidDesc.alignment = TextAnchor.MiddleLeft;
                voidDesc.horizontalOverflow = HorizontalWrapMode.Wrap;
                voidDesc.verticalOverflow = VerticalWrapMode.Overflow;
                var vdRect = voidDesc.GetComponent<RectTransform>();
                vdRect.anchorMin = new Vector2(0, 1);
                vdRect.anchorMax = new Vector2(0, 1);
                vdRect.pivot = new Vector2(0, 1);
                vdRect.anchoredPosition = new Vector2(8, -24);
                vdRect.sizeDelta = new Vector2(_innerWidth - 16, 14f);
                voidDesc.raycastTarget = false;
                float vdH = Mathf.Max(14f, voidDesc.preferredHeight);
                rowRect.sizeDelta = new Vector2(_innerWidth, 24f + vdH + 10f);
                return;
            }

            // ── 2026-10-24 第7步：朝代级 AI 状态条（可用/已用/一键重写）──
            float aiBarH = 0f;
            try { aiBarH = BuildAiStatusBar(row.transform, d); }
            catch { }

            // 皇帝行三行制（阶段2O：行1 帝号 / 行2 功过书 / 行3 盖棺史评）——超宽换行，行高按实际文字高度
            float y = -26f - aiBarH;
            int idx = 1;
            var usedTemplesShown = new System.Collections.Generic.HashSet<string>(); // 2M-33：显示层庙号去重（旧档重复修正）
            foreach (var e in d.Emperors)
            {
                float blockTop = y;   // LlmV2-2：本帝块顶部（详情页点击层用）
                bool closed = e.EndYear > 0;
                string reign = closed ? $"{e.StartYear}~{e.EndYear}年" : $"{e.StartYear}~今";

                // ── 行1 主行：第N帝 名 年号 在位 + 帝号徽章（庙+谥+皇帝 / 谥+帝·不祔庙 / 在位·今上）──
                string badge = "";
                if (closed)
                {
                    // 2M-33：显示层去重（同朝重复庙号——旧档已生成数据的修正）
                    string tShow = e.TempleName ?? "";
                    if (!string.IsNullOrEmpty(tShow) && usedTemplesShown.Contains(tShow)
                        && tShow != "末帝" && tShow != "太祖" && tShow != "高祖" && tShow != "世祖")
                    {
                        string[] fb = { "太宗", "宣宗", "神宗", "哲宗", "英宗", "成宗", "孝宗", "圣宗", "睿宗", "穆宗", "代宗", "宪宗" };
                        foreach (var f in fb) { if (!usedTemplesShown.Contains(f)) { tShow = f; break; } }
                    }
                    if (!string.IsNullOrEmpty(tShow)) usedTemplesShown.Add(tShow);

                    if (!string.IsNullOrEmpty(tShow) && !string.IsNullOrEmpty(e.PosthumousName))
                        badge = $" <color={UI.ZhuluUIStyle.Hex.Imperial}>◆{tShow}{e.PosthumousName}皇帝</color>";
                    else if (!string.IsNullOrEmpty(e.PosthumousName))
                        badge = $" <color=#6ca3fd>◆{e.PosthumousName}帝</color> <color=#5a5f6e>（不祔庙）</color>";
                }
                // 阶段2V：大一统金字标识（在位/已崩皆显示——在位达成即终身）
                string unifiedTag = e.Unified == 1 ? " <color=#FFD700>☀大一统</color>" : "";
                // 阶段3H-10：破敌天命金字标识（灭掉对面体系天命国——在位/已崩皆显示）
                string slewTag = e.SlewHolder == 1 ? " <color=#FFD700>⚔破敌天命</color>" : "";
                Text empText = CreateText(row.transform,
                    $"{AiStatusDot(d, e, idx)}第{idx}帝 <b>{StripTitle(e.Name)}</b> <color=#e0554a>{e.Era}</color> <color=#ffd8d9>{reign}</color>{unifiedTag}{slewTag}{(closed ? badge : " <color={UI.ZhuluUIStyle.Hex.Positive}>（今上）</color>")}{PeakTag(e)}", 9);
                empText.alignment = TextAnchor.MiddleLeft;
                empText.horizontalOverflow = HorizontalWrapMode.Wrap;
                empText.verticalOverflow = VerticalWrapMode.Overflow;
                var empRect = empText.GetComponent<RectTransform>();
                empRect.anchorMin = new Vector2(0, 1);
                empRect.anchorMax = new Vector2(0, 1);
                empRect.pivot = new Vector2(0, 1);
                empRect.anchoredPosition = new Vector2(8, y);
                // 已盖棺的行右侧留出「AI 重评」按钮的位置，避免文字压到按钮
                // 已盖棺行右侧留出按钮组位置（本纪▸ + AI 重评），避免文字压到按钮
                empRect.sizeDelta = new Vector2(_innerWidth - 16 - (closed ? 110f : 0f), 18f);
                empText.raycastTarget = false;

                if (closed)
                {
                    // V2-8：按钮组右对齐——「本纪 ▸」（最右，永远显示）+「AI 重评」（启用时在其左）
                    try { CreateDetailEntryButton(row.transform, d, e, idx, y); }
                    catch { }
                    try { CreateAiReevalButton(row.transform, d, e, idx, y); }
                    catch { }
                }
                float used = Mathf.Max(14f, empText.preferredHeight) + 2f;

                if (closed)
                {
                    // ── 行2 功过书：城/民/库/天命/稳/附庸 增量 + 战·政辅助数（未采集自动省略）──
                    Text deedsText = CreateText(row.transform, BuildDeedsLine(e), 8);
                    deedsText.alignment = TextAnchor.UpperLeft;   // V2-10：多行居中会向上溢出压上一行
                    deedsText.horizontalOverflow = HorizontalWrapMode.Wrap;
                    deedsText.verticalOverflow = VerticalWrapMode.Overflow;
                    var deedsRect = deedsText.GetComponent<RectTransform>();
                    deedsRect.anchorMin = new Vector2(0, 1);
                    deedsRect.anchorMax = new Vector2(0, 1);
                    deedsRect.pivot = new Vector2(0, 1);
                    deedsRect.anchoredPosition = new Vector2(8, y - used);
                    deedsRect.sizeDelta = new Vector2(_innerWidth - 16, 14f);
                    deedsText.raycastTarget = false;
                    // 2026-08-18 修复：preferredHeight 在个别字体度量下低估换行数（部分评语与功过书重叠）——
                    // 改保守估算：去富文本标签按 CJK 字宽估行数，取两者较大值
                    used += MeasureWrappedHeight(deedsText, _innerWidth - 16, 13f) + 3f;

                    // ── 行3 盖棺史评：史评句 + 九等徽章 + 特质徽章（末帝按朝代失国论）──
                    // 2026-10-24 第7步：有 AI 评语时只替换引号内的史评句，徽章与谥法释义保留
                    bool lastEmpFell = (idx == d.Emperors.Count) && d.EndYear != -1 && d.EndReason != "active";
                    string aiComment = TryGetAiComment(d, e, idx);
                    string verdictLine = BuildVerdictLine(e, lastEmpFell, aiComment);
                    if (!string.IsNullOrEmpty(verdictLine))
                    {
                        Text vText = CreateText(row.transform, verdictLine, 8);
                        vText.alignment = TextAnchor.UpperLeft;   // V2-10：长评全文多行，同因防上溢
                        vText.horizontalOverflow = HorizontalWrapMode.Wrap;
                        vText.verticalOverflow = VerticalWrapMode.Overflow;
                        var vRect = vText.GetComponent<RectTransform>();
                        vRect.anchorMin = new Vector2(0, 1);
                        vRect.anchorMax = new Vector2(0, 1);
                        vRect.pivot = new Vector2(0, 1);
                        vRect.anchoredPosition = new Vector2(8, y - used);
                        vRect.sizeDelta = new Vector2(_innerWidth - 16, 14f);
                        vText.raycastTarget = false;
                        used += MeasureWrappedHeight(vText, _innerWidth - 16, 13f);
                    }
                }
                else
                {
                    // 在位皇帝：即位快照单行
                    string startLine = $"<color=#6a6458>即位（{e.StartYear}）：{e.StartCities}城 · 库 {e.StartTreasury} · 附庸 {e.StartVassals} · 天命 {e.StartMandate:F0} · 稳定 {e.StartStability:F0}</color>";
                    if (e.StartPopulation > 0) startLine = $"<color=#6a6458>即位（{e.StartYear}）：{e.StartCities}城 · 民 {e.StartPopulation} · 库 {e.StartTreasury} · 附庸 {e.StartVassals} · 天命 {e.StartMandate:F0} · 稳定 {e.StartStability:F0}</color>";
                    Text sText = CreateText(row.transform, startLine, 8);
                    sText.alignment = TextAnchor.MiddleLeft;
                    sText.horizontalOverflow = HorizontalWrapMode.Wrap;
                    sText.verticalOverflow = VerticalWrapMode.Overflow;
                    var sRectR = sText.GetComponent<RectTransform>();
                    sRectR.anchorMin = new Vector2(0, 1);
                    sRectR.anchorMax = new Vector2(0, 1);
                    sRectR.pivot = new Vector2(0, 1);
                    sRectR.anchoredPosition = new Vector2(8, y - used);
                    sRectR.sizeDelta = new Vector2(_innerWidth - 16, 14f);
                    sText.raycastTarget = false;
                    used += MeasureWrappedHeight(sText, _innerWidth - 16, 13f);
                }

                // LlmV2-2：本帝块整块可点 → 详情页（透明层放最底层，不挡按钮）
                try { CreateEmperorHitZone(row.transform, d, e, idx, blockTop, used); } catch { }

                // LlmV2-9：帝行行距 7→9（三行制呼吸感，预览定稿）
                y -= used + 9f;
                idx++;
            }

            rowRect.sizeDelta = new Vector2(_innerWidth, -y + 8f);
        }


        /// <summary>进度条分段填充：值覆盖的段亮、超出段暗</summary>
        private void SetBar(float val)
        {
            if (_barSegments == null) return;
            float[] segW = new float[] { 12f, 16f, 22f, 22f, 28f };
            Color[] bright = new Color[]
            {
                new Color(1f, 0.2f, 0.2f), new Color(1f, 0.55f, 0.1f), new Color(1f, 0.84f, 0f),
                new Color(0.62f, 0.9f, 0.29f), new Color(0.18f, 0.77f, 0.42f)
            };
            float covered = 0f;
            for (int i = 0; i < 5; i++)
            {
                covered += segW[i];
                bool lit = val >= covered - 0.01f;
                _barSegments[i].color = lit ? bright[i] : new Color(bright[i].r * 0.18f, bright[i].g * 0.18f, bright[i].b * 0.18f, 0.55f);
            }
        }
    }
}
