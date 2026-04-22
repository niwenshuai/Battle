namespace FrameSync
{
    /// <summary>
    /// 游戏逻辑接口 — 帧同步框架通过此接口驱动具体的游戏玩法。
    ///
    /// 实现要求：
    ///   1. 所有逻辑必须是确定性的（相同输入 → 相同结果）
    ///   2. 禁止使用 UnityEngine.Random / System.Random（用框架提供的 seed 自建 RNG）
    ///   3. 禁止使用 float 运算做状态相关计算（用 FixedInt / 整数）
    ///   4. 禁止依赖 Time.deltaTime（由框架提供固定 tickInterval）
    /// </summary>
    public interface IGameLogic
    {
        /// <summary>
        /// 游戏开始时调用一次。
        /// </summary>
        /// <param name="playerCount">参与的玩家数量。</param>
        /// <param name="localPlayerId">本机玩家 ID。</param>
        /// <param name="randomSeed">随机种子（所有客户端一致）。</param>
        void OnGameStart(int playerCount, byte localPlayerId, int randomSeed);

        /// <summary>
        /// 每个逻辑帧调用一次（Lockstep Tick）。
        /// </summary>
        /// <param name="frame">权威帧数据，包含所有玩家的输入。</param>
        void OnLogicUpdate(FrameData frame);

        /// <summary>
        /// 游戏结束时调用。
        /// </summary>
        /// <param name="winnerId">获胜玩家 ID（0 表示平局或无胜者）。</param>
        void OnGameEnd(byte winnerId);

        /// <summary>
        /// 每帧采集本地玩家输入，由框架在 Unity Update 中调用。
        /// </summary>
        PlayerInput SampleLocalInput();
    }
}
