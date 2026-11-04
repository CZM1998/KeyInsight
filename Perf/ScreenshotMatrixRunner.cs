using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using KeyInsight.Stats;
using KeyInsight.UI;

namespace KeyInsight.Perf
{
    /// <summary>
    /// 截图回归矩阵（PR-5）：尺寸 × 主题 × 页面，外加最小窗口滚动状态与键盘悬停/87 键特写。
    /// 仅用于回归，不改变生产行为。
    /// </summary>
    internal static class ScreenshotMatrixRunner
    {
        private static readonly string[] Names = new string[]
        {
            "overview", "keyboard", "mouse", "apps", "time", "health", "debounce", "settings",
            "advanced", "devicecenter", "displaycenter", "usb"
        };

        private static readonly Size[] Sizes = new Size[]
        {
            new Size(1000, 680),
            new Size(1200, 780),
            new Size(1440, 900),
            new Size(1600, 1000)
        };

        public static void RunAll(DashboardForm form, Config cfg, string outDir, StringBuilder log)
        {
            // 1) 兼容旧 README 的根目录截图（1200×780，深/浅各一套）
            form.Size = new Size(1200, 780);
            Application.DoEvents();
            for (int pass = 0; pass < 2; pass++)
            {
                bool dark = pass == 0;
                Theme.ForceDark = dark;
                Theme.Refresh();
                form.ApplyTheme();
                for (int i = 0; i < Names.Length; i++)
                {
                    form.SelectPage(i);
                    Application.DoEvents();
                    Thread.Sleep(50);
                    Save(form, Path.Combine(outDir, (dark ? "dark-" : "light-") + Names[i] + ".png"));
                }
            }

            // 2) 键盘悬停与 87 键特写（沿用旧文件名）
            Theme.ForceDark = true;
            Theme.Refresh();
            form.ApplyTheme();
            form.SelectPage(1);
            form.PreviewKeyboardHover("E");
            Application.DoEvents();
            Save(form, Path.Combine(outDir, "dark-keyboard-hover.png"));
            form.PreviewKeyboardHover(null);

            cfg.KeyboardLayout = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                bool dark = pass == 0;
                Theme.ForceDark = dark;
                Theme.Refresh();
                form.ApplyTheme();
                form.SelectPage(1);
                Application.DoEvents();
                Thread.Sleep(50);
                Save(form, Path.Combine(outDir, (dark ? "dark" : "light") + "-keyboard-87.png"));
            }
            cfg.KeyboardLayout = 1;

            // 3) 尺寸 × 主题 × 页面矩阵
            foreach (Size size in Sizes)
            {
                form.Size = size;
                Application.DoEvents();
                for (int pass = 0; pass < 2; pass++)
                {
                    bool dark = pass == 0;
                    Theme.ForceDark = dark;
                    Theme.Refresh();
                    form.ApplyTheme();
                    string dir = Path.Combine(outDir, dark ? "dark" : "light", size.Width + "x" + size.Height);
                    Directory.CreateDirectory(dir);
                    for (int i = 0; i < Names.Length; i++)
                    {
                        form.SelectPage(i);
                        Application.DoEvents();
                        Thread.Sleep(40);
                        Save(form, Path.Combine(dir, Names[i] + ".png"));
                    }
                }
            }

            // 4) 最小窗口 + 滚动状态（文字与图形必须一起滚动）
            form.Size = new Size(1000, 680);
            Application.DoEvents();
            Theme.ForceDark = true;
            Theme.Refresh();
            form.ApplyTheme();
            string scrollDir = Path.Combine(outDir, "dark", "1000x680-scroll");
            Directory.CreateDirectory(scrollDir);
            for (int i = 0; i < Names.Length; i++)
            {
                form.SelectPage(i);
                form.SetPageScroll(180);
                Application.DoEvents();
                Thread.Sleep(40);
                Save(form, Path.Combine(scrollDir, Names[i] + ".png"));
                form.SetPageScroll(0);
            }

            Theme.ForceDark = null;
            Theme.Refresh();
            form.ApplyTheme();
            form.Size = new Size(1200, 780);
            log.AppendLine("screenshot matrix written to " + outDir);
        }

        private static void Save(DashboardForm form, string path)
        {
            // 绘制中抛出的异常会让 DrawToBitmap 的 WM_PRINT 永不完成（表现为此处卡死），
            // 修掉根因（导航数组 6 组越界）后恢复 DrawToBitmap；窗口保持离屏。
            Application.DoEvents();
            using (Bitmap bmp = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                bmp.Save(path, ImageFormat.Png);
            }
        }
    }
}
