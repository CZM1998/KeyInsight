using System;
using System.Collections.Generic;
using KeyInsight.Core.Input;
using KeyInsight.Core.Policy;

namespace KeyInsight.Tests.Core
{
    /// <summary>P0-03：键身份解析、快照不可变性与版本管理。</summary>
    internal static class KeyIdentityAndSnapshotTests
    {
        public static void Register()
        {
            TestRegistry.Add("Core", "ID_MainVsNumpadEnter", ID_MainVsNumpadEnter);
            TestRegistry.Add("Core", "ID_LeftRightModifiers", ID_LeftRightModifiers);
            TestRegistry.Add("Core", "ID_ArrowClusters", ID_ArrowClusters);
            TestRegistry.Add("Core", "ID_ScanZeroFallback", ID_ScanZeroFallback);
            TestRegistry.Add("Core", "ID_CanonicalScan", ID_CanonicalScan);
            TestRegistry.Add("Core", "ID_ValueEqualityAndParse", ID_ValueEqualityAndParse);
            TestRegistry.Add("Core", "SNAP_ImmutableAndVersioned", SNAP_ImmutableAndVersioned);
            TestRegistry.Add("Core", "SNAP_WhitelistParse", SNAP_WhitelistParse);
            TestRegistry.Add("Core", "SNAP_ControllerMonotonic", SNAP_ControllerMonotonic);
        }

        private static void ID_MainVsNumpadEnter()
        {
            // 主 Enter：vk=0x0D 扫描 0x1C 非扩展；小键盘 Enter：vk=0x0D 扫描 0x9C 扩展
            PhysicalKeyId main = InputIdentityResolver.Resolve(0x0D, 0x1C, 0);
            PhysicalKeyId numpad = InputIdentityResolver.Resolve(0x0D, 0x9C, InputIdentityResolver.LlkhfExtended);
            Check.True(!main.Equals(numpad), "main/numpad enter must differ");
            Check.True(main.GetHashCode() != numpad.GetHashCode() || main.ScanCode != numpad.ScanCode,
                "distinct identities");
        }

        private static void ID_LeftRightModifiers()
        {
            PhysicalKeyId lctrl = InputIdentityResolver.Resolve(0xA2, 0x1D, 0);
            PhysicalKeyId rctrl = InputIdentityResolver.Resolve(0xA3, 0x1D, InputIdentityResolver.LlkhfExtended);
            PhysicalKeyId lalt = InputIdentityResolver.Resolve(0xA4, 0x38, 0);
            PhysicalKeyId ralt = InputIdentityResolver.Resolve(0xA5, 0x38, InputIdentityResolver.LlkhfExtended);
            Check.True(!lctrl.Equals(rctrl), "L/R ctrl differ");
            Check.True(!lalt.Equals(ralt), "L/R alt (AltGr) differ");
            // 右 Shift 是非扩展键（扫描 0x36）
            PhysicalKeyId rshift = InputIdentityResolver.Resolve(0xA1, 0x36, 0);
            Check.True(rshift.Extended == false, "right shift not extended");
        }

        private static void ID_ArrowClusters()
        {
            // 主方向键簇带扩展位；NumLock 关闭时小键盘方向无扩展位 → 身份可区分
            PhysicalKeyId mainLeft = InputIdentityResolver.Resolve(0x25, 0x4B, InputIdentityResolver.LlkhfExtended);
            PhysicalKeyId numpadLeft = InputIdentityResolver.Resolve(0x25, 0x4B, 0);
            Check.True(!mainLeft.Equals(numpadLeft), "arrow clusters distinguishable by ext bit");
        }

        private static void ID_ScanZeroFallback()
        {
            // 合成事件无扫描码：降级为 (vk, 0, ext) 独立身份，不与物理键混同（保守降级规则）
            PhysicalKeyId synthetic = InputIdentityResolver.Resolve(0x45, 0, 0);
            PhysicalKeyId real = InputIdentityResolver.Resolve(0x45, 0x12, 0);
            Check.True(!synthetic.Equals(real), "scan=0 must not alias physical key");
            Check.Equal(0, synthetic.ScanCode, "synthetic scan stays 0");
        }

        private static void ID_CanonicalScan()
        {
            Check.Equal(0x1C, InputIdentityResolver.CanonicalScanFor(0x0D), "enter canonical scan");
            Check.Equal(0x2A, InputIdentityResolver.CanonicalScanFor(0xA0), "left shift canonical scan");
            Check.Equal(0x39, InputIdentityResolver.CanonicalScanFor(0x20), "space canonical scan");
        }

        private static void ID_ValueEqualityAndParse()
        {
            PhysicalKeyId a = new PhysicalKeyId(0x45, 0x12, false);
            PhysicalKeyId b = new PhysicalKeyId(0x45, 0x12, false);
            Check.True(a.Equals(b) && a.GetHashCode() == b.GetHashCode(), "value equality + stable hash");
            Dictionary<PhysicalKeyId, int> dict = new Dictionary<PhysicalKeyId, int>();
            dict[a] = 1;
            dict[b] = 2;
            Check.Equal(1, dict.Count, "dictionary dedupe by value");

            string text = a.ToString();
            PhysicalKeyId c;
            Check.True(PhysicalKeyId.TryParse(text, out c), "parse roundtrip");
            Check.True(a.Equals(c), "parse equals original");
            Check.True(!PhysicalKeyId.TryParse("garbage", out c), "garbage rejected");
        }

        private static void SNAP_ImmutableAndVersioned()
        {
            Dictionary<PhysicalKeyId, PerKeyRule> rules = new Dictionary<PhysicalKeyId, PerKeyRule>();
            PerKeyRule r = new PerKeyRule();
            r.Enabled = true;
            r.ThresholdMs = 20;
            rules[new PhysicalKeyId(0x45, 0x12, false)] = r;

            PolicySnapshot s1 = new PolicySnapshot(5, PolicyMode.PerKeyConservative, true, true, 25, rules, null);
            PerKeyRule got;
            Check.True(s1.TryGetRule(new PhysicalKeyId(0x45, 0x12, false), out got), "rule found");
            Check.Equal(20, got.ThresholdMs, "rule threshold");
            Check.True(!s1.TryGetRule(new PhysicalKeyId(0x52, 0x15, false), out got), "missing rule");

            // 规则集合拷贝后独立
            List<KeyValuePair<PhysicalKeyId, PerKeyRule>> copy = s1.SnapshotRules();
            Check.Equal(1, copy.Count, "snapshot rules copy");
            Check.Equal(25, s1.GlobalThresholdMs, "threshold clamp none");

            PolicySnapshot s2 = new PolicySnapshot(0, PolicyMode.LegacyCompatible, true, true, 999, null, null);
            Check.Equal(100, s2.GlobalThresholdMs, "threshold clamped to 100");
            PolicySnapshot s3 = new PolicySnapshot(0, PolicyMode.LegacyCompatible, true, true, 0, null, null);
            Check.Equal(1, s3.GlobalThresholdMs, "threshold clamped to 1");
        }

        private static void SNAP_WhitelistParse()
        {
            PolicySnapshot s = new PolicySnapshot(1, PolicyMode.LegacyCompatible, true, true, 25, null,
                PolicyController.ParseWhitelist("notepad, chrome.exe ; IDE"));
            Check.True(s.IsAppWhitelisted("notepad"), "notepad whitelisted");
            Check.True(s.IsAppWhitelisted("chrome"), ".exe suffix stripped");
            Check.True(s.IsAppWhitelisted("ide"), "case-insensitive");
            Check.True(!s.IsAppWhitelisted("code"), "others not");
            Check.True(!s.IsAppWhitelisted(null), "null safe");
            Check.True(!s.IsAppWhitelisted(""), "empty safe");
        }

        private static void SNAP_ControllerMonotonic()
        {
            int v0 = PolicyController.Current.Version;
            Config cfg = new Config();
            cfg.ThresholdMs = 30;
            cfg.FilterEnabled = false;
            PolicyController.Refresh(cfg);
            PolicySnapshot after = PolicyController.Current;
            Check.True(after.Version > v0, "version increments on refresh");
            Check.True(after.FilterEnabled == false, "filter from config");
            Check.Equal(30, after.GlobalThresholdMs, "threshold from config");

            // 单键规则进入快照
            KeyPolicyStore store = new KeyPolicyStore(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    "KeyInsightTests_policy_" + Guid.NewGuid().ToString("N") + ".json"));
            KeyPolicyEntry e = store.GetOrCreate(0x45, 0x12, false);
            e.Enabled = true;
            e.ThresholdMs = 15;
            PolicyController.Refresh(cfg, store);
            PolicySnapshot snap = PolicyController.Current;
            PerKeyRule rule;
            Check.True(snap.TryGetRule(new PhysicalKeyId(0x45, 0x12, false), out rule), "per-key rule in snapshot");
            Check.Equal(15, rule.ThresholdMs, "per-key threshold");
            Check.True(snap.Version > after.Version, "version increments again");

            // 还原全局状态，避免影响其他测试
            PolicyController.Refresh(new Config());
            try { System.IO.File.Delete(store.FilePath); } catch { }
        }
    }
}
