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

namespace KeyInsight
{
    internal static class ScreenshotRunner
    {
        public static int Run(string outDir)
        {
            StringBuilder log = new StringBuilder();
            string dataDir = Path.Combine(Path.GetTempPath(), "KeyInsightDemo_" + Guid.NewGuid().ToString("N"));
            DashboardForm form = null;
            StatsEngine engine = null;
            try
            {
                Directory.CreateDirectory(outDir);
                Directory.CreateDirectory(dataDir);

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                engine = new StatsEngine(dataDir, "KeyInsight");
                Synthetic.Generate(engine, 120, 900, new Random(7), true);
                engine.WaitIdle(60000);
                engine.SaveAll();
                engine.LoadAllMonths();

                try
                {
                    string[] mf = Directory.GetFiles(dataDir, "*.kds");
                    log.AppendLine("month files: " + string.Join(", ", mf));
                    List<MonthInfo> mi = engine.GetMonths();
                    string keys = "";
                    for (int i = 0; i < mi.Count; i++) keys += (i > 0 ? ", " : "") + mi[i].Key + "=" + mi[i].Total;
                    log.AppendLine("engine months: " + keys);
                }
                catch (Exception ex)
                {
                    log.AppendLine("month dump failed: " + ex.Message);
                }

                Config cfg = new Config();
                cfg.FilterEnabled = true;
                cfg.ThresholdMs = 25;
                cfg.SkipInjected = true;

                form = new DashboardForm(engine, cfg, null, null, null);
                form.ScreenshotMode = true;
                form.ShowInTaskbar = false;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-5000, -5000);
                form.Size = new Size(1200, 780);
                form.Show();
                Application.DoEvents();

                // PR-5：尺寸 × 主题 × 页面矩阵 + 滚动/悬停/87 键回归
                KeyInsight.Perf.ScreenshotMatrixRunner.RunAll(form, cfg, outDir, log);
                return 0;
            }
            catch (Exception ex)
            {
                log.AppendLine("EXCEPTION: " + ex);
                return 1;
            }
            finally
            {
                if (form != null)
                {
                    try { form.ForceClose(); } catch { }
                    try { form.Dispose(); } catch { }
                }
                if (engine != null) engine.Dispose();
                try { Directory.Delete(dataDir, true); } catch { }
                try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "screenshot.log"), log.ToString()); }
                catch { }
            }
        }
    }
}
