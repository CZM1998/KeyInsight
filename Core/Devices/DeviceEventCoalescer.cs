using System;
using System.Collections.Generic;

namespace KeyInsight.Core.Devices
{
    /// <summary>
    /// 设备事件合并与防抖（方案 §6.3）。按 InstanceKey 分组 —— 不同设备的事件绝不全局合并。
    /// 触发规则（对 now 纯函数，可测试）：
    ///   - 距最后一次 Feed ≥ QuietWindow（默认 350ms），且
    ///   - 距首次 Feed ≥ SettleDelay（默认 1200ms，等驱动枚举完成），或
    ///   - 距首次 Feed ≥ MaxCoalesce（3s，防持续事件永不刷新）。
    /// 失败重试退避 0.5/1/2/5s 由服务层按该序列调度。
    /// </summary>
    public sealed class DeviceEventCoalescer
    {
        public int QuietWindowMs = 350;
        public int SettleDelayMs = 1200;
        public int MaxCoalesceMs = 3000;

        public static readonly int[] RetryBackoffMs = new int[] { 500, 1000, 2000, 5000 };

        private sealed class Pending
        {
            public long FirstFeedTicks;
            public long LastFeedTicks;
            public int Count;
        }

        private readonly Dictionary<string, Pending> _pending =
            new Dictionary<string, Pending>(StringComparer.Ordinal);

        /// <summary>记录一次原始事件（同一键的重复通知在此合并）。</summary>
        public void Feed(string key, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(key)) return;
            long ticks = nowUtc.Ticks;
            Pending p;
            if (!_pending.TryGetValue(key, out p))
            {
                p = new Pending();
                p.FirstFeedTicks = ticks;
                _pending[key] = p;
            }
            p.LastFeedTicks = ticks;
            p.Count++;
        }

        /// <summary>取到当前时刻已满足触发条件的键（触发后从待定表移除）。</summary>
        public List<string> PollReady(DateTime nowUtc)
        {
            List<string> ready = new List<string>();
            long now = nowUtc.Ticks;
            foreach (KeyValuePair<string, Pending> kv in _pending)
            {
                Pending p = kv.Value;
                double sinceLast = (now - p.LastFeedTicks) / (double)TimeSpan.TicksPerMillisecond;
                double sinceFirst = (now - p.FirstFeedTicks) / (double)TimeSpan.TicksPerMillisecond;
                bool settled = sinceFirst >= SettleDelayMs;
                bool quiet = sinceLast >= QuietWindowMs;
                bool capped = sinceFirst >= MaxCoalesceMs;
                if ((quiet && settled) || capped) ready.Add(kv.Key);
            }
            for (int i = 0; i < ready.Count; i++)
            {
                _pending.Remove(ready[i]);
            }
            return ready;
        }

        /// <summary>待定键数量（诊断/测试用）。</summary>
        public int PendingCount
        {
            get { return _pending.Count; }
        }

        public int PendingEventCount(string key)
        {
            Pending p;
            return _pending.TryGetValue(key, out p) ? p.Count : 0;
        }

        public void Clear()
        {
            _pending.Clear();
        }
    }
}
