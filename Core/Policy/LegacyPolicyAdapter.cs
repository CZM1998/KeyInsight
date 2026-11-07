using System;
using System.Collections.Generic;

namespace KeyInsight.Core.Policy
{
    /// <summary>
    /// 纯逻辑层：v2.1.x DebounceHook 的防抖判定（LEG 兼容策略）+ 组合键保护修正。
    ///
    /// 与 v2.1.x 一致的决策边界（LEG-001 差分基线）：
    ///   Suppress 条件：gapDown &lt; ThresholdMs。
    ///   被 Suppress 的 Down 不更新 lastDown；Up 恒定更新 lastUp。
    ///
    /// 组合键保护修正（v2.1.x 已知误伤，用户实测反馈）：v2.1 的 gapUp 窗口
    /// （gapUp &lt; max(ThresholdMs, 40)）会把“同一键快速重按”一律当弹跳拦截，
    /// 导致 连续 Ctrl+C、打字后立刻 Ctrl+V、游戏内快速双击 等组合失效。
    /// 现规则：gapUp 窗口仅在上一个“按下→释放”周期本身是机器级短促
    /// （&lt; ChatterPressMaxMs，疑似触点弹跳）时才拦截；健康周期的重按放行。
    ///
    /// 本类不调用 Windows API、不读时钟、不做 I/O；时间以毫秒注入。
    /// 实例只应被键盘钩子线程访问（与旧实现的字典访问线程模型一致）。
    /// </summary>
    public sealed class LegacyPolicyAdapter
    {
        /// <summary>“按下→释放”周期短于此值视为机器级弹跳（真实抖动通常 &lt;15ms；
        /// 人类有意的重按/组合键连发至少几十毫秒）。</summary>
        public const double ChatterPressMaxMs = 30;

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
        /// 评估一次 Down。返回 true 表示应拦截（Suppress）。
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

            // 弹跳主签名：无释放的连续 Down（触点反复闭合），逐位保留 v2.1 语义
            if (gapDownMs < _thresholdMs)
            {
                return true;
            }

            // 带释放的弹跳：仅当上一个“按下→释放”周期本身短促（机器级）才拦。
            // 健康周期（人类节奏的重按、连续组合键、打字后立刻的组合键）放行。
            if (gapUpMs < _upWindowMs && hasDown && hasUp &&
                (lastUp - lastDown) < ChatterPressMaxMs)
            {
                return true;
            }

            _lastDown[vk] = nowMs;
            return false;
        }

        /// <summary>记录一次 Up（对所有非注入 Up 恒定更新 lastUp，且 Up 永远放行）。</summary>
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
            if (gapDown < _thresholdMs) return true;
            return gapUp < _upWindowMs && hasDown && hasUp &&
                   (lastUp - lastDown) < ChatterPressMaxMs;
        }

        /// <summary>钩子重启/策略切换后清理临时状态；不改变阈值。</summary>
        public void Reset()
        {
            _lastDown.Clear();
            _lastUp.Clear();
        }
    }
}
