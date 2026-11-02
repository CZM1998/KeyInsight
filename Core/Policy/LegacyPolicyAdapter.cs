using System;
using System.Collections.Generic;

namespace KeyInsight.Core.Policy
{
    /// <summary>
    /// 纯逻辑层：逐位复刻 v2.1.x DebounceHook 的防抖判定（LEG 兼容策略）。
    ///
    /// 与 v2.1.x 保持一致的决策边界（LEG-001 差分基线）：
    ///   Suppress 条件：gapDown &lt; ThresholdMs 或 gapUp &lt; max(ThresholdMs, 40)。
    ///   被 Suppress 的 Down 不更新 lastDown；Up 恒定更新 lastUp
    ///   （“被过滤 Down 的 Up 仍会放行”是 v2.1.x 已知遗留语义，本类不悄悄修复）。
    ///
    /// 本类不调用 Windows API、不读时钟、不做 I/O；时间以毫秒注入。
    /// 实例只应被键盘钩子线程访问（与旧实现的字典访问线程模型一致）。
    /// </summary>
    public sealed class LegacyPolicyAdapter
    {
        private readonly Dictionary<int, double> _lastDown = new Dictionary<int, double>();
        private readonly Dictionary<int, double> _lastUp = new Dictionary<int, double>();
        private double _thresholdMs;
        private double _upWindowMs;

        public LegacyPolicyAdapter(double thresholdMs)
        {
            SetThreshold(thresholdMs);
        }

        public double ThresholdMs
        {
            get { return _thresholdMs; }
        }

        /// <summary>Up 窗口下限 40 ms 与旧实现的 Math.Max(ThresholdMs, 40) 一致。</summary>
        public double UpWindowMs
        {
            get { return _upWindowMs; }
        }

        public void SetThreshold(double thresholdMs)
        {
            if (thresholdMs < 1) thresholdMs = 1;
            if (thresholdMs > 100) thresholdMs = 100;
            _thresholdMs = thresholdMs;
            _upWindowMs = Math.Max(thresholdMs, 40);
        }

        /// <summary>
        /// 评估一次 Down。返回 true 表示按旧策略应拦截（Suppress）。
        /// 放行时更新 lastDown（与旧实现一致）；拦截时不更新。
        /// gapDownMs/gapUpMs 供钩子的 nearmiss 记录与拦截日志使用，无历史时为 double.MaxValue。
        /// </summary>
        public bool EvaluateDown(double nowMs, int vk, out double gapDownMs, out double gapUpMs)
        {
            double lastDown;
            bool hasDown = _lastDown.TryGetValue(vk, out lastDown);
            double lastUp;
            bool hasUp = _lastUp.TryGetValue(vk, out lastUp);
            gapDownMs = hasDown ? nowMs - lastDown : double.MaxValue;
            gapUpMs = hasUp ? nowMs - lastUp : double.MaxValue;

            if (gapDownMs < _thresholdMs || gapUpMs < _upWindowMs)
            {
                return true;
            }

            _lastDown[vk] = nowMs;
            return false;
        }

        /// <summary>记录一次 Up（旧实现对所有非注入 Up 恒定更新 lastUp，且 Up 永远放行）。</summary>
        public void ObserveUp(double nowMs, int vk)
        {
            _lastUp[vk] = nowMs;
        }

        /// <summary>只读查询：该键此刻收到 Down 会被拦截吗。不改变任何状态（影子对比/测试用）。</summary>
        public bool WouldSuppressDown(double nowMs, int vk)
        {
            double lastDown;
            bool hasDown = _lastDown.TryGetValue(vk, out lastDown);
            double lastUp;
            bool hasUp = _lastUp.TryGetValue(vk, out lastUp);
            double gapDown = hasDown ? nowMs - lastDown : double.MaxValue;
            double gapUp = hasUp ? nowMs - lastUp : double.MaxValue;
            return gapDown < _thresholdMs || gapUp < _upWindowMs;
        }

        /// <summary>钩子重启/策略切换后清理临时状态；不改变阈值。</summary>
        public void Reset()
        {
            _lastDown.Clear();
            _lastUp.Clear();
        }
    }
}
