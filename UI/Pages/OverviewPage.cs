using System;
using System.Collections.Generic;
using System.Drawing;
using KeyInsight.Stats;

namespace KeyInsight.UI.Pages
{
    public sealed class OverviewPage : PageBase
    {
        private List<RankRow> _topKeys = new List<RankRow>();
        private List<RankRow> _topApps = new List<RankRow>();
        private readonly Rectangle[] _barSlots = new Rectangle[24];
        private Rectangle _barArea;
        private int _hoverHour = -1;

        public override void SetSnapshot(StatsSnapshot s)
        {
            base.SetSnapshot(s);
            // PR-2：排序/Top N 预计算，Draw 只绘制（不得在 Paint 内排序）
            _topKeys = TopKeyRows(s.Today.Keys, 8, false);
            _topApps = TopAppRows(s.Today.Apps, 5, false);
        }

        public override string Title { get { return "总览"; } }
        public override string Subtitle { get { return "今天的键盘使用情况与累计数据"; } }
        public override DateTime RangeStart { get { return DateTime.Today; } }
        public override DateTime RangeEnd { get { return DateTime.Today; } }

        protected override void Draw(Graphics g)
        {
            Palette p = Theme.P;
            DrawHeader(g);

            int y = 62;
            int gap = 14;
            int cardW = (ContentW - gap * 4) / 5;
            int cardH = 106;

            long mouseToday = 0;
            if (Snap.Today.Mouse != null) foreach (long v in Snap.Today.Mouse.Values) mouseToday += v;

            double rate = Snap.Today.Swallowed / (double)Math.Max(1, Snap.Today.Total + Snap.Today.Swallowed);
            double books = Snap.TotalAll / 100000.0;
            Charts.StatCard(g, new Rectangle(0, y, cardW, cardH), p, "今日按键", Charts.Number(Snap.Today.Total),
                Snap.Today.ActiveMinutes + " 分钟 · 峰值 " + Charts.Number(Snap.Today.KpmPeak) + " KPM", p.Accent1);
            Charts.StatCard(g, new Rectangle(cardW + gap, y, cardW, cardH), p, "今日鼠标",
                Charts.Number(mouseToday), "左/右/中/侧键 + 滚轮", p.Info);
            Charts.StatCard(g, new Rectangle((cardW + gap) * 2, y, cardW, cardH), p, "今日拦截", Charts.Number(Snap.Today.Swallowed),
                "抖动率 " + (rate * 100).ToString("F1") + "%", p.Bad);
            Charts.StatCard(g, new Rectangle((cardW + gap) * 3, y, cardW, cardH), p, "今日平均 KPM",
                Charts.Number(Math.Round(Snap.Today.KpmAverage)),
                "最近 1 分钟 " + Charts.Number(Snap.KpmNow) + " KPM", p.Accent2);
            Charts.StatCard(g, new Rectangle((cardW + gap) * 4, y, cardW, cardH), p, "累计按键",
                Charts.Number(Snap.TotalAll), "≈ 写完 " + books.ToString("F1") + " 本 10 万字小说", p.Accent3);

            int top = y + cardH + gap;
            int leftW = (int)(ContentW * 0.63);
            int rightW = ContentW - leftW - gap;
            int rowH = (ContentH - top - gap) / 2;
            if (rowH < 200) rowH = 200;

            Rectangle hourCard = new Rectangle(0, top, leftW, rowH);
            Rectangle keyCard = new Rectangle(leftW + gap, top, rightW, rowH);
            Rectangle recentCard = new Rectangle(0, top + rowH + gap, leftW, ContentH - (top + rowH + gap));
            Rectangle appCard = new Rectangle(leftW + gap, top + rowH + gap, rightW, ContentH - (top + rowH + gap));

            SectionCard(g, hourCard, "今日活跃度（24 小时）", "每个小时的按键次数（悬停查看精确值）");
            _barArea = new Rectangle(hourCard.X + 12, hourCard.Y + 58, hourCard.Width - 24, hourCard.Height - 70);
            Charts.DrawBars24(g, _barArea, Snap.Today.Hours, p, _barSlots);
            DrawBarTooltip(g, p);

            SectionCard(g, keyCard, "最常用按键", "今日 Top 8");
            if (_topKeys.Count == 0) Ui.TextCenter(g, "今天还没有按键数据", Ui.BodySmall, p.Dim,
                new Rectangle(keyCard.X + 18, keyCard.Y + 90, keyCard.Width - 36, 40));
            else Charts.DrawRankList(g, new Rectangle(keyCard.X + 18, keyCard.Y + 58, keyCard.Width - 36, keyCard.Height - 70), _topKeys, p, Ui.BodySmall);

            SectionCard(g, recentCard, "最近拦截", "被防抖吞掉的抖动事件");
            DrawRecent(g, new Rectangle(recentCard.X + 18, recentCard.Y + 52, recentCard.Width - 36, recentCard.Height - 64), p);

            SectionCard(g, appCard, "Top 应用", "今天按键最多的应用");
            if (_topApps.Count == 0) Ui.TextCenter(g, "今天还没有应用数据", Ui.BodySmall, p.Dim,
                new Rectangle(appCard.X + 18, appCard.Y + 80, appCard.Width - 36, 40));
            else Charts.DrawRankList(g, new Rectangle(appCard.X + 18, appCard.Y + 58, appCard.Width - 36, appCard.Height - 70), _topApps, p, Ui.BodySmall);
        }

        protected override void OnMouseMove(System.Windows.Forms.MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Point pt = ToContent(e.Location);
            int hour = -1;
            if (_barArea.Width > 0 && _barArea.Contains(pt))
            {
                Rectangle plot = Charts.BarsPlot(_barArea);
                hour = (int)((pt.X - plot.X) * 24 / (double)Math.Max(1, plot.Width));
                if (hour < 0) hour = 0;
                if (hour > 23) hour = 23;
            }
            if (hour != _hoverHour)
            {
                _hoverHour = hour;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverHour != -1)
            {
                _hoverHour = -1;
                Invalidate();
            }
        }

        private void DrawBarTooltip(Graphics g, Palette p)
        {
            if (_hoverHour < 0 || _hoverHour >= 24) return;
            Point cur = CursorToContent();
            Rectangle bplot = Charts.BarsPlot(_barArea);
            int bslot = Math.Max(1, bplot.Width / 24);
            Ui.FillRound(g, new RectangleF(bplot.X + _hoverHour * bslot, bplot.Y,
                bslot, bplot.Height), 4, Ui.WithAlpha(p.Accent1, 28));
            Charts.Tooltip(g, new Rectangle(0, 0, ContentW, ContentH), cur,
                _hoverHour.ToString("D2") + ":00 - " + ((_hoverHour + 1) % 24).ToString("D2") + ":00",
                Charts.Number(Snap.Today.Hours[_hoverHour]) + " 次", p);
        }

        private void DrawRecent(Graphics g, Rectangle r, Palette p)
        {
            List<RecentSwallow> list = Snap.RecentSwallows;
            if (list == null || list.Count == 0)
            {
                Ui.TextCenter(g, "暂无拦截记录", Ui.BodySmall, p.Dim, new Rectangle(r.X, r.Y + 30, r.Width, 40));
                return;
            }

            int rowH = Math.Min(28, r.Height / 5);
            int count = Math.Min(5, list.Count);
            for (int i = 0; i < count; i++)
            {
                RecentSwallow s = list[i];
                int y = r.Y + i * rowH;
                Ui.TextLeft(g, s.At.ToString("HH:mm:ss"), Ui.Mono, p.Dim, new Rectangle(r.X, y, 68, rowH));
                Ui.TextLeft(g, KeyLayout.LabelFor(s.Vk), Ui.BodySmall, p.Text, new Rectangle(r.X + 72, y, 56, rowH));
                Ui.TextLeftEllipsis(g, AppNames.Display(s.App), Ui.BodySmall, p.Muted, new Rectangle(r.X + 132, y, r.Width - 240, rowH));
                Ui.TextRight(g, s.GapDown.ToString("F1") + " ms", Ui.Mono, p.Muted, new Rectangle(r.Right - 96, y, 96, rowH));
            }
        }
    }
}
