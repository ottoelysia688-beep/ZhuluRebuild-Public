using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace ZhuluRebuild.UI
{
    /// <summary>
    /// 原版人物头像工具（2026-08-20）：世系树成员格/人物页复用。
    /// 活人：克隆原版 UiUnitAvatarElement（Resources "ui/UnitAvatarElement"）——
    ///       自带王国旗帜、君王/城主/将领底图、地形底纹，观感与原版单位窗一致。
    /// 死者：原版墓碑贴图（UnitAvatarLoader._died_sprite 反射提取，仅读一次）+ 灰度。
    /// 交互：实例内禁用一切射线/按钮——点击与悬浮统一由宿主（成员格）处理。
    /// </summary>
    public static class ZhuluAvatar
    {
        private static UiUnitAvatarElement _prefab;
        private static Sprite _diedSprite;
        private static bool _diedResolved;
        private static FieldInfo _diedSpriteField;

        /// <summary>原版头像控件 prefab（懒加载，失败返回 null）。</summary>
        public static UiUnitAvatarElement Prefab
        {
            get
            {
                if (_prefab == null)
                {
                    try { _prefab = Resources.Load<UiUnitAvatarElement>("ui/UnitAvatarElement"); }
                    catch { _prefab = null; }
                }
                return _prefab;
            }
        }

        /// <summary>原版死亡头像贴图（从 prefab 反射读取一次；无则 null）。</summary>
        public static Sprite DiedSprite
        {
            get
            {
                if (_diedResolved) return _diedSprite;
                _diedResolved = true;
                try
                {
                    if (_diedSpriteField == null)
                        _diedSpriteField = typeof(UnitAvatarLoader).GetField("_died_sprite",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                    var loader = Prefab != null ? Prefab.GetComponentInChildren<UnitAvatarLoader>(true) : null;
                    if (loader != null && _diedSpriteField != null)
                        _diedSprite = _diedSpriteField.GetValue(loader) as Sprite;
                }
                catch { _diedSprite = null; }
                return _diedSprite;
            }
        }

        /// <summary>
        /// 为活人创建原版头像（居中锚定于 parent，自然尺寸≈30px；宗族旗关闭、王国旗保留）。
        /// 返回 null 表示不可用（prefab 缺失/Actor 已故/异常），宿主自行降级。
        /// </summary>
        public static GameObject CreateLiving(Transform parent, Actor actor)
        {
            var prefab = Prefab;
            if (prefab == null || actor == null || !actor.isAlive()) return null;
            try
            {
                var el = Object.Instantiate(prefab, parent);
                var rt = el.GetComponent<RectTransform>();
                if (rt != null)
                {
                    rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                    rt.pivot = new Vector2(0.5f, 0.5f);
                    rt.anchoredPosition = Vector2.zero;
                    rt.localScale = Vector3.one;
                }
                el.show_banner_clan = false;   // 树内全员同宗，宗族旗冗余；王国旗保留（辨识国籍）
                el.show(actor);
                DisableInteraction(el.transform);
                return el.gameObject;
            }
            catch { return null; }
        }

        /// <summary>死者占位头像：灰度墓碑（无贴图时返回 null，宿主用文字占位降级）。</summary>
        public static GameObject CreateDead(Transform parent)
        {
            var sp = DiedSprite;
            if (sp == null) return null;
            try
            {
                var obj = new GameObject("DeadAvatar", typeof(RectTransform), typeof(Image));
                obj.transform.SetParent(parent, false);
                var rt = obj.GetComponent<RectTransform>();
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = Vector2.zero;
                rt.sizeDelta = new Vector2(24f, 24f);
                var img = obj.GetComponent<Image>();
                img.sprite = sp;
                img.preserveAspect = true;
                img.color = new Color(0.66f, 0.66f, 0.66f, 0.9f);   // 灰度化
                img.raycastTarget = false;
                return obj;
            }
            catch { return null; }
        }

        /// <summary>递归关闭实例的射线与按钮/悬浮组件（点击悬浮统一交给宿主处理）。</summary>
        private static void DisableInteraction(Transform root)
        {
            foreach (var img in root.GetComponentsInChildren<Image>(true))
                img.raycastTarget = false;
            foreach (var tip in root.GetComponentsInChildren<TipButton>(true))
                tip.enabled = false;
            foreach (var btn in root.GetComponentsInChildren<Button>(true))
                btn.enabled = false;
        }
    }
}
