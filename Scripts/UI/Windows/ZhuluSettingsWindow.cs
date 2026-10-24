using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using ZhuluRebuild.Core;
using ZhuluRebuild.Gameplay;

namespace ZhuluRebuild.UI.Windows
{
    /// <summary>
    /// 逐鹿设置 v3：参考 Steam 版 mod（玄鉴仙族/PopControl）配置行风格——
    /// 名称+描述+右侧开关/数值，全部真实读写 ZhuluConfig（FeatureFlags.json 持久化）。
    /// 批次8迁移（2026-08-23）：骨架收敛至 ZhuluScrollWindowBase（ContentTopBand=58 容纳副标题；
    /// WantsScrollbar=true 由基类创建滚动条）——删除手写 RebuildUI 约170行与本地工具。
    /// 经验保留：Initialize 不构建防偏左；首次 OnEnable 构建。
    /// </summary>
    public class ZhuluSettingsWindow : ZhuluScrollWindowBase
    {
        public static ZhuluSettingsWindow Instance { get; private set; }

        // 开关控件（0~10 原功能；11 地图爵位；12 爵位配色）
        private readonly List<Image> _switchImgs = new List<Image>();
        private readonly List<Text> _switchTexts = new List<Text>();
        // 数值控件（0科举间隔 1天命结算间隔 2预警阈值）
        private readonly List<Text> _numTexts = new List<Text>();
        private Text _rollbackStatText;

        protected override string WindowTitle => "<b>逐鹿设置 · 控制面板</b>";
        protected override float WindowWidth => 350f;   // 2026-08-19：开关 8→11 行，加高适配（仍可滚动）
        protected override float WindowHeight => 440f;
        protected override float PlaqueWidth => 250f;
        protected override float ContentTopBand => 58f;   // 牌匾26 + 副标题12 + 间距
        protected override bool WantsScrollbar => true;

        public void Initialize()
        {
            Instance = this;
            ZhuluContext.Instance.LogInfo("[ZhuluSettingsWindow] 初始化完成（首次打开时构建，防偏左）");
        }

        private void OnEnable()
        {
            if (World.world == null) return;
            EnsureBuilt();
            RefreshAll();
        }

        public void Show()
        {
            if (World.world != null && gameObject.activeInHierarchy)
            {
                EnsureBuilt();
                RefreshAll();
            }
        }

        public void Hide()
        {
            ZhuluContext.Instance.LogInfo("[ZhuluSettingsWindow] 隐藏设置面板");
        }

        // ═══════════════════════ 结构（骨架由基类提供） ═══════════════════════

        protected override void BuildContent()
        {
            // 副标题（固定头部：挂 Bg 绝对定位不随滚动）
            Text subText = CreateText(Bg, "<color=#9a948a>逐鹿双体制系统 · 控制面板</color>", 9);
            subText.alignment = TextAnchor.MiddleCenter;
            subText.raycastTarget = false;
            var subRect = subText.GetComponent<RectTransform>();
            subRect.anchorMin = new Vector2(0.5f, 1);
            subRect.anchorMax = new Vector2(0.5f, 1);
            subRect.pivot = new Vector2(0.5f, 1);
            subRect.anchoredPosition = new Vector2(-8, -38);
            subRect.sizeDelta = new Vector2(InnerWidth, 12f);

            // 四张卡片
            CreateSwitchCard();       // ⚙ 功能开关
            CreateNumberCard();       // ⚖ 数值调整
            CreateRollbackCard();     // 🛡 安全数值回滚
            CreateInfoCard();         // 📜 存档机制 + 版本

            RefreshAll();
        }

        // ═══════════════════════ 卡片 ═══════════════════════

        /// <summary>建卡片容器（Row 容器：纯色底 + 贴图装饰层，同父级绝对对齐）</summary>
        private Transform CreateCard(float height)
        {
            GameObject row = new GameObject("Card", typeof(RectTransform));
            row.transform.SetParent(ContentRoot, false);
            var rowRect = row.GetComponent<RectTransform>();
            rowRect.sizeDelta = new Vector2(InnerWidth, height);

            // 纯色底（-36 防贴图左装饰留白偏移切字，遮罩裁齐左缘）
            GameObject solidObj = new GameObject("SolidBg", typeof(RectTransform), typeof(Image));
            solidObj.transform.SetParent(row.transform, false);
            var solidRect = solidObj.GetComponent<RectTransform>();
            solidRect.anchorMin = Vector2.zero;
            solidRect.anchorMax = Vector2.one;
            solidRect.offsetMin = new Vector2(-36f, 0f);
            solidRect.offsetMax = Vector2.zero;
            var solidImg = solidObj.GetComponent<Image>();
            solidImg.color = new Color(0.13f, 0.14f, 0.18f, 0.92f);
            solidImg.raycastTarget = false;

            GameObject bgObj = new GameObject("Bg", typeof(RectTransform), typeof(Image));
            bgObj.transform.SetParent(row.transform, false);
            var bgRect = bgObj.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = new Vector2(-36f, 0f);
            bgRect.offsetMax = Vector2.zero;
            var bgImg = bgObj.GetComponent<Image>();
            bgImg.color = new Color(0.16f, 0.17f, 0.21f, 0.9f);
            bgImg.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            bgImg.type = Image.Type.Sliced;
            bgImg.raycastTarget = false;

            return row.transform;
        }

        private Text CreateCardTitle(Transform card, string text, string colorHex)
        {
            Text t = CreateText(card, $"<color={colorHex}>{text}</color>", 10);
            t.alignment = TextAnchor.MiddleLeft;
            LayoutLeft(t.GetComponent<RectTransform>(), 10, 4, InnerWidth - 20, 14f);
            t.raycastTarget = false;
            return t;
        }

        /// <summary>配置行：名称 + 描述（左）+ 开关（右）</summary>
        private void CreateSwitchRow(Transform card, float y, string name, string desc, Action onClick, int index)
        {
            Text nameText = CreateText(card, name, 10);
            nameText.alignment = TextAnchor.MiddleLeft;
            var nr = nameText.GetComponent<RectTransform>();
            nr.anchorMin = new Vector2(0, 1);
            nr.anchorMax = new Vector2(0, 1);
            nr.pivot = new Vector2(0, 1);
            nr.anchoredPosition = new Vector2(10, y);
            nr.sizeDelta = new Vector2(190f, 16f);
            nameText.raycastTarget = false;

            Text descText = CreateText(card, $"<color=#6a6458>{desc}</color>", 8);
            descText.alignment = TextAnchor.MiddleLeft;
            var dr = descText.GetComponent<RectTransform>();
            dr.anchorMin = new Vector2(0, 1);
            dr.anchorMax = new Vector2(0, 1);
            dr.pivot = new Vector2(0, 1);
            dr.anchoredPosition = new Vector2(10, y - 11);
            dr.sizeDelta = new Vector2(180f, 11f);
            descText.raycastTarget = false;

            // 开关（右侧 40×18）
            GameObject swObj = new GameObject("Switch", typeof(RectTransform), typeof(Image), typeof(Button));
            swObj.transform.SetParent(card, false);
            var swRect = swObj.GetComponent<RectTransform>();
            swRect.anchorMin = new Vector2(1, 1);
            swRect.anchorMax = new Vector2(1, 1);
            swRect.pivot = new Vector2(1, 1);
            swRect.anchoredPosition = new Vector2(-10, y);
            swRect.sizeDelta = new Vector2(40f, 18f);
            var swImg = swObj.GetComponent<Image>();
            swImg.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            swImg.type = Image.Type.Sliced;
            var swBtn = swObj.GetComponent<Button>();
            swBtn.onClick.AddListener(() => { if (onClick != null) onClick(); });

            Text swText = CreateText(swObj.transform, "", 9);
            swText.alignment = TextAnchor.MiddleCenter;
            Stretch(swText.GetComponent<RectTransform>());
            swText.raycastTarget = false;

            // 存引用（按 index 对齐）
            while (_switchImgs.Count <= index) { _switchImgs.Add(null); _switchTexts.Add(null); }
            _switchImgs[index] = swImg;
            _switchTexts[index] = swText;
        }

        /// <summary>配置行：名称 + 描述（左）+ −/数值/+（右）</summary>
        private void CreateNumRow(Transform card, float y, string name, string desc, int initial, int min, int max, Func<int> getter, Action<int> setter, int index)
        {
            Text nameText = CreateText(card, name, 10);
            nameText.alignment = TextAnchor.MiddleLeft;
            var nr = nameText.GetComponent<RectTransform>();
            nr.anchorMin = new Vector2(0, 1);
            nr.anchorMax = new Vector2(0, 1);
            nr.pivot = new Vector2(0, 1);
            nr.anchoredPosition = new Vector2(10, y);
            nr.sizeDelta = new Vector2(190f, 16f);
            nameText.raycastTarget = false;

            Text descText = CreateText(card, $"<color=#6a6458>{desc}</color>", 8);
            descText.alignment = TextAnchor.MiddleLeft;
            var dr = descText.GetComponent<RectTransform>();
            dr.anchorMin = new Vector2(0, 1);
            dr.anchorMax = new Vector2(0, 1);
            dr.pivot = new Vector2(0, 1);
            dr.anchoredPosition = new Vector2(10, y - 11);
            dr.sizeDelta = new Vector2(180f, 11f);
            descText.raycastTarget = false;

            // − 按钮（右 -100）
            GameObject minusObj = CreateSmallButton(card, "−", new Vector2(-100, y));
            minusObj.GetComponent<Button>().onClick.AddListener(() =>
            {
                int v = Mathf.Clamp(getter() - 1, min, max);
                setter(v);
                ZhuluConfig.Instance.Save();
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
                int v = Mathf.Clamp(getter() + 1, min, max);
                setter(v);
                ZhuluConfig.Instance.Save();
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
            t.color = new Color(0.85f, 0.87f, 0.92f);
            Stretch(t.GetComponent<RectTransform>());
            t.raycastTarget = false;
            return btnObj;
        }

        // ═══════════════════════ 四张卡片 ═══════════════════════

        private void CreateSwitchCard()
        {
            // 14 行开关（2026-10-13 新增「禅让机制」；卡片位于滚动容器内）
            Transform card = CreateCard(360f);
            CreateCardTitle(card, "⚙ 功能开关", "#05fca6");
            CreateSwitchRow(card, -22, "调试日志", "输出运行日志到 Player.log",
                () => { ZhuluConfig.Instance.DebugLogging = !ZhuluConfig.Instance.DebugLogging; ZhuluContext.Instance.DebugLogging = ZhuluConfig.Instance.DebugLogging; ZhuluConfig.Instance.Save(); RefreshSwitch(0); }, 0);
            CreateSwitchRow(card, -46, "AI 自动施政", "AI 王国每年自动颁布/废除政策",
                () => { ZhuluConfig.Instance.AiAutoPolicy = !ZhuluConfig.Instance.AiAutoPolicy; ZhuluConfig.Instance.Save(); RefreshSwitch(1); }, 1);
            CreateSwitchRow(card, -70, "三级选拔", "东方科举 / 游牧选贤共用轮次和数值",
                () => { ZhuluConfig.Instance.KejuEnabled = !ZhuluConfig.Instance.KejuEnabled; ZhuluConfig.Instance.Save(); RefreshSwitch(2); }, 2);
            CreateSwitchRow(card, -94, "城主官职化", "城主补位 / 撤职 / 退休 / 权势",
                () => { ZhuluConfig.Instance.CityLeaderEnabled = !ZhuluConfig.Instance.CityLeaderEnabled; ZhuluConfig.Instance.Save(); RefreshSwitch(3); }, 3);
            CreateSwitchRow(card, -118, "邦交体系", "和约停战 / 互不侵犯 / 朝贡",
                () => { ZhuluConfig.Instance.DiplomacyEnabled = !ZhuluConfig.Instance.DiplomacyEnabled; ZhuluConfig.Instance.Save(); RefreshSwitch(4); }, 4);
            CreateSwitchRow(card, -142, "分崩离析拦截", "拦截原版分裂 plot（文化/宗教/语言）",
                () => { ZhuluModConfig.SetSplitEventsEnabled(!ZhuluModConfig.SplitEventsEnabled); RefreshSwitch(5); }, 5);
            CreateSwitchRow(card, -166, "爵位升降", "国力五等 · 升降确认（10 年+缓冲）",
                () => { ZhuluConfig.Instance.TierSystemEnabled = !ZhuluConfig.Instance.TierSystemEnabled; ZhuluConfig.Instance.Save(); RefreshSwitch(6); }, 6);
            CreateSwitchRow(card, -190, "礼治/汗威", "东方显示礼治，游牧显示汗威（数值来源相同）",
                () => { ZhuluConfig.Instance.LiZhiEnabled = !ZhuluConfig.Instance.LiZhiEnabled; ZhuluConfig.Instance.Save(); RefreshSwitch(7); }, 7);
            CreateSwitchRow(card, -214, "党争体系", "固定四党 / 朝议 / 亲信 / 世仇 / 政变",
                () => { ZhuluConfig.Instance.FactionPoliticsEnabled = !ZhuluConfig.Instance.FactionPoliticsEnabled; ZhuluConfig.Instance.Save(); RefreshSwitch(8); }, 8);
            CreateSwitchRow(card, -238, "地基系统", "身份特质 / 个人史册 / 派系骨架",
                () => { ZhuluConfig.Instance.GroundworkEnabled = !ZhuluConfig.Instance.GroundworkEnabled; ZhuluConfig.Instance.Save(); RefreshSwitch(9); }, 9);
            CreateSwitchRow(card, -262, "严格存档", "快照收集失败阻止存档（防残缺档）",
                () => { ZhuluConfig.Instance.StrictSave = !ZhuluConfig.Instance.StrictSave; ZhuluConfig.Instance.Save(); RefreshSwitch(10); }, 10);
            CreateSwitchRow(card, -286, "地图等级", "国家铭牌显示东方爵位或游牧等级；天命国/叛军不显示",
                () => { ZhuluConfig.Instance.MapTierLabelsEnabled = !ZhuluConfig.Instance.MapTierLabelsEnabled; ZhuluConfig.Instance.Save(); RefreshSwitch(11); }, 11);
            CreateSwitchRow(card, -310, "爵位配色", "地图括号按等阶着色（不支持时自动纯文字）",
                () => { ZhuluConfig.Instance.MapTierColorsEnabled = !ZhuluConfig.Instance.MapTierColorsEnabled; ZhuluConfig.Instance.Save(); RefreshSwitch(12); }, 12);
            // 2026-10-13 新增：禅让机制开关（关闭后君主不再主动禅位，只能病故继承或篡位更替）
            CreateSwitchRow(card, -334, "禅让机制", "在位满 30 年的君主每年 10% 概率主动禅位",
                () => { ZhuluConfig.Instance.AbdicationEnabled = !ZhuluConfig.Instance.AbdicationEnabled; ZhuluConfig.Instance.Save(); RefreshSwitch(13); }, 13);
        }

        private void CreateNumberCard()
        {
            // 阶段2L：模拟节奏（3 行——含科举轮次）
            Transform card = CreateCard(94f);
            CreateCardTitle(card, "📅 模拟节奏", ZhuluUIStyle.Hex.Gold);
            CreateNumRow(card, -22, "选拔轮次间隔", "科举与游牧选贤周期（5 / 10 年）",
                ZhuluConfig.Instance.KejuIntervalYears, 5, 10,
                () => ZhuluConfig.Instance.KejuIntervalYears, v => ZhuluConfig.Instance.KejuIntervalYears = v, 0);
            CreateNumRow(card, -46, "天命结算间隔", "两套天命每 N 年各自核算（5~20）",
                ZhuluConfig.Instance.MandateTickInterval, ZhuluConfig.MinTickInterval, ZhuluConfig.MaxTickInterval,
                () => ZhuluConfig.Instance.MandateTickInterval, v => ZhuluConfig.Instance.MandateTickInterval = v, 1);
            CreateNumRow(card, -70, "易主预警阈值", "天命低于此值显示易主预警（10~60）",
                ZhuluConfig.Instance.MandateWarnThreshold, ZhuluConfig.MinWarnThreshold, ZhuluConfig.MaxWarnThreshold,
                () => ZhuluConfig.Instance.MandateWarnThreshold, v => ZhuluConfig.Instance.MandateWarnThreshold = v, 2);
        }

        private void CreateRollbackCard()
        {
            Transform card = CreateCard(72f);
            CreateCardTitle(card, "🛡 安全数值回滚", "#f73939");

            // 按钮（全宽 22 高）
            GameObject btnObj = new GameObject("RollbackBtn", typeof(RectTransform), typeof(Image), typeof(Button));
            btnObj.transform.SetParent(card, false);
            var r = btnObj.GetComponent<RectTransform>();
            r.anchorMin = new Vector2(0, 1);
            r.anchorMax = new Vector2(0, 1);
            r.pivot = new Vector2(0, 1);
            r.anchoredPosition = new Vector2(10, -24);
            r.sizeDelta = new Vector2(InnerWidth - 20, 22f);
            var img = btnObj.GetComponent<Image>();
            img.color = new Color(0.36f, 0.12f, 0.12f, 1f);
            img.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            img.type = Image.Type.Sliced;
            btnObj.GetComponent<Button>().onClick.AddListener(TriggerSoftRollback);
            Text t = CreateText(btnObj.transform, "<b>一键安全软回滚</b>", 10);
            t.alignment = TextAnchor.MiddleCenter;
            t.color = new Color(1f, 0.75f, 0.7f);
            Stretch(t.GetComponent<RectTransform>());
            t.raycastTarget = false;

            _rollbackStatText = CreateText(card, "<color=#70d468>✔ 管线挂载正常</color>", 9);
            _rollbackStatText.alignment = TextAnchor.MiddleCenter;
            var sr = _rollbackStatText.GetComponent<RectTransform>();
            sr.anchorMin = new Vector2(0, 1);
            sr.anchorMax = new Vector2(0, 1);
            sr.pivot = new Vector2(0.5f, 1);
            sr.anchoredPosition = new Vector2(InnerWidth / 2f, -52);
            sr.sizeDelta = new Vector2(InnerWidth - 20, 14f);
            _rollbackStatText.raycastTarget = false;
        }

        private void CreateInfoCard()
        {
            Transform card = CreateCard(74f);
            CreateCardTitle(card, "📜 存档机制", ZhuluUIStyle.Hex.Gold);

            Text infoText = CreateText(card,
                "<color=#b8b2a8>官职 / 国策 / 天命 / 史册均写入 <color=#70d468>custom_data（zlr_ 前缀）</color>，随游戏存档自动保存与恢复。</color>", 9);
            infoText.alignment = TextAnchor.UpperLeft;
            infoText.horizontalOverflow = HorizontalWrapMode.Wrap;
            infoText.verticalOverflow = VerticalWrapMode.Overflow;
            var ir = infoText.GetComponent<RectTransform>();
            ir.anchorMin = new Vector2(0, 1);
            ir.anchorMax = new Vector2(0, 1);
            ir.pivot = new Vector2(0, 1);
            ir.anchoredPosition = new Vector2(10, -22);
            ir.sizeDelta = new Vector2(InnerWidth - 20, 30f);
            infoText.raycastTarget = false;

            Text verText = CreateText(card,
                "<color=#8a8578>逐鹿 <color=#fbc444>v0.5.x</color> · WorldBox <color=#ffd8d9>v0.51.2</color> · 与旧版存档隔离</color>", 9);
            verText.alignment = TextAnchor.MiddleLeft;
            var vr = verText.GetComponent<RectTransform>();
            vr.anchorMin = new Vector2(0, 1);
            vr.anchorMax = new Vector2(0, 1);
            vr.pivot = new Vector2(0, 1);
            vr.anchoredPosition = new Vector2(10, -58);
            vr.sizeDelta = new Vector2(InnerWidth - 20, 14f);
            verText.raycastTarget = false;
        }

        // ═══════════════════════ 刷新 ═══════════════════════

        private void RefreshAll()
        {
            // 13 开关全刷 + 数值 3 行（科举轮次/天命间隔/预警阈值）
            for (int i = 0; i < 13; i++) RefreshSwitch(i);
            if (_numTexts.Count > 0 && _numTexts[0] != null) _numTexts[0].text = ZhuluConfig.Instance.KejuIntervalYears.ToString();
            if (_numTexts.Count > 1 && _numTexts[1] != null) _numTexts[1].text = ZhuluConfig.Instance.MandateTickInterval.ToString();
            if (_numTexts.Count > 2 && _numTexts[2] != null) _numTexts[2].text = ZhuluConfig.Instance.MandateWarnThreshold.ToString();
        }

        private void RefreshSwitch(int index)
        {
            if (_switchImgs.Count <= index || _switchImgs[index] == null || _switchTexts[index] == null) return;
            bool on;
            switch (index)
            {
                case 0: on = ZhuluConfig.Instance.DebugLogging; break;
                case 1: on = ZhuluConfig.Instance.AiAutoPolicy; break;
                case 2: on = ZhuluConfig.Instance.KejuEnabled; break;
                case 3: on = ZhuluConfig.Instance.CityLeaderEnabled; break;
                case 4: on = ZhuluConfig.Instance.DiplomacyEnabled; break;
                case 5: on = ZhuluModConfig.SplitEventsEnabled; break;
                case 6: on = ZhuluConfig.Instance.TierSystemEnabled; break;
                case 7: on = ZhuluConfig.Instance.LiZhiEnabled; break;
                case 8: on = ZhuluConfig.Instance.FactionPoliticsEnabled; break;
                case 9: on = ZhuluConfig.Instance.GroundworkEnabled; break;
                case 10: on = ZhuluConfig.Instance.StrictSave; break;
                case 11: on = ZhuluConfig.Instance.MapTierLabelsEnabled; break;
                case 12: on = ZhuluConfig.Instance.MapTierColorsEnabled; break;   // 2026-10-13：显式化（原走 default）
                case 13: on = ZhuluConfig.Instance.AbdicationEnabled; break;      // 2026-10-13：禅让机制
                default: on = false; break;                                      // 越界兜底（不应到达）
            }
            _switchImgs[index].color = on
                ? new Color(0.20f, 0.17f, 0.09f, 0.95f)   // 开：金底
                : new Color(0.14f, 0.14f, 0.17f, 0.9f);   // 关：灰底
            _switchTexts[index].text = on ? "<b>开</b>" : "关";
            _switchTexts[index].color = on ? new Color(1f, 0.84f, 0f) : new Color(0.6f, 0.58f, 0.52f);
        }

        public void TriggerSoftRollback()
        {
            ZhuluContext.Instance.LogInfo("[ZhuluSettingsWindow] 触发安全软回滚，撤销所有逐鹿数值修正（重新读档后自动恢复）");
            ModifierPipeline.Instance.ClearAll();
            if (_rollbackStatText != null)
                _rollbackStatText.text = "<color=#f73939>⚠ 已触发，重新读档后自动恢复</color>";
        }
    }
}
