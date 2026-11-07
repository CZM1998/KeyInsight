using System;
using KeyInsight.Core.Input;

namespace KeyInsight.Core.Policy
{
    public enum KeySmState
    {
        Idle = 0,
        DeliveredDown = 1,
        SuppressedPress = 2,
        Unknown = 3
    }

    /// <summary>
    /// 保守防抖有限状态机（P0-04，设计文档 §4）。
    ///
    /// 核心不变量（测试 KSM-001~020）：
    /// - DeliveredDown + Down：无法可靠区分 OS 自动重复与触点抖动 → 默认 Pass（不吞长按）。
    /// - SuppressedPress + Up：配对拦截；+ Down（安全期内，该键已显式启用）→ 继续拦截。
    /// - 未知/缺失历史：一律 Pass，不凭猜测拦截。
    /// - SuppressedPress 长期等不到 Up（KSM-018）：超过 StuckPressExpiryMs 后在下一次 Down 保守恢复放行，
    ///   绝不合成 Up/Down，绝不永久锁死按键（Fail Open）。
    /// - 时间倒退：清理该键候选信息并放行，不产生负间隔误拦。
    /// 单实例只属于一个 PhysicalKeyId，且只能被键盘钩子线程访问（与 LegacyPolicyAdapter 相同线程模型）。
    /// </summary>
    public sealed class KeyStateMachine
    {
        /// <summary>SuppressedPress 的故障过期边界（毫秒）。真实按键持续按住不会超过该时长，
        /// 超时视为丢失配对 Up，下一 Down 恢复放行。</summary>
        public const int StuckPressExpiryMs = 3000;

        public KeySmState State;
        public double LastDeliveredUpMs = double.MinValue;
        public double LastDeliveredDownMs = double.MinValue;
        public double LastObservedDownMs = double.MinValue;
        public double LastObservedUpMs = double.MinValue;
        // SuppressedPress 的起始时刻（不随后续重复 Down 刷新）——过期判定的唯一时间基准，
        // 否则按住不放的自动重复会永远续期，键被锁死（KSM-018）
        public double SuppressedPressStartedMs = double.MinValue;
        public long RepeatCandidates;
        public long BounceCandidates;
        public long StaleRecoveries;

        /// <summary>
        /// 评估一次 Down。仅在该键显式启用过滤时才可能返回 Suppress。
        /// nowMs 单调毫秒；thresholdMs 为该键生效阈值；返回间隔供日志。
        /// </summary>
        public InputDecision EvaluateDown(double nowMs, double thresholdMs, int policyVersion)
        {
            // 故障过期：SuppressedPress 等不到 Up → 恢复放行（KSM-018）
            if (State == KeySmState.SuppressedPress &&
                SuppressedPressStartedMs != double.MinValue &&
                (nowMs - SuppressedPressStartedMs) > StuckPressExpiryMs)
            {
                StaleRecoveries++;
                State = KeySmState.Idle;
                // 旧的配对 Up 何时到来不可知；此后该键处于“状态不确定”，保守放行一切
            }

            // 时间倒退（KSM-008）：清理候选信息，重新估计
            if ((LastObservedDownMs != double.MinValue && nowMs < LastObservedDownMs) ||
                (LastObservedUpMs != double.MinValue && nowMs < LastObservedUpMs))
            {
                LastObservedDownMs = nowMs;
                LastObservedUpMs = double.MinValue;
                State = KeySmState.Unknown;
                return InputDecision.Pass(DecisionReason.UnknownState, policyVersion);
            }

            switch (State)
            {
                case KeySmState.Idle:
                case KeySmState.Unknown:
                {
                    double gapUp = LastDeliveredUpMs != double.MinValue ? nowMs - LastDeliveredUpMs : double.MaxValue;
                    // 依据上次“放行的 Up”判定快速重按；仅显式启用该键策略时才拦截。
                    // 组合键保护：gapUp 窗口只对“上一个按下→释放周期本身短促（机器级弹跳）”
                    // 生效；健康周期（人类节奏的重按、连续组合键）放行。周期起点未知
                    // （孤儿 Up 等）一律放行，不凭猜测拦截。
                    bool chatterCycle = LastDeliveredUpMs != double.MinValue &&
                        LastDeliveredDownMs != double.MinValue &&
                        (LastDeliveredUpMs - LastDeliveredDownMs) < LegacyPolicyAdapter.ChatterPressMaxMs;
                    if (gapUp < thresholdMs && chatterCycle)
                    {
                        BounceCandidates++;
                        LastObservedDownMs = nowMs;
                        SuppressedPressStartedMs = nowMs;
                        State = KeySmState.SuppressedPress;
                        return InputDecision.Suppress(DecisionReason.CandidateBounce, policyVersion,
                            double.MaxValue, gapUp);
                    }
                    LastObservedDownMs = nowMs;
                    LastDeliveredDownMs = nowMs;
                    State = KeySmState.DeliveredDown;
                    return InputDecision.Pass(DecisionReason.Normal, policyVersion);
                }

                case KeySmState.DeliveredDown:
                    // 自动重复或疑似重复 Down：无法可靠区分 → 默认放行（KSM-005/016）
                    RepeatCandidates++;
                    LastObservedDownMs = nowMs;
                    return InputDecision.Pass(DecisionReason.RepeatedWhileHeld, policyVersion);

                case KeySmState.SuppressedPress:
                    // 已拦截这次 Press，重复的 Down 一并拦截（安全期内，KSM-006）
                    BounceCandidates++;
                    LastObservedDownMs = nowMs;
                    return InputDecision.Suppress(DecisionReason.CandidateBounce, policyVersion,
                        nowMs - LastObservedDownMs, double.MaxValue);

                default:
                    return InputDecision.Pass(DecisionReason.UnknownState, policyVersion);
            }
        }

        /// <summary>评估一次 Up。被拦截 Down 的配对 Up 一并拦截（含策略刚关闭后的清理，KSM-011）。</summary>
        public InputDecision EvaluateUp(double nowMs, int policyVersion)
        {
            if (LastObservedDownMs != double.MinValue && nowMs < LastObservedDownMs)
            {
                // 时间倒退的 Up：放行且不更新（避免污染时间基准）
                return InputDecision.Pass(DecisionReason.UnknownState, policyVersion);
            }

            LastObservedUpMs = nowMs;
            switch (State)
            {
                case KeySmState.DeliveredDown:
                    State = KeySmState.Idle;
                    LastDeliveredUpMs = nowMs;
                    return InputDecision.Pass(DecisionReason.Normal, policyVersion);

                case KeySmState.SuppressedPress:
                    State = KeySmState.Idle; // 配对清理完成
                    return InputDecision.Suppress(DecisionReason.SuppressedPairedRelease, policyVersion,
                        double.MaxValue, double.MaxValue);

                case KeySmState.Idle:
                case KeySmState.Unknown:
                default:
                    State = KeySmState.Idle;
                    return InputDecision.Pass(DecisionReason.UnknownState, policyVersion);
            }
        }

        /// <summary>钩子重启：清临时状态，不保留任何“已拦截待配对”假设（KSM-013）。</summary>
        public void Reset()
        {
            State = KeySmState.Idle;
            LastDeliveredUpMs = double.MinValue;
            LastDeliveredDownMs = double.MinValue;
            LastObservedDownMs = double.MinValue;
            LastObservedUpMs = double.MinValue;
            SuppressedPressStartedMs = double.MinValue;
        }
    }
}
