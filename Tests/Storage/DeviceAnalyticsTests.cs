using System;
using System.Collections.Generic;
using System.IO;
using KeyInsight.Core.Devices;
using KeyInsight.Services.DeviceCenter;

namespace KeyInsight.Tests.Storage
{
    /// <summary>
    /// 使用分析与导出（方案 PR-12/PR-13 验收，纯逻辑）：
    /// - AN001 连接时长事件对估算（含未闭合区间）；
    /// - AN002 断连批「共同上游候选」分组；
    /// - AN003 序列号脱敏；
    /// - AN004 CSV 清单与 MD 报告生成（脱敏字段核验）；
    /// - AN005 USB 拓扑父子装配；
    /// - AN006 音频端点枚举冒烟（系统 COM，允许零端点）。
    /// </summary>
    internal static class DeviceAnalyticsTests
    {
        public static void Register()
        {
            TestRegistry.Add("Storage", "AN001_UsageDurationFromEventPairs", AN001);
            TestRegistry.Add("Storage", "AN002_DisconnectBatchUpstreamCandidate", AN002);
            TestRegistry.Add("Storage", "AN003_SerialMasking", AN003);
            TestRegistry.Add("Storage", "AN004_CsvAndMarkdownExports", AN004);
            TestRegistry.Add("Storage", "AN005_UsbTopologyAssembly", AN005);
            TestRegistry.Add("Storage", "AN006_AudioEnumerateSmoke", AN006);
        }

        private static DeviceEventItem Ev(DeviceEventType t, string deviceId, DateTime utc)
        {
            DeviceEventItem e = new DeviceEventItem();
            e.EventType = t;
            e.DeviceId = deviceId;
            e.TimeUtc = utc;
            return e;
        }

        private static void AN001()
        {
            DateTime t0 = new DateTime(2026, 11, 4, 8, 0, 0, DateTimeKind.Utc);
            List<DeviceEventItem> events = new List<DeviceEventItem>();
            events.Add(Ev(DeviceEventType.Connected, "d1", t0));
            events.Add(Ev(DeviceEventType.Disconnected, "d1", t0.AddHours(2)));
            events.Add(Ev(DeviceEventType.Connected, "d1", t0.AddHours(3)));
            // 未闭合区间：算到 now（+4h）→ 1 小时
            DateTime now = t0.AddHours(4);
            DeviceAnalytics.UsageSummary s = DeviceAnalytics.Summarize(events, now);
            Check.Equal(2L, s.ConnectCount, "two connects");
            Check.Equal(1L, s.DisconnectCount, "one disconnect");
            Check.Equal(3L, (long)s.TotalConnected.TotalHours, "2h closed + 1h open");
            Check.True(s.LastConnectedUtc.HasValue && s.LastConnectedUtc.Value == t0.AddHours(3),
                "last connect recorded");

            // 乱序防御：Disconnected 早于 Connected 不产生负时长
            List<DeviceEventItem> weird = new List<DeviceEventItem>();
            weird.Add(Ev(DeviceEventType.Disconnected, "d1", t0));
            weird.Add(Ev(DeviceEventType.Connected, "d1", t0));
            DeviceAnalytics.UsageSummary s2 = DeviceAnalytics.Summarize(weird, now);
            Check.True(s2.TotalConnected >= TimeSpan.Zero, "no negative duration");
        }

        private static void AN002()
        {
            DateTime t0 = new DateTime(2026, 11, 4, 8, 0, 0, DateTimeKind.Utc);
            List<DeviceEventItem> events = new List<DeviceEventItem>();
            // 扩展坞断开 → 3 台设备 4 秒内先后消失
            events.Add(Ev(DeviceEventType.Disconnected, "dock", t0));
            events.Add(Ev(DeviceEventType.Disconnected, "mouse", t0.AddSeconds(2)));
            events.Add(Ev(DeviceEventType.Disconnected, "disk", t0.AddSeconds(4)));
            // 单独的孤立断开（不应成批）
            events.Add(Ev(DeviceEventType.Disconnected, "solo", t0.AddHours(1)));

            List<DeviceAnalytics.DisconnectBatch> batches =
                DeviceAnalytics.GroupDisconnects(events, TimeSpan.FromSeconds(5), 3);
            Check.True(batches.Count >= 1, "at least one batch");
            DeviceAnalytics.DisconnectBatch first = batches[batches.Count - 1]; // 时间最早的批
            Check.Equal(3L, (long)first.Items.Count, "three devices grouped");
            Check.True(first.CommonUpstreamCandidate, "marked as common-upstream candidate");
            DeviceAnalytics.DisconnectBatch solo = batches[0];
            Check.True(!solo.CommonUpstreamCandidate, "solo disconnect is not a candidate");
        }

        private static void AN003()
        {
            Check.Equal("****1234", DeviceAnalytics.MaskSerial("ABCD1234"), "keep last 4");
            Check.Equal("****", DeviceAnalytics.MaskSerial("AB"), "short serial fully masked");
            Check.Equal("", DeviceAnalytics.MaskSerial(null), "null serial");
        }

        private static void AN004()
        {
            DeviceRecord r = new DeviceRecord();
            r.DeviceId = "dev1";
            r.IdentityKey = "display:DEL.4011:ser:12345678";
            r.Category = DeviceCategory.Display;
            r.Name = "DELL U2415";
            r.Model = "DELL U2415";
            r.Manufacturer = "DEL";
            r.SerialNumber = "ABCD12345678";
            r.Connection = ConnectionState.Connected;
            List<DeviceRecord> records = new List<DeviceRecord>();
            records.Add(r);

            string csv = DeviceAnalytics.BuildCsv(records, DateTime.UtcNow);
            Check.True(csv.Contains("****5678"), "csv masks serial");
            Check.True(!csv.Contains("ABCD12345678"), "csv must not leak full serial");
            Check.True(csv.Contains("DELL U2415"), "csv contains model");

            string md = DeviceAnalytics.BuildDeviceMarkdown(r,
                new DeviceAnalytics.UsageSummary(), null, null, DateTime.UtcNow);
            Check.True(md.Contains("****5678"), "md masks serial");
            Check.True(!md.Contains("ABCD12345678"), "md must not leak full serial");
            Check.True(md.Contains("设备报告 · DELL U2415"), "md title");

            // 写出与回读
            string dir = Path.Combine(Path.GetTempPath(), "ki-an-" + Guid.NewGuid().ToString("N"));
            try
            {
                string path = DeviceAnalytics.WriteExport(dir, "inventory.csv", csv);
                Check.True(File.Exists(path), "export written");
                Check.True(File.ReadAllText(path).Contains("****5678"), "export content ok");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static void AN005()
        {
            DeviceRecord hub = new DeviceRecord();
            hub.DeviceId = "hub";
            hub.AttributesJson = "{\"instancePath\":\"USB\\\\VID_2109\\\\6\\\\1234\"}";
            DeviceRecord mouse = new DeviceRecord();
            mouse.DeviceId = "mouse";
            mouse.AttributesJson = "{\"instancePath\":\"USB\\\\VID_046D\\\\7\\\\5678\",\"parentKey\":\"USB\\\\VID_2109\\\\6\\\\1234\"}";
            DeviceRecord orphan = new DeviceRecord();
            orphan.DeviceId = "orphan";
            orphan.AttributesJson = "{}";

            List<DeviceRecord> records = new List<DeviceRecord>();
            records.Add(orphan);
            records.Add(mouse);
            records.Add(hub);

            List<DeviceAnalytics.TopoNode> topo = DeviceAnalytics.BuildUsbTopology(records);
            Check.Equal(3L, (long)topo.Count, "all records become nodes");
            int hubDepth = -1, mouseDepth = -1;
            for (int i = 0; i < topo.Count; i++)
            {
                if (topo[i].Record.DeviceId == "hub") hubDepth = topo[i].Depth;
                if (topo[i].Record.DeviceId == "mouse") mouseDepth = topo[i].Depth;
            }
            Check.Equal(0L, (long)hubDepth, "hub at root");
            Check.Equal(1L, (long)mouseDepth, "mouse nested under hub");
        }

        private static void AN006()
        {
            // 冒烟：COM 可用性 + 不抛异常；无音频设备的环境允许 0 端点
            KeyInsight.Adapters.Audio.AudioAdapter adapter =
                new KeyInsight.Adapters.Audio.AudioAdapter();
            KeyInsight.Adapters.Audio.AudioAdapter.AudioResult r = adapter.Enumerate();
            Check.True(r.Endpoints.Count >= 0, "enumerate never throws");
            // 有活动播放设备的机器（含测试机）应至少 1 个 render 端点
            Check.True(r.Endpoints.Count > 0 || r.DefaultRenderId == "",
                "endpoints consistent with defaults");
        }
    }
}
