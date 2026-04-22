using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 演示用脚本：Start 时填充 200 条数据，运行期间可按 Space 键追加 20 条。
/// </summary>
public class InfiniteScrollDemo : MonoBehaviour
{
    [SerializeField] private InfiniteScrollList _list;
    [SerializeField] private int _totalItems = 200;
    [SerializeField] private int _appendBatchSize = 20;

    private int _appendCount = 0;

    private void Start()
    {
        if (_list == null)
        {
            Debug.LogWarning("[InfiniteScrollDemo] 未指定 InfiniteScrollList 引用");
            return;
        }

        var data = new List<string>(_totalItems);
        string[] categories = { "角色", "道具", "关卡", "任务", "成就", "装备", "技能", "商店" };
        for (int i = 0; i < _totalItems; i++)
            data.Add($"{categories[i % categories.Length]} — 条目 #{i + 1}");

        _list.SetData(data);
    }

    private void Update()
    {
        // Space：追加一批数据
        if (Input.GetKeyDown(KeyCode.Space))
            AppendBatch();

        // 数字键 1-6：跳转到不同位置，展示不同缓动
        if (Input.GetKeyDown(KeyCode.Alpha1)) _list.ScrollToIndex(0,   0.5f, InfiniteScrollList.Ease.Linear,    () => Debug.Log("Linear → 0"));
        if (Input.GetKeyDown(KeyCode.Alpha2)) _list.ScrollToIndex(49,  0.5f, InfiniteScrollList.Ease.QuadIn,    () => Debug.Log("QuadIn → 49"));
        if (Input.GetKeyDown(KeyCode.Alpha3)) _list.ScrollToIndex(99,  0.5f, InfiniteScrollList.Ease.QuadOut,   () => Debug.Log("QuadOut → 99"));
        if (Input.GetKeyDown(KeyCode.Alpha4)) _list.ScrollToIndex(149, 0.5f, InfiniteScrollList.Ease.QuadInOut, () => Debug.Log("QuadInOut → 149"));
        if (Input.GetKeyDown(KeyCode.Alpha5)) _list.ScrollToIndex(179, 0.6f, InfiniteScrollList.Ease.CubicOut,  () => Debug.Log("CubicOut → 179"));
        if (Input.GetKeyDown(KeyCode.Alpha6)) _list.ScrollToIndex(199, 0.7f, InfiniteScrollList.Ease.BackOut,   () => Debug.Log("BackOut → 199"));
        // J：立即跳转到索引 100（无动画）
        if (Input.GetKeyDown(KeyCode.J)) _list.JumpToIndex(100);
    }

    /// <summary>追加一批数据，保持当前滚动位置。</summary>
    public void AppendBatch()
    {
        if (_list == null) return;

        var newItems = new List<string>(_appendBatchSize);
        string[] tags = { "新增", "动态", "实时" };
        for (int i = 0; i < _appendBatchSize; i++)
        {
            _appendCount++;
            newItems.Add($"{tags[_appendCount % tags.Length]} 条目 #{_totalItems + _appendCount}");
        }

        _list.AppendData(newItems);
        Debug.Log($"[InfiniteScrollDemo] 追加 {_appendBatchSize} 条，总计 {_totalItems + _appendCount} 条");
    }
}
