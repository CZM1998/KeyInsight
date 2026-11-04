using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using KeyInsight.Core.Devices;

namespace KeyInsight.Adapters.Pnp
{
    /// <summary>
    /// PnP 设备枚举 Adapter（方案 §7：SetupAPI + Configuration Manager）。
    /// 只做枚举与属性读取；拓扑/热插拔由 DeviceCenterService 编排。
    /// 枚举范围（首批）：显示、输入（HID）、USB、媒体（音频/视频类）、打印机。
    /// </summary>
    public sealed class PnpDeviceAdapter
    {
        private static readonly string[] MonitoredClasses = new string[]
        {
            "DISPLAY", "MONITOR", "HIDCLASS", "USB", "MEDIA", "IMAGE", "PRINTER", "SYSTEM"
        };

        /// <summary>枚举当前在线的受监控设备快照。调用方应在后台线程执行。</summary>
        public List<DeviceSnapshot> Enumerate()
        {
            List<DeviceSnapshot> result = new List<DeviceSnapshot>();
            Guid zero = Guid.Empty;
            IntPtr set = PnpInterop.SetupDiGetClassDevs(ref zero, null, IntPtr.Zero,
                PnpInterop.DIGCF_PRESENT | PnpInterop.DIGCF_ALLCLASSES);
            if (set == InvalidHandleValue) return result;
            try
            {
                for (int i = 0; i < 4096; i++)
                {
                    PnpInterop.SP_DEVINFO_DATA data = new PnpInterop.SP_DEVINFO_DATA();
                    data.cbSize = Marshal.SizeOf(typeof(PnpInterop.SP_DEVINFO_DATA));
                    if (!PnpInterop.SetupDiEnumDeviceInfo(set, i, ref data)) break;

                    string instanceId = GetInstanceId(set, ref data);
                    if (string.IsNullOrEmpty(instanceId)) continue;
                    string cls = PnpInterop.GetSetupString(set, ref data, PnpInterop.SPDRP_CLASS)
                        .ToUpperInvariant();
                    if (!IsMonitored(cls)) continue;

                    DeviceCategory category = CategoryFromClass(cls, instanceId);
                    if (category == DeviceCategory.Other && cls == "SYSTEM") continue; // SYSTEM 只作拓扑父节点

                    uint status, problem;
                    uint code = 0;
                    bool present = true;
                    if (PnpInterop.CM_Get_DevNode_Status(out status, out problem, data.devInst, 0)
                        == PnpInterop.CR_SUCCESS)
                    {
                        code = (status & PnpInterop.DN_HAS_PROBLEM) != 0 ? problem : 0;
                    }

                    DeviceSnapshot s = new DeviceSnapshot();
                    s.InstanceKey = instanceId;
                    s.InstanceId = instanceId;
                    s.DeviceClass = cls;
                    s.Category = category;
                    s.Name = FirstNonEmpty(
                        PnpInterop.GetSetupString(set, ref data, PnpInterop.SPDRP_FRIENDLYNAME),
                        PnpInterop.GetSetupString(set, ref data, PnpInterop.SPDRP_DEVICEDESC));
                    s.Manufacturer = PnpInterop.GetSetupString(set, ref data, PnpInterop.SPDRP_MFG);
                    s.IsPresent = present;
                    s.ProblemCode = (int)code;
                    s.Source = "Pnp";

                    uint parent;
                    if (PnpInterop.CM_Get_Parent(out parent, data.devInst, 0) == PnpInterop.CR_SUCCESS)
                    {
                        StringBuilder pb = new StringBuilder(512);
                        if (PnpInterop.CM_Get_Device_ID(parent, pb, pb.Capacity, 0)
                            == PnpInterop.CR_SUCCESS)
                        {
                            s.ParentKey = pb.ToString();
                        }
                    }

                    int containerSize = 64;
                    byte[] containerBuf = new byte[containerSize * 2];
                    int propType;
                    if (PnpInterop.CM_Get_DevNode_Registry_Property(data.devInst,
                        PnpInterop.CM_DRP_CONTAINER_ID, out propType, containerBuf,
                        ref containerSize, 0) == PnpInterop.CR_SUCCESS && containerSize >= 36)
                    {
                        try
                        {
                            byte[] guidBytes = new byte[16];
                            Array.Copy(containerBuf, guidBytes, 16);
                            string cid = new Guid(guidBytes).ToString();
                            if (!cid.StartsWith("00000000", StringComparison.Ordinal))
                            {
                                s.ContainerId = cid;
                            }
                        }
                        catch { }
                    }

                    s.AttributesJson = BuildAttributes(instanceId, s.ContainerId, s.ParentKey);
                    result.Add(s);
                }
            }
            finally
            {
                PnpInterop.SetupDiDestroyDeviceInfoList(set);
            }
            return result;
        }

        private static IntPtr InvalidHandleValue
        {
            get { return new IntPtr(-1); }
        }

        internal static bool IsMonitored(string cls)
        {
            for (int i = 0; i < MonitoredClasses.Length; i++)
            {
                if (cls == MonitoredClasses[i]) return true;
            }
            return false;
        }

        internal static DeviceCategory CategoryFromClass(string cls, string instanceId)
        {
            switch (cls)
            {
                case "DISPLAY":
                case "MONITOR": return DeviceCategory.Display;
                case "MEDIA": return DeviceCategory.Audio;
                case "IMAGE": return DeviceCategory.Video;
                case "PRINTER": return DeviceCategory.Office;
                case "HIDCLASS": return DeviceCategory.Input;
                case "USB":
                    // USB 类节点：Hub/存储控制器归扩展；其余看实例路径
                    if (instanceId != null && instanceId.ToUpperInvariant().Contains("CLASS_09"))
                    {
                        return DeviceCategory.UsbHub;
                    }
                    return DeviceCategory.UsbHub;
                default: return DeviceCategory.Other;
            }
        }

        private static string GetInstanceId(IntPtr set, ref PnpInterop.SP_DEVINFO_DATA data)
        {
            int required;
            if (!PnpInterop.SetupDiGetDeviceInstanceId(set, ref data, null, 0, out required))
            {
                if (required <= 0) return "";
            }
            StringBuilder sb = new StringBuilder(Math.Max(256, required + 2));
            if (!PnpInterop.SetupDiGetDeviceInstanceId(set, ref data, sb, sb.Capacity, out required))
            {
                return "";
            }
            return sb.ToString();
        }

        /// <summary>从实例 ID 解析 VID/PID（仅型号识别用，绝不当唯一 ID —— 方案 §5.2）。</summary>
        internal static string ParseVidPid(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return "";
            string upper = instanceId.ToUpperInvariant();
            int vid = upper.IndexOf("VID_", StringComparison.Ordinal);
            int pid = upper.IndexOf("PID_", StringComparison.Ordinal);
            if (vid < 0 || pid < 0 || pid < vid) return "";
            int end = pid + 8;
            if (end > upper.Length) end = upper.Length;
            return upper.Substring(vid, Math.Min(9, end - vid)).TrimEnd('&', '\\', ':');
        }

        private static string BuildAttributes(string instanceId, string containerId, string parentKey)
        {
            StringBuilder sb = new StringBuilder(220);
            sb.Append('{');
            string vp = ParseVidPid(instanceId);
            if (!string.IsNullOrEmpty(vp)) sb.Append("\"vidPid\":\"").Append(vp).Append("\",");
            if (!string.IsNullOrEmpty(containerId))
            {
                sb.Append("\"containerId\":\"").Append(containerId).Append("\",");
            }
            if (!string.IsNullOrEmpty(parentKey))
            {
                sb.Append("\"parentKey\":\"").Append(
                    parentKey.Replace("\\", "\\\\").Replace("\"", "'")).Append("\",");
            }
            sb.Append("\"instancePath\":\"").Append(
                (instanceId ?? "").Replace("\\", "\\\\").Replace("\"", "'")).Append('"');
            sb.Append('}');
            return sb.ToString();
        }

        private static string FirstNonEmpty(string a, string b)
        {
            return !string.IsNullOrEmpty(a) ? a : (b ?? "");
        }
    }
}
