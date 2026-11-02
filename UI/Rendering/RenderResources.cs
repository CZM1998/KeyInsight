using System;
using System.Collections.Generic;
using System.Drawing;

namespace KeyInsight.UI
{
    /// <summary>
    /// 绘制资源缓存（PR-2）：复用 SolidBrush / Pen，避免每次 Paint 大量创建 GDI 对象。
    /// 生命周期：主题切换、DPI 变化、窗口销毁时 DisposeAll；容量上限防颜色无限增长。
    /// 不得缓存 Graphics 或可变 GraphicsPath（Path 仍按需创建并释放）。
    /// </summary>
    public static class RenderResources
    {
        private const int MaxEntries = 384;

        private static readonly Dictionary<int, SolidBrush> _brushes = new Dictionary<int, SolidBrush>();
        private static readonly Dictionary<long, Pen> _pens = new Dictionary<long, Pen>();

        public static SolidBrush Brush(Color c)
        {
            lock (_brushes)
            {
                int key = c.ToArgb();
                SolidBrush b;
                if (_brushes.TryGetValue(key, out b)) return b;
                if (_brushes.Count > MaxEntries) DisposeAllLocked();
                b = new SolidBrush(c);
                _brushes[key] = b;
                return b;
            }
        }

        public static Pen Pen(Color c, float width)
        {
            lock (_pens)
            {
                int w = (int)Math.Round(width * 10f);
                long key = ((long)c.ToArgb() << 8) ^ (uint)w;
                Pen p;
                if (_pens.TryGetValue(key, out p)) return p;
                if (_pens.Count > MaxEntries) DisposePensLocked();
                p = new Pen(c, w / 10f);
                _pens[key] = p;
                return p;
            }
        }

        /// <summary>主题切换/DPI 变化时调用：释放全部缓存资源（下次绘制按新主题重建）。</summary>
        public static void DisposeAll()
        {
            lock (_brushes) DisposeAllLocked();
            lock (_pens) DisposePensLocked();
        }

        private static void DisposeAllLocked()
        {
            foreach (SolidBrush b in _brushes.Values)
            {
                try { b.Dispose(); } catch { }
            }
            _brushes.Clear();
        }

        private static void DisposePensLocked()
        {
            foreach (Pen p in _pens.Values)
            {
                try { p.Dispose(); } catch { }
            }
            _pens.Clear();
        }
    }
}
