using UnityEngine;
using FrameSync;

/// <summary>
/// 帧同步 Demo 入口脚本。挂到场景中任意 GameObject 上。
///
/// 运行前先启动 FrameSyncServer：
///   cd Tools/FrameSyncServer && dotnet run
///
/// Play 模式下：
///   - 自动连接并加入房间
///   - 按 F5 准备（所有人准备后游戏自动开始）
///   - WASD / 方向键移动方块
///   - 按 Esc 离开房间
/// </summary>
public class FrameSyncDemoEntry : MonoBehaviour
{
    [SerializeField] private string _host = "127.0.0.1";
    [SerializeField] private int    _port = 9100;
    [SerializeField] private string _playerName = "Tester";

    private FrameSyncClient   _client;
    private FrameSyncDemoLogic _logic;

    private void Start()
    {
        // 确保 NetworkManager 存在且用于帧同步服务器的端口
        _logic  = gameObject.AddComponent<FrameSyncDemoLogic>();
        _client = gameObject.AddComponent<FrameSyncClient>();

        // 通过反射/SerializedObject 设置私有字段（或改为公开）
        // 这里直接用 Inspector 配置，或调用公共 API
        _client.OnRoomJoined   += () => Debug.Log("[Demo] 已加入房间，按 F5 准备");
        _client.OnRoomUpdated  += players =>
        {
            string info = "";
            foreach (var (id, n, r) in players)
                info += $"  #{id} {n} {(r ? "✓" : "○")}\n";
            Debug.Log($"[Demo] 房间状态:\n{info}");
        };
        _client.OnGameStarted  += seed => Debug.Log($"[Demo] 游戏开始! WASD 移动方块");
        _client.OnGameEnded    += w    => Debug.Log($"[Demo] 游戏结束, Winner={w}");
        _client.OnErrorOccurred += err => Debug.LogWarning($"[Demo] 错误: {err}");

        _client.Init(_logic);
    }

    private void Update()
    {
        if (_client == null) return;

        // F5 = 准备
        if (Input.GetKeyDown(KeyCode.F5))
            _client.Ready();

        // Esc = 离开
        if (Input.GetKeyDown(KeyCode.Escape))
            _client.LeaveRoom();

        // 显示状态到屏幕
    }

    private void OnGUI()
    {
        if (_client == null) return;

        GUILayout.BeginArea(new Rect(10, 10, 300, 150));
        GUILayout.Label($"状态: {_client.CurrentPhase}");
        GUILayout.Label($"PlayerId: {_client.LocalPlayerId}");
        GUILayout.Label($"当前帧: {_client.CurrentFrame}");
        GUILayout.Label($"服务器帧: {_client.ServerFrame}");
        GUILayout.Label($"延迟帧数: {_client.FrameDelay}");

        if (_client.CurrentPhase == FrameSyncClient.Phase.InRoom)
            GUILayout.Label("按 F5 准备");
        if (_client.CurrentPhase == FrameSyncClient.Phase.Playing)
            GUILayout.Label("WASD 移动 | Esc 退出");

        GUILayout.EndArea();
    }
}
