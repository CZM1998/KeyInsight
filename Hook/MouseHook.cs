using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using KeyInsight.Stats;

namespace KeyInsight.Hook
{
    public sealed class MouseHook : IDisposable
    {
        private const int WH_MOUSE_LL = 14;
        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_RBUTTONDOWN = 0x0204;
        private const uint WM_MBUTTONDOWN = 0x0207;
        private const uint WM_MOUSEWHEEL = 0x020A;
        private const uint WM_XBUTTONDOWN = 0x020B;
        private const uint WM_MOUSEHWHEEL = 0x020E;
        private const uint LLMHF_INJECTED = 0x01;
        private const uint WM_QUIT = 0x0012;

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

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

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        private readonly Config _cfg;
        private readonly StatsEngine _stats;
        private HookProc _proc;
        private IntPtr _hook;
        private Thread _thread;
        private uint _threadId;
        private volatile bool _running;
        private long _events;

        public MouseHook(Config cfg, StatsEngine stats)
        {
            _cfg = cfg;
            _stats = stats;
        }

        public long EventCount { get { return Interlocked.Read(ref _events); } }
        public bool IsRunning { get { return _running && _hook != IntPtr.Zero; } }

        public bool Start()
        {
            if (_running) return true;
            if (_thread != null && _thread.IsAlive) return false;
            _running = true;
            _thread = new Thread(ThreadProc);
            _thread.IsBackground = true;
            _thread.Name = "KeyInsightMouseHook";
            _thread.Start();

            for (int i = 0; i < 200 && _hook == IntPtr.Zero && _running; i++) Thread.Sleep(10);
            return _hook != IntPtr.Zero;
        }

        private void ThreadProc()
        {
            _threadId = GetCurrentThreadId();
            _proc = HookCallback;
            IntPtr handle = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
            _hook = handle;
            if (handle == IntPtr.Zero)
            {
                _running = false;
                Log("=== mouse hook failed");
                return;
            }
            Log("=== mouse hook started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            MSG msg;
            while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            UnhookWindowsHookEx(handle);
            _hook = IntPtr.Zero;
            _running = false;
            Log("=== mouse hook stopped " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    uint msg = (uint)wParam.ToInt32();
                    bool isButton = msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN ||
                                    msg == WM_MBUTTONDOWN || msg == WM_XBUTTONDOWN;
                    bool isWheel = msg == WM_MOUSEWHEEL || msg == WM_MOUSEHWHEEL;
                    if (!isButton && !isWheel) return CallNextHookEx(_hook, nCode, wParam, lParam);

                    MSLLHOOKSTRUCT m = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                    if (_cfg.SkipInjected && (m.flags & LLMHF_INJECTED) != 0)
                        return CallNextHookEx(_hook, nCode, wParam, lParam);

                    int button = 0;
                    int delta = 0;

                    if (msg == WM_LBUTTONDOWN) button = MouseIds.Left;
                    else if (msg == WM_RBUTTONDOWN) button = MouseIds.Right;
                    else if (msg == WM_MBUTTONDOWN) button = MouseIds.Middle;
                    else if (msg == WM_XBUTTONDOWN)
                    {
                        int x = (int)((m.mouseData >> 16) & 0xFFFF);
                        if (x == 1) button = MouseIds.Back;
                        else if (x == 2) button = MouseIds.Forward;
                    }
                    else if (msg == WM_MOUSEWHEEL)
                    {
                        int d = (short)((m.mouseData >> 16) & 0xFFFF);
                        int ticks = Math.Abs(d) / 120;
                        if (ticks == 0) ticks = 1;
                        button = d >= 0 ? MouseIds.WheelUp : MouseIds.WheelDown;
                        delta = ticks;
                    }
                    else if (msg == WM_MOUSEHWHEEL)
                    {
                        int d = (short)((m.mouseData >> 16) & 0xFFFF);
                        int ticks = Math.Abs(d) / 120;
                        if (ticks == 0) ticks = 1;
                        button = d >= 0 ? MouseIds.WheelRight : MouseIds.WheelLeft;
                        delta = ticks;
                    }

                    if (button > 0 && _stats != null)
                    {
                        KeyEvent e = new KeyEvent();
                        e.Type = isWheel ? KeyEventType.MouseWheel : KeyEventType.MouseDown;
                        e.Button = button;
                        e.Delta = delta;
                        e.At = DateTime.Now;
                        e.App = ForegroundTracker.CurrentApp;
                        _stats.Enqueue(e);
                        long n = Interlocked.Increment(ref _events);
                        if (n == 1) Log("first event button=" + button + " app=" + e.App);
                    }
                }
            }
            catch
            {
            }

            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        public bool Restart()
        {
            Stop();
            return Start();
        }

        public void Stop()
        {
            _running = false;
            if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            if (_thread != null) _thread.Join(2000);
            if (_thread == null || !_thread.IsAlive)
            {
                _thread = null;
                _threadId = 0;
                _hook = IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private static void Log(string line)
        {
            try
            {
                File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mouse.log"),
                    line + "\r\n");
            }
            catch
            {
            }
        }
    }
}
