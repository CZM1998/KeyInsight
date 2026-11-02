using System;

namespace KeyInsight.Hook
{
    public enum HookState
    {
        Stopped = 0,
        Starting = 1,
        Installed = 2,
        Stopping = 3,
        Failed = 4
    }

    /// <summary>
    /// 钩子监管（设计文档 §5.1）：把键盘/鼠标钩子的生命周期收拢为显式状态机，
    /// 记录 Win32 错误码与最近一次收到事件的时间。注意：
    /// Windows 可能在回调超时后静默移除低级钩子，且没有可靠 API 可检测——
    /// 因此这里只报告“进程认为已安装 + 最近收到事件时间”，不构成健康证明，
    /// 也不做 SendInput 心跳探测（ADR-007）。
    /// </summary>
    public sealed class HookSupervisor : IDisposable
    {
        private readonly DebounceHook _keyboard;
        private readonly MouseHook _mouse;

        public HookState KeyboardState { get; private set; }
        public HookState MouseState { get; private set; }
        public DateTime LastInstallAttemptUtc { get; private set; }
        public string LastError { get; private set; }

        public HookSupervisor(DebounceHook keyboard, MouseHook mouse)
        {
            _keyboard = keyboard;
            _mouse = mouse;
            KeyboardState = HookState.Stopped;
            MouseState = HookState.Stopped;
            LastError = "";
        }

        public DebounceHook Keyboard { get { return _keyboard; } }
        public MouseHook Mouse { get { return _mouse; } }

        /// <summary>最近一次收到输入事件的 UTC ticks（两个钩子取最大；0 = 尚未收到）。</summary>
        public long LastEventUtcTicks
        {
            get
            {
                long a = _keyboard == null ? 0 : _keyboard.LastEventUtcTicks;
                long b = _mouse == null ? 0 : _mouse.LastEventUtcTicks;
                return a > b ? a : b;
            }
        }

        /// <summary>“最近事件距今”文本；长时间无事件属正常（不代表钩子失效）。</summary>
        public string LastEventAgoText
        {
            get
            {
                long ticks = LastEventUtcTicks;
                if (ticks <= 0) return "尚未收到事件";
                TimeSpan ago = DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc);
                if (ago.TotalSeconds < 5) return "刚刚";
                if (ago.TotalSeconds < 60) return (int)ago.TotalSeconds + " 秒前";
                if (ago.TotalMinutes < 60) return (int)ago.TotalMinutes + " 分钟前";
                if (ago.TotalHours < 24) return (int)ago.TotalHours + " 小时前";
                return (int)ago.TotalDays + " 天前";
            }
        }

        public bool StartAll()
        {
            LastInstallAttemptUtc = DateTime.UtcNow;
            bool ok = true;
            if (_keyboard != null)
            {
                KeyboardState = HookState.Starting;
                if (_keyboard.Start())
                {
                    KeyboardState = HookState.Installed;
                }
                else
                {
                    KeyboardState = HookState.Failed;
                    LastError = "键盘钩子安装失败（Win32 " + _keyboard.LastErrorCode + "）";
                    ok = false;
                }
            }
            if (_mouse != null)
            {
                MouseState = HookState.Starting;
                if (_mouse.Start())
                {
                    MouseState = HookState.Installed;
                }
                else
                {
                    MouseState = HookState.Failed;
                    string e = "鼠标钩子安装失败（Win32 " + _mouse.LastErrorCode + "）";
                    LastError = LastError.Length == 0 ? e : LastError + "；" + e;
                    ok = false;
                }
            }
            return ok;
        }

        public void StopAll()
        {
            if (_keyboard != null)
            {
                KeyboardState = HookState.Stopping;
                _keyboard.Stop();
                KeyboardState = HookState.Stopped;
            }
            if (_mouse != null)
            {
                MouseState = HookState.Stopping;
                _mouse.Stop();
                MouseState = HookState.Stopped;
            }
        }

        /// <summary>重新安装（休眠唤醒/会话切换/用户手动）。返回是否全部成功，detail 给 UI 用。</summary>
        public bool RestartAll(out string detail)
        {
            LastInstallAttemptUtc = DateTime.UtcNow;
            LastError = "";
            bool kb = false;
            bool ms = false;
            if (_keyboard != null)
            {
                KeyboardState = HookState.Starting;
                kb = _keyboard.Restart();
                KeyboardState = kb ? HookState.Installed : HookState.Failed;
                if (!kb) LastError += "键盘 Win32 " + _keyboard.LastErrorCode + "；";
            }
            if (_mouse != null)
            {
                MouseState = HookState.Starting;
                ms = _mouse.Restart();
                MouseState = ms ? HookState.Installed : HookState.Failed;
                if (!ms) LastError += "鼠标 Win32 " + _mouse.LastErrorCode + "；";
            }
            detail = StateText;
            if (!kb || !ms) detail += " · " + LastError.TrimEnd('；');
            return kb && ms;
        }

        public string StateText
        {
            get
            {
                return "键盘 " + StateTextOf(KeyboardState) + " · 鼠标 " + StateTextOf(MouseState);
            }
        }

        public static string StateTextOf(HookState state)
        {
            switch (state)
            {
                case HookState.Starting: return "安装中";
                case HookState.Installed: return "已安装";
                case HookState.Stopping: return "停止中";
                case HookState.Failed: return "失败";
                default: return "已停止";
            }
        }

        public void Dispose()
        {
            // hooks 的 Dispose 由 Program/TrayContext 负责；这里只收敛状态
            KeyboardState = HookState.Stopped;
            MouseState = HookState.Stopped;
        }
    }
}
