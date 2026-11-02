using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using KeyInsight.Stats;

namespace KeyInsight.Tests.Storage
{
    /// <summary>
    /// KDS1 兼容黄金夹具（STO-001/003/005）：手工构造的合成 2026-01 月份文件，
    /// 聚合口径自洽（全局 K 行 = 各 app AK 行之和）。读取口径冻结、Save/Load 往返无损。
    /// </summary>
    internal static class KdsCompatibilityTests
    {
        public static void Register()
        {
            TestRegistry.Add("Storage", "KDS_GoldenLoad", KDS_GoldenLoad);
            TestRegistry.Add("Storage", "KDS_RoundTrip", KDS_RoundTrip);
            TestRegistry.Add("Storage", "KDS_MalformedLinesIgnored", KDS_MalformedLinesIgnored);
            TestRegistry.Add("Storage", "KDS_ReadHeader", KDS_ReadHeader);
        }

        internal static string GoldenJanuary()
        {
            StringBuilder w = new StringBuilder();
            w.AppendLine("# KDS1 month=2026-01 total=104 swallowed=6");
            w.AppendLine("D\t20260115\t104\t6\t7\t" + Hours(9, 60, 10, 30, 11, 14));
            w.AppendLine("M\t20260115\t" + MinutesHex(new int[] { 541, 542, 600, 660, 661, 719, 840 }));
            w.AppendLine("K\t20260115\t69\t80\t3");
            w.AppendLine("K\t20260115\t65549\t20\t2");
            w.AppendLine("K\t20260115\t65\t4\t1");
            w.AppendLine("MB\t20260115\t1\t30");
            w.AppendLine("MB\t20260115\t2\t5");
            w.AppendLine("MB\t20260115\t6\t12");
            w.AppendLine("A\t20260115\tchrome\t102\t6\t" + Hours(9, 58, 10, 30, 11, 14));
            w.AppendLine("A\t20260115\tcode\t2\t0\t" + Hours(9, 2));
            w.AppendLine("AK\t20260115\tchrome\t69\t80\t3");
            w.AppendLine("AK\t20260115\tchrome\t65549\t20\t2");
            w.AppendLine("AK\t20260115\tchrome\t65\t2\t1");
            w.AppendLine("AK\t20260115\tcode\t65\t2\t0");
            return w.ToString();
        }

        internal static string GoldenFebruary()
        {
            StringBuilder w = new StringBuilder();
            w.AppendLine("# KDS1 month=2026-02 total=3 swallowed=0");
            w.AppendLine("D\t20260203\t3\t0\t1\t" + Hours(22, 3));
            w.AppendLine("K\t20260203\t9\t3\t0");
            return w.ToString();
        }

        private static string Hours(int hourA, int countA, int hourB, int countB, int hourC, int countC)
        {
            int[] h = new int[24];
            h[hourA] = countA;
            h[hourB] = countB;
            h[hourC] = countC;
            return HoursText(h);
        }

        private static string Hours(int hourA, int countA)
        {
            int[] h = new int[24];
            h[hourA] = countA;
            return HoursText(h);
        }

        private static string HoursText(int[] hours)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < hours.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(hours[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>与 MonthFile 相同的分钟位图编码（夹具构造用）。</summary>
        private static string MinutesHex(int[] minutes)
        {
            byte[] bytes = new byte[180];
            for (int i = 0; i < minutes.Length; i++) bytes[minutes[i] >> 3] |= (byte)(1 << (minutes[i] & 7));
            StringBuilder sb = new StringBuilder(360);
            for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }

        private static MonthData LoadGolden(string dir, string fileName, string content, int year, int month)
        {
            string path = Path.Combine(dir, fileName);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return MonthFile.Load(path, year, month);
        }

        private static void KDS_GoldenLoad()
        {
            string dir = TestPaths.NewTempDir();
            try
            {
                MonthData md = LoadGolden(dir, "2026-01.kds", GoldenJanuary(), 2026, 1);
                Check.Equal(104, md.Total, "month total");
                Check.Equal(6, md.Swallowed, "month swallowed");

                DayData day;
                Check.True(md.Days.TryGetValue(20260115, out day), "day present");
                Check.Equal(104, day.Total, "day total");
                Check.Equal(6, day.Swallowed, "day swallowed");
                Check.Equal(7, day.ActiveMinutes, "active minutes from M bitmap");
                Check.Equal(60, day.Hours[9], "hour 9");
                Check.Equal(30, day.Hours[10], "hour 10");
                Check.Equal(14, day.Hours[11], "hour 11");

                KeyAgg k69 = day.Keys[69];
                Check.Equal(80, k69.Count, "E count");
                Check.Equal(3, k69.Swallowed, "E swallowed");
                // 小键盘 Enter 的历史 VK 65549 (0x1000D) 必须原样保留，不得并入主 Enter
                KeyAgg kNum = day.Keys[65549];
                Check.Equal(20, kNum.Count, "numpad-enter count preserved");
                Check.Equal(2, kNum.Swallowed, "numpad-enter swallowed preserved");
                Check.True(!day.Keys.ContainsKey(13), "must not merge into vk13");
                Check.Equal(4, day.Keys[65].Count, "A count");

                AppAgg chrome = day.Apps["chrome"];
                Check.Equal(102, chrome.Total, "chrome total");
                Check.Equal(6, chrome.Swallowed, "chrome swallowed");
                Check.Equal(80, chrome.Keys[69].Count, "chrome E count");
                Check.Equal(30, day.Mouse[1], "mouse left");
                Check.Equal(5, day.Mouse[2], "mouse right");
                Check.Equal(12, day.Mouse[6], "wheel notch sum (legacy口径)");
                Check.True(!day.Apps["code"].Mouse.ContainsKey(1), "code has no mouse");

                bool minute541 = day.Minutes[541];
                Check.True(minute541, "minute 541 active");
                Check.True(day.Minutes[543] == false, "minute 543 inactive");
            }
            finally
            {
                TestPaths.Delete(dir);
            }
        }

        private static void CompareMonth(MonthData a, MonthData b, string ctx)
        {
            Check.Equal(a.Total, b.Total, ctx + " total");
            Check.Equal(a.Swallowed, b.Swallowed, ctx + " swallowed");
            Check.Equal(a.Days.Count, b.Days.Count, ctx + " day count");
            foreach (KeyValuePair<int, DayData> kv in a.Days)
            {
                DayData db;
                Check.True(b.Days.TryGetValue(kv.Key, out db), ctx + " day " + kv.Key + " present");
                Check.Equal(kv.Value.Total, db.Total, ctx + " d" + kv.Key + " total");
                Check.Equal(kv.Value.Swallowed, db.Swallowed, ctx + " d" + kv.Key + " swallowed");
                Check.Equal(kv.Value.ActiveMinutes, db.ActiveMinutes, ctx + " d" + kv.Key + " active");
                for (int h = 0; h < 24; h++)
                    Check.Equal(kv.Value.Hours[h], db.Hours[h], ctx + " d" + kv.Key + " h" + h);
                Check.Equal(kv.Value.Keys.Count, db.Keys.Count, ctx + " d" + kv.Key + " keys");
                foreach (KeyValuePair<int, KeyAgg> kk in kv.Value.Keys)
                {
                    KeyAgg kb;
                    Check.True(db.Keys.TryGetValue(kk.Key, out kb), ctx + " key " + kk.Key);
                    Check.Equal(kk.Value.Count, kb.Count, ctx + " key " + kk.Key + " count");
                    Check.Equal(kk.Value.Swallowed, kb.Swallowed, ctx + " key " + kk.Key + " sw");
                }
                foreach (KeyValuePair<int, long> mb in kv.Value.Mouse)
                    Check.Equal(mb.Value, db.Mouse.ContainsKey(mb.Key) ? db.Mouse[mb.Key] : -1,
                        ctx + " mouse btn " + mb.Key);
                foreach (KeyValuePair<string, AppAgg> aa in kv.Value.Apps)
                {
                    AppAgg ab;
                    Check.True(db.Apps.TryGetValue(aa.Key, out ab), ctx + " app " + aa.Key);
                    Check.Equal(aa.Value.Total, ab.Total, ctx + " app " + aa.Key + " total");
                    Check.Equal(aa.Value.Swallowed, ab.Swallowed, ctx + " app " + aa.Key + " sw");
                    Check.Equal(aa.Value.Keys.Count, ab.Keys.Count, ctx + " app " + aa.Key + " keys");
                }
            }
        }

        private static void KDS_RoundTrip()
        {
            string dir = TestPaths.NewTempDir();
            try
            {
                MonthData md = LoadGolden(dir, "2026-01.kds", GoldenJanuary(), 2026, 1);
                string path2 = Path.Combine(dir, "roundtrip.kds");
                MonthFile.Save(path2, md);

                Check.True(!File.Exists(path2 + ".tmp"), "tmp cleaned after save");
                Check.True(!File.Exists(path2 + ".bak"), "bak cleaned after save");

                MonthData md2 = MonthFile.Load(path2, 2026, 1);
                CompareMonth(md, md2, "roundtrip");
            }
            finally
            {
                TestPaths.Delete(dir);
            }
        }

        private static void KDS_MalformedLinesIgnored()
        {
            // STO-003：未知扩展记录与残缺行被忽略，已知记录不受影响
            string golden = GoldenJanuary();
            string polluted = golden +
                "X\t20260115\twhatever\n" +
                "D\tnotanumber\t5\t5\n" +
                "K\t20260115\n" +
                "\n" +
                "D\t20260115\t7\t7\n";
            string dir = TestPaths.NewTempDir();
            try
            {
                MonthData md = LoadGolden(dir, "2026-01.kds", polluted, 2026, 1);
                // "D\t20260115\t7\t7" 是合法短 D 行（无小时段）：total/swallowed 会被累加 —— 这正是解析器现行语义，冻结之
                Check.Equal(104 + 7, md.Total, "short D line appends (frozen current semantics)");
                Check.Equal(6 + 7, md.Swallowed, "short D line swallowed appends");
                Check.Equal(80, md.Days[20260115].Keys[69].Count, "golden keys intact");
            }
            finally
            {
                TestPaths.Delete(dir);
            }
        }

        private static void KDS_ReadHeader()
        {
            string dir = TestPaths.NewTempDir();
            try
            {
                string path = Path.Combine(dir, "2026-01.kds");
                File.WriteAllText(path, GoldenJanuary(), new UTF8Encoding(false));
                long total, swallowed;
                MonthFile.ReadHeader(path, out total, out swallowed);
                Check.Equal(104, total, "header total");
                Check.Equal(6, swallowed, "header swallowed");

                MonthFile.ReadHeader(Path.Combine(dir, "missing.kds"), out total, out swallowed);
                Check.Equal(0, total, "missing file total 0");
                Check.Equal(0, swallowed, "missing file swallowed 0");
            }
            finally
            {
                TestPaths.Delete(dir);
            }
        }
    }
}
