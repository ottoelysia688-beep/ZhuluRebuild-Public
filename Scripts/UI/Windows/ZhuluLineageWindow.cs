using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using ZhuluRebuild.Core;
using ZhuluRebuild.Domain;
using ZhuluRebuild.Presentation.Routing;

namespace ZhuluRebuild.UI.Windows
{
    /// <summary>
    /// 宗族谱牒总览窗（阶段3 F4：王国级家族入口）。
    /// 内容：王室大宗卡（传承世数/族长/家声/家风）+ 分支列表 + 家族榜单（按家声排序含分支）。
    /// 点击任一家族 → 打开家族深度页（ZhuluFamilyWindow）。
    /// 入口：王国窗口侧边栏「谱牒」按钮（RouteDestination.KingdomLineage）。
    /// 批次8迁移（2026-08-23）：骨架收敛至 ZhuluScrollWindowBase（ContentTopBand=52 容纳副状态行）；
    /// 删除手写 RebuildUI 约100行与本地 返回钮/布局/文本 工具；动态牌匾题静态化，国名并入副状态行。
    /// </summary>
    public class ZhuluLineageWindow : ZhuluScrollWindowBase
    {
        public static ZhuluLineageWindow Instance { get; private set; }

        private Text _subStatText;
        private readonly List<GameObject> _cards = new List<GameObject>();
        // 2026-08-19 串联·谱牒重设计：段折叠状态（段key→是否展开；默认展开）
        private readonly Dictionary<string, bool> _sectionState = new Dictionary<string, bool>();

        protected override string WindowTitle => "<b>宗族谱牒</b>";
        protected override float WindowWidth => 340f;
        protected override float WindowHeight => 420f;
        protected override float PlaqueWidth => 240f;
        protected override float ContentTopBand => 52f;   // 牌匾26 + 副状态14 + 间距

        public void Initialize()
        {
            Instance = this;
            ZhuluContext.Instance.LogInfo("[ZhuluLineageWindow] 初始化完成");
        }

        private void OnEnable()
        {
            if (World.world == null) return;
            EnsureBuilt();
            Refresh();
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

        // ═══════════════════════ 结构 ═══════════════════════

        protected override void BuildContent()
        {
            // 副状态行（固定头部：挂 Bg 绝对定位不随滚动，刷新只改文本）
            _subStatText = CreateText(Bg, "", 9);
            _subStatText.alignment = TextAnchor.MiddleCenter;
            var subRect = _subStatText.GetComponent<RectTransform>();
            subRect.anchorMin = subRect.anchorMax = new Vector2(0.5f, 1);
            subRect.pivot = new Vector2(0.5f, 1);
            subRect.anchoredPosition = new Vector2(0, -38);
            subRect.sizeDelta = new Vector2(InnerWidth, 14f);
            Refresh();
        }

        private void Refresh()
        {
            if (ContentRoot == null) return;
            try
            {
                ClearCards();
                string kId = CurrentKingdomId();
                Kingdom k = null;
                try { k = World.world.kingdoms.get(int.Parse(kId)); } catch { }

                if (string.IsNullOrEmpty(kId) || k == null || !k.isAlive())
                {
                    if (_subStatText != null) _subStatText.text = "";
                    AddHint("请从王国信息窗口 · 谱牒 进入");
                    return;
                }

                bool kNomad = false;
                try { var kk = ZhuluRebuild.Persistence.KingdomDataExt.FindKingdomById(kId); kNomad = kk != null && Domain.KingdomCivilizationService.Instance.IsNomadic(kk); } catch { }

                var kf = FamilyRegistry.Instance.GetKingdomFamilies(kId);
                if (kf == null)
                {
                    if (_subStatText != null) _subStatText.text = $"<b>{k.name}</b> · 建元 <color=#ffd8d9>{ZhuluTime.CurrentYear}</color> 年";
                    AddHint("谱牒未修——让时间推进一年后自动开修");
                    return;
                }

                int aliveMain = 0;
                foreach (var m in kf.Main.Members) { if (IsAliveNow(m)) aliveMain++; }
                string royalTxt = kNomad
                    ? $"汗裔 <color=#ffd700>{kf.Main.Surname}部</color> · 别帐"
                    : $"王室 <color=#ffd700>{kf.Main.Surname}氏</color> · 别支";
                if (_subStatText != null)
                    _subStatText.text = $"<b>{k.name}</b> · 建元 <color=#ffd8d9>{ZhuluTime.CurrentYear}</color> 年 · {royalTxt} {kf.Branches.Count} 家";

                // ── 王室大宗卡（2026-08-19 串联重设计：头像+两行紧凑制+门第徽章+家声条） ──
                var mainCard = CreateCard(46f);
                BuildMainCard(mainCard.transform, kf.Main, aliveMain);
                MakeClickable(mainCard, kId, "main");

                // ── 王族世系（折叠段头 ▼▶+条数；主家+分支 按家声排序） ──
                var royalList = new List<FamilyRegistry.FamilyData> { kf.Main };
                royalList.AddRange(kf.Branches);
                var sortedRoyal = royalList.OrderByDescending(f => f.Prestige).ToList();
                bool royalOpen = IsSectionOpen("royal");
                CreateSectionHeader(kNomad ? "汗帐世系" : "王族世系", sortedRoyal.Count, "royal");
                if (royalOpen)
                {
                    int rank = 1;
                    foreach (var f in sortedRoyal)
                    {
                        var captured = f;
                        string mainTxt = kNomad ? f.Surname + "部大宗" : f.Surname + "氏大宗";
                        string brTxt = f.BranchFounderName + (kNomad ? "分帐" : "支");
                        string lineName = f.IsMainLine ? mainTxt : brTxt;
                        int famAlive = 0;
                        foreach (var m in f.Members) { if (IsAliveNow(m)) famAlive++; }
                        CreateRankRow(CN(rank), lineName, f.Prestige, famAlive, -1, "", () => OpenFamily(kId, captured.FamilyId), kNomad);
                        rank++;
                    }
                }

                // ── 国中望族（折叠段头；非王族的原版 Clan——官宦/世家/大族） ──
                if (kf.Notables.Count > 0)
                {
                    bool notableOpen = IsSectionOpen("notable");
                    CreateSectionHeader(kNomad ? "诸部望族" : "国中望族", kf.Notables.Count, "notable");
                    if (notableOpen)
                    {
                        int nRank = 1;
                        foreach (var n in kf.Notables)
                        {
                            var captured = n;
                            string extra = captured.Officials > 0 ? $" · <color=#c8b98a>入仕{captured.Officials}</color>" : "";
                            string notableName = kNomad
                                ? ZhuluRebuild.Naming.ZhuluChineseNameService.Instance.NormalizeNomadFamilyName(captured.Name)
                                : captured.Name + "氏";
                            CreateRankRow(CN(nRank), notableName, captured.Prestige, captured.MemberCount,
                                captured.Officials, extra, () => OpenClan(captured.ClanId), kNomad);
                            nRank++;
                        }
                    }
                }

                var note = CreateText(ContentRoot, "<color=#6a6458>点击家族查看世系 · 望族大事纪可在原版家族窗口查看</color>", 8);
                note.alignment = TextAnchor.MiddleCenter;
                LayoutLeft(note.GetComponent<RectTransform>(), 0, 0, InnerWidth, 12);
                _cards.Add(note.gameObject);
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[LineageWindow] 刷新异常: {ex.Message}");
            }
            LayoutRebuilder.ForceRebuildLayoutImmediate(ContentRoot.GetComponent<RectTransform>());
        }

        private void OpenFamily(string kId, string familyId)
        {
            try
            {
                if (ZhuluFamilyWindow.Instance != null)
                    ZhuluFamilyWindow.Instance.Show(kId, familyId);
                ScrollWindow.showWindow(Main.FamilyWindowId);
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[LineageWindow] 打开家族页异常: {ex.Message}");
            }
        }

        /// <summary>谱牒快照可能尚未等到年度刷新；统计丁口时以全局 Actor 实时状态为准。</summary>
        private static bool IsAliveNow(FamilyRegistry.FamilyMember member)
        {
            if (member == null) return false;
            try
            {
                Actor actor = World.world?.units?.get(member.ActorId);
                if (actor != null) return actor.isAlive();
            }
            catch { }
            return member.Alive;
        }

        /// <summary>打开望族（原版 Clan 数据源——阶段3）。</summary>
        private void OpenClan(long clanId)
        {
            try
            {
                Clan c = null;
                try { c = World.world.clans.get(clanId); } catch { }
                if (c == null) { WorldTip.showNow("该族已散", false, "top", 1.5f); return; }
                if (ZhuluFamilyWindow.Instance != null)
                    ZhuluFamilyWindow.Instance.Show(c);
                ScrollWindow.showWindow(Main.FamilyWindowId);
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[LineageWindow] 打开望族页异常: {ex.Message}");
            }
        }

        // ═══════════════════════ UI 工具 ═══════════════════════

        private GameObject CreateCard(float height)
        {
            // SolidBg-36 模式（Mandate 同款）：容器纯 RectTransform，贴图层向左延 36 盖住九宫格厚装饰边
            var card = new GameObject("Card", typeof(RectTransform));
            card.transform.SetParent(ContentRoot, false);
            _cards.Add(card);
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
            bgImg.raycastTarget = true;
        }

        // ═════════ 2026-08-19 串联·谱牒重设计（方案6.1：大宗卡46px/榜单行24px/折叠段头） ═════════

        // 阶段3H-2：本家是否游牧体制（FamilyData.KingdomId 反查）
        private static bool FamNomad(FamilyRegistry.FamilyData fam)
        {
            try
            {
                var k = string.IsNullOrEmpty(fam?.KingdomId) ? null : ZhuluRebuild.Persistence.KingdomDataExt.FindKingdomById(fam.KingdomId);
                return k != null && Domain.KingdomCivilizationService.Instance.IsNomadic(k);
            }
            catch { return false; }
        }

        /// <summary>门第档（短名/色）：家声数值→视觉分档，徽章与边条统一取色。</summary>
        private static (string shortName, string hex) TierInfo(int prestige, bool nomad = false)
        {
            if (nomad)
            {
                if (prestige >= 80) return ("金帐", "#ffd700");
                if (prestige >= 60) return ("那颜", "#b48ee0");
                if (prestige >= 40) return ("控弦", "#6fc7b4");
                if (prestige >= 20) return ("部众", "#8a8578");
                return ("罪奴", "#e07070");
            }
            if (prestige >= 80) return ("簪缨", "#ffd700");
            if (prestige >= 60) return ("衣冠", "#b48ee0");
            if (prestige >= 40) return ("诗礼", "#6fc7b4");
            if (prestige >= 20) return ("寒微", "#8a8578");
            return ("罪籍", "#e07070");
        }

        private static Color HexColor(string hex)
        {
            Color c;
            return ColorUtility.TryParseHtmlString(hex, out c) ? c : Color.white;
        }

        /// <summary>大宗卡：左30头像 + 行1(族长·丁口) + 行2(家声条·家风) + 右上门第徽章（色底白字）。</summary>
        private void BuildMainCard(Transform card, FamilyRegistry.FamilyData fam, int aliveMain)
        {
            var (tier, hex) = TierInfo(fam.Prestige, FamNomad(fam));

            // 头像容器（30×30；活人动态头像，缺档降级灰墓碑，再降级色块姓氏）
            var avatarBox = new GameObject("HeadAvatar", typeof(RectTransform), typeof(Image));
            avatarBox.transform.SetParent(card, false);
            var avRect = avatarBox.GetComponent<RectTransform>();
            LayoutLeft(avRect, 8, 8, 30, 30);
            var avBg = avatarBox.GetComponent<Image>();
            avBg.color = new Color(0.12f, 0.11f, 0.09f, 0.9f);
            avBg.raycastTarget = false;
            GameObject avatar = null;
            try
            {
                Actor head = null;
                if (fam.HeadId != 0) head = World.world?.units?.get(fam.HeadId);
                avatar = head != null && head.isAlive()
                    ? ZhuluAvatar.CreateLiving(avatarBox.transform, head)
                    : ZhuluAvatar.CreateDead(avatarBox.transform);
            }
            catch { }
            if (avatar == null)
            {
                var fallback = CreateText(avatarBox.transform, $"<color={hex}>{fam.Surname}</color>", 12);
                fallback.alignment = TextAnchor.MiddleCenter;
                Stretch(fallback.GetComponent<RectTransform>());
            }

            // 行1：族长名 + 丁口
            var line1 = CreateText(card,
                $"<b>{fam.HeadName}</b>  <color=#8d867c>· 丁口 {aliveMain}</color>", 10);
            line1.alignment = TextAnchor.MiddleLeft;
            line1.color = new Color(0.95f, 0.90f, 0.78f, 1f);
            LayoutLeft(line1.GetComponent<RectTransform>(), 44, 7, InnerWidth - 96, 14);

            // 行2：家声条（门第色 fill）+ 家声值 + 家风
            float barW = 64f, barX = 44, barY = 27;
            var barBg = new GameObject("PrestigeBarBg", typeof(RectTransform), typeof(Image));
            barBg.transform.SetParent(card, false);
            LayoutLeft(barBg.GetComponent<RectTransform>(), barX, barY, barW, 8);
            var bgImg = barBg.GetComponent<Image>();
            bgImg.color = new Color(0.10f, 0.09f, 0.08f, 0.95f);
            bgImg.raycastTarget = false;
            var barFill = new GameObject("PrestigeBarFill", typeof(RectTransform), typeof(Image));
            barFill.transform.SetParent(barBg.transform, false);
            var fillRect = barFill.GetComponent<RectTransform>();
            fillRect.anchorMin = new Vector2(0, 0); fillRect.anchorMax = new Vector2(0, 1);
            fillRect.pivot = new Vector2(0, 0.5f);
            fillRect.anchoredPosition = Vector2.zero;
            fillRect.sizeDelta = new Vector2(barW * Mathf.Clamp01(fam.Prestige / 100f), 0);
            var fillImg = barFill.GetComponent<Image>();
            fillImg.color = HexColor(hex);
            fillImg.raycastTarget = false;

            bool famNomad = FamNomad(fam);
            string styleName = FamilyRegistry.GetStyleName(fam.Style, famNomad);
            string styleLabel = famNomad ? "门风" : "家风";
            var line2 = CreateText(card,
                $"{fam.Prestige}  <color=#8d867c>· {styleLabel} {(string.IsNullOrEmpty(styleName) ? "—" : styleName)}</color>", 9);
            line2.alignment = TextAnchor.MiddleLeft;
            LayoutLeft(line2.GetComponent<RectTransform>(), barX + barW + 6, barY - 1, InnerWidth - barX - barW - 60, 12);

            // 右上门第徽章：色底白字短名（簪缨金/衣冠紫/诗礼青/寒微灰/罪籍红）
            var badge = new GameObject("TierBadge", typeof(RectTransform), typeof(Image));
            badge.transform.SetParent(card, false);
            LayoutLeft(badge.GetComponent<RectTransform>(), InnerWidth - 52, 5, 44, 16);
            var badgeImg = badge.GetComponent<Image>();
            badgeImg.color = HexColor(hex) * 0.35f + new Color(0f, 0f, 0f, 0.55f);
            badgeImg.raycastTarget = false;
            var badgeText = CreateText(badge.transform, $"<b>{tier}</b>", 9);
            badgeText.alignment = TextAnchor.MiddleCenter;
            badgeText.color = HexColor(hex);
            Stretch(badgeText.GetComponent<RectTransform>());
        }

        /// <summary>折叠段头（▼▶ + 段名 + 条数；整卡可点击切换折叠，_sectionState 记忆）。</summary>
        private void CreateSectionHeader(string title, int count, string key)
        {
            var header = CreateCard(20f);
            bool open = IsSectionOpen(key);
            string arrow = open ? "▼" : "▶";
            var t = CreateText(header.transform,
                $"<b><color=#ffd8a0>{arrow} {title}</color></b>  <color=#8a8578>（{count}）</color>", 10);
            t.alignment = TextAnchor.MiddleLeft;
            LayoutLeft(t.GetComponent<RectTransform>(), 8, 2, InnerWidth - 16, 16);
            var btn = header.AddComponent<Button>();
            btn.onClick.AddListener(() =>
            {
                bool nowOpen = !IsSectionOpen(key);
                _sectionState[key] = nowOpen;
                ZhuluRebuild.Persistence.ZhuluPreferences.SetFold("lineage:" + key, nowOpen);   // 批次9d
                Refresh();
            });
        }

        private bool IsSectionOpen(string key)
        {
            // 批次9d：折叠态持久化（跨会话记忆；key 前缀窗口名防撞）
            return ZhuluRebuild.Persistence.ZhuluPreferences.GetFold("lineage:" + key, true);
        }

        /// <summary>榜单行（24px 单行制）：门第色4px左边条 + 序号 + 名 + 门第徽章 + 家声/丁口（+入仕）。</summary>
        private void CreateRankRow(string rank, string name, int prestige, int members, int officials, string extra, Action onClick, bool nomad = false)
        {
            var (tier, hex) = TierInfo(prestige, nomad);
            var row = new GameObject("RankRow", typeof(RectTransform), typeof(Button));
            row.transform.SetParent(ContentRoot, false);
            _cards.Add(row);
            var rt = row.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(InnerWidth, 24f);
            AttachCardBg(row.transform, new Color(0.17f, 0.16f, 0.13f, 0.92f));
            row.GetComponent<Button>().onClick.AddListener(() => onClick?.Invoke());

            // 左边条：门第色 4px（视觉锚点，一眼分档）
            var edge = new GameObject("TierEdge", typeof(RectTransform), typeof(Image));
            edge.transform.SetParent(row.transform, false);
            var edgeRect = edge.GetComponent<RectTransform>();
            edgeRect.anchorMin = new Vector2(0, 0); edgeRect.anchorMax = new Vector2(0, 1);
            edgeRect.pivot = new Vector2(0, 0.5f);
            edgeRect.anchoredPosition = new Vector2(-36f, 0);
            edgeRect.sizeDelta = new Vector2(4, 0);
            var edgeImg = edge.GetComponent<Image>();
            edgeImg.color = HexColor(hex);
            edgeImg.raycastTarget = false;

            // 单行：序号 名 家声N · N人 · 入仕N（门第不再内联占宽）
            string officialText = officials > 0 ? $" · <color=#c8b98a>入仕{officials}</color>" : "";
            string text = $"<color=#8a8578>{rank}</color> <b>{name}</b> " +
                          $"<color=#8d867c>家声{prestige} · {members}人</color>{officialText}{extra}";
            var t = CreateText(row.transform, text, 9);
            t.alignment = TextAnchor.MiddleLeft;
            t.color = new Color(0.95f, 0.90f, 0.78f, 1f);
            LayoutLeft(t.GetComponent<RectTransform>(), 10, 5, InnerWidth - 62, 15);

            // 批次7美化B4：右置门第药丸徽章（程序生成药丸底+门第色字，替代旧"[寒微]"内联文本）
            ZhuluWidgets.Badge(row.transform, InnerWidth - 54f, 5f, tier, hex);
        }

        private static void MakeClickable(GameObject card, string kId, string familyId)
        {
            var btn = card.GetComponent<Button>() ?? card.AddComponent<Button>();
            btn.onClick.AddListener(() =>
            {
                if (ZhuluFamilyWindow.Instance != null)
                    ZhuluFamilyWindow.Instance.Show(kId, familyId);
                ScrollWindow.showWindow(Main.FamilyWindowId);
            });
        }

        private void AddHint(string text)
        {
            var t = CreateText(ContentRoot, $"<color=#6a6458>{text}</color>", 9);
            t.alignment = TextAnchor.MiddleCenter;
            LayoutLeft(t.GetComponent<RectTransform>(), 0, 0, InnerWidth, 16);
            _cards.Add(t.gameObject);
        }

        private void ClearCards()
        {
            foreach (var go in _cards) { try { if (go != null) Destroy(go); } catch { } }
            _cards.Clear();
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
