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
        // PR-2：键盘几何缓存（命中区域与绘制矩形同源）。键 = 布局名 + 绘制区域 + DPI。
        private sealed class KeyGeometry
        {
            public Dictionary<KeyDef, RectangleF> Rects;
        }

        private static readonly Dictionary<string, KeyGeometry> _geomCache =
            new Dictionary<string, KeyGeometry>(StringComparer.Ordinal);

        private static KeyGeometry GetGeometry(Graphics g, Rectangle area, KeyLayoutDef layout)
        {
            string key = layout.Name + "|" + area.X + "," + area.Y + "," + area.Width + "," + area.Height +
                "|" + Math.Round(g.DpiX);
            KeyGeometry geom;
            if (_geomCache.TryGetValue(key, out geom)) return geom;

            if (_geomCache.Count > 6) _geomCache.Clear();
            geom = new KeyGeometry();
            geom.Rects = new Dictionary<KeyDef, RectangleF>(layout.Keys.Count);
            float unit = Math.Min(area.Width / (layout.WidthUnits + 0.45f), area.Height / (KeyLayout.HeightUnits + 0.6f));
            if (unit > 54f) unit = 54f;
            float layoutW = layout.WidthUnits * unit;
            float layoutH = KeyLayout.HeightUnits * unit;
            float ox = area.X + (area.Width - layoutW) / 2f;
            float oy = area.Y + (area.Height - layoutH) / 2f;
            float gap = unit * 0.09f;
            for (int i = 0; i < layout.Keys.Count; i++)
            {
                KeyDef k = layout.Keys[i];
                geom.Rects[k] = new RectangleF(
                    ox + k.X * unit + gap / 2f,
                    oy + k.Y * unit + gap / 2f,
                    k.W * unit - gap,
                    k.H * unit - gap);
            }
            _geomCache[key] = geom;
            return geom;
        }

        /// <summary>主题/DPI 变化后失效几何缓存。</summary>
        public static void InvalidateGeometry()
        {
            _geomCache.Clear();
        }
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
            Ui.FillRound(g, r, 10, p.Card);
            Ui.StrokeRound(g, r, 10, p.Border);
        }

        public static void StatCard(Graphics g, Rectangle r, Palette p, string label, string value, string sub, Color accent)
        {
            Card(g, r, p);
            int pad = 18;
            Ui.FillRound(g, new RectangleF(r.X + pad, r.Y + 14, 4, 20), 2, accent);
            Ui.TextLeft(g, label, Ui.BodySmall, p.Muted, new Rectangle(r.X + pad + 12, r.Y + 11, r.Width - pad * 2 - 12, 22));
            Ui.TextLeft(g, value, Ui.CardValue, p.Text, new Rectangle(r.X + pad, r.Y + 30, r.Width - pad * 2, 38));
            if (!string.IsNullOrEmpty(sub))
            {
                Ui.TextLeftEllipsis(g, sub, Ui.Label, p.Dim,
                    new Rectangle(r.X + pad, r.Bottom - 30, r.Width - pad * 2, 18));
            }
        }

        public static void DrawBars24(Graphics g, Rectangle r, int[] hours, Palette p)
        {
            DrawBars24(g, r, hours, p, null);
        }

        /// <summary>带命中槽位的柱状图（hover 数据检查）；hitSlots[h] = 该小时整列矩形。</summary>
        public static void DrawBars24(Graphics g, Rectangle r, int[] hours, Palette p,
            Rectangle[] hitSlots)
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
                if (hitSlots != null && h < hitSlots.Length)
                {
                    hitSlots[h] = new Rectangle(plot.X + h * slot, plot.Y, slot, plot.Height);
                }
                int bh = (int)Math.Round(plot.Height * (hours[h] / (double)max));
                if (hours[h] > 0 && bh < 2) bh = 2;
                int x = plot.X + h * slot + (slot - barW) / 2;
                int y = plot.Bottom - bh;
                RectangleF br = new RectangleF(x, y, barW, bh);
                if (bh > 0)
                {
                    using (GraphicsPath path = Ui.RoundRect(br, Math.Min(4, barW / 2f)))
                    {
                        g.FillPath(RenderResources.Brush(p.Accent1), path);
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
            DrawTrend(g, r, values, p, null);
        }

        /// <summary>带命中槽位的趋势图；hitSlots[i] = 第 i 个点的竖直条带。</summary>
        public static void DrawTrend(Graphics g, Rectangle r, long[] values, Palette p,
            Rectangle[] hitSlots)
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
                if (hitSlots != null && i < hitSlots.Length)
                {
                    hitSlots[i] = new Rectangle((int)(plot.X + i * dx - dx / 2), plot.Y,
                        Math.Max(3, (int)dx), plot.Height);
                }
                float x = plot.X + i * dx;
                float y = plot.Bottom - (float)(plot.Height * (values[i] / (double)max));
                pts[i] = new PointF(x, y);
            }

            using (GraphicsPath area = new GraphicsPath())
            {
                area.AddLines(pts);
                area.AddLine(pts[pts.Length - 1].X, plot.Bottom, pts[0].X, plot.Bottom);
                area.CloseFigure();
                g.FillPath(RenderResources.Brush(Ui.WithAlpha(p.Accent1, 40)), area);
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
            DrawHeat7x24(g, r, grid, p, null);
        }

        /// <summary>带命中单元的热力图；hitCells[d*24+h] = 单元矩形。</summary>
        public static void DrawHeat7x24(Graphics g, Rectangle r, int[,] grid, Palette p,
            Rectangle[] hitCells)
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
                    if (hitCells != null && d * 24 + h < hitCells.Length)
                    {
                        hitCells[d * 24 + h] = Rectangle.Round(cell);
                    }
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
                g.FillRectangle(RenderResources.Brush(p.Accent1), br);
            }
        }

        /// <summary>
        /// P1-03 图层版热力图：按任意指标字典（使用/候选/拦截）着色，不画红点。
        /// values 以 VK 为键，多 VK 的键（左右 Shift 等）求和。
        /// </summary>
        public static Dictionary<KeyDef, RectangleF> DrawKeyboardValues(Graphics g, Rectangle area,
            Dictionary<int, long> values, Palette p, KeyDef hover, KeyLayoutDef layout)
        {
            KeyGeometry geom = GetGeometry(g, area, layout);
            Dictionary<KeyDef, RectangleF> hits = geom.Rects;
            float unit = Math.Min(area.Width / (layout.WidthUnits + 0.45f), area.Height / (KeyLayout.HeightUnits + 0.6f));
            if (unit > 54f) unit = 54f;

            long max = 1;
            List<KeyDef> keys = layout.Keys;
            for (int i = 0; i < keys.Count; i++)
            {
                long n = ValueFor(keys[i], values);
                if (n > max) max = n;
            }

            for (int i = 0; i < keys.Count; i++)
            {
                KeyDef key = keys[i];
                long n = ValueFor(key, values);
                double t = n <= 0 ? 0 : Math.Log(1 + n) / Math.Log(1 + max);
                Color fill = n <= 0 ? p.HeatEmpty : p.HeatColor(t);
                Color fg = n <= 0 ? p.Dim : p.TextOn(fill);

                RectangleF rec = hits[key];

                Ui.FillRound(g, rec, Math.Max(4, unit * 0.16f), fill);
                bool isHover = hover == key;
                if (isHover) Ui.StrokeRound(g, rec, Math.Max(4, unit * 0.16f), p.Accent1, 2f);
                else if (n > 0) Ui.StrokeRound(g, rec, Math.Max(4, unit * 0.16f), Ui.WithAlpha(fg, 26), 1f);
                else Ui.StrokeRound(g, rec, Math.Max(4, unit * 0.16f), p.Border, 1f);

                Font font = key.SmallLabel ? Ui.KeyFontSmall : Ui.KeyFont;
                Ui.TextCenter(g, key.Label, font, fg, Rectangle.Round(rec));
            }
            return hits;
        }

        private static long ValueFor(KeyDef key, Dictionary<int, long> values)
        {
            if (values == null) return 0;
            long n = 0;
            for (int i = 0; i < key.Vks.Length; i++)
            {
                long v;
                if (values.TryGetValue(key.Vks[i], out v)) n += v;
            }
            return n;
        }

        public static Dictionary<KeyDef, RectangleF> DrawKeyboard(Graphics g, Rectangle area, Dictionary<int, KeyAgg> counts, Palette p, KeyDef hover, KeyLayoutDef layout)
        {
            KeyGeometry geom = GetGeometry(g, area, layout);
            Dictionary<KeyDef, RectangleF> hits = geom.Rects;
            float unit = Math.Min(area.Width / (layout.WidthUnits + 0.45f), area.Height / (KeyLayout.HeightUnits + 0.6f));
            if (unit > 54f) unit = 54f;

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

                RectangleF rec = hits[key];

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
    
        /// <summary>
        /// 图表数据检查浮层（hover tooltip）：深色圆角卡片 + 标题/数值两行，
        /// 跟随光标并钳制在页面内容区内。由页面在 Draw 末尾调用。
        /// </summary>
        public static void Tooltip(Graphics g, Rectangle bounds, Point cursor,
            string title, string value, Palette p)
        {
            if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(value)) return;
            int tw = string.IsNullOrEmpty(title) ? 0 : Ui.MeasureWidth(g, title, Ui.BodySmall);
            int vw = string.IsNullOrEmpty(value) ? 0 : Ui.MeasureWidth(g, value, Ui.Body);
            int w = Math.Max(72, Math.Max(tw, vw) + 24);
            int h = string.IsNullOrEmpty(title) ? 32 : 48;
            int x = cursor.X + 14;
            int y = cursor.Y + 16;
            if (x + w > bounds.Right - 6) x = cursor.X - w - 10;
            if (y + h > bounds.Bottom - 6) y = cursor.Y - h - 10;
            if (x < bounds.Left) x = bounds.Left;
            if (y < bounds.Top) y = bounds.Top;
            Rectangle box = new Rectangle(x, y, w, h);
            using (GraphicsPath path = Ui.RoundRect(new RectangleF(box.X, box.Y, box.Width, box.Height), 8))
            {
                using (SolidBrush bg = new SolidBrush(Ui.WithAlpha(p.Dark ? Color.FromArgb(18, 26, 40) : Color.FromArgb(32, 42, 58), 242)))
                {
                    g.FillPath(bg, path);
                }
                using (Pen pen = new Pen(Ui.WithAlpha(p.Accent1, 200), 1f))
                {
                    g.DrawPath(pen, path);
                }
            }
            if (!string.IsNullOrEmpty(title))
            {
                Ui.TextLeft(g, title, Ui.BodySmall, p.Text, new Rectangle(box.X + 12, box.Y + 6, box.Width - 24, 18));
                Ui.TextLeft(g, value, Ui.Body, p.Accent2, new Rectangle(box.X + 12, box.Y + 24, box.Width - 24, 20));
            }
            else
            {
                Ui.TextLeft(g, value, Ui.Body, p.Text, new Rectangle(box.X + 12, box.Y + 6, box.Width - 24, 20));
            }
        }

    }
}
