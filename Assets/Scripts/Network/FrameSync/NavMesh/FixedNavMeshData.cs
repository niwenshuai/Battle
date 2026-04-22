using System;
using System.Collections.Generic;

namespace FrameSync
{
    // ═══════════════════════════════════════════════════════════
    // NavMesh 数据结构
    //
    // 基于三角形网格的导航数据，全部使用定点数。
    // 数据可在编辑器烘焙后序列化，运行时加载。
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// NavMesh 中的一个顶点。
    /// </summary>
    [Serializable]
    public struct NavVertex
    {
        public FixedVector2 Position;

        public NavVertex(FixedVector2 pos) => Position = pos;
        public NavVertex(FixedInt x, FixedInt y) => Position = new FixedVector2(x, y);
    }

    /// <summary>
    /// NavMesh 中的一个三角形（多边形）。
    /// 存储 3 个顶点索引和相邻三角形索引。
    /// </summary>
    [Serializable]
    public struct NavTriangle
    {
        /// <summary>三个顶点在 NavMeshData.Vertices 中的索引。</summary>
        public int V0, V1, V2;

        /// <summary>
        /// 三条边对面的相邻三角形索引。
        /// Neighbor[i] = 与 Edge(Vi, V(i+1)%3) 共享的三角形，-1 表示边界。
        /// Neighbor[0] → Edge(V0,V1), Neighbor[1] → Edge(V1,V2), Neighbor[2] → Edge(V2,V0)
        /// </summary>
        public int Neighbor0, Neighbor1, Neighbor2;

        public int GetVertex(int i)
        {
            switch (i)
            {
                case 0: return V0;
                case 1: return V1;
                default: return V2;
            }
        }

        public int GetNeighbor(int i)
        {
            switch (i)
            {
                case 0: return Neighbor0;
                case 1: return Neighbor1;
                default: return Neighbor2;
            }
        }

        public void SetNeighbor(int i, int value)
        {
            switch (i)
            {
                case 0: Neighbor0 = value; break;
                case 1: Neighbor1 = value; break;
                default: Neighbor2 = value; break;
            }
        }

        /// <summary>获取第 i 条边的两个顶点索引。</summary>
        public void GetEdge(int i, out int a, out int b)
        {
            a = GetVertex(i);
            b = GetVertex((i + 1) % 3);
        }

        /// <summary>查找与指定三角形共享的边索引，找不到返回 -1。</summary>
        public int FindSharedEdge(int neighborTriIndex)
        {
            if (Neighbor0 == neighborTriIndex) return 0;
            if (Neighbor1 == neighborTriIndex) return 1;
            if (Neighbor2 == neighborTriIndex) return 2;
            return -1;
        }
    }

    /// <summary>
    /// NavMesh 完整数据。运行时只读。
    /// </summary>
    [Serializable]
    public class FixedNavMeshData
    {
        public NavVertex[] Vertices;
        public NavTriangle[] Triangles;

        /// <summary>获取三角形的中心点。</summary>
        public FixedVector2 GetTriangleCenter(int triIndex)
        {
            var tri = Triangles[triIndex];
            var p0 = Vertices[tri.V0].Position;
            var p1 = Vertices[tri.V1].Position;
            var p2 = Vertices[tri.V2].Position;
            var three = FixedInt.FromInt(3);
            return new FixedVector2(
                (p0.X + p1.X + p2.X) / three,
                (p0.Y + p1.Y + p2.Y) / three);
        }

        /// <summary>获取两个相邻三角形共享边的中点。</summary>
        public FixedVector2 GetSharedEdgeMidpoint(int triA, int triB)
        {
            var tri = Triangles[triA];
            int edgeIdx = tri.FindSharedEdge(triB);
            if (edgeIdx < 0) return GetTriangleCenter(triA);

            tri.GetEdge(edgeIdx, out int va, out int vb);
            var pa = Vertices[va].Position;
            var pb = Vertices[vb].Position;
            return new FixedVector2(
                (pa.X + pb.X) * FixedInt.Half,
                (pa.Y + pb.Y) * FixedInt.Half);
        }

        /// <summary>获取两个相邻三角形共享边的端点。</summary>
        public void GetSharedEdge(int triA, int triB, out FixedVector2 edgeLeft, out FixedVector2 edgeRight)
        {
            var tri = Triangles[triA];
            int edgeIdx = tri.FindSharedEdge(triB);
            if (edgeIdx < 0)
            {
                edgeLeft = edgeRight = GetTriangleCenter(triA);
                return;
            }
            tri.GetEdge(edgeIdx, out int va, out int vb);
            edgeLeft = Vertices[va].Position;
            edgeRight = Vertices[vb].Position;
        }

        /// <summary>
        /// 查找包含指定点的三角形索引，找不到返回 -1。
        /// 线性搜索，大规模 NavMesh 可用空间分区优化。
        /// </summary>
        public int FindTriangle(FixedVector2 point)
        {
            for (int i = 0; i < Triangles.Length; i++)
            {
                if (PointInTriangle(point, i))
                    return i;
            }
            return -1;
        }

        /// <summary>判断点是否在指定三角形内（叉积法）。</summary>
        public bool PointInTriangle(FixedVector2 p, int triIndex)
        {
            var tri = Triangles[triIndex];
            var a = Vertices[tri.V0].Position;
            var b = Vertices[tri.V1].Position;
            var c = Vertices[tri.V2].Position;
            return PointInTriangle(p, a, b, c);
        }

        /// <summary>判断点是否在三角形 ABC 内（叉积法，含边界）。</summary>
        public static bool PointInTriangle(FixedVector2 p, FixedVector2 a, FixedVector2 b, FixedVector2 c)
        {
            var d1 = Sign(p, a, b);
            var d2 = Sign(p, b, c);
            var d3 = Sign(p, c, a);

            bool hasNeg = (d1 < FixedInt.Zero) || (d2 < FixedInt.Zero) || (d3 < FixedInt.Zero);
            bool hasPos = (d1 > FixedInt.Zero) || (d2 > FixedInt.Zero) || (d3 > FixedInt.Zero);

            return !(hasNeg && hasPos);
        }

        private static FixedInt Sign(FixedVector2 p1, FixedVector2 p2, FixedVector2 p3)
        {
            return (p1.X - p3.X) * (p2.Y - p3.Y) - (p2.X - p3.X) * (p1.Y - p3.Y);
        }
    }
}
