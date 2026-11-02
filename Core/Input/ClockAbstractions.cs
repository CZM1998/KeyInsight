using System;
using System.Diagnostics;

namespace KeyInsight.Core.Input
{
    /// <summary>
    /// 时钟抽象（设计文档 §2.3）：核心逻辑不直接调用 DateTime.Now / Stopwatch，
    /// 生产值用系统时钟适配器，测试可注入假时钟。
    /// </summary>
    public interface IMonotonicClock
    {
        /// <summary>单调毫秒（仅用于进程内间隔计算，跨重启不可比较）。</summary>
        double NowMs { get; }
    }

    public interface IWallClock
    {
        DateTime UtcNow { get; }
    }

    public sealed class SystemMonotonicClock : IMonotonicClock
    {
        public static readonly SystemMonotonicClock Instance = new SystemMonotonicClock();

        public double NowMs
        {
            get { return Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency; }
        }
    }

    public sealed class SystemWallClock : IWallClock
    {
        public static readonly SystemWallClock Instance = new SystemWallClock();

        public DateTime UtcNow
        {
            get { return DateTime.UtcNow; }
        }
    }
}
