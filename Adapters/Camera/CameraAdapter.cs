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
        private static Guid MtMajorTypeVideo =
            new Guid("73646976-0000-0010-8000-00aa00389b71");           // MFMediaType_Video (mfapi.h 3699)
        private static Guid MtSubtypeRGB32 =
            new Guid("00000016-0000-0010-8000-00aa00389b71");           // MFVideoFormat_RGB32 (D3DFMT_X8R8G8B8=22, mfapi.h 2248/d3d9types.h 1379)
        private static Guid MtMajorType =
            new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");           // MF_MT_MAJOR_TYPE (mfapi.h 2607)
        private static Guid MtSubtype =
            new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");           // MF_MT_SUBTYPE (mfapi.h 2611)

        /// <summary>MF_SOURCE_READER_CURRENT_TYPE_INDEX（mfreadwrite.idl 333）。</summary>
        internal const int ReaderCurrentTypeIndex = unchecked((int)0xFFFFFFFF);

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
            sb.Append("\"previewTest\":\"已接入：设备详情页「预览测试」（用户主动启动）\",");
            sb.Append("\"privacy\":\"仅枚举身份与格式声明，不打开取流、不预览、不保存画面\",");
            sb.Append("\"symbolicLink\":\"").Append(
                (cam.SymbolicLink ?? "").Replace("\\", "\\\\").Replace("\"", "'")).Append('"');
            sb.Append('}');
            return sb.ToString();
        }

        // —— 用户主动预览（方案 §9.3）：仅由「预览测试」按钮触发；退出即释放全部资源 ——

        /// <summary>预览会话句柄。Dispose 释放 Reader + Source（ShutdownObject）+ MFShutdown。</summary>
        public sealed class PreviewSession : IDisposable
        {
            internal IntPtr Reader;
            internal IntPtr Activate;
            internal object SourceUnknown;
            internal int Width;
            internal int Height;
            public bool Ok { get { return Reader != IntPtr.Zero && Width > 0; } }

            public void Dispose()
            {
                try
                {
                    if (Reader != IntPtr.Zero)
                    {
                        try
                        {
                            IMFSourceReaderPrefix r =
                                (IMFSourceReaderPrefix)Marshal.GetObjectForIUnknown(Reader);
                            r.Flush(0);
                        }
                        catch { }
                        Marshal.Release(Reader);
                        Reader = IntPtr.Zero;
                    }
                    if (Activate != IntPtr.Zero)
                    {
                        try
                        {
                            IMFActivatePrefix a =
                                (IMFActivatePrefix)Marshal.GetObjectForIUnknown(Activate);
                            a.ShutdownObject();
                        }
                        catch { }
                        Marshal.Release(Activate);
                        Activate = IntPtr.Zero;
                    }
                    if (SourceUnknown != null)
                    {
                        try { Marshal.ReleaseComObject(SourceUnknown); } catch { }
                        SourceUnknown = null;
                    }
                    try { MFShutdown(); } catch { }
                }
                catch
                {
                }
            }
        }

        /// <summary>
        /// 按符号链接打开 RGB32 预览 Reader（同步模式）。失败返回 Ok=false 的会话（不抛出）。
        /// 必须在调用方后台线程执行；调用前该线程应 CoInitializeEx（MTA）。
        /// </summary>
        public static PreviewSession OpenPreview(string symbolicLink)
        {
            PreviewSession session = new PreviewSession();
            try
            {
                try { CoInitializeEx(IntPtr.Zero, 0x0); } catch { }
                if (MFStartup(MF_VERSION_WIN7, MFSTARTUP_LITE) != 0) return session;

                IntPtr store = IntPtr.Zero;
                IntPtr activateArray = IntPtr.Zero;
                try
                {
                    if (MFCreateAttributes(out store, 2) != 0 || store == IntPtr.Zero) return session;
                    IMFAttributesFull attrs =
                        (IMFAttributesFull)Marshal.GetObjectForIUnknown(store);
                    Guid srcType = AttrSourceType;
                    Guid vidcap = AttrSourceTypeVidcap;
                    if (attrs.SetGUID(ref srcType, ref vidcap) != 0) return session;

                    uint count = 0;
                    if (MFEnumDeviceSources(store, out activateArray, out count) != 0) return session;

                    string want = NormalizeLink(symbolicLink);
                    IntPtr actPtr = IntPtr.Zero;
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr p = Marshal.ReadIntPtr(activateArray, i * IntPtr.Size);
                        if (p == IntPtr.Zero) continue;
                        IMFActivatePrefix a = (IMFActivatePrefix)Marshal.GetObjectForIUnknown(p);
                        uint len;
                        IntPtr linkPtr;
                        if (a.GetAllocatedString(ref AttrSymbolicLink, out linkPtr, out len) == 0
                            && linkPtr != IntPtr.Zero)
                        {
                            string link = Marshal.PtrToStringUni(linkPtr) ?? "";
                            CoTaskMemFree(linkPtr);
                            if (NormalizeLink(link) == want)
                            {
                                actPtr = p;
                                break;
                            }
                        }
                        Marshal.Release(p);
                    }
                    if (actPtr == IntPtr.Zero) return session;      // 未找到匹配摄像头

                    session.Activate = actPtr;
                    IMFActivatePrefix act =
                        (IMFActivatePrefix)Marshal.GetObjectForIUnknown(actPtr);
                    object srcUnknown;
                    Guid iidSource = IidIMFMediaSource;
                    if (act.ActivateObject(ref iidSource, out srcUnknown) != 0
                        || srcUnknown == null)
                    {
                        return session;
                    }
                    session.SourceUnknown = srcUnknown;
                    IntPtr srcPtr = Marshal.GetComInterfaceForObject(srcUnknown,
                        typeof(IMFMediaSourceMinimal));
                    try
                    {
                        if (MFCreateSourceReaderFromMediaSource(srcPtr, IntPtr.Zero,
                            out session.Reader) != 0 || session.Reader == IntPtr.Zero)
                        {
                            session.Reader = IntPtr.Zero;
                            return session;
                        }
                    }
                    finally
                    {
                        Marshal.Release(srcPtr);
                    }

                    // 输出格式 → RGB32（Source Reader 自动插入转换器）
                    IntPtr mt;
                    if (MFCreateMediaType(out mt) != 0 || mt == IntPtr.Zero) return session;
                    try
                    {
                        IMFAttributesFull mtAttrs =
                            (IMFAttributesFull)Marshal.GetObjectForIUnknown(mt);
                        Guid video = MtMajorTypeVideo;
                        Guid rgb32 = MtSubtypeRGB32;
                        if (mtAttrs.SetGUID(ref MtMajorType, ref video) != 0) return session;
                        if (mtAttrs.SetGUID(ref MtSubtype, ref rgb32) != 0) return session;
                        IMFSourceReaderPrefix reader =
                            (IMFSourceReaderPrefix)Marshal.GetObjectForIUnknown(session.Reader);
                        if (reader.SetCurrentMediaType(0, IntPtr.Zero, mt) != 0) return session;
                    }
                    finally
                    {
                        Marshal.Release(mt);
                    }

                    // 当前类型 → 帧尺寸（FRAME_SIZE HI32=Width LO32=Height）
                    IMFSourceReaderPrefix r2 =
                        (IMFSourceReaderPrefix)Marshal.GetObjectForIUnknown(session.Reader);
                    IntPtr curMt;
                    if (r2.GetNativeMediaType(0, ReaderCurrentTypeIndex, out curMt) == 0
                        && curMt != IntPtr.Zero)
                    {
                        try
                        {
                            IMFAttributesPrefix mtAttrs =
                                (IMFAttributesPrefix)Marshal.GetObjectForIUnknown(curMt);
                            ulong size;
                            if (mtAttrs.GetUINT64(ref MtFrameSize, out size) == 0)
                            {
                                session.Width = (int)(size >> 32);
                                session.Height = (int)(size & 0xFFFFFFFF);
                            }
                        }
                        finally
                        {
                            Marshal.Release(curMt);
                        }
                    }
                }
                finally
                {
                    if (activateArray != IntPtr.Zero) CoTaskMemFree(activateArray);
                    if (store != IntPtr.Zero) Marshal.Release(store);
                }
            }
            catch
            {
            }
            return session;
        }

        /// <summary>读取一帧 RGB32 并解码为自上而下 Bitmap；失败/取消返回 null。预览线程调用。</summary>
        public static System.Drawing.Bitmap ReadPreviewFrame(IntPtr reader, int width, int height)
        {
            if (reader == IntPtr.Zero || width <= 0 || height <= 0) return null;
            try
            {
                IMFSourceReaderPrefix reader2 =
                    (IMFSourceReaderPrefix)Marshal.GetObjectForIUnknown(reader);
                uint stream, flags;
                long timestamp;
                IntPtr samplePtr;
                if (reader2.ReadSample(0, 0, out stream, out flags, out timestamp,
                    out samplePtr) != 0 || samplePtr == IntPtr.Zero)
                {
                    return null;        // EOS / gap / 失败：本帧跳过
                }
                try
                {
                    IMFSamplePrefix sample =
                        (IMFSamplePrefix)Marshal.GetObjectForIUnknown(samplePtr);
                    IntPtr bufPtr;
                    if (sample.ConvertToContiguousBuffer(out bufPtr) != 0 || bufPtr == IntPtr.Zero)
                    {
                        return null;
                    }
                    try
                    {
                        IMFMediaBufferPrefix buffer =
                            (IMFMediaBufferPrefix)Marshal.GetObjectForIUnknown(bufPtr);
                        IntPtr data;
                        uint maxLen, curLen;
                        if (buffer.Lock(out data, out maxLen, out curLen) != 0
                            || data == IntPtr.Zero)
                        {
                            return null;
                        }
                        try
                        {
                            int srcStride = width * 4;
                            long needed = (long)srcStride * height;
                            if (curLen < (ulong)needed) return null;

                            byte[] raw = new byte[(int)curLen];
                            Marshal.Copy(data, raw, 0, (int)curLen);

                            System.Drawing.Bitmap bmp =
                                new System.Drawing.Bitmap(width, height,
                                    System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                            System.Drawing.Imaging.BitmapData bd = bmp.LockBits(
                                new System.Drawing.Rectangle(0, 0, width, height),
                                System.Drawing.Imaging.ImageLockMode.WriteOnly,
                                System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                            try
                            {
                                // MF RGB32 帧为自下而上（BI_RGB 负高约定）→ 行序翻转
                                for (int dstRow = 0; dstRow < height; dstRow++)
                                {
                                    int srcRow = height - 1 - dstRow;
                                    System.Runtime.InteropServices.Marshal.Copy(raw,
                                        srcRow * srcStride,
                                        new IntPtr(bd.Scan0.ToInt64() + dstRow * bd.Stride),
                                        srcStride);
                                }
                            }
                            finally
                            {
                                bmp.UnlockBits(bd);
                            }
                            return bmp;
                        }
                        finally
                        {
                            try { buffer.Unlock(); } catch { }
                        }
                    }
                    finally
                    {
                        Marshal.Release(bufPtr);
                    }
                }
                finally
                {
                    Marshal.Release(samplePtr);
                }
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizeLink(string link)
        {
            string k = (link ?? "").Trim().ToLowerInvariant();
            return k.Replace('/', '\\');
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

        /// <summary>IMFSourceReader 槽 1-8（mfreadwrite.idl 361-536 行序）。</summary>
        [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSourceReaderPrefix
        {
            int GetStreamSelection(int streamIndex, out int selected);                     // 1
            int SetStreamSelection(int streamIndex, int selected);                         // 2
            int GetNativeMediaType(int streamIndex, int mediaTypeIndex, out IntPtr type);  // 3
            int GetCurrentMediaType(int streamIndex, out IntPtr type);                     // 4
            int SetCurrentMediaType(int streamIndex, IntPtr reserved, IntPtr mediaType);   // 5
            int SetCurrentPosition(ref Guid format, IntPtr position);                      // 6
            int ReadSample(int streamIndex, int controlFlags, out uint actualStream,
                out uint streamFlags, out long timestamp, out IntPtr sample);              // 7
            int Flush(int streamIndex);                                                    // 8
        }

        /// <summary>IMFMediaSource 最小标记（QI 用；不调用其方法）。</summary>
        [ComImport, Guid("279a808d-aec7-40c8-9c6b-a6b492c78a66"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaSourceMinimal
        {
        }

        /// <summary>IMFMediaBuffer 4 槽（mfobjects.idl 1025-1120 行序）。</summary>
        [ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaBufferPrefix
        {
            int Lock(out IntPtr data, out uint maxLength, out uint currentLength);   // 1
            int Unlock();                                                             // 2
            int GetCurrentLength(out uint currentLength);                             // 3
            int GetMaxLength(out uint maxLength);                                     // 4
        }

        /// <summary>
        /// IMFSample = IMFAttributes 26 槽 + 12 槽（mfobjects.idl 1168-1355 行序）；
        /// 本声明只用 ConvertToContiguousBuffer = 槽 35。
        /// </summary>
        [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSamplePrefix
        {
            // IMFAttributes 1-26（同 IMFAttributesFull 顺序）
            int GetItem(ref Guid key, IntPtr value);
            int GetItemType(ref Guid key, out int type);
            int CompareItem(ref Guid key, IntPtr value, out int same);
            int Compare(IntPtr other, int matchType, out int result);
            int GetUINT32(ref Guid key, out uint value);
            int GetUINT64(ref Guid key, out ulong value);
            int GetGUID(ref Guid key, out Guid value);
            int GetStringLength(ref Guid key, out uint length);
            int GetString(ref Guid key, IntPtr buffer, uint bufSize, out uint length);
            int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
            int GetBlobSize(ref Guid key, out uint size);
            int GetBlob(ref Guid key, IntPtr buffer, uint bufSize, out uint length);
            int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out uint size);
            int SetItem(ref Guid key, IntPtr value);
            int DeleteItem(ref Guid key);
            int DeleteAllItems();
            int SetUINT32(ref Guid key, uint value);
            int SetUINT64(ref Guid key, ulong value);
            int SetGUID(ref Guid key, ref Guid value);
            int SetString(ref Guid key, IntPtr value);
            int SetBlob(ref Guid key, IntPtr buffer, uint size);
            int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object obj);
            int LockStore();
            int UnlockStore();
            int GetCount(out uint count);
            int GetItemByIndex(int index, out Guid key, IntPtr value);
            // IMFSample 27-35（1156 起行序）
            int GetSampleFlags(out uint flags);                                        // 27
            int SetSampleFlags(uint flags);                                            // 28
            int GetSampleTime(out long time);                                          // 29
            int SetSampleTime(long time);                                              // 30
            int GetSampleDuration(out long duration);                                  // 31
            int SetSampleDuration(long duration);                                      // 32
            int GetBufferCount(out uint count);                                        // 33
            int GetBufferByIndex(int index, out IntPtr buffer);                        // 34
            int ConvertToContiguousBuffer(out IntPtr buffer);                          // 35
        }

        // —— MF API 导入（mfplat.dll / mfreadwrite.dll） ——

        [DllImport("mfplat.dll")]
        private static extern int MFStartup(int version, int flags);

        [DllImport("mfplat.dll")]
        private static extern int MFShutdown();

        [DllImport("mfplat.dll")]
        private static extern int MFCreateAttributes(out IntPtr attributes, uint initialSize);

        [DllImport("mfplat.dll")]
        private static extern int MFCreateMediaType(out IntPtr mediaType);

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
