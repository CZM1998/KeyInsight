using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using KeyInsight.Stats;

namespace KeyInsight.UI.Pages
{
    public sealed class TimePage : PageBase
    {
        private int _hoverKind;          // 0 无 1 小时柱 2 热力单元 3 趋势点 4 应用行
        private int _hoverIdx = -1;
        private readonly Rectangle[] _barSlots = new Rectangle[24];
        private readonly Rectangle[] _heatCells = new Rectangle[168];
        private readonly Rectangle[] _trendSlots = new Rectangle[30];
        private Rectangle _barArea, _heatArea, _trendArea;   // full chart hit areas
        private readonly List<AppRow> _appRows = new List<AppRow>();
        private long _appTotalSeconds;

        private sealed class AppRow
        {
            public Rectangle Rect;
            public string Name;
            public long Seconds;
        }

        public override string Title { get { return "时间分析"; } }
        public override string Subtitle { get { return "按键分布与应用活跃时长（前台窗口估算，空闲不计入）"; } }

        // 4 KPI + 两图 + 趋势/快捷键 + 应用活跃时长
        protected override int MinContentHeight { get { return 860; } }

        public TimePage()
        {
            Range = RangeKind.Week;
        }

        private Dictionary<string, long> AppTimeForRange()
        {
            return AppTimeTracker.GetRange(RangeStart, RangeEnd);
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

            Dictionary<string, long> appTime = AppTimeForRange();
            _appTotalSeconds = AppTimeTracker.TotalSeconds(appTime);

            SmallCard(g, new Rectangle(0, y, cardW, smallH), p, "范围按键", Charts.Number(Snap.Range.Total), p.Accent1);
            SmallCard(g, new Rectangle(cardW + gap, y, cardW, smallH), p, "综合活跃分钟",
                Charts.Number(Snap.CombinedActiveMinutes), p.Accent2);
            SmallCard(g, new Rectangle((cardW + gap) * 2, y, cardW, smallH), p, "最活跃时段", HourRange(peak), p.Accent3);
            SmallCard(g, new Rectangle((cardW + gap) * 3, y, cardW, smallH), p, "应用活跃时长",
                FormatDuration(_appTotalSeconds), p.Info);

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
                _barArea = new Rectangle(12, y1 + 58, chartW - 24, chartH - 68);
                Charts.DrawBars24(g, _barArea, Snap.Range.Hours, p, _barSlots);
                SectionCard(g, new Rectangle(chartW + gap, y1, ContentW - chartW - gap, chartH),
                    "星期 × 小时热力", "颜色越深代表该时段越活跃");
                _heatArea = new Rectangle(chartW + gap + 16, y1 + 58,
                    ContentW - chartW - gap - 32, chartH - 68);
                Charts.DrawHeat7x24(g, _heatArea, Snap.Range.WeekdayHour, p, _heatCells);

                int y3 = y1 + chartH + gap;
                const int trendH = 200;
                SectionCard(g, new Rectangle(0, y3, trendLeftW, trendH), "最近 30 天趋势", "全部应用，不受上方范围影响");
                _trendArea = new Rectangle(12, y3 + 56, trendLeftW - 24, trendH - 62);
                Charts.DrawTrend(g, _trendArea, Snap.Last30Days, p, _trendSlots);
                SectionCard(g, new Rectangle(trendLeftW + gap, y3, rightW, trendH), "常用快捷键",
                    "仅聚合计数（Ctrl/Alt/Win 组合），不记录按键序列");
                DrawShortcuts(g, new Rectangle(trendLeftW + gap + 18, y3 + 56, rightW - 36, trendH - 64), p);

                int y4 = y3 + trendH + gap;
                int appH = ContentH - y4 - 16;
                if (appH < 170) appH = 170;
                DrawAppTime(g, new Rectangle(0, y4, ContentW, appH), appTime, p);
            }
            else
            {
                int hourH = 150;
                SectionCard(g, new Rectangle(0, y1, ContentW, hourH), "24 小时分布", "按小时累计的按键次数");
                _barArea = new Rectangle(12, y1 + 58, ContentW - 24, hourH - 68);
                Charts.DrawBars24(g, _barArea, Snap.Range.Hours, p, _barSlots);

                int y2 = y1 + hourH + gap;
                int heatH = 140;
                SectionCard(g, new Rectangle(0, y2, ContentW, heatH), "星期 × 小时热力", "颜色越深代表该时段越活跃");
                _heatArea = new Rectangle(16, y2 + 58, ContentW - 32, heatH - 68);
                Charts.DrawHeat7x24(g, _heatArea, Snap.Range.WeekdayHour, p, _heatCells);

                int y3 = y2 + heatH + gap;
                int trendHc = 190;
                SectionCard(g, new Rectangle(0, y3, trendLeftW, trendHc), "最近 30 天趋势", "全部应用，不受上方范围影响");
                _trendArea = new Rectangle(12, y3 + 56, trendLeftW - 24, trendHc - 62);
                Charts.DrawTrend(g, _trendArea, Snap.Last30Days, p, _trendSlots);
                SectionCard(g, new Rectangle(trendLeftW + gap, y3, rightW, trendHc), "常用快捷键",
                    "仅聚合计数（Ctrl/Alt/Win 组合），不记录按键序列");
                DrawShortcuts(g, new Rectangle(trendLeftW + gap + 18, y3 + 56, rightW - 36, trendHc - 64), p);

                int y4c = y3 + trendHc + gap;
                int appHc = ContentH - y4c - 16;
                if (appHc < 170) appHc = 170;
                DrawAppTime(g, new Rectangle(0, y4c, ContentW, appHc), appTime, p);
            }

        }

        /// <summary>静态层只管图表与卡片；hover 高亮和 tooltip 每次交互重画。</summary>
        protected override void DrawOverlay(Graphics g)
        {
            if (_hoverKind != 0) DrawHoverTooltip(g, Theme.P);
        }

        // ---------------------------------------------------------------- 应用活跃时长

        private void DrawAppTime(Graphics g, Rectangle card, Dictionary<string, long> appTime, Palette p)
        {
            _appRows.Clear();
            long total = _appTotalSeconds;
            SectionCard(g, card, "应用活跃时长",
                total > 0
                    ? "前台窗口活跃时长估算 · 共 " + FormatDuration(total) + " · 空闲 ≥2 分钟不计入"
                    : "尚未开始统计 —— 保持应用运行，前台窗口使用时长会自动累计");

            if (total <= 0 || appTime.Count == 0)
            {
                Ui.TextCenter(g, "还没有应用活跃数据（空闲时间不计入）", Ui.BodySmall, p.Dim,
                    new Rectangle(card.X, card.Y + 80, card.Width, 40));
                return;
            }

            List<KeyValuePair<string, long>> top = AppTimeTracker.TopApps(appTime, 6);
            int x = card.X + 20;
            int w = card.Width - 40;
            int rowH = Math.Min(36, Math.Max(26, (card.Height - 66) / Math.Max(1, top.Count)));
            long max = top.Count > 0 ? top[0].Value : 1;
            if (max < 1) max = 1;
            int barLeft = x + 190;
            int barRight = x + w - 190;

            for (int i = 0; i < top.Count; i++)
            {
                int ry = card.Y + 54 + i * rowH;
                Rectangle rowRect = new Rectangle(x, ry, w, rowH - 4);
                AppRow row = new AppRow();
                row.Rect = rowRect;
                row.Name = top[i].Key;
                row.Seconds = top[i].Value;
                _appRows.Add(row);

                bool hover = _hoverKind == 4 && _hoverIdx == i;
                if (hover) Ui.FillRound(g, rowRect, 7, p.Hover);

                Ui.TextLeft(g, (i + 1).ToString(), Ui.Mono, p.Dim,
                    new Rectangle(x, ry, 22, rowH - 4));
                Ui.TextLeftEllipsis(g, AppNames.Display(top[i].Key), Ui.BodySmall, p.Text,
                    new Rectangle(x + 24, ry, 160, rowH - 4));

                int barW = (int)((barRight - barLeft) * (top[i].Value / (double)max));
                if (barW < 3) barW = 3;
                RectangleF track = new RectangleF(barLeft, ry + (rowH - 4) / 2f - 5, barRight - barLeft, 10);
                Ui.FillRound(g, track, 5, p.CardAlt);
                Ui.GradientRound(g, new RectangleF(barLeft, ry + (rowH - 4) / 2f - 5, barW, 10),
                    5, p.Accent2, p.Accent1, 0f);

                string dur = FormatDuration(top[i].Value);
                double pct = top[i].Value * 100.0 / total;
                Ui.TextRight(g, dur, Ui.Label, p.Text,
                    new Rectangle(barRight + 8, ry, 96, rowH - 4));
                Ui.TextRight(g, pct.ToString("F1") + "%", Ui.Label, p.Muted,
                    new Rectangle(barRight + 108, ry, 70, rowH - 4));
            }
        }

        private static string FormatDuration(long seconds)
        {
            if (seconds <= 0) return "0 分";
            if (seconds < 60) return "<1 分";
            long minutes = seconds / 60;
            if (minutes < 60) return minutes + " 分";
            return (minutes / 60) + " 小时 " + (minutes % 60) + " 分";
        }

        // ---------------------------------------------------------------- 悬停数据检查

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Point pt = ToContent(e.Location);
            int kind = 0, idx = -1;
            // Full coverage: any point inside a chart card maps to the nearest
            // bucket (plot rect + clamped index), so gaps and margins hover too.
            if (_barArea.Width > 0 && _barArea.Contains(pt))
            {
                kind = 1;
                idx = IndexFromX(Charts.BarsPlot(_barArea), pt.X, 24);
            }
            else if (_heatArea.Width > 0 && _heatArea.Contains(pt))
            {
                Rectangle plot = Charts.HeatPlot(_heatArea);
                int col = IndexFromX(plot, pt.X, 24);
                int row = IndexFromY(plot, pt.Y, 7);
                kind = 2;
                idx = row * 24 + col;
            }
            else if (_trendArea.Width > 0 && _trendArea.Contains(pt))
            {
                Rectangle plot = Charts.TrendPlot(_trendArea);
                kind = 3;
                idx = TrendIndex(plot, pt.X, Snap.Last30Days.Length);
            }
            else
            {
                for (int i = 0; i < _appRows.Count; i++)
                {
                    if (_appRows[i].Rect.Contains(pt)) { kind = 4; idx = i; break; }
                }
            }
            if (kind != _hoverKind || idx != _hoverIdx)
            {
                // 图表 hover（1..3）只重画叠加层；应用行高亮画在静态层里（kind 4）
                bool needsLayer = kind == 4 || _hoverKind == 4;
                _hoverKind = kind;
                _hoverIdx = idx;
                if (needsLayer) Invalidate();
                else InvalidateOverlay();
            }
        }

        private static int IndexFromX(Rectangle plot, int x, int count)
        {
            if (count <= 1) return 0;
            int i = (int)((x - plot.X) * count / (double)Math.Max(1, plot.Width));
            if (i < 0) i = 0;
            if (i > count - 1) i = count - 1;
            return i;
        }

        private static int IndexFromY(Rectangle plot, int y, int count)
        {
            if (count <= 1) return 0;
            int i = (int)((y - plot.Y) * count / (double)Math.Max(1, plot.Height));
            if (i < 0) i = 0;
            if (i > count - 1) i = count - 1;
            return i;
        }

        private static int TrendIndex(Rectangle plot, int x, int len)
        {
            if (len <= 1) return 0;
            double dx = plot.Width / (double)(len - 1);
            int i = (int)Math.Round((x - plot.X) / dx);
            if (i < 0) i = 0;
            if (i > len - 1) i = len - 1;
            return i;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverKind != 0)
            {
                bool needsLayer = _hoverKind == 4;
                _hoverKind = 0;
                _hoverIdx = -1;
                if (needsLayer) Invalidate();
                else InvalidateOverlay();
            }
        }

        private void DrawHoverTooltip(Graphics g, Palette p)
        {
            if (_hoverKind == 0 || _hoverIdx < 0) return;
            Point cur = CursorToContent();
            Rectangle bounds = new Rectangle(0, 0, ContentW, ContentH);
            if (!bounds.Contains(cur) || (cur.X == 0 && cur.Y == 0))
            {
                // Screenshot/debug: anchor near the hovered bucket
                switch (_hoverKind)
                {
                    case 1: cur = new Point(Charts.BarsPlot(_barArea).X + 24, Charts.BarsPlot(_barArea).Y + 60); break;
                    case 2: cur = new Point(Charts.HeatPlot(_heatArea).X + 40, Charts.HeatPlot(_heatArea).Y + 30); break;
                    case 3: cur = new Point(Charts.TrendPlot(_trendArea).X + 80, Charts.TrendPlot(_trendArea).Y + 40); break;
                    case 4:
                        if (_hoverIdx < _appRows.Count) cur = new Point(_appRows[_hoverIdx].Rect.X + 40, _appRows[_hoverIdx].Rect.Y + 10);
                        break;
                }
            }
            switch (_hoverKind)
            {
                case 1:
                    if (_hoverIdx < _barSlots.Length)
                    {
                        Rectangle bplot = Charts.BarsPlot(_barArea);
                        int bslot = Math.Max(1, bplot.Width / 24);
                        Ui.FillRound(g, new RectangleF(bplot.X + _hoverIdx * bslot, bplot.Y,
                            bslot, bplot.Height), 4, Ui.WithAlpha(p.Accent1, 28));
                        Charts.Tooltip(g, bounds, cur,
                            _hoverIdx.ToString("D2") + ":00 - " + ((_hoverIdx + 1) % 24).ToString("D2") + ":00",
                            Charts.Number(Snap.Range.Hours[_hoverIdx]) + " 次", p);
                    }
                    break;
                case 2:
                    if (_hoverIdx < 168)
                    {
                        int d = _hoverIdx / 24;
                        int h = _hoverIdx % 24;
                        string[] days = new string[] { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };
                        Charts.Tooltip(g, bounds, cur, days[d] + " " + h.ToString("D2") + ":00",
                            Charts.Number(Snap.Range.WeekdayHour[d, h]) + " 次", p);
                    }
                    break;
                case 3:
                    if (_hoverIdx < Snap.Last30Days.Length)
                    {
                        long v = Snap.Last30Days[_hoverIdx];
                        int total = Snap.Last30Days.Length;
                        DateTime date = DateTime.Today.AddDays(-(total - 1 - _hoverIdx));
                        Charts.Tooltip(g, bounds, cur, date.ToString("MM-dd") + "（趋势）",
                            Charts.Number(v) + " 次", p);
                    }
                    break;
                case 4:
                    if (_hoverIdx < _appRows.Count)
                    {
                        AppRow r = _appRows[_hoverIdx];
                        double pct = _appTotalSeconds > 0 ? r.Seconds * 100.0 / _appTotalSeconds : 0;
                        Charts.Tooltip(g, bounds, cur, AppNames.Display(r.Name),
                            "时长 " + FormatDuration(r.Seconds) + " · 占比 " + pct.ToString("F1") + "%", p);
                    }
                    break;
            }
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

        /// <summary>Screenshot-mode hover preview (tooltip styling verification).</summary>
        public void PreviewHover(int kind, int idx)
        {
            _hoverKind = kind;
            _hoverIdx = idx;
            // 需要真实光标位置才能画 tooltip；截图模式下用图表区域中心模拟
            Invalidate();
        }

        /// <summary>压测用：走真实 hover 路径（只重绘叠加层，不重建静态层）。</summary>
        internal void PreviewHoverOverlay(int kind, int idx)
        {
            _hoverKind = kind;
            _hoverIdx = idx;
            if (kind == 4) Invalidate();
            else InvalidateOverlay();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) HandleRangeClick(ToContent(e.Location));
        }
    }
}
