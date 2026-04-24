using System;
using System.Collections.Generic;
using System.IO;

// ═══════════════════════════════════════════════════════════════
// 帧同步协议层 — 客户端和服务器共用
//
// 消息格式（嵌套在 TCP [4字节长度][payload] 之内）：
//   payload = [1 字节 MsgType][消息体 bytes]
//
// 所有多字节数值使用大端序。
// ═══════════════════════════════════════════════════════════════

namespace FrameSync
{
    // ── 消息类型 ─────────────────────────────────────────────

    public enum MsgType : byte
    {
        // 客户端 → 服务器
        JoinRoom        = 0x01, // 加入房间
        LeaveRoom       = 0x02, // 离开房间
        PlayerReady     = 0x03, // 玩家准备
        PlayerInput     = 0x10, // 上报输入

        // 服务器 → 客户端
        JoinRoomAck     = 0x81, // 加入房间确认
        RoomSnapshot    = 0x82, // 房间快照（玩家列表）
        GameStart       = 0x83, // 游戏开始
        GameEnd         = 0x84, // 游戏结束
        FrameData       = 0x90, // 权威帧数据（所有玩家输入）
    }

    // ── 玩家输入 ─────────────────────────────────────────────

    /// <summary>
    /// 单个玩家的一帧输入。
    /// 使用整型代替浮点以保证确定性，方向用 [-1000, 1000] 表示。
    /// </summary>
    public struct PlayerInput
    {
        public byte  PlayerId;
        public int   MoveX;      // [-1000, 1000]
        public int   MoveY;      // [-1000, 1000]
        public uint  Buttons;    // 位掩码：bit0=Fire, bit1=Jump, bit2=Skill...

        public const uint ButtonFire  = 1 << 0;
        public const uint ButtonJump  = 1 << 1;
        public const uint ButtonSkill = 1 << 2;

        public void Serialize(BinaryWriter w)
        {
            w.Write(PlayerId);
            Proto.WriteInt32BE(w, MoveX);
            Proto.WriteInt32BE(w, MoveY);
            Proto.WriteUInt32BE(w, Buttons);
        }

        public static PlayerInput Deserialize(BinaryReader r)
        {
            return new PlayerInput
            {
                PlayerId = r.ReadByte(),
                MoveX    = Proto.ReadInt32BE(r),
                MoveY    = Proto.ReadInt32BE(r),
                Buttons  = Proto.ReadUInt32BE(r),
            };
        }

        public const int SerializedSize = 1 + 4 + 4 + 4; // 13 bytes
    }

    // ── 帧数据（服务器下发的权威帧） ────────────────────────

    /// <summary>
    /// 一个网络同步帧包含多个逻辑帧的输入。
    /// 每个逻辑帧含所有玩家的输入，数量由 LogicFrameCount 指定（默认2，即30Hz逻辑/15Hz网络）。
    /// </summary>
    public struct FrameData
    {
        public int             FrameId;         // 网络帧号
        public int             LogicFrameCount; // 包含的逻辑帧数（通常为2）
        public PlayerInput[][] LogicFrameInputs; // [logicFrameIndex][playerIndex]

        public void Serialize(BinaryWriter w)
        {
            Proto.WriteInt32BE(w, FrameId);
            w.Write((byte)LogicFrameCount);
            for (int i = 0; i < LogicFrameCount; i++)
            {
                var inputs = LogicFrameInputs[i];
                w.Write((byte)(inputs?.Length ?? 0));
                if (inputs != null)
                    foreach (var inp in inputs)
                        inp.Serialize(w);
            }
        }

        public static FrameData Deserialize(BinaryReader r)
        {
            var fd = new FrameData
            {
                FrameId = Proto.ReadInt32BE(r),
                LogicFrameCount = r.ReadByte(),
            };
            fd.LogicFrameInputs = new PlayerInput[fd.LogicFrameCount][];
            for (int i = 0; i < fd.LogicFrameCount; i++)
            {
                int count = r.ReadByte();
                fd.LogicFrameInputs[i] = new PlayerInput[count];
                for (int j = 0; j < count; j++)
                    fd.LogicFrameInputs[i][j] = PlayerInput.Deserialize(r);
            }
            return fd;
        }
    }

    // ── 消息打包 / 解包 ─────────────────────────────────────

    public static class Proto
    {
        // ── 客户端 → 服务器 ──────────────────────────────────

        public static byte[] PackJoinRoom(string playerName)
        {
            using var ms = new MemoryStream();
            using var w  = new BinaryWriter(ms);
            w.Write((byte)MsgType.JoinRoom);
            var nameBytes = System.Text.Encoding.UTF8.GetBytes(playerName ?? "");
            w.Write((byte)nameBytes.Length);
            w.Write(nameBytes);
            return ms.ToArray();
        }

        public static byte[] PackLeaveRoom()
        {
            return new[] { (byte)MsgType.LeaveRoom };
        }

        public static byte[] PackPlayerReady()
        {
            return new[] { (byte)MsgType.PlayerReady };
        }

        public static byte[] PackPlayerInput(int frameId, PlayerInput input)
        {
            using var ms = new MemoryStream();
            using var w  = new BinaryWriter(ms);
            w.Write((byte)MsgType.PlayerInput);
            WriteInt32BE(w, frameId);
            input.Serialize(w);
            return ms.ToArray();
        }

        // ── 服务器 → 客户端 ──────────────────────────────────

        public static byte[] PackJoinRoomAck(byte playerId, byte tickRate, byte logicFPS)
        {
            return new[] { (byte)MsgType.JoinRoomAck, playerId, tickRate, logicFPS };
        }

        public static byte[] PackRoomSnapshot(List<(byte id, string name, bool ready)> players)
        {
            using var ms = new MemoryStream();
            using var w  = new BinaryWriter(ms);
            w.Write((byte)MsgType.RoomSnapshot);
            w.Write((byte)players.Count);
            foreach (var (id, name, ready) in players)
            {
                w.Write(id);
                var nb = System.Text.Encoding.UTF8.GetBytes(name ?? "");
                w.Write((byte)nb.Length);
                w.Write(nb);
                w.Write(ready);
            }
            return ms.ToArray();
        }

        public static byte[] PackGameStart(int randomSeed)
        {
            using var ms = new MemoryStream();
            using var w  = new BinaryWriter(ms);
            w.Write((byte)MsgType.GameStart);
            WriteInt32BE(w, randomSeed);
            return ms.ToArray();
        }

        public static byte[] PackGameEnd(byte winnerId)
        {
            return new[] { (byte)MsgType.GameEnd, winnerId };
        }

        public static byte[] PackFrameData(FrameData frame)
        {
            using var ms = new MemoryStream();
            using var w  = new BinaryWriter(ms);
            w.Write((byte)MsgType.FrameData);
            frame.Serialize(w);
            return ms.ToArray();
        }

        // ── 通用解析入口 ────────────────────────────────────

        public static MsgType PeekType(byte[] data)
        {
            if (data == null || data.Length == 0)
                throw new ArgumentException("空消息");
            return (MsgType)data[0];
        }

        public static BinaryReader BodyReader(byte[] data)
        {
            var ms = new MemoryStream(data, 1, data.Length - 1, false);
            return new BinaryReader(ms);
        }

        // ── 大端读写辅助 ────────────────────────────────────

        public static void WriteInt32BE(BinaryWriter w, int v)
        {
            w.Write((byte)(v >> 24));
            w.Write((byte)(v >> 16));
            w.Write((byte)(v >> 8));
            w.Write((byte)(v));
        }

        public static void WriteUInt32BE(BinaryWriter w, uint v)
        {
            w.Write((byte)(v >> 24));
            w.Write((byte)(v >> 16));
            w.Write((byte)(v >> 8));
            w.Write((byte)(v));
        }

        public static int ReadInt32BE(BinaryReader r)
        {
            return (r.ReadByte() << 24) | (r.ReadByte() << 16) |
                   (r.ReadByte() << 8)  |  r.ReadByte();
        }

        public static uint ReadUInt32BE(BinaryReader r)
        {
            return ((uint)r.ReadByte() << 24) | ((uint)r.ReadByte() << 16) |
                   ((uint)r.ReadByte() << 8)  |  r.ReadByte();
        }
    }
}
