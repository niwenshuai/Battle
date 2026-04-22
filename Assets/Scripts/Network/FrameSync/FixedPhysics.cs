namespace FrameSync
{
    /// <summary>
    /// 确定性 2D 物理工具类 —— 碰撞检测 + 射线检测。
    ///
    /// 全部使用 FixedInt 运算，保证跨平台确定性。
    /// 所有方法都是无状态的纯函数，可安全在帧同步逻辑中调用。
    /// </summary>
    public static class FixedPhysics
    {
        // ═══════════════════════════════════════════════════════
        // 1. 点 / 线段 基础几何工具
        // ═══════════════════════════════════════════════════════

        /// <summary>点到线段的最近点。</summary>
        public static FixedVector2 ClosestPointOnSegment(
            FixedVector2 point, FixedVector2 segA, FixedVector2 segB)
        {
            var ab = segB - segA;
            var sqrLen = FixedVector2.Dot(ab, ab);
            if (sqrLen == FixedInt.Zero) return segA;

            var t = FixedVector2.Dot(point - segA, ab) / sqrLen;
            t = FixedInt.Clamp(t, FixedInt.Zero, FixedInt.OneVal);
            return segA + ab * t;
        }

        /// <summary>点到线段的最短距离的平方。</summary>
        public static FixedInt SqrDistancePointToSegment(
            FixedVector2 point, FixedVector2 segA, FixedVector2 segB)
        {
            var closest = ClosestPointOnSegment(point, segA, segB);
            return FixedVector2.SqrDistance(point, closest);
        }

        /// <summary>两条线段之间的最短距离的平方。</summary>
        public static FixedInt SqrDistanceSegments(
            FixedVector2 a1, FixedVector2 a2,
            FixedVector2 b1, FixedVector2 b2)
        {
            var d1 = SqrDistancePointToSegment(a1, b1, b2);
            var d2 = SqrDistancePointToSegment(a2, b1, b2);
            var d3 = SqrDistancePointToSegment(b1, a1, a2);
            var d4 = SqrDistancePointToSegment(b2, a1, a2);
            return FixedInt.Min(FixedInt.Min(d1, d2), FixedInt.Min(d3, d4));
        }

        /// <summary>点是否在圆内。</summary>
        public static bool PointInCircle(FixedVector2 point, FixedCircle circle)
        {
            return FixedVector2.SqrDistance(point, circle.Center) <= circle.Radius * circle.Radius;
        }

        /// <summary>点是否在 AABB 内。</summary>
        public static bool PointInAABB(FixedVector2 point, FixedAABB aabb)
        {
            return aabb.Contains(point);
        }

        // ═══════════════════════════════════════════════════════
        // 2. 碰撞检测（返回 CollisionResult 含穿透信息）
        // ═══════════════════════════════════════════════════════

        // ── 圆 vs 圆 ────────────────────────────────────────

        public static CollisionResult CircleVsCircle(FixedCircle a, FixedCircle b)
        {
            var delta = b.Center - a.Center;
            var sqrDist = delta.SqrMagnitude;
            var radSum = a.Radius + b.Radius;
            var sqrRadSum = radSum * radSum;

            if (sqrDist > sqrRadSum)
                return CollisionResult.None;

            var dist = delta.Magnitude;
            if (dist == FixedInt.Zero)
            {
                // 完全重叠，随意给一个法线
                return new CollisionResult
                {
                    Colliding   = true,
                    Normal      = FixedVector2.Right,
                    Penetration = radSum,
                };
            }

            return new CollisionResult
            {
                Colliding   = true,
                Normal      = delta / dist,
                Penetration = radSum - dist,
            };
        }

        // ── 圆 vs AABB ──────────────────────────────────────

        public static CollisionResult CircleVsAABB(FixedCircle circle, FixedAABB aabb)
        {
            // AABB 上离圆心最近的点
            var closest = new FixedVector2(
                FixedInt.Clamp(circle.Center.X, aabb.Min.X, aabb.Max.X),
                FixedInt.Clamp(circle.Center.Y, aabb.Min.Y, aabb.Max.Y));

            var delta   = circle.Center - closest;
            var sqrDist = delta.SqrMagnitude;
            var sqrR    = circle.Radius * circle.Radius;

            if (sqrDist > sqrR)
                return CollisionResult.None;

            var dist = delta.Magnitude;
            FixedVector2 normal;
            FixedInt penetration;

            if (dist == FixedInt.Zero)
            {
                // 圆心在 AABB 内部 —— 找最短推出轴
                var center = aabb.Center;
                var half   = aabb.HalfSize;
                var dx = circle.Center.X - center.X;
                var dy = circle.Center.Y - center.Y;
                var overlapX = half.X - FixedInt.Abs(dx);
                var overlapY = half.Y - FixedInt.Abs(dy);

                if (overlapX < overlapY)
                {
                    normal = dx >= FixedInt.Zero ? FixedVector2.Right : FixedVector2.Left;
                    penetration = overlapX + circle.Radius;
                }
                else
                {
                    normal = dy >= FixedInt.Zero ? FixedVector2.Up : FixedVector2.Down;
                    penetration = overlapY + circle.Radius;
                }
            }
            else
            {
                normal      = delta / dist;
                penetration = circle.Radius - dist;
            }

            return new CollisionResult
            {
                Colliding   = true,
                Normal      = normal,
                Penetration = penetration,
            };
        }

        // ── AABB vs AABB ────────────────────────────────────

        public static CollisionResult AABBvsAABB(FixedAABB a, FixedAABB b)
        {
            var overlapX = FixedInt.Min(a.Max.X, b.Max.X) - FixedInt.Max(a.Min.X, b.Min.X);
            var overlapY = FixedInt.Min(a.Max.Y, b.Max.Y) - FixedInt.Max(a.Min.Y, b.Min.Y);

            if (overlapX <= FixedInt.Zero || overlapY <= FixedInt.Zero)
                return CollisionResult.None;

            FixedVector2 normal;
            FixedInt penetration;

            if (overlapX < overlapY)
            {
                penetration = overlapX;
                var d = b.Center.X - a.Center.X;
                normal = d >= FixedInt.Zero ? FixedVector2.Right : FixedVector2.Left;
            }
            else
            {
                penetration = overlapY;
                var d = b.Center.Y - a.Center.Y;
                normal = d >= FixedInt.Zero ? FixedVector2.Up : FixedVector2.Down;
            }

            return new CollisionResult
            {
                Colliding   = true,
                Normal      = normal,
                Penetration = penetration,
            };
        }

        // ── 圆 vs 胶囊体 ────────────────────────────────────

        public static CollisionResult CircleVsCapsule(FixedCircle circle, FixedCapsule capsule)
        {
            var closest = ClosestPointOnSegment(circle.Center, capsule.PointA, capsule.PointB);
            var combined = new FixedCircle(closest, capsule.Radius);
            return CircleVsCircle(circle, combined);
        }

        // ── 胶囊体 vs 胶囊体 ────────────────────────────────

        public static CollisionResult CapsuleVsCapsule(FixedCapsule a, FixedCapsule b)
        {
            // 两线段最近点对
            var sqrDist = SqrDistanceSegments(a.PointA, a.PointB, b.PointA, b.PointB);
            var radSum  = a.Radius + b.Radius;

            if (sqrDist > radSum * radSum)
                return CollisionResult.None;

            // 精确计算法线：找到各自线段上的最近点
            var closestOnA = ClosestPointOnSegment(b.Center, a.PointA, a.PointB);
            var closestOnB = ClosestPointOnSegment(closestOnA, b.PointA, b.PointB);

            var delta = closestOnB - closestOnA;
            var dist  = delta.Magnitude;

            if (dist == FixedInt.Zero)
                return new CollisionResult { Colliding = true, Normal = FixedVector2.Right, Penetration = radSum };

            return new CollisionResult
            {
                Colliding   = true,
                Normal      = delta / dist,
                Penetration = radSum - dist,
            };
        }

        // ── AABB vs 圆（反向调用） ──────────────────────────

        public static CollisionResult AABBvsCircle(FixedAABB aabb, FixedCircle circle)
        {
            var result = CircleVsAABB(circle, aabb);
            if (result.Colliding) result.Normal = -result.Normal;
            return result;
        }

        // ── OBB vs OBB（SAT 分离轴） ────────────────────────

        public static bool OBBvsOBB(FixedOBB a, FixedOBB b)
        {
            // 4 条候选分离轴（两个 OBB 各贡献 2 条）
            var axesA = new FixedVector2[2];
            axesA[0] = a.Axis;
            axesA[1] = a.Axis.Perpendicular;

            var axesB = new FixedVector2[2];
            axesB[0] = b.Axis;
            axesB[1] = b.Axis.Perpendicular;

            var cornersA = new FixedVector2[4];
            var cornersB = new FixedVector2[4];
            a.GetCorners(cornersA);
            b.GetCorners(cornersB);

            for (int i = 0; i < 2; i++)
            {
                if (IsSeparatingAxis(axesA[i], cornersA, cornersB)) return false;
                if (IsSeparatingAxis(axesB[i], cornersA, cornersB)) return false;
            }
            return true;
        }

        private static bool IsSeparatingAxis(FixedVector2 axis, FixedVector2[] cornersA, FixedVector2[] cornersB)
        {
            ProjectCorners(axis, cornersA, out var minA, out var maxA);
            ProjectCorners(axis, cornersB, out var minB, out var maxB);
            return maxA < minB || maxB < minA;
        }

        private static void ProjectCorners(FixedVector2 axis, FixedVector2[] corners,
                                            out FixedInt min, out FixedInt max)
        {
            min = max = FixedVector2.Dot(corners[0], axis);
            for (int i = 1; i < corners.Length; i++)
            {
                var proj = FixedVector2.Dot(corners[i], axis);
                if (proj < min) min = proj;
                if (proj > max) max = proj;
            }
        }

        // ═══════════════════════════════════════════════════════
        // 3. 射线检测
        // ═══════════════════════════════════════════════════════

        /// <summary>射线 vs 圆。</summary>
        public static FixedRayHit RaycastCircle(FixedRay2D ray, FixedCircle circle, FixedInt maxDist)
        {
            var oc = ray.Origin - circle.Center;
            var b  = FixedVector2.Dot(oc, ray.Direction);
            var c  = FixedVector2.Dot(oc, oc) - circle.Radius * circle.Radius;

            var discriminant = b * b - c;
            if (discriminant < FixedInt.Zero)
                return FixedRayHit.None;

            var sqrtD = FixedInt.Sqrt(discriminant);
            var t = -b - sqrtD;

            // 如果 t < 0，射线起点在圆内，取另一个交点
            if (t < FixedInt.Zero) t = -b + sqrtD;
            if (t < FixedInt.Zero || t > maxDist)
                return FixedRayHit.None;

            var point  = ray.GetPoint(t);
            var normal = (point - circle.Center).Normalized;

            return new FixedRayHit
            {
                Hit      = true,
                Distance = t,
                Point    = point,
                Normal   = normal,
            };
        }

        /// <summary>射线 vs AABB（Slab 方法）。</summary>
        public static FixedRayHit RaycastAABB(FixedRay2D ray, FixedAABB aabb, FixedInt maxDist)
        {
            // 处理方向分量为 0 的情况
            FixedInt tMin = FixedInt.Zero;
            FixedInt tMax = maxDist;
            FixedVector2 hitNormal = FixedVector2.Zero;

            // X slab
            if (!SlabTest(ray.Origin.X, ray.Direction.X, aabb.Min.X, aabb.Max.X,
                          FixedVector2.Left, FixedVector2.Right,
                          ref tMin, ref tMax, ref hitNormal))
                return FixedRayHit.None;

            // Y slab
            if (!SlabTest(ray.Origin.Y, ray.Direction.Y, aabb.Min.Y, aabb.Max.Y,
                          FixedVector2.Down, FixedVector2.Up,
                          ref tMin, ref tMax, ref hitNormal))
                return FixedRayHit.None;

            if (tMin < FixedInt.Zero) tMin = FixedInt.Zero;
            if (tMin > maxDist) return FixedRayHit.None;

            return new FixedRayHit
            {
                Hit      = true,
                Distance = tMin,
                Point    = ray.GetPoint(tMin),
                Normal   = hitNormal,
            };
        }

        private static bool SlabTest(
            FixedInt origin, FixedInt dir, FixedInt slabMin, FixedInt slabMax,
            FixedVector2 normalMin, FixedVector2 normalMax,
            ref FixedInt tMin, ref FixedInt tMax, ref FixedVector2 hitNormal)
        {
            if (FixedInt.Abs(dir) < FixedInt.FromRaw(1)) // ≈ 0
            {
                // 射线平行于此轴
                if (origin < slabMin || origin > slabMax)
                    return false;
                return true;
            }

            var t1 = (slabMin - origin) / dir;
            var t2 = (slabMax - origin) / dir;
            var nNear = normalMin;

            if (t1 > t2)
            {
                (t1, t2) = (t2, t1);
                nNear = normalMax;
            }

            if (t1 > tMin)
            {
                tMin = t1;
                hitNormal = nNear;
            }
            if (t2 < tMax) tMax = t2;
            if (tMin > tMax) return false;
            return true;
        }

        /// <summary>射线 vs 线段。</summary>
        public static FixedRayHit RaycastSegment(
            FixedRay2D ray, FixedVector2 segA, FixedVector2 segB, FixedInt maxDist)
        {
            var segDir = segB - segA;
            var denom  = FixedVector2.Cross(ray.Direction, segDir);

            // 平行
            if (FixedInt.Abs(denom) < FixedInt.FromRaw(1))
                return FixedRayHit.None;

            var diff = segA - ray.Origin;
            var t = FixedVector2.Cross(diff, segDir) / denom;
            var u = FixedVector2.Cross(diff, ray.Direction) / denom;

            if (t < FixedInt.Zero || t > maxDist || u < FixedInt.Zero || u > FixedInt.OneVal)
                return FixedRayHit.None;

            var point = ray.GetPoint(t);
            // 线段法线（取与射线方向相对的那一面）
            var segNormal = segDir.Perpendicular.Normalized;
            if (FixedVector2.Dot(segNormal, ray.Direction) > FixedInt.Zero)
                segNormal = -segNormal;

            return new FixedRayHit
            {
                Hit      = true,
                Distance = t,
                Point    = point,
                Normal   = segNormal,
            };
        }

        /// <summary>射线 vs 胶囊体（等价于射线 vs 膨胀线段）。</summary>
        public static FixedRayHit RaycastCapsule(
            FixedRay2D ray, FixedCapsule capsule, FixedInt maxDist)
        {
            // 先测两端点圆
            var hitA = RaycastCircle(ray, new FixedCircle(capsule.PointA, capsule.Radius), maxDist);
            var hitB = RaycastCircle(ray, new FixedCircle(capsule.PointB, capsule.Radius), maxDist);

            // 测两条平行边线段
            var segDir = (capsule.PointB - capsule.PointA).Normalized;
            var offset = segDir.Perpendicular * capsule.Radius;
            var hitS1 = RaycastSegment(ray,
                capsule.PointA + offset, capsule.PointB + offset, maxDist);
            var hitS2 = RaycastSegment(ray,
                capsule.PointA - offset, capsule.PointB - offset, maxDist);

            // 取最近命中
            var best = FixedRayHit.None;
            TakeCloser(ref best, hitA);
            TakeCloser(ref best, hitB);
            TakeCloser(ref best, hitS1);
            TakeCloser(ref best, hitS2);
            return best;
        }

        private static void TakeCloser(ref FixedRayHit current, FixedRayHit candidate)
        {
            if (!candidate.Hit) return;
            if (!current.Hit || candidate.Distance < current.Distance)
                current = candidate;
        }

        // ═══════════════════════════════════════════════════════
        // 4. 扫掠检测 (Sweep / Continuous)
        // ═══════════════════════════════════════════════════════

        /// <summary>
        /// 圆沿速度向量运动，检测是否撞到另一个圆。
        /// 返回安全移动比例 t ∈ [0, 1]。
        /// </summary>
        public static bool SweepCircleVsCircle(
            FixedCircle moving, FixedVector2 velocity,
            FixedCircle stationary,
            out FixedInt t, out FixedVector2 hitNormal)
        {
            // 等价于射线 vs 膨胀圆
            var combinedRadius = moving.Radius + stationary.Radius;
            var expandedCircle = new FixedCircle(stationary.Center, combinedRadius);
            var moveDist       = velocity.Magnitude;

            t = FixedInt.OneVal;
            hitNormal = FixedVector2.Zero;

            if (moveDist == FixedInt.Zero) return false;

            var ray = new FixedRay2D(moving.Center, velocity);
            var hit = RaycastCircle(ray, expandedCircle, moveDist);

            if (!hit.Hit) return false;

            t = hit.Distance / moveDist;
            hitNormal = hit.Normal;
            return true;
        }

        /// <summary>
        /// 圆沿速度向量运动，检测是否撞到 AABB。
        /// </summary>
        public static bool SweepCircleVsAABB(
            FixedCircle moving, FixedVector2 velocity,
            FixedAABB aabb,
            out FixedInt t, out FixedVector2 hitNormal)
        {
            // Minkowski 膨胀 AABB
            var expanded = new FixedAABB(
                aabb.Min - new FixedVector2(moving.Radius, moving.Radius),
                aabb.Max + new FixedVector2(moving.Radius, moving.Radius));

            var moveDist = velocity.Magnitude;
            t = FixedInt.OneVal;
            hitNormal = FixedVector2.Zero;

            if (moveDist == FixedInt.Zero) return false;

            var ray = new FixedRay2D(moving.Center, velocity);
            var hit = RaycastAABB(ray, expanded, moveDist);

            if (!hit.Hit) return false;

            t = hit.Distance / moveDist;
            hitNormal = hit.Normal;
            return true;
        }
    }
}
