namespace FrameSync
{
    /// <summary>
    /// 确定性碰撞规避 / 转向行为工具（Steering Behaviors）。
    ///
    /// 用于帧同步中的 AI 寻路避障、角色推挤分离等。
    /// 所有方法纯函数、确定性。
    /// </summary>
    public static class FixedSteering
    {
        /// <summary>
        /// 分离力：将自身从一组邻居中推开（避免重叠堆积）。
        /// </summary>
        /// <param name="self">自身位置。</param>
        /// <param name="neighbors">邻居位置数组。</param>
        /// <param name="neighborCount">有效邻居数量。</param>
        /// <param name="separationRadius">生效半径。</param>
        /// <returns>期望的分离速度增量。</returns>
        public static FixedVector2 Separation(
            FixedVector2 self,
            FixedVector2[] neighbors, int neighborCount,
            FixedInt separationRadius)
        {
            var force = FixedVector2.Zero;
            var sqrRadius = separationRadius * separationRadius;
            int count = 0;

            for (int i = 0; i < neighborCount; i++)
            {
                var diff = self - neighbors[i];
                var sqrDist = diff.SqrMagnitude;

                if (sqrDist <= FixedInt.Zero || sqrDist > sqrRadius) continue;

                // 距离越近力越大（反比权重）
                var dist = diff.Magnitude;
                force = force + diff / dist * (FixedInt.OneVal - dist / separationRadius);
                count++;
            }

            if (count > 0)
                force = force / FixedInt.FromInt(count);

            return force;
        }

        /// <summary>
        /// 碰撞推挤分离：对两个圆形实体做对称推挤（各退一半穿透距离）。
        /// 返回 entityA 应施加的位移（entityB 应施加相反方向的等量位移）。
        /// </summary>
        public static FixedVector2 ResolveOverlap(
            FixedVector2 posA, FixedInt radiusA,
            FixedVector2 posB, FixedInt radiusB)
        {
            var result = FixedPhysics.CircleVsCircle(
                new FixedCircle(posA, radiusA),
                new FixedCircle(posB, radiusB));

            if (!result.Colliding) return FixedVector2.Zero;

            // A 往 -Normal 方向退半个穿透
            return -result.Normal * (result.Penetration * FixedInt.Half);
        }

        /// <summary>
        /// 圆形障碍物规避（前瞻射线法）。
        ///
        /// 从 position 沿 velocity 方向投射一条前瞻射线，
        /// 若命中障碍物则返回一个转向力使实体绕开。
        /// </summary>
        /// <param name="position">当前位置。</param>
        /// <param name="velocity">当前速度。</param>
        /// <param name="agentRadius">自身半径。</param>
        /// <param name="obstacles">障碍圆数组。</param>
        /// <param name="obstacleCount">有效障碍数量。</param>
        /// <param name="lookAhead">前瞻距离。</param>
        /// <returns>转向力向量；无障碍时返回 Zero。</returns>
        public static FixedVector2 ObstacleAvoidance(
            FixedVector2 position, FixedVector2 velocity,
            FixedInt agentRadius,
            FixedCircle[] obstacles, int obstacleCount,
            FixedInt lookAhead)
        {
            var speed = velocity.Magnitude;
            if (speed == FixedInt.Zero) return FixedVector2.Zero;

            var dir = velocity / speed;
            var ray = new FixedRay2D(position, dir);

            FixedRayHit closestHit = FixedRayHit.None;

            for (int i = 0; i < obstacleCount; i++)
            {
                // 膨胀障碍物半径（加上 agent 自身半径）
                var expanded = new FixedCircle(obstacles[i].Center, obstacles[i].Radius + agentRadius);
                var hit = FixedPhysics.RaycastCircle(ray, expanded, lookAhead);

                if (hit.Hit && (!closestHit.Hit || hit.Distance < closestHit.Distance))
                    closestHit = hit;
            }

            if (!closestHit.Hit) return FixedVector2.Zero;

            // 转向力：沿碰撞法线方向推，距离越近力越大
            var urgency = FixedInt.OneVal - closestHit.Distance / lookAhead;
            return closestHit.Normal * urgency * speed;
        }

        /// <summary>
        /// AABB 障碍物规避（前瞻射线法，适合墙壁/方块障碍）。
        /// </summary>
        public static FixedVector2 AABBObstacleAvoidance(
            FixedVector2 position, FixedVector2 velocity,
            FixedInt agentRadius,
            FixedAABB[] obstacles, int obstacleCount,
            FixedInt lookAhead)
        {
            var speed = velocity.Magnitude;
            if (speed == FixedInt.Zero) return FixedVector2.Zero;

            var dir = velocity / speed;
            var ray = new FixedRay2D(position, dir);

            FixedRayHit closestHit = FixedRayHit.None;

            for (int i = 0; i < obstacleCount; i++)
            {
                // Minkowski 膨胀
                var expanded = new FixedAABB(
                    obstacles[i].Min - new FixedVector2(agentRadius, agentRadius),
                    obstacles[i].Max + new FixedVector2(agentRadius, agentRadius));

                var hit = FixedPhysics.RaycastAABB(ray, expanded, lookAhead);
                if (hit.Hit && (!closestHit.Hit || hit.Distance < closestHit.Distance))
                    closestHit = hit;
            }

            if (!closestHit.Hit) return FixedVector2.Zero;

            var urgency = FixedInt.OneVal - closestHit.Distance / lookAhead;
            return closestHit.Normal * urgency * speed;
        }

        /// <summary>
        /// 沿墙面滑动：当角色碰墙时，将被阻挡的速度分量投影到墙面切线方向，
        /// 实现沿墙滑动而不是直接停下。
        /// </summary>
        /// <param name="velocity">原始期望速度。</param>
        /// <param name="wallNormal">碰撞法线（指向角色一侧）。</param>
        /// <returns>投影后的滑动速度。</returns>
        public static FixedVector2 WallSlide(FixedVector2 velocity, FixedVector2 wallNormal)
        {
            var dot = FixedVector2.Dot(velocity, wallNormal);
            if (dot >= FixedInt.Zero) return velocity; // 没有朝墙走

            // 减去法线分量，保留切线分量
            return velocity - wallNormal * dot;
        }

        /// <summary>
        /// 求趋向目标的转向力（Seek）。
        /// </summary>
        public static FixedVector2 Seek(
            FixedVector2 position, FixedVector2 target,
            FixedVector2 currentVelocity, FixedInt maxSpeed)
        {
            var desired = (target - position).Normalized * maxSpeed;
            return desired - currentVelocity;
        }

        /// <summary>
        /// 求远离目标的转向力（Flee）。
        /// </summary>
        public static FixedVector2 Flee(
            FixedVector2 position, FixedVector2 threat,
            FixedVector2 currentVelocity, FixedInt maxSpeed)
        {
            var desired = (position - threat).Normalized * maxSpeed;
            return desired - currentVelocity;
        }

        /// <summary>
        /// 到达行为（Arrive）：接近目标时减速。
        /// </summary>
        public static FixedVector2 Arrive(
            FixedVector2 position, FixedVector2 target,
            FixedVector2 currentVelocity, FixedInt maxSpeed,
            FixedInt slowRadius)
        {
            var toTarget = target - position;
            var dist     = toTarget.Magnitude;

            if (dist == FixedInt.Zero) return -currentVelocity;

            FixedInt desiredSpeed;
            if (dist < slowRadius)
                desiredSpeed = maxSpeed * dist / slowRadius;
            else
                desiredSpeed = maxSpeed;

            var desired = toTarget / dist * desiredSpeed;
            return desired - currentVelocity;
        }
    }
}
