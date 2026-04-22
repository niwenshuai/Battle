using System;

namespace FrameSync
{
    // ═══════════════════════════════════════════════════════════════
    //  BTAction（委托动作节点）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 通过委托定义行为的叶节点。
    /// tickFunc 每帧调用，返回 BTStatus。
    /// 可选 enterFunc / exitFunc 用于初始化和清理。
    /// </summary>
    public class BTAction : BTNode
    {
        readonly Func<BTContext, BTStatus> _tick;
        readonly Action<BTContext> _enter;
        readonly Action<BTContext> _exit;

        public BTAction(Func<BTContext, BTStatus> tick,
                        Action<BTContext> enter = null,
                        Action<BTContext> exit = null)
        {
            _tick = tick;
            _enter = enter;
            _exit = exit;
        }

        protected override void OnEnter(BTContext ctx) => _enter?.Invoke(ctx);
        protected override BTStatus OnTick(BTContext ctx) => _tick(ctx);
        protected override void OnExit(BTContext ctx) => _exit?.Invoke(ctx);
    }

    // ═══════════════════════════════════════════════════════════════
    //  BTSimpleAction（单帧动作）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 单帧完成的简单动作，执行一次后立即返回 Success。
    /// </summary>
    public class BTSimpleAction : BTNode
    {
        readonly Action<BTContext> _action;

        public BTSimpleAction(Action<BTContext> action)
        {
            _action = action;
        }

        protected override BTStatus OnTick(BTContext ctx)
        {
            _action(ctx);
            return BTStatus.Success;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  BTCondition（条件判断节点）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 条件检查叶节点：条件为 true 返回 Success，否则 Failure。
    /// 永远不会返回 Running。
    /// </summary>
    public class BTCondition : BTNode
    {
        readonly Func<BTContext, bool> _condition;

        public BTCondition(Func<BTContext, bool> condition)
        {
            _condition = condition;
        }

        protected override BTStatus OnTick(BTContext ctx)
        {
            return _condition(ctx) ? BTStatus.Success : BTStatus.Failure;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  BTWait（等待节点）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 等待指定帧数后返回 Success。期间持续返回 Running。
    /// </summary>
    public class BTWait : BTNode
    {
        public int WaitFrames;

        int _elapsed;

        public BTWait(int waitFrames)
        {
            WaitFrames = waitFrames;
        }

        protected override void OnEnter(BTContext ctx)
        {
            _elapsed = 0;
        }

        protected override BTStatus OnTick(BTContext ctx)
        {
            _elapsed++;
            return _elapsed >= WaitFrames ? BTStatus.Success : BTStatus.Running;
        }

        public override void Reset()
        {
            base.Reset();
            _elapsed = 0;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  BTWaitUntil（等待条件满足）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 每帧检查条件，条件满足时返回 Success，否则返回 Running。
    /// </summary>
    public class BTWaitUntil : BTNode
    {
        readonly Func<BTContext, bool> _condition;

        public BTWaitUntil(Func<BTContext, bool> condition)
        {
            _condition = condition;
        }

        protected override BTStatus OnTick(BTContext ctx)
        {
            return _condition(ctx) ? BTStatus.Success : BTStatus.Running;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  BTSetValue（设置黑板值）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 向黑板写入一个值，然后返回 Success。单帧完成。
    /// </summary>
    public class BTSetValue<T> : BTNode
    {
        readonly string _key;
        readonly Func<BTContext, T> _valueGetter;

        public BTSetValue(string key, Func<BTContext, T> valueGetter)
        {
            _key = key;
            _valueGetter = valueGetter;
        }

        public BTSetValue(string key, T value)
        {
            _key = key;
            _valueGetter = _ => value;
        }

        protected override BTStatus OnTick(BTContext ctx)
        {
            ctx.Blackboard.Set(_key, _valueGetter(ctx));
            return BTStatus.Success;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  BTCheckValue（检查黑板值）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 检查黑板中某个值是否满足条件。
    /// </summary>
    public class BTCheckValue<T> : BTNode
    {
        readonly string _key;
        readonly Func<T, bool> _predicate;

        public BTCheckValue(string key, Func<T, bool> predicate)
        {
            _key = key;
            _predicate = predicate;
        }

        protected override BTStatus OnTick(BTContext ctx)
        {
            var value = ctx.Blackboard.Get<T>(_key);
            return _predicate(value) ? BTStatus.Success : BTStatus.Failure;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  BTLog（调试日志）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 输出调试信息并返回 Success。
    /// 使用可注入的 logAction 而非 UnityEngine.Debug 以保持纯逻辑。
    /// </summary>
    public class BTLog : BTNode
    {
        readonly string _message;
        readonly Action<string> _logAction;

        /// <param name="message">要输出的消息。</param>
        /// <param name="logAction">日志输出委托（默认为 Console.WriteLine）。</param>
        public BTLog(string message, Action<string> logAction = null)
        {
            _message = message;
            _logAction = logAction ?? Console.WriteLine;
        }

        protected override BTStatus OnTick(BTContext ctx)
        {
            _logAction($"[BT F{ctx.Frame}] {_message}");
            return BTStatus.Success;
        }
    }
}
