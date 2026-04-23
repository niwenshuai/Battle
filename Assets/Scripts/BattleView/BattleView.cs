using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using FrameSync; // 仅用于 BattleEvent / BattleEventType

/// <summary>
/// 纯事件驱动的战斗显示层。
///
/// 完全不持有 BattleFighter / BattleLogic 的引用，
/// 所有数据仅通过事件队列获取，自建 ViewFighter 模型。
///
/// 接入方式：
///   view.EventSource = logic.EventQueue;
///   view.ClearEventsCallback = () => logic.ClearEvents();
///   view.LocalPlayerId = logic.LocalPlayerId;
/// </summary>
public class BattleView : MonoBehaviour
{
    // ════════════════════════════════════════════════════════════
    //  显示层内部数据模型（从事件重建，不引用逻辑层类型）
    // ════════════════════════════════════════════════════════════

    class ViewFighter
    {
        public byte PlayerId;
        public byte CharType;        // 1=Warrior, 2=Archer, 3=Assassin, 4=Mage, 5=Snowman, 6=Healer
        public byte TeamId;          // 1或2
        public int MaxHp;
        public int CurrentHp;
        public Vector3 TargetPos;
        public Vector3 DisplayPos;
        public float TargetYaw;   // 目标朝向角度（Y轴旋转）
        public bool IsMoving;
        public bool IsFleeing;
        public bool IsCasting;
        public bool IsCastingUlt;  // true=大招前后摇, false=普攻前后摇
        public bool IsDead;
        public bool IsStunned;
        public bool IsStealthed;
        public bool IsSlowed;
        public bool IsStaggered;
        public bool IsAtkBuffed;
        public bool IsAtkDebuffed;

        // 渲染对象
        public GameObject Go;
        public SpriteRenderer HeadSR;   // 头像精灵
        public SpriteRenderer TeamSR;   // 阵营颜色精灵
        public SpriteRenderer StateSR;  // 状态闪烁精灵
        public Color BaseColor;

        // 冷却信息（从 CooldownUpdate 事件获取）
        public int AtkCooldownLeft;
        public int AtkCooldownTotal;  // 首帧从 PosYRaw 高32位获取
        public int UltCooldownLeft;
        public int UltCooldownTotal;  // 首帧从 PosYRaw 低32位获取

        // 攻击状态（从 NormalAttack/UltimateCast 事件推断）
        public float AttackFlashTimer;   // >0 表示正在攻击表现中
        public float UltFlashTimer;      // >0 表示大招表现中
        public float Skill2FlashTimer;   // >0 表示副技能表现中
        public int Skill2CooldownLeft;
        public int Skill2CooldownTotal;
        public string CharName;     // 角色名 (Warrior/Archer/Assassin)
        public string PassiveName;  // 被动技能显示名
        public string Skill2Name;   // 副技能显示名
        public string Skill2Type;   // 副技能类型（用于视觉效果）
        public bool IsAttacking => AttackFlashTimer > 0;
        public bool IsUlting => UltFlashTimer > 0;

        public static Vector3 RawToWorld(long rawX, long rawY)
        {
            const double scale = 1.0 / 4294967296.0; // Q32.32
            return new Vector3((float)(rawX * scale), 0.5f, (float)(rawY * scale));
        }
    }

    /// <summary>弹射物显示对象（弓箭用圆球表示）。</summary>
    class ViewProjectile
    {
        public byte SourceId;
        public byte TargetId;
        public GameObject Go;
        public float Speed;  // 显示层飞行速度（世界单位/秒）
    }

    /// <summary>穿刺弹射物显示对象（直线飞行，不追踪目标）。</summary>
    class ViewPiercingProjectile
    {
        public GameObject Go;
        public Vector3 Direction;  // 世界空间飞行方向
        public float Speed;
        public float MaxLifetime;  // 最大存活秒数
        public float Elapsed;
    }

    /// <summary>闪电云显示对象（持久性AoE区域）。</summary>
    class ViewLightningCloud
    {
        public GameObject Go;
        public float Lifetime;     // 剩余秒数
        public float PulseTimer;   // 脉冲动画计时
    }

    /// <summary>连锁闪电连接线显示对象。</summary>
    class ViewChainLink
    {
        public GameObject Go;
        public float Lifetime;     // 剩余秒数
    }

    // ═══ 运行时生成的默认圆形精灵 ═══
    static Sprite _defaultCircle;
    static Sprite DefaultCircle
    {
        get
        {
            if (_defaultCircle != null) return _defaultCircle;
            int sz = 64;
            var tex = new Texture2D(sz, sz, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            float c = sz * 0.5f, r = c - 1;
            for (int y = 0; y < sz; y++)
                for (int x = 0; x < sz; x++)
                {
                    float dx = x - c, dy = y - c;
                    tex.SetPixel(x, y, Mathf.Sqrt(dx * dx + dy * dy) <= r ? Color.white : Color.clear);
                }
            tex.Apply();
            _defaultCircle = Sprite.Create(tex, new Rect(0, 0, sz, sz), new Vector2(0.5f, 0.5f), 64);
            return _defaultCircle;
        }
    }

    GameObject _rolePrefab;

    GameObject CreateFighterGO()
    {
        if (_rolePrefab == null)
            _rolePrefab = Resources.Load<GameObject>("role");

        if (_rolePrefab != null)
            return Instantiate(_rolePrefab);

        // Fallback: 代码创建层级
        var root = new GameObject();

        var team = new GameObject("team");
        team.transform.SetParent(root.transform);
        var teamSR = team.AddComponent<SpriteRenderer>();
        teamSR.sprite = DefaultCircle;
        teamSR.sortingOrder = 0;
        team.transform.localScale = new Vector3(1.3f, 1.3f, 1f);

        var head = new GameObject("head");
        head.transform.SetParent(root.transform);
        var headSR = head.AddComponent<SpriteRenderer>();
        headSR.sprite = DefaultCircle;
        headSR.sortingOrder = 1;

        var state = new GameObject("state");
        state.transform.SetParent(root.transform);
        var stateSR = state.AddComponent<SpriteRenderer>();
        stateSR.sprite = DefaultCircle;
        stateSR.sortingOrder = 2;
        stateSR.color = new Color(1f, 1f, 1f, 0f);

        return root;
    }

    // ════════════════════════════════════════════════════════════
    //  外部接口（唯一与逻辑层的桥梁）
    // ════════════════════════════════════════════════════════════

    /// <summary>事件源列表引用。</summary>
    [HideInInspector] public List<BattleEvent> EventSource;

    /// <summary>消费完事件后的清空回调。</summary>
    [HideInInspector] public System.Action ClearEventsCallback;

    /// <summary>本地玩家 ID（用于 UI 标记）。</summary>
    [HideInInspector] public byte LocalPlayerId;

    // ════════════════════════════════════════════════════════════
    //  内部状态（全部从事件重建）
    // ════════════════════════════════════════════════════════════

    ViewFighter[] _fighters = new ViewFighter[64]; // id-indexed, max 63 fighters
    readonly List<ViewProjectile> _viewProjectiles = new();
    readonly List<ViewPiercingProjectile> _viewPiercingProjectiles = new();
    readonly List<ViewLightningCloud> _viewLightningClouds = new();
    readonly List<ViewChainLink> _viewChainLinks = new();
    int _phase;                  // 0=Selecting, 1=Fighting, 2=Ended
    int _winnerId;
    readonly List<List<byte>> _teamSelections = new() { null, new(), new() }; // 1-based teams

    // 用于显示连接/帧信息（由 BattleEntry 赋值）
    [HideInInspector] public string ConnectionInfo = "";
    [HideInInspector] public int CurrentFrame;

    /// <summary>是否使用外部选角UI（设为true时跳过IMGUI选角界面）。</summary>
    [HideInInspector] public bool UseExternalSelectUI;

    // ════════════════════════════════════════════════════════════
    //  每帧更新
    // ════════════════════════════════════════════════════════════

    void LateUpdate()
    {
        // 1. 消费事件
        if (EventSource != null && EventSource.Count > 0)
        {
            foreach (var evt in EventSource)
                HandleEvent(evt);
            ClearEventsCallback?.Invoke();
        }

        // 2. 平滑插值（所有数据来自事件）
        for (int i = 1; i < _fighters.Length; i++)
        {
            var vf = _fighters[i];
            if (vf == null || vf.Go == null || vf.IsDead) continue;

            vf.DisplayPos = Vector3.Lerp(vf.DisplayPos, vf.TargetPos, 12f * Time.deltaTime);
            vf.Go.transform.position = vf.DisplayPos;

            // Billboard: 始终面向摄像机
            if (Camera.main != null)
                vf.Go.transform.rotation = Camera.main.transform.rotation;

            // 移动时压扁
            float scaleY = vf.IsMoving ? 0.7f : 1f;
            var s = vf.Go.transform.localScale;
            s.y = Mathf.Lerp(s.y, scaleY, 8f * Time.deltaTime);
            vf.Go.transform.localScale = s;

            // 逃跑时闪红
            if (vf.IsFleeing && vf.HeadSR != null)
            {
                float t = Mathf.PingPong(Time.time * 6f, 1f);
                vf.HeadSR.color = Color.Lerp(vf.BaseColor, new Color(1f, 0.5f, 0f), t * 0.5f);
            }

            // 眩晕时变灰
            if (vf.IsStunned && vf.HeadSR != null)
            {
                vf.HeadSR.color = Color.Lerp(Color.gray, Color.white, Mathf.PingPong(Time.time * 3f, 1f) * 0.3f);
            }

            // 僵直时闪烁红白
            if (vf.IsStaggered && !vf.IsStunned && vf.HeadSR != null)
            {
                float t = Mathf.PingPong(Time.time * 8f, 1f);
                vf.HeadSR.color = Color.Lerp(vf.BaseColor, new Color(1f, 0.2f, 0.2f), t * 0.6f);
            }

            // 隐身时闪烁半透明
            if (vf.IsStealthed && vf.HeadSR != null)
            {
                float alpha = 0.15f + Mathf.PingPong(Time.time * 4f, 1f) * 0.2f;
                var c = vf.BaseColor;
                vf.HeadSR.color = new Color(c.r, c.g, c.b, alpha);
            }

            // 减速时蓝色着色
            if (vf.IsSlowed && !vf.IsStealthed && !vf.IsStunned && !vf.IsFleeing && vf.HeadSR != null)
            {
                vf.HeadSR.color = Color.Lerp(vf.BaseColor, new Color(0.3f, 0.5f, 1f), 0.4f);
            }

            // 攻击状态计时器衰减
            if (vf.AttackFlashTimer > 0) vf.AttackFlashTimer -= Time.deltaTime;
            if (vf.UltFlashTimer > 0)    vf.UltFlashTimer -= Time.deltaTime;
            if (vf.Skill2FlashTimer > 0) vf.Skill2FlashTimer -= Time.deltaTime;
        }

        // 3. 弹射物飞行
        for (int p = _viewProjectiles.Count - 1; p >= 0; p--)
        {
            var vp = _viewProjectiles[p];
            if (vp.Go == null) { _viewProjectiles.RemoveAt(p); continue; }

            var target = _fighters[vp.TargetId];
            if (target == null) { Destroy(vp.Go); _viewProjectiles.RemoveAt(p); continue; }

            Vector3 dest = target.DisplayPos;
            Vector3 dir = dest - vp.Go.transform.position;
            float dist = dir.magnitude;
            float step = vp.Speed * Time.deltaTime;

            if (dist <= step)
            {
                // 到达目标
                Destroy(vp.Go);
                _viewProjectiles.RemoveAt(p);
            }
            else
            {
                vp.Go.transform.position += dir.normalized * step;
            }
        }

        // 4. 穿刺弹射物飞行（直线，不追踪）
        for (int p = _viewPiercingProjectiles.Count - 1; p >= 0; p--)
        {
            var vp = _viewPiercingProjectiles[p];
            if (vp.Go == null) { _viewPiercingProjectiles.RemoveAt(p); continue; }

            vp.Elapsed += Time.deltaTime;
            if (vp.Elapsed >= vp.MaxLifetime)
            {
                Destroy(vp.Go);
                _viewPiercingProjectiles.RemoveAt(p);
                continue;
            }

            vp.Go.transform.position += vp.Direction * vp.Speed * Time.deltaTime;
        }

        // 5. 闪电云持续显示
        for (int p = _viewLightningClouds.Count - 1; p >= 0; p--)
        {
            var vc = _viewLightningClouds[p];
            if (vc.Go == null) { _viewLightningClouds.RemoveAt(p); continue; }

            vc.Lifetime -= Time.deltaTime;
            if (vc.Lifetime <= 0f)
            {
                Destroy(vc.Go);
                _viewLightningClouds.RemoveAt(p);
                continue;
            }

            // 脉冲缩放动画
            vc.PulseTimer += Time.deltaTime;
            float pulse = 1f + 0.08f * Mathf.Sin(vc.PulseTimer * 4f);
            var s = vc.Go.transform.localScale;
            vc.Go.transform.localScale = new Vector3(s.x, pulse * 0.15f, s.z);

            // 渐隐（最后1秒开始）
            if (vc.Lifetime < 1f)
            {
                var rend = vc.Go.GetComponent<Renderer>();
                if (rend != null)
                {
                    var c = rend.material.color;
                    c.a = Mathf.Clamp01(vc.Lifetime);
                    rend.material.color = c;
                }
            }
        }

        // 6. 连锁闪电连接线衰减
        for (int p = _viewChainLinks.Count - 1; p >= 0; p--)
        {
            var vl = _viewChainLinks[p];
            if (vl.Go == null) { _viewChainLinks.RemoveAt(p); continue; }

            vl.Lifetime -= Time.deltaTime;
            if (vl.Lifetime <= 0f)
            {
                Destroy(vl.Go);
                _viewChainLinks.RemoveAt(p);
                continue;
            }

            // 渐隐
            var lr = vl.Go.GetComponent<LineRenderer>();
            if (lr != null)
            {
                float alpha = Mathf.Clamp01(vl.Lifetime / 0.5f);
                lr.startColor = new Color(0.4f, 0.7f, 1f, alpha);
                lr.endColor = new Color(0.8f, 0.9f, 1f, alpha);
            }
        }
    }

    // ════════════════════════════════════════════════════════════
    //  事件处理（唯一的数据来源）
    // ════════════════════════════════════════════════════════════

    void HandleEvent(BattleEvent evt)
    {
        switch (evt.Type)
        {
            case BattleEventType.PhaseChanged:
                _phase = evt.IntParam;
                break;

            case BattleEventType.CharSelected:
                if (evt.SourceId >= 1 && evt.SourceId <= 2)
                    _teamSelections[evt.SourceId].Add((byte)evt.IntParam);
                break;

            case BattleEventType.FighterSpawn:
                OnFighterSpawn(evt);
                break;

            case BattleEventType.BattleStart:
                // FighterSpawn 已处理创建
                break;

            case BattleEventType.Move:
                OnMove(evt);
                break;

            case BattleEventType.HpChanged:
                OnHpChanged(evt);
                break;

            case BattleEventType.StateChanged:
                OnStateChanged(evt);
                break;

            case BattleEventType.NormalAttack:
                OnNormalAttack(evt);
                break;

            case BattleEventType.UltimateCast:
                OnUltimateCast(evt);
                break;

            case BattleEventType.Damage:
                OnDamage(evt);
                break;

            case BattleEventType.Death:
                OnDeath(evt);
                break;

            case BattleEventType.BattleEnd:
                _winnerId = evt.IntParam;
                break;

            case BattleEventType.CooldownUpdate:
                OnCooldownUpdate(evt);
                break;

            case BattleEventType.ProjectileSpawn:
                OnProjectileSpawn(evt);
                break;

            case BattleEventType.ProjectileHit:
                OnProjectileHit(evt);
                break;

            case BattleEventType.Skill2Cast:
                OnSkill2Cast(evt);
                break;

            case BattleEventType.AoEExplosion:
                OnAoEExplosion(evt);
                break;

            case BattleEventType.BuffApplied:
                // Buff视觉由 StateChanged 处理
                break;

            case BattleEventType.HealApplied:
                OnHealApplied(evt);
                break;

            case BattleEventType.ChainLightningLink:
                OnChainLightningLink(evt);
                break;

            case BattleEventType.LightningCloudSpawn:
                OnLightningCloudSpawn(evt);
                break;
            case BattleEventType.FighterRevive:
                OnFighterRevive(evt);
                break;
            case BattleEventType.PullStart:
                OnPullStart(evt);
                break;
            case BattleEventType.ReflectDamage:
                OnReflectDamage(evt);
                break;
            case BattleEventType.SummonExplode:
                OnSummonExplode(evt);
                break;
            case BattleEventType.SelfRevive:
                OnSelfRevive(evt);
                break;
        }
    }

    void OnFighterSpawn(BattleEvent evt)
    {
        var vf = new ViewFighter
        {
            PlayerId  = evt.SourceId,
            CharType  = (byte)(evt.TargetId & 0x0F),
            TeamId    = (byte)(evt.TargetId >> 4),
            MaxHp     = evt.IntParam,
            CurrentHp = evt.IntParam,
            TargetPos = ViewFighter.RawToWorld(evt.PosXRaw, evt.PosYRaw),
        };
        vf.DisplayPos = vf.TargetPos;

        // 从配置读取角色名和技能名
        var charType = (FrameSync.CharacterType)vf.CharType;
        vf.CharName = charType.ToString();
        var charCfg = FrameSync.CharacterConfig.Get(charType);
        var passiveCfg = FrameSync.SkillConfigLoader.Get(charCfg.Passive);
        vf.PassiveName = passiveCfg != null ? passiveCfg.Name : "";
        var sk2Cfg = FrameSync.SkillConfigLoader.Get(charCfg.Skill2);
        vf.Skill2Name = sk2Cfg != null ? sk2Cfg.Name : "技能2";
        vf.Skill2Type = sk2Cfg != null ? sk2Cfg.Type : "";

        // 创建角色显示对象（优先使用预制体，fallback代码创建）
        vf.Go = CreateFighterGO();
        vf.Go.name = $"Fighter_P{vf.PlayerId}_{vf.CharName}";
        vf.Go.transform.position = vf.DisplayPos;

        // 本地玩家稍大
        float scale = (vf.PlayerId == LocalPlayerId) ? 1.2f : 0.9f;
        vf.Go.transform.localScale = Vector3.one * scale;

        // 获取子对象 SpriteRenderer
        vf.HeadSR  = vf.Go.transform.Find("head").GetComponent<SpriteRenderer>();
        vf.TeamSR  = vf.Go.transform.Find("team").GetComponent<SpriteRenderer>();
        vf.StateSR = vf.Go.transform.Find("state").GetComponent<SpriteRenderer>();

        // 确保精灵已赋值
        if (vf.HeadSR.sprite == null) vf.HeadSR.sprite = DefaultCircle;
        if (vf.TeamSR.sprite == null) vf.TeamSR.sprite = DefaultCircle;
        if (vf.StateSR.sprite == null) vf.StateSR.sprite = DefaultCircle;

        // 角色基础颜色
        vf.BaseColor = vf.CharType switch
        {
            // 1 => new Color(0.85f, 0.2f, 0.2f),   // 红=剑士
            // 2 => new Color(0.2f, 0.6f, 0.95f),    // 蓝=弓手
            // 3 => new Color(0.6f, 0.2f, 0.85f),    // 紫=刺客
            // 4 => new Color(0.95f, 0.5f, 0.1f),    // 橙=法师
            // 5 => new Color(0.7f, 0.9f, 1.0f),     // 浅蓝=雪人
            // 6 => new Color(0.3f, 0.9f, 0.4f),     // 绿=医疗师
            // 9 => new Color(0.4f, 0.6f, 1.0f),     // 亮蓝=闪电法师
            // 10 => new Color(0.9f, 0.8f, 0.2f),    // 金色=圣骑士
            // 11 => new Color(0.4f, 0.7f, 0.3f),    // 墨绿=荆棘战士
            // 12 => new Color(0.5f, 0.2f, 0.5f),    // 暗紫=骷髅王
            // 13 => new Color(0.6f, 0.5f, 0.4f),    // 骨白=小骷髅兵
            _ => Color.white,
        };

        // 设置头像精灵（优先加载配置路径，fallback用角色颜色圆形）
        Sprite headSprite = null;
        if (!string.IsNullOrEmpty(charCfg.HeadIcon))
            headSprite = Resources.Load<Sprite>(charCfg.HeadIcon);
        if (headSprite != null)
            vf.HeadSR.sprite = headSprite;
        else
            vf.HeadSR.color = vf.BaseColor;

        // 设置阵营颜色
        vf.TeamSR.color = vf.TeamId == 1
            ? new Color(0.2f, 0.5f, 1f)    // 队伍1=蓝色
            : new Color(1f, 0.3f, 0.2f);   // 队伍2=红色

        // 状态精灵初始透明
        vf.StateSR.color = new Color(1f, 1f, 1f, 0f);

        _fighters[evt.SourceId] = vf;
    }

    void OnMove(BattleEvent evt)
    {
        if (evt.SourceId >= _fighters.Length) return;
        var vf = _fighters[evt.SourceId];
        if (vf == null) return;
        vf.TargetPos = ViewFighter.RawToWorld(evt.PosXRaw, evt.PosYRaw);

        // 解码朝向（IntParam 高16位=FacingX, 低16位=FacingY, 编码=Raw>>17）
        short fx = (short)((evt.IntParam >> 16) & 0xFFFF);
        short fy = (short)(evt.IntParam & 0xFFFF);
        // Raw>>17 对于单位向量 [-1,1]: 1.0→32768, -1.0→-32768
        float fxf = fx / 32768f;
        float fyf = fy / 32768f;
        if (fxf != 0 || fyf != 0)
            vf.TargetYaw = Mathf.Atan2(fxf, fyf) * Mathf.Rad2Deg; // X→sin, Y(Z)→cos → atan2(x,z)
    }

    void OnHpChanged(BattleEvent evt)
    {
        var vf = _fighters[evt.SourceId];
        if (vf == null) return;
        vf.CurrentHp = evt.IntParam;
    }

    void OnStateChanged(BattleEvent evt)
    {
        var vf = _fighters[evt.SourceId];
        if (vf == null) return;
        vf.IsMoving     = (evt.IntParam & 1) != 0;
        vf.IsFleeing    = (evt.IntParam & 2) != 0;
        vf.IsCasting    = (evt.IntParam & 4) != 0;
        vf.IsCastingUlt = (evt.IntParam & 8) != 0;
        vf.IsStunned    = (evt.IntParam & 16) != 0;
        vf.IsStealthed  = (evt.IntParam & 32) != 0;
        vf.IsSlowed     = (evt.IntParam & 64) != 0;
        vf.IsStaggered  = (evt.IntParam & 128) != 0;
        vf.IsAtkBuffed  = (evt.IntParam & 256) != 0;
        vf.IsAtkDebuffed = (evt.IntParam & 512) != 0;

        // 非特殊状态恢复原色
        if (!vf.IsFleeing && !vf.IsStunned && !vf.IsStealthed && !vf.IsSlowed && !vf.IsStaggered && vf.HeadSR != null && !vf.IsDead)
            vf.HeadSR.color = vf.BaseColor;
    }

    void OnNormalAttack(BattleEvent evt)
    {
        var vf = _fighters[evt.SourceId];
        if (vf != null) vf.AttackFlashTimer = 0.4f;
    }

    void OnUltimateCast(BattleEvent evt)
    {
        var vf = _fighters[evt.SourceId];
        if (vf != null) vf.UltFlashTimer = 0.6f;

        // 大招释放者闪光
        if (vf?.Go != null)
        {
            StartCoroutine(ScalePulse(vf.Go, 1.5f, 0.3f));
        }
    }

    void OnCooldownUpdate(BattleEvent evt)
    {
        var vf = _fighters[evt.SourceId];
        if (vf == null) return;
        vf.AtkCooldownLeft = evt.IntParam & 0xFFFF;
        vf.Skill2CooldownLeft = (evt.IntParam >> 16) & 0xFFFF;
        vf.UltCooldownLeft = (int)evt.PosXRaw;

        // 首次发送附带总CD值（PosYRaw 高32位=普攻总CD，低32位=大招总CD）
        if (evt.PosYRaw != 0)
        {
            vf.AtkCooldownTotal = (int)(evt.PosYRaw >> 32);
            vf.UltCooldownTotal = (int)(evt.PosYRaw & 0xFFFFFFFF);
        }
    }

    void OnDamage(BattleEvent evt)
    {
        var vf = _fighters[evt.TargetId];
        if (vf?.Go != null)
        {
            StartCoroutine(ShakeEffect(vf.Go, 0.15f, 0.2f));
            FlashState(evt.TargetId, Color.red, 0.2f);
        }
    }

    void OnProjectileSpawn(BattleEvent evt)
    {
        var startPos = ViewFighter.RawToWorld(evt.PosXRaw, evt.PosYRaw);
        bool isSkill2 = evt.IntParam == 1;
        bool isAoE = evt.IntParam == 2;
        bool isPierce = evt.IntParam == 3;

        if (isPierce)
        {
            // 穿刺弹射物：直线飞行，不追踪
            var target = _fighters[evt.TargetId];
            Vector3 dir;
            if (target != null)
                dir = (target.DisplayPos - startPos).normalized;
            else
                dir = Vector3.forward;

            // 创建拉伸的闪电视觉（长条形）
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = $"PierceProj_P{evt.SourceId}";
            go.transform.position = startPos;
            go.transform.localScale = new Vector3(0.15f, 0.15f, 0.8f);
            go.transform.rotation = Quaternion.LookRotation(dir);
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);
            var rend = go.GetComponent<Renderer>();
            if (rend != null)
                rend.material.color = new Color(0.5f, 0.8f, 1f);

            _viewPiercingProjectiles.Add(new ViewPiercingProjectile
            {
                Go = go,
                Direction = dir,
                Speed = 18f,
                MaxLifetime = 1.2f,
                Elapsed = 0f,
            });
            return;
        }

        var projGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        projGo.name = isAoE ? $"AoEProj_P{evt.SourceId}"
                 : isSkill2 ? $"KnockArrow_P{evt.SourceId}"
                 : $"Arrow_P{evt.SourceId}";
        projGo.transform.localScale = Vector3.one * (isAoE ? 0.5f : isSkill2 ? 0.4f : 0.3f);
        projGo.transform.position = startPos;

        // 移除碰撞体
        var projCol = projGo.GetComponent<Collider>();
        if (projCol != null) Destroy(projCol);

        // AoE=橙色, 击退箭=青绿色, 普攻=黄色
        var projRend = projGo.GetComponent<Renderer>();
        if (projRend != null)
            projRend.material.color = isAoE
                ? new Color(1f, 0.4f, 0.1f)
                : isSkill2
                    ? new Color(0.2f, 1f, 0.6f)
                    : new Color(1f, 0.85f, 0.2f);

        _viewProjectiles.Add(new ViewProjectile
        {
            SourceId = evt.SourceId,
            TargetId = evt.TargetId,
            Go       = projGo,
            Speed    = 12f,  // 显示层飞行速度
        });
    }

    void OnProjectileHit(BattleEvent evt)
    {
        // 命中效果已由 OnDamage 处理（抖动+状态闪烁），弹射物在 LateUpdate 中自动销毁
    }

    void OnSkill2Cast(BattleEvent evt)
    {
        var vf = _fighters[evt.SourceId];
        if (vf == null) return;
        vf.Skill2FlashTimer = 0.5f;
        // 更新释放者位置（瞬移后位置变化）
        vf.TargetPos = ViewFighter.RawToWorld(evt.PosXRaw, evt.PosYRaw);
        if (vf.Skill2Type == "Blink" || vf.Skill2Type == "ReactBlink") // 瞬移：直接设置显示位置
            vf.DisplayPos = vf.TargetPos;
    }

    void OnDeath(BattleEvent evt)
    {
        var vf = _fighters[evt.SourceId];
        if (vf == null) return;
        vf.IsDead = true;
        if (vf.Go != null)
        {
            StartCoroutine(DeathEffect(vf));
        }
    }

    void OnAoEExplosion(BattleEvent evt)
    {
        // 销毁对应的弹射物显示对象（仅AoEProjectile类型，非闪电云）
        bool fromCloud = false;
        for (int i = _viewProjectiles.Count - 1; i >= 0; i--)
        {
            if (_viewProjectiles[i].SourceId == evt.SourceId)
            {
                if (_viewProjectiles[i].Go != null) Destroy(_viewProjectiles[i].Go);
                _viewProjectiles.RemoveAt(i);
                break;
            }
        }

        // 检查是否来自闪电云（没有对应的弹射物）
        for (int i = 0; i < _viewLightningClouds.Count; i++)
        {
            if (_viewLightningClouds[i].Go != null) { fromCloud = true; break; }
        }

        // 显示爆炸效果
        var pos = ViewFighter.RawToWorld(evt.PosXRaw, evt.PosYRaw);
        var source = evt.SourceId < _fighters.Length ? _fighters[evt.SourceId] : null;
        bool isIce = source != null && source.CharType == 5; // Snowman
        bool isLightning = source != null && source.CharType == 9; // LightningMage
        Color color = isLightning ? new Color(0.4f, 0.6f, 1f)
                    : isIce ? new Color(0.3f, 0.7f, 1f)
                    : new Color(1f, 0.4f, 0.1f);
        StartCoroutine(ShowExplosion(pos, evt.IntParam, color));
    }

    IEnumerator ShowExplosion(Vector3 pos, int radius, Color color)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "AoEExplosion";
        go.transform.position = pos;
        var expCol = go.GetComponent<Collider>();
        if (expCol != null) Destroy(expCol);
        var rend = go.GetComponent<Renderer>();
        if (rend != null) rend.material.color = color;
        float duration = 0.3f;
        float elapsed = 0f;
        float maxScale = Mathf.Max(radius * 2f, 1f);
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / duration;
            go.transform.localScale = Vector3.one * Mathf.Lerp(0.2f, maxScale, t);
            if (rend != null) rend.material.color = Color.Lerp(color, Color.white, t * 0.5f);
            yield return null;
        }
        Destroy(go);
    }

    // ════════════════════════════════════════════════════════════
    //  视觉效果
    // ════════════════════════════════════════════════════════════

    void FlashState(byte targetId, Color flashColor, float duration)
    {
        if (targetId < 1 || targetId >= _fighters.Length) return;
        var vf = _fighters[targetId];
        if (vf == null || vf.Go == null || vf.IsDead || vf.StateSR == null) return;
        StartCoroutine(DoStateFlash(vf.StateSR, flashColor, duration));
    }

    IEnumerator DoStateFlash(SpriteRenderer sr, Color flashColor, float dur)
    {
        if (sr == null) yield break;
        sr.color = flashColor;
        float elapsed = 0f;
        while (elapsed < dur)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / dur;
            if (sr == null) yield break;
            sr.color = Color.Lerp(flashColor, new Color(flashColor.r, flashColor.g, flashColor.b, 0f), t);
            yield return null;
        }
        if (sr != null) sr.color = new Color(1f, 1f, 1f, 0f);
    }

    void OnHealApplied(BattleEvent evt)
    {
        byte targetId = evt.TargetId;
        if (targetId < 1 || targetId >= _fighters.Length) return;
        var vf = _fighters[targetId];
        if (vf == null || vf.Go == null || vf.IsDead) return;
        vf.CurrentHp = Mathf.Min(vf.CurrentHp + evt.IntParam, vf.MaxHp);
        FlashState(targetId, new Color(0.2f, 1f, 0.3f), 0.25f);
    }

    IEnumerator ScalePulse(GameObject go, float maxScale, float duration)
    {
        if (go == null) yield break;
        var origScale = go.transform.localScale;
        float half = duration * 0.5f;

        float elapsed = 0f;
        while (elapsed < half)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / half;
            go.transform.localScale = Vector3.Lerp(origScale, origScale * maxScale, t);
            yield return null;
        }
        elapsed = 0f;
        while (elapsed < half)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / half;
            go.transform.localScale = Vector3.Lerp(origScale * maxScale, origScale, t);
            yield return null;
        }
        if (go != null)
            go.transform.localScale = origScale;
    }

    IEnumerator ShakeEffect(GameObject go, float intensity, float duration)
    {
        if (go == null) yield break;
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float x = Random.Range(-intensity, intensity);
            float z = Random.Range(-intensity, intensity);
            var pos = go.transform.position;
            go.transform.position = new Vector3(pos.x + x, pos.y, pos.z + z);
            yield return null;
        }
    }

    IEnumerator DeathEffect(ViewFighter vf)
    {
        if (vf.Go == null) yield break;

        // 逐渐缩小并变灰
        float elapsed = 0f;
        float duration = 0.5f;
        var startScale = vf.Go.transform.localScale;
        var endScale = Vector3.one * 0.2f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / duration;
            if (vf.Go == null) yield break;
            vf.Go.transform.localScale = Vector3.Lerp(startScale, endScale, t);
            if (vf.HeadSR != null)
                vf.HeadSR.color = Color.Lerp(vf.BaseColor, Color.gray, t);
            yield return null;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  OnGUI — 显示游戏 UI
    // ════════════════════════════════════════════════════════════

    GUIStyle _richLabel;
    GUIStyle _headerStyle;

    void InitStyles()
    {
        if (_richLabel != null) return;
        _richLabel = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 13 };
        _headerStyle = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 15, fontStyle = FontStyle.Bold };
    }

    void OnGUI()
    {
        InitStyles();

        GUILayout.BeginArea(new Rect(10, 10, 620, Screen.height - 20));

        // 连接状态
        GUILayout.Label($"<b>连接:</b> {ConnectionInfo}  |  ID: {LocalPlayerId}  |  帧: {CurrentFrame}", _richLabel);
        GUILayout.Space(4);

        switch (_phase)
        {
            case 0:
                if (!UseExternalSelectUI)
                    DrawSelectionUI();
                break;
            case 1: DrawCombatUI();    break;
            case 2: DrawEndUI();       break;
        }

        GUILayout.EndArea();
    }

    void DrawSelectionUI()
    {
        int teamSize = FrameSync.CharacterConfig.TeamSize;
        GUILayout.Label($"<b>══ 选择角色 (每队{teamSize}个) ══</b>", _headerStyle);

        var types = new[] { FrameSync.CharacterType.Warrior, FrameSync.CharacterType.Archer, FrameSync.CharacterType.Assassin, FrameSync.CharacterType.Mage, FrameSync.CharacterType.Healer, FrameSync.CharacterType.Witch, FrameSync.CharacterType.Barbarian, FrameSync.CharacterType.LightningMage, FrameSync.CharacterType.Paladin, FrameSync.CharacterType.ThornWarrior, FrameSync.CharacterType.SkeletonKing };
        for (int k = 0; k < types.Length; k++)
        {
            var ct = types[k];
            var cfg = FrameSync.CharacterConfig.Get(ct);
            var atkSkill = FrameSync.SkillConfigLoader.Get(cfg.NormalAttack);
            var ultSkill = FrameSync.SkillConfigLoader.Get(cfg.Ultimate);
            var passiveSkill = FrameSync.SkillConfigLoader.Get(cfg.Passive);
            string atkType = atkSkill != null && (atkSkill.Type == "RangedAttack" || atkSkill.Type == "AoEProjectile") ? "远程" : "近战";
            int atkDmg = atkSkill != null ? atkSkill.Damage : 0;
            string ultName = ultSkill != null ? ultSkill.Name : "?";
            string passiveName = passiveSkill != null ? passiveSkill.Name : "无";
            GUILayout.Label($"  [{k + 1}]  <b>{ct}</b>  HP {cfg.MaxHp}  {atkType}  普攻 {atkDmg}  大招 {ultName}  被动 {passiveName}", _richLabel);
        }
        GUILayout.Space(4);

        for (int i = 1; i <= 2; i++)
        {
            string marker = i == LocalPlayerId ? " (你)" : "";
            var sel = _teamSelections[i];
            string state;
            if (sel.Count >= teamSize)
            {
                var names = new System.Text.StringBuilder();
                for (int s = 0; s < sel.Count; s++)
                {
                    if (s > 0) names.Append(", ");
                    names.Append((FrameSync.CharacterType)sel[s]);
                }
                state = $"<color=lime>{names}</color>";
            }
            else
            {
                state = $"<color=yellow>选择中 ({sel.Count}/{teamSize})...</color>";
            }
            GUILayout.Label($"  P{i}{marker}: {state}", _richLabel);
        }
    }

    void DrawCombatUI()
    {
        GUILayout.Label("<b>══ 战斗中 ══</b>", _headerStyle);

        for (int team = 1; team <= 2; team++)
        {
            string teamLabel = team == LocalPlayerId ? $"<color=cyan>队伍{team} (你)</color>" : $"队伍{team}";
            GUILayout.Label($"  <b>{teamLabel}</b>", _richLabel);

            for (int i = 1; i < _fighters.Length; i++)
            {
                var vf = _fighters[i];
                if (vf == null) continue;
                if (vf.IsDead) continue;
                // TeamId encoded: ids 1..teamSize = team1, teamSize+1..2*teamSize = team2
                int teamSize = FrameSync.CharacterConfig.TeamSize;
                int fighterTeam = vf.TeamId > 0 ? vf.TeamId : (vf.PlayerId <= teamSize ? 1 : 2);
                if (fighterTeam != team) continue;

                float hpPct = vf.MaxHp > 0 ? (float)vf.CurrentHp / vf.MaxHp : 0;
                string hpColor = hpPct > 0.5f ? "lime" : hpPct > 0.2f ? "yellow" : "red";
                string charName = vf.CharName ?? "???";

                string state;
                if (vf.IsDead)                        state = "<color=gray>阵亡</color>";
                else if (vf.IsStaggered)              state = "<color=#ff6666>★僵直★</color>";
                else if (vf.IsStunned)                state = "<color=#888888>★眩晕★</color>";
                else if (vf.IsStealthed)              state = "<color=#aa66ff>★隐身★</color>";
                else if (vf.IsCasting && vf.IsCastingUlt) state = "<color=yellow>★大招施法中★</color>";
                else if (vf.IsCasting)                state = "<color=#ffaa44>普攻施法中</color>";
                else if (vf.IsUlting)                 state = "<color=yellow>★大招★</color>";
                else if (vf.IsAttacking)              state = "<color=white>攻击中</color>";
                else if (vf.IsFleeing)                state = "<color=orange>逃跑</color>";
                else if (vf.IsMoving)                 state = "<color=#88ccff>移动</color>";
                else                                  state = "<color=lime>站立</color>";

                int barLen = 15;
                int filled = Mathf.RoundToInt(hpPct * barLen);
                string bar = new string('█', Mathf.Max(0, filled)) + new string('░', Mathf.Max(0, barLen - filled));

                string atkCDStr = vf.AtkCooldownLeft > 0
                    ? $"<color=white>{vf.AtkCooldownLeft / 15f:F1}s</color>"
                    : "<color=lime>就绪</color>";
                string ultCDStr = vf.UltCooldownLeft > 0
                    ? $"<color=white>{vf.UltCooldownLeft / 15f:F1}s</color>"
                    : "<color=lime>就绪</color>";
                string skill2Name = vf.Skill2Name ?? "技能2";
                string sk2CDStr = vf.Skill2CooldownLeft > 0
                    ? $"<color=white>{vf.Skill2CooldownLeft / 15f:F1}s</color>"
                    : "<color=lime>就绪</color>";

                GUILayout.Label(
                    $"    #{vf.PlayerId} [{charName}]  {state}  HP:<color={hpColor}>{vf.CurrentHp}</color>/{vf.MaxHp} {bar}",
                    _richLabel);
                string buffStr = "";
                if (vf.IsStunned) buffStr += " <color=#ff4444>[眩晕]</color>";
                if (vf.IsStaggered) buffStr += " <color=#ff6666>[僵直]</color>";
                if (vf.IsSlowed)  buffStr += " <color=#6688ff>[减速]</color>";
                if (vf.IsAtkBuffed) buffStr += " <color=#44ff44>[增益]</color>";
                if (vf.IsAtkDebuffed) buffStr += " <color=#ff8844>[减益]</color>";
                string passiveStr = !string.IsNullOrEmpty(vf.PassiveName) ? $" 被动:{vf.PassiveName}" : "";
                GUILayout.Label(
                    $"      普攻:{atkCDStr} 大招:{ultCDStr} {skill2Name}:{sk2CDStr}{passiveStr}{buffStr}",
                    _richLabel);
            }
        }

        GUILayout.Space(4);
        GUILayout.Label("  按 <b>[Space]</b> 释放大招", _richLabel);
    }

    void DrawEndUI()
    {
        GUILayout.Label("<b>══ 战斗结束 ══</b>", _headerStyle);

        for (int team = 1; team <= 2; team++)
        {
            bool isWinner = team == _winnerId;
            string teamLabel = isWinner ? $"<color=lime>队伍{team} ★胜利★</color>" : $"<color=red>队伍{team}</color>";
            GUILayout.Label($"  <b>{teamLabel}</b>", _richLabel);

            int teamSize = FrameSync.CharacterConfig.TeamSize;
            for (int i = 1; i < _fighters.Length; i++)
            {
                var vf = _fighters[i];
                if (vf == null) continue;
                int fighterTeam = vf.TeamId > 0 ? vf.TeamId : (vf.PlayerId <= teamSize ? 1 : 2);
                if (fighterTeam != team) continue;

                string charName = vf.CharName ?? "???";
                string result = vf.IsDead
                    ? "<color=red>阵亡</color>"
                    : $"<color=lime>存活 HP:{vf.CurrentHp}</color>";
                GUILayout.Label($"    #{vf.PlayerId} [{charName}]  {result}", _richLabel);
            }
        }

        GUILayout.Space(8);
        if (_winnerId == LocalPlayerId)
            GUILayout.Label("  <color=lime><b>你赢了！</b></color>", _headerStyle);
        else
            GUILayout.Label("  <color=red><b>你输了...</b></color>", _headerStyle);
    }

    void OnChainLightningLink(BattleEvent evt)
    {
        var source = _fighters[evt.SourceId];
        var target = _fighters[evt.TargetId];
        if (source == null || target == null) return;

        // 创建连接线（LineRenderer）
        var go = new GameObject($"ChainLink_{evt.SourceId}_{evt.TargetId}");
        var lr = go.AddComponent<LineRenderer>();
        lr.positionCount = 2;
        lr.SetPosition(0, source.DisplayPos);
        lr.SetPosition(1, target.DisplayPos);
        lr.startWidth = 0.12f;
        lr.endWidth = 0.08f;
        lr.startColor = new Color(0.4f, 0.7f, 1f, 1f);
        lr.endColor = new Color(0.8f, 0.9f, 1f, 1f);
        lr.material = new Material(Shader.Find("Sprites/Default"));
        lr.sortingOrder = 10;

        _viewChainLinks.Add(new ViewChainLink
        {
            Go = go,
            Lifetime = 0.5f,
        });
    }

    void OnLightningCloudSpawn(BattleEvent evt)
    {
        var pos = ViewFighter.RawToWorld(evt.PosXRaw, evt.PosYRaw);
        float lifetime = evt.IntParam / 15f; // 帧→秒

        // 创建扁平半透明圆柱体作为闪电云
        var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        go.name = $"LightningCloud_P{evt.SourceId}";
        go.transform.position = pos + Vector3.up * 0.1f; // 略微抬高
        go.transform.localScale = new Vector3(5f, 0.15f, 5f); // 扁平大圆
        var col = go.GetComponent<Collider>();
        if (col != null) Destroy(col);
        var rend = go.GetComponent<Renderer>();
        if (rend != null)
        {
            // 半透明蓝紫色材质
            var mat = new Material(Shader.Find("Sprites/Default"));
            mat.color = new Color(0.3f, 0.4f, 0.9f, 0.45f);
            rend.material = mat;
        }

        _viewLightningClouds.Add(new ViewLightningCloud
        {
            Go = go,
            Lifetime = lifetime,
            PulseTimer = 0f,
        });
    }

    void OnFighterRevive(BattleEvent evt)
    {
        // SourceId=复活的角色, TargetId=施法者, IntParam=复活后HP
        byte revivedId = evt.SourceId;
        if (revivedId >= _fighters.Length || _fighters[revivedId] == null) return;
        var vf = _fighters[revivedId];
        vf.IsDead = false;
        vf.CurrentHp = evt.IntParam;
        vf.TargetPos = ViewFighter.RawToWorld(evt.PosXRaw, evt.PosYRaw);

        // 重新显示角色
        if (vf.Go != null)
        {
            vf.Go.SetActive(true);
            vf.Go.transform.position = vf.TargetPos;
            vf.Go.transform.localScale = Vector3.one;
        }
        // 恢复颜色
        if (vf.HeadSR != null)
            vf.HeadSR.color = vf.BaseColor;
    }

    void OnPullStart(BattleEvent evt)
    {
        // 拉取视觉：在拉取者和被拉者之间创建连线
        byte sourceId = evt.SourceId;
        byte targetId = evt.TargetId;
        if (sourceId >= _fighters.Length || targetId >= _fighters.Length) return;
        var source = _fighters[sourceId];
        var target = _fighters[targetId];
        if (source?.Go == null || target?.Go == null) return;

        // 创建绿色连接线（类似ChainLightning但颜色不同）
        var lineGo = new GameObject($"PullLine_P{sourceId}_P{targetId}");
        var lr = lineGo.AddComponent<LineRenderer>();
        lr.material = new Material(Shader.Find("Sprites/Default"));
        lr.startColor = new Color(0.3f, 0.8f, 0.2f, 0.8f);
        lr.endColor = new Color(0.3f, 0.8f, 0.2f, 0.3f);
        lr.startWidth = 0.15f;
        lr.endWidth = 0.08f;
        lr.positionCount = 2;
        lr.SetPosition(0, source.Go.transform.position);
        lr.SetPosition(1, target.Go.transform.position);

        _viewChainLinks.Add(new ViewChainLink { Go = lineGo, Lifetime = 0.7f });
    }

    void OnReflectDamage(BattleEvent evt)
    {
        // 反伤视觉闪烁效果
        byte targetId = evt.TargetId; // 受反伤者
        if (targetId >= _fighters.Length) return;
        var vf = _fighters[targetId];
        if (vf?.HeadSR == null) return;
        // 闪白色表示反伤
        vf.HeadSR.color = Color.white;
    }

    void OnSummonExplode(BattleEvent evt)
    {
        // 自爆视觉：在爆炸位置创建一个快速扩散消失的红色圆圈
        var pos = ViewFighter.RawToWorld(evt.PosXRaw, evt.PosYRaw);
        var go = new GameObject($"Explosion_P{evt.SourceId}");
        go.transform.position = pos;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = DefaultCircle;
        sr.color = new Color(1f, 0.3f, 0f, 0.7f); // 橙红色
        sr.sortingOrder = 20;
        float radius = evt.IntParam > 0 ? evt.IntParam : 3f;
        go.transform.localScale = Vector3.one * radius * 2f;
        Destroy(go, 0.4f);
    }

    void OnSelfRevive(BattleEvent evt)
    {
        // 自我复活：与FighterRevive类似，但不需要位置（原地复活）
        byte fid = evt.SourceId;
        if (fid >= _fighters.Length || _fighters[fid] == null) return;
        var vf = _fighters[fid];
        vf.IsDead = false;
        vf.CurrentHp = evt.IntParam;

        if (vf.Go != null)
        {
            vf.Go.SetActive(true);
            vf.Go.transform.localScale = Vector3.one;
        }
        if (vf.HeadSR != null)
            vf.HeadSR.color = vf.BaseColor;
    }

    // ════════════════════════════════════════════════════════════
    //  清理
    // ════════════════════════════════════════════════════════════

    void OnDestroy()
    {
        for (int i = 1; i < _fighters.Length; i++)
            if (_fighters[i]?.Go != null) Destroy(_fighters[i].Go);
        for (int i = _viewPiercingProjectiles.Count - 1; i >= 0; i--)
            if (_viewPiercingProjectiles[i].Go != null) Destroy(_viewPiercingProjectiles[i].Go);
        for (int i = _viewLightningClouds.Count - 1; i >= 0; i--)
            if (_viewLightningClouds[i].Go != null) Destroy(_viewLightningClouds[i].Go);
        for (int i = _viewChainLinks.Count - 1; i >= 0; i--)
            if (_viewChainLinks[i].Go != null) Destroy(_viewChainLinks[i].Go);
    }
}
