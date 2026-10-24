using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ZhuluRebuild.UI
{
    /// <summary>
    /// 窗口拖动（2026-08-20）：拖住标题牌匾移动整个窗口，双击牌匾复位。
    /// 原理：游戏 ScrollWindow.setActive 每次打开都会把窗口根节点 localPosition
    /// 重置为 (0,0,0)，故拖动目标必须是 Background（根的内层，不被游戏触碰）——
    /// 拖动偏移写在 Background.anchoredPosition 上，跨开关/跨场景持久。
    /// 挂载点：各窗口自建的 TitlePlaque（Image 本身 raycastTarget=true 即可接收拖拽）。
    /// </summary>
    public class ZhuluWindowDrag : MonoBehaviour, IBeginDragHandler, IDragHandler, IPointerClickHandler
    {
        private RectTransform _target;          // 被拖动的 Background
        private Vector2 _homePosition;          // 复位点（Attach 时快照）
        private Vector2 _startPointer;
        private Vector2 _startPos;
        private bool _hasHome;

        /// <summary>把句柄（标题牌匾）挂上拖动。target=窗口 Background。</summary>
        public static ZhuluWindowDrag Attach(Transform handle, RectTransform target)
        {
            if (handle == null || target == null) return null;
            var drag = handle.GetComponent<ZhuluWindowDrag>() ?? handle.gameObject.AddComponent<ZhuluWindowDrag>();
            drag._target = target;
            if (!drag._hasHome)
            {
                drag._homePosition = target.anchoredPosition;
                drag._hasHome = true;
            }
            return drag;
        }

        /// <summary>便捷入口：在 bg 下找 TitlePlaque 并挂载（十个窗口命名统一）。</summary>
        public static ZhuluWindowDrag AttachToPlaque(Transform bg)
        {
            if (bg == null) return null;
            var plaque = bg.Find("TitlePlaque");
            if (plaque == null) return null;
            // 牌匾必须可命中射线（拖拽前提）；其内文字由 CreateText 设为不挡射线）
            var img = plaque.GetComponent<Image>();
            if (img != null) img.raycastTarget = true;
            return Attach(plaque, bg as RectTransform ?? plaque.parent as RectTransform);
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (_target == null) return;
            _startPointer = eventData.position;
            _startPos = _target.anchoredPosition;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (_target == null) return;
            Vector2 pos = _startPos + (eventData.position - _startPointer);
            _target.anchoredPosition = ClampToParent(pos);
        }

        /// <summary>双击牌匾：窗口回到初始位置。</summary>
        public void OnPointerClick(PointerEventData eventData)
        {
            if (_target == null || eventData.clickCount < 2) return;
            _target.anchoredPosition = _homePosition;
        }

        /// <summary>软边界：窗口至少留 36px 在父级（画布）范围内，防止拖丢找不回。</summary>
        private Vector2 ClampToParent(Vector2 pos)
        {
            var parent = _target.parent as RectTransform;
            if (parent == null) return pos;
            float halfW = _target.rect.width * 0.5f;
            float halfH = _target.rect.height * 0.5f;
            float maxX = parent.rect.width * 0.5f + halfW - 36f;
            float maxY = parent.rect.height * 0.5f + halfH - 36f;
            pos.x = Mathf.Clamp(pos.x, -maxX, maxX);
            pos.y = Mathf.Clamp(pos.y, -maxY, maxY);
            return pos;
        }
    }
}
