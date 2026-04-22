# 自动战斗系统 — 显示层接入指南（纯事件驱动）

## 目录

- [1. 架构概述](#1-架构概述)
- [2. 事件协议](#2-事件协议)
- [3. 扩展事件类型](#3-扩展事件类型)
- [4. 显示层数据模型](#4-显示层数据模型)
- [5. 事件消费流程](#5-事件消费流程)
- [6. 创建显示层的完整步骤](#6-创建显示层的完整步骤)
- [7. 完整示例：SimpleBattleView](#7-完整示例simplebattleview)
- [8. 进阶：特效与动画](#8-进阶特效与动画)

---

## 1. 架构概述

逻辑层与显示层**完全隔离**，显示层**不持有**任何逻辑层对象的引用（不引用 `BattleFighter`、不读取 `BattleLogic` 的属性），所有数据**仅通过事件队列**单向传递：

```
┌──────────────────────────┐          ┌──────────────────────────┐
│       逻辑层（确定性）     │          │       显示层（渲染）       │
│                          │          │                          │
│  BattleLogic             │  Event   │  BattleView              │
│  ├─ BattleFighter[1]     │  Queue   │  ├─ ViewFighter[1]       │
│  ├─ BattleFighter[2]     │ ───────▶ │  ├─ ViewFighter[2]       │
│  │                       │ (唯一    │  ├─ 特效系统              │
│  └─ 每帧产生事件写入队列   │  通道)   │  └─ UI (血条/CD/伤害数字)  │
└──────────────────────────┘          └──────────────────────────┘
```

### 核心原则

1. **零引用**：显示层不 `using FrameSync`（除了事件结构体），不依赖 `BattleFighter`、`BattleLogic`、`FixedInt` 等逻辑层类型
2. **单向数据流**：逻辑层 → EventQueue → 显示层，显示层绝不回写
3. **自建状态**：显示层维护自己的 `ViewFighter` 数据模型，全部从事件重建
4. **纯浮点渲染**：事件携带 `long` 类型原始值（`PosXRaw`/`PosYRaw`），显示层自行转为 `float`

### 为什么要完全隔离？

| 优势 | 说明 |
|------|------|
| **可替换** | 显示层可以是 Unity、Godot、Web 前端、甚至录像回放器 |
| **可测试** | 逻辑层可以无渲染跑测试，只验证事件序列 |
| **帧同步安全** | 杜绝显示层意外修改逻辑状态导致不同步 |
| **热重载** | 显示层代码修改不影响逻辑层确定性 |

---

## 2. 事件协议

### 现有事件

`BattleEvent` 结构体（定义在 `BattleData.cs`）：

```csharp
public struct BattleEvent
{
    public int             Frame;
    public BattleEventType Type;
    public byte            SourceId;   // 事件发起者
    public byte            TargetId;   // 事件目标
    public int             IntParam;   // 通用整数参数
    public long            PosXRaw;    // 定点数X坐标原始值
    public long            PosYRaw;    // 定点数Y坐标原始值
}
```

| BattleEventType | 触发时机 | 字段含义 |
|-----------------|---------|----------|
| `CharSelected` | 玩家选角 | `SourceId`=玩家, `IntParam`=(int)CharacterType |
| `BattleStart` | 双方选角完毕 | — |
| `Move` | 每帧位置变化 | `SourceId`=角色, `PosXRaw/PosYRaw`=新位置 |
| `NormalAttack` | 普攻命中 | `SourceId`=攻击者, `TargetId`=受击者, `IntParam`=伤害 |
| `UltimateCast` | 大招释放 | `SourceId`=释放者, `TargetId`=目标, `IntParam`=伤害, `PosXRaw/PosYRaw`=释放位置 |
| `Damage` | 受到伤害 | `SourceId`=攻击者, `TargetId`=受击者, `IntParam`=伤害值 |
| `HpChanged` | 血量变化 | `SourceId`=角色, `IntParam`=当前HP |
| `Death` | 角色阵亡 | `SourceId`=阵亡者 |
| `BattleEnd` | 战斗结束 | `IntParam`=胜者ID |

---

## 3. 扩展事件类型

现有事件不足以让显示层**完全脱离**逻辑层引用。需要补充以下事件：

### 3.1 新增事件枚举

```csharp
public enum BattleEventType : byte
{
    CharSelected,
    BattleStart,
    Move,
    NormalAttack,
    UltimateCast,
    Damage,
    HpChanged,
    Death,
    BattleEnd,

    // ── 新增：显示层完全隔离所需 ──
    FighterSpawn,   // 角色创建     SourceId=玩家, TargetId=(byte)CharType,
                    //              IntParam=MaxHp, PosXRaw/PosYRaw=初始位置
    StateChanged,   // 状态变化     SourceId=角色, IntParam=状态位掩码
}
```

### 3.2 FighterSpawn — 角色初始化

在 `BeginCombat` 中，创建完 BattleFighter 后立即发出：

```csharp
// BattleLogic.BeginCombat() 中添加
_events.Add(new BattleEvent
{
    Frame     = _currentFrame,
    Type      = BattleEventType.FighterSpawn,
    SourceId  = fighter.PlayerId,
    TargetId  = (byte)fighter.CharType,
    IntParam  = fighter.MaxHp.ToInt(),            // 最大血量（整数部分）
    PosXRaw   = fighter.Position.X.Raw,
    PosYRaw   = fighter.Position.Y.Raw,
});
```

显示层收到此事件后，创建角色视图并初始化所有属性。**不再需要读取 `BattleFighter`。**

### 3.3 StateChanged — 移动/逃跑状态

在 `BattleFighter.Tick()` 末尾，当 `IsMoving`/`IsFleeing` 发生变化时发出：

```csharp
// 状态位定义
public const int StateBit_Moving  = 1 << 0; // bit 0
public const int StateBit_Fleeing = 1 << 1; // bit 1

// BattleFighter.Tick() 末尾
int newState = (IsMoving ? StateBit_Moving : 0)
             | (IsFleeing ? StateBit_Fleeing : 0);
if (newState != _prevState)
{
    _events.Add(new BattleEvent
    {
        Frame    = frame,
        Type     = BattleEventType.StateChanged,
        SourceId = PlayerId,
        IntParam = newState,
    });
    _prevState = newState;
}
```

---

## 4. 显示层数据模型

显示层自建轻量级的**视图数据模型**，完全从事件中重建：

```csharp
/// <summary>
/// 显示层角色数据 — 纯渲染用，全部从事件重建。
/// 不引用任何逻辑层类型。
/// </summary>
public class ViewFighter
{
    // ── 身份 ──
    public byte PlayerId;
    public byte CharType;       // 1=Warrior, 2=Archer（同 CharacterType 枚举值）

    // ── 位置（浮点，用于渲染） ──
    public Vector3 TargetPos;   // 事件传来的目标位置
    public Vector3 DisplayPos;  // 平滑插值后的显示位置

    // ── 血量 ──
    public int MaxHp;
    public int CurrentHp;

    // ── 状态 ──
    public bool IsMoving;
    public bool IsFleeing;
    public bool IsDead;

    /// <summary>从 FighterSpawn 事件初始化。</summary>
    public void InitFromEvent(BattleEvent evt)
    {
        PlayerId  = evt.SourceId;
        CharType  = evt.TargetId;           // TargetId 复用为 CharType
        MaxHp     = evt.IntParam;
        CurrentHp = evt.IntParam;           // 初始满血
        TargetPos = RawToWorld(evt.PosXRaw, evt.PosYRaw);
        DisplayPos = TargetPos;
        IsDead = false;
        IsMoving = false;
        IsFleeing = false;
    }

    /// <summary>将定点数原始值转为渲染坐标。</summary>
    public static Vector3 RawToWorld(long rawX, long rawY)
    {
        // Q32.32 定点数：float = raw / 2^32
        const double scale = 1.0 / 4294967296.0; // 1 / 2^32
        float x = (float)(rawX * scale);
        float y = (float)(rawY * scale);
        return new Vector3(x, 0f, y);             // XZ 平面
    }
}
```

> **关键**：`ViewFighter` 没有 `using FrameSync`，不引用 `FixedInt`。坐标转换通过已知的 Q32.32 格式自行计算。

---

## 5. 事件消费流程

显示层通过唯一接口——事件列表——获取所有信息：

```csharp
/// <summary>
/// 事件消费器接口。
/// 逻辑层将事件列表传递给显示层，显示层遍历后逻辑层清空。
/// </summary>
public interface IBattleEventConsumer
{
    void ConsumeEvents(List<BattleEvent> events);
}
```

### 推荐的接入方式

逻辑层（`BattleLogic`）暴露一个注册回调，或者显示层从公共事件队列读取：

```csharp
// 方式 A：回调注册（推荐）
// BattleLogic 中添加
public System.Action<List<BattleEvent>> OnEventsProduced;

// Tick 末尾
if (EventQueue.Count > 0)
{
    OnEventsProduced?.Invoke(EventQueue);
    EventQueue.Clear();
}
```

```csharp
// 方式 B：共享队列（简单直接）
// 显示层在 LateUpdate 中读取
void LateUpdate()
{
    if (_eventSource == null) return;
    foreach (var evt in _eventSource)
        HandleEvent(evt);
    // 逻辑层自行在 Update 末尾清空，或显示层通知清空
}
```

### 完整消费逻辑

```csharp
void ConsumeEvents(List<BattleEvent> events)
{
    foreach (var evt in events)
    {
        switch (evt.Type)
        {
            case BattleEventType.FighterSpawn:
                OnFighterSpawn(evt);
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
                OnBattleEnd(evt);
                break;
        }
    }
}

// ── 事件处理示例 ──

void OnFighterSpawn(BattleEvent evt)
{
    var vf = new ViewFighter();
    vf.InitFromEvent(evt);
    _viewFighters[evt.SourceId] = vf;

    // 创建 GameObject
    CreateFighterGO(vf);
}

void OnMove(BattleEvent evt)
{
    var vf = _viewFighters[evt.SourceId];
    if (vf == null) return;
    vf.TargetPos = ViewFighter.RawToWorld(evt.PosXRaw, evt.PosYRaw);
}

void OnHpChanged(BattleEvent evt)
{
    var vf = _viewFighters[evt.SourceId];
    if (vf == null) return;
    vf.CurrentHp = evt.IntParam;
}

void OnStateChanged(BattleEvent evt)
{
    var vf = _viewFighters[evt.SourceId];
    if (vf == null) return;
    vf.IsMoving  = (evt.IntParam & 1) != 0;
    vf.IsFleeing = (evt.IntParam & 2) != 0;
}

void OnDeath(BattleEvent evt)
{
    var vf = _viewFighters[evt.SourceId];
    if (vf == null) return;
    vf.IsDead = true;
}
```

---

## 6. 创建显示层的完整步骤

### 步骤 1：扩展事件类型

在 `BattleData.cs` 的 `BattleEventType` 枚举中添加 `FighterSpawn` 和 `StateChanged`（见[第 3 节](#3-扩展事件类型)）。

### 步骤 2：逻辑层发出新事件

- `BattleLogic.BeginCombat()`：创建 Fighter 后发出 `FighterSpawn`
- `BattleFighter.Tick()` 末尾：状态变化时发出 `StateChanged`

### 步骤 3：创建 ViewFighter 数据模型

```
Assets/Scripts/BattleView/ViewFighter.cs
```

见[第 4 节](#4-显示层数据模型)的完整代码。该文件不依赖 `FrameSync` 命名空间。

### 步骤 4：创建 BattleView 管理器

```
Assets/Scripts/BattleView/BattleView.cs
```

完整实现见下一节。

### 步骤 5：场景搭建

```
Scene Hierarchy:
├─ BattleManager (GameObject)
│   ├─ BattleEntry      (组件)  ← 网络 + 帧同步入口
│   ├─ BattleLogic      (组件)  ← 逻辑层
│   └─ BattleView       (组件)  ← 显示层（只读事件队列）
│       ├─ warriorPrefab → 拖入剑士预制体
│       └─ archerPrefab  → 拖入弓手预制体
├─ Main Camera
└─ ...
```

### 步骤 6：制作角色预制体

```
WarriorPrefab (或 ArcherPrefab)
├─ SpriteRenderer / MeshRenderer  ← 角色外观
├─ Canvas (World Space)
│   └─ HPBar (Slider)             ← 血条
└─ DamagePopupAnchor (空Transform) ← 飘字挂点
```

> 预制体上**不挂任何逻辑层脚本**，只有纯渲染组件。

---

## 7. 完整示例：SimpleBattleView

不依赖任何预制体、纯代码创建视觉的最简示例。**完全不引用 `BattleFighter`，只消费事件：**

```csharp
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using FrameSync; // 仅用于 BattleEvent / BattleEventType 结构体

/// <summary>
/// 纯事件驱动的战斗显示层。
/// 不持有 BattleFighter / BattleLogic 的引用，
/// 只通过事件队列获取数据，自建 ViewFighter 模型。
/// </summary>
public class SimpleBattleView : MonoBehaviour
{
    // ════════════════════════════════════════════
    //  显示层内部数据模型（从事件重建，不引用逻辑层）
    // ════════════════════════════════════════════

    class ViewFighter
    {
        public byte PlayerId;
        public byte CharType;        // 1=Warrior, 2=Archer
        public int MaxHp;
        public int CurrentHp;
        public Vector3 TargetPos;
        public Vector3 DisplayPos;
        public bool IsMoving;
        public bool IsFleeing;
        public bool IsDead;
        public GameObject Go;
        public Renderer Rend;
        public Color BaseColor;

        public static Vector3 RawToWorld(long rawX, long rawY)
        {
            const double scale = 1.0 / 4294967296.0;
            return new Vector3((float)(rawX * scale), 0.5f, (float)(rawY * scale));
        }
    }

    // ════════════════════════════════════════════
    //  字段
    // ════════════════════════════════════════════

    /// <summary>
    /// 事件源——唯一的逻辑层接口。
    /// 类型为 List&lt;BattleEvent&gt;，由外部（如 BattleEntry）赋值。
    /// 显示层仅遍历此列表，不调用逻辑层任何方法。
    /// </summary>
    [HideInInspector] public List<BattleEvent> EventSource;

    /// <summary>
    /// 清空事件的回调，由外部赋值。
    /// 显示层消费完事件后调用此回调通知逻辑层。
    /// </summary>
    [HideInInspector] public System.Action ClearEventsCallback;

    ViewFighter[] _fighters = new ViewFighter[3]; // 1-based
    int _winnerId;
    bool _battleEnded;

    // ════════════════════════════════════════════
    //  每帧更新
    // ════════════════════════════════════════════

    void LateUpdate()
    {
        // ── 1. 消费事件 ──
        if (EventSource != null)
        {
            foreach (var evt in EventSource)
                HandleEvent(evt);
            ClearEventsCallback?.Invoke();
        }

        // ── 2. 平滑插值（所有数据来自事件，非逻辑层读取）──
        for (int i = 1; i <= 2; i++)
        {
            var vf = _fighters[i];
            if (vf == null || vf.Go == null) continue;

            // 位置平滑
            vf.DisplayPos = Vector3.Lerp(vf.DisplayPos, vf.TargetPos, 10f * Time.deltaTime);
            vf.Go.transform.position = vf.DisplayPos;

            // 移动时压扁呼吸效果
            float scaleY = vf.IsMoving ? 0.7f : 1f;
            var s = vf.Go.transform.localScale;
            s.y = Mathf.Lerp(s.y, scaleY, 5f * Time.deltaTime);
            vf.Go.transform.localScale = s;
        }
    }

    // ════════════════════════════════════════════
    //  事件处理（唯一的数据来源）
    // ════════════════════════════════════════════

    void HandleEvent(BattleEvent evt)
    {
        switch (evt.Type)
        {
            case BattleEventType.FighterSpawn:
                OnFighterSpawn(evt);
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
                FlashColor(evt.TargetId, Color.white, 0.1f);
                break;

            case BattleEventType.UltimateCast:
                FlashColor(evt.TargetId, Color.yellow, 0.3f);
                break;

            case BattleEventType.Death:
                OnDeath(evt);
                break;

            case BattleEventType.BattleEnd:
                _battleEnded = true;
                _winnerId = evt.IntParam;
                break;
        }
    }

    void OnFighterSpawn(BattleEvent evt)
    {
        var vf = new ViewFighter
        {
            PlayerId  = evt.SourceId,
            CharType  = evt.TargetId,
            MaxHp     = evt.IntParam,
            CurrentHp = evt.IntParam,
            TargetPos = ViewFighter.RawToWorld(evt.PosXRaw, evt.PosYRaw),
        };
        vf.DisplayPos = vf.TargetPos;

        // 创建渲染对象
        vf.Go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        vf.Go.name = $"Fighter_P{vf.PlayerId}_{(vf.CharType == 1 ? "Warrior" : "Archer")}";
        vf.Go.transform.position = vf.DisplayPos;

        vf.Rend = vf.Go.GetComponent<Renderer>();
        vf.BaseColor = vf.CharType == 1
            ? new Color(0.8f, 0.2f, 0.2f)   // 红色=剑士
            : new Color(0.2f, 0.6f, 0.9f);   // 蓝色=弓手
        vf.Rend.material.color = vf.BaseColor;

        _fighters[evt.SourceId] = vf;
    }

    void OnMove(BattleEvent evt)
    {
        var vf = _fighters[evt.SourceId];
        if (vf == null) return;
        vf.TargetPos = ViewFighter.RawToWorld(evt.PosXRaw, evt.PosYRaw);
    }

    void OnHpChanged(BattleEvent evt)
    {
        var vf = _fighters[evt.SourceId];
        if (vf == null) return;
        vf.CurrentHp = evt.IntParam;

        // 血量比例 → 缩放 X 轴作为简易血条
        // （实际项目中替换为 Slider / Image.fillAmount）
    }

    void OnStateChanged(BattleEvent evt)
    {
        var vf = _fighters[evt.SourceId];
        if (vf == null) return;
        vf.IsMoving  = (evt.IntParam & 1) != 0;
        vf.IsFleeing = (evt.IntParam & 2) != 0;
    }

    void OnDeath(BattleEvent evt)
    {
        var vf = _fighters[evt.SourceId];
        if (vf == null) return;
        vf.IsDead = true;
        if (vf.Go != null)
        {
            vf.Go.transform.localScale = Vector3.one * 0.3f;
            vf.Rend.material.color = Color.gray;
        }
    }

    // ════════════════════════════════════════════
    //  视觉效果
    // ════════════════════════════════════════════

    void FlashColor(byte targetId, Color flashColor, float duration)
    {
        var vf = _fighters[targetId];
        if (vf == null || vf.Go == null) return;
        StartCoroutine(DoFlash(vf, flashColor, duration));
    }

    IEnumerator DoFlash(ViewFighter vf, Color flashColor, float dur)
    {
        if (vf.Rend == null) yield break;
        vf.Rend.material.color = flashColor;
        yield return new WaitForSeconds(dur);
        if (vf.Rend != null && !vf.IsDead)
            vf.Rend.material.color = vf.BaseColor;
    }

    void OnDestroy()
    {
        for (int i = 1; i <= 2; i++)
            if (_fighters[i]?.Go != null) Destroy(_fighters[i].Go);
    }
}
```

### 接入方式

在 `BattleEntry.Start()` 或类似入口中，将事件源桥接给显示层：

```csharp
// BattleEntry.cs 中
var view = gameObject.AddComponent<SimpleBattleView>();
view.EventSource = _logic.EventQueue;            // 传递列表引用
view.ClearEventsCallback = () => _logic.ClearEvents(); // 清空回调
```

> **注意**：显示层只持有 `List<BattleEvent>` 引用和一个 `Action` 回调——完全不知道 `BattleLogic` 或 `BattleFighter` 的存在。

---

## 8. 进阶：特效与动画

### 8.1 伤害飘字

```csharp
case BattleEventType.Damage:
    var vf = _fighters[evt.TargetId];
    if (vf?.Go == null) break;
    var pos = vf.Go.transform.position + Vector3.up;
    var popup = Instantiate(damagePopupPrefab, pos, Quaternion.identity);
    popup.GetComponent<TextMeshPro>().text = $"-{evt.IntParam}";
    Destroy(popup, 1f);
    break;
```

### 8.2 攻击弹道（弓手远程）

```csharp
case BattleEventType.NormalAttack:
    var src = _fighters[evt.SourceId];
    var tgt = _fighters[evt.TargetId];
    if (src == null || tgt == null) break;

    if (src.CharType == 2) // Archer
    {
        var arrow = Instantiate(arrowPrefab, src.DisplayPos, Quaternion.identity);
        StartCoroutine(FlyToTarget(arrow, tgt.DisplayPos, 0.3f));
    }
    else // Warrior
    {
        var slash = Instantiate(slashFxPrefab, src.DisplayPos, Quaternion.identity);
        Destroy(slash, 0.5f);
    }
    break;
```

> 注意：这里用 `src.CharType` 和 `src.DisplayPos`（ViewFighter 自建数据），而非逻辑层类型。

### 8.3 大招全屏特效

```csharp
case BattleEventType.UltimateCast:
    var caster = _fighters[evt.SourceId];
    if (caster == null) break;

    if (caster.CharType == 1) // Warrior
    {
        var trail = Instantiate(dashTrailPrefab);
        // 设置起点终点...
    }
    else // Archer
    {
        var rain = Instantiate(arrowRainPrefab, Vector3.zero, Quaternion.identity);
        Destroy(rain, 2f);
    }
    break;
```

### 8.4 状态驱动动画

```csharp
// 在 StateChanged 事件中更新 Animator
void OnStateChanged(BattleEvent evt)
{
    var vf = _fighters[evt.SourceId];
    if (vf == null) return;
    vf.IsMoving  = (evt.IntParam & 1) != 0;
    vf.IsFleeing = (evt.IntParam & 2) != 0;

    // 驱动动画
    if (_animators.TryGetValue(evt.SourceId, out var anim))
    {
        anim.SetBool("IsMoving", vf.IsMoving);
        anim.SetBool("IsFleeing", vf.IsFleeing);
    }
}

// 攻击/大招动画通过事件 Trigger 触发
void OnNormalAttack(BattleEvent evt)
{
    if (_animators.TryGetValue(evt.SourceId, out var anim))
        anim.SetTrigger("Attack");
}

void OnUltimateCast(BattleEvent evt)
{
    if (_animators.TryGetValue(evt.SourceId, out var anim))
        anim.SetTrigger("Ultimate");
}

void OnDeath(BattleEvent evt)
{
    if (_animators.TryGetValue(evt.SourceId, out var anim))
        anim.SetBool("IsDead", true);
}

// Animator Controller 状态机建议：
//   Idle ──(IsMoving=true)──▶ Run
//   Run  ──(IsMoving=false)──▶ Idle
//   Any  ──(IsFleeing=true)──▶ Flee (加速播放)
//   Any  ──(IsDead=true)──▶ Death
//   Any  ──(Attack trigger)──▶ Attack → Idle
//   Any  ──(Ultimate trigger)──▶ Ultimate → Idle
```

### 8.5 血条 UI

```csharp
// HpChanged 事件中更新血条（不读取逻辑层 Hp 属性）
void OnHpChanged(BattleEvent evt)
{
    var vf = _fighters[evt.SourceId];
    if (vf == null) return;
    vf.CurrentHp = evt.IntParam;

    // 更新 Slider
    if (_hpSliders.TryGetValue(evt.SourceId, out var slider))
    {
        float ratio = vf.MaxHp > 0 ? (float)vf.CurrentHp / vf.MaxHp : 0f;
        // 可直接赋值，或用协程平滑过渡
        StartCoroutine(AnimateHpBar(slider, ratio, 0.3f));
    }
}

IEnumerator AnimateHpBar(Slider slider, float target, float duration)
{
    float start = slider.value;
    float elapsed = 0f;
    while (elapsed < duration)
    {
        elapsed += Time.deltaTime;
        slider.value = Mathf.Lerp(start, target, elapsed / duration);
        yield return null;
    }
    slider.value = target;

    // 颜色
    var fill = slider.fillRect.GetComponent<Image>();
    fill.color = target > 0.5f ? Color.green
               : target > 0.2f ? Color.yellow
               : Color.red;
}
```

---

## 总结

### 事件 → 显示层映射一览

| 事件 | 显示层响应 | 数据来源 |
|------|-----------|---------|
| `FighterSpawn` | 创建 ViewFighter + GameObject，初始化外观/血条 | `SourceId`, `TargetId`(CharType), `IntParam`(MaxHp), `Pos` |
| `Move` | 更新 `ViewFighter.TargetPos`，每帧 Lerp 平滑 | `SourceId`, `PosXRaw/PosYRaw` |
| `HpChanged` | 更新 `ViewFighter.CurrentHp`，动画血条 | `SourceId`, `IntParam`(HP) |
| `StateChanged` | 更新 `IsMoving/IsFleeing`，切换 Animator 状态 | `SourceId`, `IntParam`(位掩码) |
| `NormalAttack` | 播放攻击动画/特效，近战挥砍或远程弹道 | `SourceId`, `TargetId` |
| `UltimateCast` | 播放大招特效 | `SourceId`, `TargetId`, `Pos` |
| `Damage` | 伤害飘字、受击闪白 | `TargetId`, `IntParam`(伤害) |
| `Death` | 死亡动画、灰化/隐藏 | `SourceId` |
| `BattleEnd` | 胜负结算 UI | `IntParam`(胜者ID) |

### 铁律

1. 显示层**不持有** `BattleFighter`、`BattleLogic` 的引用
2. 所有数据**仅从事件获取**，存入显示层自建的 `ViewFighter`
3. 坐标转换在显示层内部完成（`long` → `float`），不依赖 `FixedInt`
4. 事件在 `LateUpdate` 中消费，消费后通过回调通知清空
5. 逻辑层 → 事件队列 → 显示层，**单向**，**不可逆**
