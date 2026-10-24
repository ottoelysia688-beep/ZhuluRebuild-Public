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


// ── 由 WorldOverviewWindow.cs 按 region 拆出的 partial 部分：4.5 王国邦交纵横视图 ──
// 2026-10-23 [P1-1] 纯文件级拆分，编译后仍为同一个类型，无任何行为改动。
namespace ZhuluRebuild.UI.Windows
{
    public partial class WorldOverviewWindow : MonoBehaviour
    {
        #region 4.5 王国邦交纵横视图 (宗主/附庸/宣称)

        private void RenderKingdomDiplomacyUI(string kingdomId)
        {
            var kFact = WorldBoxReadOnlyAdapter.Instance.CaptureKingdomSnapshot(kingdomId);
            string kName = kFact != null ? kFact.KingdomName : "当前王国";

            // 五档样式
            var style = GetTierStyle(kingdomId);

            UpdateHeaderTitle($"<color={style.Hex}>{style.Mark} {kName} · 邦交纵横</color>");
            SetHeaderTabsVisible(false);
            _headerBackOverride = null;

            // ── 真实数据 ──
            Kingdom k = ZhuluRebuild.Persistence.KingdomDataExt.FindKingdomById(kingdomId);
            Kingdom suzerain = null;
            List<Kingdom> vassals = new List<Kingdom>();
            List<ClaimRecord> claims = new List<ClaimRecord>();
            try { suzerain = (k != null) ? VassalManager.Instance.GetSuzerain(k) : null; } catch { }
            try { vassals = (k != null) ? VassalManager.Instance.GetVassals(k) : new List<Kingdom>(); } catch { }
            try { claims = (k != null) ? ClaimManager.Instance.GetClaims(k) : new List<ClaimRecord>(); } catch { }
            bool hasSuzerain = suzerain != null && suzerain.isAlive();

            // 2026-08-19 串联·方案6.2：停战/互不侵犯扫描提前（态势头行需要计数；和约与互不侵犯合并一卡）
            var truceList = new List<(Kingdom other, int remain)>();
            var napList = new List<(Kingdom other, int remain)>();
            try
            {
                int year = ZhuluTime.CurrentYear;
                if (World.world?.kingdoms?.list != null && k != null)
                {
                    foreach (Kingdom o in World.world.kingdoms.list)
                    {
                        if (o == null || o == k || !o.isAlive()) continue;
                        if (ZhuluDiplomacyService.IsTruceActive(k.id.ToString(), o.id.ToString(), year))
                        {
                            long u = KingdomDataExt.GetLong(k, "truce_until_" + o.id, 0);
                            truceList.Add((o, Math.Max(0, u > 0 ? (int)u - year : 0)));
                        }
                        if (ZhuluDiplomacyService.IsNonAggressionActive(k.id.ToString(), o.id.ToString(), year))
                        {
                            long u2 = KingdomDataExt.GetLong(k, "nap_until_" + o.id, 0);
                            napList.Add((o, Math.Max(0, u2 > 0 ? (int)u2 - year : 0)));
                        }
                    }
                }
            }
            catch { }

            // 在战数（态势头行）
            int warCount = 0;
            try { var wars = k?.getWars(); if (wars != null) foreach (var _ in wars) warCount++; } catch { }

            // ── 态势头行（方案6.2：先总后分——强弱对比/附庸/宣称/在战/停战）──
            if (_headerStatText != null)
            {
                string suzText = hasSuzerain ? $"<color={UI.ZhuluUIStyle.Hex.Imperial}>{suzerain.name}</color>" : "<color=#8a8578>无</color>";
                string warText = warCount > 0 ? $"<color=#ff8a7a>在战{warCount}</color>" : "<color={UI.ZhuluUIStyle.Hex.Positive}>太平</color>";
                _headerStatText.text = $"宗主：{suzText}　附庸：<color={UI.ZhuluUIStyle.Hex.Positive}>{vassals.Count}国</color>　宣称：<color=#e0554a>{claims.Count}项</color>　{warText}　停战 <color=#88b8e8>{truceList.Count}</color> · 互不侵犯 <color=#88b8e8>{napList.Count}</color>";
            }

            // ── 卡1：宗主关系（附庸视角=蓝卡；无宗主=灰卡）──
            if (hasSuzerain)
            {
                Kingdom sz = suzerain;
                Transform row = CreateDiplomacyCard("suzerain", $"宗主：{sz.name}", "ui/icons/iconCrown",
                    new Color(0.10f, 0.18f, 0.26f, 0.95f), new Color(0.14f, 0.24f, 0.34f, 0.92f), 70f);
                CreateDiplomacyText(row, $"<color=#8a8578>附庸国 · 每年朝贡：贡金入国库（人口÷2）+ 稳定度 +1；断贡 2 年宗主震怒</color>", 8, new Vector2(42f, -28f), InnerWidth - 90f);
                CreateDiplomacyText(row, "<color=#6a6458>可发动独立战争脱离宗主；打下宗主首都或坚持5年即可独立</color>", 7, new Vector2(42f, -42f), InnerWidth - 90f);

                bool canIndep = VassalRules.CanStartIndependence(k, sz, ZhuluTime.CurrentYear);
                GameObject indepBtn = CreateButton(row, canIndep ? "发动独立战争" : "独立冷却中",
                    canIndep ? new Color(0.45f, 0.2f, 0.4f) : new Color(0.3f, 0.3f, 0.3f), () =>
                    {
                        if (VassalManager.Instance.StartIndependenceWarFromUI(k)) RebuildUI();
                    });
                var indepRect = indepBtn.GetComponent<RectTransform>();
                indepRect.anchorMin = new Vector2(1f, 0f);
                indepRect.anchorMax = new Vector2(1f, 0f);
                indepRect.pivot = new Vector2(1f, 0f);
                indepRect.anchoredPosition = new Vector2(-8f, 8f);
                indepRect.sizeDelta = new Vector2(76f, 18f);
                indepBtn.GetComponent<Button>().interactable = canIndep;
            }
            else
            {
                Transform row = CreateDiplomacyCard("suzerain", "宗主关系", "ui/icons/iconCrown",
                    new Color(0.20f, 0.21f, 0.26f, 0.95f), new Color(0.26f, 0.27f, 0.32f, 0.92f), 46f);
                CreateDiplomacyText(row, "<color=#8a8578>独立王国 · 不受制于人</color>", 8, new Vector2(42f, -28f), InnerWidth - 90f);
            }

            // ── 卡2：附庸诸国（宗主视角——阶段2M：贡金数+欠贡警示；2026-08-19 串联：占岁入比）──
            if (vassals.Count > 0)
            {
                // 宗主岁入（占比分母；取不到则不显示占比）
                long suzIncome = 0;
                try { var rep = TreasuryManager.Instance.GetReport(kingdomId); suzIncome = rep?.Income ?? 0; } catch { }

                float h = 48f + vassals.Count * 14f;
                Transform row = CreateDiplomacyCard("vassals", $"附庸诸国（{vassals.Count}）", "ui/icons/iconDiplomacy",
                    style.Solid, style.Bg, h);
                CreateDiplomacyText(row, "<color=#70d468>朝贡中</color> · <color=#8a8578>每国每年贡金（人口÷2）入国库 + 稳定度 +1</color>", 8, new Vector2(42f, -28f), InnerWidth - 90f);
                for (int i = 0; i < vassals.Count; i++)
                {
                    Kingdom v = vassals[i];
                    if (v == null) continue;
                    // 贡金数（实时计算）+ 占宗主岁入比（朝贡分层——方案6.2）
                    string tribute = "贡 0 金";
                    try
                    {
                        int trib = ZhuluDiplomacyService.ComputeTribute(v);
                        tribute = $"贡 {trib} 金/年";
                        if (suzIncome > 0 && trib > 0)
                        {
                            int pct = (int)Mathf.Round(trib * 100f / suzIncome);
                            if (pct > 0) tribute += $"（占岁入{Math.Min(999, pct)}%）";
                        }
                    }
                    catch { }
                    // 欠贡警示（tribute_deficit ≥1）
                    string deficit = "";
                    try
                    {
                        long def = KingdomDataExt.GetLong(k, "tribute_deficit", 0);
                        if (def >= 1) deficit = " <color=#ff8a7a>⚠欠贡</color>";
                    }
                    catch { }
                    CreateDiplomacyText(row, $"<color={UI.ZhuluUIStyle.Hex.Positive}>◆ {v.name}</color> <color={UI.ZhuluUIStyle.Hex.Warn}>{tribute}</color>{deficit}", 9,
                        new Vector2(42f, -28f - 14f * (i + 1)), InnerWidth - 90f);
                }
            }
            else
            {
                Transform row = CreateDiplomacyCard("vassals", "附庸诸国", "ui/icons/iconDiplomacy",
                    new Color(0.20f, 0.21f, 0.26f, 0.95f), new Color(0.26f, 0.27f, 0.32f, 0.92f), 46f);
                CreateDiplomacyText(row, "<color=#8a8578>暂无附庸 · 可谋划「附庸战争」收服弱邻</color>", 8, new Vector2(42f, -28f), InnerWidth - 90f);
            }

            // ── 卡3：领土宣称 ──
            if (claims.Count > 0)
            {
                float h = 48f + claims.Count * 26f;
                Transform row = CreateDiplomacyCard("claims", $"领土宣称（{claims.Count}）", "ui/icons/iconWar",
                    new Color(0.24f, 0.16f, 0.09f, 0.95f), new Color(0.30f, 0.20f, 0.11f, 0.92f), h);
                int curYear = ZhuluTime.CurrentYear;
                for (int i = 0; i < claims.Count; i++)
                {
                    ClaimRecord claim = claims[i];
                    if (claim == null) continue;
                    float y = -28f - 26f * i;
                    string cityName = "未知城";
                    string ownerName = "未知国";
                    City city = null;
                    if (long.TryParse(claim.CityId, out long cid) && World.world?.cities != null)
                    {
                        city = World.world.cities.get(cid);
                    }
                    if (city != null)
                    {
                        cityName = city.data != null ? (city.data.name ?? "未知城") : "未知城";
                        Kingdom owner = ClaimManager.GetCityKingdom(city);
                        if (owner != null) ownerName = owner.name ?? "未知国";
                    }
                    int remain = claim.ExpireYear - curYear;
                    if (remain < 0) remain = 0;
                    CreateDiplomacyText(row, $"<color=#e0554a>⚔ {cityName}</color> <color=#8a8578>（现属 {ownerName}）· 剩{remain}年</color>", 9,
                        new Vector2(42f, y), InnerWidth - 130f);

                    ClaimRecord c2 = claim;
                    Kingdom attacker = k;
                    GameObject warBtn = CreateButton(row, "宣战", new Color(0.7f, 0.25f, 0.15f), () =>
                    {
                        if (ClaimManager.Instance.StartClaimWarFromUI(attacker, c2)) RebuildUI();
                    });
                    var warRect = warBtn.GetComponent<RectTransform>();
                    warRect.anchorMin = new Vector2(1f, 0f);
                    warRect.anchorMax = new Vector2(1f, 0f);
                    warRect.pivot = new Vector2(1f, 0f);
                    warRect.anchoredPosition = new Vector2(-8f, 8f + 26f * i);
                    warRect.sizeDelta = new Vector2(44f, 16f);
                }
            }
            else
            {
                Transform row = CreateDiplomacyCard("claims", "领土宣称", "ui/icons/iconWar",
                    new Color(0.20f, 0.21f, 0.26f, 0.95f), new Color(0.26f, 0.27f, 0.32f, 0.92f), 46f);
                CreateDiplomacyText(row, "<color=#8a8578>暂无领土宣称 · 可在谋划中「谋取宣称」</color>", 8, new Vector2(42f, -28f), InnerWidth - 90f);
            }

            // ── 2026-08-19 串联·方案6.2：和约与互不侵犯合并一卡（同类信息合卡省高度；扫描已提前）──
            if (napList.Count > 0 || truceList.Count > 0)
            {
                int total = napList.Count + truceList.Count;
                float h = 34f + total * 14f + 12f;
                Transform row = CreateDiplomacyCard("pacts", $"🕊 和约与约（{total}）", "ui/icons/iconDiplomacy",
                    new Color(0.08f, 0.16f, 0.24f, 0.95f), new Color(0.10f, 0.20f, 0.30f, 0.92f), h);
                int line = 0;
                for (int i = 0; i < napList.Count; i++)
                {
                    var n = napList[i];
                    CreateDiplomacyText(row, $"<color=#6ca3fd>◆ {n.other.name}</color> <color=#8a8578>互不侵犯 · 剩 {n.remain} 年</color>", 9,
                        new Vector2(42f, -22f - 14f * (++line)), InnerWidth - 90f);
                }
                for (int i = 0; i < truceList.Count; i++)
                {
                    var t = truceList[i];
                    CreateDiplomacyText(row, $"<color={UI.ZhuluUIStyle.Hex.Positive}>⚔ 与 {t.other.name} 停战</color> <color=#8a8578>剩 {t.remain} 年</color>", 9,
                        new Vector2(42f, -22f - 14f * (++line)), InnerWidth - 90f);
                }
            }
        }

        /// <summary>邦交卡 Row 容器（纯色底 -36 + Sliced 贴图 + 左上图标 + 标题行）</summary>
        private Transform CreateDiplomacyCard(string cardId, string title, string iconPath, Color solidColor, Color bgColor, float height)
        {
            GameObject row = new GameObject($"Dip_{cardId}", typeof(RectTransform));
            row.transform.SetParent(_contentTransform, false);
            var rowRect = row.GetComponent<RectTransform>();
            rowRect.sizeDelta = new Vector2(InnerWidth, height);

            GameObject solidObj = new GameObject("SolidBg", typeof(RectTransform), typeof(Image));
            solidObj.transform.SetParent(row.transform, false);
            var solidRect = solidObj.GetComponent<RectTransform>();
            solidRect.anchorMin = Vector2.zero;
            solidRect.anchorMax = Vector2.one;
            solidRect.offsetMin = new Vector2(-36f, 0f);
            solidRect.offsetMax = Vector2.zero;
            var solidImg = solidObj.GetComponent<Image>();
            solidImg.color = solidColor;
            solidImg.raycastTarget = false;

            GameObject bgObj = new GameObject("Bg", typeof(RectTransform), typeof(Image));
            bgObj.transform.SetParent(row.transform, false);
            var bgRect = bgObj.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = new Vector2(-36f, 0f);
            bgRect.offsetMax = Vector2.zero;
            var bgImg = bgObj.GetComponent<Image>();
            bgImg.color = bgColor;
            bgImg.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            bgImg.type = Image.Type.Sliced;
            bgImg.raycastTarget = false;

            // 左上图标（24×24 原版图标，失败静默）
            try
            {
                Sprite icon = SpriteTextureLoader.getSprite(iconPath);
                if (icon != null)
                {
                    GameObject iconObj = new GameObject("Icon", typeof(RectTransform), typeof(Image));
                    iconObj.transform.SetParent(row.transform, false);
                    var iconRect = iconObj.GetComponent<RectTransform>();
                    iconRect.anchorMin = new Vector2(0f, 1f);
                    iconRect.anchorMax = new Vector2(0f, 1f);
                    iconRect.pivot = new Vector2(0f, 1f);
                    iconRect.anchoredPosition = new Vector2(10f, -8f);
                    iconRect.sizeDelta = new Vector2(24f, 24f);
                    var iconImg = iconObj.GetComponent<Image>();
                    iconImg.sprite = icon;
                    iconImg.type = Image.Type.Simple;
                    iconImg.preserveAspect = true;
                    iconImg.raycastTarget = false;
                }
            }
            catch { }

            // 标题行
            CreateDiplomacyText(row.transform, $"<color={ZhuluUIStyle.Hex.Gold}><b>{title}</b></color>", 10, new Vector2(42f, -12f), InnerWidth - 60f);

            return row.transform;
        }

        /// <summary>邦交卡内绝对定位文本（顶部锚点）</summary>
        private Text CreateDiplomacyText(Transform row, string content, int fontSize, Vector2 pos, float width)
        {
            Text t = CreateText(row, content, fontSize);
            var rt = t.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(width, 14f);
            return t;
        }

        #endregion

    }
}
