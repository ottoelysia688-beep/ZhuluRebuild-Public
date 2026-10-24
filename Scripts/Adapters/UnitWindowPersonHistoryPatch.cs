using System;
using UnityEngine;
using UnityEngine.UI;
using HarmonyLib;
using ZhuluRebuild.Core;
using ZhuluRebuild.Naming;
using ZhuluRebuild.UI.Windows;

namespace ZhuluRebuild.Adapters
{
    /// <summary>
    /// 单位信息窗口（UnitWindow）挂载「个人史册」按钮。
    /// 位置参考国家信息窗口右侧那排按钮（王国信息窗口 zl_right_side 同款）：
    /// 窗口右侧 170px 竖排、32×32 导航图标 + 底部文字，独立创建（不克隆原版按钮，防 inactive 继承）。
    /// 点击 → 打开 ZhuluPersonHistoryWindow 展示当前选中角色个人史册。
    /// </summary>
    [HarmonyPatch(typeof(UnitWindow), "OnEnable")]
    public static class UnitWindowPersonHistoryPatch
    {
        private const string ButtonName = "btn_zl_person_history";

        public static void Postfix(UnitWindow __instance)
        {
            try
            {
                if (__instance == null) return;
                TryAddButton(__instance);
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[UnitWindowPersonHistoryPatch] 挂载异常: {ex.Message}");
            }
        }

        private static void TryAddButton(UnitWindow window)
        {
            Transform existing = window.transform.Find(ButtonName);
            if (existing != null)
            {
                // 已存在：确保激活（原版可能隐藏）
                if (!existing.gameObject.activeSelf) existing.gameObject.SetActive(true);
                return;
            }

            // 按钮主体：32×32 图标 + 底部文字（国家信息窗口侧边按钮同款：纯图标无底，防黑块）
            GameObject btnObj = new GameObject(ButtonName, typeof(RectTransform), typeof(Image), typeof(Button));
            btnObj.transform.SetParent(window.transform, false);
            btnObj.transform.SetAsLastSibling();

            RectTransform rt = btnObj.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.anchoredPosition = new Vector2(160f, 20f);
            rt.sizeDelta = new Vector2(32f, 44f);

            // 图标（导航史册图，失败纯色块兜底；不加底 Image——backgroundKingdomElement 左装饰会变黑块）
            Image img = btnObj.GetComponent<Image>();
            try
            {
                Sprite sp = SpriteTextureLoader.getSprite("ui/zhulu/navigation/zlr_nav_person_32");
                if (sp != null)
                {
                    img.sprite = sp;
                    img.type = Image.Type.Simple;
                    img.preserveAspect = true;
                    img.color = Color.white;
                }
                else
                {
                    img.color = new Color(0.85f, 0.85f, 0.92f, 1f);
                }
            }
            catch
            {
                img.color = new Color(0.85f, 0.85f, 0.92f, 1f);
            }

            // 文字（底部）
            GameObject txtObj = new GameObject("Text", typeof(RectTransform), typeof(Text));
            txtObj.transform.SetParent(btnObj.transform, false);
            Text txt = txtObj.GetComponent<Text>();
            txt.font = LocalizedTextManager.current_font;   // 中文支持字体
            txt.fontSize = 8;
            txt.text = "史册";
            txt.alignment = TextAnchor.LowerCenter;
            txt.color = Color.white;
            txt.raycastTarget = false;
            RectTransform txtRt = txtObj.GetComponent<RectTransform>();
            txtRt.sizeDelta = new Vector2(32f, 12f);
            txtRt.anchorMin = new Vector2(0f, 0f);
            txtRt.anchorMax = new Vector2(1f, 0f);
            txtRt.pivot = new Vector2(0.5f, 0f);
            txtRt.anchoredPosition = new Vector2(0f, 0f);

            // 点击：打开当前选中角色的个人史册
            Button btn = btnObj.GetComponent<Button>();
            btn.onClick.AddListener(() =>
            {
                try
                {
                    Actor current = SelectedUnit.unit; // 原版当前选中单位
                    if (current == null)
                    {
                        WorldTip.showNow("请先选择一位角色", false, "top", 2f);
                        return;
                    }
                    if (ZhuluPersonHistoryWindow.Instance != null)
                    {
                        ZhuluPersonHistoryWindow.Instance.Show(current);
                        ScrollWindow.showWindow(Main.PersonHistoryWindowId);
                    }
                }
                catch (Exception ex)
                {
                    ZhuluContext.Instance.LogWarning($"[UnitWindowPersonHistoryPatch] 打开个人史册异常: {ex.Message}");
                }
            });

            ZhuluContext.Instance.LogInfo("[UnitWindowPersonHistoryPatch] 单位窗口个人史册按钮已挂载（右侧竖排样式）");

            // ── 阶段3 改名功能：第二个按钮「改名」（史册按钮下方）——不依赖原版编辑器路径，走逐鹿接受链 ──
            TryAddRenameButton(window);
        }

        private const string RenameButtonName = "btn_zl_rename";

        private static void TryAddRenameButton(UnitWindow window)
        {
            Transform existing = window.transform.Find(RenameButtonName);
            if (existing != null)
            {
                if (!existing.gameObject.activeSelf) existing.gameObject.SetActive(true);
                return;
            }

            GameObject btnObj = new GameObject(RenameButtonName, typeof(RectTransform), typeof(Image), typeof(Button));
            btnObj.transform.SetParent(window.transform, false);
            btnObj.transform.SetAsLastSibling();

            RectTransform rt = btnObj.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.anchoredPosition = new Vector2(160f, -24f); // 史册按钮（y=20）下方
            rt.sizeDelta = new Vector2(32f, 44f);

            Image img = btnObj.GetComponent<Image>();
            img.color = new Color(0.2f, 0.24f, 0.32f, 0.9f);
            try
            {
                Sprite sp = SpriteTextureLoader.getSprite("ui/zhulu/navigation/zlr_nav_manual_32");
                if (sp != null)
                {
                    img.sprite = sp;
                    img.type = Image.Type.Sliced;
                    img.color = new Color(0.75f, 0.85f, 1f, 0.95f);
                }
            }
            catch { }

            GameObject txtObj = new GameObject("Text", typeof(RectTransform), typeof(Text));
            txtObj.transform.SetParent(btnObj.transform, false);
            Text txt = txtObj.GetComponent<Text>();
            txt.font = LocalizedTextManager.current_font;
            txt.fontSize = 8;
            txt.text = "改名";
            txt.alignment = TextAnchor.LowerCenter;
            txt.color = Color.white;
            txt.raycastTarget = false;
            RectTransform txtRt = txtObj.GetComponent<RectTransform>();
            txtRt.sizeDelta = new Vector2(32f, 12f);
            txtRt.anchorMin = new Vector2(0f, 0f);
            txtRt.anchorMax = new Vector2(1f, 0f);
            txtRt.pivot = new Vector2(0.5f, 0f);
            txtRt.anchoredPosition = new Vector2(0f, 0f);

            btnObj.GetComponent<Button>().onClick.AddListener(() =>
            {
                try
                {
                    Actor current = SelectedUnit.unit;
                    if (current == null)
                    {
                        WorldTip.showNow("请先选择一位角色", false, "top", 2f);
                        return;
                    }
                    ShowActorRenamePopup(window, current);
                }
                catch (Exception ex)
                {
                    ZhuluContext.Instance.LogWarning($"[UnitWindowPersonHistoryPatch] 打开改名弹层异常: {ex.Message}");
                }
            });
        }

        /// <summary>阶段3：人名改名弹层（输入框 + 确定/取消；改姓自动联动全族）。</summary>
        private static void ShowActorRenamePopup(UnitWindow window, Actor actor)
        {
            try
            {
                Transform old = window.transform.Find("zl_actor_rename_panel");
                if (old != null) { UnityEngine.Object.Destroy(old.gameObject); return; }

                GameObject panel = new GameObject("zl_actor_rename_panel", typeof(RectTransform), typeof(Image));
                panel.transform.SetParent(window.transform, false);
                var pRect = panel.GetComponent<RectTransform>();
                pRect.anchorMin = Vector2.zero;
                pRect.anchorMax = Vector2.one;
                pRect.offsetMin = Vector2.zero;
                pRect.offsetMax = Vector2.zero;
                panel.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.6f);

                GameObject box = new GameObject("Box", typeof(RectTransform), typeof(Image));
                box.transform.SetParent(panel.transform, false);
                var bRect = box.GetComponent<RectTransform>();
                bRect.anchorMin = new Vector2(0.5f, 0.5f);
                bRect.anchorMax = new Vector2(0.5f, 0.5f);
                bRect.sizeDelta = new Vector2(230f, 92f);
                var bImg = box.GetComponent<Image>();
                bImg.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
                bImg.type = Image.Type.Sliced;
                bImg.color = new Color(0.13f, 0.14f, 0.18f, 0.98f);

                string currentName = ZhuluChineseNameService.Instance.GetTrackedName(actor)
                    ?? ActorAccess.GetRawName(actor) ?? "";
                Text title = CreatePopupText(box.transform, $"<color={UI.ZhuluUIStyle.Hex.Warn}>改名 · {currentName}</color>", 10);
                var tR = title.GetComponent<RectTransform>();
                tR.anchorMin = new Vector2(0, 1); tR.anchorMax = new Vector2(1, 1);
                tR.pivot = new Vector2(0.5f, 1); tR.anchoredPosition = Vector2.zero;
                tR.sizeDelta = new Vector2(0f, 18f);

                GameObject inputObj = new GameObject("Input", typeof(RectTransform), typeof(Image), typeof(InputField));
                inputObj.transform.SetParent(box.transform, false);
                var iRect = inputObj.GetComponent<RectTransform>();
                iRect.anchorMin = new Vector2(0, 1); iRect.anchorMax = new Vector2(1, 1);
                iRect.pivot = new Vector2(0.5f, 1);
                iRect.anchoredPosition = new Vector2(0, -24);
                iRect.sizeDelta = new Vector2(-22f, 24f);
                inputObj.GetComponent<Image>().color = new Color(0.08f, 0.09f, 0.12f, 1f);

                var input = BuildInputField(inputObj, currentName, "输入新姓名（改姓联动全族）", 12);

                GameObject okBtn = CreatePopupButton(box.transform, "确定", new Color(0.2f, 0.5f, 0.28f), () =>
                {
                    string name = (input.text ?? "").Trim();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        ZhuluChineseNameService.Instance.NotifyManualActorRename(actor, name);
                        WorldTip.showNow($"已改名：{name}", false, "top", 3f);
                    }
                    UnityEngine.Object.Destroy(panel);
                });
                var okR = okBtn.GetComponent<RectTransform>();
                okR.anchorMin = new Vector2(0.5f, 0); okR.anchorMax = new Vector2(0.5f, 0);
                okR.pivot = new Vector2(0.5f, 0);
                okR.anchoredPosition = new Vector2(-50f, 8f);
                okR.sizeDelta = new Vector2(62f, 20f);

                GameObject cancelBtn = CreatePopupButton(box.transform, "取消", new Color(0.4f, 0.3f, 0.3f),
                    () => UnityEngine.Object.Destroy(panel));
                var cR = cancelBtn.GetComponent<RectTransform>();
                cR.anchorMin = new Vector2(0.5f, 0); cR.anchorMax = new Vector2(0.5f, 0);
                cR.pivot = new Vector2(0.5f, 0);
                cR.anchoredPosition = new Vector2(50f, 8f);
                cR.sizeDelta = new Vector2(62f, 20f);
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[UnitWindowPersonHistoryPatch] 改名弹层异常: {ex.Message}");
            }
        }

        private static Text CreatePopupText(Transform parent, string content, int size)
        {
            GameObject obj = new GameObject("T", typeof(RectTransform), typeof(Text));
            obj.transform.SetParent(parent, false);
            Text t = obj.GetComponent<Text>();
            t.font = LocalizedTextManager.current_font;
            t.fontSize = size;
            t.text = content;
            t.alignment = TextAnchor.MiddleCenter;
            t.raycastTarget = false;
            return t;
        }

        private static GameObject CreatePopupButton(Transform parent, string label, Color color, Action onClick)
        {
            GameObject btnObj = new GameObject("B", typeof(RectTransform), typeof(Image), typeof(Button));
            btnObj.transform.SetParent(parent, false);
            btnObj.GetComponent<Image>().color = color;
            Text t = CreatePopupText(btnObj.transform, label, 9);
            var tr = t.GetComponent<RectTransform>();
            tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one;
            tr.offsetMin = Vector2.zero; tr.offsetMax = Vector2.zero;
            btnObj.GetComponent<Button>().onClick.AddListener(() => onClick());
            return btnObj;
        }

        private static InputField BuildInputField(GameObject inputObj, string initial, string placeholder, int limit)
        {
            GameObject textObj = new GameObject("Text", typeof(RectTransform), typeof(Text));
            textObj.transform.SetParent(inputObj.transform, false);
            var txR = textObj.GetComponent<RectTransform>();
            txR.anchorMin = Vector2.zero; txR.anchorMax = Vector2.one;
            txR.offsetMin = new Vector2(6f, 2f); txR.offsetMax = new Vector2(-6f, -2f);
            Text tx = textObj.GetComponent<Text>();
            tx.font = LocalizedTextManager.current_font;
            tx.fontSize = 11;
            tx.color = new Color(0.93f, 0.9f, 0.82f);
            tx.alignment = TextAnchor.MiddleLeft;

            GameObject phObj = new GameObject("Placeholder", typeof(RectTransform), typeof(Text));
            phObj.transform.SetParent(inputObj.transform, false);
            var phR = phObj.GetComponent<RectTransform>();
            phR.anchorMin = Vector2.zero; phR.anchorMax = Vector2.one;
            phR.offsetMin = new Vector2(6f, 2f); phR.offsetMax = new Vector2(-6f, -2f);
            Text ph = phObj.GetComponent<Text>();
            ph.font = LocalizedTextManager.current_font;
            ph.fontSize = 10;
            ph.fontStyle = FontStyle.Italic;
            ph.color = new Color(0.5f, 0.5f, 0.5f, 0.6f);
            ph.text = placeholder;

            InputField input = inputObj.GetComponent<InputField>();
            input.textComponent = tx;
            input.placeholder = ph;
            input.text = initial ?? "";
            input.characterLimit = limit;
            input.ActivateInputField();
            return input;
        }
    }
}
