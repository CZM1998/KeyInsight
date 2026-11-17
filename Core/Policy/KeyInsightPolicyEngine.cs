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
    /// - ObserveOnly：只观察实际投递周期并在 Down 上记录候选，绝不拦截。
    /// - LegacyCompatible（默认）：LegacyPolicyAdapter 决策（与 v2.1.x 逐事件一致）；
    ///   已配置的单键规则在独立影子状态机上并行运行，仅计数、不拦截。
    /// - PerKeyConservative：只有显式启用规则且命中抖动窗口的键才被拦截；其余键一律放行。
    /// 真实拦截状态（_machines）与影子/观察状态（_hypotheticalMachines）严格分离，
    /// 真实 SuppressedPress 的配对责任在策略/白名单切换后仍保留。
    /// 实例只应被键盘钩子线程访问。任何内部异常 → Fail Open 放行并计数。
    /// </summary>
    public sealed class KeyInsightPolicyEngine : IKeyboardPolicyEngine
    {
        private PolicySnapshot _snapshot = PolicySnapshot.Default;
        private readonly LegacyPolicyAdapter _legacy;
        private readonly Dictionary<PhysicalKeyId, KeyStateMachine> _machines =
            new Dictionary<PhysicalKeyId, KeyStateMachine>();
        private readonly Dictionary<PhysicalKeyId, KeyStateMachine> _hypotheticalMachines =
            new Dictionary<PhysicalKeyId, KeyStateMachine>();

        private long _shadowEvals;
        private long _shadowLegacyPassConsSuppress;
        private long _shadowLegacySuppressConsPass;
        private long _bounceCandidates;
        private long _malformed;
        private long _policyErrors;
        private long _whitelistPasses;
        private long _historyEpoch;

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

        /// <summary>测试注入：置 true 后 Evaluate 内部抛异常，验证 Fail Open 路径。</summary>
        internal bool FaultInjectForTests;

        /// <summary>真实过滤状态机字典条目数（测试：策略版本切换不得导致无限增长）。</summary>
        internal int MachineCount { get { return _machines.Count; } }

        public void UpdatePolicy(PolicySnapshot snapshot)
        {
            if (snapshot == null) return;

            bool transition = HasPolicyTransition(snapshot);
            _snapshot = snapshot;
            _legacy.SetThreshold(snapshot.GlobalThresholdMs);
            if (!transition) return;

            // 真实 SuppressedPress 必须继续等待配对 Up；其他旧历史在新策略下不再可信。
            PrepareActualMachinesForPolicyTransition();
            _hypotheticalMachines.Clear();
            _legacy.Reset();
        }

        public void ResetForHookRestart()
        {
            _legacy.Reset();
            _machines.Clear();
            _hypotheticalMachines.Clear();
        }

        public InputDecision Evaluate(NormalizedKeyEvent input)
        {
            try
            {
                return EvaluateInner(input);
            }
            catch
            {
                // Fail Open：任何引擎异常都不能让输入失效。
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

            bool isInjected = input.Injected;
            bool appWhitelisted = snap.IsAppWhitelisted(input.AppName);
            if (appWhitelisted && !isInjected)
            {
                // 白名单旁路期间不保存物理周期。使用全局纪元惰性作废其他按键的历史，
                // 避免在热路径扫描整个状态机字典。
                _historyEpoch++;
            }

            // 真实保守拦截的配对责任优先于模式/白名单/主开关。只处理同键的物理事件；
            // 过期后丢弃旧机器并继续按当前策略正常分发这次新输入。
            KeyStateMachine actual = null;
            if (!isInjected && TryGetActualMachine(input.Key, out actual) &&
                actual.State == KeySmState.SuppressedPress)
            {
                InputDecision paired;
                if (TryResolveOutstandingConservative(input, snap, actual, out paired))
                {
                    return paired;
                }
                actual = null;
            }

            // 应用白名单：该应用内永不开始新的拦截，也不保留旧的历史周期。
            if (appWhitelisted)
            {
                ClearWhitelistedKey(input.Key, snap);
                System.Threading.Interlocked.Increment(ref _whitelistPasses);
                return InputDecision.Pass(DecisionReason.AppWhitelist, snap.Version);
            }

            // 主开关关闭：全部放行。尚在安全期内的真实 SuppressedPress 已在上面处理。
            if (!snap.FilterEnabled)
            {
                return InputDecision.Pass(DecisionReason.Disabled, snap.Version);
            }

            switch (snap.Mode)
            {
                case PolicyMode.ObserveOnly:
                    return EvaluateObserveOnly(input, snap, isInjected);

                case PolicyMode.PerKeyConservative:
                    return EvaluatePerKey(input, snap, isInjected, actual);

                default:
                    return EvaluateLegacyWithShadow(input, snap, isInjected);
            }
        }

        /// <summary>
        /// 若该键有真实待配对 SuppressedPress，就在任何模式/白名单分发前先处理本次事件。
        /// 返回 true 表示本次事件已被配对决策消费（Suppress），调用方必须直接返回。
        /// 返回 false 表示没有待配对状态，或状态已过期并已丢弃，应继续正常分发。
        /// </summary>
        private bool TryResolveOutstandingConservative(NormalizedKeyEvent input, PolicySnapshot snap, KeyStateMachine machine,
            out InputDecision decision)
        {
            decision = new InputDecision();
            if (input.Action == InputAction.Down)
            {
                decision = machine.EvaluateDown(input.MonoMs, EffectiveThresholdMs(snap, input.Key),
                    snap.Version);
            }
            else if (input.Action == InputAction.Up)
            {
                decision = machine.EvaluateUp(input.MonoMs, snap.Version);
            }
            else
            {
                return false;
            }

            if (decision.Kind == DecisionKind.Suppress)
            {
                if (input.Action == InputAction.Down)
                {
                    System.Threading.Interlocked.Increment(ref _bounceCandidates);
                }

                // 完成配对后若当前策略已不再负责该键，立即丢弃旧历史，避免未来重开时串周期。
                if (input.Action == InputAction.Up && !ActualFilterApplies(snap, input))
                {
                    _machines.Remove(input.Key);
                }
                return true;
            }

            // 过期/时间倒退等 Fail Open 结果：旧机器不可再用，本次输入按新策略正常分发。
            _machines.Remove(input.Key);
            return false;
        }

        private InputDecision EvaluateObserveOnly(NormalizedKeyEvent input, PolicySnapshot snap,
            bool isInjected)
        {
            if (isInjected) return InputDecision.Pass(DecisionReason.InjectedBypass, snap.Version);

            PerKeyRule rule;
            bool enabled = snap.TryGetRule(input.Key, out rule) && rule.Enabled;
            double thresholdMs = enabled ? rule.ThresholdMs : snap.GlobalThresholdMs;

            if (input.Action == InputAction.Down)
            {
                KeyStateMachine machine = GetHypotheticalMachine(input.Key);
                InputDecision observed = machine.ObserveDown(input.MonoMs, thresholdMs, snap.Version);
                if (observed.Reason == DecisionReason.CandidateBounce)
                    System.Threading.Interlocked.Increment(ref _bounceCandidates);
                return observed;
            }

            // 孤立/未观察过的 Up 不创建状态，也绝不会被观察路径拦截。
            KeyStateMachine upMachine;
            if (!TryGetHypotheticalMachine(input.Key, out upMachine))
            {
                return InputDecision.Pass(DecisionReason.UnknownState, snap.Version);
            }
            return upMachine.EvaluateUp(input.MonoMs, snap.Version);
        }

        private InputDecision EvaluateLegacyWithShadow(NormalizedKeyEvent input, PolicySnapshot snap,
            bool isInjected)
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

            // 影子对比：对显式启用规则的非注入键在独立状态机上并行推进，仅统计分歧。
            if (!isInjected)
            {
                PerKeyRule rule;
                if (snap.TryGetRule(input.Key, out rule) && rule.Enabled)
                {
                    KeyStateMachine shadow = GetHypotheticalMachine(input.Key);
                    InputDecision shadowDecision = input.Action == InputAction.Down
                        ? shadow.EvaluateDown(input.MonoMs, rule.ThresholdMs, snap.Version)
                        : shadow.EvaluateUp(input.MonoMs, snap.Version);
                    System.Threading.Interlocked.Increment(ref _shadowEvals);
                    if (input.Action == InputAction.Down)
                    {
                        if (shadowDecision.Kind == DecisionKind.Suppress &&
                            legacy.Kind == DecisionKind.Pass)
                        {
                            System.Threading.Interlocked.Increment(ref _shadowLegacyPassConsSuppress);
                        }
                        else if (shadowDecision.Kind == DecisionKind.Pass &&
                            legacy.Kind == DecisionKind.Suppress)
                        {
                            System.Threading.Interlocked.Increment(ref _shadowLegacySuppressConsPass);
                        }
                    }
                }
            }

            return legacy;
        }

        private InputDecision EvaluatePerKey(NormalizedKeyEvent input, PolicySnapshot snap,
            bool isInjected, KeyStateMachine machine)
        {
            if (isInjected) return InputDecision.Pass(DecisionReason.InjectedBypass, snap.Version);

            PerKeyRule rule;
            if (!snap.TryGetRule(input.Key, out rule) || !rule.Enabled)
                return InputDecision.Pass(DecisionReason.Normal, snap.Version);

            if (input.Action == InputAction.Up)
                return machine == null ? InputDecision.Pass(DecisionReason.UnknownState, snap.Version)
                    : machine.EvaluateUp(input.MonoMs, snap.Version);

            if (machine == null)
            {
                machine = new KeyStateMachine();
                machine.HistoryEpoch = _historyEpoch;
                _machines[input.Key] = machine;
            }

            InputDecision decision = machine.EvaluateDown(input.MonoMs, rule.ThresholdMs, snap.Version);
            if (decision.Kind == DecisionKind.Suppress)
            {
                System.Threading.Interlocked.Increment(ref _bounceCandidates);
            }
            return decision;
        }

        private bool ActualFilterApplies(PolicySnapshot snap, NormalizedKeyEvent input)
        {
            if (input.Injected || !snap.FilterEnabled) return false;
            if (snap.Mode != PolicyMode.PerKeyConservative) return false;
            if (snap.IsAppWhitelisted(input.AppName)) return false;
            PerKeyRule rule;
            return snap.TryGetRule(input.Key, out rule) && rule.Enabled;
        }

        private void ClearWhitelistedKey(PhysicalKeyId key, PolicySnapshot snap)
        {
            KeyStateMachine actual;
            if (_machines.TryGetValue(key, out actual) && actual.State != KeySmState.SuppressedPress)
            {
                _machines.Remove(key);
            }
            _hypotheticalMachines.Remove(key);

            // 旧 adapter 没有逐键清理入口；白名单事件不进入 adapter，因此清空其历史，
            // 保证离开白名单后的第一个事件不会从白名单前的旧周期误拦。
            if (snap.Mode == PolicyMode.LegacyCompatible)
            {
                _legacy.Reset();
            }
        }

        private double EffectiveThresholdMs(PolicySnapshot snap, PhysicalKeyId key)
        {
            PerKeyRule rule;
            if (snap.TryGetRule(key, out rule) && rule.Enabled) return rule.ThresholdMs;
            return snap.GlobalThresholdMs;
        }

        private bool TryGetActualMachine(PhysicalKeyId key, out KeyStateMachine machine)
        {
            if (!_machines.TryGetValue(key, out machine)) return false;
            EnsureActualHistoryEpoch(machine);
            return true;
        }

        private KeyStateMachine GetHypotheticalMachine(PhysicalKeyId key)
        {
            KeyStateMachine machine;
            if (!_hypotheticalMachines.TryGetValue(key, out machine))
            {
                machine = new KeyStateMachine();
                machine.HistoryEpoch = _historyEpoch;
                _hypotheticalMachines[key] = machine;
            }
            else
            {
                EnsureHypotheticalHistoryEpoch(machine);
            }
            return machine;
        }

        private bool TryGetHypotheticalMachine(PhysicalKeyId key, out KeyStateMachine machine)
        {
            if (!_hypotheticalMachines.TryGetValue(key, out machine)) return false;
            EnsureHypotheticalHistoryEpoch(machine);
            return true;
        }

        private void EnsureActualHistoryEpoch(KeyStateMachine machine)
        {
            if (machine.HistoryEpoch == _historyEpoch) return;
            machine.PrepareForPolicyTransition();
            machine.HistoryEpoch = _historyEpoch;
        }

        private void EnsureHypotheticalHistoryEpoch(KeyStateMachine machine)
        {
            if (machine.HistoryEpoch == _historyEpoch) return;
            machine.Reset();
            machine.HistoryEpoch = _historyEpoch;
        }

        private void PrepareActualMachinesForPolicyTransition()
        {
            List<PhysicalKeyId> remove = null;
            foreach (KeyValuePair<PhysicalKeyId, KeyStateMachine> entry in _machines)
            {
                if (entry.Value.State == KeySmState.SuppressedPress)
                {
                    entry.Value.PrepareForPolicyTransition();
                    entry.Value.HistoryEpoch = _historyEpoch;
                    continue;
                }

                if (remove == null) remove = new List<PhysicalKeyId>();
                remove.Add(entry.Key);
            }

            if (remove != null)
            {
                for (int i = 0; i < remove.Count; i++)
                {
                    _machines.Remove(remove[i]);
                }
            }
        }

        private bool HasPolicyTransition(PolicySnapshot next)
        {
            if (_snapshot.Mode != next.Mode ||
                _snapshot.FilterEnabled != next.FilterEnabled ||
                _snapshot.SkipInjected != next.SkipInjected ||
                _snapshot.GlobalThresholdMs != next.GlobalThresholdMs)
            {
                return true;
            }

            if (!WhitelistsEqual(_snapshot.FilterAppWhitelist, next.FilterAppWhitelist))
            {
                return true;
            }

            if (_snapshot.PerKeyRuleCount != next.PerKeyRuleCount) return true;

            List<KeyValuePair<PhysicalKeyId, PerKeyRule>> nextRules = next.SnapshotRules();
            for (int i = 0; i < nextRules.Count; i++)
            {
                PerKeyRule oldRule;
                if (!_snapshot.TryGetRule(nextRules[i].Key, out oldRule)) return true;
                PerKeyRule newRule = nextRules[i].Value;
                if (oldRule.Enabled != newRule.Enabled ||
                    oldRule.ThresholdMs != newRule.ThresholdMs)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool WhitelistsEqual(string[] left, string[] right)
        {
            if (left == null || left.Length == 0)
            {
                return right == null || right.Length == 0;
            }
            if (right == null || left.Length != right.Length) return false;

            for (int i = 0; i < right.Length; i++)
            {
                if (!WhitelistContains(left, right[i])) return false;
            }
            for (int i = 0; i < left.Length; i++)
            {
                if (!WhitelistContains(right, left[i])) return false;
            }
            return true;
        }

        private static bool WhitelistContains(string[] values, string candidate)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (string.Equals(values[i], candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
