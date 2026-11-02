using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace KeyInsight.Stats
{
    /// <summary>
    /// UI / 统计性能埋点（PR-0）：固定容量环形缓冲，记录 UI tick、快照构建、绘制、
    /// 统计锁等待/持有、请求合并与过期丢弃。仅存耗时数值，不含任何输入内容。
    /// 写路径无锁（Interlocked 索引），读路径复制后统计分位。
    /// </summary>
    public static class UiMetrics
    {
        private const int Capacity = 512;

        private sealed class Series
        {
            public readonly double[] Values = new double[Capacity];
            public int Count;
        }

        private static readonly Series _tick = new Series();
        private static readonly Series _snapshot = new Series();
        private static readonly Series _lockWait = new Series();
        private static readonly Series _lockHold = new Series();
        private static readonly Dictionary<string, Series> _paint = new Dictionary<string, Series>();
        private static readonly object _paintGate = new object();

        private static long _snapshotRequests;
        private static long _snapshotCoalesced;
        private static long _snapshotStale;
        private static long _paintCount;

        public static void RecordTick(double ms) { Add(_tick, ms); }
        public static void RecordSnapshot(double ms) { Add(_snapshot, ms); }
        public static void RecordLockWait(double ms) { Add(_lockWait, ms); }
        public static void RecordLockHold(double ms) { Add(_lockHold, ms); }

        public static void RecordPaint(string page, double ms)
        {
            InterlockedIncrement(ref _paintCount);
            Series s;
            lock (_paintGate)
            {
                if (!_paint.TryGetValue(page ?? "?", out s))
                {
                    s = new Series();
                    _paint[page ?? "?"] = s;
                }
            }
            Add(s, ms);
        }

        public static void CountSnapshotRequest() { InterlockedIncrement(ref _snapshotRequests); }
        public static void CountSnapshotCoalesced() { InterlockedIncrement(ref _snapshotCoalesced); }
        public static void CountSnapshotStale() { InterlockedIncrement(ref _snapshotStale); }

        private static void InterlockedIncrement(ref long field)
        {
            System.Threading.Interlocked.Increment(ref field);
        }

        private static void Add(Series s, double ms)
        {
            int idx;
            lock (s)
            {
                idx = s.Count % Capacity;
                s.Values[idx] = ms;
                if (s.Count < int.MaxValue) s.Count++;
            }
        }

        public sealed class Summary
        {
            public int Count;
            public double P50;
            public double P95;
            public double P99;
            public double Max;
            public double Avg;
        }

        private static Summary Summarize(Series s)
        {
            Summary sum = new Summary();
            double[] copy;
            lock (s)
            {
                int n = Math.Min(s.Count, Capacity);
                if (n == 0) return sum;
                copy = new double[n];
                Array.Copy(s.Values, copy, n);
            }
            Array.Sort(copy);
            sum.Count = copy.Length;
            sum.P50 = copy[(int)(copy.Length * 0.50)];
            sum.P95 = copy[Math.Min(copy.Length - 1, (int)(copy.Length * 0.95))];
            sum.P99 = copy[Math.Min(copy.Length - 1, (int)(copy.Length * 0.99))];
            sum.Max = copy[copy.Length - 1];
            double total = 0;
            for (int i = 0; i < copy.Length; i++) total += copy[i];
            sum.Avg = total / copy.Length;
            return sum;
        }

        public static Summary TickSummary { get { return Summarize(_tick); } }
        public static Summary SnapshotSummary { get { return Summarize(_snapshot); } }
        public static Summary LockWaitSummary { get { return Summarize(_lockWait); } }
        public static Summary LockHoldSummary { get { return Summarize(_lockHold); } }
        public static long SnapshotRequests { get { return System.Threading.Interlocked.Read(ref _snapshotRequests); } }
        public static long SnapshotCoalesced { get { return System.Threading.Interlocked.Read(ref _snapshotCoalesced); } }
        public static long SnapshotStale { get { return System.Threading.Interlocked.Read(ref _snapshotStale); } }
        public static long PaintCount { get { return System.Threading.Interlocked.Read(ref _paintCount); } }

        public static List<KeyValuePair<string, Summary>> PaintSummaries()
        {
            List<KeyValuePair<string, Summary>> list = new List<KeyValuePair<string, Summary>>();
            lock (_paintGate)
            {
                foreach (KeyValuePair<string, Series> kv in _paint)
                {
                    list.Add(new KeyValuePair<string, Summary>(kv.Key, Summarize(kv.Value)));
                }
            }
            list.Sort(delegate(KeyValuePair<string, Summary> a, KeyValuePair<string, Summary> b)
            {
                return string.CompareOrdinal(a.Key, b.Key);
            });
            return list;
        }

        /// <summary>AdvancedPage 一行摘要（不逐页展开）。</summary>
        public static string ShortLine()
        {
            Summary tick = TickSummary;
            Summary snap = SnapshotSummary;
            Summary wait = LockWaitSummary;
            return "UI tick P95 " + tick.P95.ToString("F1") + "ms · 快照构建 P95 " + snap.P95.ToString("F1") +
                "ms · 锁等待 P95 " + wait.P95.ToString("F2") + "ms";
        }
    }
}
