using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using KeyInsight.Core.Devices;

namespace KeyInsight.Adapters.Audio
{
    /// <summary>
    /// 音频端点 Adapter（方案 §9.1，PR-8）：Windows Core Audio COM（MMDevice API）。
    /// - 枚举 播放/录音 活动端点（端点 ≠ 物理设备，身份关联底层 PnP 实例）；
    /// - 区分 默认端点（Console 角色）；
    /// - 混音格式（采样率/声道/位深）来自 IAudioClient.GetMixFormat —— 引擎混音格式，
    ///   不是"支持格式"（支持格式此处标记未读取，§9.1 区分口径）；
    /// - 只读观察，不做任何设备控制；COM 对象即取即放，不缓存。
    /// </summary>
    public sealed class AudioAdapter
    {
        public sealed class EndpointInfo
        {
            public string Id;               // MMDevice id（接口路径）
            public string PnpInstanceId;    // 底层 PnP 实例 ID（身份关联用）
            public string Name;
            public int DataFlow;            // 0 = render（播放），1 = capture（录音）
            public int State;
            public bool IsDefaultConsole;
            public int MixRate;
            public int MixChannels;
            public int MixBits;
            public bool MixKnown;
        }

        public sealed class AudioResult
        {
            public readonly List<EndpointInfo> Endpoints = new List<EndpointInfo>();
            public string DefaultRenderId = "";
            public string DefaultCaptureId = "";
        }

        public AudioResult Enumerate()
        {
            AudioResult result = new AudioResult();
            object comObj = new MMDeviceEnumeratorComObject();
            IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)comObj;

            // 默认端点（Console/Multimedia 角色；程序内统一记 Console）
            IMMDevice defRender, defCapture;
            try
            {
                if (enumerator.GetDefaultAudioEndpoint(0, 0, out defRender) == 0)
                {
                    string id;
                    if (defRender.GetId(out id) == 0) result.DefaultRenderId = id;
                    Marshal.ReleaseComObject(defRender);
                }
            }
            catch { }
            try
            {
                if (enumerator.GetDefaultAudioEndpoint(1, 0, out defCapture) == 0)
                {
                    string id;
                    if (defCapture.GetId(out id) == 0) result.DefaultCaptureId = id;
                    Marshal.ReleaseComObject(defCapture);
                }
            }
            catch { }

            for (int flow = 0; flow <= 1; flow++)
            {
                IMMDeviceCollection collection;
                try
                {
                    if (enumerator.EnumAudioEndpoints(flow, 1 /*ACTIVE*/, out collection) != 0)
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                int count = 0;
                try { collection.GetCount(out count); } catch { }

                for (int i = 0; i < count; i++)
                {
                    IMMDevice device = null;
                    try
                    {
                        if (collection.Item(i, out device) != 0) continue;
                        EndpointInfo info = ReadEndpoint(device, flow);
                        if (info != null)
                        {
                            if (string.Equals(info.Id, result.DefaultRenderId, StringComparison.OrdinalIgnoreCase)
                                || string.Equals(info.Id, result.DefaultCaptureId, StringComparison.OrdinalIgnoreCase))
                            {
                                info.IsDefaultConsole = true;
                            }
                            result.Endpoints.Add(info);
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        if (device != null) Marshal.ReleaseComObject(device);
                    }
                }
                Marshal.ReleaseComObject(collection);
            }
            return result;
        }

        private static EndpointInfo ReadEndpoint(IMMDevice device, int flow)
        {
            string id, pnp = "";
            if (device.GetId(out id) != 0 || string.IsNullOrEmpty(id)) return null;

            EndpointInfo info = new EndpointInfo();
            info.Id = id;
            info.DataFlow = flow;

            IMMEndpoint endpoint = device as IMMEndpoint;
            if (endpoint != null)
            {
                int df;
                if (endpoint.GetDataFlow(out df) == 0) info.DataFlow = df;
            }
            int state;
            if (device.GetState(out state) == 0) info.State = state;

            IPropertyStore store;
            if (device.OpenPropertyStore(0 /*STGM_READ*/, out store) == 0)
            {
                try
                {
                    int count;
                    if (store.GetCount(out count) == 0)
                    {
                        info.Name = GetProperty(store, PKEYDeviceFriendlyName);
                        pnp = GetProperty(store, PKEYDeviceInstanceId);
                    }
                    else
                    {
                        info.Name = GetProperty(store, PKEYDeviceFriendlyName);
                        pnp = GetProperty(store, PKEYDeviceInstanceId);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(store);
                }
            }
            if (string.IsNullOrEmpty(info.Name)) info.Name = flow == 0 ? "播放设备" : "录音设备";
            info.PnpInstanceId = pnp ?? "";

            ReadMixFormat(device, info);
            return info;
        }

        private static void ReadMixFormat(IMMDevice device, EndpointInfo info)
        {
            object obj;
            try
            {
                Guid iid = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"); // IAudioClient
                if (device.Activate(ref iid, 1 /*CLSCTX_INPROC_SERVER*/, IntPtr.Zero, out obj) != 0)
                {
                    return;
                }
                IAudioClient client = (IAudioClient)obj;
                try
                {
                    IntPtr fmt;
                    if (client.GetMixFormat(out fmt) == 0 && fmt != IntPtr.Zero)
                    {
                        try
                        {
                            WavFmt wf = (WavFmt)Marshal.PtrToStructure(fmt, typeof(WavFmt));
                            info.MixChannels = wf.channels;
                            info.MixRate = (int)wf.rate;
                            info.MixBits = wf.bits;
                            info.MixKnown = true;
                        }
                        finally
                        {
                            CoTaskMemFree(fmt);
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(client);
                }
            }
            catch
            {
            }
        }

        private static string GetProperty(IPropertyStore store, PropertyKey key)
        {
            try
            {
                PropVariant pv = new PropVariant();
                try
                {
                    if (store.GetValue(ref key, ref pv) != 0) return "";
                    if (pv.vt == 31 /*VT_LPWSTR*/ && pv.pointerValue != IntPtr.Zero)
                    {
                        return Marshal.PtrToStringUni(pv.pointerValue) ?? "";
                    }
                    return "";
                }
                finally
                {
                    PropVariantClear(ref pv);
                }
            }
            catch
            {
                return "";
            }
        }

        // —— 快照映射（DeviceCenterService 消费） ——

        public List<DeviceSnapshot> EnumerateSnapshots()
        {
            List<DeviceSnapshot> result = new List<DeviceSnapshot>();
            AudioResult r = Enumerate();
            for (int i = 0; i < r.Endpoints.Count; i++)
            {
                EndpointInfo e = r.Endpoints[i];
                DeviceSnapshot s = new DeviceSnapshot();
                s.Category = DeviceCategory.Audio;
                s.Source = "CoreAudio";
                s.IsPresent = e.State == 1;
                s.InstanceKey = (e.DataFlow == 0 ? "audio:render:" : "audio:capture:")
                    + PnpTail(e.PnpInstanceId, e.Id);
                s.InstanceId = e.Id;
                s.Name = e.Name;
                s.AttributesJson = ToAttributes(e);
                result.Add(s);
            }
            return result;
        }

        private static string PnpTail(string pnpInstanceId, string fallback)
        {
            if (!string.IsNullOrEmpty(pnpInstanceId)) return pnpInstanceId;
            // 无 PnP 关联（虚拟/驱动端点）：退化为接口路径哈希，仍保持稳定
            int hash = 0;
            string k = fallback ?? "";
            for (int i = 0; i < k.Length; i++) hash = (hash * 31 + k[i]) & 0x7FFFFFFF;
            return "vp:" + hash.ToString("X8");
        }

        private static string ToAttributes(EndpointInfo e)
        {
            StringBuilder sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"flow\":\"").Append(e.DataFlow == 0 ? "render" : "capture").Append("\",");
            sb.Append("\"default\":").Append(e.IsDefaultConsole ? 1 : 0).Append(",");
            if (e.MixKnown)
            {
                sb.Append("\"mixRate\":").Append(e.MixRate).Append(",");
                sb.Append("\"mixChannels\":").Append(e.MixChannels).Append(",");
                sb.Append("\"mixBits\":").Append(e.MixBits).Append(",");
            }
            sb.Append("\"formatSource\":\"引擎混音格式（非支持格式列表）\",");
            if (!string.IsNullOrEmpty(e.PnpInstanceId))
            {
                sb.Append("\"pnpInstanceId\":\"").Append(
                    e.PnpInstanceId.Replace("\\", "\\\\").Replace("\"", "'")).Append("\",");
            }
            sb.Append("\"endpointId\":\"").Append(
                (e.Id ?? "").Replace("\\", "\\\\").Replace("\"", "'")).Append('"');
            sb.Append('}');
            return sb.ToString();
        }

        // —— COM 接口（vtable 顺序不可增删，见方案 §7.1 约束） ——

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private sealed class MMDeviceEnumeratorComObject
        {
        }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
            // GetDevice / Register/UnregisterNotification 位于其后，可省略
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
            int OpenPropertyStore(int stgmAccess, out IPropertyStore store);
            int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            int GetState(out int state);
        }

        [ComImport, Guid("1DA9D8FE-E70A-4A23-99CA-4EEAF3DCCDC1"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMEndpoint
        {
            int GetDataFlow(out int dataFlow);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            int GetCount(out int count);
            int Item(int index, out IMMDevice device);
        }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            int GetCount(out int count);
            int GetAt(int index, out PropertyKey key);
            int GetValue(ref PropertyKey key, ref PropVariant value);
            int SetValue(ref PropertyKey key, ref PropVariant value);
            int Commit();
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            int Initialize(int shareMode, int streamFlags, long bufferDuration,
                long periodicity, IntPtr format, IntPtr audioSessionGuid);
            int GetBufferSize(out uint bufferFrames);
            int GetStreamLatency(out long latency);
            int GetCurrentPadding(out uint padding);
            int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
            int GetMixFormat(out IntPtr format);
            // 其余方法（GetDevicePeriod/Start/Stop/...）位于 GetMixFormat 之后，可省略
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey
        {
            public Guid fmtid;
            public int pid;
            public PropertyKey(Guid f, int p) { fmtid = f; pid = p; }
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct PropVariant
        {
            [FieldOffset(0)] public short vt;
            [FieldOffset(8)] public IntPtr pointerValue;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WavFmt
        {
            public short tag;
            public short channels;
            public uint rate;
            public uint avgBytes;
            public short align;
            public short bits;
        }

        private static readonly PropertyKey PKEYDeviceFriendlyName =
            new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
        private static readonly PropertyKey PKEYDeviceInstanceId =
            new PropertyKey(new Guid("b3f8fa53-0004-438e-9003-51a46e139bfc"), 3);

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant pv);

        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr p);
    }
}
