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


// ── 由 WorldOverviewWindow.cs 按 region 拆出的 partial 部分：3. 王国百官视图 ──
// 2026-10-23 [P1-1] 纯文件级拆分，编译后仍为同一个类型，无任何行为改动。
namespace ZhuluRebuild.UI.Windows
{
    public partial class WorldOverviewWindow : MonoBehaviour
    {
        #region 3. 王国百官视图 (五档官制：天命/公/侯/伯/子)

        /// <summary>五档官制视觉风格（配色随国家等级变化）</summary>
        private class OfficeTierStyle
        {
            public string Mark;    // ★◆●▲■
            public string Name;    // 天命国/公国/侯国/伯国/子国
            public string System;  // 天子临朝/周礼六官/朝议制度/大夫辅政/宗族议事
            public string Label;   // 丞相六部制/周礼六官制/卿制/大夫制/家臣制
            public string Hex;     // 档位主色
            public Color Solid;    // 卡片纯色底
            public Color Bg;       // 卡片贴图层
            public bool IsTm;      // 是否天命国（头像兜底色）
        }

        private static OfficeTierStyle GetOfficeTierStyle(OfficeManager.KingdomTier tier)
        {
            switch (tier)
            {
                case OfficeManager.KingdomTier.Zi:
                    return new OfficeTierStyle { Mark = "■", Name = "子国", System = "宗族议事", Label = "家臣制", Hex = "#a0a0a0",
                        Solid = new Color(0.16f, 0.16f, 0.16f, 0.95f), Bg = new Color(0.22f, 0.22f, 0.22f, 0.92f), IsTm = false };
                case OfficeManager.KingdomTier.Bo:
                    return new OfficeTierStyle { Mark = "▲", Name = "伯国", System = "大夫辅政", Label = "大夫制", Hex = "#6ca3fd",
                        Solid = new Color(0.10f, 0.18f, 0.26f, 0.95f), Bg = new Color(0.14f, 0.24f, 0.34f, 0.92f), IsTm = false };
                case OfficeManager.KingdomTier.Hou:
                    return new OfficeTierStyle { Mark = "●", Name = "侯国", System = "朝议制度", Label = "卿制", Hex = "#70d468",
                        Solid = new Color(0.10f, 0.22f, 0.14f, 0.95f), Bg = new Color(0.15f, 0.28f, 0.18f, 0.92f), IsTm = false };
                case OfficeManager.KingdomTier.Gong:
                    return new OfficeTierStyle { Mark = "◆", Name = "公国", System = "周礼六官", Label = "周礼六官制", Hex = "#c9a0ff",
                        Solid = new Color(0.16f, 0.10f, 0.26f, 0.95f), Bg = new Color(0.22f, 0.14f, 0.34f, 0.92f), IsTm = false };
                default: // Tianming：天子临朝
                    return new OfficeTierStyle { Mark = "★", Name = "天命国", System = "天子临朝", Label = "丞相六部制", Hex = "#FFD700",
                        Solid = new Color(0.20f, 0.17f, 0.09f, 0.95f), Bg = new Color(0.28f, 0.22f, 0.10f, 0.92f), IsTm = true };
            }
        }

        private static OfficeTierStyle GetNomadicOfficeTierStyle(OfficeManager.KingdomTier tier)
        {
            switch (tier)
            {
                case OfficeManager.KingdomTier.Zi:
                    return new OfficeTierStyle { Mark = "■", Name = "部族/大部", System = "部族议事", Label = "部族管事制", Hex = "#a88a66", Solid = new Color(0.17f, 0.14f, 0.10f, 0.95f), Bg = new Color(0.24f, 0.19f, 0.12f, 0.92f), IsTm = false };
                case OfficeManager.KingdomTier.Bo:
                    return new OfficeTierStyle { Mark = "▲", Name = "部盟", System = "诸部会盟", Label = "盟府官制", Hex = "#c49a65", Solid = new Color(0.18f, 0.13f, 0.08f, 0.95f), Bg = new Color(0.27f, 0.19f, 0.10f, 0.92f), IsTm = false };
                case OfficeManager.KingdomTier.Hou:
                    return new OfficeTierStyle { Mark = "●", Name = "汗国", System = "可汗议政", Label = "汗国官制", Hex = "#d3aa72", Solid = new Color(0.20f, 0.14f, 0.08f, 0.95f), Bg = new Color(0.29f, 0.20f, 0.10f, 0.92f), IsTm = false };
                case OfficeManager.KingdomTier.Gong:
                    return new OfficeTierStyle { Mark = "◆", Name = "大汗庭", System = "大汗临庭", Label = "汗庭官制", Hex = "#e0b77a", Solid = new Color(0.21f, 0.15f, 0.08f, 0.95f), Bg = new Color(0.31f, 0.22f, 0.11f, 0.92f), IsTm = false };
                default:
                    return new OfficeTierStyle { Mark = "★", Name = "王庭", System = "天可汗会盟", Label = "王庭官制", Hex = "#FFD080", Solid = new Color(0.22f, 0.17f, 0.08f, 0.95f), Bg = new Color(0.32f, 0.24f, 0.10f, 0.92f), IsTm = true };
            }
        }

        /// <summary>按王国 ID 获取五档官制样式（统一口径：城数→等级→样式，供国势/官制/国策/邦交页复用）。</summary>
        private OfficeTierStyle GetTierStyle(string kingdomId)
        {
            Kingdom k = ZhuluRebuild.Persistence.KingdomDataExt.FindKingdomById(kingdomId);
            bool isMandate = PolicyManager.IsMandateHolderForKingdom(k);
            var tier = OfficeManager.GetTierWithMandate(k, isMandate);
            return k != null && KingdomCivilizationService.Instance.IsNomadic(k)
                ? GetNomadicOfficeTierStyle(tier)
                : GetOfficeTierStyle(tier);
        }

        private void RenderKingdomOfficesUI(string kingdomId)
        {
            _headerBackOverride = null;   // 清残留返回覆盖
            var kFact = WorldBoxReadOnlyAdapter.Instance.CaptureKingdomSnapshot(kingdomId);
            string kName = kFact != null ? kFact.KingdomName : "当前王国";

            _officesVm = KingdomOfficesVm.CreateFromAdapterOrMock(kingdomId, kName);

            // 按国家等级动态获取五档官制风格（天命金/公紫/侯绿/伯蓝/子灰）
            var style = GetTierStyle(kingdomId);
            Kingdom officeK = KingdomDataExt.FindKingdomById(kingdomId);
            bool officeNomadic = officeK != null && KingdomCivilizationService.Instance.IsNomadic(officeK);

            UpdateHeaderTitle($"<color={style.Hex}>{style.Mark} {kName} · {(officeNomadic ? "汗庭官制" : "朝廷官制")}</color>");
            SetHeaderTabsVisible(false);

            // 统计行：体制（档位色）| 已任/空缺/风险（按状态变色）——短文案防临界超宽（天命国7席时324px>320px）
            string riskColor = _officesVm.AdminRiskLevel.Contains("紊乱") ? "#f73939"
                : (_officesVm.AdminRiskLevel.Contains("尚稳") ? "#e0554a" : "#70d468");
            if (_headerStatText != null)
            {
                _headerStatText.text = $"<b><color={style.Hex}>{style.System}·{style.Label}（{_officesVm.OfficeCards.Count}席）</color></b>"
                    + $"　已任<color={UI.ZhuluUIStyle.Hex.Positive}>{_officesVm.FilledSeats}</color>·空缺<color={UI.ZhuluUIStyle.Hex.Negative}>{_officesVm.VacantSeats}</color>·风险<color={riskColor}>{_officesVm.AdminRiskLevel}</color>";
            }

            if (_officesVm.OfficeCards != null)
            {
                // 阶段2M：分区（朝堂/地方官）——local_ 前缀切分区
                bool courtTitleShown = false, localTitleShown = false;
                foreach (var card in _officesVm.OfficeCards)
                {
                    bool isLocal = card.OfficeId != null && card.OfficeId.StartsWith("local_");
                    if (isLocal && !localTitleShown)
                    {
                        localTitleShown = true;
                        CreateSectionTitle("🏯 地方官");
                    }
                    else if (!isLocal && !courtTitleShown)
                    {
                        courtTitleShown = true;
                        CreateSectionTitle(officeNomadic ? "✦ 汗庭" : "✦ 朝堂");
                    }
                    CreateOfficeCard(card, kName, style);
                }
                // 阶段2M：城主区（每城城主——名/身份/权势）
                CreateCityLeadersSection(kName, style);
            }
        }

        /// <summary>阶段2M：分区标题（金色小标题+分隔线）。</summary>
        private void CreateSectionTitle(string text)
        {
            try
            {
                if (_contentTransform == null) return;
                Text t = CreateText(_contentTransform, $"<color={UI.ZhuluUIStyle.Hex.Warn}>{text}</color>", 10);
                t.alignment = TextAnchor.MiddleLeft;
                RectTransform tr = t.GetComponent<RectTransform>();
                tr.anchorMin = new Vector2(0, 1); tr.anchorMax = new Vector2(0, 1);
                tr.pivot = new Vector2(0, 1);
                tr.anchoredPosition = Vector2.zero;
                tr.sizeDelta = new Vector2(_innerWidth, 20f);
            }
            catch { }
        }

        /// <summary>阶段2M：城主区（每城城主——名/身份/权势）。</summary>
        private void CreateCityLeadersSection(string kingdomName, OfficeTierStyle style)
        {
            try
            {
                string kingdomId = _officesVm != null ? _officesVm.KingdomId : "";
                if (string.IsNullOrEmpty(kingdomId)) return;
                Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
                if (k == null || k.cities == null || k.cities.Count == 0) return;
                CreateSectionTitle("🏯 城主");
                foreach (var c in k.cities)
                {
                    if (c == null) continue;
                    Actor leader = c.leader;
                    string cityName = c.data != null ? c.data.name ?? "城" : "城";
                    string lName = leader != null ? (leader.getName() ?? "空缺") : "空缺";
                    string identity = "";
                    int power = 0;
                    if (leader != null)
                    {
                        try
                        {
                            var idType = IdentityClassifier.Instance.GetIdentity(leader.getID());
                            identity = idType == IdentityClassifier.IdentityType.Military ? "武将"
                                : idType == IdentityClassifier.IdentityType.Imperial ? "宗室" : "国民";
                        }
                        catch { }
                        try { power = CityLeaderService.GetLeaderPower(c, k); } catch { }
                    }
                    // 批次7美化B3：纯文字行→RowItem 行单元（[身份色边条][城名金][人名+身份副行][权势右值]）
                    var edge = leader == null ? ZhuluWidgets.Edge.None
                        : identity == "宗室" ? ZhuluWidgets.Edge.Imperial
                        : identity == "武将" ? ZhuluWidgets.Edge.Negative
                        : ZhuluWidgets.Edge.Azure;
                    string mainText = leader != null
                        ? $"<color={UI.ZhuluUIStyle.Hex.Azure}>{cityName}</color> <color=#e8e0d0>{lName}</color>"
                        : $"<color={UI.ZhuluUIStyle.Hex.Azure}>{cityName}</color> <color=#6a6458>空缺</color>";
                    string powerColor = power >= 70 ? "#e0554a" : power >= 40 ? "#f0c060" : "#6ca3fd";
                    string rightValue = leader != null ? $"<size=8><color={powerColor}>权势{power}</color></size>" : "";
                    var rowItem = ZhuluWidgets.RowItem(_contentTransform, _innerWidth, 18f,
                        mainText, identity, rightValue, edge);
                    rowItem.localPosition = Vector3.zero;
                }
            }
            catch { }
        }

        private void CreateOfficeCard(OfficeCardVm card, string kingdomName, OfficeTierStyle style)
        {
            bool isVacant = (card.Status == "vacant");

            // Row 容器（纯色底 -36 兜底 + Sliced 贴图装饰，同国势卡方案）
            GameObject row = new GameObject($"Office_{card.OfficeId}", typeof(RectTransform));
            row.transform.SetParent(_contentTransform, false);
            var rowRect = row.GetComponent<RectTransform>();
            rowRect.sizeDelta = new Vector2(InnerWidth, isVacant ? 44f : 58f);

            GameObject solidObj = new GameObject("SolidBg", typeof(RectTransform), typeof(Image));
            solidObj.transform.SetParent(row.transform, false);
            var solidRect = solidObj.GetComponent<RectTransform>();
            solidRect.anchorMin = Vector2.zero;
            solidRect.anchorMax = Vector2.one;
            solidRect.offsetMin = new Vector2(-36f, 0f);
            solidRect.offsetMax = Vector2.zero;
            var solidImg = solidObj.GetComponent<Image>();
            solidImg.color = isVacant ? new Color(0.24f, 0.10f, 0.10f, 0.95f) : style.Solid;
            solidImg.raycastTarget = false;

            GameObject bgObj = new GameObject("Bg", typeof(RectTransform), typeof(Image));
            bgObj.transform.SetParent(row.transform, false);
            var bgRect = bgObj.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = new Vector2(-36f, 0f);
            bgRect.offsetMax = Vector2.zero;
            var bgImg = bgObj.GetComponent<Image>();
            bgImg.color = isVacant ? new Color(0.32f, 0.14f, 0.14f, 0.92f) : style.Bg;
            bgImg.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            bgImg.type = Image.Type.Sliced;
            bgImg.raycastTarget = false;

            // 左：小头像（34×34 动态小人；空缺=空字兜底）
            Transform slot = CreateAvatarSlot(row.transform, 34f, style.IsTm);
            var slotRect = slot.GetComponent<RectTransform>();
            slotRect.anchoredPosition = new Vector2(6f, 0f);
            if (isVacant)
            {
                SetAvatar(slot, null, "空", style.IsTm);
            }
            else
            {
                Actor actor = FindActorById(card.HolderId);
                string fb = (!string.IsNullOrEmpty(card.HolderName)) ? card.HolderName.Substring(0, 1) : "官";
                SetAvatar(slot, actor, fb, style.IsTm);
            }

            // 右：按钮列（在任=查看/罢免 竖排；空缺=提名选拔）
            GameObject btnCol = new GameObject("BtnCol", typeof(RectTransform), typeof(VerticalLayoutGroup));
            btnCol.transform.SetParent(row.transform, false);
            var btnColRect = btnCol.GetComponent<RectTransform>();
            btnColRect.anchorMin = new Vector2(1f, 0.5f);
            btnColRect.anchorMax = new Vector2(1f, 0.5f);
            btnColRect.pivot = new Vector2(1f, 0.5f);
            btnColRect.anchoredPosition = new Vector2(-6f, 0f);
            btnColRect.sizeDelta = new Vector2(60f, isVacant ? 20f : 40f);
            var btnColVg = btnCol.GetComponent<VerticalLayoutGroup>();
            btnColVg.spacing = 3f;
            btnColVg.childAlignment = TextAnchor.MiddleRight;
            btnColVg.childControlWidth = false;
            btnColVg.childControlHeight = false;
            btnColVg.childForceExpandWidth = false;
            btnColVg.childForceExpandHeight = false;

            Action nominate = () => {
                ZhuluContext.Instance.LogInfo($"[OfficeCard] 点击提名选拔: {card.OfficeId}, kingdom={kingdomName}");
                UiRouter.Instance.NavigateTo(RouteDestination.Nomination, kingdomId: _officesVm.KingdomId, officeId: card.OfficeId);
                RebuildUI();
            };

            if (isVacant)
            {
                GameObject nomBtn = CreateButton(btnCol.transform, "提名选拔", new Color(0.2f, 0.6f, 0.3f), nominate);
                nomBtn.GetComponent<RectTransform>().sizeDelta = new Vector2(58f, 18f);

                // 整卡可点 → 提名（透明点击层，SolidBg/Bg 均 raycastTarget=false 不挡）
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
                cardBtn.onClick.AddListener(() => nominate());
            }
            else
            {
                string personId = card.HolderId;
                GameObject viewBtn = CreateButton(btnCol.transform, "查看", new Color(0.3f, 0.4f, 0.6f), () => {
                    Actor actor = FindActorById(personId);
                    if (actor != null)
                    {
                        MetaTypeLibrary.unit.selectAndInspect(actor, false, true, true);
                        ScrollWindow.showWindow(MetaTypeLibrary.unit.window_name);
                    }
                });
                viewBtn.GetComponent<RectTransform>().sizeDelta = new Vector2(40f, 16f);

                GameObject dismissBtn = CreateButton(btnCol.transform, "罢免", new Color(0.7f, 0.2f, 0.2f), () => {
                    int currentYear = ZhuluTime.CurrentYear;
                    OfficeManager.Instance.DismissOfficer(_officesVm.KingdomId, kingdomName, card.OfficeId, currentYear);
                    RebuildUI();
                });
                dismissBtn.GetComponent<RectTransform>().sizeDelta = new Vector2(40f, 16f);
            }

            // 中：信息列（官职+姓名 / Buff / 清廉权势）
            GameObject info = new GameObject("Info", typeof(RectTransform), typeof(VerticalLayoutGroup));
            info.transform.SetParent(row.transform, false);
            var infoRect = info.GetComponent<RectTransform>();
            infoRect.anchorMin = new Vector2(0f, 0.5f);
            infoRect.anchorMax = new Vector2(0f, 0.5f);
            infoRect.pivot = new Vector2(0f, 0.5f);
            infoRect.anchoredPosition = new Vector2(48f, 0f);
            infoRect.sizeDelta = new Vector2(InnerWidth - 48f - 66f, isVacant ? 36f : 50f);
            var infoVg = info.GetComponent<VerticalLayoutGroup>();
            infoVg.spacing = 1f;
            infoVg.childAlignment = TextAnchor.MiddleLeft;
            infoVg.childControlWidth = true;
            infoVg.childControlHeight = false;
            infoVg.childForceExpandWidth = true;
            infoVg.childForceExpandHeight = false;

            if (isVacant)
            {
                Text r1 = CreateText(info.transform, $"<b><color={UI.ZhuluUIStyle.Hex.Negative}>{card.Title}</color></b> <color=#ac8b7f>虚位以待</color>", 10);
                r1.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 13f);
                Text r2 = CreateText(info.transform, "<color=#8a8578>政事乏人主持 · 点此择贤而任</color>", 8);
                r2.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 11f);
            }
            else
            {
                Text r1 = CreateText(info.transform, $"<b><color={ZhuluUIStyle.Hex.Gold}>{card.Title}</color></b> <color={UI.ZhuluUIStyle.Hex.Positive}>{card.HolderName}</color> <color=#ffd8d9>（{TenureShort(card.Tenure)}）</color>", 10);
                r1.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 13f);
                if (!string.IsNullOrEmpty(card.BuffDescription))
                {
                    Text r2 = CreateText(info.transform, $"<color={UI.ZhuluUIStyle.Hex.Positive}>✦ {card.BuffDescription}</color>", 8);
                    r2.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 11f);
                }
                Text r3 = CreateText(info.transform, BuildCorruptionLine(card), 8);
                r3.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 11f);
            }
        }

        /// <summary>压缩在任时长文案："在任 (保护期剩: 5年)" → "在任·保5年"</summary>
        private static string TenureShort(string tenure)
        {
            if (string.IsNullOrEmpty(tenure) || tenure == "在任") return "在任";
            var m = Regex.Match(tenure, @"\d+");
            if (m.Success) return $"在任·保{m.Value}年";
            return "在任";
        }

        /// <summary>清廉/权势行：数值按档位变色（清廉 ≥90绿/≥70橙/低红；权势 <50蓝/<70橙/≥70红），等级说明灰色</summary>
        private static string BuildCorruptionLine(OfficeCardVm card)
        {
            string line = "";
            if (!string.IsNullOrEmpty(card.IntegrityDisplay))
            {
                var m = Regex.Match(card.IntegrityDisplay, @"(\d+)");
                string color = "#e0554a";
                if (m.Success)
                {
                    int v = int.Parse(m.Value);
                    color = v >= 90 ? "#70d468" : (v >= 70 ? "#e0554a" : "#f73939");
                }
                string grade = m.Success ? card.IntegrityDisplay.Substring(m.Index + m.Length) : "";
                line += $"清廉 <color={color}>{(m.Success ? m.Value + "%" : "?")}</color><color=#8a8578>{grade}</color>";
            }
            if (!string.IsNullOrEmpty(card.PowerDisplay))
            {
                var m = Regex.Match(card.PowerDisplay, @"(\d+)");
                string color = "#6ca3fd";
                if (m.Success)
                {
                    int v = int.Parse(m.Value);
                    color = v >= 70 ? "#f73939" : (v >= 50 ? "#e0554a" : "#6ca3fd");
                }
                string grade = m.Success ? card.PowerDisplay.Substring(m.Index + m.Length) : "";
                line += " · 权势 <color=" + color + ">" + (m.Success ? m.Value : "?") + "</color><color=#8a8578>" + grade + "</color>";
            }
            return line;
        }
        #endregion

    }
}
