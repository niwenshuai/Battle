using System.Collections.Generic;

namespace FrameSync
{
    /// <summary>
    /// 战斗角色 — 纯逻辑层，管理角色属性、技能冷却和行为树。
    /// 行为树逻辑由 Resources/BehaviorTreeConfig.json 驱动。
    ///
    /// 核心规则：
    ///   - 移动中不能攻击，只有停下来才能普攻/大招
    ///   - 弓手被攻击后逃跑固定位移(FleeDistance)再停下攻击
    ///   - 弓手跑得快(5)，剑士跑得慢(3)
    /// </summary>
    public class BattleFighter
    {
        // ── 基础属性 ─────────────────────────────────────────

        public byte           PlayerId;   // 唯一战斗单位ID（1-based）
        public byte           TeamId;     // 所属队伍（1或2）
        public CharacterType  CharType;
        public FixedVector2   Position;
        public FixedVector2   Facing;    // 单位朝向向量
        public FixedInt       Hp;
        public FixedInt       MaxHp;
        public FixedInt       MoveSpeed;
        public FixedInt       BaseMoveSpeed; // 不受buff影响的基础移速
        public FixedInt       TurnSpeed; // 弧度/秒
        public FixedInt       Radius;    // 碰撞半径

        // ── 普通攻击（由技能配置填充）─────────────────────────

        public FixedInt AtkRange;
        public FixedInt AtkDamage;
        public int      AtkCooldown;      // 冷却总帧数
        public int      AtkCooldownLeft;  // 剩余冷却帧数

        // ── 大招（由技能配置填充）─────────────────────────────

        public FixedInt UltDamage;
        public int      UltCooldown;
        public int      UltCooldownLeft;

        // ── 运行时状态 ───────────────────────────────────────

        public bool IsDead => Hp <= FixedInt.Zero;
        public bool IsMoving;
        public bool IsFleeing;
        public bool IsCasting;
        public bool UltRequested;
        public bool IsStunned;
        public bool IsStealthed;

        // ── 内部引用 ─────────────────────────────────────────

        BehaviorTree     _bt;
        BattleFighter    _target;        // 当前目标（每帧动态选择最近敌人）
        List<BattleFighter> _allFighters; // 全部战斗单位引用
        List<BattleEvent> _events;

        internal FixedInt _fleeDistance;
        internal FixedVector2 _fleeStartPos;
        int _prevStateBits;

        // ── 被动技能 ────────────────────────────────────────

        string _passiveType;          // 被动技能类型（FleeOnHit/CritStrike/DodgeBlock/UltCDReduce/Lifesteal）
        FixedInt _passiveParam1;      // 被动参数1
        int _passiveParam2;           // 被动参数2（整数，概率/倍率/帧数等）
        int _prevAtkCD;
        int _prevUltCD;
        bool _cdTotalsEmitted;

        // ── 施法状态（前摇/后摇）──────────────────────────────

        int _castFramesLeft;
        bool _castIsRecovery;
        bool _castIsUlt;
        int _atkWindup, _atkRecovery;
        int _ultWindup, _ultRecovery;

        // ── 技能配置引用（从 SkillConfig 加载）──────────────────

        string _normalAtkType;    // 普攻技能类型
        string _skill2Type;       // 副技能类型
        string _ultType;          // 大招技能类型
        FixedInt _normalAtkProjectileSpeed; // 远程普攻弹射物速度
        int _normalAtkConeAngle;  // MeleeAttack锥形AoE角度（度），0=单体
        int _normalAtkParam2;     // AoE弹射物爆炸半径
        BuffTemplate[] _normalAtkBuffs; // 普攻附加的buff列表

        // ── 职业与索敌优先级 ────────────────────────────────

        Profession _profession;
        Profession[] _targetPriority; // 优先索敌职业列表

        // ── 副技能 ──────────────────────────────────────────

        FixedInt _skill2Damage;
        int _skill2Cooldown;
        public int Skill2CooldownLeft;
        FixedInt _skill2Param1;      // 类型相关参数1
        FixedInt _skill2Param2;      // 类型相关参数2
        BuffTemplate[] _skill2Buffs; // 副技能附加的buff列表
        bool _skill2TargetAlly;      // true=作用友方, false=作用敌方
        bool _skill2TargetAll;       // true=全体, false=单体
        bool _skill2TargetLowestHp;  // true=血量最低优先
        int _prevSkill2CD;
        FixedInt _ultParam1;         // 大招参数1
        int _ultParam2;              // 大招参数2（召唤物HP等）
        BuffTemplate[] _ultBuffs;    // 大招附加的buff列表
        bool _ultTargetAlly;         // true=作用友方, false=作用敌方
        bool _ultTargetAll;          // true=全体, false=单体
        int _stealthFramesLeft;

        /// <summary>本帧新生成的弹射物（由 BattleLogic 取走并管理）。</summary>
        public readonly List<Projectile> PendingProjectiles = new();

        /// <summary>本帧新生成的AoE弹射物（火球/冰球，由 BattleLogic 取走）。</summary>
        public readonly List<AoEProjectile> PendingAoEProjectiles = new();

        /// <summary>本帧请求创建的召唤物（由 BattleLogic 取走并创建）。</summary>
        public readonly List<SummonRequest> PendingSummons = new();

        // ── Buff系统 ──────────────────────────────────────────
        readonly List<Buff> _buffs = new();
        public bool IsSlowed;

        // ── 召唤物支持 ────────────────────────────────────────
        public BattleFighter Master;    // 非null表示这是召唤物
        public int LifetimeLeft;         // 召唤物剩余存活帧数（0=无限制）
        public bool IsSummon => Master != null;

        // ── 碰撞规避缓冲区（复用避免GC）────────────────────────
        static readonly FixedCircle[] _obstacleBuffer = new FixedCircle[64];

        public const int StateBit_Moving    = 1 << 0;
        public const int StateBit_Fleeing   = 1 << 1;
        public const int StateBit_Casting   = 1 << 2;
        public const int StateBit_CastUlt   = 1 << 3; // 仅 Casting 时有效
        public const int StateBit_Stunned   = 1 << 4;
        public const int StateBit_Stealthed = 1 << 5;
        public const int StateBit_Slowed    = 1 << 6;

        // ── 场地边界与朝向阈值 ────────────────────────────────
        static FixedInt ArenaHalf => FixedInt.FromInt(CharacterConfig.ArenaHalf);
        static readonly FixedInt FacingThreshold = FixedInt.FromRaw(3037000499L); // ~0.707 ≈ cos(45°)

        // ═══════════════════════════════════════════════════════
        //  初始化
        // ═══════════════════════════════════════════════════════

        /// <summary>根据角色类型初始化属性，技能从SkillConfig加载。</summary>
        public void Init(CharacterType type, byte playerId, byte teamId, FixedVector2 startPos)
        {
            CharType = type;
            PlayerId = playerId;
            TeamId   = teamId;
            Position = startPos;

            var cfg = CharacterConfig.Get(type);
            MaxHp        = FixedInt.FromInt(cfg.MaxHp);
            BaseMoveSpeed = FixedInt.FromInt(cfg.MoveSpeed);
            MoveSpeed    = BaseMoveSpeed;
            TurnSpeed    = FixedInt.FromInt(cfg.TurnSpeed);
            Radius       = cfg.CollisionRadius > 0 ? FixedInt.FromFloat(cfg.CollisionRadius) : FixedInt.OneVal;

            // ── 职业与索敌优先级 ──
            _profession = CharacterConfig.ParseProfession(cfg.Profession);
            _targetPriority = CharacterConfig.GetTargetPriority(_profession);

            // ── 被动技能 ──
            var passive = SkillConfigLoader.Get(cfg.Passive);
            if (passive != null)
            {
                _passiveType     = passive.Type;
                _passiveParam1   = FixedInt.FromInt(passive.Param1);
                _passiveParam2   = passive.Param2;
                _fleeDistance     = _passiveType == "FleeOnHit" ? _passiveParam1 : FixedInt.Zero;
            }

            // ── 普攻技能 ──
            var atkSkill = SkillConfigLoader.Get(cfg.NormalAttack);
            if (atkSkill != null)
            {
                _normalAtkType = atkSkill.Type;
                AtkDamage    = FixedInt.FromInt(atkSkill.Damage);
                AtkRange     = FixedInt.FromInt(atkSkill.Range);
                AtkCooldown  = atkSkill.Cooldown;
                _atkWindup   = atkSkill.Windup;
                _atkRecovery = atkSkill.Recovery;
                _normalAtkProjectileSpeed = FixedInt.FromInt(atkSkill.Param1);
                _normalAtkConeAngle = (_normalAtkType == "MeleeAttack") ? atkSkill.Param1 : 0;
                _normalAtkParam2 = atkSkill.Param2;
                _normalAtkBuffs = BuffTemplate.FromConfigs(atkSkill.Buffs);
            }

            // ── 副技能 ──
            var sk2 = SkillConfigLoader.Get(cfg.Skill2);
            if (sk2 != null)
            {
                _skill2Type     = sk2.Type;
                _skill2Damage   = FixedInt.FromInt(sk2.Damage);
                _skill2Cooldown = sk2.Cooldown;
                _skill2Param1   = FixedInt.FromInt(sk2.Param1);
                _skill2Param2   = FixedInt.FromInt(sk2.Param2);
                _skill2Buffs    = BuffTemplate.FromConfigs(sk2.Buffs);
                _skill2TargetAlly = sk2.TargetTeam == "Ally";
                _skill2TargetAll  = sk2.TargetScope == "All";
                _skill2TargetLowestHp = sk2.TargetScope == "LowestHp";
            }

            // ── 大招技能 ──
            var ult = SkillConfigLoader.Get(cfg.Ultimate);
            if (ult != null)
            {
                _ultType     = ult.Type;
                UltDamage    = FixedInt.FromInt(ult.Damage);
                UltCooldown  = ult.Cooldown;
                _ultWindup   = ult.Windup;
                _ultRecovery = ult.Recovery;
                _ultParam1   = FixedInt.FromInt(ult.Param1);
                _ultParam2   = ult.Param2;
                _ultBuffs    = BuffTemplate.FromConfigs(ult.Buffs);
                _ultTargetAlly = ult.TargetTeam == "Ally";
                _ultTargetAll  = ult.TargetScope == "All";
            }

            Hp = MaxHp;
            AtkCooldownLeft = 0;
            UltCooldownLeft = UltCooldown;
            Skill2CooldownLeft = _skill2Cooldown;
            IsMoving = false;
            IsFleeing = false;
            IsCasting = false;
            IsStunned = false;
            IsStealthed = false;
            _stealthFramesLeft = 0;
            Facing = teamId == 1 ? FixedVector2.Right : FixedVector2.Left;
        }

        /// <summary>Init 后调用，传入全部战斗单位列表并构建行为树。</summary>
        public void BuildBT(List<BattleFighter> allFighters, List<BattleEvent> events)
        {
            _allFighters = allFighters;
            _events = events;

            var config = BTConfigLoader.Get(CharType);
            BTNode root;
            if (config != null)
            {
                root = BuildBTFromConfig(config);
            }
            else
            {
                UnityEngine.Debug.LogError($"[BattleFighter] BT config not found for {CharType}, using empty tree");
                root = new BTCondition(_ => false);
            }

            _bt = new BehaviorTree(root, PlayerId, (uint)(PlayerId * 31 + 7));
        }

        /// <summary>该角色的职业。</summary>
        public Profession Prof => _profession;

        /// <summary>优先索敌：按职业优先级从高到低，同优先级选最近的。</summary>
        BattleFighter FindNearestEnemy()
        {
            if (_targetPriority == null || _targetPriority.Length == 0)
                return FindNearestEnemySimple();

            // 逐优先级寻找
            for (int p = 0; p < _targetPriority.Length; p++)
            {
                var wantedProf = _targetPriority[p];
                BattleFighter best = null;
                FixedInt bestDist = FixedInt.FromInt(99999);
                for (int i = 0; i < _allFighters.Count; i++)
                {
                    var f = _allFighters[i];
                    if (f.TeamId == TeamId || f.IsDead || f.IsStealthed) continue;
                    if (f.Prof != wantedProf) continue;
                    var d = FixedVector2.Distance(Position, f.Position);
                    if (d < bestDist) { bestDist = d; best = f; }
                }
                if (best != null) return best;
            }

            // 优先级列表未覆盖的职业 → 回退最近
            return FindNearestEnemySimple();
        }

        /// <summary>无优先级回退：选最近的存活敌人。</summary>
        BattleFighter FindNearestEnemySimple()
        {
            BattleFighter best = null;
            FixedInt bestDist = FixedInt.FromInt(99999);
            for (int i = 0; i < _allFighters.Count; i++)
            {
                var f = _allFighters[i];
                if (f.TeamId == TeamId || f.IsDead || f.IsStealthed) continue;
                var d = FixedVector2.Distance(Position, f.Position);
                if (d < bestDist) { bestDist = d; best = f; }
            }
            return best;
        }

        /// <summary>获取所有存活敌人。</summary>
        List<BattleFighter> GetAliveEnemies()
        {
            var list = new List<BattleFighter>();
            for (int i = 0; i < _allFighters.Count; i++)
            {
                var f = _allFighters[i];
                if (f.TeamId != TeamId && !f.IsDead)
                    list.Add(f);
            }
            return list;
        }

        /// <summary>寻找需要治疗的友方目标：血量百分比最低且未满血的存活友军（含自身）。</summary>
        BattleFighter FindHealTarget()
        {
            BattleFighter best = null;
            FixedInt bestPct = FixedInt.OneVal; // 100%
            for (int i = 0; i < _allFighters.Count; i++)
            {
                var f = _allFighters[i];
                if (f.TeamId != TeamId || f.IsDead) continue;
                if (f.Hp >= f.MaxHp) continue; // 满血不治
                var pct = f.Hp / f.MaxHp;
                if (best == null || pct < bestPct)
                {
                    bestPct = pct;
                    best = f;
                }
            }
            return best;
        }

        /// <summary>对目标施加治疗（不超过最大血量）。</summary>
        void ApplyHeal(BattleFighter target, FixedInt healAmount, int frame)
        {
            target.Hp = target.Hp + healAmount;
            if (target.Hp > target.MaxHp)
                target.Hp = target.MaxHp;

            _events.Add(new BattleEvent
            {
                Frame    = frame,
                Type     = BattleEventType.HealApplied,
                SourceId = PlayerId,
                TargetId = target.PlayerId,
                IntParam = healAmount.ToInt(),
            });

            _events.Add(new BattleEvent
            {
                Frame    = frame,
                Type     = BattleEventType.HpChanged,
                SourceId = target.PlayerId,
                IntParam = target.Hp.ToInt(),
            });
        }

        // ═══════════════════════════════════════════════════════
        //  每帧驱动
        // ═══════════════════════════════════════════════════════

        public void Tick(int frame, FixedInt dt)
        {
            if (IsDead) return;

            // 动态选择当前目标（最近存活敌人）
            _target = FindNearestEnemy();

            // 冷却递减
            if (AtkCooldownLeft > 0) AtkCooldownLeft--;
            if (UltCooldownLeft > 0) UltCooldownLeft--;
            if (Skill2CooldownLeft > 0) Skill2CooldownLeft--;

            // Buff计时与效果（包含减速、眩晕等）
            TickBuffs();

            // 隐身计时
            if (_stealthFramesLeft > 0)
            {
                _stealthFramesLeft--;
                if (_stealthFramesLeft <= 0)
                    IsStealthed = false;
            }

            // ── 大招立即打断：玩家按键 + CD好 → 无视一切状态直接施放 ──
            if (UltRequested && UltCooldownLeft <= 0 && _ultType != null)
            {
                // 打断所有状态
                IsCasting = false;
                IsMoving  = false;
                IsFleeing = false;
                IsStunned = false;
                RemoveBuff(BuffType.Stun);
                _bt.ResetTree();
                BeginCast(true);
                // 跳过行为树，直接进入施法
            }
            // ── 眩晕中：不执行行为树，不施法 ──
            else if (IsStunned)
            {
                IsMoving = false;
                // 什么都不做，等待眩晕结束
            }
            // ── 施法锁定：前摇/后摇期间不执行行为树 ──
            else if (IsCasting)
            {
                _castFramesLeft--;
                if (_castFramesLeft <= 0)
                {
                    if (!_castIsRecovery)
                    {
                        // 前摇结束 → 造成伤害 → 进入后摇
                        if (_castIsUlt)
                            ExecuteUltimate(frame);
                        else
                            ExecuteNormalAttack(frame);

                        int recovery = _castIsUlt ? _ultRecovery : _atkRecovery;
                        if (recovery > 0)
                        {
                            _castFramesLeft = recovery;
                            _castIsRecovery = true;
                        }
                        else
                        {
                            IsCasting = false;
                        }
                    }
                    else
                    {
                        // 后摇结束 → 恢复正常
                        IsCasting = false;
                    }
                }
            }
            else
            {
                // 行为树决策
                _bt.Tick(frame, dt);
            }

            // 每帧发送位置+朝向通知
            _events.Add(new BattleEvent
            {
                Frame    = frame,
                Type     = BattleEventType.Move,
                SourceId = PlayerId,
                PosXRaw  = Position.X.Raw,
                PosYRaw  = Position.Y.Raw,
                IntParam = ((int)(Facing.X.Raw >> 17) & 0xFFFF) << 16
                         | ((int)(Facing.Y.Raw >> 17) & 0xFFFF),
            });

            // 状态变化通知
            int curState = (IsMoving ? StateBit_Moving : 0)
                         | (IsFleeing ? StateBit_Fleeing : 0)
                         | (IsCasting ? StateBit_Casting : 0)
                         | (IsCasting && _castIsUlt ? StateBit_CastUlt : 0)
                         | (IsStunned ? StateBit_Stunned : 0)
                         | (IsStealthed ? StateBit_Stealthed : 0)
                         | (IsSlowed ? StateBit_Slowed : 0);
            if (curState != _prevStateBits)
            {
                _events.Add(new BattleEvent
                {
                    Frame    = frame,
                    Type     = BattleEventType.StateChanged,
                    SourceId = PlayerId,
                    IntParam = curState,
                });
                _prevStateBits = curState;
            }

            // CD变化通知
            if (AtkCooldownLeft != _prevAtkCD || UltCooldownLeft != _prevUltCD || Skill2CooldownLeft != _prevSkill2CD)
            {
                var cdEvt = new BattleEvent
                {
                    Frame    = frame,
                    Type     = BattleEventType.CooldownUpdate,
                    SourceId = PlayerId,
                    IntParam = AtkCooldownLeft | (Skill2CooldownLeft << 16),
                    PosXRaw  = UltCooldownLeft,
                };
                // 首次发送时附带总CD值
                if (!_cdTotalsEmitted)
                {
                    cdEvt.PosYRaw = ((long)AtkCooldown << 32) | (uint)UltCooldown;
                    _cdTotalsEmitted = true;
                }
                _events.Add(cdEvt);
                _prevAtkCD = AtkCooldownLeft;
                _prevUltCD = UltCooldownLeft;
                _prevSkill2CD = Skill2CooldownLeft;
            }

            // 清除本帧大招指令
            UltRequested = false;
        }

        // ═══════════════════════════════════════════════════════
        //  行为树：从 JSON 配置构建
        // ═══════════════════════════════════════════════════════

        BTNode BuildBTFromConfig(BTNodeConfig config)
        {
            switch (config.type)
            {
                case "selector":
                {
                    var node = new BTSelector();
                    if (config.children != null)
                        foreach (var child in config.children)
                            node.AddChild(BuildBTFromConfig(child));
                    return node;
                }
                case "sequence":
                {
                    var node = new BTSequence();
                    if (config.children != null)
                        foreach (var child in config.children)
                            node.AddChild(BuildBTFromConfig(child));
                    return node;
                }
                case "condition":
                    return new BTCondition(_ => EvalConditions(config.check));
                case "action":
                    return ResolveAction(config.run);
                default:
                    UnityEngine.Debug.LogError($"[BT] Unknown node type: {config.type}");
                    return new BTCondition(_ => false);
            }
        }

        bool EvalConditions(string[] checks)
        {
            if (checks == null) return true;
            for (int i = 0; i < checks.Length; i++)
                if (!EvalCondition(checks[i])) return false;
            return true;
        }

        bool EvalCondition(string name)
        {
            switch (name)
            {
                case "IsFleeing":          return IsFleeing;
                case "NotCasting":         return !IsCasting;
                case "NotMoving":          return !IsMoving;
                case "EnemyInAtkRange":    return EffectiveEnemyDist() <= AtkRange;
                case "EnemyOutOfAtkRange": return EffectiveEnemyDist() > AtkRange;
                case "Skill2CdReady":      return Skill2CooldownLeft <= 0;
                case "AtkCdReady":         return AtkCooldownLeft <= 0;
                case "UltCdReady":         return UltCooldownLeft <= 0;
                case "UltRequested":       return UltRequested;
                case "FacingTarget":       return _target != null && IsFacingTarget(_target.Position);
                case "AllyNeedsHeal":      return _skill2TargetAlly && FindHealTarget() != null;
                default:
                    UnityEngine.Debug.LogError($"[BT] Unknown condition: {name}");
                    return false;
            }
        }

        BTNode ResolveAction(string name)
        {
            switch (name)
            {
                case "DoFlee":        return new BTAction(ctx => DoFlee(ctx.DeltaTime));
                case "DoMoveToward":  return new BTAction(ctx => DoMoveToward(ctx.DeltaTime));
                case "ExecuteSkill2": return new BTSimpleAction(ctx => ExecuteSkill2(ctx.Frame));
                case "BeginCastUlt":  return new BTSimpleAction(ctx => BeginCast(true));
                case "BeginCastAtk":  return new BTSimpleAction(ctx => BeginCast(false));
                default:
                    UnityEngine.Debug.LogError($"[BT] Unknown action: {name}");
                    return new BTSimpleAction(_ => { });
            }
        }

        // ═══════════════════════════════════════════════════════
        //  技能执行
        // ═══════════════════════════════════════════════════════

        FixedInt EnemyDist() => _target != null ? FixedVector2.Distance(Position, _target.Position) : FixedInt.FromInt(99999);

        /// <summary>考虑目标碰撞半径的有效攻击距离（圆心距 - 目标半径）。</summary>
        FixedInt EffectiveEnemyDist() => _target != null
            ? FixedVector2.Distance(Position, _target.Position) - _target.Radius
            : FixedInt.FromInt(99999);

        /// <summary>副技能：Type决定投递方式，TargetTeam/TargetScope决定目标，Damage正负决定伤害/治疗。</summary>
        void ExecuteSkill2(int frame)
        {
            Skill2CooldownLeft = _skill2Cooldown;
            // 作用敌方但没有敌人目标时跳过（友方技能不需要敌人目标）
            if (!_skill2TargetAlly && _target == null) return;

            // ── 投递方式特殊处理（仅位移类/弹射物类） ──
            switch (_skill2Type)
            {
                case "Blink":
                {
                    if (_target == null) break;
                    var toEnemy = _target.Position - Position;
                    var dist = toEnemy.Magnitude;
                    if (dist > AtkRange)
                    {
                        Position = _target.Position - toEnemy.Normalized * AtkRange;
                        Facing = toEnemy.Normalized;
                        ClampToArena();
                    }
                    IsMoving = false;
                    break;
                }
                case "KnockbackArrow":
                {
                    if (_target == null) break;
                    var proj = new Projectile();
                    proj.Init(PlayerId, _target, Position, _skill2Param1, _skill2Damage,
                              isSkill2: true, knockbackDist: _skill2Param2,
                              sourceFighter: this);
                    PendingProjectiles.Add(proj);

                    _events.Add(new BattleEvent
                    {
                        Frame    = frame,
                        Type     = BattleEventType.ProjectileSpawn,
                        SourceId = PlayerId,
                        TargetId = _target.PlayerId,
                        IntParam = 1,
                        PosXRaw  = Position.X.Raw,
                        PosYRaw  = Position.Y.Raw,
                    });
                    break;
                }
                default:
                {
                    // ── 统一效果施加（Instant等类型）──
                    var targets = SelectTargets(_skill2TargetAlly, _skill2TargetAll, _skill2TargetLowestHp);
                    for (int i = 0; i < targets.Count; i++)
                        ApplyEffect(targets[i], _skill2Damage, _skill2Buffs, frame);
                    break;
                }
            }

            // 技能释放事件
            var targets2 = SelectTargets(_skill2TargetAlly, _skill2TargetAll, _skill2TargetLowestHp);
            byte sk2TargetId = targets2.Count > 0 ? targets2[0].PlayerId
                             : (_target != null ? _target.PlayerId : (byte)0);
            _events.Add(new BattleEvent
            {
                Frame    = frame,
                Type     = BattleEventType.Skill2Cast,
                SourceId = PlayerId,
                TargetId = sk2TargetId,
                IntParam = _skill2Damage.ToInt(),
                PosXRaw  = Position.X.Raw,
                PosYRaw  = Position.Y.Raw,
            });
        }

        /// <summary>朝向是否大致对准敌人（dot > 0.7 ≈ ±45°）。</summary>
        bool IsFacingTarget(FixedVector2 targetPos)
        {
            var dir = targetPos - Position;
            if (dir.SqrMagnitude == FixedInt.Zero) return true;
            return FixedVector2.Dot(Facing, dir.Normalized) >= FacingThreshold;
        }

        /// <summary>角度→cos近似（度数输入，定点输出）。</summary>
        static FixedInt CosApprox(int degrees)
        {
            // 常用角度查表
            if (degrees <= 0)  return FixedInt.OneVal;
            if (degrees >= 90) return FixedInt.Zero;
            if (degrees == 60) return FixedInt.Half;
            if (degrees == 45) return FacingThreshold; // ~0.707
            // 线性插值近似: cos(d) ≈ 1 - d/90
            return FixedInt.OneVal - FixedInt.FromInt(degrees) / FixedInt.FromInt(90);
        }

        /// <summary>将位置限制在场地内。</summary>
        internal void ClampToArena()
        {
            Position = new FixedVector2(
                FixedInt.Clamp(Position.X, -ArenaHalf, ArenaHalf),
                FixedInt.Clamp(Position.Y, -ArenaHalf, ArenaHalf));
        }

        /// <summary>尝试转向目标方向，返回是否已完成转向。</summary>
        bool TurnToward(FixedVector2 targetDir, FixedInt dt)
        {
            if (targetDir.SqrMagnitude == FixedInt.Zero) return true;
            var maxRad = TurnSpeed * dt;
            Facing = FixedVector2.RotateToward(Facing, targetDir, maxRad);
            return FixedVector2.Dot(Facing, targetDir.Normalized) >= FacingThreshold;
        }

        /// <summary>进入施法状态（前摇 → 伤害结算 → 后摇）。</summary>
        void BeginCast(bool isUlt)
        {
            IsCasting = true;
            IsMoving  = false;
            _castIsUlt = isUlt;
            _castIsRecovery = false;
            _castFramesLeft = isUlt ? _ultWindup : _atkWindup;

            // 前摇 0 帧时直接在下一 Tick 开头结算（_castFramesLeft=0 → 立刻触发结算）
        }

        void ExecuteNormalAttack(int frame)
        {
            AtkCooldownLeft = AtkCooldown;

            // ── 被动：暴击 — 普攻有概率造成双倍伤害 ──
            FixedInt effectiveDmg = AtkDamage;
            if (_passiveType == "CritStrike" && _passiveParam1.ToInt() > 0)
            {
                int roll = _bt.Context.Random.Next(100);
                if (roll < _passiveParam1.ToInt())
                {
                    effectiveDmg = AtkDamage * FixedInt.FromInt(_passiveParam2) / FixedInt.FromInt(100);
                }
            }

            if (_normalAtkType == "RangedAttack")
            {
                if (_target == null) return;
                _events.Add(new BattleEvent
                {
                    Frame    = frame,
                    Type     = BattleEventType.NormalAttack,
                    SourceId = PlayerId,
                    TargetId = _target.PlayerId,
                    IntParam = effectiveDmg.ToInt(),
                });

                var proj = new Projectile();
                proj.Init(PlayerId, _target, Position, _normalAtkProjectileSpeed, effectiveDmg,
                          sourceFighter: this);
                PendingProjectiles.Add(proj);

                _events.Add(new BattleEvent
                {
                    Frame    = frame,
                    Type     = BattleEventType.ProjectileSpawn,
                    SourceId = PlayerId,
                    TargetId = _target.PlayerId,
                    PosXRaw  = Position.X.Raw,
                    PosYRaw  = Position.Y.Raw,
                });
            }
            else if (_normalAtkType == "AoEProjectile")
            {
                // AoE直线弹射物（火球/冰球）：直线飞行，碰到敌人或到达最大距离后爆炸
                if (_target == null) return;
                var dir = _target.Position - Position;
                if (dir.SqrMagnitude == FixedInt.Zero) dir = Facing;

                var proj = new AoEProjectile();
                proj.Init(PlayerId, TeamId, Position, dir.Normalized,
                          _normalAtkProjectileSpeed, effectiveDmg, AtkRange,
                          FixedInt.FromInt(_normalAtkParam2),
                          FixedInt.FromFloat(0.5f),
                          _normalAtkBuffs, _allFighters, this);
                PendingAoEProjectiles.Add(proj);

                _events.Add(new BattleEvent
                {
                    Frame    = frame,
                    Type     = BattleEventType.NormalAttack,
                    SourceId = PlayerId,
                    TargetId = _target.PlayerId,
                    IntParam = effectiveDmg.ToInt(),
                });

                _events.Add(new BattleEvent
                {
                    Frame    = frame,
                    Type     = BattleEventType.ProjectileSpawn,
                    SourceId = PlayerId,
                    TargetId = _target.PlayerId,
                    IntParam = 2, // AoE弹射物标记
                    PosXRaw  = Position.X.Raw,
                    PosYRaw  = Position.Y.Raw,
                });
            }
            else if (_normalAtkConeAngle > 0)
            {
                // 锥形AoE近战：命中扇形范围内所有敌人
                var enemies = GetAliveEnemies();
                // cos(halfAngle) 阈值 — 用近似: cos(60°)=0.5, cos(45°)≈0.707
                // halfAngle = _normalAtkConeAngle / 2, 用 cos 表查找
                FixedInt cosThreshold = CosApprox(_normalAtkConeAngle / 2);
                bool hitAny = false;
                for (int i = 0; i < enemies.Count; i++)
                {
                    var e = enemies[i];
                    var toE = e.Position - Position;
                    var dist = toE.Magnitude;
                    // 包围盒命中：距离 - 敌方半径 <= 攻击范围
                    if (dist - e.Radius > AtkRange) continue;
                    // 角度检测
                    if (dist > FixedInt.Zero)
                    {
                        var dot = FixedVector2.Dot(Facing, toE.Normalized);
                        if (dot < cosThreshold) continue;
                    }
                    hitAny = true;
                    ApplyDamage(e, effectiveDmg, frame);
                }
                _events.Add(new BattleEvent
                {
                    Frame    = frame,
                    Type     = BattleEventType.NormalAttack,
                    SourceId = PlayerId,
                    TargetId = _target != null ? _target.PlayerId : (byte)0,
                    IntParam = effectiveDmg.ToInt(),
                    PosXRaw  = Position.X.Raw,
                    PosYRaw  = Position.Y.Raw,
                });
            }
            else
            {
                // 单体近战
                if (_target == null) return;
                _events.Add(new BattleEvent
                {
                    Frame    = frame,
                    Type     = BattleEventType.NormalAttack,
                    SourceId = PlayerId,
                    TargetId = _target.PlayerId,
                    IntParam = effectiveDmg.ToInt(),
                });
                ApplyDamage(_target, effectiveDmg, frame);
            }
        }

        void ExecuteUltimate(int frame)
        {
            UltCooldownLeft = UltCooldown;

            // ── 投递方式特殊处理（仅位移类/自身buff/召唤物） ──
            switch (_ultType)
            {
                case "DashStrike":
                {
                    if (_target != null)
                    {
                        var toEnemy = _target.Position - Position;
                        var dist = toEnemy.Magnitude;
                        if (dist > AtkRange)
                        {
                            Position = _target.Position - toEnemy.Normalized * AtkRange;
                            Facing = toEnemy.Normalized;
                            ClampToArena();
                        }
                    }
                    break;
                }
                case "Stealth":
                {
                    IsStealthed = true;
                    _stealthFramesLeft = _ultParam1.ToInt();
                    break;
                }
                case "SummonPet":
                {
                    var offset = Facing * FixedInt.FromInt(2);
                    var summonPos = Position - offset;
                    PendingSummons.Add(new SummonRequest
                    {
                        Type     = CharacterType.Snowman,
                        Position = summonPos,
                        Lifetime = _ultParam1.ToInt(),
                        MaxHp    = _ultParam2,
                    });
                    break;
                }
            }

            // 大招释放事件
            _events.Add(new BattleEvent
            {
                Frame    = frame,
                Type     = BattleEventType.UltimateCast,
                SourceId = PlayerId,
                TargetId = _target != null ? _target.PlayerId : (byte)0,
                IntParam = UltDamage.ToInt(),
                PosXRaw  = Position.X.Raw,
                PosYRaw  = Position.Y.Raw,
            });

            // Stealth/SummonPet 不造成伤害/治疗
            if (_ultType == "Stealth" || _ultType == "SummonPet") return;

            // ── 统一效果施加：根据 TargetTeam/TargetScope 选择目标，Damage正负决定伤害/治疗 ──
            var targets = SelectTargets(_ultTargetAlly, _ultTargetAll, false);
            for (int i = 0; i < targets.Count; i++)
                ApplyEffect(targets[i], UltDamage, _ultBuffs, frame);
        }

        void ApplyDamage(BattleFighter target, FixedInt damage, int frame)
        {
            // ── 被动：格挡 — 受到伤害时有概率完全免疫 ──
            if (target.TryDodgeBlock())
            {
                // 格挡成功，不造成伤害，仍然触发反应技能和被动
                if (!target.IsDead)
                {
                    target.TryReactSkill2(Position, PlayerId, frame, _events);
                    target.TryPassive(Position, PlayerId, frame, _events);
                }
                return;
            }

            target.Hp = target.Hp - damage;
            if (target.Hp < FixedInt.Zero)
                target.Hp = FixedInt.Zero;

            _events.Add(new BattleEvent
            {
                Frame    = frame,
                Type     = BattleEventType.Damage,
                SourceId = PlayerId,
                TargetId = target.PlayerId,
                IntParam = damage.ToInt(),
            });

            _events.Add(new BattleEvent
            {
                Frame    = frame,
                Type     = BattleEventType.HpChanged,
                SourceId = target.PlayerId,
                IntParam = target.Hp.ToInt(),
            });

            // ── 攻击者被动：造成伤害后触发 ──
            OnDealDamage(damage, frame, _events);

            // 被击后触发反应式副技能（ReactBlink等）+ 被动技能（FleeOnHit等）
            if (!target.IsDead)
            {
                bool reacted = target.TryReactSkill2(Position, PlayerId, frame, _events);
                if (!reacted)
                    target.TryPassive(Position, PlayerId, frame, _events);
            }

            if (target.IsDead)
            {
                _events.Add(new BattleEvent
                {
                    Frame    = frame,
                    Type     = BattleEventType.Death,
                    SourceId = target.PlayerId,
                });
            }
        }

        // ═══════════════════════════════════════════════════════
        //  Buff系统
        // ═══════════════════════════════════════════════════════

        /// <summary>添加buff，同类buff刷新持续时间。眩晕立即生效并打断当前动作。</summary>
        public void AddBuff(Buff buff)
        {
            for (int i = 0; i < _buffs.Count; i++)
            {
                if (_buffs[i].Type == buff.Type)
                {
                    _buffs[i] = buff;
                    if (buff.Type == BuffType.Stun) { IsStunned = true; IsMoving = false; IsCasting = false; }
                    return;
                }
            }
            _buffs.Add(buff);
            if (buff.Type == BuffType.Stun) { IsStunned = true; IsMoving = false; IsCasting = false; }
        }

        void TickBuffs()
        {
            MoveSpeed = BaseMoveSpeed;
            IsSlowed = false;
            IsStunned = false;
            for (int i = _buffs.Count - 1; i >= 0; i--)
            {
                var b = _buffs[i];
                b.FramesLeft--;
                if (b.FramesLeft <= 0)
                {
                    _buffs.RemoveAt(i);
                    continue;
                }
                _buffs[i] = b;
                switch (b.Type)
                {
                    case BuffType.Slow:
                        MoveSpeed = MoveSpeed * b.Value;
                        IsSlowed = true;
                        break;
                    case BuffType.Stun:
                        IsStunned = true;
                        break;
                }
            }
        }

        /// <summary>移除指定类型的buff。</summary>
        void RemoveBuff(BuffType type)
        {
            for (int i = _buffs.Count - 1; i >= 0; i--)
            {
                if (_buffs[i].Type == type)
                    _buffs.RemoveAt(i);
            }
        }

        /// <summary>将buff模板列表施加到目标身上，并产生事件。</summary>
        void ApplyBuffsToTarget(BattleFighter target, BuffTemplate[] buffs, int frame)
        {
            if (buffs == null) return;
            for (int i = 0; i < buffs.Length; i++)
            {
                var bt = buffs[i];
                target.AddBuff(new Buff
                {
                    Type       = bt.Type,
                    FramesLeft = bt.Duration,
                    Value      = bt.Value,
                    SourceId   = PlayerId,
                });
                _events.Add(new BattleEvent
                {
                    Frame    = frame,
                    Type     = BattleEventType.BuffApplied,
                    SourceId = PlayerId,
                    TargetId = target.PlayerId,
                    IntParam = ((int)bt.Type << 16) | bt.Duration,
                });
            }
        }

        // ═══════════════════════════════════════════════════════
        //  统一目标选择与效果施加
        // ═══════════════════════════════════════════════════════

        /// <summary>根据配置选择技能目标。</summary>
        List<BattleFighter> SelectTargets(bool targetAlly, bool targetAll, bool targetLowestHp)
        {
            var result = new List<BattleFighter>();
            if (targetAlly)
            {
                if (targetAll)
                {
                    for (int i = 0; i < _allFighters.Count; i++)
                    {
                        var f = _allFighters[i];
                        if (f.TeamId == TeamId && !f.IsDead) result.Add(f);
                    }
                }
                else if (targetLowestHp)
                {
                    var t = FindHealTarget();
                    if (t != null) result.Add(t);
                }
            }
            else // Enemy
            {
                if (targetAll)
                    result = GetAliveEnemies();
                else if (_target != null)
                    result.Add(_target);
            }
            return result;
        }

        /// <summary>统一效果施加：正数=伤害，负数=治疗，同时施加buffs。</summary>
        void ApplyEffect(BattleFighter target, FixedInt amount, BuffTemplate[] buffs, int frame)
        {
            if (target.IsDead) return;
            if (amount > FixedInt.Zero)
                ApplyDamage(target, amount, frame);
            else if (amount < FixedInt.Zero)
                ApplyHeal(target, FixedInt.Zero - amount, frame);
            if (!target.IsDead)
                ApplyBuffsToTarget(target, buffs, frame);
        }

        /// <summary>被击后触发反应式副技能（如ReactBlink）。返回 true 表示触发了反应。</summary>
        public bool TryReactSkill2(FixedVector2 damageSourcePos, byte sourceId, int frame, List<BattleEvent> events)
        {
            if (IsDead || IsCasting) return false;
            if (_skill2Type != "ReactBlink" || Skill2CooldownLeft > 0) return false;

            Skill2CooldownLeft = _skill2Cooldown;
            var away = Position - damageSourcePos;
            var dist = away.Magnitude;
            var blinkDir = dist > FixedInt.Zero ? away / dist : -Facing;
            Position = Position + blinkDir * _skill2Param1;
            ClampToArena();
            IsMoving = false;

            events.Add(new BattleEvent
            {
                Frame    = frame,
                Type     = BattleEventType.Skill2Cast,
                SourceId = PlayerId,
                TargetId = sourceId,
                PosXRaw  = Position.X.Raw,
                PosYRaw  = Position.Y.Raw,
            });
            return true;
        }

        /// <summary>被击后触发被动技能（无CD，每次受击都会触发）。</summary>
        public bool TryPassive(FixedVector2 damageSourcePos, byte sourceId, int frame, List<BattleEvent> events)
        {
            if (IsDead || IsCasting) return false;
            if (string.IsNullOrEmpty(_passiveType)) return false;

            switch (_passiveType)
            {
                case "FleeOnHit":
                {
                    if (IsFleeing) return false; // 正在逃跑中不重复触发
                    IsFleeing = true;
                    _fleeStartPos = Position;
                    return true;
                }
                default:
                    return false;
            }
        }

        /// <summary>格挡判定。返回 true = 伤害被格挡。供弹射物等外部伤害源调用。</summary>
        public bool TryDodgeBlock()
        {
            if (_passiveType != "DodgeBlock" || _passiveParam1.ToInt() <= 0 || _bt == null)
                return false;
            int roll = _bt.Context.Random.Next(100);
            return roll < _passiveParam1.ToInt();
        }

        /// <summary>造成伤害后触发攻击者被动（UltCDReduce/Lifesteal），召唤物不触发。</summary>
        public void OnDealDamage(FixedInt damage, int frame, List<BattleEvent> events)
        {
            if (_bt == null || IsSummon) return;
            if (string.IsNullOrEmpty(_passiveType)) return;

            // 奥术涌流：有概率减少大招CD
            if (_passiveType == "UltCDReduce" && _passiveParam1.ToInt() > 0 && UltCooldownLeft > 0)
            {
                int roll = _bt.Context.Random.Next(100);
                if (roll < _passiveParam1.ToInt())
                {
                    UltCooldownLeft -= _passiveParam2;
                    if (UltCooldownLeft < 0) UltCooldownLeft = 0;
                }
            }
            // 生命汲取：有概率回复造成伤害的一定比例HP
            if (_passiveType == "Lifesteal" && _passiveParam1.ToInt() > 0 && !IsDead)
            {
                int roll = _bt.Context.Random.Next(100);
                if (roll < _passiveParam1.ToInt())
                {
                    FixedInt healAmt = damage * FixedInt.FromInt(_passiveParam2) / FixedInt.FromInt(100);
                    if (healAmt > FixedInt.Zero)
                    {
                        Hp = Hp + healAmt;
                        if (Hp > MaxHp) Hp = MaxHp;
                        events.Add(new BattleEvent
                        {
                            Frame    = frame,
                            Type     = BattleEventType.HealApplied,
                            SourceId = PlayerId,
                            TargetId = PlayerId,
                            IntParam = healAmt.ToInt(),
                        });
                        events.Add(new BattleEvent
                        {
                            Frame    = frame,
                            Type     = BattleEventType.HpChanged,
                            SourceId = PlayerId,
                            IntParam = Hp.ToInt(),
                        });
                    }
                }
            }
        }

        // ═══════════════════════════════════════════════════════
        //  移动逻辑
        // ═══════════════════════════════════════════════════════

        /// <summary>向敌人靠近（先转向再移动，到攻击范围内停下）。</summary>
        BTStatus DoMoveToward(FixedInt dt)
        {
            // 被攻击后需要逃跑 → 返回 Failure 让 Selector 重置，回到逃跑分支
            if (IsFleeing) { IsMoving = false; return BTStatus.Failure; }
            // 瞬移CD好且距离远 → 返回 Failure 让 Selector 重置，走瞬移分支
            if (_skill2Type == "Blink" && Skill2CooldownLeft <= 0 && !IsCasting
                && EffectiveEnemyDist() > AtkRange)
            { IsMoving = false; return BTStatus.Failure; }
            if (_target == null || _target.IsDead) { IsMoving = false; return BTStatus.Success; }

            var toEnemy = _target.Position - Position;
            var dist = toEnemy.Magnitude;
            var effectiveDist = dist - _target.Radius;

            if (effectiveDist <= AtkRange)
            {
                IsMoving = false;
                if (!TurnToward(toEnemy, dt))
                    return BTStatus.Running;
                return BTStatus.Success;
            }

            // 期望速度方向 = 朝目标
            var desiredDir = dist > FixedInt.Zero ? toEnemy / dist : Facing;
            var desiredVel = desiredDir * MoveSpeed;

            // ── 碰撞规避：收集前方障碍物（排除自身和目标）──
            int obsCount = 0;
            if (_allFighters != null)
            {
                for (int i = 0; i < _allFighters.Count && obsCount < _obstacleBuffer.Length; i++)
                {
                    var f = _allFighters[i];
                    if (f == this || f == _target || f.IsDead) continue;
                    _obstacleBuffer[obsCount++] = new FixedCircle(f.Position, f.Radius);
                }
            }

            FixedVector2 moveDir;
            if (obsCount > 0)
            {
                var lookAhead = MoveSpeed + Radius;
                var avoidForce = FixedSteering.ObstacleAvoidance(
                    Position, desiredVel, Radius,
                    _obstacleBuffer, obsCount, lookAhead);
                var steerVel = desiredVel + avoidForce;
                var steerMag = steerVel.Magnitude;
                moveDir = steerMag > FixedInt.Zero ? steerVel / steerMag : desiredDir;
            }
            else
            {
                moveDir = desiredDir;
            }

            // 转向到移动方向
            if (!TurnToward(moveDir, dt))
            {
                IsMoving = false;
                return BTStatus.Running;
            }

            // 已对准，沿 Facing 方向前进
            IsMoving = true;
            Position = Position + Facing * MoveSpeed * dt;
            ClampToArena();
            return BTStatus.Running;
        }

        /// <summary>主动远离敌人（弓手拉扯到射程距离）。</summary>
        BTStatus DoMoveAway(FixedInt dt)
        {
            if (_target == null || _target.IsDead) { IsMoving = false; return BTStatus.Success; }

            var awayDir = Position - _target.Position;
            var dist = awayDir.Magnitude;

            if (dist >= AtkRange)
            {
                IsMoving = false;
                return BTStatus.Success;
            }

            var desiredDir = dist > FixedInt.Zero ? awayDir / dist : Facing;
            var moveDir = ApplyAvoidance(desiredDir);

            if (!TurnToward(moveDir, dt))
            {
                IsMoving = false;
                return BTStatus.Running;
            }

            IsMoving = true;
            Position = Position + Facing * MoveSpeed * dt;
            ClampToArena();
            return BTStatus.Running;
        }

        /// <summary>被攻击后逃跑（先转向再跑，跑够 _fleeDistance 位移后停下攻击）。</summary>
        BTStatus DoFlee(FixedInt dt)
        {
            if (_target == null || _target.IsDead) { IsFleeing = false; IsMoving = false; return BTStatus.Success; }

            var awayDir = Position - _target.Position;
            var fled = (Position - _fleeStartPos).Magnitude;

            if (fled >= _fleeDistance)
            {
                IsFleeing = false;
                IsMoving = false;
                return BTStatus.Success;
            }

            var dist = awayDir.Magnitude;
            var desiredDir = dist > FixedInt.Zero ? awayDir / dist : Facing;
            var moveDir = ApplyAvoidance(desiredDir);

            if (!TurnToward(moveDir, dt))
            {
                IsMoving = false;
                return BTStatus.Running;
            }

            IsMoving = true;
            Position = Position + Facing * MoveSpeed * dt;
            ClampToArena();
            return BTStatus.Running;
        }

        /// <summary>对期望方向施加碰撞规避，返回修正后的单位方向。</summary>
        FixedVector2 ApplyAvoidance(FixedVector2 desiredDir)
        {
            int obsCount = 0;
            if (_allFighters != null)
            {
                for (int i = 0; i < _allFighters.Count && obsCount < _obstacleBuffer.Length; i++)
                {
                    var f = _allFighters[i];
                    if (f == this || f.IsDead) continue;
                    _obstacleBuffer[obsCount++] = new FixedCircle(f.Position, f.Radius);
                }
            }
            if (obsCount == 0) return desiredDir;

            var desiredVel = desiredDir * MoveSpeed;
            var lookAhead = MoveSpeed + Radius;
            var avoidForce = FixedSteering.ObstacleAvoidance(
                Position, desiredVel, Radius,
                _obstacleBuffer, obsCount, lookAhead);

            var steerVel = desiredVel + avoidForce;
            var steerMag = steerVel.Magnitude;
            return steerMag > FixedInt.Zero ? steerVel / steerMag : desiredDir;
        }

        /// <summary>碰撞分离：推开所有重叠的角色对。</summary>
        public static void ResolveCollisions(List<BattleFighter> fighters)
        {
            for (int i = 0; i < fighters.Count; i++)
            {
                var a = fighters[i];
                if (a.IsDead) continue;
                for (int j = i + 1; j < fighters.Count; j++)
                {
                    var b = fighters[j];
                    if (b.IsDead) continue;
                    var push = FixedSteering.ResolveOverlap(a.Position, a.Radius, b.Position, b.Radius);
                    if (push.SqrMagnitude > FixedInt.Zero)
                    {
                        a.Position = a.Position + push;
                        b.Position = b.Position - push;
                        a.ClampToArena();
                        b.ClampToArena();
                    }
                }
            }
        }
    }
}
