using System;
using System.Threading;

namespace KeyInsight.Stats
{
    /// <summary>
    /// 数据版本管理（综合路线图 PR-1）：不同数据域的变更各自递增版本，
    /// 快照缓存键组合相关域版本，避免无关变更导致全量缓存失效。
    /// 线程安全（Interlocked）；清空/恢复/迁移等整体变更递增 Epoch。
    /// </summary>
    public sealed class DataVersionManager
    {
        private long _event;
        private long _history;
        private long _diagnostic;
        private long _device;
        private long _epoch;

        /// <summary>键盘/鼠标事件 Apply 递增。</summary>
        public long EventVersion { get { return Interlocked.Read(ref _event); } }
        /// <summary>历史月份加载完成/删除/清空递增。</summary>
        public long HistoryVersion { get { return Interlocked.Read(ref _history); } }
        /// <summary>诊断直方图/维护记录变化递增。</summary>
        public long DiagnosticVersion { get { return Interlocked.Read(ref _diagnostic); } }
        /// <summary>设备信息变化递增。</summary>
        public long DeviceVersion { get { return Interlocked.Read(ref _device); } }
        /// <summary>导入/恢复/迁移等整体数据变更递增。</summary>
        public long Epoch { get { return Interlocked.Read(ref _epoch); } }

        public long BumpEvent() { return Interlocked.Increment(ref _event); }
        public long BumpHistory() { return Interlocked.Increment(ref _history); }
        public long BumpDiagnostic() { return Interlocked.Increment(ref _diagnostic); }
        public long BumpDevice() { return Interlocked.Increment(ref _device); }
        public long BumpEpoch()
        {
            Interlocked.Increment(ref _history);
            Interlocked.Increment(ref _epoch);
            return Interlocked.Read(ref _epoch);
        }

        /// <summary>快照缓存键的组合版本串（历史/事件相关）。</summary>
        public string SnapshotVersionKey()
        {
            return EventVersion + "|" + HistoryVersion + "|" + Epoch;
        }

        public string DiagnosticVersionKey()
        {
            return DiagnosticVersion + "|" + Epoch;
        }

        public string DeviceVersionKey()
        {
            return DeviceVersion + "|" + Epoch;
        }
    }
}
