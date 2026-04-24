// ═══════════════════════════════════════════════════════════════
// 帧同步服务器 — 配合 FrameSyncClient 使用
//
// 运行：cd Tools/FrameSyncServer && dotnet run
//       cd Tools/FrameSyncServer && dotnet run -- 9100 15
//       参数1: 端口(默认9100)  参数2: NetTickRate(默认15)
//
// 帧率架构：
//   - 网络同步帧率 15Hz（NetTickRate），每秒15次广播 FrameData
//   - 客户端逻辑帧率 30Hz（LogicFPS），每帧驱动一次游戏逻辑
//   - 每个 FrameData 携带2个逻辑帧的输入（LogicFramesPerNetTick=2）
//   - 服务器每个 tick 收集2轮输入打包成1个 FrameData 广播
//
// 房间流程：
//   1. 客户端 JoinRoom → 分配 PlayerId，返回 JoinRoomAck
//   2. 广播 RoomSnapshot
//   3. 所有玩家 PlayerReady → 广播 GameStart
//   4. 服务器以 NetTickRate 固定频率收集输入、广播 FrameData
//   5. 任一客户端 LeaveRoom 或断开 → 广播 GameEnd
// ═══════════════════════════════════════════════════════════════

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FrameSyncServer
{
    // ── 协议常量（与客户端 FrameSyncProto 保持一致） ──────────

    enum MsgType : byte
    {
        JoinRoom = 0x01, LeaveRoom = 0x02, PlayerReady = 0x03, PlayerInput = 0x10,
        JoinRoomAck = 0x81, RoomSnapshot = 0x82, GameStart = 0x83, GameEnd = 0x84,
        FrameData = 0x90,
    }

    // ── 房间玩家 ─────────────────────────────────────────────

    class RoomPlayer
    {
        public byte Id;
        public string Name;
        public bool Ready;
        public Session Session;
    }

    // ── 房间 ─────────────────────────────────────────────────

    class Room
    {
        public const int MaxPlayers = 4;
        public const int LogicFramesPerNetTick = 2; // 每个网络帧包含2个逻辑帧

        public readonly List<RoomPlayer> Players = new();
        public bool GameRunning;
        public int CurrentFrame;       // 网络帧号
        public int TickRateHz;         // 网络同步帧率 (15)
        public int LogicFPS;           // 客户端逻辑帧率 (30)
        public int RandomSeed;

        private readonly object _lock = new();
        // 每个玩家每个逻辑子帧的输入收集
        // Key: (playerId, subFrameIndex) → 13 bytes raw input
        private readonly ConcurrentDictionary<(byte playerId, int subFrame), byte[]> _pendingInputs = new();
        private int _currentSubFrame; // 当前正在收集的逻辑子帧索引 (0 或 1)

        public Room(int tickRate)
        {
            TickRateHz = tickRate;
            LogicFPS = tickRate * LogicFramesPerNetTick;
        }

        public RoomPlayer AddPlayer(Session session, string name)
        {
            lock (_lock)
            {
                if (Players.Count >= MaxPlayers) return null;
                byte id = FindFreeId();
                var p = new RoomPlayer { Id = id, Name = name, Session = session };
                Players.Add(p);
                return p;
            }
        }

        public void RemovePlayer(Session session)
        {
            lock (_lock) { Players.RemoveAll(p => p.Session == session); }
        }

        public bool AllReady()
        {
            lock (_lock) { return Players.Count >= 1 && Players.All(p => p.Ready); }
        }

        public void SetCurrentSubFrame(int subFrame)
        {
            _currentSubFrame = subFrame % LogicFramesPerNetTick;
        }

        public void CollectInput(byte playerId, byte[] inputBytes)
        {
            // 根据输入中的 frameId 确定属于哪个逻辑子帧
            // 客户端以30Hz发送输入，每个网络tick(15Hz)会收到2次输入
            // 我们用 _currentSubFrame 来分配：0=前半tick, 1=后半tick
            // 简化方案：用轮转方式分配，每个新 playerId 输入交替分配到 subFrame 0/1
            // 更精确的方案：用输入中的 frameId % LogicFramesPerNetTick
            var key = (playerId, _currentSubFrame);

            // 同一子帧内，非零 MoveX 不被后续零值覆盖
            if (_pendingInputs.TryGetValue(key, out var existing))
            {
                int oldMx = (existing[1] << 24) | (existing[2] << 16) | (existing[3] << 8) | existing[4];
                int newMx = (inputBytes[1] << 24) | (inputBytes[2] << 16) | (inputBytes[3] << 8) | inputBytes[4];
                if (oldMx != 0 && newMx == 0)
                {
                    uint oldBtn = ((uint)existing[9] << 24) | ((uint)existing[10] << 16) | ((uint)existing[11] << 8) | existing[12];
                    uint newBtn = ((uint)inputBytes[9] << 24) | ((uint)inputBytes[10] << 16) | ((uint)inputBytes[11] << 8) | inputBytes[12];
                    uint merged = oldBtn | newBtn;
                    existing[9] = (byte)(merged >> 24);
                    existing[10] = (byte)(merged >> 16);
                    existing[11] = (byte)(merged >> 8);
                    existing[12] = (byte)merged;
                    return;
                }
            }
            _pendingInputs[key] = inputBytes;
        }

        /// <summary>生成当前网络帧的 FrameData（包含2个逻辑帧的输入）并推进帧号。</summary>
        public byte[] BuildFrameData()
        {
            lock (_lock)
            {
                using var ms = new MemoryStream();
                using var w = new BinaryWriter(ms);
                w.Write((byte)MsgType.FrameData);
                WriteInt32BE(w, CurrentFrame);
                w.Write((byte)LogicFramesPerNetTick); // 逻辑帧数 = 2

                // 写入每个逻辑子帧的玩家输入
                for (int subFrame = 0; subFrame < LogicFramesPerNetTick; subFrame++)
                {
                    w.Write((byte)Players.Count);
                    foreach (var p in Players)
                    {
                        var key = (p.Id, subFrame);
                        if (_pendingInputs.TryRemove(key, out var raw))
                        {
                            w.Write(raw);
                        }
                        else
                        {
                            // 该玩家该子帧无输入，填空输入
                            w.Write(p.Id);
                            WriteInt32BE(w, 0); // MoveX
                            WriteInt32BE(w, 0); // MoveY
                            WriteUInt32BE(w, 0); // Buttons
                        }
                    }
                }

                _pendingInputs.Clear(); // 清理残留
                _currentSubFrame = 0;
                CurrentFrame++;
                return ms.ToArray();
            }
        }

        public List<(byte id, string name, bool ready)> GetSnapshot()
        {
            lock (_lock) { return Players.Select(p => (p.Id, p.Name, p.Ready)).ToList(); }
        }

        private byte FindFreeId()
        {
            for (byte i = 1; i <= MaxPlayers; i++)
                if (Players.All(p => p.Id != i)) return i;
            return 0;
        }

        static void WriteInt32BE(BinaryWriter w, int v)
        {
            w.Write((byte)(v >> 24)); w.Write((byte)(v >> 16));
            w.Write((byte)(v >> 8)); w.Write((byte)v);
        }
        static void WriteUInt32BE(BinaryWriter w, uint v)
        {
            w.Write((byte)(v >> 24)); w.Write((byte)(v >> 16));
            w.Write((byte)(v >> 8)); w.Write((byte)v);
        }
    }

    // ── Session ──────────────────────────────────────────────

    class Session
    {
        const int HeaderSize = 4;
        const int RecvBufSize = 8192;
        const int MaxPayload = 1024 * 1024;

        public int Id { get; }
        public string EndPoint { get; }

        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private readonly Action<Session, byte[]> _onData;
        private readonly Action<Session> _onDisconnected;
        private volatile bool _closed;

        private readonly byte[] _buf = new byte[RecvBufSize];
        private byte[] _accum = new byte[4096];
        private int _accumLen;

        public Session(int id, TcpClient tcp, Action<Session, byte[]> onData, Action<Session> onDisc)
        {
            Id = id; _tcp = tcp; _stream = tcp.GetStream();
            _onData = onData; _onDisconnected = onDisc;
            EndPoint = tcp.Client.RemoteEndPoint?.ToString() ?? "?";
            tcp.NoDelay = true;
        }

        public void Start() => new Thread(RecvLoop) { IsBackground = true, Name = $"FS-Recv-{Id}" }.Start();

        public void Send(byte[] payload)
        {
            if (_closed) return;
            byte[] frame = new byte[HeaderSize + payload.Length];
            int len = payload.Length;
            frame[0] = (byte)(len >> 24); frame[1] = (byte)(len >> 16);
            frame[2] = (byte)(len >> 8); frame[3] = (byte)len;
            Buffer.BlockCopy(payload, 0, frame, HeaderSize, payload.Length);
            lock (_stream) { _stream.Write(frame, 0, frame.Length); }
        }

        /// <summary>延迟发送（模拟网络延迟）。</summary>
        public void SendDelayed(byte[] payload, int delayMs)
        {
            if (_closed) return;
            _ = Task.Run(async () =>
            {
                await Task.Delay(delayMs);
                Send(payload);
            });
        }

        public void Close()
        {
            if (_closed) return; _closed = true;
            try { _stream.Close(); } catch { }
            try { _tcp.Close(); } catch { }
        }

        private void RecvLoop()
        {
            try
            {
                while (!_closed)
                {
                    int n = _stream.Read(_buf, 0, _buf.Length);
                    if (n <= 0) break;
                    EnsureAccum(_accumLen + n);
                    Buffer.BlockCopy(_buf, 0, _accum, _accumLen, n);
                    _accumLen += n;
                    while (_accumLen >= HeaderSize)
                    {
                        int pLen = (_accum[0] << 24) | (_accum[1] << 16) | (_accum[2] << 8) | _accum[3];
                        if (pLen < 0 || pLen > MaxPayload) { Close(); return; }
                        if (_accumLen < HeaderSize + pLen) break;
                        byte[] payload = new byte[pLen];
                        Buffer.BlockCopy(_accum, HeaderSize, payload, 0, pLen);
                        int rem = _accumLen - HeaderSize - pLen;
                        if (rem > 0) Buffer.BlockCopy(_accum, HeaderSize + pLen, _accum, 0, rem);
                        _accumLen = rem;
                        _onData(this, payload);
                    }
                }
            }
            catch (Exception) when (_closed) { }
            catch { }
            finally { Close(); _onDisconnected(this); }
        }

        private void EnsureAccum(int needed)
        {
            if (needed <= _accum.Length) return;
            int ns = _accum.Length;
            while (ns < needed) ns *= 2;
            byte[] nb = new byte[ns];
            Buffer.BlockCopy(_accum, 0, nb, 0, _accumLen);
            _accum = nb;
        }
    }

    // ── 主程序 ───────────────────────────────────────────────

    class Program
    {
        static readonly ConcurrentDictionary<int, Session> Sessions = new();
        static readonly ConcurrentDictionary<Session, RoomPlayer> SessionToPlayer = new();
        static int _nextId;
        static Room _room;
        static Timer _tickTimer;
        static readonly CancellationTokenSource Cts = new();

        // ── 延迟模拟 ────────────────────────────────────────
        const int LatencyMinMs = 30;
        const int LatencyMaxMs = 550;
        static readonly ThreadLocal<Random> Rng = new(() => new Random(Guid.NewGuid().GetHashCode()));
        static int RandomDelay() => Rng.Value.Next(LatencyMinMs, LatencyMaxMs + 1);

        static void Main(string[] args)
        {
            int port = 9100;
            int tickRate = 15;
            if (args.Length > 0 && int.TryParse(args[0], out int p)) port = p;
            if (args.Length > 1 && int.TryParse(args[1], out int t)) tickRate = t;

            _room = new Room(tickRate);

            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            Log($"帧同步服务器已启动 — 端口 {port}, NetTickRate {tickRate}Hz, LogicFPS {_room.LogicFPS}Hz");
            Log("命令: quit=退出  list=玩家列表  start=强制开始  end=强制结束");

            // 接受连接
            var acceptThread = new Thread(() =>
            {
                while (!Cts.IsCancellationRequested)
                {
                    try
                    {
                        if (!listener.Pending()) { Thread.Sleep(50); continue; }
                        var tcp = listener.AcceptTcpClient();
                        int id = Interlocked.Increment(ref _nextId);
                        var session = new Session(id, tcp, OnData, OnDisconnected);
                        Sessions[id] = session;
                        session.Start();
                        Log($"[+] Session #{id} 已连接 ({session.EndPoint})");
                    }
                    catch (Exception ex)
                    {
                        if (!Cts.IsCancellationRequested) Log($"Accept 异常: {ex.Message}");
                    }
                }
            })
            { IsBackground = true };
            acceptThread.Start();

            // // 控制台
            // while (true)
            // {
            //     string line = Console.ReadLine()?.Trim();
            //     if (line == null || line.Equals("quit", StringComparison.OrdinalIgnoreCase)) break;
            //     if (line.Equals("list", StringComparison.OrdinalIgnoreCase))
            //     {
            //         foreach (var entry in _room.GetSnapshot())
            //             Log($"  #{entry.id} {entry.name} ready={entry.ready}");
            //         if (_room.GameRunning) Log($"  游戏进行中 Frame={_room.CurrentFrame}");
            //         continue;
            //     }
            //     if (line.Equals("start", StringComparison.OrdinalIgnoreCase)) { StartGame(); continue; }
            //     if (line.Equals("end", StringComparison.OrdinalIgnoreCase))   { EndGame(0); continue; }
            // }
            // ========== 修改这里 ==========
            // 等待取消信号（Ctrl+C 或 systemd 停止）
            Console.CancelKeyPress += (sender, e) =>
            {
                Log("收到停止信号，正在关闭...");
                e.Cancel = true; // 阻止立即退出
                Cts.Cancel();
            };

            // 等待取消信号
            var waitHandle = new ManualResetEventSlim(false);
            Cts.Token.Register(() => waitHandle.Set());
            waitHandle.Wait();

            // Cts.Cancel();
            _tickTimer?.Dispose();
            foreach (var kv in Sessions) kv.Value.Close();
            listener.Stop();
            Log("服务器已关闭");
        }

        // ── 消息分发 ────────────────────────────────────────

        static void OnData(Session session, byte[] data)
        {
            if (data.Length == 0) return;
            var msgType = (MsgType)data[0];

            switch (msgType)
            {
                case MsgType.JoinRoom:
                    HandleJoinRoom(session, data);
                    break;
                case MsgType.LeaveRoom:
                    HandleLeaveRoom(session);
                    break;
                case MsgType.PlayerReady:
                    HandlePlayerReady(session);
                    break;
                case MsgType.PlayerInput:
                    HandlePlayerInput(session, data);
                    break;
            }
        }

        static void HandleJoinRoom(Session session, byte[] data)
        {
            string name = "Player";
            if (data.Length > 2)
            {
                int nameLen = data[1];
                if (data.Length >= 2 + nameLen)
                    name = Encoding.UTF8.GetString(data, 2, nameLen);
            }

            var player = _room.AddPlayer(session, name);
            if (player == null)
            {
                Log($"房间已满，拒绝 Session #{session.Id}");
                session.Close();
                return;
            }

            SessionToPlayer[session] = player;

            // 回复 JoinRoomAck（控制消息立即发送，不加延迟）
            // [0x81][playerId][tickRate][logicFPS]
            session.Send(new byte[] { (byte)MsgType.JoinRoomAck, player.Id, (byte)_room.TickRateHz, (byte)_room.LogicFPS });

            Log($"[JoinRoom] #{player.Id} '{name}'");

            // 广播房间快照
            BroadcastRoomSnapshot();
        }

        static void HandleLeaveRoom(Session session)
        {
            if (SessionToPlayer.TryRemove(session, out var player))
            {
                Log($"[LeaveRoom] #{player.Id} '{player.Name}'");
                _room.RemovePlayer(session);

                if (_room.GameRunning)
                    EndGame(0);
                else
                    BroadcastRoomSnapshot();
            }
        }

        static void HandlePlayerReady(Session session)
        {
            if (SessionToPlayer.TryGetValue(session, out var player))
            {
                player.Ready = true;
                Log($"[Ready] #{player.Id} '{player.Name}'");
                BroadcastRoomSnapshot();

                // 检查是否所有人都准备好了
                if (_room.AllReady() && !_room.GameRunning)
                    StartGame();
            }
        }

        static void HandlePlayerInput(Session session, byte[] data)
        {
            if (!_room.GameRunning) return;
            if (!SessionToPlayer.TryGetValue(session, out var player)) return;

            // data: [1 byte MsgType][4 byte frameId][13 byte PlayerInput]
            if (data.Length < 1 + 4 + 13) return;

            // 从 frameId 计算子帧索引：客户端逻辑帧号 % LogicFramesPerNetTick
            int clientFrameId = (data[1] << 24) | (data[2] << 16) | (data[3] << 8) | data[4];
            int subFrame = clientFrameId % Room.LogicFramesPerNetTick;
            _room.SetCurrentSubFrame(subFrame);

            // 提取 PlayerInput 原始字节（13 bytes）
            byte[] inputRaw = new byte[13];
            Buffer.BlockCopy(data, 5, inputRaw, 0, 13);
            _room.CollectInput(player.Id, inputRaw);
        }

        static void OnDisconnected(Session session)
        {
            Sessions.TryRemove(session.Id, out _);
            Log($"[-] Session #{session.Id} 断开");

            if (SessionToPlayer.TryRemove(session, out var player))
            {
                _room.RemovePlayer(session);
                if (_room.GameRunning)
                    EndGame(0);
                else
                    BroadcastRoomSnapshot();
            }
        }

        // ── 游戏控制 ────────────────────────────────────────

        static void StartGame()
        {
            if (_room.GameRunning) return;

            var seed = (int)(DateTime.UtcNow.Ticks & 0x7FFFFFFF);
            _room.RandomSeed = seed;
            _room.CurrentFrame = 0;
            _room.GameRunning = true;

            // 广播 GameStart
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write((byte)MsgType.GameStart);
            w.Write((byte)(seed >> 24)); w.Write((byte)(seed >> 16));
            w.Write((byte)(seed >> 8)); w.Write((byte)seed);
            byte[] startMsg = ms.ToArray();
            BroadcastImmediate(startMsg); // 控制消息立即发送，确保先于 FrameData 到达

            Log($"[GameStart] Seed={seed}, Players={_room.Players.Count}");

            // 启动 Tick 定时器（初始延迟 LatencyMaxMs + interval，确保 GameStart 先到）
            int intervalMs = 1000 / _room.TickRateHz;
            _tickTimer = new Timer(Tick, null, LatencyMaxMs + intervalMs, intervalMs);
        }

        static void EndGame(byte winnerId)
        {
            if (!_room.GameRunning) return;
            _room.GameRunning = false;
            _tickTimer?.Dispose();
            _tickTimer = null;

            BroadcastImmediate(new byte[] { (byte)MsgType.GameEnd, winnerId });
            Log($"[GameEnd] Winner={winnerId}, TotalFrames={_room.CurrentFrame}");

            // 重置准备状态
            foreach (var p in _room.Players) p.Ready = false;
            BroadcastRoomSnapshot();
        }

        static void Tick(object _)
        {
            if (!_room.GameRunning) return;

            byte[] frameData = _room.BuildFrameData();
            Broadcast(frameData);
        }

        // ── 广播 ────────────────────────────────────────────

        /// <summary>延迟广播（仅用于 FrameData，模拟网络延迟）。</summary>
        static void Broadcast(byte[] payload)
        {
            foreach (var p in _room.Players)
            {
                try { p.Session.SendDelayed(payload, RandomDelay()); }
                catch { /* 断开的会被 OnDisconnected 清理 */ }
            }
        }

        /// <summary>立即广播（用于控制消息：GameStart/GameEnd/RoomSnapshot）。</summary>
        static void BroadcastImmediate(byte[] payload)
        {
            foreach (var p in _room.Players)
            {
                try { p.Session.Send(payload); }
                catch { }
            }
        }

        static void BroadcastRoomSnapshot()
        {
            var snapshot = _room.GetSnapshot();
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write((byte)MsgType.RoomSnapshot);
            w.Write((byte)snapshot.Count);
            foreach (var (id, name, ready) in snapshot)
            {
                w.Write(id);
                var nb = Encoding.UTF8.GetBytes(name ?? "");
                w.Write((byte)nb.Length);
                w.Write(nb);
                w.Write(ready);
            }
            BroadcastImmediate(ms.ToArray());
        }

        static void Log(string msg) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
    }
}
