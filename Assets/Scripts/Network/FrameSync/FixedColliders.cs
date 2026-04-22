namespace FrameSync
{
    // ═══════════════════════════════════════════════════════════
    // 确定性 2D 碰撞体定义
    //
    // 所有碰撞体都是值类型，不依赖 MonoBehaviour。
    // 在帧同步逻辑中直接作为实体的组成部分使用。
    // ═══════════════════════════════════════════════════════════

    /// <summary>碰撞体类型。</summary>
    public enum ColliderType : byte
    {
        Circle,
        AABB,
        OBB,
        Capsule,
        Point,
    }

    // ── 圆形 ─────────────────────────────────────────────────

    /// <summary>圆形碰撞体。</summary>
    public struct FixedCircle
    {
        public FixedVector2 Center;
        public FixedInt     Radius;

        public FixedCircle(FixedVector2 center, FixedInt radius)
        {
            Center = center;
            Radius = radius;
        }

        /// <summary>获取包围 AABB。</summary>
        public FixedAABB BoundingBox => new(
            Center - new FixedVector2(Radius, Radius),
            Center + new FixedVector2(Radius, Radius));
    }

    // ── AABB ─────────────────────────────────────────────────

    /// <summary>轴对齐包围盒。</summary>
    public struct FixedAABB
    {
        public FixedVector2 Min; // 左下角
        public FixedVector2 Max; // 右上角

        public FixedAABB(FixedVector2 min, FixedVector2 max)
        {
            Min = min;
            Max = max;
        }

        /// <summary>从中心和半尺寸构建。</summary>
        public static FixedAABB FromCenter(FixedVector2 center, FixedVector2 halfSize)
        {
            return new FixedAABB(center - halfSize, center + halfSize);
        }

        public FixedVector2 Center => new(
            (Min.X + Max.X) * FixedInt.Half,
            (Min.Y + Max.Y) * FixedInt.Half);

        public FixedVector2 HalfSize => new(
            (Max.X - Min.X) * FixedInt.Half,
            (Max.Y - Min.Y) * FixedInt.Half);

        public FixedVector2 Size => new(Max.X - Min.X, Max.Y - Min.Y);

        /// <summary>点是否在 AABB 内。</summary>
        public bool Contains(FixedVector2 point)
        {
            return point.X >= Min.X && point.X <= Max.X &&
                   point.Y >= Min.Y && point.Y <= Max.Y;
        }
    }

    // ── OBB ──────────────────────────────────────────────────

    /// <summary>有向包围盒（2D 旋转矩形）。</summary>
    public struct FixedOBB
    {
        public FixedVector2 Center;
        public FixedVector2 HalfSize;  // 本地空间半宽高
        /// <summary>本地 X 轴方向（单位向量），Y 轴 = Axis.Perpendicular。</summary>
        public FixedVector2 Axis;

        public FixedOBB(FixedVector2 center, FixedVector2 halfSize, FixedVector2 axis)
        {
            Center   = center;
            HalfSize = halfSize;
            Axis     = axis.Normalized;
        }

        /// <summary>获取 4 个顶点（逆时针）。</summary>
        public void GetCorners(FixedVector2[] corners)
        {
            var axisX = Axis * HalfSize.X;
            var axisY = Axis.Perpendicular * HalfSize.Y;
            corners[0] = Center - axisX - axisY;
            corners[1] = Center + axisX - axisY;
            corners[2] = Center + axisX + axisY;
            corners[3] = Center - axisX + axisY;
        }
    }

    // ── 胶囊体 ───────────────────────────────────────────────

    /// <summary>2D 胶囊体（线段 + 半径）。</summary>
    public struct FixedCapsule
    {
        public FixedVector2 PointA;  // 线段端点 A
        public FixedVector2 PointB;  // 线段端点 B
        public FixedInt     Radius;

        public FixedCapsule(FixedVector2 a, FixedVector2 b, FixedInt radius)
        {
            PointA = a;
            PointB = b;
            Radius = radius;
        }

        public FixedVector2 Center => new(
            (PointA.X + PointB.X) * FixedInt.Half,
            (PointA.Y + PointB.Y) * FixedInt.Half);
    }

    // ── 射线 ─────────────────────────────────────────────────

    /// <summary>2D 射线。</summary>
    public struct FixedRay2D
    {
        public FixedVector2 Origin;
        public FixedVector2 Direction; // 单位向量

        public FixedRay2D(FixedVector2 origin, FixedVector2 direction)
        {
            Origin    = origin;
            Direction = direction.Normalized;
        }

        public FixedVector2 GetPoint(FixedInt t) => Origin + Direction * t;
    }

    // ── 射线检测结果 ─────────────────────────────────────────

    /// <summary>射线检测命中结果。</summary>
    public struct FixedRayHit
    {
        public bool         Hit;
        public FixedInt     Distance;  // 射线参数 t
        public FixedVector2 Point;     // 命中点
        public FixedVector2 Normal;    // 命中面法线

        public static readonly FixedRayHit None = new() { Hit = false };
    }

    // ── 碰撞结果 ─────────────────────────────────────────────

    /// <summary>碰撞检测结果，包含分离向量。</summary>
    public struct CollisionResult
    {
        public bool         Colliding;
        public FixedVector2 Normal;      // A→B 方向的碰撞法线
        public FixedInt     Penetration; // 穿透深度（正值）

        /// <summary>将 A 沿此向量移动即可分离。</summary>
        public FixedVector2 SeparationVector => Normal * Penetration;

        public static readonly CollisionResult None = new() { Colliding = false };
    }
}
