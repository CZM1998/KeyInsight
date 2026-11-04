using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace KeyInsight.UI
{
    public static class Ui
    {
        // ---- DPI / 缩放 ----------------------------------------------------
        // 设计坐标（1200×780）→ 物理像素。系统 DPI 决定缩放；KEYINSIGHT_UI_SCALE 仅供
        // 截图/压测覆盖（例如在 96 DPI 机器上模拟 1.25× / 1.5× 的 4K 环境）。
        public static readonly float Scale = ResolveScale();
        private static readonly float SystemDpiScale = ResolveSystemDpiScale();
        // 字体以“点”声明，GDI 已按系统 DPI 放大；这里补偿「目标缩放 − 系统 DPI」的差额，
        // 使字体像素高度与设计几何缩放后的比例一致。
        private static readonly float FontFactor = SystemDpiScale > 0.01f
            ? Scale / SystemDpiScale : 1f;

        [DllImport("user32.dll")]
        private static extern uint GetDpiForSystem();

        private static float ResolveSystemDpiScale()
        {
            try
            {
                uint dpi = GetDpiForSystem();
                if (dpi >= 48 && dpi <= 480) return dpi / 96f;
            }
            catch
            {
            }
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f;
            }
            catch
            {
            }
            return 1f;
        }

        private static float ResolveScale()
        {
            float dpi = ResolveSystemDpiScale();
            float scale = dpi;
            string env = Environment.GetEnvironmentVariable("KEYINSIGHT_UI_SCALE");
            float v;
            if (!string.IsNullOrEmpty(env) &&
                float.TryParse(env, NumberStyles.Float, CultureInfo.InvariantCulture, out v) &&
                v >= 1f && v <= 3f)
            {
                scale = v;
            }
            if (scale < 1f) scale = 1f;
            if (scale > 3f) scale = 3f;
            return scale;
        }

        /// <summary>设计像素 → 物理像素。</summary>
        public static int Px(float v)
        {
            return (int)Math.Round(v * Scale);
        }

        public static Rectangle PxRect(Rectangle r)
        {
            return new Rectangle(Px(r.X), Px(r.Y), Px(r.Width), Px(r.Height));
        }

        /// <summary>物理坐标 → 设计坐标（鼠标/滚动命中用）。</summary>
        public static Point ToDesign(Point p)
        {
            if (Scale <= 1.0001f) return p;
            return new Point((int)Math.Round(p.X / Scale), (int)Math.Round(p.Y / Scale));
        }

        public static Rectangle ToDesign(Rectangle r)
        {
            return new Rectangle(ToDesign(new Point(r.X, r.Y)),
                new Size((int)Math.Round(r.Width / Scale), (int)Math.Round(r.Height / Scale)));
        }

        /// <summary>当前 Graphics 的等比缩放（未缩放为 1）。</summary>
        public static float InkScale(Graphics g)
        {
            if (g == null) return 1f;
            try
            {
                float s = g.Transform.Elements[0];
                return s > 0.01f ? s : 1f;
            }
            catch
            {
                return 1f;
            }
        }

        /// <summary>设计矩形 → 当前 Graphics 的绘制坐标（TextRenderer 不跟随变换）。</summary>
        private static Rectangle Ink(Graphics g, Rectangle r)
        {
            float s = InkScale(g);
            if (Math.Abs(s - 1f) < 0.001f) return r;
            return new Rectangle((int)Math.Round(r.X * s), (int)Math.Round(r.Y * s),
                (int)Math.Round(r.Width * s), (int)Math.Round(r.Height * s));
        }

        public static readonly Font Body = new Font("Microsoft YaHei UI", 9.75F * FontFactor);
        public static readonly Font BodySmall = new Font("Microsoft YaHei UI", 9F * FontFactor);
        public static readonly Font Label = new Font("Microsoft YaHei UI", 8.5F * FontFactor);
        public static readonly Font Section = new Font("Microsoft YaHei UI", 11.5F * FontFactor, FontStyle.Bold);
        public static readonly Font PageTitle = new Font("Microsoft YaHei UI", 21F * FontFactor, FontStyle.Bold);
        public static readonly Font Nav = new Font("Microsoft YaHei UI", 10F * FontFactor);
        public static readonly Font NavBold = new Font("Microsoft YaHei UI", 10F * FontFactor, FontStyle.Bold);
        public static readonly Font BrandTitle = new Font("Microsoft YaHei UI", 14F * FontFactor, FontStyle.Bold);
        public static readonly Font AppTitle = new Font("Microsoft YaHei UI", 13F * FontFactor, FontStyle.Bold);
        public static readonly Font CardValue = new Font("Segoe UI", 24F * FontFactor, FontStyle.Bold);
        public static readonly Font CardValueSmall = new Font("Segoe UI", 18F * FontFactor, FontStyle.Bold);
        public static readonly Font CardValueTiny = new Font("Segoe UI", 15F * FontFactor, FontStyle.Bold);
        public static readonly Font KeyFont = new Font("Microsoft YaHei UI", 7.8F * FontFactor, FontStyle.Bold);
        public static readonly Font KeyFontSmall = new Font("Microsoft YaHei UI", 6.8F * FontFactor, FontStyle.Bold);
        public static readonly Font Mono = new Font("Consolas", 9F * FontFactor);
        public static readonly Font Glyph = new Font("Segoe MDL2 Assets", 11F * FontFactor);
        public static readonly Font GlyphSmall = new Font("Segoe MDL2 Assets", 9F * FontFactor);

        public static void Prep(Graphics g)
        {
            // PR-2：几何启用抗锯齿；不全局启用 HighQualityBicubic（仅位图缩放需要，
            // 由具体绘制点按需设置）。文字走 TextRenderer，不受 Graphics 设置影响。
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
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
            {
                g.FillPath(RenderResources.Brush(color), path);
            }
        }

        public static void FillPathColor(Graphics g, GraphicsPath path, Color color)
        {
            g.FillPath(RenderResources.Brush(color), path);
        }

        public static void StrokePath(Graphics g, GraphicsPath path, Color color, float width)
        {
            g.DrawPath(RenderResources.Pen(color, width), path);
        }

        public static void StrokeRound(Graphics g, RectangleF r, float radius, Color color)
        {
            StrokeRound(g, r, radius, color, 1f);
        }

        public static void StrokeRound(Graphics g, RectangleF r, float radius, Color color, float width)
        {
            using (GraphicsPath path = RoundRect(r, radius))
            {
                g.DrawPath(RenderResources.Pen(color, width), path);
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
            TextRenderer.DrawText(g, text, font, Ink(g, r), color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        public static void TextLeftEllipsis(Graphics g, string text, Font font, Color color, Rectangle r)
        {
            TextRenderer.DrawText(g, text, font, Ink(g, r), color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
                TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }

        /// <summary>放得下就用普通绘制（保持基线一致），放不下才省略号截断。</summary>
        public static void TextLeftFit(Graphics g, string text, Font font, Color color, Rectangle r)
        {
            if (MeasureWidth(g, text, font) <= r.Width - 1) TextLeft(g, text, font, color, r);
            else TextLeftEllipsis(g, text, font, color, r);
        }

        public static void TextCenter(Graphics g, string text, Font font, Color color, Rectangle r)
        {
            TextRenderer.DrawText(g, text, font, Ink(g, r), color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }

        public static void TextRight(Graphics g, string text, Font font, Color color, Rectangle r)
        {
            TextRenderer.DrawText(g, text, font, Ink(g, r), color,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
                TextFormatFlags.SingleLine);
        }

        public static Size Measure(Graphics g, string text, Font font)
        {
            Size raw = TextRenderer.MeasureText(g, text, font, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            float s = InkScale(g);
            if (Math.Abs(s - 1f) < 0.001f) return raw;
            return new Size((int)Math.Round(raw.Width / s), (int)Math.Round(raw.Height / s));
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
            g.FillEllipse(RenderResources.Brush(color), cx - radius, cy - radius, radius * 2, radius * 2);
        }
    }
}
