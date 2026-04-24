using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace FrameSync
{
    /// <summary>
    /// 帧同步客户端控制器（MonoBehaviour）。
    ///
    /// 职责：
    ///   1. 连接服务器并完成房间流程（加入 → 准备 → 等待开始）
    ///   2. 游戏中：每帧采集本地输入上报，接收服务器权威帧并驱动 IGameLogic
    ///   3. 预测 + 回滚支持（可选）
    ///   4. 追帧：如果积压了多帧，一次 Update 内快速执行多帧追上
    /// </summary>
    public class FrameSyncClient : MonoBehaviour
    {
        // ── 配置 ─────────────────────────────────────────────
        [Header("连接")]
        [SerializeField] private string _host = "127.0.0.1";
        [SerializeField] private int    _port = 9100;
        [SerializeField] private string _playerName = "Player";

        [Header("帧同步")]
        [Tooltip("每帧追帧上限，防止卡顿时一次性执行过多帧")]
        [SerializeField] private int _maxCatchUpPerFrame = 5;

        // ── 事件 ─────────────────────────────────────────────
        public event Action OnRoomJoined;
        public event Action<List<(byte id, string name, bool ready)>> OnRoomUpdated;
        public event Action<int> OnGameStarted;  // randomSeed
        public event Action<byte> OnGameEnded;   // winnerId
        public event Action<string> OnErrorOccurred;

        // ── 状态 ─────────────────────────────────────────────
        public enum Phase { Idle, Connecting, InRoom, Playing, Ended }
        public Phase CurrentPhase { get; private set; } = Phase.Idle;

        public byte LocalPlayerId { get; private set; }
        public int  CurrentFrame  { get; private set; }  // 逻辑帧号
        public int  ServerFrame   { get; private set; }  // 最后收到的服务器网络帧号
        public int  TickRate      { get; private set; } = FrameTime.NetTickRate;
        public int  LogicFPS      { get; private set; } = FrameTime.FPS;

        // ── 内部 ─────────────────────────────────────────────
        private NetworkManager _net;
        private IGameLogic     _gameLogic;

        // 帧缓冲：frameId → FrameData
        private readonly SortedList<int, FrameData> _frameBuffer = new();
        private float _tickAccumulator;
        private float _tickInterval; // = 1f / TickRate

        // ── 生命周期 ────────────────────────────────────────

        /// <summary>初始化并连接。需先注入 IGameLogic。</summary>
        public void Init(IGameLogic gameLogic)
        {
            _gameLogic = gameLogic ?? throw new ArgumentNullException(nameof(gameLogic));

            // 确保有 NetworkManager
            _net = NetworkManager.Instance;
            if (_net == null)
            {
                var go = new GameObject("NetworkManager");
                _net = go.AddComponent<NetworkManager>();
            }

            _net.OnConnected    += HandleConnected;
            _net.OnDisconnected += HandleDisconnected;
            _net.OnDataReceived += HandleData;
            _net.OnError        += HandleError;

            CurrentPhase = Phase.Connecting;
            _net.Connect(_host, _port);
        }

        private void OnDestroy()
        {
            if (_net != null)
            {
                _net.OnConnected    -= HandleConnected;
                _net.OnDisconnected -= HandleDisconnected;
                _net.OnDataReceived -= HandleData;
                _net.OnError        -= HandleError;
            }
        }

        // ── Update 驱动逻辑帧 ────────────────────────────────

        private void Update()
        {
            if (CurrentPhase != Phase.Playing) return;
            if (_gameLogic == null) return;

            // 1. 采集输入并上报（每个逻辑帧都上报）
            var input = _gameLogic.SampleLocalInput();
            input.PlayerId = LocalPlayerId;
            _net.Send(Proto.PackPlayerInput(CurrentFrame, input));

            // 2. 按逻辑帧率驱动追帧
            _tickAccumulator += Time.deltaTime;
            int executed = 0;
            while (_tickAccumulator >= _tickInterval && executed < _maxCatchUpPerFrame)
            {
                _tickAccumulator -= _tickInterval;

                // 计算当前逻辑帧对应的网络帧号和帧内偏移
                int logicFramesPerNetTick = LogicFPS / TickRate;
                int netFrameId = CurrentFrame / logicFramesPerNetTick;
                int subIndex   = CurrentFrame % logicFramesPerNetTick;

                if (!_frameBuffer.TryGetValue(netFrameId, out var frame))
                    break; // 还没收到对应的网络帧，等待

                // 构建单逻辑帧的输入
                var singleFrame = new FrameData
                {
                    FrameId = CurrentFrame,
                    LogicFrameCount = 1,
                    LogicFrameInputs = new PlayerInput[][]
                    {
                        subIndex < frame.LogicFrameCount ? frame.LogicFrameInputs[subIndex] : frame.LogicFrameInputs[0]
                    },
                };

                _gameLogic.OnLogicUpdate(singleFrame);
                CurrentFrame++;
                executed++;
            }
        }

        // ── 消息处理 ────────────────────────────────────────

        private void HandleConnected()
        {
            Debug.Log("[FrameSyncClient] 已连接，发送 JoinRoom");
            _net.Send(Proto.PackJoinRoom(_playerName));
        }

        private void HandleDisconnected(string reason)
        {
            Debug.LogWarning($"[FrameSyncClient] 断开: {reason}");
            // NetworkManager 会自动重连；重连后需要重新加入房间
        }

        private void HandleError(string err)
        {
            OnErrorOccurred?.Invoke(err);
        }

        private void HandleData(byte[] data)
        {
            if (data == null || data.Length == 0) return;

            var msgType = Proto.PeekType(data);

            switch (msgType)
            {
                case MsgType.JoinRoomAck:
                    HandleJoinRoomAck(data);
                    break;
                case MsgType.RoomSnapshot:
                    HandleRoomSnapshot(data);
                    break;
                case MsgType.GameStart:
                    HandleGameStart(data);
                    break;
                case MsgType.GameEnd:
                    HandleGameEnd(data);
                    break;
                case MsgType.FrameData:
                    HandleFrameData(data);
                    break;
                default:
                    Debug.LogWarning($"[FrameSyncClient] 未知消息类型: 0x{(byte)msgType:X2}");
                    break;
            }
        }

        private void HandleJoinRoomAck(byte[] data)
        {
            // [0x81][playerId][tickRate][logicFPS]
            if (data.Length < 3) return;
            LocalPlayerId = data[1];
            TickRate       = data[2];
            LogicFPS       = data.Length >= 4 ? data[3] : TickRate * FrameTime.LogicFramesPerNetTick;
            _tickInterval  = 1f / LogicFPS;
            CurrentPhase   = Phase.InRoom;
            Debug.Log($"[FrameSyncClient] 已加入房间 — PlayerId={LocalPlayerId}, TickRate={TickRate}, LogicFPS={LogicFPS}");
            OnRoomJoined?.Invoke();
        }

        private void HandleRoomSnapshot(byte[] data)
        {
            using var r = Proto.BodyReader(data);
            int count = r.ReadByte();
            var players = new List<(byte id, string name, bool ready)>(count);
            for (int i = 0; i < count; i++)
            {
                byte id = r.ReadByte();
                int nameLen = r.ReadByte();
                string name = System.Text.Encoding.UTF8.GetString(r.ReadBytes(nameLen));
                bool ready = r.ReadBoolean();
                players.Add((id, name, ready));
            }
            OnRoomUpdated?.Invoke(players);
        }

        private void HandleGameStart(byte[] data)
        {
            using var r = Proto.BodyReader(data);
            int seed = Proto.ReadInt32BE(r);

            CurrentFrame     = 0;
            ServerFrame      = 0;
            _tickAccumulator = 0f;
            _frameBuffer.Clear();
            CurrentPhase = Phase.Playing;

            _gameLogic.OnGameStart(0, LocalPlayerId, seed); // playerCount 由 RoomSnapshot 获取
            Debug.Log($"[FrameSyncClient] 游戏开始! Seed={seed}");
            OnGameStarted?.Invoke(seed);
        }

        private void HandleGameEnd(byte[] data)
        {
            byte winnerId = data.Length > 1 ? data[1] : (byte)0;
            CurrentPhase = Phase.Ended;
            _gameLogic.OnGameEnd(winnerId);
            Debug.Log($"[FrameSyncClient] 游戏结束, Winner={winnerId}");
            OnGameEnded?.Invoke(winnerId);
        }

        private void HandleFrameData(byte[] data)
        {
            using var r = Proto.BodyReader(data);
            var frame = FrameData.Deserialize(r);

            ServerFrame = Mathf.Max(ServerFrame, frame.FrameId);

            // 缓存帧（可能乱序到达）
            if (!_frameBuffer.ContainsKey(frame.FrameId))
                _frameBuffer[frame.FrameId] = frame;
        }

        // ── 公共 API ────────────────────────────────────────

        /// <summary>发送准备信号。</summary>
        public void Ready()
        {
            if (CurrentPhase != Phase.InRoom) return;
            _net.Send(Proto.PackPlayerReady());
            Debug.Log("[FrameSyncClient] 已准备");
        }

        /// <summary>主动离开房间。</summary>
        public void LeaveRoom()
        {
            _net.Send(Proto.PackLeaveRoom());
            CurrentPhase = Phase.Idle;
        }

        /// <summary>当前延迟帧数。</summary>
        public int FrameDelay => ServerFrame - CurrentFrame;
    }
}
