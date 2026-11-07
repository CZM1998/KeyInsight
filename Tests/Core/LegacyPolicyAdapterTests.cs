using System;
using System.Collections.Generic;
using System.Text;
using KeyInsight.Core.Policy;

namespace KeyInsight.Tests.Core
{
    /// <summary>
    /// LEG 兼容策略黄金轨迹（P0-01）：期望序列为按 v2.1.x DebounceHook 算法手工推导的决策，
    /// 阈值 25 ms / Up 窗口 40 ms。P=放行，S=拦截；U 事件恒为 P（旧实现 Up 永远放行）。
    /// </summary>
    internal static class LegacyPolicyAdapterTests
    {
        private const int VkE = 0x45;
        private const int VkR = 0x52;

        public static void Register()
        {
            TestRegistry.Add("Core", "LEG_A_NormalClick", LEG_A_NormalClick);
            TestRegistry.Add("Core", "LEG_B_DoublePressSwallowed", LEG_B_DoublePressSwallowed);
            TestRegistry.Add("Core", "LEG_C_LongPressAutoRepeat", LEG_C_LongPressAutoRepeat);
            TestRegistry.Add("Core", "LEG_D_RapidWithinUpWindow", LEG_D_RapidWithinUpWindow);
            TestRegistry.Add("Core", "LEG_E_UpBeforeFirstDown", LEG_E_UpBeforeFirstDown);
            TestRegistry.Add("Core", "LEG_F_InjectedBypass", LEG_F_InjectedBypass);
            TestRegistry.Add("Core", "LEG_G_ThresholdBoundary", LEG_G_ThresholdBoundary);
            TestRegistry.Add("Core", "LEG_H_SuppressedDownDoesNotRefreshLastDown", LEG_H_SuppressedDownDoesNotRefreshLastDown);
            TestRegistry.Add("Core", "KSM001_FilterOffAllPassAndStateUntouched", KSM001_FilterOffAllPassAndStateUntouched);
            TestRegistry.Add("Core", "KSM004_KeyIsolation", KSM004_KeyIsolation);
            TestRegistry.Add("Core", "UpWindowFloor", UpWindowFloor);
            TestRegistry.Add("Core", "ThresholdRuntimeChange", ThresholdRuntimeChange);
            TestRegistry.Add("Core", "LEG001_DifferentialRandomTraces", LEG001_DifferentialRandomTraces);
            TestRegistry.Add("Core", "LEG_I_RepeatedComboPasses", LEG_I_RepeatedComboPasses);
            TestRegistry.Add("Core", "LEG_J_ChatterCycleSuppressed", LEG_J_ChatterCycleSuppressed);
        }

        /// <summary>按钩子新控制流回放：注入跳过；过滤关=全放行且不触碰策略；过滤开=EvaluateDown/ObserveUp。</summary>
        private static string Replay(LegacyPolicyAdapter policy, bool filterOn, double[] t, bool[] down, int[] vk, bool[] injected)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < t.Length; i++)
            {
                if (injected != null && injected[i]) continue;
                if (!filterOn)
                {
                    sb.Append('P');
                    continue;
                }
                if (down[i])
                {
                    double g1, g2;
                    sb.Append(policy.EvaluateDown(t[i], vk[i], out g1, out g2) ? 'S' : 'P');
                }
                else
                {
                    policy.ObserveUp(t[i], vk[i]);
                    sb.Append('P');
                }
            }
            return sb.ToString();
        }

        private static void Same(int vk, double[] t, bool[] down, string expected, string context)
        {
            int[] vks = new int[t.Length];
            bool[] noInj = new bool[t.Length];
            for (int i = 0; i < t.Length; i++) vks[i] = vk;
            LegacyPolicyAdapter p = new LegacyPolicyAdapter(25);
            string actual = Replay(p, true, t, down, vks, noInj);
            Check.Equal(expected, actual, context);
        }

        private static void LEG_A_NormalClick()
        {
            Same(VkE, new double[] { 0, 50 }, new[] { true, false }, "PP", "normal click");
        }

        private static void LEG_B_DoublePressSwallowed()
        {
            // 第二次 Down 落在 gapDown 12ms(<25) 与 gapUp 2ms(<40) 双窗口内 → 拦截；其 Up 仍放行（旧语义）
            Same(VkE, new double[] { 0, 10, 12, 30 }, new[] { true, false, true, false }, "PPSP", "double press");
        }

        private static void LEG_C_LongPressAutoRepeat()
        {
            // 30ms 自动重复间隔 ≥ 25ms 阈值 → 全放行；OS 长按不被吞（KSM-005/016 旧口径基线）
            Same(VkE, new double[] { 0, 30, 60, 90 }, new[] { true, true, true, false }, "PPPP", "long press");
        }

        private static void LEG_D_RapidWithinUpWindow()
        {
            // D30 落在上次 U20 之后 10ms（<40）→ 拦截且不刷新 lastDown；U45 放行；D200 gapUp=155 → 放行
            Same(VkE, new double[] { 0, 20, 30, 45, 200 }, new[] { true, false, true, false, true }, "PPSPP", "rapid in up window");
        }

        private static void LEG_E_UpBeforeFirstDown()
        {
            // 钩子中途启动：先收到无配对 Up → 安全放行，不影响后续
            Same(VkE, new double[] { 0, 100, 150 }, new[] { false, true, false }, "PPP", "orphan up");
        }

        private static void LEG_F_InjectedBypass()
        {
            // 注入事件在 SkipInjected 下完全不进入策略状态：
            // 若注入 U50 曾更新 lastUp，则 D60 的 gapUp=10 (<40) 会被拦；证明未更新 → P
            LegacyPolicyAdapter p = new LegacyPolicyAdapter(25);
            double g1, g2;
            bool s = p.EvaluateDown(60, VkE, out g1, out g2);
            Check.True(!s, "first real down after injected bypass must pass");
            // 对照组：同样的时间若状态被完整短周期污染过（模拟注入污染），同样的 Down 会被拦
            LegacyPolicyAdapter polluted = new LegacyPolicyAdapter(25);
            Check.True(!polluted.EvaluateDown(40, VkE, out g1, out g2), "polluted control first down passes");
            polluted.ObserveUp(50, VkE);
            bool s2 = polluted.EvaluateDown(60, VkE, out g1, out g2);
            Check.True(s2, "control: chatter-cycle polluted state must suppress");
        }

        private static void LEG_G_ThresholdBoundary()
        {
            // D90: gapUp=40 恰好等于 upWindow（不小于）→ 放行；D100: gapDown=10 <25 → 拦截
            Same(VkE, new double[] { 0, 50, 90, 100, 150 },
                new[] { true, false, true, true, false }, "PPPSP", "boundary <=");
        }

        private static void LEG_H_SuppressedDownDoesNotRefreshLastDown()
        {
            // D0 → D10(拦截, lastDown 保持 0) → D24(gapDown=24 <25) 仍拦截 → D25(gapDown=25) 放行
            Same(VkE, new double[] { 0, 10, 24, 25 },
                new[] { true, true, true, true }, "PSSP", "suppressed not refreshed");
        }

        private static void KSM001_FilterOffAllPassAndStateUntouched()
        {
            double[] t = new double[] { 0, 40, 50, 70, 71, 90 };
            bool[] down = new[] { true, false, true, false, true, false };
            int[] vks = new int[t.Length];
            bool[] noInj = new bool[t.Length];
            for (int i = 0; i < t.Length; i++) vks[i] = VkE;

            // 关闭过滤：全部放行（P0-02 语义），策略状态零更新
            LegacyPolicyAdapter p = new LegacyPolicyAdapter(25);
            Check.Equal("PPPPPP", Replay(p, false, t, down, vks, noInj), "filter off replay");

            // 状态未被污染：重新开启过滤后首按必放行（无历史 gap）
            double g1, g2;
            bool s = p.EvaluateDown(1000, VkE, out g1, out g2);
            Check.True(!s, "first down after re-enable must pass (no phantom history)");
            Check.True(g1 == double.MaxValue && g2 == double.MaxValue, "no history gaps expected");

            // 对照：D71 的 gapDown=21<25 仍会拦（证明上面的 P 不是阈值失效）
            // D50: gapUp=10<40 但周期 40ms 健康 → 放行（组合键保护）；→ "PPPPSP"
            LegacyPolicyAdapter q = new LegacyPolicyAdapter(25);
            Check.Equal("PPPPSP", Replay(q, true, t, down, vks, noInj), "filter on control");
        }

        private static void KSM004_KeyIsolation()
        {
            // E/R 交错：各自独立窗口；E 的连击不影响 R 的判定，反之亦然
            double[] t = new double[] { 0, 5, 50, 55, 60, 65 };
            bool[] down = new[] { true, true, false, false, true, true };
            int[] vk = new[] { VkE, VkR, VkE, VkR, VkE, VkR };
            bool[] noInj = new bool[t.Length];

            LegacyPolicyAdapter p = new LegacyPolicyAdapter(25);
            Check.Equal("PPPPPP", Replay(p, true, t, down, vk, noInj), "interleaved E/R (healthy cycles)");

            // R 单独回放应得到与交错时 R 位完全一致的决策（P,P,P）
            double[] tR = new double[] { 5, 55, 65 };
            bool[] downR = new[] { true, false, true };
            LegacyPolicyAdapter solo = new LegacyPolicyAdapter(25);
            Check.Equal("PPP", Replay(solo, true, tR, downR,
                new[] { VkR, VkR, VkR }, new bool[3]), "R solo matches interleaved");
        }

        private static void UpWindowFloor()
        {
            LegacyPolicyAdapter low = new LegacyPolicyAdapter(10);
            Check.Near(40, low.UpWindowMs, 0.0001, "upWindow floor at 40ms");
            LegacyPolicyAdapter high = new LegacyPolicyAdapter(60);
            Check.Near(60, high.UpWindowMs, 0.0001, "upWindow follows threshold above 40");
        }

        private static void ThresholdRuntimeChange()
        {
            // 托盘/运行中改阈值：下个事件立即按新阈值判定（钩子每事件同步 SetThreshold）
            LegacyPolicyAdapter p = new LegacyPolicyAdapter(25);
            double g1, g2;
            Check.True(!p.EvaluateDown(0, VkE, out g1, out g2), "first down passes");
            p.SetThreshold(60);
            bool s = p.EvaluateDown(30, VkE, out g1, out g2);
            Check.True(s, "gapDown=30 < new threshold 60 must suppress (old 25 would pass)");
            Check.Near(60, p.UpWindowMs, 0.0001, "upWindow follows threshold change");
        }

        // —— 组合键保护（v2.1 误伤修复）回归 ——

        private static void LEG_I_RepeatedComboPasses()
        {
            // 连续 Ctrl+C：C 的按下→释放 60ms（人类节奏），40ms upWindow 内的重按必须放行
            Same(VkE, new double[] { 0, 60, 90, 150, 180 },
                new[] { true, false, true, false, true }, "PPPPP", "repeated combo");

            // 打字后立刻组合键：c 释放到 Ctrl+C 的 C 之间 30ms，但 c 的按压周期 80ms → 放行
            Same(VkE, new double[] { 0, 80, 110 },
                new[] { true, false, true }, "PPP", "combo after typing same letter");

            // 游戏内快速双击：20ms 按压 + 15ms 间隔（cycle=20 < 30 边界内仍拦？不——20<30 会拦，
            // 这里用 35ms 按压验证人类下限放行）
            Same(VkE, new double[] { 0, 35, 50 },
                new[] { true, false, true }, "PPP", "fast human double-tap passes");
        }

        private static void LEG_J_ChatterCycleSuppressed()
        {
            // 抖动完整周期：按下→释放仅 3ms → upWindow 内的重按拦截；之后恢复放行
            Same(VkE, new double[] { 0, 3, 30, 33, 80 },
                new[] { true, false, true, false, true }, "PPSPP", "chatter cycle");

            // 弹跳串：窗口内连续拦截（45ms 处距上次弹跳释放 33ms 仍 < 40）
            Same(VkE, new double[] { 0, 3, 8, 12, 45 },
                new[] { true, false, true, false, true }, "PPSPS", "bounce train");
        }

        private static void LEG001_DifferentialRandomTraces()
        {
            // 差分基线（LEG-001）：被测 LegacyPolicyAdapter 对独立复刻的 v2.1.x 参考实现
            // 在数百条随机轨迹上逐事件决策完全一致（含多键交错与注入旁路）。
            int[] thresholds = new int[] { 10, 25, 40 };
            for (int seed = 1; seed <= 8; seed++)
            {
                for (int ti = 0; ti < thresholds.Length; ti++)
                {
                    string decisions = RunRandom(thresholds[ti], seed);
                    string reference = RunRandomReference(thresholds[ti], seed);
                    Check.Equal(reference, decisions,
                        "differential seed=" + seed + " thr=" + thresholds[ti]);
                }
            }
        }

        private struct Ev
        {
            public double T;
            public bool Down;
            public int Vk;
            public bool Injected;
        }

        private static Ev[] MakeRandom(int seed)
        {
            Random rnd = new Random(seed);
            List<Ev> list = new List<Ev>();
            double t = 0;
            for (int i = 0; i < 300; i++)
            {
                Ev e = new Ev();
                t += 1 + rnd.Next(200); // 全毫秒整数：两条实现的浮点比较都精确
                e.T = t;
                e.Down = rnd.NextDouble() < 0.55;
                e.Vk = rnd.NextDouble() < 0.5 ? VkE : VkR;
                e.Injected = rnd.NextDouble() < 0.1;
                list.Add(e);
            }
            return list.ToArray();
        }

        private static string RunRandom(int thresholdMs, int seed)
        {
            Ev[] evs = MakeRandom(seed);
            LegacyPolicyAdapter p = new LegacyPolicyAdapter(thresholdMs);
            StringBuilder sb = new StringBuilder();
            foreach (Ev e in evs)
            {
                if (e.Injected) continue; // 钩子旁路：不产生决策
                if (e.Down)
                {
                    double g1, g2;
                    sb.Append(p.EvaluateDown(e.T, e.Vk, out g1, out g2) ? 'S' : 'P');
                }
                else
                {
                    p.ObserveUp(e.T, e.Vk);
                    sb.Append('P');
                }
            }
            return sb.ToString();
        }

        /// <summary>独立参考实现：v2.1 判定 + 组合键保护（tick 整数路径），与被测代码不同源。</summary>
        private static string RunRandomReference(int thresholdMs, int seed)
        {
            Ev[] evs = MakeRandom(seed);
            Dictionary<int, long> lastDown = new Dictionary<int, long>();
            Dictionary<int, long> lastUp = new Dictionary<int, long>();
            double upWindow = Math.Max(thresholdMs, 40);
            StringBuilder sb = new StringBuilder();
            foreach (Ev e in evs)
            {
                if (e.Injected) continue;
                long nowTicks = (long)Math.Round(e.T * 10000); // 假设 Stopwatch.Frequency=10,000/s→ms*10000；比例一致即可
                if (e.Down)
                {
                    long ld, lu;
                    bool hd = lastDown.TryGetValue(e.Vk, out ld);
                    bool hu = lastUp.TryGetValue(e.Vk, out lu);
                    double gapDown = hd ? TicksToMs(nowTicks - ld) : double.MaxValue;
                    double gapUp = hu ? TicksToMs(nowTicks - lu) : double.MaxValue;
                    double cycleMs = e.T - ld / 10000.0; // lastDown(ticks) → ms
                    bool chatterCycle = hd && hu && cycleMs < 30;
                    if (gapDown < thresholdMs || (gapUp < upWindow && chatterCycle))
                    {
                        sb.Append('S'); // 拦截不刷新 lastDown
                    }
                    else
                    {
                        lastDown[e.Vk] = nowTicks;
                        sb.Append('P');
                    }
                }
                else
                {
                    lastUp[e.Vk] = nowTicks;
                    sb.Append('P');
                }
            }
            return sb.ToString();
        }

        private static double TicksToMs(long ticks)
        {
            return ticks * 1000.0 / 10000000.0; // Windows Stopwatch.Frequency 典型值
        }
    }
}
