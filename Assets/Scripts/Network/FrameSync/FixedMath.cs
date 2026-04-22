namespace FrameSync
{
    /// <summary>
    /// 64 位定点数（Q32.32 格式），用于帧同步中需要确定性的"小数"运算。
    ///
    /// 内部用 long 存储：高 32 位为整数部分，低 32 位为小数部分。
    /// 精度约 2.3e-10，范围 ±2,147,483,647。
    ///
    /// 用法：
    ///   FixedInt a = FixedInt.FromInt(3);
    ///   FixedInt b = FixedInt.FromFloat(1.5f);   // 仅用于初始化常量
    ///   FixedInt c = a + b;                      // 4.5
    ///   int whole  = c.ToInt();                   // 4（截断）
    /// </summary>
    public readonly struct FixedInt
    {
        public readonly long Raw;

        public const int FractionalBits = 32;
        public const long One = 1L << FractionalBits;

        // ── 构造 ─────────────────────────────────────────────

        public FixedInt(long raw) => Raw = raw;

        public static FixedInt FromInt(int v) => new(v * One);
        public static FixedInt FromRaw(long raw) => new(raw);

        /// <summary>仅用于初始化常量，运行时不要用浮点。</summary>
        public static FixedInt FromFloat(float v) => new((long)(v * One));

        public static readonly FixedInt Zero = new(0);
        public static readonly FixedInt OneVal = new(One);
        public static readonly FixedInt Half = new(One / 2);
        public static readonly FixedInt NegOne = new(-One);

        // ── 转换 ─────────────────────────────────────────────

        public int ToInt() => (int)(Raw >> FractionalBits);
        public float ToFloat() => Raw / (float)One; // 仅用于渲染/调试

        // ── 运算符 ───────────────────────────────────────────

        public static FixedInt operator +(FixedInt a, FixedInt b) => new(a.Raw + b.Raw);
        public static FixedInt operator -(FixedInt a, FixedInt b) => new(a.Raw - b.Raw);
        public static FixedInt operator -(FixedInt a) => new(-a.Raw);

        /// <summary>乘法：(a * b) >> 32</summary>
        public static FixedInt operator *(FixedInt a, FixedInt b)
        {
            // 拆成高低 32 位避免溢出
            long al = a.Raw & 0xFFFFFFFFL;
            long ah = a.Raw >> 32;
            long bl = b.Raw & 0xFFFFFFFFL;
            long bh = b.Raw >> 32;

            long mid = ah * bl + al * bh;
            // al, bl 均为无符号 32 位，乘积可达 ~2^64，超过 long 范围，必须用 ulong
            long lowHi = (long)((ulong)al * (ulong)bl >> 32);

            return new FixedInt((ah * bh << 32) + mid + lowHi);
        }

        /// <summary>除法：(a << 32) / b，防溢出。</summary>
        public static FixedInt operator /(FixedInt a, FixedInt b)
        {
            if (b.Raw == 0) return a.Raw >= 0 ? new FixedInt(long.MaxValue) : new FixedInt(long.MinValue);

            long aRaw = a.Raw;
            long bRaw = b.Raw;

            // 处理符号
            bool neg = (aRaw < 0) ^ (bRaw < 0);
            if (aRaw < 0) aRaw = -aRaw;
            if (bRaw < 0) bRaw = -bRaw;

            // (aRaw << 32) / bRaw 会溢出，分两步：
            //   商的整数部分 = (aRaw / bRaw) << 32
            //   商的小数部分 = ((aRaw % bRaw) << 32) / bRaw
            long intPart  = aRaw / bRaw;
            long rem      = aRaw % bRaw;

            // (rem << 32) 也可能溢出，逐步左移
            long fracPart = 0;
            for (int i = 31; i >= 0; i--)
            {
                rem <<= 1;
                fracPart <<= 1;
                if (rem >= bRaw)
                {
                    rem -= bRaw;
                    fracPart |= 1;
                }
            }

            long result = (intPart << 32) + fracPart;
            return new FixedInt(neg ? -result : result);
        }

        public static bool operator ==(FixedInt a, FixedInt b) => a.Raw == b.Raw;
        public static bool operator !=(FixedInt a, FixedInt b) => a.Raw != b.Raw;
        public static bool operator < (FixedInt a, FixedInt b) => a.Raw <  b.Raw;
        public static bool operator > (FixedInt a, FixedInt b) => a.Raw >  b.Raw;
        public static bool operator <=(FixedInt a, FixedInt b) => a.Raw <= b.Raw;
        public static bool operator >=(FixedInt a, FixedInt b) => a.Raw >= b.Raw;

        // ── 数学函数 ─────────────────────────────────────────

        public static FixedInt Abs(FixedInt v) => new(v.Raw < 0 ? -v.Raw : v.Raw);
        public static FixedInt Min(FixedInt a, FixedInt b) => a.Raw < b.Raw ? a : b;
        public static FixedInt Max(FixedInt a, FixedInt b) => a.Raw > b.Raw ? a : b;

        public static FixedInt Clamp(FixedInt v, FixedInt min, FixedInt max)
        {
            if (v.Raw < min.Raw) return min;
            if (v.Raw > max.Raw) return max;
            return v;
        }

        /// <summary>定点数平方根，输入负数或零返回 0。</summary>
        public static FixedInt Sqrt(FixedInt v)
        {
            if (v.Raw <= 0) return Zero;

            // 使用逐位确定法（bit-by-bit）计算 isqrt(v.Raw << 32)，不会溢出。
            // 等价于 result^2 <= v.Raw << 32，result 的每一位从高到低尝试。
            //
            // 因为 v.Raw << 32 不能直接表示，我们把比较拆成不溢出的形式。

            long x = v.Raw;
            long result = 0;

            // 从最高有效位开始。result 最大约 2^31（对应整数部分 ~2^15）
            // 我们需要确定 result 的 bit 47 到 bit 0
            // 但实际上 isqrt(v.Raw << 32) < 2^48，所以从 bit 47 开始

            // 使用牛顿法的安全版本：在缩放后的空间中计算
            // 先右移 x 到安全范围，计算 sqrt，再补偿

            // 方法：isqrt 基于 64 位整数
            // 计算 isqrt(n) where n = x << 32，但分成 isqrt(x) << 16 作为近似
            // 然后用牛顿法精修

            // Step 1: 计算 isqrt(x) 作为粗略值
            long s = x;
            long g = 0;
            long bit = 1L << 62;
            while (bit > s) bit >>= 2;
            while (bit != 0)
            {
                if (s >= g + bit)
                {
                    s -= g + bit;
                    g = (g >> 1) + bit;
                }
                else
                {
                    g >>= 1;
                }
                bit >>= 2;
            }
            // g = isqrt(x)

            // Step 2: result ≈ g << 16
            long r = g << 16;

            // Step 3: 牛顿法精修 — 求 r 使得 r^2 ≈ x << 32
            // 由于 r 在 ~2^(bits/2+16)，r*r 可能溢出，所以用除法形式
            // r = (r + (x << 32) / r) / 2
            // 但 x << 32 溢出，改用 r = (r + x * 2^32 / r) / 2
            // = (r + (x / r) * 2^32 + ((x % r) * 2^32) / r) / 2
            // 其中 (x % r) * 2^32 也可能溢出... 用分步除法

            for (int i = 0; i < 4; i++)
            {
                if (r <= 0) break;
                // 计算 (x << 32) / r 安全版本
                long q = SafeDiv64(x, r);
                long r2 = (r + q) >> 1;
                if (r2 >= r) break;
                r = r2;
            }

            return new FixedInt(r);
        }

        /// <summary>计算 (a &lt;&lt; 32) / b 而不溢出。</summary>
        static long SafeDiv64(long a, long b)
        {
            if (b == 0) return long.MaxValue;
            bool neg = (a < 0) ^ (b < 0);
            if (a < 0) a = -a;
            if (b < 0) b = -b;

            long intPart = a / b;
            long rem     = a % b;
            long frac    = 0;
            for (int i = 31; i >= 0; i--)
            {
                rem <<= 1;
                frac <<= 1;
                if (rem >= b)
                {
                    rem -= b;
                    frac |= 1;
                }
            }
            long result = (intPart << 32) + frac;
            return neg ? -result : result;
        }

        // ── Object ───────────────────────────────────────────

        public override bool Equals(object obj) => obj is FixedInt f && f.Raw == Raw;
        public override int GetHashCode() => Raw.GetHashCode();
        public override string ToString() => ToFloat().ToString("F4");
    }

    /// <summary>确定性 2D 向量。</summary>
    public struct FixedVector2
    {
        public FixedInt X;
        public FixedInt Y;

        public FixedVector2(FixedInt x, FixedInt y) { X = x; Y = y; }

        public static readonly FixedVector2 Zero  = new(FixedInt.Zero, FixedInt.Zero);
        public static readonly FixedVector2 One   = new(FixedInt.OneVal, FixedInt.OneVal);
        public static readonly FixedVector2 Up    = new(FixedInt.Zero, FixedInt.OneVal);
        public static readonly FixedVector2 Down  = new(FixedInt.Zero, FixedInt.NegOne);
        public static readonly FixedVector2 Left  = new(FixedInt.NegOne, FixedInt.Zero);
        public static readonly FixedVector2 Right = new(FixedInt.OneVal, FixedInt.Zero);

        // ── 运算符 ───────────────────────────────────────────
        public static FixedVector2 operator +(FixedVector2 a, FixedVector2 b) => new(a.X + b.X, a.Y + b.Y);
        public static FixedVector2 operator -(FixedVector2 a, FixedVector2 b) => new(a.X - b.X, a.Y - b.Y);
        public static FixedVector2 operator -(FixedVector2 a) => new(-a.X, -a.Y);
        public static FixedVector2 operator *(FixedVector2 a, FixedInt s) => new(a.X * s, a.Y * s);
        public static FixedVector2 operator *(FixedInt s, FixedVector2 a) => new(a.X * s, a.Y * s);
        public static FixedVector2 operator /(FixedVector2 a, FixedInt s) => new(a.X / s, a.Y / s);
        public static bool operator ==(FixedVector2 a, FixedVector2 b) => a.X == b.X && a.Y == b.Y;
        public static bool operator !=(FixedVector2 a, FixedVector2 b) => a.X != b.X || a.Y != b.Y;
        public override bool Equals(object obj) => obj is FixedVector2 v && this == v;
        public override int GetHashCode() => X.GetHashCode() * 397 ^ Y.GetHashCode();

        // ── 属性 ─────────────────────────────────────────────
        public FixedInt SqrMagnitude => X * X + Y * Y;
        public FixedInt Magnitude => FixedInt.Sqrt(SqrMagnitude);

        public FixedVector2 Normalized
        {
            get
            {
                var mag = Magnitude;
                if (mag == FixedInt.Zero) return Zero;
                return new FixedVector2(X / mag, Y / mag);
            }
        }

        // ── 常用向量操作 ─────────────────────────────────────

        /// <summary>点积。</summary>
        public static FixedInt Dot(FixedVector2 a, FixedVector2 b) => a.X * b.X + a.Y * b.Y;

        /// <summary>2D 叉积（标量），a × b = ax*by - ay*bx。正值表示 b 在 a 的逆时针方向。</summary>
        public static FixedInt Cross(FixedVector2 a, FixedVector2 b) => a.X * b.Y - a.Y * b.X;

        /// <summary>返回逆时针旋转 90° 的垂直向量 (-y, x)。</summary>
        public FixedVector2 Perpendicular => new(-Y, X);

        /// <summary>两点间距离。</summary>
        public static FixedInt Distance(FixedVector2 a, FixedVector2 b) => (b - a).Magnitude;

        /// <summary>两点间距离的平方（避免开方）。</summary>
        public static FixedInt SqrDistance(FixedVector2 a, FixedVector2 b) => (b - a).SqrMagnitude;

        /// <summary>线性插值。</summary>
        public static FixedVector2 Lerp(FixedVector2 a, FixedVector2 b, FixedInt t)
        {
            t = FixedInt.Clamp(t, FixedInt.Zero, FixedInt.OneVal);
            return a + (b - a) * t;
        }

        /// <summary>将向量限制在最大长度内。</summary>
        public static FixedVector2 ClampMagnitude(FixedVector2 v, FixedInt maxLen)
        {
            var sqrMag = v.SqrMagnitude;
            var sqrMax = maxLen * maxLen;
            if (sqrMag <= sqrMax) return v;
            return v.Normalized * maxLen;
        }

        /// <summary>沿法线反射。</summary>
        public static FixedVector2 Reflect(FixedVector2 dir, FixedVector2 normal)
        {
            var two = FixedInt.FromInt(2);
            return dir - normal * (two * Dot(dir, normal));
        }

        /// <summary>
        /// 将 current 方向向 target 方向旋转，每次最多旋转 maxRadians 弧度。
        /// 使用小角度近似 sin(θ)≈θ, cos(θ)≈1 进行增量旋转。
        /// 返回归一化后的新方向。
        /// </summary>
        public static FixedVector2 RotateToward(FixedVector2 current, FixedVector2 target, FixedInt maxRadians)
        {
            var curN = current.Normalized;
            var tarN = target.Normalized;
            if (curN == Zero || tarN == Zero) return curN;

            var dot   = Dot(curN, tarN);
            var cross = Cross(curN, tarN);

            // 已对齐（dot >= ~0.999）
            if (dot >= FixedInt.OneVal - (FixedInt.OneVal / FixedInt.FromInt(1000)))
                return tarN;

            // 完全反向时取垂直方向
            if (dot <= FixedInt.NegOne + (FixedInt.OneVal / FixedInt.FromInt(1000)))
            {
                // 旋转 maxRadians（取垂直方向）
                var perp = curN.Perpendicular;
                // 小角度旋转
                var result = curN + perp * maxRadians;
                return result.Normalized;
            }

            // 判断需要旋转多少（用 cross 的绝对值近似 sin(angle)）
            // sign = cross > 0 → 逆时针, cross < 0 → 顺时针
            FixedInt sign = cross >= FixedInt.Zero ? FixedInt.OneVal : FixedInt.NegOne;
            FixedInt sinAngle = cross >= FixedInt.Zero ? cross : -cross;

            if (sinAngle <= maxRadians)
                return tarN; // 角度够小，直接对齐

            // 旋转 maxRadians: new = cos(θ)*cur + sin(θ)*perp
            // 小角度: cos≈1, sin≈θ
            var perpDir = curN.Perpendicular * sign;
            var rotated = curN + perpDir * maxRadians;
            return rotated.Normalized;
        }

        public override string ToString() => $"({X}, {Y})";
    }
}
