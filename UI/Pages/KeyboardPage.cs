using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using KeyInsight.Stats;

namespace KeyInsight.UI.Pages
{
    public sealed class KeyboardPage : PageBase
    {
        private readonly IShellActions _shell;
        private string _appFilter;
        private string[] _appKeys = new string[0];
        private Rectangle[] _appRects = new Rectangle[0];
        private Rectangle[] _layoutRects = new Rectangle[0];
        private Dictionary<KeyDef, RectangleF> _hits = new Dictionary<KeyDef, RectangleF>();
        private KeyDef _hover;
        private readonly ToolTip _tip = new ToolTip();

        public override string Title { get { return "键盘热力图"; } }
        public override string Subtitle { get { return "每个按键的使用次数，颜色越深代表使用越多"; } }

        public KeyboardPage(IShellActions shell)
        {
            _shell = shell;
            _tip.InitialDelay = 150;
            _tip.ReshowDelay = 60;
            _tip.AutoPopDelay = 6000;
            Range = RangeKind.Week;
        }

        private KeyLayoutDef LayoutDef
        {
            get
            {
                return _shell.Cfg.KeyboardLayout == 0 ? KeyLayout.Layout87 : KeyLayout.Layout108;
            }
        }

        public override void SetSnapshot(StatsSnapshot s)
        {
            base.SetSnapshot(s);
            if (_appFilter != null && !s.Range.Apps.ContainsKey(_appFilter))
            {
                _appFilter = null;
                _hover = null;
            }
        }

        protected override void Draw(Graphics g)
        {
            Palette p = Theme.P;
            DrawHeader(g);
            DrawRangeChips(g, 0, 58);
            DrawLayoutChips(g, 58);
            DrawAppChips(g, 0, 92);

            int cardY = 128;
            int statsH = 106;
            int keyboardH = Height - cardY - 14 - statsH;
            Rectangle card = new Rectangle(0, cardY, Width, keyboardH);
            KeyLayoutDef layout = LayoutDef;
            string scope = _appFilter == null ? "范围：" + RangeText() : "应用：" + AppNames.Display(_appFilter);
            SectionCard(g, card, "按键分布", scope + " · " + layout.ShortName);

            Dictionary<int, KeyAgg> counts;
            long total;
            long swallowed;
            if (_appFilter == null)
            {
                counts = Snap.Range.Keys;
                total = Snap.Range.Total;
                swallowed = Snap.Range.Swallowed;
            }
            else
            {
                AppAgg app;
                if (Snap.Range.Apps.TryGetValue(_appFilter, out app))
                {
                    counts = app.Keys;
                    total = app.Total;
                    swallowed = app.Swallowed;
                }
                else
                {
                    counts = new Dictionary<int, KeyAgg>();
                    total = 0;
                    swallowed = 0;
                }
            }

            Rectangle kbArea = new Rectangle(card.X + 16, card.Y + 54, card.Width - 32, card.Height - 54 - 42);
            _hits = Charts.DrawKeyboard(g, kbArea, counts, p, _hover, layout);
            DrawLegend(g, new Rectangle(card.X + 22, card.Bottom - 30, card.Width - 44, 18), p, counts, total);

            int gap = 14;
            int cardW = (Width - gap * 3) / 4;
            int sy = cardY + keyboardH + gap;

            string topKey = "-";
            long topVal = 0;
            int used = 0;
            foreach (KeyDef kd in layout.Keys)
            {
                long n = KeyLayout.CountFor(kd, counts);
                if (n > 0) used++;
                if (n > topVal) { topVal = n; topKey = kd.Label; }
            }

            Charts.StatCard(g, new Rectangle(0, sy, cardW, statsH), p, "已按键数", Charts.Number(total),
                "范围：" + RangeText(), p.Accent1);
            Charts.StatCard(g, new Rectangle(cardW + gap, sy, cardW, statsH), p, "最常用键", topKey,
                topVal > 0 ? Charts.Number(topVal) + " 次" : "暂无数据", p.Accent2);
            Charts.StatCard(g, new Rectangle((cardW + gap) * 2, sy, cardW, statsH), p, "使用过的键",
                used + " / " + layout.KeyCount, "至少按下一次", p.Accent3);
            Charts.StatCard(g, new Rectangle((cardW + gap) * 3, sy, cardW, statsH), p, "拦截次数",
                Charts.Number(swallowed), "该范围内的抖动", p.Bad);
        }

        private void DrawLayoutChips(Graphics g, int y)
        {
            string[] labels = new string[] { "87 键", "108 键" };
            int w0 = Ui.MeasureWidth(g, labels[0], Ui.BodySmall) + 26;
            int w1 = Ui.MeasureWidth(g, labels[1], Ui.BodySmall) + 26;
            int total = w0 + 8 + w1;
            int x = Width - total;
            int sel = _shell.Cfg.KeyboardLayout == 0 ? 0 : 1;
            DrawChips(g, labels, sel, x, y, 26, out _layoutRects);
        }

        private void DrawAppChips(Graphics g, int x, int y)
        {
            List<KeyValuePair<string, AppAgg>> list = new List<KeyValuePair<string, AppAgg>>(Snap.Range.Apps);
            list.Sort(delegate(KeyValuePair<string, AppAgg> a, KeyValuePair<string, AppAgg> b)
            {
                return b.Value.Total.CompareTo(a.Value.Total);
            });

            List<string> labels = new List<string>();
            List<string> keys = new List<string>();
            labels.Add("全部应用");
            keys.Add(null);
            for (int i = 0; i < list.Count && labels.Count < 7; i++)
            {
                if (list[i].Value.Total <= 0) continue;
                labels.Add(AppNames.Display(list[i].Key));
                keys.Add(list[i].Key);
            }
            if (_appFilter != null && !keys.Contains(_appFilter))
            {
                labels.Add(AppNames.Display(_appFilter));
                keys.Add(_appFilter);
            }

            int selected = 0;
            for (int i = 1; i < keys.Count; i++) if (keys[i] == _appFilter) { selected = i; break; }

            _appKeys = keys.ToArray();
            DrawChips(g, labels.ToArray(), selected, x, y, 26, out _appRects);
        }

        private void DrawLegend(Graphics g, Rectangle r, Palette p, Dictionary<int, KeyAgg> counts, long total)
        {
            int x = r.X;
            Ui.TextLeft(g, "少", Ui.Label, p.Dim, new Rectangle(x, r.Y, 18, r.Height));
            x += 20;
            int block = 16;
            for (int i = 0; i < p.Heat.Length; i++)
            {
                Color c = p.HeatColor(i / (double)(p.Heat.Length - 1));
                Ui.FillRound(g, new RectangleF(x, r.Y + 5, block, r.Height - 10), 2, c);
                x += block + 2;
            }
            Ui.TextLeft(g, "多", Ui.Label, p.Dim, new Rectangle(x + 4, r.Y, 18, r.Height));
            int lx = x + 42;
            int cy = r.Y + r.Height / 2;
            using (SolidBrush ring = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
            {
                g.FillEllipse(ring, lx - 6, cy - 6, 12, 12);
            }
            using (SolidBrush b = new SolidBrush(Color.FromArgb(190, 30, 30)))
            {
                g.FillEllipse(b, lx - 4, cy - 4, 8, 8);
            }
            Ui.TextLeft(g, "红点 = 抖动，颜色越深次数越多", Ui.Label, p.Muted,
                new Rectangle(lx + 12, r.Y, 320, r.Height));
            Ui.TextRight(g, "颜色越深 = 使用越多", Ui.Label, p.Dim,
                new Rectangle(r.Right - 220, r.Y, 220, r.Height));
        }

        private string RangeText()
        {
            switch (Range)
            {
                case RangeKind.Today: return "今日";
                case RangeKind.Week: return "最近 7 天";
                case RangeKind.Month: return "最近 30 天";
                default: return "全部";
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (HandleRangeClick(e.Location)) return;

            int li = HitRects(e.Location, _layoutRects);
            if (li >= 0)
            {
                _shell.Cfg.KeyboardLayout = li;
                _shell.Cfg.Save();
                _hover = null;
                Invalidate();
                return;
            }

            int idx = HitRects(e.Location, _appRects);
            if (idx >= 0 && idx < _appKeys.Length)
            {
                _appFilter = _appKeys[idx];
                _hover = null;
                Invalidate();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            KeyDef found = null;
            foreach (KeyValuePair<KeyDef, RectangleF> kv in _hits)
            {
                if (kv.Value.Contains(e.X, e.Y)) { found = kv.Key; break; }
            }
            if (found != _hover)
            {
                _hover = found;
                Invalidate();
                if (found != null)
                {
                    Dictionary<int, KeyAgg> counts = _appFilter == null
                        ? (Snap == null ? null : Snap.Range.Keys)
                        : (Snap == null || !Snap.Range.Apps.ContainsKey(_appFilter) ? null : Snap.Range.Apps[_appFilter].Keys);
                    long n = KeyLayout.CountFor(found, counts);
                    long sw = KeyLayout.SwallowedFor(found, counts);
                    long total = _appFilter == null ? (Snap == null ? 0 : Snap.Range.Total) : (Snap != null && Snap.Range.Apps.ContainsKey(_appFilter) ? Snap.Range.Apps[_appFilter].Total : 0);
                    double share = total > 0 ? (n * 100.0 / total) : 0;
                    string text = found.Label + "\n" + Charts.Number(n) + " 次 · 占比 " + share.ToString("F1") + "%";
                    if (sw > 0) text += "\n拦截 " + Charts.Number(sw) + " 次";
                    _tip.Show(text, this, e.X + 14, e.Y + 18, 5000);
                }
                else
                {
                    _tip.Hide(this);
                }
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover != null)
            {
                _hover = null;
                _tip.Hide(this);
                Invalidate();
            }
        }
    }
}
