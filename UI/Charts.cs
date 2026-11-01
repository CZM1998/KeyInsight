using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using KeyInsight.Stats;

namespace KeyInsight.UI
{
    public sealed class RankRow
    {
        public string Label;
        public long Value;
        public long Swallowed;
        public string Sub;
    }

    public static class Charts
    {
        public static string Number(long n)
        {
            return n.ToString("N0");
        }

        public static string Number(double n)
        {
            return n.ToString("N0");
        }

        public static void Card(Graphics g, Rectangle r, Palette p)
        {
            Ui.FillRound(g, r, 14, p.Card);
            Ui.StrokeRound(g, r, 14, p.Border);
        }

        public static void StatCard(Graphics g, Rectangle r, Palette p, string label, string value, string sub, Color accent)
        {
            Card(g, r, p);
            int pad = 18;
            Ui.FillRound(g, new RectangleF(r.X + pad, r.Y + 14, 4, 20), 2, accent);
            Ui.TextLeft(g, label, Ui.BodySmall, p.Muted, new Rectangle(r.X + pad + 12, r.Y + 11, r.Width - pad * 2 - 12, 22));
            Ui.TextLeft(g, value, Ui.CardValue, p.Text, new Rectangle(r.X + pad, r.Y + 33, r.Width - pad * 2, 32));
            if (!string.IsNullOrEmpty(sub))
            {
                Ui.TextLeftEllipsis(g, sub, Ui.Label, p.Dim,
                    new Rectangle(r.X + pad, r.Bottom - 30, r.Width - pad * 2, 18));
            }
        }

        public static void DrawBars24(Graphics g, Rectangle r, int[] hours, Palette p)
        {
            int left = 42;
            int bottom = 22;
            int top = 10;
            int right = 8;
            Rectangle plot = new Rectangle(r.X + left, r.Y + top, Math.Max(10, r.Width - left - right), Math.Max(10, r.Height - top - bottom));

            int max = 1;
            for (int i = 0; i < 24; i++) if (hours[i] > max) max = hours[i];

            using (Pen grid = new Pen(p.Grid, 1f))
            {
                for (int i = 0; i <= 2; i++)
                {
                    int y = plot.Y + (plot.Height * i / 2);
                    g.DrawLine(grid, plot.X, y, plot.Right, y);
                }
            }

            int slot = Math.Max(1, plot.Width / 24);
            int barW = Math.Max(3, slot - Math.Max(3, slot / 5));
            for (int h = 0; h < 24; h++)
            {
                int bh = (int)Math.Round(plot.Height * (hours[h] / (double)max));
                if (hours[h] > 0 && bh < 2) bh = 2;
                int x = plot.X + h * slot + (slot - barW) / 2;
                int y = plot.Bottom - bh;
                RectangleF br = new RectangleF(x, y, barW, bh);
                if (bh > 0)
                {
                    using (GraphicsPath path = Ui.RoundRect(br, Math.Min(4, barW / 2f)))
                    using (LinearGradientBrush b = new LinearGradientBrush(
                        new Rectangle(x, plot.Bottom - Math.Max(bh, 8), barW, Math.Max(bh, 8)),
                        p.Accent2, p.Accent1, 90f))
                    {
                        g.FillPath(b, path);
                    }
                }
            }

            for (int h = 0; h < 24; h += 3)
            {
                int x = plot.X + h * slot + (slot - 20) / 2;
                Ui.TextCenter(g, h.ToString(), Ui.Label, p.Dim, new Rectangle(x, plot.Bottom + 3, 20, 18));
            }

            Ui.TextRight(g, Number(max), Ui.Label, p.Dim, new Rectangle(r.X + 2, plot.Y - 8, left - 8, 16));
            Ui.TextRight(g, Number(max / 2), Ui.Label, p.Dim, new Rectangle(r.X + 2, plot.Y + plot.Height / 2 - 8, left - 8, 16));
            Ui.TextRight(g, "0", Ui.Label, p.Dim, new Rectangle(r.X + 2, plot.Bottom - 8, left - 8, 16));
        }

        public static void DrawTrend(Graphics g, Rectangle r, long[] values, Palette p)
        {
            int left = 46;
            int bottom = 20;
            int top = 10;
            int right = 10;
            Rectangle plot = new Rectangle(r.X + left, r.Y + top, Math.Max(10, r.Width - left - right), Math.Max(10, r.Height - top - bottom));

            long max = 1;
            for (int i = 0; i < values.Length; i++) if (values[i] > max) max = values[i];

            using (Pen grid = new Pen(p.Grid, 1f))
            {
                for (int i = 0; i <= 2; i++)
                {
                    int y = plot.Y + (plot.Height * i / 2);
                    g.DrawLine(grid, plot.X, y, plot.Right, y);
                }
            }

            if (values.Length < 2) return;

            PointF[] pts = new PointF[values.Length];
            float dx = plot.Width / (float)(values.Length - 1);
            for (int i = 0; i < values.Length; i++)
            {
                float x = plot.X + i * dx;
                float y = plot.Bottom - (float)(plot.Height * (values[i] / (double)max));
                pts[i] = new PointF(x, y);
            }

            using (GraphicsPath area = new GraphicsPath())
            {
                area.AddLines(pts);
                area.AddLine(pts[pts.Length - 1].X, plot.Bottom, pts[0].X, plot.Bottom);
                area.CloseFigure();
                using (LinearGradientBrush b = new LinearGradientBrush(
                    new Rectangle(plot.X, plot.Y, plot.Width, Math.Max(1, plot.Height)),
                    Ui.WithAlpha(p.Accent1, 105), Ui.WithAlpha(p.Accent1, 0), 90f))
                {
                    g.FillPath(b, area);
                }
            }

            using (Pen line = new Pen(p.Accent1, 2f))
            {
                line.LineJoin = LineJoin.Round;
                g.DrawLines(line, pts);
            }

            using (SolidBrush dot = new SolidBrush(p.Accent3))
            {
                g.FillEllipse(dot, pts[pts.Length - 1].X - 3.5f, pts[pts.Length - 1].Y - 3.5f, 7f, 7f);
            }

            int step = Math.Max(1, values.Length / 6);
            for (int i = 0; i < values.Length; i += step)
            {
                Ui.TextCenter(g, (i + 1).ToString(), Ui.Label, p.Dim, new Rectangle((int)(plot.X + i * dx) - 14, plot.Bottom + 2, 28, 16));
            }

            Ui.TextRight(g, Number(max), Ui.Label, p.Dim, new Rectangle(r.X + 2, plot.Y - 8, left - 8, 16));
            Ui.TextRight(g, "0", Ui.Label, p.Dim, new Rectangle(r.X + 2, plot.Bottom - 8, left - 8, 16));
        }

        public static void DrawHeat7x24(Graphics g, Rectangle r, int[,] grid, Palette p)
        {
            int left = 28;
            int top = 18;
            int right = 4;
            int bottom = 4;
            Rectangle plot = new Rectangle(r.X + left, r.Y + top, Math.Max(10, r.Width - left - right), Math.Max(10, r.Height - top - bottom));

            int max = 1;
            for (int d = 0; d < 7; d++)
                for (int h = 0; h < 24; h++)
                    if (grid[d, h] > max) max = grid[d, h];

            float cw = plot.Width / 24f;
            float ch = plot.Height / 7f;
            float gap = 2f;

            for (int d = 0; d < 7; d++)
            {
                for (int h = 0; h < 24; h++)
                {
                    double t = Math.Log(1 + grid[d, h]) / Math.Log(1 + max);
                    Color c = p.HeatColor(t);
                    RectangleF cell = new RectangleF(plot.X + h * cw + gap / 2, plot.Y + d * ch + gap / 2,
                        Math.Max(2, cw - gap), Math.Max(2, ch - gap));
                    Ui.FillRound(g, cell, 3f, c);
                }
            }

            string[] days = new string[] { "一", "二", "三", "四", "五", "六", "日" };
            for (int d = 0; d < 7; d++)
            {
                Ui.TextRight(g, days[d], Ui.Label, p.Dim,
                    new Rectangle(r.X, (int)(plot.Y + d * ch), left - 6, (int)ch));
            }

            for (int h = 0; h < 24; h += 3)
            {
                int x = (int)(plot.X + h * cw);
                Ui.TextCenter(g, h.ToString(), Ui.Label, p.Dim, new Rectangle(x - 10, r.Y, 30, 16));
            }
        }

        public static int DrawRankList(Graphics g, Rectangle r, List<RankRow> rows, Palette p, Font font)
        {
            if (rows == null || rows.Count == 0) return 0;
            long max = 1;
            for (int i = 0; i < rows.Count; i++) if (rows[i].Value > max) max = rows[i].Value;

            int rowH = Math.Min(32, Math.Max(22, r.Height / rows.Count));
            int count = Math.Min(rows.Count, r.Height / Math.Max(1, rowH));
            for (int i = 0; i < count; i++)
            {
                RankRow row = rows[i];
                int y = r.Y + i * rowH;
                Rectangle numRect = new Rectangle(r.X, y, 26, rowH);
                Ui.TextLeft(g, (i + 1).ToString(), Ui.Mono, p.Dim, numRect);

                int labelW = r.Width - 26 - 84;
                string label = row.Label ?? "";
                Ui.TextLeftEllipsis(g, label, font, p.Text, new Rectangle(r.X + 26, y, labelW, rowH));

                string val = Number(row.Value);
                if (row.Swallowed > 0) val += "  ·  " + row.Swallowed + " 拦截";
                Ui.TextRight(g, val, Ui.Label, p.Muted, new Rectangle(r.Right - 170, y, 170, rowH));

                RectangleF barBg = new RectangleF(r.X + 26, y + rowH - 5, Math.Max(4, r.Width - 30), 3f);
                Ui.FillRound(g, barBg, 1.5f, p.CardAlt);
                float w = (float)((r.Width - 30) * (row.Value / (double)max));
                if (w < 2 && row.Value > 0) w = 2;
                if (w > 0)
                {
                    Ui.GradientRound(g, new RectangleF(barBg.X, barBg.Y, w, 3f), 1.5f, p.Accent2, p.Accent1, 0f);
                }
            }
            return count;
        }

        public static void DrawMiniBars(Graphics g, Rectangle r, int[] hours, Palette p)
        {
            int max = 1;
            for (int i = 0; i < 24; i++) if (hours[i] > max) max = hours[i];
            int slot = Math.Max(2, r.Width / 24);
            int barW = Math.Max(2, slot - 2);
            for (int h = 0; h < 24; h++)
            {
                int bh = (int)Math.Round(r.Height * (hours[h] / (double)max));
                if (hours[h] > 0 && bh < 2) bh = 2;
                RectangleF br = new RectangleF(r.X + h * slot, r.Bottom - bh, barW, bh);
                using (LinearGradientBrush b = new LinearGradientBrush(
                    new Rectangle((int)br.X, r.Bottom - Math.Max(bh, 6), (int)br.Width, Math.Max(bh, 6)),
                    p.Accent2, p.Accent1, 90f))
                {
                    g.FillRectangle(b, br);
                }
            }
        }

        public static Dictionary<KeyDef, RectangleF> DrawKeyboard(Graphics g, Rectangle area, Dictionary<int, KeyAgg> counts, Palette p, KeyDef hover, KeyLayoutDef layout)
        {
            Dictionary<KeyDef, RectangleF> hits = new Dictionary<KeyDef, RectangleF>();
            float unit = Math.Min(area.Width / (layout.WidthUnits + 0.45f), area.Height / (KeyLayout.HeightUnits + 0.6f));
            if (unit > 54f) unit = 54f;
            float layoutW = layout.WidthUnits * unit;
            float layoutH = KeyLayout.HeightUnits * unit;
            float ox = area.X + (area.Width - layoutW) / 2f;
            float oy = area.Y + (area.Height - layoutH) / 2f;
            float gap = unit * 0.09f;

            long max = 1;
            long maxSw = 1;
            List<KeyDef> keys = layout.Keys;
            for (int i = 0; i < keys.Count; i++)
            {
                long n = KeyLayout.CountFor(keys[i], counts);
                if (n > max) max = n;
                long sw = KeyLayout.SwallowedFor(keys[i], counts);
                if (sw > maxSw) maxSw = sw;
            }

            for (int i = 0; i < keys.Count; i++)
            {
                KeyDef key = keys[i];
                long n = KeyLayout.CountFor(key, counts);
                double t = n <= 0 ? 0 : Math.Log(1 + n) / Math.Log(1 + max);
                Color fill = n <= 0 ? p.HeatEmpty : p.HeatColor(t);
                Color fg = n <= 0 ? p.Dim : p.TextOn(fill);

                RectangleF rec = new RectangleF(
                    ox + key.X * unit + gap / 2f,
                    oy + key.Y * unit + gap / 2f,
                    key.W * unit - gap,
                    key.H * unit - gap);
                hits[key] = rec;

                Ui.FillRound(g, rec, Math.Max(4, unit * 0.16f), fill);
                bool isHover = hover == key;
                if (isHover)
                {
                    Ui.StrokeRound(g, rec, Math.Max(4, unit * 0.16f), p.Accent1, 2f);
                }
                else if (n > 0)
                {
                    Ui.StrokeRound(g, rec, Math.Max(4, unit * 0.16f), Ui.WithAlpha(fg, 26), 1f);
                }
                else
                {
                    Ui.StrokeRound(g, rec, Math.Max(4, unit * 0.16f), p.Border, 1f);
                }

                Font font = key.SmallLabel ? Ui.KeyFontSmall : Ui.KeyFont;
                Ui.TextCenter(g, key.Label, font, fg, Rectangle.Round(rec));

                long sw = KeyLayout.SwallowedFor(key, counts);
                if (sw > 0)
                {
                    double ts = Math.Log(1 + sw) / Math.Log(1 + maxSw);
                    Color dot = Ui.Blend(Color.FromArgb(252, 165, 165), Color.FromArgb(153, 27, 27), (float)ts);
                    int rad = 2 + (int)Math.Round(ts * 2.0);
                    float cx = rec.Right - rad - 3.5f;
                    float cy = rec.Y + rad + 3.5f;
                    using (SolidBrush ring = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                    {
                        g.FillEllipse(ring, cx - rad - 1, cy - rad - 1, (rad + 1) * 2f, (rad + 1) * 2f);
                    }
                    using (SolidBrush b = new SolidBrush(dot))
                    {
                        g.FillEllipse(b, cx - rad, cy - rad, rad * 2f, rad * 2f);
                    }
                }
            }

            return hits;
        }

        public static void Chip(Graphics g, Rectangle r, string text, bool selected, Palette p, Font font)
        {
            Color bg = selected ? p.Selected : p.CardAlt;
            Color fg = selected ? p.Accent1 : p.Muted;
            Ui.FillRound(g, r, r.Height / 2f, bg);
            if (selected) Ui.StrokeRound(g, r, r.Height / 2f, Ui.WithAlpha(p.Accent1, 95), 1f);
            else Ui.StrokeRound(g, r, r.Height / 2f, p.Border, 1f);
            Ui.TextCenter(g, text, font, fg, r);
        }

        public static void Switch(Graphics g, Rectangle r, bool on, Palette p)
        {
            Color track = on ? p.Accent2 : p.BorderStrong;
            Ui.FillRound(g, r, r.Height / 2f, track);
            int pad = 2;
            int d = r.Height - pad * 2;
            int x = on ? r.Right - d - pad : r.X + pad;
            using (SolidBrush b = new SolidBrush(Color.White))
            {
                g.FillEllipse(b, x, r.Y + pad, d, d);
            }
        }

        public static void Button(Graphics g, Rectangle r, string text, bool primary, Palette p)
        {
            Color bg = primary ? p.Accent2 : p.CardAlt;
            Color fg = primary ? Color.White : p.Text;
            if (!primary) Ui.StrokeRound(g, r, 8, p.Border);
            Ui.FillRound(g, r, 8, bg);
            Ui.TextCenter(g, text, Ui.Body, fg, r);
        }
    }
}
