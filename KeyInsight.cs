using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using KeyInsight.Hook;
using KeyInsight.Stats;
using KeyInsight.UI;
using KeyInsight.Update;

namespace KeyInsight
{
    public sealed class Config
    {
        public int ThresholdMs = 25;
        public bool Enabled = true;
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
                if (File.Exists(ConfigPath))
                {
                    c.Read(ConfigPath, false);
                }
                else
                {
                    string legacy = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\KeyDebounce\KeyDebounce.ini");
                    if (File.Exists(legacy))
                    {
                        c.Enabled = true;
                        c.Read(legacy, true);
                    }
                }
            }
            catch
            {
            }
            return c;
        }

        private void Read(string path, bool legacy)
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
                        if (!legacy) Enabled = val != "0";
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
                        if (int.TryParse(val, out n)) KeyboardLayout = Math.Max(0, Math.Min(1, n));
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
                    "Enabled=" + (Enabled ? 1 : 0),
                    "SkipInjected=" + (SkipInjected ? 1 : 0),
                    "LogSwallowed=" + (LogSwallowed ? 1 : 0),
                    "LogAllKeys=" + (LogAllKeys ? 1 : 0),
                    "ThemeMode=" + ThemeMode,
                    "KeyboardLayout=" + KeyboardLayout,
                    "AutoCheckUpdates=" + (AutoCheckUpdates ? 1 : 0),
                    "LastUpdateCheck=" + LastUpdateCheckTicks
                });
            }
            catch
            {
            }
        }
    }

    public static class AutoStartHelper
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "KeyInsight";

        public static bool IsEnabled()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath))
                {
                    if (key == null) return false;
                    string v = key.GetValue(ValueName) as string;
                    return !string.IsNullOrEmpty(v);
                }
            }
            catch { return false; }
        }

        public static void Set(bool on)
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (key == null) return;
                    if (on) key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"");
                    else key.DeleteValue(ValueName, false);
                }
            }
            catch
            {
            }
        }

        public static void MigrateLegacy()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key == null) return;
                    object old = key.GetValue("KeyDebounce");
                    if (old == null) return;
                    key.DeleteValue("KeyDebounce", false);
                    if (key.GetValue(ValueName) == null)
                    {
                        key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"");
                    }
                }
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

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length >= 1 && string.Equals(args[0], "--selftest", StringComparison.OrdinalIgnoreCase))
            {
                return SelfTest.Run();
            }
            if (args.Length >= 2 && string.Equals(args[0], "--screenshot", StringComparison.OrdinalIgnoreCase))
            {
                return ScreenshotRunner.Run(args[1]);
            }

            bool created;
            using (Mutex mutex = new Mutex(true, "KeyInsight_SingleInstance", out created))
            {
                if (!created) return 0;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { LogError(e.Exception); };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    LogError(e.ExceptionObject as Exception);
                };

                Config cfg = Config.Load();
                Theme.Mode = (ThemeMode)cfg.ThemeMode;
                Theme.Refresh(false);
                AutoStartHelper.MigrateLegacy();
                cfg.Save();

                string dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
                StatsEngine stats = new StatsEngine(dataDir, "KeyInsight");
                ForegroundTracker.Start();

                DebounceHook hook = new DebounceHook(cfg, stats);
                hook.Start();
                MouseHook mouseHook = new MouseHook(cfg, stats);
                mouseHook.Start();

                try
                {
                    using (TrayContext tray = new TrayContext(cfg, stats, hook, mouseHook))
                    {
                        Application.Run(tray);
                    }
                }
                finally
                {
                    hook.Dispose();
                    mouseHook.Dispose();
                    stats.Dispose();
                    ForegroundTracker.Stop();
                }
            }
            return 0;
        }

        internal static void LogError(Exception ex)
        {
            if (ex == null) return;
            try
            {
                File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + ex + "\r\n");
            }
            catch
            {
            }
        }
    }

    public sealed class TrayContext : ApplicationContext
    {
        private readonly Config _cfg;
        private readonly StatsEngine _stats;
        private readonly DebounceHook _hook;
        private readonly MouseHook _mouseHook;
        private readonly NotifyIcon _icon;
        private readonly ToolStripMenuItem _openItem;
        private readonly ToolStripMenuItem _toggleItem;
        private readonly ToolStripMenuItem _startupItem;
        private readonly ToolStripMenuItem _statusItem;
        private readonly ToolStripMenuItem _thresholdRoot;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly SynchronizationContext _sync;
        private DashboardForm _form;
        private UpdateInfo _pendingUpdate;
        private bool _updateBusy;

        public TrayContext(Config cfg, StatsEngine stats, DebounceHook hook, MouseHook mouseHook)
        {
            _cfg = cfg;
            _stats = stats;
            _hook = hook;
            _mouseHook = mouseHook;
            _sync = SynchronizationContext.Current;

            ContextMenuStrip menu = new ContextMenuStrip();

            _openItem = new ToolStripMenuItem("打开面板");
            _openItem.Font = new Font(_openItem.Font, FontStyle.Bold);
            _openItem.Click += delegate { ShowDashboard(); };
            menu.Items.Add(_openItem);

            _toggleItem = new ToolStripMenuItem();
            _toggleItem.Click += delegate
            {
                _cfg.Enabled = !_cfg.Enabled;
                _cfg.Save();
                UpdateMenu();
                if (_form != null && !_form.IsDisposed)
                {
                    _form.RefreshSnapshot();
                    _form.Invalidate();
                }
            };
            menu.Items.Add(_toggleItem);

            _thresholdRoot = new ToolStripMenuItem("防抖窗口");
            int[] windows = new int[] { 10, 15, 20, 25, 30 };
            for (int i = 0; i < windows.Length; i++)
            {
                int ms = windows[i];
                ToolStripMenuItem item = new ToolStripMenuItem(ms + " ms");
                item.Click += delegate
                {
                    _cfg.ThresholdMs = ms;
                    _cfg.Save();
                    UpdateMenu();
                    if (_form != null && !_form.IsDisposed)
                    {
                        _form.RefreshSnapshot();
                        _form.Invalidate();
                    }
                };
                _thresholdRoot.DropDownItems.Add(item);
            }
            menu.Items.Add(_thresholdRoot);

            _startupItem = new ToolStripMenuItem("开机自启");
            _startupItem.Click += delegate
            {
                AutoStartHelper.Set(!AutoStartHelper.IsEnabled());
                UpdateMenu();
            };
            menu.Items.Add(_startupItem);

            _statusItem = new ToolStripMenuItem();
            _statusItem.Enabled = false;
            menu.Items.Add(_statusItem);

            ToolStripMenuItem reinstall = new ToolStripMenuItem("重新安装输入钩子");
            reinstall.Click += delegate
            {
                bool ok = _hook.Restart();
                bool mok = _mouseHook.Restart();
                bool all = ok && mok;
                _icon.ShowBalloonTip(2000, all ? "输入钩子已重新安装" : "重新安装失败",
                    all ? "键盘防抖与鼠标统计已重新生效" : "请尝试以管理员身份运行",
                    all ? ToolTipIcon.Info : ToolTipIcon.Error);
            };
            menu.Items.Add(reinstall);

            ToolStripMenuItem updateItem = new ToolStripMenuItem("检查更新");
            updateItem.Click += delegate { BeginUpdateCheck(true); };
            menu.Items.Add(updateItem);

            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem exit = new ToolStripMenuItem("退出");
            exit.Click += delegate { ExitApp(); };
            menu.Items.Add(exit);

            menu.Opening += delegate { UpdateMenu(); };

            _icon = new NotifyIcon();
            _icon.Icon = LoadIcon();
            _icon.ContextMenuStrip = menu;
            _icon.Text = "KeyInsight";
            _icon.Visible = true;
            _icon.DoubleClick += delegate { ShowDashboard(); };
            _icon.BalloonTipClicked += delegate { if (_pendingUpdate != null) InstallUpdate(); };

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 2000;
            _timer.Tick += delegate { UpdateMenu(); };
            _timer.Start();

            UpdateMenu();
            _icon.ShowBalloonTip(2500, "KeyInsight 已启动",
                "防抖与按键统计正在运行，双击托盘图标打开面板", ToolTipIcon.Info);

            if (_cfg.AutoCheckUpdates)
            {
                DateTime last = _cfg.LastUpdateCheckTicks > 0
                    ? new DateTime(_cfg.LastUpdateCheckTicks, DateTimeKind.Utc)
                    : DateTime.MinValue;
                if ((DateTime.UtcNow - last).TotalHours >= 24) BeginUpdateCheck(false);
            }
        }

        private static Icon LoadIcon()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream s = asm.GetManifestResourceStream("app.ico"))
                {
                    if (s != null) return new Icon(s, SystemInformation.SmallIconSize);
                }
            }
            catch
            {
            }
            return SystemIcons.Application;
        }

        private void ShowDashboard()
        {
            if (_form == null || _form.IsDisposed)
            {
                _form = new DashboardForm(_stats, _cfg, _hook, _mouseHook,
                    delegate { BeginUpdateCheck(true); });
            }
            _form.Show();
            if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
            _form.Activate();
            _form.BringToFront();
            _form.RefreshSnapshot();
        }

        private void UpdateMenu()
        {
            _toggleItem.Text = _cfg.Enabled ? "防抖：已开启" : "防抖：已关闭";
            _toggleItem.Checked = _cfg.Enabled;
            _startupItem.Checked = AutoStartHelper.IsEnabled();

            for (int i = 0; i < _thresholdRoot.DropDownItems.Count; i++)
            {
                ToolStripMenuItem item = _thresholdRoot.DropDownItems[i] as ToolStripMenuItem;
                if (item == null) continue;
                item.Checked = item.Text == _cfg.ThresholdMs + " ms";
            }

            long total, swallowed;
            _stats.GetTodayCounts(out total, out swallowed);
            long mouse = _stats.GetTodayMouseCount();
            _statusItem.Text = "今日 " + total.ToString("N0") + " 键 · 鼠标 " + mouse.ToString("N0") +
                " 次 · 拦截 " + swallowed.ToString("N0");

            string text = "KeyInsight · 今日 " + total.ToString("N0") + " 键 · 鼠标 " + mouse.ToString("N0") +
                " 次 · 拦截 " + _hook.SwallowedCount;
            if (text.Length > 63) text = text.Substring(0, 63);
            _icon.Text = text;
        }

        private void BeginUpdateCheck(bool manual)
        {
            if (_updateBusy) return;
            _updateBusy = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    UpdateInfo info = UpdateService.Check();
                    _cfg.LastUpdateCheckTicks = DateTime.UtcNow.Ticks;
                    _cfg.Save();

                    if (info == null)
                    {
                        if (manual)
                        {
                            PostBalloon("已是最新版本", "当前 v" + UpdateService.CurrentVersion, ToolTipIcon.Info);
                        }
                    }
                    else
                    {
                        _pendingUpdate = info;
                        PostBalloon("发现新版本 v" + info.Version,
                            manual ? "点击这里下载并安装" : "点击托盘气泡或右键检查更新",
                            ToolTipIcon.Info);

                        if (manual && _sync != null)
                        {
                            _sync.Post(delegate { InstallUpdate(); }, null);
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (manual) PostBalloon("检查更新失败", ex.Message, ToolTipIcon.Error);
                }
                finally
                {
                    _updateBusy = false;
                }
            });
        }

        private void InstallUpdate()
        {
            UpdateInfo info = _pendingUpdate;
            if (info == null || _updateBusy) return;
            _updateBusy = true;
            PostBalloon("正在下载更新", "v" + info.Version, ToolTipIcon.Info);

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string path = UpdateService.Download(info);
                    if (_sync != null) _sync.Post(delegate { PromptInstall(path, info); }, null);
                }
                catch (Exception ex)
                {
                    PostBalloon("更新下载失败", ex.Message, ToolTipIcon.Error);
                }
                finally
                {
                    _updateBusy = false;
                }
            });
        }

        private void PromptInstall(string path, UpdateInfo info)
        {
            DialogResult result = MessageBox.Show(
                "新版本 v" + info.Version + " 已下载。\n\n是否立即安装？安装程序会关闭 KeyInsight，安装完成后自动重新启动。",
                "KeyInsight 更新", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (result != DialogResult.Yes) return;

            try
            {
                UpdateService.RunInstaller(path);
                ExitApp();
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动安装程序失败：" + ex.Message, "KeyInsight",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void PostBalloon(string title, string text, ToolTipIcon icon)
        {
            if (_sync == null) return;
            _sync.Post(delegate
            {
                try { _icon.ShowBalloonTip(5000, title, text, icon); }
                catch { }
            }, null);
        }

        private void ExitApp()
        {
            _timer.Stop();
            if (_form != null && !_form.IsDisposed) _form.ForceClose();
            _icon.Visible = false;
            _hook.Dispose();
            _mouseHook.Dispose();
            _stats.Dispose();
            ForegroundTracker.Stop();
            _icon.Dispose();
            ExitThread();
        }
    }

    internal static class SelfTest
    {
        public static int Run()
        {
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest.log");
            StringBuilder log = new StringBuilder();
            bool ok = true;
            string dir = Path.Combine(Path.GetTempPath(), "KeyInsightSelfTest_" + Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(dir);
                StatsEngine e1 = new StatsEngine(dir, "KeyInsight");
                Synthetic.Generate(e1, 90, 240, new Random(42), false);
                e1.WaitIdle(30000);
                e1.SaveAll();
                StatsSnapshot s1 = e1.GetSnapshot(DateTime.MinValue, DateTime.MaxValue);
                long total1 = s1.TotalAll;
                long sw1 = s1.SwallowedAll;
                int months1 = s1.Months.Count;
                e1.Dispose();

                log.AppendLine("first run: total=" + total1 + " swallowed=" + sw1 + " months=" + months1);
                ok &= total1 > 0;
                ok &= sw1 > 0;
                ok &= months1 >= 2;

                StatsEngine e2 = new StatsEngine(dir, "KeyInsight");
                e2.LoadAllMonths();
                e2.WaitIdle(3000);
                StatsSnapshot s2 = e2.GetSnapshot(DateTime.MinValue, DateTime.MaxValue);
                log.AppendLine("reload: total=" + s2.TotalAll + " swallowed=" + s2.SwallowedAll +
                    " months=" + s2.Months.Count + " historyComplete=" + s2.HistoryComplete);
                ok &= s2.TotalAll == total1;
                ok &= s2.SwallowedAll == sw1;
                ok &= s2.Range.Apps.Count > 0;
                ok &= s2.Range.Keys.Count > 0;

                long mouseTotal = 0;
                foreach (long mv in s2.Range.Mouse.Values) mouseTotal += mv;
                log.AppendLine("mouse total=" + mouseTotal + " buttons=" + s2.Range.Mouse.Count);
                ok &= mouseTotal > 0;
                ok &= s2.Range.Mouse.Count >= 5;

                string exportBase = Path.Combine(dir, "export");
                e2.ExportCsv(exportBase, null);
                bool exportOk = File.Exists(exportBase + "-days.csv") &&
                    File.Exists(exportBase + "-apps.csv") &&
                    File.Exists(exportBase + "-keys.csv") &&
                    File.Exists(exportBase + "-mouse.csv") &&
                    new FileInfo(exportBase + "-days.csv").Length > 0 &&
                    new FileInfo(exportBase + "-apps.csv").Length > 0 &&
                    new FileInfo(exportBase + "-keys.csv").Length > 0 &&
                    new FileInfo(exportBase + "-mouse.csv").Length > 0;
                log.AppendLine("csv export ok=" + exportOk);
                ok &= exportOk;

                List<MonthInfo> months = e2.GetMonths();
                string deleteKey = months[months.Count - 1].Key;
                e2.DeleteMonth(deleteKey);
                e2.Dispose();
                log.AppendLine("deleted month " + deleteKey);

                StatsEngine e3 = new StatsEngine(dir, "KeyInsight");
                e3.LoadAllMonths();
                e3.WaitIdle(3000);
                StatsSnapshot s3 = e3.GetSnapshot(DateTime.MinValue, DateTime.MaxValue);
                log.AppendLine("after delete: total=" + s3.TotalAll + " months=" + s3.Months.Count);
                ok &= s3.TotalAll < s2.TotalAll;
                e3.Dispose();
            }
            catch (Exception ex)
            {
                ok = false;
                log.AppendLine("EXCEPTION: " + ex);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }

            log.AppendLine(ok ? "SELFTEST OK" : "SELFTEST FAILED");
            try { File.WriteAllText(logPath, log.ToString()); } catch { }
            return ok ? 0 : 1;
        }
    }

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
                cfg.Enabled = true;
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

                string[] names = new string[] { "overview", "keyboard", "mouse", "apps", "time", "health", "debounce", "settings" };
                for (int pass = 0; pass < 2; pass++)
                {
                    bool dark = pass == 0;
                    Theme.ForceDark = dark;
                    Theme.Refresh();
                    form.ApplyTheme();
                    for (int i = 0; i < names.Length; i++)
                    {
                        form.SelectPage(i);
                        Application.DoEvents();
                        Thread.Sleep(60);
                        using (Bitmap bmp = new Bitmap(form.Width, form.Height))
                        {
                            form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                            bmp.Save(Path.Combine(outDir, (dark ? "dark-" : "light-") + names[i] + ".png"),
                                ImageFormat.Png);
                        }
                    }
                }

                Theme.ForceDark = null;
                Theme.Refresh();

                Theme.ForceDark = true;
                Theme.Refresh();
                form.ApplyTheme();
                form.PreviewKeyboardHover("E");
                using (Bitmap bmp = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                    bmp.Save(Path.Combine(outDir, "dark-keyboard-hover.png"), ImageFormat.Png);
                }
                form.PreviewKeyboardHover(null);
                Theme.ForceDark = null;
                Theme.Refresh();

                cfg.KeyboardLayout = 0;
                for (int pass = 0; pass < 2; pass++)
                {
                    bool dark = pass == 0;
                    Theme.ForceDark = dark;
                    Theme.Refresh();
                    form.ApplyTheme();
                    form.SelectPage(1);
                    Application.DoEvents();
                    Thread.Sleep(60);
                    using (Bitmap bmp = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                        bmp.Save(Path.Combine(outDir, (dark ? "dark" : "light") + "-keyboard-87.png"),
                            ImageFormat.Png);
                    }
                }
                cfg.KeyboardLayout = 1;
                Theme.ForceDark = null;
                Theme.Refresh();
                log.AppendLine("screenshots written to " + outDir);
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

    internal static class Synthetic
    {
        private static readonly string[] Apps = new string[]
        {
            "chrome", "code", "wechat", "explorer", "windowsterminal", "msedge",
            "steam", "idea64", "discord", "notepad", "qq", "unknown"
        };

        private static readonly int[] AppWeights = new int[]
        {
            32, 20, 12, 9, 8, 6, 4, 3, 2, 2, 1, 1
        };

        private static readonly int[] MouseButtonValues = new int[] { 1, 2, 3, 4, 5, 6, 7 };
        private static readonly int[] MouseButtonWeights = new int[] { 60, 15, 5, 4, 3, 7, 6 };

        private static readonly int[] Keys = new int[]
        {
            0x45, 0x20, 0x41, 0x53, 0x44, 0x46, 0x47, 0x48, 0x4A, 0x4B, 0x4C, 0x51, 0x57, 0x5A, 0x58,
            0x43, 0x56, 0x42, 0x4E, 0x4D, 0x54, 0x59, 0x55, 0x49, 0x4F, 0x50, 0x31, 0x32, 0x33, 0x34,
            0x35, 0x36, 0x37, 0x38, 0x39, 0x30, 0x08, 0x0D, 0x09, 0xA0, 0xA2, 0x25, 0x26, 0x27, 0x28,
            0xBC, 0xBE, 0xBF, 0xBA, 0xDE, 0xDB, 0xDD, 0xDC, 0xBD, 0xBB, 0x1B,
            0x90, 0x6F, 0x6A, 0x6D, 0x67, 0x68, 0x69, 0x6B, 0x64, 0x65, 0x66, 0x61, 0x62, 0x63,
            0x60, 0x6E, 0xAD, 0xAE, 0xAF, 0xB7, 0x1000D
        };

        private static readonly int[] KeyWeights = BuildKeyWeights();

        private static int[] BuildKeyWeights()
        {
            int[] w = new int[Keys.Length];
            for (int i = 0; i < w.Length; i++) w[i] = 12;
            w[0] = 55;   // E
            w[1] = 120;  // Space
            w[36] = 45;  // Backspace
            w[37] = 40;  // Enter
            w[38] = 20;  // Tab
            return w;
        }

        private static readonly int[] HourWeights = new int[]
        {
            1, 0, 0, 0, 0, 0, 1, 2, 4, 8, 10, 8,
            5, 7, 9, 10, 9, 8, 7, 8, 9, 6, 3, 1
        };

        public static void Generate(StatsEngine engine, int days, int perDay, Random rnd, bool demo)
        {
            for (int d = days; d >= 0; d--)
            {
                DateTime date = DateTime.Today.AddDays(-d);
                int n = perDay + rnd.Next(perDay);
                if (demo) n = d == 0 ? (4000 + rnd.Next(2000)) : (700 + rnd.Next(1500));
                int maxHour = (demo || d != 0) ? 23 : DateTime.Now.Hour;

                for (int i = 0; i < n; i++)
                {
                    int hour = PickHour(rnd, maxHour);
                    int minute = rnd.Next(60);
                    int second = rnd.Next(60);
                    DateTime at = new DateTime(date.Year, date.Month, date.Day, hour, minute, second)
                        .AddMilliseconds(rnd.Next(1000));
                    if (!demo && at > DateTime.Now) at = DateTime.Now.AddMinutes(-rnd.Next(1, 60));

                    KeyEvent ev = new KeyEvent();
                    ev.Type = KeyEventType.Down;
                    ev.Vk = Pick(Keys, KeyWeights, rnd);
                    ev.At = at;
                    ev.App = Pick(Apps, AppWeights, rnd);
                    engine.Enqueue(ev);

                    if (rnd.NextDouble() < 0.015)
                    {
                        KeyEvent sw = new KeyEvent();
                        sw.Type = KeyEventType.Swallow;
                        sw.Vk = rnd.NextDouble() < 0.88 ? 0x45 : ev.Vk;
                        sw.At = at.AddMilliseconds(3);
                        sw.App = ev.App;
                        sw.GapDown = 1.5 + rnd.NextDouble() * 5;
                        sw.GapShortUp = 0.6 + rnd.NextDouble() * 2.2;
                        engine.Enqueue(sw);
                    }
                }

                int mouseCount = Math.Max(8, n / 6);
                for (int i = 0; i < mouseCount; i++)
                {
                    int hour = PickHour(rnd, maxHour);
                    DateTime at = new DateTime(date.Year, date.Month, date.Day, hour, rnd.Next(60), rnd.Next(60))
                        .AddMilliseconds(rnd.Next(1000));
                    if (!demo && at > DateTime.Now) at = DateTime.Now.AddMinutes(-rnd.Next(1, 60));

                    int button = Pick(MouseButtonValues, MouseButtonWeights, rnd);
                    KeyEvent me = new KeyEvent();
                    me.At = at;
                    me.App = Pick(Apps, AppWeights, rnd);
                    me.Button = button;
                    if (button >= 6)
                    {
                        me.Type = KeyEventType.MouseWheel;
                        me.Delta = 1 + rnd.Next(3);
                    }
                    else
                    {
                        me.Type = KeyEventType.MouseDown;
                    }
                    engine.Enqueue(me);
                }
            }
        }

        private static int PickHour(Random rnd, int maxHour)
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                int h = Pick(HourWeights, rnd);
                if (h <= maxHour) return h;
            }
            return maxHour <= 0 ? 0 : rnd.Next(maxHour + 1);
        }

        private static int Pick(int[] weights, Random rnd)
        {
            int total = 0;
            for (int i = 0; i < weights.Length; i++) total += weights[i];
            int x = rnd.Next(total);
            int acc = 0;
            for (int i = 0; i < weights.Length; i++)
            {
                acc += weights[i];
                if (x < acc) return i;
            }
            return 0;
        }

        private static int Pick(int[] values, int[] weights, Random rnd)
        {
            return values[Pick(weights, rnd)];
        }

        private static string Pick(string[] values, int[] weights, Random rnd)
        {
            return values[Pick(weights, rnd)];
        }
    }
}
