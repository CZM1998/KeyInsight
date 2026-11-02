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
            int cardW = (ContentW - gap * 3) / 4;
            int smallH = 72;
            int peak = PeakHour(Snap.Range.Hours);
            double avgDay = Snap.Range.Total / (double)Math.Max(1, Snap.Range.ActiveDays);

            SmallCard(g, new Rectangle(0, y, cardW, smallH), p, "范围按键", Charts.Number(Snap.Range.Total), p.Accent1);
            SmallCard(g, new Rectangle(cardW + gap, y, cardW, smallH), p, "综合活跃分钟",
                Charts.Number(Snap.CombinedActiveMinutes), p.Accent2);
            SmallCard(g, new Rectangle((cardW + gap) * 2, y, cardW, smallH), p, "最活跃时段", HourRange(peak), p.Accent3);
            SmallCard(g, new Rectangle((cardW + gap) * 3, y, cardW, smallH), p, "日均按键", Charts.Number(Math.Round(avgDay)), p.Info);

            // PR-3：Standard 宽度把两张图并排，趋势全宽底部；Compact 宽度堆叠。
            bool twoCol = ContentW >= 860;
            int y1 = y + smallH + gap;
            int rightW = Math.Max(300, (int)(ContentW * 0.34));
            int trendLeftW = ContentW - rightW - gap;

            if (twoCol)
            {
                int chartH = 196;
                int chartW = (ContentW - gap) / 2;
                SectionCard(g, new Rectangle(0, y1, chartW, chartH), "24 小时分布", "按小时累计的按键次数");
                Charts.DrawBars24(g, new Rectangle(12, y1 + 58, chartW - 24, chartH - 68), Snap.Range.Hours, p);
                SectionCard(g, new Rectangle(chartW + gap, y1, ContentW - chartW - gap, chartH),
                    "星期 × 小时热力", "颜色越深代表该时段越活跃");
                Charts.DrawHeat7x24(g, new Rectangle(chartW + gap + 16, y1 + 58,
                    ContentW - chartW - gap - 32, chartH - 68), Snap.Range.WeekdayHour, p);

                int y3 = y1 + chartH + gap;
                int trendH = ContentH - y3;
                if (trendH < 120) trendH = 120;
                SectionCard(g, new Rectangle(0, y3, trendLeftW, trendH), "最近 30 天趋势", "全部应用，不受上方范围影响");
                Charts.DrawTrend(g, new Rectangle(12, y3 + 56, trendLeftW - 24, trendH - 62), Snap.Last30Days, p);
                SectionCard(g, new Rectangle(trendLeftW + gap, y3, rightW, trendH), "常用快捷键",
                    "仅聚合计数（Ctrl/Alt/Win 组合），不记录按键序列");
                DrawShortcuts(g, new Rectangle(trendLeftW + gap + 18, y3 + 56, rightW - 36, trendH - 64), p);
                return;
            }

            int hourH = 150;
            SectionCard(g, new Rectangle(0, y1, ContentW, hourH), "24 小时分布", "按小时累计的按键次数");
            Charts.DrawBars24(g, new Rectangle(12, y1 + 58, ContentW - 24, hourH - 68), Snap.Range.Hours, p);

            int y2 = y1 + hourH + gap;
            int heatH = 140;
            SectionCard(g, new Rectangle(0, y2, ContentW, heatH), "星期 × 小时热力", "颜色越深代表该时段越活跃");
            Charts.DrawHeat7x24(g, new Rectangle(16, y2 + 58, ContentW - 32, heatH - 68), Snap.Range.WeekdayHour, p);

            int y3c = y2 + heatH + gap;
            int trendHc = ContentH - y3c;
            if (trendHc < 120) trendHc = 120;
            SectionCard(g, new Rectangle(0, y3c, trendLeftW, trendHc), "最近 30 天趋势", "全部应用，不受上方范围影响");
            Charts.DrawTrend(g, new Rectangle(12, y3c + 56, trendLeftW - 24, trendHc - 62), Snap.Last30Days, p);
            SectionCard(g, new Rectangle(trendLeftW + gap, y3c, rightW, trendHc), "常用快捷键",
                "仅聚合计数（Ctrl/Alt/Win 组合），不记录按键序列");
            DrawShortcuts(g, new Rectangle(trendLeftW + gap + 18, y3c + 56, rightW - 36, trendHc - 64), p);
        }

        private void DrawShortcuts(Graphics g, Rectangle r, Palette p)
        {
            if (Snap.TopShortcuts == null || Snap.TopShortcuts.Count == 0)
            {
                Ui.TextCenter(g, "暂无组合键数据", Ui.BodySmall, p.Dim,
                    new Rectangle(r.X, r.Y + 20, r.Width, 30));
                return;
            }
            int rowH = 22;
            int max = Math.Max(1, (int)Snap.TopShortcuts[0].Value);
            int count = Math.Min(Snap.TopShortcuts.Count, Math.Max(1, r.Height / rowH));
            for (int i = 0; i < count; i++)
            {
                int ry = r.Y + i * rowH;
                string name = Snap.TopShortcuts[i].Key.ToUpperInvariant();
                long v = Snap.TopShortcuts[i].Value;
                int barW = (int)((r.Width - 150) * (v / (double)max));
                if (barW < 3) barW = 3;
                Ui.FillRound(g, new RectangleF(r.X, ry + 7, barW, 8), 4, Ui.WithAlpha(p.Accent2, 90));
                Ui.TextLeft(g, name, Ui.Mono, p.Text, new Rectangle(r.X + 4, ry, 110, rowH));
                Ui.TextRight(g, Charts.Number(v) + " 次", Ui.Label, p.Muted,
                    new Rectangle(r.Right - 90, ry, 90, rowH));
            }
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
            if (e.Button == MouseButtons.Left) HandleRangeClick(ToContent(e.Location));
        }
    }
}
