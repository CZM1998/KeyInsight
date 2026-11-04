using System;
using System.Collections.Generic;
using System.IO;

namespace KeyInsight.Tests
{
    /// <summary>独立测试框架：无 NuGet 依赖，csc.exe 可编译；退出码 0 = 全部通过。</summary>
    public sealed class TestCase
    {
        public readonly string Suite;
        public readonly string Name;
        public readonly Action Body;

        public TestCase(string suite, string name, Action body)
        {
            Suite = suite;
            Name = name;
            Body = body;
        }
    }

    public static class TestRegistry
    {
        public static readonly List<TestCase> All = new List<TestCase>();

        public static void Add(string suite, string name, Action body)
        {
            All.Add(new TestCase(suite, name, body));
        }
    }

    /// <summary>断言失败抛出异常，由框架捕获并标记 FAIL。</summary>
    public static class Check
    {
        public static void True(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        public static void Equal(long expected, long actual, string context)
        {
            if (expected != actual)
                throw new Exception(context + ": expected=" + expected + " actual=" + actual);
        }

        public static void Equal(int expected, int actual, string context)
        {
            if (expected != actual)
                throw new Exception(context + ": expected=" + expected + " actual=" + actual);
        }

        public static void Equal(bool expected, bool actual, string context)
        {
            if (expected != actual)
                throw new Exception(context + ": expected=" + expected + " actual=" + actual);
        }

        public static void Equal(string expected, string actual, string context)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                throw new Exception(context + ": expected=[" + expected + "] actual=[" + actual + "]");
        }

        public static void Near(double expected, double actual, double tolerance, string context)
        {
            if (Math.Abs(expected - actual) > tolerance)
                throw new Exception(context + ": expected=" + expected + " actual=" + actual + " tol=" + tolerance);
        }
    }

    public static class TestPaths
    {
        public static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "KeyInsightTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static void Delete(string dir)
        {
            try { if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch { }
        }
    }

    internal static class TestMain
    {
        private static int Main(string[] args)
        {
            RegisterAll();

            string suite = args.Length > 0 ? args[0].Trim() : "All";
            bool all = string.Equals(suite, "All", StringComparison.OrdinalIgnoreCase);
            int pass = 0;
            int fail = 0;

            foreach (TestCase tc in TestRegistry.All)
            {
                if (!all && !string.Equals(tc.Suite, suite, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    tc.Body();
                    pass++;
                    Console.WriteLine("[PASS] " + tc.Suite + "/" + tc.Name);
                }
                catch (Exception ex)
                {
                    fail++;
                    Console.WriteLine("[FAIL] " + tc.Suite + "/" + tc.Name + " :: " + ex.Message);
                }
            }

            Console.WriteLine("suite=" + suite + " passed=" + pass + " failed=" + fail);
            return fail == 0 ? 0 : 1;
        }

        private static void RegisterAll()
        {
            KeyInsight.Tests.Core.LegacyPolicyAdapterTests.Register();
            KeyInsight.Tests.Core.ConfigCompatTests.Register();
            KeyInsight.Tests.Core.KeyIdentityAndSnapshotTests.Register();
            KeyInsight.Tests.Core.KeyStateMachineTests.Register();
            KeyInsight.Tests.Core.ProfileAndStrategyTests.Register();
            KeyInsight.Tests.Core.ModuleCompletionTests.Register();
            KeyInsight.Tests.Core.WheelAccumulatorTests.Register();
            KeyInsight.Tests.Telemetry.TelemetryTests.Register();
            KeyInsight.Tests.Storage.KdsCompatibilityTests.Register();
            KeyInsight.Tests.Storage.CsvSnapshotTests.Register();
            KeyInsight.Tests.Storage.SidecarAndMigrationTests.Register();
            KeyInsight.Tests.Storage.DeviceCenterTests.Register();
            KeyInsight.Tests.Storage.DeviceAnalyticsTests.Register();
            KeyInsight.Tests.Storage.DeviceExtTests.Register();
        }
    }
}
