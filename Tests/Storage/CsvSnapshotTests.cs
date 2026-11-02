using System;
using System.Globalization;
using System.IO;
using System.Text;
using KeyInsight.Stats;

namespace KeyInsight.Tests.Storage
{
    /// <summary>
    /// P0-01 冻结基线：StatsEngine 对固定日期合成事件产生的四份 CSV 导出逐字节冻结；
    /// StatsSnapshot 旧字段（TotalAll/SwallowedAll/Range/KeyHealth 旧健康分公式）冻结。
    /// 旧健康分是“基于拦截”口径（LEG 兼容），本测试只防止无意识漂移，不代表硬件诊断。
    /// 重新基线化：设置环境变量 KI_UPDATE_GOLDEN=1 运行本套件，把打印结果回填到下方常量。
    /// </summary>
    internal static class CsvSnapshotTests
    {
        public static void Register()
        {
            TestRegistry.Add("Storage", "CsvSnapshot_Frozen", CsvSnapshot_Frozen);
            TestRegistry.Add("Storage", "Snapshot_FieldsFrozen", Snapshot_FieldsFrozen);
            TestRegistry.Add("Storage", "Stats_SelfAppExcluded", Stats_SelfAppExcluded);
            if (Environment.GetEnvironmentVariable("KI_UPDATE_GOLDEN") == "1")
            {
                TestRegistry.Add("Storage", "CsvSnapshot_RecordGolden", CsvSnapshot_RecordGolden);
            }
        }

        // —— 黄金 CSV（由 KI_UPDATE_GOLDEN=1 生成后冻结；日期固定，不依赖当天日期）——

        private const string GoldenDaysCsv =
            "date,total,swallowed,active_minutes,h0,h1,h2,h3,h4,h5,h6,h7,h8,h9,h10,h11,h12,h13,h14,h15,h16,h17,h18,h19,h20,h21,h22,h23\n" +
            "2026-01-15,4,1,4,0,0,0,0,0,0,0,0,0,2,1,0,0,0,1,0,0,0,0,0,0,0,0,0\n" +
            "2026-01-16,1,0,1,0,0,0,0,0,0,0,0,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0\n" +
            "2026-02-03,1,0,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,1,0\n";

        private const string GoldenAppsCsv =
            "date,app,total,swallowed,h0,h1,h2,h3,h4,h5,h6,h7,h8,h9,h10,h11,h12,h13,h14,h15,h16,h17,h18,h19,h20,h21,h22,h23\n" +
            "2026-01-15,chrome,3,1,0,0,0,0,0,0,0,0,0,2,1,0,0,0,0,0,0,0,0,0,0,0,0,0\n" +
            "2026-01-15,code,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,1,0,0,0,0,0,0,0,0,0\n" +
            "2026-01-16,notepad,1,0,0,0,0,0,0,0,0,0,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0\n" +
            "2026-02-03,chrome,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,1,0\n";

        private const string GoldenKeysCsv =
            "date,app,vk,vk_hex,count,swallowed\n" +
            "2026-01-15,,65,0x41,1,0\n" +
            "2026-01-15,,69,0x45,2,1\n" +
            "2026-01-15,,65549,0x1000D,1,0\n" +
            "2026-01-15,chrome,69,0x45,2,1\n" +
            "2026-01-15,chrome,65549,0x1000D,1,0\n" +
            "2026-01-15,code,65,0x41,1,0\n" +
            "2026-01-16,,20,0x14,1,0\n" +
            "2026-01-16,notepad,20,0x14,1,0\n" +
            "2026-02-03,,9,0x09,1,0\n" +
            "2026-02-03,chrome,9,0x09,1,0\n";

        private const string GoldenMouseCsv =
            "date,app,button,count\n" +
            "2026-01-15,,1,2\n" +
            "2026-01-15,,6,2\n" +
            "2026-01-15,chrome,1,2\n" +
            "2026-01-15,chrome,6,2\n" +
            "2026-02-03,,2,1\n" +
            "2026-02-03,chrome,2,1\n";

        private static DateTime At(string iso)
        {
            return DateTime.Parse(iso, CultureInfo.InvariantCulture);
        }

        private static KeyEvent Down(string iso, string app, int vk)
        {
            KeyEvent e = new KeyEvent();
            e.Type = KeyEventType.Down;
            e.Vk = vk;
            e.At = At(iso);
            e.App = app;
            return e;
        }

        private static KeyEvent SwallowEvt(string iso, string app, int vk, double gapDown, double gapUp)
        {
            KeyEvent e = new KeyEvent();
            e.Type = KeyEventType.Swallow;
            e.Vk = vk;
            e.At = At(iso);
            e.App = app;
            e.GapDown = gapDown;
            e.GapShortUp = gapUp;
            return e;
        }

        private static KeyEvent MouseEvt(string iso, string app, int button)
        {
            KeyEvent e = new KeyEvent();
            e.Type = KeyEventType.MouseDown;
            e.Button = button;
            e.At = At(iso);
            e.App = app;
            return e;
        }

        private static KeyEvent WheelEvt(string iso, string app, int button, int delta)
        {
            KeyEvent e = new KeyEvent();
            e.Type = KeyEventType.MouseWheel;
            e.Button = button;
            e.Delta = delta;
            e.At = At(iso);
            e.App = app;
            return e;
        }

        /// <summary>固定日期事件集（含跨月、小键盘 Enter 65549、自身进程排除样例）。</summary>
        private static void EnqueueFixed(StatsEngine engine)
        {
            engine.Enqueue(Down("2026-01-15T09:05:00", "chrome", 0x45));
            engine.Enqueue(Down("2026-01-15T09:06:30", "chrome", 0x45));
            engine.Enqueue(SwallowEvt("2026-01-15T09:06:31", "chrome", 0x45, 3.2, 1.4));
            engine.Enqueue(Down("2026-01-15T10:00:00", "chrome", 65549));
            engine.Enqueue(MouseEvt("2026-01-15T11:30:00", "chrome", MouseIds.Left));
            engine.Enqueue(MouseEvt("2026-01-15T11:30:05", "chrome", MouseIds.Left));
            engine.Enqueue(WheelEvt("2026-01-15T12:00:00", "chrome", MouseIds.WheelUp, 2));
            engine.Enqueue(Down("2026-01-15T14:00:00", "code", 0x41));
            engine.Enqueue(Down("2026-01-15T14:00:01", "keyinsight", 0x45)); // 自身进程：应被排除
            engine.Enqueue(Down("2026-01-16T08:00:00", "notepad", 0x14));
            engine.Enqueue(Down("2026-02-03T22:10:00", "chrome", 0x09));
            engine.Enqueue(MouseEvt("2026-02-03T22:10:05", "chrome", MouseIds.Right));
        }

        private static StatsEngine BuildEngine(string dir)
        {
            StatsEngine engine = new StatsEngine(dir, "KeyInsight");
            EnqueueFixed(engine);
            engine.WaitIdle(30000);
            return engine;
        }

        private static string[] ExportAll(StatsEngine engine, string dir)
        {
            string baseP = Path.Combine(dir, "export");
            engine.ExportCsv(baseP, null);
            return new string[]
            {
                File.ReadAllText(baseP + "-days.csv").Replace("\r\n", "\n"),
                File.ReadAllText(baseP + "-apps.csv").Replace("\r\n", "\n"),
                File.ReadAllText(baseP + "-keys.csv").Replace("\r\n", "\n"),
                File.ReadAllText(baseP + "-mouse.csv").Replace("\r\n", "\n")
            };
        }

        private static void CsvSnapshot_Frozen()
        {
            string dir = TestPaths.NewTempDir();
            try
            {
                StatsEngine engine = BuildEngine(dir);
                string[] actual = ExportAll(engine, dir);
                engine.Dispose();

                Check.Equal(GoldenDaysCsv, actual[0], "days.csv frozen");
                Check.Equal(GoldenAppsCsv, actual[1], "apps.csv frozen");
                Check.Equal(GoldenKeysCsv, actual[2], "keys.csv frozen");
                Check.Equal(GoldenMouseCsv, actual[3], "mouse.csv frozen");
            }
            finally
            {
                TestPaths.Delete(dir);
            }
        }

        private static void CsvSnapshot_RecordGolden()
        {
            string dir = TestPaths.NewTempDir();
            try
            {
                StatsEngine engine = BuildEngine(dir);
                string[] actual = ExportAll(engine, dir);
                engine.Dispose();

                string[] names = new string[] { "days", "apps", "keys", "mouse" };
                for (int i = 0; i < 4; i++)
                {
                    Console.WriteLine("=== " + names[i] + ".csv ===");
                    Console.WriteLine(actual[i]);
                }
                Check.True(actual[0].Length > 0, "recorded days.csv non-empty");
            }
            finally
            {
                TestPaths.Delete(dir);
            }
        }

        private static void Snapshot_FieldsFrozen()
        {
            string dir = TestPaths.NewTempDir();
            try
            {
                // 黄金月份文件（KDS1 夹具）经 StatsEngine 加载后的旧字段口径
                File.WriteAllText(Path.Combine(dir, "2026-01.kds"),
                    KdsCompatibilityTests.GoldenJanuary(), new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(dir, "2026-02.kds"),
                    KdsCompatibilityTests.GoldenFebruary(), new UTF8Encoding(false));

                StatsEngine engine = new StatsEngine(dir, "KeyInsight");
                engine.LoadAllMonths();
                engine.WaitIdle(5000);
                StatsSnapshot s = engine.GetSnapshot(new DateTime(2026, 1, 1), new DateTime(2026, 2, 28));

                Check.Equal(107, s.TotalAll, "TotalAll frozen (104+3)");
                Check.Equal(6, s.SwallowedAll, "SwallowedAll frozen");
                Check.Equal(2, s.Range.ActiveDays, "active days (01-15 + 02-03)");
                Check.Equal(80, s.Range.Keys[69].Count, "range E count");
                Check.Equal(3, s.Range.Keys[69].Swallowed, "range E swallowed");

                // 旧健康分公式（基于拦截，LEG 兼容口径）冻结：score = 100 - min(90, (int)(rate*100*4 + sw7*1.2))
                Check.Equal(3, s.KeyHealth.Count, "three keys with swallowed>0");
                Check.Equal(65, s.KeyHealth[0].Vk, "worst first");
                Check.Equal(20, s.KeyHealth[0].Score, "score vk65");
                Check.Equal(3, s.KeyHealth[0].Status, "status vk65");
                Check.Equal(65549, s.KeyHealth[1].Vk, "numpad enter second");
                Check.Equal(64, s.KeyHealth[1].Score, "score vk65549");
                Check.Equal(2, s.KeyHealth[1].Status, "status vk65549");
                Check.Equal(69, s.KeyHealth[2].Vk, "E third");
                Check.Equal(86, s.KeyHealth[2].Score, "score vk69");
                Check.Equal(2, s.KeyHealth[2].Status, "status vk69");

                engine.Dispose();
            }
            finally
            {
                TestPaths.Delete(dir);
            }
        }

        private static void Stats_SelfAppExcluded()
        {
            string dir = TestPaths.NewTempDir();
            try
            {
                StatsEngine engine = new StatsEngine(dir, "KeyInsight");
                engine.Enqueue(Down("2026-01-15T09:05:00", "keyinsight", 0x45));
                engine.Enqueue(Down("2026-01-15T09:05:01", "KEYINSIGHT.EXE", 0x45));
                engine.WaitIdle(5000);
                StatsSnapshot s = engine.GetSnapshot(new DateTime(2026, 1, 1), new DateTime(2026, 1, 31));
                Check.Equal(0, s.TotalAll, "self app events excluded (case-insensitive)");

                engine.Enqueue(Down("2026-01-15T09:06:00", "chrome", 0x45));
                engine.WaitIdle(5000);
                s = engine.GetSnapshot(new DateTime(2026, 1, 1), new DateTime(2026, 1, 31));
                Check.Equal(1, s.TotalAll, "non-self event counted");
                engine.Dispose();
            }
            finally
            {
                TestPaths.Delete(dir);
            }
        }
    }
}
