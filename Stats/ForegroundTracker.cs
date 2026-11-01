using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace KeyInsight.Stats
{
    public static class ForegroundTracker
    {
        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const uint WM_QUIT = 0x0012;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

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

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint flags, StringBuilder text, ref uint size);

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

        private static WinEventDelegate _proc;
        private static IntPtr _hook;
        private static Thread _thread;
        private static uint _threadId;
        private static Timer _timer;
        private static volatile bool _running;
        private static volatile string _current = "unknown";
        private static readonly Dictionary<uint, string> _names = new Dictionary<uint, string>();
        private static readonly object _gate = new object();

        public static string CurrentApp
        {
            get
            {
                string s = _current;
                return string.IsNullOrEmpty(s) ? "unknown" : s;
            }
        }

        public static void Start()
        {
            if (_running) return;
            _running = true;

            _thread = new Thread(ThreadProc);
            _thread.IsBackground = true;
            _thread.Name = "KeyInsightForeground";
            _thread.Start();

            _timer = new Timer(Poll, null, 250, 800);
        }

        public static void Stop()
        {
            _running = false;
            if (_timer != null)
            {
                _timer.Dispose();
                _timer = null;
            }
            if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            if (_thread != null)
            {
                _thread.Join(1200);
                _thread = null;
            }
            if (_hook != IntPtr.Zero)
            {
                UnhookWinEvent(_hook);
                _hook = IntPtr.Zero;
            }
            _threadId = 0;
        }

        private static void ThreadProc()
        {
            _threadId = GetCurrentThreadId();
            _proc = Callback;
            _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
                _proc, 0, 0, WINEVENT_OUTOFCONTEXT);

            UpdateFromForeground();

            MSG msg;
            while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            if (_hook != IntPtr.Zero)
            {
                UnhookWinEvent(_hook);
                _hook = IntPtr.Zero;
            }
        }

        private static void Callback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (!_running) return;
            if (hwnd == IntPtr.Zero)
            {
                _current = "unknown";
                return;
            }
            UpdateFromHandle(hwnd);
        }

        private static void Poll(object state)
        {
            if (!_running) return;
            try { UpdateFromForeground(); }
            catch { }
        }

        private static void UpdateFromForeground()
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                _current = "unknown";
                return;
            }
            UpdateFromHandle(hwnd);
        }

        private static void UpdateFromHandle(IntPtr hwnd)
        {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (pid == 0)
            {
                _current = "unknown";
                return;
            }

            string name;
            lock (_gate)
            {
                if (!_names.TryGetValue(pid, out name))
                {
                    name = ReadProcessName(pid);
                    if (name != null) _names[pid] = name;
                }
            }

            _current = name == null ? "unknown" : name;
        }

        private static string ReadProcessName(uint pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                uint size = 1024;
                if (!QueryFullProcessImageName(h, 0, sb, ref size)) return null;
                return StatsEngine.NormalizeApp(sb.ToString());
            }
            finally
            {
                CloseHandle(h);
            }
        }
    }
}
