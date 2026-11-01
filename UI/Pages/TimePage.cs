using System;
using System.Drawing;
using System.Windows.Forms;
using KeyInsight.Stats;

namespace KeyInsight.UI.Pages
{
    public sealed class TimePage : PageBase
    {
        public override string Title { get { return "时间分析"; } }
        public override string Subtitle { get { return "按键在一天、一周和更长周期里的分布"; } }

        public TimePage()
        {
            Range = RangeKind.Week;
        }

        protected override void Draw(Graphics g)
        {
            Palette p = Theme.P;
            DrawHeader(g);
            DrawRangeChips(g, 0, 58);

            int y = 100;
            int gap = 12;
            int cardW = (Width - gap * 3) / 4;
            int smallH = 72;
            int peak = PeakHour(Snap.Range.Hours);
            double avgDay = Snap.Range.Total / (double)Math.Max(1, Snap.Range.ActiveDays);

            SmallCard(g, new Rectangle(0, y, cardW, smallH), p, "范围按键", Charts.Number(Snap.Range.Total), p.Accent1);
            SmallCard(g, new Rectangle(cardW + gap, y, cardW, smallH), p, "活跃小时", Charts.Number(Snap.Range.ActiveHours), p.Accent2);
            SmallCard(g, new Rectangle((cardW + gap) * 2, y, cardW, smallH), p, "最活跃时段", HourRange(peak), p.Accent3);
            SmallCard(g, new Rectangle((cardW + gap) * 3, y, cardW, smallH), p, "日均按键", Charts.Number(Math.Round(avgDay)), p.Info);

            int y1 = y + smallH + gap;
            int hourH = 170;
            SectionCard(g, new Rectangle(0, y1, Width, hourH), "24 小时分布", "按小时累计的按键次数");
            Charts.DrawBars24(g, new Rectangle(12, y1 + 58, Width - 24, hourH - 68), Snap.Range.Hours, p);

            int y2 = y1 + hourH + gap;
            int heatH = 170;
            SectionCard(g, new Rectangle(0, y2, Width, heatH), "星期 × 小时热力", "颜色越深代表该时段越活跃");
            Charts.DrawHeat7x24(g, new Rectangle(16, y2 + 58, Width - 32, heatH - 68), Snap.Range.WeekdayHour, p);

            int y3 = y2 + heatH + gap;
            int trendH = Height - y3;
            if (trendH < 90) trendH = 90;
            SectionCard(g, new Rectangle(0, y3, Width, trendH), "最近 30 天趋势", "全部应用，不受上方范围影响");
            Charts.DrawTrend(g, new Rectangle(12, y3 + 56, Width - 24, trendH - 62), Snap.Last30Days, p);
        }

        private static void SmallCard(Graphics g, Rectangle r, Palette p, string label, string value, Color accent)
        {
            Charts.Card(g, r, p);
            Ui.FillRound(g, new RectangleF(r.X + 16, r.Y + 16, 4, 18), 2, accent);
            Ui.TextLeft(g, label, Ui.BodySmall, p.Muted, new Rectangle(r.X + 28, r.Y + 12, r.Width - 40, 24));
            Ui.TextLeft(g, value, Ui.CardValueSmall, p.Text, new Rectangle(r.X + 16, r.Y + 36, r.Width - 32, 28));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) HandleRangeClick(e.Location);
        }
    }
}
