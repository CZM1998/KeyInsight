using System;
using System.Collections.Generic;
using System.IO;
using KeyInsight.Adapters.Display;
using KeyInsight.Core.Devices;
using KeyInsight.Storage.DeviceStore;

namespace KeyInsight.Tests.Storage
{
    /// <summary>
    /// Device Center 单元测试（方案 §13.3 身份/生命周期/存储域；不依赖真实设备）：
    /// - DC001/002 身份识别：同型号不自动合并；精确匹配高置信度；
    /// - DC003 事件合并防抖（quiet window / settle / max coalesce）；
    /// - DC004 生命周期状态机；
    /// - DC005/008 档案与诊断存储往返 + 损坏隔离；
    /// - DC006 事件日志追加/解析；
    /// - DC007 EDID 解析（合成字节）。
    /// </summary>
    internal static class DeviceCenterTests
    {
        public static void Register()
        {
            TestRegistry.Add("Storage", "DC001_IdentitySameModelNotMerged", DC001);
            TestRegistry.Add("Storage", "DC002_IdentityExactMatch", DC002);
            TestRegistry.Add("Storage", "DC003_CoalescerQuietWindow", DC003);
            TestRegistry.Add("Storage", "DC004_LifecycleTransitions", DC004);
            TestRegistry.Add("Storage", "DC005_DeviceStoreRoundTrip", DC005);
            TestRegistry.Add("Storage", "DC006_EventLogRoundTrip", DC006);
            TestRegistry.Add("Storage", "DC007_EdidParseSynthetic", DC007);
            TestRegistry.Add("Storage", "DC008_DiagnosticStoreRoundTrip", DC008);
        }

        private static DeviceSnapshot Snapshot(string instanceKey, string vendor, string product,
            string containerId, DeviceCategory category)
        {
            DeviceSnapshot s = new DeviceSnapshot();
            s.InstanceKey = instanceKey;
            s.Category = category;
            s.IsPresent = true;
            s.Name = "Test Device";
            s.ContainerId = containerId;
            if (!string.IsNullOrEmpty(vendor))
            {
                s.AttributesJson = "{\"edidVendor\":\"" + vendor + "\",\"edidProduct\":\"" + product + "\"}";
            }
            return s;
        }

        private static void DC001()
        {
            // 两台同型号显示器（EDID 厂商/产品一致、无序列号）：身份键必须不同，且不得自动合并
            string key1 = DeviceIdentityResolver.BuildIdentityKey(
                Snapshot("DISPLAY\\AAA111\\1&0001", "DEL", "4011", null, DeviceCategory.Display));
            string key2 = DeviceIdentityResolver.BuildIdentityKey(
                Snapshot("DISPLAY\\AAA111\\1&0002", "DEL", "4011", null, DeviceCategory.Display));
            Check.True(key1 != key2, "same-model displays must not share identity key");

            DeviceRecord existing = new DeviceRecord();
            existing.DeviceId = "dev1";
            existing.Category = DeviceCategory.Display;
            existing.IdentityKey = key1;
            existing.Model = "DELL U2415";
            List<DeviceRecord> records = new List<DeviceRecord>();
            records.Add(existing);

            DeviceIdentityResolver.MatchResult m = DeviceIdentityResolver.Match(
                records, Snapshot("DISPLAY\\AAA111\\1&0002", "DEL", "4011", null, DeviceCategory.Display), key2);
            Check.True(!m.AutoMerge, "same-model match must never auto-merge");
            Check.True(m.Confidence < DeviceIdentityResolver.AutoMergeThreshold, "low confidence expected");
        }

        private static void DC002()
        {
            DeviceSnapshot s = Snapshot("DISPLAY\\AAA111\\1&0001", "DEL", "4011", null, DeviceCategory.Display);
            string key = DeviceIdentityResolver.BuildIdentityKey(s);
            DeviceRecord existing = new DeviceRecord();
            existing.DeviceId = "dev1";
            existing.Category = DeviceCategory.Display;
            existing.IdentityKey = key;
            List<DeviceRecord> records = new List<DeviceRecord>();
            records.Add(existing);
            DeviceIdentityResolver.MatchResult m = DeviceIdentityResolver.Match(records, s, key);
            Check.True(m.AutoMerge, "exact identity match should auto-merge");
            Check.Equal("身份键完全一致", m.Reasons[0], "reason recorded");
        }

        private static void DC003()
        {
            DeviceEventCoalescer c = new DeviceEventCoalescer();
            DateTime t0 = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
            c.Feed("k1", t0);
            c.Feed("k1", t0.AddMilliseconds(100));
            Check.Equal(0, c.PollReady(t0.AddMilliseconds(300)).Count,
                "quiet window not elapsed");
            Check.Equal(0, c.PollReady(t0.AddMilliseconds(400)).Count,
                "settle delay not elapsed");
            List<string> ready = c.PollReady(t0.AddMilliseconds(400 + 1200));
            Check.Equal(1, ready.Count, "quiet+settled fires once");
            Check.Equal(0, c.PendingCount, "key removed after firing");

            // 持续事件风暴：不超过 MaxCoalesce（3s）必须放行一次，防止永不刷新
            DeviceEventCoalescer c2 = new DeviceEventCoalescer();
            int fired = 0;
            for (int i = 0; i < 20; i++)
            {
                c2.Feed("storm", t0.AddMilliseconds(i * 250));
                if (c2.PollReady(t0.AddMilliseconds(i * 250)).Count > 0) fired++;
            }
            Check.True(fired >= 1, "storm must fire at least once via max-coalesce cap");
        }

        private static void DC004()
        {
            Check.True(DeviceLifecycle.CanTransition(LifecyclePhase.Connected, LifecyclePhase.Disconnected),
                "connected → disconnected");
            Check.True(DeviceLifecycle.CanTransition(LifecyclePhase.Disconnected, LifecyclePhase.Connected),
                "reconnect allowed");
            Check.True(DeviceLifecycle.CanTransition(LifecyclePhase.Connected, LifecyclePhase.Archived),
                "user archive allowed");
            Check.True(DeviceLifecycle.CanTransition(LifecyclePhase.Archived, LifecyclePhase.Connected),
                "user restore allowed");
            Check.True(!DeviceLifecycle.CanTransition(LifecyclePhase.Discovered, LifecyclePhase.Degraded),
                "discovered → degraded is not a valid jump");
            Check.True(!DeviceLifecycle.CanTransition(LifecyclePhase.Disconnected, LifecyclePhase.ResolvingIdentity),
                "disconnected → resolving not allowed");
        }

        private static void DC005()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ki-dc-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string path = Path.Combine(dir, "devices.v1.json");
                DeviceStore store = new DeviceStore(path);
                DeviceRecord r = new DeviceRecord();
                r.DeviceId = "id-1";
                r.IdentityKey = "display:DEL.4011:ser:123";
                r.Category = DeviceCategory.Display;
                r.Name = "DELL U2415";
                r.UserAlias = "主力屏";
                r.Connection = ConnectionState.Connected;
                r.Health = HealthState.Unknown;
                r.InstanceKeys.Add("DISPLAY\\AAA\\1&1");
                store.Upsert(r);

                DeviceStore reloaded = new DeviceStore(path);
                Check.Equal(1, reloaded.Count, "record persisted");
                DeviceRecord got = reloaded.Get("id-1");
                Check.True(got != null, "record found");
                Check.Equal("display:DEL.4011:ser:123", got.IdentityKey, "identity key round trip");
                Check.Equal("主力屏", got.UserAlias, "alias round trip");
                Check.Equal(1L, (long)got.InstanceKeys.Count, "instance keys round trip");

                // 损坏文件：空库 + .corrupt 副本，绝不抛异常
                File.WriteAllText(path, "{ this is not json !!!");
                DeviceStore broken = new DeviceStore(path);
                Check.Equal(0, broken.Count, "corrupt store loads empty");
                Check.True(File.Exists(path + ".corrupt"), "corrupt copy kept");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static void DC006()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ki-dc-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string path = Path.Combine(dir, "device-events.v1.jsonl");
                DeviceEventLog log = new DeviceEventLog(path);
                DeviceEventItem e1 = new DeviceEventItem();
                e1.EventType = DeviceEventType.Connected;
                e1.DeviceId = "id-1";
                e1.InstanceKey = "k1";
                e1.Category = DeviceCategory.UsbHub;
                e1.TimeUtc = new DateTime(2026, 11, 4, 8, 0, 0, DateTimeKind.Utc);
                e1.EvidenceJson = "{\"problemCode\":28}";
                log.Append(e1);
                DeviceEventItem e2 = new DeviceEventItem();
                e2.EventType = DeviceEventType.DisplayModeChanged;
                e2.DeviceId = "id-2";
                e2.InstanceKey = "k2";
                e2.Category = DeviceCategory.Display;
                e2.TimeUtc = new DateTime(2026, 11, 4, 9, 30, 0, DateTimeKind.Utc);
                log.Append(e2);

                DeviceEventLog reloaded = new DeviceEventLog(path);
                Check.Equal(2, reloaded.Count, "events persisted");
                List<DeviceEventItem> recent = reloaded.Recent(1);
                Check.Equal(1, recent.Count, "recent count");
                Check.Equal((long)DeviceEventType.DisplayModeChanged, (long)recent[0].EventType, "newest first");
                Check.Equal("id-2", recent[0].DeviceId, "device id round trip");
                Check.True(string.IsNullOrEmpty(recent[0].EvidenceJson), "no evidence → empty");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static void DC007()
        {
            byte[] edid = new byte[128];
            // 固定头
            edid[0] = 0x00;
            for (int i = 1; i <= 6; i++) edid[i] = 0xFF;
            edid[7] = 0x00;
            // 厂商 DEL：D=4 E=5 L=12 → bits
            int v = (4 << 10) | (5 << 5) | 12;
            edid[8] = (byte)((v >> 8) | 0x80);   // bit15=1（厂商码固定头）
            edid[9] = (byte)(v & 0xFF);
            edid[10] = 0x11; edid[11] = 0x2F;    // product 0x112F
            edid[12] = 0x78; edid[13] = 0x56; edid[14] = 0x34; edid[15] = 0x12; // serial 0x12345678
            edid[16] = 21; edid[17] = 26;        // 2016 年第 26 周
            // 首选时序：1920x1080（偏移按 EDID 规范：56/57 高 4 位，58/60 低 8 位）
            edid[56] = 0x70;                      // hactive 高 4 位 = 7
            edid[57] = 0x40;                      // vactive 高 4 位 = 4
            edid[58] = 0x80;                      // hactive 低 8 位 → 1920
            edid[60] = 0x38;                      // vactive 低 8 位 → 1080
            // 标准时序：一个 1280x1024@60（ratio 10:16 → 0）
            edid[26] = 0x81;                      // (1280/8)-31 = 129 = 0x81
            edid[27] = 0x00;                      // ratio 16:10, refresh 60

            EdidParser.EdidInfo info = EdidParser.Parse(edid);
            Check.True(info.Valid, "synthetic EDID valid");
            Check.Equal("DEL", info.Vendor, "vendor letters");
            Check.Equal(0x2F11, info.ProductCode, "product code (little-endian)");
            Check.Equal(0x12345678L, (long)info.SerialNumber, "serial");
            Check.Equal(2016, info.ManufactureYear, "year");
            Check.Equal(1920, info.NativeWidth, "native width from preferred timing");
            Check.Equal(1080, info.NativeHeight, "native height");
            Check.True(info.StandardTimings.Count == 1, "one standard timing slot used");
        }

        private static void DC008()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ki-dc-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string path = Path.Combine(dir, "diagnostics.v1.json");
                DiagnosticStore store = new DiagnosticStore(path);
                DiagnosticSession s = new DiagnosticSession();
                s.SessionId = "sess-1";
                s.DeviceId = "dev-1";
                s.DeviceName = "DELL U2415";
                s.TestType = "display.checkup";
                s.StartedAtUtc = DateTime.UtcNow.AddMinutes(-2);
                s.EndedAtUtc = DateTime.UtcNow;
                s.Status = DiagnosticRunState.Completed;
                DiagnosticFinding f = new DiagnosticFinding();
                f.FindingId = "find-1";
                f.DiagnosticCode = "display.dead_pixel.user_observed";
                f.Severity = FindingSeverity.Attention;
                f.Status = FindingStatus.UserConfirmed;
                f.Confidence = null;   // 用户确认类不伪造置信度
                f.EvidenceSource = EvidenceSource.UserConfirmed;
                f.EvidenceJson = "{\"pattern\":\"纯红\"}";
                f.Recommendation = "复核";
                f.CreatedAtUtc = DateTime.UtcNow;
                List<DiagnosticFinding> findings = new List<DiagnosticFinding>();
                findings.Add(f);
                store.AddSession(s, findings);

                DiagnosticStore reloaded = new DiagnosticStore(path);
                Check.Equal(1, reloaded.Sessions.Count, "session persisted");
                List<DiagnosticFinding> got = reloaded.FindingsForDevice("dev-1");
                Check.Equal(1, got.Count, "finding persisted");
                Check.True(!got[0].Confidence.HasValue, "user-confirmed confidence stays null");
                Check.Equal("sess-1", got[0].SessionId, "session id linked");
                Check.True(reloaded.UpdateFindingStatus("find-1", FindingStatus.Resolved),
                    "status update");
                Check.True(reloaded.FindingsForDevice("dev-1")[0].ResolvedAtUtc.HasValue,
                    "resolved time stamped");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
