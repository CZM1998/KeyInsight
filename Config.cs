using System;
using System.IO;
using System.Reflection;

namespace KeyInsight
{
    /// <summary>
    /// 应用配置（v2.2 P0 阶段自 KeyInsight.cs 拆出，纯文件搬移、行为不变）。
    /// 读取顺序：KeyInsight.ini → 旧 KeyDebounce.ini 兼容路径。
    /// FilterEnabled/CaptureEnabled 语义见 P0-02；ini 仍保留旧键 enabled= 以支持回滚。
    /// </summary>
    public sealed class Config
    {
        public int ThresholdMs = 25;
        // P0-02：Enabled 迁移为 FilterEnabled（防抖只控制过滤，不再门控采集）。
        // ini 仍读写旧键 enabled=，用户回滚 v2.1.x 后设置不丢。
        public bool FilterEnabled = true;
        // 采集开关：false 时钩子只放行、不统计（本版无 UI，预留语义）。
        public bool CaptureEnabled = true;
        // P0-04 策略模式：0=观察、1=兼容（默认，行为同 v2.1.x）、2=单键保守
        public int PolicyMode = 1;
        // P3 应用白名单：逗号分隔的进程名（不含 .exe），白名单内永不拦截
        public string FilterAppWhitelist = "";
        // P0-05 / §4.5：采集、过滤、诊断三个状态独立（ui 开关在「高级与设备」页）
        public bool DiagnosticsEnabled = true;
        // P2-05：可选悬浮摘要
        public bool FloatingSummary = false;
        public int FloatingSummaryX = -1;
        public int FloatingSummaryY = -1;
        public bool SkipInjected = true;
        public bool LogSwallowed = true;
        public bool LogAllKeys = false;
        public int ThemeMode = 0;
        public int KeyboardLayout = 1;
        public bool AutoCheckUpdates = true;
        public long LastUpdateCheckTicks = 0;

        public static string ConfigPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "KeyInsight.ini"); }
        }

        public static Config Load()
        {
            Config c = new Config();
            try
            {
                // 版本化配置（v2 JSON）优先；不存在回退到旧 ini（下一次 Save 自动迁移）
                string v2Path = ConfigV2.GetPath();
                if (File.Exists(v2Path) && ConfigV2.TryRead(v2Path, c))
                {
                    return c;
                }
                if (File.Exists(ConfigPath))
                {
                    c.Read(ConfigPath, false);
                }
                else
                {
                    string legacy = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\KeyDebounce\KeyDebounce.ini");
                    if (File.Exists(legacy))
                    {
                        c.FilterEnabled = true;
                        c.Read(legacy, true);
                    }
                }
            }
            catch
            {
            }
            return c;
        }

        internal void Read(string path, bool legacy)
        {
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string val = line.Substring(eq + 1).Trim();
                int n;
                switch (key)
                {
                    case "thresholdms":
                        if (int.TryParse(val, out n)) ThresholdMs = Math.Max(1, Math.Min(100, n));
                        break;
                    case "enabled":
                    case "filterenabled":
                        if (!legacy) FilterEnabled = val != "0";
                        break;
                    case "captureenabled":
                        if (!legacy) CaptureEnabled = val != "0";
                        break;
                    case "diagnosticsenabled":
                        if (!legacy) DiagnosticsEnabled = val != "0";
                        break;
                    case "floatingsummary":
                        if (!legacy) FloatingSummary = val != "0";
                        break;
                    case "floatingsummaryx":
                        if (!legacy && int.TryParse(val, out n)) FloatingSummaryX = n;
                        break;
                    case "floatingsummaryy":
                        if (!legacy && int.TryParse(val, out n)) FloatingSummaryY = n;
                        break;
                    case "policymode":
                        if (!legacy && int.TryParse(val, out n)) PolicyMode = Math.Max(0, Math.Min(2, n));
                        break;
                    case "filterappwhitelist":
                        if (!legacy) FilterAppWhitelist = val;
                        break;
                    case "skipinjected":
                        SkipInjected = val != "0";
                        break;
                    case "logswallowed":
                        LogSwallowed = val != "0";
                        break;
                    case "logallkeys":
                        LogAllKeys = val != "0";
                        break;
                    case "thememode":
                        if (int.TryParse(val, out n)) ThemeMode = Math.Max(0, Math.Min(2, n));
                        break;
                    case "keyboardlayout":
                        if (int.TryParse(val, out n)) KeyboardLayout = Math.Max(0, Math.Min(3, n));
                        break;
                    case "autocheckupdates":
                        AutoCheckUpdates = val != "0";
                        break;
                    case "lastupdatecheck":
                        long ticks;
                        if (long.TryParse(val, out ticks)) LastUpdateCheckTicks = ticks;
                        break;
                }
            }
        }

        public void Save()
        {
            try
            {
                File.WriteAllLines(ConfigPath, new string[]
                {
                    "# KeyInsight settings",
                    "ThresholdMs=" + ThresholdMs,
                    "Enabled=" + (FilterEnabled ? 1 : 0),
                    "FilterEnabled=" + (FilterEnabled ? 1 : 0),
                    "CaptureEnabled=" + (CaptureEnabled ? 1 : 0),
                    "DiagnosticsEnabled=" + (DiagnosticsEnabled ? 1 : 0),
                    "FloatingSummary=" + (FloatingSummary ? 1 : 0),
                    "FloatingSummaryX=" + FloatingSummaryX,
                    "FloatingSummaryY=" + FloatingSummaryY,
                    "PolicyMode=" + PolicyMode,
                    "FilterAppWhitelist=" + (FilterAppWhitelist ?? ""),
                    "SkipInjected=" + (SkipInjected ? 1 : 0),
                    "LogSwallowed=" + (LogSwallowed ? 1 : 0),
                    "LogAllKeys=" + (LogAllKeys ? 1 : 0),
                    "ThemeMode=" + ThemeMode,
                    "KeyboardLayout=" + KeyboardLayout,
                    "AutoCheckUpdates=" + (AutoCheckUpdates ? 1 : 0),
                    "LastUpdateCheck=" + LastUpdateCheckTicks
                });
                // 双写版本化配置（失败不影响主配置）
                try { ConfigV2.SaveTo(ConfigV2.GetPath(), this); } catch { }
            }
            catch
            {
            }
        }
    }

    public static class AppInfo
    {
        public static string Version
        {
            get
            {
                Version v = Assembly.GetExecutingAssembly().GetName().Version;
                return v == null ? "0.0.0" : v.ToString(3);
            }
        }
    }
}
