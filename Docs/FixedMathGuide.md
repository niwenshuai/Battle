# 定点数使用说明

本文档基于 `Assets/Scripts/Network/FrameSync/FixedMath.cs`，介绍帧同步项目中定点数的使用方式与注意事项。

---

## 1. 为什么需要定点数

帧同步要求所有客户端在相同输入下产生完全一致的结果。浮点数 (`float`/`double`) 存在以下问题：

- **平台差异**：不同 CPU/编译器对浮点运算的舍入行为可能不同
- **不确定性**：浮点加法不满足结合律，`(a+b)+c ≠ a+(b+c)`
- **精度丢失**：累积误差导致不同客户端状态漂移

定点数通过整数运算模拟小数，保证**跨平台确定性**，是帧同步的核心基础。

---

## 2. FixedInt — 64 位定点数

### 2.1 内部格式

| 属性 | 值 |
|------|-----|
| 格式 | Q32.32（高 32 位整数 + 低 32 位小数） |
| 存储类型 | `long`（64 位有符号整数） |
| 精度 | 约 2.3×10⁻¹⁰ |
| 范围 | ±2,147,483,647 |

内部值 `Raw` 的含义：`实际值 = Raw / 2³²`

### 2.2 创建定点数

```csharp
// 从整数创建（推荐，零开销）
FixedInt a = FixedInt.FromInt(3);        // 3.0

// 从浮点创建（仅用于初始化常量，运行时禁止使用）
FixedInt b = FixedInt.FromFloat(1.5f);   // 1.5

// 从原始值创建（高级用法，Raw 直接对应内部 long）
FixedInt c = FixedInt.FromRaw(3L << 32); // 3.0
```

**规则：`FromFloat` 仅用于将配置表中的常量（如速度、距离、伤害系数）转为定点数，运行时逻辑中禁止使用 `float` 参与计算。**

### 2.3 预定义常量

| 常量 | 值 |
|------|-----|
| `FixedInt.Zero` | 0 |
| `FixedInt.OneVal` | 1 |
| `FixedInt.Half` | 0.5 |
| `FixedInt.NegOne` | -1 |

> 注意：常量名为 `OneVal` 而非 `One`，因为 `One` 已被内部用于 `1L << 32` 的原始值定义。

### 2.4 转换回其他类型

```csharp
FixedInt val = FixedInt.FromInt(7) / FixedInt.FromInt(2); // 3.5

int   i = val.ToInt();    // 3（截断，丢弃小数）
float f = val.ToFloat();  // 3.5f（仅用于渲染/调试/显示）
```

**规则：`ToFloat()` 仅在视图层（BattleView）将帧数转为秒显示时使用，逻辑层禁止使用。**

### 2.5 运算符

| 运算符 | 示例 | 说明 |
|--------|------|------|
| `+` | `a + b` | 加法 |
| `-` | `a - b` | 减法 |
| `-`（一元） | `-a` | 取反 |
| `*` | `a * b` | 乘法（内部拆高低 32 位防溢出） |
| `/` | `a / b` | 除法（除以 0 返回 `long.MaxValue` 或 `long.MinValue`） |
| `==` `!=` | `a == b` | 相等比较 |
| `<` `>` `<=` `>=` | `a < b` | 大小比较 |

**除以零的行为**：`a / 0` 时，若 `a >= 0` 返回 `long.MaxValue`，否则返回 `long.MinValue`。不会抛异常。

### 2.6 数学函数

```csharp
FixedInt.Abs(v)              // 绝对值
FixedInt.Min(a, b)           // 最小值
FixedInt.Max(a, b)           // 最大值
FixedInt.Clamp(v, min, max)  // 限制在 [min, max] 范围内
FixedInt.Sqrt(v)             // 平方根（v ≤ 0 时返回 Zero）
```

**`Sqrt` 实现**：先用逐位法计算 `isqrt(x)` 作为粗略值，再通过牛顿法精修 4 轮，兼顾精度与性能。

---

## 3. FixedVector2 — 确定性 2D 向量

### 3.1 基本结构

```csharp
public struct FixedVector2
{
    public FixedInt X;
    public FixedInt Y;
}
```

### 3.2 预定义方向

| 常量 | 值 |
|------|-----|
| `FixedVector2.Zero`  | (0, 0) |
| `FixedVector2.One`   | (1, 1) |
| `FixedVector2.Up`    | (0, 1) |
| `FixedVector2.Down`  | (0, -1) |
| `FixedVector2.Left`  | (-1, 0) |
| `FixedVector2.Right` | (1, 0) |

### 3.3 运算符

```csharp
var a = new FixedVector2(FixedInt.FromInt(1), FixedInt.FromInt(2));
var b = new FixedVector2(FixedInt.FromInt(3), FixedInt.FromInt(4));

a + b              // (4, 6)
a - b              // (-2, -2)
-a                 // (-1, -2)
a * FixedInt.FromInt(2)  // (2, 4) — 向量 × 标量
FixedInt.FromInt(3) * a  // (3, 6) — 标量 × 向量
a / FixedInt.FromInt(2)  // (0.5, 1.0) — 向量 / 标量
a == b             // false
a != b             // true
```

### 3.4 属性

```csharp
vec.SqrMagnitude   // 距离平方（避免开方，性能好）
vec.Magnitude      // 距离（内部调用 Sqrt）
vec.Normalized     // 归一化向量（零向量返回 Zero）
vec.Perpendicular  // 逆时针旋转 90° → (-Y, X)
```

> **性能提示**：距离比较优先用 `SqrMagnitude`，避免 `Sqrt` 开销。

### 3.5 静态方法

| 方法 | 签名 | 说明 |
|------|------|------|
| `Dot` | `(FixedVector2 a, FixedVector2 b) → FixedInt` | 点积：`ax*bx + ay*by` |
| `Cross` | `(FixedVector2 a, FixedVector2 b) → FixedInt` | 2D 叉积（标量）：`ax*by - ay*bx`，正值表示 b 在 a 的逆时针方向 |
| `Distance` | `(FixedVector2 a, FixedVector2 b) → FixedInt` | 两点距离 |
| `SqrDistance` | `(FixedVector2 a, FixedVector2 b) → FixedInt` | 距离平方（性能好） |
| `Lerp` | `(a, b, t) → FixedVector2` | 线性插值，t 自动 clamp 到 [0, 1] |
| `ClampMagnitude` | `(v, maxLen) → FixedVector2` | 限制向量最大长度 |
| `Reflect` | `(dir, normal) → FixedVector2` | 沿法线反射 |
| `RotateToward` | `(current, target, maxRadians) → FixedVector2` | 方向旋转，每帧最多旋转 maxRadians 弧度 |

### 3.6 RotateToward 详解

用于角色朝向、子弹追踪等场景。使用小角度近似（sinθ≈θ, cosθ≈1）实现增量旋转：

```csharp
// 每帧最多旋转 0.1 弧度向目标方向转
var newDir = FixedVector2.RotateToward(currentDir, targetDir, FixedInt.FromFloat(0.1f));
```

特殊处理：
- 已对齐（dot ≥ 0.999）：直接返回目标方向
- 完全反向（dot ≤ -0.999）：取垂直方向旋转
- `maxRadians` 为弧度制，通常由 `FrameTime` 配合旋转速度计算

---

## 4. 项目中的使用规范

### 4.1 配置层 → 逻辑层的转换

配置文件（JSON）中时间用秒（`float`），逻辑层用帧数（`int`），位置/速度用定点数（`FixedInt`）：

```
配置 JSON (float 秒) → FrameTime.Sec() → int 帧数（逻辑层使用）
配置 JSON (float 距离/速度) → FixedInt.FromFloat() → FixedInt（逻辑层使用）
```

### 4.2 逻辑层 → 视图层的转换

视图层需要 `float`/`Vector2` 供 Unity 渲染使用：

```
int 帧数 → FrameTime.ToSec() → float 秒（显示用）
FixedInt → .ToFloat() → float（渲染用）
FixedVector2 → new Vector2(v.X.ToFloat(), v.Y.ToFloat()) → Vector2（渲染用）
```

### 4.3 禁止事项

| 禁止 | 原因 |
|------|------|
| 逻辑层使用 `float`/`double` 参与运算 | 浮点不确定性 |
| 逻辑层使用 `Vector2`/`Vector3` | Unity 向量基于 float |
| 逻辑层使用 `Mathf` | Mathf 是浮点运算 |
| 运行时调用 `FixedInt.FromFloat()` | 仅用于初始化常量 |
| 使用 `FixedInt.One` 作为常量 | `One` 是内部原始值，应使用 `OneVal` |

### 4.4 正确示例

```csharp
// ✅ 正确：逻辑层全用定点数
FixedInt speed = FixedInt.FromFloat(5.0f);      // 初始化常量
FixedInt dt = FixedInt.FromInt(1);               // 每帧 dt = 1 帧
FixedVector2 displacement = direction * speed * dt;

// ✅ 正确：时间用帧数
int cooldownFrames = FrameTime.Sec(2.0f);        // 配置秒→帧数
cooldownFrames--;                                 // 逻辑层用帧数倒计时

// ✅ 正确：视图层转换
float displaySec = FrameTime.ToSec(cooldownFrames); // 帧数→秒用于显示
Vector2 renderPos = new Vector2(pos.X.ToFloat(), pos.Y.ToFloat());
```

### 4.5 错误示例

```csharp
// ❌ 错误：逻辑层使用 float
float speed = 5.0f;
float dx = speed * Time.deltaTime;

// ❌ 错误：逻辑层使用 Mathf
float angle = Mathf.Atan2(dy, dx);

// ❌ 错误：逻辑层使用 Vector2
Vector2 pos = new Vector2(x, y);

// ❌ 错误：运行时 FromFloat
FixedInt RandomValue() => FixedInt.FromFloat(UnityEngine.Random.value); // 不确定！
```

---

## 5. 常见场景速查

| 场景 | 用法 |
|------|------|
| 定义移动速度 | `FixedInt speed = FixedInt.FromFloat(config.Speed);` |
| 每帧位移 | `pos = pos + dir * speed;`（speed 已含每帧单位） |
| 距离判断 | `FixedVector2.SqrDistance(a, b) <= range * range` |
| 方向归一化 | `dir = dir.Normalized` |
| 计算角度（2D 叉积判断左右） | `FixedVector2.Cross(from, to) > FixedInt.Zero` → 逆时针 |
| 冷却倒计时 | `int frames = FrameTime.Sec(seconds); frames--;` |
| 向某方向缓慢旋转 | `FixedVector2.RotateToward(cur, target, maxRadPerFrame)` |
| 限制移动范围 | `FixedVector2.ClampMagnitude(velocity, maxSpeed)` |
