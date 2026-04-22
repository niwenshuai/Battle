using UnityEngine;

namespace FrameSync
{
    /// <summary>
    /// NavMesh 数据的 ScriptableObject 包装，方便在 Unity 中序列化和引用。
    /// 由编辑器烘焙工具生成。
    /// </summary>
    [CreateAssetMenu(fileName = "FixedNavMesh", menuName = "FrameSync/Fixed NavMesh Asset")]
    public class FixedNavMeshAsset : ScriptableObject
    {
        [Header("Vertices")]
        public Vector2[] BakedVertices;

        [Header("Triangles (3 indices per tri)")]
        public int[] BakedTriangleIndices;

        [Header("Neighbor data (3 neighbors per tri, -1 = boundary)")]
        public int[] BakedNeighbors;

        /// <summary>
        /// 从 ScriptableObject 构建运行时 FixedNavMeshData（确定性定点数）。
        /// 从 float → FixedInt 的转换只在加载时做一次。
        /// </summary>
        public FixedNavMeshData ToRuntimeData()
        {
            var data = new FixedNavMeshData();

            // 顶点
            data.Vertices = new NavVertex[BakedVertices.Length];
            for (int i = 0; i < BakedVertices.Length; i++)
            {
                data.Vertices[i] = new NavVertex(
                    FixedInt.FromFloat(BakedVertices[i].x),
                    FixedInt.FromFloat(BakedVertices[i].y));
            }

            // 三角形
            int triCount = BakedTriangleIndices.Length / 3;
            data.Triangles = new NavTriangle[triCount];
            for (int i = 0; i < triCount; i++)
            {
                int baseIdx = i * 3;
                data.Triangles[i] = new NavTriangle
                {
                    V0 = BakedTriangleIndices[baseIdx],
                    V1 = BakedTriangleIndices[baseIdx + 1],
                    V2 = BakedTriangleIndices[baseIdx + 2],
                    Neighbor0 = BakedNeighbors[baseIdx],
                    Neighbor1 = BakedNeighbors[baseIdx + 1],
                    Neighbor2 = BakedNeighbors[baseIdx + 2],
                };
            }

            return data;
        }
    }
}
