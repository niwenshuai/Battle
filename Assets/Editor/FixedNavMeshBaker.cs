using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace FrameSync.Editor
{
    /// <summary>
    /// NavMesh 烘焙编辑器窗口。
    ///
    /// 功能：
    /// 1. 在 Scene 视图手动放置多边形顶点（标记可行走区域的外轮廓）
    /// 2. 添加障碍物多边形（孔洞）
    /// 3. 三角化 → 计算邻接 → 输出 FixedNavMeshAsset
    /// </summary>
    public class FixedNavMeshBaker : EditorWindow
    {
        // ── 编辑状态 ─────────────────────────────────────────
        private enum EditMode { None, EditBoundary, EditObstacle }
        private EditMode _mode = EditMode.None;

        // 外轮廓（顺时针）
        private List<Vector2> _boundary = new();
        // 障碍物列表（每个也是顺时针多边形）
        private List<List<Vector2>> _obstacles = new();
        private int _currentObstacle = -1;

        // 输出
        private FixedNavMeshAsset _targetAsset;

        // 可视化
        private bool _showTriangulation;
        private List<Vector2> _bakedVerts;
        private List<int> _bakedTris;
        private List<int> _bakedNeighbors;

        private Vector2 _scrollPos;

        [MenuItem("FrameSync/NavMesh Baker")]
        public static void Open()
        {
            GetWindow<FixedNavMeshBaker>("NavMesh Baker");
        }

        private void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGUI;
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
        }

        // ═══════════════════════════════════════════════════════
        // Inspector GUI
        // ═══════════════════════════════════════════════════════

        private void OnGUI()
        {
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            EditorGUILayout.LabelField("NavMesh Baker", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            _targetAsset = (FixedNavMeshAsset)EditorGUILayout.ObjectField(
                "Target Asset", _targetAsset, typeof(FixedNavMeshAsset), false);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("编辑模式", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.color = _mode == EditMode.EditBoundary ? Color.green : Color.white;
                if (GUILayout.Button("编辑外轮廓"))
                {
                    _mode = _mode == EditMode.EditBoundary ? EditMode.None : EditMode.EditBoundary;
                    SceneView.RepaintAll();
                }

                GUI.color = _mode == EditMode.EditObstacle ? Color.yellow : Color.white;
                if (GUILayout.Button("编辑障碍物"))
                {
                    if (_mode != EditMode.EditObstacle)
                    {
                        _mode = EditMode.EditObstacle;
                        _obstacles.Add(new List<Vector2>());
                        _currentObstacle = _obstacles.Count - 1;
                    }
                    else
                    {
                        _mode = EditMode.None;
                    }
                    SceneView.RepaintAll();
                }
                GUI.color = Color.white;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField($"外轮廓顶点: {_boundary.Count}");
            EditorGUILayout.LabelField($"障碍物数量: {_obstacles.Count}");

            if (_obstacles.Count > 0 && _currentObstacle >= 0)
                EditorGUILayout.LabelField($"  当前障碍物顶点: {_obstacles[_currentObstacle].Count}");

            EditorGUILayout.Space();

            if (GUILayout.Button("新建障碍物多边形"))
            {
                _obstacles.Add(new List<Vector2>());
                _currentObstacle = _obstacles.Count - 1;
                _mode = EditMode.EditObstacle;
                SceneView.RepaintAll();
            }

            EditorGUILayout.Space();

            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.color = Color.red;
                if (GUILayout.Button("清空外轮廓"))
                {
                    _boundary.Clear();
                    _showTriangulation = false;
                    SceneView.RepaintAll();
                }

                if (GUILayout.Button("清空所有障碍物"))
                {
                    _obstacles.Clear();
                    _currentObstacle = -1;
                    _showTriangulation = false;
                    SceneView.RepaintAll();
                }

                if (GUILayout.Button("全部清空"))
                {
                    _boundary.Clear();
                    _obstacles.Clear();
                    _currentObstacle = -1;
                    _showTriangulation = false;
                    SceneView.RepaintAll();
                }
                GUI.color = Color.white;
            }

            EditorGUILayout.Space(20);
            EditorGUILayout.LabelField("烘焙", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledGroupScope(_boundary.Count < 3))
            {
                if (GUILayout.Button("烘焙 NavMesh", GUILayout.Height(30)))
                    Bake();
            }

            using (new EditorGUI.DisabledGroupScope(!_showTriangulation || _targetAsset == null))
            {
                if (GUILayout.Button("保存到 Asset"))
                    SaveToAsset();
            }

            _showTriangulation = _showTriangulation && _bakedTris != null;

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "操作说明:\n" +
                "1. 点击「编辑外轮廓」后在 Scene 视图左键添加顶点\n" +
                "2. 点击「编辑障碍物」添加孔洞多边形\n" +
                "3. 烘焙后检查三角化结果\n" +
                "4. 保存到 FixedNavMeshAsset\n\n" +
                "Scene 视图使用 XZ 平面 (Y=0)。",
                MessageType.Info);

            EditorGUILayout.EndScrollView();
        }

        // ═══════════════════════════════════════════════════════
        // Scene GUI — 处理点击与可视化
        // ═══════════════════════════════════════════════════════

        private void OnSceneGUI(SceneView sceneView)
        {
            // 绘制已有数据
            DrawBoundary();
            DrawObstacles();
            if (_showTriangulation) DrawTriangulation();

            if (_mode == EditMode.None) return;

            // 处理鼠标点击
            HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));

            var evt = Event.current;
            if (evt.type == EventType.MouseDown && evt.button == 0 && !evt.alt)
            {
                var ray = HandleUtility.GUIPointToWorldRay(evt.mousePosition);
                // 与 XZ 平面求交（Y=0）
                if (ray.direction.y != 0)
                {
                    float t = -ray.origin.y / ray.direction.y;
                    var worldPos = ray.origin + ray.direction * t;
                    var point = new Vector2(worldPos.x, worldPos.z);

                    if (_mode == EditMode.EditBoundary)
                    {
                        Undo.RecordObject(this, "Add Boundary Point");
                        _boundary.Add(point);
                        _showTriangulation = false;
                    }
                    else if (_mode == EditMode.EditObstacle && _currentObstacle >= 0)
                    {
                        Undo.RecordObject(this, "Add Obstacle Point");
                        _obstacles[_currentObstacle].Add(point);
                        _showTriangulation = false;
                    }

                    evt.Use();
                    Repaint();
                }
            }

            // 绘制提示
            Handles.BeginGUI();
            var rect = new Rect(10, 10, 250, 24);
            string label = _mode == EditMode.EditBoundary ? "编辑外轮廓 — 左键添加顶点" : "编辑障碍物 — 左键添加顶点";
            GUI.Label(rect, label, EditorStyles.whiteLargeLabel);
            Handles.EndGUI();
        }

        private void DrawBoundary()
        {
            if (_boundary.Count < 2) return;
            Handles.color = Color.cyan;
            for (int i = 0; i < _boundary.Count; i++)
            {
                var a = To3D(_boundary[i]);
                var b = To3D(_boundary[(i + 1) % _boundary.Count]);
                Handles.DrawLine(a, b);
                Handles.SphereHandleCap(0, a, Quaternion.identity, 0.15f, EventType.Repaint);
            }
        }

        private void DrawObstacles()
        {
            for (int o = 0; o < _obstacles.Count; o++)
            {
                var obs = _obstacles[o];
                if (obs.Count < 2) continue;
                Handles.color = o == _currentObstacle ? Color.yellow : new Color(1f, 0.5f, 0f);
                for (int i = 0; i < obs.Count; i++)
                {
                    var a = To3D(obs[i]);
                    var b = To3D(obs[(i + 1) % obs.Count]);
                    Handles.DrawLine(a, b);
                    Handles.SphereHandleCap(0, a, Quaternion.identity, 0.1f, EventType.Repaint);
                }
            }
        }

        private void DrawTriangulation()
        {
            if (_bakedVerts == null || _bakedTris == null) return;
            Handles.color = new Color(0f, 1f, 0f, 0.3f);
            for (int i = 0; i < _bakedTris.Count; i += 3)
            {
                var a = To3D(_bakedVerts[_bakedTris[i]]);
                var b = To3D(_bakedVerts[_bakedTris[i + 1]]);
                var c = To3D(_bakedVerts[_bakedTris[i + 2]]);
                Handles.DrawLine(a, b);
                Handles.DrawLine(b, c);
                Handles.DrawLine(c, a);
            }

            // 绘制邻接关系（中心连线）
            if (_bakedNeighbors != null)
            {
                Handles.color = new Color(1f, 1f, 0f, 0.15f);
                int triCount = _bakedTris.Count / 3;
                for (int i = 0; i < triCount; i++)
                {
                    var center = GetTriCenter(i);
                    for (int e = 0; e < 3; e++)
                    {
                        int n = _bakedNeighbors[i * 3 + e];
                        if (n >= 0 && n > i)
                        {
                            var nc = GetTriCenter(n);
                            Handles.DrawLine(To3D(center), To3D(nc));
                        }
                    }
                }
            }
        }

        private Vector2 GetTriCenter(int triIdx)
        {
            int b = triIdx * 3;
            var p0 = _bakedVerts[_bakedTris[b]];
            var p1 = _bakedVerts[_bakedTris[b + 1]];
            var p2 = _bakedVerts[_bakedTris[b + 2]];
            return (p0 + p1 + p2) / 3f;
        }

        private static Vector3 To3D(Vector2 v) => new(v.x, 0f, v.y);

        // ═══════════════════════════════════════════════════════
        // 烘焙流程
        // ═══════════════════════════════════════════════════════

        private void Bake()
        {
            if (_boundary.Count < 3)
            {
                EditorUtility.DisplayDialog("错误", "外轮廓至少需要3个顶点", "确定");
                return;
            }

            // 确保外轮廓逆时针（三角化算法需要）
            if (SignedArea(_boundary) < 0)
                _boundary.Reverse();

            // 确保障碍物顺时针
            foreach (var obs in _obstacles)
            {
                if (obs.Count >= 3 && SignedArea(obs) > 0)
                    obs.Reverse();
            }

            // 执行三角化
            var triangulator = new ConstrainedTriangulator();
            triangulator.SetBoundary(_boundary);
            foreach (var obs in _obstacles)
            {
                if (obs.Count >= 3)
                    triangulator.AddHole(obs);
            }

            triangulator.Triangulate();

            _bakedVerts = triangulator.Vertices;
            _bakedTris = triangulator.Indices;

            // 计算邻接
            _bakedNeighbors = BuildAdjacency(_bakedVerts, _bakedTris);

            _showTriangulation = true;
            SceneView.RepaintAll();

            int triCount = _bakedTris.Count / 3;
            Debug.Log($"[NavMesh Baker] 烘焙完成: {_bakedVerts.Count} 顶点, {triCount} 三角形");
        }

        private void SaveToAsset()
        {
            if (_targetAsset == null)
            {
                // 创建新 Asset
                var path = EditorUtility.SaveFilePanelInProject(
                    "保存 NavMesh Asset", "FixedNavMesh", "asset", "选择保存位置");
                if (string.IsNullOrEmpty(path)) return;

                _targetAsset = ScriptableObject.CreateInstance<FixedNavMeshAsset>();
                AssetDatabase.CreateAsset(_targetAsset, path);
            }

            Undo.RecordObject(_targetAsset, "Save NavMesh Data");
            _targetAsset.BakedVertices = _bakedVerts.ToArray();
            _targetAsset.BakedTriangleIndices = _bakedTris.ToArray();
            _targetAsset.BakedNeighbors = _bakedNeighbors.ToArray();
            EditorUtility.SetDirty(_targetAsset);
            AssetDatabase.SaveAssets();

            Debug.Log($"[NavMesh Baker] 已保存到 {AssetDatabase.GetAssetPath(_targetAsset)}");
        }

        // ═══════════════════════════════════════════════════════
        // 邻接计算
        // ═══════════════════════════════════════════════════════

        private static List<int> BuildAdjacency(List<Vector2> verts, List<int> tris)
        {
            int triCount = tris.Count / 3;
            var neighbors = new List<int>(triCount * 3);
            for (int i = 0; i < triCount * 3; i++)
                neighbors.Add(-1);

            // 边 → 三角形映射。Key = (min_vert, max_vert)
            var edgeMap = new Dictionary<long, (int triIdx, int edgeIdx)>();

            for (int t = 0; t < triCount; t++)
            {
                int baseIdx = t * 3;
                for (int e = 0; e < 3; e++)
                {
                    int va = tris[baseIdx + e];
                    int vb = tris[baseIdx + (e + 1) % 3];
                    long key = EdgeKey(va, vb);

                    if (edgeMap.TryGetValue(key, out var other))
                    {
                        // 建立双向邻接
                        neighbors[t * 3 + e] = other.triIdx;
                        neighbors[other.triIdx * 3 + other.edgeIdx] = t;
                        edgeMap.Remove(key);
                    }
                    else
                    {
                        edgeMap[key] = (t, e);
                    }
                }
            }

            return neighbors;
        }

        private static long EdgeKey(int a, int b)
        {
            if (a > b) (a, b) = (b, a);
            return ((long)a << 32) | (uint)b;
        }

        private static float SignedArea(List<Vector2> polygon)
        {
            float area = 0;
            for (int i = 0; i < polygon.Count; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Count];
                area += (b.x - a.x) * (b.y + a.y);
            }
            return area;
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 约束 Delaunay 三角化（Ear Clipping with Holes）
    //
    // 适用于带孔洞的简单多边形。
    // 1. 将孔洞合并到外轮廓中（桥接）
    // 2. 对合并后的多边形做 Ear Clipping 三角化
    // ═══════════════════════════════════════════════════════════

    public class ConstrainedTriangulator
    {
        private List<Vector2> _boundary;
        private readonly List<List<Vector2>> _holes = new();

        public List<Vector2> Vertices { get; private set; }
        public List<int> Indices { get; private set; }

        public void SetBoundary(List<Vector2> boundary)
        {
            _boundary = new List<Vector2>(boundary);
        }

        public void AddHole(List<Vector2> hole)
        {
            _holes.Add(new List<Vector2>(hole));
        }

        public void Triangulate()
        {
            // 合并孔洞到外轮廓
            var polygon = new List<Vector2>(_boundary);

            // 按最右顶点 X 坐标降序排序孔洞
            _holes.Sort((a, b) =>
            {
                float maxA = float.MinValue, maxB = float.MinValue;
                foreach (var p in a) if (p.x > maxA) maxA = p.x;
                foreach (var p in b) if (p.x > maxB) maxB = p.x;
                return maxB.CompareTo(maxA);
            });

            foreach (var hole in _holes)
            {
                if (hole.Count < 3) continue;
                // 确保孔洞顺时针
                if (SignedArea(hole) > 0)
                    hole.Reverse();
                polygon = MergeHole(polygon, hole);
            }

            // Ear Clipping 三角化
            Vertices = new List<Vector2>(polygon);
            Indices = new List<int>();
            EarClip(polygon);
        }

        /// <summary>
        /// 将孔洞桥接合并到外轮廓中。
        /// 找到孔洞上最右点和外轮廓上可见的顶点，插入桥接边。
        /// </summary>
        private static List<Vector2> MergeHole(List<Vector2> outer, List<Vector2> hole)
        {
            // 找孔洞最右顶点
            int holeRightIdx = 0;
            for (int i = 1; i < hole.Count; i++)
            {
                if (hole[i].x > hole[holeRightIdx].x)
                    holeRightIdx = i;
            }
            var holePoint = hole[holeRightIdx];

            // 从 holePoint 向右发射线，找 outer 上最近的交点
            int bestOuterIdx = -1;
            float bestDist = float.MaxValue;

            for (int i = 0; i < outer.Count; i++)
            {
                // 简单方法：找距离 holePoint 最近且可见的外轮廓顶点
                var dist = Vector2.Distance(holePoint, outer[i]);
                if (dist < bestDist)
                {
                    // 检查连线是否与外轮廓和孔洞的边相交
                    if (IsVisibleFrom(holePoint, outer[i], outer, hole))
                    {
                        bestDist = dist;
                        bestOuterIdx = i;
                    }
                }
            }

            if (bestOuterIdx < 0)
            {
                // Fallback：用最近点
                for (int i = 0; i < outer.Count; i++)
                {
                    var dist = Vector2.Distance(holePoint, outer[i]);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestOuterIdx = i;
                    }
                }
            }

            // 构建合并多边形：
            // outer[0..bestOuterIdx] + hole[holeRightIdx..] + hole[..holeRightIdx] + outer[bestOuterIdx..]
            var merged = new List<Vector2>();

            for (int i = 0; i <= bestOuterIdx; i++)
                merged.Add(outer[i]);

            // 从 holeRightIdx 开始遍历孔洞一圈
            for (int i = 0; i <= hole.Count; i++)
                merged.Add(hole[(holeRightIdx + i) % hole.Count]);

            // 桥接回外轮廓
            for (int i = bestOuterIdx; i < outer.Count; i++)
                merged.Add(outer[i]);

            return merged;
        }

        private static bool IsVisibleFrom(Vector2 a, Vector2 b, List<Vector2> outer, List<Vector2> hole)
        {
            // 检查 ab 线段是否与 outer 或 hole 的任何边相交
            for (int i = 0; i < outer.Count; i++)
            {
                var c = outer[i];
                var d = outer[(i + 1) % outer.Count];
                if (SegmentsIntersectProper(a, b, c, d))
                    return false;
            }

            for (int i = 0; i < hole.Count; i++)
            {
                var c = hole[i];
                var d = hole[(i + 1) % hole.Count];
                if (SegmentsIntersectProper(a, b, c, d))
                    return false;
            }

            return true;
        }

        /// <summary>判断两条线段是否严格相交（不含端点共享）。</summary>
        private static bool SegmentsIntersectProper(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            // 排除共享端点
            if (a == c || a == d || b == c || b == d) return false;

            float d1 = Cross2D(c, d, a);
            float d2 = Cross2D(c, d, b);
            float d3 = Cross2D(a, b, c);
            float d4 = Cross2D(a, b, d);

            if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
                ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
                return true;

            return false;
        }

        private static float Cross2D(Vector2 o, Vector2 a, Vector2 b)
        {
            return (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
        }

        // ── Ear Clipping 三角化 ─────────────────────────────

        private void EarClip(List<Vector2> polygon)
        {
            // 维护一个索引链表
            var indices = new List<int>();
            for (int i = 0; i < polygon.Count; i++)
                indices.Add(i);

            int safeGuard = polygon.Count * polygon.Count;

            while (indices.Count > 2 && safeGuard-- > 0)
            {
                bool earFound = false;

                for (int i = 0; i < indices.Count; i++)
                {
                    int prevIdx = (i + indices.Count - 1) % indices.Count;
                    int nextIdx = (i + 1) % indices.Count;

                    var prev = polygon[indices[prevIdx]];
                    var curr = polygon[indices[i]];
                    var next = polygon[indices[nextIdx]];

                    // 凸顶点检查（逆时针缠绕下叉积 > 0）
                    float cross = Cross2D(prev, curr, next);
                    if (cross <= 0) continue; // 凹顶点，跳过

                    // 检查没有其他顶点在三角形内
                    bool isEar = true;
                    for (int j = 0; j < indices.Count; j++)
                    {
                        if (j == prevIdx || j == i || j == nextIdx) continue;
                        if (PointInTriangle2D(polygon[indices[j]], prev, curr, next))
                        {
                            isEar = false;
                            break;
                        }
                    }

                    if (!isEar) continue;

                    // 找到耳朵，输出三角形
                    Indices.Add(indices[prevIdx]);
                    Indices.Add(indices[i]);
                    Indices.Add(indices[nextIdx]);

                    indices.RemoveAt(i);
                    earFound = true;
                    break;
                }

                if (!earFound) break; // 退化情况
            }
        }

        private static bool PointInTriangle2D(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Cross2D(a, b, p);
            float d2 = Cross2D(b, c, p);
            float d3 = Cross2D(c, a, p);

            bool hasNeg = (d1 < 0) || (d2 < 0) || (d3 < 0);
            bool hasPos = (d1 > 0) || (d2 > 0) || (d3 > 0);

            return !(hasNeg && hasPos);
        }

        private static float SignedArea(List<Vector2> polygon)
        {
            float area = 0;
            for (int i = 0; i < polygon.Count; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Count];
                area += (b.x - a.x) * (b.y + a.y);
            }
            return area;
        }
    }
}
