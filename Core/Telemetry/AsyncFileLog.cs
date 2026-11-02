using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace KeyInsight.Core.Telemetry
{
    /// <summary>
    /// 异步文件日志（P0-05）：钩子回调只入队（永不等待磁盘），专职写线程批量落盘。
    /// - 容量有限：队列满时丢弃并计数（丢弃诊断行不影响输入与统计正确性）。
    /// - Dispose/Stop 限时冲刷，进程退出不悬挂。
    /// - 写失败静默计数，绝不把异常抛回调用线程。
    /// 替代 v2.1.x 的 StreamWriter.AutoFlush 同步写（设计文档风险 #3）与 MouseHook 的
    /// File.AppendAllText 首事件同步写。
    /// </summary>
    public sealed class AsyncFileLog : IDisposable
    {
        private const int DefaultCapacity = 4096;

        private readonly string _path;
        private readonly int _capacity;
        private readonly ConcurrentQueue<string> _queue = new ConcurrentQueue<string>();
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private Thread _writer;
        private volatile bool _stop;
        private long _dropped;
        private long _written;

        public AsyncFileLog(string path)
            : this(path, DefaultCapacity)
        {
        }

        public AsyncFileLog(string path, int capacity)
        {
            _path = path;
            _capacity = Math.Max(8, capacity);
        }

        public long DroppedCount { get { return Interlocked.Read(ref _dropped); } }
        public long WrittenCount { get { return Interlocked.Read(ref _written); } }
        public string FilePath { get { return _path; } }

        /// <summary>回调线程安全：只入队 + 信号量，绝不做磁盘 I/O。</summary>
        public void Write(string line)
        {
            if (_stop) return;
            int count = Interlocked.Increment(ref _pendingCount);
            if (count > _capacity)
            {
                Interlocked.Decrement(ref _pendingCount);
                Interlocked.Increment(ref _dropped);
                return;
            }
            _queue.Enqueue(line);
            try { _signal.Set(); } catch { }
        }

        private int _pendingCount;

        /// <summary>启动写线程（幂等）。失败不抛出——日志缺失不影响功能。</summary>
        public void Start()
        {
            if (_writer != null) return;
            Thread t = new Thread(WriterLoop);
            t.IsBackground = true;
            t.Name = "KeyInsightAsyncLog";
            _writer = t;
            t.Start();
        }

        private void WriterLoop()
        {
            while (!_stop)
            {
                try { _signal.WaitOne(1000); } catch { }
                FlushOnce();
            }
            FlushOnce();
        }

        /// <summary>把当前队列写入磁盘一次。写线程与 Dispose 调用。</summary>
        private void FlushOnce()
        {
            if (_queue.IsEmpty) return;
            try
            {
                using (StreamWriter w = new StreamWriter(_path, true))
                {
                    string line;
                    while (_queue.TryDequeue(out line))
                    {
                        Interlocked.Decrement(ref _pendingCount);
                        w.WriteLine(line);
                        Interlocked.Increment(ref _written);
                    }
                }
            }
            catch
            {
                // 磁盘满/只读等：丢弃本批，保住输入路径（TEL-007）
                string line;
                while (_queue.TryDequeue(out line)) Interlocked.Decrement(ref _pendingCount);
            }
        }

        /// <summary>限时冲刷并停止写线程。</summary>
        public void Dispose()
        {
            _stop = true;
            try { _signal.Set(); } catch { }
            Thread w = _writer;
            if (w != null)
            {
                try { w.Join(2000); } catch { }
                _writer = null;
            }
            // 线程超时未退时不再强等：残留项丢弃（有界队列语义）
        }
    }
}
