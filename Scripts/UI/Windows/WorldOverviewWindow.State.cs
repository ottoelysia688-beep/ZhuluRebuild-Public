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


// ── 由 WorldOverviewWindow.cs 按 region 拆出的 partial 部分：2. 王国国势全景视图 ──
// 2026-10-23 [P1-1] 纯文件级拆分，编译后仍为同一个类型，无任何行为改动。
namespace ZhuluRebuild.UI.Windows
{
    public partial class WorldOverviewWindow : MonoBehaviour
    {
        #region 2. 王国国势全景视图
        /// <summary>阶段2M：顶部状态行（城/口/兵/国力/附庸 一行速览）。</summary>
        private void CreateStateStatusLine(Kingdom k, string kingdomId)
        {
            try
            {
                if (k == null || _contentTransform == null) return;
                int cities = k.cities != null ? k.cities.Count : 0;
                int pop = 0;
                int army = 0;
                try { pop = k.units != null ? k.units.Count : 0; } catch { }
                try { army = k.countTotalWarriors(); } catch { }
                int pp = ZhuluTierService.GetPowerPoints(k);
                int vassals = 0;
                try
                {
                    if (World.world?.kingdoms?.list != null)
                    {
                        foreach (Kingdom o in World.world.kingdoms.list)
                        {
                            if (o == null || o == k || !o.isAlive()) continue;
                            if (!VassalManager.Instance.IsVassalPair(k.id.ToString(), o.id.ToString())) continue;
                            var suz = VassalManager.Instance.GetSuzerain(o);
                            if (suz != null && suz.id == k.id) vassals++;
                        }
                    }
                }
                catch { }

                GameObject bar = new GameObject("StateStatusLine", typeof(RectTransform), typeof(HorizontalLayoutGroup));
                bar.transform.SetParent(_contentTransform, false);
                bar.GetComponent<RectTransform>().sizeDelta = new Vector2(_innerWidth, 26f);
                HorizontalLayoutGroup hg = bar.GetComponent<HorizontalLayoutGroup>();
                hg.spacing = 4f; hg.padding = new RectOffset(0, 0, 2, 2);
                hg.childControlWidth = true; hg.childControlHeight = true; hg.childForceExpandWidth = true;
                // 阶段2P：+稳 格（稳定度按档配色）
                float stab = 50f;
                try { stab = StabilityManager.Instance.GetStability(kingdomId); } catch { }
                string stabHex = stab >= 70f ? "#70d468" : stab >= 40f ? "#d8c860" : "#f08080";
                CreateStatusCell(bar.transform, "城", cities.ToString(), "#8fa8c8");
                CreateStatusCell(bar.transform, "口", pop.ToString(), "#8fa8c8");
                CreateStatusCell(bar.transform, "兵", army.ToString(), "#70d468");
                CreateStatusCell(bar.transform, "国力", pp.ToString(), "#f0c060");
                CreateStatusCell(bar.transform, "附庸", vassals.ToString(), vassals > 0 ? "#70d468" : "#6a6458");
                CreateStatusCell(bar.transform, "稳", stab.ToString("F0"), stabHex);
            }
            catch { }
        }

        private void CreateStatusCell(Transform parent, string label, string value, string colorHex)
        {
            GameObject cell = new GameObject("StatusCell", typeof(RectTransform), typeof(Image));
            cell.transform.SetParent(parent, false);
            Image img = cell.GetComponent<Image>();
            img.color = new Color(0.10f, 0.14f, 0.20f, 0.85f);
            img.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            img.type = Image.Type.Sliced;
            Text t = CreateText(cell.transform, $"<b><color={colorHex}>{value}</color></b>\n<size=7><color=#8a8578>{label}</color></size>", 9);
            t.alignment = TextAnchor.MiddleCenter;
            RectTransform tr = t.GetComponent<RectTransform>();
            tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one;
            tr.offsetMin = Vector2.zero; tr.offsetMax = Vector2.zero;
        }

        /// <summary>阶段2M：国力晋升进度卡（国力点/距下等差/晋升条件——核心）。</summary>
        private void CreateGuoliProgressCard(Kingdom k, string kingdomId, bool isTm)
        {
            try
            {
                if (k == null || _contentTransform == null) return;
                int pp = ZhuluTierService.GetPowerPoints(k);
                bool nomadic = KingdomCivilizationService.Instance.IsNomadic(k);
                string tierId = nomadic ? NomadicTierService.GetTierId(pp) : ZhuluTierService.GetTierId(pp);
                // 阶段2W：叛军无位阶（草莽僭伪——用户实报"叛军体制没有"，此处国势卡漏接）
                bool isRebel = ZhuluRebelService.IsRebel(k);
                string tierName = isRebel ? ZhuluRebelService.REBEL_TIER_NAME
                    : nomadic ? NomadicTierService.GetTierName(tierId) : ZhuluTierService.GetTierName(tierId);
                Transform card = CreateOverviewCard(52f, isTm);

                string status;
                string statusColor;
                if (isRebel)
                {
                    status = "揭竿而起 · 未建章立制";
                    statusColor = "#f73939";
                }
                else if (isTm)
                {
                    status = nomadic ? "长生天眷 · 王庭会盟" : "天命正统 · 万国来朝";
                    statusColor = "#FFD700";
                }
                else
                {
                    // 下一等阈值
                    int nextThreshold = pp < 10 ? 10 : pp < 20 ? 20 : pp < 35 ? 35 : pp < 55 ? 55 : int.MaxValue;
                    if (nextThreshold == int.MaxValue)
                    {
                        status = nomadic ? "已是大汗庭 · 汗威≥8 可争长生天眷" : "已是公国 · 礼治≥8 可争天命";
                        statusColor = "#b58ae8";
                    }
                    else
                    {
                        int diff = nextThreshold - pp;
                        status = $"距晋【{TierNameOf(nextThreshold, nomadic)}】差 {diff}";
                        statusColor = diff <= 3 ? "#f0c060" : "#70d468";
                    }
                }
                Text title = CreateText(card, $"<color=#9aa0ac>⚖ 国力</color> <color={UI.ZhuluUIStyle.Hex.Warn}><b>{pp}</b></color> <color={statusColor}>{status}</color>", 9);
                title.alignment = TextAnchor.MiddleLeft;
                RectTransform tRect = title.GetComponent<RectTransform>();
                tRect.anchorMin = new Vector2(0, 1); tRect.anchorMax = new Vector2(0, 1);
                tRect.pivot = new Vector2(0, 1);
                tRect.anchoredPosition = new Vector2(10, -6);
                tRect.sizeDelta = new Vector2(_innerWidth - 20, 18f);

                // 进度条（当前/下一等——简化：按 0-55 满档）
                GameObject prog = new GameObject("GuoliProg", typeof(RectTransform), typeof(Image));
                prog.transform.SetParent(card, false);
                RectTransform pRect = prog.GetComponent<RectTransform>();
                pRect.anchorMin = new Vector2(0, 1); pRect.anchorMax = new Vector2(0, 1);
                pRect.pivot = new Vector2(0, 1);
                pRect.anchoredPosition = new Vector2(10, -27);
                pRect.sizeDelta = new Vector2(_innerWidth - 20, 5f);
                Image progBg = prog.GetComponent<Image>();
                progBg.color = new Color(0.1f, 0.13f, 0.2f, 1f);
                float ratio = Mathf.Clamp01(pp / 55f);
                GameObject fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
                fill.transform.SetParent(prog.transform, false);
                RectTransform fr = fill.GetComponent<RectTransform>();
                fr.anchorMin = Vector2.zero; fr.anchorMax = new Vector2(ratio, 1f);
                fr.offsetMin = Vector2.zero; fr.offsetMax = Vector2.zero;
                fill.GetComponent<Image>().color = isTm ? new Color(1f, 0.84f, 0.4f, 0.9f) : new Color(0.4f, 0.7f, 1f, 0.9f);

                // 阶段2P：爵位阈值刻度线（10/20/35/55——晋升线可视化；未达标橙亮、已过半透）
                try
                {
                    float barW = _innerWidth - 20;
                    int[] thresholds = { 10, 20, 35, 55 };
                    foreach (int th in thresholds)
                    {
                        GameObject tick = new GameObject("Tick" + th, typeof(RectTransform), typeof(Image));
                        tick.transform.SetParent(prog.transform, false);
                        var tkRect = tick.GetComponent<RectTransform>();
                        tkRect.anchorMin = Vector2.zero;
                        tkRect.anchorMax = Vector2.zero;
                        tkRect.pivot = new Vector2(0.5f, 0.5f);
                        tkRect.anchoredPosition = new Vector2(barW * (th / 55f), 2.5f);
                        tkRect.sizeDelta = new Vector2(1.5f, 5f);
                        tick.GetComponent<Image>().color = pp >= th
                            ? new Color(1f, 1f, 1f, 0.25f)
                            : new Color(1f, 0.8f, 0.4f, 0.6f);
                    }
                }
                catch { }

                // 晋升条件提示
                if (!isTm)
                {
                    string cond = pp < 10 ? "再取 1 城 或 增 40 口" : pp < 20 ? "再取 1 城 或 增 120 口" : pp < 35 ? "再取 2 城 或 增 360 口" : pp < 55 ? "再取 3 城 或 增 520 口" : nomadic ? "汗威 ≥8 可争长生天眷" : "礼治 ≥8 可争天命";
                    Text condText = CreateText(card, $"<color=#6a6458>{cond} → 可晋</color>", 8);
                    condText.alignment = TextAnchor.MiddleLeft;
                    RectTransform cRect = condText.GetComponent<RectTransform>();
                    cRect.anchorMin = new Vector2(0, 1); cRect.anchorMax = new Vector2(0, 1);
                    cRect.pivot = new Vector2(0, 1);
                    cRect.anchoredPosition = new Vector2(10, -36);
                    cRect.sizeDelta = new Vector2(_innerWidth - 20, 14f);
                }
            }
            catch { }
        }

        private static string TierNameOf(int threshold, bool nomadic = false)
        {
            if (nomadic)
            {
                if (threshold <= 10) return "大部";
                if (threshold <= 20) return "部盟";
                if (threshold <= 35) return "汗国";
                return "大汗庭";
            }
            if (threshold <= 10) return "子";
            if (threshold <= 20) return "伯";
            if (threshold <= 35) return "侯";
            return "公";
        }

        /// <summary>阶段2M：礼治卡（祭祖/祭天/政策/受命）。任务12：非天命国也显示——查看问鼎礼治进度。</summary>
        private void CreateLiZhiCardV2(Kingdom k, string kingdomId, bool isTm)
        {
            try
            {
                if (k == null || _contentTransform == null) return;
                bool nomadic = KingdomCivilizationService.Instance.IsNomadic(k);
                int lz = ZhuluLiZhiService.GetLiZhiScore(k.id.ToString());
                long lastSac = KingdomDataExt.GetLong(k, "last_sacrifice", 0);
                Transform card = CreateOverviewCard(48f, isTm);
                // 阶段2S-11 修复：两行文本未锚定导致堆叠卡片中央重叠——显式锚定顶部（与重臣卡同款定位）
                // 任务12：非天命国显示距受命门槛差距（礼治≥8），天命国维持受命之阶文案
                // 口径：受命判定用净礼治（GetNetLiZhi，交战/亏空各-1）——差距按净礼治计算，主数值仍显示总礼治
                int netLz = isTm ? lz : ZhuluLiZhiService.GetNetLiZhi(k.id.ToString());
                string tail = isTm
                    ? $"<color={UI.ZhuluUIStyle.Hex.Imperial}>（{(nomadic ? "长生天眷" : "受命之阶")} · 净{(nomadic ? "汗威" : "礼治")}≥8）</color>"
                    : netLz >= 8
                        ? $"<color={UI.ZhuluUIStyle.Hex.Positive}>（净{(nomadic ? "汗威" : "礼治")}已达标 · 可谋{(nomadic ? "天眷" : "受命")}）</color>"
                        : $"<color=#6a7a8f>（问鼎之阶 · 还差 {8 - netLz} 点）</color>";
                Text title = CreateText(card, $"<color=#9aa0ac>🛕 {(nomadic ? "汗威" : "礼治")}</color> <color={UI.ZhuluUIStyle.Hex.Warn}><b>{lz}</b></color> {tail}", 9);
                title.alignment = TextAnchor.MiddleLeft;
                RectTransform tRect = title.GetComponent<RectTransform>();
                tRect.anchorMin = new Vector2(0, 1); tRect.anchorMax = new Vector2(0, 1);
                tRect.pivot = new Vector2(0, 1);
                tRect.anchoredPosition = new Vector2(10, -6);
                tRect.sizeDelta = new Vector2(_innerWidth - 20, 18f);
                string sacText = lastSac > 0 ? $"上次祭祀：建元 {lastSac} 年" : "尚未祭祀";
                Text sub = CreateText(card, $"<color=#6a6458>{sacText} ｜ 祭祖 +3 / 祭天大典 +8</color>", 8);
                sub.alignment = TextAnchor.MiddleLeft;
                RectTransform sRect = sub.GetComponent<RectTransform>();
                sRect.anchorMin = new Vector2(0, 1); sRect.anchorMax = new Vector2(0, 1);
                sRect.pivot = new Vector2(0, 1);
                sRect.anchoredPosition = new Vector2(10, -26);
                sRect.sizeDelta = new Vector2(_innerWidth - 20, 14f);
            }
            catch { }
        }

        /// <summary>阶段2M：重臣卡（只显重要人物——丞相/权臣/太子师等，按等级最多 3 人）。</summary>
        private void CreateImportantOfficialsCard(Kingdom k, string kingdomId, bool isTm)
        {
            try
            {
                if (k == null || _contentTransform == null) return;
                var recs = OfficeManager.Instance.GetRecordsPublic(kingdomId);
                if (recs == null || recs.Count == 0) return;
                // 重要人物：丞相（prime_minister）+ 权势最高者
                var important = new System.Collections.Generic.List<(string name, string office, float power)>();
                foreach (var rec in recs)
                {
                    if (rec == null || string.IsNullOrEmpty(rec.PersonId)) continue;
                    string office = OfficeManager.GetOfficeTitleForKingdom(rec.OfficeId, k);
                    float power = 0;
                    try { power = CorruptionManager.Instance.GetPower(kingdomId, rec.OfficeId); } catch { }
                    important.Add((rec.PersonName ?? "官", office, power));
                }
                // 排序：权势降序，最多 3 人
                important.Sort((a, b) => b.power.CompareTo(a.power));
                int n = Mathf.Min(3, important.Count);
                Transform card = CreateOverviewCard(18f + n * 20f + 30f, isTm); // 阶段2P：+30（文脉/地方官两行）
                Text title = CreateText(card, "<color=#9aa0ac>👥 重臣</color>", 9);
                title.alignment = TextAnchor.MiddleLeft;
                RectTransform tRect2 = title.GetComponent<RectTransform>();
                tRect2.anchorMin = new Vector2(0, 1); tRect2.anchorMax = new Vector2(0, 1);
                tRect2.pivot = new Vector2(0, 1);
                tRect2.anchoredPosition = new Vector2(10, -4);
                tRect2.sizeDelta = new Vector2(_innerWidth - 20, 15f);
                for (int i = 0; i < n; i++)
                {
                    var p = important[i];
                    string powerColor = p.power >= 70 ? "<color=#e0554a>" : p.power >= 40 ? "<color=#f0c060>" : "<color=#70d468>";
                    Text row = CreateText(card, $"<color=#e8e0d0>{p.name}</color> · {p.office} <size=7>{powerColor}权势 {p.power:F0}</color></size>", 9);
                    row.alignment = TextAnchor.MiddleLeft;
                    RectTransform rRect2 = row.GetComponent<RectTransform>();
                    rRect2.anchorMin = new Vector2(0, 1); rRect2.anchorMax = new Vector2(0, 1);
                    rRect2.pivot = new Vector2(0, 1);
                    rRect2.anchoredPosition = new Vector2(10, -22 - i * 20);
                    rRect2.sizeDelta = new Vector2(_innerWidth - 20, 18f);
                }

                // ── 阶段2P：文脉行（功名统计——科举兴衰一目了然）──
                int xiu = 0, ju = 0, jin = 0, zy = 0;
                bool talentNomadic = KingdomCivilizationService.Instance.IsNomadic(k);
                try
                {
                    if (k.units != null)
                    {
                        foreach (var u in k.units)
                        {
                            if (u == null || !u.isAlive()) continue;
                            try
                            {
                                if (talentNomadic)
                                {
                                    if (u.hasTrait(TraitManager.NOMAD_FIRST_SCHOLAR)) { zy++; jin++; }
                                    else if (u.hasTrait(TraitManager.NOMAD_COURT_SCHOLAR)) jin++;
                                    else if (u.hasTrait(TraitManager.NOMAD_LEAGUE_SCHOLAR)) ju++;
                                    else if (u.hasTrait(TraitManager.NOMAD_TRIBE_SCHOLAR)) xiu++;
                                }
                                else
                                {
                                    if (u.hasTrait(TraitManager.TRAIT_ZHUANGYUAN)) { zy++; jin++; }
                                    else if (u.hasTrait(TraitManager.TRAIT_JINSHI)) jin++;
                                    else if (u.hasTrait(TraitManager.TRAIT_JUREN)) ju++;
                                    else if (u.hasTrait(TraitManager.TRAIT_XIUCAI)) xiu++;
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
                int totalGm = xiu + ju + jin;
                string wenmai;
                if (talentNomadic)
                {
                    wenmai = totalGm > 0
                        ? $"贤士：部族 {xiu} · 部盟 {ju} · 汗庭 {jin}{(zy > 0 ? $" · <color={UI.ZhuluUIStyle.Hex.Imperial}>首贤 {zy}</color>" : "")}" 
                        : "<color=#6a6458>贤士：选贤未兴（部盟以上正式选拔）</color>";
                }
                else
                {
                    wenmai = totalGm > 0
                        ? $"文脉：秀才 {xiu} · 举人 {ju} · 进士 {jin}{(zy > 0 ? $" · <color={UI.ZhuluUIStyle.Hex.Imperial}>状元 {zy}</color>" : "")}" 
                        : "<color=#6a6458>文脉：科举未兴（伯国以上开科取士）</color>";
                }
                float extraY = -22 - n * 20;
                Text wmText = CreateText(card, $"<color=#6a7a8f>{wenmai}</color>", 8);
                wmText.alignment = TextAnchor.MiddleLeft;
                var wmRect = wmText.GetComponent<RectTransform>();
                wmRect.anchorMin = new Vector2(0, 1); wmRect.anchorMax = new Vector2(0, 1);
                wmRect.pivot = new Vector2(0, 1);
                wmRect.anchoredPosition = new Vector2(10, extraY);
                wmRect.sizeDelta = new Vector2(_innerWidth - 20, 13f);

                // ── 阶段2P：地方官/城主行 ──
                int localOfficers = 0;
                try
                {
                    foreach (var rec2 in recs)
                    {
                        if (rec2 != null && !string.IsNullOrEmpty(rec2.PersonId) && OfficeManager.IsLocalOffice(rec2.OfficeId)) localOfficers++;
                    }
                }
                catch { }
                int cityLeaders = 0;
                try
                {
                    if (k.cities != null)
                    {
                        foreach (var c in k.cities) { if (c != null && c.leader != null) cityLeaders++; }
                    }
                }
                catch { }
                Text localText = CreateText(card, $"<color=#6a6458>地方官 {localOfficers} 员 · 城主 {cityLeaders} 员</color>", 8);
                localText.alignment = TextAnchor.MiddleLeft;
                var localRect = localText.GetComponent<RectTransform>();
                localRect.anchorMin = new Vector2(0, 1); localRect.anchorMax = new Vector2(0, 1);
                localRect.pivot = new Vector2(0, 1);
                localRect.anchoredPosition = new Vector2(10, extraY - 13);
                localRect.sizeDelta = new Vector2(_innerWidth - 20, 13f);
            }
            catch { }
        }

        private void RenderKingdomStateUI(string kingdomId)
        {
            _headerBackOverride = null;   // 清残留返回覆盖
            _stateVm = KingdomStateVm.CreateFromAdapterOrMock(kingdomId);
            UpdateHeaderTitle($"{_stateVm.KingdomName} · 国势");
            SetHeaderTabsVisible(false);


            bool isTm = false;
            Kingdom k = null;
            try
            {
                k = KingdomDataExt.FindKingdomById(kingdomId);
                if (k != null) isTm = PolicyManager.IsMandateHolderForKingdom(k);
            }
            catch { }

            // Header 统计行：建元N年 · 国名 · 位阶（普通国不加"非正统"——只有天命国标记）
            if (_headerStatText != null)
            {
                bool nomadic = k != null && KingdomCivilizationService.Instance.IsNomadic(k);
                _headerStatText.text = isTm
                    ? $"<b>建元 {ZhuluTime.CurrentYear} 年</b>　{_stateVm.KingdomName} · <color={UI.ZhuluUIStyle.Hex.Imperial}>{_stateVm.KingdomTierName}</color> · ★{(nomadic ? "长生天眷" : "天命国")}"
                    : $"<b>建元 {ZhuluTime.CurrentYear} 年</b>　{_stateVm.KingdomName} · <color=#6ca3fd>{_stateVm.KingdomTierName}</color>";
            }

            // 阶段2M ①：顶部状态行（城/口/兵/国力/附庸 一行速览）
            CreateStateStatusLine(k, kingdomId);

            // 1. 君主快照卡（Row 容器：左双动态小人头像[君主+储君] + 右信息，天命国金框）
            CreateRulerSnapshotCardV2(k, _stateVm, isTm);

            // 2. 国力晋升进度卡（阶段2M：国力点 + 距下等差距 + 晋升条件——核心新增）
            CreateGuoliProgressCard(k, kingdomId, isTm);

            // 3. 指标卡（横向：左图标+标题+原因 / 右数值+等级；天命国第3张=天命观察，普通国=疆域民力）
            CreateStateMetricCardV2(_stateVm.DynastyStageCard, "🏷", ZhuluUIStyle.Hex.Gold, isTm);
            CreateStateMetricCardV2(_stateVm.PowerCard, "⚜", "#6ca3fd", isTm);
            if (isTm)
            {
                CreateStateMetricCardV2(_stateVm.MandateCard, "☯", "#ffd8d9", isTm);
                CreateLiZhiCardV2(k, kingdomId, isTm);   // 阶段2M：天命国礼治卡（祭祖/祭天/政策）
            }
            else
            {
                CreateTerritoryCardV2(_stateVm, isTm);
                CreateLiZhiCardV2(k, kingdomId, isTm);   // 任务12：非天命国也显示礼治卡（问鼎进度）
            }
            CreateStateMetricCardV2(_stateVm.TreasuryCard, "🏦", ZhuluUIStyle.Hex.Gold, isTm);
            CreateStateMetricCardV2(_stateVm.StabilityCard, "🛡", "#70d468", isTm);

            // 阶段2P：邦交概览卡（宗主/附庸/战争/条约——国势一页看外交）
            CreateDiplomacySummaryCard(k, kingdomId, isTm);

            // 4. 重臣卡（阶段2M：只显重要人物——丞相/权臣/太子师等，按等级；阶段2P：+文脉/地方官）
            CreateImportantOfficialsCard(k, kingdomId, isTm);

            // 5. 近期大事卡
            CreateRecentEventsCardV2(_stateVm, isTm);
        }

        /// <summary>国势快照卡：左君主+储君动态头像（UiUnitAvatarElement，原版组件），右信息。Row容器+纯色底，天命国金框。</summary>
        private void CreateRulerSnapshotCardV2(Kingdom k, KingdomStateVm vm, bool isTm)
        {
            Transform card = CreateOverviewCard(76f, isTm); // 阶段2P：64→76（加国祚·在位·政体行）

            // 左：双头像（52×52）
            Transform avatarGroup = new GameObject("AvatarGroup", typeof(RectTransform)).transform;
            avatarGroup.SetParent(card, false);
            var agRect = avatarGroup.GetComponent<RectTransform>();
            agRect.anchorMin = new Vector2(0, 0.5f);
            agRect.anchorMax = new Vector2(0, 0.5f);
            agRect.pivot = new Vector2(0, 0.5f);
            agRect.anchoredPosition = new Vector2(10, 0);
            agRect.sizeDelta = new Vector2(110f, 52f);

            Actor king = null, heir = null;
            string heirId = "";
            string heirTitle = "储君";
            try
            {
                if (k != null && k.king != null) king = k.king;
                heirId = KingdomDataExt.GetStr(k, "heir_id");
                if (!string.IsNullOrEmpty(heirId))
                {
                    long id = long.Parse(heirId);
                    heir = World.world.units.get(id);
                    if (k.king != null)
                        heirTitle = SuccessionManager.Instance.GetHeirTitle(k.id.ToString(), k, k.king.getID().ToString(), heirId);
                }
            }
            catch { }

            // 君主头像（金/灰框）
            Transform kingSlot = CreateAvatarSlot(avatarGroup, 52f, isTm);
            SetAvatar(kingSlot, king, "君", isTm);
            // 储君头像（同款框，字旗不同）
            Transform heirSlot = CreateAvatarSlot(avatarGroup, 52f, isTm);
            var heirRect = heirSlot.GetComponent<RectTransform>();
            heirRect.anchoredPosition = new Vector2(58f, 0);
            SetAvatar(heirSlot, heir, "储", isTm);

            // 右：信息 3 行
            string rulerName = vm.RulerName ?? "无名君";
            // 阶段2M：去称号重复（"薛谦清 - 伯君" → "薛谦清"）
            rulerName = System.Text.RegularExpressions.Regex.Replace(rulerName, "[ -·]*(伯君|子君|侯君|公君|邑君|皇帝|天王|君)$", "");
            Text r1 = CreateText(card, $"<color={(isTm ? ZhuluUIStyle.Hex.Gold : "#3CB371")}>{vm.RulerTitle}</color> {rulerName}", 10);
            r1.resizeTextForBestFit = true; r1.resizeTextMinSize = 8; r1.resizeTextMaxSize = 11; // 阶段3H3 游牧长名自适应
            r1.alignment = TextAnchor.MiddleLeft;
            var r1Rect = r1.GetComponent<RectTransform>();
            r1Rect.anchorMin = new Vector2(0, 1);
            r1Rect.anchorMax = new Vector2(0, 1);
            r1Rect.pivot = new Vector2(0, 1);
            r1Rect.anchoredPosition = new Vector2(124, -8);
            r1Rect.sizeDelta = new Vector2(_innerWidth - 134, 16f);
            r1.raycastTarget = false;

            string heirName = heir != null ? (heir.getName() ?? "储君") : "虚位以待";
            // 阶段2N W11：无继承人时不再重复显示默认称谓（原"储君 虚位以待 · 储君"）
            Text r2 = CreateText(card, heir != null
                ? $"<color={UI.ZhuluUIStyle.Hex.Azure}>储君</color> {heirName} · {heirTitle}"
                : "<color=#8a8578>储位 虚位以待</color>", 9);
            r2.resizeTextForBestFit = true; r2.resizeTextMinSize = 7; r2.resizeTextMaxSize = 9;
            r2.alignment = TextAnchor.MiddleLeft;
            var r2Rect = r2.GetComponent<RectTransform>();
            r2Rect.anchorMin = new Vector2(0, 1);
            r2Rect.anchorMax = new Vector2(0, 1);
            r2Rect.pivot = new Vector2(0, 1);
            r2Rect.anchoredPosition = new Vector2(124, -28);
            r2Rect.sizeDelta = new Vector2(_innerWidth - 134, 13f);
            r2.raycastTarget = false;

            Text r3 = CreateText(card, $"领土 <color={UI.ZhuluUIStyle.Hex.Azure}>{vm.CityCount} 城</color> · 人口 <color={UI.ZhuluUIStyle.Hex.Positive}>{vm.Population}</color> · 兵力 <color=#e0554a>{vm.Military}</color>", 9); // 阶段2M：去"空缺官席"残留（重臣卡已替代）
            r3.resizeTextForBestFit = true; r3.resizeTextMinSize = 7; r3.resizeTextMaxSize = 9;
            r3.alignment = TextAnchor.MiddleLeft;
            r3.horizontalOverflow = HorizontalWrapMode.Wrap;
            r3.verticalOverflow = VerticalWrapMode.Overflow;
            var r3Rect = r3.GetComponent<RectTransform>();
            r3Rect.anchorMin = new Vector2(0, 1);
            r3Rect.anchorMax = new Vector2(0, 1);
            r3Rect.pivot = new Vector2(0, 1);
            r3Rect.anchoredPosition = new Vector2(124, -44);
            r3Rect.sizeDelta = new Vector2(_innerWidth - 134, 20f);
            r3.raycastTarget = false;

            // 阶段2P：国祚 · 在位 · 政体行
            try
            {
                if (k != null)
                {
                    string kId = k.id.ToString();
                    int dynastyAge = KingdomExt.GetDynastyAge(kId, ZhuluTime.CurrentYear);
                    long reignStart = KingdomDataExt.GetLong(k, "reign_start", -1);
                    int reignYr = reignStart > 0 ? Mathf.Max(1, ZhuluTime.CurrentYear - (int)reignStart) : 0;
                    int powerVal = KingdomExt.GetPowerValue(kId);
                    string ageLine = $"立国 {PosthumousNameGenerator.ToChinese(dynastyAge)} 年"
                        + (reignYr > 0 ? $" · 在位 {PosthumousNameGenerator.ToChinese(reignYr)} 年" : "")
                        + $" · <color=#9aa0ac>{RegimeManager.GetBandName(powerVal)}</color>";
                    Text r4 = CreateText(card, $"<color=#8a8578>{ageLine}</color>", 8);
                    r4.alignment = TextAnchor.MiddleLeft;
                    var r4Rect = r4.GetComponent<RectTransform>();
                    r4Rect.anchorMin = new Vector2(0, 1);
                    r4Rect.anchorMax = new Vector2(0, 1);
                    r4Rect.pivot = new Vector2(0, 1);
                    r4Rect.anchoredPosition = new Vector2(124, -62);
                    r4Rect.sizeDelta = new Vector2(_innerWidth - 134, 12f);
                    r4.raycastTarget = false;
                }
            }
            catch { }
        }

        /// <summary>横向指标卡：左图标+标题+原因 / 右数值+等级+趋势。
        /// 阶段2Q 根修：左右两列硬分区防重叠（原 reason 宽190伸进右列区——中央权力卡文案变长后文本重叠混乱）。
        /// 布局：图标x12 │ 左列 x34~(inner-104) │ 右列宽100 右对齐。行1 标题|数值 · 行2 原因(可换行)|等级趋势。</summary>
        private void CreateStateMetricCardV2(StateCardVm card, string icon, string colorHex, bool isTm)
        {
            if (card == null) return;
            Transform mc = CreateOverviewCard(44f, isTm);
            float leftW = _innerWidth - 34f - 104f; // 左列宽（右侧数值列 100 + 间距）

            Text iconText = CreateText(mc, icon, 13);
            iconText.alignment = TextAnchor.MiddleCenter;
            var iconRect = iconText.GetComponent<RectTransform>();
            iconRect.anchorMin = new Vector2(0, 0.5f);
            iconRect.anchorMax = new Vector2(0, 0.5f);
            iconRect.pivot = new Vector2(0, 0.5f);
            iconRect.anchoredPosition = new Vector2(12, 0);
            iconRect.sizeDelta = new Vector2(20f, 20f);
            iconText.raycastTarget = false;

            Text titleText = CreateText(mc, $"<b>{card.Title}</b>", 10);
            titleText.alignment = TextAnchor.MiddleLeft;
            var tRect = titleText.GetComponent<RectTransform>();
            tRect.anchorMin = new Vector2(0, 1);
            tRect.anchorMax = new Vector2(0, 1);
            tRect.pivot = new Vector2(0, 1);
            tRect.anchoredPosition = new Vector2(34, -6);
            tRect.sizeDelta = new Vector2(leftW, 15f);
            titleText.raycastTarget = false;

            Text reasonText = CreateText(mc, $"<color=#6a6458>{card.MainReason}</color>", 8);
            reasonText.alignment = TextAnchor.MiddleLeft;
            reasonText.horizontalOverflow = HorizontalWrapMode.Wrap;
            reasonText.verticalOverflow = VerticalWrapMode.Overflow;
            var rr = reasonText.GetComponent<RectTransform>();
            rr.anchorMin = new Vector2(0, 1);
            rr.anchorMax = new Vector2(0, 1);
            rr.pivot = new Vector2(0, 1);
            rr.anchoredPosition = new Vector2(34, -21);
            rr.sizeDelta = new Vector2(leftW, 20f);
            reasonText.raycastTarget = false;

            Text valText = CreateText(mc, $"<b><color={colorHex}>{card.NumericDisplay}</color></b>", 12);
            valText.alignment = TextAnchor.MiddleRight;
            var vRect = valText.GetComponent<RectTransform>();
            vRect.anchorMin = new Vector2(1, 1);
            vRect.anchorMax = new Vector2(1, 1);
            vRect.pivot = new Vector2(1, 1);
            vRect.anchoredPosition = new Vector2(-10, -7);
            vRect.sizeDelta = new Vector2(96f, 16f);
            valText.raycastTarget = false;

            Text gradeText = CreateText(mc, $"<color=#8a8578>{card.GradeLabel}</color> <color=#6a6458>{card.Trend}</color>", 7);
            gradeText.alignment = TextAnchor.MiddleRight;
            gradeText.horizontalOverflow = HorizontalWrapMode.Overflow;
            var gRect = gradeText.GetComponent<RectTransform>();
            gRect.anchorMin = new Vector2(1, 1);
            gRect.anchorMax = new Vector2(1, 1);
            gRect.pivot = new Vector2(1, 1);
            gRect.anchoredPosition = new Vector2(-10, -26);
            gRect.sizeDelta = new Vector2(96f, 12f);
            gradeText.raycastTarget = false;
        }

        /// <summary>普通国第3卡：疆域民力（替代天命观察——非天命国无天命）</summary>
        private void CreateTerritoryCardV2(KingdomStateVm vm, bool isTm)
        {
            Transform mc = CreateOverviewCard(34f, isTm);

            Text iconText = CreateText(mc, "🗺", 12);
            iconText.alignment = TextAnchor.MiddleCenter;
            var iconRect = iconText.GetComponent<RectTransform>();
            iconRect.anchorMin = new Vector2(0, 0.5f);
            iconRect.anchorMax = new Vector2(0, 0.5f);
            iconRect.pivot = new Vector2(0, 0.5f);
            iconRect.anchoredPosition = new Vector2(12, 0);
            iconRect.sizeDelta = new Vector2(20f, 20f);
            iconText.raycastTarget = false;

            Text titleText = CreateText(mc, "<b>疆域民力</b>", 10);
            titleText.alignment = TextAnchor.MiddleLeft;
            var tRect = titleText.GetComponent<RectTransform>();
            tRect.anchorMin = new Vector2(0, 1);
            tRect.anchorMax = new Vector2(0, 1);
            tRect.pivot = new Vector2(0, 1);
            tRect.anchoredPosition = new Vector2(36, -6);
            tRect.sizeDelta = new Vector2(170f, 15f);
            titleText.raycastTarget = false;

            float ratio = vm.Military > 0 ? (float)vm.Population / vm.Military : 0f;
            Text reasonText = CreateText(mc, $"<color=#6a6458>人丁 {vm.Population} · 甲兵 {vm.Military} · 兵民比 {ratio:F1}:1</color>", 8); // 阶段2M：去"无天命加成"残留
            reasonText.alignment = TextAnchor.MiddleLeft;
            reasonText.horizontalOverflow = HorizontalWrapMode.Wrap;
            reasonText.verticalOverflow = VerticalWrapMode.Overflow;
            var rr = reasonText.GetComponent<RectTransform>();
            rr.anchorMin = new Vector2(0, 1);
            rr.anchorMax = new Vector2(0, 1);
            rr.pivot = new Vector2(0, 1);
            rr.anchoredPosition = new Vector2(36, -19);
            rr.sizeDelta = new Vector2(190f, 15f);
            reasonText.raycastTarget = false;

            Text valText = CreateText(mc, $"<b><color={UI.ZhuluUIStyle.Hex.Azure}>{vm.CityCount} 城</color></b>", 12);
            valText.alignment = TextAnchor.MiddleRight;
            var vRect = valText.GetComponent<RectTransform>();
            vRect.anchorMin = new Vector2(1, 1);
            vRect.anchorMax = new Vector2(1, 1);
            vRect.pivot = new Vector2(1, 1);
            vRect.anchoredPosition = new Vector2(-10, -7);
            vRect.sizeDelta = new Vector2(110f, 16f);
            valText.raycastTarget = false;

            // 阶段2M：普通国不显示"非正统"（旧文本残留清理——只有天命国标记由 Header 承担）
            Text gradeText = CreateText(mc, "", 8);
            gradeText.alignment = TextAnchor.MiddleRight;
            var gRect = gradeText.GetComponent<RectTransform>();
            gRect.anchorMin = new Vector2(1, 1);
            gRect.anchorMax = new Vector2(1, 1);
            gRect.pivot = new Vector2(1, 1);
            gRect.anchoredPosition = new Vector2(-10, -25);
            gRect.sizeDelta = new Vector2(110f, 12f);
            gradeText.raycastTarget = false;
        }

        /// <summary>阶段2P：邦交概览卡——宗主（附庸国）/附庸列表/交战状态/停战与互不侵犯。</summary>
        private void CreateDiplomacySummaryCard(Kingdom k, string kingdomId, bool isTm)
        {
            try
            {
                if (k == null || _contentTransform == null) return;
                var lines = new List<string>();

                // 宗主（本国是附庸时）
                try
                {
                    Kingdom suz = VassalManager.Instance.GetSuzerain(k);
                    if (suz != null && suz.isAlive())
                    {
                        int tribute = ZhuluDiplomacyService.ComputeTribute(k);
                        lines.Add($"臣属于 <color={UI.ZhuluUIStyle.Hex.Imperial}>【{suz.name}】</color> · 岁贡 <color={UI.ZhuluUIStyle.Hex.Warn}>{tribute} 金</color>");
                    }
                }
                catch { }

                // 附庸列表
                try
                {
                    var vassalNames = new List<string>();
                    foreach (var v in VassalManager.Instance.GetVassals(k))
                    {
                        if (v != null && v.isAlive()) vassalNames.Add(v.name ?? "?");
                    }
                    if (vassalNames.Count > 0)
                    {
                        int show = Mathf.Min(3, vassalNames.Count);
                        string shown = string.Join("、", vassalNames.GetRange(0, show).ToArray())
                            + (vassalNames.Count > show ? $" 等 {vassalNames.Count} 国" : "");
                        lines.Add($"附庸 <color={UI.ZhuluUIStyle.Hex.Positive}>{vassalNames.Count}</color> 国：{shown}");
                    }
                }
                catch { }

                // 交战状态
                try
                {
                    var warDescs = new List<string>();
                    foreach (War w in k.getWars())
                    {
                        if (w == null || w.hasEnded()) continue;
                        string typeName = "";
                        try
                        {
                            string wt = w.data?.war_type ?? "";
                            if (string.IsNullOrEmpty(wt)) { var wa = w.getAsset(); if (wa != null) wt = wa.id; }
                            typeName = WarfareManager.GetWarTypeName(wt);
                        }
                        catch { }
                        string foe = "";
                        try
                        {
                            bool isAtk = false;
                            foreach (Kingdom ak in w.getAttackers()) { if (ak == k) { isAtk = true; break; } }
                            var foes = isAtk ? w.getDefenders() : w.getAttackers();
                            foreach (Kingdom f in foes) { if (f != null && f != k && f.isAlive()) { foe = f.name; break; } }
                        }
                        catch { }
                        if (!string.IsNullOrEmpty(foe)) warDescs.Add($"{foe}·{typeName}");
                    }
                    lines.Add(warDescs.Count == 0
                        ? "<color=#70d468>四海升平，兵戈未起</color>"
                        : $"<color=#f08080>交战 {warDescs.Count} 场</color>：{string.Join("、", warDescs.GetRange(0, Mathf.Min(2, warDescs.Count)).ToArray())}{(warDescs.Count > 2 ? "…" : "")}");
                }
                catch { }

                // 停战 / 互不侵犯
                try
                {
                    int truce = 0, nap = 0;
                    int year = ZhuluTime.CurrentYear;
                    if (World.world?.kingdoms?.list != null)
                    {
                        foreach (Kingdom o in World.world.kingdoms.list)
                        {
                            if (o == null || !o.isAlive() || o == k) continue;
                            string oId = o.id.ToString();
                            if (ZhuluDiplomacyService.IsTruceActive(kingdomId, oId, year)) truce++;
                            if (ZhuluDiplomacyService.IsNonAggressionActive(kingdomId, oId, year)) nap++;
                        }
                    }
                    if (truce > 0 || nap > 0)
                    {
                        lines.Add($"🕊 停战 <color=#88b8e8>{truce}</color> 国 · 互不侵犯 <color=#88b8e8>{nap}</color> 国");
                    }
                }
                catch { }

                if (lines.Count == 0) return;
                float h = 22f + lines.Count * 15f;
                Transform card = CreateOverviewCard(h, isTm);
                Text title = CreateText(card, "<color=#9aa0ac>🤝 邦交</color>", 9);
                title.alignment = TextAnchor.MiddleLeft;
                var dTitleRect = title.GetComponent<RectTransform>();
                dTitleRect.anchorMin = new Vector2(0, 1);
                dTitleRect.anchorMax = new Vector2(0, 1);
                dTitleRect.pivot = new Vector2(0, 1);
                dTitleRect.anchoredPosition = new Vector2(12, -4);
                dTitleRect.sizeDelta = new Vector2(_innerWidth - 24, 14f);
                title.raycastTarget = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    Text line = CreateText(card, $"<color=#b8b2a8>{lines[i]}</color>", 8);
                    line.alignment = TextAnchor.MiddleLeft;
                    line.horizontalOverflow = HorizontalWrapMode.Wrap;
                    line.verticalOverflow = VerticalWrapMode.Overflow;
                    var lr2 = line.GetComponent<RectTransform>();
                    lr2.anchorMin = new Vector2(0, 1);
                    lr2.anchorMax = new Vector2(0, 1);
                    lr2.pivot = new Vector2(0, 1);
                    lr2.anchoredPosition = new Vector2(12, -19 - i * 15);
                    lr2.sizeDelta = new Vector2(_innerWidth - 24, 14f);
                    line.raycastTarget = false;
                }
            }
            catch { }
        }

        /// <summary>近期大事卡</summary>
        private void CreateRecentEventsCardV2(KingdomStateVm vm, bool isTm)
        {
            Transform mc = CreateOverviewCard(66f, isTm);

            Text titleText = CreateText(mc, "<color=#05fca6>📜 近期大事</color>", 10);
            titleText.alignment = TextAnchor.MiddleLeft;
            var tRect = titleText.GetComponent<RectTransform>();
            tRect.anchorMin = new Vector2(0, 1);
            tRect.anchorMax = new Vector2(0, 1);
            tRect.pivot = new Vector2(0, 1);
            tRect.anchoredPosition = new Vector2(12, -6);
            tRect.sizeDelta = new Vector2(_innerWidth - 24, 15f);
            titleText.raycastTarget = false;

            var events = vm.RecentMajorEvents ?? new List<string>();
            string body = events.Count == 0
                ? "<color=#6a6458>暂无大事记录。</color>"
                : string.Join("\n", events);
            Text bodyText = CreateText(mc, $"<color=#b8b2a8>{body}</color>", 9);
            bodyText.alignment = TextAnchor.UpperLeft;
            bodyText.horizontalOverflow = HorizontalWrapMode.Wrap;
            bodyText.verticalOverflow = VerticalWrapMode.Overflow;
            var bRect = bodyText.GetComponent<RectTransform>();
            bRect.anchorMin = new Vector2(0, 1);
            bRect.anchorMax = new Vector2(0, 1);
            bRect.pivot = new Vector2(0, 1);
            bRect.anchoredPosition = new Vector2(12, -24);
            bRect.sizeDelta = new Vector2(_innerWidth - 24, 50f);
            bodyText.raycastTarget = false;
        }

        /// <summary>通用卡片容器（Row：纯色底-36防贴图留白 + 贴图装饰层，同父级绝对对齐）</summary>
        private Transform CreateOverviewCard(float height, bool isTm)
        {
            GameObject row = new GameObject("Card", typeof(RectTransform));
            row.transform.SetParent(_contentTransform, false);
            var rowRect = row.GetComponent<RectTransform>();
            rowRect.sizeDelta = new Vector2(_innerWidth, height);

            GameObject solidObj = new GameObject("SolidBg", typeof(RectTransform), typeof(Image));
            solidObj.transform.SetParent(row.transform, false);
            var solidRect = solidObj.GetComponent<RectTransform>();
            solidRect.anchorMin = Vector2.zero;
            solidRect.anchorMax = Vector2.one;
            solidRect.offsetMin = new Vector2(-36f, 0f);
            solidRect.offsetMax = Vector2.zero;
            var solidImg = solidObj.GetComponent<Image>();
            solidImg.color = isTm
                ? new Color(0.20f, 0.17f, 0.09f, 0.95f)   // 天命国：金底
                : new Color(0.13f, 0.14f, 0.18f, 0.92f);
            solidImg.raycastTarget = false;

            GameObject bgObj = new GameObject("Bg", typeof(RectTransform), typeof(Image));
            bgObj.transform.SetParent(row.transform, false);
            var bgRect = bgObj.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = new Vector2(-36f, 0f);
            bgRect.offsetMax = Vector2.zero;
            var bgImg = bgObj.GetComponent<Image>();
            bgImg.color = isTm
                ? new Color(0.28f, 0.22f, 0.10f, 0.92f)
                : new Color(0.16f, 0.17f, 0.21f, 0.9f);
            bgImg.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            bgImg.type = Image.Type.Sliced;
            bgImg.raycastTarget = false;

            return row.transform;
        }

        // ═══════════════════════ 动态小人头像（原版 UiUnitAvatarElement） ═══════════════════════

        private static UiUnitAvatarElement _avatarPrefab;

        /// <summary>懒加载原版动态头像 prefab（Resources 直取，Instantiate 即用）</summary>
        private static UiUnitAvatarElement GetAvatarPrefab()
        {
            if (_avatarPrefab != null) return _avatarPrefab;
            try { _avatarPrefab = Resources.Load<UiUnitAvatarElement>("ui/UnitAvatarElement"); }
            catch { _avatarPrefab = null; }
            return _avatarPrefab;
        }

        /// <summary>创建正方形头像槽（52×52；prefab 失败回退字符头像：彩色方块+首字）</summary>
        private Transform CreateAvatarSlot(Transform parent, float size, bool isTm)
        {
            GameObject slot = new GameObject("PortraitSlot", typeof(RectTransform));
            slot.transform.SetParent(parent, false);
            var slotRect = slot.GetComponent<RectTransform>();
            slotRect.anchorMin = new Vector2(0, 0.5f);
            slotRect.anchorMax = new Vector2(0, 0.5f);
            slotRect.pivot = new Vector2(0, 0.5f);
            slotRect.anchoredPosition = Vector2.zero;
            slotRect.sizeDelta = new Vector2(size, size);

            var prefab = GetAvatarPrefab();
            if (prefab != null)
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
            else
            {
                // 兜底：字符头像（彩色方块 + 首字）
                var imgObj = new GameObject("FallbackBg", typeof(RectTransform), typeof(Image));
                imgObj.transform.SetParent(slot.transform, false);
                var imgRect = imgObj.GetComponent<RectTransform>();
                imgRect.anchorMin = Vector2.zero;
                imgRect.anchorMax = Vector2.one;
                imgRect.offsetMin = Vector2.zero;
                imgRect.offsetMax = Vector2.zero;
                var img = imgObj.GetComponent<Image>();
                img.color = isTm ? new Color(0.45f, 0.30f, 0.10f, 1f) : new Color(0.25f, 0.28f, 0.36f, 1f);
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

        /// <summary>把 Actor 显示进头像槽（动态小人）；actor 为空/已死 → 字符兜底</summary>
        private void SetAvatar(Transform slot, Actor actor, string fallbackChar, bool isTm)
        {
            if (slot == null) return;
            // 阶段2M：头像点击 → 跳转单位详情（参考名君榜 row 点击——selectAndInspect + showWindow）
            if (actor != null && slot.GetComponent<Button>() == null)
            {
                Actor captured = actor;
                var btn = slot.gameObject.AddComponent<Button>();
                btn.onClick.AddListener(() =>
                {
                    try
                    {
                        if (captured != null && captured.isAlive())
                        {
                            MetaTypeLibrary.unit.selectAndInspect(captured, false, true, true);
                            ScrollWindow.showWindow(MetaTypeLibrary.unit.window_name);
                        }
                    }
                    catch { }
                });
            }
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
                        // 放大小人填满头像框（默认渲染只占框中间，上下留白；load时 localScale = inspect_avatar_scale × avatarSize）
                        avatar.avatarLoader.avatarSize *= 1.25f;
                        avatar.avatarLoader.enabled = true;
                    }
                    avatar.show(actor);
                    ok = true;
                }
                catch (Exception ex)
                {
                    ZhuluContext.Instance.LogWarning($"[WorldOverview] 动态头像显示失败，回退字符: {ex.Message}");
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

        #endregion

    }
}
