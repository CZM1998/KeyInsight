using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace KeyInsight.Stats
{
    /// <summary>
    /// 前台应用活跃时长（类似手机屏幕使用时间，方案外新增用户需求）：
    /// - 每秒采样前台应用，累计到「日期 → 应用 → 秒」；系统空闲 ≥120s 暂停累计（活跃使用语义）；
    /// - 只记应用名与时长，绝不记录窗口标题、输入文本（隐私边界不变）；
    /// - 存储：data/app-time/v1.json（原子写，保留 400 天）；与 KDS/设备中心数据域隔离；
    /// - 聚合逻辑（AddSeconds/SumRange/Prune）为纯静态函数，可单元测试。
    /// </summary>
    public static class AppTimeTracker
    {
        private const int IdlePauseMs = 120000;      // 空闲超过 2 分钟不计入活跃时长
        private const int SaveEveryTicks = 60;       // 每 60 秒落盘一次
        private const int RetainDays = 400;

        private static readonly object _gate = new object();
        private static readonly Dictionary<string, Dictionary<string, long>> _days =
            new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
        private static Timer _timer;
        private static string _path;
        private static int _ticksSinceSave;
        private static volatile bool _running;

        /// <summary>启动采样（幂等）。dataRoot 为空则用 exe 目录下 data。</summary>
        public static void Start(string dataRoot)
        {
            if (_running) return;
            try
            {
                string dir = Path.Combine(string.IsNullOrEmpty(dataRoot)
                    ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data")
                    : dataRoot, "app-time");
                _path = Path.Combine(dir, "v1.json");
                Load();
                PruneAll(DateTime.Today, RetainDays);
                _running = true;
                _timer = new Timer(Tick, null, 1000, 1000);
            }
            catch
            {
            }
        }

        public static void Stop()
        {
            if (!_running) return;
            _running = false;
            try
            {
                if (_timer != null)
                {
                    _timer.Dispose();
                    _timer = null;
                }
            }
            catch { }
            try { Save(); } catch { }
        }

        public static bool Running { get { return _running; } }

        private static void Tick(object state)
        {
            try
            {
                string app = ForegroundTracker.CurrentApp;
                if (string.IsNullOrEmpty(app)) return;
                if (IdleMilliseconds() >= IdlePauseMs) return;   // 空闲暂停（活跃使用语义）

                lock (_gate)
                {
                    string day = DateTime.Today.ToString("yyyy-MM-dd");
                    Dictionary<string, long> apps;
                    if (!_days.TryGetValue(day, out apps))
                    {
                        apps = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                        _days[day] = apps;
                    }
                    long v;
                    apps.TryGetValue(app, out v);
                    apps[app] = v + 1;
                }
                _ticksSinceSave++;
                if (_ticksSinceSave >= SaveEveryTicks)
                {
                    _ticksSinceSave = 0;
                    Save();
                }
            }
            catch
            {
            }
        }

        // —— 纯聚合函数（单元测试覆盖） ——

        /// <summary>向内存聚合加秒数（日期键 yyyy-MM-dd）。</summary>
        public static void AddSeconds(Dictionary<string, Dictionary<string, long>> days,
            DateTime day, string app, long seconds)
        {
            if (string.IsNullOrEmpty(app) || seconds <= 0) return;
            string key = day.ToString("yyyy-MM-dd");
            Dictionary<string, long> apps;
            if (!days.TryGetValue(key, out apps))
            {
                apps = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                days[key] = apps;
            }
            long v;
            apps.TryGetValue(app, out v);
            apps[app] = v + seconds;
        }

        /// <summary>区间聚合（闭区间日期），返回 app → 秒。start/end 为 DateTime.MinValue/MaxValue 表示不限。</summary>
        public static Dictionary<string, long> SumRange(
            Dictionary<string, Dictionary<string, long>> days, DateTime start, DateTime end)
        {
            Dictionary<string, long> result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Dictionary<string, long>> day in days)
            {
                DateTime d;
                if (!DateTime.TryParseExact(day.Key, "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out d))
                {
                    continue;
                }
                if (d < start.Date || d > end.Date) continue;
                foreach (KeyValuePair<string, long> app in day.Value)
                {
                    long v;
                    result.TryGetValue(app.Key, out v);
                    result[app.Key] = v + app.Value;
                }
            }
            return result;
        }

        /// <summary>按秒数降序的应用排行（最多 max 项）。</summary>
        public static List<KeyValuePair<string, long>> TopApps(
            Dictionary<string, long> totals, int max)
        {
            List<KeyValuePair<string, long>> list = new List<KeyValuePair<string, long>>(totals);
            list.Sort(delegate(KeyValuePair<string, long> a, KeyValuePair<string, long> b)
            {
                if (a.Value != b.Value) return b.Value.CompareTo(a.Value);
                return string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
            });
            if (list.Count > max) list.RemoveRange(max, list.Count - max);
            return list;
        }

        public static long TotalSeconds(Dictionary<string, long> totals)
        {
            long sum = 0;
            foreach (long v in totals.Values) sum += v;
            return sum;
        }

        /// <summary>丢弃早于 keepDays 的日期键。</summary>
        public static void PruneAll(DateTime today, int keepDays)
        {
            DateTime cutoff = today.AddDays(-keepDays);
            List<string> drop = new List<string>();
            foreach (string key in _days.Keys)
            {
                DateTime d;
                if (!DateTime.TryParseExact(key, "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out d)) continue;
                if (d < cutoff.Date) drop.Add(key);
            }
            for (int i = 0; i < drop.Count; i++) _days.Remove(drop[i]);
        }

        /// <summary>当前区间聚合（线程安全快照）。</summary>
        public static Dictionary<string, long> GetRange(DateTime start, DateTime end)
        {
            lock (_gate)
            {
                return SumRange(_days, start, end);
            }
        }

        public static string StoragePath { get { return _path; } }

        // —— 持久化（单文件原子写） ——

        private static void Load()
        {
            try
            {
                if (string.IsNullOrEmpty(_path) || !File.Exists(_path)) return;
                Dictionary<string, Dictionary<string, long>> parsed = ParseJson(File.ReadAllText(_path));
                if (parsed == null) return;
                lock (_gate)
                {
                    _days.Clear();
                    foreach (KeyValuePair<string, Dictionary<string, long>> kv in parsed)
                    {
                        _days[kv.Key] = kv.Value;
                    }
                }
            }
            catch
            {
            }
        }

        private static void Save()
        {
            try
            {
                if (string.IsNullOrEmpty(_path)) return;
                string json;
                lock (_gate)
                {
                    json = BuildJson(_days);
                }
                string dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                if (File.Exists(_path))
                {
                    try { File.Replace(tmp, _path, null); }
                    catch { File.Copy(tmp, _path, true); File.Delete(tmp); }
                }
                else
                {
                    File.Move(tmp, _path);
                }
            }
            catch
            {
            }
        }

        /// <summary>解析存储 JSON（测试与巡检用）。</summary>
        internal static Dictionary<string, Dictionary<string, long>> ParseJson(string json)
        {
            return new JavaScriptSerializerLite().Parse(json);
        }

        internal static string BuildJson(Dictionary<string, Dictionary<string, long>> days)
        {
            StringBuilder sb = new StringBuilder(1024);
            sb.Append("{\"schema\":1,\"days\":{");
            bool firstDay = true;
            foreach (KeyValuePair<string, Dictionary<string, long>> kv in days)
            {
                if (kv.Value.Count == 0) continue;
                if (!firstDay) sb.Append(',');
                firstDay = false;
                sb.Append('"').Append(kv.Key).Append("\":{");
                bool firstApp = true;
                foreach (KeyValuePair<string, long> app in kv.Value)
                {
                    if (!firstApp) sb.Append(',');
                    firstApp = false;
                    sb.Append('"').Append(Escape(app.Key)).Append("\":").Append(app.Value);
                }
                sb.Append('}');
            }
            sb.Append("}}");
            return sb.ToString();
        }

        private static string Escape(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        /// <summary>极简 JSON 解析（结构固定：{"schema":1,"days":{"date":{"app":seconds}}}）。</summary>
        private sealed class JavaScriptSerializerLite
        {
            public Dictionary<string, Dictionary<string, long>> Parse(string json)
            {
                int daysIdx = json.IndexOf("\"days\"", StringComparison.Ordinal);
                if (daysIdx < 0) return null;
                int i = json.IndexOf('{', daysIdx);
                if (i < 0) return null;
                Dictionary<string, Dictionary<string, long>> result =
                    new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
                i++;
                while (i < json.Length)
                {
                    SkipWs(json, ref i);
                    if (i >= json.Length || json[i] == '}') break;
                    if (json[i] != '"') { i++; continue; }
                    string day = ReadString(json, ref i);
                    SkipWs(json, ref i);
                    if (i >= json.Length || json[i] != ':') break;
                    i++;
                    SkipWs(json, ref i);
                    if (i >= json.Length || json[i] != '{') break;
                    i++;
                    Dictionary<string, long> apps =
                        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                    while (i < json.Length)
                    {
                        SkipWs(json, ref i);
                        if (json[i] == '}') { i++; break; }
                        if (json[i] != '"') { i++; continue; }
                        string app = ReadString(json, ref i);
                        SkipWs(json, ref i);
                        if (i >= json.Length || json[i] != ':') break;
                        i++;
                        SkipWs(json, ref i);
                        int start = i;
                        while (i < json.Length && (char.IsDigit(json[i]) || json[i] == '-')) i++;
                        long v;
                        if (long.TryParse(json.Substring(start, i - start), out v)) apps[app] = v;
                        SkipWs(json, ref i);
                        if (i < json.Length && json[i] == ',') i++;
                    }
                    result[day] = apps;
                    SkipWs(json, ref i);
                    if (i < json.Length && json[i] == ',') i++;
                }
                return result;
            }

            private static void SkipWs(string s, ref int i)
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            }

            private static string ReadString(string s, ref int i)
            {
                StringBuilder sb = new StringBuilder();
                i++;    // 跳过开引号
                while (i < s.Length)
                {
                    char c = s[i];
                    if (c == '\\' && i + 1 < s.Length) { sb.Append(s[i + 1]); i += 2; continue; }
                    if (c == '"') { i++; break; }
                    sb.Append(c);
                    i++;
                }
                return sb.ToString();
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [DllImport("kernel32.dll")]
        private static extern uint GetTickCount();

        /// <summary>系统空闲毫秒数（无输入时长）。</summary>
        private static long IdleMilliseconds()
        {
            try
            {
                LASTINPUTINFO info = new LASTINPUTINFO();
                info.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
                if (!GetLastInputInfo(ref info)) return 0;
                uint now = GetTickCount();
                return now >= info.dwTime ? now - info.dwTime : 0;
            }
            catch
            {
                return 0;
            }
        }
    }
}
