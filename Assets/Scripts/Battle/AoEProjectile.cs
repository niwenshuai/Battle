using System.Collections.Generic;

namespace FrameSync
{
    /// <summary>
    /// 直线飞行的AoE弹射物（火球/冰球）。
    /// 直线飞行，碰到敌人包围盒后爆炸或到达最大飞行距离后爆炸。
    /// 爆炸造成范围伤害，可附带减速效果。
    /// </summary>
    public class AoEProjectile
    {
        public byte SourceId;
        public byte TeamId;
        public FixedVector2 Position;
        public FixedVector2 Direction;        // 单位方向（直线飞行，不追踪）
        public FixedInt Speed;
        public FixedInt Damage;
        public FixedInt MaxRange;             // 最大飞行距离
        public FixedInt ExplosionRadius;      // 爆炸半径
        public FixedInt ProjectileRadius;     // 弹射物自身半径（碰撞检测用）
        public BuffTemplate[] HitBuffs;      // 命中后施加的buff列表（减速等）
        public bool Done;

        FixedVector2 _startPos;
        List<BattleFighter> _allFighters;
        BattleFighter _sourceFighter; // 发射者引用（用于触发攻击者被动）

        public void Init(byte sourceId, byte teamId, FixedVector2 startPos, FixedVector2 direction,
                         FixedInt speed, FixedInt damage, FixedInt maxRange,
                         FixedInt explosionRadius, FixedInt projectileRadius,
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
            ExplosionRadius = explosionRadius;
            ProjectileRadius = projectileRadius;
            HitBuffs = hitBuffs;
            Done = false;
            _startPos = startPos;
            _allFighters = allFighters;
            _sourceFighter = sourceFighter;
        }

        /// <summary>每帧驱动。返回 true 表示已爆炸，应从列表移除。</summary>
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
                Explode(frame, events);
                return true;
            }

            // 检查是否超过最大飞行距离
            var traveled = FixedVector2.Distance(Position, _startPos);
            if (traveled >= MaxRange)
            {
                Explode(frame, events);
                return true;
            }

            // 检查是否碰到敌人包围盒（圆形碰撞）
            for (int i = 0; i < _allFighters.Count; i++)
            {
                var f = _allFighters[i];
                if (f.TeamId == TeamId || f.IsDead || f.IsStealthed) continue;
                var dist = FixedVector2.Distance(Position, f.Position);
                if (dist <= ProjectileRadius + f.Radius)
                {
                    Explode(frame, events);
                    return true;
                }
            }

            return false;
        }

        void Explode(int frame, List<BattleEvent> events)
        {
            Done = true;

            // 爆炸事件（用于显示层）
            events.Add(new BattleEvent
            {
                Frame    = frame,
                Type     = BattleEventType.AoEExplosion,
                SourceId = SourceId,
                PosXRaw  = Position.X.Raw,
                PosYRaw  = Position.Y.Raw,
                IntParam = ExplosionRadius.ToInt(),
            });

            // 对爆炸范围内所有敌人造成伤害
            for (int i = 0; i < _allFighters.Count; i++)
            {
                var f = _allFighters[i];
                if (f.TeamId == TeamId || f.IsDead) continue;
                var dist = FixedVector2.Distance(Position, f.Position);
                if (dist > ExplosionRadius + f.Radius) continue;

                // 被动：格挡 — 目标有概率完全免疫伤害
                if (f.TryDodgeBlock()) continue;

                // 造成伤害（含抗性减伤）
                var finalDmg = f.ApplyResistance(Damage);
                f.Hp = f.Hp - finalDmg;
                if (f.Hp < FixedInt.Zero) f.Hp = FixedInt.Zero;

                events.Add(new BattleEvent
                {
                    Frame    = frame,
                    Type     = BattleEventType.Damage,
                    SourceId = SourceId,
                    TargetId = f.PlayerId,
                    IntParam = finalDmg.ToInt(),
                });

                events.Add(new BattleEvent
                {
                    Frame    = frame,
                    Type     = BattleEventType.HpChanged,
                    SourceId = f.PlayerId,
                    IntParam = f.Hp.ToInt(),
                });

                // 攻击者被动触发（吸血/大招CD减少等）— 召唤物不触发主人被动
                if (_sourceFighter != null && !_sourceFighter.IsSummon)
                    _sourceFighter.OnDealDamage(finalDmg, f, frame, events);

                // 反伤护盾
                if (_sourceFighter != null)
                    f.TryReflectDamage(_sourceFighter, finalDmg, frame, events);

                // 打断判定：普攻前摇或移动中被伤害可能触发僵直
                if (!f.IsDead)
                    f.TryInterrupt();

                // 应用配置的buff（减速、眩晕等）
                if (HitBuffs != null && !f.IsDead)
                {
                    for (int b = 0; b < HitBuffs.Length; b++)
                    {
                        var bt = HitBuffs[b];
                        f.AddBuff(new Buff
                        {
                            Type       = bt.Type,
                            FramesLeft = bt.Duration,
                            Value      = bt.Value,
                            SourceId   = SourceId,
                        });

                        events.Add(new BattleEvent
                        {
                            Frame    = frame,
                            Type     = BattleEventType.BuffApplied,
                            SourceId = SourceId,
                            TargetId = f.PlayerId,
                            IntParam = ((int)bt.Type << 16) | bt.Duration,
                        });
                    }
                }

                if (!f.IsDead)
                {
                    // 被击后触发反应式副技能（ReactBlink等）+ 被动技能（FleeOnHit等）
                    bool reacted = f.TryReactSkill2(Position, SourceId, frame, events);
                    if (!reacted)
                        f.TryPassive(Position, SourceId, frame, events);
                }

                if (f.IsDead)
                {
                    events.Add(new BattleEvent
                    {
                        Frame    = frame,
                        Type     = BattleEventType.Death,
                        SourceId = f.PlayerId,
                    });
                }
            }
        }
    }
}
