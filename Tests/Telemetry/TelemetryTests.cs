using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KeyInsight.Core.Telemetry;
using KeyInsight.Stats;

namespace KeyInsight.Tests.Telemetry
{
    /// <summary>P0-05 遥测测试（TEL-001/002/006/007 语义）。</summary>
    internal static class TelemetryTests
    {
        public static void Register()
        {
            TestRegistry.Add("Core", "TEL001_BoundedQueueOverload", TEL001_BoundedQueueOverload);
            TestRegistry.Add("Core", "TEL002_TwoProducersConsistency", TEL002_TwoProducersConsistency);
            TestRegistry.Add("Core", "TEL001_SinkFlushDrains", TEL001_SinkFlushDrains);
            TestRegistry.Add("Core", "TEL006_SinkSurvivesEngine", TEL006_SinkSurvivesEngine);
            TestRegistry.Add("Core", "TEL007_AsyncLogReadOnlyDisk", TEL007_AsyncLogReadOnlyDisk);
            TestRegistry.Add("Core", "AsyncLog_BoundedDrop", AsyncLog_BoundedDrop);
        }

        private static void TEL001_BoundedQueueOverload()
        {
            // 容量 8，灌 100 条：有界、丢 92、已发布 8，计数一致
            BoundedEventQueue<int> q = new BoundedEventQueue<int>(8);
            int published = 0;
            for (int i = 0; i < 100; i++)
            {
                if (q.TryPublish(i)) published++;
            }
            Check.Equal(8, q.Count, "bounded count");
            Check.Equal(8L, q.PublishedCount, "published count");
            Check.Equal(92L, q.DroppedCount, "dropped count");
            Check.True(published == 8, "TryPublish false when full");

            int drained = 0;
            int v;
            while (q.TryDequeue(out v)) drained++;
            Check.Equal(8, drained, "drained all");
            Check.Equal(0, q.Count, "empty after drain");
        }

        private static void TEL002_TwoProducersConsistency()
        {
            // 2 生产者 × 5000，容量 100：published + dropped == 10000，排空后计数归零（不死锁、计数一致）
            BoundedEventQueue<int> q = new BoundedEventQueue<int>(100);
            Thread a = new Thread(delegate()
            {
                for (int i = 0; i < 5000; i++) q.TryPublish(i);
            });
            Thread b = new Thread(delegate()
            {
                for (int i = 0; i < 5000; i++) q.TryPublish(i);
            });
            a.Start();
            b.Start();
            Check.True(a.Join(10000), "producer a finished (no deadlock)");
            Check.True(b.Join(10000), "producer b finished (no deadlock)");

            Check.Equal(10000L, q.PublishedCount + q.DroppedCount, "published+dropped == total");
            Check.True(q.Count <= 100, "count never exceeds capacity");
            int drained = 0;
            int v;
            while (q.TryDequeue(out v)) drained++;
            Check.Equal(q.PublishedCount, (long)drained, "drained == published (no lost slot)");
            Check.Equal(0, q.Count, "empty at end");
        }

        private static void TEL001_SinkFlushDrains()
        {
            // StatsEngineSink：快灌 500，Flush 排空，引擎总额一致（TEL-001 有界 + 排空语义）
            string dir = TestPaths.NewTempDir();
            try
            {
                StatsEngine engine = new StatsEngine(dir, "KeyInsight");
                StatsEngineSink sink = new StatsEngineSink(engine, 64);
                for (int i = 0; i < 500; i++)
                {
                    TelemetryEvent e = new TelemetryEvent();
                    e.Kind = TelemetryKind.Down;
                    e.LegacyVk = 0x45;
                    e.UtcTicks = new DateTime(2026, 1, 15, 9, 5, 0).AddSeconds(i).ToUniversalTime().Ticks;
                    e.App = "chrome";
                    sink.TryPublish(e);
                }
                Check.True(sink.DroppedCount > 0, "overload dropped beyond capacity 64");
                Check.True(sink.Flush(5000), "flush drains within timeout");
                StatsSnapshot s = engine.GetSnapshot(new DateTime(2026, 1, 1), new DateTime(2026, 1, 31));
                Check.Equal(sink.PublishedCount, s.TotalAll, "engine received exactly published count");
                sink.Dispose();
                engine.Dispose();
            }
            finally
            {
                TestPaths.Delete(dir);
            }
        }

        private static void TEL006_SinkSurvivesEngine()
        {
            // 引擎在 Apply 抛异常路径外仍可用：sink 不因引擎忙而阻塞（语义冒烟）
            string dir = TestPaths.NewTempDir();
            try
            {
                StatsEngine engine = new StatsEngine(dir, "KeyInsight");
                StatsEngineSink sink = new StatsEngineSink(engine, 16);
                for (int i = 0; i < 50; i++)
                {
                    TelemetryEvent e = new TelemetryEvent();
                    e.Kind = TelemetryKind.Down;
                    e.LegacyVk = 0x41;
                    e.UtcTicks = new DateTime(2026, 1, 15, 9, 5, 0).AddSeconds(i).ToUniversalTime().Ticks;
                    e.App = "chrome";
                    sink.TryPublish(e);
                }
                Check.True(sink.Flush(5000), "flush ok");
                sink.Dispose(); // 双重 Dispose 安全
                sink.Dispose();
                engine.Dispose();
            }
            finally
            {
                TestPaths.Delete(dir);
            }
        }

        private static void TEL007_AsyncLogReadOnlyDisk()
        {
            // 异步日志：写入→Dispose 限时冲刷→文件完整；指向非法路径时不抛出（磁盘故障降级）
            string dir = TestPaths.NewTempDir();
            try
            {
                string path = Path.Combine(dir, "test.log");
                AsyncFileLog log = new AsyncFileLog(path, 4096);
                log.Start();
                for (int i = 0; i < 100; i++) log.Write("line-" + i);
                log.Dispose();
                string[] lines = File.ReadAllLines(path);
                Check.Equal(100, lines.Length, "all lines flushed on dispose");
                Check.True(lines[0] == "line-0" && lines[99] == "line-99", "order preserved");

                AsyncFileLog bad = new AsyncFileLog(Path.Combine(dir, "no_such_dir", "x.log"));
                bad.Start();
                bad.Write("should not throw");
                bad.Dispose(); // 写失败静默，不抛
            }
            finally
            {
                TestPaths.Delete(dir);
            }
        }

        private static void AsyncLog_BoundedDrop()
        {
            // 未启动写线程时灌入超容量：丢弃计数（有界，不阻塞调用线程）
            string dir = TestPaths.NewTempDir();
            try
            {
                string path = Path.Combine(dir, "drop.log");
                AsyncFileLog log = new AsyncFileLog(path, 32);
                for (int i = 0; i < 100; i++) log.Write("x");
                Check.True(log.DroppedCount >= 68, "drop counted beyond capacity, got " + log.DroppedCount);
                log.Start();
                log.Dispose(); // 冲刷已入队的 ≤32 条
                Check.True(File.Exists(path), "flushed file exists");
            }
            finally
            {
                TestPaths.Delete(dir);
            }
        }
    }
}
