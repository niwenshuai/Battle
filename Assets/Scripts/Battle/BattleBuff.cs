namespace FrameSync
{
    public enum BuffType : byte
    {
        None = 0,
        Slow = 1,         // 减速
        Stun = 2,         // 眩晕
        AtkUp = 3,        // 增加攻击力
        DefUp = 4,        // 减少受到的伤害
        AtkSpeedUp = 5,   // 增加攻击速度（减少普攻CD/前摇/后摇）
        AtkSpeedDown = 6, // 降低攻击速度（增加普攻CD/前摇/后摇）
        AtkDown = 7,      // 降低攻击力
    }

    /// <summary>JSON配置用 — 技能可附加的buff列表。</summary>
    [System.Serializable]
    public class BuffConfig
    {
        public string Type;      // "Slow", "Stun", "AtkUp", "DefUp", "AtkSpeedUp", "AtkSpeedDown", "AtkDown"
        public int    Duration;  // 持续帧数
        public float  Value;     // 效果值（百分比：0.3=30%增幅，0.5=50%减速等）
        public bool   IsDebuff;  // true=减益buff, false=增益buff
    }

    /// <summary>运行时buff模板（从BuffConfig解析而来）。</summary>
    public struct BuffTemplate
    {
        public BuffType Type;
        public int      Duration;
        public FixedInt Value;
        public bool     IsDebuff;

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
                    IsDebuff = configs[i].IsDebuff,
                };
            }
            return result;
        }

        static BuffType ParseType(string s)
        {
            switch (s)
            {
                case "Slow":         return BuffType.Slow;
                case "Stun":         return BuffType.Stun;
                case "AtkUp":        return BuffType.AtkUp;
                case "DefUp":        return BuffType.DefUp;
                case "AtkSpeedUp":   return BuffType.AtkSpeedUp;
                case "AtkSpeedDown": return BuffType.AtkSpeedDown;
                case "AtkDown":      return BuffType.AtkDown;
                default:             return BuffType.None;
            }
        }
    }

    public struct Buff
    {
        public BuffType Type;
        public int FramesLeft;   // 剩余帧数
        public FixedInt Value;   // 效果值
        public byte SourceId;    // 施加者ID
        public bool IsDebuff;    // 增益/减益标记
    }
}
