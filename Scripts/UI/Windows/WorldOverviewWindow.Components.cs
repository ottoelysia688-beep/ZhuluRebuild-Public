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


// ── 由 WorldOverviewWindow.cs 按 region 拆出的 partial 部分：6. 通用组件构建器 ──
// 2026-10-23 [P1-1] 纯文件级拆分，编译后仍为同一个类型，无任何行为改动。
namespace ZhuluRebuild.UI.Windows
{
    public partial class WorldOverviewWindow : MonoBehaviour
    {
        #region 通用组件构建器
        private void CreateHeaderBanner(string title, string subtitle)
        {
            GameObject bannerObj = new GameObject("HeaderBanner", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup));
            bannerObj.transform.SetParent(_contentTransform, false);

            RectTransform rect = bannerObj.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(InnerWidth, 38f);

            Image img = bannerObj.GetComponent<Image>();
            img.color = new Color(0.12f, 0.14f, 0.18f, 0.95f);

            VerticalLayoutGroup group = bannerObj.GetComponent<VerticalLayoutGroup>();
            group.padding = new RectOffset(4, 4, 3, 3);
            group.childAlignment = TextAnchor.MiddleCenter;

            Text titleText = CreateText(bannerObj.transform, $"<b><color={UI.ZhuluUIStyle.Hex.Imperial}>{title}</color></b>", 10);
            titleText.alignment = TextAnchor.MiddleCenter;

            Text subText = CreateText(bannerObj.transform, $"<color=#CCCCCC>{subtitle}</color>", 9);
            subText.alignment = TextAnchor.MiddleCenter;
        }

        private Text CreateText(Transform parent, string content, int fontSize)
        {
            // 委托共享版；差异补丁：本地版原本不设对齐（保持 Unity 默认 UpperLeft），共享版会设 MiddleCenter
            Text text = ZhuluUIStyle.CreateText(parent, content, fontSize);
            text.alignment = TextAnchor.UpperLeft;   // Unity 默认对齐
            return text;
        }

        private GameObject CreateButton(Transform parent, string label, Color color, Action onClick)
        {
            GameObject btnObj = new GameObject($"Btn_{label}", typeof(RectTransform), typeof(Image), typeof(Button));
            btnObj.transform.SetParent(parent, false);

            RectTransform rect = btnObj.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(170f, 20f);

            Image img = btnObj.GetComponent<Image>();
            img.color = color;

            Button btn = btnObj.GetComponent<Button>();
            btn.onClick.AddListener(() => onClick?.Invoke());

            Text txt = CreateText(btnObj.transform, $"<b>{label}</b>", 9);
            txt.alignment = TextAnchor.MiddleCenter;

            return btnObj;
        }

        private Actor FindActorById(string actorId)
        {
            if (string.IsNullOrEmpty(actorId)) return null;
            try
            {
                if (World.world?.kingdoms?.list == null) return null;
                foreach (Kingdom k in World.world.kingdoms.list)
                {
                    if (k == null || !k.isAlive() || k.units == null) continue;
                    foreach (Actor unit in k.units)
                    {
                        if (unit == null || !unit.isAlive()) continue;
                        if (unit.getID().ToString() == actorId) return unit;
                    }
                }
            }
            catch { }
            return null;
        }

        #endregion
    }
}
