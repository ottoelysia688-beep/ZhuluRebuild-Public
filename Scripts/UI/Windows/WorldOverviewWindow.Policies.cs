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


// ── 由 WorldOverviewWindow.cs 按 region 拆出的 partial 部分：4. 王国国策政令视图 ──
// 2026-10-23 [P1-1] 纯文件级拆分，编译后仍为同一个类型，无任何行为改动。
namespace ZhuluRebuild.UI.Windows
{
    public partial class WorldOverviewWindow : MonoBehaviour
    {
        #region 4. 王国国策政令视图 (五档双层：大纲分类卡 + 二级选择页)
        private void RenderKingdomPoliciesUI(string kingdomId)
        {
            var kFact = WorldBoxReadOnlyAdapter.Instance.CaptureKingdomSnapshot(kingdomId);
            string kName = kFact != null ? kFact.KingdomName : "当前王国";

            _policiesVm = KingdomPoliciesVm.CreateFromAdapterOrMock(kingdomId, kName, _policiesVm?.SelectedCategoryView ?? "");

            // 五档样式（与官制一致：天命金/公紫/侯绿/伯蓝/子灰）
            var style = GetTierStyle(kingdomId);

            SetHeaderTabsVisible(false);
            _headerBackOverride = null;

            if (string.IsNullOrEmpty(_policiesVm.SelectedCategoryView))
            {
                // ═══ 大纲页 ═══
                UpdateHeaderTitle($"<color={style.Hex}>{style.Mark} {kName} · 王国国策</color>");

                string catNames = "";
                if (_policiesVm.CategoryCards != null)
                {
                    var uniq = new List<string>();
                    foreach (var c in _policiesVm.CategoryCards)
                    {
                        if (!uniq.Contains(c.CategoryTitle)) uniq.Add(c.CategoryTitle);
                    }
                    catNames = string.Join("·", uniq);
                }
                string aiState = (ZhuluConfig.Instance != null && ZhuluConfig.Instance.AiAutoPolicy)
                    ? "<color=#70d468>开</color>" : "<color=#8a8578>关</color>";
                if (_headerStatText != null)
                {
                    // 阶段2N W6：政体效能显示（与 PolicyManager 颁行放缩实际生效值同源）
                    int powerVal = KingdomExt.GetPowerValue(kingdomId);
                    float regimeFactor = RegimeManager.GetPolicyEfficiencyFactor(powerVal);
                    string regimeText = regimeFactor >= 1f
                        ? $"<color={UI.ZhuluUIStyle.Hex.Positive}>×{regimeFactor:F2}</color>"
                        : $"<color={UI.ZhuluUIStyle.Hex.Negative}>×{regimeFactor:F2}</color>";
                    _headerStatText.text = $"<b><color={style.Hex}>{catNames}（{_policiesVm.CategoryCards.Count}类）</color></b>"
                        + $"　已颁 <color={UI.ZhuluUIStyle.Hex.Positive}>{_policiesVm.ActivePolicyCount}</color> · 政体效能 {regimeText} · AI施政 {aiState}";
                }

                if (_policiesVm.CategoryCards != null)
                {
                    foreach (var card in _policiesVm.CategoryCards)
                    {
                        CreateOverviewCategoryCardUI(card, style);
                    }
                }
            }
            else
            {
                // ═══ 二级选择页 ═══
                var catCard = _policiesVm.CategoryCards.Find(c => c.CategoryId == _policiesVm.SelectedCategoryView);
                string catTitle = catCard != null ? catCard.CategoryTitle : "选择政策";
                UpdateHeaderTitle($"<color={style.Hex}>选择政策 · {catTitle}</color>");

                // Header ◄ 返回大纲（运行时覆盖，不依赖 StateEntrySource）
                _headerBackOverride = () => { _policiesVm.SelectedCategoryView = ""; RebuildUI(); };

                if (_headerStatText != null)
                {
                    string host = (catCard != null && !string.IsNullOrEmpty(catCard.HostOfficerInfo))
                        ? catCard.HostOfficerInfo.Replace("主管官席: ", "") : "";
                    _headerStatText.text = $"主管 <color={UI.ZhuluUIStyle.Hex.Positive}>{host}</color> · 点击条目即可颁行并覆盖该分类原政策";
                }

                if (catCard != null && catCard.Options != null)
                {
                    foreach (var opt in catCard.Options)
                    {
                        CreateSubPolicyOptionCardUI(opt, catCard, style);
                    }
                }
            }
        }

        /// <summary>分类图标（汉字标记，游戏字体安全）：政/兵/财/礼/天</summary>
        private static string GetCategoryIcon(string categoryId)
        {
            switch (categoryId)
            {
                case "military": return "兵";
                case "economy": return "财";
                case "social": return "礼";
                case "mandate": return "天";
                case "governance": return "政";
                default: return "策";
            }
        }

        private void CreateOverviewCategoryCardUI(CategoryCardVm card, OfficeTierStyle style)
        {
            bool hasPolicy = !string.IsNullOrEmpty(card.ActivePolicyTitle) && card.ActivePolicyTitle != "尚无实施政令";

            // Row 容器（纯色底 -36 兜底 + Sliced 贴图装饰；有政令=档位色，无政令=灰）
            // 任务11：58→48 紧凑化（行高压至 13/12/10）
            GameObject row = new GameObject($"Card_{card.CategoryId}", typeof(RectTransform));
            row.transform.SetParent(_contentTransform, false);
            var rowRect = row.GetComponent<RectTransform>();
            rowRect.sizeDelta = new Vector2(InnerWidth, 48f);

            GameObject solidObj = new GameObject("SolidBg", typeof(RectTransform), typeof(Image));
            solidObj.transform.SetParent(row.transform, false);
            var solidRect = solidObj.GetComponent<RectTransform>();
            solidRect.anchorMin = Vector2.zero;
            solidRect.anchorMax = Vector2.one;
            solidRect.offsetMin = new Vector2(-36f, 0f);
            solidRect.offsetMax = Vector2.zero;
            var solidImg = solidObj.GetComponent<Image>();
            solidImg.color = hasPolicy ? style.Solid : new Color(0.14f, 0.15f, 0.18f, 0.95f);
            solidImg.raycastTarget = false;

            GameObject bgObj = new GameObject("Bg", typeof(RectTransform), typeof(Image));
            bgObj.transform.SetParent(row.transform, false);
            var bgRect = bgObj.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = new Vector2(-36f, 0f);
            bgRect.offsetMax = Vector2.zero;
            var bgImg = bgObj.GetComponent<Image>();
            bgImg.color = hasPolicy ? style.Bg : new Color(0.19f, 0.20f, 0.24f, 0.92f);
            bgImg.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            bgImg.type = Image.Type.Sliced;
            bgImg.raycastTarget = false;

            // 任务11：左侧状态色边条（有政令=玉绿，虚席=暗灰）——纵向状态锚点
            GameObject edgeObj = new GameObject("StatusEdge", typeof(RectTransform), typeof(Image));
            edgeObj.transform.SetParent(row.transform, false);
            var edgeRect = edgeObj.GetComponent<RectTransform>();
            edgeRect.anchorMin = new Vector2(0f, 0f);
            edgeRect.anchorMax = new Vector2(0f, 1f);
            edgeRect.pivot = new Vector2(0f, 0.5f);
            edgeRect.anchoredPosition = Vector2.zero;
            edgeRect.sizeDelta = new Vector2(3f, 0f);
            var edgeImg = edgeObj.GetComponent<Image>();
            edgeImg.color = hasPolicy ? new Color(0.44f, 0.83f, 0.41f, 0.95f) : new Color(0.30f, 0.32f, 0.36f, 0.9f);
            edgeImg.raycastTarget = false;

            // 右上：主管官席小头像（26×26 动态小人；空缺=官字兜底）
            Transform slot = CreateAvatarSlot(row.transform, 26f, style.IsTm);
            var slotRect = slot.GetComponent<RectTransform>();
            slotRect.anchorMin = new Vector2(1f, 1f);
            slotRect.anchorMax = new Vector2(1f, 1f);
            slotRect.pivot = new Vector2(1f, 1f);
            slotRect.anchoredPosition = new Vector2(-8f, -5f);
            string holderName = card.HostHolderName;
            string fallback = (!string.IsNullOrEmpty(holderName)) ? holderName.Substring(0, 1) : "官";
            Actor actor = FindActorById(card.HostHolderId);
            SetAvatar(slot, actor, fallback, style.IsTm);

            // 右下：切换/颁行按钮
            GameObject btnObj = CreateButton(row.transform, hasPolicy ? "切换政策" : "颁行政令",
                hasPolicy ? new Color(0.30f, 0.24f, 0.10f) : new Color(0.28f, 0.32f, 0.38f), () =>
                {
                    _policiesVm.SelectedCategoryView = card.CategoryId;
                    RebuildUI();
                });
            var btnRect = btnObj.GetComponent<RectTransform>();
            btnRect.anchorMin = new Vector2(1f, 0f);
            btnRect.anchorMax = new Vector2(1f, 0f);
            btnRect.pivot = new Vector2(1f, 0f);
            btnRect.anchoredPosition = new Vector2(-8f, 5f);
            btnRect.sizeDelta = new Vector2(58f, 18f);
            btnObj.GetComponent<Button>().interactable = card.HasMinister;

            // 左中：信息列（分类名+主管 / 当前政策 / 效果）
            GameObject info = new GameObject("Info", typeof(RectTransform), typeof(VerticalLayoutGroup));
            info.transform.SetParent(row.transform, false);
            var infoRect = info.GetComponent<RectTransform>();
            infoRect.anchorMin = new Vector2(0f, 0.5f);
            infoRect.anchorMax = new Vector2(0f, 0.5f);
            infoRect.pivot = new Vector2(0f, 0.5f);
            infoRect.anchoredPosition = new Vector2(10f, 0f);
            infoRect.sizeDelta = new Vector2(InnerWidth - 10f - 74f, 38f);
            var infoVg = info.GetComponent<VerticalLayoutGroup>();
            infoVg.spacing = 1f;
            infoVg.childAlignment = TextAnchor.MiddleLeft;
            infoVg.childControlWidth = true;
            infoVg.childControlHeight = false;
            infoVg.childForceExpandWidth = true;
            infoVg.childForceExpandHeight = false;

            string icon = GetCategoryIcon(card.CategoryId);
            string hostText = card.HostOfficerInfo.Replace("主管官席: ", "");
            if (!string.IsNullOrEmpty(card.HostHolderName)) hostText += "·" + card.HostHolderName;

            Text r1 = CreateText(info.transform, $"<b><color=#e0554a>[{icon}]</color></b> <color={style.Hex}><b>{card.CategoryTitle}</b></color>  <color=#8a8578>{hostText}</color>", 9);
            r1.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 13f);

            if (hasPolicy)
            {
                Text r2 = CreateText(info.transform, $"<color={UI.ZhuluUIStyle.Hex.Positive}><b>《{card.ActivePolicyTitle}》</b></color> <color=#4a7a52>已颁行</color>", 9);
                r2.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 12f);
                Text r3 = CreateText(info.transform, $"<color={UI.ZhuluUIStyle.Hex.Positive}>✦ {card.ActivePolicyBuff}</color>", 8);
                r3.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 10f);
            }
            else
            {
                Text r2 = CreateText(info.transform, "<color=#8a8578>政令未颁 · 虚席以待</color>", 9);
                r2.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 12f);
                Text r3 = CreateText(info.transform, $"<color=#6a6458>选择{card.CategoryTitle}大政，以图兴邦</color>", 8);
                r3.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 10f);
            }
        }

        private void CreateSubPolicyOptionCardUI(PolicyOptionVm opt, CategoryCardVm catCard, OfficeTierStyle style)
        {
            // 任务11：横向大卡优化——三行制紧凑卡（行1 名+徽章 / 行2 增益·代价·开销合并换行 / 行3 简介小字），
            // 高度按行2实际换行数自适应（52~72px），替代原 80px 固定七行堆叠
            float infoW = InnerWidth - 10f - 74f;

            // ── 预组装行文本 ──
            string activeTag = opt.IsActive ? " <color=#70d468>[生效中]</color>" : "";
            string gateTag = "";
            if (!string.IsNullOrEmpty(opt.MinTier) && !opt.IsEligible)
                gateTag += $" <color=#8a8578>[需{TierNameOf2(opt.MinTier, style.Name == "王庭" || style.Name == "大汗庭" || style.Name == "汗国" || style.Name == "部盟" || style.Name == "部族/大部")}级]</color>";
            if (opt.RequiresMandate && !opt.IsEligible)
                gateTag += $" <color={UI.ZhuluUIStyle.Hex.Imperial}>[{(style.Name == "王庭" ? "天眷" : "天命")}专属]</color>";

            var effSb = new System.Text.StringBuilder();
            if (!string.IsNullOrEmpty(opt.BuffDesc)) effSb.Append($"<color={UI.ZhuluUIStyle.Hex.Positive}>✦ {opt.BuffDesc}</color>　");
            if (!string.IsNullOrEmpty(opt.TradeoffDesc)) effSb.Append($"<color={UI.ZhuluUIStyle.Hex.Negative}>✖ {opt.TradeoffDesc}</color>　");
            if (opt.UpkeepPct > 0f) effSb.Append($"<color={UI.ZhuluUIStyle.Hex.Warn}>♾ 岁入 -{opt.UpkeepPct * 100:F0}%/年</color>");
            string effText = effSb.ToString().TrimEnd('　');
            bool hasSummary = !string.IsNullOrEmpty(opt.SummaryDesc);

            // ── 行2 换行数保守估算（去富文本标签按 CJK 字宽估行数——天命窗同法）──
            float effH = 0f;
            if (!string.IsNullOrEmpty(effText))
            {
                string plain = System.Text.RegularExpressions.Regex.Replace(effText, "<[^>]+>", "");
                float charW = 8f * 1.05f;
                int lines = Mathf.Max(1, Mathf.CeilToInt(plain.Length * charW / infoW));
                effH = lines * 12f;
            }
            float cardH = Mathf.Max(52f, 7f + 15f + (effH > 0f ? effH + 2f : 0f) + (hasSummary ? 11f : 0f) + 7f);

            // Row 容器（生效=绿框，未生效=档位暗色底）
            GameObject row = new GameObject($"Option_{opt.PolicyId}", typeof(RectTransform));
            row.transform.SetParent(_contentTransform, false);
            var rowRect = row.GetComponent<RectTransform>();
            rowRect.sizeDelta = new Vector2(InnerWidth, cardH);

            GameObject solidObj = new GameObject("SolidBg", typeof(RectTransform), typeof(Image));
            solidObj.transform.SetParent(row.transform, false);
            var solidRect = solidObj.GetComponent<RectTransform>();
            solidRect.anchorMin = Vector2.zero;
            solidRect.anchorMax = Vector2.one;
            solidRect.offsetMin = new Vector2(-36f, 0f);
            solidRect.offsetMax = Vector2.zero;
            var solidImg = solidObj.GetComponent<Image>();
            solidImg.color = opt.IsActive ? new Color(0.10f, 0.22f, 0.14f, 0.95f) : new Color(0.13f, 0.14f, 0.17f, 0.95f);
            solidImg.raycastTarget = false;

            GameObject bgObj = new GameObject("Bg", typeof(RectTransform), typeof(Image));
            bgObj.transform.SetParent(row.transform, false);
            var bgRect = bgObj.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = new Vector2(-36f, 0f);
            bgRect.offsetMax = Vector2.zero;
            var bgImg = bgObj.GetComponent<Image>();
            bgImg.color = opt.IsActive ? new Color(0.15f, 0.28f, 0.18f, 0.92f) : style.Bg;
            bgImg.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            bgImg.type = Image.Type.Sliced;
            bgImg.raycastTarget = false;

            // 任务11：左侧状态边条（生效中=玉绿实条；可颁行=暗金细条；不可颁=灰）
            GameObject edgeObj = new GameObject("StatusEdge", typeof(RectTransform), typeof(Image));
            edgeObj.transform.SetParent(row.transform, false);
            var edgeRect = edgeObj.GetComponent<RectTransform>();
            edgeRect.anchorMin = new Vector2(0f, 0f);
            edgeRect.anchorMax = new Vector2(0f, 1f);
            edgeRect.pivot = new Vector2(0f, 0.5f);
            edgeRect.anchoredPosition = Vector2.zero;
            edgeRect.sizeDelta = new Vector2(3f, 0f);
            var edgeImg = edgeObj.GetComponent<Image>();
            edgeImg.color = opt.IsActive ? new Color(0.44f, 0.83f, 0.41f, 0.95f)
                : (opt.IsEligible ? new Color(0.98f, 0.77f, 0.27f, 0.55f) : new Color(0.30f, 0.32f, 0.36f, 0.9f));
            edgeImg.raycastTarget = false;

            // 右下：颁行/生效中按钮
            string btnLabel = opt.IsActive ? "生效中" : "颁 行"; // 阶段2Q：文雅统一（原"就是你了!"）
            Color btnColor = opt.IsActive ? new Color(0.20f, 0.40f, 0.25f) : new Color(0.20f, 0.55f, 0.30f);
            Action onConfirm = () =>
            {
                if (opt.IsActive) return;
                if (!catCard.HasMinister) return;
                int currentYear = ZhuluTime.CurrentYear;
                PolicyManager.Instance.IssuePolicy(_policiesVm.KingdomId, _policiesVm.KingdomName, opt.PolicyId, currentYear, isPlayer: true);
                RebuildUI();
            };
            GameObject confirmBtn = CreateButton(row.transform, btnLabel, btnColor, onConfirm);
            var confirmRect = confirmBtn.GetComponent<RectTransform>();
            confirmRect.anchorMin = new Vector2(1f, 0f);
            confirmRect.anchorMax = new Vector2(1f, 0f);
            confirmRect.pivot = new Vector2(1f, 0f);
            confirmRect.anchoredPosition = new Vector2(-8f, 6f);
            confirmRect.sizeDelta = new Vector2(58f, 18f);
            confirmBtn.GetComponent<Button>().interactable = !opt.IsActive && catCard.HasMinister;

            // 整卡可点 → 颁行（透明点击层）
            if (!opt.IsActive && catCard.HasMinister)
            {
                GameObject clickLayer = new GameObject("ClickLayer", typeof(RectTransform), typeof(Image));
                clickLayer.transform.SetParent(row.transform, false);
                var clickRect = clickLayer.GetComponent<RectTransform>();
                clickRect.anchorMin = Vector2.zero;
                clickRect.anchorMax = Vector2.one;
                clickRect.offsetMin = Vector2.zero;
                clickRect.offsetMax = new Vector2(-64f, 0f); // 阶段2N W13：让出右侧按钮列（原全覆盖致按钮无按压反馈）
                var clickImg = clickLayer.GetComponent<Image>();
                clickImg.color = new Color(1f, 1f, 1f, 0.01f);
                clickImg.raycastTarget = true;
                Button cardBtn = row.AddComponent<Button>();
                cardBtn.targetGraphic = clickImg;
                cardBtn.onClick.AddListener(() => onConfirm());
            }

            // 左侧：信息列（顶部锚定，行高按估算铺开）
            GameObject info = new GameObject("Info", typeof(RectTransform), typeof(VerticalLayoutGroup));
            info.transform.SetParent(row.transform, false);
            var infoRect = info.GetComponent<RectTransform>();
            infoRect.anchorMin = new Vector2(0f, 1f);
            infoRect.anchorMax = new Vector2(0f, 1f);
            infoRect.pivot = new Vector2(0f, 1f);
            infoRect.anchoredPosition = new Vector2(10f, -7f);
            infoRect.sizeDelta = new Vector2(infoW, cardH - 14f);
            var infoVg = info.GetComponent<VerticalLayoutGroup>();
            infoVg.spacing = 1f;
            infoVg.childAlignment = TextAnchor.MiddleLeft;
            infoVg.childControlWidth = true;
            infoVg.childControlHeight = false;
            infoVg.childForceExpandWidth = true;
            infoVg.childForceExpandHeight = false;

            // 行1：政策名 + 生效/门槛徽章
            Text r1 = CreateText(info.transform, $"<b><color={ZhuluUIStyle.Hex.Gold}>《{opt.Title}》</color></b>{activeTag}{gateTag}", 10);
            r1.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 15f);
            r1.horizontalOverflow = HorizontalWrapMode.Wrap;
            r1.verticalOverflow = VerticalWrapMode.Overflow;

            // 行2：增益·代价·开销合并（Wrap 换行）
            if (!string.IsNullOrEmpty(effText))
            {
                Text r2 = CreateText(info.transform, effText, 8);
                r2.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, effH);
                r2.horizontalOverflow = HorizontalWrapMode.Wrap;
                r2.verticalOverflow = VerticalWrapMode.Overflow;
            }

            // 行3：简介小字（风味描述）
            if (hasSummary)
            {
                Text r3 = CreateText(info.transform, $"<color=#6a6458>{opt.SummaryDesc}</color>", 7);
                r3.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 11f);
                r3.horizontalOverflow = HorizontalWrapMode.Wrap;
                r3.verticalOverflow = VerticalWrapMode.Overflow;
            }
        }

        private static string TierNameOf2(string tier, bool nomadic = false)
        {
            if (nomadic)
            {
                switch (tier)
                {
                    case "zi": return "部族/大部";
                    case "bo": return "部盟";
                    case "hou": return "汗国";
                    default: return "大汗庭";
                }
            }
            switch (tier)
            {
                case "zi": return "子";
                case "bo": return "伯";
                case "hou": return "侯";
                default: return "公";
            }
        }
        #endregion

    }
}
