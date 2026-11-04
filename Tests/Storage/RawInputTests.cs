using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using KeyInsight.Hook;
using KeyInsight.Stats;

namespace KeyInsight.Tests.Storage
{
    /// <summary>
    /// RAW001：Raw Input 端到端冒烟 —— 启动真实 RawInputTracker，
    /// 用 SendInput 注入鼠标移动，验证 WM_INPUT 管线能计入设备事件。
    /// （注入输入在 Raw Input 栈中同样产生 WM_INPUT；设备可能记为 unknown，但必须计数。）
    /// </summary>
    internal static class RawInputTests
    {
        public static void Register()
        {
            TestRegistry.Add("Storage", "RAW001_TrackerReceivesSendInput", RAW001);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public MOUSEINPUT mi;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        private const uint INPUT_MOUSE = 0;
        private const uint MOUSEEVENTF_MOVE = 0x0001;

        private static void RAW001()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ki-raw-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            RawInputTracker tracker = null;
            try
            {
                tracker = new RawInputTracker(dir);
                bool started = tracker.Start();
                Check.True(started, "tracker starts (window + RegisterRawInputDevices ok); error=" + (tracker.LastStartError ?? ""));

                // 注入少量鼠标移动（dx=0 不动光标但产生移动事件；多试几次确保队列送达）
                INPUT[] batch = new INPUT[1];
                batch[0].type = INPUT_MOUSE;
                batch[0].mi.dx = 1;
                batch[0].mi.dy = 0;
                batch[0].mi.dwFlags = MOUSEEVENTF_MOVE;
                for (int i = 0; i < 8; i++)
                {
                    SendInput(1, batch, Marshal.SizeOf(typeof(INPUT)));
                    batch[0].mi.dx = -batch[0].mi.dx;
                    Thread.Sleep(40);
                }
                // 给消息线程处理时间
                for (int i = 0; i < 20 && tracker.DeviceCount == 0; i++) Thread.Sleep(50);

                List<DeviceProfile> profiles = tracker.SnapshotProfiles();
                Check.True(profiles.Count >= 1,
                    "WM_INPUT received (devices=" + profiles.Count + ")");
                Check.True(profiles[0].EventCount > 0, "event counted");
                tracker.SaveDeviceStats();
                Check.True(File.Exists(Path.Combine(dir, "devices.v1.json")), "sidecar written");
            }
            finally
            {
                if (tracker != null)
                {
                    try { tracker.Dispose(); } catch { }
                }
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
