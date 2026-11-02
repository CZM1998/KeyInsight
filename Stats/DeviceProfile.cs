using System;

namespace KeyInsight.Stats
{
    /// <summary>
    /// 输入设备画像（设计文档 §6.1 DeviceProfile / P3）：
    /// 来自 Raw Input 的每设备累计统计。仅统计用途，不参与任何过滤决策；
    /// 设备级过滤是独立研究项（ADR-005），本模型不承诺支持。
    /// </summary>
    public sealed class DeviceProfile
    {
        public string DeviceId;      // 接口路径短名（稳定但可读性有限）
        public string Kind;          // keyboard / mouse
        public string VendorId;      // VID_xxxx（无法解析时为空）
        public string ProductId;     // PID_xxxx
        public long EventCount;
        public long FirstSeenUtcTicks;
        public long LastSeenUtcTicks;

        public string DisplayName
        {
            get
            {
                string name = Kind == "keyboard" ? "键盘" : "鼠标";
                if (!string.IsNullOrEmpty(VendorId) && !string.IsNullOrEmpty(ProductId))
                {
                    name += " " + VendorId + ":" + ProductId;
                }
                return name;
            }
        }

        public string IdSuffix
        {
            get
            {
                if (string.IsNullOrEmpty(DeviceId)) return "unknown";
                int idx = DeviceId.IndexOf('\\');
                string tail = idx >= 0 && idx + 1 < DeviceId.Length ? DeviceId.Substring(idx + 1) : DeviceId;
                if (tail.Length > 28) tail = tail.Substring(tail.Length - 28);
                return tail;
            }
        }
    }
}
