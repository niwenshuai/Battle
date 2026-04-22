using UnityEngine;
using TMPro;

/// <summary>
/// 无限循环列表的单个条目视图，负责展示一条数据。
/// </summary>
public class ScrollItemData : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI _indexText;
    [SerializeField] private TextMeshProUGUI _contentText;

    /// <summary>
    /// 用指定的数据索引和内容刷新该条目的显示。
    /// </summary>
    public void SetData(int index, string content)
    {
        if (_indexText != null)
            _indexText.text = (index + 1).ToString();
        if (_contentText != null)
            _contentText.text = content;
    }
}
