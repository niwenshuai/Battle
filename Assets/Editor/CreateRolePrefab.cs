using UnityEditor;
using UnityEngine;

/// <summary>
/// 编辑器工具：生成 role.prefab 预制体。
/// 结构：root → head(SpriteRenderer) + team(SpriteRenderer) + state(SpriteRenderer)
/// </summary>
public static class CreateRolePrefab
{
    [MenuItem("Tools/Create Role Prefab")]
    public static void Create()
    {
        var root = new GameObject("role");

        // team — 阵营颜色圆圈（底层，稍大）
        var team = new GameObject("team");
        team.transform.SetParent(root.transform);
        var teamSR = team.AddComponent<SpriteRenderer>();
        teamSR.sortingOrder = 0;
        team.transform.localScale = new Vector3(1.3f, 1.3f, 1f);

        // head — 角色头像
        var head = new GameObject("head");
        head.transform.SetParent(root.transform);
        var headSR = head.AddComponent<SpriteRenderer>();
        headSR.sortingOrder = 1;

        // state — 状态闪烁覆盖层（默认透明）
        var state = new GameObject("state");
        state.transform.SetParent(root.transform);
        var stateSR = state.AddComponent<SpriteRenderer>();
        stateSR.sortingOrder = 2;
        stateSR.color = new Color(1f, 1f, 1f, 0f);

        // 确保 Resources 目录存在
        if (!AssetDatabase.IsValidFolder("Assets/Resources"))
            AssetDatabase.CreateFolder("Assets", "Resources");

        string path = "Assets/Resources/role.prefab";
        PrefabUtility.SaveAsPrefabAsset(root, path);
        Object.DestroyImmediate(root);

        Debug.Log($"[CreateRolePrefab] Prefab saved to {path}");
    }
}
