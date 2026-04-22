# 帧同步战斗系统 — 架构说明与扩展指南

> 本文档详细描述战斗系统的整体架构、配置方法、代码结构，以及**如何添加新角色和新技能**。

---

## 目录

1. [架构总览](#1-架构总览)
2. [核心概念](#2-核心概念)
3. [文件结构](#3-文件结构)
4. [配置文件详解](#4-配置文件详解)
   - 4.1 [CharacterConfig.json — 角色与技能配置](#41-characterconfigjson)
   - 4.2 [BehaviorTreeConfig.json — 行为树AI配置](#42-behaviortreeconfigjson)
5. [代码结构详解](#5-代码结构详解)
   - 5.1 [BattleData.cs — 数据定义层](#51-battledatacs)
   - 5.2 [BattleFighter.cs — 战斗角色逻辑](#52-battlefightercs)
   - 5.3 [BattleLogic.cs — 战斗主控](#53-battlelogiccs)
   - 5.4 [LocalBattleEntry.cs — 本地入口](#54-localbattleentrycs)
   - 5.5 [BattleView.cs — 显示层](#55-battleviewcs)
6. [战斗流程](#6-战斗流程)
7. [技能系统](#7-技能系统)
8. [行为树系统](#8-行为树系统)
9. [添加新角色 — 完整步骤](#9-添加新角色--完整步骤)
10. [添加新技能 — 完整步骤](#10-添加新技能--完整步骤)
11. [添加新技能类型 — 需要改代码](#11-添加新技能类型--需要改代码)
12. [常见问题与注意事项](#12-常见问题与注意事项)

---

## 1. 架构总览

```
┌──────────────────────────────────────────────────────┐
│                 LocalBattleEntry                     │
│          (15Hz Tick 驱动, 输入采集+转发)               │
└────────────┬────────────────────┬────────────────────┘
             │ OnLogicUpdate()    │ EventQueue
             ▼                    ▼
┌─────────────────────┐  ┌─────────────────────────────┐
│    BattleLogic      │  │       BattleView            │
│  (选角→战斗→结算)    │  │  (纯事件驱动, 不持有逻辑引用)  │
│                     │  │                             │
│  ┌───────────────┐  │  │  ViewFighter[64]            │
│  │ BattleFighter │  │  │  ViewProjectile[]           │
│  │ × N (每角色)   │  │  │                             │
│  │  ├ BehaviorTree│  │  │  消费 BattleEvent 驱动表现   │
│  │  ├ 技能执行    │  │  │                             │
│  │  └ 碰撞/移动   │  │  │                             │
│  └───────────────┘  │  │                             │
└─────────────────────┘  └─────────────────────────────┘
             ▲                    ▲
             │ JSON               │ JSON
┌────────────┴────────────────────┴────────────────────┐
│  CharacterConfig.json    BehaviorTreeConfig.json     │
│  (角色数值+技能定义)       (AI行为树定义)               │
└──────────────────────────────────────────────────────┘
```

**关键设计原则：**

- **帧同步确定性** — 所有逻辑运算使用定点数 (`FixedInt`, `FixedVector2`)，确保不同平台结果一致
- **逻辑/表现分离** — `BattleLogic` 产出 `BattleEvent` 事件流，`BattleView` 只消费事件不持有逻辑引用
- **数据驱动** — 角色数值、技能参数、AI行为树均从 JSON 配置加载，不需要改代码即可调整
- **15Hz 固定帧率** — 逻辑以 `1/15秒` 为间隔，所有帧数单位基于此（如 Cooldown=15 表示 1 秒）

---

## 2. 核心概念

| 概念 | 说明 |
|------|------|
| **FixedInt** | Q32.32 定点数，`Raw` 为 `long`，`One = 1L << 32`。用 `FromFloat()`/`FromInt()` 创建 |
| **FixedVector2** | 定点数二维向量，提供 Distance/SqrDistance/Normalized/Dot/Cross 等运算 |
| **BattleEvent** | 值类型事件结构，每帧产生，包含 Frame/Type/SourceId/TargetId/IntParam/PosRaw |
| **BehaviorTree** | JSON 驱动的 AI 决策树，节点类型：selector（选择）、sequence（顺序）、condition（条件）、action（动作） |
| **Profession** | 职业枚举，决定索敌优先级。当前有：Warrior/Mage/Archer/Assassin/Support |
| **CharacterType** | 角色类型枚举，对应具体角色（Warrior/Archer/Assassin） |
| **StateBits** | 位掩码表示角色状态：Moving/Fleeing/Casting/CastUlt/Stunned/Stealthed |

---

## 3. 文件结构

```
Assets/
├── Resources/
│   ├── CharacterConfig.json      # 角色数值 + 技能定义 + 布阵 + 索敌优先级
│   └── BehaviorTreeConfig.json   # 每种角色的 AI 行为树
├── Scripts/
│   └── Battle/
│       ├── BattleData.cs         # 枚举、结构体、配置加载器
│       ├── BattleFighter.cs      # 单角色逻辑（行为树、技能、移动、碰撞）
│       ├── BattleLogic.cs        # 战斗主控（阶段管理、帧驱动）
│       └── LocalBattleEntry.cs   # 本地单人模式入口
└── Scripts/
    └── BattleView/
        └── BattleView.cs         # 纯事件驱动的显示层
```

---

## 4. 配置文件详解

### 4.1 CharacterConfig.json

位于 `Assets/Resources/CharacterConfig.json`，由 `CharacterConfig` 静态类加载。

#### 全局设置

| 字段 | 类型 | 说明 |
|------|------|------|
| `ArenaHalf` | int | 场地半边长（正方形场地，坐标范围 [-ArenaHalf, ArenaHalf]） |
| `TeamSize` | int | 每队角色数量（双方各选 TeamSize 个） |
| `Formation` | FormationPos[] | P1方布阵位置（P2方自动 X 取反镜像） |

#### 技能定义 — Skills 数组

每个技能是独立定义的，角色通过 ID 引用。

| 字段 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `Id` | string | ✅ | 唯一标识，角色配置中引用此 ID |
| `Name` | string | ✅ | 显示名称 |
| `Type` | string | ✅ | 技能类型（见下表） |
| `Damage` | int | | 伤害值（0 = 不造成伤害） |
| `Range` | int | | 攻击范围（普攻用） |
| `Cooldown` | int | ✅ | 冷却帧数（15帧 = 1秒） |
| `Windup` | int | | 前摇帧数（施法开始到伤害结算） |
| `Recovery` | int | | 后摇帧数（伤害结算后到可行动） |
| `Param1` | int | | 技能特有参数1（含义因Type而异） |
| `Param2` | int | | 技能特有参数2 |

**已有技能类型及 Param 含义：**

| Type | 说明 | Param1 | Param2 |
|------|------|--------|--------|
| `MeleeAttack` | 近战攻击。Param1>0 为锥形AoE | AoE角度（度） | — |
| `RangedAttack` | 远程弹射物普攻 | 弹射物速度 | — |
| `Blink` | 瞬移到敌人身前 | — | — |
| `KnockbackArrow` | 击退弹射物 | 弹射物速度 | 击退距离 |
| `StunStrike` | 伤害 + 眩晕 | 眩晕帧数 | — |
| `DashStrike` | 冲刺到敌人身前 + 伤害 | — | — |
| `ArrowRain` | 全图 AoE 伤害 | — | — |
| `Stealth` | 隐身（免疫伤害） | 隐身帧数 | — |

#### 索敌优先级 — TargetPriority 数组

每个职业可配置索敌优先级列表。数组为空时按最近距离索敌。

```json
{"Profession": "Assassin", "Priority": ["Archer", "Mage", "Support", "Assassin", "Warrior"]}
```
表示刺客优先攻击射手 > 法师 > 辅助 > 刺客 > 战士。

#### 角色配置

每个角色以 CharacterType 枚举名为 key（如 `"Warrior"`, `"Archer"`）。

| 字段 | 类型 | 说明 |
|------|------|------|
| `MaxHp` | int | 最大血量 |
| `MoveSpeed` | int | 移动速度 |
| `TurnSpeed` | int | 转向速度（弧度/秒） |
| `SafeDistance` | int | 安全距离（远程角色拉扯用，0=不拉扯） |
| `FleeDistance` | int | 逃跑距离（被攻击后跑多远，0=不逃跑） |
| `CollisionRadius` | float | 碰撞半径 |
| `Profession` | string | 职业名（对应 Profession 枚举） |
| `NormalAttack` | string | 普攻技能 ID |
| `Skill2` | string | 副技能 ID |
| `Ultimate` | string | 大招技能 ID |

### 4.2 BehaviorTreeConfig.json

位于 `Assets/Resources/BehaviorTreeConfig.json`，每个角色类型一棵行为树。

#### 节点类型

| 类型 | 说明 |
|------|------|
| `selector` | 选择节点 — 依次尝试子节点，第一个返回 Success/Running 的生效 |
| `sequence` | 顺序节点 — 所有子节点依次执行，任一 Failure 则中止 |
| `condition` | 条件节点 — `check` 数组中所有条件 AND 判断 |
| `action` | 动作节点 — `run` 指定执行的动作名 |

#### 可用条件

| 条件名 | 含义 |
|--------|------|
| `IsFleeing` | 正在逃跑中 |
| `NotCasting` | 不在施法中（前/后摇） |
| `NotMoving` | 不在移动中 |
| `EnemyInAtkRange` | 目标在普攻范围内（已扣除碰撞半径） |
| `EnemyOutOfAtkRange` | 目标在普攻范围外 |
| `Skill2CdReady` | 副技能 CD 就绪 |
| `AtkCdReady` | 普攻 CD 就绪 |
| `FacingTarget` | 朝向对准目标（dot ≥ 0.707 ≈ 45°以内） |

#### 可用动作

| 动作名 | 说明 |
|--------|------|
| `DoFlee` | 远离敌人逃跑（返回 Running，跑够 FleeDistance 后完成） |
| `DoMoveToward` | 向目标靠近（返回 Running，到攻击范围内停止） |
| `ExecuteSkill2` | 释放副技能 |
| `BeginCastAtk` | 开始普攻施法（进入前摇） |

> **注意：** 大招 (`Ultimate`) **不在行为树中配置**。大招由 `Tick()` 层直接拦截处理——当 `UltRequested=true` 且 CD 就绪时，打断一切状态立即施放。

#### 行为树模板示例

```json
{
  "type": "selector",
  "children": [
    {
      "_说明": "逃跑中：继续跑",
      "type": "sequence",
      "children": [
        { "type": "condition", "check": ["IsFleeing"] },
        { "type": "action", "run": "DoFlee" }
      ]
    },
    {
      "_说明": "副技能：条件满足时释放",
      "type": "sequence",
      "children": [
        { "type": "condition", "check": ["Skill2CdReady", "NotCasting", "其他条件..."] },
        { "type": "action", "run": "ExecuteSkill2" }
      ]
    },
    {
      "_说明": "普攻：停下+射程内+CD好+朝向敌人",
      "type": "sequence",
      "children": [
        { "type": "condition", "check": ["NotMoving", "NotCasting", "EnemyInAtkRange", "AtkCdReady", "FacingTarget"] },
        { "type": "action", "run": "BeginCastAtk" }
      ]
    },
    {
      "_说明": "兜底：追击",
      "type": "action",
      "run": "DoMoveToward"
    }
  ]
}
```

---

## 5. 代码结构详解

### 5.1 BattleData.cs

**数据定义层**，包含所有枚举、配置结构体和加载器。

#### 关键枚举

```csharp
enum CharacterType : byte { None=0, Warrior=1, Archer=2, Assassin=3 }
enum Profession : byte    { None=0, Warrior=1, Mage=2, Archer=3, Assassin=4, Support=5 }
enum BattleEventType : byte { CharSelected, BattleStart, Move, NormalAttack, ... }
```

#### 配置加载器

- **`CharacterConfig`** — 从 `CharacterConfig.json` 加载，提供:
  - `Get(CharacterType) → CharacterStats` — 获取角色数值
  - `TeamSize` / `ArenaHalf` / `Formation` — 全局参数
  - `ParseProfession(string) → Profession` — 字符串→枚举
  - `GetTargetPriority(Profession) → Profession[]` — 索敌优先级
  - `Reload()` — 热重载配置

- **`SkillConfigLoader`** — 从 Skills 数组按 ID 索引:
  - `Get(string id) → SkillConfig`
  - `Reload()`

- **`BTConfigLoader`** — 从 `BehaviorTreeConfig.json` 加载行为树:
  - `Get(CharacterType) → BTNodeConfig`

### 5.2 BattleFighter.cs

**单角色完整逻辑**，包含行为树执行、技能释放、移动碰撞。

#### 初始化链路

```
Init(type, playerId, teamId, startPos)
  → 从 CharacterConfig.Get() 读取数值
  → 从 SkillConfigLoader.Get() 读取三个技能配置
  → 设置 _normalAtkType/_skill2Type/_ultType
  → 设置职业和索敌优先级

BuildBT(allFighters, events)
  → 从 BTConfigLoader.Get() 读取 JSON 行为树
  → BuildBTFromConfig() 递归构建节点树
```

#### 每帧更新 (`Tick`)

```
1. 选择目标  → FindNearestEnemy()（优先级索敌）
2. CD 递减   → 普攻/大招/副技能
3. 状态计时  → 隐身/眩晕倒计时
4. 大招检查  → UltRequested + CD好 → 打断一切，立即施放（最高优先级）
5. 眩晕中    → 跳过行为树
6. 施法锁定  → 前摇倒计时 → 伤害结算 → 后摇倒计时
7. 行为树    → _bt.Tick() 做决策
8. 发送事件  → Move事件（位置+朝向）、状态变化、CD变化
```

#### 技能执行派发

每种技能类型在代码中有独立的 `switch/if` 分支处理：

- **`ExecuteNormalAttack`** — 按 `_normalAtkType` 派发：RangedAttack（弹射物）、MeleeAttack（锥形AoE/单体）
- **`ExecuteSkill2`** — 按 `_skill2Type` 派发：Blink/StunStrike/KnockbackArrow
- **`ExecuteUltimate`** — 按 `_ultType` 派发：DashStrike/Stealth/ArrowRain/默认单体

### 5.3 BattleLogic.cs

**战斗主控 MonoBehaviour**，管理三个阶段：

| 阶段 | 说明 |
|------|------|
| `Selecting` | 双方选角，每帧读输入，选满 TeamSize 个后进入战斗 |
| `Fighting` | 15Hz 驱动所有 BattleFighter，碰撞分离，胜负判定 |
| `Ended` | 战斗结束，显示结果 |

### 5.4 LocalBattleEntry.cs

**本地单人模式入口**，职责：
- 创建 BattleLogic + BattleView 组件
- 以 15Hz 频率驱动逻辑帧（float 累加器）
- P1 输入采集 + 缓存（解决 GetKeyDown 与 15Hz 不同步的丢失问题）
- P2 自动选角（Bot）

### 5.5 BattleView.cs

**纯事件驱动的显示层**，关键设计：
- **不持有** `BattleFighter` / `BattleLogic` 引用
- 只消费 `List<BattleEvent>` 事件队列
- 自建 `ViewFighter` 数据模型，通过事件更新状态
- 位置平滑插值、攻击闪光、CD 显示等纯表现逻辑

---

## 6. 战斗流程

```
游戏启动
  │
  ▼
┌─────────────────┐
│   选角阶段       │  P1: 按键 1/2/3 选角色（Warrior/Archer/Assassin）
│   (Selecting)    │  P2: Bot 自动选角
│                  │  双方各选 TeamSize 个后 → BeginCombat
└────────┬────────┘
         ▼
┌─────────────────┐
│   战斗阶段       │  每 1/15 秒一帧：
│   (Fighting)     │    1. 读取大招输入（Space → 全队 UltRequested）
│                  │    2. 驱动所有 BattleFighter.Tick()
│                  │    3. 驱动弹射物
│                  │    4. 碰撞分离
│                  │    5. 胜负判定（某方全灭 → 结束）
└────────┬────────┘
         ▼
┌─────────────────┐
│   结束阶段       │  发送 BattleEnd 事件
│   (Ended)        │  显示胜负结果
└─────────────────┘
```

---

## 7. 技能系统

### 技能槽位

每个角色有 3 个技能槽：

| 槽位 | 字段 | 触发方式 |
|------|------|----------|
| 普攻 | `NormalAttack` | 行为树 `BeginCastAtk` 自动释放 |
| 副技能 | `Skill2` | 行为树 `ExecuteSkill2` 自动释放 |
| 大招 | `Ultimate` | 玩家按 Space 手动触发（`UltRequested`），打断一切状态 |

### 施法流程

```
BeginCast() → 进入 Casting 状态
  │
  ├── 前摇 (Windup 帧) — 角色锁定不可移动
  │
  ├── 伤害结算 — 调用 ExecuteNormalAttack/ExecuteSkill2/ExecuteUltimate
  │
  └── 后摇 (Recovery 帧) — 仍然锁定
```

### 伤害结算

`ApplyDamage(target, damage, frame)`:
1. 隐身目标免疫
2. 扣除血量
3. 发送 Damage + HpChanged 事件
4. 目标 FleeDistance > 0 且不在施法 → 触发逃跑
5. 血量 ≤ 0 → 发送 Death 事件

---

## 8. 行为树系统

### 执行规则

- **Selector（选择）**：依次尝试子节点，第一个返回 Success 或 Running 的生效；记住 Running 的子节点索引，下帧直接继续
- **Sequence（顺序）**：所有子节点必须 Success，任一 Failure 则整体 Failure
- **Condition（条件）**：`check[]` 数组所有条件 AND 判断
- **Action（动作）**：执行指定行为，返回 Success 或 Running

### 设计模式

典型的角色行为树遵循以下优先级模式：

```
selector
  ├── [逃跑中？] → 继续逃跑
  ├── [副技能条件？] → 释放副技能
  ├── [普攻条件？] → 开始普攻
  └── 兜底 → 追击/移动
```

副技能的条件根据技能特性不同：
- **近战副技能**（如眩晕击）：需要 `EnemyInAtkRange` + `FacingTarget`
- **远程副技能**（如瞬移）：需要 `EnemyOutOfAtkRange`（距离远时才使用）

---

## 9. 添加新角色 — 完整步骤

以添加一个 **法师 (Mage)** 为例：

### 步骤 1：修改枚举 (BattleData.cs)

```csharp
// CharacterType 枚举添加新值
enum CharacterType : byte { None=0, Warrior=1, Archer=2, Assassin=3, Mage=4 }
```

> `Profession` 枚举中已有 `Mage=2`，无需修改。如需新职业则也要添加。

### 步骤 2：定义技能 (CharacterConfig.json)

在 `Skills` 数组中添加法师的技能：

```json
{
  "Id": "mage_atk", "Name": "火球术",
  "Type": "RangedAttack",
  "Damage": 60, "Range": 5, "Cooldown": 15, "Windup": 4, "Recovery": 3,
  "Param1": 6, "_Param1说明": "弹射物飞行速度"
},
{
  "Id": "frost_nova", "Name": "霜冻新星",
  "Type": "StunStrike",
  "Damage": 40, "Cooldown": 60,
  "Param1": 20, "_Param1说明": "眩晕帧数"
},
{
  "Id": "meteor", "Name": "陨石术",
  "Type": "ArrowRain",
  "Damage": 200, "Cooldown": 135, "Windup": 10, "Recovery": 5
}
```

### 步骤 3：配置角色数值 (CharacterConfig.json)

添加角色配置块：

```json
"_Mage说明": "法师 — 远程法师，低血量中移速，远程弹射物普攻，副技能范围眩晕，大招全图陨石",
"Mage": {
  "MaxHp": 1200,
  "MoveSpeed": 2,
  "TurnSpeed": 10,
  "SafeDistance": 4,
  "FleeDistance": 3,
  "CollisionRadius": 0.5,
  "Profession": "Mage", "_职业": "法师",
  "NormalAttack": "mage_atk", "_普攻": "火球术（远程弹射物）",
  "Skill2": "frost_nova", "_副技能": "霜冻新星（范围眩晕）",
  "Ultimate": "meteor", "_大招": "陨石术（全图AoE）"
}
```

### 步骤 4：更新 Formation（如果 TeamSize 增加）

如果 TeamSize 增大，需在 `Formation` 数组中添加新的布阵位置。

### 步骤 5：配置索敌优先级 (CharacterConfig.json)

在 `TargetPriority` 中添加法师的索敌优先级，并更新其他职业的优先级列表：

```json
{"Profession": "Mage", "_职业": "法师", "Priority": ["Mage", "Support", "Archer", "Assassin", "Warrior"], "_优先级": "法师>辅助>射手>刺客>战士"}
```

### 步骤 6：配置行为树 (BehaviorTreeConfig.json)

添加法师的 AI 行为树（远程角色模板，类似 Archer）：

```json
"Mage": {
  "type": "selector",
  "children": [
    {
      "_说明": "逃跑中：继续跑",
      "type": "sequence",
      "children": [
        { "type": "condition", "check": ["IsFleeing"] },
        { "type": "action", "run": "DoFlee" }
      ]
    },
    {
      "_说明": "副技能：霜冻新星（射程内释放）",
      "type": "sequence",
      "children": [
        { "type": "condition", "check": ["Skill2CdReady", "NotCasting", "NotMoving", "EnemyInAtkRange", "FacingTarget"] },
        { "type": "action", "run": "ExecuteSkill2" }
      ]
    },
    {
      "_说明": "普攻：火球术",
      "type": "sequence",
      "children": [
        { "type": "condition", "check": ["NotMoving", "NotCasting", "EnemyInAtkRange", "AtkCdReady", "FacingTarget"] },
        { "type": "action", "run": "BeginCastAtk" }
      ]
    },
    {
      "_说明": "兜底：靠近敌人",
      "type": "action",
      "run": "DoMoveToward"
    }
  ]
}
```

### 步骤 7：修改代码 — 配置加载器 (BattleData.cs)

在 `CharacterConfig.ConfigRoot` 中添加字段：

```csharp
public CharacterStats Mage;
```

在 `CharacterConfig.Get()` 的 switch 中添加：

```csharp
case CharacterType.Mage: return _mage;
```

在 `Load()` 中添加加载和回退默认值。

### 步骤 8：修改选角输入 (BattleLogic.cs / LocalBattleEntry.cs)

在 `SampleLocalInput()` 中添加新的选角按键映射（如按键 4 选法师）。

### 步骤 9：修改显示层 (BattleView.cs)

为新角色添加颜色和显示名配置（在创建 ViewFighter 的代码中）。

---

## 10. 添加新技能 — 完整步骤

**如果新技能的 Type 是已有类型**（如 MeleeAttack, RangedAttack, StunStrike 等），只需改配置：

### 步骤 1：在 Skills 数组中添加技能定义

```json
{
  "Id": "power_slash", "Name": "强力斩",
  "Type": "MeleeAttack",
  "Damage": 120, "Range": 2, "Cooldown": 20, "Windup": 3, "Recovery": 2,
  "Param1": 90, "_Param1说明": "锥形AoE角度（90度=四分之一圆）"
}
```

### 步骤 2：在角色配置中引用

将角色的 `NormalAttack`、`Skill2` 或 `Ultimate` 设为新技能 ID：

```json
"Skill2": "power_slash"
```

### 步骤 3：调整行为树条件（如有需要）

如果新技能的释放条件与原技能不同，更新行为树中对应节点的 `check` 条件。

**完成！** 不需要修改任何代码。

---

## 11. 添加新技能类型 — 需要改代码

如果需要全新的技能机制（如"召唤物"、"持续伤害"、"治疗"等），则需要修改代码：

### 步骤 1：定义技能 (CharacterConfig.json)

```json
{
  "Id": "heal_wave", "Name": "治愈波",
  "Type": "HealWave",
  "Cooldown": 90,
  "Param1": 200, "_Param1说明": "治疗量"
}
```

### 步骤 2：实现技能逻辑 (BattleFighter.cs)

根据技能所属槽位，在对应的执行方法中添加分支：

**如果是普攻类型** — 在 `ExecuteNormalAttack` 中添加：
```csharp
else if (_normalAtkType == "HealWave")
{
    // 实现治疗逻辑
}
```

**如果是副技能类型** — 在 `ExecuteSkill2` 中添加：
```csharp
else if (_skill2Type == "HealWave")
{
    // 实现治疗逻辑
}
```

**如果是大招类型** — 在 `ExecuteUltimate` 中添加：
```csharp
else if (_ultType == "HealWave")
{
    // 实现治疗逻辑
}
```

### 步骤 3：添加新事件类型（如需要）

在 `BattleEventType` 枚举中添加新事件类型，在 `BattleView.cs` 中处理对应的表现。

### 步骤 4：添加新状态位（如需要）

如果技能引入新状态（如"中毒"），需要：
1. `BattleFighter` 中添加 `StateBit_Poisoned` 常量
2. 添加对应的计时字段和状态 flag
3. 在 `Tick()` 中添加计时逻辑
4. 在行为树条件中添加新条件名（如 `IsPoisoned`）

### 步骤 5：添加新行为树条件/动作（如需要）

- 新条件：在 `EvalCondition(string)` 的 switch 中添加
- 新动作：在 `ResolveAction(string)` 的 switch 中添加

---

## 12. 常见问题与注意事项

### 帧数单位

所有时间相关配置都以 **帧** 为单位，基于 15Hz：
- 15 帧 = 1 秒
- 30 帧 = 2 秒
- 45 帧 = 3 秒

### 定点数

- 所有逻辑层数值使用 `FixedInt`（Q32.32 定点数），确保帧同步确定性
- `CollisionRadius` 在 JSON 中为 float，加载后用 `FixedInt.FromFloat()` 转换
- 其他 int 字段用 `FixedInt.FromInt()` 转换

### 碰撞相关

- **碰撞半径** (`CollisionRadius`)：影响推挤分离和移动避障
- **有效攻击距离** = 角色中心距离 - 目标碰撞半径（`EffectiveEnemyDist()`）
- `ResolveCollisions()` 每帧对所有存活角色做两两碰撞分离

### 弓手类角色（SafeDistance / FleeDistance > 0）

- `SafeDistance`：理想射击距离，弓手会保持在此距离外
- `FleeDistance`：被攻击后逃跑的距离，逃跑途中不攻击
- 设为 0 的角色不会逃跑（如战士、刺客）

### JSON 注释约定

JSON 不支持注释，使用 `_` 前缀字段作为说明：
```json
"_Param1说明": "弹射物飞行速度"
```
这些字段在代码加载时会被自动忽略（JsonUtility 不映射无对应字段的 key）。

### 行为树调试

行为树使用 `selector` 记住上一帧 Running 的子节点索引，下帧优先从该节点继续。这意味着一旦进入"追击"状态（DoMoveToward 返回 Running），不会每帧重新从头评估——直到追击完成后才回到顶层选择。

### BattleView 与逻辑层隔离

BattleView **完全不引用** BattleFighter。如果新角色/技能需要特殊表现效果，应通过发送新的 `BattleEvent` 传递信息，在 BattleView 中消费处理。
