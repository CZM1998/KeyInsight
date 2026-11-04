using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using KeyInsight.Stats;

namespace KeyInsight.UI.Pages
{
    public sealed class AppsPage : PageBase
    {
        private string _selected;
        private Rectangle[] _rowRects = new Rectangle[0];
        private string[] _rowKeys = new string[0];
        private List<KeyValuePair<string, AppAgg>> _sorted = new List<KeyValuePair<string, AppAgg>>();
        private long _maxTotal = 1;

        public override string Title { get { return "应用统计"; } }
        public override string Subtitle { get { return "按前台应用分组，看看时间和按键花在了哪里"; } }

        public AppsPage()
        {
            Range = RangeKind.Week;
        }

        public override void SetSnapshot(StatsSnapshot s)
        {
            base.SetSnapshot(s);
            // PR-2：应用排序在快照阶段完成
            _sorted = new List<KeyValuePair<string, AppAgg>>(s.Range.Apps);
            _sorted.Sort(delegate(KeyValuePair<string, AppAgg> a, KeyValuePair<string, AppAgg> b)
            {
                return b.Value.Total.CompareTo(a.Value.Total);
            });
            _maxTotal = 1;
            for (int i = 0; i < _sorted.Count; i++)
            {
                if (_sorted[i].Value.Total > _maxTotal) _maxTotal = _sorted[i].Value.Total;
            }
            if (_selected == null || !s.Range.Apps.ContainsKey(_selected))
            {
                _selected = TopApp(s.Range.Apps);
            }
        }

        protected override void Draw(Graphics g)
        {
            Palette p = Theme.P;
            DrawHeader(g);
            DrawRangeChips(g, 0, 58);

            int top = 100;
            int leftW = (int)(ContentW * 0.44);
            int gap = 14;
            int rightW = ContentW - leftW - gap;
            int height = ContentH - top;

            Rectangle listCard = new Rectangle(0, top, leftW, height);
            Rectangle detailCard = new Rectangle(leftW + gap, top, rightW, height);
            SectionCard(g, listCard, "应用排行", "点击查看该应用的详细数据");
            DrawAppList(g, new Rectangle(listCard.X + 18, listCard.Y + 58, listCard.Width - 36, listCard.Height - 70), p);
            DrawDetail(g, detailCard, p);
        }

        private void DrawAppList(Graphics g, Rectangle r, Palette p)
        {
            List<KeyValuePair<string, AppAgg>> list = _sorted;

            List<string> keys = new List<string>();
            List<Rectangle> rects = new List<Rectangle>();
            int rowH = 36;
            int maxRows = Math.Max(1, r.Height / rowH);
            for (int i = 0; i < list.Count && i < maxRows; i++)
            {
                if (list[i].Value.Total <= 0) continue;
                keys.Add(list[i].Key);
                rects.Add(new Rectangle(r.X, r.Y + rects.Count * rowH, r.Width, rowH - 4));
            }
            _rowKeys = keys.ToArray();
            _rowRects = rects.ToArray();

            if (_rowKeys.Length == 0)
            {
                Ui.TextCenter(g, "这个范围内还没有应用数据", Ui.BodySmall, p.Dim,
                    new Rectangle(r.X, r.Y + 40, r.Width, 40));
                return;
            }

            long max = _maxTotal;
            long total = Math.Max(1, Snap.Range.Total);

            for (int i = 0; i < keys.Count; i++)
            {
                AppAgg a = Snap.Range.Apps[keys[i]];
                Rectangle row = _rowRects[i];
                bool sel = keys[i] == _selected;
                if (sel) Ui.FillRound(g, row, 8, p.Selected);
                else if (row.Contains(CursorToContent())) Ui.FillRound(g, row, 8, p.Hover);

                Ui.TextRight(g, (i + 1).ToString(), Ui.Mono, p.Dim, new Rectangle(row.X + 2, row.Y, 22, row.Height));
                int dotX = row.X + 30;
                int dotY = row.Y + row.Height / 2;
                Ui.DrawDot(g, dotX, dotY, 4, AppNames.ColorFor(keys[i]));
                Ui.TextLeftEllipsis(g, AppNames.Display(keys[i]), sel ? Ui.Body : Ui.BodySmall,
                    sel ? p.Text : p.Muted, new Rectangle(row.X + 42, row.Y, row.Width - 150, row.Height));
                Ui.TextRight(g, Charts.Number(a.Total), Ui.BodySmall, p.Text,
                    new Rectangle(row.Right - 120, row.Y, 70, row.Height));
                Ui.TextRight(g, (a.Total * 100.0 / total).ToString("F0") + "%", Ui.Label, p.Dim,
                    new Rectangle(row.Right - 48, row.Y, 46, row.Height));

                float w = (float)((row.Width - 44) * (a.Total / (double)max));
                if (w < 2) w = 2;
                Ui.FillRound(g, new RectangleF(row.X + 42, row.Bottom - 7, row.Width - 44, 3), 1.5f, p.CardAlt);
                Ui.GradientRound(g, new RectangleF(row.X + 42, row.Bottom - 7, w, 3), 1.5f, p.Accent2, p.Accent1, 0f);
            }
        }

        private void DrawDetail(Graphics g, Rectangle card, Palette p)
        {
            Charts.Card(g, card, p);
            AppAgg app;
            if (_selected == null || !Snap.Range.Apps.TryGetValue(_selected, out app))
            {
                Ui.TextCenter(g, "选择左侧应用查看详情", Ui.BodySmall, p.Dim,
                    new Rectangle(card.X, card.Y + 100, card.Width, 60));
                return;
            }

            int x = card.X + 20;
            int y = card.Y + 18;
            Ui.DrawDot(g, x + 6, y + 16, 6, AppNames.ColorFor(_selected));
            Ui.TextLeft(g, AppNames.Display(_selected), Ui.AppTitle,
                p.Text, new Rectangle(x + 20, y, card.Width - 180, 30));
            Ui.TextRight(g, (app.Total * 100.0 / Math.Max(1, Snap.Range.Total)).ToString("F1") + "% 占比",
                Ui.Label, p.Muted, new Rectangle(card.Right - 170, y, 150, 30));

            int statsY = y + 42;
            int gap = 10;
            int w = (card.Width - 40 - gap) / 2;
            int h = 66;
            string[] miniValues = new string[]
            {
                Charts.Number(app.Total),
                Charts.Number(app.Swallowed),
                HourRange(PeakHour(app.Hours)),
                (app.Total * 100.0 / Math.Max(1, Snap.Range.Total)).ToString("F1") + "%"
            };
            Font miniFont = Charts.FitValueFont(g, miniValues, Math.Max(20, w - 34), 28);
            MiniStat(g, new Rectangle(x, statsY, w, h), p, "按键数", miniValues[0], p.Accent1, miniFont);
            MiniStat(g, new Rectangle(x + w + gap, statsY, w, h), p, "拦截", miniValues[1], p.Bad, miniFont);
            MiniStat(g, new Rectangle(x, statsY + h + gap, w, h), p, "活跃时段", miniValues[2], p.Accent2, miniFont);
            MiniStat(g, new Rectangle(x + w + gap, statsY + h + gap, w, h), p, "占比",
                miniValues[3], p.Accent3, miniFont);

            int listY = statsY + (h + gap) * 2 + 6;
            Ui.TextLeft(g, "Top 按键", Ui.Section, p.Text, new Rectangle(x, listY, card.Width - 40, 24));
            List<RankRow> keys = TopKeyRows(app.Keys, 6, false);
            if (keys.Count == 0)
            {
                Ui.TextCenter(g, "暂无按键数据", Ui.BodySmall, p.Dim,
                    new Rectangle(x, listY + 30, card.Width - 40, 40));
            }
            else
            {
                Charts.DrawRankList(g, new Rectangle(x, listY + 30, card.Width - 40, 190), keys, p, Ui.BodySmall);
            }

            int barsY = Math.Min(card.Bottom - 92, listY + 230);
            Ui.TextLeft(g, "24 小时分布", Ui.Section, p.Text, new Rectangle(x, barsY, card.Width - 40, 24));
            Rectangle bars = new Rectangle(x + 4, barsY + 34, card.Width - 48, 46);
            Charts.DrawMiniBars(g, bars, app.Hours, p);
            Ui.TextLeft(g, "00", Ui.Label, p.Dim, new Rectangle(bars.X, bars.Bottom + 2, 22, 14));
            Ui.TextCenter(g, "12", Ui.Label, p.Dim, new Rectangle(bars.X + bars.Width / 2 - 10, bars.Bottom + 2, 22, 14));
            Ui.TextRight(g, "23", Ui.Label, p.Dim, new Rectangle(bars.Right - 22, bars.Bottom + 2, 22, 14));
        }

        private static void MiniStat(Graphics g, Rectangle r, Palette p, string label, string value,
            Color accent, Font valueFont)
        {
            Ui.FillRound(g, r, 10, p.CardAlt);
            Ui.StrokeRound(g, r, 10, p.Border);
            Ui.FillRound(g, new RectangleF(r.X + 14, r.Y + 16, 3, 16), 1.5f, accent);
            Ui.TextLeftFit(g, label, Ui.Label, p.Muted, new Rectangle(r.X + 24, r.Y + 10, r.Width - 34, 20));
            int avail = Math.Max(20, r.Width - 34);
            Font vf = valueFont;
            if (vf == null || Ui.MeasureWidth(g, value, vf) > avail) vf = Charts.FitValueFont(g, value, avail, 28);
            if (vf == null) vf = Ui.CardValueSmall;
            if (vf.Height / Math.Max(0.01f, Ui.InkScale(g)) > 28) vf = Ui.CardValueSmall;
            Ui.TextLeftFit(g, value, vf, p.Text, new Rectangle(r.X + 24, r.Y + 30, avail, 28));
        }

        private static string TopApp(Dictionary<string, AppAgg> apps)
        {
            string best = null;
            long max = 0;
            foreach (KeyValuePair<string, AppAgg> kv in apps)
            {
                if (kv.Value.Total > max) { max = kv.Value.Total; best = kv.Key; }
            }
            return best;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Point pt = ToContent(e.Location);
            if (HandleRangeClick(pt)) return;
            int idx = HitRects(pt, _rowRects);
            if (idx >= 0 && idx < _rowKeys.Length)
            {
                _selected = _rowKeys[idx];
                Invalidate();
            }
        }
    }
}
