using System;
using System.Collections.Generic;
using System.IO;

namespace KeyInsight.Stats
{
    /// <summary>
    /// 性能/回归用的合成数据生成器（PR-0）：直接构造 MonthData 并落盘 KDS1，
    /// 结构与真实数据一致（D/M/K/A/AK/MB/AMB），可重复（固定随机种子）。
    /// 不经过 StatsEngine，生成 36 个月也只需数秒。
    /// </summary>
    public static class PerfDataGenerator
    {
        private static readonly int[] Keys = new int[]
        {
            0x45, 0x20, 0x41, 0x53, 0x44, 0x46, 0x47, 0x48, 0x4A, 0x4B, 0x4C, 0x51, 0x57, 0x5A, 0x58,
            0x43, 0x56, 0x42, 0x4E, 0x4D, 0x54, 0x59, 0x55, 0x49, 0x4F, 0x50, 0x31, 0x32, 0x33, 0x34,
            0x35, 0x36, 0x37, 0x38, 0x39, 0x30, 0x08, 0x0D, 0x09, 0xA0, 0xA2, 0x25, 0x26, 0x27, 0x28,
            0xBC, 0xBE, 0xBF, 0xBA, 0xDE, 0xDB, 0xDD, 0xDC, 0xBD, 0xBB, 0x1B,
            0x60, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6E, 0x1000D
        };

        private static readonly string[] Apps = new string[]
        {
            "chrome", "code", "wechat", "explorer", "windowsterminal", "msedge", "steam", "idea64", "discord", "notepad"
        };

        /// <summary>在 dir 下生成截止当月的 months 个完整月份。perDay 为每天按键数。</summary>
        public static void Generate(string dir, int months, int perDay, int seed)
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            Random rnd = new Random(seed);
            DateTime cursor = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            for (int m = 0; m < months; m++)
            {
                DateTime monthStart = cursor.AddMonths(-m);
                MonthData md = new MonthData();
                md.Year = monthStart.Year;
                md.Month = monthStart.Month;

                int daysInMonth = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);
                DateTime now = DateTime.Now;
                for (int day = 1; day <= daysInMonth; day++)
                {
                    DateTime date = new DateTime(monthStart.Year, monthStart.Month, day);
                    if (date > now) break;
                    int n = perDay / 2 + rnd.Next(perDay);
                    DayData d = new DayData();
                    d.Day = DayKeys.From(date);
                    for (int i = 0; i < n; i++)
                    {
                        int hour = WeightedHour(rnd);
                        int minuteOfDay = hour * 60 + rnd.Next(60);
                        string app = Apps[rnd.Next(Apps.Length)];
                        int vk = Keys[WeightedKeyIndex(rnd)];

                        d.Total++;
                        d.Hours[hour]++;
                        if (!d.Minutes[minuteOfDay])
                        {
                            d.Minutes[minuteOfDay] = true;
                            d.ActiveMinutes++;
                        }
                        KeyAgg ka;
                        if (!d.Keys.TryGetValue(vk, out ka))
                        {
                            ka = new KeyAgg();
                            d.Keys[vk] = ka;
                        }
                        ka.Count++;

                        AppAgg aa;
                        if (!d.Apps.TryGetValue(app, out aa))
                        {
                            aa = new AppAgg(app);
                            d.Apps[app] = aa;
                        }
                        aa.Total++;
                        aa.Hours[hour]++;
                        KeyAgg aak;
                        if (!aa.Keys.TryGetValue(vk, out aak))
                        {
                            aak = new KeyAgg();
                            aa.Keys[vk] = aak;
                        }
                        aak.Count++;

                        if (rnd.NextDouble() < 0.012)
                        {
                            int svk = rnd.NextDouble() < 0.85 ? 0x45 : vk;
                            d.Swallowed++;
                            KeyAgg sk;
                            if (!d.Keys.TryGetValue(svk, out sk))
                            {
                                sk = new KeyAgg();
                                d.Keys[svk] = sk;
                            }
                            sk.Swallowed++;
                            aa.Swallowed++;
                            KeyAgg sak;
                            if (!aa.Keys.TryGetValue(svk, out sak))
                            {
                                sak = new KeyAgg();
                                aa.Keys[svk] = sak;
                            }
                            sak.Swallowed++;
                        }
                    }

                    int mice = Math.Max(4, n / 6);
                    for (int i = 0; i < mice; i++)
                    {
                        int hour = WeightedHour(rnd);
                        int minuteOfDay = hour * 60 + rnd.Next(60);
                        string app = Apps[rnd.Next(Apps.Length)];
                        int button = rnd.Next(1, 8);
                        long cur;
                        d.Mouse.TryGetValue(button, out cur);
                        d.Mouse[button] = cur + 1;
                        AppAgg aa;
                        if (!d.Apps.TryGetValue(app, out aa))
                        {
                            aa = new AppAgg(app);
                            d.Apps[app] = aa;
                        }
                        long acur;
                        aa.Mouse.TryGetValue(button, out acur);
                        aa.Mouse[button] = acur + 1;
                        if (!d.MouseMinutes[minuteOfDay]) d.MouseMinutes[minuteOfDay] = true;
                    }

                    md.Days[d.Day] = d;
                    md.Total += d.Total;
                    md.Swallowed += d.Swallowed;
                }

                MonthFile.Save(Path.Combine(dir, md.Key + ".kds"), md);
            }
        }

        private static int WeightedHour(Random rnd)
        {
            // 9-18 点占大头
            int r = rnd.Next(100);
            if (r < 55) return 9 + rnd.Next(10);
            if (r < 80) return 8 + rnd.Next(14);
            return rnd.Next(24);
        }

        private static int WeightedKeyIndex(Random rnd)
        {
            int r = rnd.Next(100);
            if (r < 26) return 1;   // Space
            if (r < 40) return 0;   // E
            if (r < 48) return 36;  // Backspace
            if (r < 54) return 37;  // Enter
            return rnd.Next(Keys.Length);
        }
    }
}
