using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
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

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private readonly IStatsSource _source;
        private readonly Config _cfg;
        private readonly DebounceHook _hook;
        private readonly MouseHook _mouseHook;
        private readonly Action _checkUpdates;
        private readonly PageBase[] _pages;
        private readonly string[] _navLabels = new string[] { "总览", "键盘热力图", "鼠标统计", "应用统计", "时间分析", "按键健康", "防抖统计", "设置与数据" };
        private readonly string[] _navGlyphs = new string[] { "\uE80F", "\uE765", "\uE962", "\uE71D", "\uE823", "\uE95E", "\uE7BA", "\uE713" };
        private readonly Rectangle[] _navRects = new Rectangle[8];
        private int _current;
        private int _hoverNav = -1;
        private bool _hoverMin;
        private bool _hoverMax;
        private bool _hoverClose;
        private bool _hoverStatus;
        private Rectangle _statusRect;
        private Rectangle _minRect;
        private Rectangle _maxRect;
        private Rectangle _closeRect;
        private readonly Timer _timer;
        private readonly Image _logo;
        private bool _exiting;

        public bool ScreenshotMode;

        public DashboardForm(IStatsSource source, Config cfg, DebounceHook hook, MouseHook mouseHook, Action checkUpdates)
        {
            _source = source;
            _cfg = cfg;
            _hook = hook;
            _mouseHook = mouseHook;
            _checkUpdates = checkUpdates;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1200, 780);
            MinimumSize = new Size(1000, 680);
            BackColor = Theme.P.Bg;
            DoubleBuffered = true;
            Text = "KeyInsight";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            _logo = LoadLogo();
            Icon = LoadAppIcon();

            _pages = new PageBase[]
            {
                new OverviewPage(),
                new KeyboardPage(this),
                new MousePage(),
                new AppsPage(),
                new TimePage(),
                new HealthPage(),
                new DebouncePage(this),
                new SettingsPage(this)
            };

            for (int i = 0; i < _pages.Length; i++)
            {
                PageBase page = _pages[i];
                page.Visible = i == 0;
                page.DataRequested += delegate { RefreshSnapshot(); };
                Controls.Add(page);
            }

            LayoutPages();

            _timer = new Timer();
            _timer.Interval = 1000;
            _timer.Tick += delegate { RefreshSnapshot(); };
            _timer.Start();

            Theme.Changed += ApplyTheme;
            RefreshSnapshot();
        }

        public Config Cfg { get { return _cfg; } }
        public IStatsSource Source { get { return _source; } }

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

        public void CheckUpdates()
        {
            if (_checkUpdates != null) _checkUpdates();
        }

        public void SetEnabled(bool on)
        {
            _cfg.Enabled = on;
            _cfg.Save();
            RefreshSnapshot();
            Invalidate();
        }

        public void SetThreshold(int ms)
        {
            _cfg.ThresholdMs = ms;
            _cfg.Save();
            RefreshSnapshot();
            Invalidate();
        }

        public void SetSkipInjected(bool on)
        {
            _cfg.SkipInjected = on;
            _cfg.Save();
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

        public void RefreshSnapshot()
        {
            if (_pages == null) return;
            PageBase page = _pages[_current];
            StatsSnapshot snapshot = _source.GetSnapshot(page.RangeStart, page.RangeEnd);
            page.SetSnapshot(snapshot);
            Invalidate(new Rectangle(0, 0, Width, TitleHeight));
        }

        public void SelectPage(int index)
        {
            if (index < 0 || index >= _pages.Length) return;
            _current = index;
            for (int i = 0; i < _pages.Length; i++) _pages[i].Visible = i == index;
            RefreshSnapshot();
            Invalidate();
        }

        public void PreviewKeyboardHover(string label)
        {
            KeyboardPage page = _pages[1] as KeyboardPage;
            if (page == null) return;
            SelectPage(1);
            page.PreviewHover(label);
            Application.DoEvents();
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

        private void LayoutPages()
        {
            if (_pages == null) return;
            Rectangle content = new Rectangle(
                SidebarWidth + ContentPad,
                TitleHeight + 18,
                ClientSize.Width - SidebarWidth - ContentPad * 2,
                ClientSize.Height - TitleHeight - 18 - 18);
            for (int i = 0; i < _pages.Length; i++) _pages[i].Bounds = content;

            _minRect = new Rectangle(ClientSize.Width - 140, 13, 40, 32);
            _maxRect = new Rectangle(ClientSize.Width - 96, 13, 40, 32);
            _closeRect = new Rectangle(ClientSize.Width - 52, 13, 40, 32);
            _statusRect = new Rectangle(ClientSize.Width - 148 - 168, 15, 168, 28);
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

            Rectangle title = new Rectangle(0, 0, ClientSize.Width, TitleHeight);
            using (SolidBrush b = new SolidBrush(p.TitleBar)) g.FillRectangle(b, title);
            using (Pen pen = new Pen(p.Border, 1f)) g.DrawLine(pen, 0, TitleHeight - 1, ClientSize.Width, TitleHeight - 1);

            Rectangle side = new Rectangle(0, TitleHeight, SidebarWidth, ClientSize.Height - TitleHeight);
            using (SolidBrush b = new SolidBrush(p.Sidebar)) g.FillRectangle(b, side);
            using (Pen pen = new Pen(p.Border, 1f)) g.DrawLine(pen, SidebarWidth - 1, TitleHeight, SidebarWidth - 1, ClientSize.Height);

            if (_logo != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(_logo, new Rectangle(18, 14, 30, 30));
            }
            Ui.TextLeft(g, "KeyInsight", Ui.BrandTitle, p.Text,
                new Rectangle(58, 8, 220, 26));
            Ui.TextLeft(g, "键盘洞察 · 按键统计与防抖", Ui.Label, p.Muted, new Rectangle(58, 31, 260, 18));

            DrawStatus(g, p);
            DrawWindowButtons(g, p);
            DrawNav(g, p);

            Ui.TextLeft(g, "本地数据 · 不上传", Ui.Label, p.Dim, new Rectangle(18, ClientSize.Height - 44, 160, 18));
            Ui.TextLeft(g, "v" + AppInfo.Version, Ui.Label, p.Dim, new Rectangle(18, ClientSize.Height - 26, 80, 18));

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
                        int off = 7 + i * 4;
                        g.DrawLine(grip, ClientSize.Width - off, ClientSize.Height - 4,
                            ClientSize.Width - 4, ClientSize.Height - off);
                    }
                }
            }
        }

        private void DrawStatus(Graphics g, Palette p)
        {
            Color dot = _cfg.Enabled ? p.Good : p.Warn;
            Color bg = _hoverStatus ? p.Selected : p.CardAlt;
            Ui.FillRound(g, _statusRect, _statusRect.Height / 2f, bg);
            Ui.StrokeRound(g, _statusRect, _statusRect.Height / 2f, p.Border);
            Ui.DrawDot(g, _statusRect.X + 16, _statusRect.Y + _statusRect.Height / 2, 4, dot);
            string text = _cfg.Enabled ? "防抖 开 · " + _cfg.ThresholdMs + " ms" : "防抖 已暂停";
            Ui.TextLeft(g, text, Ui.BodySmall, _cfg.Enabled ? p.Text : p.Warn,
                new Rectangle(_statusRect.X + 26, _statusRect.Y, _statusRect.Width - 32, _statusRect.Height));
        }

        private void DrawWindowButtons(Graphics g, Palette p)
        {
            if (_hoverMin) Ui.FillRound(g, _minRect, 6, p.Hover);
            if (_hoverMax) Ui.FillRound(g, _maxRect, 6, p.Hover);
            if (_hoverClose) Ui.FillRound(g, _closeRect, 6, p.Bad);

            using (Pen pen = new Pen(_hoverClose ? Color.White : p.Muted, 1.5f))
            {
                int cx = _minRect.X + _minRect.Width / 2;
                int cy = _minRect.Y + _minRect.Height / 2;
                g.DrawLine(pen, cx - 5, cy, cx + 5, cy);

                cx = _maxRect.X + _maxRect.Width / 2;
                cy = _maxRect.Y + _maxRect.Height / 2;
                g.DrawRectangle(pen, cx - 5, cy - 5, 10, 10);

                cx = _closeRect.X + _closeRect.Width / 2;
                cy = _closeRect.Y + _closeRect.Height / 2;
                g.DrawLine(pen, cx - 5, cy - 5, cx + 5, cy + 5);
                g.DrawLine(pen, cx + 5, cy - 5, cx - 5, cy + 5);
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
                bool active = i == _current;
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
            int nav = -1;
            for (int i = 0; i < _navRects.Length; i++) if (_navRects[i].Contains(e.Location)) { nav = i; break; }
            bool min = _minRect.Contains(e.Location);
            bool max = _maxRect.Contains(e.Location);
            bool close = _closeRect.Contains(e.Location);
            bool status = _statusRect.Contains(e.Location);
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

            if (_closeRect.Contains(e.Location))
            {
                Hide();
                return;
            }
            if (_maxRect.Contains(e.Location))
            {
                ToggleMaximize();
                return;
            }
            if (_minRect.Contains(e.Location))
            {
                WindowState = FormWindowState.Minimized;
                return;
            }
            if (_statusRect.Contains(e.Location))
            {
                SelectPage(6);
                return;
            }

            if (e.Clicks == 2 && e.Y < TitleHeight)
            {
                ToggleMaximize();
                return;
            }
            for (int i = 0; i < _navRects.Length; i++)
            {
                if (_navRects[i].Contains(e.Location))
                {
                    SelectPage(i);
                    return;
                }
            }

            if (e.Y < TitleHeight)
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
                int edge = 6;
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
                Hide();
                return;
            }
            _timer.Stop();
            Theme.Changed -= ApplyTheme;
            base.OnFormClosing(e);
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
