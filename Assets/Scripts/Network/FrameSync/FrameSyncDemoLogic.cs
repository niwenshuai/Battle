using UnityEngine;
using FrameSync;

/// <summary>
/// 帧同步 Demo 游戏逻辑：简单的多人移动方块。
/// 每个玩家控制一个方块，用 WASD / 方向键移动。
///
/// 演示确定性逻辑：所有运算使用 FixedInt，不依赖 Time.deltaTime。
/// </summary>
public class FrameSyncDemoLogic : MonoBehaviour, IGameLogic
{
    [SerializeField] private float _moveDisplaySpeed = 5f;

    // ── 内部状态（确定性） ───────────────────────────────────
    private int _playerCount;
    private byte _localPlayerId;
    private FixedVector2[] _positions;
    private static readonly FixedInt MoveSpeed = FixedInt.FromFloat(0.1f); // 每帧移动量
    private static readonly FixedInt InputScale = FixedInt.FromInt(1) / FixedInt.FromInt(1000);

    // ── 渲染用 ──────────────────────────────────────────────
    private GameObject[] _cubes;
    private static readonly Color[] PlayerColors = { Color.blue, Color.red, Color.green, Color.yellow };

    // ── IGameLogic 实现 ──────────────────────────────────────

    public void OnGameStart(int playerCount, byte localPlayerId, int randomSeed)
    {
        // playerCount 可能在 RoomSnapshot 中确定，这里先用 4 作为上限
        _localPlayerId = localPlayerId;
        _playerCount = 4; // 最大支持

        _positions = new FixedVector2[_playerCount + 1]; // index = playerId (1-based)
        for (int i = 1; i <= _playerCount; i++)
            _positions[i] = new FixedVector2(FixedInt.FromInt(i * 2 - 5), FixedInt.Zero);

        // 创建渲染方块
        CleanupCubes();
        _cubes = new GameObject[_playerCount + 1];
        for (int i = 1; i <= _playerCount; i++)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = $"Player_{i}";
            cube.transform.localScale = Vector3.one * 0.8f;
            var rend = cube.GetComponent<Renderer>();
            rend.material.color = PlayerColors[(i - 1) % PlayerColors.Length];

            // 标记本地玩家
            if (i == _localPlayerId)
                cube.transform.localScale = Vector3.one * 1.0f;

            cube.transform.position = new Vector3(_positions[i].X.ToFloat(), 0.5f, _positions[i].Y.ToFloat());
            _cubes[i] = cube;
        }

        Debug.Log($"[DemoLogic] GameStart: localId={localPlayerId}, seed={randomSeed}");
    }

    public void OnLogicUpdate(FrameData frame)
    {
        if (_positions == null) return;

        foreach (var input in frame.LogicFrameInputs[0])
        {
            byte pid = input.PlayerId;
            if (pid == 0 || pid > _playerCount) continue;

            // 确定性移动
            var moveX = FixedInt.FromInt(input.MoveX) * InputScale * MoveSpeed;
            var moveY = FixedInt.FromInt(input.MoveY) * InputScale * MoveSpeed;
            _positions[pid] = _positions[pid] + new FixedVector2(moveX, moveY);
        }

        // 更新渲染位置
        for (int i = 1; i <= _playerCount; i++)
        {
            if (_cubes != null && i < _cubes.Length && _cubes[i] != null)
            {
                var target = new Vector3(_positions[i].X.ToFloat(), 0.5f, _positions[i].Y.ToFloat());
                _cubes[i].transform.position = Vector3.Lerp(
                    _cubes[i].transform.position, target, _moveDisplaySpeed * Time.deltaTime);
            }
        }
    }

    public void OnGameEnd(byte winnerId)
    {
        Debug.Log($"[DemoLogic] GameEnd: winner={winnerId}");
        CleanupCubes();
    }

    public PlayerInput SampleLocalInput()
    {
        int mx = 0, my = 0;
        if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow))    my =  1000;
        if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow))  my = -1000;
        if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow))  mx = -1000;
        if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) mx =  1000;

        uint buttons = 0;
        if (Input.GetKey(KeyCode.Space))      buttons |= PlayerInput.ButtonFire;
        if (Input.GetKey(KeyCode.LeftShift))   buttons |= PlayerInput.ButtonJump;

        return new PlayerInput
        {
            MoveX   = mx,
            MoveY   = my,
            Buttons = buttons,
        };
    }

    // ── 辅助 ─────────────────────────────────────────────────

    private void CleanupCubes()
    {
        if (_cubes == null) return;
        foreach (var c in _cubes)
            if (c != null) Destroy(c);
        _cubes = null;
    }

    private void OnDestroy() => CleanupCubes();
}
