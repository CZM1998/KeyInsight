using System;
using System.Collections.Generic;
using System.Drawing;
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

        protected PageBase()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.P.Bg;
            DoubleBuffered = true;
            TabStop = false;
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
            Invalidate();
        }

        public virtual void ApplyTheme()
        {
            BackColor = Theme.P.Bg;
            Invalidate();
        }

        protected void RequestData()
        {
            if (DataRequested != null) DataRequested();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.P.Bg);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Ui.Prep(e.Graphics);
            if (Snap == null) return;
            Draw(e.Graphics);
        }

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
