using System;
using System.Collections.Generic;
using KeyInsight.Core.Input;
using KeyInsight.Core.Policy;

namespace KeyInsight.Tests.Core
{
    /// <summary>
    /// KSM-001~020（测试计划 §3）在 KeyStateMachine / KeyInsightPolicyEngine 上的确定性回放。
    /// 语法：D(ms)/U(ms) 事件；期望字符串 P=放行 S=拦截。保守策略阈值示例 20ms（非旧策略 25/40）。
    /// </summary>
    internal static class KeyStateMachineTests
    {
        private const int VkE = 0x45;
        private const int VkR = 0x52;
        private static readonly PhysicalKeyId KeyE = new PhysicalKeyId(0x45, 0x12, false);
        private static readonly PhysicalKeyId KeyR = new PhysicalKeyId(0x52, 0x15, false);
        private static readonly PhysicalKeyId MainEnter = new PhysicalKeyId(0x0D, 0x1C, false);
        private static readonly PhysicalKeyId NumpadEnter = new PhysicalKeyId(0x0D, 0x9C, true);

        public static void Register()
        {
            TestRegistry.Add("Core", "KSM001_FilterOff", KSM001);
            TestRegistry.Add("Core", "KSM002_ObserveOnly", KSM002);
            TestRegistry.Add("Core", "KSM003_PerKeySuppressPair", KSM003);
            TestRegistry.Add("Core", "KSM004_KeyIsolation", KSM004);
            TestRegistry.Add("Core", "KSM005_LongPressSafe", KSM005);
            TestRegistry.Add("Core", "KSM006_NoUpLeakWhileSuppressed", KSM006);
            TestRegistry.Add("Core", "KSM007_HookStartOrphanUp", KSM007);
            TestRegistry.Add("Core", "KSM008_TimeRegression", KSM008);
            TestRegistry.Add("Core", "KSM009_TwoEnters", KSM009);
            TestRegistry.Add("Core", "KSM010_InjectedPolicy", KSM010);
            TestRegistry.Add("Core", "KSM011_DisableDuringSuppressedPress", KSM011);
            TestRegistry.Add("Core", "KSM012_UnknownStatePasses", KSM012);
            TestRegistry.Add("Core", "KSM013_RestartResets", KSM013);
            TestRegistry.Add("Core", "KSM014_PerKeyIndependent", KSM014);
            TestRegistry.Add("Core", "KSM015_MalformedEvent", KSM015);
            TestRegistry.Add("Core", "KSM016_AutoRepeatNotFault", KSM016);
            TestRegistry.Add("Core", "KSM017_AltGrCombo", KSM017);
            TestRegistry.Add("Core", "KSM018_StuckPressFailOpen", KSM018);
            TestRegistry.Add("Core", "KSM019_EngineErrorFailOpen", KSM019);
            TestRegistry.Add("Core", "KSM020_BoundaryStrictLess", KSM020);
            TestRegistry.Add("Core", "KSM_AppWhitelist", KSM_AppWhitelist);
            TestRegistry.Add("Core", "KSM_LegacyParityViaEngine", KSM_LegacyParityViaEngine);
        }

        // —— 回放工具 ——

        private static PolicySnapshot Snapshot(PolicyMode mode, int threshold, bool filterOn,
            Dictionary<PhysicalKeyId, PerKeyRule> rules, string[] whitelist, bool skipInjected)
        {
            return new PolicySnapshot(1, mode, filterOn, skipInjected, threshold, rules, whitelist);
        }

        private static Dictionary<PhysicalKeyId, PerKeyRule> RulesFor(params PhysicalKeyId[] keys)
        {
            Dictionary<PhysicalKeyId, PerKeyRule> rules = new Dictionary<PhysicalKeyId, PerKeyRule>();
            foreach (PhysicalKeyId k in keys)
            {
                PerKeyRule r = new PerKeyRule();
                r.Enabled = true;
                r.ThresholdMs = 20;
                rules[k] = r;
            }
            return rules;
        }

        private static InputDecision Eval(KeyInsightPolicyEngine engine, PhysicalKeyId key, bool down, double t, int vk)
        {
            NormalizedKeyEvent ev = new NormalizedKeyEvent();
            ev.Key = key;
            ev.Action = down ? InputAction.Down : InputAction.Up;
            ev.MonoMs = t;
            ev.LegacyVk = vk;
            ev.AppName = "chrome";
            return engine.Evaluate(ev);
        }

        private static string Decisions(KeyInsightPolicyEngine engine, double[] t, bool[] down, PhysicalKeyId key)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < t.Length; i++)
            {
                InputDecision d = Eval(engine, key, down[i], t[i], key.VirtualKey);
                sb.Append(d.Kind == DecisionKind.Suppress ? 'S' : 'P');
            }
            return sb.ToString();
        }

        // —— KSM 用例 ——

        private static void KSM001()
        {
            // 过滤关闭：D0 U40 全部放行；关闭后重放同键也不再拦截
            KeyInsightPolicyEngine e = new KeyInsightPolicyEngine(Snapshot(PolicyMode.PerKeyConservative, 20, false, RulesFor(KeyE), null, true));
            Check.Equal("PP", Decisions(e, new double[] { 0, 40 }, new[] { true, false }, KeyE), "KSM-001 replay");
            Check.Equal("PPPP", Decisions(e, new double[] { 100, 115, 200, 215 }, new[] { true, false, true, false }, KeyE),
                "after disable no suppression");
            Check.Equal(0L, e.BounceCandidates, "no candidates counted while disabled");
        }

        private static void KSM002()
        {
            // 观察模式：合法快速点击不阻挡，只计候选
            KeyInsightPolicyEngine e = new KeyInsightPolicyEngine(Snapshot(PolicyMode.ObserveOnly, 20, true, RulesFor(KeyE), null, true));
            string d = Decisions(e, new double[] { 0, 40, 50, 70 }, new[] { true, false, true, false }, KeyE);
            Check.Equal("PPPP", d, "observe mode never suppresses");
            Check.True(e.BounceCandidates > 0, "candidates observed");
        }

        private static void KSM003()
        {
            // 单键显式启用：D0 U50 D60 U90 → P P S S（第二次 Down/Up 一并过滤）
            KeyInsightPolicyEngine e = new KeyInsightPolicyEngine(Snapshot(PolicyMode.PerKeyConservative, 20, true, RulesFor(KeyE), null, true));
            Check.Equal("PPSS", Decisions(e, new double[] { 0, 50, 60, 90 }, new[] { true, false, true, false }, KeyE),
                "KSM-003");
        }

        private static void KSM004()
        {
            // E/R 交错，状态完全隔离
            KeyInsightPolicyEngine e = new KeyInsightPolicyEngine(Snapshot(PolicyMode.PerKeyConservative, 20, true, RulesFor(KeyE, KeyR), null, true));
            double[] t = new double[] { 0, 5, 50, 55, 60, 65 };
            bool[] down = new[] { true, true, false, false, true, true };
            PhysicalKeyId[] keys = new[] { KeyE, KeyR, KeyE, KeyR, KeyE, KeyR };
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < t.Length; i++)
            {
                InputDecision d = Eval(e, keys[i], down[i], t[i], keys[i].VirtualKey);
                sb.Append(d.Kind == DecisionKind.Suppress ? 'S' : 'P');
            }
            Check.Equal("PPPPSS", sb.ToString(), "KSM-004 interleaved");
        }

        private static void KSM005()
        {
            // 长按自动重复：D0 D450 D510 U900 全放行；重复计入 RepeatCandidate 但不吞
            KeyInsightPolicyEngine e = new KeyInsightPolicyEngine(Snapshot(PolicyMode.PerKeyConservative, 20, true, RulesFor(KeyE), null, true));
            Check.Equal("PPPP", Decisions(e, new double[] { 0, 450, 510, 900 }, new[] { true, true, true, false }, KeyE),
                "KSM-005 long press");
        }

        private static void KSM006()
        {
            // 已过滤 Press 期间的重复 Down 不放行，Up 不泄露
            KeyInsightPolicyEngine e = new KeyInsightPolicyEngine(Snapshot(PolicyMode.PerKeyConservative, 20, true, RulesFor(KeyE), null, true));
            Check.Equal("PPSSS", Decisions(e, new double[] { 0, 50, 60, 80, 100 }, new[] { true, false, true, true, false }, KeyE),
                "KSM-006");
        }

        private static void KSM007()
        {
            // 钩子中途启动：先收到无配对 Up → 安全放行
            KeyInsightPolicyEngine e = new KeyInsightPolicyEngine(Snapshot(PolicyMode.PerKeyConservative, 20, true, RulesFor(KeyE), null, true));
            Check.Equal("PPP", Decisions(e, new double[] { 0, 100, 200 }, new[] { false, true, false }, KeyE), "KSM-007");
        }

        private static void KSM008()
        {
            // 时间戳倒退：不得负间隔误吞，全部保守放行
            KeyInsightPolicyEngine e = new KeyInsightPolicyEngine(Snapshot(PolicyMode.PerKeyConservative, 20, true, RulesFor(KeyE), null, true));
            Check.Equal("PPPP", Decisions(e, new double[] { 100, 80, 200, 250 }, new[] { true, false, true, false }, KeyE),
                "KSM-008");
        }

        private static void KSM009()
        {
            // 两个 Enter 身份互不污染（即使主/小键盘 Enter 都启用了独立策略）
            KeyInsightPolicyEngine e = new KeyInsightPolicyEngine(Snapshot(PolicyMode.PerKeyConservative, 20, true,
                RulesFor(MainEnter, NumpadEnter), null, true));
            Check.Equal("PPSS", Decisions(e, new double[] { 0, 50, 60, 90 }, new[] { true, false, true, false }, NumpadEnter),
                "numpad enter own window");
            Check.Equal("PPSS", Decisions(e, new double[] { 0, 50, 60, 90 }, new[] { true, false, true, false }, MainEnter),
                "main enter own window");
            KeyStateMachine numpadSm = null;
            // 状态隔离的强验证：num enter 已拦截待配对后，主 enter 首按必须放行
            KeyInsightPolicyEngine e2 = new KeyInsightPolicyEngine(Snapshot(PolicyMode.PerKeyConservative, 20, true,
                RulesFor(MainEnter, NumpadEnter), null, true));
            Eval(e2, NumpadEnter, true, 0, 0x1000D);
            Eval(e2, NumpadEnter, false, 50, 0x1000D);
            Eval(e2, NumpadEnter, true, 60, 0x1000D); // suppressed press pending
            InputDecision mainFirst = Eval(e2, MainEnter, true, 65, 0x0D);
            Check.True(mainFirst.Kind == DecisionKind.Pass, "main enter unaffected by numpad pending press");
            Check.True(numpadSm == null, "placeholder");
        }

        private static void KSM010()
        {
            // SkipInjected=true：引擎不应被喂入注入事件（钩子旁路）；SkipInjected=false 时按普通事件处理（旧口径一致）
            KeyInsightPolicyEngine legacy = new KeyInsightPolicyEngine(Snapshot(PolicyMode.LegacyCompatible, 25, true, null, null, false));
            // 注入 D0 U50 D60：gapUp=10 < 40 → 旧实现会拦（SkipInjected=false 时注入也参与过滤）
            Check.Equal("PPS", Decisions(legacy, new double[] { 0, 50, 60 }, new[] { true, false, true }, KeyE),
                "legacy treats injected as normal when skip=false (v2.1.x parity)");

            KeyInsightPolicyEngine perKey = new KeyInsightPolicyEngine(Snapshot(PolicyMode.PerKeyConservative, 20, true, RulesFor(KeyE), null, false));
            NormalizedKeyEvent inj = new NormalizedKeyEvent();
            inj.Key = KeyE;
            inj.Action = InputAction.Down;
            inj.MonoMs = 0;
            inj.Injected = true;
            inj.LegacyVk = VkE;
            InputDecision d = perKey.Evaluate(inj);
            Check.True(d.Kind == DecisionKind.Pass && d.Reason == DecisionReason.InjectedBypass,
                "per-key mode bypasses injected");
        }

        private static void KSM011()
        {
            // 改策略期间处于 SuppressedPress：D0 U50 D60 [Disable] U80 → P P S S（安全配对清理），后续全放行
            KeyInsightPolicyEngine e = new KeyInsightPolicyEngine(Snapshot(PolicyMode.PerKeyConservative, 20, true, RulesFor(KeyE), null, true));
            InputDecision d1 = Eval(e, KeyE, true, 0, VkE);
            InputDecision d2 = Eval(e, KeyE, false, 50, VkE);
            InputDecision d3 = Eval(e, KeyE, true, 60, VkE);
            Check.True(d1.Kind == DecisionKind.Pass && d2.Kind == DecisionKind.Pass && d3.Kind == DecisionKind.Suppress,
                "pre-disable decisions");

            e.UpdatePolicy(Snapshot(PolicyMode.PerKeyConservative, 20, false, RulesFor(KeyE), null, true));
            InputDecision upPaired = Eval(e, KeyE, false, 80, VkE);
            Check.True(upPaired.Kind == DecisionKind.Suppress,
                "KSM-011: suppressed Down's paired Up must be cleaned up, not leaked");

            // 清理完成后的新事件全部放行
            InputDecision nextDown = Eval(e, KeyE, true, 120, VkE);
            InputDecision nextUp = Eval(e, KeyE, false, 150, VkE);
            Check.True(nextDown.Kind == DecisionKind.Pass && nextUp.Kind == DecisionKind.Pass, "post-cleanup passes");
        }

        private static void KSM012()
        {
            // 过滤启用但历史缺失：Up0 Down100 Up200 全放行，不因状态缺失抑制真实输入
            KeyInsightPolicyEngine e = new KeyInsightPolicyEngine(Snapshot(PolicyMode.PerKeyConservative, 20, true, RulesFor(KeyE), null, true));
            Check.Equal("PPP", Decisions(e, new double[] { 0, 100, 200 }, new[] { false, true, false }, KeyE), "KSM-012");
        }

        private static void KSM013()
        {
            // Hook 重启：状态重置后首按不产生重复键
            KeyInsightPolicyEngine e = new KeyInsightPolicyEngine(Snapshot(PolicyMode.PerKeyConservative, 20, true, RulesFor(KeyE), null, true));
            Eval(e, KeyE, true, 0, VkE);
            Eval(e, KeyE, false, 10, VkE);
            Eval(e, KeyE, true, 60, VkE); // suppressed pending
            e.ResetForHookRestart();
            InputDecision first = Eval(e, KeyE, true, 100, VkE);
            Check.True(first.Kind == DecisionKind.Pass, "KSM-013: first down after restart passes");
        }

        private static void KSM014()
        {
            // E 启用、R 未启用：同轨迹 E 过滤、R 全放行
            KeyInsightPolicyEngine e = new KeyInsightPolicyEngine(Snapshot(PolicyMode.PerKeyConservative, 20, true, RulesFor(KeyE), null, true));
            Check.Equal("PPSS", Decisions(e, new double[] { 0, 50, 60, 90 }, new[] { true, false, true, false }, KeyE), "E filtered");
            Check.Equal("PPPP", Decisions(e, new double[] { 0, 50, 60, 90 }, new[] { true, false, true, false }, KeyR),
                "R untouched");
        }

        private static void KSM015()
        {
            // 非法 VK/ScanCode：Pass + 诊断计数，不抛出
            KeyInsightPolicyEngine e = new KeyInsightPolicyEngine(Snapshot(PolicyMode.PerKeyConservative, 20, true, RulesFor(KeyE), null, true));
            Check.Equal("PP", Decisions(e, new double[] { 0, 5 }, new[] { true, true }, new PhysicalKeyId(0, 0, false)), "vk=0 passes");
            Check.Equal("PP", Decisions(e, new double[] { 10, 15 }, new[] { true, true }, new PhysicalKeyId(0x200, 0, false)), "vk>0xFF passes");
            Check.True(e.MalformedEvents >= 4, "malformed counted");
        }

        private static void KSM016()
        {
            // 自动重复结束后的新按下：默认保守放行（不把相邻 Down 当故障）
            KeyInsightPolicyEngine e = new KeyInsightPolicyEngine(Snapshot(PolicyMode.PerKeyConservative, 20, true, RulesFor(KeyE), null, true));
            Check.Equal("PPPPPP", Decisions(e, new double[] { 0, 300, 350, 500, 550, 600 },
                new[] { true, true, true, false, true, false }, KeyE), "KSM-016");
        }

        private static void KSM017()
        {
            // AltGr/组合键序列：默认全放行（修饰键无规则）
            KeyInsightPolicyEngine e = new KeyInsightPolicyEngine(Snapshot(PolicyMode.PerKeyConservative, 20, true, RulesFor(KeyE), null, true));
            PhysicalKeyId lctrl = new PhysicalKeyId(0xA2, 0x1D, false);
            PhysicalKeyId ralt = new PhysicalKeyId(0xA5, 0x38, true);
            PhysicalKeyId[] keys = new[] { lctrl, ralt, KeyE, KeyE, ralt, lctrl };
            bool[] down = new[] { true, true, true, false, false, false };
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < keys.Length; i++)
            {
                InputDecision d = Eval(e, keys[i], down[i], i * 30, keys[i].VirtualKey);
                sb.Append(d.Kind == DecisionKind.Suppress ? 'S' : 'P');
            }
            Check.Equal("PPPPPP", sb.ToString(), "KSM-017 combo untouched");
        }

        private static void KSM018()
        {
            // 抑制后丢失配对 Up：超过过期边界后下一 Down 保守恢复放行，绝不永久锁死
            KeyInsightPolicyEngine e = new KeyInsightPolicyEngine(Snapshot(PolicyMode.PerKeyConservative, 20, true, RulesFor(KeyE), null, true));
            InputDecision d1 = Eval(e, KeyE, true, 0, VkE);
            InputDecision d2 = Eval(e, KeyE, false, 50, VkE);
            InputDecision d3 = Eval(e, KeyE, true, 60, VkE);
            Check.True(d3.Kind == DecisionKind.Suppress, "suppressed press pending");

            // 安全期内仍未恢复
            InputDecision within = Eval(e, KeyE, true, 2000, VkE);
            Check.True(within.Kind == DecisionKind.Suppress, "still suppressed within safe window");

            // 超过 StuckPressExpiryMs：下一 Down 恢复放行
            InputDecision stale = Eval(e, KeyE, true, 60 + KeyStateMachine.StuckPressExpiryMs + 100, VkE);
            Check.True(stale.Kind == DecisionKind.Pass, "KSM-018: fail open after expiry");
            // 后续放行
            InputDecision next = Eval(e, KeyE, true, 60 + KeyStateMachine.StuckPressExpiryMs + 150, VkE);
            Check.True(next.Kind == DecisionKind.Pass, "subsequent passes");
        }

        private static void KSM019()
        {
            // 引擎抛异常 → Fail Open 放行 + 计数（通过测试注入点触发）
            KeyInsightPolicyEngine e = new KeyInsightPolicyEngine(Snapshot(PolicyMode.PerKeyConservative, 20, true, RulesFor(KeyE), null, true));
            e.FaultInjectForTests = true;
            InputDecision d = Eval(e, KeyE, true, 0, VkE);
            e.FaultInjectForTests = false;
            Check.True(d.Kind == DecisionKind.Pass && d.Reason == DecisionReason.PolicyErrorFailOpen,
                "KSM-019 fail open");
            Check.True(e.PolicyErrors >= 1, "error counted");
        }

        private static void KSM020()
        {
            // 边界阈值：间隔恰好 == 阈值 → 放行（统一约定严格 <）；略小于 → 拦截
            KeyInsightPolicyEngine e = new KeyInsightPolicyEngine(Snapshot(PolicyMode.PerKeyConservative, 20, true, RulesFor(KeyE), null, true));
            InputDecision a = Eval(e, KeyE, true, 0, VkE);
            InputDecision b = Eval(e, KeyE, false, 50, VkE);
            InputDecision c = Eval(e, KeyE, true, 70, VkE); // gapUp = 20 恰好
            Check.True(c.Kind == DecisionKind.Pass, "gap == threshold passes (strict <)");

            KeyInsightPolicyEngine e2 = new KeyInsightPolicyEngine(Snapshot(PolicyMode.PerKeyConservative, 20, true, RulesFor(KeyE), null, true));
            Eval(e2, KeyE, true, 0, VkE);
            Eval(e2, KeyE, false, 50, VkE);
            InputDecision c2 = Eval(e2, KeyE, true, 69.9, VkE);
            Check.True(c2.Kind == DecisionKind.Suppress, "gap < threshold suppresses");
        }

        private static void KSM_AppWhitelist()
        {
            // 应用白名单：白名单内永不拦截且不计数候选
            string[] wl = new string[] { "game" };
            KeyInsightPolicyEngine e = new KeyInsightPolicyEngine(Snapshot(PolicyMode.PerKeyConservative, 20, true, RulesFor(KeyE), wl, true));
            NormalizedKeyEvent ev = new NormalizedKeyEvent();
            ev.Key = KeyE;
            ev.Action = InputAction.Down;
            ev.MonoMs = 0;
            ev.LegacyVk = VkE;
            ev.AppName = "game";
            Check.True(e.Evaluate(ev).Kind == DecisionKind.Pass, "whitelisted app passes");
            ev.MonoMs = 5;
            Check.True(e.Evaluate(ev).Kind == DecisionKind.Pass, "whitelisted app passes again");
            Check.True(e.AppWhitelistPasses >= 2, "whitelist pass counted");
            Check.Equal(0L, e.BounceCandidates, "no candidates in whitelisted app");

            ev.AppName = "chrome";
            ev.MonoMs = 1000;
            Check.True(e.Evaluate(ev).Kind == DecisionKind.Pass, "outside whitelist still pass (first)");
            ev.Action = InputAction.Up;
            ev.MonoMs = 1005;
            Check.True(e.Evaluate(ev).Kind == DecisionKind.Pass, "up passes");
            ev.Action = InputAction.Down;
            ev.MonoMs = 1010;
            InputDecision d = e.Evaluate(ev);
            Check.True(d.Kind == DecisionKind.Suppress, "outside whitelist filters (bounce window, gapUp=5<20)");
        }

        private static void KSM_LegacyParityViaEngine()
        {
            // 引擎 LegacyCompatible 模式 = LegacyPolicyAdapter 黄金轨迹（D0 U20 D30 U45 D200 → PPSPP，thr25/upWin40）
            KeyInsightPolicyEngine e = new KeyInsightPolicyEngine(Snapshot(PolicyMode.LegacyCompatible, 25, true, null, null, true));
            Check.Equal("PPSPP", Decisions(e, new double[] { 0, 20, 30, 45, 200 },
                new[] { true, false, true, false, true }, KeyE), "engine legacy: rapid in up window");
            Check.Equal("PPSP", Decisions(e, new double[] { 1000, 1010, 1012, 1030 },
                new[] { true, false, true, false }, KeyE), "engine legacy: double press");
        }
    }
}
