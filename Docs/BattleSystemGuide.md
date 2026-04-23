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
   - 5.5 [BattleEntry.cs — 网络模式入口](#55-battleentrycs)
   - 5.6 [BattleView.cs — 显示层](#56-battleviewcs)
6. [战斗流程](#6-战斗流程)
7. [技能系统](#7-技能系统)
8. [Buff 系统](#8-buff-系统)
9. [被动技能系统](#9-被动技能系统)
10. [打断与僵直机制](#10-打断与僵直机制)
11. [弹射物系统](#11-弹射物系统)
12. [行为树系统](#12-行为树系统)
13. [网络帧同步](#13-网络帧同步)
14. [添加新角色 — 完整步骤](#14-添加新角色--完整步骤)
15. [添加新技能 — 完整步骤](#15-添加新技能--完整步骤)
16. [添加新技能类型 — 需要改代码](#16-添加新技能类型--需要改代码)
17. [常见问题与注意事项](#17-常见问题与注意事项)

---

## 1. 架构总览

```
┌──────────────────────────────────────────────────────┐
│      LocalBattleEntry / BattleEntry(网络)             │
│     (15Hz Tick 驱动, 输入采集 + 帧数据构建/接收)        │
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
│  │  ├ 技能/被动   │  │  │                             │
│  │  ├ Buff系统    │  │  │                             │
│  │  └ 碰撞/移动   │  │  │                             │
│  ├───────────────┤  │  │                             │
│  │ Projectile[]  │  │  │                             │
│  │ AoEProjectile │  │  │                             │
│  └───────────────┘  │  │                             │
└─────────────────────┘  └─────────────────────────────┘
             ▲                    ▲
             │ JSON               │ JSON
┌────────────┴────────────────────┴────────────────────┐
│  CharacterConfig.json    BehaviorTreeConfig.json     │
│  (角色数值+技能定义)       (AI行为树定义)               │
└──────────────────────────────────────────────────────┘

网络模式额外组件：
┌─────────────────┐         ┌─────────────────┐
│ FrameSyncClient │◄───TCP──►│ FrameSyncServer │
│  (客户端)        │  15Hz   │  (.NET 6 独立)   │
└─────────────────┘         └─────────────────┘
```

**关键设计原则：**

- **帧同步确定性** — 所有逻辑运算使用定点数 (`FixedInt`, `FixedVector2`)，随机数使用确定性 `BTRandom`，确保不同平台结果一致
- **逻辑/表现分离** — `BattleLogic` 产出 `BattleEvent` 事件流，`BattleView` 只消费事件不持有逻辑引用
- **数据驱动** — 角色数值、技能参数、被动技能、Buff效果、AI行为树均从 JSON 配置加载，不需要改代码即可调整
- **15Hz 固定帧率** — 逻辑以 `1/15秒` 为间隔，所有帧数单位基于此（如 Cooldown=15 表示 1 秒）

---

## 2. 核心概念

| 概念 | 说明 |
|------|------|
| **FixedInt** | Q32.32 定点数，`Raw` 为 `long`，`One = 1L << 32`。用 `FromFloat()`/`FromInt()` 创建 |
| **FixedVector2** | 定点数二维向量，提供 Distance/SqrDistance/Normalized/Dot/Cross 等运算 |
| **BattleEvent** | 值类型事件结构，每帧产生，包含 Frame/Type/SourceId/TargetId/IntParam/PosRaw |
| **BehaviorTree** | JSON 驱动的 AI 决策树，节点类型：selector / sequence / condition / action |
| **BTRandom** | 确定性伪随机数生成器，由种子初始化，确保帧同步一致性 |
| **Profession** | 职业枚举，决定索敌优先级：Warrior / Mage / Archer / Assassin / Support |
| **CharacterType** | 角色类型枚举：Warrior / Archer / Assassin / Mage / Snowman / Healer |
| **StateBits** | 位掩码表示角色状态（8 位）：Moving / Fleeing / Casting / CastUlt / Stunned / Stealthed / Slowed / Staggered |

---

## 3. 文件结构

```
Assets/
├── Resources/
│   ├── CharacterConfig.json      # 角色数值 + 技能定义 + 布阵 + 索敌优先级
│   └── BehaviorTreeConfig.json   # 每种角色的 AI 行为树
├── Scripts/
│   ├── Battle/
│   │   ├── BattleData.cs         # 枚举、结构体、配置加载器
│   │   ├── BattleFighter.cs      # 单角色逻辑（行为树、技能、移动、碰撞、被动、打断）
│   │   ├── BattleBuff.cs         # Buff类型定义、模板、运行时结构
│   │   ├── BattleLogic.cs        # 战斗主控（阶段管理、帧驱动）
│   │   ├── Projectile.cs         # 追踪弹射物（弓箭、击退箭、魔法球）
│   │   ├── AoEProjectile.cs      # 直线AoE弹射物（火球、冰球）
│   │   ├── LocalBattleEntry.cs   # 本地单人模式入口
│   │   └── BattleEntry.cs        # 网络模式入口
│   ├── BattleView/
│   │   └── BattleView.cs         # 纯事件驱动的显示层
│   └── Network/
│       └── FrameSync/
│           └── FrameSyncClient.cs # 帧同步客户端
├── Editor/
│   └── FixSpriteImport.cs        # 精灵导入设置工具
Tools/
└── FrameSyncServer/
    └── Program.cs                # .NET 6 帧同步服务器
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
| `InterruptChance` | int | 全局打断触发概率（百分比），普攻前摇或移动中受伤时的打断几率 |
| `Formation` | FormationPos[] | P1方布阵位置（P2方自动 X 取反镜像） |
| `TargetPriority` | array | 每个职业的索敌优先级列表 |

#### 技能定义 — Skills 数组

每个技能是独立定义的，角色通过 ID 引用。

| 字段 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `Id` | string | ✅ | 唯一标识，角色配置中引用此 ID |
| `Name` | string | ✅ | 显示名称 |
| `Type` | string | ✅ | 技能类型（见下表） |
| `Damage` | int | | 正数=伤害，负数=治疗 |
| `Range` | int | | 攻击范围（普攻用） |
| `Cooldown` | int | ✅ | 冷却帧数（15帧 = 1秒） |
| `Windup` | int | | 前摇帧数（施法开始到伤害结算） |
| `Recovery` | int | | 后摇帧数（伤害结算后到可行动） |
| `Param1` | int | | 技能特有参数1（含义因Type而异） |
| `Param2` | int | | 技能特有参数2 |
| `Param3` | int | | 技能特有参数3 |
| `TargetTeam` | string | | `"Enemy"` / `"Ally"` — 作用于敌方还是友方 |
| `TargetScope` | string | | `"Single"` / `"All"` / `"LowestHp"` — 单体/全体/血量最低 |
| `Buffs` | BuffConfig[] | | 技能附加的buff列表（减速、眩晕等） |

**已有技能类型及 Param 含义：**

| Type | 说明 | Param1 | Param2 |
|------|------|--------|--------|
| `MeleeAttack` | 近战攻击。Param1>0 为锥形AoE | AoE角度（度） | — |
| `RangedAttack` | 远程追踪弹射物普攻 | 弹射物速度 | — |
| `AoEProjectile` | 直线飞行AoE弹射物，碰到敌人或最大距离后爆炸 | 弹射物速度 | 爆炸半径 |
| `Instant` | 立即生效，根据TargetTeam/TargetScope选目标 | — | — |
| `Blink` | 瞬移到敌人身前 | — | — |
| `DashStrike` | 冲刺到敌人身前 + 伤害 | — | — |
| `KnockbackArrow` | 击退弹射物，命中后推开目标 | 弹射物速度 | 击退距离 |
| `Stealth` | 隐身（免疫索敌，仍受AoE伤害） | 隐身帧数 | — |
| `ReactBlink` | 被动瞬移，受攻击后自动后撤 | 瞬移距离 | — |
| `SummonPet` | 召唤召唤物（继承主人索敌规则） | 存活帧数 | 召唤物血量 |
| `FleeOnHit` | 被动：受击后向远离敌人方向后撤 | 后撤距离 | — |
| `CritStrike` | 被动：普攻概率造成双倍伤害 | 触发概率% | 伤害倍率% |
| `DodgeBlock` | 被动：受伤概率完全格挡 | 触发概率% | — |
| `UltCDReduce` | 被动：造成伤害概率减少大招CD | 触发概率% | 减少帧数 |
| `Lifesteal` | 被动：造成伤害概率回复血量 | 触发概率% | 吸血百分比% |

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
| `CollisionRadius` | float | 碰撞半径 |
| `StaggerDuration` | int | 被打断后僵直帧数（0=不可被打断） |
| `HeadIcon` | string | 头像精灵路径（Resources下） |
| `Profession` | string | 职业名（Warrior/Mage/Archer/Assassin/Support） |
| `Passive` | string | 被动技能 ID（引用 Skills 数组中的被动技能） |
| `NormalAttack` | string | 普攻技能 ID |
| `Skill2` | string | 副技能 ID |
| `Ultimate` | string | 大招技能 ID |

#### 当前角色一览

| 角色 | 职业 | HP | 移速 | 僵直帧 | 被动 | 普攻 | 副技能 | 大招 |
|------|------|-----|------|--------|------|------|--------|------|
| Warrior | 战士 | 2000 | 1 | 8 | 暴击(30%) | 斩击(锥形AoE) | 眩晕击 | 裂地斩(冲刺) |
| Archer | 射手 | 1500 | 3 | 12 | 受击后撤 | 射击(追踪弹) | 击退箭 | 箭雨(全图AoE) |
| Assassin | 刺客 | 1000 | 2 | 10 | 格挡(25%) | 刺击(单体) | 瞬移 | 隐身 |
| Mage | 法师 | 1200 | 2 | 15 | 奥术涌流(CD减少) | 火球术(AoE弹射) | 闪避瞬移(被动) | 召唤雪人 |
| Snowman | 法师(召唤物) | 500 | 1 | 0 | 无 | 冰球(AoE+减速) | — | — |
| Healer | 辅助 | 1300 | 2 | 12 | 生命汲取(30%) | 魔法球(追踪弹) | 治愈术(单体) | 圣光普照(群体) |

### 4.2 BehaviorTreeConfig.json

位于 `Assets/Resources/BehaviorTreeConfig.json`，所有角色共用一棵统一行为树（`Default`）。

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
| `Skill2Ready` | **复合条件**：CD就绪 + 根据技能类型自动判定使用时机（见下表） |
| `AtkCdReady` | 普攻 CD 就绪 |
| `FacingTarget` | 朝向对准目标（dot ≥ 0.707 ≈ 45°以内） |
| `AllyNeedsHeal` | 有友方需要治疗（仅友方技能有效） |

**`Skill2Ready` 内部判定逻辑：**

| 副技能类型 | 条件 |
|----------|------|
| 无副技能 | 始终 false |
| 被动反应式（ReactBlink） | 始终 false（由受击自动触发） |
| 友方技能（治疗） | CD好 + 队友需要治疗 |
| 突进类（Blink） | CD好 + 敌人在攻击范围外 |
| 其他战斗技能 | CD好 + 站定 + 敌人在范围内 + 朝向正确 |

#### 可用动作

| 动作名 | 说明 |
|--------|------|
| `DoFlee` | 远离敌人逃跑（返回 Running，跑够距离后完成） |
| `DoMoveToward` | 向目标靠近（返回 Running，到攻击范围内停止） |
| `ExecuteSkill2` | 释放副技能 |
| `BeginCastAtk` | 开始普攻施法（进入前摇） |

> **注意：** 大招 (`Ultimate`) **不在行为树中配置**。大招由 `Tick()` 层直接拦截处理——当 `UltRequested=true` 且 CD 就绪时，打断一切状态（包括僵直）立即施放。

#### 统一行为树结构

所有角色共用同一棵行为树，技能条件由代码根据配置自动判定：

```json
{
  "Default": {
    "type": "selector",
    "children": [
      {
        "_说明": "逃跑中：继续跑完",
        "type": "sequence",
        "children": [
          { "type": "condition", "check": ["IsFleeing"] },
          { "type": "action", "run": "DoFlee" }
        ]
      },
      {
        "_说明": "副技能：根据技能类型自动判断时机",
        "type": "sequence",
        "children": [
          { "type": "condition", "check": ["NotCasting", "Skill2Ready"] },
          { "type": "action", "run": "ExecuteSkill2" }
        ]
      },
      {
        "_说明": "普攻",
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
}
```

---

## 5. 代码结构详解

### 5.1 BattleData.cs

**数据定义层**，包含所有枚举、配置结构体和加载器。

#### 关键枚举

```csharp
enum CharacterType : byte { None=0, Warrior=1, Archer=2, Assassin=3, Mage=4, Snowman=5, Healer=6 }
enum Profession : byte    { None=0, Warrior=1, Mage=2, Archer=3, Assassin=4, Support=5 }
enum BattleEventType : byte {
    CharSelected, BattleStart, Move, NormalAttack, UltimateCast, Damage,
    HpChanged, Death, BattleEnd, FighterSpawn, StateChanged, PhaseChanged,
    CooldownUpdate, ProjectileSpawn, ProjectileHit, Skill2Cast, AoEExplosion,
    BuffApplied, HealApplied
}
```

#### 配置加载器

- **`CharacterConfig`** — 从 `CharacterConfig.json` 加载，提供:
  - `Get(CharacterType) → CharacterStats` — 获取角色数值
  - `TeamSize` / `ArenaHalf` / `InterruptChance` / `Formation` — 全局参数
  - `ParseProfession(string) → Profession` — 字符串→枚举
  - `GetTargetPriority(Profession) → Profession[]` — 索敌优先级
  - `Reload()` — 热重载配置

- **`SkillConfigLoader`** — 从 Skills 数组按 ID 索引:
  - `Get(string id) → SkillConfig`
  - `Reload()`

- **`BTConfigLoader`** — 从 `BehaviorTreeConfig.json` 加载统一行为树:
  - `Get(CharacterType) → BTNodeConfig` — 返回 `Default` 行为树（所有角色共用）

### 5.2 BattleFighter.cs

**单角色完整逻辑**，包含行为树执行、技能释放、被动技能、Buff管理、打断僵直、移动碰撞。

#### 初始化链路

```
Init(type, playerId, teamId, startPos)
  → 从 CharacterConfig.Get() 读取数值（含 StaggerDuration）
  → 从 SkillConfigLoader.Get() 读取三个技能 + 被动技能配置
  → 设置 _normalAtkType/_skill2Type/_ultType/_passiveType
  → 设置职业和索敌优先级
  → 副技能和大招 CD 初始满（开场不能立即释放）

BuildBT(allFighters, events)
  → 从 BTConfigLoader.Get() 读取 JSON 行为树
  → BuildBTFromConfig() 递归构建节点树
```

#### 每帧更新 (`Tick`)

```
1. 选择目标  → FindNearestEnemy()（职业优先级索敌，隐身敌人跳过）
2. CD 递减   → 普攻/大招/副技能
3. Buff 计时  → TickBuffs()（减速/眩晕到期移除，刷新 MoveSpeed/IsSlowed/IsStunned）
4. 隐身计时  → _stealthFramesLeft 倒计时
5. 僵直计时  → _staggerFramesLeft 倒计时，到 0 时解除 IsStaggered
6. 决策优先级：
   a. 大招请求 → UltRequested + CD好 → 打断一切状态（含僵直/眩晕），立即施放
   b. 僵直中   → 什么都不做，等待僵直结束
   c. 眩晕中   → 什么都不做，等待眩晕结束
   d. 施法锁定 → 前摇倒计时 → 伤害结算 → 后摇倒计时
   e. 行为树   → _bt.Tick() 做决策
7. 发送事件  → Move（位置+朝向）、StateChanged（状态位掩码）、CooldownUpdate
```

**状态位掩码（StateChanged 事件的 IntParam）：**

| 位 | 常量 | 含义 |
|----|------|------|
| bit0 | `StateBit_Moving` | 移动中 |
| bit1 | `StateBit_Fleeing` | 逃跑中 |
| bit2 | `StateBit_Casting` | 施法中 |
| bit3 | `StateBit_CastUlt` | 大招施法（仅 Casting 时有效） |
| bit4 | `StateBit_Stunned` | 眩晕 |
| bit5 | `StateBit_Stealthed` | 隐身 |
| bit6 | `StateBit_Slowed` | 减速 |
| bit7 | `StateBit_Staggered` | 僵直（打断） |
| bit8 | `StateBit_AtkBuffed` | 有增益战斗buff |
| bit9 | `StateBit_AtkDebuffed` | 有减益战斗buff |

#### 技能执行派发

每种技能类型在代码中有独立的 `switch/if` 分支处理：

- **`ExecuteNormalAttack`** — 按 `_normalAtkType` 派发：RangedAttack（追踪弹）、MeleeAttack（锥形AoE/单体）、AoEProjectile（直线AoE弹）
- **`ExecuteSkill2`** — 按 `_skill2Type` 派发：Blink / StunStrike / KnockbackArrow / Instant / ReactBlink
- **`ExecuteUltimate`** — 按 `_ultType` 派发：DashStrike / Stealth / ArrowRain / SummonPet / Instant / 默认单体

### 5.3 BattleLogic.cs

**战斗主控 MonoBehaviour**，管理三个阶段：

| 阶段 | 说明 |
|------|------|
| `Selecting` | 双方选角，MoveX 传递角色类型（1~6），选满 TeamSize 个后自动开始 |
| `Fighting` | 15Hz 驱动所有 BattleFighter，弹射物，碰撞分离，召唤物生命周期，胜负判定 |
| `Ended` | 战斗结束，显示结果 |

**战斗 Tick 流水线：**
1. 读取大招指令 → ButtonFire → 该队所有存活角色 `UltRequested = true`
2. 驱动所有角色 `BattleFighter.Tick()`
3. 收集新弹射物（`PendingProjectiles` / `PendingAoEProjectiles`）
4. 处理召唤请求（`PendingSummons`）
5. 驱动弹射物飞行
6. 碰撞分离（`ResolveCollisions`）
7. 召唤物生命周期（主人死亡→消失，存活时间到期→消失）
8. 胜负判定（一方所有非召唤物角色阵亡即败）

### 5.4 LocalBattleEntry.cs

**本地单人模式入口**，完全跳过网络，自己构建 FrameData 驱动逻辑帧。

- 按 15Hz 节奏本地构建 `FrameData`
- **P1/P2 手动选角**：先为 P2（对手）选 TeamSize 个角色，再为 P1（自己）选
- **输入缓冲**：`_pendingSelectionMx` 缓存 `GetKeyDown` 输入，解决按键 1 帧与 15Hz 逻辑不同步的丢失问题
- Space 键映射到 P1 的大招指令，P2 纯 AI 驱动

### 5.5 BattleEntry.cs

**网络模式入口**，通过 `FrameSyncClient` 连接帧同步服务器。

- 创建 `BattleLogic` + `FrameSyncClient` + `BattleView` 三个组件
- 连接 → F5 准备 → 选角 → 战斗 → Space 大招 → Esc 离开
- 由 FrameSyncClient 驱动逻辑帧（而非本地 Update）

### 5.6 BattleView.cs

**纯事件驱动的显示层**，关键设计：

- **不持有** `BattleFighter` / `BattleLogic` 引用
- 只消费 `List<BattleEvent>` 事件队列
- 自建 `ViewFighter` 数据模型，通过事件更新状态
- 位置平滑插值、朝向插值、状态颜色着色
- 死亡角色从 GUI 隐藏
- GUI 显示：血条、CD、状态标签（阵亡/僵直/眩晕/隐身/施法/移动等）、Buff 标签

**视觉效果映射：**

| 状态 | 视觉表现 |
|------|----------|
| 逃跑 | 闪红（橙色脉冲） |
| 眩晕 | 灰白闪烁 |
| 僵直 | 红白快速闪烁 |
| 隐身 | 半透明闪烁 |
| 减速 | 蓝色着色 |
| 攻击/大招 | 白色闪光 |

---

## 6. 战斗流程

```
游戏启动
  │
  ▼
┌─────────────────┐
│   选角阶段       │  本地模式：键盘 1~6 选角色
│   (Selecting)    │    先为P2选 TeamSize 个，再为P1选
│                  │  网络模式：各玩家独立选角
│                  │  双方各选满后 → BeginCombat
└────────┬────────┘
         ▼
┌─────────────────┐
│   战斗阶段       │  每 1/15 秒一帧：
│   (Fighting)     │    1. 读取大招输入
│                  │    2. 驱动所有 BattleFighter.Tick()
│                  │    3. 收集/驱动弹射物
│                  │    4. 处理召唤请求
│                  │    5. 碰撞分离
│                  │    6. 召唤物生命周期
│                  │    7. 胜负判定（某方全灭 → 结束）
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

每个角色最多 4 个技能槽：

| 槽位 | 字段 | 触发方式 | CD 初始状态 |
|------|------|----------|-------------|
| 普攻 | `NormalAttack` | 行为树 `BeginCastAtk` 自动释放 | 0（立即可用） |
| 副技能 | `Skill2` | 行为树 `ExecuteSkill2` 自动释放 | 满CD（开场不可用） |
| 大招 | `Ultimate` | 玩家按 Space 手动触发（`UltRequested`） | 满CD（开场不可用） |
| 被动 | `Passive` | 条件触发（受击/攻击时自动） | 无CD |

### 施法流程

```
BeginCast(isUlt) → 进入 Casting 状态
  │
  ├── 前摇 (Windup 帧) — 角色锁定不可移动
  │     └── 普攻前摇期间可被打断（见第10节）
  │
  ├── 伤害结算 — 调用 ExecuteNormalAttack/ExecuteSkill2/ExecuteUltimate
  │
  └── 后摇 (Recovery 帧) — 仍然锁定
```

### 伤害结算

`ApplyDamage(target, damage, frame)` 流程：
1. 格挡判定 → `target.TryDodgeBlock()`，成功则跳过伤害
2. 扣除血量 → 发送 Damage + HpChanged 事件
3. 攻击者被动触发 → `OnDealDamage()`（吸血、大招CD减少）
4. **打断判定** → `target.TryInterrupt()`（见第10节）
5. 目标被动/反应 → `TryReactSkill2()` + `TryPassive()`
6. 血量 ≤ 0 → 发送 Death 事件

---

## 8. Buff 系统

### 数据结构

```
BuffConfig (JSON配置) → BuffTemplate (运行时模板) → Buff (实例)
```

- **BuffConfig**：JSON 序列化用，`Type` (string) / `Duration` (int) / `Value` (float) / `IsDebuff` (bool)
- **BuffTemplate**：运行时解析后，`Value` 转为 `FixedInt`，含 `IsDebuff` 标记
- **Buff**：运行时实例，含 `Type` / `FramesLeft` / `Value` / `SourceId` / `IsDebuff`

### 已有 Buff 类型

| BuffType | 效果 | Value 含义 | 增益/减益 |
|----------|------|-----------|----------|
| `Slow` | 减速 | 0.5 = 移速变为 50% | 减益 |
| `Stun` | 眩晕（完全无法行动） | 不使用 | 减益 |
| `AtkUp` | 增加攻击力 | 0.3 = 攻击力提升 30% | 增益 |
| `DefUp` | 减少受到的伤害 | 0.3 = 受伤减少 30% | 增益 |
| `AtkSpeedUp` | 增加攻击速度（缩短普攻CD/前摇/后摇） | 0.3 = 攻速提升 30% | 增益 |
| `AtkSpeedDown` | 降低攻击速度（增加普攻CD/前摇/后摇） | 0.3 = 攻速降低 30% | 减益 |

### 增益/减益标记

每个 Buff 配置有 `IsDebuff` 字段标记增益或减益：
- `IsDebuff: false`（默认）= 增益 buff，视觉显示绿色 `[增益]` 标签
- `IsDebuff: true` = 减益 buff，视觉显示橙色 `[减益]` 标签

### 运行机制

- **AddBuff**：同类型 Buff 刷新持续时间（不叠加）；眩晕立即打断当前动作
- **TickBuffs**：每帧倒计时，到期移除；重算 MoveSpeed/AtkDamage/AtkCooldown/_atkWindup/_atkRecovery/DefReduction 等
- **RemoveBuff**：按类型移除（大招施放时移除眩晕）
- 任何技能都可通过 `Buffs` 数组配置附加 Buff
- **DefUp 减伤**：在 `ApplyDamage` 中按比例减少实际伤害（至少 1 点）
- **AtkSpeedUp/Down**：修改普攻的 CD、前摇、后摇帧数（攻速提升有 10% 下限）

### 配置示例

```json
"Buffs": [{"Type": "Slow", "Duration": 30, "Value": 0.5, "IsDebuff": true}]
```
表示：减益减速 30 帧（2秒），移速降为 50%。

```json
"Buffs": [{"Type": "AtkUp", "Duration": 45, "Value": 0.3, "IsDebuff": false}]
```
表示：增益攻击力提升 45 帧（3秒），攻击力提升 30%。

---

## 9. 被动技能系统

被动技能在 `Skills` 数组中定义，角色通过 `Passive` 字段引用 ID。被动技能无 Cooldown，由特定条件自动触发。

### 已有被动类型

| Type | 名称 | 触发时机 | 效果 |
|------|------|----------|------|
| `FleeOnHit` | 受击后撤 | 受到伤害后 | 向远离敌人方向后撤 Param1 距离 |
| `CritStrike` | 暴击 | 普攻伤害结算时 | Param1% 概率，伤害乘以 Param2%（如 200%=双倍） |
| `DodgeBlock` | 格挡 | 受到伤害时（含弹射物） | Param1% 概率完全免疫伤害 |
| `UltCDReduce` | 奥术涌流 | 造成伤害后（召唤物除外） | Param1% 概率减少大招CD Param2 帧 |
| `Lifesteal` | 生命汲取 | 造成伤害后（召唤物除外） | Param1% 概率回复伤害 Param2% 的血量 |

### 触发链路

- **受击被动**（DodgeBlock / FleeOnHit）：在 `ApplyDamage()` 和弹射物命中时判定
- **攻击者被动**（CritStrike）：在 `ExecuteNormalAttack()` 中伤害结算前判定
- **造成伤害后被动**（UltCDReduce / Lifesteal）：通过 `OnDealDamage()` 在伤害结算后触发，弹射物命中也会调用
- **反应式副技能**（ReactBlink）：受击后自动触发，有独立 CD

### 当前角色被动分配

| 角色 | 被动 | 参数 |
|------|------|------|
| Warrior | CritStrike | 30%概率，200%伤害 |
| Archer | FleeOnHit | 后撤4距离 |
| Assassin | DodgeBlock | 25%概率格挡 |
| Mage | UltCDReduce | 30%概率，减15帧 |
| Snowman | 无 | — |
| Healer | Lifesteal | 30%概率，吸血50% |

---

## 10. 打断与僵直机制

### 概述

角色在**普攻前摇**或**移动中**受到伤害时，有一定概率被打断，进入僵直状态。僵直期间角色完全无法行动。

### 配置

- **全局配置**：`InterruptChance`（百分比）— 打断触发概率，所有角色共用
- **角色配置**：`StaggerDuration`（帧数）— 每个角色独立的僵直持续时间，0 表示不可被打断

### 触发条件

打断判定在 `TryInterrupt()` 中执行，满足**全部**以下条件时才会判定：

1. 角色 `StaggerDuration > 0`（配置允许被打断）
2. 角色当前未僵直且未眩晕
3. 角色处于以下状态之一：
   - **普攻前摇中**（`IsCasting && !_castIsRecovery && !_castIsUlt`）
   - **移动中**（`IsMoving && !IsCasting`）
4. 随机数判定通过（`BTRandom.Next(100) < InterruptChance`）

### 不可打断的状态

- 副技能施法中（行为树触发的 Skill2）
- 大招施法中（前摇/后摇均不可打断）
- 普攻后摇中
- 站立/待机状态
- 已经处于僵直或眩晕中

### 触发效果

打断成功后：
1. 设置 `IsStaggered = true`，开始 `_staggerFramesLeft` 倒计时
2. 取消当前动作（`IsCasting = false`，`IsMoving = false`，`IsFleeing = false`）
3. 重置行为树（`_bt.ResetTree()`）

### 僵直期间

- 角色完全无法行动（与眩晕类似）
- 行为树不执行
- **大招可打破僵直**：如果玩家按下大招且 CD 就绪，会清除僵直状态并立即施放

### 触发位置

`TryInterrupt()` 在以下三处被调用：
- `ApplyDamage()` — 直接伤害（近战/即时技能）
- `Projectile.Tick()` — 追踪弹命中
- `AoEProjectile.Explode()` — AoE 爆炸伤害

### 当前配置值

| 角色 | StaggerDuration | 约等于 |
|------|----------------|--------|
| Warrior | 8帧 | ~0.5秒（战士护甲厚，僵直短） |
| Archer | 12帧 | ~0.8秒 |
| Assassin | 10帧 | ~0.67秒 |
| Mage | 15帧 | 1秒（法师脆弱，僵直长） |
| Snowman | 0帧 | 不可被打断 |
| Healer | 12帧 | ~0.8秒 |

---

## 11. 弹射物系统

### 追踪弹射物 (Projectile)

用于远程普攻（弓箭、魔法球）和击退箭。

- **飞行方式**：以固定速度追踪目标当前位置，自动转向
- **命中判定**：当前帧步长 ≥ 距目标距离时命中
- **免疫判定**：隐身目标免疫伤害，弹射物消失
- **伤害流程**：格挡判定 → 扣血 → 攻击者被动 → 打断判定 → 目标被动/反应
- **击退**：`KnockbackDist > 0` 时沿飞行方向推开目标，受场地边界限制
- 击退箭不触发目标的反应式副技能和被动

### AoE 弹射物 (AoEProjectile)

用于法师火球术和雪人冰球。

- **飞行方式**：固定方向直线飞行（不追踪）
- **爆炸触发**：碰到敌人碰撞体 / 飞出场地 / 超过最大飞行距离
- **爆炸效果**：对爆炸半径内所有敌人造成伤害
- **Buff 施加**：命中后可施加配置的 Buff（如冰球的减速效果）
- **同样触发**：格挡、攻击者被动、打断、目标反应式技能/被动

### 弹射物生命周期

```
BattleFighter 创建弹射物 → 加入 PendingProjectiles/PendingAoEProjectiles
  → BattleLogic 收集并管理
  → 每帧 Tick 驱动飞行
  → 命中/爆炸后移除
```

---

## 12. 行为树系统

### 统一行为树设计

所有角色共用一棵行为树（`Default`），技能可以与任意角色自由搭配。行为树通过 `Skill2Ready` 复合条件自动适配不同技能类型的释放时机，无需按角色单独配置。

### 执行规则

- **Selector（选择）**：依次尝试子节点，第一个返回 Success 或 Running 的生效；记住 Running 的子节点索引，下帧直接继续
- **Sequence（顺序）**：所有子节点必须 Success，任一 Failure 则整体 Failure
- **Condition（条件）**：`check[]` 数组所有条件 AND 判断
- **Action（动作）**：执行指定行为，返回 Success 或 Running
- **ResetTree**：被打断/僵直/大招施放时重置行为树状态（清除 Running 记忆）

### 统一树优先级

```
selector
  ├── [逃跑中？] → 继续逃跑
  ├── [副技能条件？] → 释放副技能（Skill2Ready 自动判断时机）
  ├── [普攻条件？] → 开始普攻
  └── 兜底 → 追击/移动
```

`Skill2Ready` 会根据副技能类型自动判断：
- **无副技能 / 被动反应式**（ReactBlink）→ 永远跳过
- **友方技能**（治疗）→ 队友需要治疗时触发
- **突进类**（Blink）→ 敌人在攻击范围外时触发
- **其他战斗技能** → 站定 + 敌人在范围内 + 朝向正确时触发

### 确定性保证

- 行为树使用 `BTRandom`（确定性伪随机），种子由 PlayerId 派生
- 所有概率判定（被动、打断等）均通过 `_bt.Context.Random.Next()` 进行
- 确保不同客户端同帧同结果

---

## 13. 网络帧同步

### 架构

```
客户端 A ──TCP──┐
                ├── FrameSyncServer (15Hz) ──广播 FrameData──→ 所有客户端
客户端 B ──TCP──┘
```

### 服务器 (FrameSyncServer)

- **运行环境**：.NET 6 独立控制台应用
- **启动**：`dotnet run -- [port=9100] [tickRate=15]`
- **房间**：最多 4 人，所有人 Ready 后自动开始
- **延迟模拟**：每条 FrameData 随机延迟 30~550ms 发送（模拟网络抖动），控制消息立即发送
- **输入合并**：同一 tick 内，非零 MoveX 不被后续零值覆盖，Buttons 按位 OR 合并
- **Tick 驱动**：Timer 以 `1000/TickRate` ms 间隔触发，收集输入并广播
- **控制台命令**：`quit` / `list` / `start`（强制开始） / `end`（强制结束）

### 客户端 (FrameSyncClient)

- **连接流程**：`Idle → Connecting → InRoom → Playing → Ended`
- **帧缓冲**：收到的 FrameData 存入 `SortedList<int, FrameData>`，按序消费
- **追帧**：每 Update 最多消费 5 帧，防止延迟累积后一次性执行过多
- **延迟监测**：`FrameDelay = ServerFrame - CurrentFrame`

### 协议

- TCP 自定义协议，4 字节长度头
- 消息类型：JoinRoom / JoinRoomAck / RoomSnapshot / Ready / GameStart / FrameData / GameEnd

---

## 14. 添加新角色 — 完整步骤

### 步骤 1：修改枚举 (BattleData.cs)

```csharp
enum CharacterType : byte { ..., NewChar = 7 }
```

如需新职业也在 `Profession` 枚举中添加。

### 步骤 2：定义技能 (CharacterConfig.json)

在 `Skills` 数组中添加新角色的技能（普攻/副技能/大招/被动）。

### 步骤 3：配置角色数值 (CharacterConfig.json)

```json
"NewChar": {
  "MaxHp": 1500,
  "MoveSpeed": 2,
  "TurnSpeed": 10,
  "CollisionRadius": 0.5,
  "StaggerDuration": 10,
  "HeadIcon": "HeadIcons/newchar",
  "Profession": "Warrior",
  "Passive": "some_passive_id",
  "NormalAttack": "newchar_atk",
  "Skill2": "newchar_skill2",
  "Ultimate": "newchar_ult"
}
```

### 步骤 4：配置索敌优先级 (CharacterConfig.json)

在 `TargetPriority` 中添加新角色的职业优先级，并更新其他职业的优先级列表。

### 步骤 5：配置行为树 (BehaviorTreeConfig.json)

添加新角色的 AI 行为树。参考近战（Warrior）或远程（Archer）模板。

### 步骤 6：修改代码 (BattleData.cs)

在 `CharacterConfig.ConfigRoot` 中添加字段：
```csharp
public CharacterStats NewChar;
```

在 `CharacterConfig` 中添加静态字段、`Get()` switch 分支、`EnsureLoaded()` 赋值和回退默认值。

### 步骤 7：修改显示层 (BattleView.cs)

为新角色添加颜色和显示名配置。

### 步骤 8：更新 Formation（如果 TeamSize 增加）

在 `Formation` 数组中添加新的布阵位置。

---

## 15. 添加新技能 — 完整步骤

**如果新技能的 Type 是已有类型**（如 MeleeAttack, RangedAttack, Instant, AoEProjectile 等），只需改配置：

### 步骤 1：在 Skills 数组中添加技能定义

```json
{
  "Id": "power_slash", "Name": "强力斩",
  "Type": "MeleeAttack",
  "Damage": 120, "Range": 2, "Cooldown": 20, "Windup": 3, "Recovery": 2,
  "Param1": 90,
  "TargetTeam": "Enemy", "TargetScope": "Single",
  "Buffs": [{"Type": "Slow", "Duration": 15, "Value": 0.6}]
}
```

### 步骤 2：在角色配置中引用

```json
"Skill2": "power_slash"
```

### 步骤 3：调整行为树条件（如有需要）

如果新技能的释放条件与原技能不同，更新行为树中对应节点的 `check` 条件。

**完成！** 不需要修改任何代码。

---

## 16. 添加新技能类型 — 需要改代码

如果需要全新的技能机制，则需要修改代码：

### 步骤 1：定义技能 (CharacterConfig.json)

```json
{
  "Id": "poison_cloud", "Name": "毒雾",
  "Type": "PoisonCloud",
  "Cooldown": 90,
  "Param1": 45, "Param2": 10
}
```

### 步骤 2：实现技能逻辑 (BattleFighter.cs)

根据技能所属槽位，在对应的执行方法中添加分支：

- **普攻** → 在 `ExecuteNormalAttack` 中添加 `else if (_normalAtkType == "PoisonCloud")`
- **副技能** → 在 `ExecuteSkill2` 中添加
- **大招** → 在 `ExecuteUltimate` 中添加

### 步骤 3：添加新事件类型（如需要）

在 `BattleEventType` 枚举中添加新事件类型，在 `BattleView.cs` 中处理对应的表现。

### 步骤 4：添加新 Buff 类型（如需要）

1. 在 `BattleBuff.cs` 的 `BuffType` 枚举中添加新类型
2. 在 `BuffTemplate.ParseType()` 中添加解析
3. 在 `BattleFighter.TickBuffs()` 中添加效果逻辑
4. 在 `BattleFighter.AddBuff()` 中添加立即效果（如果有）

### 步骤 5：添加新状态位（如需要）

1. `BattleFighter` 中添加 `StateBit_XXX` 常量（当前使用到 bit7）
2. 添加对应的 bool 标志和计时字段
3. 在 `Tick()` 的 curState 计算中添加新位
4. 在 `BattleView.OnStateChanged()` 中解析新位

### 步骤 6：添加新行为树条件/动作（如需要）

- 新条件：在 `EvalCondition(string)` 的 switch 中添加
- 新动作：在 `ResolveAction(string)` 的 switch 中添加

### 步骤 7：添加新被动技能类型（如需要）

1. 在 `Skills` 数组中定义新被动（Type 为新类型名）
2. 在 `BattleFighter` 中选择触发点：
   - 受击时 → `TryPassive()` 中添加
   - 攻击时 → `ExecuteNormalAttack()` 中添加
   - 造成伤害后 → `OnDealDamage()` 中添加
3. 角色配置中设置 `Passive` 引用

---

## 17. 常见问题与注意事项

### 帧数单位

所有时间相关配置都以 **帧** 为单位，基于 15Hz：
- 15 帧 = 1 秒
- 30 帧 = 2 秒
- 8 帧 ≈ 0.53 秒

### 定点数

- 所有逻辑层数值使用 `FixedInt`（Q32.32 定点数），确保帧同步确定性
- `CollisionRadius` 在 JSON 中为 float，加载后用 `FixedInt.FromFloat()` 转换
- 其他 int 字段用 `FixedInt.FromInt()` 转换

### 碰撞相关

- **碰撞半径** (`CollisionRadius`)：影响推挤分离和移动避障
- **有效攻击距离** = 角色中心距离 - 目标碰撞半径（`EffectiveEnemyDist()`）
- `ResolveCollisions()` 每帧对所有存活角色做两两碰撞分离

### 技能 CD 初始状态

- **普攻**：开场 CD = 0（立即可用）
- **副技能和大招**：开场 CD = 满值（需等待CD转好）
- 这防止了开场瞬间所有角色同时释放大招

### 召唤物特殊规则

- 召唤物有 `Master` 引用和 `LifetimeLeft` 生存计时
- 主人死亡 → 召唤物立即消失
- 召唤物不触发主人的攻击者被动（UltCDReduce/Lifesteal）
- 召唤物按 `IsSummon` 标记在胜负判定时排除

### 打断与眩晕的区别

| | 打断（僵直） | 眩晕 |
|--|------------|------|
| 触发方式 | 受伤时概率触发 | Buff 施加 |
| 可打断状态 | 普攻前摇/移动中 | 任何时候 |
| 持续时间来源 | 角色 StaggerDuration 配置 | Buff Duration 配置 |
| 大招能否打破 | ✅ | ✅ |
| 状态位 | bit7 (StateBit_Staggered) | bit4 (StateBit_Stunned) |
| 副技能/大招施法中 | 不可被打断 | 可被眩晕 |

### JSON 注释约定

JSON 不支持注释，使用 `_` 前缀字段作为说明：
```json
"_Param1说明": "弹射物飞行速度"
```
这些字段在代码加载时会被自动忽略（JsonUtility 不映射无对应字段的 key）。

### 行为树调试

所有角色共用一棵统一行为树（`Default`），技能条件由 `Skill2Ready` 复合条件根据技能类型自动判定。行为树使用 `selector` 记住上一帧 Running 的子节点索引，下帧优先从该节点继续。这意味着一旦进入"追击"状态（DoMoveToward 返回 Running），不会每帧重新从头评估——直到追击完成后才回到顶层选择。打断和大招会调用 `ResetTree()` 清除这一记忆。

### BattleView 与逻辑层隔离

BattleView **完全不引用** BattleFighter。如果新角色/技能需要特殊表现效果，应通过发送新的 `BattleEvent` 传递信息，在 BattleView 中消费处理。
