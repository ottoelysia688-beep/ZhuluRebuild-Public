using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using NeoModLoader.General;
using ZhuluRebuild.Core;
using ZhuluRebuild.Domain;
using ZhuluRebuild.Presentation.Routing;

namespace ZhuluRebuild.UI.Windows
{
    /// <summary>
    /// 科举窗口（阶段1·布局已确认）。
    /// 入口：王国信息窗口第 6 侧边按钮「科举」（zlr_nav_keju_32）。
    /// 内容：①开科设置（开关+间隔−/++人数−/+，钳制 5/10/20 · 2/4/6）
    ///       ②人才库（姓名/评分/科年/科目，空缺自动补位）
    ///       ③武举授职（首名城主/其余队长）
    ///       ④最近开科（同步史册，重大金章）
    /// 数据：全部来自 KejuManager / ChronicleManager 真实数据，无编造。
    /// 批次8迁移（2026-08-23）：骨架收敛至 ZhuluScrollWindowBase（ContentTopBand=56 容纳副标题）——
    /// 删除手写 RebuildUI 约190行与本地 返回钮/文本/贴图 工具；保留"年变全量重建内容卡"语义：
    /// 骨架只建一次，年变/开窗改为 ClearContent+RebuildCards（视觉等价、chrome 不再重复创建）；
    /// 动态牌匾题静态化为双体制合并题（王国名由副状态行承载）。
    /// </summary>
    public class ZhuluKejuWindow : ZhuluScrollWindowBase
    {
        public static ZhuluKejuWindow Instance { get; private set; }

        private Text _subStatText;
        // 批次9a：年变感知改订阅制（原每帧 Update 年检删除——暂停零开销）

        private readonly List<Text> _numTexts = new List<Text>();
        private Image _switchImg;
        private Text _switchText;

        protected override string WindowTitle => "<b>科举取士 · 选贤纳士</b>";
        protected override float WindowWidth => 350f;
        protected override float WindowHeight => 400f;
        protected override float PlaqueWidth => 250f;
        protected override float ContentTopBand => 56f;   // 牌匾26 + 副标题12 + 间距

        public void Initialize()
        {
            Instance = this;
            ZhuluContext.Instance.LogInfo("[ZhuluKejuWindow] 初始化完成（首次打开时构建，防偏左）");
        }

        private void OnEnable()
        {
            if (World.world == null) return;
            // 阶段2M-24：年未就绪（挂载早期 year=1）不构建——防"永远县试"（year%5=1）
            if (ZhuluTime.CurrentYear <= 1) return;
            EnsureBuilt();
            RebuildCards();
            ZhuluYearEvents.Subscribe(OnYearChanged);
        }

        private void OnDisable()
        {
            ZhuluYearEvents.Unsubscribe(OnYearChanged);
        }

        /// <summary>跨年回调（批次9a）：开窗状态下跨年即重建内容卡；关窗时下次 Show 自会重建。</summary>
        private void OnYearChanged(int newYear)
        {
            if (!gameObject.activeInHierarchy || ContentRoot == null) return;
            RebuildCards();
        }

        public void Show()
        {
            if (World.world != null && gameObject.activeInHierarchy)
            {
                // 阶段2M-24：每次显示强制重建（防挂载早期 year=1 的错误固化——kId/year 就绪时数据最新）
                EnsureBuilt();
                RebuildCards();
            }
        }

        // ═══════════════════════ 内容构建（骨架由基类提供；卡片按年全量重建） ═══════════════════════

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

            RebuildCards();
        }

        /// <summary>清空并重建全部内容卡（年变/开窗时调用——原 RebuildUI 的内容段）。</summary>
        private void RebuildCards()
        {
            if (ContentRoot == null) return;
            for (int i = ContentRoot.childCount - 1; i >= 0; i--)
                Destroy(ContentRoot.GetChild(i).gameObject);

            // 卡片（阶段2M：等级差异——邑/子未开科；伯=县试；侯=县试+乡试；公/天命=三级）
            string kId0 = CurrentKingdomId();
            bool canExam = true; // 默认可（kId 空时不明文——防误判"未开科"）
            try
            {
                Kingdom kk0 = ZhuluRebuild.Persistence.KingdomDataExt.FindKingdomById(kId0);
                if (kk0 == null)
                {
                    // 路由 kId 拿不到：尝试用选中王国
                    try
                    {
                        var sel0 = SelectedMetas.selected_kingdom;
                        if (sel0 != null) kk0 = sel0;
                    }
                    catch { }
                }
                if (kk0 != null)
                {
                    bool isMandate0 = PolicyManager.IsMandateHolderForKingdom(kk0);
                    canExam = PolicyManager.GetKingdomRank(kk0, isMandate0) >= 1;
                }
            }
            catch { }
            if (!canExam)
            {
                CreateNoExamCard();   // 邑/子：未开科提示 + 晋升引导
            }
            else
            {
                CreateGongmingCard(); // 🏅 功名榜（按等级）——三级科举说明内置在开科设置卡
            }
            CreateSettingsCard();   // ⚙ 开科设置
            CreateTalentCard();     // 📜 人才库（待命）
            CreateWuCard();         // ⚔ 武举授职
            CreateKejuHistoryCard();// 📖 最近开科

            RefreshContent();
        }

        // ═══════════════════════ 卡片 ═══════════════════════

        /// <summary>建卡片容器（Row 容器：纯色底 -36 + 贴图装饰层同延，RectMask2D 裁齐）</summary>
        private Transform CreateCard(float height)
        {
            GameObject row = new GameObject("Card", typeof(RectTransform));
            row.transform.SetParent(ContentRoot, false);
            var rowRect = row.GetComponent<RectTransform>();
            rowRect.sizeDelta = new Vector2(InnerWidth, height);

            // 纯色底（-36 防贴图左装饰留白偏移切字）
            GameObject solidObj = new GameObject("SolidBg", typeof(RectTransform), typeof(Image));
            solidObj.transform.SetParent(row.transform, false);
            var solidRect = solidObj.GetComponent<RectTransform>();
            solidRect.anchorMin = Vector2.zero;
            solidRect.anchorMax = Vector2.one;
            solidRect.offsetMin = new Vector2(-36f, 0f);
            solidRect.offsetMax = Vector2.zero;
            var solidImg = solidObj.GetComponent<Image>();
            solidImg.color = new Color(0.13f, 0.14f, 0.19f, 0.96f);

            // 贴图装饰层（同延，仅装饰不裁文字）
            GameObject texObj = new GameObject("TexLayer", typeof(RectTransform), typeof(Image));
            texObj.transform.SetParent(row.transform, false);
            var texRect = texObj.GetComponent<RectTransform>();
            texRect.anchorMin = Vector2.zero;
            texRect.anchorMax = Vector2.one;
            texRect.offsetMin = new Vector2(-36f, 0f);
            texRect.offsetMax = Vector2.zero;
            var texImg = texObj.GetComponent<Image>();
            texImg.color = new Color(1f, 1f, 1f, 0.10f);
            try
            {
                Sprite sp = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
                if (sp != null)
                {
                    texImg.sprite = sp;
                    texImg.type = Image.Type.Sliced;
                }
            }
            catch { }
            return row.transform;
        }

        private void CreateCardTitle(Transform card, string title, string colorHex)
        {
            Text t = CreateText(card, $"<color={colorHex}>{title}</color>", 10); // 阶段2R：卡题11→10（与设置/史册窗口统一）
            t.alignment = TextAnchor.MiddleLeft;
            t.color = Color.white;
            var r = t.GetComponent<RectTransform>();
            r.anchorMin = new Vector2(0, 1);
            r.anchorMax = new Vector2(0, 1);
            r.pivot = new Vector2(0, 1);
            r.anchoredPosition = new Vector2(10, -4);
            r.sizeDelta = new Vector2(200f, 16f);
            t.raycastTarget = false;
        }

        // ── 阶段2M ① 未开科提示（邑/子）──
        private void CreateNoExamCard()
        {
            bool nomadic = IsCurrentNomadic();
            Transform card = CreateCard(80f);
            CreateCardTitle(card, nomadic ? "🏕 部族选贤" : "🏛 科举取士", "#f0c060");
            Text t1 = CreateText(card, nomadic ? "<color=#8a8578>本部等级尚不设正式选拔</color>" : "<color=#8a8578>本国爵位尚不设科举</color>", 10);
            t1.alignment = TextAnchor.MiddleCenter;
            var r1 = t1.GetComponent<RectTransform>();
            r1.anchorMin = new Vector2(0, 1); r1.anchorMax = new Vector2(0, 1);
            r1.pivot = new Vector2(0, 1); r1.anchoredPosition = new Vector2(0, -30);
            r1.sizeDelta = new Vector2(InnerWidth, 18f);
            Text t2 = CreateText(card, nomadic ? "<color=#f0a050>需晋【部盟】级以上方可举行选贤</color>" : "<color=#f0a050>需晋【伯】级以上方可开科取士</color>", 9);
            t2.alignment = TextAnchor.MiddleCenter;
            var r2 = t2.GetComponent<RectTransform>();
            r2.anchorMin = new Vector2(0, 1); r2.anchorMax = new Vector2(0, 1);
            r2.pivot = new Vector2(0, 1); r2.anchoredPosition = new Vector2(0, -50);
            r2.sizeDelta = new Vector2(InnerWidth, 15f);
            Text t3 = CreateText(card, "<color=#6a6458>提升国力：取城 / 增人口</color>", 8);
            t3.alignment = TextAnchor.MiddleCenter;
            var r3 = t3.GetComponent<RectTransform>();
            r3.anchorMin = new Vector2(0, 1); r3.anchorMax = new Vector2(0, 1);
            r3.pivot = new Vector2(0, 1); r3.anchoredPosition = new Vector2(0, -66);
            r3.sizeDelta = new Vector2(InnerWidth, 12f);
        }

        // ── 阶段2M ② 三级流程（阶段灯——按等级锁级）──

        // ── 阶段2M ③ 功名榜（本国功名人才）──
        private void CreateGongmingCard()
        {
            string kId = CurrentKingdomId();
            // 阶段2M-25：先收集再定卡高（防溢出——卡高 = 标题区 40 + 行数×18）
            var list = new List<(Actor a, string gong, int rank)>();
            try
            {
                Kingdom kk = ZhuluRebuild.Persistence.KingdomDataExt.FindKingdomById(kId);
                if (kk?.units != null)
                {
                    foreach (var u in kk.units)
                    {
                        if (u == null || !u.isAlive()) continue;
                        if (u.traits == null) continue;
                        int rank = 0; string gong = "";
                        foreach (var t in u.traits)
                        {
                            if (t == null) continue;
                            if (t.id == TraitManager.TRAIT_ZHUANGYUAN) { rank = 5; gong = "状元"; }
                            else if (t.id == TraitManager.TRAIT_JINSHI && rank < 4) { rank = 4; gong = "进士"; }
                            else if (t.id == TraitManager.TRAIT_WUJINSHI && rank < 4) { rank = 4; gong = "武进士"; }
                            else if (t.id == TraitManager.TRAIT_JUREN && rank < 3) { rank = 3; gong = "举人"; }
                            else if (t.id == TraitManager.TRAIT_WUJUREN && rank < 3) { rank = 3; gong = "武举人"; }
                            else if (t.id == TraitManager.TRAIT_XIUCAI && rank < 2) { rank = 2; gong = "秀才"; }
                            else if (t.id == TraitManager.TRAIT_WUXIUCAI && rank < 2) { rank = 2; gong = "武秀才"; }
                            else if (t.id == TraitManager.NOMAD_FIRST_SCHOLAR) { rank = 5; gong = "王庭首贤"; }
                            else if (t.id == TraitManager.NOMAD_COURT_SCHOLAR && rank < 4) { rank = 4; gong = "汗庭贤臣"; }
                            else if (t.id == TraitManager.NOMAD_CHILIARCH && rank < 4) { rank = 4; gong = "千夫长"; }
                            else if (t.id == TraitManager.NOMAD_LEAGUE_SCHOLAR && rank < 3) { rank = 3; gong = "部盟贤士"; }
                            else if (t.id == TraitManager.NOMAD_CENTURION && rank < 3) { rank = 3; gong = "百夫长"; }
                            else if (t.id == TraitManager.NOMAD_TRIBE_SCHOLAR && rank < 2) { rank = 2; gong = "部族贤士"; }
                            else if (t.id == TraitManager.NOMAD_WARRIOR && rank < 2) { rank = 2; gong = "勇士"; }
                        }
                        if (rank > 0) list.Add((u, gong, rank));
                    }
                }
            }
            catch { }
            list.Sort((a, b) => b.rank.CompareTo(a.rank));
            int n = Mathf.Min(6, list.Count);
            Transform card = CreateCard(n == 0 ? 70f : 40f + n * 18f); // 阶段2M-25：动态卡高（防溢出）
            bool currentNomadic = IsCurrentNomadic();
            CreateCardTitle(card, currentNomadic ? "🏅 部族贤勇榜" : "🏅 功名榜", "#FFD700");
            Text hint = CreateText(card, $"<color=#6a6458>{(currentNomadic ? "本部贤勇" : "本国功名贤才")} {list.Count} 人</color>", 8);
            hint.alignment = TextAnchor.MiddleRight;
            var hr = hint.GetComponent<RectTransform>();
            hr.anchorMin = new Vector2(1, 1); hr.anchorMax = new Vector2(1, 1);
            hr.pivot = new Vector2(1, 1); hr.anchoredPosition = new Vector2(-10, -5);
            hr.sizeDelta = new Vector2(150f, 12f);
            if (n == 0)
            {
                Text empty = CreateText(card, "<color=#6a6458>— 本国暂无功名贤才 —</color>", 9);
                empty.alignment = TextAnchor.MiddleCenter;
                var er = empty.GetComponent<RectTransform>();
                er.anchorMin = new Vector2(0, 1); er.anchorMax = new Vector2(0, 1);
                er.pivot = new Vector2(0, 1); er.anchoredPosition = new Vector2(0, -28);
                er.sizeDelta = new Vector2(InnerWidth, 18f);
                return;
            }
            for (int i = 0; i < n; i++)
            {
                var item = list[i];
                string gcolor = item.rank >= 5 ? "#FFD700" : item.rank >= 4 ? "#70d468" : item.rank >= 3 ? "#f0c060" : "#8a8578";
                Text row = CreateText(card, $"<color=#e8e0d0>{item.a.getName() ?? "?"}</color> <color={gcolor}>【{item.gong}】</color> <size=7><color=#6a6f7c>{(item.a.isCityLeader() ? "城主" : "在野")}</color></size>", 9);
                row.alignment = TextAnchor.MiddleLeft;
                RectTransform rr = row.GetComponent<RectTransform>();
                rr.anchorMin = new Vector2(0, 1); rr.anchorMax = new Vector2(0, 1);
                rr.pivot = new Vector2(0, 1);
                rr.anchoredPosition = new Vector2(10, -26 - i * 18);
                rr.sizeDelta = new Vector2(InnerWidth - 20, 16f);
            }
        }

        // ── ① 开科设置 ──
        private void CreateSettingsCard()
        {
            bool nomadic = IsCurrentNomadic();
            Transform card = CreateCard(96f);
            CreateCardTitle(card, nomadic ? "⚙ 选贤设置" : "⚙ 开科设置", ZhuluUIStyle.Hex.Gold);

            // 开关行
            GameObject swObj = new GameObject("Switch", typeof(RectTransform), typeof(Image), typeof(Button));
            swObj.transform.SetParent(card, false);
            var swRect = swObj.GetComponent<RectTransform>();
            swRect.anchorMin = new Vector2(0, 1);
            swRect.anchorMax = new Vector2(0, 1);
            swRect.pivot = new Vector2(0, 1);
            swRect.anchoredPosition = new Vector2(10, -26);
            swRect.sizeDelta = new Vector2(34f, 18f);
            var swImg = swObj.GetComponent<Image>();
            swImg.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            swImg.type = Image.Type.Sliced;
            swImg.color = new Color(0.42f, 0.50f, 0.42f, 1f); // 开=绿，刷新时改
            _switchImg = swImg;

            Text swText = CreateText(swObj.transform, "", 9);
            swText.alignment = TextAnchor.MiddleCenter;
            var sr = swText.GetComponent<RectTransform>();
            sr.anchorMin = Vector2.zero;
            sr.anchorMax = Vector2.one;
            sr.sizeDelta = Vector2.zero;
            swText.raycastTarget = false;
            _switchText = swText;

            Text swName = CreateText(card, nomadic ? "选贤" : "开科", 10);
            swName.alignment = TextAnchor.MiddleLeft;
            var sn = swName.GetComponent<RectTransform>();
            sn.anchorMin = new Vector2(0, 1);
            sn.anchorMax = new Vector2(0, 1);
            sn.pivot = new Vector2(0, 1);
            sn.anchoredPosition = new Vector2(52, -26);
            sn.sizeDelta = new Vector2(60f, 16f);
            swName.raycastTarget = false;

            // 阶段2M-28：三级科举说明内置（按等级——静态——不实时）
            int rank2 = 0;
            try
            {
                string kId2 = CurrentKingdomId();
                Kingdom kk2 = ZhuluRebuild.Persistence.KingdomDataExt.FindKingdomById(kId2);
                if (kk2 != null) rank2 = PolicyManager.GetKingdomRank(kk2, PolicyManager.IsMandateHolderForKingdom(kk2));
            }
            catch { }
            string builtin = nomadic
                ? "三级选拔：部族选拔✓ · 部盟选拔✓ · 汗庭选拔✓"
                : "三级科举：县试（秀才）✓ · 乡试（举人）✓ · 殿试（进士/状元）✓";
            if (rank2 == 1) builtin = nomadic
                ? "三级选拔：部族选拔✓ · 部盟选拔需【汗国】· 汗庭选拔需【大汗庭】"
                : "三级科举：县试（秀才）· 乡试需【侯】· 殿试需【公】";
            else if (rank2 == 2) builtin = nomadic
                ? "三级选拔：部族选拔✓ · 部盟选拔✓ · 汗庭选拔需【大汗庭】"
                : "三级科举：县试（秀才）✓ · 乡试（举人）✓ · 殿试需【公】";
            Text swDesc = CreateText(card, $"<color=#8a8578>{builtin}（{ZhuluConfig.Instance.KejuIntervalYears} 年一届）</color>", 8);
            swDesc.alignment = TextAnchor.MiddleLeft;
            var sd = swDesc.GetComponent<RectTransform>();
            sd.anchorMin = new Vector2(0, 1);
            sd.anchorMax = new Vector2(0, 1);
            sd.pivot = new Vector2(0, 1);
            sd.anchoredPosition = new Vector2(110, -26);
            sd.sizeDelta = new Vector2(160f, 12f);
            swDesc.raycastTarget = false;

            swObj.GetComponent<Button>().onClick.AddListener(() =>
            {
                string kId = CurrentKingdomId();
                if (string.IsNullOrEmpty(kId)) return;
                var s = KejuManager.Instance.GetSettings(kId);
                KejuManager.Instance.SetSettings(kId, enabled: !s.Enabled);
                RefreshSwitch();
            });

            // 间隔行
            CreateNumRow(card, -48, "间隔", nomadic ? "三级选拔轮次（部族→部盟→汗庭）" : "三级考试轮次（县试→乡试→殿试）",
                ZhuluConfig.Instance.KejuIntervalYears, 5, 10,
                () => ZhuluConfig.Instance.KejuIntervalYears,
                v => { ZhuluConfig.Instance.KejuIntervalYears = v; ZhuluConfig.Instance.Save(); }, 0);

            // 人数行
            CreateNumRow(card, -72, "人数", "每科录取人数（2 / 4 / 6 人）",
                KejuManager.DefaultCount, KejuManager.MinCount, KejuManager.MaxCount,
                () => KejuManager.Instance.GetSettings(CurrentKingdomId()).Count,
                v => KejuManager.Instance.SetSettings(CurrentKingdomId(), count: v), 1);
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

        private bool IsCurrentNomadic()
        {
            try
            {
                Kingdom k = ZhuluRebuild.Persistence.KingdomDataExt.FindKingdomById(CurrentKingdomId());
                return k != null && KingdomCivilizationService.Instance.IsNomadic(k);
            }
            catch { return false; }
        }

        /// <summary>配置行：名称 + 描述（左）+ −/数值/+（右）——抄 ZhuluSettingsWindow.CreateNumRow</summary>
        private void CreateNumRow(Transform card, float y, string name, string desc, int initial, int min, int max, Func<int> getter, Action<int> setter, int index)
        {
            Text nameText = CreateText(card, name, 10);
            nameText.alignment = TextAnchor.MiddleLeft;
            var nr = nameText.GetComponent<RectTransform>();
            nr.anchorMin = new Vector2(0, 1);
            nr.anchorMax = new Vector2(0, 1);
            nr.pivot = new Vector2(0, 1);
            nr.anchoredPosition = new Vector2(10, y);
            nr.sizeDelta = new Vector2(60f, 16f);
            nameText.raycastTarget = false;

            Text descText = CreateText(card, $"<color=#6a6458>{desc}</color>", 8);
            descText.alignment = TextAnchor.MiddleLeft;
            var dr = descText.GetComponent<RectTransform>();
            dr.anchorMin = new Vector2(0, 1);
            dr.anchorMax = new Vector2(0, 1);
            dr.pivot = new Vector2(0, 1);
            dr.anchoredPosition = new Vector2(70, y);
            dr.sizeDelta = new Vector2(150f, 12f);
            descText.raycastTarget = false;

            // − 按钮（右 -100）
            GameObject minusObj = CreateSmallButton(card, "−", new Vector2(-100, y));
            minusObj.GetComponent<Button>().onClick.AddListener(() =>
            {
                string kId = CurrentKingdomId();
                if (string.IsNullOrEmpty(kId)) return;
                int v = Mathf.Clamp(getter() - 1, min, max);
                setter(v);
                if (_numTexts.Count > index && _numTexts[index] != null) _numTexts[index].text = v.ToString();
            });

            // 数值（右 -62）
            Text numText = CreateText(card, initial.ToString(), 10);
            numText.alignment = TextAnchor.MiddleCenter;
            numText.color = new Color(1f, 0.85f, 0.75f);
            var nRect = numText.GetComponent<RectTransform>();
            nRect.anchorMin = new Vector2(1, 1);
            nRect.anchorMax = new Vector2(1, 1);
            nRect.pivot = new Vector2(0.5f, 1);
            nRect.anchoredPosition = new Vector2(-62, y);
            nRect.sizeDelta = new Vector2(36f, 18f);
            numText.raycastTarget = false;

            // + 按钮（右 -10）
            GameObject plusObj = CreateSmallButton(card, "+", new Vector2(-10, y));
            plusObj.GetComponent<Button>().onClick.AddListener(() =>
            {
                string kId = CurrentKingdomId();
                if (string.IsNullOrEmpty(kId)) return;
                int v = Mathf.Clamp(getter() + 1, min, max);
                setter(v);
                if (_numTexts.Count > index && _numTexts[index] != null) _numTexts[index].text = v.ToString();
            });

            while (_numTexts.Count <= index) { _numTexts.Add(null); }
            _numTexts[index] = numText;
        }

        private GameObject CreateSmallButton(Transform parent, string label, Vector2 pos)
        {
            GameObject btnObj = new GameObject("Btn" + label, typeof(RectTransform), typeof(Image), typeof(Button));
            btnObj.transform.SetParent(parent, false);
            var r = btnObj.GetComponent<RectTransform>();
            r.anchorMin = new Vector2(1, 1);
            r.anchorMax = new Vector2(1, 1);
            r.pivot = new Vector2(1, 1);
            r.anchoredPosition = pos;
            r.sizeDelta = new Vector2(20f, 18f);
            var img = btnObj.GetComponent<Image>();
            img.color = new Color(0.20f, 0.22f, 0.28f, 1f);
            img.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            img.type = Image.Type.Sliced;
            Text t = CreateText(btnObj.transform, $"<b>{label}</b>", 10);
            t.alignment = TextAnchor.MiddleCenter;
            t.raycastTarget = false;
            var tr = t.GetComponent<RectTransform>();
            tr.anchorMin = Vector2.zero;
            tr.anchorMax = Vector2.one;
            tr.sizeDelta = Vector2.zero;
            return btnObj;
        }

        // ── ② 人才库 ──
        private void CreateTalentCard()
        {
            bool nomadic = IsCurrentNomadic();
            Transform card = CreateCard(150f);
            _talentCard = card;
            CreateCardTitle(card, nomadic ? "📜 贤勇库（待命）" : "📜 人才库（待命）", "#6ca3fd");
            Text hint = CreateText(card, "<color=#6a6458>官位空缺时按评分自动补位</color>", 8);
            hint.alignment = TextAnchor.MiddleRight;
            var hr = hint.GetComponent<RectTransform>();
            hr.anchorMin = new Vector2(1, 1);
            hr.anchorMax = new Vector2(1, 1);
            hr.pivot = new Vector2(1, 1);
            hr.anchoredPosition = new Vector2(-10, -5);
            hr.sizeDelta = new Vector2(150f, 12f);
            hint.raycastTarget = false;

            // 表头
            CreateTableHeader(card, -24, new[] { ("姓名", 10f), ("评分", 118f), (nomadic ? "选拔年" : "科年", 165f), (nomadic ? "类别" : "科目", 215f) });

            // 行区域（动态填充：行锚点 y=-44 起，每行 20）
            // 空态
            Text empty = CreateText(card, "— 人才库暂无待命贤才 —", 10);
            empty.alignment = TextAnchor.MiddleCenter;
            var er = empty.GetComponent<RectTransform>();
            er.anchorMin = new Vector2(0, 1);
            er.anchorMax = new Vector2(0, 1);
            er.pivot = new Vector2(0, 1);
            er.anchoredPosition = new Vector2(0, -50);
            er.sizeDelta = new Vector2(InnerWidth, 22f);
            empty.color = new Color(0.55f, 0.58f, 0.70f, 1f);
            empty.gameObject.SetActive(false);
            _talentEmpty = empty;
        }

        // ── ③ 武举授职 ──
        private void CreateWuCard()
        {
            bool nomadic = IsCurrentNomadic();
            Transform card = CreateCard(140f);
            _wuCard = card;
            CreateCardTitle(card, nomadic ? "⚔ 勇士授职" : "⚔ 武举授职", "#f78d8d");
            Text hint = CreateText(card, nomadic ? "<color=#6a6458>军功晋身 · 千夫长可任断事官</color>" : "<color=#6a6458>首名任城主 · 其余队长衔</color>", 8); // 阶段3F
            hint.alignment = TextAnchor.MiddleRight;
            var hr = hint.GetComponent<RectTransform>();
            hr.anchorMin = new Vector2(1, 1);
            hr.anchorMax = new Vector2(1, 1);
            hr.pivot = new Vector2(1, 1);
            hr.anchoredPosition = new Vector2(-10, -5);
            hr.sizeDelta = new Vector2(150f, 12f);
            hint.raycastTarget = false;

            CreateTableHeader(card, -24, new[] { ("姓名", 10f), ("职位", 118f), (nomadic ? "授职年" : "科年", 215f) }); // 阶段3F

            Text empty = CreateText(card, nomadic ? "— 暂无勇士授职记录 —" : "— 暂无武举授职记录 —", 10); // 阶段3F
            empty.alignment = TextAnchor.MiddleCenter;
            var er = empty.GetComponent<RectTransform>();
            er.anchorMin = new Vector2(0, 1);
            er.anchorMax = new Vector2(0, 1);
            er.pivot = new Vector2(0, 1);
            er.anchoredPosition = new Vector2(0, -50);
            er.sizeDelta = new Vector2(InnerWidth, 22f);
            empty.color = new Color(0.55f, 0.58f, 0.70f, 1f);
            empty.gameObject.SetActive(false);
            _wuEmpty = empty;
        }

        private void CreateTableHeader(Transform card, float y, (string label, float x)[] cols)
        {
            foreach (var (label, x) in cols)
            {
                Text t = CreateText(card, label, 8);
                t.alignment = TextAnchor.MiddleLeft;
                t.color = new Color(0.55f, 0.58f, 0.70f, 1f);
                var r = t.GetComponent<RectTransform>();
                r.anchorMin = new Vector2(0, 1);
                r.anchorMax = new Vector2(0, 1);
                r.pivot = new Vector2(0, 1);
                r.anchoredPosition = new Vector2(x, y);
                r.sizeDelta = new Vector2(90f, 12f);
                t.raycastTarget = false;
            }
        }

        // ── ④ 最近开科 ──
        private void CreateKejuHistoryCard()
        {
            bool nomadic = IsCurrentNomadic();
            Transform card = CreateCard(160f);
            _histCard = card;
            CreateCardTitle(card, nomadic ? "📖 最近选拔" : "📖 最近开科", "#ffd8d9");
            Text hint = CreateText(card, "<color=#6a6458>同步史册</color>", 8);
            hint.alignment = TextAnchor.MiddleRight;
            var hr = hint.GetComponent<RectTransform>();
            hr.anchorMin = new Vector2(1, 1);
            hr.anchorMax = new Vector2(1, 1);
            hr.pivot = new Vector2(1, 1);
            hr.anchoredPosition = new Vector2(-10, -5);
            hr.sizeDelta = new Vector2(80f, 12f);
            hint.raycastTarget = false;

            Text empty = CreateText(card, nomadic ? "— 暂无选拔记录 —" : "— 暂无开科记录 —", 10); // 阶段3G
            empty.alignment = TextAnchor.MiddleCenter;
            var er = empty.GetComponent<RectTransform>();
            er.anchorMin = new Vector2(0, 1);
            er.anchorMax = new Vector2(0, 1);
            er.pivot = new Vector2(0, 1);
            er.anchoredPosition = new Vector2(0, -50);
            er.sizeDelta = new Vector2(InnerWidth, 22f);
            empty.color = new Color(0.55f, 0.58f, 0.70f, 1f);
            empty.gameObject.SetActive(false);
            _histEmpty = empty;
        }

        private Transform _talentCard, _wuCard, _histCard;
        private Text _talentEmpty, _wuEmpty, _histEmpty;
        private readonly List<GameObject> _talentRows = new List<GameObject>();
        private readonly List<GameObject> _wuRows = new List<GameObject>();
        private readonly List<GameObject> _histRows = new List<GameObject>();

        // ═══════════════════════ 内容刷新 ═══════════════════════

        private void RefreshContent()
        {
            if (ContentRoot == null) return;

            string kId = CurrentKingdomId();
            bool isKingdom = !string.IsNullOrEmpty(kId);

            if (!isKingdom)
            {
                _subStatText.text = "<color=#6a6458>请从王国信息窗口进入</color>";
                return;
            }

            var s = KejuManager.Instance.GetSettings(kId);
            var pool = KejuManager.Instance.GetTalentPool(kId);
            var caps = KejuManager.Instance.GetCaptainMarks(kId);

            string kName = "";
            try
            {
                var k = World.world.kingdoms.get(int.Parse(kId));
                if (k != null) kName = k.name;
            }
            catch { }

            bool nomadic = false;
            try
            {
                var typeK = ZhuluRebuild.Persistence.KingdomDataExt.FindKingdomById(kId);
                nomadic = typeK != null && KingdomCivilizationService.Instance.IsNomadic(typeK);
            }
            catch { }

            // 阶段2V：叛军无正式选拔体制
            try
            {
                var kR = World.world.kingdoms.get(int.Parse(kId));
                if (Domain.ZhuluRebelService.IsRebel(kR))
                {
                    // 批次8迁移：叛军提示并入副状态行（牌匾静态化）
                    _subStatText.text = $"<b>{kName} · 临时叛军体制</b> <color={UI.ZhuluUIStyle.Hex.Negative}>—— 叛乱期间不运行正式官制与选拔；叛乱成功后按国王种族确定体系。</color>";
                    return;
                }
            }
            catch { }

            // 批次8迁移：王国名/体制由副状态行承载（牌匾静态化）
            _subStatText.text = $"<b>{kName}</b> · 建元 <color=#ffd8d9>{ZhuluTime.CurrentYear}</color> 年 · {(nomadic ? "贤勇库" : "人才库")}待命 <color={UI.ZhuluUIStyle.Hex.Azure}>{pool.Count}</color> 人 · {(nomadic ? "勇士授职" : "武举授职")} <color=#f78d8d>{caps.Count}</color> 人";

            // 开关状态
            RefreshSwitch();

            // 数值行
            if (_numTexts.Count > 0 && _numTexts[0] != null) _numTexts[0].text = ZhuluConfig.Instance.KejuIntervalYears.ToString();
            if (_numTexts.Count > 1 && _numTexts[1] != null) _numTexts[1].text = s.Count.ToString();

            // 人才库行
            RefreshTalentRows(pool);
            // 武举行
            RefreshWuRows(caps);
            // 最近开科
            RefreshHistoryRows(kId);

            LayoutRebuilder.ForceRebuildLayoutImmediate(ContentRoot.GetComponent<RectTransform>());
        }

        private void RefreshSwitch()
        {
            if (_switchImg == null || _switchText == null) return;
            string kId = CurrentKingdomId();
            bool on = !string.IsNullOrEmpty(kId) && KejuManager.Instance.GetSettings(kId).Enabled;
            _switchImg.color = on ? new Color(0.42f, 0.66f, 0.42f, 1f) : new Color(0.36f, 0.36f, 0.42f, 1f);
            _switchText.text = on ? "开" : "关";
        }

        private void RefreshTalentRows(List<KejuManager.TalentEntry> pool)
        {
            ClearRows(_talentRows);
            if (pool == null || pool.Count == 0)
            {
                if (_talentEmpty != null) _talentEmpty.gameObject.SetActive(true);
                return;
            }
            if (_talentEmpty != null) _talentEmpty.gameObject.SetActive(false);
            // 评分降序，最多 5 行
            pool.Sort((a, b) => b.Score.CompareTo(a.Score));
            int n = Mathf.Min(pool.Count, 5);
            for (int i = 0; i < n; i++)
            {
                var e = pool[i];
                GameObject row = new GameObject("TalentRow", typeof(RectTransform));
                row.transform.SetParent(_talentCard != null ? _talentCard : ContentRoot, false);
                var r = row.GetComponent<RectTransform>();
                r.anchorMin = new Vector2(0, 1);
                r.anchorMax = new Vector2(0, 1);
                r.pivot = new Vector2(0, 1);
                r.anchoredPosition = new Vector2(0f, -44f - i * 20f);
                r.sizeDelta = new Vector2(InnerWidth, 20f);
                // 4 列：姓名/评分/科年/科目（用点锚定位）
                CreateCell(row, e.Name ?? "贤士", 10f, "#c9d4ff", 9);
                CreateCell(row, e.Score.ToString(), 118f, "#70d468", 9);
                CreateCell(row, e.Year.ToString(), 165f, "#ffd8d9", 9);
                bool nomadic = IsCurrentNomadic();
                CreateCell(row, e.BranchWu
                    ? $"<color=#f78d8d>{(nomadic ? "勇士选" : "武举")}</color>"
                    : $"<color={UI.ZhuluUIStyle.Hex.Azure}>{(nomadic ? "贤士选" : "文举")}</color>", 215f, "", 9);
                _talentRows.Add(row);
            }
        }

        private void RefreshWuRows(List<KejuManager.CaptainMark> caps)
        {
            ClearRows(_wuRows);
            if (caps == null || caps.Count == 0)
            {
                if (_wuEmpty != null) _wuEmpty.gameObject.SetActive(true);
                return;
            }
            if (_wuEmpty != null) _wuEmpty.gameObject.SetActive(false);
            int n = Mathf.Min(caps.Count, 4);
            for (int i = 0; i < n; i++)
            {
                var e = caps[i];
                GameObject row = new GameObject("WuRow", typeof(RectTransform));
                row.transform.SetParent(_wuCard != null ? _wuCard : ContentRoot, false);
                var r = row.GetComponent<RectTransform>();
                r.anchorMin = new Vector2(0, 1);
                r.anchorMax = new Vector2(0, 1);
                r.pivot = new Vector2(0, 1);
                r.anchoredPosition = new Vector2(0f, -44f - i * 20f);
                r.sizeDelta = new Vector2(InnerWidth, 20f);
                CreateCell(row, e.Name ?? "将", 10f, "#c9d4ff", 9);
                CreateCell(row, e.Title ?? "队长", 118f, "#f78d8d", 9);
                CreateCell(row, e.Year.ToString(), 215f, "#ffd8d9", 9);
                _wuRows.Add(row);
            }
        }

        private void RefreshHistoryRows(string kId)
        {
            ClearRows(_histRows);
            List<ChronicleEntry> entries = null;
            try { entries = ChronicleManager.Instance.GetKingdomChronicle(kId); } catch { }
            if (entries == null) entries = new List<ChronicleEntry>();

            // 过滤开科事件（最近 6 条）
            int shown = 0;
            for (int i = entries.Count - 1; i >= 0 && shown < 6; i--)
            {
                var e = entries[i];
                if (e == null || string.IsNullOrEmpty(e.ContentText)) continue;
                if (!e.ContentText.Contains("开科取士") && !e.ContentText.Contains("开科")) continue;
                shown++;
                GameObject row = new GameObject("HistRow", typeof(RectTransform));
                row.transform.SetParent(_histCard != null ? _histCard : ContentRoot, false);
                var r = row.GetComponent<RectTransform>();
                r.anchorMin = new Vector2(0, 1);
                r.anchorMax = new Vector2(0, 1);
                r.pivot = new Vector2(0, 1);
                r.anchoredPosition = new Vector2(0f, -24f - shown * 22f);
                r.sizeDelta = new Vector2(InnerWidth, 22f);
                // 年份
                CreateCell(row, e.Year.ToString(), 10f, "#ffd8d9", 9);
                // 文本（Wrap 防溢出，宽 240 左对齐）
                Text t = CreateText(row.transform, e.ContentText, 9);
                t.alignment = TextAnchor.UpperLeft;
                t.horizontalOverflow = HorizontalWrapMode.Wrap;
                t.verticalOverflow = VerticalWrapMode.Overflow;
                t.color = new Color(0.85f, 0.84f, 0.92f, 1f);
                var tr = t.GetComponent<RectTransform>();
                tr.anchorMin = new Vector2(0, 0.5f);
                tr.anchorMax = new Vector2(0, 0.5f);
                tr.pivot = new Vector2(0, 0.5f);
                tr.anchoredPosition = new Vector2(50f, 0f);
                tr.sizeDelta = new Vector2(260f, 20f);
                t.raycastTarget = false;
                // 重大金章
                if (e.IsWorldLevelMajor)
                {
                    Text badge = CreateText(row.transform, "<color=#70d468>重大</color>", 8);
                    badge.alignment = TextAnchor.MiddleLeft;
                    var br = badge.GetComponent<RectTransform>();
                    br.anchorMin = new Vector2(1, 1);
                    br.anchorMax = new Vector2(1, 1);
                    br.pivot = new Vector2(1, 1);
                    br.anchoredPosition = new Vector2(-8, -3);
                    br.sizeDelta = new Vector2(30f, 12f);
                    badge.raycastTarget = false;
                }
                _histRows.Add(row);
            }
            if (shown == 0)
            {
                if (_histEmpty != null) _histEmpty.gameObject.SetActive(true);
            }
            else if (_histEmpty != null) _histEmpty.gameObject.SetActive(false);
        }

        private void CreateCell(GameObject row, string text, float x, string colorHex, int fontSize)
        {
            Text t = CreateText(row.transform, string.IsNullOrEmpty(colorHex) ? text : $"<color={colorHex}>{text}</color>", fontSize);
            t.alignment = TextAnchor.MiddleLeft;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            var r = t.GetComponent<RectTransform>();
            r.anchorMin = new Vector2(0, 0.5f);
            r.anchorMax = new Vector2(0, 0.5f);
            r.pivot = new Vector2(0, 0.5f);
            r.anchoredPosition = new Vector2(x, 0f);
            r.sizeDelta = new Vector2(100f, 16f);
            t.raycastTarget = false;
        }

        private void ClearRows(List<GameObject> rows)
        {
            foreach (var g in rows)
            {
                if (g != null)
                {
                    if (g.activeSelf) g.SetActive(false); // 先隐藏防同帧双份闪烁
                    Destroy(g);
                }
            }
            rows.Clear();
        }

        // ═══════════════════════ 工具 ═══════════════════════
    }
}
