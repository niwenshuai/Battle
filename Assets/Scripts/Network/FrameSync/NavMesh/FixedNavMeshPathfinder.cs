using System.Collections.Generic;

namespace FrameSync
{
    /// <summary>
    /// 基于三角形 NavMesh 的 A* 寻路器。
    ///
    /// 全部使用定点数运算，保证确定性。
    /// 输出三角形通道（channel），再由 FunnelSmooth 细化为精确路径。
    /// </summary>
    public class FixedNavMeshPathfinder
    {
        private readonly FixedNavMeshData _navMesh;

        // A* 内部节点
        private struct Node
        {
            public int TriIndex;
            public int Parent;      // 在 _closed 中的索引，-1 = 起点
            public FixedInt G;      // 已知代价
            public FixedInt H;      // 启发式（到目标的估计）
            public FixedInt F;      // G + H
        }

        public FixedNavMeshPathfinder(FixedNavMeshData navMesh)
        {
            _navMesh = navMesh;
        }

        /// <summary>
        /// 寻路：从 start 到 end，返回经过的三角形索引列表（通道）。
        /// 如果不可达返回 null。
        /// </summary>
        public List<int> FindTrianglePath(FixedVector2 start, FixedVector2 end)
        {
            int startTri = _navMesh.FindTriangle(start);
            int endTri   = _navMesh.FindTriangle(end);

            if (startTri < 0 || endTri < 0) return null;
            if (startTri == endTri) return new List<int> { startTri };

            var endCenter = _navMesh.GetTriangleCenter(endTri);

            // Open list（简单二叉堆）
            var open   = new List<Node>();
            var closed = new List<Node>();

            // 已访问表：triIndex → closed list 中的索引，-2 = 在 open 中
            var visited = new Dictionary<int, int>();

            // 起点
            var startH = Heuristic(start, end);
            open.Add(new Node
            {
                TriIndex = startTri,
                Parent   = -1,
                G        = FixedInt.Zero,
                H        = startH,
                F        = startH,
            });
            visited[startTri] = -2;

            while (open.Count > 0)
            {
                // 取 F 最小节点
                int bestIdx = 0;
                for (int i = 1; i < open.Count; i++)
                {
                    if (open[i].F < open[bestIdx].F)
                        bestIdx = i;
                }

                var current = open[bestIdx];
                open[bestIdx] = open[open.Count - 1];
                open.RemoveAt(open.Count - 1);

                int closedIdx = closed.Count;
                closed.Add(current);
                visited[current.TriIndex] = closedIdx;

                // 到达终点
                if (current.TriIndex == endTri)
                    return ReconstructPath(closed, closedIdx);

                // 展开邻居
                var tri = _navMesh.Triangles[current.TriIndex];
                for (int edge = 0; edge < 3; edge++)
                {
                    int neighbor = tri.GetNeighbor(edge);
                    if (neighbor < 0) continue; // 边界

                    if (visited.TryGetValue(neighbor, out int state) && state >= 0)
                        continue; // 已在 closed

                    var neighborCenter = _navMesh.GetTriangleCenter(neighbor);
                    var currentCenter  = _navMesh.GetTriangleCenter(current.TriIndex);
                    var edgeCost = FixedVector2.Distance(currentCenter, neighborCenter);
                    var tentativeG = current.G + edgeCost;

                    if (state == -2) // 在 open 中
                    {
                        // 找到并看看能不能更新
                        for (int i = 0; i < open.Count; i++)
                        {
                            if (open[i].TriIndex == neighbor && tentativeG < open[i].G)
                            {
                                var updated = open[i];
                                updated.G = tentativeG;
                                updated.F = tentativeG + updated.H;
                                updated.Parent = closedIdx;
                                open[i] = updated;
                                break;
                            }
                        }
                    }
                    else
                    {
                        // 新节点
                        var h = Heuristic(neighborCenter, end);
                        open.Add(new Node
                        {
                            TriIndex = neighbor,
                            Parent   = closedIdx,
                            G        = tentativeG,
                            H        = h,
                            F        = tentativeG + h,
                        });
                        visited[neighbor] = -2;
                    }
                }
            }

            return null; // 不可达
        }

        /// <summary>
        /// 完整寻路：返回精确路径点（经过漏斗算法平滑）。
        /// </summary>
        public List<FixedVector2> FindPath(FixedVector2 start, FixedVector2 end)
        {
            var triPath = FindTrianglePath(start, end);
            if (triPath == null) return null;

            if (triPath.Count == 1)
                return new List<FixedVector2> { start, end };

            return FunnelSmooth(triPath, start, end);
        }

        // ── A* 辅助 ─────────────────────────────────────────

        private static FixedInt Heuristic(FixedVector2 a, FixedVector2 b)
        {
            return FixedVector2.Distance(a, b);
        }

        private List<int> ReconstructPath(List<Node> closed, int endIdx)
        {
            var path = new List<int>();
            int idx = endIdx;
            while (idx >= 0)
            {
                path.Add(closed[idx].TriIndex);
                idx = closed[idx].Parent;
            }
            path.Reverse();
            return path;
        }

        // ═══════════════════════════════════════════════════════
        // 漏斗算法（Simple Stupid Funnel Algorithm）
        //
        // 输入三角形通道，输出拐点序列。
        // ═══════════════════════════════════════════════════════

        private List<FixedVector2> FunnelSmooth(List<int> triPath, FixedVector2 start, FixedVector2 end)
        {
            // 构建通道的左右边序列
            int portalCount = triPath.Count - 1;
            var portalLeft  = new FixedVector2[portalCount + 2];
            var portalRight = new FixedVector2[portalCount + 2];

            // 第一个 portal = start
            portalLeft[0] = start;
            portalRight[0] = start;

            for (int i = 0; i < portalCount; i++)
            {
                _navMesh.GetSharedEdge(triPath[i], triPath[i + 1], out var eL, out var eR);

                // 确保 left/right 相对于行进方向正确
                // 用叉积判定：如果从 start→end 方向看，eL 应在左边
                // 这里用前进方向的叉积来决定
                var mid = new FixedVector2(
                    (eL.X + eR.X) * FixedInt.Half,
                    (eL.Y + eR.Y) * FixedInt.Half);

                // 从上一个 portal 中点到当前中点的方向
                var prevMid = new FixedVector2(
                    (portalLeft[i].X + portalRight[i].X) * FixedInt.Half,
                    (portalLeft[i].Y + portalRight[i].Y) * FixedInt.Half);
                var fwd = mid - prevMid;

                var edgeDir = eR - eL;
                var cross = FixedVector2.Cross(fwd, edgeDir);

                if (cross < FixedInt.Zero)
                {
                    // eL 实际在右边，交换
                    portalLeft[i + 1] = eR;
                    portalRight[i + 1] = eL;
                }
                else
                {
                    portalLeft[i + 1] = eL;
                    portalRight[i + 1] = eR;
                }
            }

            // 最后一个 portal = end
            portalLeft[portalCount + 1] = end;
            portalRight[portalCount + 1] = end;

            // Simple Stupid Funnel Algorithm
            var path = new List<FixedVector2> { start };

            var apex      = start;
            var funLeft   = start;
            var funRight  = start;
            int apexIdx   = 0;
            int leftIdx   = 0;
            int rightIdx  = 0;

            int totalPortals = portalCount + 2;

            for (int i = 1; i < totalPortals; i++)
            {
                var left  = portalLeft[i];
                var right = portalRight[i];

                // 收紧右侧
                if (TriArea2(apex, funRight, right) <= FixedInt.Zero)
                {
                    if (apex == funRight || TriArea2(apex, funLeft, right) > FixedInt.Zero)
                    {
                        funRight = right;
                        rightIdx = i;
                    }
                    else
                    {
                        // 右侧越过左侧 → 左侧成为新 apex
                        path.Add(funLeft);
                        apex = funLeft;
                        apexIdx = leftIdx;
                        funLeft = apex;
                        funRight = apex;
                        leftIdx = apexIdx;
                        rightIdx = apexIdx;
                        i = apexIdx + 1;
                        continue;
                    }
                }

                // 收紧左侧
                if (TriArea2(apex, funLeft, left) >= FixedInt.Zero)
                {
                    if (apex == funLeft || TriArea2(apex, funRight, left) < FixedInt.Zero)
                    {
                        funLeft = left;
                        leftIdx = i;
                    }
                    else
                    {
                        // 左侧越过右侧 → 右侧成为新 apex
                        path.Add(funRight);
                        apex = funRight;
                        apexIdx = rightIdx;
                        funLeft = apex;
                        funRight = apex;
                        leftIdx = apexIdx;
                        rightIdx = apexIdx;
                        i = apexIdx + 1;
                        continue;
                    }
                }
            }

            // 添加终点
            if (path.Count == 0 || path[path.Count - 1] != end)
                path.Add(end);

            return path;
        }

        /// <summary>有符号三角面积的两倍（叉积）。正=逆时针。</summary>
        private static FixedInt TriArea2(FixedVector2 a, FixedVector2 b, FixedVector2 c)
        {
            return (b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y);
        }
    }
}
