using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

/// <summary>
/// TCP 连接核心层，负责 Socket 连接 / 收发 / 断开。
/// 全部网络 IO 运行在独立线程，通过 ConcurrentQueue 将事件投递回主线程。
///
/// 协议格式（Length-Prefixed）：
///   [4 字节大端长度][payload]
/// </summary>
public class TcpConnection : IDisposable
{
    // ── 常量 ────────────────────────────────────────────────
    private const int HeaderSize       = 4;           // 包头：4 字节大端 payload 长度
    private const int MaxPayloadSize   = 1024 * 1024; // 单包最大 1 MB
    private const int RecvBufferSize   = 8192;

    // ── 网络事件 ────────────────────────────────────────────
    public enum EventType { Connected, Disconnected, DataReceived, Error }

    public readonly struct NetEvent
    {
        public readonly EventType Type;
        public readonly byte[]    Data;    // DataReceived 时有效
        public readonly string    Message; // Error / Disconnected 时有效

        public NetEvent(EventType type, byte[] data = null, string msg = null)
        {
            Type    = type;
            Data    = data;
            Message = msg;
        }
    }

    // ── 状态 ─────────────────────────────────────────────────
    public bool IsConnected => _socket is { Connected: true } && !_disposed;
    public string Host { get; private set; }
    public int    Port { get; private set; }

    private Socket          _socket;
    private Thread          _recvThread;
    private volatile bool   _disposed;

    // 收包缓冲
    private readonly PacketBuffer _packetBuffer = new();

    // 主线程消费的事件队列
    public readonly ConcurrentQueue<NetEvent> EventQueue = new();

    // 发送队列 + 发送线程
    private readonly ConcurrentQueue<byte[]> _sendQueue = new();
    private readonly AutoResetEvent          _sendSignal = new(false);
    private Thread                           _sendThread;

    // ── 连接 ─────────────────────────────────────────────────

    public void Connect(string host, int port)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TcpConnection));

        Host = host;
        Port = port;

        Close();   // 确保旧连接被清理

        try
        {
            var addresses = Dns.GetHostAddresses(host);
            if (addresses.Length == 0)
            {
                EventQueue.Enqueue(new NetEvent(EventType.Error, msg: $"DNS 解析失败: {host}"));
                return;
            }

            _socket = new Socket(addresses[0].AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay        = true,
                SendTimeout    = 5000,
                ReceiveTimeout = 0,
                SendBufferSize    = 65536,
                ReceiveBufferSize = 65536,
            };

            var result = _socket.BeginConnect(addresses[0], port, null, null);
            bool success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5));
            if (!success || !_socket.Connected)
            {
                _socket.Close();
                _socket = null;
                EventQueue.Enqueue(new NetEvent(EventType.Error, msg: $"连接超时: {host}:{port}"));
                return;
            }
            _socket.EndConnect(result);

            _packetBuffer.Reset();

            // 启动收发线程
            _recvThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "TCP-Recv" };
            _recvThread.Start();

            _sendThread = new Thread(SendLoop) { IsBackground = true, Name = "TCP-Send" };
            _sendThread.Start();

            EventQueue.Enqueue(new NetEvent(EventType.Connected));
        }
        catch (Exception ex)
        {
            EventQueue.Enqueue(new NetEvent(EventType.Error, msg: $"连接异常: {ex.Message}"));
        }
    }

    // ── 发送 ─────────────────────────────────────────────────

    /// <summary>发送 payload（自动添加 4 字节长度头）。线程安全。</summary>
    public void Send(byte[] payload)
    {
        if (payload == null || payload.Length == 0) return;
        if (!IsConnected) return;

        // 组装帧：[4 字节大端长度][payload]
        byte[] frame = new byte[HeaderSize + payload.Length];
        int len = payload.Length;
        frame[0] = (byte)(len >> 24);
        frame[1] = (byte)(len >> 16);
        frame[2] = (byte)(len >> 8);
        frame[3] = (byte)(len);
        Buffer.BlockCopy(payload, 0, frame, HeaderSize, payload.Length);

        _sendQueue.Enqueue(frame);
        _sendSignal.Set();
    }

    private void SendLoop()
    {
        try
        {
            while (!_disposed && IsConnected)
            {
                _sendSignal.WaitOne(200);

                while (_sendQueue.TryDequeue(out byte[] frame))
                {
                    if (_disposed || _socket == null) return;

                    int offset = 0;
                    int remaining = frame.Length;
                    while (remaining > 0)
                    {
                        int sent = _socket.Send(frame, offset, remaining, SocketFlags.None);
                        if (sent <= 0) throw new SocketException((int)SocketError.ConnectionReset);
                        offset    += sent;
                        remaining -= sent;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (!_disposed)
                EventQueue.Enqueue(new NetEvent(EventType.Error, msg: $"发送异常: {ex.Message}"));
            HandleDisconnect("发送线程异常断开");
        }
    }

    // ── 接收 ─────────────────────────────────────────────────

    private void ReceiveLoop()
    {
        byte[] buffer = new byte[RecvBufferSize];
        try
        {
            while (!_disposed && _socket is { Connected: true })
            {
                int bytesRead = _socket.Receive(buffer, 0, buffer.Length, SocketFlags.None);
                if (bytesRead <= 0)
                {
                    HandleDisconnect("服务器主动关闭连接");
                    return;
                }

                _packetBuffer.Append(buffer, 0, bytesRead);

                // 拆包：可能一次收到多个完整包
                while (_packetBuffer.TryReadPacket(out byte[] payload))
                {
                    if (payload.Length > MaxPayloadSize)
                    {
                        HandleDisconnect($"收到超大包 ({payload.Length} bytes)，疑似协议错误");
                        return;
                    }
                    EventQueue.Enqueue(new NetEvent(EventType.DataReceived, data: payload));
                }
            }
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.Interrupted)
        {
            // Close() 中断了阻塞的 Receive，正常退出
        }
        catch (ObjectDisposedException)
        {
            // Socket 已被 Dispose
        }
        catch (Exception ex)
        {
            if (!_disposed)
                EventQueue.Enqueue(new NetEvent(EventType.Error, msg: $"接收异常: {ex.Message}"));
            HandleDisconnect("接收线程异常断开");
        }
    }

    // ── 断开 / 清理 ──────────────────────────────────────────

    private void HandleDisconnect(string reason)
    {
        if (_disposed) return;
        Close();
        EventQueue.Enqueue(new NetEvent(EventType.Disconnected, msg: reason));
    }

    public void Close()
    {
        try { _socket?.Shutdown(SocketShutdown.Both); } catch { /* ignore */ }
        try { _socket?.Close(); } catch { /* ignore */ }
        _socket = null;

        _sendSignal.Set(); // 唤醒发送线程使其退出
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
        _sendSignal.Dispose();
    }
}
