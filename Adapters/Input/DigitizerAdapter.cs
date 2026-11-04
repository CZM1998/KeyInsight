using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using KeyInsight.Adapters.Pnp;
using KeyInsight.Core.Devices;

namespace KeyInsight.Adapters.Input
{
    /// <summary>
    /// 数位板/触屏 HID 适配（方案 §9.4，PR-11）：
    /// - 识别：HIDClass 设备接口枚举 → CreateFile → HidD_GetPreparsedData →
    ///   HidP_GetCaps，顶层集合 UsagePage == 0x0D（Digitizers）即数位板输入集合；
    /// - 报告能力：HidP_GetValueCaps 扫描输入值 —— 0x30 压力、0x3E/0x3F X/Y 倾斜；
    /// - 结构布局逐字段对照 Windows SDK 10.0.26100.0 shared/hidpi.h（108-267 行）核验；
    /// - 只读打开（CreateFile access=0），不读取任何输入内容（输入文本永不采集）；
    /// - 能力声明依赖驱动开放程度（§9.4），读不到即「未读取」，不猜测。
    /// </summary>
    public static class DigitizerAdapter
    {
        public sealed class DigitizerCaps
        {
            public string MatchKey;          // 与 PnP InstanceId 归一化后匹配
            public ushort Usage;             // 0x01 Digitizer / 0x02 Pen / 0x03 TouchPad / 0x04 TouchScreen
            public ushort UsagePage;
            public ushort InputReportLen;
            public bool Pressure;            // 0x30
            public bool TiltX;               // 0x3E
            public bool TiltY;               // 0x3F
        }

        private static readonly Guid GuidDevinterfaceHid =
            new Guid("4d1e55b2-f16f-11cf-88cb-001111000030");

        private const int HidpInput = 0;   // HIDP_REPORT_TYPE.HidP_Input

        /// <summary>扫描全部 HID 设备接口，返回数位板输入集合列表。后台线程调用；永不抛出。</summary>
        public static List<DigitizerCaps> Scan()
        {
            List<DigitizerCaps> result = new List<DigitizerCaps>();
            Guid hid = GuidDevinterfaceHid;
            IntPtr set = PnpInterop.SetupDiGetClassDevs(ref hid, null, IntPtr.Zero,
                PnpInterop.DIGCF_PRESENT | PnpInterop.DIGCF_DEVICEINTERFACE);
            if (set == IntPtr.Zero || set == new IntPtr(-1))
            {
                return result;
            }
            try
            {
                for (int i = 0; i < 512; i++)
                {
                    DigitizerCaps caps = ReadOne(set, hid, i);
                    if (caps == null) break;                        // 枚举到尾
                    if (!string.IsNullOrEmpty(caps.MatchKey)) result.Add(caps);  // 只收数位板
                }
            }
            finally
            {
                try { PnpInterop.SetupDiDestroyDeviceInfoList(set); } catch { }
            }
            return result;
        }

        private static DigitizerCaps ReadOne(IntPtr set, Guid hid, int index)
        {
            // 枚举接口；到尾返回 null（作为枚举结束信号）
            SetupDeviceInterfaceData did = new SetupDeviceInterfaceData();
            did.cbSize = Marshal.SizeOf(typeof(SetupDeviceInterfaceData));
            if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hid, index, ref did))
            {
                return null;
            }

            // 两次调用取接口细节路径
            int required = 0;
            SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, out required, IntPtr.Zero);
            if (required <= 0) return null;
            IntPtr detailBuf = Marshal.AllocHGlobal(required);
            try
            {
                Marshal.WriteInt32(detailBuf, 0, IntPtr.Size == 8 ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetail(set, ref did, detailBuf, required,
                    out required, IntPtr.Zero))
                {
                    return new DigitizerCaps();     // 有接口但读不到细节：不计入（不中断枚举）
                }
                string path = Marshal.PtrToStringUni(new IntPtr(detailBuf.ToInt64() + 4)) ?? "";
                if (path.Length == 0) return new DigitizerCaps();

                IntPtr file = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (file == IntPtr.Zero || file == new IntPtr(-1))
                {
                    return new DigitizerCaps();     // 被独占占用：跳过
                }
                try
                {
                    IntPtr preparsed;
                    if (!HidD_GetPreparsedData(file, out preparsed) || preparsed == IntPtr.Zero)
                    {
                        return new DigitizerCaps();
                    }
                    try
                    {
                        HidpCaps caps;
                        if (HidP_GetCaps(preparsed, out caps) != HidpSuccess) return new DigitizerCaps();

                        // 非数位板页 = null（作为「非目标设备」继续枚举的信号）
                        if (caps.UsagePage != 0x0D) return new DigitizerCaps();

                        DigitizerCaps d = new DigitizerCaps();
                        d.Usage = caps.Usage;
                        d.UsagePage = caps.UsagePage;
                        d.InputReportLen = caps.InputReportByteLength;
                        ScanValueCaps(preparsed, caps, d);
                        d.MatchKey = NormalizeInstanceKey(path);
                        return d;
                    }
                    finally
                    {
                        try { HidD_FreePreparsedData(preparsed); } catch { }
                    }
                }
                finally
                {
                    CloseHandle(file);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(detailBuf);
            }
        }

        private static void ScanValueCaps(IntPtr preparsed, HidpCaps caps, DigitizerCaps d)
        {
            if (caps.NumberInputValueCaps <= 0) return;
            int count = caps.NumberInputValueCaps;
            // 分配按托管 72 字节/项 + 每项 128 字节冗余，防止本地结构尺寸差异越界
            int stride = 72 + 128;
            IntPtr buffer = Marshal.AllocHGlobal(count * stride);
            try
            {
                ushort len = (ushort)Math.Min(count, 512); // explicit cast ok
                if (HidP_GetValueCaps(HidpInput, preparsed, ref len, buffer) != HidpSuccess)
                {
                    return;
                }
                for (int i = 0; i < len; i++)
                {
                    IntPtr entry = new IntPtr(buffer.ToInt64() + i * stride);
                    ushort usagePage = (ushort)Marshal.ReadInt16(entry, 0);
                    if (usagePage != 0x0D) continue;
                    int isRange = Marshal.ReadByte(entry, 12);
                    ushort usageMin = (ushort)Marshal.ReadInt16(entry, 56);
                    ushort usageMax = (ushort)Marshal.ReadInt16(entry, 58);
                    ushort usage = isRange == 0
                        ? (ushort)Marshal.ReadInt16(entry, 56)   // NotRange.Usage
                        : usageMin;
                    // usageMin/usageMax 已是 ushort；三元结果类型 = ushort
                    if (isRange != 0)
                    {
                        if (InRange(0x30, usageMin, usageMax)) d.Pressure = true;
                        if (InRange(0x3E, usageMin, usageMax)) d.TiltX = true;
                        if (InRange(0x3F, usageMin, usageMax)) d.TiltY = true;
                    }
                    else
                    {
                        if (usage == 0x30) d.Pressure = true;
                        if (usage == 0x3E) d.TiltX = true;
                        if (usage == 0x3F) d.TiltY = true;
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static bool InRange(int usage, int min, int max)
        {
            return usage >= min && usage <= max;
        }

        /// <summary>
        /// 设备接口路径 → 与 PnP InstanceId 可比的键：
        /// "\\?\hid#vid_x&pid_y&mi_00#8&xx&0&0000#{guid}" → "hid#vid_x&pid_y&mi_00#8&xx&0&0000"。
        /// </summary>
        public static string NormalizeInstanceKey(string interfacePath)
        {
            string p = interfacePath ?? "";
            if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p.Substring(4);
            int hash = p.LastIndexOf('#');
            if (hash > 0) p = p.Substring(0, hash);
            return p.ToLowerInvariant();
        }

        /// <summary>PnP InstanceId → 可比键（"HID\VID_..\MI_00\7&xx" → "hid#vid_..&mi_00#7&xx"）。</summary>
        public static string NormalizeInstanceId(string instanceId)
        {
            string k = (instanceId ?? "").Trim().ToLowerInvariant();
            return k.Replace('\\', '#');
        }

        // —— Win32/hid 导入 ——

        [StructLayout(LayoutKind.Sequential)]
        private struct SetupDeviceInterfaceData
        {
            public int cbSize;
            public Guid interfaceClassGuid;
            public int flags;
            public IntPtr reserved;
        }

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet,
            IntPtr deviceInfoData, ref Guid interfaceClassGuid, int memberIndex,
            ref SetupDeviceInterfaceData data);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet,
            ref SetupDeviceInterfaceData data, IntPtr detail, int detailSize,
            out int requiredSize, IntPtr deviceInfoData);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(string name, int access, int share,
            IntPtr security, int disposition, int flags, IntPtr template);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("hid.dll")]
        private static extern bool HidD_GetPreparsedData(IntPtr hidDeviceObject,
            out IntPtr preparsedData);

        [DllImport("hid.dll")]
        private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll")]
        private static extern int HidP_GetCaps(IntPtr preparsedData, out HidpCaps caps);

        [DllImport("hid.dll")]
        private static extern int HidP_GetValueCaps(int reportType, IntPtr preparsedData,
            ref ushort valueCapsLength, IntPtr valueCaps);

        private const int HidpSuccess = 0x0;

        /// <summary>HIDP_CAPS（hidpi.h 245-267 行，64 字节；无指针，x86/x64 同布局）。</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct HidpCaps
        {
            public ushort Usage;                    // 0-2
            public ushort UsagePage;                // 2-4
            public ushort InputReportByteLength;    // 4-6
            public ushort OutputReportByteLength;   // 6-8
            public ushort FeatureReportByteLength;  // 8-10
            public ushort Reserved1;                // 10-12
            public ushort Reserved2;                // 12-14
            public ushort Reserved3;                // 14-16
            public ushort Reserved4;                // 16-18
            public ushort Reserved5;                // 18-20
            public ushort Reserved6;                // 20-22
            public ushort Reserved7;                // 22-24
            public ushort Reserved8;                // 24-26
            public ushort Reserved9;                // 26-28
            public ushort Reserved10;               // 28-30
            public ushort Reserved11;               // 30-32
            public ushort Reserved12;               // 32-34
            public ushort Reserved13;               // 34-36
            public ushort Reserved14;               // 36-38
            public ushort Reserved15;               // 38-40
            public ushort Reserved16;               // 40-42
            public ushort Reserved17;               // 42-44
            public ushort NumberLinkCollectionNodes;// 44-46
            public ushort NumberInputButtonCaps;    // 46-48
            public ushort NumberInputValueCaps;     // 48-50
            public ushort NumberInputDataIndices;   // 50-52
            public ushort NumberOutputButtonCaps;   // 52-54
            public ushort NumberOutputValueCaps;    // 54-56
            public ushort NumberOutputDataIndices;  // 56-58
            public ushort NumberFeatureButtonCaps;  // 58-60
            public ushort NumberFeatureValueCaps;   // 60-62
            public ushort NumberFeatureDataIndices; // 62-64
        }
    }
}
