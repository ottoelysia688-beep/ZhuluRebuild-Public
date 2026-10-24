using System;
using UnityEngine;
using UnityEngine.UI;
using HarmonyLib;
using ZhuluRebuild.Core;
using ZhuluRebuild.UI.Windows;

namespace ZhuluRebuild.Adapters
{
    /// <summary>
    /// 原版家族信息窗口（ClanWindow）右侧挂两个按钮（阶段3）：
    /// ①「大事」→ ZhuluClanEventsWindow（族长更替/族中在朝/族中大事）
    /// ②「简介」→ ZhuluFamilyWindow Clan 模式（古风世系+介绍）
    /// 当前选中家族：SelectedMetas.selected_clan（0.51.2 反编译确认）。
    /// </summary>
    [HarmonyPatch(typeof(ClanWindow), "showStatsRows")]
    public static class ClanWindowFamilyPatch
    {
        private const string ContainerName = "zl_clan_side";

        public static void Postfix(ClanWindow __instance)
        {
            try
            {
                if (__instance == null) return;
                TryAddButtons(__instance);
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[ClanWindowFamilyPatch] 挂载异常: {ex.Message}");
            }
        }

        private static void TryAddButtons(ClanWindow window)
        {
            Transform existing = window.transform.Find(ContainerName);
            if (existing != null)
            {
                if (!existing.gameObject.activeSelf) existing.gameObject.SetActive(true);
                return;
            }

            // 右侧竖排容器（王国窗口 zl_right_side 同款）
            var sideObj = new GameObject(ContainerName, typeof(RectTransform), typeof(VerticalLayoutGroup));
            sideObj.transform.SetParent(window.transform, false);

            var rt = sideObj.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.anchoredPosition = new Vector2(165f, 0f);
            rt.sizeDelta = new Vector2(36f, 68f);

            var vlg = sideObj.GetComponent<VerticalLayoutGroup>();
            vlg.childAlignment = TextAnchor.UpperLeft;
            vlg.spacing = 4f;
            vlg.childControlWidth = false;
            vlg.childControlHeight = false;
            vlg.childForceExpandWidth = false;
            vlg.childForceExpandHeight = false;

            CreateSideButton(sideObj.transform, "btn_zl_clan_events", "大事",
                "ui/zhulu/navigation/zlr_nav_clan_events_32", () =>
            {
                try
                {
                    var clan = SelectedMetas.selected_clan;
                    if (clan == null) { WorldTip.showNow("未选中家族", false, "top", 1.5f); return; }
                    if (ZhuluClanEventsWindow.Instance != null)
                        ZhuluClanEventsWindow.Instance.Show(clan);
                    ScrollWindow.showWindow(Main.ClanEventsWindowId);
                }
                catch (Exception ex) { ZhuluContext.Instance.LogWarning($"[ClanWindowFamilyPatch] 大事异常: {ex.Message}"); }
            });

            CreateSideButton(sideObj.transform, "btn_zl_clan_intro", "简介",
                "ui/zhulu/navigation/zlr_nav_clan_intro_32", () =>
            {
                try
                {
                    var clan = SelectedMetas.selected_clan;
                    if (clan == null) { WorldTip.showNow("未选中家族", false, "top", 1.5f); return; }
                    if (ZhuluFamilyWindow.Instance != null)
                        ZhuluFamilyWindow.Instance.Show(clan);
                    ScrollWindow.showWindow(Main.FamilyWindowId);
                }
                catch (Exception ex) { ZhuluContext.Instance.LogWarning($"[ClanWindowFamilyPatch] 简介异常: {ex.Message}"); }
            });

            ZhuluContext.Instance.LogInfo("[ClanWindowFamilyPatch] 原版家族窗口侧边按钮已挂载（大事+简介）");
        }

        /// <summary>32×32 图标 + 底部文字按钮（王国侧边栏同款样式）。</summary>
        private static void CreateSideButton(Transform parent, string name, string label, string spritePath, Action onClick)
        {
            var btnObj = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            btnObj.transform.SetParent(parent, false);

            var rt = btnObj.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(32f, 32f);

            var img = btnObj.GetComponent<Image>();
            try
            {
                var sp = SpriteTextureLoader.getSprite(spritePath);
                if (sp != null)
                {
                    img.sprite = sp;
                    img.type = Image.Type.Simple;
                    img.preserveAspect = true;
                    img.color = Color.white;
                }
                else img.color = new Color(0.18f, 0.22f, 0.30f, 0.95f);
            }
            catch { img.color = new Color(0.18f, 0.22f, 0.30f, 0.95f); }

            var txtObj = new GameObject("Text", typeof(RectTransform), typeof(Text));
            txtObj.transform.SetParent(btnObj.transform, false);
            var txt = txtObj.GetComponent<Text>();
            txt.font = LocalizedTextManager.current_font;
            txt.fontSize = 8;
            txt.text = label;
            txt.alignment = TextAnchor.LowerCenter;
            txt.color = Color.white;
            txt.raycastTarget = false;
            var txtRt = txtObj.GetComponent<RectTransform>();
            txtRt.sizeDelta = new Vector2(32f, 12f);
            txtRt.anchorMin = new Vector2(0f, 0f);
            txtRt.anchorMax = new Vector2(1f, 0f);
            txtRt.pivot = new Vector2(0.5f, 0f);
            txtRt.anchoredPosition = new Vector2(0f, 1f);

            btnObj.GetComponent<Button>().onClick.AddListener(() => onClick?.Invoke());
        }
    }
}
