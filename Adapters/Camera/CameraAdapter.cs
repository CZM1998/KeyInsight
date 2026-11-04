using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using KeyInsight.Core.Devices;

namespace KeyInsight.Adapters.Camera
{
    /// <summary>
    /// 摄像头 Adapter（方案 §9.3，PR-10）：
    /// - 身份：MFEnumDeviceSources（VIDCAP）→ 友好名 + 符号链接（跨插拔稳定）；
    /// - 媒体格式：Source Reader GetNativeMediaType 枚举 分辨率/帧率（UINT64 HI/LO 解码）；
    /// - 隐私红线（§12.3）：仅枚举身份与格式声明，绝不打开取流、不预览、不保存画面；
    ///   预览测试为用户主动功能，本版未接入（能力标记「未接入」，§15.1）；
    /// - 所有 COM 指针即取即放；任何失败路径返回空表/未读取，绝不抛出到 UI。
    /// 全部 GUID/IID 与 vtable 顺序已对照 Windows SDK 10.0.26100.0 头文件核验
    /// （mfidl.h 17942-17967 / mfobjects.idl 104-942+4359-4399 / mfreadwrite.idl 339-397 / mfapi.h 3065-3071）。
    /// </summary>
    public sealed class CameraAdapter
    {
        public sealed class CameraInfo
        {
            public string FriendlyName;
            public string SymbolicLink;         // 实例路径（身份辅助）
            public List<string> Formats = new List<string>();   // 如 "1280x720@30.0fps"
            public bool FormatsKnown;
        }

        // —— 已核验 GUID（SDK 10.0.26100.0） ——

        private static Guid AttrSourceType =
            new Guid("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3");           // MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE
        private static Guid AttrSourceTypeVidcap =
            new Guid("8ac3587a-4ae7-42d8-99e0-0a6013eef90f");           // ..._VIDCAP_GUID
        private static Guid AttrFriendlyName =
            new Guid("60d0e559-52f8-4fa2-bbce-acdb34a8ec01");           // ..._FRIENDLY_NAME
        private static Guid AttrSymbolicLink =
            new Guid("58f0aad8-22bf-4f8a-bb3d-d2c4978c6e2f");           // ..._VIDCAP_SYMBOLIC_LINK
        private static Guid MtFrameSize =
            new Guid("1652c33d-d6b2-4012-b834-72030849a37d");           // MF_MT_FRAME_SIZE
        private static Guid MtFrameRate =
            new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");           // MF_MT_FRAME_RATE
        private static Guid IidIMFMediaSource =
            new Guid("279a808d-aec7-40c8-9c6b-a6b492c78a66");           // IMFMediaSource

        private const int MFSTARTUP_LITE = 0x1;
        private const int MF_VERSION_WIN7 = 0x20070;                    // (2<<16)|112

        public sealed class EnumResult
        {
            public readonly List<CameraInfo> Cameras = new List<CameraInfo>();
            public bool MfAvailable;            // Media Foundation 运行时可用
            public string Error = "";           // 失败类别（诊断用，不抛出）
        }

        public EnumResult Enumerate()
        {
            EnumResult result = new EnumResult();
            IntPtr activateArray = IntPtr.Zero;
            IntPtr store = IntPtr.Zero;
            bool mfStarted = false;
            try
            {
                try { CoInitializeEx(IntPtr.Zero, 0x0); } catch { }   // MTA；已初始化则忽略
                int hr = MFStartup(MF_VERSION_WIN7, MFSTARTUP_LITE);
                if (hr != 0)
                {
                    result.Error = "MFStartup hr=0x" + hr.ToString("X8");
                    return result;
                }
                mfStarted = true;

                if (MFCreateAttributes(out store, 2) != 0 || store == IntPtr.Zero)
                {
                    result.Error = "MFCreateAttributes failed";
                    return result;
                }
                IMFAttributesFull attrs =
                    (IMFAttributesFull)Marshal.GetObjectForIUnknown(store);
                Guid vidcap = AttrSourceTypeVidcap;
                if (attrs.SetGUID(ref AttrSourceType, ref vidcap) != 0)
                {
                    result.Error = "SetGUID failed";
                    return result;
                }

                uint count = 0;
                if (MFEnumDeviceSources(store, out activateArray, out count) != 0
                    || count == 0 || activateArray == IntPtr.Zero)
                {
                    result.MfAvailable = true;
                    return result;                  // 无摄像头 = 合法空表
                }

                for (int i = 0; i < count; i++)
                {
                    IntPtr actPtr = Marshal.ReadIntPtr(activateArray, i * IntPtr.Size);
                    if (actPtr == IntPtr.Zero) continue;
                    CameraInfo cam = ReadCamera(actPtr);
                    if (cam != null) result.Cameras.Add(cam);
                    Marshal.Release(actPtr);
                }
                result.MfAvailable = true;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }
            finally
            {
                if (store != IntPtr.Zero) Marshal.Release(store);
                if (activateArray != IntPtr.Zero) CoTaskMemFree(activateArray);
                if (mfStarted)
                {
                    try { MFShutdown(); } catch { }
                }
            }
            return result;
        }

        private static CameraInfo ReadCamera(IntPtr activatePtr)
        {
            CameraInfo cam = new CameraInfo();
            try
            {
                IMFActivatePrefix activate =
                    (IMFActivatePrefix)Marshal.GetObjectForIUnknown(activatePtr);
                uint len;
                IntPtr namePtr;
                if (activate.GetAllocatedString(ref AttrFriendlyName, out namePtr, out len) == 0
                    && namePtr != IntPtr.Zero)
                {
                    cam.FriendlyName = Marshal.PtrToStringUni(namePtr) ?? "";
                    CoTaskMemFree(namePtr);
                }
                IntPtr linkPtr;
                if (activate.GetAllocatedString(ref AttrSymbolicLink, out linkPtr, out len) == 0
                    && linkPtr != IntPtr.Zero)
                {
                    cam.SymbolicLink = Marshal.PtrToStringUni(linkPtr) ?? "";
                    CoTaskMemFree(linkPtr);
                }

                // 媒体格式：Activate(IMFMediaSource) → Source Reader → GetNativeMediaType。
                // 只枚举声明，不 ReadSample、不开流（隐私红线 §12.3）。
                try
                {
                    object srcUnknown;
                    Guid iidSource = IidIMFMediaSource;
                    if (activate.ActivateObject(ref iidSource, out srcUnknown) == 0
                        && srcUnknown != null)
                    {
                        IntPtr srcPtr = Marshal.GetComInterfaceForObject(srcUnknown,
                            typeof(IMFMediaSourceMinimal));
                        try
                        {
                            IntPtr readerPtr;
                            if (MFCreateSourceReaderFromMediaSource(srcPtr, IntPtr.Zero,
                                out readerPtr) == 0 && readerPtr != IntPtr.Zero)
                            {
                                try
                                {
                                    ReadFormats(readerPtr, cam);
                                }
                                finally
                                {
                                    Marshal.Release(readerPtr);
                                }
                            }
                        }
                        finally
                        {
                            Marshal.Release(srcPtr);
                            Marshal.ReleaseComObject(srcUnknown);
                        }
                    }
                }
                catch
                {
                    cam.FormatsKnown = false;       // 格式未读取 ≠ 设备异常（§9.3）
                }
                return cam;
            }
            catch
            {
                return null;
            }
        }

        private static void ReadFormats(IntPtr readerPtr, CameraInfo cam)
        {
            IMFSourceReaderPrefix reader =
                (IMFSourceReaderPrefix)Marshal.GetObjectForIUnknown(readerPtr);
            cam.FormatsKnown = true;
            for (int idx = 0; idx < 24; idx++)
            {
                IntPtr mtPtr;
                if (reader.GetNativeMediaType(0, idx, out mtPtr) != 0 || mtPtr == IntPtr.Zero)
                {
                    break;
                }
                try
                {
                    IMFAttributesPrefix mt =
                        (IMFAttributesPrefix)Marshal.GetObjectForIUnknown(mtPtr);
                    ulong size, rate;
                    bool okSize = mt.GetUINT64(ref MtFrameSize, out size) == 0;
                    bool okRate = mt.GetUINT64(ref MtFrameRate, out rate) == 0;
                    if (okSize)
                    {
                        uint w = (uint)(size >> 32);
                        uint h = (uint)(size & 0xFFFFFFFF);
                        string fmt = w + "x" + h;
                        if (okRate && (rate >> 32) > 0)
                        {
                            double fps = (rate >> 32) / (double)(rate & 0xFFFFFFFF);
                            fmt += "@" + fps.ToString("F1") + "fps";
                        }
                        cam.Formats.Add(fmt);
                    }
                    Marshal.ReleaseComObject(mt);
                }
                finally
                {
                    Marshal.Release(mtPtr);
                }
            }
        }

        /// <summary>摄像头 → 统一设备快照（DeviceCenterService 消费）。</summary>
        public List<DeviceSnapshot> EnumerateSnapshots()
        {
            List<DeviceSnapshot> result = new List<DeviceSnapshot>();
            EnumResult r = Enumerate();
            for (int i = 0; i < r.Cameras.Count; i++)
            {
                CameraInfo cam = r.Cameras[i];
                DeviceSnapshot s = new DeviceSnapshot();
                s.Category = DeviceCategory.Video;
                s.Source = "MediaFoundation";
                s.IsPresent = true;
                string link = cam.SymbolicLink ?? "";
                s.InstanceKey = "video:mf:" + StableTail(link, i);
                s.InstanceId = link;
                s.Name = string.IsNullOrEmpty(cam.FriendlyName) ? "摄像头 " + (i + 1) : cam.FriendlyName;
                s.Model = cam.FriendlyName;
                s.AttributesJson = ToAttributes(cam);
                result.Add(s);
            }
            return result;
        }

        private static string ToAttributes(CameraInfo cam)
        {
            StringBuilder sb = new StringBuilder(512);
            sb.Append('{');
            sb.Append("\"formatsKnown\":").Append(cam.FormatsKnown ? 1 : 0).Append(",");
            sb.Append("\"formatCount\":").Append(cam.Formats.Count).Append(",");
            if (cam.Formats.Count > 0)
            {
                sb.Append("\"formats\":[");
                int shown = Math.Min(cam.Formats.Count, 10);
                for (int i = 0; i < shown; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(cam.Formats[i]).Append('"');
                }
                sb.Append("],");
            }
            sb.Append("\"previewTest\":\"未接入（用户主动功能，后续版本）\",");
            sb.Append("\"privacy\":\"仅枚举身份与格式声明，不打开取流、不预览、不保存画面\",");
            sb.Append("\"symbolicLink\":\"").Append(
                (cam.SymbolicLink ?? "").Replace("\\", "\\\\").Replace("\"", "'")).Append('"');
            sb.Append('}');
            return sb.ToString();
        }

        private static string StableTail(string text, int fallback)
        {
            if (string.IsNullOrEmpty(text)) return "idx" + fallback.ToString("X4");
            int hash = 0;
            for (int i = 0; i < text.Length; i++) hash = (hash * 31 + text[i]) & 0x7FFFFFFF;
            return hash.ToString("X8");
        }

        // —— COM 接口声明（vtable 顺序对照 SDK 头文件；只声明用到的前缀槽） ——
        //
        // IMFAttributes 槽位（mfobjects.idl 行号序）：
        //   1 GetItem 2 GetItemType 3 CompareItem 4 Compare 5 GetUINT32 6 GetUINT64
        //   7 GetGUID 8 GetStringLength 9 GetString 10 GetAllocatedString 11 GetBlobSize
        //   12 GetBlob 13 GetAllocatedBlob 14 SetItem 15 DeleteItem 16 DeleteAllItems
        //   17 SetUINT32 18 SetUINT64 19 SetGUID 20 SetString 21 SetBlob 22 SetUnknown
        //   23 LockStore 24 UnlockStore 25 GetCount 26 GetItemByIndex
        // IMFActivate（7FEE9E9A…）= 上述 26 槽 + 27 ActivateObject 28 ShutdownObject 29 DetachObject。

        /// <summary>IMFAttributes 槽 1-10 前缀（IMFMediaType 同构：只需 GetUINT64 槽 6）。</summary>
        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFAttributesPrefix
        {
            int GetItem(ref Guid key, IntPtr value);                                       // 1
            int GetItemType(ref Guid key, out int type);                                   // 2
            int CompareItem(ref Guid key, IntPtr value, out int same);                     // 3
            int Compare(IntPtr other, int matchType, out int result);                      // 4
            int GetUINT32(ref Guid key, out uint value);                                   // 5
            int GetUINT64(ref Guid key, out ulong value);                                  // 6
            int GetGUID(ref Guid key, out Guid value);                                     // 7
            int GetStringLength(ref Guid key, out uint length);                            // 8
            int GetString(ref Guid key, IntPtr buffer, uint bufSize, out uint length);     // 9
            int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);       // 10
        }

        /// <summary>IMFAttributes 全 26 槽（供 MFCreateAttributes 的 SetGUID 槽 19）。</summary>
        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFAttributesFull
        {
            int GetItem(ref Guid key, IntPtr value);                                       // 1
            int GetItemType(ref Guid key, out int type);                                   // 2
            int CompareItem(ref Guid key, IntPtr value, out int same);                     // 3
            int Compare(IntPtr other, int matchType, out int result);                      // 4
            int GetUINT32(ref Guid key, out uint value);                                   // 5
            int GetUINT64(ref Guid key, out ulong value);                                  // 6
            int GetGUID(ref Guid key, out Guid value);                                     // 7
            int GetStringLength(ref Guid key, out uint length);                            // 8
            int GetString(ref Guid key, IntPtr buffer, uint bufSize, out uint length);     // 9
            int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);       // 10
            int GetBlobSize(ref Guid key, out uint size);                                  // 11
            int GetBlob(ref Guid key, IntPtr buffer, uint bufSize, out uint length);       // 12
            int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out uint size);          // 13
            int SetItem(ref Guid key, IntPtr value);                                       // 14
            int DeleteItem(ref Guid key);                                                  // 15
            int DeleteAllItems();                                                          // 16
            int SetUINT32(ref Guid key, uint value);                                       // 17
            int SetUINT64(ref Guid key, ulong value);                                      // 18
            int SetGUID(ref Guid key, ref Guid value);                                     // 19
            int SetString(ref Guid key, IntPtr value);                                     // 20
            int SetBlob(ref Guid key, IntPtr buffer, uint size);                           // 21
            int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object obj);  // 22
            int LockStore();                                                               // 23
            int UnlockStore();                                                             // 24
            int GetCount(out uint count);                                                  // 25
            int GetItemByIndex(int index, out Guid key, IntPtr value);                     // 26
        }

        /// <summary>IMFActivate = IMFAttributes 26 槽 + ActivateObject/ShutdownObject/DetachObject。</summary>
        [ComImport, Guid("7fee9e9a-4a89-47a6-899c-b6a53a70fb67"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFActivatePrefix
        {
            int GetItem(ref Guid key, IntPtr value);                                       // 1
            int GetItemType(ref Guid key, out int type);                                   // 2
            int CompareItem(ref Guid key, IntPtr value, out int same);                     // 3
            int Compare(IntPtr other, int matchType, out int result);                      // 4
            int GetUINT32(ref Guid key, out uint value);                                   // 5
            int GetUINT64(ref Guid key, out ulong value);                                  // 6
            int GetGUID(ref Guid key, out Guid value);                                     // 7
            int GetStringLength(ref Guid key, out uint length);                            // 8
            int GetString(ref Guid key, IntPtr buffer, uint bufSize, out uint length);     // 9
            int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);       // 10
            int GetBlobSize(ref Guid key, out uint size);                                  // 11
            int GetBlob(ref Guid key, IntPtr buffer, uint bufSize, out uint length);       // 12
            int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out uint size);          // 13
            int SetItem(ref Guid key, IntPtr value);                                       // 14
            int DeleteItem(ref Guid key);                                                  // 15
            int DeleteAllItems();                                                          // 16
            int SetUINT32(ref Guid key, uint value);                                       // 17
            int SetUINT64(ref Guid key, ulong value);                                      // 18
            int SetGUID(ref Guid key, ref Guid value);                                     // 19
            int SetString(ref Guid key, IntPtr value);                                     // 20
            int SetBlob(ref Guid key, IntPtr buffer, uint size);                           // 21
            int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object obj);  // 22
            int LockStore();                                                               // 23
            int UnlockStore();                                                             // 24
            int GetCount(out uint count);                                                  // 25
            int GetItemByIndex(int index, out Guid key, IntPtr value);                     // 26
            int ActivateObject(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object obj); // 27
            int ShutdownObject();                                                          // 28
            int DetachObject();                                                            // 29
        }

        /// <summary>IMFSourceReader 槽 1-3（mfreadwrite.idl 361-397 行序）。</summary>
        [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSourceReaderPrefix
        {
            int GetStreamSelection(int streamIndex, out int selected);                     // 1
            int SetStreamSelection(int streamIndex, int selected);                         // 2
            int GetNativeMediaType(int streamIndex, int mediaTypeIndex, out IntPtr type);  // 3
        }

        /// <summary>IMFMediaSource 最小标记（QI 用；不调用其方法）。</summary>
        [ComImport, Guid("279a808d-aec7-40c8-9c6b-a6b492c78a66"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaSourceMinimal
        {
        }

        // —— MF API 导入（mfplat.dll / mfreadwrite.dll） ——

        [DllImport("mfplat.dll")]
        private static extern int MFStartup(int version, int flags);

        [DllImport("mfplat.dll")]
        private static extern int MFShutdown();

        [DllImport("mfplat.dll")]
        private static extern int MFCreateAttributes(out IntPtr attributes, uint initialSize);

        [DllImport("mfplat.dll")]
        private static extern int MFEnumDeviceSources(IntPtr attributes,
            out IntPtr activateArray, out uint count);

        [DllImport("mfreadwrite.dll")]
        private static extern int MFCreateSourceReaderFromMediaSource(IntPtr source,
            IntPtr attributes, out IntPtr reader);

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr reserved, int coInit);

        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr p);
    }
}
