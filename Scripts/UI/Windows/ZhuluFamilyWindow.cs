using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using ZhuluRebuild.Core;
using ZhuluRebuild.Domain;

namespace ZhuluRebuild.UI.Windows
{
    /// <summary>
    /// 家族深度页（阶段3 重构：两页制 + 纵向分代世系）。
    /// 页①简介：家族卡（族长/丁口/家声/家风）+ 主家分支互查（Registry 模式）+ 先祖录。
    /// 页②世系：纵向分代组（祖辈/父辈/本人同辈/子辈/孙辈），
    ///          组内 GridLayout 网格自动换行；小字+描边；颜色分身份（本人金/死者灰/普通白）；无连线。
    /// 双数据源：Registry（谱牒总览进入）｜Clan（原版家族窗口「简介」按钮进入，族长为中心动态采集）。
    /// 批次8迁移（2026-08-23）：骨架收敛至 ZhuluScrollWindowBase（ContentTopBand=78 容纳 副标题+家声条；
    /// ContentBottomBand=30 容纳底部页签；PlaqueText/ContentScroll 为基类新暴露设施）——
    /// 删除手写 RebuildUI 约115行与本地 返回钮/布局/贴图 工具。
    /// </summary>
    public class ZhuluFamilyWindow : ZhuluScrollWindowBase
    {
        public static ZhuluFamilyWindow Instance { get; private set; }

        private Text _subStatText;
        private Image _prestigeFill;
        private Text _prestigeValText;

        private Transform _pageIntro;
        private Transform _pageTree;

        // 世系树（2026-08-20 树形重构：平移/缩放画布 + 父子连线）
        private RectTransform _treeCanvas;
        private ZhuluTreePanZoom _treePan;
        private RectTransform _treeSideHint;   // 任务7：操作提示移出窗口——右侧竖排批注条

        private int _currentPage;             // 0=简介 1=世系
        private string _kingdomId = "";
        private string _familyId = "main";
        private Clan _clanMode;               // 非 null = 原版 Clan 数据源
        private long _treeCenterId;           // 世系中心（0=族长/君主）；点击成员后以他为中心显示上下三代

        private readonly List<GameObject> _introCards = new List<GameObject>();
        private readonly List<GameObject> _treeCards = new List<GameObject>();

        private Text _tabIntro, _tabTree;
        private readonly Color TabOn = new Color(0.32f, 0.26f, 0.18f, 0.95f);
        private readonly Color TabOff = new Color(0.16f, 0.15f, 0.13f, 0.85f);

        // 身份配色
        private static readonly Color DeadGrey = new Color(0.286f, 0.286f, 0.286f);   // #494949 已故
        private static readonly Color PlainWhite = new Color(0.91f, 0.88f, 0.82f);    // 普通
        private static readonly Color SelectedRed = new Color(0.93f, 0.27f, 0.22f);   // 点选中心（变红）

        /// <summary>世系格统一展示模型（两种数据源都转成这个）。</summary>
        private class DispMember
        {
            public long ActorId;
            public string Name = "";
            public string Title = "";
            public bool Alive = true;
            public int Gen;          // 相对中心：-2祖 -1父 0本人/同辈 1子 2孙
            public int DiedYear;
            public bool IsSelf;
            public bool Female;
            public int Age;
            public long SpouseId;
            public List<long> ParentIds = new List<long>();
        }

        public void Initialize()
        {
            Instance = this;
            ZhuluContext.Instance.LogInfo("[ZhuluFamilyWindow] 初始化完成（两页制·纵向分代世系）");
        }

        protected override string WindowTitle => "<b>族谱</b>";
        protected override float WindowWidth => 340f;
        protected override float WindowHeight => 430f;
        protected override float PlaqueWidth => 230f;
        protected override float ContentTopBand => 78f;     // 牌匾26 + 副标题14 + 家声条18 + 间距
        protected override float ContentBottomBand => 30f;  // 底部页签条

        private void OnEnable()
        {
            if (World.world == null) return;
            EnsureBuilt();
            RefreshPage();
        }

        /// <summary>Registry 模式入口（谱牒总览/王国进入）。</summary>
        // 阶段3F：本家王国是否游牧体制（家声/家风/门第另表）
        private bool FamNomad()
        {
            try
            {
                var k = ZhuluRebuild.Persistence.KingdomDataExt.FindKingdomById(_kingdomId);
                return k != null && Domain.KingdomCivilizationService.Instance.IsNomadic(k);
            }
            catch { return false; }
        }

        public void Show(string kingdomId, string familyId)
        {
            _kingdomId = kingdomId ?? "";
            _familyId = string.IsNullOrEmpty(familyId) ? "main" : familyId;
            _clanMode = null;
            _treeCenterId = 0;           // 换家族重置中心（防旧中心跨族残留）
            if (World.world != null && gameObject.activeInHierarchy) EnsureBuilt();
        }

        /// <summary>Clan 模式入口（原版家族窗口「简介」按钮进入，阶段3）。</summary>
        public void Show(Clan clan)
        {
            _clanMode = clan;
            _kingdomId = "";
            _familyId = "";
            _treeCenterId = 0;           // 换族重置中心
            // 从 clan 首个成员反查所属王国（称号链/官职判定用）
            try
            {
                Kingdom kk = null;
                foreach (Kingdom k in World.world.kingdoms.list)
                {
                    if (k == null || !k.isAlive() || k.units == null) continue;
                    foreach (Actor u in k.units)
                    {
                        if (u != null && u.clan != null && u.clan.getID() == clan.getID()) { kk = k; break; }
                    }
                    if (kk != null) break;
                }
                if (kk != null) _kingdomId = kk.id.ToString();
            }
            catch { }
            if (World.world != null && gameObject.activeInHierarchy) EnsureBuilt();
        }

        // ═══════════════════════ 结构（骨架由基类提供） ═══════════════════════

        protected override void BuildContent()
        {
            _subStatText = CreateText(Bg, "", 9);
            _subStatText.alignment = TextAnchor.MiddleCenter;
            var subRect = _subStatText.GetComponent<RectTransform>();
            subRect.anchorMin = subRect.anchorMax = new Vector2(0.5f, 1);
            subRect.pivot = new Vector2(0.5f, 1);
            subRect.anchoredPosition = new Vector2(0, -38);
            subRect.sizeDelta = new Vector2(InnerWidth, 14f);

            CreatePrestigeBar();

            _pageIntro = CreatePage("PageIntro");
            _pageTree = CreatePage("PageTree");
            AddVlg(_pageIntro);          // 简介页：VLG 自动叠放
            // 2026-08-18 修复：简介页补 CSF——页高随卡片（含先祖录）增长，外层 ScrollRect 才能拖动
            _pageIntro.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            // 世系页（2026-08-20 树形重构）：固定高视口 + 可平移/缩放树画布 + 父子连线
            BuildTreeStructure();

            CreateBottomTabs();
            ShowPage(0);
        }

        private Transform CreatePage(string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(ContentRoot, false);
            var r = go.GetComponent<RectTransform>();
            r.sizeDelta = new Vector2(InnerWidth, 0f);
            go.SetActive(false);
            return go.transform;
        }

        private static void AddVlg(Transform page)
        {
            var vlg = page.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.spacing = 6f;
            vlg.padding = new RectOffset(2, 2, 4, 4);
            vlg.childControlWidth = false;
            vlg.childControlHeight = false;
            vlg.childForceExpandWidth = false;
            vlg.childForceExpandHeight = false;
        }

        /// <summary>
        /// 世系页结构（一次构建）：固定高视口（整页不滚动，平移交给树画布）+
        /// 左上锚定树画布（成员格与连线都挂它下面，可无限延展）+ 底部操作提示。
        /// 2026-08-20 树形重构：替代旧的分代网格滚动页（无连线/无缩放/换行拥挤）。
        /// </summary>
        private void BuildTreeStructure()
        {
            // 可视滚动区高度 = WindowHeight − 顶 30 − 底 78（与 ContentScroll 几何一致）
            float pageH = WindowHeight - 108f;
            var pageRect = _pageTree.GetComponent<RectTransform>();
            pageRect.sizeDelta = new Vector2(InnerWidth, pageH);

            // 树视口：透明射线接收面（空白区拖拽/滚轮/双击复位）
            var viewportObj = new GameObject("TreeViewport", typeof(RectTransform), typeof(Image));
            viewportObj.transform.SetParent(_pageTree, false);
            var vpRect = viewportObj.GetComponent<RectTransform>();
            vpRect.anchorMin = new Vector2(0f, 1f);
            vpRect.anchorMax = new Vector2(0f, 1f);
            vpRect.pivot = new Vector2(0f, 1f);
            vpRect.anchoredPosition = Vector2.zero;
            vpRect.sizeDelta = new Vector2(InnerWidth, pageH - 4f);
            var vpImg = viewportObj.GetComponent<Image>();
            vpImg.color = new Color(0.08f, 0.07f, 0.06f, 0.35f); // 微暗底衬出画布区域
            vpImg.raycastTarget = true;

            // 树画布：左上锚定（成员格/连线坐标 = x 从左、y 从上，anchoredPosition=(x,-y)）
            var canvasObj = new GameObject("TreeCanvas", typeof(RectTransform));
            canvasObj.transform.SetParent(viewportObj.transform, false);
            _treeCanvas = canvasObj.GetComponent<RectTransform>();
            _treeCanvas.anchorMin = new Vector2(0f, 1f);
            _treeCanvas.anchorMax = new Vector2(0f, 1f);
            _treeCanvas.pivot = new Vector2(0f, 1f);
            _treeCanvas.anchoredPosition = Vector2.zero;
            _treeCanvas.sizeDelta = new Vector2(InnerWidth, pageH - 4f);

            // 平移/缩放组件（挂视口；拖动事件从成员格 Button 冒泡上来）
            _treePan = ZhuluTreePanZoom.Bind(vpRect, _treeCanvas);

            // 任务7：操作提示移出窗口外——右侧竖排批注条（空间不足自动翻左侧）
            CreateTreeSideHint();
        }

        /// <summary>
        /// 世系页操作提示（任务7）：原底部一行小字改为窗口外右侧竖排批注条。
        /// 长文案竖排分行（每行≤5字），窄条不占屏；挂 Background 随窗口拖动；
        /// LateUpdate 检测右侧空间不足时自动翻到窗口左侧，保证不移出屏幕。
        /// </summary>
        private void CreateTreeSideHint()
        {
            var hintObj = new GameObject("TreeSideHint", typeof(RectTransform), typeof(Image));
            hintObj.transform.SetParent(Bg, false);
            _treeSideHint = hintObj.GetComponent<RectTransform>();
            _treeSideHint.anchorMin = new Vector2(1f, 0.5f);
            _treeSideHint.anchorMax = new Vector2(1f, 0.5f);
            _treeSideHint.pivot = new Vector2(0f, 0.5f);
            _treeSideHint.anchoredPosition = new Vector2(6f, 0f);
            _treeSideHint.sizeDelta = new Vector2(60f, 84f);

            var img = hintObj.GetComponent<Image>();
            img.color = new Color(0.10f, 0.09f, 0.07f, 0.88f);
            ZhuluUIStyle.SetSlicedSprite(img, "ui/special/backgroundKingdomElement");
            img.raycastTarget = false;

            var t = CreateText(hintObj.transform, "拖动平移\n滚轮缩放\n双击复位\n点格换中心\n再点阅史册", 9);
            t.alignment = TextAnchor.MiddleCenter;
            t.color = new Color(0.93f, 0.85f, 0.64f);
            AddOutline(t);
            Stretch(t.GetComponent<RectTransform>());
            hintObj.SetActive(false);   // 仅世系页显示（ShowPage 切换）
        }

        /// <summary>右侧空间不足（窗口贴近画布右缘）时把提示条翻到窗口左侧。</summary>
        private void LateUpdate()
        {
            if (_treeSideHint == null || !_treeSideHint.gameObject.activeSelf) return;
            if (Time.frameCount % 20 != 0) return;   // 节流：约 1/3 秒一测
            try
            {
                var bgRect = Bg as RectTransform;
                var rootRect = _treeSideHint.root as RectTransform;
                if (bgRect == null || rootRect == null) return;
                var cw = new Vector3[4]; rootRect.GetWorldCorners(cw);
                var bw = new Vector3[4]; bgRect.GetWorldCorners(bw);
                bool wantRight = cw[2].x - bw[2].x > _treeSideHint.sizeDelta.x + 14f;
                bool isRight = _treeSideHint.anchorMin.x >= 1f;
                if (wantRight == isRight) return;
                if (wantRight)
                {
                    _treeSideHint.anchorMin = new Vector2(1f, 0.5f);
                    _treeSideHint.anchorMax = new Vector2(1f, 0.5f);
                    _treeSideHint.pivot = new Vector2(0f, 0.5f);
                    _treeSideHint.anchoredPosition = new Vector2(6f, 0f);
                }
                else
                {
                    _treeSideHint.anchorMin = new Vector2(0f, 0.5f);
                    _treeSideHint.anchorMax = new Vector2(0f, 0.5f);
                    _treeSideHint.pivot = new Vector2(1f, 0.5f);
                    _treeSideHint.anchoredPosition = new Vector2(-6f, 0f);
                }
            }
            catch { }
        }

        private void CreatePrestigeBar()
        {
            var barObj = new GameObject("PrestigeBar", typeof(RectTransform), typeof(Image));
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

            var label = CreateText(barObj.transform, "家声", 9);
            label.alignment = TextAnchor.MiddleLeft;
            label.color = new Color(0.62f, 0.60f, 0.55f, 1f);
            var lr = label.GetComponent<RectTransform>();
            lr.anchorMin = new Vector2(0, 0.5f); lr.anchorMax = new Vector2(0, 0.5f);
            lr.pivot = new Vector2(0, 0.5f);
            lr.anchoredPosition = new Vector2(8, 0);
            lr.sizeDelta = new Vector2(30f, 14f);

            var fillBg = new GameObject("FillBg", typeof(RectTransform), typeof(Image));
            fillBg.transform.SetParent(barObj.transform, false);
            var fr = fillBg.GetComponent<RectTransform>();
            fr.anchorMin = new Vector2(0, 0.5f); fr.anchorMax = new Vector2(0, 0.5f);
            fr.pivot = new Vector2(0, 0.5f);
            fr.anchoredPosition = new Vector2(42, 0);
            fr.sizeDelta = new Vector2(200f, 7f);
            fillBg.GetComponent<Image>().color = new Color(0.08f, 0.09f, 0.12f, 1f);

            var fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            fill.transform.SetParent(fillBg.transform, false);
            var fillRect = fill.GetComponent<RectTransform>();
            fillRect.anchorMin = Vector2.zero; fillRect.anchorMax = Vector2.one;
            fillRect.offsetMin = Vector2.zero; fillRect.offsetMax = Vector2.zero;
            _prestigeFill = fill.GetComponent<Image>();
            _prestigeFill.color = new Color(0.95f, 0.78f, 0.35f, 1f);

            _prestigeValText = CreateText(barObj.transform, "", 9);
            _prestigeValText.alignment = TextAnchor.MiddleLeft;
            var vr = _prestigeValText.GetComponent<RectTransform>();
            vr.anchorMin = new Vector2(0, 0.5f); vr.anchorMax = new Vector2(0, 0.5f);
            vr.pivot = new Vector2(0, 0.5f);
            vr.anchoredPosition = new Vector2(250, 0);
            vr.sizeDelta = new Vector2(80f, 14f);
            _prestigeValText.color = new Color(0.95f, 0.78f, 0.35f, 1f);
        }

        private void CreateBottomTabs()
        {
            var tabsObj = new GameObject("BottomTabs", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            tabsObj.transform.SetParent(Bg, false);
            var tr = tabsObj.GetComponent<RectTransform>();
            tr.anchorMin = new Vector2(0.5f, 0);
            tr.anchorMax = new Vector2(0.5f, 0);
            tr.pivot = new Vector2(0.5f, 0);
            tr.anchoredPosition = new Vector2(0, 6);
            tr.sizeDelta = new Vector2(200f, 22f);
            var hlg = tabsObj.GetComponent<HorizontalLayoutGroup>();
            hlg.spacing = 10f;
            hlg.childControlWidth = false;
            hlg.childControlHeight = false;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = false;

            _tabIntro = CreateTabBtn(tabsObj.transform, "简介", () => ShowPage(0));
            _tabTree = CreateTabBtn(tabsObj.transform, "世系", () => ShowPage(1));
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

            var t = CreateText(obj.transform, label, 10);
            t.alignment = TextAnchor.MiddleCenter;
            t.color = new Color(0.88f, 0.84f, 0.75f, 1f);
            Stretch(t.GetComponent<RectTransform>());
            return t;
        }

        private void ShowPage(int page)
        {
            _currentPage = page;
            if (_pageIntro != null) _pageIntro.gameObject.SetActive(page == 0);
            if (_pageTree != null) _pageTree.gameObject.SetActive(page == 1);
            if (_tabIntro != null) _tabIntro.transform.parent.GetComponent<Image>().color = page == 0 ? TabOn : TabOff;
            if (_tabTree != null) _tabTree.transform.parent.GetComponent<Image>().color = page == 1 ? TabOn : TabOff;
            // 2026-08-20：世系页用自己的平移/缩放交互——关外层滚动防拖拽/滚轮抢事件
            if (ContentScroll != null) ContentScroll.enabled = page != 1;
            // 任务7：右侧竖排操作提示条仅世系页显示
            if (_treeSideHint != null) _treeSideHint.gameObject.SetActive(page == 1);
            RefreshPage();
        }

        private void RefreshPage()
        {
            if (ContentRoot == null) return;
            try
            {
                // 数据解析：Clan 模式 or Registry 模式
                if (_clanMode != null)
                {
                    var notable = FamilyRegistry.Instance.FindNotableByClan(_clanMode.getID());
                    string clanName = _clanMode.data != null ? (_clanMode.data.name ?? "") : "";
                    RefreshHeader(clanName, _clanMode.getChief()?.getName() ?? "—",
                        notable?.Prestige ?? 45, notable?.Style ?? "");
                    if (_currentPage == 0) BuildIntroClan(notable);
                    else BuildTree(BuildClanMembers());
                }
                else
                {
                    var fam = FamilyRegistry.Instance.GetFamily(_kingdomId, _familyId);
                    if (fam == null)
                    {
                        ClearChildren(_pageIntro, _introCards);
                        AddHint(_pageIntro, _introCards, "尚未建立谱牒——请让时间推进一年");
                        RefreshHeader("族谱", "—", 0, "");
                        return;
                    }
                    RefreshHeader(
                        fam.IsMainLine ? $"{fam.Surname}氏大宗" : $"{fam.BranchFounderName}支",
                        fam.HeadName, fam.Prestige, fam.Style);
                    if (_currentPage == 0) BuildIntro(fam);
                    else BuildTree(BuildRegistryMembers(fam));
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[FamilyWindow] 刷新异常: {ex.Message}");
            }
            LayoutRebuilder.ForceRebuildLayoutImmediate(ContentRoot.GetComponent<RectTransform>());
        }

        private void RefreshHeader(string famName, string headName, int prestige, string style)
        {
            if (PlaqueText == null) return;
            Kingdom k = null;
            try { if (!string.IsNullOrEmpty(_kingdomId)) k = World.world.kingdoms.get(int.Parse(_kingdomId)); } catch { }
            string kName = k != null ? k.name + " · " : "";
            PlaqueText.text = $"<b>{kName}{famName}</b>";
            _subStatText.text = $"建元 <color=#ffd8d9>{ZhuluTime.CurrentYear}</color> 年 · 族长 <color=#ffd700>{headName}</color> · {FamilyRegistry.GetStyleName(style, FamNomad())} · {FamilyRegistry.GetPrestigeGrade(prestige, FamNomad())}";

            float p = Mathf.Clamp01(prestige / 100f);
            if (_prestigeFill != null)
            {
                var fr = _prestigeFill.GetComponent<RectTransform>();
                fr.anchorMax = new Vector2(p, 1f);
                fr.offsetMin = Vector2.zero;
                fr.offsetMax = Vector2.zero;
            }
            if (_prestigeValText != null) _prestigeValText.text = $"{prestige}";
        }

        // ═══════════════════════ 数据采集（双源 → DispMember） ═══════════════════════

        /// <summary>Registry 模式：FamilyData.Members → DispMember（可指定中心，默认族长）。</summary>
        private List<DispMember> BuildRegistryMembers(FamilyRegistry.FamilyData fam)
        {
            long centerId = _treeCenterId != 0 && fam.Members.Any(m => m.ActorId == _treeCenterId)
                ? _treeCenterId : fam.HeadId;
            int centerGen = 0;
            foreach (var m in fam.Members) { if (m.ActorId == centerId) { centerGen = m.Gen; break; } }
            var list = new List<DispMember>();
            foreach (var m in fam.Members)
            {
                var d = new DispMember
                {
                    ActorId = m.ActorId,
                    Name = m.Name,
                    Title = m.Title,
                    Alive = m.Alive,
                    Gen = m.Gen - centerGen,
                    DiedYear = m.DiedYear,
                    IsSelf = m.ActorId == centerId,
                    SpouseId = m.SpouseId,
                    ParentIds = m.ParentIds != null ? new List<long>(m.ParentIds) : new List<long>()
                };
                FillLiveInfo(d);
                list.Add(d);
            }
            return list;
        }

        /// <summary>补全性别/年龄（在世者实时取；已故者保默认）。</summary>
        private static void FillLiveInfo(DispMember d)
        {
            try
            {
                Actor a = World.world.units.get(d.ActorId);
                if (a == null) return;
                // Registry 快照可能尚未等到下一年度刷新；全局 Actor 仍存活时，展示层必须即时纠正。
                if (a.isAlive())
                {
                    d.Alive = true;
                    d.DiedYear = 0;
                    d.Name = a.getName();
                }
                try { d.Female = a.isSexFemale(); } catch { }
                try { d.Age = a.getAge(); } catch { }
            }
            catch { }
        }

        /// <summary>Clan 模式：以选中成员（_treeCenterId，默认族长）为中心动态采集上下三代。</summary>
        private List<DispMember> BuildClanMembers()
        {
            var list = new List<DispMember>();
            if (_clanMode == null) return list;
            Kingdom k = null;
            try { if (!string.IsNullOrEmpty(_kingdomId)) k = World.world.kingdoms.get(int.Parse(_kingdomId)); } catch { }
            string kId = _kingdomId;

            Actor chief = _clanMode.getChief();
            if (chief == null || !chief.isAlive()) return list;

            // 中心：优先点选的在世族人，回退族长
            Actor center = chief;
            if (_treeCenterId != 0)
            {
                try
                {
                    var picked = World.world.units.get(_treeCenterId);
                    if (picked != null && picked.isAlive()) center = picked;
                    else _treeCenterId = 0;
                }
                catch { _treeCenterId = 0; }
            }

            var bag = new Dictionary<long, DispMember>();
            Action<Actor, int, bool> add = (a, gen, isSelf) =>
            {
                if (a == null || bag.ContainsKey(a.getID())) return;
                var d = new DispMember
                {
                    ActorId = a.getID(),
                    Name = a.getName(),
                    Title = k != null ? FamilyRegistry.Instance.BuildTitleSnapshot(k, kId, a) : "",
                    Alive = a.isAlive(),
                    Gen = gen,
                    IsSelf = isSelf
                };
                try { d.Female = a.isSexFemale(); } catch { }
                try { d.Age = a.getAge(); } catch { }
                try { d.SpouseId = GetSpouseIdSafeLocal(a); } catch { }
                try
                {
                    var ps = a.getParents();
                    if (ps != null) foreach (var p in ps) { if (p != null) d.ParentIds.Add(p.getID()); }
                }
                catch { }
                bag[d.ActorId] = d;
            };

            add(center, 0, true);

            // 上2代：父母 → 祖父母（含父母的兄弟姐妹=叔伯姑）
            try
            {
                var parents = SafeParents(center);
                foreach (var p in parents) add(p, -1, false);
                foreach (var p in parents)
                {
                    foreach (var gp in SafeParents(p)) add(gp, -2, false);
                }
            }
            catch { }

            // 同辈（共享父母者且同 Clan）+ 其子女（侄辈，归子代组显示）
            var siblingIds = new List<long>();
            try
            {
                var parentIds = new HashSet<long>();
                foreach (var p in SafeParents(center)) { if (p != null) parentIds.Add(p.getID()); }
                if (k != null && k.units != null)
                {
                    foreach (Actor u in k.units)
                    {
                        if (u == null || !u.isAlive() || u.clan == null || u.clan.getID() != _clanMode.getID()) continue;
                        if (u.getID() == center.getID() || bag.ContainsKey(u.getID())) continue;
                        foreach (var up in SafeParents(u))
                        {
                            if (up != null && parentIds.Contains(up.getID()))
                            {
                                add(u, 0, false);
                                siblingIds.Add(u.getID());
                                break;
                            }
                        }
                    }
                }
            }
            catch { }

            // 下2代：子女 → 孙；另收兄弟姐妹之子女（侄）入 +1 组
            try
            {
                if (k != null)
                {
                    var frontier = new List<Actor> { center };
                    for (int g = 1; g <= 2; g++)
                    {
                        var next = new List<Actor>();
                        foreach (var person in frontier)
                        {
                            foreach (var c in KinshipTracker.GetAliveChildren(k, person.getID().ToString()))
                            {
                                if (c == null || bag.ContainsKey(c.getID())) continue;
                                add(c, g, false);
                                next.Add(c);
                            }
                        }
                        frontier = next;
                    }
                    foreach (long sid in siblingIds)
                    {
                        foreach (var c in KinshipTracker.GetAliveChildren(k, sid.ToString()))
                        {
                            if (c == null || bag.ContainsKey(c.getID())) continue;
                            add(c, 1, false); // 侄辈与子辈同代
                        }
                    }
                }
            }
            catch { }

            // ── 兜底：血缘网外的同族活人按齿序估代（阶段3——父母已死被回收时 getParents 空，
            //    同辈/上代判定全失败，十几人族只显示中心+子女几人。按年龄差归最近代组，保全族可见）──
            try
            {
                if (_clanMode.units != null)
                {
                    int cAge = 0;
                    try { cAge = center.getAge(); } catch { }
                    foreach (Actor u in _clanMode.units)
                    {
                        if (u == null || !u.isAlive() || bag.ContainsKey(u.getID())) continue;
                        int age = 0;
                        try { age = u.getAge(); } catch { }
                        int gen = 0;
                        if (cAge > 0 && age > 0)
                        {
                            int d = age - cAge;   // 正=年长
                            if (d >= 55) gen = -3;
                            else if (d >= 33) gen = -2;
                            else if (d >= 13) gen = -1;
                            else if (d <= -55) gen = 3;
                            else if (d <= -33) gen = 2;
                            else if (d <= -13) gen = 1;
                        }
                        add(u, gen, false);
                    }
                }
            }
            catch { }

            list.AddRange(bag.Values);
            return list;
        }

        private static List<Actor> SafeParents(Actor a)
        {
            var result = new List<Actor>();
            try
            {
                var parents = a.getParents();
                if (parents != null)
                    foreach (var p in parents) { if (p != null) result.Add(p); }
            }
            catch { }
            return result;
        }

        // ═══════════════════════ 第一页：简介 ═══════════════════════

        private void BuildIntro(FamilyRegistry.FamilyData fam)
        {
            ClearChildren(_pageIntro, _introCards);
            var kf = FamilyRegistry.Instance.GetKingdomFamilies(_kingdomId);

            // 主家/分支互查（Registry 模式核心）
            if (fam.IsMainLine)
            {
                if (kf != null && kf.Branches.Count > 0)
                {
                    var sect = CreateCard(_pageIntro, _introCards, 20f);
                    AddSectionLabel(sect.transform, "族中分支");
                    foreach (var br in kf.Branches)
                    {
                        string bid = br.FamilyId;
                        CreateRow(_pageIntro, _introCards, $"{br.BranchFounderName}支",
                            $"族长 {br.HeadName} · {FamilyRegistry.GetPrestigeGrade(br.Prestige, FamNomad())}",
                            () => { _familyId = bid; ShowPage(0); });
                    }
                }
            }
            else
            {
                CreateRow(_pageIntro, _introCards, "↥ 本支出自大宗",
                    kf != null ? $"{kf.Main.Surname}氏大宗 · 族长 {kf.Main.HeadName}" : "",
                    () => { _familyId = "main"; ShowPage(0); });
            }

            // 家族概况
            int alive = 0, dead = 0;
            foreach (var m in fam.Members) { if (m.Alive) alive++; else dead++; }
            int minGen = int.MaxValue, maxGen = int.MinValue;
            foreach (var m in fam.Members) { if (m.Gen < minGen) minGen = m.Gen; if (m.Gen > maxGen) maxGen = m.Gen; }
            int span = fam.Members.Count > 0 ? maxGen - minGen + 1 : 1;
            int officials = 0;
            try
            {
                var recs = OfficeManager.Instance.GetRecordsPublic(_kingdomId);
                if (recs != null)
                {
                    var ids = new HashSet<long>(fam.Members.Select(m => m.ActorId));
                    foreach (var r in recs)
                        if (r != null && long.TryParse(r.PersonId, out var pid) && ids.Contains(pid)) officials++;
                }
            }
            catch { }
            Kingdom kHere = null;
            try { if (!string.IsNullOrEmpty(_kingdomId)) kHere = World.world.kingdoms.get(int.Parse(_kingdomId)); } catch { }
            string kName = kHere != null && kHere.isAlive() ? kHere.name : "故国";

            var infoCard = CreateCard(_pageIntro, _introCards, 92f);
            var info = CreateText(infoCard.transform,
                $"<color={ZhuluUIStyle.Hex.GoldDim}>所属</color> {kName} · {(fam.IsMainLine ? "王室大宗" : "宗族分支")}\n" +
                $"<color={ZhuluUIStyle.Hex.GoldDim}>丁口</color> 在世 {alive} 人 · 已故 {dead} 人 · 传世 {CN(span)} 代\n" +
                $"<color={ZhuluUIStyle.Hex.GoldDim}>入仕</color> {CN(officials)} 人\n" +
                $"<color={ZhuluUIStyle.Hex.GoldDim}>家风</color> {FamilyRegistry.GetStyleName(fam.Style, FamNomad())}（{FamilyRegistry.GetStyleEffect(fam.Style, FamNomad())}）\n" +
                $"<color={ZhuluUIStyle.Hex.GoldDim}>家声</color> {fam.Prestige} · {FamilyRegistry.GetPrestigeGrade(fam.Prestige, FamNomad())}", 9);
            info.alignment = TextAnchor.MiddleLeft;
            LayoutLeft(info.GetComponent<RectTransform>(), 8, 4, InnerWidth - 20, 84);

            // 先祖录（2026-08-18 收录门槛修复：仅显赫先人——官职/君主/储君/城主/功名/爵位/奇才；
            // 旧档无 Distinguished 标记时按称号白名单兜底，身份名"士人/庶民"等不再混入）
            var deadList = fam.Members
                .Where(m => !m.Alive && FamilyRegistry.IsDistinguishedTitle(m.Title))
                .OrderBy(m => m.DiedYear)
                .ToList();
            BuildAncestorGrid(deadList);
        }

        /// <summary>先祖录小卡片网格（手动定位防嵌套布局组重叠；cellW×cols 自适应行宽）。</summary>
        private void BuildAncestorGrid(List<FamilyRegistry.FamilyMember> deadList)
        {
            if (deadList.Count == 0)
            {
                var t2 = CreateCard(_pageIntro, _introCards, 20f);
                AddSectionLabel(t2.transform, "先祖录");
                AddHint(_pageIntro, _introCards, "阖族无恙，尚无显赫先人入录");
                return;
            }

            const float cellH = 30f, gap = 4f;
            int cols = Mathf.Max(1, Mathf.FloorToInt((InnerWidth - 12f) / (74f + gap)));
            float cellW = (InnerWidth - 12f - (cols - 1) * gap) / cols;
            int rows = (deadList.Count + cols - 1) / cols;
            float cardH = 24f + rows * cellH + (rows - 1) * gap + 6f;

            var card = CreateCard(_pageIntro, _introCards, cardH);
            AddSectionLabel(card.transform, $"先祖录 · 显赫 {CN(deadList.Count)} 人");

            for (int i = 0; i < deadList.Count; i++)
            {
                var d = deadList[i];
                float x = 6f + (i % cols) * (cellW + gap);
                float y = 22f + (i / cols) * (cellH + gap);

                // 单格：名 + 称谓/卒年（浅色小框——世系格同款风格）
                var cell = new GameObject("A_" + d.ActorId, typeof(RectTransform), typeof(Image));
                cell.transform.SetParent(card.transform, false);
                _introCards.Add(cell);
                var crt = cell.GetComponent<RectTransform>();
                crt.anchorMin = new Vector2(0, 1);
                crt.anchorMax = new Vector2(0, 1);
                crt.pivot = new Vector2(0, 1);
                crt.anchoredPosition = new Vector2(x, -y);
                crt.sizeDelta = new Vector2(cellW, cellH);
                var cimg = cell.GetComponent<Image>();
                cimg.color = new Color(0.96f, 0.92f, 0.80f, 0.10f);
                cimg.raycastTarget = false;

                var nameT = CreateText(cell.transform, $"<color={ZhuluUIStyle.Hex.PaperDim}>{d.Name}</color>", 8);
                nameT.alignment = TextAnchor.UpperCenter;
                AddOutline(nameT);
                LayoutLeft(nameT.GetComponent<RectTransform>(), 0, 2, cellW, 13);

                string sub = d.DiedYear > 0 ? $"{d.Title}·卒{d.DiedYear}" : d.Title;
                if (sub.Length > 8) sub = sub.Substring(0, 8);
                var subT = CreateText(cell.transform, sub, 6);
                subT.alignment = TextAnchor.UpperCenter;
                subT.color = new Color(0.55f, 0.52f, 0.47f);
                AddOutline(subT);
                LayoutLeft(subT.GetComponent<RectTransform>(), 0, 16, cellW, 11);
            }

            if (rows > 2)
            {
                var drag = CreateText(card.transform, "<color=#6a6458>↕ 上下拖动可查看更多先人</color>", 7);
                drag.alignment = TextAnchor.MiddleRight;
                LayoutLeft(drag.GetComponent<RectTransform>(), 6, cardH - 14f, InnerWidth - 16, 12);
            }
        }

        /// <summary>Clan 模式简介（望族/普通家族——成员实时取：王国/丁口/入仕/功名/历任族长）。</summary>
        private void BuildIntroClan(FamilyRegistry.NotableFamilyRef notable)
        {
            ClearChildren(_pageIntro, _introCards);

            Kingdom k = null;
            try { if (!string.IsNullOrEmpty(_kingdomId)) k = World.world.kingdoms.get(int.Parse(_kingdomId)); } catch { }

            // 丁口/入仕/功名统计（实时）
            int memberCount = 0, males = 0, females = 0, officials = 0, degrees = 0;
            var officialIds = new HashSet<string>();
            try
            {
                var records = k != null ? OfficeManager.Instance.GetRecordsPublic(_kingdomId) : null;
                if (records != null)
                    foreach (var r in records)
                        if (r != null && r.PersonId != null) officialIds.Add(r.PersonId);
            }
            catch { }
            try
            {
                if (_clanMode.units != null)
                {
                    memberCount = _clanMode.units.Count;
                    foreach (Actor u in _clanMode.units)
                    {
                        if (u == null) continue;
                        try { if (u.isSexFemale()) females++; else males++; } catch { }
                        if (officialIds.Contains(u.getID().ToString())) officials++;
                        if (u.hasTrait(TraitManager.TRAIT_XIUCAI) || u.hasTrait(TraitManager.TRAIT_JUREN)
                            || u.hasTrait(TraitManager.TRAIT_JINSHI) || u.hasTrait(TraitManager.TRAIT_ZHUANGYUAN)
                            || u.hasTrait(TraitManager.TRAIT_WUXIUCAI) || u.hasTrait(TraitManager.TRAIT_WUJUREN)
                            || u.hasTrait(TraitManager.TRAIT_WUJINSHI)) degrees++;
                    }
                }
            }
            catch { }

            string status = "在野家族";
            try
            {
                if (k != null && k.king != null && k.king.clan != null && k.king.clan.getID() == _clanMode.getID())
                    status = "当朝王族";
                else if (notable != null) status = "国中望族";
            }
            catch { }

            // 历任族长（原版 past_chiefs）
            int pastCount = 0;
            var pastList = new List<string>();
            try
            {
                var past = _clanMode.data?.past_chiefs;
                if (past != null)
                {
                    pastCount = past.Count;
                    for (int i = past.Count - 1; i >= 0 && pastList.Count < 4; i--)
                    {
                        var e = past[i];
                        if (e == null) continue;
                        // 2026-08-20 bug修复：timestamp_ago 是原始世界时间戳（如 43155.397），
                        // 原样当"年"显示成乱码；用原版 Date.getYearsSince 换算真实年差
                        string ago = "";
                        try
                        {
                            if (e.timestamp_ago > 0)
                            {
                                int yAgo = Date.getYearsSince(e.timestamp_ago);
                                ago = yAgo > 0 ? $"{yAgo}年前" : "近岁";
                            }
                        }
                        catch { }
                        int order = i + 1;
                        pastList.Add($"第{CN(order)}任 <color={ZhuluUIStyle.Hex.PaperDim}>{e.name ?? "?"}</color>{(ago.Length > 0 ? $"（{ago}）" : "")}");
                    }
                }
            }
            catch { }

            var infoCard = CreateCard(_pageIntro, _introCards, 110f);
            var info = CreateText(infoCard.transform,
                $"<color={ZhuluUIStyle.Hex.GoldDim}>所属</color> {(k != null && k.isAlive() ? k.name : "在野")} · {status}\n" +
                $"<color={ZhuluUIStyle.Hex.GoldDim}>族长</color> {_clanMode.getChief()?.getName() ?? "—"} · 历任 {CN(pastCount + 1)} 任\n" +
                $"<color={ZhuluUIStyle.Hex.GoldDim}>丁口</color> {memberCount} 人（男 {males} · 女 {females}）\n" +
                $"<color={ZhuluUIStyle.Hex.GoldDim}>入仕</color> {CN(officials)} 人 · <color={ZhuluUIStyle.Hex.GoldDim}>功名</color> {CN(degrees)} 人\n" +
                $"<color={ZhuluUIStyle.Hex.GoldDim}>家声</color> {(notable != null ? $"{notable.Prestige} · {FamilyRegistry.GetPrestigeGrade(notable.Prestige, FamNomad())}" : "未入谱牒")}\n" +
                $"<color={ZhuluUIStyle.Hex.GoldDim}>家训</color> {TryGetMotto()}", 9);
            info.alignment = TextAnchor.MiddleLeft;
            LayoutLeft(info.GetComponent<RectTransform>(), 8, 4, InnerWidth - 20, 102);

            // 历代族长（现任最前）
            if (pastCount > 0)
            {
                var t = CreateCard(_pageIntro, _introCards, 20f);
                AddSectionLabel(t.transform, "历代族长");
                var cur = CreateText(_pageIntro, $"第{CN(pastCount + 1)}任 <color=#ffd8a0>{_clanMode.getChief()?.getName() ?? "—"}</color>（在任）", 9, _introCards);
                cur.alignment = TextAnchor.MiddleLeft;
                LayoutLeft(cur.GetComponent<RectTransform>(), 10, 0, InnerWidth - 20, 14);
                foreach (var p in pastList)
                {
                    var r = CreateText(_pageIntro, p, 9, _introCards);
                    r.alignment = TextAnchor.MiddleLeft;
                    LayoutLeft(r.GetComponent<RectTransform>(), 10, 0, InnerWidth - 20, 14);
                }
            }

            var tip = AddHint(_pageIntro, _introCards, "世系页以族长为中心 · 上下各三代 · 点击成员可换中心");
            tip.fontSize = 8;
        }

        private string TryGetMotto()
        {
            try { var m = _clanMode?.getMotto(); return string.IsNullOrEmpty(m) ? "无" : m; }
            catch { return "无"; }
        }

        // ═══════════════════════ 第二页：世系（树形 · 平移缩放） ═══════════════════════

        /// <summary>
        /// 世系树（2026-08-20 重构）：真树形布局替代旧分代网格——
        /// ① 每代一行，左侧代名栏；行内按父系聚簇排序（同父子女相邻）；
        /// ② 自下而上父格对齐子格均值（受同代邻格夹取，永不重叠）；
        /// ③ 父子三段式连线（竖-横-竖），血缘一目了然；
        /// ④ 画布可拖拽平移/滚轮缩放/双击复位（宽家谱横向延展，不换行不挤压）。
        /// </summary>
        private void BuildTree(List<DispMember> members)
        {
            if (_treeCanvas == null) return;
            ClearChildren(_treeCanvas, _treeCards);
            if (_treePan != null) _treePan.ResetView();
            _treeCanvas.anchoredPosition = Vector2.zero;

            if (members.Count == 0)
            {
                var empty = CreateText(_treeCanvas, "<color=#6a6458>谱系尚空</color>", 10, _treeCards);
                empty.alignment = TextAnchor.MiddleCenter;
                LayoutLeft(empty.GetComponent<RectTransform>(), 0, 40f, InnerWidth, 20f);
                return;
            }

            // ── 布局常量 ──
            const float cellW = 64f, cellH = 66f;   // 2026-08-20 头像化：34→66（头像30 + 名 + 称谓）
            const float gapX = 8f, rowGap = 26f;
            const float gutterW = 50f;   // 左侧代名栏宽
            const float padTop = 6f, padBottom = 8f, padRight = 12f;
            const int rowMin = 0, rowMax = 6; // Gen -3..+3 → 行 0..6

            // ── 分代（越界代丢弃）──
            string[] genNames = { "曾祖辈", "祖辈", "父辈", "本人同辈", "子辈", "孙辈", "曾孙辈" };
            var rows = new List<DispMember>[rowMax + 1];
            for (int i = 0; i <= rowMax; i++) rows[i] = new List<DispMember>();
            foreach (var m in members)
            {
                int r = m.Gen + 3;
                if (r < rowMin || r > rowMax) continue;
                rows[r].Add(m);
            }
            // 空行压缩：只保留非空行（代际跳跃时行距仍连续，视觉留白可接受）
            var usedRows = new List<int>();
            for (int i = 0; i <= rowMax; i++) if (rows[i].Count > 0) usedRows.Add(i);

            // 中心（IsSelf）→ 亲属称谓
            long centerId = 0;
            foreach (var m in members) { if (m.IsSelf) { centerId = m.ActorId; break; } }
            var terms = KinshipTerms(members, centerId);

            // ── 行内排序 + x 槽位（自上而下：同父子女聚簇，按父行槽位顺序）──
            var slotOf = new Dictionary<long, int>();    // ActorId → 行内槽位
            var rowOf = new Dictionary<long, int>();     // ActorId → 所在代行号（Gen+3）
            var xOf = new Dictionary<long, float>();     // ActorId → 画布 x
            var yOf = new Dictionary<long, float>();     // ActorId → 画布 y（从上）
            // 父→子 反向索引
            var childrenOf = new Dictionary<long, List<DispMember>>();
            foreach (var m in members)
                foreach (var pid in m.ParentIds)
                {
                    if (!childrenOf.TryGetValue(pid, out var list)) { list = new List<DispMember>(); childrenOf[pid] = list; }
                    list.Add(m);
                }

            bool firstRow = true;
            int prevRow = -1;
            foreach (int r in usedRows)
            {
                var row = rows[r];
                if (firstRow)
                {
                    // 首行（最古老代）：按年龄稳定排
                    row.Sort((a, b) => (a.IsSelf ? 0 : 1).CompareTo(b.IsSelf ? 0 : 1));
                }
                else
                {
                    // 非首行：按父格槽位聚簇（无父者殿后按年龄）
                    row.Sort((a, b) =>
                    {
                        int sa = MinParentSlot(a, slotOf, prevRow, rowOf), sb = MinParentSlot(b, slotOf, prevRow, rowOf);
                        if (sa != sb) return sa.CompareTo(sb);
                        return (a.Age).CompareTo(b.Age);
                    });
                }
                for (int i = 0; i < row.Count; i++)
                {
                    slotOf[row[i].ActorId] = i;
                    rowOf[row[i].ActorId] = r;
                    xOf[row[i].ActorId] = gutterW + i * (cellW + gapX);
                    yOf[row[i].ActorId] = padTop + (usedRows.IndexOf(r)) * (cellH + rowGap);
                }
                firstRow = false;
                prevRow = r;
            }

            // ── 父格对齐子格均值（自下而上；受同代邻格夹取 → 永不重叠）──
            for (int ri = usedRows.Count - 2; ri >= 0; ri--)
            {
                int r = usedRows[ri];
                int rBelow = usedRows[ri + 1];
                var row = rows[r];
                // 当前行按 x 排序的邻接参考
                var ordered = row.OrderBy(m => xOf[m.ActorId]).ToList();
                for (int i = 0; i < ordered.Count; i++)
                {
                    var m = ordered[i];
                    if (!childrenOf.TryGetValue(m.ActorId, out var kids) || kids.Count == 0) continue;
                    var kidsInBelow = kids.Where(k => xOf.ContainsKey(k.ActorId) && rows[rBelow].Contains(k)).ToList();
                    if (kidsInBelow.Count == 0) continue;
                    float meanX = kidsInBelow.Average(k => xOf[k.ActorId] + cellW * 0.5f) - cellW * 0.5f;
                    // 夹取：不越过左右邻格
                    float lo = i > 0 ? xOf[ordered[i - 1].ActorId] + cellW + gapX : float.MinValue;
                    float hi = i < ordered.Count - 1 ? xOf[ordered[i + 1].ActorId] - cellW - gapX : float.MaxValue;
                    if (meanX >= lo && meanX <= hi) xOf[m.ActorId] = meanX;
                }
            }

            // ── 画布尺寸 ──
            float maxRight = gutterW;
            foreach (var kv in xOf) maxRight = Mathf.Max(maxRight, kv.Value + cellW);
            float canvasW = maxRight + padRight;
            float canvasH = padTop + usedRows.Count * (cellH + rowGap) - rowGap + padBottom;
            _treeCanvas.sizeDelta = new Vector2(Mathf.Max(canvasW, InnerWidth), canvasH);

            // ── 代名栏（左侧，每行一枚）──
            foreach (int r in usedRows)
            {
                if (rows[r].Count == 0) continue;
                int ri = usedRows.IndexOf(r);
                float y = padTop + ri * (cellH + rowGap);
                var label = CreateText(_treeCanvas, $"<color={ZhuluUIStyle.Hex.GoldDim}>{genNames[r]}</color>\n<color=#6a6458>{CN(rows[r].Count)}人</color>", 7, _treeCards);
                label.alignment = TextAnchor.MiddleCenter;
                LayoutLeft(label.GetComponent<RectTransform>(), 2f, y + 4f, gutterW - 6f, cellH - 8f);
            }

            // ── 父子连线（三段式：父底→总线→子顶；先画线后画格 → 格盖线头）──
            foreach (int r in usedRows)
            {
                int ri = usedRows.IndexOf(r);
                int belowIdx = ri + 1;
                if (belowIdx >= usedRows.Count) continue;
                int rBelow = usedRows[belowIdx];
                foreach (var child in rows[rBelow])
                {
                    foreach (var pid in child.ParentIds)
                    {
                        // 父必须恰在紧邻上方的非空行（跨代连线不画——保持画面干净）
                        if (!rowOf.TryGetValue(pid, out int prow) || prow != r) continue;
                        float px = xOf[pid] + cellW * 0.5f;
                        float py = padTop + ri * (cellH + rowGap) + cellH;
                        float cx = xOf[child.ActorId] + cellW * 0.5f;
                        float cy = padTop + (ri + 1) * (cellH + rowGap);
                        float busY = py + rowGap * 0.5f;
                        DrawTreeLine(px, py, busY, cx, cy);
                    }
                }
            }

            // ── 成员格 ──
            foreach (int r in usedRows)
            {
                foreach (var m in rows[r])
                {
                    float x = xOf[m.ActorId];
                    float y = yOf[m.ActorId];
                    CreateMemberCell(_treeCanvas, m, terms, x, y, _treeCards);
                }
            }

            // ── 初始定位：中心成员滚到视口中央 ──
            if (_treePan != null && centerId != 0 && xOf.ContainsKey(centerId))
            {
                _treePan.CenterOn(xOf[centerId] + cellW * 0.5f, yOf[centerId] + cellH * 0.5f);
            }
        }

        /// <summary>成员在紧邻上一行中的最小父槽位（父不在上一行 → int.MaxValue 殿后）。</summary>
        private static int MinParentSlot(DispMember m, Dictionary<long, int> slotOf, int prevRow, Dictionary<long, int> rowOf)
        {
            int min = int.MaxValue;
            foreach (var pid in m.ParentIds)
            {
                if (rowOf.TryGetValue(pid, out int pr) && pr == prevRow
                    && slotOf.TryGetValue(pid, out int s))
                    min = Mathf.Min(min, s);
            }
            return min;
        }

        /// <summary>树连线：竖(父底→总线) + 横(总线) + 竖(总线→子顶) 三段细线。</summary>
        private void DrawTreeLine(float px, float py, float busY, float cx, float cy)
        {
            const float thickness = 1.2f;
            var color = new Color(0.78f, 0.73f, 0.54f, 0.38f); // 暗金细线
            // 竖段：父底 → 总线
            MakeLine(px, py, thickness, busY - py, color);
            // 横段：总线
            if (Mathf.Abs(cx - px) > 0.5f)
            {
                float left = Mathf.Min(px, cx);
                MakeLine(left, busY - thickness * 0.5f, Mathf.Abs(cx - px), thickness, color);
            }
            // 竖段：总线 → 子顶
            MakeLine(cx, busY, thickness, cy - busY, color);
        }

        /// <summary>画布上生成一段纯色矩形线（x,y 为左上角，y 从上向下为正）。</summary>
        private void MakeLine(float x, float y, float w, float h, Color color)
        {
            if (w <= 0f || h <= 0f) return;
            var obj = new GameObject("L", typeof(RectTransform), typeof(Image));
            obj.transform.SetParent(_treeCanvas, false);
            _treeCards.Add(obj);
            var rt = obj.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
            var img = obj.GetComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
        }

        /// <summary>计算各成员相对中心的亲属称谓（爷爷/侄子/叔伯/堂表…；血缘不可辨者不标）。</summary>
        private static Dictionary<long, string> KinshipTerms(List<DispMember> members, long centerId)
        {
            var terms = new Dictionary<long, string>();
            if (centerId == 0 || members == null || members.Count == 0) return terms;

            var byId = new Dictionary<long, DispMember>();
            foreach (var m in members) if (!byId.ContainsKey(m.ActorId)) byId[m.ActorId] = m;
            if (!byId.TryGetValue(centerId, out var c)) return terms;

            var cParents = c.ParentIds.Where(p => byId.ContainsKey(p)).ToList();
            var cParentSet = new HashSet<long>(cParents);
            var cGrand = new HashSet<long>();
            foreach (var pid in cParents)
                foreach (var gp in byId[pid].ParentIds)
                    if (byId.ContainsKey(gp)) cGrand.Add(gp);

            // 反向索引：父 → 子
            var childrenOf = new Dictionary<long, List<long>>();
            foreach (var m in members)
                foreach (var pid in m.ParentIds)
                {
                    if (!childrenOf.TryGetValue(pid, out var list)) { list = new List<long>(); childrenOf[pid] = list; }
                    list.Add(m.ActorId);
                }
            bool IsChildOf(long child, long parent) =>
                childrenOf.TryGetValue(parent, out var l) && l.Contains(child);

            foreach (var m in members)
            {
                if (m.ActorId == centerId) { terms[centerId] = "本人"; continue; }
                string term = "";

                // 配偶
                if (c.SpouseId != 0 && m.ActorId == c.SpouseId) term = m.Female ? "妻" : "夫";
                // 父母
                if (term == "" && cParentSet.Contains(m.ActorId))
                    term = m.Female ? "母亲" : "父亲";
                // 祖父母（外区分：经母亲一系）
                if (term == "" && cGrand.Contains(m.ActorId))
                {
                    bool maternal = false;
                    foreach (var pid in cParents)
                    {
                        var p = byId[pid];
                        if (p.Female && p.ParentIds.Contains(m.ActorId)) { maternal = true; break; }
                    }
                    term = m.Female ? (maternal ? "外婆" : "奶奶") : (maternal ? "外公" : "爷爷");
                }

                int dg = m.Gen - c.Gen;
                if (term == "")
                {
                    if (dg == -3) term = m.Female ? "曾祖母" : "曾祖父";
                    else if (dg == -1)
                    {
                        // 叔伯姑舅姨：与中心父母同源
                        foreach (var pid in cParents)
                        {
                            var p = byId[pid];
                            if (p.ParentIds.Any(gp => m.ParentIds.Contains(gp)))
                            {
                                if (p.Female) term = m.Female ? "姨母" : "舅父";
                                else term = m.Female ? "姑母" : "叔伯";
                                break;
                            }
                        }
                    }
                    else if (dg == 0)
                    {
                        bool sibling = m.ParentIds.Any(pid => cParentSet.Contains(pid));
                        if (sibling)
                        {
                            bool ageKnown = m.Age > 0 && c.Age > 0;
                            if (m.Female) term = !ageKnown ? "姐妹" : (m.Age > c.Age ? "姐" : "妹");
                            else term = !ageKnown ? "兄弟" : (m.Age > c.Age ? "兄" : "弟");
                        }
                        else
                        {
                            // 堂表兄弟：其父母为中心父母的兄弟姊妹
                            bool tang = false, found = false;
                            foreach (var pid in m.ParentIds)
                            {
                                if (!byId.TryGetValue(pid, out var par)) continue;
                                foreach (var cp in cParents)
                                {
                                    var p = byId[cp];
                                    if (p.ParentIds.Any(gp => par.ParentIds.Contains(gp)))
                                    { found = true; tang = !p.Female; break; }
                                }
                                if (found) break;
                            }
                            if (found)
                            {
                                string pref = tang ? "堂" : "表";
                                bool ageKnown = m.Age > 0 && c.Age > 0;
                                term = m.Female
                                    ? (!ageKnown ? pref + "姐妹" : pref + (m.Age > c.Age ? "姐" : "妹"))
                                    : (!ageKnown ? pref + "兄弟" : pref + (m.Age > c.Age ? "兄" : "弟"));
                            }
                        }
                    }
                    else if (dg == 1)
                    {
                        if (IsChildOf(m.ActorId, centerId)) term = m.Female ? "女儿" : "儿子";
                        else
                        {
                            // 侄（兄弟之子女）/ 外甥（姐妹之子女）
                            bool viaBro = false, viaSis = false;
                            foreach (var pid in m.ParentIds)
                                if (byId.TryGetValue(pid, out var par) && par.ActorId != centerId
                                    && par.ParentIds.Any(gp => cParentSet.Contains(gp)))
                                { if (par.Female) viaSis = true; else viaBro = true; }
                            if (viaBro) term = m.Female ? "侄女" : "侄子";
                            else if (viaSis) term = m.Female ? "外甥女" : "外甥";
                        }
                    }
                    else if (dg == 2) term = m.Female ? "孙女" : "孙子";
                    else if (dg == 3) term = m.Female ? "曾孙女" : "曾孙";
                }

                if (!string.IsNullOrEmpty(term)) terms[m.ActorId] = term;
            }
            return terms;
        }

        /// <summary>成员小格（手动定位）：扁平面板浅色框（无九宫格贴图边）+ 名（8号描边，中心红）+ 称谓·称号。
        /// track 必传——曾漏加入销毁清单致换家族时旧格残留叠加。</summary>
        private void CreateMemberCell(Transform parent, DispMember m, Dictionary<long, string> terms, float x, float y, List<GameObject> track)
        {
            const float cellW = 64f, cellH = 66f;
            var cell = new GameObject("C_" + m.ActorId, typeof(RectTransform), typeof(Image), typeof(Button));
            cell.transform.SetParent(parent, false);
            track.Add(cell);
            var rt = cell.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(cellW, cellH);

            // 浅色显示框（扁平面板色；中心者泛红晕）
            var bg = cell.GetComponent<Image>();
            bg.color = m.IsSelf
                ? new Color(0.90f, 0.32f, 0.26f, 0.28f)
                : new Color(0.96f, 0.92f, 0.80f, 0.13f);

            // ── 头像层（2026-08-20：活人=原版头像控件带旗帜/君王底图；死者=灰度墓碑）──
            var holder = new GameObject("AvatarSlot", typeof(RectTransform));
            holder.transform.SetParent(cell.transform, false);
            var hRect = holder.GetComponent<RectTransform>();
            hRect.anchorMin = hRect.anchorMax = new Vector2(0.5f, 1f);
            hRect.pivot = new Vector2(0.5f, 1f);
            hRect.anchoredPosition = new Vector2(0f, -2f);
            hRect.sizeDelta = new Vector2(30f, 30f);

            GameObject avatar = null;
            if (m.Alive)
            {
                Actor liveActor = null;
                try { liveActor = World.world.units.get(m.ActorId); } catch { }
                if (liveActor != null && liveActor.isAlive())
                    avatar = ZhuluAvatar.CreateLiving(holder.transform, liveActor);
            }
            if (avatar == null && !m.Alive)
                avatar = ZhuluAvatar.CreateDead(holder.transform);
            if (avatar == null)
            {
                // 兜底占位：死者无贴图时灰字"故"；活人头像系统不可用则空占位（不可给活人立碑）
                var fb = CreateText(holder.transform, m.Alive ? "" : "故", 9);
                fb.alignment = TextAnchor.MiddleCenter;
                fb.color = new Color(0.5f, 0.48f, 0.45f, 0.9f);
                LayoutLeft(fb.GetComponent<RectTransform>(), 0, 0, 30f, 30f);
            }

            var nameT = CreateText(cell.transform, m.Name, 8);
            nameT.alignment = TextAnchor.UpperCenter;
            nameT.color = m.IsSelf ? SelectedRed : (m.Alive ? PlainWhite : DeadGrey);
            AddOutline(nameT);
            LayoutLeft(nameT.GetComponent<RectTransform>(), 0, 35, cellW, 13);

            // 称谓行：称谓·称号（死者标卒年）
            string term = terms.TryGetValue(m.ActorId, out var t) ? t : "";
            string sub = m.Alive ? m.Title : (m.DiedYear > 0 ? $"卒{m.DiedYear}" : m.Title);
            if (!string.IsNullOrEmpty(term)) sub = term + "·" + sub;
            if (sub.Length > 9) sub = sub.Substring(0, 9);

            var titleT = CreateText(cell.transform, sub, 6);
            titleT.alignment = TextAnchor.UpperCenter;
            titleT.color = m.Alive ? new Color(0.78f, 0.73f, 0.60f) : new Color(0.45f, 0.43f, 0.40f);
            AddOutline(titleT);
            LayoutLeft(titleT.GetComponent<RectTransform>(), 0, 49, cellW, 11);

            long aid = m.ActorId;
            bool isCenter = m.IsSelf;

            // 2026-08-20 悬浮详情：小格截断的称谓/生卒用原生 tooltip 补全（不刷 missing text 日志）
            string hoverName = m.Name ?? "?";
            string hoverTitle = terms.TryGetValue(m.ActorId, out var ht) ? ht + "·" : "";
            hoverTitle += m.Alive ? (m.Title ?? "") : (m.DiedYear > 0 ? $"卒于{m.DiedYear}年" : (m.Title ?? ""));
            string hoverDesc = $"{(m.Alive ? "在世" : "已故")}{(m.Age > 0 ? $" · {CN(m.Age)}岁" : "")}\n{hoverTitle}";
            ZhuluRawTooltip.Attach(cell, hoverName, hoverDesc);

            cell.GetComponent<Button>().onClick.AddListener(() =>
            {
                try
                {
                    if (!isCenter)
                    {
                        // 第一次点击：变红并以其为中心展开上下三代
                        _treeCenterId = aid;
                        ShowPage(1);
                        return;
                    }
                    // 再点本人：跳转史册
                    Actor a = null;
                    try { a = World.world.units.get(aid); } catch { }
                    if (a == null || !a.isAlive()) { WorldTip.showNow("其人已故", false, "top", 1.5f); return; }
                    if (ZhuluPersonHistoryWindow.Instance != null)
                    {
                        ZhuluPersonHistoryWindow.Instance.Show(a);
                        ScrollWindow.showWindow(Main.PersonHistoryWindowId);
                    }
                }
                catch (Exception ex) { ZhuluContext.Instance.LogWarning($"[FamilyWindow] 世系点击异常: {ex.Message}"); }
            });
        }

        private static long GetSpouseIdSafeLocal(Actor a)
        {
            try
            {
                var wife = HarmonyLib.Traverse.Create(a).Field("wife").GetValue<Actor>();
                if (wife != null) return wife.getID();
                var husband = HarmonyLib.Traverse.Create(a).Field("husband").GetValue<Actor>();
                if (husband != null) return husband.getID();
            }
            catch { }
            return 0;
        }

        private static void AddOutline(Text t)
        {
            try
            {
                var outline = t.gameObject.AddComponent<Outline>();
                outline.effectColor = new Color(0f, 0f, 0f, 0.55f);
                outline.effectDistance = new Vector2(1f, -1f);
            }
            catch { }
        }

        // ═══════════════════════ UI 工具 ═══════════════════════

        private GameObject CreateCard(Transform parent, List<GameObject> track, float height)
        {
            // SolidBg-36 模式（Mandate 同款）：容器纯 RectTransform，贴图层向左延 36 盖住九宫格厚装饰边
            var card = new GameObject("Card", typeof(RectTransform));
            card.transform.SetParent(parent, false);
            track.Add(card);
            var rt = card.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(InnerWidth, height);
            AttachCardBg(card.transform, ZhuluUIStyle.InkCard);
            return card;
        }

        /// <summary>卡片背景双层：纯色兜底层 + 九宫格贴图层，均 offsetMin.x=-36 向左延长（被 RectMask2D 裁齐列表左缘）。</summary>
        private static void AttachCardBg(Transform parent, Color color)
        {
            var solidObj = new GameObject("SolidBg", typeof(RectTransform), typeof(Image));
            solidObj.transform.SetParent(parent, false);
            var solidRect = solidObj.GetComponent<RectTransform>();
            solidRect.anchorMin = Vector2.zero;
            solidRect.anchorMax = Vector2.one;
            solidRect.offsetMin = new Vector2(-36f, 0f);
            solidRect.offsetMax = Vector2.zero;
            var solidImg = solidObj.GetComponent<Image>();
            solidImg.color = color;
            solidImg.raycastTarget = false;

            var bgObj = new GameObject("Bg", typeof(RectTransform), typeof(Image));
            bgObj.transform.SetParent(parent, false);
            var bgRect = bgObj.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = new Vector2(-36f, 0f);
            bgRect.offsetMax = Vector2.zero;
            var bgImg = bgObj.GetComponent<Image>();
            bgImg.color = color;
            bgImg.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            bgImg.type = Image.Type.Sliced;
            bgImg.raycastTarget = true;   // 兼作父 Row 的点击区
        }

        private void AddSectionLabel(Transform card, string text)
        {
            var t = CreateText(card, $"<b><color=#ffd8a0>{text}</color></b>", 10);
            t.alignment = TextAnchor.MiddleLeft;
            LayoutLeft(t.GetComponent<RectTransform>(), 8, 2, InnerWidth - 20, 16);
        }

        private GameObject CreateRow(Transform parent, List<GameObject> track, string title, string sub, Action onClick)
        {
            var row = new GameObject("Row", typeof(RectTransform), typeof(Button));
            row.transform.SetParent(parent, false);
            track.Add(row);
            var rt = row.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(InnerWidth, 30f);
            AttachCardBg(row.transform, new Color(0.17f, 0.16f, 0.13f, 0.92f));
            row.GetComponent<Button>().onClick.AddListener(() => onClick?.Invoke());

            var t1 = CreateText(row.transform, $"<b>{title}</b>", 10);
            t1.alignment = TextAnchor.MiddleLeft;
            t1.color = new Color(0.95f, 0.90f, 0.78f, 1f);
            LayoutLeft(t1.GetComponent<RectTransform>(), 8, 14, InnerWidth - 16, 14);

            var t2 = CreateText(row.transform, $"<color=#8d867c>{sub}</color>", 8);
            t2.alignment = TextAnchor.MiddleLeft;
            LayoutLeft(t2.GetComponent<RectTransform>(), 8, 2, InnerWidth - 16, 12);
            return row;
        }

        private Text AddHint(Transform page, List<GameObject> track, string text)
        {
            var t = CreateText(page, $"<color=#6a6458>{text}</color>", 9, track);
            t.alignment = TextAnchor.MiddleCenter;
            LayoutLeft(t.GetComponent<RectTransform>(), 0, 0, InnerWidth, 16);
            return t;
        }

        private Text CreateText(Transform parent, string content, int fontSize, List<GameObject> track = null)
        {
            // 2026-08-20 委托共享样式库（本窗口差异：入 track 清单 + 不设色/对齐保 Unity 默认）
            var t = ZhuluUIStyle.CreateText(parent, content, fontSize);
            if (track != null) track.Add(t.gameObject);
            t.color = new Color(0.2f, 0.2f, 0.2f);            // 本地历史默认色（Text 组件默认深灰）
            t.alignment = TextAnchor.UpperLeft;               // 本地历史默认对齐
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;   // 防 Truncate 整行隐藏（中文行高>字号）
            return t;
        }

        private void ClearChildren(Transform parent, List<GameObject> track)
        {
            foreach (var go in track) { try { if (go != null) Destroy(go); } catch { } }
            track.Clear();
        }

        private static string CN(int n)
        {
            string[] d = { "零", "一", "二", "三", "四", "五", "六", "七", "八", "九" };
            if (n <= 0) return "零";
            if (n < 10) return d[n];
            if (n < 20) return "十" + (n % 10 == 0 ? "" : d[n % 10]);
            if (n < 100) return d[n / 10] + "十" + (n % 10 == 0 ? "" : d[n % 10]);
            return n.ToString();
        }
    }
}
