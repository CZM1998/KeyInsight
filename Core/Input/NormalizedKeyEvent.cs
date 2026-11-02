using System;

namespace KeyInsight.Core.Input
{
    public enum InputAction
    {
        Down = 0,
        Up = 1,
        Wheel = 2
    }

    public enum DecisionKind
    {
        Pass = 0,
        Suppress = 1
    }

    /// <summary>
    /// 决策原因（设计文档 §2.3）。Normal=正常放行；RepeatedWhileHeld=按下期间的自动重复；
    /// CandidateBounce=该键已显式启用保守过滤且落在抖动窗口；SuppressedPairedRelease=已拦截
    /// Down 的配对 Up（一并拦截）；Disabled=过滤主开关关闭；InjectedBypass=注入旁路；
    /// UnknownState=历史状态缺失（保守放行）；PolicyErrorFailOpen=引擎异常兜底放行。
    /// </summary>
    public enum DecisionReason
    {
        Normal = 0,
        RepeatedWhileHeld = 1,
        CandidateBounce = 2,
        SuppressedPairedRelease = 3,
        Disabled = 4,
        InjectedBypass = 5,
        UnknownState = 6,
        PolicyErrorFailOpen = 7,
        Malformed = 8,
        AppWhitelist = 9
    }

    /// <summary>归一化后的键盘事件。MonoMs 为单调毫秒（仅用于进程内间隔计算，跨重启不可比较）。</summary>
    public struct NormalizedKeyEvent
    {
        public PhysicalKeyId Key;
        public InputAction Action;
        public double MonoMs;
        public long UtcTicks;
        public bool Injected;
        public int LegacyVk;   // StatsVk 口径（小键盘 Enter=0x1000D），仅供统计投影
        public string AppName; // 后台缓存的前台进程名，归因不确定时为 unknown；钩子线程只读缓存
    }

    /// <summary>策略决策。GapDownMs/GapUpMs 供拦截日志与 nearmiss 使用，无历史时为 double.MaxValue。</summary>
    public struct InputDecision
    {
        public DecisionKind Kind;
        public DecisionReason Reason;
        public int PolicyVersion;
        public double GapDownMs;
        public double GapUpMs;

        public static InputDecision Pass(DecisionReason reason, int version)
        {
            InputDecision d = new InputDecision();
            d.Kind = DecisionKind.Pass;
            d.Reason = reason;
            d.PolicyVersion = version;
            d.GapDownMs = double.MaxValue;
            d.GapUpMs = double.MaxValue;
            return d;
        }

        public static InputDecision Suppress(DecisionReason reason, int version, double gapDownMs, double gapUpMs)
        {
            InputDecision d = new InputDecision();
            d.Kind = DecisionKind.Suppress;
            d.Reason = reason;
            d.PolicyVersion = version;
            d.GapDownMs = gapDownMs;
            d.GapUpMs = gapUpMs;
            return d;
        }
    }
}
