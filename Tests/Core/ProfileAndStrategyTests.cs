using System;
using System.Collections.Generic;
using System.IO;
using KeyInsight.Core.Policy;
using KeyInsight.Stats;

namespace KeyInsight.Tests.Core
{
    /// <summary>P1-02 / P2-01 / P2-02 / P2-04 / P2-05 纯逻辑测试。</summary>
    internal static class ProfileAndStrategyTests
    {
        public static void Register()
        {
            TestRegistry.Add("Core", "SIM_ThresholdProjection", SIM_ThresholdProjection);
            TestRegistry.Add("Core", "SIM_NoData", SIM_NoData);
            TestRegistry.Add("Core", "MAINT_AppendLoadCompare", MAINT_AppendLoadCompare);
            TestRegistry.Add("Core", "SHORTCUT_Observer", SHORTCUT_Observer);
            TestRegistry.Add("Core", "PROFILE_Projector", PROFILE_Projector);
            TestRegistry.Add("Storage", "REPORT_WeeklyAndManifest", REPORT_WeeklyAndManifest);
            TestRegistry.Add("Core", "LAYOUT_IsoVariants", LAYOUT_IsoVariants);
            TestRegistry.Add("Core", "STORE_KeyPolicyRoundTrip", STORE_KeyPolicyRoundTrip);
        }

        private static StatsSnapshot SnapWithHist(int vk, long[] gapDown)
        {
            StatsSnapshot snap = new StatsSnapshot();
            snap.Range = new RangeView();
            SwallowGapHist h = new SwallowGapHist();
            for (int i = 0; i < gapDown.Length && i < SwallowGapHist.BucketCount; i++) h.GapDown[i] = gapDown[i];
            h.Count = 0;
            for (int i = 0; i < gapDown.Length; i++) h.Count += gapDown[i];
            snap.GapHistograms[vk] = h;
            return snap;
        }

        private static void SIM_ThresholdProjection()
        {
            // 桶：0(<1),1(1-2),2(2-5),3(5-10),4(10-15),5(15-20),6(20-25),7(25-30),8(30-40),9(40-60),10(>=60)
            long[] gd = new long[] { 0, 1, 1, 2, 2, 1, 1, 1, 1, 0, 0 }; // total 10
            StatsSnapshot snap = SnapWithHist(0x45, gd);
            SimulationResult r = PolicySimulator.Simulate(0x45, snap);
            Check.True(r.HasData, "has data");

            SimulationRow row10 = null, row20 = null, row30 = null;
            foreach (SimulationRow row in r.Rows)
            {
                if (row.ThresholdMs == 10) row10 = row;
                if (row.ThresholdMs == 20) row20 = row;
                if (row.ThresholdMs == 30) row30 = row;
            }
            // 阈值 10：桶上界 <=10 → 桶0..3 计入 = 0+1+1+2 = 4
            Check.Equal(4L, row10.EstimatedFiltered, "thr10 estimate");
            // 阈值 20：桶0..5 = 0+1+1+2+2+1 = 7
            Check.Equal(7L, row20.EstimatedFiltered, "thr20 estimate");
            // 阈值 30：桶0..7 = 7+1+1 = 9
            Check.Equal(9L, row30.EstimatedFiltered, "thr30 estimate");
            Check.Equal(10L, row30.TotalCandidateEvents, "total candidates");
            Check.True(r.RecommendedThresholdMs > 0, "recommendation set");
            Check.True(r.Note.IndexOf("不等于真实误拦率", StringComparison.Ordinal) >= 0, "prediction disclaimer");
        }

        private static void SIM_NoData()
        {
            SimulationResult r = PolicySimulator.Simulate(0x52, new StatsSnapshot());
            Check.True(!r.HasData, "no data");
            Check.True(r.Note.IndexOf("数据不足", StringComparison.Ordinal) >= 0, "insufficient note");
        }

        private static void MAINT_AppendLoadCompare()
        {
            string dir = TestPaths.NewTempDir();
            try
            {
                MaintenanceEvent a = new MaintenanceEvent();
                a.TimeUtcTicks = DateTime.UtcNow.Ticks;
                a.Vk = 0x45;
                a.Action = "clean";
                a.Note = "触点有点灰";
                MaintenanceStore.Append(dir, a);

                MaintenanceEvent b = new MaintenanceEvent();
                b.TimeUtcTicks = DateTime.UtcNow.Ticks + TimeSpan.TicksPerMinute;
                b.Vk = 0x52;
                b.Action = "retest";
                MaintenanceStore.Append(dir, b);

                List<MaintenanceEvent> loaded = MaintenanceStore.Load(dir);
                Check.Equal(2, loaded.Count, "two events");
                Check.Equal(0x45, loaded[0].Vk, "sorted by time");
                Check.True(loaded[0].ActionText == "清洁触点", "action text");
                Check.True(loaded[0].Note == "触点有点灰", "note preserved");

                // 前后对比：事件在今天，两侧样本不足 → 显式提示（不伪精确）
                StatsSnapshot snap = new StatsSnapshot();
                snap.KeyTrendStart = DateTime.Today.AddDays(-29);
                snap.KeySwallowTrend[0x45] = new int[30];
                MaintenanceStore.BeforeAfter cmp = MaintenanceStore.Compare(0x45, loaded[0], snap);
                Check.True(cmp.Verdict.Length > 0, "verdict present");

                // 构造两侧样本：事件 10 天前，前 7 天 5 次，后 7 天 2 次 → 有改善
                MaintenanceEvent old = new MaintenanceEvent();
                old.TimeUtcTicks = DateTime.Today.AddDays(-10).ToUniversalTime().Ticks;
                old.Vk = 0x45;
                old.Action = "switch";
                for (int i = 11; i <= 17; i++)
                {
                    if (i < 30) snap.KeySwallowTrend[0x45][30 - i] = 1; // 大致落在事件前
                }
                MaintenanceStore.BeforeAfter cmp2 = MaintenanceStore.Compare(0x45, old, snap);
                Check.True(cmp2.HasBefore || cmp2.Verdict.Length > 0, "comparison computed");
            }
            finally
            {
                TestPaths.Delete(dir);
            }
        }

        private static void SHORTCUT_Observer()
        {
            ShortcutObserver obs = new ShortcutObserver();
            // Ctrl 按下 → C → 释放
            Check.True(obs.Observe(0xA2, true, false) == null, "modifier tracked");
            string combo = obs.Observe(0x43, true, false);
            Check.True(combo == "ctrl+c", "ctrl+c observed, got " + combo);
            obs.Observe(0x43, false, false);
            obs.Observe(0xA2, false, false);

            // 无修饰键的普通按键不计数
            Check.True(obs.Observe(0x45, true, false) == null, "plain key ignored");
            // 未白名单的 Ctrl 组合不计数
            obs.Observe(0xA2, true, false);
            Check.True(obs.Observe(0x30, true, false) == null, "non-whitelisted combo ignored");
            obs.Observe(0xA2, false, false);
            // Alt+Tab / Alt+F4
            obs.Observe(0xA4, true, false);
            Check.True(obs.Observe(0x09, true, false) == "alt+tab", "alt+tab");
            obs.Observe(0xA4, false, false);
            obs.Observe(0xA4, true, false);
            Check.True(obs.Observe(0x73, true, false) == "alt+f4", "alt+f4");
            obs.Observe(0xA4, false, false);
            Check.Equal(3L, obs.TotalCombos, "three combos counted");
        }

        private static void PROFILE_Projector()
        {
            StatsSnapshot snap = new StatsSnapshot();
            snap.Range = new RangeView();
            KeyAgg agg = new KeyAgg(100, 5);
            snap.Range.Keys[0x45] = agg;

            KeyHealthRow row = new KeyHealthRow();
            row.Vk = 0x45;
            row.Total = 100;
            row.Swallowed = 5;
            row.Swallowed7 = 3;
            row.SwallowedPrev7 = 2;
            row.Score = 87;
            row.Status = 2;
            snap.KeyHealth.Add(row);

            SwallowGapHist h = new SwallowGapHist();
            h.Add(3, 1);
            snap.GapHistograms[0x45] = h;

            string dir = TestPaths.NewTempDir();
            try
            {
                KeyPolicyStore store = new KeyPolicyStore(Path.Combine(dir, "kp.json"));
                KeyPolicyEntry e = store.GetOrCreate(0x45, KeyInsight.Core.Input.InputIdentityResolver.CanonicalScanFor(0x45), false);
                e.Enabled = true;
                e.ThresholdMs = 15;

                KeyProfileView v = KeyProfileProjector.Project(0x45, snap, store, PolicyMode.PerKeyConservative);
                Check.Equal(100L, v.ObservedDown, "observed");
                Check.Equal(5L, v.SuppressedDown, "suppressed");
                Check.Equal(87, v.LegacyScore, "legacy score");
                Check.True(v.PerKeyEnabled, "per-key enabled");
                Check.Equal(15, v.PerKeyThresholdMs, "per-key threshold");
                Check.True(v.HasGapHistogram, "histogram flag");
                Check.Equal(1L, v.CandidateEvents30d, "candidate events");
                Check.True(!v.InsufficientData, "sample sufficient");
                Check.True(v.Label.Length > 0, "label present");
            }
            finally
            {
                TestPaths.Delete(dir);
            }
        }

        private static void REPORT_WeeklyAndManifest()
        {
            string dir = TestPaths.NewTempDir();
            try
            {
                StatsSnapshot snap = new StatsSnapshot();
                snap.Range = new RangeView();
                snap.Range.Daily = new List<DayPoint>();
                DayPoint p1 = new DayPoint();
                p1.Date = DateTime.Today;
                p1.Total = 120;
                p1.Swallowed = 3;
                snap.Range.Daily.Add(p1);
                snap.Range.Total = 120;
                snap.Range.Swallowed = 3;
                snap.Range.Keys[0x45] = new KeyAgg(80, 3);
                snap.CombinedActiveMinutes = 42;
                KeyHealthRow row = new KeyHealthRow();
                row.Vk = 0x45;
                row.Score = 55;
                row.Swallowed7 = 4;
                row.SwallowedPrev7 = 2;
                snap.KeyHealth.Add(row);
                snap.TopShortcuts.Add(new KeyValuePair<string, long>("ctrl+c", 30));

                string path = WeeklyReport.Generate(dir, snap);
                Check.True(path != null && File.Exists(path), "weekly report written");
                string text = File.ReadAllText(path);
                Check.True(text.IndexOf("精度说明", StringComparison.Ordinal) >= 0, "precision disclaimer");
                Check.True(text.IndexOf("ctrl+c", StringComparison.Ordinal) >= 0, "shortcuts listed");
                Check.True(text.IndexOf("基于拦截", StringComparison.Ordinal) >= 0, "legacy metric labeled");

                string manifest = WeeklyReport.WriteExportManifest(dir, DateTime.MinValue, DateTime.MaxValue,
                    new string[] { "a-days.csv", "a-keys.csv" });
                Check.True(manifest != null && File.Exists(manifest), "manifest written");
                string json = File.ReadAllText(manifest);
                Check.True(json.IndexOf("keyboard down events", StringComparison.Ordinal) >= 0, "precision in manifest");
                Check.True(json.IndexOf("a-days.csv", StringComparison.Ordinal) >= 0, "files listed");
            }
            finally
            {
                TestPaths.Delete(dir);
            }
        }

        private static void LAYOUT_IsoVariants()
        {
            Check.True(KeyInsight.UI.KeyLayout.Layout87Iso != null, "iso87 exists");
            Check.True(KeyInsight.UI.KeyLayout.Layout108Iso != null, "iso108 exists");
            Check.Equal(KeyInsight.UI.KeyLayout.Layout87.KeyCount + 1,
                KeyInsight.UI.KeyLayout.Layout87Iso.KeyCount, "iso adds one key");
            Check.True(KeyInsight.UI.KeyLayout.Layout108Iso.KeyCount >
                KeyInsight.UI.KeyLayout.Layout87Iso.KeyCount, "iso108 has numpad");
            bool hasOem102 = false;
            foreach (KeyInsight.UI.KeyDef k in KeyInsight.UI.KeyLayout.Layout87Iso.Keys)
            {
                if (k.Label == "<>") hasOem102 = true;
            }
            Check.True(hasOem102, "ISO <> key present");
        }

        private static void STORE_KeyPolicyRoundTrip()
        {
            string dir = TestPaths.NewTempDir();
            try
            {
                string path = Path.Combine(dir, "profiles", "key-policies.v1.json");
                KeyPolicyStore store = new KeyPolicyStore(path);
                KeyPolicyEntry e = store.GetOrCreate(0x45, 0x12, false);
                e.Enabled = true;
                e.ThresholdMs = 18;
                e.Note = "test";
                store.Save();
                Check.True(File.Exists(path), "file written");

                KeyPolicyStore reload = new KeyPolicyStore(path);
                KeyPolicyEntry back = reload.Find(0x45, 0x12, false);
                Check.True(back != null, "entry reloaded");
                Check.True(back.Enabled, "enabled preserved");
                Check.Equal(18, back.ThresholdMs, "threshold preserved");
                Check.True(back.Note == "test", "note preserved");

                // 删除与损坏恢复
                reload.Remove(0x45, 0x12, false);
                reload.Save();
                Check.True(new KeyPolicyStore(path).Find(0x45, 0x12, false) == null, "removed");

                File.WriteAllText(path, "{ not json !!");
                KeyPolicyStore corrupt = new KeyPolicyStore(path);
                Check.Equal(0, corrupt.Entries.Count, "corrupt falls back to empty");
                Check.True(File.Exists(path + ".corrupt"), "corrupt copy preserved");
            }
            finally
            {
                TestPaths.Delete(dir);
            }
        }
    }
}
