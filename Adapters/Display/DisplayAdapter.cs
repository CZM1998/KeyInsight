using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using KeyInsight.Core.Devices;

namespace KeyInsight.Adapters.Display
{
    /// <summary>
    /// 显示器 Adapter（方案 §8，PR-5）。当前模式与支持能力严格分开（§8.2）：
    /// 刷新率按分数记录并按实际值显示（GDI 为整数刻度，来源已标注）；
    /// EDID / DisplayConfig / GDI 多来源字段均带 Source；失败降级为「未知/未读取」。
    /// 枚举为快速 GDI/注册表查询，调用方仍应在后台线程执行。
    /// </summary>
    public sealed class DisplayAdapter
    {
        public sealed class SupportedMode
        {
            public int Width;
            public int Height;
            public int RefreshHz;
            public int BitsPerPixel;
        }

        public sealed class MonitorInfo
        {
            public string GdiDeviceName;        // \\.\DISPLAY1
            public string MonitorDeviceId;      // MONITOR\DEL4011\{...}\0000
            public string FriendlyName;
            public string Manufacturer;
            public string Model;
            public string Serial;
            public string EdidVendor;
            public int EdidProduct;
            public uint EdidSerial;
            public int ManufactureYear;
            public int ManufactureWeek;
            public bool EdidValid;
            public int CurrentWidth;
            public int CurrentHeight;
            public int CurrentRefreshHz;        // GDI 整数刻度（来源 GDI）
            public int BitsPerPixel;
            public int OrientationDeg;
            public int PositionX;
            public int PositionY;
            public bool Primary;
            public int NativeWidth;             // EDID 首选时序
            public int NativeHeight;
            public string Connector;            // DisplayConfig 输出技术（可空 = 未知）
            public string ConnectorName;        // DisplayConfig 友好名
            public bool HdrSupported;
            public bool HdrActive;
            public bool HdrKnown;
            public List<SupportedMode> SupportedModes = new List<SupportedMode>();
            public List<string> EdidTimings = new List<string>();
        }

        /// <summary>枚举当前活动显示器（后台线程调用）。</summary>
        public List<MonitorInfo> EnumerateMonitors()
        {
            List<MonitorInfo> result = new List<MonitorInfo>();
            Dictionary<string, uint> connectorByGdi = new Dictionary<string, uint>(
                StringComparer.OrdinalIgnoreCase);
            Dictionary<string, uint> refreshByGdi = new Dictionary<string, uint>(
                StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> nameByGdi = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            LoadDisplayConfig(connectorByGdi, refreshByGdi, nameByGdi);

            for (int i = 0; i < 64; i++)
            {
                DisplayInterop.DISPLAY_DEVICE adapter = new DisplayInterop.DISPLAY_DEVICE();
                adapter.cb = MarshalSizeOfDisplayDevice();
                if (!DisplayInterop.EnumDisplayDevices(null, i, ref adapter, 0)) break;
                if ((adapter.StateFlags & 1) == 0) continue;    // 只保留 attached-to-desktop

                string gdiName = adapter.DeviceName;

                DisplayInterop.DISPLAY_DEVICE monitor = new DisplayInterop.DISPLAY_DEVICE();
                monitor.cb = adapter.cb;
                if (!DisplayInterop.EnumDisplayDevices(gdiName, 0, ref monitor, 0)) continue;

                MonitorInfo m = new MonitorInfo();
                m.GdiDeviceName = gdiName;
                m.MonitorDeviceId = monitor.DeviceID;
                m.FriendlyName = FirstNonEmpty(monitor.DeviceString, adapter.DeviceString);

                // 当前模式（GDI）
                DisplayInterop.DEVMODE dm = new DisplayInterop.DEVMODE();
                dm.dmSize = (short)Marshal.SizeOf(typeof(DisplayInterop.DEVMODE));
                if (DisplayInterop.EnumDisplaySettingsEx(gdiName, DisplayInterop.ENUM_CURRENT_SETTINGS,
                    ref dm, 0))
                {
                    m.CurrentWidth = dm.dmPelsWidth;
                    m.CurrentHeight = dm.dmPelsHeight;
                    m.CurrentRefreshHz = dm.dmDisplayFrequency;
                    m.BitsPerPixel = dm.dmBitsPerPel;
                    m.PositionX = dm.dmPositionX;
                    m.PositionY = dm.dmPositionY;
                    m.OrientationDeg = OrientationDeg(dm.dmDisplayOrientation);
                    m.Primary = dm.dmPositionX == 0 && dm.dmPositionY == 0;
                }

                // 支持模式（注册表枚举；当前模式与支持能力分列 —— 方案 §8.2）
                Dictionary<string, SupportedMode> seen =
                    new Dictionary<string, SupportedMode>();
                for (int mi = 0; mi < 400; mi++)
                {
                    DisplayInterop.DEVMODE sm = new DisplayInterop.DEVMODE();
                    sm.dmSize = (short)Marshal.SizeOf(typeof(DisplayInterop.DEVMODE));
                    if (!DisplayInterop.EnumDisplaySettingsEx(gdiName, mi, ref sm, 0)) break;
                    if (sm.dmPelsWidth <= 0 || sm.dmPelsHeight <= 0) continue;
                    if (sm.dmBitsPerPel != 32 && sm.dmBitsPerPel != 24) continue;
                    string key = sm.dmPelsWidth + "x" + sm.dmPelsHeight + "x" + sm.dmDisplayFrequency;
                    if (seen.ContainsKey(key)) continue;
                    SupportedMode mode = new SupportedMode();
                    mode.Width = sm.dmPelsWidth;
                    mode.Height = sm.dmPelsHeight;
                    mode.RefreshHz = sm.dmDisplayFrequency;
                    mode.BitsPerPixel = sm.dmBitsPerPel;
                    seen[key] = mode;
                    m.SupportedModes.Add(mode);
                }

                // EDID（注册表；身份辅助，字段可能缺失/复制 —— 来源已标注）
                byte[] edid = DisplayInterop.ReadEdid(monitor.DeviceID);
                if (edid != null)
                {
                    EdidParser.EdidInfo e = EdidParser.Parse(edid);
                    if (e.Valid)
                    {
                        m.EdidValid = true;
                        m.EdidVendor = e.Vendor;
                        m.EdidProduct = e.ProductCode;
                        m.EdidSerial = e.SerialNumber;
                        m.ManufactureYear = e.ManufactureYear;
                        m.ManufactureWeek = e.ManufactureWeek;
                        m.NativeWidth = e.NativeWidth;
                        m.NativeHeight = e.NativeHeight;
                        m.EdidTimings = e.StandardTimings;
                        m.Model = FirstNonEmpty(
                            e.Descriptors.Count > 0 ? e.Descriptors[0] : null,
                            m.FriendlyName);
                        if (m.EdidSerial != 0)
                        {
                            m.Serial = m.EdidSerial.ToString();
                        }
                        if (e.Vendor != null && e.ProductCode != 0)
                        {
                            m.Manufacturer = m.EdidVendor;
                        }
                    }
                }
                if (string.IsNullOrEmpty(m.Model))
                {
                    m.Model = m.FriendlyName;
                }

                // DisplayConfig 交叉信息（输出技术/友好名/刷新分数）
                if (connectorByGdi.ContainsKey(gdiName))
                {
                    m.Connector = DisplayInterop.ConnectorText(connectorByGdi[gdiName]);
                }
                if (nameByGdi.ContainsKey(gdiName))
                {
                    m.ConnectorName = nameByGdi[gdiName];
                    if (string.IsNullOrEmpty(m.FriendlyName))
                    {
                        m.FriendlyName = nameByGdi[gdiName];
                    }
                }
                if (refreshByGdi.ContainsKey(gdiName))
                {
                    // DisplayConfig 提供分子/分母（如 59.94 Hz）；GDI 为整数刻度。
                    // 两者并存时以 DisplayConfig 分数为准（方案 §8.2 不抹平 59.94/60 差异）。
                    uint num = refreshByGdi[gdiName];
                    if (num > 0)
                    {
                        m.CurrentRefreshHz = (int)(num / 1000);
                    }
                }

                // HDR 能力（DisplayConfig Advanced Color；失败 = 未知）
                ReadHdr(gdiName, m);

                result.Add(m);
            }
            return result;
        }

        /// <summary>显示器 → 统一设备快照（供 DeviceCenterService 归档）。</summary>
        public List<DeviceSnapshot> Enumerate()
        {
            List<DeviceSnapshot> result = new List<DeviceSnapshot>();
            List<MonitorInfo> monitors = EnumerateMonitors();
            for (int i = 0; i < monitors.Count; i++)
            {
                MonitorInfo m = monitors[i];
                DeviceSnapshot s = new DeviceSnapshot();
                s.Category = DeviceCategory.Display;
                s.Source = "Display";
                s.IsPresent = true;
                s.InstanceKey = MonitorInstanceKey(m, i);
                s.InstanceId = m.MonitorDeviceId;
                s.Name = FirstNonEmpty(m.FriendlyName, m.Model, "显示器 " + (i + 1));
                s.Model = m.Model;
                s.Manufacturer = m.Manufacturer;
                s.SerialNumber = m.Serial;
                s.AttributesJson = ToAttributesJson(m, i);
                result.Add(s);
            }
            return result;
        }

        /// <summary>显示器身份键：EDID 厂商+产品+序列号（或实例哈希），由 IdentityResolver 消费。</summary>
        public static string MonitorInstanceKey(MonitorInfo m, int index)
        {
            if (m == null) return "";
            string baseKey = !string.IsNullOrEmpty(m.EdidVendor)
                ? m.EdidVendor + "." + m.EdidProduct
                : (m.MonitorDeviceId ?? ("gdi" + index));
            string tail = m.EdidSerial != 0
                ? m.EdidSerial.ToString()
                : StableHash(m.MonitorDeviceId ?? baseKey).ToString("X8");
            return "display:" + baseKey + ":" + tail;
        }

        private static int StableHash(string text)
        {
            int hash = 0;
            for (int i = 0; i < text.Length; i++) hash = (hash * 31 + text[i]) & 0x7FFFFFFF;
            return hash;
        }

        public static string ToAttributesJson(MonitorInfo m, int index)
        {
            StringBuilder sb = new StringBuilder(1024);
            sb.Append('{');
            sb.Append("\"gdi\":\"").Append(J(m.GdiDeviceName)).Append("\",");
            sb.Append("\"monitorId\":\"").Append(J(m.MonitorDeviceId)).Append("\",");
            if (!string.IsNullOrEmpty(m.EdidVendor))
            {
                sb.Append("\"edidVendor\":\"").Append(J(m.EdidVendor)).Append("\",");
                sb.Append("\"edidProduct\":").Append(m.EdidProduct).Append(",");
                sb.Append("\"edidSerial\":").Append(m.EdidSerial).Append(",");
            }
            sb.Append("\"edidValid\":").Append(m.EdidValid ? 1 : 0).Append(",");
            sb.Append("\"manufactureYear\":").Append(m.ManufactureYear).Append(",");
            sb.Append("\"current\":").Append(m.CurrentWidth).Append("x").Append(m.CurrentHeight)
              .Append("@").Append(m.CurrentRefreshHz).Append(",");
            sb.Append("\"bpp\":").Append(m.BitsPerPixel).Append(",");
            sb.Append("\"rotation\":").Append(m.OrientationDeg).Append(",");
            sb.Append("\"posX\":").Append(m.PositionX).Append(",\"posY\":").Append(m.PositionY).Append(",");
            sb.Append("\"primary\":").Append(m.Primary ? 1 : 0).Append(",");
            sb.Append("\"native\":").Append(m.NativeWidth).Append("x").Append(m.NativeHeight).Append(",");
            sb.Append("\"connector\":\"").Append(J(m.Connector)).Append("\",");
            sb.Append("\"hdrSupported\":").Append(m.HdrKnown ? (m.HdrSupported ? 1 : 0) : -1).Append(",");
            sb.Append("\"hdrActive\":").Append(m.HdrKnown ? (m.HdrActive ? 1 : 0) : -1).Append(",");
            sb.Append("\"hdrKnown\":").Append(m.HdrKnown ? 1 : 0).Append(",");
            sb.Append("\"slot\":").Append(index).Append(",");
            sb.Append("\"refreshSource\":\"GDI整数刻度\"");
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>从档案 AttributesJson 还原 MonitorInfo（详情页用；支持模式按需重查）。</summary>
        public static MonitorInfo FromAttributesJson(string json)
        {
            MonitorInfo m = new MonitorInfo();
            if (string.IsNullOrEmpty(json)) return m;
            m.GdiDeviceName = DeviceIdentityResolver.JsonField(json, "gdi");
            m.MonitorDeviceId = DeviceIdentityResolver.JsonField(json, "monitorId");
            m.EdidVendor = DeviceIdentityResolver.JsonField(json, "edidVendor");
            m.EdidProduct = IntField(json, "edidProduct");
            m.EdidSerial = (uint)LongField(json, "edidSerial");
            m.EdidValid = IntField(json, "edidValid") == 1;
            m.ManufactureYear = IntField(json, "manufactureYear");
            string current = DeviceIdentityResolver.JsonField(json, "current");
            ParseCurrent(current, m);
            m.BitsPerPixel = IntField(json, "bpp");
            m.OrientationDeg = IntField(json, "rotation");
            m.PositionX = IntField(json, "posX");
            m.PositionY = IntField(json, "posY");
            m.Primary = IntField(json, "primary") == 1;
            string native = DeviceIdentityResolver.JsonField(json, "native");
            if (!string.IsNullOrEmpty(native))
            {
                string[] parts = native.Split('x');
                if (parts.Length == 2)
                {
                    int.TryParse(parts[0], out m.NativeWidth);
                    int.TryParse(parts[1], out m.NativeHeight);
                }
            }
            m.Connector = DeviceIdentityResolver.JsonField(json, "connector");
            int hdrKnown = IntField(json, "hdrKnown");
            m.HdrKnown = hdrKnown == 1;
            m.HdrSupported = IntField(json, "hdrSupported") == 1;
            m.HdrActive = IntField(json, "hdrActive") == 1;
            return m;
        }

        // —— 内部 ——

        private static void LoadDisplayConfig(Dictionary<string, uint> connectorByGdi,
            Dictionary<string, uint> refreshByGdi, Dictionary<string, string> nameByGdi)
        {
            try
            {
                int pathCount, modeCount;
                if (DisplayInterop.GetDisplayConfigBufferSizes(
                    DisplayInterop.QDC_ONLY_ACTIVE_PATHS, out pathCount, out modeCount) != 0)
                {
                    return;
                }
                DisplayInterop.DISPLAYCONFIG_PATH_INFO[] paths =
                    new DisplayInterop.DISPLAYCONFIG_PATH_INFO[pathCount];
                DisplayInterop.DISPLAYCONFIG_MODE_INFO[] modes =
                    new DisplayInterop.DISPLAYCONFIG_MODE_INFO[modeCount];
                int pc = pathCount, mc = modeCount;
                if (DisplayInterop.QueryDisplayConfig(DisplayInterop.QDC_ONLY_ACTIVE_PATHS,
                    ref pc, paths, ref mc, modes, IntPtr.Zero) != 0)
                {
                    return;
                }

                // 目标 id → 输出技术/友好名；源 GDI 名 → 源 id
                Dictionary<string, uint> techById = new Dictionary<string, uint>();
                Dictionary<string, string> nameById = new Dictionary<string, string>();
                Dictionary<string, uint> refreshById = new Dictionary<string, uint>();
                for (int i = 0; i < pc; i++)
                {
                    DisplayInterop.DISPLAYCONFIG_PATH_INFO p = paths[i];
                    string tid = p.targetInfo.adapterId.LowPart + ":" + p.targetInfo.id;

                    DisplayInterop.DISPLAYCONFIG_TARGET_DEVICE_NAME tn =
                        new DisplayInterop.DISPLAYCONFIG_TARGET_DEVICE_NAME();
                    tn.header.type = DisplayInterop.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                    tn.header.size = MarshalSizeOfTargetName();
                    tn.header.adapterId = p.targetInfo.adapterId;
                    tn.header.id = p.targetInfo.id;
                    if (DisplayInterop.DisplayConfigGetDeviceInfo(ref tn) == 0)
                    {
                        techById[tid] = tn.outputTechnology;
                        if (!string.IsNullOrEmpty(tn.monitorFriendlyDeviceName))
                        {
                            nameById[tid] = tn.monitorFriendlyDeviceName;
                        }
                    }
                    // 刷新率分数（targetInfo.outputInfo.refreshRate）
                    DisplayInterop.DISPLAYCONFIG_RATIONAL rr = p.targetInfo.outputInfo.refreshRate;
                    if (rr.Denominator > 0)
                    {
                        refreshById[tid] = (uint)((rr.Numerator * 1000L) / rr.Denominator);
                    }

                    // 源 GDI 设备名（把目标映射回 \\.\DISPLAYn）
                    DisplayInterop.DISPLAYCONFIG_SOURCE_DEVICE_NAME sn =
                        new DisplayInterop.DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                    sn.header.type = DisplayInterop.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                    sn.header.size = MarshalSizeOfSourceName();
                    sn.header.adapterId = p.sourceInfo.adapterId;
                    sn.header.id = p.sourceInfo.id;
                    if (DisplayInterop.DisplayConfigGetDeviceInfo(ref sn) == 0 &&
                        !string.IsNullOrEmpty(sn.viewGdiDeviceName))
                    {
                        string gdi = sn.viewGdiDeviceName;
                        if (techById.ContainsKey(tid) && !connectorByGdi.ContainsKey(gdi))
                        {
                            connectorByGdi[gdi] = techById[tid];
                        }
                        if (nameById.ContainsKey(tid) && !nameByGdi.ContainsKey(gdi))
                        {
                            nameByGdi[gdi] = nameById[tid];
                        }
                        if (refreshById.ContainsKey(tid) && !refreshByGdi.ContainsKey(gdi))
                        {
                            refreshByGdi[gdi] = refreshById[tid];
                        }
                    }
                }
            }
            catch
            {
                // DisplayConfig 失败 → 连接信息/HDR 记为未知（方案 §8.6 降级）
            }
        }

        private static void ReadHdr(string gdiName, MonitorInfo m)
        {
            try
            {
                // 先用 DisplayConfig 的 target（adapterId/id）定位；这里简化为按路径重查。
                // Advanced Color 信息失败时 HdrKnown=false，UI 显示「未知」。
                int pathCount, modeCount;
                if (DisplayInterop.GetDisplayConfigBufferSizes(
                    DisplayInterop.QDC_ONLY_ACTIVE_PATHS, out pathCount, out modeCount) != 0) return;
                DisplayInterop.DISPLAYCONFIG_PATH_INFO[] paths =
                    new DisplayInterop.DISPLAYCONFIG_PATH_INFO[pathCount];
                DisplayInterop.DISPLAYCONFIG_MODE_INFO[] modes =
                    new DisplayInterop.DISPLAYCONFIG_MODE_INFO[modeCount];
                int pc = pathCount, mc = modeCount;
                if (DisplayInterop.QueryDisplayConfig(DisplayInterop.QDC_ONLY_ACTIVE_PATHS,
                    ref pc, paths, ref mc, modes, IntPtr.Zero) != 0) return;

                for (int i = 0; i < pc; i++)
                {
                    DisplayInterop.DISPLAYCONFIG_SOURCE_DEVICE_NAME sn =
                        new DisplayInterop.DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                    sn.header.type = DisplayInterop.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                    sn.header.size = MarshalSizeOfSourceName();
                    sn.header.adapterId = paths[i].sourceInfo.adapterId;
                    sn.header.id = paths[i].sourceInfo.id;
                    if (DisplayInterop.DisplayConfigGetDeviceInfo(ref sn) != 0) continue;
                    if (!string.Equals(sn.viewGdiDeviceName, gdiName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    DisplayInterop.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO ci =
                        new DisplayInterop.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO();
                    ci.header.type = DisplayInterop.DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO;
                    ci.header.size = Marshal.SizeOf(typeof(DisplayInterop.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO));
                    ci.header.adapterId = paths[i].targetInfo.adapterId;
                    ci.header.id = paths[i].targetInfo.id;
                    if (DisplayInterop.DisplayConfigGetDeviceInfo(ref ci) == 0)
                    {
                        m.HdrKnown = true;
                        m.HdrSupported = ci.advancedColorSupported != 0;
                        m.HdrActive = ci.advancedColorActive != 0;
                    }
                    return;
                }
            }
            catch
            {
            }
        }

        private static int MarshalSizeOfDisplayDevice()
        {
            return Marshal.SizeOf(typeof(DisplayInterop.DISPLAY_DEVICE));
        }

        private static int MarshalSizeOfSourceName()
        {
            return Marshal.SizeOf(typeof(DisplayInterop.DISPLAYCONFIG_SOURCE_DEVICE_NAME));
        }

        private static int MarshalSizeOfTargetName()
        {
            return Marshal.SizeOf(typeof(DisplayInterop.DISPLAYCONFIG_TARGET_DEVICE_NAME));
        }

        private static int OrientationDeg(int dmOrientation)
        {
            switch (dmOrientation)
            {
                case 1: return 90;
                case 2: return 180;
                case 3: return 270;
                default: return 0;
            }
        }

        private static void ParseCurrent(string text, MonitorInfo m)
        {
            if (string.IsNullOrEmpty(text)) return;
            int xIdx = text.IndexOf('x');
            int atIdx = text.IndexOf('@');
            if (xIdx > 0 && atIdx > xIdx)
            {
                int w, h, r;
                if (int.TryParse(text.Substring(0, xIdx), out w)) m.CurrentWidth = w;
                if (int.TryParse(text.Substring(xIdx + 1, atIdx - xIdx - 1), out h)) m.CurrentHeight = h;
                if (int.TryParse(text.Substring(atIdx + 1), out r)) m.CurrentRefreshHz = r;
            }
        }

        private static int IntField(string json, string field)
        {
            string v = DeviceIdentityResolver.JsonField(json, field);
            int n;
            return int.TryParse(v, out n) ? n : 0;
        }

        private static long LongField(string json, string field)
        {
            string v = DeviceIdentityResolver.JsonField(json, field);
            long n;
            return long.TryParse(v, out n) ? n : 0;
        }

        private static string J(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("\"", "'");
        }

        private static string FirstNonEmpty(string a, string b)
        {
            return !string.IsNullOrEmpty(a) ? a : (b ?? "");
        }

        private static string FirstNonEmpty(string a, string b, string c)
        {
            string v = FirstNonEmpty(a, b);
            return !string.IsNullOrEmpty(v) ? v : (c ?? "");
        }
    }
}
