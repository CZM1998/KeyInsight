using System;
using System.Collections.Generic;
using System.Text;

namespace KeyInsight.Core.Devices
{
    /// <summary>
    /// 设备身份解析（方案 §5.2）。依据优先级：
    /// 显示器 = EDID 厂商+产品+序列号（序列号非零时）→ EDID 厂商+产品+实例序号；
    /// 通用 = ContainerId（同物理机多端口稳定）→ VID/PID + 类（仅型号级，不得当唯一 ID）→ 实例路径。
    /// 输出候选匹配 + 依据 + 置信度；低于阈值不自动合并（防止同型号设备被误归一）。
    /// 纯静态逻辑，可单元测试。
    /// </summary>
    public static class DeviceIdentityResolver
    {
        public const double AutoMergeThreshold = 0.85;
        public const double ReviewThreshold = 0.55;

        /// <summary>身份匹配结论。</summary>
        public sealed class MatchResult
        {
            public DeviceRecord Candidate;      // 最佳候选档案（可为 null = 全新设备）
            public double Confidence;           // 0..1，证据充分度（非损坏概率）
            public readonly List<string> Reasons = new List<string>();
            public bool AutoMerge
            {
                get { return Candidate != null && Confidence >= AutoMergeThreshold; }
            }
            public bool NeedsReview
            {
                get { return Candidate != null && Confidence >= ReviewThreshold && Confidence < AutoMergeThreshold; }
            }
        }

        /// <summary>从快照推导稳定身份键（跨端口/重连尽量一致）。</summary>
        public static string BuildIdentityKey(DeviceSnapshot s)
        {
            if (s == null || string.IsNullOrEmpty(s.InstanceKey)) return "";
            StringBuilder sb = new StringBuilder();
            sb.Append(CategoryPrefix(s.Category)).Append(':');

            if (s.Category == DeviceCategory.Display)
            {
                // 显示器：EDID 三元组 + 实例序号由 Attributes 携带（AttributesJson 由 DisplayAdapter 写入）
                string vendor = JsonField(s.AttributesJson, "edidVendor");
                string product = JsonField(s.AttributesJson, "edidProduct");
                string serial = JsonField(s.AttributesJson, "edidSerial");
                if (!string.IsNullOrEmpty(vendor) && !string.IsNullOrEmpty(product))
                {
                    sb.Append("edid:").Append(vendor).Append(':').Append(product);
                    if (!string.IsNullOrEmpty(serial) && serial != "0")
                    {
                        return sb.Append(":ser:").Append(serial).ToString();
                    }
                    // 无序列号：同型号显示器用实例序号区分，避免两台被合并
                    return sb.Append(":inst:").Append(StableTail(s.InstanceKey)).ToString();
                }
                return sb.Append("inst:").Append(StableTail(s.InstanceKey)).ToString();
            }

            if (!string.IsNullOrEmpty(s.ContainerId))
            {
                // ContainerId 跨端口稳定；同容器多功能设备共享一个物理档案
                return sb.Append("cid:").Append(s.ContainerId).ToString();
            }

            // 无 ContainerId：型号级 VID/PID 只能到「同型号」粒度，叠加实例路径尾段区分端口
            string vidpid = JsonField(s.AttributesJson, "vidPid");
            if (!string.IsNullOrEmpty(vidpid))
            {
                return sb.Append("vp:").Append(vidpid).Append(":inst:").Append(StableTail(s.InstanceKey)).ToString();
            }
            return sb.Append("inst:").Append(StableTail(s.InstanceKey)).ToString();
        }

        /// <summary>快照与既有档案的最优匹配（含依据与置信度）。</summary>
        public static MatchResult Match(IList<DeviceRecord> records, DeviceSnapshot snapshot,
            string snapshotIdentityKey)
        {
            MatchResult r = new MatchResult();
            if (records == null || records.Count == 0 || snapshot == null ||
                string.IsNullOrEmpty(snapshotIdentityKey))
            {
                return r;
            }

            DeviceRecord exactIdentity = null;
            DeviceRecord sameModel = null;
            DeviceRecord sameContainer = null;
            foreach (DeviceRecord rec in records)
            {
                if (rec.Category != snapshot.Category) continue;
                if (!string.IsNullOrEmpty(rec.IdentityKey) &&
                    string.Equals(rec.IdentityKey, snapshotIdentityKey, StringComparison.OrdinalIgnoreCase))
                {
                    exactIdentity = rec;
                    break;
                }
                if (sameContainer == null && !string.IsNullOrEmpty(snapshot.ContainerId) &&
                    rec.AttributesJson != null &&
                    snapshot.ContainerId == JsonField(rec.AttributesJson, "containerId"))
                {
                    sameContainer = rec;
                }
                if (sameModel == null && !string.IsNullOrEmpty(snapshot.Model) &&
                    string.Equals(rec.Model, snapshot.Model, StringComparison.OrdinalIgnoreCase))
                {
                    sameModel = rec;
                }
            }

            if (exactIdentity != null)
            {
                r.Candidate = exactIdentity;
                r.Confidence = 1.0;
                r.Reasons.Add("身份键完全一致");
                return r;
            }
            if (sameContainer != null)
            {
                r.Candidate = sameContainer;
                r.Confidence = 0.7;
                r.Reasons.Add("ContainerId 一致");
                return r;   // 中置信度：保留并请求用户确认，不静默合并
            }
            if (sameModel != null)
            {
                r.Candidate = sameModel;
                r.Confidence = 0.35;
                r.Reasons.Add("仅同型号（同型号设备不自动合并）");
                return r;
            }
            return r;
        }

        /// <summary>新档案或并入档案时的显示名。</summary>
        public static string DisplayName(DeviceSnapshot s)
        {
            if (s == null) return "未知设备";
            if (!string.IsNullOrEmpty(s.Model)) return s.Model;
            if (!string.IsNullOrEmpty(s.Name)) return s.Name;
            return (string.IsNullOrEmpty(s.Manufacturer) ? "" : s.Manufacturer + " ") + "未命名设备";
        }

        private static string CategoryPrefix(DeviceCategory c)
        {
            switch (c)
            {
                case DeviceCategory.Input: return "input";
                case DeviceCategory.Display: return "display";
                case DeviceCategory.Audio: return "audio";
                case DeviceCategory.Video: return "video";
                case DeviceCategory.UsbHub: return "usb";
                case DeviceCategory.Office: return "office";
                default: return "dev";
            }
        }

        /// <summary>实例路径的稳定尾段（去掉 \??\ 前缀与尾部实例号前的易变段）。</summary>
        private static string StableTail(string instanceKey)
        {
            string k = instanceKey ?? "";
            k = k.Replace("\\\\?\\", "").Replace("#", ":");
            int hash = 0;
            for (int i = 0; i < k.Length; i++) hash = (hash * 31 + k[i]) & 0x7FFFFFFF;
            return hash.ToString("X8");
        }

        /// <summary>极简 JSON 平铺字段读取（仅本层 DTO 的平铺字符串/数字用）。</summary>
        public static string JsonField(string json, string field)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(field)) return null;
            string needle = "\"" + field + "\":";
            int i = json.IndexOf(needle, StringComparison.Ordinal);
            if (i < 0) return null;
            i += needle.Length;
            while (i < json.Length && json[i] == ' ') i++;
            if (i >= json.Length) return null;
            if (json[i] == '"')
            {
                int j = json.IndexOf('"', i + 1);
                return j < 0 ? null : json.Substring(i + 1, j - i - 1);
            }
            int k2 = i;
            while (k2 < json.Length && "-0123456789.eE".IndexOf(json[k2]) >= 0) k2++;
            return k2 > i ? json.Substring(i, k2 - i) : null;
        }
    }
}
