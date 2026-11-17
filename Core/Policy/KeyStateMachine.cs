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

    /// <summary>一个物理键的配对与防抖状态，仅供键盘钩子线程使用。</summary>
    public sealed class KeyStateMachine
    {
        // 丢失 Up 后，超过此时长的下一次 Down 可以恢复投递。
        public const int StuckPressExpiryMs = 3000;
        public const double MaxBurstWindowMs = 100.0;

        public KeySmState State;
        public double LastDeliveredUpMs = double.MinValue;
        public double LastDeliveredDownMs = double.MinValue;
        public double LastObservedDownMs = double.MinValue;
        public double LastObservedUpMs = double.MinValue;
        public double SuppressedPressStartedMs = double.MinValue;
        public long RepeatCandidates;
        public long BounceCandidates;
        public long StaleRecoveries;
        internal long HistoryEpoch;

        private double _lastSuppressedShortUpMs = double.MinValue;

        public InputDecision EvaluateDown(double nowMs, double thresholdMs, int policyVersion)
        {
            return EvaluateDown(nowMs, thresholdMs, policyVersion, true);
        }

        public InputDecision ObserveDown(double nowMs, double thresholdMs, int policyVersion)
        {
            return EvaluateDown(nowMs, thresholdMs, policyVersion, false);
        }

        private InputDecision EvaluateDown(double nowMs, double thresholdMs, int version, bool canSuppress)
        {
            if (HasTimeRegression(nowMs))
            {
                Reset();
                DeliverDown(nowMs);
                return InputDecision.Pass(DecisionReason.UnknownState, version);
            }

            double gapDown = LastObservedDownMs == double.MinValue
                ? double.MaxValue : nowMs - LastObservedDownMs;
            LastObservedDownMs = nowMs;
            if (State == KeySmState.DeliveredDown)
            {
                RepeatCandidates++;
                return InputDecision.Pass(DecisionReason.RepeatedWhileHeld, version);
            }

            if (State == KeySmState.SuppressedPress)
            {
                if (nowMs - SuppressedPressStartedMs <= StuckPressExpiryMs)
                {
                    BounceCandidates++;
                    return InputDecision.Suppress(DecisionReason.CandidateBounce, version, gapDown, double.MaxValue);
                }
                StaleRecoveries++;
                Reset();
                DeliverDown(nowMs);
                return InputDecision.Pass(DecisionReason.UnknownState, version);
            }

            double gapUp;
            if (IsChatterCandidate(nowMs, thresholdMs, out gapUp))
            {
                BounceCandidates++;
                if (canSuppress)
                {
                    State = KeySmState.SuppressedPress;
                    SuppressedPressStartedMs = nowMs;
                    return InputDecision.Suppress(DecisionReason.CandidateBounce, version, gapDown, gapUp);
                }

                DeliverDown(nowMs);
                InputDecision candidate = InputDecision.Pass(DecisionReason.CandidateBounce, version);
                candidate.GapDownMs = gapDown;
                candidate.GapUpMs = gapUp;
                return candidate;
            }

            DeliverDown(nowMs);
            return InputDecision.Pass(DecisionReason.Normal, version);
        }

        public InputDecision EvaluateUp(double nowMs, int version)
        {
            if (HasTimeRegression(nowMs))
            {
                Reset();
                LastObservedUpMs = nowMs;
                State = KeySmState.Unknown;
                return InputDecision.Pass(DecisionReason.UnknownState, version);
            }

            LastObservedUpMs = nowMs;
            KeySmState previous = State;
            State = KeySmState.Idle;
            if (previous == KeySmState.SuppressedPress)
            {
                // 只有短促的被拦截按压能延展同一串弹跳。仍待配对的 Up 始终与原 Down 一起拦截。
                if (nowMs - SuppressedPressStartedMs < LegacyPolicyAdapter.ChatterPressMaxMs)
                {
                    _lastSuppressedShortUpMs = nowMs;
                }
                else
                {
                    ClearReleasedCycle();
                }
                SuppressedPressStartedMs = double.MinValue;
                return InputDecision.Suppress(DecisionReason.SuppressedPairedRelease, version,
                    double.MaxValue, double.MaxValue);
            }

            if (previous == KeySmState.DeliveredDown)
            {
                LastDeliveredUpMs = nowMs;
                _lastSuppressedShortUpMs = double.MinValue;
                return InputDecision.Pass(DecisionReason.Normal, version);
            }

            return InputDecision.Pass(DecisionReason.UnknownState, version);
        }

        internal void PrepareForPolicyTransition()
        {
            if (State == KeySmState.SuppressedPress) ClearReleasedCycle();
            else Reset();
        }

        public void Reset()
        {
            State = KeySmState.Idle;
            LastObservedDownMs = double.MinValue;
            LastObservedUpMs = double.MinValue;
            SuppressedPressStartedMs = double.MinValue;
            ClearReleasedCycle();
        }

        private bool IsChatterCandidate(double nowMs, double thresholdMs, out double gapUp)
        {
            gapUp = double.MaxValue;
            if (LastDeliveredUpMs == double.MinValue || LastDeliveredDownMs == double.MinValue ||
                LastDeliveredUpMs - LastDeliveredDownMs >= LegacyPolicyAdapter.ChatterPressMaxMs)
                return false;

            if (nowMs - LastDeliveredUpMs >= MaxBurstWindowMs) return false;
            double reference = _lastSuppressedShortUpMs == double.MinValue
                ? LastDeliveredUpMs : _lastSuppressedShortUpMs;
            gapUp = nowMs - reference;
            return gapUp >= 0 && gapUp < thresholdMs;
        }

        private bool HasTimeRegression(double nowMs)
        {
            return nowMs < LastObservedDownMs || nowMs < LastObservedUpMs;
        }

        private void DeliverDown(double nowMs)
        {
            ClearReleasedCycle();
            LastObservedDownMs = nowMs;
            LastDeliveredDownMs = nowMs;
            State = KeySmState.DeliveredDown;
        }

        private void ClearReleasedCycle()
        {
            LastDeliveredUpMs = double.MinValue;
            LastDeliveredDownMs = double.MinValue;
            _lastSuppressedShortUpMs = double.MinValue;
        }
    }
}
