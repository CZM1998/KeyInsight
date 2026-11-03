using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using KeyInsight.Stats;

namespace KeyInsight.Hook
{
    /// <summary>
    /// Raw Input 多设备统计（P3，仅观察）：用 message-only 窗口注册键盘/鼠标 Raw Input，
    /// 按设备（HID 接口路径哈希）累计事件数。只做统计，不参与任何过滤决策
    /// （WH_KEYBOARD_LL 不提供可靠的来源设备，设备级过滤是独立研究项，ADR-005）。
    /// 设备统计持久化到 data/v2/devices.v1.json（累计值）。
    /// </summary>
    public sealed class RawInputTracker : IDisposable
    {
        /// <summary>设备集合变化回调（路线图 PR-1：设备数据版本）。</summary>
        public Action DeviceSetChanged;
        private const int WM_INPUT = 0x00FF;
        private const int WM_QUIT = 0x0012;
        private const uint RID_INPUT = 0x10000003;
        private const uint RIDI_DEVICENAME = 0x20000007;
        private const uint RIDEV_INPUTSINK = 0x00000100;
        private const ushort HID_USAGE_PAGE_GENERIC = 0x01;
        private const ushort HID_USAGE_GENERIC_KEYBOARD = 0x06;
        private const ushort HID_USAGE_GENERIC_MOUSE = 0x02;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName,
            uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern ushort RegisterClass(ref WNDCLASS wc);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint numDevices, uint size);

        [DllImport("user32.dll")]
        private static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data, ref uint size);

        private delegate IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASS
        {
            public uint style;
            public WndProc lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTHEADER
        {
            public uint dwType;
            public uint dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        private readonly string _v2Dir;
        private Thread _thread;
        private uint _threadId;
        private IntPtr _hwnd;
        private WndProc _wndProc;
        private volatile bool _running;
        private readonly Dictionary<string, DeviceProfile> _devices = new Dictionary<string, DeviceProfile>();

        public RawInputTracker(string v2Dir)
        {
            _v2Dir = v2Dir;
            LoadDeviceStats();
        }

        public bool IsRunning { get { return _running && _hwnd != IntPtr.Zero; } }

        public bool Start()
        {
            if (_running) return true;
            _running = true;
            _thread = new Thread(ThreadProc);
            _thread.IsBackground = true;
            _thread.Name = "KeyInsightRawInput";
            _thread.Start();
            for (int i = 0; i < 100 && _hwnd == IntPtr.Zero && _running; i++) Thread.Sleep(10);
            return _hwnd != IntPtr.Zero;
        }

        private void ThreadProc()
        {
            try
            {
                _threadId = GetCurrentThreadId();
                _wndProc = WndProcImpl;
                string cls = "KeyInsightRawInputWnd_" + System.Diagnostics.Process.GetCurrentProcess().Id;
                WNDCLASS wc = new WNDCLASS();
                wc.lpfnWndProc = _wndProc;
                wc.hInstance = GetModuleHandle(null);
                wc.lpszClassName = cls;
                RegisterClass(ref wc);

                // HWND_MESSAGE (-3) 的 message-only 窗口
                _hwnd = CreateWindowEx(0, cls, "KeyInsightRawInput", 0, 0, 0, 0, 0,
                    new IntPtr(-3), IntPtr.Zero, wc.hInstance, IntPtr.Zero);
                if (_hwnd == IntPtr.Zero)
                {
                    _running = false;
                    return;
                }

                RAWINPUTDEVICE[] devices = new RAWINPUTDEVICE[2];
                devices[0].usUsagePage = HID_USAGE_PAGE_GENERIC;
                devices[0].usUsage = HID_USAGE_GENERIC_KEYBOARD;
                devices[0].dwFlags = RIDEV_INPUTSINK;
                devices[0].hwndTarget = _hwnd;
                devices[1].usUsagePage = HID_USAGE_PAGE_GENERIC;
                devices[1].usUsage = HID_USAGE_GENERIC_MOUSE;
                devices[1].dwFlags = RIDEV_INPUTSINK;
                devices[1].hwndTarget = _hwnd;
                if (!RegisterRawInputDevices(devices, 2, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE))))
                {
                    // 注册失败：功能降级，不影响键盘钩子
                    DestroyWindow(_hwnd);
                    _hwnd = IntPtr.Zero;
                    _running = false;
                    return;
                }

                MSG msg;
                while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }
            catch
            {
            }
            finally
            {
                if (_hwnd != IntPtr.Zero)
                {
                    DestroyWindow(_hwnd);
                    _hwnd = IntPtr.Zero;
                }
                _running = false;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);

        private IntPtr WndProcImpl(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_INPUT)
            {
                try { HandleRawInput(lParam); }
                catch { }
            }
            return DefWindowProc(hwnd, msg, wParam, lParam);
        }

        private void HandleRawInput(IntPtr hRawInput)
        {
            uint size = 0;
            GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER)));
            if (size == 0 || size > 4096) return;

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                uint read = GetRawInputData(hRawInput, RID_INPUT, buffer, ref size,
                    (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER)));
                if (read != size) return;
                RAWINPUTHEADER header = (RAWINPUTHEADER)Marshal.PtrToStructure(buffer, typeof(RAWINPUTHEADER));
                // RIM_TYPEMOUSE=0, RIM_TYPEKEYBOARD=1
                string kind = header.dwType == 1 ? "keyboard" : "mouse";
                string rawName = DeviceIdOf(header.hDevice);
                string id = rawName + " (" + kind + ")";
                lock (_devices)
                {
                    DeviceProfile profile;
                    if (!_devices.TryGetValue(id, out profile))
                    {
                        profile = new DeviceProfile();
                        profile.DeviceId = rawName;
                        profile.Kind = kind;
                        ParseVidPid(rawName, out profile.VendorId, out profile.ProductId);
                        profile.FirstSeenUtcTicks = DateTime.UtcNow.Ticks;
                        _devices[id] = profile;
                    }
                    bool isNew = profile.EventCount == 0;
                    profile.EventCount++;
                    profile.LastSeenUtcTicks = DateTime.UtcNow.Ticks;
                    if (isNew && DeviceSetChanged != null)
                    {
                        try { DeviceSetChanged(); } catch { }
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static string DeviceIdOf(IntPtr hDevice)
        {

            try
            {
                uint size = 0;
                GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref size);
                if (size == 0 || size > 1024) return "unknown";
                IntPtr buf = Marshal.AllocHGlobal((int)size * 2);
                try
                {
                    if (GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, buf, ref size) <= 0) return "unknown";
                    string name = Marshal.PtrToStringUni(buf) ?? "";
                    if (name.Length > 160) name = name.Substring(0, 160);
                    return name.Length == 0 ? "unknown" : name;
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
            catch
            {
                return "unknown";
            }
        }

        /// <summary>设备画像快照（按事件数降序）。</summary>
        public List<DeviceProfile> SnapshotProfiles()
        {

            lock (_devices)
            {
                List<DeviceProfile> list = new List<DeviceProfile>(_devices.Values);
                list.Sort(delegate(DeviceProfile a, DeviceProfile b)
                {
                    return b.EventCount.CompareTo(a.EventCount);
                });
                return list;
            }
        }

        public int DeviceCount
        {
            get { lock (_devices) { return _devices.Count; } }
        }

        /// <summary>从接口路径解析 VID_xxxx / PID_xxxx（无则空串）。</summary>
        internal static void ParseVidPid(string path, out string vid, out string pid)
        {
            vid = ExtractHexTag(path, "VID_");
            pid = ExtractHexTag(path, "PID_");
        }

        private static string ExtractHexTag(string text, string tag)
        {
            if (string.IsNullOrEmpty(text)) return "";
            int idx = text.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (idx < 0 || idx + tag.Length + 4 > text.Length) return "";
            return text.Substring(idx, tag.Length + 4).ToUpperInvariant();
        }

        /// <summary>累计值持久化（设备画像侧车，schema=2；兼容读取旧 v1 计数格式）。</summary>
        public void SaveDeviceStats()
        {
            try
            {
                if (!Directory.Exists(_v2Dir)) Directory.CreateDirectory(_v2Dir);
                StringBuilder sb = new StringBuilder();
                sb.Append("{\"schema\":2,\"savedUtc\":\"").Append(DateTime.UtcNow.ToString("u")).Append("\",\"devices\":[");
                lock (_devices)
                {
                    bool first = true;
                    foreach (KeyValuePair<string, DeviceProfile> kv in _devices)
                    {
                        DeviceProfile d = kv.Value;
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append("{\"id\":\"").Append(JsonEscape(d.DeviceId))
                          .Append("\",\"kind\":\"").Append(JsonEscape(d.Kind))
                          .Append("\",\"vid\":\"").Append(JsonEscape(d.VendorId))
                          .Append("\",\"pid\":\"").Append(JsonEscape(d.ProductId))
                          .Append("\",\"count\":").Append(d.EventCount)
                          .Append(",\"firstSeen\":").Append(d.FirstSeenUtcTicks)
                          .Append(",\"lastSeen\":").Append(d.LastSeenUtcTicks)
                          .Append('}');
                    }
                }
                sb.Append("]}");
                string path = Path.Combine(_v2Dir, "devices.v1.json");
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        private static string JsonEscape(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private void LoadDeviceStats()
        {
            try
            {
                string path = Path.Combine(_v2Dir, "devices.v1.json");
                if (!File.Exists(path)) return;
                string text = File.ReadAllText(path);
                int schema = 1;
                int schemaIdx = text.IndexOf("\"schema\":", StringComparison.Ordinal);
                if (schemaIdx >= 0)
                {
                    int start = schemaIdx + 9;
                    int endPos = start;
                    while (endPos < text.Length && char.IsDigit(text[endPos])) endPos++;
                    int.TryParse(text.Substring(start, endPos - start), out schema);
                }
                if (schema >= 2) LoadProfiles(text);
                else LoadLegacyCounts(text);
            }
            catch
            {
            }
        }

        private void LoadProfiles(string text)
        {
            int arrIdx = text.IndexOf("\"devices\":[", StringComparison.Ordinal);
            if (arrIdx < 0) return;
            string body = text.Substring(arrIdx + 11);
            int i = 0;
            while (i < body.Length)
            {
                int objStart = body.IndexOf('{', i);
                if (objStart < 0) break;
                int objEnd = body.IndexOf('}', objStart);
                if (objEnd < 0) break;
                string obj = body.Substring(objStart + 1, objEnd - objStart - 1);
                DeviceProfile d = new DeviceProfile();
                d.DeviceId = ExtractString(obj, "\"id\":");
                d.Kind = ExtractString(obj, "\"kind\":");
                d.VendorId = ExtractString(obj, "\"vid\":");
                d.ProductId = ExtractString(obj, "\"pid\":");
                d.EventCount = ExtractLong(obj, "\"count\":");
                d.FirstSeenUtcTicks = ExtractLong(obj, "\"firstSeen\":");
                d.LastSeenUtcTicks = ExtractLong(obj, "\"lastSeen\":");
                if (d.DeviceId.Length > 0)
                {
                    _devices[d.DeviceId + " (" + d.Kind + ")"] = d;
                }
                i = objEnd + 1;
            }
        }

        private void LoadLegacyCounts(string text)
        {
            int start = text.IndexOf("\"devices\":{", StringComparison.Ordinal);
            if (start < 0) return;
            string body = text.Substring(start + 11).TrimEnd('}');
            int i = 0;
            while (i < body.Length)
            {
                int q1 = body.IndexOf('"', i);
                if (q1 < 0) break;
                int q2 = body.IndexOf('"', q1 + 1);
                if (q2 < 0) break;
                int colon = body.IndexOf(':', q2);
                if (colon < 0) break;
                int endPos = colon + 1;
                while (endPos < body.Length && char.IsDigit(body[endPos])) endPos++;
                long count;
                long.TryParse(body.Substring(colon + 1, endPos - colon - 1), out count);
                string legacyId = body.Substring(q1 + 1, q2 - q1 - 1);
                DeviceProfile d = new DeviceProfile();
                d.DeviceId = legacyId;
                d.Kind = legacyId.EndsWith("(keyboard)", StringComparison.OrdinalIgnoreCase) ? "keyboard" : "mouse";
                d.EventCount = count;
                d.LastSeenUtcTicks = DateTime.UtcNow.Ticks;
                _devices[legacyId] = d;
                int comma = body.IndexOf(',', endPos);
                i = comma < 0 ? body.Length : comma + 1;
            }
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

        private static long ExtractLong(string body, string marker)
        {
            int idx = body.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return 0;
            int start = idx + marker.Length;
            int endPos = start;
            while (endPos < body.Length && char.IsDigit(body[endPos])) endPos++;
            long v;
            long.TryParse(body.Substring(start, endPos - start), out v);
            return v;
        }

        public void Stop()
        {
            _running = false;
            if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            if (_thread != null)
            {
                try { _thread.Join(1500); } catch { }
                _thread = null;
            }
        }

        public void Dispose()
        {
            Stop();
            SaveDeviceStats();
        }
    }
}
