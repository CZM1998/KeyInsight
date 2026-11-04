using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.Windows.Forms;
using KeyInsight.Stats;

namespace KeyInsight.UI.Pages
{
    public enum RangeKind
    {
        Today = 0,
        Week = 1,
        Month = 2,
        All = 3
    }

    public abstract class PageBase : Panel
    {
        protected StatsSnapshot Snap;
        protected RangeKind Range = RangeKind.Today;
        private Rectangle[] _rangeRects = new Rectangle[0];

        public event Action DataRequested;

        public abstract string Title { get; }
        public abstract string Subtitle { get; }

        public virtual DateTime RangeStart { get { return StartFor(Range); } }
        public virtual DateTime RangeEnd { get { return EndFor(Range); } }

        private int _scrollY;
        private readonly PageCanvas _canvas;
        // 静态层缓存：整页内容（不含 hover 叠加）渲染一次，交互时只做内存 DC 拷贝
        private readonly KeyInsight.UI.Rendering.LayerSurface _staticLayer =
            new KeyInsight.UI.Rendering.LayerSurface();
        private bool _staticLayerValid;

        protected PageBase()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.P.Bg;
            DoubleBuffered = true;
            TabStop = false;

            // 视口 + 子画布结构：内容画在高为 ContentH 的真实子控件上，滚动 = 移动子控件。
            // 不能用 Graphics.TranslateTransform 平移自绘内容：TextRenderer（GDI 文本）
            // 不跟随 GDI+ 变换，会导致滚动后文字留在原位、与图形错位。
            _canvas = new PageCanvas(this);
            _canvas.BackColor = Theme.P.Bg;
            Controls.Add(_canvas);
            SyncCanvas();
        }

        /// <summary>内容画布（真实子控件）：所有绘制与命中都在画布坐标（= 内容坐标）中。</summary>
        private sealed class PageCanvas : Panel
        {
            private readonly PageBase _owner;

            public PageCanvas(PageBase owner)
            {
                _owner = owner;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                    ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                DoubleBuffered = true;
                TabStop = false;
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                e.Graphics.Clear(_owner.ThemePageBackground);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                _owner.PaintPage(e.Graphics);
            }

            protected override void OnMouseDown(MouseEventArgs e) { _owner.DispatchMouseDown(e); }
            protected override void OnMouseMove(MouseEventArgs e) { _owner.DispatchMouseMove(e); }
            protected override void OnMouseUp(MouseEventArgs e) { _owner.DispatchMouseUp(e); }
            protected override void OnMouseLeave(EventArgs e) { _owner.DispatchMouseLeave(e); }
            protected override void OnMouseWheel(MouseEventArgs e) { _owner.ScrollByWheel(e.Delta); }
        }

        private Color ThemePageBackground { get { return Theme.P.Bg; } }

        internal void PaintPage(Graphics g)
        {

            long t0 = Stopwatch.GetTimestamp();
            Ui.Prep(g);
            if (Snap != null)
            {
                if (EnsureLayer())
                {
                    // 静态内容已缓存：整块 BitBlt 拷贝 + 叠加层（hover/tooltip）
                    _staticLayer.BlitTo(g, _canvas.ClientSize.Width, _canvas.ClientSize.Height);
                    DrawOverlay(g);
                }
                else
                {
                    Draw(g);
                }
            }
            UiMetrics.RecordPaint(GetType().Name.Replace("Page", ""),
                (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);

        }

        /// <summary>静态层：数据/主题/尺寸变化时整页重绘一次，其余时候只做整块拷贝。</summary>
        private bool EnsureLayer()
        {
            Size want = _canvas.ClientSize;
            if (want.Width <= 0 || want.Height <= 0) return false;
            if (!_staticLayer.Ensure(want.Width, want.Height)) return false;
            if (_staticLayerValid) return true;

            try
            {
                using (Graphics lg = _staticLayer.BeginRender())
                {
                    if (lg == null) return false;
                    lg.Clear(Theme.P.Bg);
                    Ui.Prep(lg);
                    Draw(lg);
                }
                _staticLayerValid = true;
                return true;
            }
            catch
            {
                _staticLayerValid = false;
                return false;
            }
        }

        /// <summary>静态层之上的叠加绘制（hover 高亮、tooltip）。默认无。</summary>
        protected virtual void DrawOverlay(Graphics g)
        {
        }

        private void DropLayer()
        {
            _staticLayerValid = false;
            _staticLayer.Dispose();
        }

        /// <summary>诊断/压测用：立即处理挂起的画布重绘（离屏窗口收不到 WM_PAINT 时也能计到真实绘制）。</summary>
        internal void FlushPaint()
        {
            _canvas.Update();
        }

        internal void DispatchMouseDown(MouseEventArgs e) { OnMouseDown(e); }
        internal void DispatchMouseMove(MouseEventArgs e) { OnMouseMove(e); }
        internal void DispatchMouseUp(MouseEventArgs e) { OnMouseUp(e); }
        internal void DispatchMouseLeave(EventArgs e) { OnMouseLeave(e); }

        /// <summary>页面状态变化统一重绘画布（视口本身无内容）；静态层随之失效。</summary>
        public new void Invalidate()
        {
            _staticLayerValid = false;
            _canvas.Invalidate();
        }

        public new void Invalidate(Rectangle rc)
        {
            _canvas.Invalidate(rc);
        }

        /// <summary>仅重绘叠加层（hover/tooltip 等），复用已渲染的静态层。</summary>
        public void InvalidateOverlay()
        {
            _canvas.Invalidate();
        }

        /// <summary>同步画布尺寸与滚动位置（视口大小、内容高度、滚动值变化时调用）。</summary>
        private void SyncCanvas()
        {
            int w = Math.Max(10, ClientSize.Width);
            int h = Math.Max(ClientSize.Height, MinContentHeight);
            _canvas.Bounds = new Rectangle(0, -_scrollY, w, h);
        }

        /// <summary>滚动位置变化（壳层据此重绘右侧自绘滚动条）。</summary>
        public event Action ScrollChanged;

        /// <summary>页面设计最小高度 = 默认窗口(1200×780)内容区高度；
        /// 默认窗口下不出滚动条，窗口更小时内容按此高度布局并由壳层绘制滚动条。</summary>
        protected virtual int MinContentHeight
        {
            get { return 686; }
        }

        /// <summary>布局用内容高度：始终 ≥ 视口高度，页面内所有纵向计算都应使用本属性。</summary>
        protected int ContentH
        {
            get { return Math.Max(ClientSize.Height, MinContentHeight); }
        }

        /// <summary>布局用内容宽度：滚轮滚动条占据非客户区，右对齐元素必须按客户端宽度计算。</summary>
        protected int ContentW
        {
            get { return ClientSize.Width; }
        }

        /// <summary>当前内容滚动偏移（0 表示顶部）。壳层绘制滚动条时读取。</summary>
        public int ScrollOffset
        {
            get { return _scrollY; }
        }

        /// <summary>可滚动最大值（内容高 - 视口高）。</summary>
        public int ScrollMax
        {
            get { return Math.Max(0, ContentH - ClientSize.Height); }
        }

        protected int ViewTop
        {
            get { return _scrollY; }
        }

        protected int ViewBottom
        {
            get { return _scrollY + ClientSize.Height; }
        }

        /// <summary>画布事件坐标已是内容坐标；保留本方法供页面统一调用（恒等映射）。</summary>
        protected static Point ToContent(Point p)
        {
            return p;
        }

        /// <summary>屏幕光标 → 内容坐标（画布子控件坐标，已包含滚动偏移）。</summary>
        protected Point CursorToContent()
        {
            return _canvas.PointToClient(Cursor.Position);
        }

        public void SetScrollOffset(int offset)
        {
            int max = ScrollMax;
            if (offset < 0) offset = 0;
            if (offset > max) offset = max;
            if (offset == _scrollY) return;
            _scrollY = offset;
            _canvas.Top = -_scrollY;
            _canvas.Invalidate();
            if (ScrollChanged != null) ScrollChanged();
        }

        /// <summary>鼠标滚轮滚动（由 DashboardForm 转发；delta 为系统 Wheel 增量）。</summary>
        public void ScrollByWheel(int delta)
        {
            SetScrollOffset(_scrollY - delta);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_scrollY > ScrollMax) _scrollY = ScrollMax;
            SyncCanvas();
            if (ScrollChanged != null) ScrollChanged();
        }

        public static DateTime StartFor(RangeKind r)
        {
            switch (r)
            {
                case RangeKind.Today: return DateTime.Today;
                case RangeKind.Week: return DateTime.Today.AddDays(-6);
                case RangeKind.Month: return DateTime.Today.AddDays(-29);
                default: return DateTime.MinValue;
            }
        }

        public static DateTime EndFor(RangeKind r)
        {
            return r == RangeKind.All ? DateTime.MaxValue : DateTime.Today;
        }

        public virtual void SetSnapshot(StatsSnapshot s)
        {
            Snap = s;
            _staticLayerValid = false;
            _canvas.Invalidate();
        }

        public virtual void ApplyTheme()
        {
            BackColor = Theme.P.Bg;
            if (_canvas != null) _canvas.BackColor = Theme.P.Bg;
            _staticLayerValid = false;
            Invalidate(true);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            // 隐藏页面释放静态层：12 页各 3MB 级位图没必要常驻
            if (!Visible) DropLayer();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) DropLayer();
            base.Dispose(disposing);
        }

        protected void RequestData()
        {
            if (DataRequested != null) DataRequested();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.P.Bg);
        }

        // 绘制委托给 _canvas（PageCanvas.OnPaint → PaintPage），视口本身不绘制内容。

        protected abstract void Draw(Graphics g);

        protected void DrawHeader(Graphics g)
        {
            Palette p = Theme.P;
            Ui.TextLeft(g, Title, Ui.PageTitle, p.Text, new Rectangle(0, 0, Math.Max(200, Width - 400), 34));
            Ui.TextLeft(g, Subtitle, Ui.BodySmall, p.Muted, new Rectangle(0, 34, Math.Max(200, Width - 400), 20));
        }

        protected void DrawRangeChips(Graphics g, int x, int y)
        {
            string[] labels = new string[] { "今日", "7 天", "30 天", "全部" };
            DrawChips(g, labels, (int)Range, x, y, 26, out _rangeRects);
        }

        protected bool HandleRangeClick(Point pt)
        {
            for (int i = 0; i < _rangeRects.Length; i++)
            {
                if (_rangeRects[i].Contains(pt))
                {
                    Range = (RangeKind)i;
                    RequestData();
                    Invalidate();
                    return true;
                }
            }
            return false;
        }

        protected void DrawChips(Graphics g, string[] labels, int selected, int x, int y, int height, out Rectangle[] rects)
        {
            Palette p = Theme.P;
            rects = new Rectangle[labels.Length];
            int cx = x;
            for (int i = 0; i < labels.Length; i++)
            {
                int w = Ui.MeasureWidth(g, labels[i], Ui.BodySmall) + 26;
                Rectangle r = new Rectangle(cx, y, w, height);
                Charts.Chip(g, r, labels[i], i == selected, p, Ui.BodySmall);
                rects[i] = r;
                cx += w + 8;
            }
        }

        protected static int HitRects(Point pt, Rectangle[] rects)
        {
            if (rects == null) return -1;
            for (int i = 0; i < rects.Length; i++)
            {
                if (rects[i].Contains(pt)) return i;
            }
            return -1;
        }

        protected void SectionCard(Graphics g, Rectangle r, string title, string subtitle)
        {
            Palette p = Theme.P;
            Charts.Card(g, r, p);
            Ui.TextLeft(g, title, Ui.Section, p.Text, new Rectangle(r.X + 18, r.Y + 14, r.Width - 36, 24));
            if (!string.IsNullOrEmpty(subtitle))
            {
                Ui.TextLeftEllipsis(g, subtitle, Ui.Label, p.Dim,
                    new Rectangle(r.X + 18, r.Y + 36, r.Width - 36, 18));
            }
        }

        protected static int PeakHour(int[] hours)
        {
            int best = 0;
            for (int i = 1; i < 24; i++) if (hours[i] > hours[best]) best = i;
            return best;
        }

        protected static string HourRange(int hour)
        {
            int end = (hour + 1) % 24;
            return hour.ToString("D2") + ":00 - " + end.ToString("D2") + ":00";
        }

        protected static List<RankRow> TopKeyRows(Dictionary<int, KeyAgg> keys, int count, bool bySwallow)
        {
            List<KeyValuePair<int, KeyAgg>> list = new List<KeyValuePair<int, KeyAgg>>(keys);
            list.Sort(delegate(KeyValuePair<int, KeyAgg> a, KeyValuePair<int, KeyAgg> b)
            {
                long av = bySwallow ? a.Value.Swallowed : a.Value.Count;
                long bv = bySwallow ? b.Value.Swallowed : b.Value.Count;
                if (av != bv) return bv.CompareTo(av);
                return a.Key.CompareTo(b.Key);
            });
            List<RankRow> rows = new List<RankRow>();
            for (int i = 0; i < list.Count && rows.Count < count; i++)
            {
                long value = bySwallow ? list[i].Value.Swallowed : list[i].Value.Count;
                if (value <= 0) continue;
                RankRow row = new RankRow();
                row.Label = KeyLayout.LabelFor(list[i].Key);
                row.Value = value;
                row.Swallowed = bySwallow ? 0 : list[i].Value.Swallowed;
                rows.Add(row);
            }
            return rows;
        }

        protected static List<RankRow> TopAppRows(Dictionary<string, AppAgg> apps, int count, bool bySwallow)
        {
            List<KeyValuePair<string, AppAgg>> list = new List<KeyValuePair<string, AppAgg>>(apps);
            list.Sort(delegate(KeyValuePair<string, AppAgg> a, KeyValuePair<string, AppAgg> b)
            {
                long av = bySwallow ? a.Value.Swallowed : a.Value.Total;
                long bv = bySwallow ? b.Value.Swallowed : b.Value.Total;
                if (av != bv) return bv.CompareTo(av);
                return string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
            });
            List<RankRow> rows = new List<RankRow>();
            for (int i = 0; i < list.Count && rows.Count < count; i++)
            {
                long value = bySwallow ? list[i].Value.Swallowed : list[i].Value.Total;
                if (value <= 0) continue;
                RankRow row = new RankRow();
                row.Label = AppNames.Display(list[i].Key);
                row.Value = value;
                row.Swallowed = bySwallow ? 0 : list[i].Value.Swallowed;
                rows.Add(row);
            }
            return rows;
        }
    }
}
