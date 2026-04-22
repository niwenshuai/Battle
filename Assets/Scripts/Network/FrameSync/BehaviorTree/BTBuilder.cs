using System;
using System.Collections.Generic;

namespace FrameSync
{
    /// <summary>
    /// 行为树流式构建器 — 使用链式调用构造行为树。
    ///
    /// 用法示例：
    /// <code>
    /// var root = BT.Selector()
    ///     .Sequence()                          // 攻击分支
    ///         .Condition(ctx => IsEnemyInRange(ctx))
    ///         .Action(ctx => Attack(ctx))
    ///     .End()
    ///     .Sequence()                          // 追逐分支
    ///         .Condition(ctx => CanSeeEnemy(ctx))
    ///         .Action(ctx => ChaseEnemy(ctx))
    ///     .End()
    ///     .Action(ctx => Patrol(ctx))           // 巡逻
    ///     .Build();
    /// </code>
    /// </summary>
    public class BTBuilder
    {
        readonly Stack<BTComposite> _stack = new();
        BTComposite _current;

        internal BTBuilder(BTComposite root)
        {
            _current = root;
        }

        // ── 静态入口 ─────────────────────────────────────────

        BTBuilder Push(BTComposite node)
        {
            _current.AddChild(node);
            _stack.Push(_current);
            _current = node;
            return this;
        }

        BTBuilder AddLeaf(BTNode node)
        {
            _current.AddChild(node);
            return this;
        }

        BTBuilder AddDecorated(BTDecorator dec, BTNode child)
        {
            dec.Child = child;
            _current.AddChild(dec);
            return this;
        }

        // ── 组合节点（开启子树） ─────────────────────────────

        /// <summary>嵌套 Sequence。</summary>
        public BTBuilder Sequence()
            => Push(new BTSequence());

        /// <summary>嵌套 Selector。</summary>
        public BTBuilder Selector()
            => Push(new BTSelector());

        /// <summary>嵌套 Parallel。</summary>
        public BTBuilder Parallel(BTParallelPolicy policy = BTParallelPolicy.RequireAll)
            => Push(new BTParallel(policy));

        /// <summary>嵌套 RandomSelector。</summary>
        public BTBuilder RandomSelector()
            => Push(new BTRandomSelector());

        /// <summary>嵌套 RandomSequence。</summary>
        public BTBuilder RandomSequence()
            => Push(new BTRandomSequence());

        /// <summary>关闭当前子树，返回到父节点。</summary>
        public BTBuilder End()
        {
            if (_stack.Count == 0)
                throw new InvalidOperationException("BTBuilder: End() 调用次数超过了嵌套深度");
            _current = _stack.Pop();
            return this;
        }

        // ── 叶节点 ──────────────────────────────────────────

        /// <summary>添加动作节点（可返回 Running）。</summary>
        public BTBuilder Action(Func<BTContext, BTStatus> tick,
                                Action<BTContext> enter = null,
                                Action<BTContext> exit = null)
            => AddLeaf(new BTAction(tick, enter, exit));

        /// <summary>添加单帧动作。</summary>
        public BTBuilder SimpleAction(Action<BTContext> action)
            => AddLeaf(new BTSimpleAction(action));

        /// <summary>添加条件检查节点。</summary>
        public BTBuilder Condition(Func<BTContext, bool> condition)
            => AddLeaf(new BTCondition(condition));

        /// <summary>等待指定帧数。</summary>
        public BTBuilder Wait(int frames)
            => AddLeaf(new BTWait(frames));

        /// <summary>等待条件满足。</summary>
        public BTBuilder WaitUntil(Func<BTContext, bool> condition)
            => AddLeaf(new BTWaitUntil(condition));

        /// <summary>向黑板写入值。</summary>
        public BTBuilder SetValue<T>(string key, Func<BTContext, T> valueGetter)
            => AddLeaf(new BTSetValue<T>(key, valueGetter));

        /// <summary>向黑板写入常量值。</summary>
        public BTBuilder SetValue<T>(string key, T value)
            => AddLeaf(new BTSetValue<T>(key, value));

        /// <summary>检查黑板中的值。</summary>
        public BTBuilder CheckValue<T>(string key, Func<T, bool> predicate)
            => AddLeaf(new BTCheckValue<T>(key, predicate));

        /// <summary>调试日志。</summary>
        public BTBuilder Log(string message, Action<string> logAction = null)
            => AddLeaf(new BTLog(message, logAction));

        /// <summary>添加任意自定义节点。</summary>
        public BTBuilder Node(BTNode node)
            => AddLeaf(node);

        // ── 装饰器（包装下一个叶节点或子树） ────────────────

        /// <summary>
        /// 以装饰器包装接下来的一个叶节点。
        /// 例: .Inverter().Condition(...)  → Inverter 包裹 Condition
        /// 对于包装子树，使用带 lambda 的重载：
        /// .Inverter(b => b.Sequence().Action(...).End())
        /// </summary>
        public BTBuilder Inverter(Action<BTBuilder> childBuilder)
            => DecoratorSubtree(new BTInverter(), childBuilder);

        public BTBuilder AlwaysSucceed(Action<BTBuilder> childBuilder)
            => DecoratorSubtree(new BTAlwaysSucceed(), childBuilder);

        public BTBuilder AlwaysFail(Action<BTBuilder> childBuilder)
            => DecoratorSubtree(new BTAlwaysFail(), childBuilder);

        public BTBuilder Repeat(int count, Action<BTBuilder> childBuilder)
            => DecoratorSubtree(new BTRepeater(count), childBuilder);

        public BTBuilder RepeatForever(Action<BTBuilder> childBuilder)
            => DecoratorSubtree(new BTRepeater(-1), childBuilder);

        public BTBuilder UntilFail(Action<BTBuilder> childBuilder)
            => DecoratorSubtree(new BTUntilFail(), childBuilder);

        public BTBuilder UntilSuccess(Action<BTBuilder> childBuilder)
            => DecoratorSubtree(new BTUntilSuccess(), childBuilder);

        public BTBuilder Cooldown(int frames, Action<BTBuilder> childBuilder)
            => DecoratorSubtree(new BTCooldown(frames), childBuilder);

        public BTBuilder Guard(Func<BTContext, bool> condition, Action<BTBuilder> childBuilder)
            => DecoratorSubtree(new BTGuard(condition), childBuilder);

        public BTBuilder TimeLimit(int frames, Action<BTBuilder> childBuilder)
            => DecoratorSubtree(new BTTimeLimit(frames), childBuilder);

        /// <summary>用自定义装饰器包装子树。</summary>
        public BTBuilder Decorator(BTDecorator decorator, Action<BTBuilder> childBuilder)
            => DecoratorSubtree(decorator, childBuilder);

        BTBuilder DecoratorSubtree(BTDecorator dec, Action<BTBuilder> childBuilder)
        {
            // 创建临时子构建器来收集子节点
            var wrapper = new BTSequence(); // 临时容器
            var sub = new BTBuilder(wrapper);
            childBuilder(sub);

            if (wrapper.Children.Count == 1)
                dec.Child = wrapper.Children[0];
            else
            {
                // 多个子节点时，用 Sequence 包裹
                dec.Child = wrapper;
            }

            _current.AddChild(dec);
            return this;
        }

        // ── 构建 ────────────────────────────────────────────

        /// <summary>完成构建，返回根节点。</summary>
        public BTNode Build()
        {
            if (_stack.Count > 0)
                throw new InvalidOperationException(
                    $"BTBuilder: 还有 {_stack.Count} 个未关闭的子树，请检查 End() 调用");
            return _current;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  BT — 静态入口
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 行为树构建的静态入口。
    ///
    /// <code>
    /// var tree = BT.Selector()
    ///     .Sequence()
    ///         .Condition(ctx => ctx.Blackboard.GetFixed("hp") > FixedInt.Zero)
    ///         .Action(ctx => DoAttack(ctx))
    ///     .End()
    ///     .Action(ctx => DoFlee(ctx))
    ///     .Build();
    /// </code>
    /// </summary>
    public static class BT
    {
        public static BTBuilder Sequence() => new(new BTSequence());
        public static BTBuilder Selector() => new(new BTSelector());
        public static BTBuilder Parallel(BTParallelPolicy policy = BTParallelPolicy.RequireAll)
            => new(new BTParallel(policy));
        public static BTBuilder RandomSelector() => new(new BTRandomSelector());
        public static BTBuilder RandomSequence() => new(new BTRandomSequence());
    }
}
