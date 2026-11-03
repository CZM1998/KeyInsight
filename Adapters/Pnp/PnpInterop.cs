using System;
using System.Runtime.InteropServices;
using System.Text;

namespace KeyInsight.Adapters.Pnp
{
    /// <summary>
    /// SetupAPI / Configuration Manager P/Invoke（方案 §7）。
    /// 仅在 Adapters 层使用；UI 与服务层不得直接调用本文件。
    /// 所有句柄遵循 CloseDeviceInfoSet/释放模式；调用失败返回 false 并由调用方降级。
    /// </summary>
    internal static class PnpInterop
    {
        internal const int DIGCF_PRESENT = 0x2;
        internal const int DIGCF_DEVICEINTERFACE = 0x10;
        internal const int DIGCF_ALLCLASSES = 0x4;

        internal const uint SPDRP_DEVICEDESC = 0x00000000;
        internal const uint SPDRP_MFG = 0x0000000B;
        internal const uint SPDRP_FRIENDLYNAME = 0x0000000C;
        internal const uint SPDRP_CLASS = 0x00000007;
        internal const uint SPDRP_CLASSGUID = 0x00000008;

        internal const int CR_SUCCESS = 0x0;
        internal const int CM_GETIDLIST_FILTER_CLASS = 0x00000002;
        internal const int CM_GETIDLIST_FILTER_PRESENT = 0x00000100;

        internal const int CM_DRP_DEVICEDESC = 0x00000001;
        internal const int CM_DRP_MFG = 0x0000000B;
        internal const int CM_DRP_FRIENDLYNAME = 0x0000000C;
        internal const int CM_DRP_CLASS = 0x00000009;
        internal const int CM_DRP_CONTAINER_ID = 0x0000003B;
        internal const int CM_DRP_ADDRESS = 0x0000001D;

        [StructLayout(LayoutKind.Sequential)]
        internal struct DEVPROPTYPE { }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string enumerator,
            IntPtr hwndParent, int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("setupapi.dll", SetLastError = true)]
        internal static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, int memberIndex,
            ref SP_DEVINFO_DATA deviceInfoData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool SetupDiGetDeviceRegistryProperty(IntPtr deviceInfoSet,
            ref SP_DEVINFO_DATA deviceInfoData, uint property, out int propertyRegDataType,
            byte[] propertyBuffer, int propertyBufferSize, out int requiredSize);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool SetupDiGetDeviceInstanceId(IntPtr deviceInfoSet,
            ref SP_DEVINFO_DATA deviceInfoData, StringBuilder instanceId, int instanceIdSize,
            out int requiredSize);

        [StructLayout(LayoutKind.Sequential)]
        internal struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid classGuid;
            public uint devInst;
            public IntPtr reserved;
        }

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        internal static extern int CM_Get_Device_ID(uint devInst, StringBuilder buffer,
            int bufferLen, int flags);

        [DllImport("cfgmgr32.dll")]
        internal static extern int CM_Get_DevNode_Status(out uint status, out uint problemNumber,
            uint devInst, int flags);

        [DllImport("cfgmgr32.dll")]
        internal static extern int CM_Get_Parent(out uint parentDevInst, uint devInst, int flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        internal static extern int CM_Get_DevNode_Registry_Property(uint devInst, int property,
            out int propertyRegDataType, byte[] buffer, ref int bufferSize, int flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        internal static extern int CM_Get_Device_ID_List(string filter, StringBuilder buffer,
            int bufferLen, int flags);

        [DllImport("cfgmgr32.dll")]
        internal static extern int CM_Get_Device_ID_List_Size(out int size, string filter, int flags);

        internal const uint DN_HAS_PROBLEM = 0x00000400;
        internal const uint DN_STARTED = 0x00000008;

        // WM_DEVICECHANGE / RegisterDeviceNotification（方案 §7：消息窗只转发事件）
        internal const int WM_DEVICECHANGE = 0x0219;
        internal const int DBT_DEVICEARRIVAL = 0x8000;
        internal const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
        internal const int DBT_DEVNODES_CHANGED = 0x0007;
        internal const int DBT_DEVTYP_DEVICEINTERFACE = 5;
        internal const int DEVICE_NOTIFY_WINDOW_HANDLE = 0;

        [StructLayout(LayoutKind.Sequential)]
        internal struct DEV_BROADCAST_DEVICEINTERFACE
        {
            public int dbcc_size;
            public int dbcc_devicetype;
            public int dbcc_reserved;
            public Guid dbcc_classguid;
            public short dbcc_name;
        }

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr RegisterDeviceNotification(IntPtr hRecipient,
            IntPtr notificationFilter, int flags);

        [DllImport("user32.dll")]
        internal static extern bool UnregisterDeviceNotification(IntPtr handle);

        /// <summary>GUID_DEVINTERFACE_* 常用接口类（监听所有类用空 GUID 不行 —— 用以下组合）。</summary>
        internal static readonly Guid GuidDevinterfaceDisplay = new Guid("{1CA05180-A699-450A-9A0C-DE4FBE3DDD89}");
        internal static readonly Guid GuidDevinterfaceDisk = new Guid("{53F56307-B6BF-11D0-94F2-00A0C91EFB8B}");
        internal static readonly Guid GuidDevinterfaceUsbDevice = new Guid("{A5DCBF10-6530-11D2-901F-00C04FB951ED}");
        internal static readonly Guid GuidDevinterfaceHid = new Guid("{4D1E55B2-F16F-11CF-88CB-001111000030}");

        /// <summary>读取 CM 注册表字符串属性；失败返回空串。</summary>
        internal static string GetDevNodeString(uint devInst, int property)
        {
            int size = 1024;
            byte[] buffer = new byte[size];
            int type;
            int r = CM_Get_DevNode_Registry_Property(devInst, property, out type, buffer, ref size, 0);
            if (r != CR_SUCCESS || size <= 0) return "";
            // CM 对 REG_SZ 返回的字节数含结尾 \0
            int chars = size / 2;
            if (chars > 0 && buffer[chars * 2 - 2] == 0 && buffer[chars * 2 - 1] == 0) chars--;
            return chars <= 0 ? "" : Encoding.Unicode.GetString(buffer, 0, chars * 2);
        }

        /// <summary>读取 SetupAPI 注册表字符串属性；失败返回空串。</summary>
        internal static string GetSetupString(IntPtr set, ref SP_DEVINFO_DATA data, uint property)
        {
            int required = 0;
            int type;
            if (!SetupDiGetDeviceRegistryProperty(set, ref data, property, out type, null, 0, out required))
            {
                if (required <= 0) return "";
            }
            byte[] buffer = new byte[required + 64];
            int type2;
            if (!SetupDiGetDeviceRegistryProperty(set, ref data, property, out type2,
                buffer, buffer.Length, out required))
            {
                return "";
            }
            int chars = required / 2;
            if (chars > 0 && buffer[chars * 2 - 2] == 0 && buffer[chars * 2 - 1] == 0) chars--;
            return chars <= 0 ? "" : Encoding.Unicode.GetString(buffer, 0, chars * 2);
        }
    }
}
