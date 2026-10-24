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


// ── 由 WorldOverviewWindow.cs 按 region 拆出的 partial 部分：1. 天下总览视图 ──
// 2026-10-23 [P1-1] 纯文件级拆分，编译后仍为同一个类型，无任何行为改动。
namespace ZhuluRebuild.UI.Windows
{
    public partial class WorldOverviewWindow : MonoBehaviour
    {
        #region 1. 天下总览视图
        private void RenderWorldOverviewUI()
        {
            _headerBackOverride = null;   // 清残留返回覆盖（防国策/邦交二级页残留误触发）
            UpdateHeaderTitle("天下总览");
            SetHeaderTabsVisible(true);
            string currentTab = _worldVm != null ? _worldVm.ActiveTab : "state";
            _worldVm = WorldOverviewVm.CreateFromAdapterOrMock(currentTab);

            // 两套天命归属文本
            string easternMandateText = "<color=#888888>空悬</color>";
            string nomadicMandateText = "<color=#888888>空悬</color>";
            try
            {
                string holderId = OrthodoxMandateManager.Instance.GetMandateHolderId();
                if (!string.IsNullOrEmpty(holderId))
                {
                    Kingdom holder = KingdomDataExt.FindKingdomById(holderId);
                    easternMandateText = holder != null ? $"<color={UI.ZhuluUIStyle.Hex.Imperial}>{holder.name}</color>" : "<color={UI.ZhuluUIStyle.Hex.Imperial}>天命国</color>";
                }
                string nomadicHolderId = NomadicMandateManager.Instance.GetMandateHolderId();
                if (!string.IsNullOrEmpty(nomadicHolderId))
                {
                    Kingdom holder = KingdomDataExt.FindKingdomById(nomadicHolderId);
                    nomadicMandateText = holder != null ? $"<color=#c8b98a>{holder.name}</color>" : "<color=#c8b98a>王庭</color>";
                }
            }
            catch { }

            // 更新 Header 统计行（Header 固定在 Background，只改文本）
            if (_headerStatText != null)
            {
                _headerStatText.text = $"<b>建元 {_worldVm.CurrentYear} 年</b>　列国:<color=#6ca3fd>{_worldVm.TotalKingdoms}</color> | 战:<color=#e0554a>{_worldVm.ActiveWars}</color> | 天命:{easternMandateText} | 天眷:{nomadicMandateText}";
            }

            switch (_worldVm.ActiveTab)
            {
                case "monarch":
                    CreateMonarchBoard();
                    break;
                case "talent":
                    _talentBuildCoroutine = StartCoroutine(BuildTalentBoardCoroutine(0));
                    break;
                case "civil":
                    _talentBuildCoroutine = StartCoroutine(BuildTalentBoardCoroutine(1));
                    break;
                case "marshal":
                    _talentBuildCoroutine = StartCoroutine(BuildTalentBoardCoroutine(2));
                    break;
                default:
                    CreateWorldStateBar();   // 阶段2J：天下大势状态行（太平/大治/危局/邦交）
                    CreateWorldRankList();
                    CreateWorldNews();       // 阶段2J：时局快讯（最近大事）
                    break;
            }
        }

        /// <summary>
        /// 2026-08-19 升级为接管式关闭按钮：隐藏原版红×与方框背景，
        /// 在 Background 右上角自建逐鹿关闭钮（位置可控、随窗口拖动）。
        /// </summary>
        private static void HideCloseButtonFrame(Transform root)
        {
            ZhuluUIStyle.ReplaceCloseButton(root);
        }

        private void CreateWorldRankList()
        {
            // 表头（列宽与数据行完全一致，保证对齐；总宽 ≤ 内容宽 308）
            _rankSortEnabled = true;
            CreateRankTableHeader(new[] { ("档位", 60f), ("国家", 76f), ("君主", 50f), ("城", 28f), ("兵", 36f), ("稳", 26f) });

            var entries = new List<(Kingdom k, string kId, int tierOrder, int cities, int army, string kingName, string tierName, Color tierColor, Color tierBg, float stab, bool isTm)>();
            if (World.world?.kingdoms?.list != null)
            {
                foreach (Kingdom k in World.world.kingdoms.list)
                {
                    if (k == null || !k.isAlive()) continue;
                    try
                    {
                        // 逐国 try-catch：单个国家数据异常不影响其他国（防止整榜变"天下无国"）
                        string kId = k.id.ToString();
                        bool isTm = PolicyManager.IsMandateHolderForKingdom(k);
                        bool nomadic = KingdomCivilizationService.Instance.IsNomadic(k);
                        int effCities = KingdomExt.GetEffectiveCities(kId, k.cities != null ? k.cities.Count : 0);
                        var tier = OfficeManager.GetTierWithMandate(k, isTm);
                        int order = tier switch
                        {
                            OfficeManager.KingdomTier.Tianming => 0,
                            OfficeManager.KingdomTier.Gong => 1,
                            OfficeManager.KingdomTier.Hou => 2,
                            OfficeManager.KingdomTier.Bo => 3,
                            _ => 4
                        };
                        string tierName = nomadic ? tier switch
                        {
                            OfficeManager.KingdomTier.Tianming => "王庭会盟",
                            OfficeManager.KingdomTier.Gong => "大汗庭议政",
                            OfficeManager.KingdomTier.Hou => "汗国议事",
                            OfficeManager.KingdomTier.Bo => "部盟议事",
                            _ => "部族议事"
                        } : tier switch
                        {
                            OfficeManager.KingdomTier.Tianming => "天子临朝",
                            OfficeManager.KingdomTier.Gong => "周礼六官",
                            OfficeManager.KingdomTier.Hou => "朝议制度",
                            OfficeManager.KingdomTier.Bo => "大夫辅政",
                            _ => "宗族议事"
                        };
                        Color tierColor = tier switch
                        {
                            OfficeManager.KingdomTier.Tianming => new Color(1f, 0.8f, 0.4f),
                            OfficeManager.KingdomTier.Gong => new Color(0.8f, 0.53f, 1f),
                            OfficeManager.KingdomTier.Hou => new Color(0.4f, 1f, 0.53f),
                            OfficeManager.KingdomTier.Bo => new Color(0.4f, 0.8f, 1f),
                            _ => new Color(0.67f, 0.67f, 0.67f)
                        };
                        Color tierBg = tier switch
                        {
                            OfficeManager.KingdomTier.Tianming => new Color(0.35f, 0.16f, 0.1f, 1f),
                            OfficeManager.KingdomTier.Gong => new Color(0.16f, 0.1f, 0.29f, 1f),
                            OfficeManager.KingdomTier.Hou => new Color(0.1f, 0.23f, 0.16f, 1f),
                            OfficeManager.KingdomTier.Bo => new Color(0.16f, 0.23f, 0.29f, 1f),
                            _ => new Color(0.2f, 0.2f, 0.2f, 1f)
                        };
                        string kingName = k.king != null ? (k.king.getName() ?? k.king.name ?? "无名君") : "国君空缺";
                        float stab = StabilityManager.Instance.GetStability(kId);
                        entries.Add((k, kId, order, effCities,
                            k.countTotalWarriors(), kingName, tierName, tierColor, tierBg, stab, isTm));
                    }
                    catch (Exception ex)
                    {
                        ZhuluContext.Instance.LogWarning($"[WorldOverview] 今势榜国家数据异常: {k?.name ?? "?"}, {ex.Message}");
                    }
                }
            }

            // 阶段2J 排序：默认天命→公→侯→伯→子同级按兵力；点列头按城/兵/稳排序（升/降）
            if (_rankSortColumn == 3 || _rankSortColumn == 4 || _rankSortColumn == 5)
            {
                float SortKey((Kingdom k, string kId, int tierOrder, int cities, int army, string kingName, string tierName, Color tierColor, Color tierBg, float stab, bool isTm) e)
                {
                    return _rankSortColumn == 3 ? e.cities : (_rankSortColumn == 4 ? e.army : e.stab);
                }
                entries.Sort((a, b) =>
                {
                    float ka = SortKey(a), kb = SortKey(b);
                    int c = ka.CompareTo(kb);
                    if (c != 0) return _rankSortAsc ? c : -c;
                    return a.tierOrder.CompareTo(b.tierOrder);
                });
            }
            else
            {
                entries.Sort((a, b) =>
                {
                    int c = a.tierOrder.CompareTo(b.tierOrder);
                    if (c != 0) return c;
                    return b.army.CompareTo(a.army);
                });
            }

            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                CreateRankRow(e, i);
            }

            if (entries.Count == 0)
            {
                CreateText(_contentTransform, "<color=#888888>天下无国。</color>", 9);
            }
        }

        /// <summary>阶段2J：天下大势状态行（太平评价 / 大治国数 / 危局国数 / 和约互不侵犯数）。</summary>
        private void CreateWorldStateBar()
        {
            try
            {
                int totalWars = 0, stableCount = 0, crisisCount = 0, truceCount = 0, napCount = 0;
                float stabSum = 0f;
                int alive = 0;
                var list = World.world?.kingdoms?.list;
                if (list != null)
                {
                    foreach (Kingdom k in list)
                    {
                        if (k == null || !k.isAlive()) continue;
                        alive++;
                        string kId = k.id.ToString();
                        try
                        {
                            float stab = StabilityManager.Instance.GetStability(kId);
                            stabSum += stab;
                            if (stab >= 60f) stableCount++;
                            if (stab < 45f) crisisCount++;
                        }
                        catch { }
                        // 厌战高也算危局
                        try
                        {
                            int ex = ZhuluWarService.GetWarExhaustion(kId);
                            if (ex >= 60) crisisCount++;
                        }
                        catch { }
                        // 战争数
                        try
                        {
                            var wars = k.getWars();
                            if (wars != null) { foreach (var _ in wars) totalWars++; }
                        }
                        catch { }
                    }
                    // 和约/互不侵犯（扫描 pairwise——国家数少，可接受）
                    int year = ZhuluTime.CurrentYear;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var a = list[i];
                        if (a == null || !a.isAlive()) continue;
                        for (int j = i + 1; j < list.Count; j++)
                        {
                            var b = list[j];
                            if (b == null || !b.isAlive()) continue;
                            try
                            {
                                if (ZhuluDiplomacyService.IsTruceActive(a.id.ToString(), b.id.ToString(), year)) truceCount++;
                                if (ZhuluDiplomacyService.IsNonAggressionActive(a.id.ToString(), b.id.ToString(), year)) napCount++;
                            }
                            catch { }
                        }
                    }
                }

                // 太平评价
                string worldGrade, gradeColor;
                float avgStab = alive > 0 ? stabSum / alive : 0f;
                if (totalWars <= 3 && avgStab >= 60f) { worldGrade = "☀ 太平之世"; gradeColor = "#70d468"; }
                else if (totalWars <= 8) { worldGrade = "☁ 烽火渐起"; gradeColor = "#f0c060"; }
                else { worldGrade = "⚡ 乱世纷争"; gradeColor = "#e0554a"; }

                // 状态行容器（横向 4 格均分）
                GameObject barObj = new GameObject("WorldStateBar", typeof(RectTransform), typeof(HorizontalLayoutGroup));
                barObj.transform.SetParent(_contentTransform, false);
                RectTransform barRect = barObj.GetComponent<RectTransform>();
                barRect.sizeDelta = new Vector2(_innerWidth, 34f);
                HorizontalLayoutGroup barLayout = barObj.GetComponent<HorizontalLayoutGroup>();
                barLayout.padding = new RectOffset(0, 0, 2, 2);
                barLayout.spacing = 4f;
                barLayout.childControlWidth = true;
                barLayout.childControlHeight = true;
                barLayout.childForceExpandWidth = true;

                CreateStateCell(barObj.transform, $"<color={gradeColor}>{worldGrade}</color>", alive > 0 ? $"均稳 {avgStab:F0}" : "", gradeColor, 9);
                CreateStateCell(barObj.transform, $"<color={UI.ZhuluUIStyle.Hex.Positive}>🛡 大治 {stableCount}</color>", "稳定≥60", UI.ZhuluUIStyle.Hex.Positive, 9);
                CreateStateCell(barObj.transform, $"<color=#e0554a>⚔ 危局 {crisisCount}</color>", "厌战高/不稳", "#e0554a", 9);
                CreateStateCell(barObj.transform, $"<color=#6ca3fd>🏛 和约 {truceCount}</color>", $"互不侵犯 {napCount}", "#6ca3fd", 9);
            }
            catch { }
        }

        /// <summary>状态格（小卡片）。</summary>
        private void CreateStateCell(Transform parent, string title, string sub, string colorHex, int fontSize)
        {
            GameObject cell = new GameObject("StateCell", typeof(RectTransform), typeof(Image));
            cell.transform.SetParent(parent, false);
            Image cellImg = cell.GetComponent<Image>();
            cellImg.color = new Color(0.10f, 0.14f, 0.20f, 0.8f);
            cellImg.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            cellImg.type = Image.Type.Sliced;
            Text t = CreateText(cell.transform, title, fontSize);
            t.alignment = TextAnchor.MiddleCenter;
            t.color = Color.white;
            RectTransform tr = t.GetComponent<RectTransform>();
            tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one;
            tr.offsetMin = new Vector2(2f, 10f); tr.offsetMax = new Vector2(-2f, -2f);
            Text s = CreateText(cell.transform, sub, 7);
            s.alignment = TextAnchor.MiddleCenter;
            s.color = new Color(0.65f, 0.65f, 0.65f);
            RectTransform sr = s.GetComponent<RectTransform>();
            sr.anchorMin = Vector2.zero; sr.anchorMax = Vector2.one;
            sr.offsetMin = new Vector2(2f, 2f); sr.offsetMax = new Vector2(-2f, -10f);
        }

        /// <summary>阶段2J：时局快讯（最近 5 条世界大事——轻量史册）。</summary>
        private void CreateWorldNews()
        {
            try
            {
                var chronicles = ChronicleManager.Instance.GetWorldMajorChronicle();
                if (chronicles == null || chronicles.Count == 0) return;
                // 分隔
                GameObject sep = new GameObject("NewsSep", typeof(RectTransform), typeof(Image));
                sep.transform.SetParent(_contentTransform, false);
                sep.GetComponent<RectTransform>().sizeDelta = new Vector2(_innerWidth, 2f);
                sep.GetComponent<Image>().color = new Color(0.3f, 0.25f, 0.1f, 0.6f);

                Text title = CreateText(_contentTransform, "<color=#f0c060>⚡ 时局快讯</color>", 10);
                title.alignment = TextAnchor.MiddleLeft;
                title.GetComponent<RectTransform>().sizeDelta = new Vector2(_innerWidth, 18f);

                int count = 0;
                for (int i = chronicles.Count - 1; i >= 0 && count < 5; i--)
                {
                    var e = chronicles[i];
                    if (e == null) continue;
                    count++;
                    string text = System.Text.RegularExpressions.Regex.Replace(e.ContentText ?? "", "<color=#[0-9A-Fa-f]{6}>", "").Replace("</color>", "");
                    if (text.Length > 42) text = text.Substring(0, 42) + "…";
                    // 2026-08-19 串联·方案6.3：快讯可点击——战争类跳战争窗、天命类跳天命窗（其余不响应）
                    var entry = e;
                    GameObject lineObj = new GameObject("NewsLine", typeof(RectTransform), typeof(Button));
                    lineObj.transform.SetParent(_contentTransform, false);
                    lineObj.GetComponent<RectTransform>().sizeDelta = new Vector2(_innerWidth, 16f);
                    var lineBtn = lineObj.GetComponent<Button>();
                    lineBtn.transition = Selectable.Transition.ColorTint;
                    bool isWarNews = entry.Type == ChronicleEventType.WarDeclared || entry.Type == ChronicleEventType.PeaceTreaty;
                    bool isMandateNews = entry.Type == ChronicleEventType.MandateGranted || entry.Type == ChronicleEventType.MandateCollapsed;
                    if (isWarNews)
                        lineBtn.onClick.AddListener(() => { try { ScrollWindow.showWindow(Main.WarsWindowId); } catch { } });
                    else if (isMandateNews)
                        lineBtn.onClick.AddListener(() => { try { ScrollWindow.showWindow(Main.MandateWindowId); } catch { } });
                    Text line = CreateText(lineObj.transform, $"<size=8><color=#8a8578>[{e.Year}]</color></size> {text}", 9);
                    line.alignment = TextAnchor.MiddleLeft;
                    line.GetComponent<RectTransform>().sizeDelta = new Vector2(_innerWidth, 16f);
                    if (!isWarNews && !isMandateNews) lineBtn.interactable = false;
                }
            }
            catch { }
        }

        /// <summary>阶段2J：功名徽章（状元/进士/举人/秀才——科举系统）。</summary>
        private static string BuildGongmingBadge(Actor actor)
        {
            try
            {
                if (actor == null || actor.traits == null) return "<color=#6a6458>—</color>";
                foreach (var t in actor.traits)
                {
                    if (t == null) continue;
                    string id = t.id;
                    if (id == TraitManager.TRAIT_ZHUANGYUAN) return "<color=#FFD700>状元</color>";
                    if (id == TraitManager.TRAIT_JINSHI) return "<color=#70d468>进士</color>";
                    if (id == TraitManager.TRAIT_WUJINSHI) return "<color=#6ca3fd>武进士</color>";
                    if (id == TraitManager.TRAIT_JUREN) return "<color=#f0c060>举人</color>";
                    if (id == TraitManager.TRAIT_WUJUREN) return "<color=#6ca3fd>武举人</color>";
                    if (id == TraitManager.TRAIT_XIUCAI) return "<color=#8a8578>秀才</color>";
                    if (id == TraitManager.TRAIT_WUXIUCAI) return "<color=#6ca3fd>武秀才</color>";
                }
                return "<color=#6a6458>—</color>";
            }
            catch { return ""; }
        }

        /// <summary>阶段2J：君主风评徽章（特质→彩色标签）。</summary>
        private static string BuildMonarchReputation(Actor king)
        {
            try
            {
                if (king == null || king.traits == null) return "";
                var tags = new System.Collections.Generic.List<string>();
                foreach (var t in king.traits)
                {
                    if (t == null) continue;
                    string id = t.id;
                    if (id == TraitManager.TRAIT_BENEVOLENT) tags.Add("<color=#70d468>仁君</color>");
                    else if (id == TraitManager.TRAIT_TYRANT) tags.Add("<color=#e0554a>暴君</color>");
                    else if (id == TraitManager.TRAIT_FOOLISH) tags.Add("<color=#e0554a>昏君</color>");
                    else if (id == TraitManager.TRAIT_FOUNDER) tags.Add("<color=#FFD700>开国</color>");
                    else if (id == TraitManager.TRAIT_AMBITIOUS) tags.Add("<color=#f0c060>野心</color>");
                    else if (id == TraitManager.TRAIT_GENIUS) tags.Add("<color=#6ca3fd>天才</color>");
                    else if (id == TraitManager.TRAIT_MEDIOCRE) tags.Add("<color=#8a8578>平庸</color>");
                    else if (id == TraitManager.TRAIT_CORRUPT) tags.Add("<color=#aa7744>贪腐</color>");
                }
                if (tags.Count == 0) return "<color=#6a6458>—</color>";
                string result = "";
                int n = Mathf.Min(2, tags.Count);
                for (int i = 0; i < n; i++) result += tags[i] + " ";
                return result;
            }
            catch { return ""; }
        }

        /// <summary>阶段2J：行状态徽章（战争/附庸/厌战/权臣——国家名后内联，最多 2 个）。</summary>
        private static string BuildRankBadges(Kingdom k, string kId)
        {
            try
            {
                var badges = new System.Collections.Generic.List<string>();
                // 战争数
                int wars = 0;
                try { var ws = k.getWars(); if (ws != null) { foreach (var _ in ws) wars++; } } catch { }
                if (wars > 0) badges.Add($"<size=8><color=#e0554a>战{wars}</color></size>");
                // 附庸数（遍历其他国查 IsVassalPair——只数"k 是宗主"的）
                int vassals = 0;
                try
                {
                    if (World.world?.kingdoms?.list != null)
                    {
                        foreach (Kingdom o in World.world.kingdoms.list)
                        {
                            if (o == null || o == k || !o.isAlive()) continue;
                            if (!VassalManager.Instance.IsVassalPair(k.id.ToString(), o.id.ToString())) continue;
                            // o 是附庸且宗主是 k
                            try
                            {
                                var suz = VassalManager.Instance.GetSuzerain(o);
                                if (suz != null && suz.id == k.id) vassals++;
                            }
                            catch { }
                        }
                    }
                }
                catch { }
                if (vassals > 0) badges.Add($"<size=8><color={UI.ZhuluUIStyle.Hex.Positive}>附{vassals}</color></size>");
                // 厌战高
                try
                {
                    int ex = ZhuluWarService.GetWarExhaustion(kId);
                    if (ex >= 60) badges.Add("<size=8><color=#f0c060>厌战</color></size>");
                }
                catch { }
                // 权臣（任一官 Power≥70）
                try
                {
                    var recs = OfficeManager.Instance.GetRecordsPublic(kId);
                    if (recs != null)
                    {
                        foreach (var rec in recs)
                        {
                            if (rec == null || string.IsNullOrEmpty(rec.PersonId)) continue;
                            float p = CorruptionManager.Instance.GetPower(kId, rec.OfficeId);
                            if (p >= 70f) { badges.Add("<size=8><color=#6ca3fd>权臣</color></size>"); break; }
                        }
                    }
                }
                catch { }
                // 最多 2 个（防国家名区溢出）
                string result = "";
                int n = Mathf.Min(2, badges.Count);
                for (int i = 0; i < n; i++) result += " " + badges[i];
                return result;
            }
            catch { return ""; }
        }

        /// <summary>表格行：档位标签 | 国家 | 君主 | 城 | 兵 | 稳定色点</summary>
        private void CreateRankRow((Kingdom k, string kId, int tierOrder, int cities, int army, string kingName, string tierName, Color tierColor, Color tierBg, float stab, bool isTm) e, int index)
        {
            GameObject rowObj = new GameObject($"RankRow_{index}", typeof(RectTransform), typeof(Image), typeof(Button), typeof(HorizontalLayoutGroup));
            rowObj.transform.SetParent(_contentTransform, false);

            RectTransform rowRect = rowObj.GetComponent<RectTransform>();
            rowRect.sizeDelta = new Vector2(_innerWidth, 26f);

            Image rowImg = rowObj.GetComponent<Image>();
            rowImg.color = e.isTm ? new Color(0.22f, 0.16f, 0.07f, 0.6f)
                : (index % 2 == 0 ? new Color(0.12f, 0.12f, 0.16f, 0.5f) : new Color(0.08f, 0.08f, 0.12f, 0.5f));

            Button rowBtn = rowObj.GetComponent<Button>();
            string targetId = e.kId;
            rowBtn.onClick.AddListener(() =>
            {
                StateEntrySource = "overview";   // 从天下总览内部进入国势
                UiRouter.Instance.NavigateTo(RouteDestination.KingdomState, kingdomId: targetId);
                RebuildUI();
            });

            HorizontalLayoutGroup layout = rowObj.GetComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(2, 2, 2, 2);
            layout.spacing = 2f;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = false;

            // 2026-08-19 串联·方案6.3：档位色 3px 左边条（视觉锚点——一眼分档）
            GameObject edgeObj = new GameObject("TierEdge", typeof(RectTransform), typeof(Image));
            edgeObj.transform.SetParent(rowObj.transform, false);
            edgeObj.GetComponent<RectTransform>().sizeDelta = new Vector2(3f, 22f);
            Image edgeImg = edgeObj.GetComponent<Image>();
            edgeImg.color = e.tierColor;
            edgeImg.raycastTarget = false;

            // 档位标签（Sliced 贴图自带边框描边效果）
            GameObject tierObj = new GameObject("Tier", typeof(RectTransform), typeof(Image));
            tierObj.transform.SetParent(rowObj.transform, false);
            tierObj.GetComponent<RectTransform>().sizeDelta = new Vector2(60f, 16f);
            Image tierImg = tierObj.GetComponent<Image>();
            tierImg.color = e.tierBg;
            tierImg.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            tierImg.type = Image.Type.Sliced;
            Text tierText = CreateText(tierObj.transform, e.tierName, 8);
            tierText.alignment = TextAnchor.MiddleCenter;
            tierText.color = e.tierColor;
            tierText.resizeTextForBestFit = true;
            tierText.resizeTextMinSize = 6;
            tierText.resizeTextMaxSize = 8;
            RectTransform ttRect = tierText.GetComponent<RectTransform>();
            ttRect.anchorMin = Vector2.zero;
            ttRect.anchorMax = Vector2.one;
            ttRect.sizeDelta = Vector2.zero;

            // 国家（阶段2J：后内联状态徽章；2026-08-19 串联·方案6.3：天命国★前缀 + 附庸国缩进显示宗主名）
            string starPrefix = e.isTm ? "<color=#FFD700>★</color>" : "";
            string indent = "";
            string suzTag = "";
            try
            {
                var suz = VassalManager.Instance.GetSuzerain(e.k);
                if (suz != null && suz.isAlive() && suz.id != e.k.id)
                {
                    indent = "　";   // 全角空格缩进（附庸国层级感）
                    string suzName = suz.name ?? "";
                    if (suzName.Length > 4) suzName = suzName.Substring(0, 4);
                    suzTag = $"<size=7><color=#8a8578>↳{suzName}</color></size>";
                }
            }
            catch { }
            Text nameText = CreateText(rowObj.transform, $"{starPrefix}{indent}<b>{e.k.name}</b>{suzTag}{BuildRankBadges(e.k, e.kId)}", 10);
            nameText.alignment = TextAnchor.MiddleLeft;
            nameText.color = e.isTm ? new Color(1f, 0.85f, 0.4f) : Color.white;
            nameText.GetComponent<RectTransform>().sizeDelta = new Vector2(76f, 20f);
            nameText.resizeTextForBestFit = true;
            nameText.resizeTextMinSize = 6;
            nameText.resizeTextMaxSize = 10;

            // 君主
            Text kingText = CreateText(rowObj.transform, e.kingName, 9);
            kingText.alignment = TextAnchor.MiddleCenter;
            kingText.color = new Color(0.8f, 0.8f, 0.8f);
            kingText.GetComponent<RectTransform>().sizeDelta = new Vector2(50f, 18f);
            kingText.resizeTextForBestFit = true;
            kingText.resizeTextMinSize = 6;
            kingText.resizeTextMaxSize = 9;

            // 城（2026-08-19 串联·方案6.3：趋势箭头——对比上年基线，城/兵变化一目了然）
            string trend = GetTrendArrow(e.k, e.cities, e.army);
            Text cityText = CreateText(rowObj.transform, $"{e.cities}{trend}", 9);
            cityText.alignment = TextAnchor.MiddleCenter;
            cityText.color = new Color(0.53f, 0.8f, 1f);
            cityText.GetComponent<RectTransform>().sizeDelta = new Vector2(28f, 18f);

            // 兵
            Text armyText = CreateText(rowObj.transform, e.army.ToString(), 9);
            armyText.alignment = TextAnchor.MiddleCenter;
            armyText.color = new Color(0.53f, 1f, 0.53f);
            armyText.GetComponent<RectTransform>().sizeDelta = new Vector2(36f, 18f);

            // 稳定度色点（纯色 Image——无 sprite 用内置白纹理，颜色鲜艳；加白描边突出）
            // 阶段2J：色点包 28px 容器（MiddleCenter——与表头"稳"列对齐，防偏右）
            GameObject stabWrap = new GameObject("StabWrap", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            stabWrap.transform.SetParent(rowObj.transform, false);
            stabWrap.GetComponent<RectTransform>().sizeDelta = new Vector2(26f, 18f);
            HorizontalLayoutGroup stabLayout = stabWrap.GetComponent<HorizontalLayoutGroup>();
            stabLayout.childAlignment = TextAnchor.MiddleCenter;
            stabLayout.childControlWidth = false;
            stabLayout.childControlHeight = false;
            stabLayout.childForceExpandWidth = false;
            GameObject stabObj = new GameObject("StabDot", typeof(RectTransform), typeof(Image));
            stabObj.transform.SetParent(stabWrap.transform, false);
            stabObj.GetComponent<RectTransform>().sizeDelta = new Vector2(11f, 11f);
            Image stabImg = stabObj.GetComponent<Image>();
            stabImg.color = GetStabilityDotColor(e.stab);
            // 注意：不能设 backgroundKingdomElement 贴图——深色贴图会把颜色压暗
            var stabOutline = stabObj.AddComponent<Outline>();
            stabOutline.effectColor = new Color(1f, 1f, 1f, 0.35f);
            stabOutline.effectDistance = new Vector2(1f, -1f);
            stabImg.raycastTarget = false;
        }

        /// <summary>
        /// 2026-08-19 串联·方案6.3：国势榜趋势箭头（城/兵对比上年基线）。
        /// 基线按年存 custom_data（跨年首刷更新——同年内重复开窗不覆盖，趋势对比本年首开快照）。
        /// ▲=拓城或增兵显著 · ▼=失城或损兵显著 · ·=持平/无基线。
        /// </summary>
        private static string GetTrendArrow(Kingdom k, int cities, int army)
        {
            try
            {
                if (k == null) return "";
                int year = ZhuluTime.CurrentYear;
                long lastYear = KingdomDataExt.GetLong(k, "overview_snap_year", 0);
                long lastCities = KingdomDataExt.GetLong(k, "overview_snap_cities", 0);
                long lastArmy = KingdomDataExt.GetLong(k, "overview_snap_army", 0);
                string arrow = "<color=#8a8578>·</color>";
                if (lastYear > 0 && lastCities >= 0 && lastYear != year)
                {
                    int cityDelta = cities - (int)lastCities;
                    int armyDelta = army - (int)lastArmy;
                    if (cityDelta > 0 || armyDelta > 100) arrow = "<color=#70d468>▲</color>";
                    else if (cityDelta < 0 || armyDelta < -100) arrow = "<color=#e0554a>▼</color>";
                }
                if (lastYear != year)
                {
                    KingdomDataExt.SetLong(k, "overview_snap_year", year);
                    KingdomDataExt.SetLong(k, "overview_snap_cities", cities);
                    KingdomDataExt.SetLong(k, "overview_snap_army", army);
                }
                return arrow;
            }
            catch { return ""; }
        }

        private static Color GetStabilityDotColor(float val)
        {
            if (val >= 80f) return new Color(1f, 0.84f, 0f);
            if (val >= 60f) return new Color(0.56f, 0.93f, 0.56f);
            if (val >= 40f) return new Color(0.87f, 0.82f, 0.38f);
            if (val >= 20f) return new Color(1f, 0.55f, 0f);
            return new Color(1f, 0.27f, 0f);
        }

        /// <summary>通用表格表头（列名+宽度；宽 0 表示占剩余）</summary>
        private void CreateRankTableHeader((string label, float width)[] cols)
        {
            GameObject headerObj = new GameObject("TableHeader", typeof(RectTransform), typeof(Image), typeof(HorizontalLayoutGroup));
            headerObj.transform.SetParent(_contentTransform, false);

            RectTransform rect = headerObj.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(_innerWidth, 18f);

            headerObj.GetComponent<Image>().color = new Color(0.15f, 0.15f, 0.22f, 0.95f);

            HorizontalLayoutGroup layout = headerObj.GetComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(2, 2, 2, 2);
            layout.spacing = 2f;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = false;

            float fixedW = 0f;
            int flexCount = 0;
            foreach (var c in cols) { if (c.width > 0f) fixedW += c.width; else flexCount++; }
            float flexW = Mathf.Max(30f, (_innerWidth - 16f - fixedW - cols.Length * 2f) / Mathf.Max(1, flexCount));

            int colIdx = 0;
            foreach (var c in cols)
            {
                // 阶段2J：城/兵/稳列（索引 3/4/5）可点击排序（▲▼ 标记当前排序列）
                bool sortable = _rankSortEnabled && (colIdx == 3 || colIdx == 4 || colIdx == 5);
                string label = c.label;
                if (_rankSortEnabled && colIdx == _rankSortColumn)
                    label += _rankSortAsc ? " ▲" : " ▼";
                // 阶段2J：所有列统一深色底（当前排序列=金色高亮；可排序=可点；不可排序=同款深底）
                GameObject colObj = new GameObject("HeaderCol", typeof(RectTransform), typeof(Image));
                colObj.transform.SetParent(headerObj.transform, false);
                colObj.GetComponent<RectTransform>().sizeDelta = new Vector2(c.width > 0f ? c.width : flexW, 14f);
                Image colImg = colObj.GetComponent<Image>();
                if (_rankSortEnabled && colIdx == _rankSortColumn)
                    colImg.color = new Color(0.30f, 0.24f, 0.10f, 0.95f);      // 当前排序列：金色底
                else if (sortable)
                    colImg.color = new Color(0.22f, 0.24f, 0.30f, 0.9f);      // 可排序列：深蓝底
                else
                    colImg.color = new Color(0.12f, 0.13f, 0.18f, 0.9f);      // 不可排序列：同款深底（不再白底）
                colImg.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
                colImg.type = Image.Type.Sliced;
                if (sortable)
                {
                    var btn = colObj.AddComponent<Button>();
                    int capture = colIdx;
                    btn.onClick.AddListener(() =>
                    {
                        if (_rankSortColumn == capture) _rankSortAsc = !_rankSortAsc;
                        else { _rankSortColumn = capture; _rankSortAsc = true; }
                        RebuildUI();
                    });
                }
                Text t = CreateText(colObj.transform, label, 8);
                t.alignment = TextAnchor.MiddleCenter;
                t.color = (_rankSortEnabled && colIdx == _rankSortColumn)
                    ? new Color(1f, 0.85f, 0.4f)
                    : new Color(0.75f, 0.75f, 0.75f);
                RectTransform tr = t.GetComponent<RectTransform>();
                tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one;
                tr.offsetMin = Vector2.zero; tr.offsetMax = Vector2.zero;
                colIdx++;
            }
        }

        /// <summary>君主榜：各国君主按丞相公式评分排序</summary>
        private void CreateMonarchBoard()
        {
            _rankSortEnabled = false; // 名君榜不启用列排序（保持按评分）
            CreateRankTableHeader(new[] { ("排名", 28f), ("君主", 76f), ("国家", 52f), ("在位", 34f), ("评分", 40f), ("风评", 58f) });

            var entries = new List<(string kingName, string kName, int tenure, int score, bool isTm, Actor king)>();
            if (World.world?.kingdoms?.list != null)
            {
                int year = ZhuluTime.CurrentYear;
                foreach (Kingdom k in World.world.kingdoms.list)
                {
                    if (k == null || !k.isAlive() || k.king == null) continue;
                    try
                    {
                        // 逐国 try-catch：单个国家异常不影响其他国
                        string kId = k.id.ToString();
                        Actor king = k.king;
                        string kingName = king.getName() ?? king.name ?? "无名君";
                        long reignStart = KingdomDataExt.GetLong(k, "reign_start", year);
                        int tenure = Mathf.Max(0, year - (int)reignStart);
                        int kingAge = 0; try { kingAge = king.getAge(); } catch { }
                        var (score, _) = OfficialRatingManager.CalcOfficialScore(
                            king.intelligence, king.stewardship, king.warfare, king.diplomacy, king.level, 6, kingAge);
                        bool isTm = PolicyManager.IsMandateHolderForKingdom(k);
                        entries.Add((kingName, k.name, tenure, score, isTm, king));
                    }
                    catch { }
                }
            }

            entries.Sort((a, b) => b.score.CompareTo(a.score));

            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                GameObject rowObj = new GameObject($"MonarchRow_{i}", typeof(RectTransform), typeof(Image), typeof(Button), typeof(HorizontalLayoutGroup));
                rowObj.transform.SetParent(_contentTransform, false);
                rowObj.GetComponent<RectTransform>().sizeDelta = new Vector2(_innerWidth, 26f);

                Image rowImg = rowObj.GetComponent<Image>();
                rowImg.color = e.isTm ? new Color(0.22f, 0.16f, 0.07f, 0.6f)
                    : (i % 2 == 0 ? new Color(0.12f, 0.12f, 0.16f, 0.5f) : new Color(0.08f, 0.08f, 0.12f, 0.5f));

                Actor capturedKing = e.king;
                rowObj.GetComponent<Button>().onClick.AddListener(() =>
                {
                    if (capturedKing != null && capturedKing.isAlive())
                    {
                        MetaTypeLibrary.unit.selectAndInspect(capturedKing, false, true, true);
                        ScrollWindow.showWindow(MetaTypeLibrary.unit.window_name);
                    }
                });

                HorizontalLayoutGroup layout = rowObj.GetComponent<HorizontalLayoutGroup>();
                layout.padding = new RectOffset(2, 2, 2, 2);
                layout.spacing = 2f;
                layout.childAlignment = TextAnchor.MiddleLeft;
                layout.childControlWidth = false;
                layout.childControlHeight = false;
                layout.childForceExpandWidth = false;

                string rankColor = i == 0 ? "#FFD700" : i == 1 ? "#C0C0C0" : i == 2 ? "#CD7F32" : "#888888";
                Text rankText = CreateText(rowObj.transform, $"<b><color={rankColor}>#{i + 1}</color></b>", 9);
                rankText.alignment = TextAnchor.MiddleCenter;
                rankText.GetComponent<RectTransform>().sizeDelta = new Vector2(28f, 18f);

                Text nameText = CreateText(rowObj.transform, $"<b>{e.kingName}</b>", 10);
                nameText.alignment = TextAnchor.MiddleLeft;
                nameText.color = Color.white;
                nameText.GetComponent<RectTransform>().sizeDelta = new Vector2(76f, 20f);
                // 批次7美化B6根因：共享CreateText默认Overflow使BestFit永不触发——长名(札萨克图·巴雅尔)
                // 原字号溢出76px盒压到国名列。改Wrap让BestFit生效（放不下自动缩号）。
                nameText.horizontalOverflow = HorizontalWrapMode.Wrap;
                nameText.resizeTextForBestFit = true;
                nameText.resizeTextMinSize = 6;
                nameText.resizeTextMaxSize = 10;

                Text kText = CreateText(rowObj.transform, e.kName, 9);
                kText.alignment = TextAnchor.MiddleCenter;
                kText.color = e.isTm ? new Color(1f, 0.8f, 0.4f) : new Color(0.8f, 0.8f, 0.8f);
                kText.GetComponent<RectTransform>().sizeDelta = new Vector2(52f, 18f);
                kText.horizontalOverflow = HorizontalWrapMode.Wrap;
                kText.resizeTextForBestFit = true;
                kText.resizeTextMinSize = 6;
                kText.resizeTextMaxSize = 9;

                Text tenureText = CreateText(rowObj.transform, $"{e.tenure}年", 9);
                tenureText.alignment = TextAnchor.MiddleCenter;
                tenureText.color = new Color(0.7f, 0.7f, 0.7f);
                tenureText.GetComponent<RectTransform>().sizeDelta = new Vector2(34f, 18f);

                Text scoreText = CreateText(rowObj.transform, $"<b><color=#FFCC00>{e.score}</color></b>", 10);
                scoreText.alignment = TextAnchor.MiddleCenter;
                scoreText.GetComponent<RectTransform>().sizeDelta = new Vector2(40f, 20f);

                // 阶段2J：风评徽章（君主特质——仁君/暴君/昏君/开国之主/野心家/天才/平庸/贪腐）
                string rep = BuildMonarchReputation(e.king);
                Text repText = CreateText(rowObj.transform, rep, 8);
                repText.alignment = TextAnchor.MiddleLeft;
                repText.GetComponent<RectTransform>().sizeDelta = new Vector2(58f, 18f);
                repText.horizontalOverflow = HorizontalWrapMode.Wrap;
                repText.resizeTextForBestFit = true;
                repText.resizeTextMinSize = 6;
                repText.resizeTextMaxSize = 8;
            }

            if (entries.Count == 0)
            {
                CreateText(_contentTransform, "<color=#888888>天下无君主。</color>", 9);
            }
        }

        private int _buildGeneration = 0;

        private void CancelTalentBuild()
        {
            _buildGeneration++;
            if (_talentBuildCoroutine != null)
            {
                StopCoroutine(_talentBuildCoroutine);
                _talentBuildCoroutine = null;
            }
            if (_talentLoadingObject != null)
            {
                Destroy(_talentLoadingObject);
                _talentLoadingObject = null;
            }
        }

        /// <summary>
        /// 贤才榜/文臣榜/武将榜：分帧扫描并评分，切 Tab/关窗时代际中断。
        /// 2026-08-18 重构：mode 0=贤才榜（全图国民，排除君主——君主乱串修复）
        /// mode 1=文臣榜（在职官员，丞相全能权重）mode 2=武将榜（在职官员，兵部军事权重）。
        /// </summary>
        private System.Collections.IEnumerator BuildTalentBoardCoroutine(int mode)
        {
            bool isOfficialBoard = mode != 0;
            string boardName = mode == 1 ? "文臣榜" : mode == 2 ? "武将榜" : "贤才榜";
            int generation = ++_buildGeneration;
            Text loadingText = CreateText(_contentTransform, $"<color={UI.ZhuluUIStyle.Hex.Muted}>{boardName}构建中...</color>", 9);
            _talentLoadingObject = loadingText != null ? loadingText.gameObject : null;
            loadingText.GetComponent<RectTransform>().sizeDelta = new Vector2(_innerWidth, 20f);

            // 1. 收集候选：贤才榜=全图国民（去君主）；文臣/武将榜=在职官员（中央+地方+城主）
            var candidates = new List<(Actor actor, string kName, string office)>();
            var officeByPerson = new Dictionary<string, string>();
            try
            {
                if (World.world?.kingdoms?.list != null)
                {
                    foreach (Kingdom k in World.world.kingdoms.list)
                    {
                        if (k == null || !k.isAlive()) continue;
                        string kId = k.id.ToString();
                        long kingId = 0;
                        try { if (k.king != null) kingId = k.king.getID(); } catch { }

                        // 官职记录（三榜共用：贤才榜显示官职列；文臣/武将榜为候选来源）
                        var records = OfficeManager.Instance.GetRecordsPublic(kId);
                        if (records != null)
                        {
                            foreach (var rec in records)
                            {
                                if (rec == null || string.IsNullOrEmpty(rec.PersonId)) continue;
                                if (!officeByPerson.ContainsKey(rec.PersonId))
                                {
                                    officeByPerson[rec.PersonId] = OfficeManager.GetOfficeTitleForKingdom(rec.OfficeId, k);
                                }
                                if (isOfficialBoard && long.TryParse(rec.PersonId, out long opid))
                                {
                                    var oa = World.world?.units?.get(opid);
                                    if (oa == null || !oa.isAlive()) continue;
                                    // 2026-10-06 文武分流（用户实锤武进士/武官混入文臣榜）：
                                    // 与 OfficeManager 武官判定同口径（兵部席 或 武将身份——含武举出身/队长城主）
                                    bool isMil = rec.OfficeId == "military"
                                        || IdentityClassifier.Instance.GetIdentity(opid) == IdentityClassifier.IdentityType.Military;
                                    if (mode == 1 && isMil) continue;   // 文臣榜不收武人
                                    if (mode == 2 && !isMil) continue;  // 武将榜不收文官
                                    candidates.Add((oa, k.name ?? "", officeByPerson[rec.PersonId]));
                                }
                            }
                        }

                        if (isOfficialBoard)
                        {
                            // 城主（食邑在职——武将榜含平乱守土之城主；文臣榜略过纯武职城主不细分，统一参评）
                            if (k.cities != null)
                            {
                                foreach (var c in k.cities)
                                {
                                    try
                                    {
                                        if (c?.leader == null || !c.leader.isAlive()) continue;
                                        if (c.leader.getID() == kingId) continue;
                                        var exists = false;
                                        foreach (var cand in candidates)
                                        {
                                            if (cand.actor == c.leader) { exists = true; break; }
                                        }
                                        if (!exists) candidates.Add((c.leader, k.name ?? "", "城主"));
                                    }
                                    catch { }
                                }
                            }
                            continue;
                        }

                        // 贤才榜：全图国民（排除君主——君主不入贤才榜）
                        if (k.units == null) continue;
                        foreach (Actor unit in k.units)
                        {
                            if (unit == null || !unit.isAlive()) continue;
                            if (kingId != 0 && unit.getID() == kingId) continue;
                            candidates.Add((unit, k.name, ""));
                        }
                    }
                }
            }
            catch { }

            if (generation != _buildGeneration) yield break;

            // 2. 分帧评分（每帧 150 人；三榜专用权重体系——2026-08-18 重构）
            var scored = new List<(Actor actor, string name, string kName, string office, int score)>();
            for (int i = 0; i < candidates.Count; i++)
            {
                if (generation != _buildGeneration) yield break;
                var c = candidates[i];
                try
                {
                    Actor a = c.actor;
                    int aAge3 = 0; try { aAge3 = a.getAge(); } catch { }
                    int kills = OfficialRatingManager.GetActorKills(a);
                    float dmg = OfficialRatingManager.GetActorDamage(a);
                    int score = BoardScore(mode, a.intelligence, a.stewardship, a.warfare, a.diplomacy, a.level, aAge3, kills, dmg);
                    string name = a.getName() ?? a.name ?? "某人";
                    string pid = a.getID().ToString();
                    string office = !string.IsNullOrEmpty(c.office) ? c.office
                        : (officeByPerson.TryGetValue(pid, out var off) ? off : "");
                    scored.Add((a, name, c.kName, office, score));
                }
                catch { }

                if (i % 150 == 149) yield return null;
            }

            if (generation != _buildGeneration) yield break;

            // 3. 排序取前 50
            scored.Sort((a, b) => b.score.CompareTo(a.score));
            int count = Mathf.Min(50, scored.Count);

            // 4. 构建表格
            if (loadingText != null) Destroy(loadingText.gameObject);
            _talentLoadingObject = null;
            if (generation != _buildGeneration) yield break;

            string headName = mode == 1 ? "名臣" : mode == 2 ? "名将" : "贤才";
            CreateRankTableHeader(new[] { ("排名", 28f), (headName, 60f), ("功名", 46f), ("国家", 42f), ("官职", 54f), ("评分", 38f) });

            for (int i = 0; i < count; i++)
            {
                var e = scored[i];
                GameObject rowObj = new GameObject($"TalentRow_{i}", typeof(RectTransform), typeof(Image), typeof(Button), typeof(HorizontalLayoutGroup));
                rowObj.transform.SetParent(_contentTransform, false);
                rowObj.GetComponent<RectTransform>().sizeDelta = new Vector2(_innerWidth, 26f);

                Image rowImg = rowObj.GetComponent<Image>();
                rowImg.color = (i % 2 == 0 ? new Color(0.12f, 0.12f, 0.16f, 0.5f) : new Color(0.08f, 0.08f, 0.12f, 0.5f));

                Actor capturedActor = e.actor;
                rowObj.GetComponent<Button>().onClick.AddListener(() =>
                {
                    if (capturedActor != null && capturedActor.isAlive())
                    {
                        MetaTypeLibrary.unit.selectAndInspect(capturedActor, false, true, true);
                        ScrollWindow.showWindow(MetaTypeLibrary.unit.window_name);
                    }
                });

                HorizontalLayoutGroup layout = rowObj.GetComponent<HorizontalLayoutGroup>();
                layout.padding = new RectOffset(2, 2, 2, 2);
                layout.spacing = 2f;
                layout.childAlignment = TextAnchor.MiddleLeft;
                layout.childControlWidth = false;
                layout.childControlHeight = false;
                layout.childForceExpandWidth = false;

                string rankColor = i == 0 ? "#FFD700" : i == 1 ? "#C0C0C0" : i == 2 ? "#CD7F32" : "#888888";
                Text rankText = CreateText(rowObj.transform, $"<b><color={rankColor}>#{i + 1}</color></b>", 9);
                rankText.alignment = TextAnchor.MiddleCenter;
                rankText.GetComponent<RectTransform>().sizeDelta = new Vector2(28f, 18f);

                Text nameText = CreateText(rowObj.transform, $"<b>{e.name}</b>", 10);
                nameText.alignment = TextAnchor.MiddleLeft;
                nameText.color = Color.white;
                nameText.GetComponent<RectTransform>().sizeDelta = new Vector2(62f, 20f);
                nameText.resizeTextForBestFit = true;
                nameText.resizeTextMinSize = 6;
                nameText.resizeTextMaxSize = 10;

                // 阶段2J：功名徽章（状元/进士/举人/秀才）
                string gongming = BuildGongmingBadge(e.actor);
                Text gmText = CreateText(rowObj.transform, gongming, 8);
                gmText.alignment = TextAnchor.MiddleCenter;
                gmText.GetComponent<RectTransform>().sizeDelta = new Vector2(56f, 18f);
                gmText.resizeTextForBestFit = true;
                gmText.resizeTextMinSize = 6;
                gmText.resizeTextMaxSize = 8;

                Text kText = CreateText(rowObj.transform, e.kName, 9);
                kText.alignment = TextAnchor.MiddleCenter;
                kText.color = new Color(0.8f, 0.8f, 0.8f);
                kText.GetComponent<RectTransform>().sizeDelta = new Vector2(48f, 18f);
                kText.resizeTextForBestFit = true;
                kText.resizeTextMinSize = 6;
                kText.resizeTextMaxSize = 9;

                // 官职标签（无官显示 —）
                GameObject officeObj = new GameObject("Office", typeof(RectTransform), typeof(Image));
                officeObj.transform.SetParent(rowObj.transform, false);
                officeObj.GetComponent<RectTransform>().sizeDelta = new Vector2(60f, 15f);
                bool hasOffice = !string.IsNullOrEmpty(e.office);
                officeObj.GetComponent<Image>().color = hasOffice ? new Color(0.23f, 0.16f, 0.1f, 1f) : new Color(0f, 0f, 0f, 0f);
                Text officeText = CreateText(officeObj.transform, hasOffice ? e.office : "—", 8);
                officeText.alignment = TextAnchor.MiddleCenter;
                officeText.color = hasOffice ? new Color(1f, 0.8f, 0.53f) : new Color(0.45f, 0.45f, 0.45f);
                officeText.resizeTextForBestFit = true;
                officeText.resizeTextMinSize = 6;
                officeText.resizeTextMaxSize = 8;
                RectTransform otRect = officeText.GetComponent<RectTransform>();
                otRect.anchorMin = Vector2.zero;
                otRect.anchorMax = Vector2.one;
                otRect.sizeDelta = Vector2.zero;

                Text scoreText = CreateText(rowObj.transform, $"<b><color=#FFCC00>{e.score}</color></b>", 10);
                scoreText.alignment = TextAnchor.MiddleCenter;
                scoreText.GetComponent<RectTransform>().sizeDelta = new Vector2(38f, 20f);

                if (i % 10 == 9) yield return null;  // 分帧构建行
            }

            if (count == 0)
            {
                string hint = isOfficialBoard ? "天下尚无在职官员。" : "天下无贤才记录。";
                CreateText(_contentTransform, $"<color={UI.ZhuluUIStyle.Hex.Muted}>{hint}</color>", 9);
            }
            _talentBuildCoroutine = null;
        }

        /// <summary>
        /// 2026-08-18 三榜专用评分（与官制考评 CalcOfficialScore 解耦）：
        /// mode 0 贤才榜——四维均衡全面发展（短板惩罚：最低维减半）；
        /// mode 1 文臣榜——智力为纲（int 全重 + 组织辅 + 略外交，武力的负相关微调）；
        /// mode 2 武将榜——组织指挥为纲（stewardship 统兵 + warfare 勇武）+ 战功加分（击杀 + 伤害，与武举同口径）。
        /// 通用：等级 + 年龄黄金期（25~45 当出之年 +5 / 未冠或迟暮递减）。
        /// </summary>
        private static int BoardScore(int mode, int intel, int stew, int war, int dip, int level, int age, int kills, float damage = 0f)
        {
            float s;
            if (mode == 1)
            {
                // 文臣：智力×1.3 + 组织×0.7 + 外交×0.4 − 武力×0.05（重文轻武取士）
                // 2026-08-19 校准：存档四维普遍 3~36，原 1.0/0.55/0.3 系数致榜分偏低
                s = intel * 1.3f + stew * 0.7f + dip * 0.4f - war * 0.05f;
            }
            else if (mode == 2)
            {
                // 武将：组织统兵×1.0 + 勇武×0.85 + 外交×0.2（将才贵能统众，次论勇力）
                s = stew * 1.0f + war * 0.85f + dip * 0.2f;
                // 战功：每 8 颗首级 +1 分，封顶 +25（百人斩名震天下）+ 每 5 伤害 +1，封顶 +10
                // （2026-08-18 与武举 GetWarMeritBonus 同源口径）
                s += Mathf.Min(25f, kills / 8f);
                s += Mathf.Min(10f, damage / 5f);
            }
            else
            {
                // 贤才：四维均衡——均值×1.2 + 均衡奖励（最高最低差越小越加分，上限 8）
                float avg = (intel + stew + war + dip) * 0.25f;
                int minV = Mathf.Min(intel, Mathf.Min(stew, Mathf.Min(war, dip)));
                int maxV = Mathf.Max(intel, Mathf.Max(stew, Mathf.Max(war, dip)));
                float balance = Mathf.Max(0f, 8f - (maxV - minV) * 0.4f);
                s = avg * 1.2f + balance;
            }
            s += level * 3f;
            // 年龄黄金期：25~45 +5；16~24 或 46~60 +2；其余 +0（垂髫/迟暮不入榜）
            if (age >= 25 && age <= 45) s += 5f;
            else if (age >= 16 && age <= 60) s += 2f;
            return Mathf.Max(0, Mathf.RoundToInt(s));
        }
        #endregion

    }
}
