using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using KeyInsight.Core.Policy;
using KeyInsight.Core.Telemetry;
using KeyInsight.Hook;
using KeyInsight.Stats;
using KeyInsight.UI;
using KeyInsight.Update;

namespace KeyInsight
{
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
            if (args.Length >= 1 && string.Equals(args[0], "--migrate-data", StringComparison.OrdinalIgnoreCase))
            {
                return MigrateDataRunner.Run();
            }
            // 独立更新器健康心跳（路线图 PR-6）：启动成功后写入 nonce 供 updater 校验
            string healthNonce = "";
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--health-nonce") healthNonce = args[++i];
            }

            if (args.Length >= 3 && string.Equals(args[0], "--gen-data", StringComparison.OrdinalIgnoreCase))
            {
                int months = int.Parse(args[2]);
                int perDay = args.Length >= 4 ? int.Parse(args[3]) : 3000;
                PerfDataGenerator.Generate(args[1], months, perDay, 42);
                return 0;
            }
            if (args.Length >= 2 && string.Equals(args[0], "--perf-report", StringComparison.OrdinalIgnoreCase))
            {
                return KeyInsight.Perf.PerfRunner.Run(args[1]);
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

                string profilesDir = DataPathResolver.ResolveProfilesDir(AppDomain.CurrentDomain.BaseDirectory);
                KeyPolicyStore keyPolicies = new KeyPolicyStore(
                    Path.Combine(profilesDir, "key-policies.v1.json"));
                PolicyController.AttachStore(keyPolicies);
                PolicyController.Refresh(cfg);

                string dataDir = DataPathResolver.ResolveDataRoot(AppDomain.CurrentDomain.BaseDirectory);
                StatsEngine stats = new StatsEngine(dataDir, "KeyInsight");
                ForegroundTracker.Start();
                // 应用活跃时长（前台窗口估算；空闲不计入）
                AppTimeTracker.Start(dataDir);

                StatsEngineSink sink = new StatsEngineSink(stats);
                DebounceHook hook = new DebounceHook(cfg, sink);
                MouseHook mouseHook = new MouseHook(cfg, sink);
                HookSupervisor supervisor = new HookSupervisor(hook, mouseHook);
                supervisor.StartAll();

                // PR-6：更新后健康心跳——钩子成功安装即写入 nonce，独立 updater 据此判定回滚/成功
                if (healthNonce.Length > 0)
                {
                    try
                    {
                        string healthDir = Path.Combine(dataDir, "v2");
                        if (!Directory.Exists(healthDir)) Directory.CreateDirectory(healthDir);
                        File.WriteAllText(Path.Combine(healthDir, "health.json"),
                            "{\"nonce\":\"" + healthNonce.Replace("\"", "") + "\",\"utcTicks\":" + DateTime.UtcNow.Ticks + "}");
                    }
                    catch { }
                }
                RawInputTracker rawInput = new RawInputTracker(Path.Combine(dataDir, "v2"));
                rawInput.Start();

                // PR-5/6：更新编排器 + 路线目录（staging 在 data/update-staging，与用户数据隔离）
                KeyInsight.Update.Routing.RouteCatalog routeCatalog =
                    new KeyInsight.Update.Routing.RouteCatalog(Path.Combine(dataDir, "v2", "route-health.json"));
                KeyInsight.Update.Orchestration.UpdateOrchestrator orchestrator =
                    new KeyInsight.Update.Orchestration.UpdateOrchestrator(
                        routeCatalog, Path.Combine(dataDir, "update-staging"), null);

                // Device Center（综合外设管理平台 PR-2）：独立数据域，KDS 零接触
                KeyInsight.Services.DeviceCenter.DeviceCenterService deviceCenter =
                    new KeyInsight.Services.DeviceCenter.DeviceCenterService(
                        DataPathResolver.ResolveDeviceCenterDir(AppDomain.CurrentDomain.BaseDirectory));

                try
                {
                    using (TrayContext tray = new TrayContext(cfg, stats, hook, mouseHook,
                        keyPolicies, rawInput, supervisor, sink, orchestrator, routeCatalog,
                        deviceCenter))
                    {
                        Application.Run(tray);
                    }
                }
                finally
                {
                    try { deviceCenter.Dispose(); } catch { }
                    supervisor.Dispose();
                    hook.Dispose();
                    mouseHook.Dispose();
                    sink.Dispose();
                    rawInput.Dispose();
                    stats.Dispose();
                    ForegroundTracker.Stop();
            AppTimeTracker.Stop();
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
        private readonly KeyPolicyStore _keyPolicies;
        private readonly RawInputTracker _rawInput;
        private readonly HookSupervisor _supervisor;
        private readonly StatsEngineSink _sink;
        private readonly KeyInsight.Update.Orchestration.UpdateOrchestrator _updater;
        private readonly KeyInsight.Update.Routing.RouteCatalog _routeCatalog;
        private readonly KeyInsight.Services.DeviceCenter.DeviceCenterService _deviceCenter;
        private FloatingSummaryForm _floating;
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
        private bool _pendingUpdateAvailable;   // 检查发现新版本（托盘气泡点击 → 下载安装）
        private string _pendingUpdateVersion;
        private bool _updateBusy;
        private string _lastUpdateCheckResult;

        public TrayContext(Config cfg, StatsEngine stats, DebounceHook hook, MouseHook mouseHook,
            KeyPolicyStore keyPolicies, RawInputTracker rawInput, HookSupervisor supervisor, StatsEngineSink sink,
            KeyInsight.Update.Orchestration.UpdateOrchestrator updater,
            KeyInsight.Update.Routing.RouteCatalog routeCatalog,
            KeyInsight.Services.DeviceCenter.DeviceCenterService deviceCenter)
        {
            _cfg = cfg;
            _stats = stats;
            _hook = hook;
            _mouseHook = mouseHook;
            _keyPolicies = keyPolicies;
            _rawInput = rawInput;
            _supervisor = supervisor;
            _sink = sink;
            _updater = updater;
            _routeCatalog = routeCatalog;
            _deviceCenter = deviceCenter;
            _sync = SynchronizationContext.Current;

            ContextMenuStrip menu = new ContextMenuStrip();

            _openItem = new ToolStripMenuItem("打开面板");
            _openItem.Font = new Font(_openItem.Font, FontStyle.Bold);
            _openItem.Click += delegate { ShowDashboard(); };
            menu.Items.Add(_openItem);

            _toggleItem = new ToolStripMenuItem();
            _toggleItem.Click += delegate
            {
                _cfg.FilterEnabled = !_cfg.FilterEnabled;
                _cfg.Save();
                PolicyController.Refresh(_cfg);
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
                    PolicyController.Refresh(_cfg);
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
                string detail;
                bool all = _supervisor.RestartAll(out detail);
                _icon.ShowBalloonTip(2500, all ? "输入钩子已重新安装" : "重新安装失败",
                    all ? "键盘防抖与鼠标统计已重新生效" : detail,
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

            try { _deviceCenter.Start(); } catch { }
            _icon = new NotifyIcon();
            _icon.Icon = LoadIcon();
            _icon.ContextMenuStrip = menu;
            _icon.Text = "KeyInsight";
            _icon.Visible = true;
            _icon.DoubleClick += delegate { ShowDashboard(); };
            _icon.BalloonTipClicked += delegate { if (_pendingUpdateAvailable) InstallUpdate(); };

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 2000;
            _timer.Tick += delegate { UpdateMenu(); ApplyFloatingSummary(); };
            _timer.Start();

            UpdateMenu();
            ApplyFloatingSummary();
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
                    delegate { BeginUpdateCheck(true); }, _keyPolicies, _rawInput, _supervisor,
                    _sink, _updater, _routeCatalog, _deviceCenter);
            }
            _form.Show();
            if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
            _form.Activate();
            _form.BringToFront();
            _form.RefreshSnapshot();
        }

        private void ApplyFloatingSummary()
        {
            try
            {
            if (_cfg.FloatingSummary)
            {
                if (_floating == null || _floating.IsDisposed)
                {
                    _floating = new FloatingSummaryForm(_stats, _cfg);
                    _floating.Show();
                }
            }
            else if (_floating != null)
            {
                _floating.StopUpdates();
                _floating.Close();
                _floating = null;
            }
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
            }
        }

        private void UpdateMenu()
        {
            _toggleItem.Text = _cfg.FilterEnabled ? "防抖过滤：已开启" : "防抖过滤：已关闭（仍统计）";
            _toggleItem.Checked = _cfg.FilterEnabled;
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
            string hookState = _supervisor == null ? "" : " · " + _supervisor.StateText;
            string dropped = _sink != null && _sink.DroppedCount > 0 ? " · 统计不完整" : "";
            _statusItem.Text = "今日 " + total.ToString("N0") + " 键 · 鼠标 " + mouse.ToString("N0") +
                " 次 · 拦截 " + swallowed.ToString("N0") + hookState + dropped;

            string text = "KeyInsight · 今日 " + total.ToString("N0") + " 键 · 鼠标 " + mouse.ToString("N0") +
                " 次 · 拦截 " + _hook.SwallowedCount;
            if (text.Length > 63) text = text.Substring(0, 63);
            _icon.Text = text;
        }

        private void BeginUpdateCheck(bool manual)
        {
            if (_updateBusy) return;
            _updateBusy = true;
            _lastUpdateCheckResult = "正在检查更新…";
            PostUpdateStateChanged();
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (_updater != null)
                    {
                        // 复审方案 PR-3/PR-4：固定名 Release 清单（官方 + 公共代理）hedged 获取，
                        // 验签成功才可信；不再依赖 api.github.com。
                        // §2.2：只有验签+解析成功才写 LastUpdateCheckTicks，失败不清空 24h 自动重试窗口。
                        KeyInsight.Update.Orchestration.UpdateOrchestrator.CheckResult r =
                            _updater.CheckOnly(new Version(AppInfo.Version), "stable", _cfg.UpdateCohort);
                        _updater.SaveRouteHealth();
                        if (!r.Ok)
                        {
                            _lastUpdateCheckResult = "检查失败：" +
                                (string.IsNullOrEmpty(r.ErrorCategory) ? r.Error
                                    : r.ErrorCategory + " · " + r.Error);
                            if (manual)
                            {
                                PostBalloon("检查更新失败", r.Error, ToolTipIcon.Error);
                            }
                            return;
                        }
                        _cfg.LastUpdateCheckTicks = DateTime.UtcNow.Ticks;
                        _cfg.Save();

                        string via = string.IsNullOrEmpty(r.RouteName) ? "官方源" : r.RouteName;
                        if (r.UpdateAvailable)
                        {
                            _pendingUpdateAvailable = true;
                            _pendingUpdateVersion = r.VersionText;
                            _lastUpdateCheckResult = "发现新版本 " + r.VersionText + "（经由 " + via + "）";
                            PostBalloon("发现新版本 " + r.VersionText,
                                manual ? "点击这里下载并安装" : "点击托盘气泡或右键检查更新",
                                ToolTipIcon.Info);
                            if (manual && _sync != null)
                            {
                                _sync.Post(delegate { InstallUpdate(); }, null);
                            }
                        }
                        else
                        {
                            _pendingUpdateAvailable = false;
                            _lastUpdateCheckResult = "已是最新（最新发布 " + r.VersionText + " · 经由 " + via + "）";
                            if (manual)
                            {
                                PostBalloon("已是最新版本",
                                    "当前 v" + UpdateService.CurrentVersion + "（最新发布 " + r.VersionText + "）",
                                    ToolTipIcon.Info);
                            }
                        }
                        return;
                    }

                    // 兜底：编排器不可用时走旧通道（保留以隔离回归）
                    UpdateInfo info = UpdateService.Check();
                    if (info != null)
                    {
                        _cfg.LastUpdateCheckTicks = DateTime.UtcNow.Ticks;
                        _cfg.Save();
                        if (info.UpToDate)
                        {
                            _lastUpdateCheckResult = "已是最新（最新发布 " + info.Tag + "）";
                            if (manual)
                            {
                                PostBalloon("已是最新版本",
                                    "当前 v" + UpdateService.CurrentVersion + "（最新发布 " + info.Tag + "）",
                                    ToolTipIcon.Info);
                            }
                        }
                        else
                        {
                            _pendingUpdate = info;
                            _pendingUpdateAvailable = true;
                            _lastUpdateCheckResult = "发现新版本 v" + info.Version;
                            PostBalloon("发现新版本 v" + info.Version,
                                manual ? "点击这里下载并安装" : "点击托盘气泡或右键检查更新",
                                ToolTipIcon.Info);
                            if (manual && _sync != null)
                            {
                                _sync.Post(delegate { InstallUpdate(); }, null);
                            }
                        }
                    }
                    else
                    {
                        _lastUpdateCheckResult = "检查失败：发布信息不可用";
                        if (manual)
                        {
                            PostBalloon("检查更新失败", "无法解析发布信息", ToolTipIcon.Error);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _lastUpdateCheckResult = "检查失败：" + ex.Message;
                    if (manual) PostBalloon("检查更新失败", ex.Message, ToolTipIcon.Error);
                }
                finally
                {
                    _updateBusy = false;
                    PostUpdateStateChanged();
                }
            });
        }

        private void InstallUpdate()
        {
            if (_updateBusy) return;

            // 编排链路（复审方案 PR-4/PR-5）：多线路下载 + Range 续传 + 整文件 SHA-256，
            // portable 安装形态下载 portable zip 原样升级，安装失败由 Updater 回滚。
            if (_updater != null)
            {
                _updateBusy = true;
                PostBalloon("正在下载更新", "自动选择最快线路下载", ToolTipIcon.Info);
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        bool setupInstall = File.Exists(
                            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "unins000.exe"));
                        string assetId = setupInstall ? "windows-x64-setup" : "windows-x64-portable";
                        bool ready = _updater.CheckAndDownload(
                            new Version(AppInfo.Version), "stable", _cfg.UpdateCohort, assetId);
                        _updater.SaveRouteHealth();
                        if (!ready)
                        {
                            if (_updater.State != KeyInsight.Update.Orchestration.UpdateState.UpToDate)
                            {
                                PostBalloon("更新下载失败",
                                    _updater.LastError ?? _updater.LastErrorCategory ?? "未知错误",
                                    ToolTipIcon.Error);
                                _lastUpdateCheckResult = "更新下载失败：" +
                                    (_updater.LastErrorCategory ?? "未知");
                            }
                            return;
                        }
                        string path = _updater.ReadyInstallerPath;
                        if (_sync != null)
                        {
                            _sync.Post(delegate { PromptInstall(path, null); }, null);
                        }
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
                return;
            }

            // 兜底：旧下载通道
            UpdateInfo info = _pendingUpdate;
            if (info == null) return;
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
            string verText = info != null ? ("新版本 v" + info.Version + " 已下载。")
                : "新版本安装包已下载并通过整文件 SHA-256 校验。";
            bool go = Dialogs.Confirm(null, "KeyInsight 更新",
                verText + "\n\n是否立即安装？安装前会自动备份当前版本，完成后重新启动；安装失败自动回滚。");
            if (!go) return;

            try
            {
                if (info != null) UpdateService.RunInstaller(path);
                else LaunchUpdaterHelper(path);
                ExitApp();
            }
            catch (Exception ex)
            {
                Dialogs.Error(null, "启动安装程序失败", ex.Message);
            }
        }

        /// <summary>经由独立 KeyInsight.Updater.exe 安装（备份 → 应用 → 健康检查 → 失败回滚）。</summary>
        private void LaunchUpdaterHelper(string installerPath)
        {
            string updaterExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "KeyInsight.Updater.exe");
            if (!File.Exists(updaterExe))
            {
                throw new FileNotFoundException("缺少 KeyInsight.Updater.exe，无法安全安装", updaterExe);
            }
            bool setupInstall = File.Exists(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "unins000.exe"));
            string dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");

            System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo();
            psi.FileName = updaterExe;
            psi.Arguments = "--wait-pid " + System.Diagnostics.Process.GetCurrentProcess().Id +
                " --installer \"" + installerPath + "\"" +
                " --package " + (setupInstall ? "setup" : "portable") +
                " --target \"" + AppDomain.CurrentDomain.BaseDirectory + "\"" +
                " --health-nonce " + Guid.NewGuid().ToString("N") +
                " --health-timeout 60";
            psi.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
            psi.UseShellExecute = true;
            System.Diagnostics.Process.Start(psi);
        }

        private void PostUpdateStateChanged()
        {
            if (_sync == null) return;
            _sync.Post(delegate
            {
                try
                {
                    if (_form != null && !_form.IsDisposed)
                    {
                        _form.OnUpdateStateChanged(_updateBusy, _lastUpdateCheckResult);
                    }
                }
                catch { }
            }, null);
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
            if (_floating != null) { _floating.StopUpdates(); _floating.Close(); _floating = null; }
            _hook.Dispose();
            _mouseHook.Dispose();
            if (_rawInput != null) _rawInput.Dispose();
            _stats.Dispose();
            ForegroundTracker.Stop();
            _icon.Dispose();
            ExitThread();
        }
    }

    /// <summary>--migrate-data：便携 → 安装数据布局的显式迁移（P0-06）。</summary>
    internal static class MigrateDataRunner
    {
        public static int Run()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            Console.WriteLine("source: " + exeDir);
            Console.WriteLine("target: " + DataPathResolver.InstalledRoot);
            MigrationResult r = DataMigrator.MigratePortableToInstalled(exeDir);
            Console.WriteLine("ok=" + r.Ok + " alreadyMigrated=" + r.AlreadyMigrated + " : " + r.Message);
            if (r.Files.Count > 0)
            {
                foreach (string f in r.Files) Console.WriteLine("  " + f);
            }
            if (!string.IsNullOrEmpty(r.ManifestPath)) Console.WriteLine("manifest: " + r.ManifestPath);
            if (!string.IsNullOrEmpty(r.BackupDir)) Console.WriteLine("backup: " + r.BackupDir);
            if (!r.Ok) Console.WriteLine("rollback: no marker written; source data untouched. Restore from backup dir if needed.");
            return r.Ok ? 0 : 1;
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
