using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace KeyInsight.UI.Rendering
{
    /// <summary>
    /// 静态层画布：用 GDI 内存 DC + 屏幕兼容位图承载整页内容。
    /// 相比 Graphics.FromImage(32bppRgb)，屏幕兼容 DC 走的是与真实窗口一致的
    /// GDI/GDI+ 快路径（ClearType 文本、渐变、圆角路径），整页渲染不会被拖慢；
    /// 命中缓存时用 BitBlt 整块拷贝，避免逐图元重绘。
    /// </summary>
    internal sealed class LayerSurface : IDisposable
    {
        private const int SRCCOPY = 0x00CC0020;

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h,
            IntPtr src, int sx, int sy, int rop);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        private IntPtr _dc;
        private IntPtr _bmp;
        private IntPtr _old;
        private int _width;
        private int _height;

        public int Width { get { return _width; } }
        public int Height { get { return _height; } }
        public bool Ready { get { return _dc != IntPtr.Zero && _width > 0 && _height > 0; } }

        /// <summary>确保画布尺寸；尺寸变化时重建（旧的 GDI 对象全部释放）。</summary>
        public bool Ensure(int width, int height)
        {
            if (width <= 0 || height <= 0) return false;
            if (Ready && _width == width && _height == height) return true;

            Dispose();
            IntPtr screen = GetDC(IntPtr.Zero);
            try
            {
                _dc = CreateCompatibleDC(screen);
                _bmp = CreateCompatibleBitmap(screen, width, height);
                if (_dc == IntPtr.Zero || _bmp == IntPtr.Zero)
                {
                    Dispose();
                    return false;
                }
                _old = SelectObject(_dc, _bmp);
                _width = width;
                _height = height;
                return true;
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        /// <summary>开始绘制（调用方负责 Dispose 返回的 Graphics，之后才能 BitBlt）。</summary>
        public Graphics BeginRender()
        {
            if (!Ready) return null;
            return Graphics.FromHdc(_dc);
        }

        /// <summary>整层拷贝到目标 Graphics（跳过 GDI+ 逐图元绘制）。</summary>
        public void BlitTo(Graphics target, int width, int height)
        {
            if (!Ready || target == null) return;
            int w = Math.Min(width, _width);
            int h = Math.Min(height, _height);
            if (w <= 0 || h <= 0) return;
            IntPtr dst = target.GetHdc();
            try
            {
                BitBlt(dst, 0, 0, w, h, _dc, 0, 0, SRCCOPY);
            }
            finally
            {
                target.ReleaseHdc(dst);
            }
        }

        public void Dispose()
        {
            if (_dc != IntPtr.Zero)
            {
                if (_old != IntPtr.Zero)
                {
                    try { SelectObject(_dc, _old); } catch { }
                    _old = IntPtr.Zero;
                }
                try { DeleteDC(_dc); } catch { }
                _dc = IntPtr.Zero;
            }
            if (_bmp != IntPtr.Zero)
            {
                try { DeleteObject(_bmp); } catch { }
                _bmp = IntPtr.Zero;
            }
            _width = 0;
            _height = 0;
        }
    }
}
