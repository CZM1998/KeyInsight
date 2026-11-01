using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using KeyInsight.Stats;

namespace KeyInsight.UI.Pages
{
    public sealed class MousePage : PageBase
    {
        public override string Title { get { return "鼠标统计"; } }
        public override string Subtitle { get { return "鼠标点击、侧键与滚轮使用情况"; } }

        public MousePage()
        {
            Range = RangeKind.Week;
        }

        protected override void Draw(Graphics g)
        {
            Palette p = Theme.P;
            DrawHeader(g);
            DrawRangeChips(g, 0, 58);

            long today = Sum(Snap.Today.Mouse);
            long range = Sum(Snap.Range.Mouse);
            int topButton = TopButton(Snap.Range.Mouse);
            long wheel = WheelSum(Snap.Range.Mouse);

            int y = 100;
            int gap = 14;
            int cardW = (Width - gap * 3) / 4;
            int cardH = 100;

            Charts.StatCard(g, new Rectangle(0, y, cardW, cardH), p, "今日点击", Charts.Number(today),
                "左/右/中/侧键 + 滚轮", p.Accent1);
            Charts.StatCard(g, new Rectangle(cardW + gap, y, cardW, cardH), p, "范围点击", Charts.Number(range),
                "范围：" + RangeText(), p.Accent2);
            Charts.StatCard(g, new Rectangle((cardW + gap) * 2, y, cardW, cardH), p, "最常用按键",
                topButton > 0 ? MouseNames.Short(topButton) : "-",
                topButton > 0 ? Charts.Number(Count(Snap.Range.Mouse, topButton)) + " 次" : "暂无数据", p.Accent3);
            Charts.StatCard(g, new Rectangle((cardW + gap) * 3, y, cardW, cardH), p, "滚轮格数",
                Charts.Number(wheel), "上下左右滚动", p.Info);

            int contentY = y + cardH + gap;
            int leftW = (int)(Width * 0.46);
            int rightW = Width - leftW - gap;
            int midH = 276;

            SectionCard(g, new Rectangle(0, contentY, leftW, midH), "鼠标按键", "按使用频率着色");
            DrawMouse(g, new Rectangle(18, contentY + 56, leftW - 36, midH - 70), Snap.Range.Mouse, p);

            SectionCard(g, new Rectangle(leftW + gap, contentY, rightW, midH), "按键排行", "点击与滚轮次数");
            List<RankRow> rows = MouseRows(Snap.Range.Mouse);
            if (rows.Count == 0)
            {
                Ui.TextCenter(g, "这个范围内还没有鼠标数据", Ui.BodySmall, p.Dim,
                    new Rectangle(leftW + gap + 18, contentY + 90, rightW - 36, 40));
            }
            else
            {
                Charts.DrawRankList(g, new Rectangle(leftW + gap + 18, contentY + 58, rightW - 36, midH - 70),
                    rows, p, Ui.BodySmall);
            }

            int bottomY = contentY + midH + gap;
            int bottomH = Height - bottomY;
            int appsW = (int)(Width * 0.58);
            int funW = Width - appsW - gap;

            SectionCard(g, new Rectangle(0, bottomY, appsW, bottomH), "Top 应用（鼠标点击）", "按前台应用统计点击与滚轮");
            List<RankRow> apps = AppRows(Snap.Range.Apps);
            if (apps.Count == 0)
            {
                Ui.TextCenter(g, "这个范围内还没有应用数据", Ui.BodySmall, p.Dim,
                    new Rectangle(18, bottomY + 80, appsW - 36, 40));
            }
            else
            {
                Charts.DrawRankList(g, new Rectangle(18, bottomY + 58, appsW - 36, bottomH - 96),
                    apps, p, Ui.BodySmall);
            }
            Ui.TextLeftEllipsis(g,
                "Windows 只暴露 5 个鼠标键；更多侧键见键盘热力图。",
                Ui.Label, p.Dim, new Rectangle(18, bottomY + bottomH - 24, appsW - 36, 18));

            SectionCard(g, new Rectangle(appsW + gap, bottomY, funW, bottomH), "趣味换算", "按 1 格滚轮 ≈ 3 cm 估算");
            DrawFun(g, new Rectangle(appsW + gap + 18, bottomY + 56, funW - 36, bottomH - 70), p);
        }

        private void DrawFun(Graphics g, Rectangle r, Palette p)
        {
            long ticks = WheelSum(Snap.Range.Mouse);
            double meters = ticks * 0.03;
            string distance = meters >= 1000
                ? (meters / 1000.0).ToString("F1") + " km"
                : meters.ToString("F1") + " m";
            string scrollSub;
            if (ticks <= 0) scrollSub = "还没有滚轮数据";
            else if (meters < 100) scrollSub = "≈ 爬 " + Math.Max(1, (int)Math.Round(meters / 3.0)) + " 层楼";
            else if (meters < 1000) scrollSub = "≈ " + (meters / 400.0).ToString("F2") + " 个 400 米操场";
            else if (meters < 10000) scrollSub = "≈ " + (meters / 330.0).ToString("F1") + " 个埃菲尔铁塔";
            else scrollSub = "≈ 珠峰的 " + (meters / 8848.0 * 100).ToString("F1") + "%";

            long clicks = 0;
            for (int b = 1; b <= 5; b++) clicks += Count(Snap.Range.Mouse, b);
            long side = Count(Snap.Range.Mouse, MouseIds.Back) + Count(Snap.Range.Mouse, MouseIds.Forward);

            int rowH = 36;
            DrawFunRow(g, new Rectangle(r.X, r.Y, r.Width, rowH), p,
                "滚轮距离", distance, scrollSub);
            DrawFunRow(g, new Rectangle(r.X, r.Y + rowH, r.Width, rowH), p,
                "鼠标点击", Charts.Number(clicks) + " 次",
                clicks > 0 ? "≈ 每秒 2 次连点 " + Math.Max(1, (int)Math.Round(clicks / 120.0)) + " 分钟" : "还没有点击数据");
            DrawFunRow(g, new Rectangle(r.X, r.Y + rowH * 2, r.Width, rowH), p,
                "侧键 4/5", Charts.Number(side) + " 次",
                side > 0 ? "≈ " + Charts.Number(side) + " 次网页前进/后退" : "还没有侧键数据");
        }

        private static void DrawFunRow(Graphics g, Rectangle r, Palette p, string label, string value, string sub)
        {
            Ui.TextLeft(g, label, Ui.BodySmall, p.Muted, new Rectangle(r.X, r.Y, r.Width - 130, 20));
            Ui.TextRight(g, value, Ui.Section, p.Text, new Rectangle(r.Right - 150, r.Y, 150, 20));
            Ui.TextLeftEllipsis(g, sub, Ui.Label, p.Dim, new Rectangle(r.X, r.Y + 18, r.Width, 16));
        }

        private void DrawMouse(Graphics g, Rectangle r, Dictionary<int, long> mouse, Palette p)
        {
            long max = 1;
            for (int b = 1; b <= 5; b++)
            {
                long v = Count(mouse, b);
                if (v > max) max = v;
            }

            int bodyH = Math.Min(r.Height, 192);
            int bodyW = Math.Min(r.Width - 70, 128);
            RectangleF bodyRect = new RectangleF(
                r.X + (r.Width - bodyW) / 2f,
                r.Y + (r.Height - bodyH) / 2f,
                bodyW, bodyH);

            Func<float, float, PointF> at = delegate(float x, float y)
            {
                return new PointF(bodyRect.X + x * bodyRect.Width / 100f,
                                  bodyRect.Y + y * bodyRect.Height / 160f);
            };

            using (GraphicsPath body = new GraphicsPath())
            {
                body.AddBezier(at(50, 1), at(34, 1), at(24, 8), at(20, 22));
                body.AddBezier(at(20, 22), at(15, 40), at(11, 62), at(11, 86));
                body.AddBezier(at(11, 86), at(11, 112), at(15, 136), at(27, 150));
                body.AddBezier(at(27, 150), at(34, 157), at(42, 160), at(50, 160));
                body.AddBezier(at(50, 160), at(58, 157), at(66, 157), at(73, 150));
                body.AddBezier(at(73, 150), at(85, 136), at(89, 112), at(89, 86));
                body.AddBezier(at(89, 86), at(89, 62), at(85, 40), at(80, 22));
                body.AddBezier(at(80, 22), at(76, 8), at(66, 1), at(50, 1));
                body.CloseFigure();

                using (GraphicsPath shadow = (GraphicsPath)body.Clone())
                using (Matrix shift = new Matrix())
                {
                    shift.Translate(3f, 4f);
                    shadow.Transform(shift);
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
                    {
                        g.FillPath(sb, shadow);
                    }
                }

                DrawSideButton(g, at, mouse, MouseIds.Back, max, p, 84f);
                DrawSideButton(g, at, mouse, MouseIds.Forward, max, p, 116f);

                Color bodyTop = p.Dark ? Ui.Blend(p.Card, Color.White, 0.07f) : Color.White;
                Color bodyBottom = p.Dark ? Ui.Blend(p.Card, Color.Black, 0.32f) : Color.FromArgb(232, 237, 245);
                using (LinearGradientBrush bg = new LinearGradientBrush(
                    new RectangleF(bodyRect.X, bodyRect.Y, bodyRect.Width, bodyRect.Height),
                    bodyTop, bodyBottom, 90f))
                {
                    g.FillPath(bg, body);
                }

                using (GraphicsPath thumb = new GraphicsPath())
                {
                    thumb.AddBezier(at(12, 76), at(20, 88), at(21, 118), at(16, 150));
                    thumb.AddLine(at(16, 150), at(28, 150));
                    thumb.AddBezier(at(28, 150), at(35, 118), at(35, 88), at(27, 76));
                    thumb.CloseFigure();
                    using (SolidBrush tb = new SolidBrush(Ui.WithAlpha(
                        p.Dark ? Color.Black : Color.White, p.Dark ? 42 : 85)))
                    {
                        g.FillPath(tb, thumb);
                    }
                }

                GraphicsState state = g.Save();
                g.SetClip(body, CombineMode.Intersect);
                using (GraphicsPath lb = new GraphicsPath())
                {
                    lb.AddBezier(at(50, 2), at(35, 2), at(25, 8), at(21, 22));
                    lb.AddBezier(at(21, 22), at(17, 38), at(14, 56), at(14, 76));
                    lb.AddBezier(at(14, 76), at(22, 82), at(34, 84), at(48, 84));
                    lb.AddLine(at(48, 84), at(49, 2));
                    lb.CloseFigure();
                    FillKeyArea(g, lb, Count(mouse, MouseIds.Left), max, p);
                }
                using (GraphicsPath rb = new GraphicsPath())
                {
                    rb.AddBezier(at(50, 2), at(65, 2), at(75, 8), at(79, 22));
                    rb.AddBezier(at(79, 22), at(83, 38), at(86, 56), at(86, 76));
                    rb.AddBezier(at(86, 76), at(78, 82), at(66, 84), at(52, 84));
                    rb.AddLine(at(52, 84), at(51, 2));
                    rb.CloseFigure();
                    FillKeyArea(g, rb, Count(mouse, MouseIds.Right), max, p);
                }
                g.Restore(state);

                using (Pen split = new Pen(Ui.WithAlpha(p.Dark ? Color.Black : Color.White, 70), 1f))
                {
                    g.DrawLine(split, at(50, 3), at(50, 16));
                    g.DrawLine(split, at(50, 62), at(50, 84));
                }
                using (Pen seam = new Pen(Ui.WithAlpha(p.Border, 140), 1f))
                {
                    g.DrawBezier(seam, at(14, 78), at(30, 85), at(70, 85), at(86, 78));
                }

                RectangleF recess = RectBetween(at(42.5f, 13), at(57.5f, 64));
                Ui.FillRound(g, recess, 7, Ui.WithAlpha(p.Bg, 242));
                Ui.StrokeRound(g, recess, 7, p.BorderStrong);

                RectangleF wheel = RectBetween(at(45.5f, 18), at(54.5f, 60));
                long wheelCount = Count(mouse, MouseIds.Middle);
                double wt = wheelCount <= 0 ? 0 : Math.Log(1 + wheelCount) / Math.Log(1 + max);
                Color wheelBase = p.Dark ? Color.FromArgb(54, 63, 82) : Color.FromArgb(208, 214, 224);
                if (wheelCount > 0) wheelBase = Ui.Blend(wheelBase, p.HeatColor(wt), 0.40f);
                using (GraphicsPath wp = Ui.RoundRect(wheel, 5))
                using (LinearGradientBrush wb = new LinearGradientBrush(wheel,
                    Ui.Blend(wheelBase, Color.White, 0.18f),
                    Ui.Blend(wheelBase, Color.Black, 0.16f), 90f))
                {
                    g.FillPath(wb, wp);
                    using (Pen pen = new Pen(Ui.WithAlpha(p.TextOn(wheelBase), 90), 1f))
                    {
                        g.DrawPath(pen, wp);
                    }
                }
                using (Pen ridge = new Pen(Ui.WithAlpha(p.TextOn(wheelBase), 95), 1f))
                {
                    for (int i = 0; i < 3; i++)
                    {
                        float yy = wheel.Y + wheel.Height * (0.26f + i * 0.22f);
                        g.DrawLine(ridge, wheel.X + 2.5f, yy, wheel.Right - 2.5f, yy);
                    }
                }

                RectangleF dpi = RectBetween(at(46.5f, 68), at(53.5f, 74));
                Ui.FillRound(g, dpi, 2, Ui.Blend(bodyTop, bodyBottom, 0.45f));
                Ui.StrokeRound(g, dpi, 2, p.Border);

                using (Pen border = new Pen(p.BorderStrong, 1.2f))
                {
                    g.DrawPath(border, body);
                }
                using (Pen hl = new Pen(Color.FromArgb(p.Dark ? 34 : 150, 255, 255, 255), 2f))
                {
                    g.DrawBezier(hl, at(50, 4), at(33, 4), at(23, 11), at(20, 26));
                }
                using (Pen logo = new Pen(Ui.WithAlpha(p.Border, 150), 1f))
                {
                    g.DrawEllipse(logo, RectBetween(at(44, 108), at(56, 122)));
                }
                using (SolidBrush dot = new SolidBrush(Ui.WithAlpha(p.Border, 90)))
                {
                    g.FillEllipse(dot, RectBetween(at(49, 114), at(51, 116)));
                }

                DrawSideLabel(g, at, mouse, MouseIds.Back, max, "4", p, 84f);
                DrawSideLabel(g, at, mouse, MouseIds.Forward, max, "5", p, 116f);

                DrawButtonText(g, at, mouse, MouseIds.Left, 27, 42, "左键", max, p);
                DrawButtonText(g, at, mouse, MouseIds.Right, 73, 42, "右键", max, p);

                Ui.TextCenter(g, "滚轮", Ui.Label, p.Dim,
                    Rectangle.Round(RectBetween(at(38, 86), at(62, 96))));
                Ui.TextLeft(g, "侧键", Ui.Label, p.Dim,
                    new Rectangle((int)bodyRect.X - 32, (int)at(0, 82).Y, 24, 16));
            }
        }

        private static Color ButtonBase(long n, long max, Palette p)
        {
            Color neutral = p.Dark ? Ui.Blend(p.Card, Color.White, 0.06f) : Color.White;
            if (n <= 0) return neutral;
            double t = Math.Log(1 + n) / Math.Log(1 + max);
            return Ui.Blend(neutral, p.HeatColor(t), p.Dark ? 0.62f : 0.50f);
        }

        private static void FillKeyArea(Graphics g, GraphicsPath path, long n, long max, Palette p)
        {
            Color baseColor = ButtonBase(n, max, p);
            using (LinearGradientBrush bg = new LinearGradientBrush(
                path.GetBounds(),
                Ui.Blend(baseColor, Color.White, p.Dark ? 0.05f : 0.25f),
                Ui.Blend(baseColor, Color.Black, 0.12f), 90f))
            {
                g.FillPath(bg, path);
            }
            using (Pen pen = new Pen(Ui.WithAlpha(p.TextOn(baseColor), 60), 1f))
            {
                g.DrawPath(pen, path);
            }
        }

        private static void DrawButtonText(Graphics g, Func<float, float, PointF> at,
            Dictionary<int, long> mouse, int button, float cx, float cy, string label, long max, Palette p)
        {
            long n = Count(mouse, button);
            Color baseColor = ButtonBase(n, max, p);
            Color fg = p.TextOn(baseColor);
            RectangleF title = RectBetween(at(cx - 13, cy - 8), at(cx + 13, cy + 3));
            RectangleF value = RectBetween(at(cx - 13, cy + 5), at(cx + 13, cy + 16));
            Ui.TextCenter(g, label, Ui.BodySmall, fg, Rectangle.Round(title));
            Ui.TextCenter(g, Charts.Number(n), Ui.Label, fg, Rectangle.Round(value));
        }

        private static void DrawSideButton(Graphics g, Func<float, float, PointF> at,
            Dictionary<int, long> mouse, int button, long max, Palette p, float top)
        {
            Color fill = ButtonBase(Count(mouse, button), max, p);
            using (GraphicsPath tab = new GraphicsPath())
            {
                tab.AddBezier(at(14, top), at(4, top - 1), at(-10, top + 4), at(-10, top + 10));
                tab.AddLine(at(-10, top + 10), at(-10, top + 18));
                tab.AddBezier(at(-10, top + 18), at(-10, top + 24), at(2, top + 28), at(14, top + 28));
                tab.AddLine(at(14, top + 28), at(14, top));
                tab.CloseFigure();

                Ui.FillPathColor(g, tab, fill);
                Ui.StrokePath(g, tab, p.Border, 1f);
            }
        }

        private static void DrawSideLabel(Graphics g, Func<float, float, PointF> at,
            Dictionary<int, long> mouse, int button, long max, string label, Palette p, float top)
        {
            Color fill = ButtonBase(Count(mouse, button), max, p);
            RectangleF text = RectBetween(at(-7, top + 8), at(10, top + 20));
            Ui.TextCenter(g, label, Ui.KeyFontSmall, p.TextOn(fill), Rectangle.Round(text));
        }

        private static RectangleF RectBetween(PointF a, PointF b)
        {
            return new RectangleF(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
                Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        }

        private static long Count(Dictionary<int, long> mouse, int button)
        {
            long v;
            return mouse != null && mouse.TryGetValue(button, out v) ? v : 0;
        }

        private static long Sum(Dictionary<int, long> mouse)
        {
            long n = 0;
            if (mouse != null) foreach (long v in mouse.Values) n += v;
            return n;
        }

        private static long WheelSum(Dictionary<int, long> mouse)
        {
            long n = 0;
            for (int b = MouseIds.WheelUp; b <= MouseIds.WheelRight; b++) n += Count(mouse, b);
            return n;
        }

        private static int TopButton(Dictionary<int, long> mouse)
        {
            int best = 0;
            long max = 0;
            if (mouse != null)
            {
                foreach (KeyValuePair<int, long> kv in mouse)
                {
                    if (kv.Value > max) { max = kv.Value; best = kv.Key; }
                }
            }
            return best;
        }

        private static List<RankRow> MouseRows(Dictionary<int, long> mouse)
        {
            List<KeyValuePair<int, long>> list = new List<KeyValuePair<int, long>>(mouse);
            list.Sort(delegate(KeyValuePair<int, long> a, KeyValuePair<int, long> b)
            {
                if (a.Value != b.Value) return b.Value.CompareTo(a.Value);
                return a.Key.CompareTo(b.Key);
            });
            List<RankRow> rows = new List<RankRow>();
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Value <= 0) continue;
                RankRow row = new RankRow();
                row.Label = MouseNames.Full(list[i].Key);
                row.Value = list[i].Value;
                rows.Add(row);
            }
            return rows;
        }

        private static List<RankRow> AppRows(Dictionary<string, AppAgg> apps)
        {
            List<KeyValuePair<string, long>> list = new List<KeyValuePair<string, long>>();
            foreach (KeyValuePair<string, AppAgg> kv in apps)
            {
                long n = Sum(kv.Value.Mouse);
                if (n > 0) list.Add(new KeyValuePair<string, long>(kv.Key, n));
            }
            list.Sort(delegate(KeyValuePair<string, long> a, KeyValuePair<string, long> b)
            {
                if (a.Value != b.Value) return b.Value.CompareTo(a.Value);
                return string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
            });
            List<RankRow> rows = new List<RankRow>();
            for (int i = 0; i < list.Count && rows.Count < 8; i++)
            {
                RankRow row = new RankRow();
                row.Label = AppNames.Display(list[i].Key);
                row.Value = list[i].Value;
                rows.Add(row);
            }
            return rows;
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
            if (e.Button == MouseButtons.Left) HandleRangeClick(e.Location);
        }
    }
}
