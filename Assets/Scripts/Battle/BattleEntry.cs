using UnityEngine;
using FrameSync;

/// <summary>
/// 自动战斗入口脚本。挂到场景中任意 GameObject 上。
///
/// 运行前启动 FrameSyncServer：
///   cd Tools/FrameSyncServer && dotnet run
///
/// Play 模式：
///   1. 自动连接并加入房间
///   2. 按 F5 准备
///   3. 按 1~5 选角（每队选 TeamSize 个角色）：
///      1=剑士  2=弓手  3=刺客  4=法师  5=医疗师
///   4. 双方选角后自动开始战斗
///   5. 战斗中按 Space 释放大招
///   6. Esc 离开房间
///
/// 架构说明：
///   BattleEntry 仅负责桥接 —— 将 BattleLogic 的事件队列引用传给 BattleView。
///   BattleView 完全不知道 BattleLogic / BattleFighter 的存在。
/// </summary>
public class BattleEntry : MonoBehaviour
{
    FrameSyncClient _client;
    BattleLogic     _logic;
    BattleView      _view;

    void Start()
    {
        _logic  = gameObject.AddComponent<BattleLogic>();
        _client = gameObject.AddComponent<FrameSyncClient>();

        // 创建显示层并桥接事件（唯一的逻辑层→显示层通道）
        _view = gameObject.AddComponent<BattleView>();
        _view.EventSource         = _logic.EventQueue;
        _view.ClearEventsCallback = () => _logic.ClearEvents();

        _client.OnRoomJoined    += () => Debug.Log("[Battle] 已加入房间，按 F5 准备");
        _client.OnRoomUpdated   += players =>
        {
            string info = "";
            foreach (var (id, n, r) in players)
                info += $"  #{id} {n} {(r ? "✓" : "○")}\n";
            Debug.Log($"[Battle] 房间:\n{info}");
        };
        _client.OnGameStarted   += _ =>
        {
            // 游戏开始时，将本地玩家 ID 传给显示层
            _view.LocalPlayerId = _logic.LocalPlayerId;
        };
        _client.OnGameEnded     += w => Debug.Log($"[Battle] 服务器结束 winner={w}");
        _client.OnErrorOccurred += e => Debug.LogWarning($"[Battle] {e}");

        _client.Init(_logic);
    }

    void Update()
    {
        if (_client == null) return;

        if (Input.GetKeyDown(KeyCode.F5))
            _client.Ready();
        if (Input.GetKeyDown(KeyCode.Escape))
            _client.LeaveRoom();

        // 传递连接信息给显示层（仅简单值传递，不暴露逻辑层对象）
        if (_view != null)
        {
            _view.ConnectionInfo = _client.CurrentPhase.ToString();
            _view.CurrentFrame = _client.CurrentFrame;
        }
    }
}
