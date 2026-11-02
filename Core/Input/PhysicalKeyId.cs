using System;

namespace KeyInsight.Core.Input
{
    /// <summary>
    /// 物理键身份（P0-03）：(VirtualKey, ScanCode, Extended) 三元组。
    /// 用于新策略（P0-04 KeyStateMachine）的控制状态；值相等性与哈希稳定，
    /// 可作为字典键与 JSON 键（ToString 形如 "vk=45;scan=18;ext=0"）。
    /// 旧 KDS1 的历史 VK 不受影响：旧统计投影仍用 LegacyVk（见 StatsVk）。
    /// </summary>
    public struct PhysicalKeyId : IEquatable<PhysicalKeyId>
    {
        public readonly int VirtualKey;
        public readonly int ScanCode;
        public readonly bool Extended;

        public PhysicalKeyId(int virtualKey, int scanCode, bool extended)
        {
            VirtualKey = virtualKey;
            ScanCode = scanCode;
            Extended = extended;
        }

        public bool Equals(PhysicalKeyId other)
        {
            return VirtualKey == other.VirtualKey && ScanCode == other.ScanCode && Extended == other.Extended;
        }

        public override bool Equals(object obj)
        {
            if (!(obj is PhysicalKeyId)) return false;
            return Equals((PhysicalKeyId)obj);
        }

        public override int GetHashCode()
        {
            // 稳定组合：vk(9bit 有效范围) ^ scan ^ ext；不依赖运行时哈希种子
            unchecked
            {
                return (VirtualKey * 397) ^ (ScanCode * 31) ^ (Extended ? 1 : 0);
            }
        }

        public override string ToString()
        {
            return "vk=" + VirtualKey.ToString("X") + ";scan=" + ScanCode.ToString("X") +
                ";ext=" + (Extended ? "1" : "0");
        }

        public static bool TryParse(string text, out PhysicalKeyId id)
        {
            id = new PhysicalKeyId(0, 0, false);
            if (text == null) return false;
            // 形如 vk=45;scan=18;ext=0
            int vk = -1;
            int scan = -1;
            int ext = 0;
            string[] parts = text.Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i];
                if (p.StartsWith("vk=", StringComparison.Ordinal))
                {
                    int v;
                    if (!int.TryParse(p.Substring(3), System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out v)) return false;
                    vk = v;
                }
                else if (p.StartsWith("scan=", StringComparison.Ordinal))
                {
                    int v;
                    if (!int.TryParse(p.Substring(5), System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out v)) return false;
                    scan = v;
                }
                else if (p.StartsWith("ext=", StringComparison.Ordinal))
                {
                    int v;
                    if (!int.TryParse(p.Substring(4), out v)) return false;
                    ext = v;
                }
            }
            if (vk < 0 || scan < 0) return false;
            id = new PhysicalKeyId(vk, scan, ext != 0);
            return true;
        }
    }
}
