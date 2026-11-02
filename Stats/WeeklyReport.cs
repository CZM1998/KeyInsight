using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace KeyInsight.Stats
{
    /// <summary>
    /// 周报与导出清单（P2-05）：
    /// - 周报：data/exports/weekly-YYYY-Www.md，纯本地生成，含数据区间与精度说明；
    /// - 导出清单：ExportCsv 后写 export-manifest.json，标注数据区间、行数与精度边界
    ///   （CSV 本身格式冻结，不改动）。
    /// </summary>
    public static class WeeklyReport
    {
        public static string Generate(string exportDir, StatsSnapshot snap)
        {
            try
            {
                if (!Directory.Exists(exportDir)) Directory.CreateDirectory(exportDir);
                DateTime today = DateTime.Today;
                // ISO 周一为一周开始
                int dow = ((int)today.DayOfWeek + 6) % 7;
                DateTime weekStart = today.AddDays(-dow);
                DateTime weekEnd = weekStart.AddDays(6);

                long total = 0;
                long swallowed = 0;
                int activeDays = 0;
                int kbMinutes = 0;
                foreach (DayPoint pt in snap.Range.Daily)
                {
                    if (pt.Date >= weekStart && pt.Date <= weekEnd)
                    {
                        total += pt.Total;
                        swallowed += pt.Swallowed;
                        if (pt.Total > 0) activeDays++;
                    }
                }
                kbMinutes = snap.Range.ActiveMinutes;

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# KeyInsight 周报");
                sb.AppendLine();
                sb.AppendLine("- 数据区间：" + weekStart.ToString("yyyy-MM-dd") + " ~ " + weekEnd.ToString("yyyy-MM-dd"));
                sb.AppendLine("- 生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
                sb.AppendLine("- 精度说明：统计口径为键盘按下事件（含长按自动重复），不是字符数；");
                sb.AppendLine("  拦截次数为防抖过滤的按下事件，不是硬件故障次数（旧口径，基于拦截策略）。");
                sb.AppendLine();
                sb.AppendLine("## 本周概览");
                sb.AppendLine();
                sb.AppendLine("| 指标 | 值 |");
                sb.AppendLine("|---|---|");
                sb.AppendLine("| 键盘按下事件 | " + total.ToString("N0") + " |");
                sb.AppendLine("| 防抖拦截 | " + swallowed.ToString("N0") + " |");
                sb.AppendLine("| 活跃天数 | " + activeDays + " / 7 |");
                sb.AppendLine("| 综合活跃分钟（键盘∪鼠标，范围口径） | " + snap.CombinedActiveMinutes.ToString("N0") + " |");
                sb.AppendLine();

                sb.AppendLine("## 最常用按键（范围内 Top 10）");
                sb.AppendLine();
                sb.AppendLine("| 按键 | 次数 | 拦截 |");
                sb.AppendLine("|---|---|---|");
                List<KeyValuePair<int, KeyAgg>> keys = new List<KeyValuePair<int, KeyAgg>>(snap.Range.Keys);
                keys.Sort(delegate(KeyValuePair<int, KeyAgg> a, KeyValuePair<int, KeyAgg> b)
                {
                    return b.Value.Count.CompareTo(a.Value.Count);
                });
                for (int i = 0; i < keys.Count && i < 10; i++)
                {
                    sb.AppendLine("| " + UI.KeyLayout.LabelFor(keys[i].Key) + " | " +
                        keys[i].Value.Count.ToString("N0") + " | " + keys[i].Value.Swallowed.ToString("N0") + " |");
                }
                sb.AppendLine();

                sb.AppendLine("## 需关注的按键");
                sb.AppendLine();
                bool anyHealth = false;
                for (int i = 0; i < snap.KeyHealth.Count && i < 5; i++)
                {
                    KeyHealthRow row = snap.KeyHealth[i];
                    if (row.Score >= 90) continue;
                    anyHealth = true;
                    sb.AppendLine("- " + UI.KeyLayout.LabelFor(row.Vk) + "：健康分 " + row.Score +
                        "（旧口径·基于拦截），本周拦截 " + row.Swallowed7 + " 次" +
                        (row.Swallowed7 > row.SwallowedPrev7 ? "（较上周上升）" : "（较上周持平或下降）"));
                }
                if (!anyHealth) sb.AppendLine("- 无（该范围内没有低健康分按键）");
                sb.AppendLine();

                sb.AppendLine("## 常用快捷键（累计 Top 10，仅聚合计数）");
                sb.AppendLine();
                if (snap.TopShortcuts.Count == 0)
                {
                    sb.AppendLine("- 暂无数据");
                }
                else
                {
                    for (int i = 0; i < snap.TopShortcuts.Count; i++)
                    {
                        sb.AppendLine("- " + snap.TopShortcuts[i].Key + "：" + snap.TopShortcuts[i].Value.ToString("N0") + " 次");
                    }
                }
                sb.AppendLine();

                string fileName = "weekly-" + weekStart.ToString("yyyy-MM-dd") + ".md";
                string path = Path.Combine(exportDir, fileName);
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                return path;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>导出清单（P2-05）：与 ExportCsv 产物并列，标注数据区间与精度。</summary>
        public static string WriteExportManifest(string dir, DateTime start, DateTime end, string[] files)
        {
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                StringBuilder sb = new StringBuilder();
                sb.Append("{\"schema\":1,\"kind\":\"keyinsight-export\",\"rangeStart\":\"")
                    .Append(start == DateTime.MinValue ? "earliest" : start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                    .Append("\",\"rangeEnd\":\"")
                    .Append(end == DateTime.MaxValue ? "today" : end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                    .Append("\",\"precision\":\"keyboard down events incl. auto-repeat; swallow counts are policy-based legacy metric\",\"files\":[");
                for (int i = 0; i < files.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(files[i].Replace("\\", "/")).Append('"');
                }
                sb.Append("],\"generatedUtc\":\"").Append(DateTime.UtcNow.ToString("u")).Append("\"}");
                string path = Path.Combine(dir, "export-manifest.json");
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                return path;
            }
            catch
            {
                return null;
            }
        }
    }
}
