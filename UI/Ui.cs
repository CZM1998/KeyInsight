using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace KeyInsight.UI
{
    public static class Ui
    {
        public static readonly Font Body = new Font("Microsoft YaHei UI", 9F);
        public static readonly Font BodySmall = new Font("Microsoft YaHei UI", 8.25F);
        public static readonly Font Label = new Font("Microsoft YaHei UI", 8F);
        public static readonly Font Section = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold);
        public static readonly Font PageTitle = new Font("Microsoft YaHei UI", 19F, FontStyle.Bold);
        public static readonly Font Nav = new Font("Microsoft YaHei UI", 10F);
        public static readonly Font NavBold = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
        public static readonly Font BrandTitle = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold);
        public static readonly Font AppTitle = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold);
        public static readonly Font CardValue = new Font("Segoe UI", 20F, FontStyle.Bold);
        public static readonly Font CardValueSmall = new Font("Segoe UI", 16F, FontStyle.Bold);
        public static readonly Font KeyFont = new Font("Microsoft YaHei UI", 7.8F, FontStyle.Bold);
        public static readonly Font KeyFontSmall = new Font("Microsoft YaHei UI", 6.8F, FontStyle.Bold);
        public static readonly Font Mono = new Font("Consolas", 8.5F);
        public static readonly Font Glyph = new Font("Segoe MDL2 Assets", 11F);
        public static readonly Font GlyphSmall = new Font("Segoe MDL2 Assets", 9F);

        public static void Prep(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        }

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float d = Math.Max(0, radius * 2);
            if (d <= 0.5f)
            {
                path.AddRectangle(r);
                return path;
            }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void FillRound(Graphics g, RectangleF r, float radius, Color color)
        {
            using (GraphicsPath path = RoundRect(r, radius))
            using (SolidBrush b = new SolidBrush(color))
            {
                g.FillPath(b, path);
            }
        }

        public static void FillPathColor(Graphics g, GraphicsPath path, Color color)
        {
            using (SolidBrush b = new SolidBrush(color))
            {
                g.FillPath(b, path);
            }
        }

        public static void StrokePath(Graphics g, GraphicsPath path, Color color, float width)
        {
            using (Pen p = new Pen(color, width))
            {
                g.DrawPath(p, path);
            }
        }

        public static void StrokeRound(Graphics g, RectangleF r, float radius, Color color)
        {
            StrokeRound(g, r, radius, color, 1f);
        }

        public static void StrokeRound(Graphics g, RectangleF r, float radius, Color color, float width)
        {
            using (GraphicsPath path = RoundRect(r, radius))
            using (Pen p = new Pen(color, width))
            {
                g.DrawPath(p, path);
            }
        }

        public static void GradientRect(Graphics g, RectangleF r, Color a, Color b, float angle)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (LinearGradientBrush br = new LinearGradientBrush(r, a, b, angle))
            {
                g.FillRectangle(br, r);
            }
        }

        public static void GradientRound(Graphics g, RectangleF r, float radius, Color a, Color b, float angle)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (GraphicsPath path = RoundRect(r, radius))
            using (LinearGradientBrush br = new LinearGradientBrush(r, a, b, angle))
            {
                g.FillPath(br, path);
            }
        }

        public static void TextLeft(Graphics g, string text, Font font, Color color, Rectangle r)
        {
            TextRenderer.DrawText(g, text, font, r, color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        public static void TextLeftEllipsis(Graphics g, string text, Font font, Color color, Rectangle r)
        {
            TextRenderer.DrawText(g, text, font, r, color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
                TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }

        public static void TextCenter(Graphics g, string text, Font font, Color color, Rectangle r)
        {
            TextRenderer.DrawText(g, text, font, r, color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }

        public static void TextRight(Graphics g, string text, Font font, Color color, Rectangle r)
        {
            TextRenderer.DrawText(g, text, font, r, color,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
                TextFormatFlags.SingleLine);
        }

        public static Size Measure(Graphics g, string text, Font font)
        {
            return TextRenderer.MeasureText(g, text, font, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }

        public static int MeasureWidth(Graphics g, string text, Font font)
        {
            return Measure(g, text, font).Width;
        }

        public static Color Blend(Color a, Color b, float t)
        {
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            return Color.FromArgb(
                (int)(a.A + ((b.A - a.A) * t)),
                (int)(a.R + ((b.R - a.R) * t)),
                (int)(a.G + ((b.G - a.G) * t)),
                (int)(a.B + ((b.B - a.B) * t)));
        }

        public static Color WithAlpha(Color c, int alpha)
        {
            return Color.FromArgb(alpha, c.R, c.G, c.B);
        }

        public static int S(Graphics g, int value)
        {
            return (int)Math.Round(value * g.DpiX / 96.0);
        }

        public static float S(Graphics g, float value)
        {
            return (float)(value * g.DpiX / 96.0);
        }

        public static void Pill(Graphics g, Rectangle r, Color bg, Color fg, string text, Font font)
        {
            FillRound(g, r, r.Height / 2f, bg);
            TextCenter(g, text, font, fg, r);
        }

        public static void SectionTitle(Graphics g, string text, int x, int y, Palette p)
        {
            TextLeft(g, text, Section, p.Text, new Rectangle(x, y, 420, 24));
        }

        public static void Divider(Graphics g, int x, int y, int width, Palette p)
        {
            using (Pen pen = new Pen(p.Border, 1f))
            {
                g.DrawLine(pen, x, y, x + width, y);
            }
        }

        public static void DrawDot(Graphics g, int cx, int cy, int radius, Color color)
        {
            using (SolidBrush b = new SolidBrush(color))
            {
                g.FillEllipse(b, cx - radius, cy - radius, radius * 2, radius * 2);
            }
        }
    }
}
