using UnityEngine;
using FrameSync;

/// <summary>
/// 本地单人入口 — 无需服务器，启动即可战斗。
///
/// 选角流程：先为对手(P2)选角，再为自己(P1)选角，全部由键盘控制。
/// 战斗由行为树全自动驱动，玩家仅需按 Space 释放大招。
///
/// 用法：将此脚本挂到场景中任意 GameObject 上（替代 BattleEntry）。
/// </summary>
public class LocalBattleEntry : MonoBehaviour
{
    BattleLogic _logic;
    BattleView  _view;
    CharacterSelectUI _selectUI;
    int         _frameId;
    bool        _started;
    float       _tickAccumulator;
    int         _pendingSelectionMx; // 缓存选角输入（GetKeyDown 只持续1帧，tick 可能晚到）
    byte        _pendingSelectionPid; // 缓存选角目标玩家ID
    bool        _selectingForP2 = true; // true=正在为P2选角，false=为P1选角
    int         _p2SelectionsSent;
    const float TickInterval = 1f / 15f;

    void Start()
    {
        _logic = gameObject.AddComponent<BattleLogic>();
        _view  = gameObject.AddComponent<BattleView>();

        // 创建选角UI
        _selectUI = gameObject.AddComponent<CharacterSelectUI>();
        _selectUI.IsLocalMode = true;
        _selectUI.LocalPlayerId = 1;
        _selectUI.TeamSize = CharacterConfig.TeamSize;
        _selectUI.OnCharacterPicked = OnCharacterPicked;

        // 桥接事件
        _view.EventSource         = _logic.EventQueue;
        _view.ClearEventsCallback = () => _logic.ClearEvents();
        _view.LocalPlayerId       = 1;
        _view.ConnectionInfo      = "Local";
        _view.UseExternalSelectUI = true; // 禁用IMGUI选角界面

        // 直接启动游戏（跳过网络房间流程）
        _logic.OnGameStart(2, 1, System.Environment.TickCount);
        _started = true;
        _frameId = 0;
        _selectingForP2 = true;
        _p2SelectionsSent = 0;

        Debug.Log($"[LocalBattle] 本地模式启动 — 每队{CharacterConfig.TeamSize}个角色，点击角色头像选角");
    }

    void OnCharacterPicked(byte playerId, CharacterType charType)
    {
        _pendingSelectionMx = (int)charType;
        _pendingSelectionPid = playerId;
    }

    void Update()
    {
        if (!_started) return;

        // 按 15Hz 节奏驱动逻辑帧
        _tickAccumulator += Time.deltaTime;

        // 战斗中仍采集键盘输入（Space放大招）
        var rawInput = _logic.SampleLocalInput();

        while (_tickAccumulator >= TickInterval)
        {
            _tickAccumulator -= TickInterval;

            var p1Input = new PlayerInput { PlayerId = 1 };
            var p2Input = new PlayerInput { PlayerId = 2 };

            if (_logic.Phase == BattleLogic.BattlePhase.Selecting)
            {
                // 选角通过UI回调驱动
                if (_pendingSelectionMx != 0)
                {
                    if (_pendingSelectionPid == 2)
                        p2Input.MoveX = _pendingSelectionMx;
                    else
                        p1Input.MoveX = _pendingSelectionMx;
                    _pendingSelectionMx = 0;
                    _pendingSelectionPid = 0;
                }
            }
            else if (_logic.Phase == BattleLogic.BattlePhase.Fighting)
            {
                // 战斗开始后隐藏选角UI
                if (_selectUI != null && _selectUI.gameObject.activeSelf)
                    _selectUI.Hide();

                // 战斗中：Space放大招
                p1Input.Buttons = rawInput.Buttons;
            }

            var frame = new FrameData
            {
                FrameId = _frameId,
                Inputs  = new[] { p1Input, p2Input },
            };

            _logic.OnLogicUpdate(frame);
            _frameId++;
        }

        // 更新GUI提示信息
        if (_logic.Phase == BattleLogic.BattlePhase.Selecting)
        {
            _view.ConnectionInfo = "Local — 选角中";
        }
        else
        {
            _view.ConnectionInfo = "Local";
        }

        _view.CurrentFrame = _frameId;
    }
}
