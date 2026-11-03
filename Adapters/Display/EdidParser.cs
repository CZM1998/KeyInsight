using System;
using System.Collections.Generic;
using System.Text;

namespace KeyInsight.Adapters.Display
{
    /// <summary>
    /// EDID 1.x 解析（方案 §8.1）。只提取可验证字段：
    /// 厂商三字母码、产品码、序列号、生产年/周、首选时序（原生分辨率）、标准时序列表。
    /// 字段可能缺失或被 KVM 复制 —— 调用方须记录来源与可信度，不得当作绝对事实。
    /// </summary>
    public static class EdidParser
    {
        public sealed class EdidInfo
        {
            public string Vendor;               // 三字母码（如 DEL）
            public int ProductCode;
            public uint SerialNumber;
            public int ManufactureYear;
            public int ManufactureWeek;
            public int NativeWidth;             // 首选时序（字节寻址分辨率）
            public int NativeHeight;
            public readonly List<string> StandardTimings = new List<string>();
            public readonly List<string> Descriptors = new List<string>();  // 名称等描述符
            public bool Valid;
        }

        public static EdidInfo Parse(byte[] edid)
        {
            EdidInfo info = new EdidInfo();
            if (edid == null || edid.Length < 128) return info;
            // 固定头 00 FF FF FF FF FF FF 00
            if (edid[0] != 0x00 || edid[1] != 0xFF || edid[2] != 0xFF || edid[3] != 0xFF ||
                edid[4] != 0xFF || edid[5] != 0xFF || edid[6] != 0xFF || edid[7] != 0x00)
            {
                return info;
            }

            // 厂商 ID：2 字节 → 3 组 5bit（bit15 必为 1）
            int idManu = (edid[8] << 8) | edid[9];
            char c1 = (char)('A' + ((idManu >> 10) & 0x1F) - 1);
            char c2 = (char)('A' + ((idManu >> 5) & 0x1F) - 1);
            char c3 = (char)('A' + (idManu & 0x1F) - 1);
            if (c1 >= 'A' && c1 <= 'Z' && c2 >= 'A' && c2 <= 'Z' && c3 >= 'A' && c3 <= 'Z')
            {
                info.Vendor = new string(new[] { c1, c2, c3 });
            }
            info.ProductCode = edid[10] | (edid[11] << 8);
            info.SerialNumber = (uint)(edid[12] | (edid[13] << 8) | (edid[14] << 16) | (edid[15] << 24));
            info.ManufactureWeek = edid[16];
            info.ManufactureYear = 1990 + edid[17];

            // 首选时序描述符（18..53 第一块）
            int pc = 54;
            // 时序描述符：56 高 4 位 = Hactive[11:8]，57 高 4 位 = Vactive[11:8]，
            // 58/60 = Hactive/Vactive 低 8 位（EDID 1.x 规范）
            int hActive = ((edid[pc + 2] >> 4) << 8) | edid[pc + 4];
            int vActive = ((edid[pc + 3] >> 4) << 8) | edid[pc + 6];
            if (hActive > 0 && vActive > 0)
            {
                info.NativeWidth = hActive;
                info.NativeHeight = vActive;
            }

            // 描述符 2..4（72..89, 90..107, 108..125）：文本/范围/名称
            for (int d = 1; d < 4; d++)
            {
                int off = 54 + d * 18;
                int type = edid[off] & 0x7F;
                if (edid[off] == 0 && edid[off + 1] == 0)
                {
                    // 文本描述符：type 0xFF 名称 / 0xFE 未定义文本
                    int textType = edid[off + 3];
                    string text = Ascii(edid, off + 5, 13);
                    if (textType == 0xFF && text.Length > 0)
                    {
                        info.Descriptors.Add(text);
                    }
                }
            }

            // 标准时序（26..53）：8 组 2 字节；未使用槽位规范为 01 01，实机常见 00 00
            for (int i = 0; i < 8; i++)
            {
                int b1 = edid[26 + i * 2];
                int b2 = edid[27 + i * 2];
                if (b1 == 0x01 && b2 == 0x01) continue;   // 未使用槽
                if (b1 == 0x00 && b2 == 0x00) continue;   // 实机常见空槽
                int w = (b1 + 31) * 8;
                if (w < 256) continue;                    // 槽位无效保护
                int ratioId = (b2 >> 6) & 0x03;
                int h = 0;
                switch (ratioId)
                {
                    case 0: h = w * 10 / 16; break;
                    case 1: h = w * 3 / 4; break;
                    case 2: h = w * 4 / 5; break;
                    default: h = w * 9 / 16; break;
                }
                int refresh = (b2 & 0x3F) + 60;
                info.StandardTimings.Add(w + "x" + h + " @" + refresh + " Hz");
            }

            info.Valid = true;
            return info;
        }

        private static string Ascii(byte[] data, int offset, int len)
        {
            StringBuilder sb = new StringBuilder(len);
            for (int i = 0; i < len; i++)
            {
                byte b = data[offset + i];
                if (b == 0x0A || b == 0x20 || (b >= 0x20 && b < 0x7F)) sb.Append((char)b);
            }
            return sb.ToString().Trim();
        }
    }
}
