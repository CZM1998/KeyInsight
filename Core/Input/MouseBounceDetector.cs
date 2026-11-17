namespace KeyInsight.Core.Input
{
    /// <summary>鼠标同键快速重按观测。输入为低级钩子的消息时间（毫秒）。</summary>
    internal sealed class MouseBounceDetector
    {
        private const uint ShortReleaseMs = 20;
        private const uint ShortPressMs = 20;

        private struct ButtonState
        {
            public bool IsDown;
            public uint DownTime;
            public bool HasRelease;
            public uint ReleaseTime;
        }

        private readonly ButtonState[] _buttons;

        public MouseBounceDetector(int buttonCount)
        {
            _buttons = new ButtonState[buttonCount];
        }

        public void ObserveDown(int button, uint timestamp)
        {
            if (_buttons[button].IsDown) return;
            _buttons[button].DownTime = timestamp;
            _buttons[button].IsDown = true;
        }

        public bool ObserveUp(int button, uint timestamp, out uint pressMs)
        {
            pressMs = 0;
            ButtonState state = _buttons[button];
            if (!state.IsDown) return false;

            // DWORD 时钟回绕时，短间隔的无符号差值依然正确。
            pressMs = unchecked(timestamp - state.DownTime);
            uint gapMs = unchecked(state.DownTime - state.ReleaseTime);
            bool candidate = state.HasRelease && pressMs < ShortPressMs && gapMs < ShortReleaseMs;
            _buttons[button].IsDown = false;
            _buttons[button].HasRelease = pressMs <= int.MaxValue;
            _buttons[button].ReleaseTime = timestamp;
            return candidate;
        }

        public void Reset()
        {
            System.Array.Clear(_buttons, 0, _buttons.Length);
        }
    }
}
