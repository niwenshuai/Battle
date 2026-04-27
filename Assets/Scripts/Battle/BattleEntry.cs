using UnityEngine;
using FrameSync;

// [InputSystem重构] F5/Esc 改用 GameInput

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
    CharacterSelectUI _selectUI;

    void Start()
    {
        // [InputSystem重构] 确保 GameInput 单例存在
        if (GameInput.Instance == null)
        {
            var inputGO = new GameObject("GameInput");
            inputGO.AddComponent<GameInput>();
        }

        _logic  = gameObject.AddComponent<BattleLogic>();
        _client = gameObject.AddComponent<FrameSyncClient>();

        // 创建显示层并桥接事件（唯一的逻辑层→显示层通道）
        _view = gameObject.AddComponent<BattleView>();
        _view.EventSource         = _logic.EventQueue;
        _view.ClearEventsCallback = () => _logic.ClearEvents();
        _view.UseExternalSelectUI = true; // 禁用IMGUI选角界面

        // 创建大招按钮UI
        var ultBtnGO = new GameObject("UltimateButtonUI");
        ultBtnGO.transform.SetParent(transform);
        var ultBtn = ultBtnGO.AddComponent<UltimateButtonUI>();
        ultBtn.OnUltRequested += (playerId) => _logic.PendingUltRequest = playerId;
        _view.UltButtonUI = ultBtn;

        // 创建选角UI（网络模式）
        _selectUI = gameObject.AddComponent<CharacterSelectUI>();
        _selectUI.IsLocalMode = false;
        _selectUI.TeamSize = CharacterConfig.TeamSize;
        _selectUI.OnCharacterPicked = OnCharacterPicked;

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
            // 游戏开始时，将本地玩家 ID 传给显示层和选角UI
            _view.LocalPlayerId = _logic.LocalPlayerId;
            _selectUI.LocalPlayerId = _logic.LocalPlayerId;
        };
        _client.OnGameEnded     += w => Debug.Log($"[Battle] 服务器结束 winner={w}");
        _client.OnErrorOccurred += e => Debug.LogWarning($"[Battle] {e}");

        _client.Init(_logic);
    }

    void OnCharacterPicked(byte playerId, CharacterType charType)
    {
        // 网络模式下设置UI选角输入，通过SampleLocalInput发送给服务器
        _logic.PendingUISelection = (int)charType;
    }

    void Update()
    {
        if (_client == null) return;

        // [InputSystem重构] F5/Esc 改用 GameInput.WasPressedThisFrame()
        var gi = GameInput.Instance;
        if (gi != null && gi.ReadyPressed)
            _client.Ready();
        if (gi != null && gi.EscapePressed)
            _client.LeaveRoom();

        // 传递连接信息给显示层（仅简单值传递，不暴露逻辑层对象）
        if (_view != null)
        {
            _view.ConnectionInfo = _client.CurrentPhase.ToString();
            _view.CurrentFrame = _client.CurrentFrame;
        }

        // 同步选角事件到选角UI
        if (_selectUI != null && _logic != null)
        {
            foreach (var evt in _logic.EventQueue)
            {
                if (evt.Type == BattleEventType.CharSelected)
                {
                    byte pid = evt.SourceId;
                    var ct = (CharacterType)evt.IntParam;
                    // 对手的选角通知到UI
                    if (pid != _logic.LocalPlayerId)
                        _selectUI.OnOpponentSelected(pid, ct);
                }
                else if (evt.Type == BattleEventType.BattleStart)
                {
                    _selectUI.Hide();
                }
            }
        }
    }
}
