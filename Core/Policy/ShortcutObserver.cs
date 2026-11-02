using System;
using System.Collections.Generic;

namespace KeyInsight.Core.Policy
{
    /// <summary>
    /// 快捷键组合观察器（P2-04）：键盘钩子线程内维护修饰键状态，对白名单组合做纯聚合计数。
    /// 只记录「组合名 → 次数」，绝不记录按键序列或内容（隐私约束）。
    /// 单线程访问（键盘钩子线程）；计数器 Interlocked 供 UI 读取。
    /// </summary>
    public sealed class ShortcutObserver
    {
        // 组合白名单：修饰键 + 常用主键。限制集合大小 = 内存有界。
        private static readonly int[] WhitelistedVks = new int[]
        {
            0x41, 0x43, 0x44, 0x46, 0x4E, 0x52, 0x53, 0x56, 0x57, 0x58, 0x59, 0x5A, // A C D F N R S V W X Y Z
            0x54, 0x54, 0x51, 0x45, 0x42, 0x48, 0x4B, 0x4C, // T Q E B H K L（部分重复无害）
            0x09, 0x0D, 0x08, 0x1B, 0x70, 0x71, 0x72, 0x73, 0x74 // Tab Enter Back Esc F1-F5
        };

        private readonly bool[] _ctrl = new bool[2];   // L,R
        private readonly bool[] _alt = new bool[2];
        private readonly bool[] _shift = new bool[2];
        private readonly bool[] _win = new bool[2];

        private long _totalCombos;
        public long TotalCombos { get { return System.Threading.Interlocked.Read(ref _totalCombos); } }

        /// <summary>
        /// 观察一个键盘事件。返回组合名（如 "ctrl+c"）或 null（非白名单组合）。
        /// 在引擎评估之前/之后调用皆可；本观察器不改变任何输入行为。
        /// </summary>
        public string Observe(int vk, bool down, bool extended)
        {
            switch (vk)
            {
                case 0xA2: _ctrl[0] = down; return null;
                case 0xA3: _ctrl[1] = down; return null;
                case 0xA4: _alt[0] = down; return null;
                case 0xA5: _alt[1] = down; return null;
                case 0xA0: _shift[0] = down; return null;
                case 0xA1: _shift[1] = down; return null;
                case 0x5B: _win[0] = down; return null;
                case 0x5C: _win[1] = down; return null;
                default: break;
            }

            if (!down) return null;
            if (vk <= 0) return null;

            bool hasCtrl = _ctrl[0] || _ctrl[1];
            bool hasAlt = _alt[0] || _alt[1];
            bool hasShift = _shift[0] || _shift[1];
            bool hasWin = _win[0] || _win[1];
            if (!hasCtrl && !hasAlt && !hasWin) return null; // 单 Shift 不构成白名单组合

            bool whitelisted = false;
            for (int i = 0; i < WhitelistedVks.Length; i++)
            {
                if (WhitelistedVks[i] == vk) { whitelisted = true; break; }
            }
            if (!whitelisted) return null;

            string combo = ComboName(vk, hasCtrl, hasAlt, hasShift, hasWin);
            System.Threading.Interlocked.Increment(ref _totalCombos);
            return combo;
        }

        private static string ComboName(int vk, bool ctrl, bool alt, bool shift, bool win)
        {
            string key = KeyName(vk);
            if (ctrl) return (alt ? "ctrl+alt+" : "ctrl+") + (shift && vk >= 0x41 && vk <= 0x5A ? "shift+" : "") + key;
            if (alt) return (win ? "win+alt+" : "alt+") + key;
            return "win+" + key;
        }

        private static string KeyName(int vk)
        {
            if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString().ToLowerInvariant();
            if (vk >= 0x70 && vk <= 0x74) return "f" + (vk - 0x70 + 1);
            switch (vk)
            {
                case 0x09: return "tab";
                case 0x0D: return "enter";
                case 0x08: return "back";
                case 0x1B: return "esc";
                default: return "vk" + vk.ToString("x2");
            }
        }
    }
}
