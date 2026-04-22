using UnityEngine;
using FrameSync;

/// <summary>
/// 本地单人入口 — 无需服务器，启动即可战斗。
///
/// P1 由键盘控制（选角 + 大招），P2 自动随机选角。
/// 战斗由行为树全自动驱动，玩家仅需按 Space 释放大招。
///
/// 用法：将此脚本挂到场景中任意 GameObject 上（替代 BattleEntry）。
/// </summary>
public class LocalBattleEntry : MonoBehaviour
{
    [Header("虚拟玩家")]
    [Tooltip("P2 自动选择的角色（None=每个槽位随机）")]
    [SerializeField] private CharacterType _botCharacter = CharacterType.None;

    BattleLogic _logic;
    BattleView  _view;
    int         _frameId;
    bool        _started;
    float       _tickAccumulator;
    int         _botSelectionsSent;
    int         _pendingSelectionMx; // 缓存选角输入（GetKeyDown 只持续1帧，tick 可能晚到）
    const float TickInterval = 1f / 15f;

    void Start()
    {
        _logic = gameObject.AddComponent<BattleLogic>();
        _view  = gameObject.AddComponent<BattleView>();

        // 桥接事件
        _view.EventSource         = _logic.EventQueue;
        _view.ClearEventsCallback = () => _logic.ClearEvents();
        _view.LocalPlayerId       = 1;
        _view.ConnectionInfo      = "Local";

        // 直接启动游戏（跳过网络房间流程）
        _logic.OnGameStart(2, 1, System.Environment.TickCount);
        _started = true;
        _frameId = 0;
        _botSelectionsSent = 0;

        Debug.Log($"[LocalBattle] 本地模式启动 — 每队{CharacterConfig.TeamSize}个角色，按 1/2/3/4/5 选角，Space 放大招");
    }

    void Update()
    {
        if (!_started) return;

        // 按 15Hz 节奏驱动逻辑帧
        _tickAccumulator += Time.deltaTime;

        // 每帧采集输入，外部缓存选角（GetKeyDown 只持续1帧，tick 可能隔多帧才来）
        var p1Input = _logic.SampleLocalInput();
        p1Input.PlayerId = 1;
        if (p1Input.MoveX != 0)
            _pendingSelectionMx = p1Input.MoveX;
        if (_pendingSelectionMx != 0)
            p1Input.MoveX = _pendingSelectionMx;

        while (_tickAccumulator >= TickInterval)
        {
            _tickAccumulator -= TickInterval;

            // P2 虚拟玩家输入
            var p2Input = new PlayerInput { PlayerId = 2 };

            if (_logic.Phase == BattleLogic.BattlePhase.Selecting)
            {
                int teamSize = CharacterConfig.TeamSize;
                if (_botSelectionsSent < teamSize)
                {
                    var bot = _botCharacter;
                    if (bot == CharacterType.None)
                    {
                        int r = (Time.frameCount * 7 + 13 + _botSelectionsSent * 3) % 5;
                        bot = r == 0 ? CharacterType.Warrior
                            : r == 1 ? CharacterType.Archer
                            : r == 2 ? CharacterType.Assassin
                            : r == 3 ? CharacterType.Mage
                            : CharacterType.Healer;
                    }
                    p2Input.MoveX = (int)bot;
                    _botSelectionsSent++;
                }
            }

            var frame = new FrameData
            {
                FrameId = _frameId,
                Inputs  = new[] { p1Input, p2Input },
            };

            _logic.OnLogicUpdate(frame);
            _frameId++;

            // tick 已消费选角输入，清除缓存
            _pendingSelectionMx = 0;
            p1Input = new PlayerInput { PlayerId = 1 };
        }

        _view.CurrentFrame = _frameId;
    }
}
