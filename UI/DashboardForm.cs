using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using KeyInsight.Core.Policy;
using KeyInsight.Hook;
using KeyInsight.Stats;
using KeyInsight.UI.Pages;

namespace KeyInsight.UI
{
    public sealed class DashboardForm : Form, IShellActions
    {
        private const int TitleHeight = 58;
        private const int SidebarWidth = 204;
        private const int ContentPad = 24;
        private const int ScrollStrip = 14;

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private readonly IStatsSource _source;
        private readonly Config _cfg;
        private readonly DebounceHook _hook;
        private readonly MouseHook _mouseHook;
        private readonly KeyPolicyStore _keyPolicies;
        private readonly RawInputTracker _rawInput;
        private readonly HookSupervisor _supervisor;
        private readonly Core.Telemetry.StatsEngineSink _sink;
        private readonly KeyInsight.Update.Orchestration.UpdateOrchestrator _updater;
        private readonly KeyInsight.Update.Routing.RouteCatalog _routeCatalog;
        private readonly Action _checkUpdates;
        private readonly PageBase[] _pages;
        // 6 个一级入口：总览 / 键盘 / 鼠标 / 设备（Device Center）/ 分析与诊断 / 设置与数据
        private readonly string[] _navLabels = new string[] { "总览", "键盘", "鼠标", "设备", "分析与诊断", "设置与数据" };
        private readonly string[] _navGlyphs = new string[] { "\uE80F", "\uE765", "\uE962", "\uE721", "\uE95E", "\uE713" };
        private static readonly int[][] _groupPages = new int[][]
        {
            new int[] { 0 },          // 总览
            new int[] { 1, 6 },       // 键盘热力图 / 防抖统计
            new int[] { 2 },          // 鼠标统计
            new int[] { 9, 10, 11 },  // 设备中心 / 显示设备 / USB与扩展（PR-4/6/9）
            new int[] { 5, 4, 3 },    // 按键健康 / 时间分析 / 应用统计
            new int[] { 7, 8 }        // 设置 / 高级与设备
        };
        private readonly int[] _groupActive = new int[] { 0, 1, 2, 9, 5, 7 };
        private readonly Rectangle[] _navRects = new Rectangle[6];
        private Rectangle[] _tabRects = new Rectangle[0];
        private int[] _tabPageIndices = new int[0];
        private int _currentGroup;
        private int _current;
        private int _hoverNav = -1;
        private bool _hoverMin;
        private bool _hoverMax;
        private bool _hoverClose;
        private bool _hoverStatus;
        private Rectangle _statusRect;
        private Rectangle _scrollTrack;
        private Rectangle _scrollThumb;
        private bool _scrollDragging;
        private bool _scrollHover;
        private int _scrollGrabOffset;
        private Rectangle _minRect;
        private Rectangle _maxRect;
        private Rectangle _closeRect;
        private readonly Timer _timer;
        private readonly Image _logo;
        // 彩蛋：连点标题栏 Logo，图标会“脱落”掉到侧栏底部，停留片刻后自动归位
        private readonly Timer _logoAnim;
        private readonly Random _rng = new Random();
        private const int LogoBaseX = 18;
        private const int LogoBaseY = 14;
        private const int LogoSize = 30;
        private int _logoTaps;
        private DateTime _logoLastTapUtc = DateTime.MinValue;
        private int _logoState;                 // 0 静止 / 1 下落 / 2 落地停留 / 3 归位
        private float _logoX = LogoBaseX;
        private float _logoY = LogoBaseY;
        private float _logoVx;
        private float _logoVy;
        private float _logoRot;
        private float _logoSpin;
        private float _logoFromX;
        private float _logoFromY;
        private float _logoFromRot;
        private DateTime _logoAnimLastUtc;
        private DateTime _logoRestUntilUtc;
        private DateTime _logoReturnStartUtc;
        private readonly Services.DeviceCenter.DeviceCenterService _deviceCenter;
        private bool _exiting;
        private StatsEngine _asyncEngine;
        private long _pendingSnapshotGen;
        private int _pendingSnapshotPage = -1;
        private DateTime _lastSnapshotRequestUtc = DateTime.MinValue;
        private long _lastAppliedDataVersion = -1;

        public bool ScreenshotMode;

        public DashboardForm(IStatsSource source, Config cfg, DebounceHook hook, MouseHook mouseHook, Action checkUpdates)
            : this(source, cfg, hook, mouseHook, checkUpdates, null, null, null, null, null, null, null)
        {
        }

        public DashboardForm(IStatsSource source, Config cfg, DebounceHook hook, MouseHook mouseHook,
            Action checkUpdates, KeyPolicyStore keyPolicies, RawInputTracker rawInput)
            : this(source, cfg, hook, mouseHook, checkUpdates, keyPolicies, rawInput, null, null, null, null, null)
        {
        }

        public DashboardForm(IStatsSource source, Config cfg, DebounceHook hook, MouseHook mouseHook,
            Action checkUpdates, KeyPolicyStore keyPolicies, RawInputTracker rawInput,
            HookSupervisor supervisor, Core.Telemetry.StatsEngineSink sink)
            : this(source, cfg, hook, mouseHook, checkUpdates, keyPolicies, rawInput, supervisor, sink, null, null, null)
        {
        }

        public DashboardForm(IStatsSource source, Config cfg, DebounceHook hook, MouseHook mouseHook,
            Action checkUpdates, KeyPolicyStore keyPolicies, RawInputTracker rawInput,
            HookSupervisor supervisor, Core.Telemetry.StatsEngineSink sink,
            KeyInsight.Update.Orchestration.UpdateOrchestrator updater,
            KeyInsight.Update.Routing.RouteCatalog routeCatalog,
            Services.DeviceCenter.DeviceCenterService deviceCenter)
        {
            _source = source;
            _cfg = cfg;
            _deviceCenter = deviceCenter;
            _hook = hook;
            _mouseHook = mouseHook;
            _checkUpdates = checkUpdates;
            _keyPolicies = keyPolicies;
            _rawInput = rawInput;
            _supervisor = supervisor;
            _sink = sink;
            _updater = updater;
            _routeCatalog = routeCatalog;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(Ui.Px(1200), Ui.Px(780));
            MinimumSize = new Size(Ui.Px(1000), Ui.Px(680));
            BackColor = Theme.P.Bg;
            DoubleBuffered = true;
            Text = "KeyInsight";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            _logo = LoadLogo();
            Icon = LoadAppIcon();

            _logoAnim = new Timer();
            _logoAnim.Interval = 15;
            _logoAnim.Tick += LogoAnimTick;

            _pages = new PageBase[]
            {
                new OverviewPage(),
                new KeyboardPage(this),
                new MousePage(this),
                new AppsPage(),
                new TimePage(),
                new HealthPage(this),
                new DebouncePage(this),
                new SettingsPage(this),
                new AdvancedPage(this),
                new DeviceCenterPage(this),
                new DisplayCenterPage(this),
                new UsbPage(this)
            };

            for (int i = 0; i < _pages.Length; i++)
            {
                PageBase page = _pages[i];
                page.Visible = i == 0;
                page.DataRequested += delegate { RefreshSnapshot(); };
                page.ScrollChanged += delegate { Invalidate(); };
                Controls.Add(page);
            }

            LayoutPages();

            _timer = new Timer();
            _timer.Interval = 1000;
            _timer.Tick += delegate
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                RequestSnapshot(false);
                UiMetrics.RecordTick((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 /
                    System.Diagnostics.Stopwatch.Frequency);
            };
            _timer.Start();

            Theme.Changed += ApplyTheme;
            Theme.Changed += delegate { RenderResources.DisposeAll(); Charts.InvalidateGeometry(); };
            _asyncEngine = _source as StatsEngine;
            if (_asyncEngine != null)
            {
                _asyncEngine.SnapshotBuilt += OnSnapshotBuilt;
            }
            RefreshSnapshot();
        }

        public Config Cfg { get { return _cfg; } }
        public IStatsSource Source { get { return _source; } }
        public KeyPolicyStore KeyPolicies { get { return _keyPolicies; } }
        public HookSupervisor Supervisor { get { return _supervisor; } }
        public long TelemetryDropped { get { return _sink == null ? 0 : _sink.DroppedCount; } }
        public bool CaptureEnabled { get { return _cfg.CaptureEnabled; } }
        public bool DiagnosticsEnabled { get { return _cfg.DiagnosticsEnabled; } }
        public bool FloatingSummaryOn { get { return _cfg.FloatingSummary; } }
        public KeyInsight.Update.Orchestration.UpdateOrchestrator Updater { get { return _updater; } }
        public KeyInsight.Update.Routing.RouteCatalog RouteCatalog { get { return _routeCatalog; } }
        public Services.DeviceCenter.DeviceCenterService DeviceCenter { get { return _deviceCenter; } }

        public void SetCaptureEnabled(bool on)
        {
            _cfg.CaptureEnabled = on;
            _cfg.Save();
            RefreshSnapshot();
            Invalidate();
        }

        public void SetDiagnosticsEnabled(bool on)
        {
            _cfg.DiagnosticsEnabled = on;
            _cfg.Save();
            StatsEngine engine = _source as StatsEngine;
            if (engine != null) engine.DiagnosticsEnabled = on;
            Invalidate();
        }

        public void SetFloatingSummary(bool on)
        {
            _cfg.FloatingSummary = on;
            _cfg.Save();
            Invalidate();
        }
        public PolicySnapshot PolicyStatus { get { return PolicyController.Current; } }
        public RawInputTracker RawInput { get { return _rawInput; } }
        public long MouseRawWheelDelta { get { return _mouseHook == null ? 0 : _mouseHook.RawWheelDelta; } }
        public long MouseCycleCandidates { get { return _mouseHook == null ? 0 : _mouseHook.CycleCandidates; } }
        public string MaintenanceDir
        {
            get { return System.IO.Path.Combine(_source.DataDirectory, "maintenance"); }
        }

        public void SetPolicyMode(int mode)
        {
            _cfg.PolicyMode = mode < 0 ? 0 : (mode > 2 ? 2 : mode);
            _cfg.Save();
            PolicyController.Refresh(_cfg);
            RefreshSnapshot();
            Invalidate();
        }

        public void RefreshPolicy()
        {
            if (_keyPolicies != null) _keyPolicies.Save();
            PolicyController.Refresh(_cfg);
        }

        public bool AutoStart
        {
            get { return AutoStartHelper.IsEnabled(); }
            set { AutoStartHelper.Set(value); }
        }

        public bool AutoCheckUpdates
        {
            get { return _cfg.AutoCheckUpdates; }
            set { _cfg.AutoCheckUpdates = value; _cfg.Save(); }
        }

        public bool UpdateCheckBusy { get; private set; }
        public string LastUpdateCheckResult { get; private set; }

        /// <summary>托盘回调：更新检查状态变化（busy/结果文本），转发给设置页重绘。</summary>
        public void OnUpdateStateChanged(bool busy, string result)
        {
            UpdateCheckBusy = busy;
            LastUpdateCheckResult = result;
            for (int i = 0; i < _pages.Length; i++)
            {
                _pages[i].Invalidate();
            }
        }

        public void CheckUpdates()
        {
            if (_checkUpdates != null) _checkUpdates();
        }

        public void SetEnabled(bool on)
        {
            _cfg.FilterEnabled = on;
            _cfg.Save();
            Core.Policy.PolicyController.Refresh(_cfg);
            RefreshSnapshot();
            Invalidate();
        }

        public void SetThreshold(int ms)
        {
            _cfg.ThresholdMs = ms;
            _cfg.Save();
            Core.Policy.PolicyController.Refresh(_cfg);
            RefreshSnapshot();
            Invalidate();
        }

        public void SetSkipInjected(bool on)
        {
            _cfg.SkipInjected = on;
            _cfg.Save();
            Core.Policy.PolicyController.Refresh(_cfg);
            Invalidate();
        }

        public void SetThemeMode(ThemeMode mode)
        {
            Theme.Mode = mode;
            _cfg.ThemeMode = (int)mode;
            _cfg.Save();
            Theme.Refresh();
            ApplyTheme();
        }

        public void ReinstallHook()
        {
            if (_hook != null) _hook.Restart();
            if (_mouseHook != null) _mouseHook.Restart();
            RefreshSnapshot();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            // 页面为自绘 Panel，不获取焦点；滚轮统一由窗体转发给当前页
            if (_current >= 0 && _current < _pages.Length)
            {
                _pages[_current].ScrollByWheel(e.Delta);
            }
            base.OnMouseWheel(e);
        }

        /// <summary>页面切换/范围变化立即请求（绕过节流）；Timer 使用节流请求。</summary>
        public void RefreshSnapshot()
        {
            RequestSnapshot(true);
        }

        private void RequestSnapshot(bool immediate)
        {
            if (_current < 0 || _current >= _pages.Length) return;
            PageBase page = _pages[_current];

            if (_asyncEngine == null || ScreenshotMode)
            {
                // 截图/测试同步路径（保证 DrawToBitmap 前内容已就绪）
                StatsSnapshot snap = _source.GetSnapshot(page.RangeStart, page.RangeEnd);
                page.SetSnapshot(snap);
                return;
            }

            DateTime now = DateTime.UtcNow;
            long version = _asyncEngine.DataVersion;
            if (!immediate)
            {
                // 节流：1s 内不重复；数据版本未变化则不重算
                if ((now - _lastSnapshotRequestUtc).TotalMilliseconds < 900) return;
                if (version == _lastAppliedDataVersion && _pendingSnapshotGen != 0) return;
            }
            _lastSnapshotRequestUtc = now;
            _pendingSnapshotPage = _current;
            _pendingSnapshotGen = _asyncEngine.RequestSnapshot(page.RangeStart, page.RangeEnd);
        }

        private void OnSnapshotBuilt(StatsSnapshot snap, long generation)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((Action)delegate
                {
                    if (IsDisposed) return;
                    if (generation != _pendingSnapshotGen) { UiMetrics.CountSnapshotStale(); return; }
                    int pageIndex = _pendingSnapshotPage;
                    if (pageIndex < 0 || pageIndex >= _pages.Length) return;
                    _pages[pageIndex].SetSnapshot(snap);
                    _lastAppliedDataVersion = _asyncEngine == null ? -1 : _asyncEngine.DataVersion;
                });
            }
            catch
            {
            }
        }

        public void RefreshSnapshotSyncLegacy()
        {
            if (_pages == null) return;
            PageBase page = _pages[_current];
            StatsSnapshot snapshot = _source.GetSnapshot(page.RangeStart, page.RangeEnd);
            page.SetSnapshot(snapshot);
            Invalidate();
        }

        /// <summary>设置当前页滚动位置（截图回归/测试用）。</summary>
        public void SetPageScroll(int offset)
        {
            if (_current >= 0 && _current < _pages.Length) _pages[_current].SetScrollOffset(offset);
            Invalidate();
        }

        public void SelectPage(int index)
        {
            if (index < 0 || index >= _pages.Length) return;
            _current = index;
            for (int g = 0; g < _groupPages.Length; g++)
            {
                for (int k = 0; k < _groupPages[g].Length; k++)
                {
                    if (_groupPages[g][k] == index)
                    {
                        _currentGroup = g;
                        _groupActive[g] = index;
                    }
                }
            }
            for (int i = 0; i < _pages.Length; i++) _pages[i].Visible = i == index;
            LayoutPages();
            RefreshSnapshot();
            Invalidate();
        }

        private void SelectGroup(int group)
        {
            if (group < 0 || group >= _groupPages.Length) return;
            SelectPage(_groupActive[group]);
        }

        private bool TabsVisible
        {
            get { return _groupPages[_currentGroup].Length > 1; }
        }

        /// <summary>Screenshot/debug hook: access a page instance without switching UI.</summary>
        internal PageBase FindPageForPreview(int index)
        {
            if (_pages == null || index < 0 || index >= _pages.Length) return null;
            return _pages[index];
        }

        public void PreviewKeyboardHover(string label)
        {
            KeyboardPage page = _pages[1] as KeyboardPage;
            if (page == null) return;
            SelectPage(1);
            page.PreviewHover(label);
            Application.DoEvents();
        }

        /// <summary>截图回归用：把 Logo 固定在下落中的某一帧（不启动动画）。</summary>
        internal void PreviewLogoFall(float y, float rot)
        {
            if (_logo == null) return;
            _logoAnim.Stop();
            _logoState = 1;
            _logoX = LogoBaseX + 34f;
            _logoY = y;
            _logoRot = rot;
            _logoVx = 0f;
            _logoVy = 0f;
            _logoSpin = 0f;
            InvalidateLogo();
            Update();
        }

        internal void PreviewLogoReset()
        {
            ResetLogoAnim();
            InvalidateLogo();
            Update();
        }

        public void ApplyTheme()
        {
            BackColor = Theme.P.Bg;
            if (_pages != null)
            {
                for (int i = 0; i < _pages.Length; i++) _pages[i].ApplyTheme();
            }
            Invalidate(true);
        }

        public void ForceClose()
        {
            _exiting = true;
            Close();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (_timer == null) return;
            if (Visible)
            {
                Theme.Refresh();
                ApplyTheme();
                _timer.Start();
                RefreshSnapshot();
            }
            else
            {
                _timer.Stop();
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            try { MaximizedBounds = Screen.FromControl(this).WorkingArea; }
            catch { }
        }

        private void ToggleMaximize()
        {
            if (WindowState == FormWindowState.Maximized)
            {
                WindowState = FormWindowState.Normal;
            }
            else
            {
                try { MaximizedBounds = Screen.FromControl(this).WorkingArea; }
                catch { }
                WindowState = FormWindowState.Maximized;
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutPages();
        }

        private int DesignW { get { return (int)Math.Round(ClientSize.Width / Ui.Scale); } }
        private int DesignH { get { return (int)Math.Round(ClientSize.Height / Ui.Scale); } }

        private void LayoutPages()
        {
            if (_pages == null) return;
            int tabsH = TabsVisible ? 36 : 0;
            Rectangle content = new Rectangle(
                Ui.Px(SidebarWidth + ContentPad),
                Ui.Px(TitleHeight + 18 + tabsH),
                ClientSize.Width - Ui.Px(SidebarWidth + ContentPad * 2 + ScrollStrip),
                ClientSize.Height - Ui.Px(TitleHeight + 18 + 18 + tabsH));
            for (int i = 0; i < _pages.Length; i++) _pages[i].Bounds = content;

            // 命中区域用设计坐标（输入坐标会先换算到设计空间）
            _minRect = new Rectangle(DesignW - 140, 13, 40, 32);
            _maxRect = new Rectangle(DesignW - 96, 13, 40, 32);
            _closeRect = new Rectangle(DesignW - 52, 13, 40, 32);
            _statusRect = new Rectangle(DesignW - 148 - 168, 15, 168, 28);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.P.Bg);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Ui.Prep(e.Graphics);
            Palette p = Theme.P;
            Graphics g = e.Graphics;

            g.Clear(p.Bg);

            GraphicsState shellState = g.Save();
            if (Ui.Scale != 1f) g.ScaleTransform(Ui.Scale, Ui.Scale);
            int designW = DesignW;
            int designH = DesignH;

            Rectangle title = new Rectangle(0, 0, designW, TitleHeight);
            using (SolidBrush b = new SolidBrush(p.TitleBar)) g.FillRectangle(b, title);
            using (Pen pen = new Pen(p.Border, 1f)) g.DrawLine(pen, 0, TitleHeight - 1, designW, TitleHeight - 1);

            Rectangle side = new Rectangle(0, TitleHeight, SidebarWidth, designH - TitleHeight);
            using (SolidBrush b = new SolidBrush(p.Sidebar)) g.FillRectangle(b, side);
            using (Pen pen = new Pen(p.Border, 1f)) g.DrawLine(pen, SidebarWidth - 1, TitleHeight, SidebarWidth - 1, designH);

            Ui.TextLeft(g, "KeyInsight", Ui.BrandTitle, p.Text,
                new Rectangle(58, LogoBaseY, 220, LogoSize));

            DrawStatus(g, p);
            DrawGroupTabs(g, p);
            DrawPageScrollBar(g, p);
            DrawWindowButtons(g, p);
            DrawNav(g, p);
            // Logo 最后画：掉落时会在侧栏导航之上，不会被导航高亮盖住
            DrawLogo(g);

            Ui.TextLeft(g, "v" + AppInfo.Version, Ui.Label, p.Dim, new Rectangle(18, designH - 22, 80, 18));
            g.Restore(shellState);

            // 边框与缩放手柄按物理像素绘制，避免缩放后出现半像素缝隙
            using (Pen pen = new Pen(p.BorderStrong, 1f))
            {
                g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            }

            if (WindowState == FormWindowState.Normal)
            {
                using (Pen grip = new Pen(Ui.WithAlpha(p.Dim, 130), 1f))
                {
                    for (int i = 0; i < 3; i++)
                    {
                        int off = Ui.Px(7 + i * 4);
                        g.DrawLine(grip, ClientSize.Width - off, ClientSize.Height - Ui.Px(4),
                            ClientSize.Width - Ui.Px(4), ClientSize.Height - off);
                    }
                }
            }
        }

        private void DrawStatus(Graphics g, Palette p)
        {
            Color dot = _cfg.FilterEnabled ? p.Good : p.Warn;
            Color bg = _hoverStatus ? p.Selected : p.CardAlt;
            Ui.FillRound(g, _statusRect, _statusRect.Height / 2f, bg);
            Ui.StrokeRound(g, _statusRect, _statusRect.Height / 2f, p.Border);
            Ui.DrawDot(g, _statusRect.X + 16, _statusRect.Y + _statusRect.Height / 2, 4, dot);
            string text = _cfg.FilterEnabled ? "防抖过滤 开 · " + _cfg.ThresholdMs + " ms" : "防抖过滤 关（仍统计）";
            if (TelemetryDropped > 0) text += " · 统计不完整";
            Ui.TextLeft(g, text, Ui.BodySmall, _cfg.FilterEnabled ? p.Text : p.Warn,
                new Rectangle(_statusRect.X + 26, _statusRect.Y, _statusRect.Width - 32, _statusRect.Height));
        }

        private void DrawPageScrollBar(Graphics g, Palette p)
        {
            PageBase page = (_current >= 0 && _current < _pages.Length) ? _pages[_current] : null;
            if (page == null) { _scrollTrack = Rectangle.Empty; _scrollThumb = Rectangle.Empty; return; }

            Rectangle content = Ui.ToDesign(page.Bounds);
            int max = page.ScrollMax;
            if (max <= 0)
            {
                _scrollTrack = Rectangle.Empty;
                _scrollThumb = Rectangle.Empty;
                return;
            }

            _scrollTrack = new Rectangle(content.Right + 4, content.Y + 6, 6, content.Height - 12);
            int trackH = _scrollTrack.Height;
            int viewport = Math.Max(1, (int)Math.Round(page.ClientSize.Height / Ui.Scale));
            int contentH = viewport + max;
            int thumbH = Math.Max(42, (int)(trackH * (viewport / (double)contentH)));
            int scrollRange = Math.Max(1, trackH - thumbH);
            int thumbY = _scrollTrack.Y + (int)Math.Round(scrollRange * (page.ScrollOffset / (double)max));
            _scrollThumb = new Rectangle(_scrollTrack.X, thumbY, _scrollTrack.Width, thumbH);

            Ui.FillRound(g, new RectangleF(_scrollTrack.X, _scrollTrack.Y, _scrollTrack.Width, _scrollTrack.Height),
                3f, Ui.WithAlpha(p.Border, 90));
            Color thumbColor = _scrollDragging || _scrollHover
                ? p.Accent1 : Ui.WithAlpha(p.Muted, 170);
            Ui.FillRound(g, new RectangleF(_scrollThumb.X, _scrollThumb.Y, _scrollThumb.Width, _scrollThumb.Height),
                3f, thumbColor);
        }

        private bool IsScrollBarVisible()
        {
            PageBase page = (_current >= 0 && _current < _pages.Length) ? _pages[_current] : null;
            return page != null && page.ScrollMax > 0 && _scrollTrack.Height > 0;
        }

        private Rectangle ScrollProbeTrack()
        {
            return new Rectangle(_scrollTrack.X - 3, _scrollTrack.Y, _scrollTrack.Width + 6, _scrollTrack.Height);
        }

        /// <summary>滚动条点击：拇指内开始拖动；轨道上点击跳转。命中返回 true（吞掉该次点击）。</summary>
        private bool ScrollBarHit(Point pt)
        {
            if (!IsScrollBarVisible()) return false;
            if (!ScrollProbeTrack().Contains(pt)) return false;
            if (_scrollThumb.Contains(pt))
            {
                _scrollDragging = true;
                _scrollGrabOffset = pt.Y - _scrollThumb.Y;
            }
            else
            {
                _scrollGrabOffset = _scrollThumb.Height / 2;
                ScrollBarDragTo(pt.Y);
                _scrollDragging = true;
            }
            Invalidate();
            return true;
        }

        /// <summary>拖动更新；悬停重绘。返回 true 表示鼠标事件已被滚动条消费。</summary>
        private bool UpdateScrollBarDrag(Point pt, bool leftDown)
        {
            if (_scrollDragging)
            {
                if (leftDown) { ScrollBarDragTo(pt.Y); return true; }
                _scrollDragging = false;
                Invalidate();
                return false;
            }
            bool over = IsScrollBarVisible() && ScrollProbeTrack().Contains(pt);
            if (over != _scrollHover)
            {
                _scrollHover = over;
                Invalidate();
            }
            return false;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_scrollDragging)
            {
                _scrollDragging = false;
                Invalidate();
            }
        }

        private void ScrollBarDragTo(int mouseY)
        {
            PageBase page = _pages[_current];
            int max = page.ScrollMax;
            if (max <= 0) return;
            int trackH = _scrollTrack.Height;
            int thumbH = _scrollThumb.Height;
            int scrollRange = Math.Max(1, trackH - thumbH);
            int y = mouseY - _scrollGrabOffset - _scrollTrack.Y;
            if (y < 0) y = 0;
            if (y > scrollRange) y = scrollRange;
            page.SetScrollOffset((int)Math.Round(max * (y / (double)scrollRange)));
        }

        private void DrawWindowButtons(Graphics g, Palette p)
        {
            // 新配色下 p.Hover 与标题栏底色过近，窗口按钮悬停改用可见的低饱和强调色
            Color winHover = Ui.WithAlpha(p.Accent1, p.Dark ? 70 : 90);
            if (_hoverMin) Ui.FillRound(g, _minRect, 6, winHover);
            if (_hoverMax) Ui.FillRound(g, _maxRect, 6, winHover);
            if (_hoverClose) Ui.FillRound(g, _closeRect, 6, p.Bad);

            // 三个图标独立取色：悬停关闭只允许关闭图标变白，
            // 否则浅色主题下最小化/最大化图标也变成白色，在浅色标题栏上“消失”
            using (Pen pen = new Pen(_hoverMin ? p.Text : p.Muted, 1.5f))
            {
                int cx = _minRect.X + _minRect.Width / 2;
                int cy = _minRect.Y + _minRect.Height / 2;
                g.DrawLine(pen, cx - 5, cy, cx + 5, cy);
            }
            using (Pen pen = new Pen(_hoverMax ? p.Text : p.Muted, 1.5f))
            {
                int cx = _maxRect.X + _maxRect.Width / 2;
                int cy = _maxRect.Y + _maxRect.Height / 2;
                g.DrawRectangle(pen, cx - 5, cy - 5, 10, 10);
            }
            using (Pen pen = new Pen(_hoverClose ? Color.White : p.Muted, 1.5f))
            {
                int cx = _closeRect.X + _closeRect.Width / 2;
                int cy = _closeRect.Y + _closeRect.Height / 2;
                g.DrawLine(pen, cx - 5, cy - 5, cx + 5, cy + 5);
                g.DrawLine(pen, cx + 5, cy - 5, cx - 5, cy + 5);
            }
        }

        private int HitTabs(Point pt)
        {
            for (int i = 0; i < _tabRects.Length; i++)
            {
                if (_tabRects[i].Contains(pt)) return i;
            }
            return -1;
        }

        private void DrawGroupTabs(Graphics g, Palette p)
        {
            int[] pages = _groupPages[_currentGroup];
            if (pages.Length <= 1)
            {
                _tabRects = new Rectangle[0];
                _tabPageIndices = new int[0];
                return;
            }
            _tabRects = new Rectangle[pages.Length];
            _tabPageIndices = new int[pages.Length];
            int x = SidebarWidth + ContentPad;
            int y = TitleHeight + 12;
            for (int i = 0; i < pages.Length; i++)
            {
                PageBase page = _pages[pages[i]];
                int w = Ui.MeasureWidth(g, page.Title, Ui.BodySmall) + 28;
                Rectangle r = new Rectangle(x, y, w, 24);
                bool selected = pages[i] == _current;
                Charts.Chip(g, r, page.Title, selected, p, Ui.BodySmall);
                _tabRects[i] = r;
                _tabPageIndices[i] = pages[i];
                x += w + 8;
            }
        }

        private void DrawNav(Graphics g, Palette p)
        {
            int navY = TitleHeight + 22;
            int navH = 46;
            int gap = 4;
            for (int i = 0; i < _navLabels.Length; i++)
            {
                Rectangle r = new Rectangle(12, navY + i * (navH + gap), SidebarWidth - 24, navH);
                _navRects[i] = r;
                bool active = i == _currentGroup;
                if (active)
                {
                    Ui.FillRound(g, r, 10, p.Selected);
                    Ui.FillRound(g, new RectangleF(r.X, r.Y + 11, 3, r.Height - 22), 1.5f, p.Accent1);
                }
                else if (i == _hoverNav)
                {
                    Ui.FillRound(g, r, 10, p.Hover);
                }

                Color fg = active ? p.Text : p.Muted;
                Ui.TextLeft(g, _navGlyphs[i], Ui.Glyph, active ? p.Accent1 : p.Muted,
                    new Rectangle(r.X + 16, r.Y, 24, r.Height));
                Ui.TextLeft(g, _navLabels[i], active ? Ui.NavBold : Ui.Nav, fg,
                    new Rectangle(r.X + 46, r.Y, r.Width - 54, r.Height));
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Point pt = Ui.ToDesign(e.Location);
            if (UpdateScrollBarDrag(pt, (e.Button & MouseButtons.Left) != 0)) return;
            int nav = -1;
            for (int i = 0; i < _navRects.Length; i++) if (_navRects[i].Contains(pt)) { nav = i; break; }
            bool min = _minRect.Contains(pt);
            bool max = _maxRect.Contains(pt);
            bool close = _closeRect.Contains(pt);
            bool status = _statusRect.Contains(pt);
            if (nav != _hoverNav || min != _hoverMin || max != _hoverMax ||
                close != _hoverClose || status != _hoverStatus)
            {
                _hoverNav = nav;
                _hoverMin = min;
                _hoverMax = max;
                _hoverClose = close;
                _hoverStatus = status;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hoverNav = -1;
            _hoverMin = false;
            _hoverMax = false;
            _hoverClose = false;
            _hoverStatus = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Point pt = Ui.ToDesign(e.Location);

            // Logo 区域先吃掉点击：连点 5 次触发掉落彩蛋，同时避免被当成标题栏拖动
            if (new Rectangle(LogoBaseX - 4, LogoBaseY - 4, LogoSize + 8, LogoSize + 8).Contains(pt))
            {
                RegisterLogoTap();
                return;
            }

            if (ScrollBarHit(pt)) return;

            if (_closeRect.Contains(pt))
            {
                Hide();
                return;
            }
            if (_maxRect.Contains(pt))
            {
                ToggleMaximize();
                return;
            }
            if (_minRect.Contains(pt))
            {
                WindowState = FormWindowState.Minimized;
                return;
            }
            if (_statusRect.Contains(pt))
            {
                SelectPage(6);
                return;
            }

            if (e.Clicks == 2 && pt.Y < TitleHeight)
            {
                ToggleMaximize();
                return;
            }
            for (int i = 0; i < _navRects.Length; i++)
            {
                if (_navRects[i].Contains(pt))
                {
                    SelectGroup(i);
                    return;
                }
            }
            int tab = HitTabs(pt);
            if (tab >= 0)
            {
                SelectPage(_tabPageIndices[tab]);
                return;
            }

            if (pt.Y < TitleHeight)
            {
                ReleaseCapture();
                SendMessage(Handle, 0x00A1, (IntPtr)2, IntPtr.Zero);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0084 && WindowState == FormWindowState.Normal)
            {
                long lp = m.LParam.ToInt64();
                int sx = (short)(lp & 0xFFFF);
                int sy = (short)((lp >> 16) & 0xFFFF);
                Point pt = PointToClient(new Point(sx, sy));
                int edge = Ui.Px(6);
                bool left = pt.X <= edge;
                bool right = pt.X >= ClientSize.Width - edge;
                bool top = pt.Y <= edge;
                bool bottom = pt.Y >= ClientSize.Height - edge;
                int code = 0;
                if (top && left) code = 13;
                else if (top && right) code = 14;
                else if (bottom && left) code = 16;
                else if (bottom && right) code = 17;
                else if (left) code = 10;
                else if (right) code = 11;
                else if (top) code = 12;
                else if (bottom) code = 15;
                if (code != 0)
                {
                    m.Result = (IntPtr)code;
                    return;
                }
            }

            if (m.Msg == 0x001A)
            {
                Theme.Refresh();
                ApplyTheme();
            }
            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_exiting && !ScreenshotMode)
            {
                e.Cancel = true;
                ResetLogoAnim();
                Hide();
                return;
            }
            _timer.Stop();
            ResetLogoAnim();
            Theme.Changed -= ApplyTheme;
            base.OnFormClosing(e);
        }

        private int LogoFloorY
        {
            get { return Math.Max(LogoBaseY, DesignH - LogoSize - 34); }
        }

        private void DrawLogo(Graphics g)
        {
            if (_logo == null) return;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            if (_logoState == 0)
            {
                g.DrawImage(_logo, new Rectangle(LogoBaseX, LogoBaseY, LogoSize, LogoSize));
                return;
            }

            // 地面投影：离地越高越淡越小，落地时收拢
            float gap = Math.Max(0f, LogoFloorY - _logoY);
            int shadowAlpha = (int)Math.Max(0f, 64f - gap * 0.16f);
            if (shadowAlpha > 0)
            {
                int sw = (int)Math.Max(12f, LogoSize - gap * 0.03f);
                float cx = _logoX + LogoSize / 2f;
                RectangleF sh = new RectangleF(cx - sw / 2f, LogoFloorY + LogoSize - 3, sw, 5);
                using (SolidBrush b = new SolidBrush(Ui.WithAlpha(Color.Black, shadowAlpha)))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.FillEllipse(b, sh);
                }
            }

            GraphicsState state = g.Save();
            g.TranslateTransform(_logoX + LogoSize / 2f, _logoY + LogoSize / 2f);
            if (Math.Abs(_logoRot) > 0.01f) g.RotateTransform(_logoRot);
            g.DrawImage(_logo, new RectangleF(-LogoSize / 2f, -LogoSize / 2f, LogoSize, LogoSize));
            g.Restore(state);
        }

        private void InvalidateLogo()
        {
            // 只在标题栏 + 侧栏范围内重绘：Logo 的活动范围被限制在这一列
            Invalidate(Ui.PxRect(new Rectangle(0, 0, SidebarWidth + 2, DesignH)));
        }

        private void ResetLogoAnim()
        {
            if (_logoAnim != null) _logoAnim.Stop();
            _logoState = 0;
            _logoX = LogoBaseX;
            _logoY = LogoBaseY;
            _logoRot = 0f;
            _logoTaps = 0;
        }

        private void RegisterLogoTap()
        {
            if (_logoState != 0) return;             // 动画进行中不重复触发
            DateTime now = DateTime.UtcNow;
            if ((now - _logoLastTapUtc).TotalMilliseconds > 900.0) _logoTaps = 0;
            _logoLastTapUtc = now;
            _logoTaps++;
            if (_logoTaps < 5) return;
            _logoTaps = 0;
            StartLogoDrop(now);
        }

        private void StartLogoDrop(DateTime now)
        {
            if (_logo == null) return;
            _logoState = 1;
            _logoX = LogoBaseX;
            _logoY = LogoBaseY;
            _logoVx = (float)(_rng.NextDouble() * 60.0 - 10.0);
            _logoVy = 40f;
            _logoRot = 0f;
            _logoSpin = (float)(_rng.NextDouble() * 200.0 + 220.0) * (_rng.Next(2) == 0 ? -1f : 1f);
            _logoAnimLastUtc = now;
            _logoAnim.Start();
            InvalidateLogo();
        }

        private void LogoAnimTick(object sender, EventArgs e)
        {
            DateTime now = DateTime.UtcNow;
            float dt = (float)(now - _logoAnimLastUtc).TotalSeconds;
            _logoAnimLastUtc = now;
            if (dt < 0f) dt = 0f;
            if (dt > 0.05f) dt = 0.05f;             // 掉帧时限步，避免穿透地面

            int floor = LogoFloorY;

            if (_logoState == 1)
            {
                _logoVy += 2400f * dt;               // 重力
                _logoX += _logoVx * dt;
                _logoY += _logoVy * dt;
                _logoRot += _logoSpin * dt;

                float minX = 8f;
                float maxX = Math.Max(minX, SidebarWidth - LogoSize - 8f);
                if (_logoX < minX)
                {
                    _logoX = minX;
                    _logoVx = -_logoVx * 0.55f;
                    _logoSpin *= 0.8f;
                }
                else if (_logoX > maxX)
                {
                    _logoX = maxX;
                    _logoVx = -_logoVx * 0.55f;
                    _logoSpin *= 0.8f;
                }

                if (_logoY >= floor)
                {
                    _logoY = floor;
                    if (_logoVy > 90f)
                    {
                        _logoVy = -_logoVy * 0.42f;  // 回弹
                        _logoVx *= 0.86f;
                        _logoSpin *= 0.7f;
                    }
                    else
                    {
                        _logoVy = 0f;
                        _logoVx = 0f;
                        _logoState = 2;
                        _logoRestUntilUtc = now.AddMilliseconds(2600);
                    }
                }
            }
            else if (_logoState == 2)
            {
                if (_logoY > floor) _logoY = floor;  // 窗口改变大小时贴地
                float target = (float)(Math.Round(_logoRot / 90.0) * 90.0);
                _logoRot += (target - _logoRot) * Math.Min(1f, dt * 14f);
                if (Math.Abs(target - _logoRot) < 0.4f) _logoRot = target;
                if (now >= _logoRestUntilUtc)
                {
                    _logoState = 3;
                    _logoReturnStartUtc = now;
                    _logoFromX = _logoX;
                    _logoFromY = _logoY;
                    _logoFromRot = _logoRot;
                }
            }
            else if (_logoState == 3)
            {
                float t = (float)((now - _logoReturnStartUtc).TotalMilliseconds / 560.0);
                if (t >= 1f)
                {
                    _logoState = 0;
                    _logoX = LogoBaseX;
                    _logoY = LogoBaseY;
                    _logoRot = 0f;
                    _logoAnim.Stop();
                    InvalidateLogo();
                    return;
                }
                if (t < 0f) t = 0f;
                float ease = 1f - (float)Math.Pow(1.0 - t, 3.0);
                _logoX = _logoFromX + (LogoBaseX - _logoFromX) * ease;
                _logoY = _logoFromY + (LogoBaseY - _logoFromY) * ease;
                _logoRot = _logoFromRot * (1f - ease);
            }

            InvalidateLogo();
        }

        private static Image LoadLogo()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (System.IO.Stream s = asm.GetManifestResourceStream("app256.png"))
                {
                    if (s == null) return null;
                    using (Image img = Image.FromStream(s))
                    {
                        return new Bitmap(img);
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        private static Icon LoadAppIcon()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (System.IO.Stream s = asm.GetManifestResourceStream("app.ico"))
                {
                    if (s == null) return SystemIcons.Application;
                    return new Icon(s);
                }
            }
            catch
            {
                return SystemIcons.Application;
            }
        }
    }
}
