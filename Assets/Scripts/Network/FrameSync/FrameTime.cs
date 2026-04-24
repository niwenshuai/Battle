namespace FrameSync
{
    /// <summary>
    /// 帧时间工具 — 秒与帧数的互转。
    /// 帧同步逻辑层以帧为时间单位，配置文件以秒为单位。
    /// </summary>
    public static class FrameTime
    {
        /// <summary>逻辑帧率（每秒帧数）。</summary>
        public const int FPS = 15;

        /// <summary>秒 → 帧数（四舍五入取整）。使用 FixedInt 运算避免浮点精度跨平台差异。</summary>
        public static int Sec(float seconds)
        {
            return (FixedInt.FromFloat(seconds) * FixedInt.FromInt(FPS)).ToInt();
        }

        /// <summary>帧数 → 秒（用于显示）。</summary>
        public static float ToSec(int frames)
        {
            return frames / (float)FPS;
        }
    }
}
