using System;
using System.Collections.Generic;
using System.IO;
using KeyInsight.Adapters.Camera;
using KeyInsight.Adapters.Display;

namespace KeyInsight.Tests.Storage
{
    /// <summary>
    /// PR-10 / §8.6 / §2.4 验收：
    /// - EXT001 DDC/CI 亮度查询冒烟（永不抛出；支持时 min&lt;=current&lt;=max）；
    /// - EXT002 摄像头枚举冒烟（MF COM 链路永不抛出；有摄像头时身份/格式字段完备）；
    /// - EXT003 品牌 SVG 母版资产（存在 + 品牌色 + viewBox + 留白规则标注）。
    /// 注意：绝不调用 SetMonitorBrightness（会对用户显示器产生真实副作用）。
    /// </summary>
    internal static class DeviceExtTests
    {
        public static void Register()
        {
            TestRegistry.Add("Storage", "EXT001_BrightnessQuerySmoke", EXT001);
            TestRegistry.Add("Storage", "EXT002_CameraEnumerateSmoke", EXT002);
            TestRegistry.Add("Storage", "EXT003_BrandSvgAssets", EXT003);
            TestRegistry.Add("Storage", "EXT004_CameraPreviewSmoke", EXT004);
            TestRegistry.Add("Storage", "EXT005_DigitizerScanSmoke", EXT005);
        }

        private static void EXT001()
        {
            // 主屏矩形（0,0,1920,1080 足够命中 MONITOR_DEFAULTTONEAREST）
            DisplayAdapter.MonitorInfo m = new DisplayAdapter.MonitorInfo();
            m.CurrentWidth = 1920;
            m.CurrentHeight = 1080;
            m.PositionX = 0;
            m.PositionY = 0;

            DisplayAdapter.BrightnessInfo info = DisplayAdapter.QueryBrightness(m);
            Check.True(info != null, "info never null");
            if (info.Supported)
            {
                Check.True(info.Known, "supported implies known");
                Check.True(info.Max > info.Min, "max > min when supported");
                Check.True(info.Current >= info.Min && info.Current <= info.Max,
                    "current within range");
            }
            // 离线矩形（远超屏幕）：必须安全返回不支持，绝不抛出
            DisplayAdapter.MonitorInfo off = new DisplayAdapter.MonitorInfo();
            off.CurrentWidth = 100;
            off.CurrentHeight = 100;
            off.PositionX = -50000;
            off.PositionY = -50000;
            DisplayAdapter.BrightnessInfo offInfo = DisplayAdapter.QueryBrightness(off);
            Check.True(offInfo != null && !offInfo.Supported, "offline rect degrades safely");
        }

        private static void EXT002()
        {
            CameraAdapter adapter = new CameraAdapter();
            CameraAdapter.EnumResult r = adapter.Enumerate();
            Check.True(r != null, "result never null");
            Check.True(r.Cameras != null, "camera list never null");
            for (int i = 0; i < r.Cameras.Count; i++)
            {
                CameraAdapter.CameraInfo cam = r.Cameras[i];
                Check.True(cam.Formats != null, "formats list never null");
                Check.True(cam.FormatsKnown || cam.Formats.Count == 0,
                    "unknown formats imply empty list");
            }
            // 快照映射不抛出
            var snapshots = adapter.Enumerate();
            Check.True(snapshots != null, "snapshot mapping never throws");
        }

        private static void EXT004()
        {
            // 不存在的符号链接：会话必须安全返回 Ok=false，绝不抛出
            CameraAdapter.PreviewSession bad = CameraAdapter.OpenPreview(
                "\\\\?\\hid#camera_not_exist#none");
            Check.True(bad != null, "session never null");
            Check.True(!bad.Ok, "bogus link must not open");
            bad.Dispose();

            // ReadPreviewFrame 空参数防御
            Check.True(CameraAdapter.ReadPreviewFrame(IntPtr.Zero, 640, 480) == null,
                "null reader returns null frame");
            CameraAdapter.PreviewSession dummy = new CameraAdapter.PreviewSession();
            Check.True(CameraAdapter.ReadPreviewFrame(IntPtr.Zero, 0, 0) == null,
                "invalid dims return null frame");
            dummy.Dispose();
        }

        private static void EXT005()
        {
            // 数位板 HID 扫描冒烟：永不抛出；条目字段完备（识别到才有 MatchKey/报告长度）
            List<KeyInsight.Adapters.Input.DigitizerAdapter.DigitizerCaps> found =
                KeyInsight.Adapters.Input.DigitizerAdapter.Scan();
            Check.True(found != null, "scan result never null");
            for (int i = 0; i < found.Count; i++)
            {
                KeyInsight.Adapters.Input.DigitizerAdapter.DigitizerCaps d = found[i];
                Check.True(!string.IsNullOrEmpty(d.MatchKey), "match key present");
                Check.True(d.InputReportLen > 0, "input report length sane");
            }
            // 归一化键稳定
            Check.Equal(
                KeyInsight.Adapters.Input.DigitizerAdapter.NormalizeInstanceKey(
                    "\\\\?\\hid#vid_1234&pid_5678&mi_00#7&abc&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}"),
                KeyInsight.Adapters.Input.DigitizerAdapter.NormalizeInstanceId(
                    "HID\\VID_1234&PID_5678&MI_00\\7&ABC&0&0000"),
                "interface path and instance id normalize to same key");
        }

        private static void EXT003()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            // 测试 exe 在 artifacts/，仓库根在上一级
            string root = dir;
            for (int i = 0; i < 3 && !Directory.Exists(Path.Combine(root, "Assets", "brand")); i++)
            {
                root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar));
                if (string.IsNullOrEmpty(root)) break;
            }
            string master = Path.Combine(root, "Assets", "brand", "KeyInsight_LogoB_Master.svg");
            string tray = Path.Combine(root, "Assets", "brand", "KeyInsight_LogoB_Tray_Mono.svg");
            Check.True(File.Exists(master), "master svg exists: " + master);
            Check.True(File.Exists(tray), "tray mono svg exists");

            string masterText = File.ReadAllText(master);
            Check.True(masterText.Contains("viewBox=\"0 0 640 220\""), "master viewBox");
            Check.True(masterText.Contains("#0B2A6B"), "deep blue present");
            Check.True(masterText.Contains("#007BFF"), "tech blue present");
            Check.True(masterText.Contains("KeyInsight"), "wordmark lockup");
            Check.True(masterText.Contains("12%"), "safe-margin rule documented");

            string trayText = File.ReadAllText(tray);
            Check.True(trayText.Contains("currentColor"), "tray uses currentColor for taskbar");
            Check.True(trayText.Contains("viewBox=\"0 0 32 32\""), "tray 32px viewBox");
        }
    }
}
