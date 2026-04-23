using System.Collections.Generic;

namespace FrameSync
{
    /// <summary>
    /// 追踪弹射物（弓箭）— 纯逻辑层。
    /// 以固定速度飞向目标，到达后造成伤害。目标移动时弹射物自动追踪。
    /// 可附带击退效果（击退箭）。
    /// </summary>
    public class Projectile
    {
        public byte SourceId;
        public byte TargetId;
        public FixedVector2 Position;
        public FixedInt Speed;
        public FixedInt Damage;
        public bool Hit;

        /// <summary>是否为副技能弹射物（击退箭等）。</summary>
        public bool IsSkill2;
        /// <summary>击退距离，大于0时命中后将目标沿飞行方向推开。</summary>
        public FixedInt KnockbackDist;
        /// <summary>击退方向起点（发射者位置，用于计算推开方向）。</summary>
        FixedVector2 _sourcePos;

        BattleFighter _target;
        BattleFighter _sourceFighter; // 发射者引用（用于触发攻击者被动）
        BuffTemplate[] _hitBuffs;     // 命中后附加的buff列表

        public void Init(byte sourceId, BattleFighter target, FixedVector2 startPos,
                         FixedInt speed, FixedInt damage,
                         bool isSkill2 = false, FixedInt knockbackDist = default,
                         BattleFighter sourceFighter = null,
                         BuffTemplate[] hitBuffs = null)
        {
            SourceId = sourceId;
            TargetId = target.PlayerId;
            _target  = target;
            _sourceFighter = sourceFighter;
            Position = startPos;
            Speed    = speed;
            Damage   = damage;
            Hit      = false;
            IsSkill2 = isSkill2;
            KnockbackDist = knockbackDist;
            _sourcePos = startPos;
            _hitBuffs = hitBuffs;
        }

        /// <summary>每帧驱动。返回 true 表示命中，应从列表移除。</summary>
        public bool Tick(FixedInt dt, int frame, List<BattleEvent> events)
        {
            if (Hit) return true;

            var dir  = _target.Position - Position;
            var dist = dir.Magnitude;
            var step = Speed * dt;

            if (dist <= step)
            {
                // 命中
                Position = _target.Position;
                Hit = true;

                // 隐身目标免疫伤害，弹射物消失
                if (_target.IsStealthed)
                    return true;

                // 被动：格挡 — 目标有概率完全免疫伤害
                if (_target.TryDodgeBlock())
                {
                    // 格挡成功，不造成伤害
                    if (!IsSkill2 && !_target.IsDead)
                    {
                        bool reacted = _target.TryReactSkill2(_sourcePos, SourceId, frame, events);
                        if (!reacted)
                            _target.TryPassive(_sourcePos, SourceId, frame, events);
                    }
                    return true;
                }

                // 造成伤害（含抗性减伤）
                var finalDmg = _target.ApplyResistance(Damage);
                _target.Hp = _target.Hp - finalDmg;
                if (_target.Hp < FixedInt.Zero)
                    _target.Hp = FixedInt.Zero;

                events.Add(new BattleEvent
                {
                    Frame    = frame,
                    Type     = BattleEventType.ProjectileHit,
                    SourceId = SourceId,
                    TargetId = TargetId,
                    IntParam = finalDmg.ToInt(),
                });

                events.Add(new BattleEvent
                {
                    Frame    = frame,
                    Type     = BattleEventType.Damage,
                    SourceId = SourceId,
                    TargetId = TargetId,
                    IntParam = finalDmg.ToInt(),
                });

                events.Add(new BattleEvent
                {
                    Frame    = frame,
                    Type     = BattleEventType.HpChanged,
                    SourceId = TargetId,
                    IntParam = _target.Hp.ToInt(),
                });

                // 攻击者被动触发（吸血/大招CD减少/命中debuff等）
                if (_sourceFighter != null)
                    _sourceFighter.OnDealDamage(finalDmg, _target, frame, events);

                // 应用弹射物附加buff（普攻减速等）
                if (_hitBuffs != null && !_target.IsDead)
                {
                    for (int b = 0; b < _hitBuffs.Length; b++)
                    {
                        var bt = _hitBuffs[b];
                        _target.AddBuff(new Buff
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
                            TargetId = TargetId,
                            IntParam = ((int)bt.Type << 16) | bt.Duration,
                        });
                    }
                }

                // 打断判定：普攻前摇或移动中被伤害可能触发僵直
                if (!_target.IsDead)
                    _target.TryInterrupt();

                // 被攻击后触发反应式副技能 + 被动技能（击退箭不触发）
                if (!IsSkill2 && !_target.IsDead)
                {
                    bool reacted = _target.TryReactSkill2(_sourcePos, SourceId, frame, events);
                    if (!reacted)
                        _target.TryPassive(_sourcePos, SourceId, frame, events);
                }

                // 击退效果
                if (KnockbackDist > FixedInt.Zero && !_target.IsDead)
                {
                    var pushDir = _target.Position - _sourcePos;
                    if (pushDir.SqrMagnitude > FixedInt.Zero)
                    {
                        _target.Position = _target.Position + pushDir.Normalized * KnockbackDist;
                        _target.ClampToArena();
                    }
                }

                if (_target.IsDead)
                {
                    events.Add(new BattleEvent
                    {
                        Frame    = frame,
                        Type     = BattleEventType.Death,
                        SourceId = TargetId,
                    });
                }

                return true;
            }

            // 追踪飞行
            Position = Position + dir.Normalized * step;
            return false;
        }
    }
}
