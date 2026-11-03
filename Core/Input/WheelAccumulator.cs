using System;
using System.Threading;

namespace KeyInsight.Core.Input
{
    /// <summary>
    /// 高分辨率滚轮累计器（复审方案 PR-1 §2.4）：
    /// 逐事件 Math.Abs(d)/120 会把不足 120 的小步滚动抬成整 1 格；
    /// 改为按方向累计原始 delta，满 120 才换算出刻度，余数保留在累计器中。
    /// 线程安全（钩子回调线程串行，Interlocked 用于与旧口径一致的防御式写法）。
    /// </summary>
    public sealed class WheelAccumulator
    {
        private const int TicksPerDetent = 120;
        private long _acc;

        /// <summary>当前余数（未满一格的原始 delta 残留）。</summary>
        public long Residual
        {
            get { return Interlocked.Read(ref _acc); }
        }

        /// <summary>
        /// 喂入一个原始 delta；返回带方向的刻度数（正 = 上/右，负 = 下/左），
        /// 0 表示累计尚未满 120。余数始终保留。
        /// </summary>
        public int Feed(int delta)
        {
            long total = Interlocked.Add(ref _acc, delta);
            int ticks = (int)(Math.Abs(total) / TicksPerDetent);
            if (ticks <= 0) return 0;
            int sign = total >= 0 ? 1 : -1;
            Interlocked.Add(ref _acc, -sign * (long)ticks * TicksPerDetent);
            return sign * ticks;
        }
    }
}
