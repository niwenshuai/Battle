namespace FrameSync
{
    /// <summary>
    /// 帧时间工具 — 秒与帧数的互转。
    /// 帧同步逻辑层以帧为时间单位，配置文件以秒为单位。
    ///
    /// 帧率架构：
    ///   - LogicFPS = 30  客户端战斗逻辑帧率
    ///   - NetTickRate = 15  服务器网络同步帧率
    ///   - 每两个逻辑帧对应一次网络同步帧 (LogicFramesPerNetTick = 2)
    /// </summary>
    public static class FrameTime
    {
        /// <summary>客户端战斗逻辑帧率（每秒逻辑帧数）。</summary>
        public const int FPS = 30;

        /// <summary>服务器网络同步帧率（每秒网络帧数）。</summary>
        public const int NetTickRate = 15;

        /// <summary>每个网络同步帧包含的逻辑帧数。</summary>
        public const int LogicFramesPerNetTick = FPS / NetTickRate; // 2

        /// <summary>秒 → 逻辑帧数（四舍五入取整）。使用 FixedInt 运算避免浮点精度跨平台差异。</summary>
        public static int Sec(float seconds)
        {
            return (FixedInt.FromFloat(seconds) * FixedInt.FromInt(FPS) + FixedInt.Half).ToInt();
        }

        /// <summary>逻辑帧数 → 秒（用于显示）。</summary>
        public static float ToSec(int frames)
        {
            return frames / (float)FPS;
        }
    }
}
