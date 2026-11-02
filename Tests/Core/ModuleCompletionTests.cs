using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using KeyInsight.Core.Input;
using KeyInsight.Core.Policy;
using KeyInsight.Hook;
using KeyInsight.Stats;
using KeyInsight.Tests.Storage;

namespace KeyInsight.Tests.Core
{
    /// <summary>
    /// 建议模块与遗留门禁缺口测试：侧车清理（STO-015）、.bak 恢复（STO-007）、
    /// 策略回退（DIA-008）、配置 v2、设备画像解析、版本切换边界、Dispose 并发、跨年/乱序。
    /// </summary>
    internal static class ModuleCompletionTests
    {
        public static void Register()
        {
            TestRegistry.Add("Storage", "STO015_SidecarCleanupOnDelete", STO015_SidecarCleanupOnDelete);
            TestRegistry.Add("Storage", "STO007_BakRecoveryOnLoad", STO007_BakRecoveryOnLoad);
            TestRegistry.Add("Core", "DIA008_PolicyRevert", DIA008_PolicyRevert);
            TestRegistry.Add("Core", "CONFIGV2_RoundTripAndPrecedence", CONFIGV2_RoundTripAndPrecedence);
            TestRegistry.Add("Core", "DEVICE_ParseVidPid", DEVICE_ParseVidPid);
            TestRegistry.Add("Core", "POLICY_VersionSwitchBounded", POLICY_VersionSwitchBounded);
            TestRegistry.Add("Storage", "TEL005_DisposeConcurrentEnqueue", TEL005_DisposeConcurrentEnqueue);
            TestRegistry.Add("Storage", "STO013_YearBoundaryCsvOrder", STO013_YearBoundaryCsvOrder);
            TestRegistry.Add("Storage", "TEL004_LateEventsCrossMonth", TEL004_LateEventsCrossMonth);
            TestRegistry.Add("Storage", "PR1_AsyncSnapshotEqualsSync", PR1_AsyncSnapshotEqualsSync);
            TestRegistry.Add("Core", "METRICS_Percentiles", METRICS_Percentiles);
        }

        private static void STO015_SidecarCleanupOnDelete()
        {
            string dir = TestPaths.NewTempDir();
            try
            {
                DateTime today = DateTime.Today;
                StatsEngine engine = new StatsEngine(dir, "KeyInsight");
                KeyEvent down = new KeyEvent();
                down.Type = KeyEventType.Down;
                down.Vk = 0x45;
                down.At = today.AddHours(9);
                down.App = "chrome";
                KeyEvent sw = new KeyEvent();
                sw.Type = KeyEventType.Swallow;
                sw.Vk = 0x45;
                sw.At = today.AddHours(9).AddSeconds(1);
                sw.App = "chrome";
                sw.GapDown = 3.2;
                sw.GapShortUp = 1.4;
                engine.Enqueue(down);
                engine.Enqueue(sw);
                engine.WaitIdle(5000);
                engine.SaveAll();

                string monthKey = today.ToString("yyyy-MM");
                string usage = Path.Combine(dir, "v2", "usage-" + monthKey + ".jsonl");
                string kdiag = Path.Combine(dir, "diagnostics", "kdiag-" + monthKey + ".jsonl");
                Check.True(File.Exists(usage), "usage sidecar exists");
                Check.True(File.Exists(kdiag), "kdiag sidecar exists");

                engine.DeleteMonth(monthKey);
                engine.Dispose();

                Check.True(!File.Exists(usage), "STO-015: usage sidecar deleted with month");
                Check.True(!File.Exists(kdiag), "STO-015: diagnostics sidecar deleted with month");
            }
            finally
            {
                TestPaths.Delete(dir);
            }
        }

        private static void STO007_BakRecoveryOnLoad()
        {
            string dir = TestPaths.NewTempDir();
            try
            {
                string path = Path.Combine(dir, "2026-01.kds");
                File.WriteAllText(path + ".bak", KdsCompatibilityTests.GoldenJanuary(), new UTF8Encoding(false));
                Check.True(!File.Exists(path), "main file missing (simulated crash)");

                MonthData md = MonthFile.Load(path, 2026, 1);
                Check.Equal(104L, md.Total, "STO-007: recovered from .bak");
                Check.True(File.Exists(path), "recovered file materialized");

                // 主文件存在 + 残留 .tmp → 清理 tmp 不影响读取
                File.WriteAllText(path + ".tmp", "leftover");
                MonthData md2 = MonthFile.Load(path, 2026, 1);
                Check.Equal(104L, md2.Total, "main file still authoritative");
                Check.True(!File.Exists(path + ".tmp"), "stale tmp cleaned");
            }
            finally
            {
                TestPaths.Delete(dir);
            }
        }

        private static void DIA008_PolicyRevert()
        {
            string dir = TestPaths.NewTempDir();
            try
            {
                KeyPolicyStore store = new KeyPolicyStore(Path.Combine(dir, "kp.json"));
                KeyPolicyEntry e = store.GetOrCreate(0x45, 0x12, false);
                e.Enabled = true;
                e.ThresholdMs = 20;
                store.Save();

                // 应用新策略前拍快照，再修改
                store.BeginEdit();
                KeyPolicyEntry changed = store.GetOrCreate(0x45, 0x12, false);
                changed.ThresholdMs = 30;
                changed.Enabled = false;
                store.Save();
                Check.True(store.HasUndo, "undo snapshot exists");

                Check.True(store.Revert(), "revert succeeds");
                KeyPolicyEntry back = new KeyPolicyStore(Path.Combine(dir, "kp.json")).Find(0x45, 0x12, false);
                Check.True(back != null && back.Enabled, "DIA-008: enabled restored");
                Check.Equal(20, back.ThresholdMs, "DIA-008: threshold restored");
                Check.True(!File.Exists(Path.Combine(dir, "kp.json.undo")), "undo consumed");

                Check.True(!store.Revert(), "no undo when none pending");
            }
            finally
            {
                TestPaths.Delete(dir);
            }
        }

        private static void CONFIGV2_RoundTripAndPrecedence()
        {
            string dir = TestPaths.NewTempDir();
            try
            {
                string path = Path.Combine(dir, "KeyInsight.v2.json");
                Config c = new Config();
                c.ThresholdMs = 17;
                c.FilterEnabled = false;
                c.CaptureEnabled = false;
                c.DiagnosticsEnabled = false;
                c.FloatingSummary = true;
                c.FloatingSummaryX = 123;
                c.FloatingSummaryY = 45;
                c.PolicyMode = 2;
                c.FilterAppWhitelist = "game,ide";
                c.KeyboardLayout = 3;
                ConfigV2.SaveTo(path, c);

                Config back = new Config();
                Check.True(ConfigV2.TryRead(path, back), "v2 readable");
                Check.Equal(17, back.ThresholdMs, "threshold");
                Check.True(!back.FilterEnabled, "filter");
                Check.True(!back.CaptureEnabled, "capture");
                Check.True(!back.DiagnosticsEnabled, "diagnostics");
                Check.True(back.FloatingSummary, "floating");
                Check.Equal(123, back.FloatingSummaryX, "floating x");
                Check.Equal(45, back.FloatingSummaryY, "floating y");
                Check.Equal(2, back.PolicyMode, "policy mode");
                Check.True(back.FilterAppWhitelist == "game,ide", "whitelist");
                Check.Equal(3, back.KeyboardLayout, "layout");

                // 未来 schema：保守拒绝解析（调用方回退 ini）
                File.WriteAllText(path, "{\"schema\":99,\"thresholdMs\":5}");
                Config future = new Config();
                Check.True(!ConfigV2.TryRead(path, future), "future schema rejected");
                Check.Equal(25, future.ThresholdMs, "no partial population");
            }
            finally
            {
                TestPaths.Delete(dir);
            }
        }

        private static void DEVICE_ParseVidPid()
        {
            string vid, pid;
            RawInputTracker.ParseVidPid(
                @"\\?\HID#VID_046D&PID_C52B&MI_00#8&2f4b1c3&0&0000#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}",
                out vid, out pid);
            Check.True(vid == "VID_046D", "vid parsed: " + vid);
            Check.True(pid == "PID_C52B", "pid parsed: " + pid);

            RawInputTracker.ParseVidPid("no ids here", out vid, out pid);
            Check.True(vid == "" && pid == "", "missing ids → empty");

            DeviceProfile d = new DeviceProfile();
            d.Kind = "keyboard";
            d.VendorId = "VID_1234";
            d.ProductId = "PID_5678";
            Check.True(d.DisplayName.IndexOf("VID_1234:PID_5678", StringComparison.Ordinal) >= 0, "display name");
        }

        private static void POLICY_VersionSwitchBounded()
        {
            // 属性测试：多次策略版本切换后，状态机字典只随“不同按键数”增长（不随版本数增长）
            PolicySnapshot snap = new PolicySnapshot(1, PolicyMode.PerKeyConservative, true, true, 20,
                null, null);
            KeyInsightPolicyEngine engine = new KeyInsightPolicyEngine(snap);

            PhysicalKeyId key = new PhysicalKeyId(0x45, 0x12, false);
            for (int v = 0; v < 50; v++)
            {
                PolicySnapshot s = new PolicySnapshot(v + 2, PolicyMode.PerKeyConservative, true, true, 20, null, null);
                engine.UpdatePolicy(s);
                NormalizedKeyEvent ev = new NormalizedKeyEvent();
                ev.Key = key;
                ev.Action = InputAction.Down;
                ev.MonoMs = v * 100;
                ev.LegacyVk = 0x45;
                ev.AppName = "chrome";
                engine.Evaluate(ev);
                ev.Action = InputAction.Up;
                ev.MonoMs = v * 100 + 10;
                engine.Evaluate(ev);
            }
            Check.Equal(1, engine.MachineCount, "50 version switches, still one machine: " + engine.MachineCount);
        }

        private static void TEL005_DisposeConcurrentEnqueue()
        {
            string dir = TestPaths.NewTempDir();
            try
            {
                StatsEngine engine = new StatsEngine(dir, "KeyInsight");
                Thread producer = new Thread(delegate()
                {
                    for (int i = 0; i < 5000; i++)
                    {
                        KeyEvent e = new KeyEvent();
                        e.Type = KeyEventType.Down;
                        e.Vk = 0x45;
                        e.At = DateTime.Now;
                        e.App = "chrome";
                        engine.Enqueue(e);
                    }
                });
                producer.IsBackground = true;
                producer.Start();

                engine.Dispose(); // 与生产并发
                bool joined = producer.Join(5000);
                Check.True(joined, "TEL-005: producer finished (no deadlock)");
            }
            finally
            {
                TestPaths.Delete(dir);
            }
        }

        private static void STO013_YearBoundaryCsvOrder()
        {
            string dir = TestPaths.NewTempDir();
            try
            {
                StatsEngine engine = new StatsEngine(dir, "KeyInsight");
                KeyEvent dec = new KeyEvent();
                dec.Type = KeyEventType.Down;
                dec.Vk = 0x45;
                dec.At = new DateTime(2025, 12, 31, 23, 59, 0);
                dec.App = "chrome";
                KeyEvent jan = new KeyEvent();
                jan.Type = KeyEventType.Down;
                jan.Vk = 0x52;
                jan.At = new DateTime(2026, 1, 1, 0, 0, 30);
                jan.App = "chrome";
                engine.Enqueue(dec);
                engine.Enqueue(jan);
                engine.WaitIdle(5000);

                string basePath = Path.Combine(dir, "export");
                engine.ExportCsv(basePath, null);
                engine.Dispose();

                string[] lines = File.ReadAllLines(basePath + "-days.csv");
                Check.True(lines.Length >= 3, "has header + two days");
                Check.True(lines[1].StartsWith("2025-12-31", StringComparison.Ordinal), "year boundary day 1 order");
                Check.True(lines[2].StartsWith("2026-01-01", StringComparison.Ordinal), "year boundary day 2 order");
                Check.True(File.Exists(Path.Combine(dir, "2025-12.kds")), "december file written");
                Check.True(File.Exists(Path.Combine(dir, "2026-01.kds")), "january file written");
            }
            finally
            {
                TestPaths.Delete(dir);
            }
        }

        private static void PR1_AsyncSnapshotEqualsSync()
        {
            // PR-1：异步快照与同步 GetSnapshot 必须给出同一统计口径
            string dir = TestPaths.NewTempDir();
            try
            {
                StatsEngine engine = new StatsEngine(dir, "KeyInsight");
                for (int i = 0; i < 40; i++)
                {
                    KeyEvent e = new KeyEvent();
                    e.Type = KeyEventType.Down;
                    e.Vk = i % 3 == 0 ? 0x45 : 0x52;
                    e.At = new DateTime(2026, 1, 15, 9, 0, 0).AddMinutes(i);
                    e.App = i % 2 == 0 ? "chrome" : "code";
                    engine.Enqueue(e);
                }
                engine.WaitIdle(5000);

                StatsSnapshot async = null;
                long asyncGen = -1;
                using (System.Threading.ManualResetEvent done = new System.Threading.ManualResetEvent(false))
                {
                    engine.SnapshotBuilt += delegate(StatsSnapshot s, long gen)
                    {
                        async = s;
                        asyncGen = gen;
                        done.Set();
                    };
                    long requested = engine.RequestSnapshot(new DateTime(2026, 1, 1), new DateTime(2026, 1, 31));
                    Check.True(done.WaitOne(8000), "async snapshot callback fired");
                    Check.Equal(requested, asyncGen, "generation preserved");
                }

                StatsSnapshot sync = engine.GetSnapshot(new DateTime(2026, 1, 1), new DateTime(2026, 1, 31));
                Check.Equal(sync.TotalAll, async.TotalAll, "async total equals sync");
                Check.Equal(sync.SwallowedAll, async.SwallowedAll, "async swallowed equals sync");
                Check.Equal(sync.Range.Keys.Count, async.Range.Keys.Count, "async key set equals sync");
                Check.Equal(sync.Range.Apps.Count, async.Range.Apps.Count, "async app set equals sync");
                engine.Dispose();
            }
            finally
            {
                TestPaths.Delete(dir);
            }
        }

        private static void METRICS_Percentiles()
        {
            for (int i = 1; i <= 100; i++) UiMetrics.RecordPaint("MetricsProbe", i);
            bool found = false;
            foreach (KeyValuePair<string, UiMetrics.Summary> kv in UiMetrics.PaintSummaries())
            {
                if (kv.Key != "MetricsProbe") continue;
                found = true;
                Check.Equal(100, kv.Value.Count, "sample count");
                Check.True(kv.Value.P50 >= 49 && kv.Value.P50 <= 51, "P50 median: " + kv.Value.P50);
                Check.True(kv.Value.P95 >= 94 && kv.Value.P95 <= 96, "P95: " + kv.Value.P95);
                Check.True(kv.Value.P99 >= 98 && kv.Value.P99 <= 100, "P99: " + kv.Value.P99);
            }
            Check.True(found, "paint series present");
            UiMetrics.Summary tick = UiMetrics.TickSummary;
            Check.True(tick.Count >= 0, "tick summary accessible");
        }

        private static void TEL004_LateEventsCrossMonth()
        {
            // 迟到/乱序事件：按事件自身时间归月归日，不因投递顺序错位
            string dir = TestPaths.NewTempDir();
            try
            {
                StatsEngine engine = new StatsEngine(dir, "KeyInsight");
                KeyEvent later = new KeyEvent();
                later.Type = KeyEventType.Down;
                later.Vk = 0x45;
                later.At = new DateTime(2026, 2, 10, 10, 0, 0);
                later.App = "chrome";
                KeyEvent earlier = new KeyEvent();
                earlier.Type = KeyEventType.Down;
                earlier.Vk = 0x52;
                earlier.At = new DateTime(2026, 1, 20, 9, 0, 0);
                earlier.App = "chrome";
                engine.Enqueue(later);
                engine.Enqueue(earlier);
                engine.WaitIdle(5000);
                engine.SaveAll();

                StatsSnapshot s = engine.GetSnapshot(new DateTime(2026, 1, 1), new DateTime(2026, 2, 28));
                Check.Equal(2L, s.TotalAll, "both months retained");
                MonthData m1 = MonthFile.Load(Path.Combine(dir, "2026-01.kds"), 2026, 1);
                MonthData m2 = MonthFile.Load(Path.Combine(dir, "2026-02.kds"), 2026, 2);
                Check.Equal(1L, m1.Total, "january got its late event");
                Check.Equal(1L, m2.Total, "february retained its event");
                engine.Dispose();
            }
            finally
            {
                TestPaths.Delete(dir);
            }
        }
    }
}
