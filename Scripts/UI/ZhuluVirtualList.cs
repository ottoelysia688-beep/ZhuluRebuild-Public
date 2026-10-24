using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZhuluRebuild.UI
{
    /// <summary>
    /// 泛型虚拟列表（批次9b，2026-08-23）：固定行高 + 可见区增删的实例管理器。
    /// 抽取自战争窗孤本回收器（RenderVisibleRows/_rowByIndex/ClearRows 三合一）——
    /// 用法：宿主持有本类实例；数据变化调 SetItems（重置滚动位）；宿主每帧/滚动回调调 Refresh。
    /// 策略说明：滚出缓冲区即销毁、滚入即从 prefab Instantiate（非池化复用）——
    /// 静止滚动时每帧零分配；窗口关闭时实例随窗口销毁，无跨局残留，省去池化脏字段清理的复杂度。
    /// 千年档收益：任意条数下同时存活对象 ≈ 可见行数+2，与总量无关。
    /// </summary>
    public sealed class ZhuluVirtualList<T>
    {
        private readonly RectTransform _viewport;   // 测量可见高度
        private readonly RectTransform _content;    // 虚拟容器（高度=Count×行高）
        private readonly Transform _contentTf;      // 行挂载点
        private readonly GameObject _rowPrefab;     // 行模板
        private readonly float _rowHeight;
        private readonly Action<GameObject, T, int> _bind;  // 数据绑定回调（行, 数据, 序号）

        private IReadOnlyList<T> _items = Array.Empty<T>();
        private readonly Dictionary<int, GameObject> _activeByIndex = new Dictionary<int, GameObject>();

        public int Count { get { return _items.Count; } }

        public ZhuluVirtualList(RectTransform viewport, RectTransform content, Transform contentParent,
                                GameObject rowPrefab, float rowHeight, Action<GameObject, T, int> bind)
        {
            _viewport = viewport;
            _content = content;
            _contentTf = contentParent;
            _rowPrefab = rowPrefab;
            _rowHeight = Mathf.Max(1f, rowHeight);
            _bind = bind;
        }

        /// <summary>换数据源：清全部活动行、content 高度=新数量×行高、回顶部。（与战争窗原 RefreshList 几何行为逐行等价）</summary>
        public void SetItems(IReadOnlyList<T> items)
        {
            _items = items ?? Array.Empty<T>();
            ClearAll();
            if (_content != null)
            {
                _content.sizeDelta = new Vector2(0f, _items.Count * _rowHeight);
                _content.anchoredPosition = Vector2.zero;
            }
            Refresh();
        }

        /// <summary>同一列表引用被原地重排/改内容后调用——只清行重建，不动高度与滚动位。</summary>
        public void Reload()
        {
            ClearAll();
            Refresh();
        }

        /// <summary>滚动驱动：算可见区间（±1 行缓冲），删区间外、补建区间内。</summary>
        public void Refresh()
        {
            if (_content == null || _viewport == null || _items.Count == 0) return;
            float scrollY = _content.anchoredPosition.y;
            float viewH = _viewport.rect.height;
            if (viewH <= 0f) return;

            int first = Mathf.Max(0, Mathf.FloorToInt(scrollY / _rowHeight) - 1);
            int last = Mathf.Min(Mathf.CeilToInt((scrollY + viewH) / _rowHeight) + 1, _items.Count - 1);

            List<int> doomed = null;
            foreach (var kvp in _activeByIndex)
            {
                if (kvp.Key < first || kvp.Key > last)
                {
                    if (kvp.Value != null) UnityEngine.Object.Destroy(kvp.Value);
                    (doomed ?? (doomed = new List<int>())).Add(kvp.Key);
                }
            }
            if (doomed != null) foreach (var k in doomed) _activeByIndex.Remove(k);

            for (int i = first; i <= last; i++)
            {
                if (i < 0 || i >= _items.Count || _activeByIndex.ContainsKey(i)) continue;
                GameObject row = UnityEngine.Object.Instantiate(_rowPrefab, _contentTf);
                row.SetActive(true);
                row.transform.localPosition = new Vector3(0f, -_rowHeight * 0.5f - i * _rowHeight, 0f);
                try { _bind(row, _items[i], i); }
                catch (Exception ex)
                {
                    try { Core.ZhuluContext.Instance.LogRateLimited("VirtualList.Bind",
                        "[VirtualList] 行绑定异常(限频): " + ex.Message, 30f); } catch { }
                }
                _activeByIndex[i] = row;
            }
        }

        /// <summary>销毁全部活动行（换筛选/排序/关详情前调用）。</summary>
        public void ClearAll()
        {
            foreach (var kvp in _activeByIndex)
            {
                if (kvp.Value != null) UnityEngine.Object.Destroy(kvp.Value);
            }
            _activeByIndex.Clear();
        }

        public void ScrollToTop()
        {
            if (_content != null) _content.anchoredPosition = Vector2.zero;
        }

        public void ScrollToBottom()
        {
            if (_content == null || _viewport == null) return;
            float maxY = Mathf.Max(0f, _content.sizeDelta.y - _viewport.rect.height);
            _content.anchoredPosition = new Vector2(0f, maxY);
        }
    }
}
