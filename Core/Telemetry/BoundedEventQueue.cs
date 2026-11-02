using System;
using System.Collections.Concurrent;
using System.Threading;

namespace KeyInsight.Core.Telemetry
{
    /// <summary>
    /// 有界事件队列（P0-05，设计文档 §5.2）：
    /// - TryPublish 先 CAS 预占容量再入队，成功预占者保证能入队（两生产者并发下计数一致）；
    /// - 队列满时立即返回 false 并累计 Dropped，调用方（钩子回调）绝不等待；
    /// - TryDequeue 释放容量计数；
    /// - 详细事件被丢弃时统计侧必须体现「数据可能不完整」（DroppedCount 供 UI/侧车标注）。
    /// </summary>
    public sealed class BoundedEventQueue<T>
    {
        private readonly ConcurrentQueue<T> _queue = new ConcurrentQueue<T>();
        private readonly int _capacity;
        private int _count;
        private long _dropped;
        private long _published;

        public BoundedEventQueue(int capacity)
        {
            _capacity = Math.Max(1, capacity);
        }

        public int Capacity { get { return _capacity; } }
        public int Count { get { return Volatile.Read(ref _count); } }
        public long DroppedCount { get { return Interlocked.Read(ref _dropped); } }
        public long PublishedCount { get { return Interlocked.Read(ref _published); } }

        public bool TryPublish(T item)
        {
            int now = Interlocked.Increment(ref _count);
            if (now > _capacity)
            {
                Interlocked.Decrement(ref _count);
                Interlocked.Increment(ref _dropped);
                return false;
            }
            _queue.Enqueue(item);
            Interlocked.Increment(ref _published);
            return true;
        }

        public bool TryDequeue(out T item)
        {
            if (_queue.TryDequeue(out item))
            {
                Interlocked.Decrement(ref _count);
                return true;
            }
            return false;
        }

        public bool IsEmpty
        {
            get { return _queue.IsEmpty; }
        }
    }
}
