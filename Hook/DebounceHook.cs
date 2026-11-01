using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using KeyInsight.Stats;

namespace KeyInsight.Hook
{
    public sealed class DebounceHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const uint LLKHF_INJECTED = 0x10;
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
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        private readonly Config _cfg;
        private readonly StatsEngine _stats;
        private readonly Dictionary<uint, long> _lastDown = new Dictionary<uint, long>();
        private readonly Dictionary<uint, long> _lastUp = new Dictionary<uint, long>();
        private readonly Dictionary<uint, long> _lastNearMiss = new Dictionary<uint, long>();
        private readonly object _logGate = new object();

        private HookProc _proc;
        private IntPtr _hook = IntPtr.Zero;
        private Thread _thread;
        private uint _threadId;
        private volatile bool _running;
        private StreamWriter _log;
        private long _swallowed;
        private long _seen;

        public DebounceHook(Config cfg, StatsEngine stats)
        {
            _cfg = cfg;
            _stats = stats;
            if (cfg.LogSwallowed)
            {
                try
                {
                    _log = new StreamWriter(
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debounce.log"), true);
                    _log.AutoFlush = true;
                }
                catch { _log = null; }
            }
        }

        public long SwallowedCount { get { return Interlocked.Read(ref _swallowed); } }
        public long SeenCount { get { return Interlocked.Read(ref _seen); } }
        public bool IsRunning { get { return _running && _hook != IntPtr.Zero; } }

        public bool Start()
        {
            if (_running) return true;
            if (_thread != null && _thread.IsAlive) return false;
            _running = true;
            _thread = new Thread(ThreadProc);
            _thread.IsBackground = true;
            _thread.Name = "KeyInsightHook";
            _thread.Start();

            for (int i = 0; i < 200 && _hook == IntPtr.Zero && _running; i++) Thread.Sleep(10);
            return _hook != IntPtr.Zero;
        }

        private void ThreadProc()
        {
            _threadId = GetCurrentThreadId();
            _proc = HookCallback;
            IntPtr handle = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            _hook = handle;
            if (handle == IntPtr.Zero)
            {
                _running = false;
                return;
            }

            LogLine("=== started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                    " threshold=" + _cfg.ThresholdMs + "ms upWindow=" + Math.Max(_cfg.ThresholdMs, 40) +
                    "ms skipInjected=" + _cfg.SkipInjected);

            MSG msg;
            while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            UnhookWindowsHookEx(handle);
            _hook = IntPtr.Zero;
            _running = false;
            LogLine("=== stopped " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                    " swallowed=" + _swallowed);
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0 && _cfg.Enabled)
                {
                    int msg = wParam.ToInt32();
                    bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                    bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;

                    if (down || up)
                    {
                        KBDLLHOOKSTRUCT k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                        bool injected = (k.flags & LLKHF_INJECTED) != 0;

                        if (_cfg.LogAllKeys && down)
                        {
                            LogLine(DateTime.Now.ToString("HH:mm:ss.fff") +
                                    " down vk=0x" + k.vkCode.ToString("X2") +
                                    " scan=0x" + k.scanCode.ToString("X2") +
                                    " flags=0x" + k.flags.ToString("X") +
                                    " injected=" + (injected ? "1" : "0") +
                                    " app=" + ForegroundTracker.CurrentApp);
                        }

                        if (!(_cfg.SkipInjected && injected))
                        {
                            long now = Stopwatch.GetTimestamp();
                            string app = ForegroundTracker.CurrentApp;

                            if (down)
                            {
                                Interlocked.Increment(ref _seen);

                                long lastDown, lastUp;
                                bool hasDown = _lastDown.TryGetValue(k.vkCode, out lastDown);
                                bool hasUp = _lastUp.TryGetValue(k.vkCode, out lastUp);
                                double gapDown = hasDown ? TicksToMs(now - lastDown) : double.MaxValue;
                                double gapUp = hasUp ? TicksToMs(now - lastUp) : double.MaxValue;
                                double upWindow = Math.Max(_cfg.ThresholdMs, 40);

                                if (gapDown < _cfg.ThresholdMs || gapUp < upWindow)
                                {
                                    Interlocked.Increment(ref _swallowed);
                                    LogLine(DateTime.Now.ToString("HH:mm:ss.fff") +
                                            " swallow vk=0x" + k.vkCode.ToString("X2") +
                                            " scan=0x" + k.scanCode.ToString("X2") +
                                            " gapDown=" + Fmt(gapDown) +
                                            " gapUp=" + Fmt(gapUp) +
                                            " app=" + app);

                                    if (_stats != null)
                                    {
                                        KeyEvent sw = new KeyEvent();
                                        sw.Type = KeyEventType.Swallow;
                                        sw.Vk = StatsVk(k);
                                        sw.At = DateTime.Now;
                                        sw.App = app;
                                        sw.GapDown = gapDown == double.MaxValue ? 0 : gapDown;
                                        sw.GapShortUp = gapUp == double.MaxValue ? 0 : gapUp;
                                        _stats.Enqueue(sw);
                                    }
                                    return (IntPtr)1;
                                }

                                if (gapUp < 60 || gapDown < 45)
                                {
                                    LogNearMiss(k.vkCode, gapDown, gapUp);
                                }

                                _lastDown[k.vkCode] = now;

                                if (_stats != null)
                                {
                                    KeyEvent ke = new KeyEvent();
                                    ke.Type = KeyEventType.Down;
                                    ke.Vk = StatsVk(k);
                                    ke.At = DateTime.Now;
                                    ke.App = app;
                                    _stats.Enqueue(ke);
                                }
                            }
                            else
                            {
                                _lastUp[k.vkCode] = now;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogLine("ERR " + ex.GetType().Name + ": " + ex.Message);
            }

            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private static double TicksToMs(long ticks)
        {
            return ticks * 1000.0 / Stopwatch.Frequency;
        }

        private static int StatsVk(KBDLLHOOKSTRUCT k)
        {
            int vk = (int)k.vkCode;
            if (vk == 0x0D && (k.flags & 0x01) != 0) vk = 0x1000D;
            return vk;
        }

        private static string Fmt(double ms)
        {
            return ms == double.MaxValue ? "--" : ms.ToString("F2") + "ms";
        }

        private void LogNearMiss(uint vk, double gapDown, double gapUp)
        {
            long last;
            long now = Stopwatch.GetTimestamp();
            if (_lastNearMiss.TryGetValue(vk, out last) && TicksToMs(now - last) < 500) return;
            _lastNearMiss[vk] = now;
            LogLine(DateTime.Now.ToString("HH:mm:ss.fff") +
                    " nearmiss vk=0x" + vk.ToString("X2") +
                    " gapDown=" + Fmt(gapDown) +
                    " gapUp=" + Fmt(gapUp));
        }

        private void LogLine(string line)
        {
            if (_log == null) return;
            lock (_logGate)
            {
                try { _log.WriteLine(line); }
                catch { }
            }
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
                _lastDown.Clear();
                _lastUp.Clear();
                _lastNearMiss.Clear();
            }
        }

        public void Dispose()
        {
            Stop();
            lock (_logGate)
            {
                if (_log != null)
                {
                    try { _log.Flush(); _log.Close(); }
                    catch { }
                    _log = null;
                }
            }
        }
    }
}
