using System;
using System.Threading;
using KeyInsight.Stats;

namespace KeyInsight.Core.Telemetry
{
    /// <summary>
    /// 钩子线程向统计侧投递事件的唯一通道。
    /// 实现要求：TryPublish 绝不等待（回调内永不阻塞）、绝不向钩子线程抛异常。
    /// 传输模型为 TelemetryEvent；映射到旧 KDS1 统计口径由 StatsEngineSink 完成。
    /// </summary>
    public interface ITelemetrySink
    {
        bool TryPublish(TelemetryEvent ev);
    }

    /// <summary>
    /// 有界遥测适配（P0-05）：钩子入队固定容量队列（默认 8192）即返回；
    /// 专职泵线程搬运到 StatsEngine。满载丢弃并计数（DroppedCount 供“统计可能不完整”标注），
    /// 绝不阻塞输入路径（TEL-001/002）。Flush 供退出/测试限时排空。
    /// </summary>
    public sealed class StatsEngineSink : ITelemetrySink, IDisposable
    {
        public const int DefaultCapacity = 8192;

        private readonly StatsEngine _stats;
        private readonly BoundedEventQueue<TelemetryEvent> _queue;
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private Thread _pump;
        private volatile bool _stop;

        public StatsEngineSink(StatsEngine stats)
            : this(stats, DefaultCapacity)
        {
        }

        public StatsEngineSink(StatsEngine stats, int capacity)
        {
            _stats = stats;
            _queue = new BoundedEventQueue<TelemetryEvent>(capacity);
            Thread t = new Thread(PumpLoop);
            t.IsBackground = true;
            t.Name = "KeyInsightTelemetryPump";
            _pump = t;
            t.Start();
        }

        public long DroppedCount { get { return _queue.DroppedCount; } }
        public long PublishedCount { get { return _queue.PublishedCount; } }
        public int PendingCount { get { return _queue.Count; } }

        public bool TryPublish(TelemetryEvent ev)
        {
            if (_stop || _stats == null) return false;
            bool ok = _queue.TryPublish(ev);
            if (ok)
            {
                try { _signal.Set(); } catch { }
            }
            return ok;
        }

        private void PumpLoop()
        {
            while (!_stop)
            {
                try { _signal.WaitOne(200); } catch { }
                DrainAll();
            }
            DrainAll();
        }

        private void DrainAll()
        {
            TelemetryEvent ev;
            while (_queue.TryDequeue(out ev))
            {
                try { _stats.Enqueue(ToKeyEvent(ev)); }
                catch { }
            }
        }

        /// <summary>TelemetryEvent → 旧统计模型（KDS1 口径，字段含义冻结）。</summary>
        internal static KeyEvent ToKeyEvent(TelemetryEvent ev)
        {
            KeyEvent e = new KeyEvent();
            e.At = new DateTime(ev.UtcTicks, DateTimeKind.Utc).ToLocalTime();
            e.App = ev.App;
            e.Vk = ev.LegacyVk;
            e.Button = ev.Button;
            e.Delta = ev.Delta;
            e.GapDown = ev.GapDownMs;
            e.GapShortUp = ev.GapShortUpMs;
            switch (ev.Kind)
            {
                case TelemetryKind.Down: e.Type = KeyEventType.Down; break;
                case TelemetryKind.Swallow: e.Type = KeyEventType.Swallow; break;
                case TelemetryKind.MouseDown: e.Type = KeyEventType.MouseDown; break;
                case TelemetryKind.MouseWheel: e.Type = KeyEventType.MouseWheel; break;
                case TelemetryKind.Shortcut: e.Type = KeyEventType.Shortcut; break;
                case TelemetryKind.WheelRaw: e.Type = KeyEventType.WheelRaw; break;
                default: e.Type = KeyEventType.Down; break;
            }
            return e;
        }

        /// <summary>限时排空当前积压（退出/测试用）。运行中的生产者可能继续投递，尽力而为。</summary>
        public bool Flush(int timeoutMs)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (_queue.Count > 0 && DateTime.UtcNow < end)
            {
                DrainAll();
                try { _signal.Set(); } catch { }
                Thread.Sleep(10);
            }
            return _queue.Count == 0;
        }

        public void Dispose()
        {
            _stop = true;
            try { _signal.Set(); } catch { }
            Thread p = _pump;
            if (p != null)
            {
                try { p.Join(1500); } catch { }
                _pump = null;
            }
        }
    }

    /// <summary>测试/无统计模式用的空汇：只吞事件。</summary>
    public sealed class NullTelemetrySink : ITelemetrySink
    {
        public bool TryPublish(TelemetryEvent ev)
        {
            return true;
        }
    }
}
