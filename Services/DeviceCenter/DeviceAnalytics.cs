using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using KeyInsight.Core.Devices;

namespace KeyInsight.Services.DeviceCenter
{
    /// <summary>
    /// 设备使用分析与报告导出（方案 §10.3/§12/PR-12/PR-13，最小可用版）：
    /// - 连接时长估算 = 事件对（Connected→Disconnected）区间求和；未闭合区间算到当前时刻；
    /// - 断连批分析：窗口内多设备同时断开 → 「共同上游变化候选」（§9.2，不下结论）；
    /// - 报告脱敏：序列号只保留末 4 位；
    /// - 导出：设备清单 CSV + 单设备 Markdown 报告，写入 devicecenter/exports。
    /// 全部为纯函数 + 文件写出，可单元测试。
    /// </summary>
    public static class DeviceAnalytics
    {
        public sealed class UsageSummary
        {
            public int ConnectCount;
            public int DisconnectCount;
            public TimeSpan TotalConnected;
            public DateTime? LastConnectedUtc;
            public int PlugCycleEstimate        // 插拔次数估算 = 断开次数（有开有关才有循环）
            {
                get { return DisconnectCount; }
            }
        }

        /// <summary>按事件对估算连接时长。事件需已按时间升序。</summary>
        public static UsageSummary Summarize(IList<DeviceEventItem> eventsForDevice, DateTime nowUtc)
        {
            UsageSummary s = new UsageSummary();
            DateTime? open = null;
            if (eventsForDevice != null)
            {
                for (int i = 0; i < eventsForDevice.Count; i++)
                {
                    DeviceEventItem e = eventsForDevice[i];
                    if (e.EventType == DeviceEventType.Connected)
                    {
                        s.ConnectCount++;
                        s.LastConnectedUtc = e.TimeUtc;
                        open = e.TimeUtc;
                    }
                    else if (e.EventType == DeviceEventType.Disconnected)
                    {
                        s.DisconnectCount++;
                        if (open.HasValue)
                        {
                            if (e.TimeUtc > open.Value)
                            {
                                s.TotalConnected += e.TimeUtc - open.Value;
                            }
                            open = null;
                        }
                    }
                }
            }
            if (open.HasValue)
            {
                DateTime end = nowUtc > open.Value ? nowUtc : open.Value;
                s.TotalConnected += end - open.Value;
            }
            return s;
        }

        public sealed class DisconnectBatch
        {
            public DateTime TimeUtc;
            public readonly List<DeviceEventItem> Items = new List<DeviceEventItem>();
            public bool CommonUpstreamCandidate;    // 共同上游变化候选（仅候选，非结论）
        }

        /// <summary>同一窗口内的多设备断开 → 归为一批，标记「共同上游变化候选」。</summary>
        public static List<DisconnectBatch> GroupDisconnects(IList<DeviceEventItem> events,
            TimeSpan window, int minBatchSize)
        {
            List<DisconnectBatch> batches = new List<DisconnectBatch>();
            if (events == null) return batches;
            List<DeviceEventItem> disc = new List<DeviceEventItem>();
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i].EventType == DeviceEventType.Disconnected) disc.Add(events[i]);
            }
            disc.Sort(delegate(DeviceEventItem a, DeviceEventItem b)
            {
                return a.TimeUtc.CompareTo(b.TimeUtc);
            });

            DisconnectBatch current = null;
            for (int i = 0; i < disc.Count; i++)
            {
                if (current == null || (disc[i].TimeUtc - current.TimeUtc) > window)
                {
                    current = new DisconnectBatch();
                    current.TimeUtc = disc[i].TimeUtc;
                    batches.Add(current);
                }
                current.Items.Add(disc[i]);
                if (disc[i].TimeUtc > current.TimeUtc) current.TimeUtc = disc[i].TimeUtc;
            }
            for (int i = 0; i < batches.Count; i++)
            {
                batches[i].CommonUpstreamCandidate = batches[i].Items.Count >= minBatchSize;
            }
            batches.Reverse();      // 最新批次在前
            return batches;
        }

        /// <summary>序列号脱敏：只保留末 4 位（报告/清单统一口径）。</summary>
        public static string MaskSerial(string serial)
        {
            if (string.IsNullOrEmpty(serial)) return "";
            if (serial.Length <= 4) return "****";
            return "****" + serial.Substring(serial.Length - 4);
        }

        /// <summary>设备清单 CSV（序列号脱敏）。</summary>
        public static string BuildCsv(IList<DeviceRecord> records, DateTime nowUtc)
        {
            StringBuilder sb = new StringBuilder(256 * Math.Max(1, records.Count));
            sb.Append("名称,类别,连接,健康,诊断,型号,制造商,序列号(脱敏),首次记录,最近出现,插拔次数\r\n");
            List<DeviceRecord> sorted = new List<DeviceRecord>(records);
            sorted.Sort(delegate(DeviceRecord a, DeviceRecord b)
            {
                int ca = a.Connection == ConnectionState.Connected ? 0 : 1;
                int cb = b.Connection == ConnectionState.Connected ? 0 : 1;
                if (ca != cb) return ca.CompareTo(cb);
                return b.LastSeenUtc.CompareTo(a.LastSeenUtc);
            });
            for (int i = 0; i < sorted.Count; i++)
            {
                DeviceRecord r = sorted[i];
                sb.Append(Csv(r.UserAlias)).Append(',')
                  .Append(Csv(DeviceCategoryNames.Get(r.Category))).Append(',')
                  .Append(Csv(ConnectionStateNames.Get(r.Connection))).Append(',')
                  .Append(Csv(HealthStateNames.Get(r.Health))).Append(',')
                  .Append(Csv(DiagText(r.Diagnostic))).Append(',')
                  .Append(Csv(r.Model)).Append(',')
                  .Append(Csv(r.Manufacturer)).Append(',')
                  .Append(Csv(MaskSerial(r.SerialNumber))).Append(',')
                  .Append(r.FirstSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")).Append(',')
                  .Append(r.LastSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")).Append(',')
                  .Append(CycleText(r)).Append("\r\n");
            }
            return sb.ToString();
        }

        private static string CycleText(DeviceRecord r)
        {
            string v = KeyInsight.Core.Devices.DeviceIdentityResolver.JsonField(
                r.AttributesJson, "plugCycles");
            return string.IsNullOrEmpty(v) ? "" : v;
        }

        private static string DiagText(DiagnosticRunState s)
        {
            switch (s)
            {
                case DiagnosticRunState.Completed: return "已完成";
                case DiagnosticRunState.Failed: return "已失败";
                case DiagnosticRunState.Running: return "进行中";
                case DiagnosticRunState.Queued: return "排队中";
                default: return "尚未检测";
            }
        }

        private static string Csv(string v)
        {
            if (string.IsNullOrEmpty(v)) return "";
            if (v.IndexOf(',') >= 0 || v.IndexOf('"') >= 0 || v.IndexOf('\n') >= 0)
            {
                return "\"" + v.Replace("\"", "\"\"") + "\"";
            }
            return v;
        }

        /// <summary>单设备 Markdown 报告（序列号脱敏）。</summary>
        public static string BuildDeviceMarkdown(DeviceRecord r, UsageSummary usage,
            IList<DiagnosticFinding> findings, IList<DeviceEventItem> recentEvents, DateTime nowUtc)
        {
            StringBuilder sb = new StringBuilder(2048);
            string name = string.IsNullOrEmpty(r.UserAlias) ? r.Name : r.UserAlias;
            sb.Append("# 设备报告 · ").Append(name).Append("\n\n");
            sb.Append("- 生成时间：").Append(nowUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")).Append("\n");
            sb.Append("- 类别：").Append(DeviceCategoryNames.Get(r.Category)).Append("\n");
            sb.Append("- 连接：").Append(ConnectionStateNames.Get(r.Connection))
              .Append(" · 健康：").Append(HealthStateNames.Get(r.Health))
              .Append(" · 诊断：").Append(DiagText(r.Diagnostic)).Append("\n");
            sb.Append("- 型号：").Append(string.IsNullOrEmpty(r.Model) ? "未知" : r.Model).Append("\n");
            sb.Append("- 制造商：").Append(string.IsNullOrEmpty(r.Manufacturer) ? "未知" : r.Manufacturer).Append("\n");
            sb.Append("- 序列号：").Append(MaskSerial(r.SerialNumber)).Append("（已脱敏）\n");
            sb.Append("- 首次记录：").Append(r.FirstSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
              .Append(" · 最近出现：").Append(r.LastSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")).Append("\n");
            if (usage != null)
            {
                sb.Append("\n## 使用估算\n\n");
                sb.Append("- 插入次数：").Append(usage.ConnectCount).Append(" · 拔出次数：").Append(usage.DisconnectCount).Append("\n");
                sb.Append("- 累计连接时长（估算）：").Append(FormatDuration(usage.TotalConnected)).Append("\n");
                sb.Append("- 最近连接：").Append(usage.LastConnectedUtc.HasValue
                    ? usage.LastConnectedUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "无记录").Append("\n");
            }
            if (findings != null && findings.Count > 0)
            {
                sb.Append("\n## 诊断记录\n\n");
                for (int i = findings.Count - 1; i >= 0 && i >= findings.Count - 8; i--)
                {
                    DiagnosticFinding f = findings[i];
                    sb.Append("- ").Append(f.CreatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm"))
                      .Append(" ").Append(f.DiagnosticCode)
                      .Append(" · ").Append(f.Status == FindingStatus.UserConfirmed ? "用户确认异常"
                        : f.Status == FindingStatus.Resolved ? "已解决" : "候选")
                      .Append("\n");
                }
            }
            if (recentEvents != null && recentEvents.Count > 0)
            {
                sb.Append("\n## 最近变化\n\n");
                int shown = 0;
                for (int i = recentEvents.Count - 1; i >= 0 && shown < 8; i--, shown++)
                {
                    DeviceEventItem e = recentEvents[i];
                    sb.Append("- ").Append(e.TimeUtc.ToLocalTime().ToString("MM-dd HH:mm:ss"))
                      .Append(" ").Append(EventText(e.EventType)).Append("\n");
                }
            }
            sb.Append("\n> 本报告由本地数据生成，序列号已脱敏；未检测 ≠ 设备健康。\n");
            return sb.ToString();
        }

        public static string FormatDuration(TimeSpan t)
        {
            if (t.TotalHours >= 1)
            {
                return ((int)t.TotalHours) + " 小时 " + t.Minutes + " 分";
            }
            if (t.TotalMinutes >= 1)
            {
                return ((int)t.TotalMinutes) + " 分钟";
            }
            return ((int)t.TotalSeconds) + " 秒";
        }

        private static string EventText(DeviceEventType t)
        {
            switch (t)
            {
                case DeviceEventType.Connected: return "已插入";
                case DeviceEventType.Disconnected: return "已拔出";
                case DeviceEventType.DisplayModeChanged: return "模式变化";
                case DeviceEventType.DefaultEndpointChanged: return "默认设备变化";
                case DeviceEventType.AttentionCandidate: return "关注候选";
                case DeviceEventType.HealthChanged: return "健康变化";
                case DeviceEventType.Archived: return "已归档";
                default: return "身份变化";
            }
        }

        /// <summary>把导出写入 devicecenter/exports，返回完整路径。</summary>
        public static string WriteExport(string deviceCenterDir, string fileName, string content)
        {
            string dir = Path.Combine(deviceCenterDir, "exports");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, fileName);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }

        // ---------------------------------------------------------------- USB 拓扑（方案 §9.2）

        public sealed class TopoNode
        {
            public DeviceRecord Record;
            public int Depth;
            public string ParentKey;
        }

        /// <summary>
        /// 从档案属性组装 USB 父子拓扑（PnP parent → 子设备）。节点信息不完整时按根展示，
        /// 不做猜测式补全（§9.2：允许节点信息不完整）。
        /// </summary>
        public static List<TopoNode> BuildUsbTopology(IList<DeviceRecord> records)
        {
            List<TopoNode> nodes = new List<TopoNode>();
            Dictionary<string, int> byPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < records.Count; i++)
            {
                DeviceRecord r = records[i];
                TopoNode n = new TopoNode();
                n.Record = r;
                n.ParentKey = DeviceIdentityResolver.JsonField(r.AttributesJson, "parentKey");
                n.Depth = 0;
                int self = nodes.Count;
                string selfPath = DeviceIdentityResolver.JsonField(r.AttributesJson, "instancePath");
                if (!string.IsNullOrEmpty(selfPath) && !byPath.ContainsKey(selfPath))
                {
                    byPath[selfPath] = self;
                }
                nodes.Add(n);
            }
            // 两遍式：先建索引再挂父子，避免乱序
            for (int i = 0; i < nodes.Count; i++)
            {
                TopoNode n = nodes[i];
                if (string.IsNullOrEmpty(n.ParentKey)) continue;
                int parentIdx;
                if (byPath.TryGetValue(n.ParentKey, out parentIdx) && parentIdx != i)
                {
                    n.Depth = nodes[parentIdx].Depth + 1;
                }
            }
            return nodes;
        }
    }
}
