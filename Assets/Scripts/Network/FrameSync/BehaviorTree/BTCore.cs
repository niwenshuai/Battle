using System.Collections.Generic;

namespace FrameSync
{
    // ═══════════════════════════════════════════════════════════════
    //  行为树节点状态
    // ═══════════════════════════════════════════════════════════════

    public enum BTStatus
    {
        Success,
        Failure,
        Running
    }

    // ═══════════════════════════════════════════════════════════════
    //  确定性伪随机数生成器（LCG）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 线性同余随机数生成器，所有客户端给定相同 seed 产生相同序列。
    /// </summary>
    public struct BTRandom
    {
        uint _state;

        public BTRandom(uint seed)
        {
            _state = seed == 0 ? 1u : seed;
        }

        /// <summary>返回 [0, 2^31) 范围的非负整数。</summary>
        public int Next()
        {
            _state = _state * 1103515245u + 12345u;
            return (int)((_state >> 1) & 0x7FFFFFFF);
        }

        /// <summary>返回 [0, max) 范围的整数。</summary>
        public int Next(int max)
        {
            if (max <= 0) return 0;
            return Next() % max;
        }

        /// <summary>返回 [min, max) 范围的整数。</summary>
        public int Range(int min, int max)
        {
            if (max <= min) return min;
            return min + Next(max - min);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  黑板 — 行为树节点间的共享数据
    // ═══════════════════════════════════════════════════════════════

    public class BTBlackboard
    {
        readonly Dictionary<string, object> _data = new();

        public void Set<T>(string key, T value)
        {
            _data[key] = value;
        }

        public T Get<T>(string key)
        {
            if (_data.TryGetValue(key, out var v))
                return (T)v;
            return default;
        }

        public T Get<T>(string key, T defaultValue)
        {
            if (_data.TryGetValue(key, out var v))
                return (T)v;
            return defaultValue;
        }

        public bool Has(string key) => _data.ContainsKey(key);

        public bool Remove(string key) => _data.Remove(key);

        public void Clear() => _data.Clear();

        // ── 常用定点数快捷方法 ──────────────────────────────

        public void SetInt(string key, int value) => _data[key] = value;
        public int GetInt(string key, int def = 0) => Has(key) ? (int)_data[key] : def;

        public void SetFixed(string key, FixedInt value) => _data[key] = value;
        public FixedInt GetFixed(string key) => Has(key) ? (FixedInt)_data[key] : FixedInt.Zero;

        public void SetVector(string key, FixedVector2 value) => _data[key] = value;
        public FixedVector2 GetVector(string key) => Has(key) ? (FixedVector2)_data[key] : FixedVector2.Zero;

        public void SetBool(string key, bool value) => _data[key] = value;
        public bool GetBool(string key, bool def = false) => Has(key) ? (bool)_data[key] : def;
    }

    // ═══════════════════════════════════════════════════════════════
    //  执行上下文 — 每次 Tick 传递给节点
    // ═══════════════════════════════════════════════════════════════

    public class BTContext
    {
        /// <summary>节点间共享数据。</summary>
        public BTBlackboard Blackboard;

        /// <summary>当前逻辑帧号。</summary>
        public int Frame;

        /// <summary>每帧固定时间步长（定点数）。</summary>
        public FixedInt DeltaTime;

        /// <summary>确定性随机数。</summary>
        public BTRandom Random;

        /// <summary>实体 / 代理自身的 ID（可用于区分不同 AI）。</summary>
        public int EntityId;
    }

    // ═══════════════════════════════════════════════════════════════
    //  节点基类
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 行为树节点基类。
    /// 生命周期：OnEnter → OnTick(每帧) → OnExit
    /// 当 OnTick 返回 Running 时，下一帧继续调用 OnTick；
    /// 返回 Success/Failure 时调用 OnExit，然后节点等待下次进入。
    /// </summary>
    public abstract class BTNode
    {
        public BTStatus Status { get; protected set; } = BTStatus.Failure;

        bool _entered;

        /// <summary>外部调用入口：驱动节点的完整生命周期。</summary>
        public BTStatus Tick(BTContext ctx)
        {
            if (!_entered)
            {
                OnEnter(ctx);
                _entered = true;
            }

            Status = OnTick(ctx);

            if (Status != BTStatus.Running)
            {
                OnExit(ctx);
                _entered = false;
            }

            return Status;
        }

        /// <summary>节点首次被激活时调用（Running 状态期间不重复调用）。</summary>
        protected virtual void OnEnter(BTContext ctx) { }

        /// <summary>每帧核心逻辑，子类必须实现。</summary>
        protected abstract BTStatus OnTick(BTContext ctx);

        /// <summary>节点完成（Success/Failure）时调用。</summary>
        protected virtual void OnExit(BTContext ctx) { }

        /// <summary>重置节点状态，用于行为树重启。</summary>
        public virtual void Reset()
        {
            Status = BTStatus.Failure;
            _entered = false;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  组合节点基类
    // ═══════════════════════════════════════════════════════════════

    /// <summary>拥有多个子节点的组合节点基类。</summary>
    public abstract class BTComposite : BTNode
    {
        public readonly List<BTNode> Children = new();

        public BTComposite AddChild(BTNode child)
        {
            Children.Add(child);
            return this;
        }

        public override void Reset()
        {
            base.Reset();
            for (int i = 0; i < Children.Count; i++)
                Children[i].Reset();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  装饰节点基类
    // ═══════════════════════════════════════════════════════════════

    /// <summary>包装单个子节点的装饰节点基类。</summary>
    public abstract class BTDecorator : BTNode
    {
        public BTNode Child { get; set; }

        public override void Reset()
        {
            base.Reset();
            Child?.Reset();
        }
    }
}
