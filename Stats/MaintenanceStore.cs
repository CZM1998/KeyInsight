using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace KeyInsight.Stats
{
    public sealed class MaintenanceEvent
    {
        public long TimeUtcTicks;
        public int Vk;
        public string Action;    // clean / switch / replace-device / retest / note
        public string Note;

        public DateTime LocalTime
        {
            get { return new DateTime(TimeUtcTicks, DateTimeKind.Utc).ToLocalTime(); }
        }

        public string ActionText
        {
            get
            {
                switch (Action)
                {
                    case "clean": return "清洁触点";
                    case "switch": return "更换轴体";
                    case "replace-device": return "更换键盘";
                    case "retest": return "复测";
                    default: return "备注";
                }
            }
        }
    }

    /// <summary>
    /// 维护事件存储（P2-02）：data/maintenance/maintenance.v1.jsonl，追加写、按日轮转可选（此处单文件）。
    /// 只存事件元数据，绝不存原始键序列。前后对比由 UI 用同一键的拦截趋势在事件时间两侧取样比较。
    /// </summary>
    public static class MaintenanceStore
    {
        private const int SchemaVersion = 1;

        public static void Append(string dir, MaintenanceEvent ev)
        {
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "maintenance.v1.jsonl");
                StringBuilder sb = new StringBuilder();
                sb.Append("{\"schema\":").Append(SchemaVersion)
                    .Append(",\"timeUtcTicks\":").Append(ev.TimeUtcTicks.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"vk\":").Append(ev.Vk.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"action\":\"").Append(Escape(ev.Action)).Append('"');
                if (!string.IsNullOrEmpty(ev.Note))
                {
                    sb.Append(",\"note\":\"").Append(Escape(ev.Note)).Append('"');
                }
                sb.AppendLine("}");
                File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        public static List<MaintenanceEvent> Load(string dir)
        {
            List<MaintenanceEvent> list = new List<MaintenanceEvent>();
            try
            {
                string path = Path.Combine(dir, "maintenance.v1.jsonl");
                if (!File.Exists(path)) return list;
                foreach (string line in File.ReadAllLines(path))
                {
                    if (line.Length == 0) continue;
                    MaintenanceEvent ev = new MaintenanceEvent();
                    ev.TimeUtcTicks = ExtractLong(line, "\"timeUtcTicks\":");
                    ev.Vk = (int)ExtractLong(line, "\"vk\":");
                    ev.Action = ExtractString(line, "\"action\":");
                    ev.Note = ExtractString(line, "\"note\":");
                    if (ev.TimeUtcTicks > 0) list.Add(ev);
                }
                list.Sort(delegate(MaintenanceEvent a, MaintenanceEvent b) { return a.TimeUtcTicks.CompareTo(b.TimeUtcTicks); });
            }
            catch
            {
            }
            return list;
        }

        public sealed class BeforeAfter
        {
            public long Before7d;
            public long After7d;
            public bool HasBefore;
            public bool HasAfter;
            public string Verdict = "样本不足，暂无法比较";
        }

        /// <summary>维修前后对比（P2-02）：以事件时间为界，用拦截趋势（30 天窗口）取两侧 7 天样本。
        /// 样本量不同时给出显式警告，不做伪精确百分比。</summary>
        public static BeforeAfter Compare(int vk, MaintenanceEvent ev, StatsSnapshot snap)
        {
            BeforeAfter r = new BeforeAfter();
            int[] trend;
            if (!snap.KeySwallowTrend.TryGetValue(vk, out trend)) return r;
            DateTime today = DateTime.Today;
            int eventDayIndex = (int)(today - ev.LocalTime.Date).Days; // 0 = 今天，29 = 29 天前
            if (eventDayIndex < 0 || eventDayIndex > 29) return r;

            long before = 0;
            long after = 0;
            for (int i = 1; i <= 7; i++)
            {
                int bi = eventDayIndex - i;
                if (bi >= 0 && bi < trend.Length) { before += trend[bi]; r.HasBefore = true; }
                int ai = eventDayIndex + i;
                if (ai >= 0 && ai < trend.Length && ai <= (int)(today - snap.KeyTrendStart).Days) { after += trend[ai]; r.HasAfter = true; }
            }
            r.Before7d = before;
            r.After7d = after;

            if (!r.HasBefore || !r.HasAfter)
            {
                r.Verdict = "事件两侧不足 7 天样本，暂无法比较（需更多时间积累）";
            }
            else if (after < before)
            {
                r.Verdict = "维护后 7 天拦截 " + after + " 次 / 前 7 天 " + before + " 次 —— 有改善（同条件观察）";
            }
            else if (after > before)
            {
                r.Verdict = "维护后 7 天拦截 " + after + " 次 / 前 7 天 " + before + " 次 —— 未改善，建议复测";
            }
            else
            {
                r.Verdict = "前后 7 天拦截次数相同（" + before + "），继续观察";
            }
            return r;
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

        private static string ExtractString(string body, string marker)
        {
            int idx = body.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return "";
            int q1 = body.IndexOf('"', idx + marker.Length);
            if (q1 < 0) return "";
            int q2 = body.IndexOf('"', q1 + 1);
            if (q2 < 0) return "";
            return body.Substring(q1 + 1, q2 - q1 - 1);
        }

        private static string Escape(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
