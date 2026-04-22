using System;

/// <summary>
/// 粘包/拆包缓冲区。
///
/// 协议格式：[4 字节大端 payload 长度][payload bytes]
/// 线程安全说明：仅在接收线程中使用，无需加锁。
/// </summary>
public class PacketBuffer
{
    private const int HeaderSize     = 4;
    private const int InitialSize    = 4096;
    private const int MaxBufferSize  = 4 * 1024 * 1024; // 防止恶意数据撑爆内存

    private byte[] _buffer;
    private int    _writePos;  // 已写入但未读取的尾部

    public PacketBuffer()
    {
        _buffer  = new byte[InitialSize];
        _writePos = 0;
    }

    /// <summary>清空缓冲区。</summary>
    public void Reset()
    {
        _writePos = 0;
    }

    /// <summary>将网络层收到的原始字节追加到缓冲区。</summary>
    public void Append(byte[] data, int offset, int count)
    {
        if (count <= 0) return;

        EnsureCapacity(_writePos + count);
        Buffer.BlockCopy(data, offset, _buffer, _writePos, count);
        _writePos += count;
    }

    /// <summary>
    /// 尝试从缓冲区中读取一个完整的 payload。
    /// 成功时 payload 为完整的包体数据，缓冲区头部前移；
    /// 不完整时返回 false，缓冲区不变。
    /// </summary>
    public bool TryReadPacket(out byte[] payload)
    {
        payload = null;

        if (_writePos < HeaderSize) return false;

        // 读取大端 4 字节长度
        int payloadLen = (_buffer[0] << 24) |
                         (_buffer[1] << 16) |
                         (_buffer[2] << 8)  |
                          _buffer[3];

        if (payloadLen < 0 || payloadLen > MaxBufferSize)
        {
            // 协议错误，清空防止死循环
            Reset();
            return false;
        }

        int totalLen = HeaderSize + payloadLen;
        if (_writePos < totalLen) return false;

        // 提取 payload
        payload = new byte[payloadLen];
        Buffer.BlockCopy(_buffer, HeaderSize, payload, 0, payloadLen);

        // 将剩余字节搬到头部
        int remaining = _writePos - totalLen;
        if (remaining > 0)
            Buffer.BlockCopy(_buffer, totalLen, _buffer, 0, remaining);
        _writePos = remaining;

        return true;
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _buffer.Length) return;

        int newSize = _buffer.Length;
        while (newSize < required) newSize *= 2;
        if (newSize > MaxBufferSize)
            throw new InvalidOperationException($"PacketBuffer 超出上限 ({MaxBufferSize} bytes)");

        byte[] newBuf = new byte[newSize];
        Buffer.BlockCopy(_buffer, 0, newBuf, 0, _writePos);
        _buffer = newBuf;
    }
}
