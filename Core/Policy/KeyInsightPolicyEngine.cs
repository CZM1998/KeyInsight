using System;
using System.Collections.Generic;
using KeyInsight.Core.Input;

namespace KeyInsight.Core.Policy
{
    public interface IKeyboardPolicyEngine
    {
        InputDecision Evaluate(NormalizedKeyEvent input);
        void UpdatePolicy(PolicySnapshot snapshot);
        void ResetForHookRestart();

        // 影子对比与诊断计数（结构化摘要，不含输入内容）
        long ShadowEvaluations { get; }
        long ShadowLegacyPassConservativeSuppress { get; }
        long ShadowLegacySuppressConservativePass { get; }
        long BounceCandidates { get; }
        long MalformedEvents { get; }
        long PolicyErrors { get; }
        long AppWhitelistPasses { get; }
    }

    /// <summary>
    /// 统一策略引擎（P0-03/P0-04）：
    /// - ObserveOnly：保守状态机只推进与计数，绝不拦截；旧策略不运行。
    /// - LegacyCompatible（默认）：LegacyPolicyAdapter 决策（与 v2.1.x 逐事件一致）；
    ///   已配置的单键规则并行跑保守影子对比，仅计数、不拦截。
    /// - PerKeyConservative：只有显式启用规则且命中抖动窗口的键才被拦截；其余键一律放行。
    /// 实例只应被键盘钩子线程访问。任何内部异常 → Fail Open 放行并计数（KSM-019）。
    /// </summary>
    public sealed class KeyInsightPolicyEngine : IKeyboardPolicyEngine
    {
        private PolicySnapshot _snapshot = PolicySnapshot.Default;
        private readonly LegacyPolicyAdapter _legacy;
        private readonly Dictionary<PhysicalKeyId, KeyStateMachine> _machines =
            new Dictionary<PhysicalKeyId, KeyStateMachine>();

        private long _shadowEvals;
        private long _shadowLegacyPassConsSuppress;
        private long _shadowLegacySuppressConsPass;
        private long _bounceCandidates;
        private long _malformed;
        private long _policyErrors;
        private long _whitelistPasses;

        public KeyInsightPolicyEngine(PolicySnapshot initial)
        {
            _snapshot = initial ?? PolicySnapshot.Default;
            _legacy = new LegacyPolicyAdapter(_snapshot.GlobalThresholdMs);
        }

        public long ShadowEvaluations { get { return System.Threading.Interlocked.Read(ref _shadowEvals); } }
        public long ShadowLegacyPassConservativeSuppress { get { return System.Threading.Interlocked.Read(ref _shadowLegacyPassConsSuppress); } }
        public long ShadowLegacySuppressConservativePass { get { return System.Threading.Interlocked.Read(ref _shadowLegacySuppressConsPass); } }
        public long BounceCandidates { get { return System.Threading.Interlocked.Read(ref _bounceCandidates); } }
        public long MalformedEvents { get { return System.Threading.Interlocked.Read(ref _malformed); } }
        public long PolicyErrors { get { return System.Threading.Interlocked.Read(ref _policyErrors); } }
        public long AppWhitelistPasses { get { return System.Threading.Interlocked.Read(ref _whitelistPasses); } }

        public PolicySnapshot CurrentSnapshot { get { return _snapshot; } }

        /// <summary>测试注入：置 true 后 Evaluate 内部抛异常，验证 Fail Open 路径（KSM-019）。生产恒为 false。</summary>
        internal bool FaultInjectForTests;

        /// <summary>状态机字典条目数（测试：策略版本切换不得导致无限增长）。</summary>
        internal int MachineCount { get { return _machines.Count; } }

        public void UpdatePolicy(PolicySnapshot snapshot)
        {
            if (snapshot == null) return;
            _snapshot = snapshot;
            _legacy.SetThreshold(snapshot.GlobalThresholdMs);
        }

        public void ResetForHookRestart()
        {
            _legacy.Reset();
            _machines.Clear();
        }

        public InputDecision Evaluate(NormalizedKeyEvent input)
        {
            try
            {
                return EvaluateInner(input);
            }
            catch
            {
                // Fail Open：任何引擎异常都不能让输入失效（KSM-019）
                System.Threading.Interlocked.Increment(ref _policyErrors);
                return InputDecision.Pass(DecisionReason.PolicyErrorFailOpen, _snapshot.Version);
            }
        }

        private InputDecision EvaluateInner(NormalizedKeyEvent input)
        {
            if (FaultInjectForTests) throw new InvalidOperationException("test fault injection");
            PolicySnapshot snap = _snapshot;

            if (input.Key.VirtualKey <= 0 || input.Key.VirtualKey > 0xFE)
            {
                System.Threading.Interlocked.Increment(ref _malformed);
                return InputDecision.Pass(DecisionReason.Malformed, snap.Version);
            }

            // 应用白名单：该应用内永不拦截（也不更新过滤状态）
            if (snap.IsAppWhitelisted(input.AppName))
            {
                System.Threading.Interlocked.Increment(ref _whitelistPasses);
                return InputDecision.Pass(DecisionReason.AppWhitelist, snap.Version);
            }

            // 注入事件：SkipInjected 时由钩子前置旁路；未旁路时按普通事件处理（与旧实现一致），
            // 但不进入保守影子/状态机，避免污染物理键画像（KSM-010）。
            bool isInjected = input.Injected;

            // 主开关关闭：全部放行。已处于 SuppressedPress 的键保留配对清理责任（KSM-011，
            // 见保守路径的 Up 处理；旧策略无此状态，直接透传与 v2.1.x 一致）。
            if (!snap.FilterEnabled)
            {
                PerKeyRule rule;
                bool hasRule = !isInjected && snap.TryGetRule(input.Key, out rule) && rule.Enabled;
                if (input.Action == InputAction.Up && hasRule)
                {
                    KeyStateMachine sm = GetMachine(input.Key);
                    InputDecision d = sm.EvaluateUp(input.MonoMs, snap.Version);
                    if (d.Kind == DecisionKind.Suppress) return d; // 已拦截 Down 的配对清理
                }
                return InputDecision.Pass(DecisionReason.Disabled, snap.Version);
            }

            switch (snap.Mode)
            {
                case PolicyMode.ObserveOnly:
                    return EvaluateObserveOnly(input, snap, isInjected);

                case PolicyMode.PerKeyConservative:
                    return EvaluatePerKey(input, snap, isInjected);

                default:
                    return EvaluateLegacyWithShadow(input, snap, isInjected);
            }
        }

        private InputDecision EvaluateObserveOnly(NormalizedKeyEvent input, PolicySnapshot snap, bool isInjected)
        {
            if (isInjected) return InputDecision.Pass(DecisionReason.InjectedBypass, snap.Version);

            PerKeyRule rule;
            bool enabled = snap.TryGetRule(input.Key, out rule) && rule.Enabled;
            if (!enabled)
            {
                // 观察模式：未配置规则的键只推进状态并记录候选，不拦截
                if (input.Action == InputAction.Down)
                {
                    KeyStateMachine sm = GetMachine(input.Key);
                    InputDecision d = sm.EvaluateDown(input.MonoMs, rule.Enabled ? rule.ThresholdMs : snap.GlobalThresholdMs, snap.Version);
                    if (d.Kind == DecisionKind.Suppress)
                    {
                        System.Threading.Interlocked.Increment(ref _bounceCandidates);
                        return InputDecision.Pass(DecisionReason.CandidateBounce, snap.Version); // 观察模式绝不拦截
                    }
                    return d;
                }
                if (input.Action == InputAction.Up)
                {
                    KeyStateMachine sm = GetMachine(input.Key);
                    InputDecision d = sm.EvaluateUp(input.MonoMs, snap.Version);
                    if (d.Kind == DecisionKind.Suppress)
                    {
                        System.Threading.Interlocked.Increment(ref _bounceCandidates);
                        return InputDecision.Pass(DecisionReason.CandidateBounce, snap.Version);
                    }
                    return d;
                }
                return InputDecision.Pass(DecisionReason.Normal, snap.Version);
            }

            // 与上相同（观察模式对启用规则的键也只记录）
            if (input.Action == InputAction.Down)
            {
                KeyStateMachine sm = GetMachine(input.Key);
                InputDecision d = sm.EvaluateDown(input.MonoMs, rule.ThresholdMs, snap.Version);
                if (d.Kind == DecisionKind.Suppress)
                {
                    System.Threading.Interlocked.Increment(ref _bounceCandidates);
                    return InputDecision.Pass(DecisionReason.CandidateBounce, snap.Version);
                }
                return d;
            }
            KeyStateMachine smUp = GetMachine(input.Key);
            InputDecision dUp = smUp.EvaluateUp(input.MonoMs, snap.Version);
            if (dUp.Kind == DecisionKind.Suppress)
            {
                System.Threading.Interlocked.Increment(ref _bounceCandidates);
                return InputDecision.Pass(DecisionReason.CandidateBounce, snap.Version);
            }
            return dUp;
        }

        private InputDecision EvaluateLegacyWithShadow(NormalizedKeyEvent input, PolicySnapshot snap, bool isInjected)
        {
            InputDecision legacy;
            if (input.Action == InputAction.Down)
            {
                double gapDown, gapUp;
                bool suppress = _legacy.EvaluateDown(input.MonoMs, input.LegacyVk, out gapDown, out gapUp);
                legacy = suppress
                    ? InputDecision.Suppress(DecisionReason.CandidateBounce, snap.Version, gapDown, gapUp)
                    : InputDecision.Pass(DecisionReason.Normal, snap.Version);
                legacy.GapDownMs = gapDown;
                legacy.GapUpMs = gapUp;
            }
            else
            {
                _legacy.ObserveUp(input.MonoMs, input.LegacyVk);
                legacy = InputDecision.Pass(DecisionReason.Normal, snap.Version);
            }

            // 影子对比：对显式启用规则的非注入键并行推进保守状态机，仅统计分歧
            if (!isInjected)
            {
                PerKeyRule rule;
                if (snap.TryGetRule(input.Key, out rule) && rule.Enabled)
                {
                    KeyStateMachine sm = GetMachine(input.Key);
                    InputDecision shadow = input.Action == InputAction.Down
                        ? sm.EvaluateDown(input.MonoMs, rule.ThresholdMs, snap.Version)
                        : sm.EvaluateUp(input.MonoMs, snap.Version);
                    System.Threading.Interlocked.Increment(ref _shadowEvals);
                    if (input.Action == InputAction.Down)
                    {
                        if (shadow.Kind == DecisionKind.Suppress && legacy.Kind == DecisionKind.Pass)
                            System.Threading.Interlocked.Increment(ref _shadowLegacyPassConsSuppress);
                        else if (shadow.Kind == DecisionKind.Pass && legacy.Kind == DecisionKind.Suppress)
                            System.Threading.Interlocked.Increment(ref _shadowLegacySuppressConsPass);
                    }
                }
            }

            return legacy;
        }

        private InputDecision EvaluatePerKey(NormalizedKeyEvent input, PolicySnapshot snap, bool isInjected)
        {
            if (isInjected) return InputDecision.Pass(DecisionReason.InjectedBypass, snap.Version);

            PerKeyRule rule;
            bool enabled = snap.TryGetRule(input.Key, out rule) && rule.Enabled;
            if (!enabled)
            {
                // 未显式启用的键：一律放行（设计 §4.2 第一行）
                if (input.Action == InputAction.Up)
                {
                    // 清理可能残留的旧状态，保持“Up 不泄露”约束
                    KeyStateMachine sm = GetMachine(input.Key);
                    InputDecision d = sm.EvaluateUp(input.MonoMs, snap.Version);
                    if (d.Kind == DecisionKind.Suppress) return d;
                }
                return InputDecision.Pass(DecisionReason.Normal, snap.Version);
            }

            KeyStateMachine machine = GetMachine(input.Key);
            InputDecision decision = input.Action == InputAction.Down
                ? machine.EvaluateDown(input.MonoMs, rule.ThresholdMs, snap.Version)
                : machine.EvaluateUp(input.MonoMs, snap.Version);
            if (decision.Kind == DecisionKind.Suppress)
            {
                System.Threading.Interlocked.Increment(ref _bounceCandidates);
            }
            return decision;
        }

        private KeyStateMachine GetMachine(PhysicalKeyId key)
        {
            KeyStateMachine sm;
            if (!_machines.TryGetValue(key, out sm))
            {
                sm = new KeyStateMachine();
                _machines[key] = sm;
            }
            return sm;
        }
    }
}
