using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using KeyInsight.Core.Devices;

namespace KeyInsight.Storage.DeviceStore
{
    /// <summary>
    /// 设备事件摘要日志（方案 §10.2 DeviceEvents）：device-events.v1.jsonl 追加写，
    /// 按「最近 3000 条」滚动截断；只保存有意义的变化（合并器之后的事件）。
    /// </summary>
    public sealed class DeviceEventLog
    {
        private const int MaxEvents = 3000;
        private readonly string _path;
        private readonly object _gate = new object();
        private List<DeviceEventItem> _cache;

        public DeviceEventLog(string path)
        {
            _path = path;
            Load();
        }

        public List<DeviceEventItem> Recent(int count)
        {
            lock (_gate)
            {
                int n = Math.Min(count, _cache.Count);
                List<DeviceEventItem> result = new List<DeviceEventItem>(n);
                for (int i = _cache.Count - n; i < _cache.Count; i++) result.Add(_cache[i]);
                return result;
            }
        }

        public int Count
        {
            get { lock (_gate) return _cache.Count; }
        }

        public void Append(DeviceEventItem e)
        {
            if (e == null) return;
            lock (_gate)
            {
                _cache.Add(e);
                if (_cache.Count > MaxEvents)
                {
                    _cache.RemoveRange(0, _cache.Count - MaxEvents);
                    Rewrite();
                    return;
                }
                try
                {
                    EnsureDir();
                    File.AppendAllText(_path, Serialize(e) + Environment.NewLine,
                        new UTF8Encoding(false));
                }
                catch
                {
                }
            }
        }

        private static string Serialize(DeviceEventItem e)
        {
            StringBuilder sb = new StringBuilder(200);
            sb.Append("{\"type\":").Append((int)e.EventType)
              .Append(",\"deviceId\":\"").Append(Escape(e.DeviceId))
              .Append("\",\"instanceKey\":\"").Append(Escape(e.InstanceKey))
              .Append("\",\"category\":").Append((int)e.Category)
              .Append(",\"timeUtcTicks\":").Append(e.TimeUtc.Ticks);
            if (!string.IsNullOrEmpty(e.EvidenceJson))
            {
                sb.Append(",\"evidence\":").Append(e.EvidenceJson);
            }
            sb.Append('}');
            return sb.ToString();
        }

        private void Rewrite()
        {
            try
            {
                EnsureDir();
                StringBuilder sb = new StringBuilder(_cache.Count * 160);
                for (int i = 0; i < _cache.Count; i++)
                {
                    sb.Append(Serialize(_cache[i])).Append(Environment.NewLine);
                }
                File.WriteAllText(_path, sb.ToString(), new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        private void EnsureDir()
        {
            string dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }

        private void Load()
        {
            _cache = new List<DeviceEventItem>();
            try
            {
                if (!File.Exists(_path)) return;
                string[] lines = File.ReadAllLines(_path);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length < 3) continue;
                    DeviceEventItem e = Parse(line);
                    if (e != null) _cache.Add(e);
                }
                if (_cache.Count > MaxEvents)
                {
                    _cache.RemoveRange(0, _cache.Count - MaxEvents);
                }
            }
            catch
            {
                _cache = new List<DeviceEventItem>();
            }
        }

        /// <summary>极简平铺 JSON 解析（与写入格式一一对应，避免引重序列化器）。</summary>
        internal static DeviceEventItem Parse(string line)
        {
            DeviceEventItem e = new DeviceEventItem();
            long ticks = LongField(line, "timeUtcTicks");
            e.TimeUtc = ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : DateTime.UtcNow;
            e.EventType = (DeviceEventType)(int)LongField(line, "type");
            e.Category = (DeviceCategory)(int)LongField(line, "category");
            e.DeviceId = StringField(line, "deviceId");
            e.InstanceKey = StringField(line, "instanceKey");
            int ei = line.IndexOf("\"evidence\":", StringComparison.Ordinal);
            if (ei >= 0)
            {
                int start = line.IndexOf('{', ei);
                if (start >= 0)
                {
                    int depth = 0;
                    for (int i = start; i < line.Length; i++)
                    {
                        if (line[i] == '{') depth++;
                        else if (line[i] == '}')
                        {
                            depth--;
                            if (depth == 0)
                            {
                                e.EvidenceJson = line.Substring(start, i - start + 1);
                                break;
                            }
                        }
                    }
                }
            }
            return e;
        }

        private static long LongField(string json, string field)
        {
            string needle = "\"" + field + "\":";
            int i = json.IndexOf(needle, StringComparison.Ordinal);
            if (i < 0) return 0;
            i += needle.Length;
            int j = i;
            while (j < json.Length && json[j] >= '0' && json[j] <= '9') j++;
            long v;
            return j > i && long.TryParse(json.Substring(i, j - i), out v) ? v : 0;
        }

        private static string StringField(string json, string field)
        {
            string needle = "\"" + field + "\":\"";
            int i = json.IndexOf(needle, StringComparison.Ordinal);
            if (i < 0) return "";
            i += needle.Length;
            StringBuilder sb = new StringBuilder();
            for (int j = i; j < json.Length; j++)
            {
                char c = json[j];
                if (c == '\\' && j + 1 < json.Length) { sb.Append(json[j + 1]); j++; continue; }
                if (c == '"') break;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
