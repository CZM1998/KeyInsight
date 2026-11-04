using System;
using System.Runtime.InteropServices;
using System.Text;

namespace KeyInsight.Adapters.Display
{
    /// <summary>
    /// 显示子系统 P/Invoke（方案 §7：EnumDisplayDevices/EnumDisplaySettings + QueryDisplayConfig）。
    /// 仅 Adapters 层使用；UI 不得直接调用。所有失败路径返回 false / null，由上层降级为"未知/未读取"。
    /// </summary>
    internal static class DisplayInterop
    {
        internal const int ENUM_CURRENT_SETTINGS = -1;
        internal const int ENUM_REGISTRY_SETTINGS = -2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
            public int dmICMMethod;
            public int dmICMIntent;
            public int dmMediaType;
            public int dmDitherType;
            public int dmReserved1;
            public int dmReserved2;
            public int dmPanningWidth;
            public int dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern bool EnumDisplayDevices(string lpDevice, int iDevNum,
            ref DISPLAY_DEVICE lpDisplayDevice, int dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern bool EnumDisplaySettingsEx(string lpszDeviceName, int iModeNum,
            ref DEVMODE lpDevMode, int dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int GetDisplayConfigBufferSizes(int flags, out int numPathArrayElements,
            out int numModeArrayElements);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int QueryDisplayConfig(int flags, ref int numPathArrayElements,
            [In, Out] DISPLAYCONFIG_PATH_INFO[] pathArray, ref int numModeArrayElements,
            [In, Out] DISPLAYCONFIG_MODE_INFO[] modeArray, IntPtr currentTopologyId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME request);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME request);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO request);

        internal const int QDC_ONLY_ACTIVE_PATHS = 0x2;
        internal const int QDC_INCLUDE_HMD = 0x10;

        // —— DisplayConfig 结构（尺寸与 wingdi.h 严格对齐，见 PR-0 审计风险 5） ——

        [StructLayout(LayoutKind.Sequential)]
        internal struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_RATIONAL
        {
            public uint Numerator;
            public uint Denominator;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_VIDEO_OUTPUT_INFO
        {
            public uint rotation;           // DISPLAYCONFIG_ROTATION
            public uint scaling;            // DISPLAYCONFIG_SCALING
            public DISPLAYCONFIG_RATIONAL refreshRate;
            public uint scanLineOrdering;
            public int targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id;
            public ushort technologyInfo;   // DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY
            public ushort padding;
            public DISPLAYCONFIG_VIDEO_OUTPUT_INFO outputInfo;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_SOURCE_MODE
        {
            public uint width;
            public uint height;
            public uint pixelFormat;
            public int positionX;
            public int positionY;
        }

        [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
        internal struct DISPLAYCONFIG_MODE_INFO
        {
            [FieldOffset(0)] public int infoType;       // 1=Source 2=Target
            [FieldOffset(4)] public uint id;
            [FieldOffset(8)] public LUID adapterId;
            [FieldOffset(16)] public DISPLAYCONFIG_SOURCE_MODE sourceMode;
            [FieldOffset(16)] public long targetPad0;
            [FieldOffset(24)] public long targetPad1;
            [FieldOffset(32)] public long targetPad2;
            [FieldOffset(40)] public long targetPad3;
            [FieldOffset(48)] public long targetPad4;
            [FieldOffset(56)] public long targetPad5;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public int type;
            public int size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string viewGdiDeviceName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint flags;
            public uint outputTechnology;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string monitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string monitorDevicePath;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint value;
            public int advancedColorSupported;
            public int advancedColorActive;
            public int wideColorEnforced;
            public int advancedColorForceDisabled;
        }

        internal const int DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
        internal const int DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
        internal const int DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9;

        // —— DDC/CI 亮度（方案 §8.6，dxva2.dll）——
        // 仅用户主动操作时调用 Set；读取失败一律降级为「不支持/暂不可用」，
        // 不把 VCP 失败认定为显示器故障（可能是线材/转接器/KVM/驱动限制）。

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string description;
        }

        internal const uint MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);

        [DllImport("dxva2.dll", SetLastError = true)]
        internal static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor,
            ref uint count);

        [DllImport("dxva2.dll", SetLastError = true)]
        internal static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor,
            uint count, [Out] PHYSICAL_MONITOR[] monitors);

        [DllImport("dxva2.dll", SetLastError = true)]
        internal static extern bool GetMonitorBrightness(IntPtr handle,
            ref uint min, ref uint current, ref uint max);

        [DllImport("dxva2.dll", SetLastError = true)]
        internal static extern bool SetMonitorBrightness(IntPtr handle, uint newValue);

        [DllImport("dxva2.dll", SetLastError = true)]
        internal static extern bool DestroyPhysicalMonitors(uint count,
            [In] PHYSICAL_MONITOR[] monitors);

        /// <summary>输出技术 → 可读文本（方案 §8.1 显示链路）。</summary>
        internal static string ConnectorText(uint technology)
        {
            switch (technology)
            {
                case 0: return "未知";
                case 1: return "HDMI";
                case 2: return "VGA (HD15)";
                case 5: return "DisplayPort";
                case 6: return "EDP（内屏）";
                case 7: return "USB-C (DP Alt)";
                case 8: return "虚拟";
                case 9: return "DVI";
                case 10: return "SDI";
                case 11: return "Mini DisplayPort";
                case 12: return "USB-C (非 DP)";
                case 13: return "LVDS";
                default: return "其他 (" + technology + ")";
            }
        }

        /// <summary>读取注册表中的显示器 EDID 字节（Enum\DISPLAY\<id>\<inst>\Device Parameters\EDID）。</summary>
        internal static byte[] ReadEdid(string monitorDeviceId)
        {
            try
            {
                if (string.IsNullOrEmpty(monitorDeviceId)) return null;
                string rel = monitorDeviceId;
                if (rel.StartsWith("MONITOR\\", StringComparison.OrdinalIgnoreCase))
                {
                    rel = rel.Substring(8);
                }
                string path = "SYSTEM\\CurrentControlSet\\Enum\\DISPLAY\\" + rel + "\\Device Parameters";
                using (Microsoft.Win32.RegistryKey key =
                    Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path))
                {
                    if (key == null) return null;
                    return key.GetValue("EDID") as byte[];
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
