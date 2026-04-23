using System.Collections.Generic;

namespace FrameSync
{
    /// <summary>
    /// 闪电云请求（由BattleFighter创建，BattleLogic接收后生成LightningCloud）。
    /// </summary>
    public struct LightningCloudRequest
    {
        public byte SourceId;
        public byte TeamId;
        public FixedVector2 Position;    // 云的中心位置
        public FixedInt Radius;          // 伤害半径
        public FixedInt Damage;          // 每次闪电伤害
        public int TickInterval;         // 每隔多少帧造成一次伤害
        public int Lifetime;             // 总存活帧数
    }

    /// <summary>
    /// 闪电云 — 持久性AoE区域，在固定位置周期性对范围内敌人造成伤害。
    /// 不可移动，持续一段时间后消失。
    /// </summary>
    public class LightningCloud
    {
        public byte SourceId;
        public byte TeamId;
        public FixedVector2 Position;
        public FixedInt Radius;
        public FixedInt Damage;
        public int TickInterval;         // 每隔多少帧造成一次伤害
        public int FramesLeft;           // 剩余存活帧数
        public bool Done;

        int _tickCounter;                // 距下次伤害的帧计数
        List<BattleFighter> _allFighters;
        BattleFighter _sourceFighter;

        public void Init(LightningCloudRequest req, List<BattleFighter> allFighters,
                         BattleFighter sourceFighter)
        {
            SourceId = req.SourceId;
            TeamId = req.TeamId;
            Position = req.Position;
            Radius = req.Radius;
            Damage = req.Damage;
            TickInterval = req.TickInterval;
            FramesLeft = req.Lifetime;
            Done = false;
            _tickCounter = 0; // 首次立即造成伤害
            _allFighters = allFighters;
            _sourceFighter = sourceFighter;
        }

        /// <summary>每帧驱动。返回 true 表示云已消失，应从列表移除。</summary>
        public bool Tick(int frame, List<BattleEvent> events)
        {
            if (Done) return true;

            FramesLeft--;
            if (FramesLeft <= 0)
            {
                Done = true;
                return true;
            }

            _tickCounter--;
            if (_tickCounter > 0) return false;

            // 到达伤害间隔，对范围内所有敌人造成伤害
            _tickCounter = TickInterval;

            // AoE爆炸事件（用于显示层）
            events.Add(new BattleEvent
            {
                Frame    = frame,
                Type     = BattleEventType.AoEExplosion,
                SourceId = SourceId,
                PosXRaw  = Position.X.Raw,
                PosYRaw  = Position.Y.Raw,
                IntParam = Radius.ToInt(),
            });

            for (int i = 0; i < _allFighters.Count; i++)
            {
                var f = _allFighters[i];
                if (f.TeamId == TeamId || f.IsDead || f.IsStealthed) continue;

                var dist = FixedVector2.Distance(Position, f.Position);
                if (dist > Radius + f.Radius) continue;

                // 格挡判定
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

                // 攻击者被动
                if (_sourceFighter != null && !_sourceFighter.IsDead && !_sourceFighter.IsSummon)
                    _sourceFighter.OnDealDamage(finalDmg, f, frame, events);

                // 反伤护盾
                if (_sourceFighter != null)
                    f.TryReflectDamage(_sourceFighter, finalDmg, frame, events);

                // 打断判定
                if (!f.IsDead)
                    f.TryInterrupt();

                // 被击后触发反应式副技能 + 被动
                if (!f.IsDead)
                {
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

            return false;
        }
    }
}
