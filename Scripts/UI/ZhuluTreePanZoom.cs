using UnityEngine;
using UnityEngine.EventSystems;

namespace ZhuluRebuild.UI
{
    /// <summary>
    /// 逐鹿树画布交互组件（2026-08-20）：拖拽平移 + 滚轮缩放（视口中心）+ 双击复位。
    /// 挂在视口上；成员格的 Button 不实现 IDragHandler，拖动事件沿 EventSystem
    /// 冒泡到本组件 → "点成员格 = 点击，拖任意处 = 平移画布"互不冲突。
    /// 坐标模型：画布 anchorMin=anchorMax=(0,1)、pivot=(0,1)（锚定视口左上角），
    /// 子元素以 (x, -yFromTop) 定位；本组件全部按该模型换算。
    /// </summary>
    public class ZhuluTreePanZoom : MonoBehaviour, IDragHandler, IBeginDragHandler, IScrollHandler, IPointerClickHandler
    {
        private RectTransform _canvas;      // 被平移/缩放的树画布
        private RectTransform _viewport;    // 可视区

        private const float MIN_SCALE = 0.35f;   // 最小缩放（大家族缩小说明总览）
        private const float MAX_SCALE = 1.6f;    // 最大缩放（看单格细节）
        private const float SCALE_STEP = 0.12f;  // 每格滚轮步长

        private Vector2 _startAnchored;
        private Vector2 _startPointer;

        /// <summary>挂载并绑定画布（幂等：已存在则复用）。</summary>
        public static ZhuluTreePanZoom Bind(RectTransform viewport, RectTransform canvas)
        {
            if (viewport == null || canvas == null) return null;
            var comp = viewport.GetComponent<ZhuluTreePanZoom>();
            if (comp == null) comp = viewport.gameObject.AddComponent<ZhuluTreePanZoom>();
            comp._canvas = canvas;
            comp._viewport = viewport;
            return comp;
        }

        /// <summary>复位：平移归零 + 缩放 1。</summary>
        public void ResetView()
        {
            if (_canvas == null) return;
            _canvas.anchoredPosition = Vector2.zero;
            _canvas.localScale = Vector3.one;
        }

        /// <summary>把画布上某点（x 从左、yFromTop 从上）滚到视口中心（初始定位中心成员用）。</summary>
        public void CenterOn(float x, float yFromTop)
        {
            if (_canvas == null || _viewport == null) return;
            float s = Mathf.Max(0.01f, _canvas.localScale.x);
            Vector2 viewportCenter = new Vector2(_viewport.rect.width * 0.5f, -_viewport.rect.height * 0.5f);
            Vector2 canvasPoint = new Vector2(x, -yFromTop);
            _canvas.anchoredPosition = viewportCenter - canvasPoint * s;
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (_canvas == null) return;
            _startAnchored = _canvas.anchoredPosition;
            _startPointer = eventData.position;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (_canvas == null) return;
            Vector2 delta = eventData.position - _startPointer;
            // 屏幕像素 → 画布单位（除以缩放，缩放后拖拽手感一致；屏幕 y 向上与锚点系同向）
            float s = Mathf.Max(0.01f, _canvas.localScale.x);
            _canvas.anchoredPosition = _startAnchored + delta / s;
        }

        public void OnScroll(PointerEventData eventData)
        {
            if (_canvas == null) return;
            float cur = _canvas.localScale.x;
            float next = Mathf.Clamp(cur + eventData.scrollDelta.y * SCALE_STEP, MIN_SCALE, MAX_SCALE);
            if (Mathf.Approximately(next, cur)) return;

            // 以视口中心为缩放锚：保持视口中心的画布点不动
            Vector2 viewportCenter = new Vector2(_viewport.rect.width * 0.5f, -_viewport.rect.height * 0.5f);
            Vector2 centerCanvasPoint = (viewportCenter - _canvas.anchoredPosition) / Mathf.Max(0.01f, cur);
            _canvas.localScale = new Vector3(next, next, 1f);
            _canvas.anchoredPosition = viewportCenter - centerCanvasPoint * next;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            // 双击空白复位（成员格 Button 拦截自身点击，不会冒泡成空白双击）
            if (eventData.clickCount == 2 && eventData.button == PointerEventData.InputButton.Left)
                ResetView();
        }
    }
}
