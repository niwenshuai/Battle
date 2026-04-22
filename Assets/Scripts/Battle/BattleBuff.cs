namespace FrameSync
{
    public enum BuffType : byte
    {
        None = 0,
        Slow = 1, // 减速
        Stun = 2, // 眩晕
    }

    /// <summary>JSON配置用 — 技能可附加的buff列表。</summary>
    [System.Serializable]
    public class BuffConfig
    {
        public string Type;    // "Slow", "Stun"
        public int    Duration; // 持续帧数
        public float  Value;    // 效果值（Slow: 0.5 = 移速变为50%）
    }

    /// <summary>运行时buff模板（从BuffConfig解析而来）。</summary>
    public struct BuffTemplate
    {
        public BuffType Type;
        public int      Duration;
        public FixedInt Value;

        public static BuffTemplate[] FromConfigs(BuffConfig[] configs)
        {
            if (configs == null || configs.Length == 0) return null;
            var result = new BuffTemplate[configs.Length];
            for (int i = 0; i < configs.Length; i++)
            {
                result[i] = new BuffTemplate
                {
                    Type     = ParseType(configs[i].Type),
                    Duration = configs[i].Duration,
                    Value    = configs[i].Value > 0 ? FixedInt.FromFloat(configs[i].Value) : FixedInt.Zero,
                };
            }
            return result;
        }

        static BuffType ParseType(string s)
        {
            switch (s)
            {
                case "Slow": return BuffType.Slow;
                case "Stun": return BuffType.Stun;
                default:     return BuffType.None;
            }
        }
    }

    public struct Buff
    {
        public BuffType Type;
        public int FramesLeft;   // 剩余帧数
        public FixedInt Value;   // 效果值（Slow: 0.5 = 移速变为50%）
        public byte SourceId;    // 施加者ID
    }
}
