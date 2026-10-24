using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using HarmonyLib;
using ZhuluRebuild.Core;
using ZhuluRebuild.Domain;
using ZhuluRebuild.Presentation.Routing;
using ZhuluRebuild.UI.Windows;

namespace ZhuluRebuild.Adapters
{
    [HarmonyPatch]
    public static class KingdomWindowTabsPatch
    {
        private const string RightSideContainerName = "zl_right_side";

        [HarmonyPostfix]
        [HarmonyPatch(typeof(KingdomWindow), "showStatsRows")]
        public static void ShowStatsRowsPostfix(KingdomWindow __instance)
        {
            TryAddRightSideButtons(__instance);
        }

        public static Kingdom GetKingdomFromWindow(KingdomWindow window)
        {
            if (window == null) return null;

            // 1. 首选权威源：0.51.2 反编译确认 KingdomWindow.meta_object => SelectedMetas.selected_kingdom（public static）
            try
            {
                var direct = SelectedMetas.selected_kingdom;
                if (direct != null && direct.isAlive()) return direct;
            }
            catch (Exception ex) { ZhuluContext.Instance.LogWarning($"[KingdomWindowTabsPatch] 读取选中王国异常: {ex.Message}"); }

            // 2. 兜底：反射读窗口的 meta_object 属性
            try
            {
                var pMeta = AccessTools.Property(window.GetType(), "meta_object");
                if (pMeta != null)
                {
                    var k = pMeta.GetValue(window) as Kingdom;
                    if (k != null) return k;
                }
            }
            catch (Exception ex) { ZhuluContext.Instance.LogWarning($"[KingdomWindowTabsPatch] 反射读取王国异常: {ex.Message}"); }

            return null;
        }

        private static void TryAddRightSideButtons(KingdomWindow window)
        {
            try
            {
                if (window == null) return;

                Kingdom currentKingdom = GetKingdomFromWindow(window);
                string kingdomId = currentKingdom != null ? currentKingdom.id.ToString() : "";
                string kingdomName = (currentKingdom != null) ? currentKingdom.name : "";

                ZhuluContext.Instance.LogInfo($"[KingdomWindowTabsPatch] 刷新王国侧边栏 -> 准确精准抓取当前窗口国家: {kingdomName} (ID: {kingdomId})");

                Transform existing = window.transform.Find(RightSideContainerName);
                if (existing != null)
                {
                    existing.gameObject.SetActive(true);
                    RebindButtonEvents(window, existing, kingdomId, kingdomName);
                    return;
                }

                // 创建右侧独立挂载容器 zl_right_side
                GameObject rightSideObj = new GameObject(RightSideContainerName, typeof(RectTransform), typeof(VerticalLayoutGroup));
                rightSideObj.transform.SetParent(window.transform, false);

                RectTransform rt = rightSideObj.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0f, 0.5f);
                rt.anchoredPosition = new Vector2(170f, 20f);
                rt.sizeDelta = new Vector2(36f, 300f); // 阶段3：8 按钮（32×8+间距7×5=291）

                VerticalLayoutGroup vlg = rightSideObj.GetComponent<VerticalLayoutGroup>();
                vlg.childAlignment = TextAnchor.UpperLeft;
                vlg.spacing = 5f;
                vlg.childControlWidth = false;
                vlg.childControlHeight = false;
                vlg.childForceExpandWidth = false;

                // 创建 5 大功能按钮：国势全景, 朝廷官制, 王国政策, 礼乐史册, 邦交纵横（navigation 图标贴图）
                CreateTabButton(rightSideObj.transform, "btn_zl_state", "国势", "ui/zhulu/navigation/zlr_nav_state_32", () => OnSideButtonClick(window, RouteDestination.KingdomState, kingdomId, kingdomName));
                CreateTabButton(rightSideObj.transform, "btn_zl_offices", "官制", "ui/zhulu/navigation/zlr_nav_offices_32", () => OnSideButtonClick(window, RouteDestination.KingdomOffices, kingdomId, kingdomName));
                CreateTabButton(rightSideObj.transform, "btn_zl_policies", "国策", "ui/zhulu/navigation/zlr_nav_policies_32", () => OnSideButtonClick(window, RouteDestination.KingdomPolicies, kingdomId, kingdomName));
                CreateTabButton(rightSideObj.transform, "btn_zl_diplomacy", "邦交", "ui/zhulu/navigation/zlr_nav_diplomacy_32", () => OnSideButtonClick(window, RouteDestination.KingdomDiplomacy, kingdomId, kingdomName));
                CreateTabButton(rightSideObj.transform, "btn_zl_chronicle", "史册", "ui/zhulu/navigation/zlr_nav_chronicle_32", () => OnSideButtonClick(window, RouteDestination.WorldChronicle, kingdomId, kingdomName));
                string selectionLabel = currentKingdom != null && KingdomCivilizationService.Instance.IsNomadic(currentKingdom)
                    ? "选贤" : "科举";
                CreateTabButton(rightSideObj.transform, "btn_zl_keju", selectionLabel, "ui/zhulu/navigation/zlr_nav_keju_32", () => OnSideButtonClick(window, RouteDestination.KingdomKeju, kingdomId, kingdomName));
                CreateTabButton(rightSideObj.transform, "btn_zl_faction", "党争", "ui/zhulu/navigation/zlr_nav_faction_32", () => OnSideButtonClick(window, RouteDestination.KingdomFaction, kingdomId, kingdomName));
                CreateTabButton(rightSideObj.transform, "btn_zl_lineage", "谱牒", "ui/zhulu/navigation/zlr_nav_dynasty_32", () => OnSideButtonClick(window, RouteDestination.KingdomLineage, kingdomId, kingdomName));
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[KingdomWindowTabsPatch] 挂载侧边栏 4 大按钮发生异常", ex);
            }
        }

        private static void RebindButtonEvents(KingdomWindow window, Transform container, string kingdomId, string kingdomName)
        {
            Kingdom currentKingdom = GetKingdomFromWindow(window);
            SetButtonLabel(container, "btn_zl_keju",
                currentKingdom != null && KingdomCivilizationService.Instance.IsNomadic(currentKingdom) ? "选贤" : "科举");
            RebindButton(container, "btn_zl_state", () => OnSideButtonClick(window, RouteDestination.KingdomState, kingdomId, kingdomName));
            RebindButton(container, "btn_zl_offices", () => OnSideButtonClick(window, RouteDestination.KingdomOffices, kingdomId, kingdomName));
            RebindButton(container, "btn_zl_policies", () => OnSideButtonClick(window, RouteDestination.KingdomPolicies, kingdomId, kingdomName));
            RebindButton(container, "btn_zl_diplomacy", () => OnSideButtonClick(window, RouteDestination.KingdomDiplomacy, kingdomId, kingdomName));
            RebindButton(container, "btn_zl_chronicle", () => OnSideButtonClick(window, RouteDestination.WorldChronicle, kingdomId, kingdomName));
            RebindButton(container, "btn_zl_keju", () => OnSideButtonClick(window, RouteDestination.KingdomKeju, kingdomId, kingdomName));
            RebindButton(container, "btn_zl_faction", () => OnSideButtonClick(window, RouteDestination.KingdomFaction, kingdomId, kingdomName));
            RebindButton(container, "btn_zl_lineage", () => OnSideButtonClick(window, RouteDestination.KingdomLineage, kingdomId, kingdomName));
        }

        private static void SetButtonLabel(Transform parent, string name, string label)
        {
            Transform button = parent?.Find(name);
            Text text = button?.Find("Text")?.GetComponent<Text>();
            if (text != null) text.text = label;
        }

        private static void RebindButton(Transform parent, string name, Action onClick)
        {
            Transform b = parent.Find(name);
            if (b != null)
            {
                Button btn = b.GetComponent<Button>();
                if (btn != null)
                {
                    btn.onClick.RemoveAllListeners();
                    btn.onClick.AddListener(() => onClick?.Invoke());
                }
            }
        }

        private static void CreateTabButton(Transform parent, string name, string labelText, string spritePath, Action onClick)
        {
            GameObject bObj = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            bObj.transform.SetParent(parent, false);

            RectTransform rt = bObj.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(32f, 32f);

            Image img = bObj.GetComponent<Image>();
            img.color = Color.white;
            // 贴图图标（失败回退原色块，防白块）
            try
            {
                Sprite sp = SpriteTextureLoader.getSprite(spritePath);
                if (sp != null)
                {
                    img.sprite = sp;
                    img.type = Image.Type.Simple;
                    img.preserveAspect = true;
                }
                else
                {
                    img.color = new Color(0.18f, 0.22f, 0.30f, 0.95f);
                }
            }
            catch
            {
                img.color = new Color(0.18f, 0.22f, 0.30f, 0.95f);
            }

            Button btn = bObj.GetComponent<Button>();
            btn.onClick.AddListener(() => onClick?.Invoke());

            GameObject txtObj = new GameObject("Text", typeof(RectTransform), typeof(Text));
            txtObj.transform.SetParent(bObj.transform, false);

            Text txt = txtObj.GetComponent<Text>();
            txt.font = LocalizedTextManager.current_font;   // 中文支持字体（Arial 无中文字形）
            txt.fontSize = 8;
            txt.text = labelText;
            txt.alignment = TextAnchor.LowerCenter;
            txt.color = Color.white;
            txt.raycastTarget = false;

            RectTransform txtRt = txtObj.GetComponent<RectTransform>();
            txtRt.sizeDelta = new Vector2(32f, 12f);
            txtRt.anchorMin = new Vector2(0f, 0f);
            txtRt.anchorMax = new Vector2(1f, 0f);
            txtRt.pivot = new Vector2(0.5f, 0f);
            txtRt.anchoredPosition = new Vector2(0f, 1f);
        }

        private static void OnSideButtonClick(KingdomWindow window, RouteDestination dest, string kingdomId, string kingdomName)
        {
            // 实时重新提取以防按钮重绑定遗留
            Kingdom k = GetKingdomFromWindow(window);
            if (k != null)
            {
                kingdomId = k.id.ToString();
                if (!string.IsNullOrEmpty(k.name)) kingdomName = k.name;
            }

            ZhuluContext.Instance.LogInfo($"[KingdomWindowTabsPatch] 侧边栏按钮点击 -> 目标路由: {dest}, 所选国家: {kingdomName} (ID: {kingdomId})");

            // 隐藏原版王国信息窗口，防止两个窗口重叠（逐鹿页面在中央偏右，左侧会露出原版列表）
            // 用 GameObject 显隐（原版 showWindow 会重新 SetActive(true)，窗口栈不受影响）
            try
            {
                if (window != null)
                {
                    window.gameObject.SetActive(false);
                }
            }
            catch { }

            UiRouter.Instance.NavigateTo(dest, kingdomId: kingdomId);

            if (dest == RouteDestination.WorldChronicle)
            {
                // 史册走独立的史册窗口，不再错开到总览窗口
                if (ZhuluChronicleWindow.Instance != null)
                {
                    ZhuluChronicleWindow.Instance.Show();
                }
                ScrollWindow.showWindow(Main.ChronicleWindowId);
                return;
            }

            if (dest == RouteDestination.KingdomKeju)
            {
                // 科举走独立窗口
                if (ZhuluKejuWindow.Instance != null)
                {
                    ZhuluKejuWindow.Instance.Show();
                }
                ScrollWindow.showWindow(Main.KejuWindowId);
                return;
            }

            if (dest == RouteDestination.KingdomFaction)
            {
                // 党争走独立窗口
                if (ZhuluFactionWindow.Instance != null)
                {
                    ZhuluFactionWindow.Instance.Show();
                }
                ScrollWindow.showWindow(Main.FactionWindowId);
                return;
            }

            if (dest == RouteDestination.KingdomLineage)
            {
                // 谱牒走独立窗口（阶段3 F4）
                ScrollWindow.showWindow(Main.LineageWindowId);
                return;
            }

            if (WorldOverviewWindow.Instance != null)
            {
                // 记录入口来源：从王国信息窗口进入 → 返回按钮走原版 clickBack 窗口栈回王国信息
                WorldOverviewWindow.StateEntrySource = "kingdom_info";
                WorldOverviewWindow.Instance.Show();
                ScrollWindow.showWindow(Main.OverviewWindowId);
            }
        }
    }
}
