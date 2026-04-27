using System.Text;
using UnityEngine;

// [InputSystem重构] T/X/R 改用 GameInput

/// <summary>
/// 网络框架使用示例。挂到任意 GameObject 上即可测试。
/// </summary>
public class NetworkDemo : MonoBehaviour
{
    private void Start()
    {
        var net = NetworkManager.Instance;
        if (net == null)
        {
            Debug.LogWarning("[NetworkDemo] 场景中需要一个 NetworkManager");
            return;
        }

        net.OnConnected     += ()    => Debug.Log("[Demo] 连接成功");
        net.OnDisconnected  += r     => Debug.Log($"[Demo] 断开: {r}");
        net.OnDataReceived  += data  => Debug.Log($"[Demo] 收到: {Encoding.UTF8.GetString(data)}");
        net.OnError         += err   => Debug.LogWarning($"[Demo] 错误: {err}");
        net.OnReconnecting  += (n,d) => Debug.Log($"[Demo] 第 {n} 次重连，{d:F1}s 后...");

        // 自动连接（使用 Inspector 中配置的地址）
        net.Connect();
    }

    private void Update()
    {
        var gi = GameInput.Instance;
        if (gi == null) return;

        // [InputSystem重构] T/X/R 改用 GameInput.WasPressedThisFrame()
        // 按 T 发送一条测试消息
        if (gi.SendPressed && NetworkManager.Instance != null)
        {
            NetworkManager.Instance.Send("Hello Server " + Time.frameCount);
            Debug.Log("[Demo] 已发送测试消息");
        }

        // 按 X 主动断开
        if (gi.DisconnectPressed && NetworkManager.Instance != null)
        {
            NetworkManager.Instance.Disconnect();
            Debug.Log("[Demo] 主动断开");
        }

        // 按 R 重新连接
        if (gi.ReconnectPressed && NetworkManager.Instance != null)
        {
            NetworkManager.Instance.Connect();
            Debug.Log("[Demo] 手动重连");
        }
    }
}
