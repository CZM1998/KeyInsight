using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace KeyInsight.Stats
{
    public static class MonthFile
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static MonthData Load(string path, int year, int month)
        {
            MonthData md = new MonthData();
            md.Year = year;
            md.Month = month;

            // STO-007：崩溃恢复。主文件缺失时按 .bak → .tmp 顺序尝试恢复；
            // 主文件存在但残留 .tmp 时顺手清理（正常保存路径本应已删除）。
            if (!File.Exists(path))
            {
                if (File.Exists(path + ".bak"))
                {
                    try { File.Copy(path + ".bak", path, false); }
                    catch { }
                }
                else if (File.Exists(path + ".tmp"))
                {
                    try { File.Copy(path + ".tmp", path, false); }
                    catch { }
                }
            }
            else if (File.Exists(path + ".tmp"))
            {
                try { File.Delete(path + ".tmp"); }
                catch { }
            }
            if (!File.Exists(path)) return md;

            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader reader = new StreamReader(fs, Encoding.UTF8, true))
            {
                // 文件级完整性门（STO-004）：v2.x 写出的 KDS1 首行都是 "# KDS1 ..." 注释头。
                // 非空文件缺头 = 半截写入/二进制垃圾/外部损坏 —— 行级容错（STO-003）无法区分
                // “未知扩展记录”和“损坏”，必须在文件级隔离，否则垃圾会被静默误收进统计。
                // 由 StatsEngine 捕获并隔离为 .corrupt。
                string firstLine = reader.ReadLine();
                if (firstLine != null)
                {
                    string trimmedFirst = firstLine.TrimStart('\ufeff');
                    if (trimmedFirst.Length > 0 && !trimmedFirst.StartsWith("#", StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("missing KDS1 header; file appears corrupt: " + path);
                    }
                }

                string line = firstLine;
                while (line != null)
                {
                    if (line.Length > 0 && line[0] != '#')
                    {
                        string[] p = line.Split('\t');
                        if (p.Length >= 2)
                        {
                            int dayKey;
                            if (int.TryParse(p[1], NumberStyles.Integer, Inv, out dayKey))
                            {
                                DayData day = GetDay(md, dayKey);
                                ParseRecord(p, day, md);
                            }
                        }
                    }
                    line = reader.ReadLine();
                }
            }

            return md;
        }

        private static void ParseRecord(string[] p, DayData day, MonthData md)
        {
            switch (p[0])
            {
                case "D":
                    if (p.Length >= 4)
                    {
                        long total, sw;
                        long.TryParse(p[2], NumberStyles.Integer, Inv, out total);
                        long.TryParse(p[3], NumberStyles.Integer, Inv, out sw);
                        day.Total += total;
                        day.Swallowed += sw;
                        md.Total += total;
                        md.Swallowed += sw;
                    }
                    if (p.Length >= 6)
                    {
                        int active;
                        if (int.TryParse(p[4], NumberStyles.Integer, Inv, out active)) day.ActiveMinutes = active;
                        ParseHours(p[5], day.Hours);
                    }
                    break;

                case "M":
                    if (p.Length >= 3)
                    {
                        HexToMinutes(p[2], day);
                        day.ActiveMinutes = CountActive(day.Minutes);
                    }
                    break;

                case "K":
                    if (p.Length >= 4)
                    {
                        int vk;
                        long c, s;
                        if (int.TryParse(p[2], NumberStyles.Integer, Inv, out vk) &&
                            long.TryParse(p[3], NumberStyles.Integer, Inv, out c))
                        {
                            long.TryParse(p.Length >= 5 ? p[4] : "0", NumberStyles.Integer, Inv, out s);
                            day.Keys[vk] = new KeyAgg(c, s);
                        }
                    }
                    break;

                case "A":
                    if (p.Length >= 5)
                    {
                        string app = p[2];
                        long total, sw;
                        long.TryParse(p[3], NumberStyles.Integer, Inv, out total);
                        long.TryParse(p[4], NumberStyles.Integer, Inv, out sw);
                        AppAgg agg = GetApp(day, app);
                        agg.Total += total;
                        agg.Swallowed += sw;
                        if (p.Length >= 6) ParseHours(p[5], agg.Hours);
                    }
                    break;

                case "AK":
                    if (p.Length >= 5)
                    {
                        string app = p[2];
                        int vk;
                        long c, s;
                        if (int.TryParse(p[3], NumberStyles.Integer, Inv, out vk) &&
                            long.TryParse(p[4], NumberStyles.Integer, Inv, out c))
                        {
                            long.TryParse(p.Length >= 6 ? p[5] : "0", NumberStyles.Integer, Inv, out s);
                            AppAgg agg = GetApp(day, app);
                            agg.Keys[vk] = new KeyAgg(c, s);
                        }
                    }
                    break;

                case "MB":
                    if (p.Length >= 4)
                    {
                        int button;
                        long count;
                        if (int.TryParse(p[2], NumberStyles.Integer, Inv, out button) &&
                            long.TryParse(p[3], NumberStyles.Integer, Inv, out count))
                        {
                            long cur;
                            day.Mouse.TryGetValue(button, out cur);
                            day.Mouse[button] = cur + count;
                        }
                    }
                    break;

                case "AMB":
                    if (p.Length >= 5)
                    {
                        int button;
                        long count;
                        if (int.TryParse(p[3], NumberStyles.Integer, Inv, out button) &&
                            long.TryParse(p[4], NumberStyles.Integer, Inv, out count))
                        {
                            AppAgg agg = GetApp(day, p[2]);
                            long cur;
                            agg.Mouse.TryGetValue(button, out cur);
                            agg.Mouse[button] = cur + count;
                        }
                    }
                    break;
            }
        }

        public static void Save(string path, MonthData md)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string tmp = path + ".tmp";
            using (StreamWriter w = new StreamWriter(tmp, false, new UTF8Encoding(false)))
            {
                w.NewLine = "\n";
                w.WriteLine("# KDS1 month=" + md.Key + " total=" + md.Total + " swallowed=" + md.Swallowed);

                List<int> dayKeys = new List<int>(md.Days.Keys);
                dayKeys.Sort();

                foreach (int dk in dayKeys)
                {
                    DayData d = md.Days[dk];
                    w.Write("D\t");
                    w.Write(dk.ToString(Inv));
                    w.Write('\t');
                    w.Write(d.Total.ToString(Inv));
                    w.Write('\t');
                    w.Write(d.Swallowed.ToString(Inv));
                    w.Write('\t');
                    w.Write(d.ActiveMinutes.ToString(Inv));
                    w.Write('\t');
                    w.WriteLine(HoursToText(d.Hours));

                    w.Write("M\t");
                    w.Write(dk.ToString(Inv));
                    w.Write('\t');
                    w.WriteLine(MinutesToHex(d.Minutes));

                    List<int> vks = new List<int>(d.Keys.Keys);
                    vks.Sort();
                    foreach (int vk in vks)
                    {
                        KeyAgg k = d.Keys[vk];
                        w.Write("K\t");
                        w.Write(dk.ToString(Inv));
                        w.Write('\t');
                        w.Write(vk.ToString(Inv));
                        w.Write('\t');
                        w.Write(k.Count.ToString(Inv));
                        w.Write('\t');
                        w.WriteLine(k.Swallowed.ToString(Inv));
                    }

                    List<int> mouseButtons = new List<int>(d.Mouse.Keys);
                    mouseButtons.Sort();
                    foreach (int button in mouseButtons)
                    {
                        w.Write("MB\t");
                        w.Write(dk.ToString(Inv));
                        w.Write('\t');
                        w.Write(button.ToString(Inv));
                        w.Write('\t');
                        w.WriteLine(d.Mouse[button].ToString(Inv));
                    }

                    List<string> apps = new List<string>(d.Apps.Keys);
                    apps.Sort(StringComparer.OrdinalIgnoreCase);
                    foreach (string app in apps)
                    {
                        AppAgg a = d.Apps[app];
                        w.Write("A\t");
                        w.Write(dk.ToString(Inv));
                        w.Write('\t');
                        w.Write(app);
                        w.Write('\t');
                        w.Write(a.Total.ToString(Inv));
                        w.Write('\t');
                        w.Write(a.Swallowed.ToString(Inv));
                        w.Write('\t');
                        w.WriteLine(HoursToText(a.Hours));
                    }

                    foreach (string app in apps)
                    {
                        AppAgg a = d.Apps[app];
                        List<int> ak = new List<int>(a.Keys.Keys);
                        ak.Sort();
                        foreach (int vk in ak)
                        {
                            KeyAgg k = a.Keys[vk];
                            w.Write("AK\t");
                            w.Write(dk.ToString(Inv));
                            w.Write('\t');
                            w.Write(app);
                            w.Write('\t');
                            w.Write(vk.ToString(Inv));
                            w.Write('\t');
                            w.Write(k.Count.ToString(Inv));
                            w.Write('\t');
                            w.WriteLine(k.Swallowed.ToString(Inv));
                        }

                        List<int> amb = new List<int>(a.Mouse.Keys);
                        amb.Sort();
                        foreach (int button in amb)
                        {
                            w.Write("AMB\t");
                            w.Write(dk.ToString(Inv));
                            w.Write('\t');
                            w.Write(app);
                            w.Write('\t');
                            w.Write(button.ToString(Inv));
                            w.Write('\t');
                            w.WriteLine(a.Mouse[button].ToString(Inv));
                        }
                    }
                }
            }

            if (File.Exists(path))
            {
                string backup = path + ".bak";
                bool done = false;
                for (int i = 0; i < 4 && !done; i++)
                {
                    try
                    {
                        File.Replace(tmp, path, backup);
                        done = true;
                    }
                    catch
                    {
                        try
                        {
                            File.Copy(tmp, path, true);
                            done = true;
                        }
                        catch
                        {
                            System.Threading.Thread.Sleep(40);
                        }
                    }
                }
                if (!done) throw new IOException("Could not replace " + path);
                try { if (File.Exists(backup)) File.Delete(backup); } catch { }
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
            else
            {
                File.Move(tmp, path);
            }
        }

        public static void ReadHeader(string path, out long total, out long swallowed)
        {
            total = 0;
            swallowed = 0;
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader r = new StreamReader(fs, Encoding.UTF8, true))
                {
                    string line = r.ReadLine();
                    if (line == null) return;
                    string[] parts = line.Split(' ');
                    foreach (string part in parts)
                    {
                        if (part.StartsWith("total=", StringComparison.Ordinal))
                        {
                            long.TryParse(part.Substring(6), NumberStyles.Integer, Inv, out total);
                        }
                        else if (part.StartsWith("swallowed=", StringComparison.Ordinal))
                        {
                            long.TryParse(part.Substring(10), NumberStyles.Integer, Inv, out swallowed);
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private static DayData GetDay(MonthData md, int dayKey)
        {
            DayData day;
            if (!md.Days.TryGetValue(dayKey, out day))
            {
                day = new DayData();
                day.Day = dayKey;
                md.Days[dayKey] = day;
            }
            return day;
        }

        private static AppAgg GetApp(DayData day, string name)
        {
            AppAgg agg;
            if (!day.Apps.TryGetValue(name, out agg))
            {
                agg = new AppAgg(name);
                day.Apps[name] = agg;
            }
            return agg;
        }

        private static void ParseHours(string text, int[] hours)
        {
            string[] parts = text.Split(',');
            int n = Math.Min(24, parts.Length);
            for (int i = 0; i < n; i++)
            {
                int v;
                if (int.TryParse(parts[i], NumberStyles.Integer, Inv, out v)) hours[i] = v;
            }
        }

        private static string HoursToText(int[] hours)
        {
            StringBuilder sb = new StringBuilder(96);
            for (int i = 0; i < 24; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(hours[i].ToString(Inv));
            }
            return sb.ToString();
        }

        private static string MinutesToHex(bool[] minutes)
        {
            byte[] bytes = new byte[180];
            for (int i = 0; i < 1440; i++)
            {
                if (minutes[i]) bytes[i >> 3] |= (byte)(1 << (i & 7));
            }
            StringBuilder sb = new StringBuilder(360);
            for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }

        private static void HexToMinutes(string hex, DayData day)
        {
            if (hex == null) return;
            int count = Math.Min(180, hex.Length / 2);
            for (int i = 0; i < count; i++)
            {
                int b;
                if (!int.TryParse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, Inv, out b)) continue;
                for (int bit = 0; bit < 8; bit++)
                {
                    int minute = (i * 8) + bit;
                    if (minute < 1440 && (b & (1 << bit)) != 0) day.Minutes[minute] = true;
                }
            }
        }

        private static int CountActive(bool[] minutes)
        {
            int n = 0;
            for (int i = 0; i < minutes.Length; i++) if (minutes[i]) n++;
            return n;
        }
    }
}
