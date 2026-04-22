// Assets/Editor/BuildInfiniteScrollScene.cs
// 一次性 Editor 工具 —— 执行后可删除
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public static class BuildInfiniteScrollScene
{
    [MenuItem("Tools/Build Infinite Scroll List Scene")]
    public static void Build()
    {
        // ── Canvas ──────────────────────────────────────────────
        var canvasGO = new GameObject("Canvas");
        var canvas   = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvasGO.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        canvasGO.GetComponent<CanvasScaler>().referenceResolution = new Vector2(1080, 1920);
        canvasGO.AddComponent<GraphicRaycaster>();

        // ── Background Panel ────────────────────────────────────
        var bgGO  = CreateRect("Background", canvasGO.transform);
        var bgImg = bgGO.AddComponent<Image>();
        bgImg.color = new Color(0.12f, 0.14f, 0.18f, 1f);
        Stretch(bgGO.GetComponent<RectTransform>());

        // ── ScrollRect ──────────────────────────────────────────
        var scrollGO   = CreateRect("ScrollRect", bgGO.transform);
        var scrollRect = scrollGO.AddComponent<ScrollRect>();
        scrollRect.horizontal    = false;
        scrollRect.vertical      = true;
        scrollRect.movementType  = ScrollRect.MovementType.Elastic;
        scrollRect.decelerationRate = 0.135f;
        // 占满背景，留 40px 边距
        var scrollRt = scrollGO.GetComponent<RectTransform>();
        scrollRt.anchorMin   = new Vector2(0f, 0f);
        scrollRt.anchorMax   = new Vector2(1f, 1f);
        scrollRt.offsetMin   = new Vector2(40f, 40f);
        scrollRt.offsetMax   = new Vector2(-40f, -40f);

        // ── Viewport ────────────────────────────────────────────
        var vpGO = CreateRect("Viewport", scrollGO.transform);
        var vpImg = vpGO.AddComponent<Image>();   // Mask 需要 Graphic 组件
        vpImg.color = new Color(1, 1, 1, 0);     // 透明，不遮挡内容
        vpGO.AddComponent<Mask>().showMaskGraphic = false;
        Stretch(vpGO.GetComponent<RectTransform>());
        scrollRect.viewport = vpGO.GetComponent<RectTransform>();

        // ── Content ─────────────────────────────────────────────
        var contentGO = CreateRect("Content", vpGO.transform);
        var contentRt = contentGO.GetComponent<RectTransform>();
        contentRt.anchorMin   = new Vector2(0f, 1f);
        contentRt.anchorMax   = new Vector2(1f, 1f);
        contentRt.pivot       = new Vector2(0.5f, 1f);
        contentRt.anchoredPosition = Vector2.zero;
        contentRt.sizeDelta   = new Vector2(0f, 0f);   // 高度由 InfiniteScrollList 动态设置
        scrollRect.content    = contentRt;

        // ── InfiniteScrollList (挂在 ScrollRect 上) ─────────────
        var infiniteList = scrollGO.AddComponent<InfiniteScrollList>();

        // ── Item 预制体 ──────────────────────────────────────────
        var itemPrefabGO   = CreateItemPrefab();
        var itemPrefabComp = itemPrefabGO.GetComponent<ScrollItemData>();

        // 通过 SerializedObject 赋值
        var so = new SerializedObject(infiniteList);
        so.FindProperty("_itemPrefab").objectReferenceValue = itemPrefabComp;
        so.FindProperty("_itemHeight").floatValue = 100f;
        so.FindProperty("_spacing").floatValue    = 4f;
        so.FindProperty("_bufferCount").intValue  = 2;
        so.ApplyModifiedProperties();

        // ── 保存预制体到 Assets ──────────────────────────────────
        if (!System.IO.Directory.Exists("Assets/Prefabs"))
            System.IO.Directory.CreateDirectory("Assets/Prefabs");

        var prefabPath = "Assets/Prefabs/ScrollItem.prefab";
        PrefabUtility.SaveAsPrefabAsset(itemPrefabGO, prefabPath);
        Object.DestroyImmediate(itemPrefabGO);   // 清理场景中的临时 GO

        // 重新加载预制体并重新赋值
        var savedPrefab = AssetDatabase.LoadAssetAtPath<ScrollItemData>(prefabPath);
        so = new SerializedObject(infiniteList);
        so.FindProperty("_itemPrefab").objectReferenceValue = savedPrefab;
        so.ApplyModifiedProperties();

        // ── 添加测试用 InfiniteScrollDemo 脚本 ───────────────────
        var demo = scrollGO.AddComponent<InfiniteScrollDemo>();
        var soDemo = new SerializedObject(demo);
        soDemo.FindProperty("_list").objectReferenceValue = infiniteList;
        soDemo.ApplyModifiedProperties();

        // ── 标记场景已修改 ───────────────────────────────────────
        UnityEditor.SceneManagement.EditorSceneManager.MarkAllScenesDirty();
        Debug.Log("[BuildInfiniteScrollScene] UI 搭建完成！请保存场景并进入 Play 模式测试。");
        Selection.activeGameObject = canvasGO;
    }

    // ── 辅助方法 ────────────────────────────────────────────────

    private static GameObject CreateRect(string name, Transform parent)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<RectTransform>();
        return go;
    }

    private static void Stretch(RectTransform rt)
    {
        rt.anchorMin        = Vector2.zero;
        rt.anchorMax        = Vector2.one;
        rt.offsetMin        = Vector2.zero;
        rt.offsetMax        = Vector2.zero;
    }

    /// <summary>在场景中临时创建 Item 预制体原型。</summary>
    private static GameObject CreateItemPrefab()
    {
        // 根节点
        var rootGO = CreateRect("ScrollItem", null);
        var rootRt = rootGO.GetComponent<RectTransform>();
        rootRt.sizeDelta = new Vector2(960f, 100f);

        // 背景
        var bg    = rootGO.AddComponent<Image>();
        bg.color  = new Color(0.2f, 0.22f, 0.28f, 1f);

        // 圆角阴影（Shadow）
        var shadow = rootGO.AddComponent<Shadow>();
        shadow.effectColor    = new Color(0, 0, 0, 0.4f);
        shadow.effectDistance = new Vector2(2f, -2f);

        // 序号 Label
        var indexGO = CreateRect("IndexText", rootGO.transform);
        var indexRt = indexGO.GetComponent<RectTransform>();
        indexRt.anchorMin        = new Vector2(0f, 0f);
        indexRt.anchorMax        = new Vector2(0f, 1f);
        indexRt.pivot            = new Vector2(0f, 0.5f);
        indexRt.offsetMin        = new Vector2(16f, 0f);
        indexRt.offsetMax        = new Vector2(80f, 0f);
        var indexTmp = indexGO.AddComponent<TextMeshProUGUI>();
        indexTmp.text      = "1";
        indexTmp.fontSize  = 28;
        indexTmp.alignment = TextAlignmentOptions.MidlineLeft;
        indexTmp.color     = new Color(0.6f, 0.8f, 1f, 1f);

        // 内容 Label
        var contentGO = CreateRect("ContentText", rootGO.transform);
        var contentRt = contentGO.GetComponent<RectTransform>();
        contentRt.anchorMin  = new Vector2(0f, 0f);
        contentRt.anchorMax  = new Vector2(1f, 1f);
        contentRt.offsetMin  = new Vector2(96f, 8f);
        contentRt.offsetMax  = new Vector2(-16f, -8f);
        var contentTmp = contentGO.AddComponent<TextMeshProUGUI>();
        contentTmp.text      = "Item Content";
        contentTmp.fontSize  = 26;
        contentTmp.alignment = TextAlignmentOptions.MidlineLeft;
        contentTmp.color     = Color.white;

        // 挂 ScrollItemData，并赋值引用
        var itemData = rootGO.AddComponent<ScrollItemData>();
        var so = new SerializedObject(itemData);
        so.FindProperty("_indexText").objectReferenceValue   = indexTmp;
        so.FindProperty("_contentText").objectReferenceValue = contentTmp;
        so.ApplyModifiedProperties();

        return rootGO;
    }
}
