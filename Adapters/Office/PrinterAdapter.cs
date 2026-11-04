using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace KeyInsight.Adapters.Office
{
    /// <summary>
    /// 打印机状态 Adapter（方案 §9.4，PR-11 最小版）：winspool EnumPrinters Level 2，
    /// 只取 名称/状态/任务数；高级耗材/喷头诊断需厂商接口，不做通用承诺（§9.4 边界）。
    /// </summary>
    public static class PrinterAdapter
    {
        public sealed class PrinterStatus
        {
            public string Name;
            public uint StatusFlags;
            public int Jobs;
            public bool Default;
        }

        private const int PRINTER_ENUM_LOCAL = 0x2;
        private const int PRINTER_ENUM_CONNECTIONS = 0x4;

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool EnumPrinters(int flags, string name, int level,
            IntPtr buffer, int cbBuf, out int cbNeeded, out int returned);

        /// <summary>打印机名 → 状态。失败/无打印机返回空表（不抛异常）。</summary>
        public static List<PrinterStatus> Enumerate()
        {
            List<PrinterStatus> result = new List<PrinterStatus>();
            int needed, returned;
            EnumPrinters(PRINTER_ENUM_LOCAL | PRINTER_ENUM_CONNECTIONS, null, 2,
                IntPtr.Zero, 0, out needed, out returned);
            if (needed <= 0) return result;

            IntPtr buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!EnumPrinters(PRINTER_ENUM_LOCAL | PRINTER_ENUM_CONNECTIONS, null, 2,
                    buffer, needed, out needed, out returned))
                {
                    return result;
                }
                // PRINTER_INFO_2：8 个指针 + Attributes/Priority/DefaultPriority/StartTime/
                // UntilTime/Status/cJobs/AveragePPM（x64 元素 96 字节，x86 60 字节）
                int elemSize = Marshal.SizeOf(typeof(IntPtr)) == 8 ? 96 : 60;
                for (int i = 0; i < returned; i++)
                {
                    IntPtr basePtr = new IntPtr(buffer.ToInt64() + i * elemSize);
                    PrinterStatus ps = new PrinterStatus();
                    string name = ReadStr(Marshal.ReadIntPtr(basePtr, 0));
                    if (string.IsNullOrEmpty(name)) continue;
                    ps.Name = name;
                    long afterPtrs = basePtr.ToInt64() + Marshal.SizeOf(typeof(IntPtr)) * 8;
                    ps.StatusFlags = (uint)Marshal.ReadInt32(new IntPtr(afterPtrs + 20));
                    ps.Jobs = Marshal.ReadInt32(new IntPtr(afterPtrs + 24));
                    result.Add(ps);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            return result;
        }

        /// <summary>状态位 → 可读文本（§9.4 只陈述可验证状态）。</summary>
        public static string StatusText(uint flags)
        {
            if (flags == 0) return "就绪";
            List<string> parts = new List<string>();
            if ((flags & 0x1) != 0) parts.Add("暂停");
            if ((flags & 0x2) != 0) parts.Add("错误");
            if ((flags & 0x8) != 0) parts.Add("缺纸");
            if ((flags & 0x10) != 0) parts.Add("缺墨");
            if ((flags & 0x20) != 0) parts.Add("脱机");
            if ((flags & 0x80) != 0) parts.Add("正在打印");
            if ((flags & 0x400) != 0) parts.Add("卡纸");
            if ((flags & 0x800) != 0) parts.Add("打开舱盖");
            if ((flags & 0x20000) != 0) parts.Add("服务请求");
            return parts.Count > 0 ? string.Join(" · ", parts.ToArray()) : "状态 " + flags;
        }

        private static string ReadStr(IntPtr ptr)
        {
            return ptr == IntPtr.Zero ? "" : Marshal.PtrToStringUni(ptr) ?? "";
        }
    }
}
