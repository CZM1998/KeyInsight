using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using KeyInsight.Stats;
using KeyInsight.UI;

namespace KeyInsight.Perf
{
    /// <summary>
    /// 性能基线/回归 runner（PR-0 / PR-5）：
    /// 生成 1/12/36 个月合成 KDS 数据，测量 GetSnapshot（各页面时间范围）与
    /// 每页绘制耗时（离屏 DrawToBitmap），记录 GDI 对象与内存，输出 Markdown 报告。
    /// 仅用于诊断，不改变生产行为。
    /// </summary>
    internal static class PerfRunner
    {
        [DllImport("user32.dll")]
        private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);
        private const uint GR_GDIOBJECTS = 0;

        private static readonly int[] DataMonths = new int[] { 1, 12, 36 };
        private static readonly string[] RangeNames = new string[] { "Today", "Week", "Month", "All" };
        private static readonly string[] PageNames = new string[]
        {
            "overview", "keyboard", "mouse", "apps", "time", "health", "debounce", "settings", "advanced"
        };

        public static int Run(string outPath)
        {
            StringBuilder md = new StringBuilder();
            md.AppendLine("# KeyInsight 性能基线报告");
            md.AppendLine();
            md.AppendLine("- 生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            md.AppendLine("- 环境：" + Environment.OSVersion + " · .NET " + Environment.Version +
                " · " + Environment.ProcessorCount + " 核");
            md.AppendLine("- 方法：合成 1/12/36 个月 KDS 数据；GetSnapshot 各范围连续 5 次取 P50；" +
                "每页离屏 DrawToBitmap 3 次取 P50。");
            md.AppendLine();

            md.AppendLine("## Snapshot 构建（GetSnapshot，含锁）");
            md.AppendLine();
            md.AppendLine("| 数据量 | 范围 | P50 ms | P95 ms | 备注 |");
            md.AppendLine("|---|---|---|---|---|");

            string root = Path.Combine(Path.GetTempPath(), "KeyInsightPerf_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            StatsEngine lastEngine = null;
            string lastDir = null;
            int lastMonths = 0;
            try
            {
                foreach (int months in DataMonths)
                {
                    string dir = Path.Combine(root, months + "mo");
                    PerfDataGenerator.Generate(dir, months, 3000, 42);
                    StatsEngine engine = new StatsEngine(dir, "KeyInsight");
                    engine.LoadAllMonths();
                    engine.WaitIdle(60000);

                    DateTime[][] ranges = new DateTime[][]
                    {
                        new DateTime[] { DateTime.Today, DateTime.Today },
                        new DateTime[] { DateTime.Today.AddDays(-6), DateTime.Today },
                        new DateTime[] { DateTime.Today.AddDays(-29), DateTime.Today },
                        new DateTime[] { DateTime.MinValue, DateTime.MaxValue }
                    };

                    List<double> all = new List<double>();
                    for (int r = 0; r < ranges.Length; r++)
                    {
                        List<double> samples = new List<double>();
                        for (int i = 0; i < 5; i++)
                        {
                            Stopwatch sw = Stopwatch.StartNew();
                            StatsSnapshot snap = engine.GetSnapshot(ranges[r][0], ranges[r][1]);
                            sw.Stop();
                            samples.Add(sw.Elapsed.TotalMilliseconds);
                            all.Add(sw.Elapsed.TotalMilliseconds);
                            if (snap == null) throw new InvalidOperationException("null snapshot");
                        }
                        samples.Sort();
                        md.AppendLine("| " + months + " 个月 | " + RangeNames[r] + " | " +
                            samples[samples.Count / 2].ToString("F1") + " | " +
                            samples[samples.Count - 1].ToString("F1") + " | |");
                    }

                    if (months == 36)
                    {
                        lastEngine = engine;
                        lastDir = dir;
                        lastMonths = months;
                    }
                    else
                    {
                        engine.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                md.AppendLine();
                md.AppendLine("ERROR: " + ex.Message);
                TryWrite(outPath, md.ToString());
                return 1;
            }

            // 每页绘制耗时（36 个月数据，1200×780）
            md.AppendLine();
            md.AppendLine("## 页面绘制（离屏渲染 P50，36 个月数据，1200×780）");
            md.AppendLine();
            md.AppendLine("| 页面 | P50 ms | GDI 对象 |");
            md.AppendLine("|---|---|---|");

            DashboardForm form = null;
            Config cfg = new Config();
            cfg.FilterEnabled = true;
            string paintError = "";
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                form = new DashboardForm(lastEngine, cfg, null, null, null);
                form.ScreenshotMode = true;
                form.ShowInTaskbar = false;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-6000, -6000);
                form.Size = new Size(1200, 780);
                form.Show();
                Application.DoEvents();

                for (int p = 0; p < PageNames.Length; p++)
                {
                    form.SelectPage(p);
                    Application.DoEvents();
                    List<double> samples = new List<double>();
                    long gdi = GetGuiResources(Process.GetCurrentProcess().Handle, GR_GDIOBJECTS);
                    for (int i = 0; i < 3; i++)
                    {
                        Stopwatch sw = Stopwatch.StartNew();
                        using (Bitmap bmp = new Bitmap(form.Width, form.Height))
                        {
                            form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                        }
                        sw.Stop();
                        samples.Add(sw.Elapsed.TotalMilliseconds);
                        gdi = GetGuiResources(Process.GetCurrentProcess().Handle, GR_GDIOBJECTS);
                    }
                    samples.Sort();
                    md.AppendLine("| " + PageNames[p] + " | " + samples[1].ToString("F1") + " | " + gdi + " |");
                }

                // 运行期 UI 指标（PR-1）：让 Timer 跑约 2.5 秒，读取埋点分位
                DateTime runEnd = DateTime.UtcNow.AddMilliseconds(2500);
                while (DateTime.UtcNow < runEnd)
                {
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(50);
                }
                md.AppendLine();
                md.AppendLine("## 运行期 UI 指标（埋点）");
                md.AppendLine();
                UiMetrics.Summary tickS = UiMetrics.TickSummary;
                UiMetrics.Summary snapS = UiMetrics.SnapshotSummary;
                UiMetrics.Summary waitS = UiMetrics.LockWaitSummary;
                md.AppendLine("| 指标 | P50 | P95 | P99 | Max | 样本 |");
                md.AppendLine("|---|---|---|---|---|---|");
                md.AppendLine("| UI tick (ms) | " + tickS.P50.ToString("F2") + " | " + tickS.P95.ToString("F2") +
                    " | " + tickS.P99.ToString("F2") + " | " + tickS.Max.ToString("F2") + " | " + tickS.Count + " |");
                md.AppendLine("| 异步快照构建 (ms) | " + snapS.P50.ToString("F2") + " | " + snapS.P95.ToString("F2") +
                    " | " + snapS.P99.ToString("F2") + " | " + snapS.Max.ToString("F2") + " | " + snapS.Count + " |");
                md.AppendLine("| 统计锁等待 (ms) | " + waitS.P50.ToString("F2") + " | " + waitS.P95.ToString("F2") +
                    " | " + waitS.P99.ToString("F2") + " | " + waitS.Max.ToString("F2") + " | " + waitS.Count + " |");
                md.AppendLine();
                md.AppendLine("绘制埋点（按页面 P95 ms）：");
                md.AppendLine();
                md.AppendLine("| 页面 | P50 | P95 | 样本 |");
                md.AppendLine("|---|---|---|---|");
                foreach (KeyValuePair<string, UiMetrics.Summary> kv in UiMetrics.PaintSummaries())
                {
                    md.AppendLine("| " + kv.Key + " | " + kv.Value.P50.ToString("F1") + " | " +
                        kv.Value.P95.ToString("F1") + " | " + kv.Value.Count + " |");
                }
                md.AppendLine();
                md.AppendLine("快照请求：" + UiMetrics.SnapshotRequests + " · 合并：" +
                    UiMetrics.SnapshotCoalesced + " · 过期丢弃：" + UiMetrics.SnapshotStale);
                md.AppendLine();
                Process proc = Process.GetCurrentProcess();
                md.AppendLine();
                md.AppendLine("## 资源");
                md.AppendLine();
                md.AppendLine("- WorkingSet：" + (proc.WorkingSet64 / 1024 / 1024) + " MB");
                md.AppendLine("- PrivateBytes：" + (proc.PrivateMemorySize64 / 1024 / 1024) + " MB");
                md.AppendLine("- GC Heap：" + (GC.GetTotalMemory(false) / 1024 / 1024) + " MB");
                md.AppendLine("- 数据目录：" + lastDir + "（" + lastMonths + " 个月）");
            }
            catch (Exception ex)
            {
                paintError = ex.Message;
            }
            finally
            {
                if (form != null)
                {
                    try { form.ForceClose(); } catch { }
                    try { form.Dispose(); } catch { }
                }
                if (lastEngine != null) lastEngine.Dispose();
                try { Directory.Delete(root, true); } catch { }
            }

            if (paintError.Length > 0)
            {
                md.AppendLine();
                md.AppendLine("绘制测量失败：" + paintError);
            }

            md.AppendLine();
            md.AppendLine("## 埋点口径");
            md.AppendLine();
            md.AppendLine("- UI tick：DashboardForm Timer handler 耗时（P95 目标 < 5ms）。");
            md.AppendLine("- 快照构建：GetSnapshot 整体耗时（含锁）。");
            md.AppendLine("- 锁等待/持有：StatsEngine._gate（P95 记录）。");
            md.AppendLine("- 绘制：PageBase.OnPaint → Draw（P95 目标 < 16.7ms）。");
            md.AppendLine();
            md.AppendLine("> 数值为工程目标对照，非 Windows 保证指标；改造前后须在同一机器、同一数据集下对比。");

            TryWrite(outPath, md.ToString());
            return 0;
        }

        private static void TryWrite(string path, string text)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, text, new UTF8Encoding(false));
                Console.WriteLine("perf report written to " + path);
            }
            catch (Exception ex)
            {
                Console.WriteLine("write report failed: " + ex.Message);
            }
        }
    }
}
