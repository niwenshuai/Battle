using System;

namespace FrameSync
{
    // ═══════════════════════════════════════════════════════════════
    //  Inverter（取反节点）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 将子节点的 Success 翻转为 Failure，Failure 翻转为 Success。
    /// Running 保持不变。
    /// </summary>
    public class BTInverter : BTDecorator
    {
        protected override BTStatus OnTick(BTContext ctx)
        {
            var s = Child.Tick(ctx);
            if (s == BTStatus.Success) return BTStatus.Failure;
            if (s == BTStatus.Failure) return BTStatus.Success;
            return BTStatus.Running;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  AlwaysSucceed / AlwaysFail
    // ═══════════════════════════════════════════════════════════════

    /// <summary>无论子节点返回什么（Running 除外），都返回 Success。</summary>
    public class BTAlwaysSucceed : BTDecorator
    {
        protected override BTStatus OnTick(BTContext ctx)
        {
            var s = Child.Tick(ctx);
            if (s == BTStatus.Running) return BTStatus.Running;
            return BTStatus.Success;
        }
    }

    /// <summary>无论子节点返回什么（Running 除外），都返回 Failure。</summary>
    public class BTAlwaysFail : BTDecorator
    {
        protected override BTStatus OnTick(BTContext ctx)
        {
            var s = Child.Tick(ctx);
            if (s == BTStatus.Running) return BTStatus.Running;
            return BTStatus.Failure;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Repeater（重复节点）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 重复执行子节点指定次数。
    /// repeatCount = -1 表示永远重复（直到树被外部停止）。
    /// ignoreFailure = true 时子节点失败也继续重复；
    /// ignoreFailure = false 时子节点失败则整体 Failure。
    /// </summary>
    public class BTRepeater : BTDecorator
    {
        public int RepeatCount;
        public bool IgnoreFailure;

        int _iteration;

        public BTRepeater(int repeatCount = -1, bool ignoreFailure = true)
        {
            RepeatCount = repeatCount;
            IgnoreFailure = ignoreFailure;
        }

        protected override void OnEnter(BTContext ctx)
        {
            _iteration = 0;
        }

        protected override BTStatus OnTick(BTContext ctx)
        {
            while (RepeatCount < 0 || _iteration < RepeatCount)
            {
                var s = Child.Tick(ctx);

                if (s == BTStatus.Running)
                    return BTStatus.Running;

                if (s == BTStatus.Failure && !IgnoreFailure)
                    return BTStatus.Failure;

                _iteration++;
                Child.Reset();
            }
            return BTStatus.Success;
        }

        public override void Reset()
        {
            base.Reset();
            _iteration = 0;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  UntilFail（直到失败）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 持续执行子节点，直到子节点返回 Failure 时返回 Success。
    /// </summary>
    public class BTUntilFail : BTDecorator
    {
        protected override BTStatus OnTick(BTContext ctx)
        {
            var s = Child.Tick(ctx);
            if (s == BTStatus.Failure) return BTStatus.Success;
            if (s == BTStatus.Success) Child.Reset();
            return BTStatus.Running;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  UntilSuccess（直到成功）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 持续执行子节点，直到子节点返回 Success 时返回 Success。
    /// </summary>
    public class BTUntilSuccess : BTDecorator
    {
        protected override BTStatus OnTick(BTContext ctx)
        {
            var s = Child.Tick(ctx);
            if (s == BTStatus.Success) return BTStatus.Success;
            if (s == BTStatus.Failure) Child.Reset();
            return BTStatus.Running;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Cooldown（冷却节点）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 子节点执行完成后，在 cooldownFrames 帧内再次 Tick 直接返回 Failure。
    /// 基于帧计数实现，完全确定性。
    /// </summary>
    public class BTCooldown : BTDecorator
    {
        public int CooldownFrames;

        int _readyFrame; // 冷却结束可再次执行的帧号

        public BTCooldown(int cooldownFrames)
        {
            CooldownFrames = cooldownFrames;
        }

        protected override BTStatus OnTick(BTContext ctx)
        {
            if (ctx.Frame < _readyFrame)
                return BTStatus.Failure;

            var s = Child.Tick(ctx);

            if (s != BTStatus.Running)
                _readyFrame = ctx.Frame + CooldownFrames;

            return s;
        }

        public override void Reset()
        {
            base.Reset();
            _readyFrame = 0;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  ConditionalGuard（条件守卫）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 每帧先检查条件：
    /// - 条件为 true → Tick 子节点
    /// - 条件为 false → 返回 Failure（如果子节点正在 Running，会先 Reset 子节点）
    /// </summary>
    public class BTGuard : BTDecorator
    {
        readonly Func<BTContext, bool> _condition;

        public BTGuard(Func<BTContext, bool> condition)
        {
            _condition = condition;
        }

        protected override BTStatus OnTick(BTContext ctx)
        {
            if (!_condition(ctx))
            {
                if (Child.Status == BTStatus.Running)
                    Child.Reset();
                return BTStatus.Failure;
            }
            return Child.Tick(ctx);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  TimeLimit（限时节点）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 子节点必须在指定帧数内完成，否则强制返回 Failure。
    /// </summary>
    public class BTTimeLimit : BTDecorator
    {
        public int LimitFrames;

        int _startFrame;

        public BTTimeLimit(int limitFrames)
        {
            LimitFrames = limitFrames;
        }

        protected override void OnEnter(BTContext ctx)
        {
            _startFrame = ctx.Frame;
        }

        protected override BTStatus OnTick(BTContext ctx)
        {
            if (ctx.Frame - _startFrame >= LimitFrames)
            {
                Child.Reset();
                return BTStatus.Failure;
            }
            return Child.Tick(ctx);
        }

        public override void Reset()
        {
            base.Reset();
            _startFrame = 0;
        }
    }
}
