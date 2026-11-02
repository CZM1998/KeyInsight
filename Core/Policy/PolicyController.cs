using System;
using System.Collections.Generic;
using System.Threading;
using KeyInsight.Core.Input;
using KeyInsight.Core.Telemetry;

namespace KeyInsight.Core.Policy
{
    /// <summary>
    /// 策略快照交换中心（P0-03）：UI/配置线程构建新快照并整体替换引用；
    /// 钩子线程只读 Current。版本号由本类分配，单调递增。
    /// </summary>
    public static class PolicyController
    {
        private static int _version = 1;
        private static PolicySnapshot _current = PolicySnapshot.Default;
        private static KeyPolicyStore _store;

        /// <summary>挂接单键策略存储（Program 启动时一次）；Refresh(Config) 自动带上规则。</summary>
        public static void AttachStore(KeyPolicyStore store)
        {
            _store = store;
        }

        public static PolicySnapshot Current
        {
            get { return Volatile.Read(ref _current); }
        }

        public static void Update(PolicySnapshot snapshot)
        {
            if (snapshot == null) return;
            int v = Interlocked.Increment(ref _version);
            // 以传入快照为模板重建，保证版本号由控制器分配且只增
            PolicySnapshot next = new PolicySnapshot(v, snapshot.Mode, snapshot.FilterEnabled,
                snapshot.SkipInjected, snapshot.GlobalThresholdMs,
                CopyRules(snapshot), snapshot.FilterAppWhitelist);
            Volatile.Write(ref _current, next);
        }

        public static PolicySnapshot BuildFromConfig(Config cfg)
        {
            return BuildFromConfig(cfg, null);
        }

        /// <summary>从 Config（+可选单键规则）构建快照。UI 线程调用。</summary>
        public static PolicySnapshot BuildFromConfig(Config cfg, KeyPolicyStore store)
        {
            PolicyMode mode = (PolicyMode)Math.Max(0, Math.Min(2, (int)cfg.PolicyMode));
            Dictionary<PhysicalKeyId, PerKeyRule> rules = new Dictionary<PhysicalKeyId, PerKeyRule>();
            if (store != null)
            {
                List<KeyPolicyEntry> entries = store.Entries;
                for (int i = 0; i < entries.Count; i++)
                {
                    KeyPolicyEntry e = entries[i];
                    if (!e.Enabled) continue;
                    PhysicalKeyId id = new PhysicalKeyId(e.VirtualKey, e.ScanCode, e.Extended);
                    PerKeyRule rule = new PerKeyRule();
                    rule.Enabled = true;
                    rule.ThresholdMs = Math.Max(1, Math.Min(100, e.ThresholdMs));
                    rules[id] = rule;
                }
            }
            string[] whitelist = ParseWhitelist(cfg.FilterAppWhitelist);
            int v = Volatile.Read(ref _current).Version;
            return new PolicySnapshot(v, mode, cfg.FilterEnabled, cfg.SkipInjected,
                cfg.ThresholdMs, rules, whitelist);
        }

        public static void Refresh(Config cfg)
        {
            Update(BuildFromConfig(cfg, _store));
        }

        public static void Refresh(Config cfg, KeyPolicyStore store)
        {
            Update(BuildFromConfig(cfg, store));
        }

        public static string[] ParseWhitelist(string text)
        {
            if (string.IsNullOrEmpty(text)) return new string[0];
            string[] parts = text.Split(new char[] { ',', ';', '|' });
            List<string> list = new List<string>();
            for (int i = 0; i < parts.Length; i++)
            {
                string s = parts[i].Trim();
                if (s.Length == 0) continue;
                if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
                list.Add(s.ToLowerInvariant());
            }
            return list.ToArray();
        }

        private static Dictionary<PhysicalKeyId, PerKeyRule> CopyRules(PolicySnapshot snapshot)
        {
            Dictionary<PhysicalKeyId, PerKeyRule> copy = new Dictionary<PhysicalKeyId, PerKeyRule>();
            // 通过遍历可达规则集：快照内部字典不直接暴露，用已知接口拿不到全部键，
            // 因此控制器直接以引用复用（快照不可变，共享安全）。
            // 这里复制 entries 以保持“整体替换”语义清晰。
            List<KeyValuePair<PhysicalKeyId, PerKeyRule>> all = snapshot.SnapshotRules();
            for (int i = 0; i < all.Count; i++) copy[all[i].Key] = all[i].Value;
            return copy;
        }
    }
}
