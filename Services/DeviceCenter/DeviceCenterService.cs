using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using KeyInsight.Adapters.Display;
using KeyInsight.Adapters.Pnp;
using KeyInsight.Core.Devices;
using KeyInsight.Storage.DeviceStore;

namespace KeyInsight.Services.DeviceCenter
{
    /// <summary>
    /// 设备中心服务（方案 §4/§6，PR-2）：
    /// - 枚举：PnP（SetupAPI）+ 显示（GDI/DisplayConfig），均后台线程执行；
    /// - 热插拔：专用 STA 消息窗接收 WM_DEVICECHANGE（只转发事件，不查询），
    ///   经 DeviceEventCoalescer 防抖后触发单飞行刷新；
    /// - 持久化：档案/事件/诊断三个独立数据域，KDS 零接触；
    /// - UI：仅经 Changed 事件 + IDataRow 快照，UI 线程不做任何设备查询。
    /// </summary>
    public sealed class DeviceCenterService : IDisposable
    {
        private readonly DeviceStore _store;
        private readonly DeviceEventLog _events;
        private readonly DiagnosticStore _diagnostics;
        private readonly PnpDeviceAdapter _pnp = new PnpDeviceAdapter();
        private readonly DisplayAdapter _display = new DisplayAdapter();
        private readonly DeviceEventCoalescer _coalescer = new DeviceEventCoalescer();
        private readonly object _gate = new object();
        private DeviceNotifyWindow _window;
        private Thread _windowThread;
        private Timer _pollTimer;
        private volatile bool _refreshing;
        private long _lastRefreshTicks;
        private bool _dirty;                      // 合并后有待刷新事件
        private bool _disposed;
        private string _lastDefaultRender;        // 默认播放端点（§9.1 默认设备变化事件）
        private string _lastDefaultCapture;       // 默认录音端点
        private readonly KeyInsight.Adapters.Audio.AudioAdapter _audio =
            new KeyInsight.Adapters.Audio.AudioAdapter();

        /// <summary>设备数据变化（已在后台线程触发；UI 需自行 Post）。</summary>
        public event Action Changed;

        public DeviceCenterService(string dataDir)
        {
            _store = new DeviceStore(System.IO.Path.Combine(dataDir, "devices.v1.json"));
            _events = new DeviceEventLog(System.IO.Path.Combine(dataDir, "device-events.v1.jsonl"));
            _diagnostics = new DiagnosticStore(System.IO.Path.Combine(dataDir, "diagnostics.v1.json"));
        }

        public void Start()
        {
            StartHotPlugWindow();
            _pollTimer = new Timer(delegate
            {
                PollCoalescer();
            }, null, 250, 250);
            RefreshNow();                          // 后台首扫
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_pollTimer != null)
            {
                try { _pollTimer.Dispose(); } catch { }
                _pollTimer = null;
            }
            StopHotPlugWindow();
        }

        // —— UI 读取（缓存优先；枚举不在 UI 线程） ——

        public List<DeviceRecord> Records()
        {
            return _store.All();
        }

        public List<DeviceEventItem> RecentEvents(int count)
        {
            return _events.Recent(count);
        }

        public DeviceStore Store { get { return _store; } }
        public DiagnosticStore Diagnostics { get { return _diagnostics; } }
        public DisplayAdapter Display { get { return _display; } }

        /// <summary>总览四格计数：已连接 / 已断开 / 需要关注 / 尚未检测。</summary>
        public int[] Counts()
        {
            int connected = 0, disconnected = 0, attention = 0, notTested = 0;
            List<DeviceRecord> records = _store.All();
            for (int i = 0; i < records.Count; i++)
            {
                DeviceRecord r = records[i];
                if (r.Connection == ConnectionState.Connected) connected++;
                else if (r.Connection == ConnectionState.Disconnected) disconnected++;
                if (r.Health == HealthState.Attention || r.Health == HealthState.ConfirmedIssue)
                {
                    attention++;
                }
                if (r.Diagnostic == DiagnosticRunState.NotTested) notTested++;
            }
            return new int[] { connected, disconnected, attention, notTested };
        }

        public void SetUserAlias(string deviceId, string alias)
        {
            DeviceRecord r = _store.Get(deviceId);
            if (r == null) return;
            r.UserAlias = alias;
            _store.Upsert(r);
            RaiseChanged();
        }

        public void SetDeviceNote(string deviceId, string note)
        {
            DeviceRecord r = _store.Get(deviceId);
            if (r == null) return;
            r.UserNote = note ?? "";
            _store.Upsert(r);
        }

        /// <summary>用户确认身份合并（方案 §5.2：低置信度匹配需用户确认）。</summary>
        public void ConfirmIdentity(string deviceId)
        {
            DeviceRecord r = _store.Get(deviceId);
            if (r == null) return;
            r.IdentityConfirmedByUser = true;
            r.LastIdentityConfidence = 1.0;
            _store.Upsert(r);
            RaiseChanged();
        }

        public void Archive(string deviceId)
        {
            if (_store.Archive(deviceId))
            {
                DeviceRecord r = _store.Get(deviceId);
                if (r != null)
                {
                    DeviceEventItem e = new DeviceEventItem();
                    e.EventType = DeviceEventType.Archived;
                    e.DeviceId = r.DeviceId;
                    e.InstanceKey = r.InstanceKeys.Count > 0 ? r.InstanceKeys[0] : "";
                    e.Category = r.Category;
                    e.TimeUtc = DateTime.UtcNow;
                    _events.Append(e);
                }
                RaiseChanged();
            }
        }

        public void AddDiagnosticSession(DiagnosticSession session, List<DiagnosticFinding> findings)
        {
            _diagnostics.AddSession(session, findings);
            DeviceRecord r = _store.Get(session.DeviceId);
            if (r != null)
            {
                r.Diagnostic = session.Status;
                if (findings != null && findings.Count > 0)
                {
                    for (int i = 0; i < findings.Count; i++)
                    {
                        if (findings[i].Status == FindingStatus.UserConfirmed &&
                            findings[i].Severity != FindingSeverity.Info)
                        {
                            r.Health = HealthState.ConfirmedIssue;
                            break;
                        }
                    }
                    if (r.Health != HealthState.ConfirmedIssue &&
                        r.Health == HealthState.Unknown)
                    {
                        r.Health = HealthState.NoIssueObserved;
                    }
                }
                else if (r.Health == HealthState.Unknown)
                {
                    r.Health = HealthState.NoIssueObserved;
                }
                _store.Upsert(r);
            }
            RaiseChanged();
        }

        /// <summary>立即全量刷新（后台线程）。</summary>
        public void RefreshNow()
        {
            if (_disposed || _refreshing) return;
            long now = DateTime.UtcNow.Ticks;
            if ((now - _lastRefreshTicks) / (double)TimeSpan.TicksPerMillisecond < 400) return;
            _refreshing = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    ApplyAll();
                    _lastRefreshTicks = DateTime.UtcNow.Ticks;
                }
                finally
                {
                    _refreshing = false;
                    RaiseChanged();
                }
            });
        }

        // —— 合并 → 单飞行刷新 ——

        private void PollCoalescer()
        {
            if (_disposed) return;
            bool fire = false;
            lock (_gate)
            {
                if (_dirty)
                {
                    List<string> ready = _coalescer.PollReady(DateTime.UtcNow);
                    if (ready.Count > 0) fire = true;
                    if (_coalescer.PendingCount == 0) _dirty = false;
                }
            }
            if (fire) RefreshNow();
        }

        // —— 枚举与档案归并 ——

        private void ApplyAll()
        {
            List<DeviceSnapshot> snapshots = new List<DeviceSnapshot>();
            try { snapshots.AddRange(_display.Enumerate()); } catch { }
            try { snapshots.AddRange(_pnp.Enumerate()); } catch { }
            try { snapshots.AddRange(_audio.EnumerateSnapshots()); } catch { }
            try { snapshots.AddRange(KeyInsight.Adapters.Input.XInputAdapter.Enumerate()); } catch { }
            try { snapshots.AddRange(
                new KeyInsight.Adapters.Camera.CameraAdapter().EnumerateSnapshots()); } catch { }
            try { EnrichPrinters(snapshots); } catch { }
            try { EnrichDigitizers(snapshots); } catch { }
            ApplySnapshots(snapshots);
            TrackDefaultAudioEndpoints();
        }

        /// <summary>把 winspool 状态合并进 PnP 打印机快照（按名称匹配，方案 §9.4）。</summary>
        private static void EnrichPrinters(List<DeviceSnapshot> snapshots)
        {
            List<KeyInsight.Adapters.Office.PrinterAdapter.PrinterStatus> printers =
                KeyInsight.Adapters.Office.PrinterAdapter.Enumerate();
            if (printers.Count == 0) return;
            for (int i = 0; i < snapshots.Count; i++)
            {
                DeviceSnapshot s = snapshots[i];
                if (s.Category != DeviceCategory.Office || s.DeviceClass != "PRINTER") continue;
                for (int k = 0; k < printers.Count; k++)
                {
                    KeyInsight.Adapters.Office.PrinterAdapter.PrinterStatus ps = printers[k];
                    if (!NameMatches(s.Name, ps.Name) && !NameMatches(s.Model, ps.Name)) continue;
                    StringBuilder sb = new StringBuilder(s.AttributesJson ?? "{}");
                    if (sb[sb.Length - 1] == '}') sb.Length -= 1;
                    if (sb[sb.Length - 1] != '{') sb.Append(',');
                    sb.Append("\"printerStatus\":\"").Append(
                        KeyInsight.Adapters.Office.PrinterAdapter.StatusText(ps.StatusFlags)
                            .Replace("\\", "\\\\").Replace("\"", "'"))
                      .Append("\",\"printerJobs\":").Append(ps.Jobs)
                      .Append('}');
                    s.AttributesJson = sb.ToString();
                    break;
                }
            }
        }

        private static bool NameMatches(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return a.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0
                || b.IndexOf(a, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 数位板 HID 能力合并进 PnP 输入快照（方案 §9.4，PR-11）：
        /// 按归一化实例键匹配；能力来自 HID 顶层集合与值能力（真实解析，非名称猜测）。
        /// </summary>
        private static void EnrichDigitizers(List<DeviceSnapshot> snapshots)
        {
            List<KeyInsight.Adapters.Input.DigitizerAdapter.DigitizerCaps> digitizers =
                KeyInsight.Adapters.Input.DigitizerAdapter.Scan();
            if (digitizers.Count == 0) return;
            for (int i = 0; i < snapshots.Count; i++)
            {
                DeviceSnapshot s = snapshots[i];
                if (s.Category != DeviceCategory.Input || string.IsNullOrEmpty(s.InstanceId))
                {
                    continue;
                }
                string key = KeyInsight.Adapters.Input.DigitizerAdapter.NormalizeInstanceId(
                    s.InstanceId);
                for (int k = 0; k < digitizers.Count; k++)
                {
                    KeyInsight.Adapters.Input.DigitizerAdapter.DigitizerCaps d = digitizers[k];
                    if (!string.Equals(d.MatchKey, key, StringComparison.Ordinal)) continue;

                    StringBuilder sb = new StringBuilder(s.AttributesJson ?? "{}");
                    if (sb[sb.Length - 1] == '}') sb.Length -= 1;
                    if (sb[sb.Length - 1] != '{') sb.Append(',');
                    sb.Append("\"digitizerUsagePage\":13,\"digitizerUsage\":")
                      .Append(d.Usage)
                      .Append(",\"digitizerPressure\":").Append(d.Pressure ? 1 : 0)
                      .Append(",\"digitizerTiltX\":").Append(d.TiltX ? 1 : 0)
                      .Append(",\"digitizerTiltY\":").Append(d.TiltY ? 1 : 0)
                      .Append(",\"digitizerInputReportLen\":").Append(d.InputReportLen)
                      .Append(",\"digitizerBasis\":\"HID 顶层集合与值能力（hid.dll 解析）\"");
                    if (sb[sb.Length - 1] != '}') sb.Append('}');
                    s.AttributesJson = sb.ToString();
                    break;
                }
            }
        }

        /// <summary>默认音频端点变化 → 事件（方案 §9.1/§10.3，不采集音频内容）。</summary>
        private void TrackDefaultAudioEndpoints()
        {
            try
            {
                KeyInsight.Adapters.Audio.AudioAdapter.AudioResult r = _audio.Enumerate();
                bool changed = false;
                if (!string.Equals(r.DefaultRenderId, _lastDefaultRender, StringComparison.OrdinalIgnoreCase)
                    && _lastDefaultRender != null)
                {
                    changed = true;
                }
                if (!string.Equals(r.DefaultCaptureId, _lastDefaultCapture, StringComparison.OrdinalIgnoreCase)
                    && _lastDefaultCapture != null)
                {
                    changed = true;
                }
                if (_lastDefaultRender == null && _lastDefaultCapture == null)
                {
                    changed = false;    // 首轮只记录基线
                }
                _lastDefaultRender = r.DefaultRenderId;
                _lastDefaultCapture = r.DefaultCaptureId;
                if (changed)
                {
                    DeviceEventItem e = new DeviceEventItem();
                    e.EventType = DeviceEventType.DefaultEndpointChanged;
                    e.Category = DeviceCategory.Audio;
                    e.TimeUtc = DateTime.UtcNow;
                    e.EvidenceJson = "{\"renderChanged\":" + "1}";
                    _events.Append(e);
                    RaiseChanged();
                }
            }
            catch
            {
            }
        }

        internal void ApplySnapshots(List<DeviceSnapshot> snapshots)
        {
            if (snapshots == null) return;
            DateTime now = DateTime.UtcNow;
            List<DeviceEventItem> newEvents = new List<DeviceEventItem>();
            Dictionary<string, bool> seenRecords = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            bool changed = false;

            for (int i = 0; i < snapshots.Count; i++)
            {
                DeviceSnapshot s = snapshots[i];
                if (s == null || string.IsNullOrEmpty(s.InstanceKey)) continue;
                string identityKey = DeviceIdentityResolver.BuildIdentityKey(s);
                DeviceRecord record;
                DeviceIdentityResolver.MatchResult match =
                    DeviceIdentityResolver.Match(_store.All(), s, identityKey);

                if (match.AutoMerge || (match.Candidate != null &&
                    (match.Candidate.IdentityConfirmedByUser || match.NeedsReview && match.Confidence >= 0.7)))
                {
                    record = match.Candidate;
                }
                else
                {
                    record = _store.FindByInstanceKey(s.InstanceKey);
                }
                if (record == null)
                {
                    record = new DeviceRecord();
                    record.DeviceId = Guid.NewGuid().ToString("N");
                    record.FirstSeenUtc = now;
                    record.LastIdentityConfidence = match.Candidate == null ? 1.0 : match.Confidence;
                    record.IdentityKey = identityKey;
                    changed = true;
                }
                else if (!string.Equals(record.IdentityKey, identityKey, StringComparison.OrdinalIgnoreCase))
                {
                    record.IdentityKey = identityKey;
                    changed = true;
                }

                LifecyclePhase before = PhaseOf(record);
                LifecyclePhase after = DeviceLifecycle.Apply(before, s);
                ConnectionState oldConn = record.Connection;
                ConnectionState newConn = s.IsPresent ? ConnectionState.Connected : ConnectionState.Disconnected;

                if (!record.InstanceKeys.Contains(s.InstanceKey))
                {
                    if (record.InstanceKeys.Count >= 8) record.InstanceKeys.RemoveAt(0);
                    record.InstanceKeys.Add(s.InstanceKey);
                }

                record.Category = s.Category;
                record.Connection = newConn;
                record.LastSeenUtc = now;
                if (!string.IsNullOrEmpty(s.Name)) record.Name = s.Name;
                record.Manufacturer = s.Manufacturer;
                record.Model = s.Model;
                record.SerialNumber = s.SerialNumber;
                record.AttributesJson = s.AttributesJson;
                record.AttributesSource = s.Source;
                record.AttributesUpdatedUtc = now;
                if (s.ProblemCode != 0) record.Operational = OperationalState.Degraded;
                else if (newConn == ConnectionState.Connected) record.Operational = OperationalState.Available;

                // 事件（只记有意义的变化 —— 方案 §6.4 PersistOnlyMeaningfulChange）
                if (oldConn != newConn)
                {
                    DeviceEventItem e = new DeviceEventItem();
                    e.EventType = newConn == ConnectionState.Connected
                        ? DeviceEventType.Connected : DeviceEventType.Disconnected;
                    e.DeviceId = record.DeviceId;
                    e.InstanceKey = s.InstanceKey;
                    e.Category = s.Category;
                    e.TimeUtc = now;
                    newEvents.Add(e);
                    changed = true;
                }
                if (s.ProblemCode != 0 && record.Health == HealthState.Unknown)
                {
                    DeviceEventItem e = new DeviceEventItem();
                    e.EventType = DeviceEventType.AttentionCandidate;
                    e.DeviceId = record.DeviceId;
                    e.InstanceKey = s.InstanceKey;
                    e.Category = s.Category;
                    e.TimeUtc = now;
                    e.EvidenceJson = "{\"problemCode\":" + s.ProblemCode + "}";
                    newEvents.Add(e);
                    record.Health = HealthState.Attention;
                    changed = true;
                }
                if (s.Category == DeviceCategory.Display && before != LifecyclePhase.Unknown)
                {
                    // 显示模式变化检测（对比属性里的 current 串）
                    string oldCurrent = DeviceIdentityResolver.JsonField(record.AttributesJson, "current");
                    string newCurrent = DeviceIdentityResolver.JsonField(s.AttributesJson, "current");
                    if (!string.IsNullOrEmpty(oldCurrent) && oldCurrent != newCurrent)
                    {
                        DeviceEventItem e = new DeviceEventItem();
                        e.EventType = DeviceEventType.DisplayModeChanged;
                        e.DeviceId = record.DeviceId;
                        e.InstanceKey = s.InstanceKey;
                        e.Category = s.Category;
                        e.TimeUtc = now;
                        e.EvidenceJson = "{\"from\":\"" + oldCurrent + "\",\"to\":\"" + newCurrent + "\"}";
                        newEvents.Add(e);
                    }
                }

                seenRecords[record.DeviceId] = true;
                _store.Upsert(record);
            }

            // 本次快照未见的已连接记录 → 断开候选（PnP 类别才有意义；显示器由 GDI 枚举全量覆盖）
            List<DeviceRecord> all = _store.All();
            for (int i = 0; i < all.Count; i++)
            {
                DeviceRecord r = all[i];
                if (r.Connection != ConnectionState.Connected) continue;
                if (seenRecords.ContainsKey(r.DeviceId)) continue;
                if (r.Category == DeviceCategory.Display) continue;
                bool seenByInstance = false;
                for (int k = 0; k < snapshots.Count; k++)
                {
                    if (r.InstanceKeys.Contains(snapshots[k].InstanceKey))
                    {
                        seenByInstance = true;
                        break;
                    }
                }
                if (seenByInstance) continue;
                r.Connection = ConnectionState.Disconnected;
                r.Operational = OperationalState.Unknown;
                DeviceEventItem e = new DeviceEventItem();
                e.EventType = DeviceEventType.Disconnected;
                e.DeviceId = r.DeviceId;
                e.InstanceKey = r.InstanceKeys.Count > 0 ? r.InstanceKeys[0] : "";
                e.Category = r.Category;
                e.TimeUtc = now;
                newEvents.Add(e);
                changed = true;
                _store.Upsert(r);
            }

            for (int i = 0; i < newEvents.Count; i++) _events.Append(newEvents[i]);
            if (changed || newEvents.Count > 0) RaiseChanged();
        }

        private static LifecyclePhase PhaseOf(DeviceRecord r)
        {
            switch (r.Connection)
            {
                case ConnectionState.Connected: return LifecyclePhase.Connected;
                case ConnectionState.Disconnected: return LifecyclePhase.Disconnected;
                default: return LifecyclePhase.Unknown;
            }
        }

        private void RaiseChanged()
        {
            Action h = Changed;
            if (h != null)
            {
                try { h(); } catch { }
            }
        }

        // —— WM_DEVICECHANGE 消息窗（专用 STA 线程 + GetMessage 循环；只转发事件 —— 方案 §7） ——

        private int _windowThreadId;

        private void StartHotPlugWindow()
        {
            _windowThread = new Thread(delegate()
            {
                try
                {
                    _windowThreadId = GetCurrentThreadId();
                    _window = new DeviceNotifyWindow(delegate
                    {
                        // 热插拔通知：进合并器；实际枚举延迟到 quiet window 之后
                        lock (_gate)
                        {
                            _coalescer.Feed("device-tree", DateTime.UtcNow);
                            _dirty = true;
                        }
                    });
                    if (_window.Handle == IntPtr.Zero) return;
                    RunMessageLoop();
                }
                catch
                {
                }
            });
            _windowThread.SetApartmentState(ApartmentState.STA);
            _windowThread.IsBackground = true;
            _windowThread.Start();
        }

        [DllImport("kernel32.dll")]
        private static extern int GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern int GetMessage(out NativeMessage msg, IntPtr hwnd, int min, int max);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref NativeMessage msg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref NativeMessage msg);

        [DllImport("user32.dll")]
        private static extern void PostThreadMessage(int threadId, int msg, IntPtr w, IntPtr l);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMessage
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public int time;
            public int ptX;
            public int ptY;
        }

        private void RunMessageLoop()
        {
            NativeMessage msg = new NativeMessage();
            while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }

        private void StopHotPlugWindow()
        {
            try
            {
                if (_windowThreadId != 0)
                {
                    PostThreadMessage(_windowThreadId, 0x12, IntPtr.Zero, IntPtr.Zero); // WM_QUIT
                }
                if (_window != null) _window.Dispose();
            }
            catch
            {
            }
        }

        private sealed class DeviceNotifyWindow : System.Windows.Forms.NativeWindow, IDisposable
        {
            private readonly Action _onChange;
            private IntPtr _notification;

            public DeviceNotifyWindow(Action onChange)
            {
                _onChange = onChange;
                System.Windows.Forms.CreateParams cp = new System.Windows.Forms.CreateParams();
                cp.Caption = "KeyInsightDeviceNotify";
                cp.Style = unchecked((int)0x80000000);              // WS_POPUP（隐藏顶层窗，能收到广播）
                cp.ClassName = "STATIC";
                CreateHandle(cp);
                if (Handle != IntPtr.Zero)
                {
                    PnpInterop.DEV_BROADCAST_DEVICEINTERFACE filter =
                        new PnpInterop.DEV_BROADCAST_DEVICEINTERFACE();
                    filter.dbcc_size = Marshal.SizeOf(typeof(PnpInterop.DEV_BROADCAST_DEVICEINTERFACE));
                    filter.dbcc_devicetype = PnpInterop.DBT_DEVTYP_DEVICEINTERFACE;
                    filter.dbcc_classguid = PnpInterop.GuidDevinterfaceUsbDevice;
                    IntPtr buffer = Marshal.AllocHGlobal(filter.dbcc_size + 64);
                    try
                    {
                        Marshal.StructureToPtr(filter, buffer, false);
                        _notification = PnpInterop.RegisterDeviceNotification(Handle, buffer,
                            PnpInterop.DEVICE_NOTIFY_WINDOW_HANDLE);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
            }

            protected override void WndProc(ref System.Windows.Forms.Message m)
            {
                if (m.Msg == PnpInterop.WM_DEVICECHANGE)
                {
                    int type = m.WParam.ToInt32();
                    if (type == PnpInterop.DBT_DEVICEARRIVAL ||
                        type == PnpInterop.DBT_DEVICEREMOVECOMPLETE ||
                        type == PnpInterop.DBT_DEVNODES_CHANGED)
                    {
                        Action h = _onChange;
                        if (h != null) h();
                    }
                }
                base.WndProc(ref m);
            }

            public void Dispose()
            {
                if (_notification != IntPtr.Zero)
                {
                    try { PnpInterop.UnregisterDeviceNotification(_notification); } catch { }
                    _notification = IntPtr.Zero;
                }
                try { if (Handle != IntPtr.Zero) DestroyHandle(); } catch { }
            }
        }
    }
}
