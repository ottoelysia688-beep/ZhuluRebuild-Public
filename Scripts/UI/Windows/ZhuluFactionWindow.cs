using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using ZhuluRebuild.Assets;
using ZhuluRebuild.Core;
using ZhuluRebuild.Domain;
using ZhuluRebuild.Persistence;
using ZhuluRebuild.Presentation.Routing;

namespace ZhuluRebuild.UI.Windows
{
    /// <summary>
    /// 党争窗口（阶段2 UI·三层结构，布局已确认 2026-08）。
    /// 第一页=党派总览（4固定党+自建，执政金章/影响力条/近事3条/烈度条）
    /// 第二页=成员列表（动态小人头像 UiUnitAvatarElement + 姓名/官职/亲信/权势）
    /// 第三页=朝议（近年朝议近事 + 历代朝议存档·史册 CourtDebate 条目，2026-08-19 新增，底部页签切换）
    /// 第四页=成员详情（52动态小人大头 + 亲信/权势条 + 官制/史册摘要/党籍 + 查看单位/个人史册跳转）
    /// 数据：FactionManager（成员/党魁）+ FactionPoliticsManager（亲信/影响力/烈度/政变数）
    /// 批次8迁移（2026-08-23）：骨架收敛至 ZhuluScrollWindowBase（ContentTopBand=80/BottomBand=32；
    /// HandleBackClick 覆写实现 页内回退 详情→成员→总览）——删除手写 RebuildUI 约150行与本地工具。
    /// </summary>
    public class ZhuluFactionWindow : ZhuluScrollWindowBase
    {
        public static ZhuluFactionWindow Instance { get; private set; }

        private Text _subStatText;
        private Image _intensityFill;
        private Text _intensityValText;
        private Text _intensityGovText;
        private Transform _pageOverview;
        private Transform _pageMembers;
        private Transform _pageCourt;
        private Transform _pageDetail;
        private Transform _overviewContent;
        private Transform _membersContent;
        private Transform _detailContent;

        private int _currentPage;                 // 0=总览 1=成员 2=朝议 3=详情
        private FactionManager.FactionRecord _currentFaction;
        private long _currentActorId;

        // 底部页签（总览/成员/朝议；详情为成员下钻不设页签。配色同家族窗口 TabOn/TabOff）
        private Text _tabOverview, _tabMembers, _tabCourt;
        private readonly Color TabOn = new Color(0.32f, 0.26f, 0.18f, 0.95f);
        private readonly Color TabOff = new Color(0.16f, 0.15f, 0.13f, 0.85f);

        private static UiUnitAvatarElement _avatarPrefab;

        public void Initialize()
        {
            Instance = this;
            ZhuluContext.Instance.LogInfo("[ZhuluFactionWindow] 初始化完成（首次打开时构建，防偏左）");
        }

        protected override string WindowTitle => "<b>党争风云</b>";
        protected override float WindowWidth => 350f;
        protected override float WindowHeight => 400f;
        protected override float PlaqueWidth => 250f;
        protected override float ContentTopBand => 80f;     // 牌匾26 + 副标题12 + 烈度条24 + 间距
        protected override float ContentBottomBand => 32f;  // 底部页签条

        private void OnEnable()
        {
            if (World.world == null) return;
            EnsureBuilt();
            RefreshPage();
        }

        public void Show()
        {
            if (World.world != null && gameObject.activeInHierarchy)
            {
                EnsureBuilt();
                RefreshPage();
            }
        }

        /// <summary>页内返回：详情→成员；成员/朝议→总览；总览→原版导航栈。</summary>
        protected override void HandleBackClick()
        {
            if (_currentPage == 3) { ShowPage(1); return; }
            if (_currentPage > 0) { ShowPage(0); return; }
            base.HandleBackClick();
        }

        // 阶段3F：当前王国是否游牧体制（窗口文本分支用）
        private bool IsNomadicCtx(string kId)
        {
            try
            {
                var k = ZhuluRebuild.Persistence.KingdomDataExt.FindKingdomById(kId);
                return k != null && Domain.KingdomCivilizationService.Instance.IsNomadic(k);
            }
            catch { return false; }
        }

        private string CurrentKingdomId()
        {
            string id = UiRouter.Instance.CurrentRoute.ActiveKingdomId;
            if (!string.IsNullOrEmpty(id)) return id;
            try
            {
                var sel = SelectedMetas.selected_kingdom;
                if (sel != null) return sel.id.ToString();
            }
            catch { }
            return "";
        }


        protected override void BuildContent()
        {
            _subStatText = CreateText(Bg, "", 9);
            _subStatText.alignment = TextAnchor.MiddleCenter;
            _subStatText.raycastTarget = false;
            var subRect = _subStatText.GetComponent<RectTransform>();
            subRect.anchorMin = new Vector2(0.5f, 1);
            subRect.anchorMax = new Vector2(0.5f, 1);
            subRect.pivot = new Vector2(0.5f, 1);
            subRect.anchoredPosition = new Vector2(-8, -38);
            subRect.sizeDelta = new Vector2(InnerWidth, 12f);

            // ?????????
            CreateIntensityBar();

            // ????????????
            _pageOverview = CreatePage("PageOverview");
            _pageMembers = CreatePage("PageMembers");
            _pageCourt = CreatePage("PageCourt");
            _pageDetail = CreatePage("PageDetail");

            // ????????????/??????????? VLG?
            var ovVlg = _pageOverview.gameObject.AddComponent<VerticalLayoutGroup>();
            ovVlg.childAlignment = TextAnchor.UpperCenter;
            ovVlg.spacing = 5f;
            ovVlg.padding = new RectOffset(2, 2, 4, 4);
            ovVlg.childControlWidth = false;
            ovVlg.childControlHeight = false;
            ovVlg.childForceExpandWidth = false;
            ovVlg.childForceExpandHeight = false;
            _overviewContent = _pageOverview;
            _membersContent = _pageMembers;
            _detailContent = _pageDetail;

            // ????????/??/???????????????
            CreateBottomTabs();

            ShowPage(0);
        }

        private Transform CreatePage(string name)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(ContentRoot, false);
            var r = go.GetComponent<RectTransform>();
            r.sizeDelta = new Vector2(InnerWidth, 0f);
            r.anchoredPosition = Vector2.zero;
            go.SetActive(false);
            return go.transform;
        }

        // ═══════════════════════ 页面切换 ═══════════════════════

        private void ShowPage(int page)
        {
            _currentPage = page;
            bool ov = page == 0, mb = page == 1, cy = page == 2, dt = page == 3;
            if (_pageOverview != null) _pageOverview.gameObject.SetActive(ov);
            if (_pageMembers != null) _pageMembers.gameObject.SetActive(mb);
            if (_pageCourt != null) _pageCourt.gameObject.SetActive(cy);
            if (_pageDetail != null) _pageDetail.gameObject.SetActive(dt);
            // 页签高亮（详情=成员页下钻，随成员页签点亮）
            if (_tabOverview != null) _tabOverview.transform.parent.GetComponent<Image>().color = ov ? TabOn : TabOff;
            if (_tabMembers != null) _tabMembers.transform.parent.GetComponent<Image>().color = (mb || dt) ? TabOn : TabOff;
            if (_tabCourt != null) _tabCourt.transform.parent.GetComponent<Image>().color = cy ? TabOn : TabOff;
            RefreshPage();
        }

        private void RefreshPage()
        {
            if (ContentRoot == null) return;
            try
            {
                if (_currentPage == 0) BuildOverview();
                else if (_currentPage == 1) BuildMembers();
                else if (_currentPage == 2) BuildCourtPage();
                else BuildDetail();
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[FactionWindow] 刷新异常: {ex.Message}");
            }
            LayoutRebuilder.ForceRebuildLayoutImmediate(ContentRoot.GetComponent<RectTransform>());
        }

        // ═══════════════════════ 烈度条 ═══════════════════════

        private void CreateIntensityBar()
        {
            GameObject barObj = new GameObject("IntensityBar", typeof(RectTransform), typeof(Image));
            barObj.transform.SetParent(Bg, false);
            var br = barObj.GetComponent<RectTransform>();
            br.anchorMin = new Vector2(0, 1);
            br.anchorMax = new Vector2(0, 1);
            br.pivot = new Vector2(0, 1);
            br.anchoredPosition = new Vector2(10, -56);
            br.sizeDelta = new Vector2(InnerWidth, 18f);
            var bImg = barObj.GetComponent<Image>();
            bImg.color = new Color(0.13f, 0.14f, 0.19f, 0.95f);
            bImg.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            bImg.type = Image.Type.Sliced;

            // 阶段2N W11：烈度语义=一党独大危险（D3 定稿保持公式，文案明确化）
            Text label = CreateText(barObj.transform, IsNomadicCtx(CurrentKingdomId()) ? "汗庭独大" : "朝堂独大", 9); // 阶段3F
            label.alignment = TextAnchor.MiddleLeft;
            var lr = label.GetComponent<RectTransform>();
            lr.anchorMin = new Vector2(0, 0.5f);
            lr.anchorMax = new Vector2(0, 0.5f);
            lr.pivot = new Vector2(0, 0.5f);
            lr.anchoredPosition = new Vector2(8, 0);
            lr.sizeDelta = new Vector2(56f, 14f);
            label.color = new Color(0.62f, 0.60f, 0.55f, 1f);
            label.raycastTarget = false;

            // 进度条底
            GameObject fillBg = new GameObject("FillBg", typeof(RectTransform), typeof(Image));
            fillBg.transform.SetParent(barObj.transform, false);
            var fr = fillBg.GetComponent<RectTransform>();
            fr.anchorMin = new Vector2(0, 0.5f);
            fr.anchorMax = new Vector2(0, 0.5f);
            fr.pivot = new Vector2(0, 0.5f);
            fr.anchoredPosition = new Vector2(66, 0);
            fr.sizeDelta = new Vector2(120f, 7f);
            fillBg.GetComponent<Image>().color = new Color(0.08f, 0.09f, 0.12f, 1f);

            GameObject fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            fill.transform.SetParent(fillBg.transform, false);
            var fillRect = fill.GetComponent<RectTransform>();
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = new Vector2(1, 1);
            fillRect.sizeDelta = Vector2.zero;
            fillRect.offsetMax = new Vector2(0, 0);
            _intensityFill = fill.GetComponent<Image>();
            _intensityFill.color = new Color(0.97f, 0.55f, 0.55f, 1f);

            _intensityValText = CreateText(barObj.transform, "0.00", 9);
            _intensityValText.alignment = TextAnchor.MiddleCenter;
            var vr = _intensityValText.GetComponent<RectTransform>();
            vr.anchorMin = new Vector2(0, 0.5f);
            vr.anchorMax = new Vector2(0, 0.5f);
            vr.pivot = new Vector2(0, 0.5f);
            vr.anchoredPosition = new Vector2(192, 0);
            vr.sizeDelta = new Vector2(34f, 14f);
            _intensityValText.color = new Color(0.97f, 0.55f, 0.55f, 1f);
            _intensityValText.raycastTarget = false;

            _intensityGovText = CreateText(barObj.transform, "", 9);
            _intensityGovText.alignment = TextAnchor.MiddleLeft;
            var gr = _intensityGovText.GetComponent<RectTransform>();
            gr.anchorMin = new Vector2(0, 0.5f);
            gr.anchorMax = new Vector2(0, 0.5f);
            gr.pivot = new Vector2(0, 0.5f);
            gr.anchoredPosition = new Vector2(232, 0);
            gr.sizeDelta = new Vector2(90f, 14f);
            _intensityGovText.raycastTarget = false;
        }

        private void RefreshHeader(string kId)
        {
            if (PlaqueText == null) return;
            Kingdom k = null;
            try { k = World.world.kingdoms.get(int.Parse(kId)); } catch { }
            string kName = k != null ? k.name : "";
            PlaqueText.text = $"<b>{kName} · 党争风云</b>";
            if (_subStatText == null) return;

            var st = FactionPoliticsManager.Instance.GetState(kId);
            int coupCount = st?.CoupCount ?? 0;
            string govName = k != null ? FactionPoliticsManager.Instance.GetRulingFactionName(k, kId) : "无";
            _subStatText.text = $"建元 <color=#ffd8d9>{ZhuluTime.CurrentYear}</color> 年 · 执政党 <color=#ffd700>{govName}</color> · 政变 <color={UI.ZhuluUIStyle.Hex.Negative}>{coupCount}</color> 次";

            // 烈度
            float intensity = k != null ? FactionPoliticsManager.Instance.GetIntensity(k, kId) : 0f;
            if (_intensityFill != null)
            {
                var fr = _intensityFill.GetComponent<RectTransform>();
                fr.anchorMax = new Vector2(Mathf.Clamp01(intensity), 1f);
                fr.offsetMin = Vector2.zero;
                fr.offsetMax = Vector2.zero;
                _intensityFill.color = intensity >= 0.6f
                    ? new Color(0.97f, 0.35f, 0.35f, 1f)
                    : intensity >= 0.3f
                        ? new Color(0.97f, 0.65f, 0.40f, 1f)
                        : new Color(0.55f, 0.80f, 0.55f, 1f);
            }
            if (_intensityValText != null) _intensityValText.text = intensity.ToString("0.00");
            if (_intensityGovText != null)
            {
                _intensityGovText.text = govName + "执政";
                // 阶段2V：按党 Type 判色（显示名随位阶变——公室党/侯府党同为皇权系金色）
                var rulingRec = k != null ? FactionPoliticsManager.Instance.GetRulingFaction(k, kId) : null;
                var rt = rulingRec?.Type ?? FactionManager.FactionType.Custom;
                _intensityGovText.color = rt == FactionManager.FactionType.HuangQuan ? new Color(1f, 0.84f, 0f, 1f)
                    : rt == FactionManager.FactionType.WenGuan ? new Color(0.42f, 0.64f, 0.99f, 1f)
                    : rt == FactionManager.FactionType.WuGuan ? new Color(0.97f, 0.55f, 0.55f, 1f)
                    : rt == FactionManager.FactionType.TaiZi ? new Color(0.52f, 0.95f, 0.41f, 1f)
                    : new Color(0.79f, 0.63f, 1f, 1f);
            }
        }

        // ═══════════════════════ 底部页签 ═══════════════════════

        /// <summary>底部页签条（总览/成员/朝议；详情为成员下钻不设页签）。布局与配色同家族窗口。</summary>
        private void CreateBottomTabs()
        {
            try
            {
                var tabsObj = new GameObject("BottomTabs", typeof(RectTransform), typeof(HorizontalLayoutGroup));
                tabsObj.transform.SetParent(Bg, false);
                var tr = tabsObj.GetComponent<RectTransform>();
                tr.anchorMin = new Vector2(0.5f, 0);
                tr.anchorMax = new Vector2(0.5f, 0);
                tr.pivot = new Vector2(0.5f, 0);
                tr.anchoredPosition = new Vector2(0, 6);
                tr.sizeDelta = new Vector2(284f, 22f);
                var hlg = tabsObj.GetComponent<HorizontalLayoutGroup>();
                hlg.spacing = 10f;
                hlg.childControlWidth = false;
                hlg.childControlHeight = false;
                hlg.childForceExpandWidth = false;
                hlg.childForceExpandHeight = false;

                _tabOverview = CreateTabBtn(tabsObj.transform, "总览", () => ShowPage(0));
                _tabMembers = CreateTabBtn(tabsObj.transform, "成员", () => ShowPage(1));
                _tabCourt = CreateTabBtn(tabsObj.transform, IsNomadicCtx(CurrentKingdomId()) ? "库里尔台" : "朝议", () => ShowPage(2));
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[FactionWindow] 页签创建异常: {ex.Message}");
            }
        }

        private Text CreateTabBtn(Transform parent, string label, Action onClick)
        {
            var obj = new GameObject("Tab_" + label, typeof(RectTransform), typeof(Image), typeof(Button));
            obj.transform.SetParent(parent, false);
            var rt = obj.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(88f, 22f);
            var img = obj.GetComponent<Image>();
            img.color = TabOff;
            img.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            img.type = Image.Type.Sliced;
            obj.GetComponent<Button>().onClick.AddListener(() => onClick?.Invoke());

            Text t = CreateText(obj.transform, label, 10);
            t.alignment = TextAnchor.MiddleCenter;
            t.color = new Color(0.88f, 0.84f, 0.75f, 1f);
            var tRect = t.GetComponent<RectTransform>();
            tRect.anchorMin = Vector2.zero;
            tRect.anchorMax = Vector2.one;
            tRect.sizeDelta = Vector2.zero;
            return t;
        }

        // ═══════════════════════ 第一页：党派总览 ═══════════════════════

        private readonly List<GameObject> _overviewCards = new List<GameObject>();

        private void BuildOverview()
        {
            ClearChildren(_pageOverview, _overviewCards);
            string kId = CurrentKingdomId();
            RefreshHeader(kId);
            if (string.IsNullOrEmpty(kId))
            {
                AddPageHint(_pageOverview, _overviewCards, "请从王国信息窗口 · 党争 进入"); // 阶段2N W13：空态提示（原静默空白）
                return;
            }

            Kingdom k = null;
            try { k = World.world.kingdoms.get(int.Parse(kId)); } catch { }
            if (k == null || !k.isAlive())
            {
                AddPageHint(_pageOverview, _overviewCards, "该国已亡，党争烟消云散");
                return;
            }

            // 阶段2V：叛军无党争体制（草泽聚义，无庙堂可争）
            if (FactionManager.GetKingdomTierOf(k) == OfficeManager.KingdomTier.Zi
                || FactionManager.GetKingdomTierOf(k) == OfficeManager.KingdomTier.Bo
                || Domain.ZhuluRebelService.IsRebel(k))
            {
                RefreshHeader(kId);
                AddPageHint(_pageOverview, _overviewCards, Domain.ZhuluRebelService.IsRebel(k)
                    ? "<color=#f73939>叛军揭竿，未建章立制——无爵位、无百官、无党争。\n平定转正后方入天下体制。</color>"
                    : (IsNomadicCtx(kId) ? "部众未成部盟，尚无党争" : "国格未至侯位，尚无党争")); // 阶段3F
                SetPageHeight(_pageOverview, 84f);
                return;
            }

            var factions = FactionManager.Instance.GetAllFactions(kId);
            if (factions.Count == 0)
            {
                // 阶段2T-2：旧档空态 → 一键启用党争（建骨架+即时聚合，此后与新生世界同规则）
                Text t = CreateText(_pageOverview, "<color=#6a6458>本存档尚未启用党争（新世界自动生成）</color>", 10);
                _overviewCards.Add(t.gameObject);
                t.alignment = TextAnchor.MiddleCenter;
                var r = t.GetComponent<RectTransform>();
                r.anchorMin = new Vector2(0, 1);
                r.anchorMax = new Vector2(0, 1);
                r.pivot = new Vector2(0, 1);
                r.anchoredPosition = new Vector2(0, -10);
                r.sizeDelta = new Vector2(InnerWidth, 24f);

                GameObject enableBtn = CreateSmallButton(_pageOverview, "为此档启用党争", new Vector2(InnerWidth * 0.5f - 65f, -42f), true);
                enableBtn.GetComponent<RectTransform>().anchorMin = new Vector2(0, 1);
                enableBtn.GetComponent<RectTransform>().anchorMax = new Vector2(0, 1);
                enableBtn.GetComponent<RectTransform>().pivot = new Vector2(0, 1);
                _overviewCards.Add(enableBtn);
                enableBtn.GetComponent<Button>().onClick.AddListener(() =>
                {
                    FactionManager.Instance.BootstrapForExistingKingdoms();
                    RefreshPage();
                });
                SetPageHeight(_pageOverview, 84f);
                return;
            }

            var ruling = FactionPoliticsManager.Instance.GetRulingFaction(k, kId);
            foreach (var f in factions)
            {
                _overviewCards.Add(CreateFactionCard(k, kId, f, ruling));
            }
            float totalH = 14f;
            foreach (var f in factions)
            {
                bool empty = f.MemberIds == null || f.MemberIds.Count == 0;
                totalH += (empty ? 52f : 80f) + 4f; // 阶段2T-2：66→80（加储君/世仇行）
            }

            // 阶段2T-2：党争近事卡（Recent 滚动日志，最新在前）
            var st = FactionPoliticsManager.Instance.GetState(kId);
            int recentCount = st?.Recent?.Count ?? 0;
            if (recentCount > 0)
            {
                _overviewCards.Add(CreateRecentCard(st));
                totalH += 30f + recentCount * 15f + 6f;
            }
            SetPageHeight(_pageOverview, totalH);
        }

        private void SetPageHeight(Transform page, float height)
        {
            if (page == null) return;
            var r = page.GetComponent<RectTransform>();
            if (r != null) r.sizeDelta = new Vector2(InnerWidth, height);
        }

        private GameObject CreateFactionCard(Kingdom k, string kId, FactionManager.FactionRecord f, FactionManager.FactionRecord ruling)
        {
            float power = FactionPoliticsManager.Instance.ComputeFactionPower(k, kId, f);
            float maxPower = 1f;
            foreach (var other in FactionManager.Instance.GetAllFactions(kId))
            {
                float p = FactionPoliticsManager.Instance.ComputeFactionPower(k, kId, other);
                if (p > maxPower) maxPower = p;
            }
            float pct = maxPower > 0 ? Mathf.Clamp01(power / maxPower) : 0f;
            bool isRuling = ruling != null && ruling == f;
            bool isEmpty = f.MemberIds == null || f.MemberIds.Count == 0;
            string colorHex = GetFactionColorHex(f.Type);
            Color fillCol = ColorHex(colorHex);
            float cardH = isEmpty ? 52f : 80f;

            // 公会风卡（Row 容器纯色底 -36 + 贴图层同延）：40 大徽章 + Text.color 党名（零富文本，绝对可靠）
            GameObject card = new GameObject("FactionCard", typeof(RectTransform));
            card.transform.SetParent(_pageOverview, false);
            var cr = card.GetComponent<RectTransform>();
            cr.sizeDelta = new Vector2(InnerWidth, cardH);
            cr.anchoredPosition = Vector2.zero;

            // 底
            GameObject bg = new GameObject("Bg", typeof(RectTransform), typeof(Image));
            bg.transform.SetParent(card.transform, false);
            var bgr = bg.GetComponent<RectTransform>();
            bgr.anchorMin = Vector2.zero; bgr.anchorMax = Vector2.one;
            bgr.offsetMin = new Vector2(-36f, 0f); bgr.offsetMax = Vector2.zero;
            if (isRuling)
                bg.GetComponent<Image>().color = new Color(0.21f, 0.18f, 0.10f, 1f);
            else if (isEmpty)
                bg.GetComponent<Image>().color = new Color(0.10f, 0.10f, 0.13f, 1f);
            else
                bg.GetComponent<Image>().color = new Color(0.13f, 0.14f, 0.19f, 1f);
            try
            {
                var sp = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
                if (sp != null) { bg.GetComponent<Image>().sprite = sp; bg.GetComponent<Image>().type = Image.Type.Sliced; }
            }
            catch { }

            // 执政金边
            if (isRuling)
            {
                GameObject fb = new GameObject("GoldFrame", typeof(RectTransform), typeof(Image));
                fb.transform.SetParent(card.transform, false);
                var fbr = fb.GetComponent<RectTransform>();
                fbr.anchorMin = Vector2.zero; fbr.anchorMax = Vector2.one;
                fbr.offsetMin = new Vector2(-36f, 0f); fbr.offsetMax = Vector2.zero; // 阶段2N W13：随卡片 Bg 左延 -36（原 0 断边）
                fb.GetComponent<Image>().color = new Color(1f, 0.84f, 0f, 0.85f);
                fb.GetComponent<Image>().fillCenter = false;
                var fimg = fb.GetComponent<Image>();
                fimg.type = Image.Type.Sliced;
                try { fimg.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement"); } catch { }
            }

            // 党徽 40×40（左侧居中，公会风大徽章）
            GameObject badgeBg = new GameObject("FBadgeBg", typeof(RectTransform), typeof(Image));
            badgeBg.transform.SetParent(card.transform, false);
            var bbg = badgeBg.GetComponent<RectTransform>();
            bbg.anchorMin = new Vector2(0, 0.5f); bbg.anchorMax = new Vector2(0, 0.5f);
            bbg.pivot = new Vector2(0, 0.5f);
            bbg.anchoredPosition = new Vector2(8, 0);
            bbg.sizeDelta = new Vector2(40f, 40f);
            badgeBg.GetComponent<Image>().color = new Color(0.09f, 0.11f, 0.15f, 1f);

            GameObject iconObj = new GameObject("FIcon", typeof(RectTransform), typeof(Image));
            iconObj.transform.SetParent(badgeBg.transform, false);
            var icRect = iconObj.GetComponent<RectTransform>();
            icRect.anchorMin = new Vector2(0.5f, 0.5f); icRect.anchorMax = new Vector2(0.5f, 0.5f);
            icRect.pivot = new Vector2(0.5f, 0.5f);
            icRect.anchoredPosition = Vector2.zero;
            icRect.sizeDelta = new Vector2(32f, 32f);
            var icImg = iconObj.GetComponent<Image>();
            Sprite icon = GetFactionIcon(f.Type);
            if (icon != null) icImg.sprite = icon;
            else icImg.color = new Color(0.35f, 0.38f, 0.48f, 1f);

            // 党名：12px + Text.color（牌匾同款字号 12px 已验证可渲染；禁用 Bold 与 15px 大字）
            string fShow = FactionManager.GetFactionDisplayName(k, f); // 阶段2V：显示名按位阶
            Text name = CreateText(card.transform, fShow, 12);
            name.color = fillCol;
            name.alignment = TextAnchor.MiddleLeft;
            var nr = name.GetComponent<RectTransform>();
            nr.anchorMin = new Vector2(0, 1); nr.anchorMax = new Vector2(0, 1);
            nr.pivot = new Vector2(0, 1);
            nr.anchoredPosition = new Vector2(56, -6);
            nr.sizeDelta = new Vector2(170f, 18f);
            name.raycastTarget = false;

            if (!isEmpty)
            {
                // 党魁行
                string leaderName = f.LeaderId != 0 ? GetActorName(f.LeaderId) : "待定";
                string lTitle = "";
                if (f.LeaderId != 0)
                {
                    var lo = FindOfficeRecord(kId, f.LeaderId);
                    if (lo != null)
                    {
                        try { lTitle = OfficeManager.GetOfficeTitleForKingdom(lo.OfficeId, k); } catch { lTitle = lo.OfficeId; }
                    }
                }
                Text leader = CreateText(card.transform, fShow + " · 党魁：" + leaderName + (lTitle.Length > 0 ? " · " + lTitle : ""), 9);
                leader.color = new Color(0.79f, 0.83f, 1f, 1f);
                leader.alignment = TextAnchor.MiddleLeft;
                var lr2 = leader.GetComponent<RectTransform>();
                lr2.anchorMin = new Vector2(0, 0.5f); lr2.anchorMax = new Vector2(0, 0.5f);
                lr2.pivot = new Vector2(0, 0.5f);
                lr2.anchoredPosition = new Vector2(56, -8);
                lr2.sizeDelta = new Vector2(220f, 13f);
                leader.raycastTarget = false;

                // 阶段2T-2：第二行信息（太子党显储君 / 皇权党显皇帝亲掌 / 其余显世仇或成立年）
                string subLine = "";
                if (f.Type == FactionManager.FactionType.TaiZi)
                {
                    long heirId = 0;
                    try { long.TryParse(Persistence.KingdomDataExt.GetStr(k, "heir_id"), out heirId); } catch { }
                    if (heirId != 0 && f.MemberIds.Contains(heirId))
                        subLine = $"<color={UI.ZhuluUIStyle.Hex.Positive}>储君 {GetActorName(heirId)} 在党</color>";
                    else
                        subLine = "<color=#6a6458>未立储 · 宗藩观望</color>";
                }
                else if (f.Type == FactionManager.FactionType.HuangQuan)
                {
                    bool nomadic = k != null && KingdomCivilizationService.Instance.IsNomadic(k);
                    string rulerTitle = nomadic ? RegimeManager.GetKingTitle(k) : "皇帝";
                    subLine = $"<color=#ffd700>{rulerTitle}亲掌 · 简拔亲信入党</color>";
                }
                else
                {
                    string feudLine = BuildFeudText(kId, k, f);
                    subLine = feudLine.Length > 0 ? feudLine : $"<color=#6a6458>立于建元{f.FoundedYear}年</color>";
                }
                Text sub2 = CreateText(card.transform, subLine, 8);
                sub2.alignment = TextAnchor.MiddleLeft;
                var s2r = sub2.GetComponent<RectTransform>();
                s2r.anchorMin = new Vector2(0, 0.5f); s2r.anchorMax = new Vector2(0, 0.5f);
                s2r.pivot = new Vector2(0, 0.5f);
                s2r.anchoredPosition = new Vector2(56, -22);
                s2r.sizeDelta = new Vector2(252f, 12f);
                sub2.raycastTarget = false;

                // 底行：影响力标签 + 条 + 数值 + 成员数
                Text pLabel = CreateText(card.transform, "影响力", 7);
                pLabel.color = new Color(0.62f, 0.60f, 0.55f, 1f);
                pLabel.alignment = TextAnchor.MiddleLeft;
                var pl = pLabel.GetComponent<RectTransform>();
                pl.anchorMin = new Vector2(0, 0.5f); pl.anchorMax = new Vector2(0, 0.5f);
                pl.pivot = new Vector2(0, 0.5f);
                pl.anchoredPosition = new Vector2(56, -38);
                pl.sizeDelta = new Vector2(40f, 11f);

                GameObject pBg = new GameObject("PBarBg", typeof(RectTransform), typeof(Image));
                pBg.transform.SetParent(card.transform, false);
                var pbr = pBg.GetComponent<RectTransform>();
                pbr.anchorMin = new Vector2(0, 0.5f); pbr.anchorMax = new Vector2(0, 0.5f);
                pbr.pivot = new Vector2(0, 0.5f);
                pbr.anchoredPosition = new Vector2(98, -38);
                pbr.sizeDelta = new Vector2(92f, 6f);
                pBg.GetComponent<Image>().color = new Color(0.08f, 0.09f, 0.12f, 1f);

                GameObject pFill = new GameObject("PBarFill", typeof(RectTransform), typeof(Image));
                pFill.transform.SetParent(pBg.transform, false);
                var pfr = pFill.GetComponent<RectTransform>();
                pfr.anchorMin = Vector2.zero; pfr.anchorMax = new Vector2(pct, 1f);
                pfr.offsetMin = Vector2.zero; pfr.offsetMax = Vector2.zero;
                pFill.GetComponent<Image>().color = fillCol;

                Text pNum = CreateText(card.transform, ((int)power).ToString(), 9);
                pNum.color = new Color(0.79f, 0.83f, 1f, 1f);
                pNum.alignment = TextAnchor.MiddleCenter;
                var pnr = pNum.GetComponent<RectTransform>();
                pnr.anchorMin = new Vector2(0, 0.5f); pnr.anchorMax = new Vector2(0, 0.5f);
                pnr.pivot = new Vector2(0, 0.5f);
                pnr.anchoredPosition = new Vector2(194, -38);
                pnr.sizeDelta = new Vector2(28f, 12f);

                Text cnt = CreateText(card.transform, "成员 " + f.MemberIds.Count + " 人", 8);
                cnt.color = new Color(0.62f, 0.60f, 0.55f, 1f);
                cnt.alignment = TextAnchor.MiddleLeft;
                var cnr = cnt.GetComponent<RectTransform>();
                cnr.anchorMin = new Vector2(0, 0.5f); cnr.anchorMax = new Vector2(0, 0.5f);
                cnr.pivot = new Vector2(0, 0.5f);
                cnr.anchoredPosition = new Vector2(228, -38);
                cnr.sizeDelta = new Vector2(80f, 11f);
            }
            else
            {
                Text emptyTag = CreateText(card.transform, fShow + " · 空壳党 · 无成员", 9);
                emptyTag.color = new Color(0.55f, 0.58f, 0.68f, 1f);
                emptyTag.alignment = TextAnchor.MiddleLeft;
                var er2 = emptyTag.GetComponent<RectTransform>();
                er2.anchorMin = new Vector2(0, 0.5f); er2.anchorMax = new Vector2(0, 0.5f);
                er2.pivot = new Vector2(0, 0.5f);
                er2.anchoredPosition = new Vector2(56, -8);
                er2.sizeDelta = new Vector2(160f, 13f);
            }

            // 执政金章（右上）
            if (isRuling)
            {
                GameObject bg2 = new GameObject("RulingBg", typeof(RectTransform), typeof(Image));
                bg2.transform.SetParent(card.transform, false);
                var bg2r = bg2.GetComponent<RectTransform>();
                bg2r.anchorMin = new Vector2(1, 1); bg2r.anchorMax = new Vector2(1, 1);
                bg2r.pivot = new Vector2(1, 1);
                bg2r.anchoredPosition = new Vector2(-8, -6);
                bg2r.sizeDelta = new Vector2(34f, 14f);
                bg2.GetComponent<Image>().color = new Color(1f, 0.84f, 0f, 1f);

                Text badge = CreateText(bg2.transform, "执政", 8);
                badge.alignment = TextAnchor.MiddleCenter;
                var bRect = badge.GetComponent<RectTransform>();
                bRect.anchorMin = Vector2.zero; bRect.anchorMax = Vector2.one;
                bRect.offsetMin = Vector2.zero; bRect.offsetMax = Vector2.zero;
                badge.color = new Color(0.16f, 0.11f, 0f, 1f);
                badge.raycastTarget = false;
            }

            // 整卡点击 → 成员页
            GameObject clickLayer = new GameObject("ClickLayer", typeof(RectTransform), typeof(Image), typeof(Button));
            clickLayer.transform.SetParent(card.transform, false);
            var clr = clickLayer.GetComponent<RectTransform>();
            clr.anchorMin = Vector2.zero; clr.anchorMax = Vector2.one;
            clr.offsetMin = Vector2.zero; clr.offsetMax = Vector2.zero;
            var clImg = clickLayer.GetComponent<Image>();
            clImg.color = new Color(1f, 1f, 1f, 0.01f);   // 透明可点
            var btn = clickLayer.GetComponent<Button>();
            FactionManager.FactionRecord captured = f;
            btn.onClick.AddListener(() =>
            {
                _currentFaction = captured;
                ShowPage(1);
            });

            return card;
        }


        /// <summary>阶段2T-2：党争近事卡（Recent 滚动日志最新在前，上限 8 条）。</summary>
        private GameObject CreateRecentCard(FactionPoliticsManager.PoliticsState st)
        {
            int n = st.Recent.Count;
            float h = 26f + n * 15f;
            GameObject card = new GameObject("RecentCard", typeof(RectTransform));
            card.transform.SetParent(_pageOverview, false);
            var cr = card.GetComponent<RectTransform>();
            cr.sizeDelta = new Vector2(InnerWidth, h);
            cr.anchoredPosition = Vector2.zero;

            GameObject bg = new GameObject("Bg", typeof(RectTransform), typeof(Image));
            bg.transform.SetParent(card.transform, false);
            var bgr = bg.GetComponent<RectTransform>();
            bgr.anchorMin = Vector2.zero; bgr.anchorMax = Vector2.one;
            bgr.offsetMin = new Vector2(-36f, 0f); bgr.offsetMax = Vector2.zero;
            bg.GetComponent<Image>().color = new Color(0.12f, 0.13f, 0.17f, 1f);
            try
            {
                var sp = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
                if (sp != null) { bg.GetComponent<Image>().sprite = sp; bg.GetComponent<Image>().type = Image.Type.Sliced; }
            }
            catch { }

            Text title = CreateText(card.transform, "<color=#ffd8d9>⚑ 党争近事</color>", 10);
            title.alignment = TextAnchor.MiddleLeft;
            title.raycastTarget = false;
            var tr = title.GetComponent<RectTransform>();
            tr.anchorMin = new Vector2(0, 1); tr.anchorMax = new Vector2(0, 1);
            tr.pivot = new Vector2(0, 1);
            tr.anchoredPosition = new Vector2(10, -4);
            tr.sizeDelta = new Vector2(160f, 14f);

            for (int i = 0; i < n; i++)
            {
                Text line = CreateText(card.transform, $"<color=#8a8578>{st.Recent[i]}</color>", 8);
                line.alignment = TextAnchor.MiddleLeft;
                line.raycastTarget = false;
                var lr = line.GetComponent<RectTransform>();
                lr.anchorMin = new Vector2(0, 1); lr.anchorMax = new Vector2(0, 1);
                lr.pivot = new Vector2(0, 1);
                lr.anchoredPosition = new Vector2(10, -20 - i * 15f);
                lr.sizeDelta = new Vector2(InnerWidth - 20f, 14f);
            }
            return card;
        }

        private static string GetFactionColorHex(FactionManager.FactionType type)
        {
            switch (type)
            {
                case FactionManager.FactionType.HuangQuan: return "#ffd700";
                case FactionManager.FactionType.WenGuan: return "#6ca3fd";
                case FactionManager.FactionType.WuGuan: return "#f78d8d";
                case FactionManager.FactionType.TaiZi: return "#70d468";
                default: return "#c9a0ff";
            }
        }

        private static Color ColorHex(string hex)
        {
            Color c;
            return ColorUtility.TryParseHtmlString(hex, out c) ? c : Color.gray;
        }

        private static Sprite GetFactionIcon(FactionManager.FactionType type)
        {
            try
            {
                string key;
                switch (type)
                {
                    case FactionManager.FactionType.HuangQuan: key = "factions/zlr_faction_huangquan_32"; break;
                    case FactionManager.FactionType.WenGuan: key = "factions/zlr_faction_wenguan_32"; break;
                    case FactionManager.FactionType.WuGuan: key = "factions/zlr_faction_wuguan_32"; break;
                    case FactionManager.FactionType.TaiZi: key = "factions/zlr_faction_taizi_32"; break;
                    default: key = "factions/zlr_faction_huangquan_32"; break;
                }
                var sp = ZhuluAssetLoader.Instance.GetSprite(key);
                return sp;
            }
            catch { return null; }
        }

        private void ClearChildren(Transform parent, List<GameObject> list)
        {
            foreach (var g in list)
            {
                if (g != null)
                {
                    if (g.activeSelf) g.SetActive(false);
                    Destroy(g);
                }
            }
            list.Clear();
        }

        // ═══════════════════════ 第二页：成员列表 ═══════════════════════

        private readonly List<GameObject> _memberRows = new List<GameObject>();
        // 页头/世仇/空壳提示（历史教训：不清理会叠加，点谁谁在最上层）
        private readonly List<GameObject> _memberHeaders = new List<GameObject>();

        private void BuildMembers()
        {
            ClearChildren(_pageMembers, _memberRows);
            ClearChildren(_pageMembers, _memberHeaders);
            string kId = CurrentKingdomId();
            RefreshHeader(kId);
            if (string.IsNullOrEmpty(kId) || _currentFaction == null)
            {
                AddPageHint(_pageMembers, _memberRows, "请先从党派总览选择一个党派"); // 阶段2N W13：空态提示
                return;
            }

            Kingdom k = null;
            try { k = World.world.kingdoms.get(int.Parse(kId)); } catch { }
            if (k == null || !k.isAlive())
            {
                AddPageHint(_pageMembers, _memberRows, "该国已亡，党众星散");
                return;
            }

            // 页头：党名 · 成员N · 世仇
            string colorHex = GetFactionColorHex(_currentFaction.Type);
            Text head = CreateText(_pageMembers, $"<color={colorHex}>{FactionManager.GetFactionDisplayName(k, _currentFaction)}</color> · 成员 {_currentFaction.MemberIds.Count} 人", 10);
            head.alignment = TextAnchor.MiddleLeft;
            var hr = head.GetComponent<RectTransform>();
            hr.anchorMin = new Vector2(0, 1); hr.anchorMax = new Vector2(0, 1);
            hr.pivot = new Vector2(0, 1);
            hr.anchoredPosition = new Vector2(10, -6);
            hr.sizeDelta = new Vector2(200f, 16f);
            head.raycastTarget = false;
            _memberHeaders.Add(head.gameObject);

            // 世仇串
            var feuds = BuildFeudText(kId, k, _currentFaction);
            if (feuds.Length > 0)
            {
                Text ft = CreateText(_pageMembers, feuds, 8);
                ft.alignment = TextAnchor.MiddleLeft;
                var fr = ft.GetComponent<RectTransform>();
                fr.anchorMin = new Vector2(0, 1); fr.anchorMax = new Vector2(0, 1);
                fr.pivot = new Vector2(0, 1);
                fr.anchoredPosition = new Vector2(10, -24);
                fr.sizeDelta = new Vector2(InnerWidth - 20f, 12f);
                ft.raycastTarget = false;
                _memberHeaders.Add(ft.gameObject);
            }

            var st = FactionPoliticsManager.Instance.GetState(kId);
            var members = _currentFaction.MemberIds ?? new List<long>();
            if (members.Count == 0)
            {
                Text empty = CreateText(_pageMembers, "<color=#5a5e70>空壳党 · 无成员（党籍永不消散）</color>", 10);
                empty.alignment = TextAnchor.MiddleCenter;
                var er = empty.GetComponent<RectTransform>();
                er.anchorMin = new Vector2(0, 1); er.anchorMax = new Vector2(0, 1);
                er.pivot = new Vector2(0, 1);
                er.anchoredPosition = new Vector2(0, -50);
                er.sizeDelta = new Vector2(InnerWidth, 22f);
                _memberHeaders.Add(empty.gameObject);
                return;
            }

            // 阶段2T-2：按权势降序 + 党魁置顶（原按聚合顺序，权臣沉底难找）
            long kingId = 0, heirId = 0;
            try { if (k.king != null) kingId = k.king.getID(); } catch { }
            try { long.TryParse(Persistence.KingdomDataExt.GetStr(k, "heir_id"), out heirId); } catch { }
            var sorted = new List<long>(members);
            sorted.Sort((a, b) => FactionPoliticsManager.GetMemberPowerPublic(kId, b).CompareTo(FactionPoliticsManager.GetMemberPowerPublic(kId, a)));
            if (_currentFaction.LeaderId != 0 && sorted.Contains(_currentFaction.LeaderId))
            {
                sorted.Remove(_currentFaction.LeaderId);
                sorted.Insert(0, _currentFaction.LeaderId);
            }

            float y = -42f;
            foreach (var mid in sorted)
            {
                Actor a = GetActor(mid);
                GameObject row = new GameObject("MemberRow", typeof(RectTransform));
                row.transform.SetParent(_pageMembers, false);
                var rr = row.GetComponent<RectTransform>();
                rr.anchorMin = new Vector2(0, 1); rr.anchorMax = new Vector2(0, 1);
                rr.pivot = new Vector2(0, 1);
                rr.anchoredPosition = new Vector2(0, y);
                rr.sizeDelta = new Vector2(InnerWidth, 30f);

                // 底
                GameObject rowBg = new GameObject("RowBg", typeof(RectTransform), typeof(Image));
                rowBg.transform.SetParent(row.transform, false);
                var rbr = rowBg.GetComponent<RectTransform>();
                rbr.anchorMin = Vector2.zero; rbr.anchorMax = Vector2.one;
                rbr.offsetMin = new Vector2(-36f, 0f); rbr.offsetMax = Vector2.zero;
                rowBg.GetComponent<Image>().color = new Color(0.13f, 0.14f, 0.19f, 0.9f);

                // 动态小人 22×22
                Transform slot = CreateAvatarSlot(row.transform, 22f);
                var slotRect = slot.GetComponent<RectTransform>();
                slotRect.anchorMin = new Vector2(0, 0.5f);
                slotRect.anchorMax = new Vector2(0, 0.5f);
                slotRect.pivot = new Vector2(0, 0.5f);
                slotRect.anchoredPosition = new Vector2(8, 0);
                slotRect.sizeDelta = new Vector2(22f, 22f);
                SetAvatar(slot, a, a != null && a.getName().Length > 0 ? a.getName().Substring(0, 1) : "?", false);

                // 姓名 + 徽章（阶段2T-2：君=金 储=绿 魁=党色）
                string badge = "";
                if (mid == kingId) badge = "<color=#ffd700>君</color> ";
                else if (mid == heirId) badge = "<color=#70d468>储</color> ";
                else if (mid == _currentFaction.LeaderId) badge = $"<color={GetFactionColorHex(_currentFaction.Type)}>魁</color> ";
                Text nameT = CreateText(row.transform, badge + (a != null ? a.getName() : "无名氏"), 11);
                nameT.alignment = TextAnchor.MiddleLeft;
                var nr = nameT.GetComponent<RectTransform>();
                nr.anchorMin = new Vector2(0, 0.5f); nr.anchorMax = new Vector2(0, 0.5f);
                nr.pivot = new Vector2(0, 0.5f);
                nr.anchoredPosition = new Vector2(36, 0);
                nr.sizeDelta = new Vector2(76f, 16f);
                nameT.color = new Color(0.79f, 0.83f, 1f, 1f);
                nameT.raycastTarget = false;

                // 官职（阶段2W：兜底君主/储君/城主/身份——原一律"布衣"）
                var office = FindOfficeRecord(kId, mid); // 权势查询仍需官职记录
                string title = GetActorDisplayTitle(k, kId, mid);
                Text officeT = CreateText(row.transform, title, 10);
                officeT.alignment = TextAnchor.MiddleLeft;
                var orr = officeT.GetComponent<RectTransform>();
                orr.anchorMin = new Vector2(0, 0.5f); orr.anchorMax = new Vector2(0, 0.5f);
                orr.pivot = new Vector2(0, 0.5f);
                orr.anchoredPosition = new Vector2(108, 0);
                orr.sizeDelta = new Vector2(82f, 15f);
                officeT.color = new Color(0.62f, 0.60f, 0.55f, 1f);
                officeT.raycastTarget = false;

                // 亲信（绿）+ 权势（红）合并右对齐标签（右 8~128 → 左界 206，与官职右界 204 不重叠）
                int favor = st != null && st.Favor.TryGetValue(mid, out int fv) ? fv : 0;
                string fgrade = FactionPoliticsManager.GetFavorGrade(favor);
                float mp = 0f;
                if (office != null) mp = CorruptionManager.Instance.GetPower(kId, office.OfficeId);
                Text mergeT = CreateText(row.transform, $"<color={UI.ZhuluUIStyle.Hex.Positive}>{favor} {fgrade}</color> <color=#f78d8d>{mp:0}</color>", 10);
                mergeT.alignment = TextAnchor.MiddleRight;
                var mrr = mergeT.GetComponent<RectTransform>();
                mrr.anchorMin = new Vector2(1, 0.5f); mrr.anchorMax = new Vector2(1, 0.5f);
                mrr.pivot = new Vector2(1, 0.5f);
                mrr.anchoredPosition = new Vector2(-8, 0);
                mrr.sizeDelta = new Vector2(116f, 15f);
                mergeT.raycastTarget = false;

                // 点击 → 详情页
                GameObject clickLayer = new GameObject("ClickLayer", typeof(RectTransform), typeof(Image), typeof(Button));
                clickLayer.transform.SetParent(row.transform, false);
                var clr = clickLayer.GetComponent<RectTransform>();
                clr.anchorMin = Vector2.zero; clr.anchorMax = Vector2.one;
                clr.offsetMin = Vector2.zero; clr.offsetMax = Vector2.zero;
                var clImg = clickLayer.GetComponent<Image>();
                clImg.color = new Color(1f, 1f, 1f, 0.01f);
                long captured = mid;
                clickLayer.GetComponent<Button>().onClick.AddListener(() =>
                {
                    _currentActorId = captured;
                    ShowPage(3); // 详情（朝议页占用 index 2 后详情移至 3）
                });

                _memberRows.Add(row);
                y -= 30f;
            }
            SetPageHeight(_pageMembers, 42f + members.Count * 30f + 8f); // 阶段2N W13：行高 28→30 对齐实际行距（原末行被裁）
        }

        private string BuildFeudText(string kId, Kingdom k, FactionManager.FactionRecord f)
        {
            var st = FactionPoliticsManager.Instance.GetState(kId);
            if (st == null) return "";
            var parts = new List<string>();
            foreach (var other in FactionManager.Instance.GetAllFactions(kId))
            {
                if (other == f) continue;
                int v = FactionPoliticsManager.GetFeud(st, f.Name, other.Name);
                if (v > 0) parts.Add($"与{FactionManager.GetFactionDisplayName(k, other)}世仇 <color={UI.ZhuluUIStyle.Hex.Negative}>{v}</color>"); // 阶段2V：显示名
            }
            return parts.Count > 0 ? string.Join(" · ", parts) : "";
        }

        private OfficeAppointmentRecord FindOfficeRecord(string kId, long actorId)
        {
            try
            {
                var records = OfficeManager.Instance.GetRecordsPublic(kId);
                if (records == null) return null;
                foreach (var r in records)
                {
                    if (long.TryParse(r.PersonId, out long pid) && pid == actorId) return r;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 阶段2W：成员称号兜底。原只查 OfficeManager 记录（中央/地方官），查不到一律"布衣"——
        /// 皇帝/储君/城主（队长）等非中央官全躺枪（用户实报"皇帝还显示布衣"）。
        /// 优先级：官职 ＞ 君主 ＞ 储君 ＞ 城主 ＞ 身份名。
        /// </summary>
        private string GetActorDisplayTitle(Kingdom k, string kId, long actorId)
        {
            var office = FindOfficeRecord(kId, actorId);
            if (office != null)
            {
                try { return OfficeManager.GetOfficeTitleForKingdom(office.OfficeId, k); } catch { }
            }
            int effCities = k.cities != null ? k.cities.Count : 0;
            try { if (k.king != null && k.king.isAlive() && k.king.getID() == actorId) return RegimeManager.GetKingTitle(k); } catch { }
            try
            {
                string heirStr = Persistence.KingdomDataExt.GetStr(k, "heir_id");
                if (!string.IsNullOrEmpty(heirStr) && long.TryParse(heirStr, out long heirId) && heirId == actorId)
                    return RegimeManager.GetHeirTitle(k);
            }
            catch { }
            try
            {
                if (k.cities != null)
                {
                    foreach (var c in k.cities)
                    {
                        if (c != null && c.leader != null && c.leader.getID() == actorId) return "城主";
                    }
                }
            }
            catch { }
            try { return IdentityClassifier.GetIdentityName(IdentityClassifier.Instance.GetIdentity(actorId)); } catch { }
            return "国人";
        }

        // ═══════════════════════ 第三页：朝议 ═══════════════════════

        private readonly List<GameObject> _courtCards = new List<GameObject>();
        private float _courtCursor = 0f;

        /// <summary>
        /// 朝议页（2026-08-19 新增）：上=近年朝议（Recent 过滤"朝议「"条目，最新在前），
        /// 下=历代朝议存档（史册 CourtDebate 条目按年份倒序，最多 15 条），底部说明行。
        /// </summary>
        private void BuildCourtPage()
        {
            ClearChildren(_pageCourt, _courtCards);
            _courtCursor = 0f;
            string kId = CurrentKingdomId();
            RefreshHeader(kId);
            if (string.IsNullOrEmpty(kId))
            {
                AddPageHint(_pageCourt, _courtCards, "请从王国信息窗口 · 党争 进入");
                return;
            }

            // ── 区1：近年朝议（Recent 滚动日志过滤，最新在前；非朝议近事不显示）──
            AddCourtSectionTitle(IsNomadicCtx(kId) ? "⚖ 近年库里尔台" : "⚖ 近年朝议"); // 阶段3H2
            var recentDebates = new List<string>();
            try
            {
                var st = FactionPoliticsManager.Instance.GetState(kId);
                if (st?.Recent != null)
                {
                    foreach (var r in st.Recent)
                    {
                        if (!string.IsNullOrEmpty(r) && (r.Contains("朝议「") || r.Contains("库里尔台「")))
                            recentDebates.Add(r);
                    }
                }
            }
            catch (Exception ex) { ZhuluContext.Instance.LogWarning($"[FactionWindow] 朝议近事读取异常: {ex.Message}"); }
            if (recentDebates.Count == 0)
            {
                AddCourtHint(IsNomadicCtx(kId) ? "近年暂无库里尔台" : "近年暂无朝议");
            }
            else
            {
                foreach (var entry in recentDebates)
                {
                    AddCourtEntryCard(ColorizeCourtYear(entry));
                }
            }

            // ── 区2：历代朝议存档（史册 CourtDebate 条目，年份倒序，最近 15 条）──
            var archive = new List<ChronicleEntry>();
            try
            {
                var all = ChronicleManager.Instance.GetKingdomChronicle(kId);
                if (all != null)
                {
                    foreach (var e in all)
                    {
                        if (e != null && e.Type == ChronicleEventType.CourtDebate)
                            archive.Add(e);
                    }
                }
                archive = archive.OrderByDescending(e => e.Year).ToList(); // 年份倒序（每年至多一场）
            }
            catch (Exception ex) { ZhuluContext.Instance.LogWarning($"[FactionWindow] 朝议存档读取异常: {ex.Message}"); }

            AddCourtSectionTitle($"📜 历代朝议存档（{archive.Count}）");
            if (archive.Count == 0)
            {
                AddCourtHint("尚无朝议记录——需两党以上并存且时间推进");
            }
            else
            {
                int show = Math.Min(archive.Count, 15);
                for (int i = 0; i < show; i++)
                {
                    var e = archive[i];
                    AddCourtEntryCard($"<color=#ffd8d9>建元{e.Year}年</color> {e.ContentText}");
                }
                if (archive.Count > 15)
                    AddCourtHint("……更早的朝议已入史册");
            }

            // ── 底部说明行 ──
            AddCourtHint(IsNomadicCtx(kId)
                ? "库里尔台：每部每年至多一场，诸派按党势与考课对决，胜负落地为真实政策效果"
                : "朝议：每国每年至多一场，两党按党势与考课对决，胜负落地为真实政策效果");
            SetPageHeight(_pageCourt, -_courtCursor + 8f);
        }

        /// <summary>朝议页区标题行。</summary>
        private void AddCourtSectionTitle(string title)
        {
            Text t = CreateText(_pageCourt, $"<color=#ffd8d9>{title}</color>", 10);
            t.alignment = TextAnchor.MiddleLeft;
            t.raycastTarget = false;
            var r = t.GetComponent<RectTransform>();
            r.anchorMin = new Vector2(0, 1);
            r.anchorMax = new Vector2(0, 1);
            r.pivot = new Vector2(0, 1);
            r.anchoredPosition = new Vector2(10, _courtCursor - 2);
            r.sizeDelta = new Vector2(InnerWidth - 20f, 14f);
            _courtCards.Add(t.gameObject);
            _courtCursor -= 20f;
        }

        /// <summary>朝议页灰字提示行（空态/截断/底部说明）。</summary>
        private void AddCourtHint(string text)
        {
            Text t = CreateText(_pageCourt, $"<color=#8a8578>{text}</color>", 8);
            t.alignment = TextAnchor.MiddleLeft;
            t.raycastTarget = false;
            var r = t.GetComponent<RectTransform>();
            r.anchorMin = new Vector2(0, 1);
            r.anchorMax = new Vector2(0, 1);
            r.pivot = new Vector2(0, 1);
            r.anchoredPosition = new Vector2(10, _courtCursor);
            r.sizeDelta = new Vector2(InnerWidth - 20f, 12f);
            _courtCards.Add(t.gameObject);
            _courtCursor -= 16f;
        }

        /// <summary>朝议条目卡：深底 + 左侧暗金 3px 边条 + 实测行高（Wrap 换行防截断，同详情块量高法）。</summary>
        private void AddCourtEntryCard(string richText)
        {
            GameObject card = new GameObject("CourtCard", typeof(RectTransform));
            card.transform.SetParent(_pageCourt, false);
            var cr = card.GetComponent<RectTransform>();
            cr.anchorMin = new Vector2(0, 1);
            cr.anchorMax = new Vector2(0, 1);
            cr.pivot = new Vector2(0, 1);
            cr.anchoredPosition = new Vector2(0, _courtCursor);

            // 卡底（-36 左延被 RectMask2D 裁齐左缘——同近事卡）
            GameObject bg = new GameObject("Bg", typeof(RectTransform), typeof(Image));
            bg.transform.SetParent(card.transform, false);
            var bgr = bg.GetComponent<RectTransform>();
            bgr.anchorMin = Vector2.zero;
            bgr.anchorMax = Vector2.one;
            bgr.offsetMin = new Vector2(-36f, 0f);
            bgr.offsetMax = Vector2.zero;
            bg.GetComponent<Image>().color = new Color(0.12f, 0.13f, 0.17f, 1f);
            try
            {
                var sp = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
                if (sp != null) { bg.GetComponent<Image>().sprite = sp; bg.GetComponent<Image>().type = Image.Type.Sliced; }
            }
            catch { }

            // 左侧暗金边条 3px（#c8b98a 朝议主题色；-2 落在可见卡缘——若按底图同延 -36 会整条裁掉不可见）
            GameObject edge = new GameObject("CourtEdge", typeof(RectTransform), typeof(Image));
            edge.transform.SetParent(card.transform, false);
            var er = edge.GetComponent<RectTransform>();
            er.anchorMin = new Vector2(0, 0);
            er.anchorMax = new Vector2(0, 1);
            er.pivot = new Vector2(0, 0.5f);
            er.anchoredPosition = new Vector2(-2f, 0);
            er.sizeDelta = new Vector2(3f, 0);
            var edgeImg = edge.GetComponent<Image>();
            edgeImg.color = ColorHex("#c8b98a");
            edgeImg.raycastTarget = false;

            // 正文（先量高再定卡高，防止 Wrap 自动换行被截断）
            Text body = CreateText(card.transform, richText, 9);
            body.alignment = TextAnchor.MiddleLeft;
            body.horizontalOverflow = HorizontalWrapMode.Wrap;
            body.verticalOverflow = VerticalWrapMode.Overflow;
            body.color = new Color(0.85f, 0.83f, 0.78f, 1f);
            body.raycastTarget = false;
            var br = body.GetComponent<RectTransform>();
            br.anchorMin = new Vector2(0, 1);
            br.anchorMax = new Vector2(0, 1);
            br.pivot = new Vector2(0, 1);
            br.anchoredPosition = new Vector2(10, -4);
            br.sizeDelta = new Vector2(InnerWidth - 20f, 14f);

            float bodyH;
            try
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(br);
                bodyH = Mathf.Max(14f, body.preferredHeight);
            }
            catch { bodyH = 14f; }
            float h = bodyH + 8f;
            cr.sizeDelta = new Vector2(InnerWidth, h);
            br.sizeDelta = new Vector2(InnerWidth - 20f, bodyH);

            _courtCards.Add(card);
            _courtCursor -= h + 4f;
        }

        /// <summary>近事条目年份着色（"建元X年 …"前缀高亮浅粉，其余浅米色）。</summary>
        private static string ColorizeCourtYear(string entry)
        {
            if (string.IsNullOrEmpty(entry)) return entry;
            int sp = entry.IndexOf(' ');
            if (sp <= 0 || sp >= entry.Length - 1)
                return $"<color=#c9c4b8>{entry}</color>";
            return $"<color=#ffd8d9>{entry.Substring(0, sp)}</color> <color=#c9c4b8>{entry.Substring(sp + 1)}</color>";
        }

        // ═══════════════════════ 第四页：成员详情 ═══════════════════════

        private readonly List<GameObject> _detailBlocks = new List<GameObject>();

        private void BuildDetail()
        {
            ClearChildren(_pageDetail, _detailBlocks);
            _detailBlockCursor = 0f;
            string kId = CurrentKingdomId();
            RefreshHeader(kId);
            if (string.IsNullOrEmpty(kId))
            {
                AddPageHint(_pageDetail, _detailBlocks, "请从成员列表选择一位党众"); // 阶段2N W13：空态提示
                return;
            }
            Kingdom k = null;
            try { k = World.world.kingdoms.get(int.Parse(kId)); } catch { }
            if (k == null || !k.isAlive())
            {
                AddPageHint(_pageDetail, _detailBlocks, "该国已亡，档案湮灭");
                return;
            }

            Actor a = GetActor(_currentActorId);
            if (a == null)
            {
                Text gone = CreateText(_pageDetail, "<color=#f73939>此人已不在人世</color>", 10);
                gone.alignment = TextAnchor.MiddleCenter;
                var gr = gone.GetComponent<RectTransform>();
                gr.anchorMin = new Vector2(0, 1); gr.anchorMax = new Vector2(0, 1);
                gr.pivot = new Vector2(0, 1);
                gr.anchoredPosition = new Vector2(0, -10);
                gr.sizeDelta = new Vector2(InnerWidth, 22f);
                return;
            }

            string name = a.getName();
            var office = FindOfficeRecord(kId, _currentActorId);
            string title = GetActorDisplayTitle(k, kId, _currentActorId); // 阶段2W：兜底君主/储君/城主/身份
            var st = FactionPoliticsManager.Instance.GetState(kId);
            int favor = st != null && st.Favor.TryGetValue(_currentActorId, out int fv) ? fv : 0;
            float power = office != null ? CorruptionManager.Instance.GetPower(kId, office.OfficeId) : 0f;

            // 页头
            Text head = CreateText(_pageDetail, $"<b>{name}</b> · <color={UI.ZhuluUIStyle.Hex.Azure}>{title}</color>", 12);
            head.alignment = TextAnchor.MiddleLeft;
            var hr = head.GetComponent<RectTransform>();
            hr.anchorMin = new Vector2(0, 1); hr.anchorMax = new Vector2(0, 1);
            hr.pivot = new Vector2(0, 1);
            hr.anchoredPosition = new Vector2(10, -6);
            hr.sizeDelta = new Vector2(240f, 18f);
            head.raycastTarget = false;

            // 动态小人大头 44×44（紧凑）
            Transform bigSlot = CreateAvatarSlot(_pageDetail, 44f);
            var bsr = bigSlot.GetComponent<RectTransform>();
            bsr.anchorMin = new Vector2(0, 1); bsr.anchorMax = new Vector2(0, 1);
            bsr.pivot = new Vector2(0, 1);
            bsr.anchoredPosition = new Vector2(10, -30);
            bsr.sizeDelta = new Vector2(44f, 44f);
            SetAvatar(bigSlot, a, name.Length > 0 ? name.Substring(0, 1) : "?", false);

            // 基本信息（大头右侧）
            Text info = CreateText(_pageDetail, BuildDetailInfo(k, a), 9);
            info.alignment = TextAnchor.UpperLeft;
            info.horizontalOverflow = HorizontalWrapMode.Wrap;
            info.verticalOverflow = VerticalWrapMode.Overflow;
            var ir = info.GetComponent<RectTransform>();
            ir.anchorMin = new Vector2(0, 1); ir.anchorMax = new Vector2(0, 1);
            ir.pivot = new Vector2(0, 1);
            ir.anchoredPosition = new Vector2(62, -30);
            ir.sizeDelta = new Vector2(260f, 44f);
            info.raycastTarget = false;

            // 亲信条
            float fy = -84f;
            CreateStatBar(_pageDetail, ref fy, "亲信", favor / 100f, favor, FactionPoliticsManager.GetFavorGrade(favor),
                new Color(0.52f, 0.95f, 0.41f, 1f), "#70d468");
            // 权势条
            CreateStatBar(_pageDetail, ref fy, "权势", Mathf.Clamp01(power / 100f), (int)power, power >= 75 ? "权倾朝野" : power >= 50 ? "势大权重" : "寻常",
                new Color(0.97f, 0.55f, 0.55f, 1f), "#f78d8d");

            // 官制块
            if (office != null)
            {
                string policy = "";
                try
                {
                    var acts = PolicyManager.Instance.GetActivePolicies(kId);
                    if (acts != null)
                    {
                        foreach (var ap in acts)
                        {
                            if (ap != null && ap.HostPersonName == name) { policy = ap.PolicyId; break; }
                        }
                    }
                }
                catch { }
                string policyTitle = "";
                try
                {
                    var pd = PolicyManager.Instance.GetDefinition(policy);
                    if (pd != null) policyTitle = pd.Title;
                }
                catch { }
                AddDetailBlock("🏛 官制",
                    $"<color=#c9d4ff>{title}</color>" + (policyTitle.Length > 0 ? $" · 任内政策：<color={UI.ZhuluUIStyle.Hex.Azure}>{policyTitle}</color>" : ""));
            }

            // 个人史册摘要
            AddDetailBlock("📜 个人史册（摘要）", BuildPersonHistorySummary(_currentActorId));

            // 党籍块
            string factionName = _currentFaction != null ? FactionManager.GetFactionDisplayName(k, _currentFaction) : "无"; // 阶段2V：显示名
            string feuds = _currentFaction != null ? BuildFeudText(kId, k, _currentFaction) : "";
            AddDetailBlock("⚖ 党籍", $"<color={GetFactionColorHex(_currentFaction != null ? _currentFaction.Type : FactionManager.FactionType.Custom)}>{factionName}</color>"
                + (feuds.Length > 0 ? $" · {feuds}" : ""));

            // 按钮行
            GameObject btns = new GameObject("Btns", typeof(RectTransform));
            btns.transform.SetParent(_pageDetail, false);
            var bnr = btns.GetComponent<RectTransform>();
            bnr.anchorMin = new Vector2(0, 1); bnr.anchorMax = new Vector2(0, 1);
            bnr.pivot = new Vector2(0, 1);
            bnr.anchoredPosition = new Vector2(10, fy);
            bnr.sizeDelta = new Vector2(InnerWidth - 20f, 28f);

            GameObject viewBtn = CreateSmallButton(btns.transform, "👁 查看单位", new Vector2(0, 0), true);
            viewBtn.GetComponent<Button>().onClick.AddListener(() =>
            {
                try
                {
                    // 原版正确打开方式（ActionLibrary.openUnitWindow 同款）：clear + select + showWindow("unit")
                    // 窗口 ID 是 "unit"（UnitWindow 注册进 ScrollWindow 的 ID），不是 "unit_window"！
                    if (!a.isRekt())
                    {
                        SelectedUnit.clear();
                        SelectedUnit.select(a);
                    }
                    else if (!SelectedUnit.isSet())
                    {
                        return;
                    }
                    ScrollWindow.showWindow("unit");
                }
                catch (Exception ex) { ZhuluContext.Instance.LogWarning($"[FactionWindow] 打开单位窗口失败: {ex.Message}"); }
            });

            GameObject histBtn = CreateSmallButton(btns.transform, "📜 个人史册", new Vector2(140, 0), false);
            histBtn.GetComponent<Button>().onClick.AddListener(() =>
            {
                try
                {
                    if (ZhuluPersonHistoryWindow.Instance != null)
                        ZhuluPersonHistoryWindow.Instance.Show(a);
                    ScrollWindow.showWindow(Main.PersonHistoryWindowId);
                }
                catch (Exception ex) { ZhuluContext.Instance.LogWarning($"[FactionWindow] 打开个人史册失败: {ex.Message}"); }
            });
            SetPageHeight(_pageDetail, 170f + _detailBlockCursor + 40f);
        }

        private string BuildDetailInfo(Kingdom k, Actor a)
        {
            var parts = new List<string>();
            try { parts.Add($"年龄 <color=#ffd8d9>{a.getAge()}</color>"); } catch { }
            try
            {
                var idType = IdentityClassifier.Instance.GetIdentity(a.getID());
                parts.Add($"身份 <color={UI.ZhuluUIStyle.Hex.Azure}>{IdentityClassifier.GetIdentityName(idType)}</color>");
            }
            catch { }
            try
            {
                string origin = IdentityClassifier.GetOrigin(a);
                parts.Add($"出身 <color={UI.ZhuluUIStyle.Hex.Positive}>{origin}</color>");
            }
            catch { }
            return string.Join(" · ", parts);
        }

        private string BuildPersonHistorySummary(long actorId)
        {
            try
            {
                var rec = PersonHistoryManager.Instance.GetRecord(actorId);
                if (rec == null || rec.Entries == null || rec.Entries.Count == 0)
                    return "<color=#5a5e70>暂无个人史册记录</color>";
                int n = Math.Min(rec.Entries.Count, 4);
                var lines = new List<string>();
                for (int i = rec.Entries.Count - n; i < rec.Entries.Count; i++)
                {
                    var e = rec.Entries[i];
                    lines.Add($"<color=#ffd8d9>{e.Year}</color> {e.Text}");
                }
                return string.Join("\n", lines);
            }
            catch { return "<color=#5a5e70>暂无个人史册记录</color>"; }
        }

        private void CreateStatBar(Transform parent, ref float y, string label, float pct, int num, string grade, Color fillCol, string numHex)
        {
            Text l = CreateText(parent, label, 9);
            l.alignment = TextAnchor.MiddleLeft;
            var lr = l.GetComponent<RectTransform>();
            lr.anchorMin = new Vector2(0, 1); lr.anchorMax = new Vector2(0, 1);
            lr.pivot = new Vector2(0, 1);
            lr.anchoredPosition = new Vector2(10, y);
            lr.sizeDelta = new Vector2(34f, 13f);
            l.color = new Color(0.62f, 0.60f, 0.55f, 1f);
            l.raycastTarget = false;

            GameObject bg = new GameObject("SBg", typeof(RectTransform), typeof(Image));
            bg.transform.SetParent(parent, false);
            var br = bg.GetComponent<RectTransform>();
            br.anchorMin = new Vector2(0, 1); br.anchorMax = new Vector2(0, 1);
            br.pivot = new Vector2(0, 1);
            br.anchoredPosition = new Vector2(48, y + 2);
            br.sizeDelta = new Vector2(180f, 7f);
            bg.GetComponent<Image>().color = new Color(0.08f, 0.09f, 0.12f, 1f);

            GameObject fill = new GameObject("SFill", typeof(RectTransform), typeof(Image));
            fill.transform.SetParent(bg.transform, false);
            var fr = fill.GetComponent<RectTransform>();
            fr.anchorMin = Vector2.zero; fr.anchorMax = new Vector2(pct, 1f);
            fr.offsetMin = Vector2.zero; fr.offsetMax = Vector2.zero;
            fill.GetComponent<Image>().color = fillCol;

            Text numT = CreateText(parent, $"<color={numHex}>{num} {grade}</color>", 9);
            numT.alignment = TextAnchor.MiddleRight;
            var nr = numT.GetComponent<RectTransform>();
            nr.anchorMin = new Vector2(1, 1); nr.anchorMax = new Vector2(1, 1);
            nr.pivot = new Vector2(1, 1);
            nr.anchoredPosition = new Vector2(-10, y);
            nr.sizeDelta = new Vector2(110f, 14f);
            numT.raycastTarget = false;
            y -= 22f;
        }

        private void AddDetailBlock(string title, string content)
        {
            GameObject block = new GameObject("DetailBlock", typeof(RectTransform));
            block.transform.SetParent(_pageDetail, false);
            var br = block.GetComponent<RectTransform>();
            br.anchorMin = new Vector2(0, 1); br.anchorMax = new Vector2(0, 1);
            br.pivot = new Vector2(0, 1);

            GameObject bg = new GameObject("BlockBg", typeof(RectTransform), typeof(Image));
            bg.transform.SetParent(block.transform, false);
            var bgr = bg.GetComponent<RectTransform>();
            bgr.anchorMin = Vector2.zero; bgr.anchorMax = Vector2.one;
            bgr.offsetMin = new Vector2(-36f, 0f); bgr.offsetMax = Vector2.zero;
            bg.GetComponent<Image>().color = new Color(0.13f, 0.14f, 0.19f, 0.92f);

            Text t = CreateText(block.transform, $"<color=#ffd8d9>{title}</color>", 10);
            t.alignment = TextAnchor.UpperLeft;
            var tr = t.GetComponent<RectTransform>();
            tr.anchorMin = new Vector2(0, 1); tr.anchorMax = new Vector2(0, 1);
            tr.pivot = new Vector2(0, 1);
            tr.anchoredPosition = new Vector2(10, -5);
            tr.sizeDelta = new Vector2(200f, 15f);
            t.raycastTarget = false;

            Text body = CreateText(block.transform, content, 10);
            body.alignment = TextAnchor.UpperLeft;
            body.horizontalOverflow = HorizontalWrapMode.Wrap;
            body.verticalOverflow = VerticalWrapMode.Overflow;
            var brr = body.GetComponent<RectTransform>();
            brr.anchorMin = new Vector2(0, 1); brr.anchorMax = new Vector2(0, 1);
            brr.pivot = new Vector2(0, 1);
            brr.anchoredPosition = new Vector2(10, -21);
            brr.sizeDelta = new Vector2(InnerWidth - 20f, 60f);

            // 阶段2W：实测正文高度定位块（原按 '\n' 计数估高——Wrap 自动换行不产生 \n，
            // 多行长文严重低估 → 下一块 anchoredPosition 压进上一块内容 = 详情页重叠，用户实报）
            float bodyH;
            try
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(brr);
                bodyH = Mathf.Max(16f, body.preferredHeight);
            }
            catch { bodyH = 18f + content.Count(c => c == '\n') * 16f; }
            float h = 27f + bodyH + 6f;
            br.sizeDelta = new Vector2(InnerWidth, h);
            br.anchoredPosition = new Vector2(0, -168f - _detailBlockCursor);
            _detailBlockCursor += h + 3f;
            brr.sizeDelta = new Vector2(InnerWidth - 20f, bodyH);
            body.raycastTarget = false;
            _detailBlocks.Add(block);
        }

        private float _detailBlockCursor = 0f;

        private GameObject CreateSmallButton(Transform parent, string label, Vector2 pos, bool hot)
        {
            GameObject btnObj = new GameObject("Btn", typeof(RectTransform), typeof(Image), typeof(Button));
            btnObj.transform.SetParent(parent, false);
            var r = btnObj.GetComponent<RectTransform>();
            r.anchorMin = new Vector2(0, 0);
            r.anchorMax = new Vector2(0, 0);
            r.pivot = new Vector2(0, 0);
            r.anchoredPosition = pos;
            r.sizeDelta = new Vector2(130f, 26f);
            var img = btnObj.GetComponent<Image>();
            img.color = hot ? new Color(0.23f, 0.20f, 0.10f, 1f) : new Color(0.14f, 0.14f, 0.17f, 0.95f);
            img.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            img.type = Image.Type.Sliced;
            Text t = CreateText(btnObj.transform, label, 10);
            t.alignment = TextAnchor.MiddleCenter;
            t.color = hot ? new Color(1f, 0.84f, 0f, 1f) : new Color(0.85f, 0.84f, 0.92f, 1f);
            t.raycastTarget = false;
            var tr = t.GetComponent<RectTransform>();
            tr.anchorMin = Vector2.zero;
            tr.anchorMax = Vector2.one;
            tr.sizeDelta = Vector2.zero;
            return btnObj;
        }

        // ═══════════════════════ 动态小人（原版 UiUnitAvatarElement） ═══════════════════════

        private static UiUnitAvatarElement GetAvatarPrefab()
        {
            if (_avatarPrefab != null) return _avatarPrefab;
            try { _avatarPrefab = Resources.Load<UiUnitAvatarElement>("ui/UnitAvatarElement"); }
            catch { _avatarPrefab = null; }
            return _avatarPrefab;
        }

        private Transform CreateAvatarSlot(Transform parent, float size)
        {
            GameObject slot = new GameObject("PortraitSlot", typeof(RectTransform));
            slot.transform.SetParent(parent, false);
            var slotRect = slot.GetComponent<RectTransform>();
            slotRect.sizeDelta = new Vector2(size, size);

            var prefab = GetAvatarPrefab();
            if (prefab != null)
            {
                try
                {
                    var avatar = Instantiate(prefab, slot.transform);
                    var avRect = avatar.GetComponent<RectTransform>();
                    if (avRect != null)
                    {
                        avRect.anchorMin = new Vector2(0.5f, 0.5f);
                        avRect.anchorMax = new Vector2(0.5f, 0.5f);
                        avRect.pivot = new Vector2(0.5f, 0.5f);
                        avRect.anchoredPosition = Vector2.zero;
                        avRect.sizeDelta = new Vector2(size, size);
                        avRect.localScale = Vector3.one;
                    }
                    avatar.gameObject.SetActive(true);
                }
                catch { }
            }
            else
            {
                var imgObj = new GameObject("FallbackBg", typeof(RectTransform), typeof(Image));
                imgObj.transform.SetParent(slot.transform, false);
                var imgRect = imgObj.GetComponent<RectTransform>();
                imgRect.anchorMin = Vector2.zero;
                imgRect.anchorMax = Vector2.one;
                imgRect.offsetMin = Vector2.zero;
                imgRect.offsetMax = Vector2.zero;
                var img = imgObj.GetComponent<Image>();
                img.color = new Color(0.25f, 0.28f, 0.36f, 1f);
                img.raycastTarget = false;
                Text fallback = CreateText(slot.transform, "?", 20);
                fallback.alignment = TextAnchor.MiddleCenter;
                var fbRect = fallback.GetComponent<RectTransform>();
                fbRect.anchorMin = Vector2.zero;
                fbRect.anchorMax = Vector2.one;
                fbRect.sizeDelta = Vector2.zero;
                fallback.raycastTarget = false;
                fallback.name = "FallbackChar";
            }
            return slot.transform;
        }

        private void SetAvatar(Transform slot, Actor actor, string fallbackChar, bool isTm)
        {
            if (slot == null) return;
            var avatar = slot.GetComponentInChildren<UiUnitAvatarElement>();
            bool ok = false;
            if (avatar != null && actor != null)
            {
                try
                {
                    avatar.gameObject.SetActive(true);
                    avatar.enabled = true;
                    if (avatar.avatarLoader != null)
                    {
                        avatar.avatarLoader.avatarSize *= 1.25f;
                        avatar.avatarLoader.enabled = true;
                    }
                    avatar.show(actor);
                    ok = true;
                }
                catch (Exception ex)
                {
                    ZhuluContext.Instance.LogWarning($"[FactionWindow] 动态头像显示失败，回退字符: {ex.Message}");
                }
            }
            if (!ok)
            {
                if (avatar != null) avatar.gameObject.SetActive(false);
                var fb = slot.Find("FallbackChar");
                if (fb != null)
                {
                    var t = fb.GetComponent<Text>();
                    if (t != null) t.text = fallbackChar;
                    fb.gameObject.SetActive(true);
                }
            }
        }


        // ═══════════════════════ 工具 ═══════════════════════

        /// <summary>阶段2N W13：页面空态提示（居中灰字，替代静默空白）。</summary>
        private void AddPageHint(Transform page, List<GameObject> track, string text)
        {
            Text t = CreateText(page, $"<color=#6a6458>{text}</color>", 10);
            track.Add(t.gameObject);
            t.alignment = TextAnchor.MiddleCenter;
            var r = t.GetComponent<RectTransform>();
            r.anchorMin = new Vector2(0, 1);
            r.anchorMax = new Vector2(0, 1);
            r.pivot = new Vector2(0, 1);
            r.anchoredPosition = new Vector2(0, -10);
            r.sizeDelta = new Vector2(InnerWidth, 24f);
        }

        private static Actor GetActor(long id)
        {
            try { return World.world.units.get(id); } catch { return null; }
        }

        private static string GetActorName(long id)
        {
            var a = GetActor(id);
            return a != null ? a.getName() : "无名氏";
        }
    }
}
