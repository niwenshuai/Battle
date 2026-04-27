using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 全局输入管理器 — 基于新 Input System 的统一输入入口。
/// 替代旧版 UnityEngine.Input，通过代码生成的 InputActionAsset 管理所有输入。
///
/// 使用方式：
///   - 将 GameInput 挂到场景中任意 GameObject（或代码 AddComponent）
///   - 各脚本通过 GameInput.Instance 读取输入状态
///
/// 改动说明：
///   - 所有旧版 Input.GetKey / Input.GetKeyDown 调用已替换为对应 InputAction
///   - 选角数字键使用 wasPressedThisFrame（等效旧版 GetKeyDown）
///   - 大招/移动使用 IsPressed()（等效旧版 GetKey）
///   - F5/Esc 使用 wasPressedThisFrame（等效旧版 GetKeyDown）
/// </summary>
public class GameInput : MonoBehaviour
{
    public static GameInput Instance { get; private set; }

    // ── Input Action Asset（代码生成） ──────────────────────
    InputActionAsset _asset;

    // ── Action Maps ─────────────────────────────────────────
    InputActionMap _battleMap;
    InputActionMap _networkMap;
    InputActionMap _demoMap;

    // ── 选角数字键 (Battle map) ────────────────────────────
    InputAction _selectKey1;
    InputAction _selectKey2;
    InputAction _selectKey3;
    InputAction _selectKey4;
    InputAction _selectKey5;
    InputAction _selectKey6;
    InputAction _selectKey7;
    InputAction _selectKey8;
    InputAction _selectKey9;
    InputAction _selectKey0;

    // ── 战斗动作 (Battle map) ──────────────────────────────
    InputAction _ultAction;

    // ── 网络/系统动作 (Network map) ────────────────────────
    InputAction _readyAction;
    InputAction _escapeAction;

    // ── Demo 动作 (Demo map) ───────────────────────────────
    InputAction _moveX;
    InputAction _moveY;
    InputAction _jumpAction;
    InputAction _demoFireAction;

    // ── NetworkDemo 动作 (Demo map) ────────────────────────
    InputAction _sendAction;
    InputAction _disconnectAction;
    InputAction _reconnectAction;

    // ── InfiniteScrollDemo 动作 ────────────────────────────
    InputAction _appendAction;
    InputAction _jumpToAction;

    // ═══════════════════════════════════════════════════════
    //  公开查询接口 — 各脚本通过这些属性读取输入
    // ═══════════════════════════════════════════════════════

    #region Battle — 选角阶段

    /// <summary>数字键1 是否本帧按下（等效旧 Input.GetKeyDown）</summary>
    public bool SelectKey1Pressed => _selectKey1.WasPressedThisFrame();
    /// <summary>数字键2 是否本帧按下</summary>
    public bool SelectKey2Pressed => _selectKey2.WasPressedThisFrame();
    /// <summary>数字键3 是否本帧按下</summary>
    public bool SelectKey3Pressed => _selectKey3.WasPressedThisFrame();
    /// <summary>数字键4 是否本帧按下</summary>
    public bool SelectKey4Pressed => _selectKey4.WasPressedThisFrame();
    /// <summary>数字键5 是否本帧按下</summary>
    public bool SelectKey5Pressed => _selectKey5.WasPressedThisFrame();
    /// <summary>数字键6 是否本帧按下</summary>
    public bool SelectKey6Pressed => _selectKey6.WasPressedThisFrame();
    /// <summary>数字键7 是否本帧按下</summary>
    public bool SelectKey7Pressed => _selectKey7.WasPressedThisFrame();
    /// <summary>数字键8 是否本帧按下</summary>
    public bool SelectKey8Pressed => _selectKey8.WasPressedThisFrame();
    /// <summary>数字键9 是否本帧按下</summary>
    public bool SelectKey9Pressed => _selectKey9.WasPressedThisFrame();
    /// <summary>数字键0 是否本帧按下</summary>
    public bool SelectKey0Pressed => _selectKey0.WasPressedThisFrame();

    #endregion

    #region Battle — 战斗阶段

    /// <summary>大招键(Space)是否持续按住（等效旧 Input.GetKey）</summary>
    public bool UltPressed => _ultAction.IsPressed();

    #endregion

    #region Network / System

    /// <summary>F5 准备键是否本帧按下</summary>
    public bool ReadyPressed => _readyAction.WasPressedThisFrame();
    /// <summary>Esc 键是否本帧按下</summary>
    public bool EscapePressed => _escapeAction.WasPressedThisFrame();

    #endregion

    #region FrameSyncDemo

    /// <summary>WASD/方向键 水平轴（-1, 0, 1）</summary>
    public float MoveXValue => _moveX.ReadValue<float>();
    /// <summary>WASD/方向键 垂直轴（-1, 0, 1）</summary>
    public float MoveYValue => _moveY.ReadValue<float>();
    /// <summary>LeftShift 跳跃键是否按住</summary>
    public bool JumpPressed => _jumpAction.IsPressed();

    #endregion

    #region NetworkDemo

    /// <summary>T键 发送测试消息是否本帧按下</summary>
    public bool SendPressed => _sendAction.WasPressedThisFrame();
    /// <summary>X键 断开连接是否本帧按下</summary>
    public bool DisconnectPressed => _disconnectAction.WasPressedThisFrame();
    /// <summary>R键 重连是否本帧按下</summary>
    public bool ReconnectPressed => _reconnectAction.WasPressedThisFrame();

    #endregion

    #region InfiniteScrollDemo

    /// <summary>Space 追加数据是否本帧按下</summary>
    public bool AppendPressed => _appendAction.WasPressedThisFrame();
    /// <summary>J键 跳转是否本帧按下</summary>
    public bool JumpToPressed => _jumpToAction.WasPressedThisFrame();
    /// <summary>数字键1-6 本帧按下（InfiniteScrollDemo 用）</summary>
    public bool ScrollKey1Pressed => _selectKey1.WasPressedThisFrame();
    public bool ScrollKey2Pressed => _selectKey2.WasPressedThisFrame();
    public bool ScrollKey3Pressed => _selectKey3.WasPressedThisFrame();
    public bool ScrollKey4Pressed => _selectKey4.WasPressedThisFrame();
    public bool ScrollKey5Pressed => _selectKey5.WasPressedThisFrame();
    public bool ScrollKey6Pressed => _selectKey6.WasPressedThisFrame();

    #endregion

    // ═══════════════════════════════════════════════════════
    //  生命周期
    // ═══════════════════════════════════════════════════════

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        BuildActionAsset();
        EnableAll();
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (_asset != null)
        {
            _battleMap?.Dispose();
            _networkMap?.Dispose();
            _demoMap?.Dispose();
        }
    }

    void EnableAll()
    {
        _battleMap?.Enable();
        _networkMap?.Enable();
        _demoMap?.Enable();
    }

    // ═══════════════════════════════════════════════════════
    //  构建 InputActionAsset（代码生成，无需 .inputactions 文件）
    // ═══════════════════════════════════════════════════════

    void BuildActionAsset()
    {
        _asset = ScriptableObject.CreateInstance<InputActionAsset>();

        // ── Battle Map ──────────────────────────────────────
        _battleMap = _asset.AddActionMap("Battle");

        // 选角数字键：主键盘 + 小键盘双重绑定
        _selectKey1 = AddKeyAction(_battleMap, "SelectKey1", "<Keyboard>/1", "<Keypad>/1");
        _selectKey2 = AddKeyAction(_battleMap, "SelectKey2", "<Keyboard>/2", "<Keypad>/2");
        _selectKey3 = AddKeyAction(_battleMap, "SelectKey3", "<Keyboard>/3", "<Keypad>/3");
        _selectKey4 = AddKeyAction(_battleMap, "SelectKey4", "<Keyboard>/4", "<Keypad>/4");
        _selectKey5 = AddKeyAction(_battleMap, "SelectKey5", "<Keyboard>/5", "<Keypad>/5");
        _selectKey6 = AddKeyAction(_battleMap, "SelectKey6", "<Keyboard>/6", "<Keypad>/6");
        _selectKey7 = AddKeyAction(_battleMap, "SelectKey7", "<Keyboard>/7", "<Keypad>/7");
        _selectKey8 = AddKeyAction(_battleMap, "SelectKey8", "<Keyboard>/8", "<Keypad>/8");
        _selectKey9 = AddKeyAction(_battleMap, "SelectKey9", "<Keyboard>/9", "<Keypad>/9");
        _selectKey0 = AddKeyAction(_battleMap, "SelectKey0", "<Keyboard>/0", "<Keypad>/0");

        // 大招
        _ultAction = AddKeyAction(_battleMap, "Ult", "<Keyboard>/space");

        // ── Network Map ─────────────────────────────────────
        _networkMap = _asset.AddActionMap("Network");
        _readyAction   = AddKeyAction(_networkMap, "Ready",   "<Keyboard>/f5");
        _escapeAction  = AddKeyAction(_networkMap, "Escape",  "<Keyboard>/escape");

        // ── Demo Map ────────────────────────────────────────
        _demoMap = _asset.AddActionMap("Demo");

        // WASD + 方向键移动（Value 类型，1D Axis）
        _moveX = AddAxisAction(_demoMap, "MoveX",
            "<Keyboard>/a", "<Keyboard>/d",
            "<Keyboard>/leftArrow", "<Keyboard>/rightArrow");
        _moveY = AddAxisAction(_demoMap, "MoveY",
            "<Keyboard>/s", "<Keyboard>/w",
            "<Keyboard>/downArrow", "<Keyboard>/upArrow");

        _jumpAction    = AddKeyAction(_demoMap, "Jump",    "<Keyboard>/leftShift");
        _demoFireAction = AddKeyAction(_demoMap, "Fire",   "<Keyboard>/space");

        // NetworkDemo
        _sendAction       = AddKeyAction(_demoMap, "Send",       "<Keyboard>/t");
        _disconnectAction = AddKeyAction(_demoMap, "Disconnect", "<Keyboard>/x");
        _reconnectAction  = AddKeyAction(_demoMap, "Reconnect",  "<Keyboard>/r");

        // InfiniteScrollDemo
        _appendAction = AddKeyAction(_demoMap, "Append", "<Keyboard>/space");
        _jumpToAction = AddKeyAction(_demoMap, "JumpTo", "<Keyboard>/j");
    }

    /// <summary>添加按钮类型 Action，支持多键绑定。</summary>
    static InputAction AddKeyAction(InputActionMap map, string name, params string[] bindings)
    {
        var action = map.AddAction(name, InputActionType.Button);
        foreach (var path in bindings)
            action.AddBinding(path);
        return action;
    }

    /// <summary>添加 1D 轴类型 Action（负键 + 正键，支持多组）。</summary>
    static InputAction AddAxisAction(InputActionMap map, string name,
        string neg1, string pos1, string neg2 = null, string pos2 = null)
    {
        var action = map.AddAction(name, InputActionType.Value);
        // 第一组（如 WASD）
        action.AddCompositeBinding("1DAxis")
            .With("Negative", neg1)
            .With("Positive", pos1);
        // 第二组（如方向键，可选）
        if (neg2 != null && pos2 != null)
        {
            action.AddCompositeBinding("1DAxis")
                .With("Negative", neg2)
                .With("Positive", pos2);
        }
        return action;
    }
}
