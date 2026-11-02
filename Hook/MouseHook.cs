using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using KeyInsight.Core.Input;
using KeyInsight.Core.Telemetry;
using KeyInsight.Stats;

namespace KeyInsight.Hook
{
    public sealed class MouseHook : IDisposable
    {
        private const int WH_MOUSE_LL = 14;
        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_LBUTTONUP = 0x0202;
        private const uint WM_RBUTTONDOWN = 0x0204;
        private const uint WM_RBUTTONUP = 0x0205;
        private const uint WM_MBUTTONDOWN = 0x0207;
        private const uint WM_MBUTTONUP = 0x0208;
        private const uint WM_MOUSEWHEEL = 0x020A;
        private const uint WM_XBUTTONDOWN = 0x020B;
        private const uint WM_XBUTTONUP = 0x020C;
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
        private readonly ITelemetrySink _sink;
        private readonly AsyncFileLog _log;

        // 时钟注入（设计 §2.3）
        public IMonotonicClock MonotonicClock = SystemMonotonicClock.Instance;
        public IWallClock WallClock = SystemWallClock.Instance;
        private HookProc _proc;
        private IntPtr _hook;
        private Thread _thread;
        private uint _threadId;
        private volatile bool _running;
        private long _events;

        // P2-03：鼠标 Up 周期与微动候选（仅检测，不拦截）。状态只被鼠标钩子线程访问。
        private readonly long[] _lastDownTicks = new long[10];   // button id -> ticks
        private readonly bool[] _isDown = new bool[10];
        private long _upCount;
        private readonly long[] _lastCycleTicks = new long[10];
        private long _cycleCandidates;
        private long _rawWheelDelta;                             // 原始 delta 累计（正负抵消后的净滚动量，V2 口径）
        private long _rawWheelEvents;

        public MouseHook(Config cfg, StatsEngine stats)
            : this(cfg, stats == null ? null : (ITelemetrySink)new StatsEngineSink(stats))
        {
        }

        public MouseHook(Config cfg, ITelemetrySink sink)
        {
            _cfg = cfg;
            _sink = sink;
            try
            {
                _log = new AsyncFileLog(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mouse.log"));
                _log.Start();
            }
            catch
            {
                _log = null;
            }
        }

        public long EventCount { get { return Interlocked.Read(ref _events); } }
        public long UpCount { get { return Interlocked.Read(ref _upCount); } }
        public long CycleCandidates { get { return Interlocked.Read(ref _cycleCandidates); } }
        public long RawWheelDelta { get { return Interlocked.Read(ref _rawWheelDelta); } }
        public long RawWheelEvents { get { return Interlocked.Read(ref _rawWheelEvents); } }
        public bool IsRunning { get { return _running && _hook != IntPtr.Zero; } }

        /// <summary>安装失败或 GetMessage 出错时的 Win32 错误码（仅诊断）。</summary>
        public int LastErrorCode;

        private long _lastEventTicks;
        /// <summary>最近一次收到输入事件的 UTC ticks（0 = 未收到）。</summary>
        public long LastEventUtcTicks { get { return Interlocked.Read(ref _lastEventTicks); } }

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
                LastErrorCode = Marshal.GetLastWin32Error();
                _running = false;
                Log("=== mouse hook failed err=" + LastErrorCode);
                return;
            }
            Log("=== mouse hook started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            MSG msg;
            int ret;
            while ((ret = GetMessage(out msg, IntPtr.Zero, 0, 0)) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            if (ret == -1)
            {
                LastErrorCode = Marshal.GetLastWin32Error();
                Log("ERR GetMessage failed code=" + LastErrorCode);
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
                    bool isDownMsg = msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN ||
                                    msg == WM_MBUTTONDOWN || msg == WM_XBUTTONDOWN;
                    bool isUpMsg = msg == WM_LBUTTONUP || msg == WM_RBUTTONUP ||
                                   msg == WM_MBUTTONUP || msg == WM_XBUTTONUP;
                    bool isWheel = msg == WM_MOUSEWHEEL || msg == WM_MOUSEHWHEEL;
                    if (!isDownMsg && !isUpMsg && !isWheel) return CallNextHookEx(_hook, nCode, wParam, lParam);

                    Interlocked.Exchange(ref _lastEventTicks, DateTime.UtcNow.Ticks);
                    MSLLHOOKSTRUCT m = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                    if (_cfg.SkipInjected && (m.flags & LLMHF_INJECTED) != 0)
                        return CallNextHookEx(_hook, nCode, wParam, lParam);

                    // P2-03：Up 周期观测（仅检测不拦截；不进入旧统计口径）
                    if (isUpMsg)
                    {
                        int upButton = ButtonFromMessage(msg, m);
                        if (upButton > 0 && upButton < 10)
                        {
                            Interlocked.Increment(ref _upCount);
                            bool wasDown = _isDown[upButton];
                            _isDown[upButton] = false;
                            if (wasDown)
                            {
                                long pressTicks = Stopwatch.GetTimestamp() - _lastDownTicks[upButton];
                                double pressMs = pressTicks * 1000.0 / Stopwatch.Frequency;
                                // 微动故障候选：异常短的按下周期（<40ms，正常点击很难低于此值）
                                if (pressMs < 40)
                                {
                                    long nowT = Stopwatch.GetTimestamp();
                                    bool hasLast = _lastCycleTicks[upButton] != 0;
                                    if (!hasLast ||
                                        (nowT - _lastCycleTicks[upButton]) * 1000.0 / Stopwatch.Frequency >= 500)
                                    {
                                        _lastCycleTicks[upButton] = nowT;
                                        Interlocked.Increment(ref _cycleCandidates);
                                        Log("cycle-candidate button=" + upButton +
                                            " pressMs=" + pressMs.ToString("F1"));
                                    }
                                }
                            }
                        }
                        return CallNextHookEx(_hook, nCode, wParam, lParam);
                    }

                    int button = 0;
                    int delta = 0;
                    int rawDelta = 0;

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
                        rawDelta = d;
                    }
                    else if (msg == WM_MOUSEHWHEEL)
                    {
                        int d = (short)((m.mouseData >> 16) & 0xFFFF);
                        int ticks = Math.Abs(d) / 120;
                        if (ticks == 0) ticks = 1;
                        button = d >= 0 ? MouseIds.WheelRight : MouseIds.WheelLeft;
                        delta = ticks;
                        rawDelta = d;
                    }

                    if (button > 0)
                    {
                        // P2-03 观测：按下周期起点（供 Up 配对）
                        if (isDownMsg && button < 10)
                        {
                            _lastDownTicks[button] = Stopwatch.GetTimestamp();
                            _isDown[button] = true;
                        }

                        // V2 口径：原始滚轮 delta（保留高分辨率小滚动；旧 MB 格口累计不动）
                        if (isWheel)
                        {
                            Interlocked.Increment(ref _rawWheelEvents);
                            Interlocked.Add(ref _rawWheelDelta, rawDelta);
                        }

                        if (_sink != null)
                        {
                            long utcTicks = WallClock.UtcNow.Ticks;
                            string app = ForegroundTracker.CurrentApp;
                            TelemetryEvent e = new TelemetryEvent();
                            e.Kind = isWheel ? TelemetryKind.MouseWheel : TelemetryKind.MouseDown;
                            e.Button = button;
                            e.Delta = isWheel ? delta : 0;
                            e.UtcTicks = utcTicks;
                            e.App = app;
                            _sink.TryPublish(e);
                            if (isWheel)
                            {
                                // P2-03：原始 delta 单独一路（高分辨率滚轮保留小步滚动）
                                TelemetryEvent raw = new TelemetryEvent();
                                raw.Kind = TelemetryKind.WheelRaw;
                                raw.Delta = rawDelta;
                                raw.UtcTicks = utcTicks;
                                raw.App = app;
                                _sink.TryPublish(raw);
                            }
                            long n = Interlocked.Increment(ref _events);
                            if (n == 1) Log("first event button=" + button + " app=" + app);
                        }
                    }
                }
            }
            catch
            {
            }

            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private static int ButtonFromMessage(uint msg, MSLLHOOKSTRUCT m)
        {
            if (msg == WM_LBUTTONUP) return MouseIds.Left;
            if (msg == WM_RBUTTONUP) return MouseIds.Right;
            if (msg == WM_MBUTTONUP) return MouseIds.Middle;
            if (msg == WM_XBUTTONUP)
            {
                int x = (int)((m.mouseData >> 16) & 0xFFFF);
                if (x == 1) return MouseIds.Back;
                if (x == 2) return MouseIds.Forward;
            }
            return 0;
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
            if (_log != null)
            {
                try { _log.Dispose(); } catch { }
            }
        }

        private void Log(string line)
        {
            AsyncFileLog log = _log;
            if (log == null) return;
            log.Write(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + line);
        }
    }
}
