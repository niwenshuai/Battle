# 帧同步游戏框架 — 使用说明文档

> **适用 Unity 版本**: 2022.3+  
> **命名空间**: `FrameSync`  
> **核心原则**: 全确定性 — 禁止浮点运算、`UnityEngine.Random`、`Time.deltaTime`

---

## 目录

- [1. 架构总览](#1-架构总览)
- [2. 定点数学库 FixedMath](#2-定点数学库-fixedmath)
  - [2.1 FixedInt (Q32.32)](#21-fixedint-q3232)
  - [2.2 FixedVector2](#22-fixedvector2)
- [3. TCP 网络层](#3-tcp-网络层)
  - [3.1 PacketBuffer](#31-packetbuffer)
  - [3.2 TcpConnection](#32-tcpconnection)
  - [3.3 NetworkManager](#33-networkmanager)
- [4. 帧同步协议与客户端](#4-帧同步协议与客户端)
  - [4.1 协议定义 FrameSyncProto](#41-协议定义-framesyncproto)
  - [4.2 IGameLogic 接口](#42-igamelogic-接口)
  - [4.3 FrameSyncClient](#43-framesyncclient)
  - [4.4 帧同步服务器](#44-帧同步服务器)
  - [4.5 快速启动 Demo](#45-快速启动-demo)
- [5. 定点数物理系统](#5-定点数物理系统)
  - [5.1 碰撞体类型 FixedColliders](#51-碰撞体类型-fixedcolliders)
  - [5.2 碰撞检测 FixedPhysics](#52-碰撞检测-fixedphysics)
  - [5.3 射线检测 Raycasting](#53-射线检测-raycasting)
  - [5.4 扫掠检测 Sweep Tests](#54-扫掠检测-sweep-tests)
  - [5.5 转向行为 FixedSteering](#55-转向行为-fixedsteering)
- [6. NavMesh 寻路系统](#6-navmesh-寻路系统)
  - [6.1 数据结构 FixedNavMeshData](#61-数据结构-fixednavmeshdata)
  - [6.2 A* 寻路 + 漏斗算法](#62-a-寻路--漏斗算法)
  - [6.3 NavMesh Agent](#63-navmesh-agent)
  - [6.4 NavMesh Baker 编辑器工具](#64-navmesh-baker-编辑器工具)
- [7. 行为树系统](#7-行为树系统)
  - [7.1 核心概念](#71-核心概念)
  - [7.2 组合节点 Composites](#72-组合节点-composites)
  - [7.3 装饰节点 Decorators](#73-装饰节点-decorators)
  - [7.4 叶节点 Leaves](#74-叶节点-leaves)
  - [7.5 流式构建器 BTBuilder](#75-流式构建器-btbuilder)
  - [7.6 行为树运行器 BehaviorTree](#76-行为树运行器-behaviortree)
  - [7.7 完整 AI 示例](#77-完整-ai-示例)
- [8. 文件索引](#8-文件索引)

---

## 1. 架构总览

```
┌──────────────────────────────────────────────────────┐
│                    游戏逻辑层                         │
│  ┌─────────────┐  ┌────────────┐  ┌───────────────┐ │
│  │  行为树 BT   │  │  NavMesh   │  │  Physics      │ │
│  │  (AI 决策)   │  │  (寻路)     │  │  (碰撞/转向)  │ │
│  └──────┬──────┘  └─────┬──────┘  └──────┬────────┘ │
│         └───────────┬───┴─────────────────┘          │
│                     │                                │
│         ┌───────────▼──────────────┐                 │
│         │    IGameLogic 接口       │                 │
│         │  (确定性逻辑更新)         │                 │
│         └───────────┬──────────────┘                 │
├─────────────────────┼────────────────────────────────┤
│                     │     帧同步层                    │
│         ┌───────────▼──────────────┐                 │
│         │    FrameSyncClient       │                 │
│         │  (帧缓冲 / 追帧 / 输入)  │                 │
│         └───────────┬──────────────┘                 │
├─────────────────────┼────────────────────────────────┤
│                     │     网络层                      │
│         ┌───────────▼──────────────┐                 │
│         │    NetworkManager        │                 │
│         │  (重连 / 心跳 / 事件)    │                 │
│         └───────────┬──────────────┘                 │
│         ┌───────────▼──────────────┐                 │
│         │    TcpConnection         │                 │
│         │  (Socket / 收发线程)     │                 │
│         └──────────────────────────┘                 │
└──────────────────────────────────────────────────────┘
              FixedInt / FixedVector2
           (全部计算使用定点数，确定性)
```

所有游戏逻辑层的运算均使用 `FixedInt`（Q32.32 定点数）和 `FixedVector2`，保证在不同平台上的确定性。

---

## 2. 定点数学库 FixedMath

> **文件**: `Assets/Scripts/Network/FrameSync/FixedMath.cs`

### 2.1 FixedInt (Q32.32)

64 位定点数，高 32 位整数、低 32 位小数。精度 ≈ 2.3×10⁻¹⁰，范围 ±2,147,483,647。

#### 创建方式

```csharp
// ✅ 正确方式
FixedInt a = FixedInt.FromInt(5);           // 整数 → 定点数
FixedInt b = FixedInt.FromFloat(1.5f);      // ⚠ 仅用于初始化常量，运行时禁用
FixedInt c = FixedInt.FromRaw(0x180000000); // 从原始 long 值创建

// 预定义常量
FixedInt.Zero;    // 0
FixedInt.OneVal;  // 1
FixedInt.Half;    // 0.5
FixedInt.NegOne;  // -1
```

#### 四则运算

```csharp
FixedInt a = FixedInt.FromInt(10);
FixedInt b = FixedInt.FromInt(3);

FixedInt sum  = a + b;   // 13
FixedInt diff = a - b;   // 7
FixedInt prod = a * b;   // 30（乘法内部拆高低 32 位防溢出）
FixedInt quot = a / b;   // 3.3333...（除零返回 MaxValue/MinValue）
FixedInt neg  = -a;      // -10
```

#### 比较运算

```csharp
bool eq = a == b;   // false
bool gt = a > b;    // true
// 同理: !=, <, <=, >=
```

#### 数学函数

```csharp
FixedInt.Abs(FixedInt.NegOne);         // 1
FixedInt.Min(a, b);                     // 较小值
FixedInt.Max(a, b);                     // 较大值
FixedInt.Clamp(val, min, max);          // 范围钳制
FixedInt.Sqrt(FixedInt.FromInt(16));    // 4（牛顿迭代法）
```

#### 调试输出

```csharp
int whole = a.ToInt();       // 截断取整
float view = a.ToFloat();   // ⚠ 仅用于渲染/调试，不可用于逻辑判断
```

### 2.2 FixedVector2

确定性 2D 向量，基于 `FixedInt`。

#### 创建方式

```csharp
var v = new FixedVector2(FixedInt.FromInt(3), FixedInt.FromInt(4));

// 预定义常量
FixedVector2.Zero;   // (0, 0)
FixedVector2.One;    // (1, 1)
FixedVector2.Up;     // (0, 1)
FixedVector2.Down;   // (0, -1)
FixedVector2.Left;   // (-1, 0)
FixedVector2.Right;  // (1, 0)
```

#### 向量运算

```csharp
var a = new FixedVector2(FixedInt.FromInt(3), FixedInt.FromInt(4));
var b = new FixedVector2(FixedInt.FromInt(1), FixedInt.FromInt(2));
FixedInt s = FixedInt.FromInt(2);

var sum   = a + b;            // (4, 6)
var diff  = a - b;            // (2, 2)
var scale = a * s;            // (6, 8)
var div   = a / s;            // (1.5, 2)
var neg   = -a;               // (-3, -4)
```

#### 属性与方法

```csharp
FixedInt sqrMag = a.SqrMagnitude;   // 25 (3²+4²)
FixedInt mag    = a.Magnitude;       // 5  (√25)
FixedVector2 n  = a.Normalized;      // (0.6, 0.8)
FixedVector2 p  = a.Perpendicular;   // (-4, 3) 逆时针90°

// 静态方法
FixedInt dot   = FixedVector2.Dot(a, b);       // 11
FixedInt cross = FixedVector2.Cross(a, b);     // 2 (正=b在a逆时针方向)
FixedInt dist  = FixedVector2.Distance(a, b);
FixedInt sqDst = FixedVector2.SqrDistance(a, b);
FixedVector2 l = FixedVector2.Lerp(a, b, FixedInt.Half);
FixedVector2 c = FixedVector2.ClampMagnitude(a, FixedInt.FromInt(3));
FixedVector2 r = FixedVector2.Reflect(dir, normal);
```

---

## 3. TCP 网络层

### 3.1 PacketBuffer

> **文件**: `Assets/Scripts/Network/PacketBuffer.cs`

处理 TCP 粘包/拆包的缓冲区。协议格式：**`[4字节大端长度][payload]`**。

```csharp
var buffer = new PacketBuffer();

// 收到 Socket 数据时追加
buffer.Append(rawBytes, 0, bytesRead);

// 循环取出完整包
while (buffer.TryReadPacket(out byte[] payload))
{
    // 处理 payload
}
```

| 常量 | 值 | 说明 |
|------|-----|------|
| `HeaderSize` | 4 | 包头长度 |
| `InitialSize` | 4096 | 初始缓冲区大小 |
| `MaxBufferSize` | 4 MB | 防止异常数据撑爆内存 |

### 3.2 TcpConnection

> **文件**: `Assets/Scripts/Network/TcpConnection.cs`

底层 TCP 连接封装，独立收发线程 + `ConcurrentQueue<NetEvent>` 线程安全事件投递。

```csharp
var conn = new TcpConnection();

// 连接
conn.Connect("127.0.0.1", 9000);

// 主线程轮询事件
while (conn.EventQueue.TryDequeue(out var evt))
{
    switch (evt.Type)
    {
        case EventType.Connected:     /* 连接成功 */ break;
        case EventType.DataReceived:  /* evt.Data */ break;
        case EventType.Disconnected:  /* 断开 */    break;
        case EventType.Error:         /* evt.Message */ break;
    }
}

// 发送（线程安全，自动加 4 字节长度头）
conn.Send(payloadBytes);

// 关闭
conn.Close();
```

### 3.3 NetworkManager

> **文件**: `Assets/Scripts/Network/NetworkManager.cs`

MonoBehaviour 单例，封装 `TcpConnection`，提供自动重连（指数退避）和可选心跳。

#### 配置（Inspector）

| 字段 | 默认值 | 说明 |
|------|--------|------|
| `_host` | `"127.0.0.1"` | 服务器地址 |
| `_port` | `9000` | 服务器端口 |
| `_autoReconnect` | `true` | 是否自动重连 |
| `_reconnectBaseDelay` | `1f` | 重连基础延迟（秒） |
| `_reconnectMaxDelay` | `30f` | 重连最大延迟（秒） |
| `_maxReconnectTimes` | `10` | 最大重连次数 |
| `_heartbeatEnabled` | `false` | 是否启用心跳 |
| `_heartbeatInterval` | `15f` | 心跳间隔（秒） |

#### 事件

```csharp
NetworkManager.Instance.OnConnected     += () => Debug.Log("已连接");
NetworkManager.Instance.OnDisconnected  += reason => Debug.Log($"断开: {reason}");
NetworkManager.Instance.OnDataReceived  += data => HandlePacket(data);
NetworkManager.Instance.OnError         += msg => Debug.LogError(msg);
NetworkManager.Instance.OnReconnecting  += (attempt, delay) => Debug.Log($"第 {attempt} 次重连");
```

#### API

```csharp
// 连接
NetworkManager.Instance.Connect();
NetworkManager.Instance.Connect("192.168.1.100", 9000);

// 发送
NetworkManager.Instance.Send(payloadBytes);
NetworkManager.Instance.Send("hello");

// 断开
NetworkManager.Instance.Disconnect();

// 状态
bool connected = NetworkManager.Instance.IsConnected;
NetworkManager.State state = NetworkManager.Instance.CurrentState;
```

---

## 4. 帧同步协议与客户端

### 4.1 协议定义 FrameSyncProto

> **文件**: `Assets/Scripts/Network/FrameSync/FrameSyncProto.cs`

#### 消息类型

| 方向 | MsgType | 值 | 说明 |
|------|---------|-----|------|
| C→S | `JoinRoom` | 0x01 | 加入房间 |
| C→S | `LeaveRoom` | 0x02 | 离开房间 |
| C→S | `PlayerReady` | 0x03 | 准备就绪 |
| C→S | `PlayerInput` | 0x10 | 上传输入 |
| S→C | `JoinRoomAck` | 0x81 | 加入确认（返回 PlayerId） |
| S→C | `RoomSnapshot` | 0x82 | 房间快照 |
| S→C | `GameStart` | 0x83 | 游戏开始 |
| S→C | `GameEnd` | 0x84 | 游戏结束 |
| S→C | `FrameData` | 0x90 | 逻辑帧数据 |

#### PlayerInput 结构

```csharp
struct PlayerInput  // 13 bytes
{
    byte PlayerId;
    int  MoveX;     // 定点数 Raw
    int  MoveY;     // 定点数 Raw
    uint Buttons;   // 位标志
}

// 按钮常量
PlayerInput.ButtonFire  = 1 << 0;  // 开火
PlayerInput.ButtonJump  = 1 << 1;  // 跳跃
PlayerInput.ButtonSkill = 1 << 2;  // 技能
```

#### FrameData 结构

```csharp
struct FrameData
{
    int FrameId;
    PlayerInput[] Inputs;  // 所有玩家在该帧的输入
}
```

#### 打包/解析

```csharp
// 打包（C→S）
byte[] packet = Proto.PackJoinRoom("PlayerName");
byte[] packet = Proto.PackPlayerReady();
byte[] packet = Proto.PackPlayerInput(input);

// 解析（S→C）
MsgType type = Proto.PeekType(data);
using var reader = Proto.BodyReader(data);
var ack = /* 按协议读取字段 */;
```

### 4.2 IGameLogic 接口

> **文件**: `Assets/Scripts/Network/FrameSync/IGameLogic.cs`

所有游戏逻辑必须实现此接口，由 `FrameSyncClient` 驱动。

```csharp
public interface IGameLogic
{
    /// <summary>游戏开始，初始化状态。</summary>
    void OnGameStart(int playerCount, byte localPlayerId, int randomSeed);

    /// <summary>每个逻辑帧调用，处理所有玩家输入。</summary>
    void OnLogicUpdate(FrameData frame);

    /// <summary>游戏结束。</summary>
    void OnGameEnd(byte winnerId);

    /// <summary>采集本地玩家输入（每帧调用）。</summary>
    PlayerInput SampleLocalInput();
}
```

**实现规则（违反将破坏同步）：**

| ❌ 禁止 | ✅ 替代方案 |
|---------|------------|
| `float` / `double` 做状态计算 | `FixedInt` / `FixedVector2` |
| `UnityEngine.Random` / `System.Random` | `BTRandom` 或自建 LCG（使用 `randomSeed`） |
| `Time.deltaTime` | 框架提供的固定 `tickInterval` |
| `Physics` / `Physics2D` | `FixedPhysics` 静态方法 |
| `NavMesh.CalculatePath` | `FixedNavMeshPathfinder` |
| 访问网络/文件系统 | 通过黑板传递外部数据 |

### 4.3 FrameSyncClient

> **文件**: `Assets/Scripts/Network/FrameSync/FrameSyncClient.cs`

帧同步客户端 MonoBehaviour，管理房间流程和帧驱动。

#### 配置

| 字段 | 默认值 | 说明 |
|------|--------|------|
| `_host` | `"127.0.0.1"` | 服务器地址 |
| `_port` | `9100` | 服务器端口 |
| `_playerName` | `"Player"` | 玩家名 |
| `_maxCatchUpPerFrame` | `5` | 每个 Update 最多追帧数量 |

#### 生命周期

```
Connect → JoinRoom → JoinRoomAck → [等待其他玩家] → Ready →
GameStart → [逻辑帧循环] → GameEnd
```

#### 使用方式

```csharp
public class MyGame : MonoBehaviour, IGameLogic
{
    FrameSyncClient _sync;

    void Start()
    {
        _sync = GetComponent<FrameSyncClient>();
        _sync.Init(this);

        // 监听事件
        _sync.OnGameStarted += seed => Debug.Log($"开始! 种子={seed}");
        _sync.OnGameEnded += winnerId => Debug.Log($"结束! 赢家={winnerId}");
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.F5))
            _sync.Ready();
    }

    // === IGameLogic ===

    public void OnGameStart(int playerCount, byte localPlayerId, int randomSeed)
    {
        // 初始化游戏世界
    }

    public void OnLogicUpdate(FrameData frame)
    {
        // 处理所有玩家输入，更新游戏状态
        foreach (var input in frame.Inputs)
        {
            var moveX = FixedInt.FromRaw(input.MoveX);
            var moveY = FixedInt.FromRaw(input.MoveY);
            // 移动角色...
        }
    }

    public void OnGameEnd(byte winnerId) { /* 显示结算 */ }

    public PlayerInput SampleLocalInput()
    {
        return new PlayerInput
        {
            MoveX = Input.GetKey(KeyCode.D) ? (int)FixedInt.OneVal.Raw
                   : Input.GetKey(KeyCode.A) ? (int)FixedInt.NegOne.Raw : 0,
            MoveY = Input.GetKey(KeyCode.W) ? (int)FixedInt.OneVal.Raw
                   : Input.GetKey(KeyCode.S) ? (int)FixedInt.NegOne.Raw : 0,
            Buttons = Input.GetKey(KeyCode.Space) ? PlayerInput.ButtonFire : 0
        };
    }
}
```

### 4.4 帧同步服务器

> **文件**: `Tools/FrameSyncServer/Program.cs`  
> **运行**: `dotnet run` (端口 9100, TickRate 15Hz)

.NET 8 控制台应用，功能：
- **Room**: 最多 4 玩家，管理加入/离开/准备状态
- **Tick Timer**: 全员准备后以 15Hz（约 66ms）广播 `FrameData`
- **Session**: 每个 TCP 连接一个会话

```bash
cd Tools/FrameSyncServer
dotnet run
# 输出: FrameSyncServer listening on port 9100 ...
```

### 4.5 快速启动 Demo

1. **启动服务器**：
   ```bash
   cd Tools/FrameSyncServer && dotnet run
   ```

2. **Unity 场景配置**：
   - 场景中添加空 GameObject
   - 挂载 `FrameSyncClient`、`FrameSyncDemoLogic`、`FrameSyncDemoEntry`
   - Inspector 中 Host 设为 `127.0.0.1`，Port 设为 `9100`

3. **Play**：
   - 按 **F5** 准备
   - **WASD** 移动
   - 多开客户端可看到多玩家方块同步移动
   - **Esc** 离开房间

---

## 5. 定点数物理系统

### 5.1 碰撞体类型 FixedColliders

> **文件**: `Assets/Scripts/Network/FrameSync/FixedColliders.cs`

```csharp
enum ColliderType : byte { Circle, AABB, OBB, Capsule, Point }
```

| 碰撞体 | 结构 | 字段 | 说明 |
|--------|------|------|------|
| **FixedCircle** | struct | `Center`, `Radius` | 圆形碰撞体 |
| **FixedAABB** | struct | `Min`, `Max` | 轴对齐包围盒 |
| **FixedOBB** | struct | `Center`, `HalfSize`, `Axis` | 旋转包围盒 |
| **FixedCapsule** | struct | `PointA`, `PointB`, `Radius` | 胶囊体 |

#### 创建碰撞体

```csharp
// 圆
var circle = new FixedCircle
{
    Center = new FixedVector2(FixedInt.FromInt(5), FixedInt.FromInt(3)),
    Radius = FixedInt.FromInt(2)
};

// AABB（两种方式）
var aabb = new FixedAABB
{
    Min = new FixedVector2(FixedInt.Zero, FixedInt.Zero),
    Max = new FixedVector2(FixedInt.FromInt(4), FixedInt.FromInt(3))
};
var aabb2 = FixedAABB.FromCenter(center, halfSize);

// OBB
var obb = new FixedOBB
{
    Center = FixedVector2.Zero,
    HalfSize = new FixedVector2(FixedInt.FromInt(2), FixedInt.FromInt(1)),
    Axis = FixedVector2.Right  // 局部 X 轴方向（需归一化）
};

// 胶囊
var capsule = new FixedCapsule
{
    PointA = new FixedVector2(FixedInt.Zero, FixedInt.FromInt(-2)),
    PointB = new FixedVector2(FixedInt.Zero, FixedInt.FromInt(2)),
    Radius = FixedInt.OneVal
};
```

#### 碰撞结果结构

```csharp
struct CollisionResult
{
    bool       Colliding;         // 是否碰撞
    FixedVector2 Normal;          // 分离方向（A→B）
    FixedInt     Penetration;     // 穿透深度
    FixedVector2 SeparationVector; // Normal * Penetration（直接加到位置上分离）
}

struct FixedRayHit
{
    bool         Hit;       // 是否命中
    FixedInt     Distance;  // 命中距离
    FixedVector2 Point;     // 命中点
    FixedVector2 Normal;    // 命中面法线
}
```

### 5.2 碰撞检测 FixedPhysics

> **文件**: `Assets/Scripts/Network/FrameSync/FixedPhysics.cs`

所有方法均为 `static`，纯函数，完全确定性。

#### 点包含测试

```csharp
bool inCircle = FixedPhysics.PointInCircle(point, circle);
bool inAABB   = FixedPhysics.PointInAABB(point, aabb);
```

#### 碰撞检测

```csharp
// 圆 vs 圆
CollisionResult r1 = FixedPhysics.CircleVsCircle(circleA, circleB);

// 圆 vs AABB
CollisionResult r2 = FixedPhysics.CircleVsAABB(circle, aabb);

// AABB vs AABB
CollisionResult r3 = FixedPhysics.AABBvsAABB(aabbA, aabbB);

// 圆 vs 胶囊
CollisionResult r4 = FixedPhysics.CircleVsCapsule(circle, capsule);

// 胶囊 vs 胶囊
CollisionResult r5 = FixedPhysics.CapsuleVsCapsule(capsuleA, capsuleB);

// OBB vs OBB（布尔结果，SAT 分离轴定理）
bool overlap = FixedPhysics.OBBvsOBB(obbA, obbB);
```

#### 碰撞响应

```csharp
var result = FixedPhysics.CircleVsCircle(a, b);
if (result.Colliding)
{
    // 将 A 推出碰撞
    posA = posA + result.SeparationVector;

    // 或者双方各推一半
    var halfSep = result.SeparationVector / FixedInt.FromInt(2);
    posA = posA + halfSep;
    posB = posB - halfSep;
}
```

### 5.3 射线检测 Raycasting

```csharp
var ray = new FixedRay2D
{
    Origin = playerPos,
    Direction = aimDir.Normalized
};
FixedInt maxDist = FixedInt.FromInt(50);

// 射线 vs 圆
FixedRayHit hit1 = FixedPhysics.RaycastCircle(ray, circle, maxDist);

// 射线 vs AABB（Slab 法）
FixedRayHit hit2 = FixedPhysics.RaycastAABB(ray, aabb, maxDist);

// 射线 vs 线段
FixedRayHit hit3 = FixedPhysics.RaycastSegment(ray, segStart, segEnd, maxDist);

// 射线 vs 胶囊
FixedRayHit hit4 = FixedPhysics.RaycastCapsule(ray, capsule, maxDist);

if (hit1.Hit)
{
    Debug.Log($"命中距离: {hit1.Distance.ToFloat()}, 命中点: {hit1.Point}");
}
```

### 5.4 扫掠检测 Sweep Tests

用于连续碰撞检测（CCD），防止高速物体穿透。

```csharp
// 移动中的圆 vs 静止的圆
FixedVector2 velocity = FixedVector2.Right * FixedInt.FromInt(10);
bool willHit = FixedPhysics.SweepCircleVsCircle(
    movingCircle, velocity, staticCircle,
    out FixedInt t,            // 碰撞发生时间 [0, 1]
    out FixedVector2 hitNormal
);

if (willHit)
{
    // t=0.3 表示在 30% 路程处碰撞
    var safePos = movingCircle.Center + velocity * t;
}

// 移动中的圆 vs 静止的 AABB
bool willHit2 = FixedPhysics.SweepCircleVsAABB(
    movingCircle, velocity, aabb,
    out FixedInt t2, out FixedVector2 hitNormal2
);
```

### 5.5 转向行为 FixedSteering

> **文件**: `Assets/Scripts/Network/FrameSync/FixedSteering.cs`

常用 AI 移动行为，全部返回期望速度向量。

```csharp
// 寻找目标
FixedVector2 seekVel = FixedSteering.Seek(myPos, targetPos, currentVel, maxSpeed);

// 逃离威胁
FixedVector2 fleeVel = FixedSteering.Flee(myPos, threatPos, currentVel, maxSpeed);

// 到达（接近目标时减速）
FixedVector2 arriveVel = FixedSteering.Arrive(
    myPos, targetPos, currentVel, maxSpeed, slowRadius
);

// 分离（避免与邻居重叠）
FixedVector2[] neighbors = ...;
FixedVector2 sepForce = FixedSteering.Separation(
    myPos, neighbors, neighborCount, separationRadius
);

// 重叠解算（两个圆形实体）
FixedVector2 pushOut = FixedSteering.ResolveOverlap(
    posA, radiusA, posB, radiusB
);

// 障碍物避让（圆形障碍物）
FixedCircle[] obstacles = ...;
FixedVector2 avoidForce = FixedSteering.ObstacleAvoidance(
    myPos, velocity, agentRadius, obstacles, obstacleCount, lookAheadDist
);

// AABB 障碍物避让
FixedAABB[] wallBoxes = ...;
FixedVector2 avoidWall = FixedSteering.AABBObstacleAvoidance(
    myPos, velocity, agentRadius, wallBoxes, count, lookAheadDist
);

// 墙壁滑行（碰墙后沿墙面滑动）
FixedVector2 slideVel = FixedSteering.WallSlide(velocity, wallNormal);
```

#### 综合运动示例

```csharp
public void OnLogicUpdate(FrameData frame)
{
    var input = frame.Inputs[localId];
    var moveDir = new FixedVector2(
        FixedInt.FromRaw(input.MoveX),
        FixedInt.FromRaw(input.MoveY)
    );

    // 基础移动
    var desiredVel = moveDir.Normalized * maxSpeed;

    // 叠加转向力
    desiredVel = desiredVel + FixedSteering.Separation(pos, neighbors, count, sepRadius);
    desiredVel = desiredVel + FixedSteering.ObstacleAvoidance(pos, vel, radius, obs, obsCount, lookAhead);
    desiredVel = FixedVector2.ClampMagnitude(desiredVel, maxSpeed);

    // 碰撞检测
    pos = pos + desiredVel * dt;
    var result = FixedPhysics.CircleVsAABB(myCollider, wall);
    if (result.Colliding)
        pos = pos + result.SeparationVector;
}
```

---

## 6. NavMesh 寻路系统

### 6.1 数据结构 FixedNavMeshData

> **文件**: `Assets/Scripts/Network/FrameSync/NavMesh/FixedNavMeshData.cs`

NavMesh 由顶点数组和三角形数组组成，每个三角形记录 3 个邻接三角形索引（-1 表示无邻接）。

```csharp
struct NavVertex
{
    FixedVector2 Position;
}

struct NavTriangle
{
    int V0, V1, V2;                    // 顶点索引
    int Neighbor0, Neighbor1, Neighbor2; // 邻接三角形索引（-1=无）
}

class FixedNavMeshData
{
    NavVertex[]   Vertices;
    NavTriangle[] Triangles;

    // 查找点所在的三角形（线性搜索）
    int FindTriangle(FixedVector2 point);

    // 点是否在某三角形内（叉积法）
    bool PointInTriangle(FixedVector2 p, int triIdx);

    // 获取两个相邻三角形的共享边
    bool GetSharedEdge(int triA, int triB, out FixedVector2 left, out FixedVector2 right);
}
```

### 6.2 A* 寻路 + 漏斗算法

> **文件**: `Assets/Scripts/Network/FrameSync/NavMesh/FixedNavMeshPathfinder.cs`

两步寻路：
1. **A\*** 在三角形图上搜索通道（三角形序列）
2. **Simple Stupid Funnel Algorithm** 在通道内生成平滑路径

```csharp
// 创建寻路器
var pathfinder = new FixedNavMeshPathfinder(navMeshData);

// 寻路（返回路径点列表）
List<FixedVector2> path = pathfinder.FindPath(startPos, endPos);

if (path != null && path.Count > 0)
{
    // path[0] = startPos
    // path[^1] = endPos（或最近可达点）
    foreach (var waypoint in path)
        Debug.Log(waypoint);
}

// 也可以只获取三角形通道（用于调试可视化）
List<int> trianglePath = pathfinder.FindTrianglePath(startPos, endPos);
```

### 6.3 NavMesh Agent

> **文件**: `Assets/Scripts/Network/FrameSync/NavMesh/FixedNavMeshAgent.cs`

运行时寻路代理组件，挂载到 GameObject 上使用。

```csharp
[ExecuteAlways]
public class FixedNavMeshAgent : MonoBehaviour
{
    public FixedNavMeshAsset NavMeshAsset;  // Inspector 拖入烘焙好的 Asset
    public float Speed = 5f;
    public float StoppingDistance = 0.1f;
}
```

#### 使用方式

```csharp
FixedNavMeshAgent agent = GetComponent<FixedNavMeshAgent>();

// 初始化（游戏开始时）
agent.Init(new FixedVector2(startX, startY));

// 设置目标
bool pathFound = agent.SetDestination(targetPos);

// 在帧同步逻辑帧中驱动
public void OnLogicUpdate(FrameData frame)
{
    agent.Tick(tickInterval);       // 定点数时间步长
    agent.SyncTransform();          // 同步到 Transform（仅渲染用）
}

// 停止
agent.Stop();

// 状态查询
bool moving = agent.HasPath;
bool arrived = agent.ReachedDestination;
FixedVector2 pos = agent.Position;
```

### 6.4 NavMesh Baker 编辑器工具

> **文件**: `Assets/Editor/FixedNavMeshBaker.cs`  
> **打开方式**: Unity 菜单 → `FrameSync` → `NavMesh Baker`

#### 操作流程

1. **打开 Baker**: 菜单 `FrameSync → NavMesh Baker`
2. **编辑边界**: 点击 `Edit Boundary`，在 Scene 视图 XZ 平面上点击放置边界多边形顶点
3. **编辑障碍物**: 点击 `Add Obstacle`，同样在 Scene 中点击放置障碍物多边形
4. **烘焙**: 点击 `Bake` 按钮
   - 使用 Ear Clipping 三角化算法（支持孔洞桥接）
   - 自动计算三角形邻接关系
5. **保存**: 指定 `FixedNavMeshAsset`，点击 `Save to Asset`

#### 算法细节

- **三角化**: Ear Clipping with Holes — 先将障碍物（孔洞）通过桥接边合并到外边界，再三角化
- **邻接计算**: 边哈希表 — 同一条边（顶点对排序后）被两个三角形共享即为邻接
- **坐标系**: Scene 视图中使用 XZ 平面（Y=0），存入 Asset 时转为 2D 定点数

---

## 7. 行为树系统

### 7.1 核心概念

> **文件**: `Assets/Scripts/Network/FrameSync/BehaviorTree/BTCore.cs`

行为树是一种分层决策模型，由**根节点**向下 Tick，每帧返回三种状态之一：

| 状态 | 含义 |
|------|------|
| `BTStatus.Success` | 节点任务完成 |
| `BTStatus.Failure` | 节点任务失败 |
| `BTStatus.Running` | 节点任务尚在执行，下帧继续 |

#### 节点生命周期

```
OnEnter(首次进入) → OnTick(每帧) → OnExit(完成时)
                      ↑                |
                      └── Running ─────┘
```

#### 节点类型

```
BTNode (抽象基类)
├── BTComposite (组合节点 — 多个子节点)
│   ├── BTSequence
│   ├── BTSelector
│   ├── BTParallel
│   ├── BTRandomSelector
│   └── BTRandomSequence
├── BTDecorator (装饰节点 — 单个子节点)
│   ├── BTInverter
│   ├── BTAlwaysSucceed / BTAlwaysFail
│   ├── BTRepeater
│   ├── BTUntilFail / BTUntilSuccess
│   ├── BTCooldown
│   ├── BTGuard
│   └── BTTimeLimit
└── (叶节点 — 无子节点)
    ├── BTAction / BTSimpleAction
    ├── BTCondition
    ├── BTWait / BTWaitUntil
    ├── BTSetValue<T> / BTCheckValue<T>
    └── BTLog
```

#### 黑板 BTBlackboard

节点间共享数据的键值存储：

```csharp
ctx.Blackboard.SetFixed("hp", FixedInt.FromInt(100));
ctx.Blackboard.SetVector("target", enemyPos);
ctx.Blackboard.SetBool("alerted", true);
ctx.Blackboard.SetInt("ammo", 30);

FixedInt hp = ctx.Blackboard.GetFixed("hp");
bool exists = ctx.Blackboard.Has("target");
ctx.Blackboard.Remove("tempData");
```

#### 确定性随机 BTRandom

LCG（线性同余）算法，相同 seed 保证相同序列：

```csharp
var rng = new BTRandom(42);
int n = rng.Next();          // [0, 2^31)
int m = rng.Next(10);        // [0, 10)
int r = rng.Range(5, 15);    // [5, 15)
```

#### BTContext

每次 Tick 传递给所有节点的上下文：

```csharp
class BTContext
{
    BTBlackboard Blackboard;  // 共享数据
    int          Frame;       // 当前逻辑帧号
    FixedInt     DeltaTime;   // 帧时间步长
    BTRandom     Random;      // 确定性随机
    int          EntityId;    // 实体 ID
}
```

### 7.2 组合节点 Composites

> **文件**: `Assets/Scripts/Network/FrameSync/BehaviorTree/BTComposites.cs`

| 节点 | 成功条件 | 失败条件 | 遇到 Running |
|------|---------|---------|-------------|
| **BTSequence** | 全部 Success | 任一 Failure | 暂停，下帧继续 |
| **BTSelector** | 任一 Success | 全部 Failure | 暂停，下帧继续 |
| **BTParallel(RequireAll)** | 全部 Success | 任一 Failure | 继续执行其他 |
| **BTParallel(RequireOne)** | 任一 Success | 全部 Failure | 继续执行其他 |
| **BTRandomSelector** | 同 Selector | 同 Selector | 同（随机执行顺序） |
| **BTRandomSequence** | 同 Sequence | 同 Sequence | 同（随机执行顺序） |

```csharp
// Sequence：依次检查条件 → 执行攻击 → 播放动画
Sequence
├── Condition: 敌人在攻击范围内?
├── Action: 执行攻击
└── Action: 播放攻击动画

// Selector：优先攻击，其次追逐，最后巡逻
Selector
├── Sequence(攻击)
├── Sequence(追逐)
└── Action(巡逻)

// Parallel：同时移动和检测碰撞
Parallel(RequireAll)
├── Action: 移动到目标
└── Condition: 路径仍然有效?
```

### 7.3 装饰节点 Decorators

> **文件**: `Assets/Scripts/Network/FrameSync/BehaviorTree/BTDecorators.cs`

| 节点 | 参数 | 行为 |
|------|------|------|
| **BTInverter** | - | Success↔Failure 互换 |
| **BTAlwaysSucceed** | - | 无论结果返回 Success |
| **BTAlwaysFail** | - | 无论结果返回 Failure |
| **BTRepeater** | `count`, `ignoreFailure` | 重复 N 次（-1=永远） |
| **BTUntilFail** | - | 重复执行直到 Failure→返回 Success |
| **BTUntilSuccess** | - | 重复执行直到 Success |
| **BTCooldown** | `frames` | 执行后冷却 N 帧，期间返回 Failure |
| **BTGuard** | `condition` | 每帧检查前置条件，不满足则 Failure |
| **BTTimeLimit** | `frames` | 超时则强制 Failure + Reset 子节点 |

```csharp
// Cooldown 示例：技能冷却 60 帧（15Hz ≈ 4 秒）
Cooldown(60)
└── Action: 释放大招

// Guard 示例：只有血量大于 0 才执行子树
Guard(ctx => ctx.Blackboard.GetFixed("hp") > FixedInt.Zero)
└── Sequence(战斗行为)

// TimeLimit 示例：限时 150 帧完成（10 秒）
TimeLimit(150)
└── Sequence(执行复杂任务)
```

### 7.4 叶节点 Leaves

> **文件**: `Assets/Scripts/Network/FrameSync/BehaviorTree/BTLeaves.cs`

| 节点 | 参数 | 行为 |
|------|------|------|
| **BTAction** | `tick`, `enter?`, `exit?` | 通用动作（可返回 Running） |
| **BTSimpleAction** | `action` | 单帧动作→Success |
| **BTCondition** | `condition` | 条件检查→Success/Failure |
| **BTWait** | `frames` | 等待 N 帧→Success |
| **BTWaitUntil** | `condition` | 等待条件满足→Success |
| **BTSetValue\<T\>** | `key`, `valueGetter` | 写黑板→Success |
| **BTCheckValue\<T\>** | `key`, `predicate` | 读黑板检查→Success/Failure |
| **BTLog** | `message`, `logAction?` | 调试输出→Success |

```csharp
// BTAction：持续追逐直到到达
new BTAction(
    tick: ctx =>
    {
        var pos = ctx.Blackboard.GetVector("myPos");
        var target = ctx.Blackboard.GetVector("targetPos");
        var dist = FixedVector2.Distance(pos, target);
        if (dist < FixedInt.FromFloat(0.5f))
            return BTStatus.Success;
        // 移动...
        return BTStatus.Running;
    },
    enter: ctx => Debug.Log("开始追逐"),
    exit: ctx => Debug.Log("追逐结束")
);

// BTWait：等待 30 帧（2 秒 @ 15Hz）
new BTWait(30);

// BTSetValue：记录最后已知敌人位置
new BTSetValue<FixedVector2>("lastEnemyPos",
    ctx => ctx.Blackboard.GetVector("enemyPos"));
```

### 7.5 流式构建器 BTBuilder

> **文件**: `Assets/Scripts/Network/FrameSync/BehaviorTree/BTBuilder.cs`

使用链式调用声明式构建行为树。

#### 基本语法

```csharp
var root = BT.Selector()       // 根节点类型
    .Sequence()                 // 嵌套子树（需匹配 .End()）
        .Condition(...)
        .Action(...)
    .End()                      // 关闭子树
    .Action(...)                // 叶节点（无需 .End()）
    .Build();                   // 完成构建
```

#### 完整语法参考

```csharp
BT.Selector()                          // 静态入口
BT.Sequence()
BT.Parallel(BTParallelPolicy.RequireAll)
BT.RandomSelector()
BT.RandomSequence()

// 嵌套组合节点
.Sequence()    ... .End()
.Selector()    ... .End()
.Parallel()    ... .End()

// 叶节点
.Action(ctx => BTStatus.Running)
.Action(tick, enter, exit)
.SimpleAction(ctx => { /* 一帧完成 */ })
.Condition(ctx => true/false)
.Wait(30)
.WaitUntil(ctx => condition)
.SetValue<int>("key", ctx => 42)
.SetValue<int>("key", 42)
.CheckValue<int>("key", v => v > 0)
.Log("调试信息")
.Node(customNode)

// 装饰器（lambda 包装子树）
.Inverter(b => b.Action(...))
.AlwaysSucceed(b => b.Action(...))
.AlwaysFail(b => b.Action(...))
.Repeat(3, b => b.Action(...))
.RepeatForever(b => b.Sequence().Action(...).End())
.UntilFail(b => b.Action(...))
.UntilSuccess(b => b.Action(...))
.Cooldown(60, b => b.Action(...))
.Guard(ctx => condition, b => b.Sequence()...End())
.TimeLimit(150, b => b.Action(...))
.Decorator(new MyDecorator(), b => b.Action(...))
```

### 7.6 行为树运行器 BehaviorTree

> **文件**: `Assets/Scripts/Network/FrameSync/BehaviorTree/BehaviorTree.cs`

```csharp
// 创建
var bt = new BehaviorTree(root, entityId: 1, randomSeed: 42);

// 帧同步中驱动
public void OnLogicUpdate(FrameData frame)
{
    // 更新黑板数据
    bt.Context.Blackboard.SetVector("myPos", myPosition);
    bt.Context.Blackboard.SetFixed("hp", hp);

    // Tick 行为树
    BTStatus status = bt.Tick(frame.FrameId, tickInterval);
}

// 暂停/恢复
bt.Paused = true;

// 重置（树 + 黑板）
bt.Reset();

// 仅重置树节点状态（保留黑板数据）
bt.ResetTree();
```

**自动重启机制**：根节点返回 Success/Failure 后，框架自动 Reset 整棵树，下帧从根节点重新开始。

### 7.7 完整 AI 示例

以下是一个帧同步 MOBA 小兵 AI 的完整示例：

```csharp
using FrameSync;

public class MinionAI
{
    BehaviorTree _bt;
    FixedNavMeshPathfinder _pathfinder;
    FixedVector2 _position;
    FixedInt _hp;
    FixedInt _attackRange = FixedInt.FromInt(2);
    FixedInt _detectRange = FixedInt.FromInt(8);
    FixedInt _speed = FixedInt.FromInt(3);

    public void Init(int entityId, uint seed, FixedNavMeshData navMesh)
    {
        _pathfinder = new FixedNavMeshPathfinder(navMesh);

        var root = BT.Selector()
            // ── 分支 1：死亡 ──
            .Sequence()
                .Condition(ctx => ctx.Blackboard.GetFixed("hp") <= FixedInt.Zero)
                .SimpleAction(ctx => OnDeath(ctx))
            .End()

            // ── 分支 2：攻击 ──
            .Sequence()
                .Condition(ctx =>
                {
                    var dist = ctx.Blackboard.GetFixed("enemyDist");
                    return dist <= _attackRange && dist > FixedInt.Zero;
                })
                .Cooldown(15, b => b  // 1 秒冷却 @ 15Hz
                    .SimpleAction(ctx => DoAttack(ctx)))
            .End()

            // ── 分支 3：追逐 ──
            .Sequence()
                .Condition(ctx =>
                {
                    var dist = ctx.Blackboard.GetFixed("enemyDist");
                    return dist <= _detectRange && dist > FixedInt.Zero;
                })
                .Action(ctx => DoChase(ctx))
            .End()

            // ── 分支 4：巡逻 ──
            .Sequence()
                .Action(ctx => DoPatrol(ctx))
                .Wait(45)  // 巡逻点停留 3 秒
            .End()

            .Build();

        _bt = new BehaviorTree(root, entityId, seed);
    }

    public void Tick(int frame, FixedInt dt, FixedVector2 nearestEnemyPos)
    {
        // 更新黑板
        var bb = _bt.Context.Blackboard;
        bb.SetVector("myPos", _position);
        bb.SetFixed("hp", _hp);
        bb.SetVector("enemyPos", nearestEnemyPos);
        bb.SetFixed("enemyDist", FixedVector2.Distance(_position, nearestEnemyPos));

        // 驱动行为树
        _bt.Tick(frame, dt);
    }

    BTStatus DoChase(BTContext ctx)
    {
        var myPos = ctx.Blackboard.GetVector("myPos");
        var target = ctx.Blackboard.GetVector("enemyPos");
        var dist = ctx.Blackboard.GetFixed("enemyDist");

        if (dist <= _attackRange)
            return BTStatus.Success;

        // 用 NavMesh 寻路
        var path = _pathfinder.FindPath(myPos, target);
        if (path == null || path.Count < 2)
            return BTStatus.Failure;

        // 朝下一个路点移动
        var nextWaypoint = path[1];
        var dir = (nextWaypoint - myPos).Normalized;
        _position = myPos + dir * _speed * ctx.DeltaTime;

        return BTStatus.Running;
    }

    BTStatus DoPatrol(BTContext ctx)
    {
        // 如果没有巡逻目标，随机选一个
        if (!ctx.Blackboard.Has("patrolTarget"))
        {
            var x = FixedInt.FromInt(ctx.Random.Range(-10, 10));
            var y = FixedInt.FromInt(ctx.Random.Range(-10, 10));
            ctx.Blackboard.SetVector("patrolTarget", new FixedVector2(x, y));
        }

        var myPos = ctx.Blackboard.GetVector("myPos");
        var target = ctx.Blackboard.GetVector("patrolTarget");
        var dist = FixedVector2.Distance(myPos, target);

        if (dist < FixedInt.OneVal)
        {
            ctx.Blackboard.Remove("patrolTarget");
            return BTStatus.Success;
        }

        var dir = (target - myPos).Normalized;
        _position = myPos + dir * _speed * ctx.DeltaTime;
        return BTStatus.Running;
    }

    void DoAttack(BTContext ctx)
    {
        // 对最近敌人造成伤害...
    }

    void OnDeath(BTContext ctx)
    {
        // 播放死亡、移除实体...
    }
}
```

---

## 8. 文件索引

### 网络层

| 文件 | 说明 |
|------|------|
| `Assets/Scripts/Network/PacketBuffer.cs` | TCP 粘包缓冲区 |
| `Assets/Scripts/Network/TcpConnection.cs` | 底层 TCP 连接 |
| `Assets/Scripts/Network/NetworkManager.cs` | 网络管理器单例 |
| `Assets/Scripts/Network/NetworkDemo.cs` | 网络测试 Demo |

### 帧同步

| 文件 | 说明 |
|------|------|
| `Assets/Scripts/Network/FrameSync/FrameSyncProto.cs` | 协议定义与序列化 |
| `Assets/Scripts/Network/FrameSync/IGameLogic.cs` | 游戏逻辑接口 |
| `Assets/Scripts/Network/FrameSync/FrameSyncClient.cs` | 帧同步客户端 |
| `Assets/Scripts/Network/FrameSync/FrameSyncDemoLogic.cs` | Demo 游戏逻辑 |
| `Assets/Scripts/Network/FrameSync/FrameSyncDemoEntry.cs` | Demo 入口 |
| `Tools/TestServer/Program.cs` | TCP 测试服务器 (端口 9000) |
| `Tools/FrameSyncServer/Program.cs` | 帧同步服务器 (端口 9100) |

### 定点数学

| 文件 | 说明 |
|------|------|
| `Assets/Scripts/Network/FrameSync/FixedMath.cs` | FixedInt + FixedVector2 |

### 物理系统

| 文件 | 说明 |
|------|------|
| `Assets/Scripts/Network/FrameSync/FixedColliders.cs` | 碰撞体定义 |
| `Assets/Scripts/Network/FrameSync/FixedPhysics.cs` | 碰撞检测 + 射线 + 扫掠 |
| `Assets/Scripts/Network/FrameSync/FixedSteering.cs` | 转向行为 |

### NavMesh 寻路

| 文件 | 说明 |
|------|------|
| `Assets/Scripts/Network/FrameSync/NavMesh/FixedNavMeshData.cs` | NavMesh 数据结构 |
| `Assets/Scripts/Network/FrameSync/NavMesh/FixedNavMeshPathfinder.cs` | A* + 漏斗算法 |
| `Assets/Scripts/Network/FrameSync/NavMesh/FixedNavMeshAsset.cs` | ScriptableObject 资源 |
| `Assets/Scripts/Network/FrameSync/NavMesh/FixedNavMeshAgent.cs` | 运行时寻路代理 |
| `Assets/Editor/FixedNavMeshBaker.cs` | NavMesh 烘焙编辑器 |

### 行为树

| 文件 | 说明 |
|------|------|
| `Assets/Scripts/Network/FrameSync/BehaviorTree/BTCore.cs` | 核心类型（状态、节点基类、黑板、上下文） |
| `Assets/Scripts/Network/FrameSync/BehaviorTree/BTComposites.cs` | 组合节点 |
| `Assets/Scripts/Network/FrameSync/BehaviorTree/BTDecorators.cs` | 装饰节点 |
| `Assets/Scripts/Network/FrameSync/BehaviorTree/BTLeaves.cs` | 叶节点 |
| `Assets/Scripts/Network/FrameSync/BehaviorTree/BTBuilder.cs` | 流式构建器 |
| `Assets/Scripts/Network/FrameSync/BehaviorTree/BehaviorTree.cs` | 行为树运行器 |
