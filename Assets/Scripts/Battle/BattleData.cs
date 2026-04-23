namespace FrameSync
{
    // ═══════════════════════════════════════════════════════════════
    //  角色类型
    // ═══════════════════════════════════════════════════════════════

    public enum CharacterType : byte
    {
        None    = 0,
        Warrior = 1, // 剑士（近战）
        Archer  = 2, // 弓手（远程）
        Assassin = 3, // 刺客（近战，眩晕+隐身）
        Mage     = 4, // 法师（远程，火球+召唤）
        Snowman  = 5, // 雪人（召唤物）
        Healer   = 6, // 医疗师（远程辅助，治疗）
        Witch    = 7, // 巫师（远程，魔法球+攻速buff/debuff）
        Barbarian = 8, // 野蛮人（近战，攻击buff/debuff+吸血）
        LightningMage = 9, // 闪电法师（远程，穿刺闪电+连锁+闪电云）
    }

    /// <summary>职业类型（用于索敌优先级）。</summary>
    public enum Profession : byte
    {
        None     = 0,
        Warrior  = 1, // 战士
        Mage     = 2, // 法师
        Archer   = 3, // 射手
        Assassin = 4, // 刺客
        Support  = 5, // 辅助
    }

    // ═══════════════════════════════════════════════════════════════
    //  战斗事件类型 — 通知队列条目
    // ═══════════════════════════════════════════════════════════════

    public enum BattleEventType : byte
    {
        CharSelected,   // 角色选定           SourceId=玩家, IntParam=CharacterType
        BattleStart,    // 战斗开始
        Move,           // 位置更新           SourceId=角色, PosXRaw/PosYRaw=位置
        NormalAttack,   // 普通攻击           SourceId→TargetId, IntParam=伤害
        UltimateCast,   // 释放大招           SourceId→TargetId, IntParam=伤害
        Damage,         // 受到伤害           TargetId=受击者, IntParam=伤害值
        HpChanged,      // 血量变化           SourceId=角色, IntParam=当前HP
        Death,          // 角色阵亡           SourceId=阵亡者
        BattleEnd,      // 战斗结束           IntParam=胜者ID

        // ── 显示层完全隔离所需 ──
        FighterSpawn,   // 角色创建     SourceId=玩家, TargetId=(byte)CharType,
                        //              IntParam=MaxHp, PosXRaw/PosYRaw=初始位置
        StateChanged,   // 状态变化     SourceId=角色, IntParam=状态位掩码(bit0=Moving,bit1=Fleeing,bit2=Casting,bit3=CastUlt)
        PhaseChanged,   // 阶段变化     IntParam=(int)BattlePhase
        CooldownUpdate, // CD更新       SourceId=角色, IntParam=普攻CD剩余,
                        //              PosXRaw=大招CD剩余, PosYRaw=(普攻总CD<<32)|大招总CD — 仅首帧发
        ProjectileSpawn, // 弹射物创建  SourceId=发射者, TargetId=目标, PosXRaw/PosYRaw=初始位置
        ProjectileHit,   // 弹射物命中  SourceId=发射者, TargetId=目标, IntParam=伤害
        Skill2Cast,      // 副技能释放  SourceId=释放者, TargetId=目标, IntParam=伤害, PosXRaw/PosYRaw=释放者位置
        AoEExplosion,    // AoE爆炸     SourceId=发射者, PosXRaw/PosYRaw=爆炸位置, IntParam=爆炸半径
        BuffApplied,     // Buff施加     SourceId=施加者, TargetId=目标, IntParam=持续帧数
        HealApplied,     // 治疗         SourceId=治疗者, TargetId=目标, IntParam=治疗量
        ChainLightningLink, // 连锁闪电链接  SourceId=上一个目标(或施法者), TargetId=下一个目标
        LightningCloudSpawn, // 闪电云生成  SourceId=施法者, PosXRaw/PosYRaw=位置, IntParam=半径(Q16.16编码)
    }

    // ═══════════════════════════════════════════════════════════════
    //  战斗事件 — 每帧产生，渲染层消费
    // ═══════════════════════════════════════════════════════════════

    public struct BattleEvent
    {
        public int             Frame;
        public BattleEventType Type;
        public byte            SourceId;
        public byte            TargetId;
        public int             IntParam;
        public long            PosXRaw;  // FixedInt.Raw
        public long            PosYRaw;  // FixedInt.Raw

        public override string ToString()
        {
            return Type switch
            {
                BattleEventType.CharSelected  => $"[F{Frame}] P{SourceId} 选择 {(CharacterType)IntParam}",
                BattleEventType.BattleStart   => $"[F{Frame}] ===== 战斗开始 =====",
                BattleEventType.Move          => $"[F{Frame}] P{SourceId} → ({FixedInt.FromRaw(PosXRaw).ToFloat():F1},{FixedInt.FromRaw(PosYRaw).ToFloat():F1})",
                BattleEventType.NormalAttack  => $"[F{Frame}] P{SourceId} 普攻 → P{TargetId} ({IntParam} dmg)",
                BattleEventType.UltimateCast  => $"[F{Frame}] P{SourceId} ★大招★ → P{TargetId} ({IntParam} dmg)",
                BattleEventType.Damage        => $"[F{Frame}] P{TargetId} 受击 -{IntParam}",
                BattleEventType.HpChanged     => $"[F{Frame}] P{SourceId} HP={IntParam}",
                BattleEventType.Death         => $"[F{Frame}] P{SourceId} 阵亡",
                BattleEventType.BattleEnd     => $"[F{Frame}] ===== P{IntParam} 获胜 =====",
                BattleEventType.FighterSpawn  => $"[F{Frame}] P{SourceId} 出生 CharType={(CharacterType)TargetId} HP={IntParam}",
                BattleEventType.StateChanged  => $"[F{Frame}] P{SourceId} 状态={IntParam}",
                BattleEventType.PhaseChanged  => $"[F{Frame}] 阶段={IntParam}",
                BattleEventType.CooldownUpdate => $"[F{Frame}] P{SourceId} AtkCD={IntParam} UltCD={PosXRaw}",
                BattleEventType.ProjectileSpawn => $"[F{Frame}] P{SourceId}→P{TargetId} 弹射物生成",
                BattleEventType.ProjectileHit   => $"[F{Frame}] P{SourceId}→P{TargetId} 弹射物命中 {IntParam}dmg",
                BattleEventType.Skill2Cast       => $"[F{Frame}] P{SourceId} 副技能 → P{TargetId} ({IntParam}dmg)",
                BattleEventType.AoEExplosion     => $"[F{Frame}] P{SourceId} AoE爆炸 r={IntParam}",
                BattleEventType.BuffApplied      => $"[F{Frame}] P{SourceId}→P{TargetId} 施加Buff {IntParam}帧",
                BattleEventType.HealApplied      => $"[F{Frame}] P{SourceId}→P{TargetId} 治疗 +{IntParam}",
                BattleEventType.ChainLightningLink => $"[F{Frame}] 连锁闪电 P{SourceId}→P{TargetId}",
                BattleEventType.LightningCloudSpawn => $"[F{Frame}] P{SourceId} 闪电云生成",
                _                             => $"[F{Frame}] {Type}",
            };
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  技能配置 — 独立定义，角色通过ID引用
    // ═══════════════════════════════════════════════════════════════

    /// <summary>技能配置（JSON 反序列化用）。</summary>
    [System.Serializable]
    public class SkillConfig
    {
        public string Id;
        public string Name;
        public string Type;      // 投递方式: MeleeAttack, RangedAttack, AoEProjectile, Instant, Blink, DashStrike, KnockbackArrow, Stealth, ReactBlink, SummonPet
        public int    Damage;    // 正数=伤害，负数=治疗
        public int    Range;
        public int    Cooldown;
        public int    Windup;
        public int    Recovery;
        public int    Param1;    // 类型相关参数1
        public int    Param2;    // 类型相关参数2
        public int    Param3;    // 类型相关参数3
        public string TargetTeam;  // "Enemy" / "Ally" — 作用于敌方还是友方
        public string TargetScope; // "Single" / "All" / "LowestHp" — 单体/全体/血量最低
        public BuffConfig[] Buffs; // 技能附加的buff列表（减速、眩晕等）
    }

    /// <summary>技能配置加载器。</summary>
    public static class SkillConfigLoader
    {
        static System.Collections.Generic.Dictionary<string, SkillConfig> _skills;
        static bool _loaded;

        static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            _skills = new System.Collections.Generic.Dictionary<string, SkillConfig>();
            CharacterConfig.EnsureSkillsLoaded(_skills);
        }

        public static SkillConfig Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            EnsureLoaded();
            return _skills.TryGetValue(id, out var cfg) ? cfg : null;
        }

        public static void Reload() => _loaded = false;
    }

    // ═══════════════════════════════════════════════════════════════
    //  角色配置表 — 从 Resources/CharacterConfig.json 加载
    // ═══════════════════════════════════════════════════════════════

    /// <summary>角色基础数值（JSON 反序列化用）。技能通过ID引用。</summary>
    [System.Serializable]
    public class CharacterStats
    {
        public int    MaxHp;
        public int    MoveSpeed;
        public int    TurnSpeed;
        public float  CollisionRadius;
        public int    StaggerDuration;   // 被打断后僵直帧数（0=不可打断）
        public string HeadIcon;         // 头像精灵路径（Resources下）
        public string Profession;       // 职业名（Warrior/Mage/Archer/Assassin/Support）
        public string Passive;          // 被动技能ID
        public string NormalAttack;
        public string Skill2;
        public string Ultimate;
        public int    Resistance = 50;  // 抗性（默认50，百分比减伤；>=100免疫，0=全额，负值=增伤）
    }

    /// <summary>布阵位置（JSON 反序列化用）。</summary>
    [System.Serializable]
    public class FormationPos
    {
        public int X;
        public int Y;
    }

    /// <summary>职业索敌优先级（JSON 反序列化用）。</summary>
    [System.Serializable]
    public class TargetPriorityEntry
    {
        public string Profession;   // 本职业名
        public string[] Priority;   // 优先索敌职业列表（从高到低）
    }

    /// <summary>
    /// 从 Resources/CharacterConfig.json 读取角色数值和技能配置。
    /// </summary>
    public static class CharacterConfig
    {
        static CharacterStats _warrior;
        static CharacterStats _archer;
        static CharacterStats _assassin;
        static CharacterStats _mage;
        static CharacterStats _snowman;
        static CharacterStats _healer;
        static CharacterStats _witch;
        static CharacterStats _barbarian;
        static CharacterStats _lightningMage;
        static SkillConfig[] _skillsArray;
        static FormationPos[] _formation;
        static TargetPriorityEntry[] _targetPriority;
        static System.Collections.Generic.Dictionary<Profession, Profession[]> _priorityMap;
        static int _teamSize = 1;
        static bool _loaded;

        [System.Serializable]
        class ConfigRoot
        {
            public int ArenaHalf = 10;
            public int TeamSize = 1;
            public int InterruptChance = 25;
            public FormationPos[] Formation;
            public TargetPriorityEntry[] TargetPriority;
            public SkillConfig[] Skills;
            public CharacterStats Warrior;
            public CharacterStats Archer;
            public CharacterStats Assassin;
            public CharacterStats Mage;
            public CharacterStats Snowman;
            public CharacterStats Healer;
            public CharacterStats Witch;
            public CharacterStats Barbarian;
            public CharacterStats LightningMage;
        }

        static int _arenaHalf = 10;
        static int _interruptChance = 25;

        static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;

            var asset = UnityEngine.Resources.Load<UnityEngine.TextAsset>("CharacterConfig");
            if (asset != null)
            {
                var root = UnityEngine.JsonUtility.FromJson<ConfigRoot>(asset.text);
                _warrior     = root.Warrior;
                _archer      = root.Archer;
                _assassin    = root.Assassin;
                _mage        = root.Mage;
                _snowman     = root.Snowman;
                _healer      = root.Healer;
                _witch       = root.Witch;
                _barbarian   = root.Barbarian;
                _lightningMage = root.LightningMage;
                _skillsArray = root.Skills;
                _formation   = root.Formation;
                _teamSize    = root.TeamSize > 0 ? root.TeamSize : 1;
                _arenaHalf   = root.ArenaHalf > 0 ? root.ArenaHalf : 10;
                _interruptChance = root.InterruptChance;
                _targetPriority = root.TargetPriority;
                BuildPriorityMap();
            }
            else
            {
                UnityEngine.Debug.LogError("[CharacterConfig] Resources/CharacterConfig.json not found");
                _warrior  = new CharacterStats { MaxHp=1000, MoveSpeed=3, TurnSpeed=8, CollisionRadius=0.5f, NormalAttack="warrior_atk", Skill2="stun_strike", Ultimate="dash_slash" };
                _archer   = new CharacterStats { MaxHp=800, MoveSpeed=5, TurnSpeed=10, CollisionRadius=0.5f, Passive="flee_on_hit", NormalAttack="archer_atk", Skill2="knockback_arrow", Ultimate="arrow_rain" };
                _assassin = new CharacterStats { MaxHp=1000, MoveSpeed=4, TurnSpeed=10, CollisionRadius=0.5f, NormalAttack="assassin_atk", Skill2="blink", Ultimate="stealth" };
                _mage     = new CharacterStats { MaxHp=1200, MoveSpeed=2, TurnSpeed=10, CollisionRadius=0.5f, NormalAttack="mage_fireball", Skill2="react_blink", Ultimate="summon_snowman" };
                _snowman  = new CharacterStats { MaxHp=500, MoveSpeed=1, TurnSpeed=10, CollisionRadius=0.5f, NormalAttack="snowman_iceball" };
                _healer   = new CharacterStats { MaxHp=1300, MoveSpeed=2, TurnSpeed=10, CollisionRadius=0.5f, Profession="Support", NormalAttack="healer_orb", Skill2="single_heal", Ultimate="group_heal" };
                _witch    = new CharacterStats { MaxHp=1100, MoveSpeed=2, TurnSpeed=10, CollisionRadius=0.5f, Profession="Mage", Passive="on_hit_atk_slow", NormalAttack="witch_orb", Skill2="witch_atk_speed_up", Ultimate="witch_mass_slow" };
                _barbarian = new CharacterStats { MaxHp=1800, MoveSpeed=1, TurnSpeed=8, CollisionRadius=0.5f, Profession="Warrior", Passive="barbarian_lifesteal", NormalAttack="barbarian_atk", Skill2="barbarian_atk_up", Ultimate="barbarian_mass_atk_down" };
                _lightningMage = new CharacterStats { MaxHp=1000, MoveSpeed=2, TurnSpeed=10, CollisionRadius=0.5f, Profession="Mage", Passive="react_lightning", NormalAttack="lightning_bolt", Skill2="chain_lightning", Ultimate="lightning_storm" };
                _skillsArray = System.Array.Empty<SkillConfig>();
                _formation = new[] { new FormationPos { X = -5, Y = 0 } };
                _teamSize = 1;
            }
        }

        /// <summary>供 SkillConfigLoader 调用，填充技能字典。</summary>
        internal static void EnsureSkillsLoaded(System.Collections.Generic.Dictionary<string, SkillConfig> dict)
        {
            EnsureLoaded();
            if (_skillsArray != null)
                foreach (var s in _skillsArray)
                    if (!string.IsNullOrEmpty(s.Id))
                        dict[s.Id] = s;
        }

        public static CharacterStats Get(CharacterType type)
        {
            EnsureLoaded();
            return type switch
            {
                CharacterType.Warrior  => _warrior,
                CharacterType.Archer   => _archer,
                CharacterType.Assassin => _assassin,
                CharacterType.Mage     => _mage,
                CharacterType.Snowman  => _snowman,
                CharacterType.Healer   => _healer,
                CharacterType.Witch    => _witch,
                CharacterType.Barbarian => _barbarian,
                CharacterType.LightningMage => _lightningMage,
                _ => _warrior,
            };
        }

        public static int ArenaHalf
        {
            get { EnsureLoaded(); return _arenaHalf; }
        }

        public static int TeamSize
        {
            get { EnsureLoaded(); return _teamSize; }
        }

        public static int InterruptChance
        {
            get { EnsureLoaded(); return _interruptChance; }
        }

        public static FormationPos[] Formation
        {
            get { EnsureLoaded(); return _formation ?? System.Array.Empty<FormationPos>(); }
        }

        /// <summary>解析职业字符串。</summary>
        public static Profession ParseProfession(string name)
        {
            if (string.IsNullOrEmpty(name)) return Profession.None;
            return name switch
            {
                "Warrior"  => Profession.Warrior,
                "Mage"     => Profession.Mage,
                "Archer"   => Profession.Archer,
                "Assassin" => Profession.Assassin,
                "Support"  => Profession.Support,
                _          => Profession.None,
            };
        }

        /// <summary>获取指定职业的索敌优先级列表（从高到低）。</summary>
        public static Profession[] GetTargetPriority(Profession prof)
        {
            EnsureLoaded();
            if (_priorityMap != null && _priorityMap.TryGetValue(prof, out var list))
                return list;
            return System.Array.Empty<Profession>();
        }

        static void BuildPriorityMap()
        {
            _priorityMap = new System.Collections.Generic.Dictionary<Profession, Profession[]>();
            if (_targetPriority == null) return;
            for (int i = 0; i < _targetPriority.Length; i++)
            {
                var entry = _targetPriority[i];
                var prof = ParseProfession(entry.Profession);
                if (prof == Profession.None) continue;
                if (entry.Priority == null) { _priorityMap[prof] = System.Array.Empty<Profession>(); continue; }
                var arr = new Profession[entry.Priority.Length];
                for (int j = 0; j < entry.Priority.Length; j++)
                    arr[j] = ParseProfession(entry.Priority[j]);
                _priorityMap[prof] = arr;
            }
        }

        public static void Reload() { _loaded = false; SkillConfigLoader.Reload(); }
    }

    // ═══════════════════════════════════════════════════════════════
    //  召唤物请求
    // ═══════════════════════════════════════════════════════════════

    public struct SummonRequest
    {
        public CharacterType Type;
        public FixedVector2 Position;
        public int Lifetime;
        public int MaxHp;
    }

    // ═══════════════════════════════════════════════════════════════
    //  行为树配置 — 从 Resources/BehaviorTreeConfig.json 加载
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 行为树节点配置（JSON 反序列化用，支持递归嵌套）。
    /// type: selector / sequence / condition / action
    /// </summary>
    [System.Serializable]
    public class BTNodeConfig
    {
        public string   type;       // selector, sequence, condition, action
        public string[] check;      // 条件名数组（AND逻辑），仅 condition 节点
        public string   run;        // 动作名，仅 action 节点
        public BTNodeConfig[] children; // 子节点，仅 selector/sequence 节点
    }

    /// <summary>
    /// 行为树配置加载器 — 首次访问自动加载，运行期间缓存。
    /// </summary>
    public static class BTConfigLoader
    {
        static BTNodeConfig _defaultConfig;
        static bool _loaded;

        [System.Serializable]
        class BTConfigRoot
        {
            public BTNodeConfig Default;
        }

        static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;

            var asset = UnityEngine.Resources.Load<UnityEngine.TextAsset>("BehaviorTreeConfig");
            if (asset != null)
            {
                var root = UnityEngine.JsonUtility.FromJson<BTConfigRoot>(asset.text);
                _defaultConfig = root.Default;
            }
            else
            {
                UnityEngine.Debug.LogError("[BTConfigLoader] Resources/BehaviorTreeConfig.json not found!");
            }
        }

        /// <summary>获取统一行为树配置（所有角色共用）。</summary>
        public static BTNodeConfig Get(CharacterType type)
        {
            EnsureLoaded();
            return _defaultConfig;
        }

        /// <summary>编辑器或热重载时可调用，强制下次访问重新读取 JSON。</summary>
        public static void Reload() => _loaded = false;
    }
}
