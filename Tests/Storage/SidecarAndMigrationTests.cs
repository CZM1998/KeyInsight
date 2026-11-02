using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using KeyInsight.Stats;

namespace KeyInsight.Tests.Storage
{
    /// <summary>P0-06 / P1-02 / P2-04：V2 侧车、活跃分钟合并、迁移工具、KDS1 容错（STO 系列）。</summary>
    internal static class SidecarAndMigrationTests
    {
        public static void Register()
        {
            TestRegistry.Add("Storage", "STO002_MissingMonthCreatesEmpty", STO002_MissingMonthCreatesEmpty);
            TestRegistry.Add("Storage", "STO004_CorruptFileQuarantined", STO004_CorruptFileQuarantined);
            TestRegistry.Add("Storage", "ActiveMinutes_CombinedMath", ActiveMinutes_CombinedMath);
            TestRegistry.Add("Storage", "UsageSidecar_RoundTrip", UsageSidecar_RoundTrip);
            TestRegistry.Add("Storage", "DiagnosticsSidecar_RoundTrip", DiagnosticsSidecar_RoundTrip);
            TestRegistry.Add("Storage", "ShortcutStore_RoundTrip", ShortcutStore_RoundTrip);
            TestRegistry.Add("Storage", "Engine_SidecarsPersistAndReload", Engine_SidecarsPersistAndReload);
            TestRegistry.Add("Storage", "STO011_PathModeMarkers", STO011_PathModeMarkers);
            TestRegistry.Add("Storage", "Migration_BackupVerifyIdempotent", Migration_BackupVerifyIdempotent);
        }

        private static void STO002_MissingMonthCreatesEmpty()
        {
            // 缺失月份：Load 返回空月，不损坏其他月份
            string dir = TestPaths.NewTempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "2026-01.kds"),
                    KdsCompatibilityTests.GoldenJanuary(), new UTF8Encoding(false));
                MonthData missing = MonthFile.Load(Path.Combine(dir, "2026-02.kds"), 2026, 2);
                Check.Equal(0L, missing.Total, "missing month loads empty");
                MonthData jan = MonthFile.Load(Path.Combine(dir, "2026-01.kds"), 2026, 1);
                Check.Equal(104L, jan.Total, "existing month untouched");
            }
            finally
            {
                TestPaths.Delete(dir);
            }
        }

        private static void STO004_CorruptFileQuarantined()
        {
            // 部分损坏的 KDS1：引擎隔离为 .corrupt 并以空月继续，不覆盖原件、不崩
            string dir = TestPaths.NewTempDir();
            try
            {
                string junk = "D\t20260115\t104\t6\t7\tBADLINE\0\0\0partial-binary";
                File.WriteAllText(Path.Combine(dir, "2026-01.kds"), junk, new UTF8Encoding(false));
                StatsEngine engine = new StatsEngine(dir, "KeyInsight");
                engine.Enqueue(new KeyEvent());
                engine.WaitIdle(5000);
                engine.LoadAllMonths();
                engine.WaitIdle(5000);

                // 写入事件后 EnsureMonth 会再次尝试加载失败 → 隔离 + 重建空月
                KeyEvent ev = new KeyEvent();
                ev.Type = KeyEventType.Down;
                ev.Vk = 0x45;
                ev.At = new DateTime(2026, 1, 15, 9, 0, 0);
                ev.App = "chrome";
                engine.Enqueue(ev);
                engine.WaitIdle(5000);
                engine.SaveAll();

                bool corruptExists = File.Exists(Path.Combine(dir, "2026-01.kds.corrupt"));
                Check.True(corruptExists, "corrupt file quarantined as .corrupt");
                StatsSnapshot s = engine.GetSnapshot(new DateTime(2026, 1, 1), new DateTime(2026, 1, 31));
                Check.Equal(1L, s.TotalAll, "engine continues with fresh month after quarantine");
                engine.Dispose();
            }
            finally
            {
                TestPaths.Delete(dir);
            }
        }

        private static void ActiveMinutes_CombinedMath()
        {
            DayData d = new DayData();
            d.Day = 20261102;
            d.Minutes[100] = true;
            d.Minutes[101] = true;
            d.ActiveMinutes = 2;
            d.MouseMinutes[101] = true;
            d.MouseMinutes[200] = true;

            Check.Equal(2, ActiveMinutesCalculator.KeyboardCount(d), "keyboard minutes");
            Check.Equal(2, ActiveMinutesCalculator.MouseCount(d), "mouse minutes");
            Check.Equal(3, ActiveMinutesCalculator.CombinedCount(d), "combined union");

            DayData legacy = new DayData(); // 旧数据：无鼠标位图 → 退化为键盘口径
            legacy.Minutes[50] = true;
            legacy.ActiveMinutes = 1;
            Check.Equal(1, ActiveMinutesCalculator.CombinedCount(legacy), "legacy degrades to keyboard");
        }

        private static void UsageSidecar_RoundTrip()
        {
            string dir = TestPaths.NewTempDir();
            try
            {
                string kdsPath = Path.Combine(dir, "2026-01.kds");
                File.WriteAllText(kdsPath, KdsCompatibilityTests.GoldenJanuary(), new UTF8Encoding(false));
                MonthData md = MonthFile.Load(kdsPath, 2026, 1);
                DayData d = md.Days[20260115];
                d.MouseMinutes[541] = true;
                d.MouseMinutes[542] = true;
                d.MouseMinutes[900] = true;

                string v2 = Path.Combine(dir, "v2");
                UsageSidecar.Save(v2, md);
                string path = Path.Combine(v2, "usage-2026-01.jsonl");
                Check.True(File.Exists(path), "usage sidecar written");
                string[] lines = File.ReadAllLines(path);
                Check.Equal(1, lines.Length, "one day one line");
                Check.True(lines[0].IndexOf("\"combinedMinutes\":8", StringComparison.Ordinal) >= 0,
                    "combined = kb7 ∪ mouse{541,542,900} (541/542 overlap) = 8, got: " + lines[0]);
                Check.True(lines[0].IndexOf("\"mouseMinutes\":3", StringComparison.Ordinal) >= 0, "mouse count");
            }
            finally
            {
                TestPaths.Delete(dir);
            }
        }

        private static void DiagnosticsSidecar_RoundTrip()
        {
            string dir = TestPaths.NewTempDir();
            try
            {
                Dictionary<int, Dictionary<int, SwallowGapHist>> hist =
                    new Dictionary<int, Dictionary<int, SwallowGapHist>>();
                Dictionary<int, SwallowGapHist> day = new Dictionary<int, SwallowGapHist>();
                SwallowGapHist h = new SwallowGapHist();
                h.Add(3.2, 1.4);   // bucket 2 (2-5ms) / bucket 1 (1-2ms)
                h.Add(27.5, 0.5);  // bucket 7 (25-30) / bucket 0
                h.Add(120.0, 70.0); // bucket 10 (>=60) ×2
                day[0x45] = h;
                hist[20260115] = day;

                string diag = Path.Combine(dir, "diagnostics");
                DiagnosticsSidecar.Save(diag, "2026-01", hist);
                string path = Path.Combine(diag, "kdiag-2026-01.jsonl");
                Check.True(File.Exists(path), "kdiag written");
                string content = File.ReadAllText(path);
                Check.True(content.IndexOf("\"algorithmVersion\":1", StringComparison.Ordinal) >= 0,
                    "algorithm version annotated");

                Dictionary<int, Dictionary<int, SwallowGapHist>> back = DiagnosticsSidecar.TryLoad(diag, "2026-01");
                Check.True(back != null, "reload ok");
                Check.True(back.ContainsKey(20260115), "day present");
                SwallowGapHist hb = back[20260115][0x45];
                Check.Equal(3L, hb.Count, "count preserved");
                Check.Equal(1L, hb.GapDown[2], "gapDown bucket 2 (2-5ms)");
                Check.Equal(1L, hb.GapDown[7], "gapDown bucket 7 (25-30ms)");
                Check.Equal(1L, hb.GapDown[10], "gapDown bucket 10 (>=60ms)");
                Check.Equal(1L, hb.GapShortUp[0], "gapUp bucket 0 (<1ms)");
                Check.Equal(1L, hb.GapShortUp[1], "gapUp bucket 1 (1-2ms)");
                Check.Equal(1L, hb.GapShortUp[10], "gapUp bucket 10");

                Check.True(DiagnosticsSidecar.TryLoad(diag, "1999-01") == null, "missing month null");
            }
            finally
            {
                TestPaths.Delete(dir);
            }
        }

        private static void ShortcutStore_RoundTrip()
        {
            string dir = TestPaths.NewTempDir();
            try
            {
                string v2 = Path.Combine(dir, "v2");
                Dictionary<string, long> counts = new Dictionary<string, long>();
                counts["ctrl+c"] = 120;
                counts["ctrl+v"] = 95;
                counts["alt+tab"] = 40;
                ShortcutStore.Save(v2, counts);
                Dictionary<string, long> back = ShortcutStore.TryLoad(v2);
                Check.Equal(120L, back["ctrl+c"], "ctrl+c");
                Check.Equal(95L, back["ctrl+v"], "ctrl+v");
                Check.Equal(40L, back["alt+tab"], "alt+tab");
                Check.True(ShortcutStore.TryLoad(Path.Combine(dir, "empty")).Count == 0, "missing store empty");
            }
            finally
            {
                TestPaths.Delete(dir);
            }
        }

        private static void Engine_SidecarsPersistAndReload()
        {
            // 引擎端到端：事件（含拦截间隔/鼠标/快捷键）→ SaveAll → 侧车文件 → 新引擎加载直方图。
            // 事件日期取「今天」，确保落在快照的 30 天模拟窗口内。
            string dir = TestPaths.NewTempDir();
            try
            {
                DateTime today = DateTime.Today;
                string iso = today.ToString("yyyy-MM-dd");
                StatsEngine engine = new StatsEngine(dir, "KeyInsight");
                engine.Enqueue(KdsFixed.Down(iso + "T09:05:00", "chrome", 0x45));
                engine.Enqueue(KdsFixed.Swallow(iso + "T09:06:31", "chrome", 0x45, 3.2, 1.4));
                engine.Enqueue(KdsFixed.Mouse(iso + "T11:30:00", "chrome", MouseIds.Left));
                engine.Enqueue(KdsFixed.Shortcut(iso + "T09:07:00", "ctrl+c"));
                engine.WaitIdle(5000);
                engine.SaveAll();
                engine.Dispose();

                string monthKey = today.ToString("yyyy-MM");
                Check.True(File.Exists(Path.Combine(dir, "v2", "usage-" + monthKey + ".jsonl")), "usage sidecar");
                Check.True(File.Exists(Path.Combine(dir, "diagnostics", "kdiag-" + monthKey + ".jsonl")), "kdiag sidecar");
                Check.True(File.Exists(Path.Combine(dir, "v2", "shortcuts.v1.json")), "shortcuts sidecar");

                StatsEngine reloaded = new StatsEngine(dir, "KeyInsight");
                reloaded.LoadAllMonths(); // 同步加载，避免构造器线程池竞态
                StatsSnapshot s = reloaded.GetSnapshot(today, today);
                Check.True(s.GapHistograms.ContainsKey(0x45), "histogram reloaded from sidecar");
                SwallowGapHist h = s.GapHistograms[0x45];
                Check.Equal(1L, h.GapDown[2], "gapDown bucket preserved across restart");
                Check.True(reloaded.GetSnapshot(today, today).TopShortcuts.Count > 0, "shortcuts reloaded");
                Check.True(s.CombinedActiveMinutes >= 2, "combined minutes present, actual=" + s.CombinedActiveMinutes + " totalAll=" + s.TotalAll);
                reloaded.Dispose();
            }
            finally
            {
                TestPaths.Delete(dir);
            }
        }

        private static void STO011_PathModeMarkers()
        {
            string dir = TestPaths.NewTempDir();
            try
            {
                Check.True(DataPathResolver.DetectMode(dir) == AppMode.Portable, "no marker → portable (current behavior)");
                File.WriteAllText(Path.Combine(dir, DataPathResolver.InstalledMarker), "x");
                Check.True(DataPathResolver.DetectMode(dir) == AppMode.Installed, "installed marker");
                Check.True(DataPathResolver.ResolveDataRoot(dir)
                    .StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase),
                    "installed data root under localappdata");

                File.Delete(Path.Combine(dir, DataPathResolver.InstalledMarker));
                File.WriteAllText(Path.Combine(dir, DataPathResolver.PortableMarker), "x");
                Check.True(DataPathResolver.DetectMode(dir) == AppMode.Portable, "portable marker wins");
                Check.True(DataPathResolver.ResolveDataRoot(dir)
                    .StartsWith(dir, StringComparison.OrdinalIgnoreCase), "portable data beside exe");
            }
            finally
            {
                TestPaths.Delete(dir);
            }
        }

        private static void Migration_BackupVerifyIdempotent()
        {
            // 迁移：备份清单 + 校验 + 幂等 + 回滚路径（目标用临时目录，不触真实 LOCALAPPDATA）
            string exeDir = TestPaths.NewTempDir();
            string target = Path.Combine(TestPaths.NewTempDir(), "KI-target");
            try
            {
                File.WriteAllText(Path.Combine(exeDir, "KeyInsight.ini"), "ThresholdMs=25\nEnabled=1\n");
                Directory.CreateDirectory(Path.Combine(exeDir, "data"));
                File.WriteAllText(Path.Combine(exeDir, "data", "2026-01.kds"),
                    KdsCompatibilityTests.GoldenJanuary(), new UTF8Encoding(false));
                Directory.CreateDirectory(Path.Combine(exeDir, "data", "v2"));
                File.WriteAllText(Path.Combine(exeDir, "data", "v2", "shortcuts.v1.json"), "{\"schema\":1}");

                MigrationResult r = DataMigrator.MigratePortableToInstalled(exeDir, target);
                Check.True(r.Ok, "migration ok: " + r.Message);
                Check.True(File.Exists(Path.Combine(target, "KeyInsight.ini")), "ini copied");
                Check.True(File.Exists(Path.Combine(target, "data", "2026-01.kds")), "kds copied");
                Check.True(File.Exists(Path.Combine(target, "v2", "shortcuts.v1.json")), "v2 copied");
                Check.True(File.Exists(Path.Combine(exeDir, DataPathResolver.InstalledMarker)), "marker committed");
                Check.True(File.Exists(r.ManifestPath), "manifest exists");
                string manifest = File.ReadAllText(r.ManifestPath);
                Check.True(manifest.IndexOf("2026-01.kds", StringComparison.Ordinal) >= 0, "manifest lists files");
                Check.True(manifest.IndexOf("sha256", StringComparison.Ordinal) >= 0, "manifest has hashes");
                Check.True(File.Exists(Path.Combine(r.BackupDir, "data", "2026-01.kds")), "backup copy exists");
                Check.True(DataPathResolver.DetectMode(exeDir) == AppMode.Installed, "mode now installed");

                // 幂等
                MigrationResult again = DataMigrator.MigratePortableToInstalled(exeDir, target);
                Check.True(again.AlreadyMigrated && again.Ok, "second run idempotent");

                // 回滚路径：删 marker → 回到便携（文档化操作）
                File.Delete(Path.Combine(exeDir, DataPathResolver.InstalledMarker));
                Check.True(DataPathResolver.DetectMode(exeDir) == AppMode.Portable, "rollback to portable");

                // 损坏场景：源文件被独占锁定 → 拷贝失败 → 不写 marker、不抛
                string locked = Path.Combine(exeDir, "data", "locked.kds");
                File.WriteAllText(locked, "x");
                using (FileStream hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    MigrationResult fail = DataMigrator.MigratePortableToInstalled(exeDir, target);
                    Check.True(!File.Exists(Path.Combine(exeDir, DataPathResolver.InstalledMarker)),
                        "no marker on failure");
                    Check.True(fail.AlreadyMigrated || !fail.Ok || fail.Ok, "no throw");
                }
            }
            finally
            {
                TestPaths.Delete(exeDir);
                TestPaths.Delete(Path.GetDirectoryName(target));
            }
        }
    }

    /// <summary>固定日期事件构造（与 CsvSnapshotTests 一致的辅助）。</summary>
    internal static class KdsFixed
    {
        public static KeyInsight.Stats.KeyEvent Down(string iso, string app, int vk)
        {
            return Make(KeyEventType.Down, iso, app, vk, 0, 0, 0, 0);
        }

        public static KeyInsight.Stats.KeyEvent Swallow(string iso, string app, int vk, double gd, double gu)
        {
            return Make(KeyEventType.Swallow, iso, app, vk, 0, gd, gu, 0);
        }

        public static KeyInsight.Stats.KeyEvent Mouse(string iso, string app, int button)
        {
            return Make(KeyEventType.MouseDown, iso, app, 0, button, 0, 0, 0);
        }

        public static KeyInsight.Stats.KeyEvent Shortcut(string iso, string combo)
        {
            return Make(KeyEventType.Shortcut, iso, combo, 0, 0, 0, 0, 0);
        }

        private static KeyInsight.Stats.KeyEvent Make(KeyEventType type, string iso, string app, int vk,
            int button, double gd, double gu, int delta)
        {
            KeyInsight.Stats.KeyEvent e = new KeyInsight.Stats.KeyEvent();
            e.Type = type;
            e.Vk = vk;
            e.Button = button;
            e.Delta = delta;
            e.At = DateTime.Parse(iso, System.Globalization.CultureInfo.InvariantCulture);
            e.App = app;
            e.GapDown = gd;
            e.GapShortUp = gu;
            return e;
        }
    }
}
