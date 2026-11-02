using System;
using KeyInsight.Core.Input;

namespace KeyInsight.Core.Telemetry
{
    /// <summary>遥测事件种类（设计文档 §2.2 TelemetryEvent）。</summary>
    public enum TelemetryKind
    {
        Down = 0,
        Swallow = 1,
        MouseDown = 2,
        MouseWheel = 3,
        Shortcut = 4,
        WheelRaw = 5
    }

    /// <summary>
    /// 钩子线程与统计层之间的传输模型：只携带统计所需字段，不含任何按键内容。
    /// 映射到旧统计引擎（KDS1 口径）由 StatsEngineSink 负责，KDS1 格式不变。
    /// </summary>
    public struct TelemetryEvent
    {
        public TelemetryKind Kind;
        public PhysicalKeyId Key;
        public int LegacyVk;      // StatsVk 口径（小键盘 Enter=0x1000D）
        public int Button;        // 鼠标键 id（MouseIds）
        public int Delta;         // 滚轮格数 / WheelRaw 的原始 delta
        public long UtcTicks;
        public string App;        // 前台进程名或快捷键组合名（Shortcut）
        public double GapDownMs;
        public double GapShortUpMs;
    }
}
