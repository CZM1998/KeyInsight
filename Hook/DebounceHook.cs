using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using KeyInsight.Core.Input;
using KeyInsight.Core.Policy;
using KeyInsight.Core.Telemetry;
using KeyInsight.Stats;

namespace KeyInsight.Hook
{
    /// <summary>
    /// 键盘低级钩子（WH_KEYBOARD_LL）。v2.2 P0-03/04/05 重构后本类只做三件事：
    /// 1. 安装/卸载钩子与消息循环（安装壳保持原有生命周期语义）；
    /// 2. 把原始事件解析为 NormalizedKeyEvent 交给 IKeyboardPolicyEngine 决策；
    /// 3. 按决策放行/拦截，并把统计事件投给 ITelemetrySink。
    /// 回调内禁止磁盘 I/O（日志经 AsyncFileLog 异步）、禁止等待（遥测经有界队列）；
    /// 策略来自 PolicySnapshot 不可变快照（PolicyController）。
    /// </summary>
    public sealed class DebounceHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const uint LLKHF_INJECTED = 0x10;
        private const uint WM_QUIT = 0x0012;
        private const int LatencySampleEvery = 256;

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
        private readonly ITelemetrySink _sink;
        private readonly KeyInsightPolicyEngine _engine;
        private readonly ShortcutObserver _shortcuts = new ShortcutObserver();
        private readonly Dictionary<int, long> _lastNearMiss = new Dictionary<int, long>();
        private readonly object _logGate = new object();

        private HookProc _proc;
        private IntPtr _hook = IntPtr.Zero;
        private Thread _thread;
        private uint _threadId;
        private volatile bool _running;
        private AsyncFileLog _log;
        private long _swallowed;
        private long _seen;
        private long _eventCounter;
        private readonly long[] _latencyBuckets = new long[32];
        private long _latencySamples;

        public DebounceHook(Config cfg, StatsEngine stats)
            : this(cfg, stats == null ? null : (ITelemetrySink)new StatsEngineSink(stats))
        {
        }

        public DebounceHook(Config cfg, ITelemetrySink sink)
        {
            _cfg = cfg;
            _sink = sink;
            _engine = new KeyInsightPolicyEngine(PolicyController.Current);
            if (cfg.LogSwallowed)
            {
                try
                {
                    _log = new AsyncFileLog(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debounce.log"));
                    _log.Start();
                }
                catch { _log = null; }
            }
        }

        // 时钟注入（设计 §2.3）：默认系统时钟，测试可替换
        public IMonotonicClock MonotonicClock = SystemMonotonicClock.Instance;
        public IWallClock WallClock = SystemWallClock.Instance;

        public long SwallowedCount { get { return Interlocked.Read(ref _swallowed); } }
        public long SeenCount { get { return Interlocked.Read(ref _seen); } }
        public bool IsRunning { get { return _running && _hook != IntPtr.Zero; } }

        /// <summary>安装失败或 GetMessage 出错时的 Win32 错误码（仅诊断）。</summary>
        public int LastErrorCode;

        private long _lastEventTicks;
        /// <summary>最近一次收到输入事件的 UTC ticks（0 = 未收到）。</summary>
        public long LastEventUtcTicks { get { return Interlocked.Read(ref _lastEventTicks); } }

        /// <summary>策略引擎（UI 读取影子对比/候选计数用；不得在钩子线程之外调用其 Evaluate）。</summary>
        public KeyInsightPolicyEngine Engine { get { return _engine; } }

        /// <summary>回调耗时直方图（2 的幂微秒桶，采样每 256 个事件取 1 个）。只读快照。</summary>
        public long[] LatencyBucketsSnapshot()
        {
            lock (_logGate)
            {
                long[] copy = new long[_latencyBuckets.Length];
                Array.Copy(_latencyBuckets, copy, copy.Length);
                return copy;
            }
        }

        public long LatencySamples { get { return Interlocked.Read(ref _latencySamples); } }

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
                LastErrorCode = Marshal.GetLastWin32Error();
                _running = false;
                return;
            }

            PolicySnapshot snap = PolicyController.Current;
            LogLine("=== started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                    " threshold=" + snap.GlobalThresholdMs + "ms upWindow=" + Math.Max(snap.GlobalThresholdMs, 40) +
                    "ms skipInjected=" + snap.SkipInjected +
                    " mode=" + snap.Mode);

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
                LogLine("ERR GetMessage failed code=" + LastErrorCode);
            }

            UnhookWindowsHookEx(handle);
            _hook = IntPtr.Zero;
            _running = false;
            LogLine("=== stopped " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                    " swallowed=" + _swallowed);
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            long startTicks = 0;
            bool sample = (Interlocked.Increment(ref _eventCounter) % LatencySampleEvery) == 0;
            if (sample) startTicks = Stopwatch.GetTimestamp();

            try
            {
                if (nCode >= 0)
                {
                    int msg = wParam.ToInt32();
                    bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                    bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;

                    if (down || up)
                    {
                        Interlocked.Exchange(ref _lastEventTicks, DateTime.UtcNow.Ticks);
                        KBDLLHOOKSTRUCT k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                        bool injected = (k.flags & LLKHF_INJECTED) != 0;
                        PolicySnapshot snap = PolicyController.Current;
                        // 快照更替同步到引擎（版本号比较 + 整体引用替换；钩子线程不触碰配置文件）
                        if (_engine.CurrentSnapshot.Version != snap.Version)
                        {
                            _engine.UpdatePolicy(snap);
                        }

                        if (_cfg.LogAllKeys && down)
                        {
                            LogLine(DateTime.Now.ToString("HH:mm:ss.fff") +
                                    " down vk=0x" + k.vkCode.ToString("X2") +
                                    " scan=0x" + k.scanCode.ToString("X2") +
                                    " flags=0x" + k.flags.ToString("X") +
                                    " injected=" + (injected ? "1" : "0") +
                                    " app=" + ForegroundTracker.CurrentApp);
                        }

                        if (snap.SkipInjected && injected)
                        {
                            // 注入旁路：不进策略、不进统计、不进状态（与 v2.1.x 一致）
                        }
                        else if (!_cfg.CaptureEnabled)
                        {
                            // 采集关闭：只放行（P0-02 预留语义）
                        }
                        else
                        {
                            double nowMs = MonotonicClock.NowMs;
                            NormalizedKeyEvent ev = new NormalizedKeyEvent();
                            ev.Key = InputIdentityResolver.Resolve((int)k.vkCode, (int)k.scanCode, k.flags);
                            ev.Action = down ? InputAction.Down : InputAction.Up;
                            ev.MonoMs = nowMs;
                            ev.UtcTicks = WallClock.UtcNow.Ticks;
                            ev.Injected = injected;
                            ev.LegacyVk = StatsVk(k);
                            ev.AppName = ForegroundTracker.CurrentApp;

                            // P2-04：快捷键白名单聚合（只记组合名与次数，独立于过滤决策）
                            string combo = _shortcuts.Observe((int)k.vkCode, down, (k.flags & 0x01) != 0);
                            if (combo != null && _sink != null)
                            {
                                TelemetryEvent se = new TelemetryEvent();
                                se.Kind = TelemetryKind.Shortcut;
                                se.App = combo;
                                se.UtcTicks = ev.UtcTicks;
                                _sink.TryPublish(se);
                            }

                            InputDecision decision = _engine.Evaluate(ev);

                            if (decision.Kind == DecisionKind.Suppress)
                            {
                                Interlocked.Increment(ref _swallowed);
                                LogLine(DateTime.Now.ToString("HH:mm:ss.fff") +
                                        " swallow vk=0x" + k.vkCode.ToString("X2") +
                                        " scan=0x" + k.scanCode.ToString("X2") +
                                        " gapDown=" + Fmt(decision.GapDownMs) +
                                        " gapUp=" + Fmt(decision.GapUpMs) +
                                        " reason=" + decision.Reason +
                                        " pv=" + decision.PolicyVersion +
                                        " app=" + ev.AppName);

                                if (down && _sink != null)
                                {
                                    TelemetryEvent sw = new TelemetryEvent();
                                    sw.Kind = TelemetryKind.Swallow;
                                    sw.Key = ev.Key;
                                    sw.LegacyVk = ev.LegacyVk;
                                    sw.UtcTicks = ev.UtcTicks;
                                    sw.App = ev.AppName;
                                    sw.GapDownMs = decision.GapDownMs == double.MaxValue ? 0 : decision.GapDownMs;
                                    sw.GapShortUpMs = decision.GapUpMs == double.MaxValue ? 0 : decision.GapUpMs;
                                    _sink.TryPublish(sw);
                                }
                                return (IntPtr)1;
                            }

                            if (down)
                            {
                                Interlocked.Increment(ref _seen);

                                // nearmiss 诊断行（旧口径保留，仅日志）
                                if (decision.GapUpMs < 60 || decision.GapDownMs < 45)
                                {
                                    LogNearMiss(ev.LegacyVk, decision.GapDownMs, decision.GapUpMs);
                                }

                                if (_sink != null)
                                {
                                    TelemetryEvent ke = new TelemetryEvent();
                                    ke.Kind = TelemetryKind.Down;
                                    ke.Key = ev.Key;
                                    ke.LegacyVk = ev.LegacyVk;
                                    ke.UtcTicks = ev.UtcTicks;
                                    ke.App = ev.AppName;
                                    _sink.TryPublish(ke);
                                }
                            }
                            // Up：v2.1.x 口径不产生 Up 统计，保持不变
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogLine("ERR " + ex.GetType().Name + ": " + ex.Message);
            }

            if (sample) RecordLatency(Stopwatch.GetTimestamp() - startTicks);

            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private void RecordLatency(long elapsedTicks)
        {
            double us = elapsedTicks * 1000000.0 / Stopwatch.Frequency;
            int bucket = 0;
            double v = us;
            while (v > 1.0 && bucket < _latencyBuckets.Length - 1)
            {
                v /= 2.0;
                bucket++;
            }
            lock (_logGate)
            {
                _latencyBuckets[bucket]++;
            }
            Interlocked.Increment(ref _latencySamples);
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

        private void LogNearMiss(int vk, double gapDown, double gapUp)
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
            AsyncFileLog log = _log;
            if (log == null) return;
            log.Write(line);
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
                _engine.ResetForHookRestart();
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
                    try { _log.Dispose(); }
                    catch { }
                    _log = null;
                }
            }
        }
    }
}
