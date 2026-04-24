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
        public FixedInt BaseAtkDamage;    // 不受buff影响的基础攻击力
        public int      AtkCooldown;      // 冷却总帧数（受攻速buff影响）
        public int      AtkCooldownLeft;  // 剩余冷却帧数
        int _baseAtkCooldown;             // 配置基础CD
        int _baseAtkWindup;               // 配置基础前摇
        int _baseAtkRecovery;             // 配置基础后摇

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
        public bool IsStaggered;  // 被打断僵直中
        public bool IsAtkBuffed;  // 有增益性战斗buff（攻击力提升/攻速提升/减伤）
        public bool IsAtkDebuffed; // 有减益性战斗buff（攻速降低）

        // ── 内部引用 ─────────────────────────────────────────

        BehaviorTree     _bt;
        BattleFighter    _target;        // 当前目标（每帧动态选择最近敌人）
        List<BattleFighter> _allFighters; // 全部战斗单位引用
        List<BattleEvent> _events;

        internal FixedInt _fleeDistance;
        internal FixedVector2 _fleeStartPos;
        int _prevStateBits;

        // ── 被动技能 ────────────────────────────────────────

        string _passiveType;          // 被动技能类型（FleeOnHit/CritStrike/DodgeBlock/UltCDReduce/Lifesteal/OnHitDebuff/AuraResistance）
        FixedInt _passiveParam1;      // 被动参数1
        FixedInt _passiveParam2;      // 被动参数2
        BuffTemplate[] _passiveBuffs; // OnHitDebuff被动附加的buff列表

        // ── 光环被动（AuraResistance）──
        public bool HasAuraResistance => _passiveType == "AuraResistance";
        public FixedInt AuraResBonus => _passiveParam1;  // 光环加成的抗性值
        public FixedInt AuraRange => _passiveParam2 > FixedInt.Zero ? _passiveParam2 : FixedInt.FromInt(5);
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
        FixedInt _normalAtkParam2;     // AoE弹射物爆炸半径
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
        bool _skill2IsGapCloser;     // true=突进类技能(Blink)，远距离使用
        bool _skill2IsReactive;      // true=被动反应式技能(ReactBlink)，不在行为树中触发
        int _skill2TargetRandomCount; // >0时随机选N个目标
        int _prevSkill2CD;
        FixedInt _ultParam1;         // 大招参数1
        FixedInt _ultParam2;         // 大招参数2
        BuffTemplate[] _ultBuffs;    // 大招附加的buff列表
        bool _ultTargetAlly;         // true=作用友方, false=作用敌方
        bool _ultTargetAll;          // true=全体, false=单体
        int _ultTargetRandomCount;   // >0时随机选N个目标
        int _stealthFramesLeft;
        int _staggerDuration;     // 本角色僵直帧数（配置值）
        FixedInt _resistance;     // 抗性（百分比减伤，50=受50%伤害）
        public FixedInt BonusResistance; // 光环加成的额外抗性
        int _staggerFramesLeft;   // 当前僵直剩余帧数

        /// <summary>本帧新生成的弹射物（由 BattleLogic 取走并管理）。</summary>
        public readonly List<Projectile> PendingProjectiles = new();

        /// <summary>本帧新生成的AoE弹射物（火球/冰球，由 BattleLogic 取走）。</summary>
        public readonly List<AoEProjectile> PendingAoEProjectiles = new();

        /// <summary>本帧新生成的穿刺弹射物（闪电，由 BattleLogic 取走）。</summary>
        public readonly List<PiercingProjectile> PendingPiercingProjectiles = new();

        /// <summary>本帧请求创建的召唤物（由 BattleLogic 取走并创建）。</summary>
        public readonly List<SummonRequest> PendingSummons = new();

        /// <summary>本帧请求创建的闪电云（由 BattleLogic 取走并创建）。</summary>
        public readonly List<LightningCloudRequest> PendingLightningClouds = new();

        /// <summary>本帧请求创建的拉取效果（由 BattleLogic 取走并处理）。</summary>
        public readonly List<PullEffect> PendingPulls = new();

        // ── 反伤护盾（ReflectShield大招）────────────────────────
        public int _reflectFramesLeft;      // 反伤剩余帧数
        FixedInt _reflectPercent;           // 反伤百分比
        bool _isReflecting;                 // 防止反伤递归

        // ── Buff系统 ──────────────────────────────────────────
        readonly List<Buff> _buffs = new();
        public bool IsSlowed;

        // ── 召唤物支持 ────────────────────────────────────────
        public BattleFighter Master;    // 非null表示这是召唤物
        public int LifetimeLeft;         // 召唤物剩余存活帧数（0=无限制）
        public bool IsSummon => Master != null;

        // ── 自我复活（SelfRevive被动）────────────────────────
        public int SelfReviveFramesLeft;   // >0表示正在等待复活的倒计时
        FixedInt _selfReviveDelay;              // 复活延迟帧数（从配置加载）
        FixedInt _selfRevivesLeft;              // 剩余可复活次数
        public bool PendingSelfRevive => SelfReviveFramesLeft > 0;

        // ── 死亡爆炸（DeathExplode被动）────────────────────────
        public bool HasDeathExplode => _passiveType == "DeathExplode";
        public FixedInt DeathExplodeRadius => _passiveParam1;
        public FixedInt DeathExplodeDamage => _passiveParam2;
        public bool DeathExplodeProcessed;

        // ── 碰撞规避缓冲区（复用避免GC）────────────────────────
        static readonly FixedCircle[] _obstacleBuffer = new FixedCircle[64];

        public const int StateBit_Moving    = 1 << 0;
        public const int StateBit_Fleeing   = 1 << 1;
        public const int StateBit_Casting   = 1 << 2;
        public const int StateBit_CastUlt   = 1 << 3; // 仅 Casting 时有效
        public const int StateBit_Stunned   = 1 << 4;
        public const int StateBit_Stealthed = 1 << 5;
        public const int StateBit_Slowed    = 1 << 6;
        public const int StateBit_Staggered  = 1 << 7;
        public const int StateBit_AtkBuffed   = 1 << 8;
        public const int StateBit_AtkDebuffed = 1 << 9;

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
            _staggerDuration = cfg.StaggerDuration;
            _resistance = FixedInt.FromInt(cfg.Resistance);

            // ── 职业与索敌优先级 ──
            _profession = CharacterConfig.ParseProfession(cfg.Profession);
            _targetPriority = CharacterConfig.GetTargetPriority(_profession);

            // ── 被动技能 ──
            var passive = SkillConfigLoader.Get(cfg.Passive);
            if (passive != null)
            {
                _passiveType     = passive.Type;
                _passiveParam1   = FixedInt.FromFloat(passive.Param1);
                _passiveParam2   = FixedInt.FromFloat(passive.Param2);
                _fleeDistance     = _passiveType == "FleeOnHit" ? _passiveParam1 : FixedInt.Zero;
                _passiveBuffs    = BuffTemplate.FromConfigs(passive.Buffs);
                if (_passiveType == "SelfRevive")
                {
                    _selfReviveDelay = FixedInt.FromFloat(passive.Param1);
                    _selfRevivesLeft = FixedInt.FromFloat(passive.Param2);
                }
            }

            // ── 普攻技能 ──
            var atkSkill = SkillConfigLoader.Get(cfg.NormalAttack);
            if (atkSkill != null)
            {
                _normalAtkType = atkSkill.Type;
                BaseAtkDamage = FixedInt.FromInt(atkSkill.Damage);
                AtkDamage    = BaseAtkDamage;
                AtkRange     = FixedInt.FromFloat(atkSkill.Range);
                _baseAtkCooldown = atkSkill.Cooldown;
                _baseAtkWindup   = atkSkill.Windup;
                _baseAtkRecovery = atkSkill.Recovery;
                AtkCooldown  = _baseAtkCooldown;
                _atkWindup   = _baseAtkWindup;
                _atkRecovery = _baseAtkRecovery;
                _normalAtkProjectileSpeed = FixedInt.FromFloat(atkSkill.Param1);
                _normalAtkConeAngle = (_normalAtkType == "MeleeAttack") ? FixedInt.FromFloat(atkSkill.Param1).ToInt() : 0;
                _normalAtkParam2 = FixedInt.FromFloat(atkSkill.Param2);
                _normalAtkBuffs = BuffTemplate.FromConfigs(atkSkill.Buffs);
            }

            // ── 副技能 ──
            var sk2 = SkillConfigLoader.Get(cfg.Skill2);
            if (sk2 != null)
            {
                _skill2Type     = sk2.Type;
                _skill2Damage   = FixedInt.FromInt(sk2.Damage);
                _skill2Cooldown = sk2.Cooldown;
                _skill2Param1   = FixedInt.FromFloat(sk2.Param1);
                _skill2Param2   = FixedInt.FromFloat(sk2.Param2);
                _skill2Buffs    = BuffTemplate.FromConfigs(sk2.Buffs);
                _skill2TargetAlly = sk2.TargetTeam == "Ally";
                _skill2TargetAll  = sk2.TargetScope == "All";
                _skill2TargetLowestHp = sk2.TargetScope == "LowestHp";
                _skill2TargetRandomCount = 0;
                if (sk2.TargetScope != null && sk2.TargetScope.StartsWith("Random"))
                    int.TryParse(sk2.TargetScope.Substring(6), out _skill2TargetRandomCount);
                _skill2IsGapCloser = _skill2Type == "Blink";
                _skill2IsReactive  = _skill2Type == "ReactBlink";
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
                _ultParam1   = FixedInt.FromFloat(ult.Param1);
                _ultParam2   = FixedInt.FromFloat(ult.Param2);
                _ultBuffs    = BuffTemplate.FromConfigs(ult.Buffs);
                _ultTargetAlly = ult.TargetTeam == "Ally";
                _ultTargetAll  = ult.TargetScope == "All";
                _ultTargetRandomCount = 0;
                if (ult.TargetScope != null && ult.TargetScope.StartsWith("Random"))
                    int.TryParse(ult.TargetScope.Substring(6), out _ultTargetRandomCount);
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
            IsStaggered = false;
            _staggerFramesLeft = 0;
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

        /// <summary>统计当前存活的由自己召唤的召唤物数量。</summary>
        int CountAliveSummons()
        {
            int count = 0;
            for (int i = 0; i < _allFighters.Count; i++)
            {
                var f = _allFighters[i];
                if (f.Master == this && !f.IsDead) count++;
            }
            return count;
        }

        /// <summary>寻找一个阵亡的非召唤物友方（用于复活）。</summary>
        BattleFighter FindDeadAlly()
        {
            for (int i = 0; i < _allFighters.Count; i++)
            {
                var f = _allFighters[i];
                if (f.TeamId != TeamId || !f.IsDead || f.IsSummon) continue;
                if (f.PlayerId == PlayerId) continue; // 不能复活自己
                return f;
            }
            return null;
        }

        /// <summary>寻找拉取目标：范围内距离最远的敌人。</summary>
        BattleFighter FindPullTarget()
        {
            var pullRange = _skill2Param1;
            BattleFighter farthest = null;
            FixedInt farthestDist = FixedInt.Zero;

            for (int i = 0; i < _allFighters.Count; i++)
            {
                var f = _allFighters[i];
                if (f.TeamId == TeamId || f.IsDead || f.IsStealthed) continue;
                var dist = FixedVector2.Distance(Position, f.Position);
                if (dist <= pullRange && dist > farthestDist)
                {
                    farthest = f;
                    farthestDist = dist;
                }
            }
            return farthest;
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

            // 反伤护盾计时
            if (_reflectFramesLeft > 0)
                _reflectFramesLeft--;

            // 僵直计时
            if (_staggerFramesLeft > 0)
            {
                _staggerFramesLeft--;
                if (_staggerFramesLeft <= 0)
                    IsStaggered = false;
            }

            // ── 大招立即打断：玩家按键 + CD好 → 无视一切状态直接施放 ──
            // Revive类型大招需要有阵亡友方才能施放
            bool ultCanCast = UltRequested && UltCooldownLeft <= 0 && _ultType != null;
            if (ultCanCast && _ultType == "Revive" && FindDeadAlly() == null)
                ultCanCast = false;
            if (ultCanCast)
            {
                // 打断所有状态
                IsCasting = false;
                IsMoving  = false;
                IsFleeing = false;
                IsStunned = false;
                IsStaggered = false;
                _staggerFramesLeft = 0;
                RemoveBuff(BuffType.Stun);
                _bt.ResetTree();
                BeginCast(true);
                // 跳过行为树，直接进入施法
            }
            // ── 僵直中：不执行行为树，不施法 ──
            else if (IsStaggered)
            {
                IsMoving = false;
                // 什么都不做，等待僵直结束
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
                         | (IsSlowed ? StateBit_Slowed : 0)
                         | (IsStaggered ? StateBit_Staggered : 0)
                         | (IsAtkBuffed ? StateBit_AtkBuffed : 0)
                         | (IsAtkDebuffed ? StateBit_AtkDebuffed : 0);
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
                case "Skill2Ready":        return EvalSkill2Ready();
                default:
                    UnityEngine.Debug.LogError($"[BT] Unknown condition: {name}");
                    return false;
            }
        }

        /// <summary>
        /// 复合条件：副技能是否可以释放。
        /// 综合判断CD、技能类型和使用时机：
        ///   - 无副技能 / 被动反应式(ReactBlink) → false
        ///   - 治疗类(TargetAlly) → 队友需要治疗
        ///   - 突进类(Blink) → 敌人在攻击范围外
        ///   - 其他战斗技能 → 站定 + 敌人在攻击范围内 + 朝向正确
        /// </summary>
        bool EvalSkill2Ready()
        {
            if (Skill2CooldownLeft > 0) return false;
            if (_skill2Type == null) return false;
            if (_skill2IsReactive) return false;
            if (_skill2Type == "SummonPersistent") return CountAliveSummons() < _skill2Param1.ToInt();
            if (_skill2TargetAlly) return FindHealTarget() != null;
            if (_skill2IsGapCloser) return EffectiveEnemyDist() > AtkRange;
            if (_skill2Type == "Pull") return FindPullTarget() != null;
            return !IsMoving && EffectiveEnemyDist() <= AtkRange
                && _target != null && IsFacingTarget(_target.Position);
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
                case "ChainLightning":
                {
                    // 连锁闪电：命中主目标后依次链接到最近的敌人，最多Param2个目标
                    if (_target == null) break;
                    int maxChains = _skill2Param2.ToInt(); // 最大连接数（含首个目标）
                    if (maxChains < 1) maxChains = 3;
                    FixedInt chainRange = _skill2Param1; // 连接距离上限

                    var hitTargets = new List<BattleFighter>();
                    hitTargets.Add(_target);
                    ApplyEffect(_target, _skill2Damage, _skill2Buffs, frame);

                    // 首段链接：施法者→首个目标
                    _events.Add(new BattleEvent
                    {
                        Frame    = frame,
                        Type     = BattleEventType.ChainLightningLink,
                        SourceId = PlayerId,
                        TargetId = _target.PlayerId,
                    });

                    // 依次找最近的未命中敌人
                    var current = _target;
                    for (int c = 1; c < maxChains; c++)
                    {
                        BattleFighter nearest = null;
                        FixedInt nearestDist = chainRange + FixedInt.OneVal;

                        for (int e = 0; e < _allFighters.Count; e++)
                        {
                            var f = _allFighters[e];
                            if (f.TeamId == TeamId || f.IsDead || f.IsStealthed) continue;
                            bool alreadyHit = false;
                            for (int h = 0; h < hitTargets.Count; h++)
                                if (hitTargets[h].PlayerId == f.PlayerId) { alreadyHit = true; break; }
                            if (alreadyHit) continue;

                            var dist = FixedVector2.Distance(current.Position, f.Position);
                            if (dist <= chainRange && dist < nearestDist)
                            {
                                nearest = f;
                                nearestDist = dist;
                            }
                        }
                        if (nearest == null) break; // 范围内没有更多敌人
                        hitTargets.Add(nearest);
                        ApplyEffect(nearest, _skill2Damage, _skill2Buffs, frame);

                        // 链接事件：上一目标→当前目标
                        _events.Add(new BattleEvent
                        {
                            Frame    = frame,
                            Type     = BattleEventType.ChainLightningLink,
                            SourceId = current.PlayerId,
                            TargetId = nearest.PlayerId,
                        });
                        current = nearest;
                    }
                    break;
                }
                case "HealPercent":
                {
                    // 按目标最大HP的百分比治疗（Param1=百分比，如30=30%）
                    var t = FindHealTarget();
                    if (t != null)
                    {
                        var healAmt = t.MaxHp * _skill2Param1 / FixedInt.FromInt(100);
                        ApplyHeal(t, healAmt, frame);
                    }
                    break;
                }
                case "Pull":
                {
                    // 拉取技能：优先拉取范围内距离最远的敌人
                    var pullRange = _skill2Param1; // Param1=拉取范围
                    int pullDuration = _skill2Param2.ToInt(); // Param2=拉取持续帧数
                    if (pullDuration < 1) pullDuration = 10;

                    BattleFighter farthest = null;
                    FixedInt farthestDist = FixedInt.Zero;

                    for (int e = 0; e < _allFighters.Count; e++)
                    {
                        var f = _allFighters[e];
                        if (f.TeamId == TeamId || f.IsDead || f.IsStealthed) continue;
                        var dist = FixedVector2.Distance(Position, f.Position);
                        if (dist <= pullRange && dist > farthestDist)
                        {
                            farthest = f;
                            farthestDist = dist;
                        }
                    }

                    if (farthest != null)
                    {
                        // 打断被拉目标的所有动作
                        farthest.IsCasting = false;
                        farthest.IsMoving  = false;
                        farthest.IsFleeing = false;
                        farthest.IsStaggered = true;
                        farthest._staggerFramesLeft = pullDuration + _staggerDuration;

                        // 创建拉取效果
                        var pullSpeed = farthestDist / FixedInt.FromInt(pullDuration) * FixedInt.FromInt(15); // 距离/时间
                        PendingPulls.Add(new PullEffect
                        {
                            SourceId   = PlayerId,
                            TargetId   = farthest.PlayerId,
                            FramesLeft = pullDuration,
                            Speed      = pullSpeed,
                        });

                        // 拉取开始事件
                        _events.Add(new BattleEvent
                        {
                            Frame    = frame,
                            Type     = BattleEventType.PullStart,
                            SourceId = PlayerId,
                            TargetId = farthest.PlayerId,
                        });

                        // 拉取造成伤害
                        if (_skill2Damage > FixedInt.Zero)
                            ApplyDamage(farthest, _skill2Damage, frame);
                    }
                    break;
                }
                case "SummonPersistent":
                {
                    // 召唤永久召唤物，不超过上限
                    int maxCount = _skill2Param1.ToInt();
                    if (CountAliveSummons() < maxCount)
                    {
                        var offset = Facing * FixedInt.FromInt(2);
                        var summonPos = Position - offset;
                        PendingSummons.Add(new SummonRequest
                        {
                            Type     = CharacterType.SkeletonMinion,
                            Position = summonPos,
                            Lifetime = 0, // 无时间限制
                            MaxHp    = _skill2Param2.ToInt(),
                        });
                    }
                    break;
                }
                default:
                {
                    // ── 统一效果施加（Instant等类型）──
                    var targets = SelectTargets(_skill2TargetAlly, _skill2TargetAll, _skill2TargetLowestHp, _skill2TargetRandomCount);
                    for (int i = 0; i < targets.Count; i++)
                        ApplyEffect(targets[i], _skill2Damage, _skill2Buffs, frame);
                    break;
                }
            }

            // 技能释放事件
            var targets2 = SelectTargets(_skill2TargetAlly, _skill2TargetAll, _skill2TargetLowestHp, _skill2TargetRandomCount);
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
                    effectiveDmg = AtkDamage * _passiveParam2 / FixedInt.FromInt(100);
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
                          sourceFighter: this, hitBuffs: _normalAtkBuffs);
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
            else if (_normalAtkType == "PierceLine")
            {
                // 穿刺直线弹射物（闪电）：直线飞行，穿过所有敌人
                if (_target == null) return;
                var dir = _target.Position - Position;
                if (dir.SqrMagnitude == FixedInt.Zero) dir = Facing;

                var proj = new PiercingProjectile();
                proj.Init(PlayerId, TeamId, Position, dir.Normalized,
                          _normalAtkProjectileSpeed, effectiveDmg, AtkRange,
                          FixedInt.FromFloat(0.5f),
                          _normalAtkBuffs, _allFighters, this);
                PendingPiercingProjectiles.Add(proj);

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
                    IntParam = 3, // 穿刺弹射物标记
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
                          _normalAtkParam2,
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
                        MaxHp    = _ultParam2.ToInt(),
                    });
                    break;
                }
                case "AoEZone":
                {
                    // 在目标位置创建持久性AoE区域（闪电云等）
                    var zonePos = _target != null ? _target.Position : Position;
                    PendingLightningClouds.Add(new LightningCloudRequest
                    {
                        SourceId     = PlayerId,
                        TeamId       = TeamId,
                        Position     = zonePos,
                        Radius       = _ultParam1,                    // Param1=半径
                        Damage       = UltDamage,
                        TickInterval = _ultParam2.ToInt(),                    // Param2=伤害间隔帧数
                        Lifetime     = FixedInt.FromFloat(SkillConfigLoader.Get(CharacterConfig.Get(CharType).Ultimate)?.Param3 ?? 60f).ToInt(),
                    });
                    break;
                }
                case "Revive":
                {
                    // 复活一名阵亡的非召唤物友方
                    var dead = FindDeadAlly();
                    if (dead != null)
                    {
                        // 复活：设置HP为最大HP的Param1%
                        var reviveHp = dead.MaxHp * _ultParam1 / FixedInt.FromInt(100);
                        if (reviveHp < FixedInt.OneVal) reviveHp = FixedInt.OneVal;
                        dead.Hp = reviveHp;

                        // 所有技能CD设为刚开始冷却状态
                        dead.AtkCooldownLeft = dead.AtkCooldown;
                        dead.Skill2CooldownLeft = dead._skill2Cooldown;
                        dead.UltCooldownLeft = dead.UltCooldown;

                        // 清除异常状态
                        dead.IsStunned = false;
                        dead.IsStaggered = false;
                        dead.IsFleeing = false;
                        dead.IsMoving = false;
                        dead.IsCasting = false;
                        dead.IsStealthed = false;
                        dead._staggerFramesLeft = 0;
                        dead._stealthFramesLeft = 0;

                        // 复活位置在施法者旁边
                        dead.Position = Position + Facing * FixedInt.FromInt(2);
                        dead.ClampToArena();
                        dead.Facing = Facing;

                        // 复活事件
                        _events.Add(new BattleEvent
                        {
                            Frame    = frame,
                            Type     = BattleEventType.FighterRevive,
                            SourceId = dead.PlayerId,
                            TargetId = PlayerId,
                            IntParam = dead.Hp.ToInt(),
                            PosXRaw  = dead.Position.X.Raw,
                            PosYRaw  = dead.Position.Y.Raw,
                        });

                        // HP变化事件
                        _events.Add(new BattleEvent
                        {
                            Frame    = frame,
                            Type     = BattleEventType.HpChanged,
                            SourceId = dead.PlayerId,
                            IntParam = dead.Hp.ToInt(),
                        });
                    }
                    break;
                }
                case "ReflectShield":
                {
                    // 开启反伤护盾，持续Param1帧，反弹Param2%伤害
                    _reflectFramesLeft = _ultParam1.ToInt();
                    _reflectPercent = _ultParam2 / FixedInt.FromInt(100);
                    break;
                }
                case "DetonateSummons":
                {
                    // 引爆所有己方召唤物（设HP=0触发死亡爆炸），然后召唤满额新的
                    for (int i = 0; i < _allFighters.Count; i++)
                    {
                        var f = _allFighters[i];
                        if (f.Master == this && !f.IsDead)
                        {
                            f.Hp = FixedInt.Zero;
                            _events.Add(new BattleEvent { Frame = frame, Type = BattleEventType.Death, SourceId = f.PlayerId });
                        }
                    }
                    // 召唤3个新骷髅兵
                    int maxCount = 3;
                    int summonHp = _ultParam1.ToInt();
                    for (int s = 0; s < maxCount; s++)
                    {
                        // 扇形分布在身后
                        var offsetAngle = (s - 1); // -1, 0, 1
                        var offset = Facing * FixedInt.FromInt(-2) + new FixedVector2(FixedInt.FromInt(offsetAngle), FixedInt.Zero);
                        PendingSummons.Add(new SummonRequest
                        {
                            Type     = CharacterType.SkeletonMinion,
                            Position = Position + offset,
                            Lifetime = 0,
                            MaxHp    = summonHp,
                        });
                    }
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

            // Stealth/SummonPet/AoEZone/Revive/ReflectShield 不走统一伤害路径
            if (_ultType == "Stealth" || _ultType == "SummonPet" || _ultType == "AoEZone" || _ultType == "Revive" || _ultType == "ReflectShield" || _ultType == "DetonateSummons") return;

            // ── 统一效果施加：根据 TargetTeam/TargetScope 选择目标，Damage正负决定伤害/治疗 ──
            var targets = SelectTargets(_ultTargetAlly, _ultTargetAll, false, _ultTargetRandomCount);
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

            // ── 抗性减伤 ──
            damage = target.ApplyResistance(damage);

            // ── DefUp减伤 ──
            if (target._defReduction > FixedInt.Zero)
            {
                damage = damage * (FixedInt.OneVal - target._defReduction);
                if (damage < FixedInt.OneVal) damage = FixedInt.OneVal; // 至少1点伤害
            }

            // ── 被动：伤害吸收 — 极小概率把伤害转为治疗 ──
            if (target._passiveType == "DamageAbsorb" && target._bt != null
                && target._passiveParam1.ToInt() > 0 && damage > FixedInt.Zero)
            {
                int roll = target._bt.Context.Random.Next(100);
                if (roll < target._passiveParam1.ToInt())
                {
                    // 伤害转为治疗
                    target.Hp = target.Hp + damage;
                    if (target.Hp > target.MaxHp) target.Hp = target.MaxHp;

                    _events.Add(new BattleEvent
                    {
                        Frame    = frame,
                        Type     = BattleEventType.HealApplied,
                        SourceId = target.PlayerId,
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

                    // 伤害吸收后不扣血，仍触发反应和被动
                    if (!target.IsDead)
                    {
                        target.TryReactSkill2(Position, PlayerId, frame, _events);
                        target.TryPassive(Position, PlayerId, frame, _events);
                    }
                    return;
                }
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
            OnDealDamage(damage, target, frame, _events);

            // ── 反伤护盾：将受到伤害的一部分反弹给攻击者 ──
            if (target._reflectFramesLeft > 0 && !IsDead && !_isReflecting)
            {
                var reflectDmg = damage * target._reflectPercent;
                if (reflectDmg >= FixedInt.OneVal)
                {
                    _isReflecting = true;
                    reflectDmg = ApplyResistance(reflectDmg); // 攻击者自身抗性减伤
                    Hp = Hp - reflectDmg;
                    if (Hp < FixedInt.Zero) Hp = FixedInt.Zero;

                    _events.Add(new BattleEvent
                    {
                        Frame    = frame,
                        Type     = BattleEventType.ReflectDamage,
                        SourceId = target.PlayerId,
                        TargetId = PlayerId,
                        IntParam = reflectDmg.ToInt(),
                    });
                    _events.Add(new BattleEvent
                    {
                        Frame    = frame,
                        Type     = BattleEventType.Damage,
                        SourceId = target.PlayerId,
                        TargetId = PlayerId,
                        IntParam = reflectDmg.ToInt(),
                    });
                    _events.Add(new BattleEvent
                    {
                        Frame    = frame,
                        Type     = BattleEventType.HpChanged,
                        SourceId = PlayerId,
                        IntParam = Hp.ToInt(),
                    });
                    if (IsDead)
                    {
                        _events.Add(new BattleEvent { Frame = frame, Type = BattleEventType.Death, SourceId = PlayerId });
                        TryStartSelfRevive();
                    }
                    _isReflecting = false;
                }
            }

            // ── 打断判定：普攻前摇或移动中被伤害可能触发僵直 ──
            if (!target.IsDead)
                target.TryInterrupt();

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
                target.TryStartSelfRevive();
            }
        }

        /// <summary>反伤护盾触发：如果本角色有反伤护盾，将部分伤害反弹给攻击者。供外部弹射物调用。</summary>
        public void TryReflectDamage(BattleFighter attacker, FixedInt damage, int frame, List<BattleEvent> events)
        {
            if (_reflectFramesLeft <= 0 || attacker == null || attacker.IsDead || _isReflecting) return;
            var reflectDmg = damage * _reflectPercent;
            if (reflectDmg < FixedInt.OneVal) return;

            _isReflecting = true;
            // 直接扣除攻击者HP（不递归调用完整ApplyDamage避免复杂循环）
            reflectDmg = attacker.ApplyResistance(reflectDmg);
            attacker.Hp = attacker.Hp - reflectDmg;
            if (attacker.Hp < FixedInt.Zero) attacker.Hp = FixedInt.Zero;

            events.Add(new BattleEvent { Frame = frame, Type = BattleEventType.ReflectDamage, SourceId = PlayerId, TargetId = attacker.PlayerId, IntParam = reflectDmg.ToInt() });
            events.Add(new BattleEvent { Frame = frame, Type = BattleEventType.Damage, SourceId = PlayerId, TargetId = attacker.PlayerId, IntParam = reflectDmg.ToInt() });
            events.Add(new BattleEvent { Frame = frame, Type = BattleEventType.HpChanged, SourceId = attacker.PlayerId, IntParam = attacker.Hp.ToInt() });

            if (attacker.IsDead)
            {
                events.Add(new BattleEvent { Frame = frame, Type = BattleEventType.Death, SourceId = attacker.PlayerId });
                attacker.TryStartSelfRevive();
            }
            _isReflecting = false;
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
            AtkDamage = BaseAtkDamage;
            AtkCooldown = _baseAtkCooldown;
            _atkWindup = _baseAtkWindup;
            _atkRecovery = _baseAtkRecovery;
            IsSlowed = false;
            IsStunned = false;
            IsAtkBuffed = false;
            IsAtkDebuffed = false;
            FixedInt defMultiplier = FixedInt.Zero; // 减伤比例累计
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
                    case BuffType.AtkUp:
                        // Value=0.3 → 攻击力提升30%（乘以1.3）
                        AtkDamage = AtkDamage + BaseAtkDamage * b.Value;
                        IsAtkBuffed = true;
                        break;
                    case BuffType.DefUp:
                        // Value=0.3 → 减少30%受到的伤害（在ApplyDamage中使用）
                        defMultiplier = defMultiplier + b.Value;
                        IsAtkBuffed = true;
                        break;
                    case BuffType.AtkSpeedUp:
                    {
                        // Value=0.3 → CD/前摇/后摇缩短30%（乘以0.7）
                        var factor = FixedInt.OneVal - b.Value;
                        if (factor < FixedInt.FromFloat(0.1f)) factor = FixedInt.FromFloat(0.1f); // 下限10%
                        AtkCooldown = (FixedInt.FromInt(AtkCooldown) * factor).ToInt();
                        _atkWindup  = (FixedInt.FromInt(_atkWindup)  * factor).ToInt();
                        _atkRecovery = (FixedInt.FromInt(_atkRecovery) * factor).ToInt();
                        if (AtkCooldown < 1) AtkCooldown = 1;
                        if (_atkWindup < 0) _atkWindup = 0;
                        IsAtkBuffed = true;
                        break;
                    }
                    case BuffType.AtkSpeedDown:
                    {
                        // Value=0.3 → CD/前摇/后摇增加30%（乘以1.3）
                        var factor = FixedInt.OneVal + b.Value;
                        AtkCooldown = (FixedInt.FromInt(AtkCooldown) * factor).ToInt();
                        _atkWindup  = (FixedInt.FromInt(_atkWindup)  * factor).ToInt();
                        _atkRecovery = (FixedInt.FromInt(_atkRecovery) * factor).ToInt();
                        IsAtkDebuffed = true;
                        break;
                    }
                    case BuffType.AtkDown:
                        // Value=0.3 → 攻击力降低30%（减去BaseAtkDamage*Value）
                        AtkDamage = AtkDamage - BaseAtkDamage * b.Value;
                        if (AtkDamage < FixedInt.OneVal) AtkDamage = FixedInt.OneVal; // 至少1点攻击力
                        IsAtkDebuffed = true;
                        break;
                }
            }
            _defReduction = defMultiplier;
        }

        /// <summary>当前减伤比例（DefUp buff累计值），在ApplyDamage中使用。</summary>
        FixedInt _defReduction;

        /// <summary>
        /// 抗性减伤。所有伤害源在扣血前调用此方法。
        /// resistance=50 → 受50%伤害, >=100 → 免疫, 0 → 全额, -100 → 200%伤害。
        /// </summary>
        public FixedInt ApplyResistance(FixedInt damage)
        {
            if (damage <= FixedInt.Zero) return damage; // 治疗不受抗性影响
            var totalRes = _resistance + BonusResistance;
            if (totalRes >= FixedInt.FromInt(100)) return FixedInt.Zero; // 完全免疫
            var multiplier = FixedInt.FromInt(100) - totalRes; // (100 - resistance)
            damage = damage * multiplier / FixedInt.FromInt(100);
            if (damage < FixedInt.OneVal) damage = FixedInt.OneVal; // 至少1点伤害
            return damage;
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
                    IsDebuff   = bt.IsDebuff,
                });
                _events.Add(new BattleEvent
                {
                    Frame    = frame,
                    Type     = BattleEventType.BuffApplied,
                    SourceId = PlayerId,
                    TargetId = target.PlayerId,
                    IntParam = ((int)bt.Type << 16) | (bt.IsDebuff ? (1 << 15) : 0) | bt.Duration,
                });
            }
        }

        // ═══════════════════════════════════════════════════════
        //  统一目标选择与效果施加
        // ═══════════════════════════════════════════════════════

        /// <summary>根据配置选择技能目标。</summary>
        List<BattleFighter> SelectTargets(bool targetAlly, bool targetAll, bool targetLowestHp, int randomCount = 0)
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
                else if (randomCount > 0)
                {
                    result = GetAliveEnemies();
                    if (result.Count > randomCount)
                    {
                        // Fisher-Yates部分洗牌取前N个
                        for (int i = 0; i < randomCount; i++)
                        {
                            int j = i + _bt.Context.Random.Next(result.Count - i);
                            var tmp = result[i]; result[i] = result[j]; result[j] = tmp;
                        }
                        result.RemoveRange(randomCount, result.Count - randomCount);
                    }
                }
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
                case "ReactLightning":
                {
                    // 受击后有概率发射穿刺闪电反击
                    if (_bt == null || _passiveParam1.ToInt() <= 0) return false;
                    int roll = _bt.Context.Random.Next(100);
                    if (roll >= _passiveParam1.ToInt()) return false;

                    // 向攻击者方向发射穿刺弹射物（使用普攻参数）
                    var atkSkill = SkillConfigLoader.Get(CharacterConfig.Get(CharType).NormalAttack);
                    if (atkSkill == null) return false;
                    var dir = new FixedVector2(FixedInt.FromRaw(damageSourcePos.X.Raw) - Position.X,
                                               FixedInt.FromRaw(damageSourcePos.Y.Raw) - Position.Y);
                    if (dir.SqrMagnitude == FixedInt.Zero) dir = Facing;

                    var proj = new PiercingProjectile();
                    proj.Init(PlayerId, TeamId, Position, dir.Normalized,
                              FixedInt.FromFloat(atkSkill.Param1), AtkDamage, AtkRange,
                              FixedInt.FromFloat(0.5f),
                              _normalAtkBuffs, _allFighters, this);
                    PendingPiercingProjectiles.Add(proj);

                    events.Add(new BattleEvent
                    {
                        Frame    = frame,
                        Type     = BattleEventType.ProjectileSpawn,
                        SourceId = PlayerId,
                        TargetId = sourceId,
                        IntParam = 3, // 穿刺弹射物标记
                        PosXRaw  = Position.X.Raw,
                        PosYRaw  = Position.Y.Raw,
                    });
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

        /// <summary>
        /// 打断判定：普攻前摇中或移动中受伤时有概率触发僵直。
        /// 副技能/大招施法中不会被打断。僵直期间角色无法行动。
        /// </summary>
        public void TryInterrupt()
        {
            if (_staggerDuration <= 0) return;       // 该角色不可被打断（配置为0）
            if (IsStaggered || IsStunned) return;    // 已在控制状态
            if (_bt == null) return;

            // 判定是否处于可打断状态：普攻前摇 或 移动中
            bool inNormalAtkWindup = IsCasting && !_castIsRecovery && !_castIsUlt;
            bool canInterrupt = inNormalAtkWindup || (IsMoving && !IsCasting);
            if (!canInterrupt) return;

            int chance = CharacterConfig.InterruptChance;
            if (chance <= 0) return;
            int roll = _bt.Context.Random.Next(100);
            if (roll >= chance) return;

            // 打断成功 → 进入僵直
            _staggerFramesLeft = _staggerDuration;
            IsStaggered = true;

            // 取消当前动作
            if (IsCasting) { IsCasting = false; _castFramesLeft = 0; }
            IsMoving = false;
            IsFleeing = false;
            _bt.ResetTree();
        }

        /// <summary>造成伤害后触发攻击者被动（UltCDReduce/Lifesteal/OnHitDebuff），召唤物不触发。</summary>
        public void OnDealDamage(FixedInt damage, BattleFighter target, int frame, List<BattleEvent> events)
        {
            if (_bt == null || IsSummon) return;
            if (string.IsNullOrEmpty(_passiveType)) return;

            // 奥术涌流：有概率减少大招CD
            if (_passiveType == "UltCDReduce" && _passiveParam1.ToInt() > 0 && UltCooldownLeft > 0)
            {
                int roll = _bt.Context.Random.Next(100);
                if (roll < _passiveParam1.ToInt())
                {
                    UltCooldownLeft -= _passiveParam2.ToInt();
                    if (UltCooldownLeft < 0) UltCooldownLeft = 0;
                }
            }
            // 生命汲取：有概率回复造成伤害的一定比例HP
            if (_passiveType == "Lifesteal" && _passiveParam1.ToInt() > 0 && !IsDead)
            {
                int roll = _bt.Context.Random.Next(100);
                if (roll < _passiveParam1.ToInt())
                {
                    FixedInt healAmt = damage * _passiveParam2 / FixedInt.FromInt(100);
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
            // OnHitDebuff：普攻命中后对目标施加被动buff（降低攻速等）
            if (_passiveType == "OnHitDebuff" && _passiveBuffs != null && target != null && !target.IsDead)
            {
                ApplyBuffsToTarget(target, _passiveBuffs, frame);
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
            var prevPos = Position;
            Position = Position + Facing * MoveSpeed * dt;
            ClampToArena();

            // 被场地边界卡住（位置几乎没变）时提前结束逃跑
            var moved = (Position - prevPos).SqrMagnitude;
            if (moved < FixedInt.FromRaw(42949672L)) // ~0.01^2 容差
            {
                IsFleeing = false;
                IsMoving = false;
                return BTStatus.Success;
            }

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

        // ═══════════════════════════════════════════════════════════════
        //  自我复活（SelfRevive 被动）
        // ═══════════════════════════════════════════════════════════════

        /// <summary>角色死亡时调用，如果有SelfRevive被动且次数未用完，开始倒计时。返回true表示已开始。</summary>
        public bool TryStartSelfRevive()
        {
            if (_passiveType != "SelfRevive") return false;
            if (_selfRevivesLeft <= FixedInt.Zero) return false;
            _selfRevivesLeft = _selfRevivesLeft - FixedInt.OneVal;
            SelfReviveFramesLeft = _selfReviveDelay.ToInt();
            return true;
        }

        /// <summary>由BattleLogic调用：执行自我复活，满血，所有技能进入冷却。</summary>
        public void ExecuteSelfRevive(int frame)
        {
            Hp = MaxHp;
            SelfReviveFramesLeft = 0;

            // 所有技能进入完整冷却
            AtkCooldownLeft = AtkCooldown;
            Skill2CooldownLeft = _skill2Cooldown;
            UltCooldownLeft = UltCooldown;

            // 清除状态
            IsStaggered = false;
            _staggerFramesLeft = 0;
            IsStunned = false;
            IsFleeing = false;
            IsMoving = false;
            IsCasting = false;
            IsStealthed = false;
            _stealthFramesLeft = 0;
            _reflectFramesLeft = 0;
            _reflectPercent = FixedInt.Zero;

            _events.Add(new BattleEvent
            {
                Frame    = frame,
                Type     = BattleEventType.SelfRevive,
                SourceId = PlayerId,
                IntParam = MaxHp.ToInt(),
            });
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
