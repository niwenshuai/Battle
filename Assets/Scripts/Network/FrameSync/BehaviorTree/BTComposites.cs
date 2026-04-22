namespace FrameSync
{
    // ═══════════════════════════════════════════════════════════════
    //  Sequence（顺序节点）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 依次执行子节点：
    /// - 子节点返回 Success → 继续下一个
    /// - 子节点返回 Failure → 立即返回 Failure
    /// - 子节点返回 Running → 返回 Running，下帧从该子节点继续
    /// - 全部 Success → 返回 Success
    /// </summary>
    public class BTSequence : BTComposite
    {
        int _current;

        protected override void OnEnter(BTContext ctx)
        {
            _current = 0;
        }

        protected override BTStatus OnTick(BTContext ctx)
        {
            while (_current < Children.Count)
            {
                var status = Children[_current].Tick(ctx);
                if (status == BTStatus.Running) return BTStatus.Running;
                if (status == BTStatus.Failure) return BTStatus.Failure;
                _current++;
            }
            return BTStatus.Success;
        }

        public override void Reset()
        {
            base.Reset();
            _current = 0;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Selector（选择节点）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 依次尝试子节点：
    /// - 子节点返回 Failure → 继续下一个
    /// - 子节点返回 Success → 立即返回 Success
    /// - 子节点返回 Running → 返回 Running，下帧从该子节点继续
    /// - 全部 Failure → 返回 Failure
    /// </summary>
    public class BTSelector : BTComposite
    {
        int _current;

        protected override void OnEnter(BTContext ctx)
        {
            _current = 0;
        }

        protected override BTStatus OnTick(BTContext ctx)
        {
            while (_current < Children.Count)
            {
                var status = Children[_current].Tick(ctx);
                if (status == BTStatus.Running) return BTStatus.Running;
                if (status == BTStatus.Success) return BTStatus.Success;
                _current++;
            }
            return BTStatus.Failure;
        }

        public override void Reset()
        {
            base.Reset();
            _current = 0;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Parallel（并行节点）
    // ═══════════════════════════════════════════════════════════════

    public enum BTParallelPolicy
    {
        /// <summary>所有子节点成功才成功；任一失败则失败。</summary>
        RequireAll,
        /// <summary>任一子节点成功即成功；全部失败才失败。</summary>
        RequireOne
    }

    /// <summary>
    /// 每帧同时执行所有未完成的子节点。
    /// 根据策略决定何时返回 Success/Failure。
    /// </summary>
    public class BTParallel : BTComposite
    {
        public BTParallelPolicy Policy;

        BTStatus[] _childStatus;

        public BTParallel(BTParallelPolicy policy = BTParallelPolicy.RequireAll)
        {
            Policy = policy;
        }

        protected override void OnEnter(BTContext ctx)
        {
            _childStatus = new BTStatus[Children.Count];
            for (int i = 0; i < _childStatus.Length; i++)
                _childStatus[i] = BTStatus.Running;
        }

        protected override BTStatus OnTick(BTContext ctx)
        {
            int successCount = 0;
            int failureCount = 0;
            int count = Children.Count;

            for (int i = 0; i < count; i++)
            {
                // 已完成的子节点不再 Tick
                if (_childStatus[i] != BTStatus.Running)
                {
                    if (_childStatus[i] == BTStatus.Success) successCount++;
                    else failureCount++;
                    continue;
                }

                _childStatus[i] = Children[i].Tick(ctx);

                if (_childStatus[i] == BTStatus.Success) successCount++;
                else if (_childStatus[i] == BTStatus.Failure) failureCount++;
            }

            switch (Policy)
            {
                case BTParallelPolicy.RequireAll:
                    if (failureCount > 0) return BTStatus.Failure;
                    if (successCount == count) return BTStatus.Success;
                    break;

                case BTParallelPolicy.RequireOne:
                    if (successCount > 0) return BTStatus.Success;
                    if (failureCount == count) return BTStatus.Failure;
                    break;
            }

            return BTStatus.Running;
        }

        public override void Reset()
        {
            base.Reset();
            _childStatus = null;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  RandomSelector（随机选择节点）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 与 Selector 类似，但每次进入时使用确定性随机打乱子节点执行顺序。
    /// </summary>
    public class BTRandomSelector : BTComposite
    {
        int _current;
        int[] _order;

        protected override void OnEnter(BTContext ctx)
        {
            _current = 0;
            int n = Children.Count;
            if (_order == null || _order.Length != n)
                _order = new int[n];
            for (int i = 0; i < n; i++)
                _order[i] = i;

            // Fisher-Yates 洗牌（确定性）
            for (int i = n - 1; i > 0; i--)
            {
                int j = ctx.Random.Next(i + 1);
                int tmp = _order[i];
                _order[i] = _order[j];
                _order[j] = tmp;
            }
        }

        protected override BTStatus OnTick(BTContext ctx)
        {
            while (_current < Children.Count)
            {
                var status = Children[_order[_current]].Tick(ctx);
                if (status == BTStatus.Running) return BTStatus.Running;
                if (status == BTStatus.Success) return BTStatus.Success;
                _current++;
            }
            return BTStatus.Failure;
        }

        public override void Reset()
        {
            base.Reset();
            _current = 0;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  RandomSequence（随机顺序节点）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 与 Sequence 类似，但每次进入时随机打乱子节点执行顺序。
    /// </summary>
    public class BTRandomSequence : BTComposite
    {
        int _current;
        int[] _order;

        protected override void OnEnter(BTContext ctx)
        {
            _current = 0;
            int n = Children.Count;
            if (_order == null || _order.Length != n)
                _order = new int[n];
            for (int i = 0; i < n; i++)
                _order[i] = i;

            for (int i = n - 1; i > 0; i--)
            {
                int j = ctx.Random.Next(i + 1);
                int tmp = _order[i];
                _order[i] = _order[j];
                _order[j] = tmp;
            }
        }

        protected override BTStatus OnTick(BTContext ctx)
        {
            while (_current < Children.Count)
            {
                var status = Children[_order[_current]].Tick(ctx);
                if (status == BTStatus.Running) return BTStatus.Running;
                if (status == BTStatus.Failure) return BTStatus.Failure;
                _current++;
            }
            return BTStatus.Success;
        }

        public override void Reset()
        {
            base.Reset();
            _current = 0;
        }
    }
}
