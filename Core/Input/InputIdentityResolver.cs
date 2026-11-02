using System;
using System.Collections.Generic;

namespace KeyInsight.Core.Input
{
    /// <summary>
    /// 输入身份解析（P0-03）：把低级钩子的原始 (vkCode, scanCode, flags) 解析为 PhysicalKeyId。
    ///
    /// 规则（保守、可测试）：
    /// - Extended = KBDLLHOOKSTRUCT.flags 的 LLKHF_EXTENDED(0x01)。
    /// - 主 Enter (vk 0x0D, ext=0) 与小键盘 Enter (vk 0x0D, ext=1) 身份不同；左右 Ctrl/Alt/Win 同理。
    /// - scanCode=0（合成事件无扫描码）：降级为 (vk, 0, ext) 独立身份槽，不与物理键混同。
    /// - NumLock 关闭时小键盘 2/4/6/8 发送方向键 VK 但不带扩展位，主方向键簇带扩展位，身份可区分。
    /// - AltGr 在多数布局表现为右 Alt (vk 0xA5, ext=1)，与左 Alt (0xA4) 身份分离。
    /// </summary>
    public static class InputIdentityResolver
    {
        public const uint LlkhfExtended = 0x01;

        /// <summary>无扫描码时的 VK → 常见扫描码对照（用于配置/诊断场景离线构造身份）。</summary>
        private static readonly Dictionary<int, int> CanonicalScan = BuildCanonicalScan();

        public static PhysicalKeyId Resolve(int vkCode, int scanCode, uint flags)
        {
            bool ext = (flags & LlkhfExtended) != 0;
            return new PhysicalKeyId(vkCode, scanCode, ext);
        }

        /// <summary>离线构造（无真实扫描码时）：vk + 常见扫描码 + 非扩展。仅配置/诊断入口使用。</summary>
        public static PhysicalKeyId ResolveCanonical(int vkCode)
        {
            int scan;
            if (!CanonicalScan.TryGetValue(vkCode, out scan)) scan = 0;
            return new PhysicalKeyId(vkCode, scan, false);
        }

        /// <summary>取某 VK 的常见扫描码（未知返回 0）。KeyPolicyStore 序列化/诊断入口用。</summary>
        public static int CanonicalScanFor(int vkCode)
        {
            int scan;
            if (!CanonicalScan.TryGetValue(vkCode, out scan)) return 0;
            return scan;
        }

        private static Dictionary<int, int> BuildCanonicalScan()
        {
            Dictionary<int, int> map = new Dictionary<int, int>();
            // 字母区（set 1 make codes）
            int[] letters = new int[] { 0x1E, 0x30, 0x2E, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24, 0x25, 0x26, 0x32, 0x31, 0x18, 0x19, 0x10, 0x13, 0x1F, 0x14, 0x16, 0x2F, 0x11, 0x2D, 0x15, 0x2C };
            for (int i = 0; i < 26; i++) map[0x41 + i] = letters[i]; // A..Z
            int[] digits = new int[] { 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B };
            for (int i = 0; i < 10; i++) map[0x30 + i] = digits[i]; // 0..9
            map[0x0D] = 0x1C;  // Enter
            map[0x08] = 0x0E;  // Backspace
            map[0x09] = 0x0F;  // Tab
            map[0x20] = 0x39;  // Space
            map[0x1B] = 0x01;  // Esc
            map[0x14] = 0x3A;  // CapsLock
            map[0xA0] = 0x2A;  // LShift
            map[0xA1] = 0x36;  // RShift（非扩展）
            map[0xA2] = 0x1D;  // LCtrl
            map[0xA3] = 0x1D;  // RCtrl（扩展位区分）
            map[0xA4] = 0x38;  // LAlt
            map[0xA5] = 0x38;  // RAlt（扩展位区分）
            map[0x25] = 0x4B; map[0x26] = 0x48; map[0x27] = 0x4D; map[0x28] = 0x50; // 方向
            map[0x2D] = 0x52; map[0x2E] = 0x53; // Ins/Del
            map[0x24] = 0x47; map[0x23] = 0x4F; map[0x21] = 0x49; map[0x22] = 0x51; // Home/End/PgUp/PgDn
            map[0x70] = 0x3B;  // F1
            map[0x0C] = 0x2D;  // VK_CLEAR -> Minus 不对；仅作占位不使用
            map.Remove(0x0C);
            map[0xBD] = 0x0C;  // - _
            map[0xBB] = 0x0D;  // = +
            map[0xDB] = 0x1A;  // [ {
            map[0xDD] = 0x1B;  // ] }
            map[0xDC] = 0x2B;  // \ |
            map[0xBA] = 0x27;  // ; :
            map[0xDE] = 0x28;  // ' "
            map[0xBC] = 0x33;  // , <
            map[0xBE] = 0x34;  // . >
            map[0xBF] = 0x35;  // / ?
            map[0xC0] = 0x29;  // ` ~
            return map;
        }
    }
}
