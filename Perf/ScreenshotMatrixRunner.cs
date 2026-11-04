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

            // 1.5) Time-page chart hover preview (tooltip styling, both themes)
            for (int pass = 0; pass < 2; pass++)
            {
                bool dark = pass == 0;
                Theme.ForceDark = dark;
                Theme.Refresh();
                form.ApplyTheme();
                form.SelectPage(4);
                Application.DoEvents();
                KeyInsight.UI.Pages.TimePage tp = form.FindPageForPreview(4) as KeyInsight.UI.Pages.TimePage;
                if (tp != null)
                {
                    tp.PreviewHover(1, 10);
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(30);
                    Save(form, Path.Combine(outDir, (dark ? "dark" : "light") + "-time-hover.png"));
                    tp.PreviewHover(0, -1);
                }
            }

            // 1.6) Logo 掉落彩蛋：定格一帧下落中的 Logo（两主题）
            for (int pass = 0; pass < 2; pass++)
            {
                bool dark = pass == 0;
                Theme.ForceDark = dark;
                Theme.Refresh();
                form.ApplyTheme();
                form.SelectPage(0);
                Application.DoEvents();
                form.PreviewLogoFall(360f, -35f);
                Save(form, Path.Combine(outDir, (dark ? "dark" : "light") + "-logo-fall.png"));
                form.PreviewLogoReset();
            }

            // 1.7) Logo 连点彩蛋：真实走 OnMouseDown 命中 + 动画定时器 + 自动归位
            Theme.ForceDark = true;
            Theme.Refresh();
            form.ApplyTheme();
            form.SelectPage(0);
            form.Size = new Size(1200, 780);
            Application.DoEvents();
            SendLogoClicks(form, 5);
            Pump(230);
            Save(form, Path.Combine(outDir, "dark-logo-drop.png"));
            Pump(320);
            Save(form, Path.Combine(outDir, "dark-logo-drop2.png"));
            Pump(5000);
            Save(form, Path.Combine(outDir, "dark-logo-restored.png"));

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

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        /// <summary>边泵消息边等待：WinForms Timer 只在消息循环里推进，Thread.Sleep 不会走动画。</summary>
        private static void Pump(int ms)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end)
            {
                Application.DoEvents();
                Thread.Sleep(5);
            }
        }

        /// <summary>向标题栏 Logo 发送真实鼠标按下/抬起消息，走 OnMouseDown 的彩蛋计数。</summary>
        private static void SendLogoClicks(Form form, int count)
        {
            IntPtr lp = (IntPtr)((29 << 16) | 33);   // 客户端坐标 (33,29)：Logo 中心
            for (int i = 0; i < count; i++)
            {
                SendMessage(form.Handle, 0x0201, (IntPtr)1, lp);   // WM_LBUTTONDOWN
                SendMessage(form.Handle, 0x0202, IntPtr.Zero, lp); // WM_LBUTTONUP
                Application.DoEvents();
                Thread.Sleep(90);
            }
        }
    }
}
