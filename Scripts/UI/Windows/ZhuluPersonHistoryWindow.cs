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
    /// 个人史册窗口（阶段1·科举/地基 UI）。
    /// 入口：单位信息窗口（UnitWindow）右侧「个人史册」按钮。
    /// 内容：身份档案（姓名/身份徽章/出身/生卒）+ 逐鹿专属六类时间线（📜功名/🪶仕途/⚔️朝争/👑君临/🎀恩遇/🏮家族——阶段2S-10）。
    /// 批次8迁移（2026-08-23）：骨架收敛至 ZhuluScrollWindowBase（ContentTopBand=78 容纳 副标题14+分类栏20）；
    /// 删除手写 RebuildUI 约140行与本地 返回钮/文本 工具；动态牌匾题静态化（人名由人物卡承载）。
    /// 经验保留：Initialize 不构建（Canvas 未就绪偏左）；首次 OnEnable 构建。
    /// </summary>
    public class ZhuluPersonHistoryWindow : ZhuluScrollWindowBase
    {
        public static ZhuluPersonHistoryWindow Instance { get; private set; }

        private Text _subStatText;
        private Text _emptyText;
        private Transform _catBar;

        public long CurrentActorId { get; private set; } = -1;

        // 分类：0全部 1功名 2仕途 3朝争 4君临 5恩遇 6家族（阶段2S-10 逐鹿专属六类）
        private static readonly string[] CatNames = { "全部", "📜功名", "🪶仕途", "⚔️朝争", "👑君临", "🎀恩遇", "🏮家族" };
        private static readonly string[] CatKeys = { "", PersonHistoryManager.CAT_GONGMING, PersonHistoryManager.CAT_SHITU, PersonHistoryManager.CAT_CHAOZHENG, PersonHistoryManager.CAT_JUNLIN, PersonHistoryManager.CAT_ENYU, PersonHistoryManager.CAT_JIAZU };
        private static readonly string[] CatColors = { "", "#FFD700", "#70d468", "#f73939", "#f0c060", "#ffd8d9", "#6ca3fd" };
        private int _currentCategory;
        private readonly List<GameObject> _catButtons = new List<GameObject>();
        private readonly List<Text> _catTexts = new List<Text>();

        protected override string WindowTitle => "<b>个人史册</b>";
        protected override float WindowWidth => 350f;
        protected override float WindowHeight => 400f;
        protected override float PlaqueWidth => 250f;
        protected override float ContentTopBand => 78f;   // 牌匾26 + 副标题14 + 分类20 + 间距

        public void Initialize()
        {
            Instance = this;
            ZhuluContext.Instance.LogInfo("[ZhuluPersonHistoryWindow] 初始化完成（首次打开时构建，防偏左）");
        }

        private void OnEnable()
        {
            if (World.world == null) return;
            EnsureBuilt();
            RefreshContent();
        }

        /// <summary>打开指定人物的个人史册。</summary>
        public void Show(Actor actor)
        {
            if (actor == null) return;
            CurrentActorId = actor.getID();
            if (gameObject.activeInHierarchy && ContentRoot != null)
            {
                RefreshContent();
            }
        }

        // ═══════════════════════ 结构（骨架由基类提供） ═══════════════════════

        protected override void BuildContent()
        {
            // 固定头部：副标题 + 分类栏（挂 Bg 绝对定位，不随滚动；x=-8 与原版避让位一致）
            _subStatText = CreateText(Bg, "", 9);
            _subStatText.alignment = TextAnchor.MiddleCenter;
            _subStatText.raycastTarget = false;
            var subRect = _subStatText.GetComponent<RectTransform>();
            subRect.anchorMin = subRect.anchorMax = new Vector2(0.5f, 1);
            subRect.pivot = new Vector2(0.5f, 1);
            subRect.anchoredPosition = new Vector2(-8, -38);
            subRect.sizeDelta = new Vector2(WindowWidth - 16f, 14f);

            CreateCategoryBar();

            // 空态文本（常驻内容流，不参与清理）
            _emptyText = CreateText(ContentRoot, "<color=#6a6458>尚无人生记录。</color>", 10);
            _emptyText.alignment = TextAnchor.MiddleCenter;
            _emptyText.GetComponent<RectTransform>().sizeDelta = new Vector2(InnerWidth, 60f);
            _emptyText.gameObject.SetActive(false);

            RefreshContent();
        }

        private void CreateCategoryBar()
        {
            GameObject barObj = new GameObject("CatBar", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            barObj.transform.SetParent(Bg, false);
            var barRect = barObj.GetComponent<RectTransform>();
            barRect.anchorMin = new Vector2(0.5f, 1);
            barRect.anchorMax = new Vector2(0.5f, 1);
            barRect.pivot = new Vector2(0.5f, 1);
            barRect.anchoredPosition = new Vector2(-8, -54);
            barRect.sizeDelta = new Vector2(WindowWidth - 16f, 20f);
            _catBar = barObj.transform;

            var hg = barObj.GetComponent<HorizontalLayoutGroup>();
            hg.spacing = 3f;
            hg.childAlignment = TextAnchor.MiddleCenter;
            hg.childControlWidth = true;
            hg.childControlHeight = true;
            hg.childForceExpandWidth = true;
            hg.childForceExpandHeight = true;

            _catButtons.Clear();
            _catTexts.Clear();
            for (int i = 0; i < CatNames.Length; i++)
            {
                int idx = i;
                GameObject btnObj = new GameObject("CatBtn" + i, typeof(RectTransform), typeof(Image), typeof(Button));
                btnObj.transform.SetParent(barObj.transform, false);
                var img = btnObj.GetComponent<Image>();
                img.color = new Color(0.14f, 0.14f, 0.17f, 0.9f);
                img.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
                img.type = Image.Type.Sliced;
                var btn = btnObj.GetComponent<Button>();
                btn.onClick.AddListener(() =>
                {
                    _currentCategory = idx;
                    RefreshContent();
                });
                Text t = CreateText(btnObj.transform, CatNames[i], 8);
                t.alignment = TextAnchor.MiddleCenter;
                Stretch(t.GetComponent<RectTransform>());
                t.raycastTarget = false;
                _catButtons.Add(btnObj);
                _catTexts.Add(t);
            }
        }

        // ═══════════════════════ 内容刷新 ═══════════════════════

        private void RefreshContent()
        {
            if (ContentRoot == null || _subStatText == null) return;

            // 清除旧条目（保留空态文本）
            for (int i = ContentRoot.childCount - 1; i >= 0; i--)
            {
                var child = ContentRoot.GetChild(i);
                if (_emptyText != null && child == _emptyText.transform) continue;
                Destroy(child.gameObject);
            }

            if (CurrentActorId < 0)
            {
                _emptyText.gameObject.SetActive(true);
                _emptyText.text = "<color=#6a6458>请选择一位人物。</color>";
                return;
            }

            var record = PersonHistoryManager.Instance.GetRecord(CurrentActorId);
            var info = IdentityClassifier.Instance.GetInfo(CurrentActorId);
            Actor actor = null;
            try { actor = World.world.units.get(CurrentActorId); } catch { }

            string name = actor != null ? (actor.getName() ?? "无名氏") : "无名氏";
            string identity = IdentityClassifier.GetIdentityName(info != null ? info.Type : IdentityClassifier.IdentityType.Commoner);
            string origin = info != null && !string.IsNullOrEmpty(info.Origin) ? info.Origin : "平民";

            // 身份徽章颜色（君主金/宗藩紫/外戚橙/武将红/文官蓝/平民灰）
            string idColor = identity switch
            {
                "君主" => "#FFD700",
                "宗藩" => "#c9a0ff",
                "外戚" => "#e0554a",
                "武将" => "#f78d8d",
                "文官" => "#6ca3fd",
                _ => "#a0a0a0"
            };

            // 生卒
            string life = "";
            if (record != null)
            {
                if (record.DeathYear >= 0) life = $"<color=#ffd8d9>{record.BirthYear}</color> — <color={ZhuluUIStyle.Hex.Negative}>{record.DeathYear} 卒</color>";
                else life = $"<color=#ffd8d9>{record.BirthYear}</color> — 今";
            }

            string originMark = origin == "世家" ? "<color=#05fca6>世家</color>" : "<color=#a0a0a0>寒门</color>";
            _subStatText.text = $"<b>{name}</b> · <color={idColor}>{identity}</color> · {originMark} {life}";

            // 2026-08-20 人物卡：头像（活人=原版头像控件；死者=灰度墓碑）+ 姓名/身份/生卒
            CreateProfileCard(actor, record, name, $"<color={idColor}>{identity}</color> · {originMark}", life);

            List<PersonHistoryManager.HistoryEntry> entries = record != null && record.Entries != null ? record.Entries : new List<PersonHistoryManager.HistoryEntry>();
            List<PersonHistoryManager.HistoryEntry> shown;
            if (_currentCategory == 0)
            {
                shown = entries;
            }
            else
            {
                shown = new List<PersonHistoryManager.HistoryEntry>();
                foreach (var e in entries)
                {
                    if (e != null && e.Category == CatKeys[_currentCategory]) shown.Add(e);
                }
            }
            // 阶段2S-6：稳定排序（LINQ OrderByDescending 稳定——同年内保记录先后序；List.Sort 不稳定会乱）
            shown = shown.OrderByDescending(e => e.Year).ToList();

            // 分类计数
            int[] counts = new int[CatNames.Length];
            if (entries != null)
            {
                foreach (var e in entries)
                {
                    if (e == null) continue;
                    counts[0]++;
                    for (int i = 1; i < CatKeys.Length; i++)
                    {
                        if (e.Category == CatKeys[i]) { counts[i]++; break; }
                    }
                }
            }
            for (int i = 0; i < _catTexts.Count; i++)
            {
                if (_catTexts[i] != null) _catTexts[i].text = $"{CatNames[i]}·{counts[i]}";
            }
            for (int i = 0; i < _catButtons.Count; i++)
            {
                if (_catButtons[i] == null) continue;
                var img = _catButtons[i].GetComponent<Image>();
                if (img == null) continue;
                img.color = i == _currentCategory
                    ? new Color(0.20f, 0.17f, 0.09f, 0.95f)   // 选中：金色底
                    : new Color(0.14f, 0.14f, 0.17f, 0.9f);
            }

            if (shown.Count == 0)
            {
                _emptyText.gameObject.SetActive(true);
                // 阶段2S-7：空态分档——普通人（只记出生/死亡，条目区自然为空）与详记者（尚未有事件）
                _emptyText.text = _currentCategory == 0
                    ? (record != null && record.IsDetailed ? "<color=#6a6458>此生尚无著迹。</color>" : "<color=#6a6458>布衣一生，无事可记。</color>")
                    : "<color=#6a6458>此分类暂无记录。</color>";
                return;
            }
            _emptyText.gameObject.SetActive(false);

            // 时间线条目卡片（Row 容器：Bg 锚全屏纯色底 -36 + 文字同父级 x=8 Wrap 防溢出）
            foreach (var e in shown)
            {
                CreateEntryRow(e);
            }
        }

        /// <summary>人物卡（2026-08-20）：头像 + 姓名 + 身份·出身 + 生卒，置于时间线顶端。</summary>
        private void CreateProfileCard(Actor actor, PersonHistoryManager.PersonRecord record, string name, string idLine, string life)
        {
            try
            {
                GameObject card = new GameObject("ProfileCard", typeof(RectTransform), typeof(Image));
                card.transform.SetParent(ContentRoot, false);
                var cardRect = card.GetComponent<RectTransform>();
                cardRect.sizeDelta = new Vector2(InnerWidth, 52f);
                var cardImg = card.GetComponent<Image>();
                ZhuluUIStyle.SetSlicedSprite(cardImg, "ui/special/backgroundKingdomElement");
                cardImg.color = ZhuluUIStyle.InkCard;

                // 头像槽（左侧）
                var holder = new GameObject("AvatarSlot", typeof(RectTransform));
                holder.transform.SetParent(card.transform, false);
                var hRect = holder.GetComponent<RectTransform>();
                hRect.anchorMin = hRect.anchorMax = new Vector2(0f, 0.5f);
                hRect.pivot = new Vector2(0f, 0.5f);
                hRect.anchoredPosition = new Vector2(7f, 0f);
                hRect.sizeDelta = new Vector2(36f, 36f);

                bool isDead = record != null && record.DeathYear >= 0;
                GameObject avatar = null;
                if (actor != null && actor.isAlive())
                    avatar = ZhuluAvatar.CreateLiving(holder.transform, actor);
                if (avatar == null && isDead)
                    avatar = ZhuluAvatar.CreateDead(holder.transform);
                if (avatar == null && isDead)
                {
                    var fb = CreateText(holder.transform, "故", 10);
                    fb.alignment = TextAnchor.MiddleCenter;
                    fb.color = new Color(0.5f, 0.48f, 0.45f, 0.9f);
                    ZhuluUIStyle.LayoutLeft(fb.GetComponent<RectTransform>(), 0f, 0f, 36f, 36f);
                }

                // 右侧文字：姓名（金）+ 身份·出身 + 生卒
                var nameT = CreateText(card.transform, $"<b>{name}</b>", 10);
                nameT.alignment = TextAnchor.UpperLeft;
                nameT.color = new Color(1f, 0.84f, 0f);
                ZhuluUIStyle.LayoutLeft(nameT.GetComponent<RectTransform>(), 48f, -5f, InnerWidth - 54f, 15f);

                var infoT = CreateText(card.transform, $"{idLine}  {life}", 9);
                infoT.alignment = TextAnchor.UpperLeft;
                infoT.color = new Color(0.85f, 0.83f, 0.78f);
                ZhuluUIStyle.LayoutLeft(infoT.GetComponent<RectTransform>(), 48f, -22f, InnerWidth - 54f, 14f);
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[PersonHistoryWindow] 人物卡异常: {ex.Message}");
            }
        }

        private void CreateEntryRow(PersonHistoryManager.HistoryEntry e)
        {
            GameObject row = new GameObject("EntryRow", typeof(RectTransform));
            row.transform.SetParent(ContentRoot, false);
            var rowRect = row.GetComponent<RectTransform>();
            rowRect.sizeDelta = new Vector2(InnerWidth, 0f); // 高度由文字撑起

            // 背景（锚全屏填满，纯色底向左延长 -36 防 Sliced 贴图左装饰偏移）
            GameObject bg = new GameObject("Bg", typeof(RectTransform), typeof(Image));
            bg.transform.SetParent(row.transform, false);
            var bgRect = bg.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = new Vector2(-36f, 0f);
            bgRect.offsetMax = new Vector2(0f, 0f);
            var bgImg = bg.GetComponent<Image>();
            bgImg.color = new Color(0.14f, 0.14f, 0.17f, 0.9f);
            bgImg.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            bgImg.type = Image.Type.Sliced;

            // 分类色点（左侧竖条）
            string catColor = "#a0a0a0";
            for (int i = 1; i < CatKeys.Length; i++)
            {
                if (e.Category == CatKeys[i]) { catColor = CatColors[i]; break; }
            }
            GameObject dot = new GameObject("CatDot", typeof(RectTransform), typeof(Image));
            dot.transform.SetParent(row.transform, false);
            var dotRect = dot.GetComponent<RectTransform>();
            dotRect.anchorMin = new Vector2(0f, 0.5f);
            dotRect.anchorMax = new Vector2(0f, 0.5f);
            dotRect.pivot = new Vector2(0.5f, 0.5f);
            dotRect.anchoredPosition = new Vector2(-30f, 0f);
            dotRect.sizeDelta = new Vector2(4f, 20f);
            var dotImg = dot.GetComponent<Image>();
            ColorUtility.TryParseHtmlString(catColor, out var dotCol);
            dotImg.color = dotCol;

            // 文字（同父级，x=8，Wrap 防溢出，水平 overflow 关闭）
            Text t = CreateText(row.transform, "", 10);
            t.alignment = TextAnchor.UpperLeft;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.color = new Color(0.85f, 0.84f, 0.92f, 1f);
            t.raycastTarget = false;
            string emoji = "📜";
            for (int i = 1; i < CatKeys.Length; i++)
            {
                if (e.Category == CatKeys[i]) { emoji = CatNames[i].Substring(0, 2); break; }
            }
            t.text = $"<color=#ffd8d9>{e.Year}</color>  {emoji}  {e.Text}";
            var tRect = t.GetComponent<RectTransform>();
            tRect.anchorMin = new Vector2(0f, 0f);
            tRect.anchorMax = new Vector2(1f, 1f);
            tRect.pivot = new Vector2(0f, 1f);
            tRect.offsetMin = new Vector2(8f, 4f);
            tRect.offsetMax = new Vector2(-6f, -4f);

            // 行高：最小 26，文字高不足时撑到最小；宽文字自然撑高（ContentSizeFitter 父容器会算）
            float prefH = t.preferredHeight + 8f;
            rowRect.sizeDelta = new Vector2(InnerWidth, Mathf.Max(26f, prefH));
            LayoutRebuilder.ForceRebuildLayoutImmediate(rowRect);
        }
    }
}
