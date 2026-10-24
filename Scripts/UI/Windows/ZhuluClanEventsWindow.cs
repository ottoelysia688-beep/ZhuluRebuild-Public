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
    /// 家族大事窗（阶段3：原版家族窗口「大事」按钮的目标窗口）。
    /// 三段内容：①当前身份与族长更替史（原版 past_chiefs）
    /// ②族中现任官员（实时）③族中成员史册大事（个人史册过滤：君临/仕途/功名/朝争）。
    /// 实时聚合，无新增持久化。
    /// 批次8迁移（2026-08-23）：骨架收敛至 ZhuluScrollWindowBase——删除手写 RebuildUI 约100行
    /// 与本地 返回钮/布局/文本 工具；动态牌匾题静态化，氏族名并入首行副状态（刷新仅改文本不重建）。
    /// </summary>
    public class ZhuluClanEventsWindow : ZhuluScrollWindowBase
    {
        public static ZhuluClanEventsWindow Instance { get; private set; }

        private Text _subStatText;
        private Clan _clan;
        private string _kingdomId = "";

        private readonly List<GameObject> _cards = new List<GameObject>();

        protected override string WindowTitle => "<b>家族大事</b>";
        protected override float WindowWidth => 350f;
        protected override float WindowHeight => 430f;
        protected override float PlaqueWidth => 240f;

        public void Initialize()
        {
            Instance = this;
            ZhuluContext.Instance.LogInfo("[ZhuluClanEventsWindow] 初始化完成");
        }

        private void OnEnable()
        {
            if (World.world == null) return;
            EnsureBuilt();
            Refresh();
        }

        /// <summary>外部入口：定位家族并打开。</summary>
        public void Show(Clan clan)
        {
            _clan = clan;
            _kingdomId = ResolveKingdomId(clan);
            if (World.world != null && gameObject.activeInHierarchy)
            {
                EnsureBuilt();
                Refresh();
            }
        }

        private static string ResolveKingdomId(Clan clan)
        {
            try
            {
                foreach (Kingdom k in World.world.kingdoms.list)
                {
                    if (k == null || !k.isAlive() || k.units == null) continue;
                    foreach (Actor u in k.units)
                    {
                        if (u != null && u.clan != null && u.clan.getID() == clan.getID()) return k.id.ToString();
                    }
                }
            }
            catch { }
            return "";
        }

        /// <summary>副状态行（常驻内容流首项，不参与 ClearCards）。</summary>
        protected override void BuildContent()
        {
            _subStatText = CreateText(ContentRoot, "", 9);
            _subStatText.alignment = TextAnchor.MiddleCenter;
            LayoutLeft(_subStatText.GetComponent<RectTransform>(), 0f, 0f, InnerWidth, 14f);
            Refresh();
        }

        private void Refresh()
        {
            if (ContentRoot == null || _clan == null) return;
            try
            {
                ClearCards();
                string clanName = _clan.data != null ? (_clan.data.name ?? "") : "";
                Kingdom k = null;
                try { if (!string.IsNullOrEmpty(_kingdomId)) k = World.world.kingdoms.get(int.Parse(_kingdomId)); } catch { }

                // 身份判定
                string status = "在野家族";
                if (k != null && k.king != null && k.king.clan != null && k.king.clan.getID() == _clan.getID())
                    status = "当朝王族";
                else if (FamilyRegistry.Instance.FindNotableByClan(_clan.getID()) != null)
                    status = "国中望族";

                if (_subStatText != null)
                    _subStatText.text = $"<b>{clanName}</b> · 建元 <color=#ffd8d9>{ZhuluTime.CurrentYear}</color> 年 · <color=#ffd700>{status}</color> · 族长 {_clan.getChief()?.getName() ?? "—"}";

                // ── ① 族长更替史（原版 past_chiefs）──
                var chiefCard = CreateCard(20f);
                AddSectionLabel(chiefCard.transform, "族长更替");
                int chiefRows = AppendPastChiefs();

                // ── ② 族中现任官员 ──
                var officialsCard = CreateCard(20f);
                AddSectionLabel(officialsCard.transform, "族中在朝");
                int officialRows = AppendOfficials(k);

                // ── ③ 成员史册大事 ──
                var eventsCard = CreateCard(20f);
                AddSectionLabel(eventsCard.transform, "族中大事");
                int eventRows = AppendMemberEvents();

                if (chiefRows + officialRows + eventRows == 0)
                    AddHint("此族尚无大事可录");
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[ClanEventsWindow] 刷新异常: {ex.Message}");
            }
            LayoutRebuilder.ForceRebuildLayoutImmediate(ContentRoot.GetComponent<RectTransform>());
        }

        /// <summary>族长更替史（原版 ClanData.past_chiefs——字段名按反编译，异常即跳过；倒序=最近在前）。</summary>
        private int AppendPastChiefs()
        {
            int n = 0;
            try
            {
                var past = _clan.data?.past_chiefs;
                if (past == null || past.Count == 0) { AddHint("首任族长在位，尚无更替"); return 0; }
                for (int i = past.Count - 1; i >= 0 && n < 8; i--)
                {
                    var e = past[i];
                    if (e == null) continue;
                    string name = e.name ?? "?";
                    // 2026-08-20 bug修复：timestamp_ago 为原始世界时间戳，须 Date.getYearsSince 换算
                    string span = "";
                    try
                    {
                        if (e.timestamp_ago > 0)
                        {
                            int yAgo = Date.getYearsSince(e.timestamp_ago);
                            span = yAgo > 0 ? $"{yAgo} 年前继任" : "新近继任";
                        }
                    }
                    catch { }
                    var t = CreateText(ContentRoot, $"<color={ZhuluUIStyle.Hex.PaperDim}>前任族长</color> <color=#e8e0d0>{name}</color> {span}", 9);
                    t.alignment = TextAnchor.MiddleLeft;
                    LayoutLeft(t.GetComponent<RectTransform>(), 10, 0, InnerWidth - 20, 14);
                    _cards.Add(t.gameObject);
                    n++;
                }
            }
            catch { }
            return n;
        }

        /// <summary>族中现任官员（实时：clan.units × 官制记录）。</summary>
        private int AppendOfficials(Kingdom k)
        {
            int n = 0;
            try
            {
                if (k == null || _clan.units == null) { AddHint("无邦国官职"); return 0; }
                string kId = _kingdomId;
                var records = OfficeManager.Instance.GetRecordsPublic(kId);
                if (records == null || records.Count == 0) { AddHint("族中无人入仕"); return 0; }

                foreach (Actor u in _clan.units)
                {
                    if (u == null || !u.isAlive()) continue;
                    string uid = u.getID().ToString();
                    foreach (var r in records)
                    {
                        if (r == null || r.PersonId != uid) continue;
                        string title = "";
                        try { title = OfficeManager.GetOfficeTitleForKingdom(r.OfficeId, k); } catch { }
                        var t = CreateText(ContentRoot, $"<color=#ffd8a0>{u.getName()}</color> 现任 {title}", 9);
                        t.alignment = TextAnchor.MiddleLeft;
                        LayoutLeft(t.GetComponent<RectTransform>(), 10, 0, InnerWidth - 20, 14);
                        _cards.Add(t.gameObject);
                        n++;
                        break;
                    }
                }
                if (n == 0) AddHint("族中无人入仕");
            }
            catch { }
            return n;
        }

        /// <summary>族中成员史册大事（君临/仕途/功名/朝争——按年倒序，30 条封顶）。</summary>
        private int AppendMemberEvents()
        {
            int n = 0;
            try
            {
                if (_clan.units == null) return 0;
                var rows = new List<(int year, string text)>();
                string[] cats = { PersonHistoryManager.CAT_JUNLIN, PersonHistoryManager.CAT_SHITU,
                                  PersonHistoryManager.CAT_GONGMING, PersonHistoryManager.CAT_CHAOZHENG };
                foreach (Actor u in _clan.units)
                {
                    if (u == null) continue;
                    var rec = PersonHistoryManager.Instance.GetRecord(u.getID());
                    if (rec == null) continue;
                    foreach (var entry in rec.Entries)
                    {
                        if (entry == null || !cats.Contains(entry.Category)) continue;
                        rows.Add((entry.Year, $"{u.getName()}：{entry.Text}"));
                    }
                }
                if (rows.Count == 0) { AddHint("尚无大事入册"); return 0; }
                foreach (var r in rows.OrderByDescending(x => x.year).Take(30))
                {
                    var t = CreateText(ContentRoot, $"<color=#6a8f6a>{r.year}</color> {r.text}", 9);
                    t.alignment = TextAnchor.MiddleLeft;
                    LayoutLeft(t.GetComponent<RectTransform>(), 10, 0, InnerWidth - 20, 14);
                    _cards.Add(t.gameObject);
                    n++;
                }
            }
            catch { }
            return n;
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

            var solidObj = new GameObject("SolidBg", typeof(RectTransform), typeof(Image));
            solidObj.transform.SetParent(card.transform, false);
            var solidRect = solidObj.GetComponent<RectTransform>();
            solidRect.anchorMin = Vector2.zero;
            solidRect.anchorMax = Vector2.one;
            solidRect.offsetMin = new Vector2(-36f, 0f);
            solidRect.offsetMax = Vector2.zero;
            var solidImg = solidObj.GetComponent<Image>();
            solidImg.color = ZhuluUIStyle.InkCard;
            solidImg.raycastTarget = false;

            var bgObj = new GameObject("Bg", typeof(RectTransform), typeof(Image));
            bgObj.transform.SetParent(card.transform, false);
            var bgRect = bgObj.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = new Vector2(-36f, 0f);
            bgRect.offsetMax = Vector2.zero;
            var bgImg = bgObj.GetComponent<Image>();
            bgImg.color = ZhuluUIStyle.InkCard;
            bgImg.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            bgImg.type = Image.Type.Sliced;
            bgImg.raycastTarget = false;
            return card;
        }

        private void AddSectionLabel(Transform card, string text)
        {
            var t = CreateText(card, $"<b><color=#ffd8a0>{text}</color></b>", 10);
            t.alignment = TextAnchor.MiddleLeft;
            LayoutLeft(t.GetComponent<RectTransform>(), 8, 2, InnerWidth - 20, 16);
        }

        private void AddHint(string text)
        {
            var t = CreateText(ContentRoot, $"<color=#6a6458>{text}</color>", 9);
            t.alignment = TextAnchor.MiddleLeft;
            LayoutLeft(t.GetComponent<RectTransform>(), 10, 0, InnerWidth - 20, 14);
            _cards.Add(t.gameObject);
        }

        private void ClearCards()
        {
            foreach (var go in _cards) { try { if (go != null) Destroy(go); } catch { } }
            _cards.Clear();
        }
    }
}
