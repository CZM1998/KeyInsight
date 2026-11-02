using System;
using System.Collections.Generic;
using System.Globalization;
using KeyInsight.Core.Input;

namespace KeyInsight.Core.Policy
{
    public enum PolicyMode
    {
        ObserveOnly = 0,        // 只观察不拦截：记录候选，绝不改变输入
        LegacyCompatible = 1,   // 与 v2.1.x 行为一致（默认），并并行运行保守策略影子对比
        PerKeyConservative = 2  // 仅对用户显式启用的按键启用状态机过滤，其余键一律放行
    }

    public struct PerKeyRule
    {
        public bool Enabled;
        public int ThresholdMs;
    }

    /// <summary>
    /// 不可变策略快照（P0-03）：构建完成后整体替换共享引用；钩子线程只读取，绝不写入配置文件。
    /// 版本号单调递增，用于钩子线程检测快照更替与统计口径标注。
    /// </summary>
    public sealed class PolicySnapshot
    {
        public readonly int Version;
        public readonly PolicyMode Mode;
        public readonly bool FilterEnabled;
        public readonly bool SkipInjected;
        public readonly int GlobalThresholdMs;
        public readonly string[] FilterAppWhitelist; // 前台进程名（小写）；命中则该应用内永不拦截
        private readonly Dictionary<PhysicalKeyId, PerKeyRule> _perKey;

        public static readonly PolicySnapshot Default = new PolicySnapshot(0, PolicyMode.LegacyCompatible,
            true, true, 25, null, null);

        public PolicySnapshot(int version, PolicyMode mode, bool filterEnabled, bool skipInjected,
            int globalThresholdMs, Dictionary<PhysicalKeyId, PerKeyRule> perKey, string[] filterAppWhitelist)
        {
            Version = version;
            Mode = mode;
            FilterEnabled = filterEnabled;
            SkipInjected = skipInjected;
            GlobalThresholdMs = globalThresholdMs < 1 ? 1 : (globalThresholdMs > 100 ? 100 : globalThresholdMs);
            _perKey = perKey ?? new Dictionary<PhysicalKeyId, PerKeyRule>();
            FilterAppWhitelist = filterAppWhitelist ?? new string[0];
        }

        public bool TryGetRule(PhysicalKeyId key, out PerKeyRule rule)
        {
            return _perKey.TryGetValue(key, out rule);
        }

        /// <summary>规则全集的只读拷贝（快照不可变，供 PolicyController 复制/序列化）。</summary>
        public List<KeyValuePair<PhysicalKeyId, PerKeyRule>> SnapshotRules()
        {
            return new List<KeyValuePair<PhysicalKeyId, PerKeyRule>>(_perKey);
        }

        public int PerKeyRuleCount
        {
            get { return _perKey.Count; }
        }

        public bool IsAppWhitelisted(string appName)
        {
            if (FilterAppWhitelist.Length == 0 || string.IsNullOrEmpty(appName)) return false;
            for (int i = 0; i < FilterAppWhitelist.Length; i++)
            {
                if (string.Equals(FilterAppWhitelist[i], appName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>把 JSON 字典键（"vk=45;scan=18;ext=0"）解析回 PhysicalKeyId 的辅助入口。</summary>
        public static Dictionary<PhysicalKeyId, PerKeyRule> ParseRules(
            IList<KeyValuePair<string, PerKeyRule>> entries)
        {
            Dictionary<PhysicalKeyId, PerKeyRule> map = new Dictionary<PhysicalKeyId, PerKeyRule>();
            if (entries == null) return map;
            for (int i = 0; i < entries.Count; i++)
            {
                PhysicalKeyId id;
                if (!PhysicalKeyId.TryParse(entries[i].Key, out id)) continue;
                map[id] = entries[i].Value;
            }
            return map;
        }

        public static string ThresholdText(int thresholdMs)
        {
            return thresholdMs.ToString(CultureInfo.InvariantCulture) + "ms";
        }
    }
}
