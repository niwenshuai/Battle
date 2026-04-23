using System.Collections.Generic;

namespace FrameSync
{
    /// <summary>
    /// 穿刺弹射物（闪电）— 纯逻辑层。
    /// 沿直线飞行，穿过路径上的所有敌人并对每个敌人造成一次伤害。
    /// 每个敌人只会被同一道穿刺弹射物命中一次。
    /// </summary>
    public class PiercingProjectile
    {
        public byte SourceId;
        public byte TeamId;
        public FixedVector2 Position;
        public FixedVector2 Direction;        // 单位方向（直线飞行，不追踪）
        public FixedInt Speed;
        public FixedInt Damage;
        public FixedInt MaxRange;             // 最大飞行距离
        public FixedInt HitRadius;            // 命中检测半径
        public BuffTemplate[] HitBuffs;       // 命中后施加的buff列表
        public bool Done;

        FixedVector2 _startPos;
        List<BattleFighter> _allFighters;
        BattleFighter _sourceFighter;
        readonly HashSet<byte> _hitIds = new(); // 已命中的角色ID，避免重复伤害

        public void Init(byte sourceId, byte teamId, FixedVector2 startPos, FixedVector2 direction,
                         FixedInt speed, FixedInt damage, FixedInt maxRange, FixedInt hitRadius,
                         BuffTemplate[] hitBuffs, List<BattleFighter> allFighters,
                         BattleFighter sourceFighter = null)
        {
            SourceId = sourceId;
            TeamId = teamId;
            Position = startPos;
            Direction = direction.SqrMagnitude > FixedInt.Zero ? direction.Normalized : FixedVector2.Right;
            Speed = speed;
            Damage = damage;
            MaxRange = maxRange;
            HitRadius = hitRadius;
            HitBuffs = hitBuffs;
            Done = false;
            _startPos = startPos;
            _allFighters = allFighters;
            _sourceFighter = sourceFighter;
            _hitIds.Clear();
        }

        /// <summary>每帧驱动。返回 true 表示飞行结束，应从列表移除。</summary>
        public bool Tick(FixedInt dt, int frame, List<BattleEvent> events)
        {
            if (Done) return true;

            var step = Speed * dt;
            Position = Position + Direction * step;

            // 检查是否飞出场地
            var arenaHalf = FixedInt.FromInt(CharacterConfig.ArenaHalf);
            if (Position.X < -arenaHalf || Position.X > arenaHalf ||
                Position.Y < -arenaHalf || Position.Y > arenaHalf)
            {
                Done = true;
                return true;
            }

            // 检查是否超过最大飞行距离
            var traveled = FixedVector2.Distance(Position, _startPos);
            if (traveled >= MaxRange)
            {
                Done = true;
                return true;
            }

            // 穿刺检测：对路径上未命中的敌人造成伤害
            for (int i = 0; i < _allFighters.Count; i++)
            {
                var f = _allFighters[i];
                if (f.TeamId == TeamId || f.IsDead || f.IsStealthed) continue;
                if (_hitIds.Contains(f.PlayerId)) continue;

                var dist = FixedVector2.Distance(Position, f.Position);
                if (dist <= HitRadius + f.Radius)
                {
                    _hitIds.Add(f.PlayerId);
                    HitTarget(f, frame, events);
                }
            }

            return false;
        }

        void HitTarget(BattleFighter target, int frame, List<BattleEvent> events)
        {
            // 格挡判定
            if (target.TryDodgeBlock())
            {
                if (!target.IsDead)
                {
                    bool reacted = target.TryReactSkill2(Position, SourceId, frame, events);
                    if (!reacted)
                        target.TryPassive(Position, SourceId, frame, events);
                }
                return;
            }

            // 造成伤害（含抗性减伤）
            var finalDmg = target.ApplyResistance(Damage);
            target.Hp = target.Hp - finalDmg;
            if (target.Hp < FixedInt.Zero)
                target.Hp = FixedInt.Zero;

            events.Add(new BattleEvent
            {
                Frame    = frame,
                Type     = BattleEventType.Damage,
                SourceId = SourceId,
                TargetId = target.PlayerId,
                IntParam = finalDmg.ToInt(),
            });

            events.Add(new BattleEvent
            {
                Frame    = frame,
                Type     = BattleEventType.HpChanged,
                SourceId = target.PlayerId,
                IntParam = target.Hp.ToInt(),
            });

            // 攻击者被动
            if (_sourceFighter != null && !_sourceFighter.IsSummon)
                _sourceFighter.OnDealDamage(finalDmg, target, frame, events);

            // 打断判定
            if (!target.IsDead)
                target.TryInterrupt();

            // 应用buff
            if (HitBuffs != null && !target.IsDead)
            {
                for (int b = 0; b < HitBuffs.Length; b++)
                {
                    var bt = HitBuffs[b];
                    target.AddBuff(new Buff
                    {
                        Type       = bt.Type,
                        FramesLeft = bt.Duration,
                        Value      = bt.Value,
                        SourceId   = SourceId,
                        IsDebuff   = bt.IsDebuff,
                    });
                    events.Add(new BattleEvent
                    {
                        Frame    = frame,
                        Type     = BattleEventType.BuffApplied,
                        SourceId = SourceId,
                        TargetId = target.PlayerId,
                        IntParam = ((int)bt.Type << 16) | bt.Duration,
                    });
                }
            }

            // 被击后触发反应式副技能 + 被动
            if (!target.IsDead)
            {
                bool reacted = target.TryReactSkill2(Position, SourceId, frame, events);
                if (!reacted)
                    target.TryPassive(Position, SourceId, frame, events);
            }

            if (target.IsDead)
            {
                events.Add(new BattleEvent
                {
                    Frame    = frame,
                    Type     = BattleEventType.Death,
                    SourceId = target.PlayerId,
                });
            }
        }
    }
}
