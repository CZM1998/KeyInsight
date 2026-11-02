using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace KeyInsight.Stats
{
    /// <summary>活跃分钟合并口径（P0-06/P2-04）：键盘分钟 ∪ 鼠标分钟。</summary>
    public static class ActiveMinutesCalculator
    {
        public static int KeyboardCount(DayData day)
        {
            return day == null ? 0 : day.ActiveMinutes;
        }

        public static int MouseCount(DayData day)
        {
            if (day == null) return 0;
            int n = 0;
            for (int i = 0; i < day.MouseMinutes.Length; i++)
            {
                if (day.MouseMinutes[i]) n++;
            }
            return n;
        }

        /// <summary>键盘 ∪ 鼠标的去重活跃分钟数。旧数据（无鼠标位图）自动退化为键盘口径。</summary>
        public static int CombinedCount(DayData day)
        {
            if (day == null) return 0;
            int n = 0;
            for (int i = 0; i < 1440; i++)
            {
                if (day.Minutes[i] || day.MouseMinutes[i]) n++;
            }
            return n;
        }
    }

    /// <summary>
    /// V2 用量侧车（P0-06）：data/v2/usage-YYYY-MM.jsonl，每日一行聚合摘要
    /// （键盘/鼠标/综合活跃分钟）。整体重写、原子替换；不写任何键序列或应用明细。
    /// </summary>
    public static class UsageSidecar
    {
        private const int SchemaVersion = 1;

        public static void Save(string v2Dir, MonthData md)
        {
            try
            {
                if (!Directory.Exists(v2Dir)) Directory.CreateDirectory(v2Dir);
                string path = Path.Combine(v2Dir, "usage-" + md.Key + ".jsonl");
                string tmp = path + ".tmp";

                StringBuilder sb = new StringBuilder();
                List<int> days = new List<int>(md.Days.Keys);
                days.Sort();
                bool wroteAny = false;
                foreach (int dk in days)
                {
                    DayData d = md.Days[dk];
                    int kb = ActiveMinutesCalculator.KeyboardCount(d);
                    int mouse = ActiveMinutesCalculator.MouseCount(d);
                    int combined = ActiveMinutesCalculator.CombinedCount(d);
                    sb.Append("{\"schema\":").Append(SchemaVersion)
                        .Append(",\"month\":\"").Append(md.Key).Append('"')
                        .Append(",\"date\":\"").Append(DayKeys.ToText(dk)).Append('"')
                        .Append(",\"keyboardMinutes\":").Append(kb)
                        .Append(",\"mouseMinutes\":").Append(mouse)
                        .Append(",\"combinedMinutes\":").Append(combined)
                        .Append(",\"keyboardTotal\":").Append(d.Total)
                        .Append(",\"swallowed\":").Append(d.Swallowed)
                        .Append(",\"mouseEvents\":").Append(SumMouse(d.Mouse))
                        .Append(",\"rawWheelDelta\":").Append(d.RawWheelDelta)
                        .Append(",\"rawWheelEvents\":").Append(d.RawWheelEvents)
                        .Append(",\"mouseMinutesHex\":\"").Append(MinutesHex(d.MouseMinutes)).Append('"')
                        .AppendLine("}");
                    wroteAny = true;
                }

                if (!wroteAny)
                {
                    // 无数据（如月份被清空）：删除侧车而不是留空文件，保证与主数据一致（STO-015）
                    try { if (File.Exists(path)) File.Delete(path); } catch { }
                    return;
                }

                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch
            {
                // 侧车失败不影响 KDS1 主链路（只影响 V2 分析数据的新鲜度）
            }
        }

        /// <summary>与 KDS1 M 行相同的分钟位图编码（1440 分钟 → 360 hex 字符）。</summary>
        public static string MinutesHex(bool[] minutes)
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

        /// <summary>读取某月用量侧车中的鼠标分钟位图（日 -> 位图）。缺失/损坏返回 null。</summary>
        public static Dictionary<int, bool[]> TryLoadMouseMinutes(string v2Dir, string monthKey)
        {
            try
            {
                string path = Path.Combine(v2Dir, "usage-" + monthKey + ".jsonl");
                if (!File.Exists(path)) return null;
                Dictionary<int, bool[]> result = new Dictionary<int, bool[]>();
                foreach (string line in File.ReadAllLines(path))
                {
                    int dateIdx = line.IndexOf("\"date\":\"", StringComparison.Ordinal);
                    int hexIdx = line.IndexOf("\"mouseMinutesHex\":\"", StringComparison.Ordinal);
                    if (dateIdx < 0 || hexIdx < 0) continue;
                    int dateStart = dateIdx + 8;
                    string dateText = line.Substring(dateStart, 10);
                    int hexStart = hexIdx + 19;
                    int hexEnd = line.IndexOf('"', hexStart);
                    if (hexEnd < 0) continue;
                    string hex = line.Substring(hexStart, hexEnd - hexStart);
                    int dayKey = DayKeys.From(DateTime.ParseExact(dateText, "yyyy-MM-dd",
                        CultureInfo.InvariantCulture));
                    bool[] minutes = new bool[1440];
                    int count = Math.Min(180, hex.Length / 2);
                    for (int i = 0; i < count; i++)
                    {
                        int b;
                        if (!int.TryParse(hex.Substring(i * 2, 2), NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture, out b)) continue;
                        for (int bit = 0; bit < 8; bit++)
                        {
                            int minute = (i * 8) + bit;
                            if (minute < 1440 && (b & (1 << bit)) != 0) minutes[minute] = true;
                        }
                    }
                    result[dayKey] = minutes;
                }
                return result.Count > 0 ? result : null;
            }
            catch
            {
                return null;
            }
        }

        private static long SumMouse(Dictionary<int, long> mouse)
        {
            long n = 0;
            foreach (KeyValuePair<int, long> kv in mouse) n += kv.Value;
            return n;
        }

    }

    /// <summary>
    /// 诊断侧车（P1-02）：data/diagnostics/kdiag-YYYY-MM.jsonl —— 按日按 VK 的
    /// 拦截间隔直方图摘要 + 算法版本标注。只有聚合，没有原始输入序列。
    /// </summary>
    public static class DiagnosticsSidecar
    {
        private const int SchemaVersion = 1;
        public const int AlgorithmVersion = 1;

        public static void Save(string diagDir, string monthKey, Dictionary<int, Dictionary<int, SwallowGapHist>> hist)
        {
            try
            {
                if (!Directory.Exists(diagDir)) Directory.CreateDirectory(diagDir);
                string path = Path.Combine(diagDir, "kdiag-" + monthKey + ".jsonl");
                string tmp = path + ".tmp";

                StringBuilder sb = new StringBuilder();
                List<int> days = new List<int>(hist.Keys);
                days.Sort();
                bool wroteAny = false;
                foreach (int dk in days)
                {
                    int y = dk / 10000;
                    int m = (dk / 100) % 100;
                    if (DayKeys.MonthKey(y, m) != monthKey) continue;

                    Dictionary<int, SwallowGapHist> perVk = hist[dk];
                    if (perVk.Count == 0) continue;

                    sb.Append("{\"schema\":").Append(SchemaVersion)
                        .Append(",\"type\":\"swallow-gap-hist\"")
                        .Append(",\"algorithmVersion\":").Append(AlgorithmVersion)
                        .Append(",\"date\":\"").Append(DayKeys.ToText(dk)).Append('"')
                        .Append(",\"keys\":{");
                    bool first = true;
                    List<int> vks = new List<int>(perVk.Keys);
                    vks.Sort();
                    foreach (int vk in vks)
                    {
                        SwallowGapHist h = perVk[vk];
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append('"').Append(vk.ToString(CultureInfo.InvariantCulture)).Append("\":{\"n\":")
                            .Append(h.Count)
                            .Append(",\"gd\":").Append(Joins(h.GapDown))
                            .Append(",\"gu\":").Append(Joins(h.GapShortUp))
                            .Append('}');
                    }
                    sb.Append("}}").AppendLine();
                    wroteAny = true;
                }

                if (!wroteAny)
                {
                    try { if (File.Exists(path)) File.Delete(path); } catch { }
                    return;
                }

                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch
            {
            }
        }

        /// <summary>读取某月诊断侧车（P2-01 跨重启模拟用）。损坏返回 null。</summary>
        public static Dictionary<int, Dictionary<int, SwallowGapHist>> TryLoad(string diagDir, string monthKey)
        {
            try
            {
                string path = Path.Combine(diagDir, "kdiag-" + monthKey + ".jsonl");
                if (!File.Exists(path)) return null;
                Dictionary<int, Dictionary<int, SwallowGapHist>> result =
                    new Dictionary<int, Dictionary<int, SwallowGapHist>>();
                foreach (string line in File.ReadAllLines(path))
                {
                    if (line.Length == 0) continue;
                    int dateIdx = line.IndexOf("\"date\":\"", StringComparison.Ordinal);
                    int keysIdx = line.IndexOf("\"keys\":", StringComparison.Ordinal);
                    if (dateIdx < 0 || keysIdx < 0) continue;
                    string dateText = line.Substring(dateIdx + 8, 10);
                    int dayKey = DayKeys.From(DateTime.ParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture));
                    Dictionary<int, SwallowGapHist> perVk = ParseKeysObject(line.Substring(keysIdx + 7));
                    result[dayKey] = perVk;
                }
                return result;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>解析形如 {"45":{"n":3,"gd":[...],"gu":[...]}} 的紧凑对象（自描述格式，不依赖完整 JSON 库）。</summary>
        private static Dictionary<int, SwallowGapHist> ParseKeysObject(string text)
        {
            Dictionary<int, SwallowGapHist> result = new Dictionary<int, SwallowGapHist>();
            int i = 0;
            while (i < text.Length)
            {
                int quote = text.IndexOf('"', i);
                if (quote < 0) break;
                int quoteEnd = text.IndexOf('"', quote + 1);
                if (quoteEnd < 0) break;
                int vk;
                if (!int.TryParse(text.Substring(quote + 1, quoteEnd - quote - 1), out vk))
                {
                    i = quoteEnd + 1;
                    continue;
                }
                int objStart = text.IndexOf('{', quoteEnd);
                if (objStart < 0) break;
                int objEnd = text.IndexOf('}', objStart);
                if (objEnd < 0) break;
                string body = text.Substring(objStart + 1, objEnd - objStart - 1);
                SwallowGapHist h = new SwallowGapHist();
                h.Count = ExtractLong(body, "\"n\":");
                long[] gd = ExtractArray(body, "\"gd\":");
                long[] gu = ExtractArray(body, "\"gu\":");
                if (gd != null) Array.Copy(gd, h.GapDown, Math.Min(gd.Length, h.GapDown.Length));
                if (gu != null) Array.Copy(gu, h.GapShortUp, Math.Min(gu.Length, h.GapShortUp.Length));
                result[vk] = h;
                i = objEnd + 1;
            }
            return result;
        }

        private static long ExtractLong(string body, string marker)
        {
            int idx = body.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return 0;
            int start = idx + marker.Length;
            int end = start;
            while (end < body.Length && (char.IsDigit(body[end]) || (end == start && body[end] == '-'))) end++;
            long v;
            long.TryParse(body.Substring(start, end - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
            return v;
        }

        private static long[] ExtractArray(string body, string marker)
        {
            int idx = body.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return null;
            int start = body.IndexOf('[', idx);
            int end = body.IndexOf(']', start);
            if (start < 0 || end < 0) return null;
            string[] parts = body.Substring(start + 1, end - start - 1).Split(',');
            long[] arr = new long[SwallowGapHist.BucketCount];
            for (int i = 0; i < parts.Length && i < arr.Length; i++)
            {
                long v;
                long.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
                arr[i] = v;
            }
            return arr;
        }

        private static string Joins(long[] buckets)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < buckets.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(buckets[i].ToString(CultureInfo.InvariantCulture));
            }
            sb.Append(']');
            return sb.ToString();
        }
    }

    /// <summary>快捷键聚合侧车（P2-04）：data/v2/shortcuts.v1.json。仅组合名 + 次数。</summary>
    public static class ShortcutStore
    {
        private const int SchemaVersion = 1;

        public static void Save(string v2Dir, Dictionary<string, long> counts)
        {
            try
            {
                if (!Directory.Exists(v2Dir)) Directory.CreateDirectory(v2Dir);
                string path = Path.Combine(v2Dir, "shortcuts.v1.json");
                string tmp = path + ".tmp";
                StringBuilder sb = new StringBuilder();
                sb.Append("{\"schema\":").Append(SchemaVersion)
                    .Append(",\"savedUtc\":\"").Append(DateTime.UtcNow.ToString("u")).Append('"')
                    .Append(",\"counts\":{");
                bool first = true;
                List<string> keys = new List<string>(counts.Keys);
                keys.Sort(StringComparer.Ordinal);
                foreach (string key in keys)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('"').Append(Escape(key)).Append("\":")
                        .Append(counts[key].ToString(CultureInfo.InvariantCulture));
                }
                sb.Append("}}");
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch
            {
            }
        }

        public static Dictionary<string, long> TryLoad(string v2Dir)
        {
            try
            {
                string path = Path.Combine(v2Dir, "shortcuts.v1.json");
                if (!File.Exists(path)) return new Dictionary<string, long>();
                string text = File.ReadAllText(path);
                int countsIdx = text.IndexOf("\"counts\":{", StringComparison.Ordinal);
                if (countsIdx < 0) return new Dictionary<string, long>();
                Dictionary<string, long> result = new Dictionary<string, long>();
                string body = text.Substring(countsIdx + 10).TrimEnd('}');
                int i = 0;
                while (i < body.Length)
                {
                    int q1 = body.IndexOf('"', i);
                    if (q1 < 0) break;
                    int q2 = body.IndexOf('"', q1 + 1);
                    if (q2 < 0) break;
                    int colon = body.IndexOf(':', q2);
                    if (colon < 0) break;
                    int end = colon + 1;
                    while (end < body.Length && (char.IsDigit(body[end]) || (end == colon + 1 && body[end] == '-'))) end++;
                    long v;
                    long.TryParse(body.Substring(colon + 1, end - colon - 1), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out v);
                    result[body.Substring(q1 + 1, q2 - q1 - 1)] = v;
                    int nextComma = body.IndexOf(',', end);
                    i = nextComma < 0 ? body.Length : nextComma + 1;
                }
                return result;
            }
            catch
            {
                return new Dictionary<string, long>();
            }
        }

        private static string Escape(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
