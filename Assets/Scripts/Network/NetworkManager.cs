using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// TCP 客户端管理器（MonoBehaviour 单例）。
///
/// 职责：
/// - 暴露 Connect / Disconnect / Send 等高层 API
/// - 在 Update 中消费 TcpConnection 投递的事件并派发 C# 事件
/// - 断线自动重连（指数退避，可配置）
/// - 心跳包定时发送（可选）
/// </summary>
public class NetworkManager : MonoBehaviour
{
    // ── 配置 ─────────────────────────────────────────────────
    [Header("连接设置")]
    [SerializeField] private string _host = "127.0.0.1";
    [SerializeField] private int    _port = 9000;

    [Header("重连设置")]
    [SerializeField] private bool  _autoReconnect      = true;
    [SerializeField] private float _reconnectBaseDelay = 1f;   // 首次重连等待（秒）
    [SerializeField] private float _reconnectMaxDelay  = 30f;  // 最大重连等待（秒）
    [SerializeField] private int   _maxReconnectTimes  = 10;   // 0 = 无限

    [Header("心跳设置")]
    [SerializeField] private bool  _heartbeatEnabled  = false;
    [SerializeField] private float _heartbeatInterval = 15f;   // 秒
    [Tooltip("心跳包内容（UTF-8 字符串），也可重写 BuildHeartbeatPayload()")]
    [SerializeField] private string _heartbeatPayload = "ping";

    // ── 事件（主线程） ───────────────────────────────────────
    /// <summary>连接成功。</summary>
    public event Action OnConnected;
    /// <summary>连接断开，参数为原因。</summary>
    public event Action<string> OnDisconnected;
    /// <summary>收到数据，参数为完整 payload。</summary>
    public event Action<byte[]> OnDataReceived;
    /// <summary>发生错误，参数为描述。</summary>
    public event Action<string> OnError;
    /// <summary>重连中，参数 (第几次, 下次延迟秒)。</summary>
    public event Action<int, float> OnReconnecting;

    // ── 状态 ─────────────────────────────────────────────────
    public bool IsConnected => _connection != null && _connection.IsConnected;
    public int  ReconnectCount => _reconnectCount;

    public enum State { Idle, Connecting, Connected, Reconnecting, Disposed }
    public State CurrentState { get; private set; } = State.Idle;

    // ── 内部 ─────────────────────────────────────────────────
    private TcpConnection _connection;
    private Coroutine     _reconnectCoroutine;
    private Coroutine     _heartbeatCoroutine;
    private int           _reconnectCount;
    private bool          _manualDisconnect;   // 区分用户主动断开和异常断开

    // ── 单例（可选，不强制） ─────────────────────────────────
    public static NetworkManager Instance { get; private set; }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        Dispose();
    }

    // ── 公共 API ─────────────────────────────────────────────

    /// <summary>连接到 Inspector 中配置的地址。</summary>
    public void Connect() => Connect(_host, _port);

    /// <summary>连接到指定地址。</summary>
    public void Connect(string host, int port)
    {
        _host = host;
        _port = port;
        _manualDisconnect = false;
        _reconnectCount   = 0;

        StopReconnect();

        CurrentState = State.Connecting;
        ConnectInternal();
    }

    /// <summary>主动断开，不自动重连。</summary>
    public void Disconnect()
    {
        _manualDisconnect = true;
        StopReconnect();
        StopHeartbeat();
        _connection?.Close();
        CurrentState = State.Idle;
    }

    /// <summary>发送 payload（线程安全）。</summary>
    public void Send(byte[] payload)
    {
        if (!IsConnected)
        {
            Debug.LogWarning("[NetworkManager] 未连接，无法发送");
            return;
        }
        _connection.Send(payload);
    }

    /// <summary>发送 UTF-8 字符串。</summary>
    public void Send(string text) => Send(System.Text.Encoding.UTF8.GetBytes(text));

    // ── Update 消费事件 ──────────────────────────────────────

    private void Update()
    {
        if (_connection == null) return;

        while (_connection.EventQueue.TryDequeue(out var evt))
        {
            switch (evt.Type)
            {
                case TcpConnection.EventType.Connected:
                    CurrentState    = State.Connected;
                    _reconnectCount = 0;
                    Debug.Log($"[NetworkManager] 已连接 {_host}:{_port}");
                    OnConnected?.Invoke();
                    StartHeartbeat();
                    break;

                case TcpConnection.EventType.Disconnected:
                    Debug.Log($"[NetworkManager] 断开: {evt.Message}");
                    StopHeartbeat();
                    OnDisconnected?.Invoke(evt.Message);
                    TryReconnect();
                    break;

                case TcpConnection.EventType.DataReceived:
                    OnDataReceived?.Invoke(evt.Data);
                    break;

                case TcpConnection.EventType.Error:
                    Debug.LogWarning($"[NetworkManager] 错误: {evt.Message}");
                    OnError?.Invoke(evt.Message);
                    // 连接阶段的错误也要尝试重连
                    if (CurrentState == State.Connecting)
                        TryReconnect();
                    break;
            }
        }
    }

    // ── 连接内部 ─────────────────────────────────────────────

    private void ConnectInternal()
    {
        _connection?.Dispose();
        _connection = new TcpConnection();

        // 在后台线程做阻塞连接
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            _connection.Connect(_host, _port);
        });
    }

    // ── 重连逻辑 ─────────────────────────────────────────────

    private void TryReconnect()
    {
        if (_manualDisconnect || !_autoReconnect) return;
        if (_maxReconnectTimes > 0 && _reconnectCount >= _maxReconnectTimes)
        {
            CurrentState = State.Idle;
            Debug.LogWarning("[NetworkManager] 已达最大重连次数");
            OnError?.Invoke("已达最大重连次数，停止重连");
            return;
        }

        StopReconnect();
        _reconnectCoroutine = StartCoroutine(ReconnectCoroutine());
    }

    private IEnumerator ReconnectCoroutine()
    {
        CurrentState = State.Reconnecting;

        // 指数退避：delay = base * 2^count，上限 maxDelay
        float delay = Mathf.Min(
            _reconnectBaseDelay * Mathf.Pow(2f, _reconnectCount),
            _reconnectMaxDelay);

        _reconnectCount++;
        Debug.Log($"[NetworkManager] 第 {_reconnectCount} 次重连，等待 {delay:F1}s ...");
        OnReconnecting?.Invoke(_reconnectCount, delay);

        yield return new WaitForSeconds(delay);

        CurrentState = State.Connecting;
        ConnectInternal();
        _reconnectCoroutine = null;
    }

    private void StopReconnect()
    {
        if (_reconnectCoroutine != null)
        {
            StopCoroutine(_reconnectCoroutine);
            _reconnectCoroutine = null;
        }
    }

    // ── 心跳 ─────────────────────────────────────────────────

    private void StartHeartbeat()
    {
        if (!_heartbeatEnabled) return;
        StopHeartbeat();
        _heartbeatCoroutine = StartCoroutine(HeartbeatCoroutine());
    }

    private void StopHeartbeat()
    {
        if (_heartbeatCoroutine != null)
        {
            StopCoroutine(_heartbeatCoroutine);
            _heartbeatCoroutine = null;
        }
    }

    private IEnumerator HeartbeatCoroutine()
    {
        var wait = new WaitForSeconds(_heartbeatInterval);
        while (IsConnected)
        {
            yield return wait;
            if (!IsConnected) break;

            byte[] payload = BuildHeartbeatPayload();
            _connection?.Send(payload);
        }
        _heartbeatCoroutine = null;
    }

    /// <summary>
    /// 构建心跳包内容。默认使用 Inspector 中配置的字符串，
    /// 可重写为自定义二进制格式。
    /// </summary>
    protected virtual byte[] BuildHeartbeatPayload()
    {
        return System.Text.Encoding.UTF8.GetBytes(_heartbeatPayload);
    }

    // ── 清理 ─────────────────────────────────────────────────

    private void Dispose()
    {
        CurrentState = State.Disposed;
        StopReconnect();
        StopHeartbeat();
        _connection?.Dispose();
        _connection = null;
    }
}
