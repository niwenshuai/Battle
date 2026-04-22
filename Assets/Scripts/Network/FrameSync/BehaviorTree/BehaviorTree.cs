namespace FrameSync
{
    /// <summary>
    /// 行为树运行器 — 管理根节点、上下文和每帧 Tick。
    ///
    /// 用法：
    /// <code>
    /// var root = BT.Selector()
    ///     .Sequence()
    ///         .Condition(ctx => ctx.Blackboard.GetInt("hp") > 0)
    ///         .Action(ctx => Attack(ctx))
    ///     .End()
    ///     .Action(ctx => Flee(ctx))
    ///     .Build();
    ///
    /// var bt = new BehaviorTree(root, entityId: 1, randomSeed: 42);
    ///
    /// // 在帧同步逻辑帧中调用
    /// bt.Tick(frameNumber, tickInterval);
    /// </code>
    /// </summary>
    public class BehaviorTree
    {
        public BTNode Root { get; private set; }
        public BTContext Context { get; private set; }

        /// <summary>上一次 Tick 的结果。</summary>
        public BTStatus LastStatus { get; private set; }

        /// <summary>树是否暂停。暂停时 Tick 直接返回上次状态。</summary>
        public bool Paused { get; set; }

        public BehaviorTree(BTNode root, int entityId = 0, uint randomSeed = 1)
        {
            Root = root;
            Context = new BTContext
            {
                Blackboard = new BTBlackboard(),
                Frame = 0,
                DeltaTime = FixedInt.Zero,
                Random = new BTRandom(randomSeed),
                EntityId = entityId
            };
            LastStatus = BTStatus.Failure;
        }

        /// <summary>
        /// 驱动行为树执行一帧。
        /// </summary>
        /// <param name="frame">当前逻辑帧号。</param>
        /// <param name="deltaTime">帧时间步长（定点数）。</param>
        /// <returns>根节点的执行状态。</returns>
        public BTStatus Tick(int frame, FixedInt deltaTime)
        {
            if (Paused)
                return LastStatus;

            Context.Frame = frame;
            Context.DeltaTime = deltaTime;

            LastStatus = Root.Tick(Context);

            // 根节点完成后自动重置，下帧重新从头开始
            if (LastStatus != BTStatus.Running)
                Root.Reset();

            return LastStatus;
        }

        /// <summary>重置整棵树和黑板。</summary>
        public void Reset()
        {
            Root.Reset();
            Context.Blackboard.Clear();
            LastStatus = BTStatus.Failure;
        }

        /// <summary>仅重置树节点状态，保留黑板数据。</summary>
        public void ResetTree()
        {
            Root.Reset();
            LastStatus = BTStatus.Failure;
        }
    }
}
