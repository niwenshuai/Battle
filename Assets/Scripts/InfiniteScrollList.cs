using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 无限循环列表控制器（垂直方向）。
///
/// 工作原理：
/// - 维护一个固定大小的环形对象池（ring buffer）。
/// - _firstPoolSlot  : 当前"最顶部"条目在池数组中的槽位。
/// - _firstDataIndex : 最顶部槽位对应的数据索引（可超出 dataCount，无模运算，
///                     用于正确计算 Content 内的绝对 Y 坐标）。
/// - 每次 ScrollRect.onValueChanged 时，用 while 循环持续将头/尾条目移到另一端，
///   直到覆盖当前可视范围，天然支持快速拖拽和大幅度跳转。
/// </summary>
[RequireComponent(typeof(ScrollRect))]
public class InfiniteScrollList : MonoBehaviour
{
    [Header("引用")]
    [Tooltip("单个条目预制体，需挂载 ScrollItemData 组件")]
    [SerializeField] private ScrollItemData _itemPrefab;

    [Header("布局参数")]
    [SerializeField] private float _itemHeight = 100f;
    [SerializeField] private float _spacing = 4f;
    [Tooltip("在可视区域上下各额外保留的缓冲条目数")]
    [SerializeField] private int _bufferCount = 2;

    // ── 运行时状态 ──────────────────────────────────────────
    private ScrollRect _scrollRect;
    private RectTransform _content;
    private readonly List<RectTransform> _itemRects = new();
    private readonly List<ScrollItemData> _itemViews = new();

    private List<string> _dataList = new();
    private int _poolSize;
    private float _stride;           // itemHeight + spacing

    // 环形缓冲区状态
    private int _firstPoolSlot;      // _itemRects 中"最顶部"条目的槽位
    private int _firstDataIndex;     // 最顶部槽位显示的数据绝对索引（不做模运算）

    // 滚动动画
    private Coroutine _scrollCoroutine;

    /// <summary>内置缓动类型。</summary>
    public enum Ease { Linear, QuadIn, QuadOut, QuadInOut, CubicOut, BackOut }

    // ── 生命周期 ────────────────────────────────────────────

    private void Awake()
    {
        _scrollRect = GetComponent<ScrollRect>();
        _content    = _scrollRect.content;
    }

    private void OnEnable()
    {
        _scrollRect.onValueChanged.AddListener(OnScrollValueChanged);
    }

    private void OnDisable()
    {
        _scrollRect.onValueChanged.RemoveListener(OnScrollValueChanged);
    }

    // ── 公共 API ────────────────────────────────────────────

    /// <summary>设置数据源并重建列表（回到顶部）。</summary>
    public void SetData(List<string> data)
    {
        _dataList = data ?? new List<string>();
        Rebuild();
    }

    /// <summary>
    /// 在列表末尾追加若干条数据，保持当前滚动位置不变。
    /// 若追加后对象池不足（首次数据量少于视口，pool 被限制），
    /// 会自动补充缺少的 Item 并刷新显示。
    /// </summary>
    public void AppendData(List<string> newItems)
    {
        if (newItems == null || newItems.Count == 0) return;

        // 如果列表为空则走初始化路径
        if (_dataList.Count == 0 || _poolSize == 0)
        {
            _dataList.AddRange(newItems);
            Rebuild();
            return;
        }

        int oldCount = _dataList.Count;
        _dataList.AddRange(newItems);

        // 1. 扩展 Content 高度，不移动滚动位置
        float totalHeight = _dataList.Count * _stride - _spacing;
        _content.sizeDelta = new Vector2(_content.sizeDelta.x, totalHeight);

        // 2. 如果之前 pool 被数据量限制（oldCount < 理想 pool 大小），补充 Item
        float viewportHeight = _scrollRect.viewport.rect.height;
        int idealPool = Mathf.CeilToInt(viewportHeight / _stride) + _bufferCount * 2;
        int targetPool = Mathf.Min(idealPool, _dataList.Count);

        while (_poolSize < targetPool)
        {
            // 新 Item 放在当前池底部的下一个位置
            int newAbsIndex = _firstDataIndex + _poolSize;
            if (newAbsIndex >= _dataList.Count) break;

            var item = Instantiate(_itemPrefab, _content);
            var rt   = item.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot     = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(rt.sizeDelta.x, _itemHeight);

            PlaceItem(rt, newAbsIndex);
            item.SetData(newAbsIndex, _dataList[newAbsIndex]);

            _itemRects.Add(rt);
            _itemViews.Add(item);
            _poolSize++;
        }

        // 3. 刷新当前可见条目（数据内容可能因索引对应关系改变而需要更新）
        RefreshVisible();
    }

    /// <summary>追加单条数据，保持当前滚动位置不变。</summary>
    public void AppendItem(string item) => AppendData(new List<string> { item });

    /// <summary>
    /// 滚动到指定数据索引，动画时长 duration 秒，缓动函数 ease。
    /// duration ≤ 0 则立即跳转。onComplete 在到达后回调。
    /// </summary>
    public void ScrollToIndex(int dataIndex, float duration = 0.4f,
                              Ease ease = Ease.QuadOut, Action onComplete = null)
    {
        if (_dataList.Count == 0 || _poolSize == 0) return;
        dataIndex = Mathf.Clamp(dataIndex, 0, _dataList.Count - 1);

        float targetY = GetTargetContentY(dataIndex);

        if (_scrollCoroutine != null) StopCoroutine(_scrollCoroutine);

        if (duration <= 0f)
        {
            SetContentY(targetY);
            UpdateItems();
            onComplete?.Invoke();
        }
        else
        {
            _scrollCoroutine = StartCoroutine(ScrollCoroutine(targetY, duration, ease, onComplete));
        }
    }

    /// <summary>立即跳转到指定索引，不播放动画。</summary>
    public void JumpToIndex(int dataIndex) => ScrollToIndex(dataIndex, 0f);

    // ── 内部方法 ────────────────────────────────────────────

    private void Rebuild()
    {
        foreach (var rt in _itemRects)
            if (rt != null) Destroy(rt.gameObject);
        _itemRects.Clear();
        _itemViews.Clear();

        if (_dataList.Count == 0 || _itemPrefab == null) return;

        _stride = _itemHeight + _spacing;

        float totalHeight = _dataList.Count * _stride - _spacing;
        _content.sizeDelta        = new Vector2(_content.sizeDelta.x, totalHeight);
        _content.anchoredPosition = Vector2.zero;

        float viewportHeight = _scrollRect.viewport.rect.height;
        int visibleCount = Mathf.CeilToInt(viewportHeight / _stride);
        _poolSize = Mathf.Min(visibleCount + _bufferCount * 2, _dataList.Count);

        _firstPoolSlot  = 0;
        _firstDataIndex = 0;

        for (int i = 0; i < _poolSize; i++)
        {
            var item = Instantiate(_itemPrefab, _content);
            var rt   = item.GetComponent<RectTransform>();

            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot     = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(rt.sizeDelta.x, _itemHeight);

            PlaceItem(rt, i);
            item.SetData(i, _dataList[i]);

            _itemRects.Add(rt);
            _itemViews.Add(item);
        }
    }

    /// <summary>将条目放置到绝对数据索引对应的 Y 坐标。</summary>
    private void PlaceItem(RectTransform rt, int absoluteDataIndex)
    {
        rt.anchoredPosition = new Vector2(0f, -absoluteDataIndex * _stride);
    }

    private void OnScrollValueChanged(Vector2 _)
    {
        if (_dataList.Count == 0 || _poolSize == 0) return;
        UpdateItems();
    }

    /// <summary>
    /// 根据当前 contentY 持续移动环形缓冲区头/尾，直到覆盖可视范围。
    /// 使用 while 循环确保快速拖拽或大幅跳转时也能完整追上。
    /// </summary>
    private void UpdateItems()
    {
        float contentY       = _content.anchoredPosition.y;
        float viewportHeight = _scrollRect.viewport.rect.height;

        // 期望的"第一个可见"数据索引（含上缓冲）
        int neededFirst = Mathf.FloorToInt(contentY / _stride) - _bufferCount;
        neededFirst = Mathf.Clamp(neededFirst, 0, Mathf.Max(0, _dataList.Count - _poolSize));

        // ── 向下滚动：把顶部条目循环移到底部 ──────────────
        while (_firstDataIndex < neededFirst)
        {
            int slot        = _firstPoolSlot;
            int newAbsIndex = _firstDataIndex + _poolSize;

            // 超出数据范围则夹住（非循环列表）
            if (newAbsIndex >= _dataList.Count)
                break;

            PlaceItem(_itemRects[slot], newAbsIndex);
            _itemViews[slot].SetData(
                newAbsIndex % _dataList.Count,
                _dataList[newAbsIndex % _dataList.Count]);

            _firstPoolSlot  = (_firstPoolSlot + 1) % _poolSize;
            _firstDataIndex++;
        }

        // ── 向上滚动：把底部条目循环移到顶部 ──────────────
        while (_firstDataIndex > neededFirst)
        {
            _firstDataIndex--;
            _firstPoolSlot = (_firstPoolSlot - 1 + _poolSize) % _poolSize;

            int slot = _firstPoolSlot;
            if (_firstDataIndex < 0)
            {
                _firstDataIndex = 0;
                _firstPoolSlot  = (_firstPoolSlot + 1) % _poolSize;
                break;
            }

            PlaceItem(_itemRects[slot], _firstDataIndex);
            _itemViews[slot].SetData(
                _firstDataIndex % _dataList.Count,
                _dataList[_firstDataIndex % _dataList.Count]);
        }
    }

    /// <summary>
    /// 按环形缓冲区当前状态重新刷新所有可见条目的数据文本，
    /// 不改变任何位置或滚动偏移。
    /// </summary>
    private void RefreshVisible()
    {
        for (int i = 0; i < _poolSize; i++)
        {
            int slot     = (_firstPoolSlot + i) % _poolSize;
            int absIndex = _firstDataIndex + i;
            if (absIndex >= _dataList.Count) break;
            _itemViews[slot].SetData(absIndex, _dataList[absIndex]);
        }
    }

    // ── 滚动动画内部实现 ────────────────────────────────────

    /// <summary>计算让指定索引条目出现在视口顶部所需的 contentY。</summary>
    private float GetTargetContentY(int dataIndex)
    {
        float maxY = Mathf.Max(0f,
            _dataList.Count * _stride - _spacing - _scrollRect.viewport.rect.height);
        return Mathf.Clamp(dataIndex * _stride, 0f, maxY);
    }

    private void SetContentY(float y)
    {
        _content.anchoredPosition = new Vector2(_content.anchoredPosition.x, y);
    }

    private IEnumerator ScrollCoroutine(float targetY, float duration,
                                         Ease ease, Action onComplete)
    {
        // 动画期间暂停 onValueChanged 驱动，改为每帧主动调用 UpdateItems
        _scrollRect.onValueChanged.RemoveListener(OnScrollValueChanged);

        float startY  = _content.anchoredPosition.y;
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t  = Mathf.Clamp01(elapsed / duration);
            float et = Evaluate(ease, t);
            SetContentY(Mathf.LerpUnclamped(startY, targetY, et));
            UpdateItems();
            yield return null;
        }

        SetContentY(targetY);
        UpdateItems();

        _scrollRect.onValueChanged.AddListener(OnScrollValueChanged);
        _scrollCoroutine = null;
        onComplete?.Invoke();
    }

    /// <summary>缓动函数求值，t ∈ [0,1] → [0,1]。</summary>
    private static float Evaluate(Ease ease, float t)
    {
        return ease switch
        {
            Ease.Linear    => t,
            Ease.QuadIn    => t * t,
            Ease.QuadOut   => 1f - (1f - t) * (1f - t),
            Ease.QuadInOut => t < 0.5f ? 2f * t * t : 1f - 2f * (1f - t) * (1f - t),
            Ease.CubicOut  => 1f - Mathf.Pow(1f - t, 3f),
            Ease.BackOut   => 1f + 2.70158f * Mathf.Pow(t - 1f, 3f) + 1.70158f * Mathf.Pow(t - 1f, 2f),
            _              => t,
        };
    }
}
