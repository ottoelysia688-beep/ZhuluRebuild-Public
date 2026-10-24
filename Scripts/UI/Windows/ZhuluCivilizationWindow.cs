using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using ZhuluRebuild.Core;
using ZhuluRebuild.Domain;

namespace ZhuluRebuild.UI.Windows
{
    /// <summary>为人类、兽人以外的现存智慧种族设置“未来新国家”采用的体制。</summary>
    /// 批次8迁移（2026-08-23）：骨架收敛至 ZhuluScrollWindowBase——删除手写 Build()约65行
    /// （隐藏tabs/牌匾/滚动区/VLG 全部由基类提供），本类只保留说明行+种族行构建与刷新。
    public sealed class ZhuluCivilizationWindow : ZhuluScrollWindowBase
    {
        public static ZhuluCivilizationWindow Instance { get; private set; }
        private readonly List<GameObject> _rows = new List<GameObject>();

        protected override string WindowTitle => "<b>种族体制</b>";
        protected override float WindowWidth => 340f;
        protected override float WindowHeight => 400f;

        public void Initialize()
        {
            Instance = this;
            ZhuluContext.Instance.LogInfo("[CivilizationWindow] 初始化完成");
        }

        private void OnEnable()
        {
            if (World.world == null) return;
            EnsureBuilt();
            RefreshRows();
        }

        public void Show()
        {
            if (World.world == null) return;
            EnsureBuilt();
            RefreshRows();
        }

        /// <summary>说明行 + 各种族体制行（VLG 流式）。</summary>
        protected override void BuildContent()
        {
            Text note = CreateText(ContentRoot,
                "<color=#8a8072>修改只影响该种族以后新建、叛乱转正或异体系篡位的国家；现有国家不变。</color>", 9);
            note.alignment = TextAnchor.MiddleCenter;
            LayoutLeft(note.rectTransform, 0f, 0f, InnerWidth, 34f);
            _rows.Add(note.gameObject);

            RefreshRows();
        }

        private void RefreshRows()
        {
            if (ContentRoot == null) return;
            foreach (GameObject row in _rows)
            {
                if (row == null) continue;
                if (row.transform.parent == ContentRoot) Destroy(row); // 说明行等常驻元素不重复删
            }
            _rows.Clear();

            var species = new HashSet<string> { "human", "orc" };
            try
            {
                if (World.world?.units?.getSimpleList() != null)
                    foreach (Actor actor in World.world.units.getSimpleList())
                    {
                        if (actor == null || !actor.isAlive() || !actor.isSapient()) continue;
                        string id = KingdomCivilizationService.GetActorSpeciesId(actor);
                        if (!string.IsNullOrEmpty(id)) species.Add(id);
                    }
            }
            catch
            {
                // 0.51.x 若单位容器接口变化，至少仍显示人类与兽人固定项。
            }

            var ordered = new List<string>(species);
            ordered.Sort(StringComparer.Ordinal);
            ordered.Sort((a, b) => SpeciesOrder(a).CompareTo(SpeciesOrder(b)));
            foreach (string id in ordered) AddSpeciesRow(id);
        }

        private void AddSpeciesRow(string speciesId)
        {
            bool locked = speciesId == "human" || speciesId == "orc";
            CivilizationType type = KingdomCivilizationService.Instance.GetSpeciesDefault(speciesId);

            GameObject row = new GameObject("Species_" + speciesId, typeof(RectTransform), typeof(Image));
            row.transform.SetParent(ContentRoot, false);
            row.GetComponent<RectTransform>().sizeDelta = new Vector2(InnerWidth, 52f);
            Image bg = row.GetComponent<Image>();
            bg.color = new Color(0.13f, 0.12f, 0.1f, 0.90f);
            bg.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            bg.type = Image.Type.Sliced;
            _rows.Add(row);

            Text name = CreateText(row.transform, $"<b>{SpeciesName(speciesId)}</b>\n<color=#81796c>{speciesId}</color>", 10);
            name.alignment = TextAnchor.MiddleLeft;
            Place(name.rectTransform, 10f, 4f, 170f, 44f);

            GameObject buttonObject = new GameObject("TypeButton", typeof(RectTransform), typeof(Image), typeof(Button));
            buttonObject.transform.SetParent(row.transform, false);
            Place(buttonObject.GetComponent<RectTransform>(), 196f, 11f, 108f, 30f);
            Image bi = buttonObject.GetComponent<Image>();
            bi.color = type == CivilizationType.Nomadic ? new Color(0.38f, 0.25f, 0.12f, 1f) : new Color(0.18f, 0.28f, 0.38f, 1f);
            bi.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement"); bi.type = Image.Type.Sliced;
            Text label = CreateText(buttonObject.transform,
                (type == CivilizationType.Nomadic ? "游牧文化" : "东方文化") + (locked ? " · 固定" : ""), 9);
            label.alignment = TextAnchor.MiddleCenter; Stretch(label.rectTransform);
            Button button = buttonObject.GetComponent<Button>();
            button.interactable = !locked;
            if (!locked)
            {
                string captured = speciesId;
                button.onClick.AddListener(() =>
                {
                    CivilizationType current = KingdomCivilizationService.Instance.GetSpeciesDefault(captured);
                    KingdomCivilizationService.Instance.SetSpeciesDefaultForFuture(captured,
                        current == CivilizationType.Eastern ? CivilizationType.Nomadic : CivilizationType.Eastern);
                    RefreshRows();
                });
            }
        }

        private static int SpeciesOrder(string id) => id == "human" ? 0 : id == "orc" ? 1 : 2;
        private static string SpeciesName(string id)
        {
            switch (id)
            {
                case "human": return "人类";
                case "orc": return "兽人";
                case "elf": return "精灵";
                case "dwarf": return "矮人";
                case "fish": case "fishman": return "鱼人";
                case "skeleton": return "骷髅";
                case "werewolf": return "狼人";
                // ── 全文明种族版常见种族（阶段3H-5：窗口出现即有中文名，不再裸英文）──
                case "civ_acid_gentleman": return "酸绅士";
                case "civ_alien": return "外星客";
                case "civ_alpaca": return "羊驼族";
                case "civ_angle": return "天使族";
                case "civ_armadillo": return "犰狳族";
                case "civ_bandit": return "匪帮";
                case "civ_bear": return "熊族";
                case "civ_bee": return "蜂族";
                case "civ_beetle": return "甲虫族";
                case "civ_buffalo": return "野牛族";
                case "civ_candy_man": return "糖人";
                case "civ_capybara": return "水豚族";
                case "civ_cat": return "猫族";
                case "civ_chicken": return "鸡族";
                case "civ_cold_one": return "冰冷者";
                case "civ_cow": return "牛族";
                case "civ_crab": return "蟹族";
                case "civ_crocodile": return "鳄族";
                case "civ_crystal_golem": return "晶石魔像";
                case "civ_demon": return "恶魔族";
                case "civ_dog": return "犬族";
                case "civ_druid": return "德鲁伊";
                case "civ_evil_mage": return "邪法师";
                case "civ_fox": return "狐族";
                case "civ_frog": return "蛙族";
                case "civ_garlic_man": return "蒜人";
                case "civ_goat": return "山羊族";
                case "civ_greg": return "葛瑞格";
                case "civ_hyena": return "鬣狗族";
                case "civ_lemon_man": return "柠檬人";
                case "civ_liliar": return "莉莉尔族";
                case "civ_monkey": return "猴族";
                case "civ_necromancer": return "亡灵师";
                case "civ_penguin": return "企鹅族";
                case "civ_piranha": return "食人鱼族";
                case "civ_rabbit": return "兔族";
                case "civ_rat": return "鼠族";
                case "civ_rhino": return "犀族";
                case "civ_scorpion": return "蝎族";
                case "civ_sheep": return "羊族";
                case "civ_snake": return "蛇族";
                case "civ_snowman": return "雪人族";
                case "civ_turtle": return "龟族";
                case "civ_white_mage": return "白袍师";
                case "civ_wolf": return "狼族";
                default: return id.StartsWith("civ_") ? id.Substring(4) : id;
            }
        }

        private static void Place(RectTransform r, float x, float y, float w, float h)
        {
            r.anchorMin = r.anchorMax = new Vector2(0f, 1f); r.pivot = new Vector2(0f, 1f);
            r.anchoredPosition = new Vector2(x, -y); r.sizeDelta = new Vector2(w, h);
        }
    }
}
