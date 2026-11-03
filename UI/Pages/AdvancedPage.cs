using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using System.Diagnostics;
using System.IO;
using System.Threading;
using KeyInsight.Hook;
using KeyInsight.Stats;

using KeyInsight;

namespace KeyInsight.UI.Pages
{
    /// <summary>
    /// 高级与设备（设计 §5.1/§6.1/§9）：钩子运行状态、采集/诊断/悬浮摘要开关、
    /// 数据完整性（遥测丢弃）、Raw Input 设备画像列表与滚轮原始 delta。
    /// 设备统计仅观察，不参与过滤（ADR-005）。
    /// </summary>
    public sealed class AdvancedPage : PageBase
    {
        private readonly IShellActions _shell;
        private Rectangle _switchCapture;
        private Rectangle _switchDiagnostics;
        private Rectangle _switchFloating;
        private Rectangle _reinstallRect;
        private Rectangle _updateCheckRect;
        private Rectangle _updateInstallRect;
        private Rectangle _routeDiagRect;
        private long _updateDone;
        private long _updateTotal;
        private bool _updateRunning;
        private string _updateStateText = "空闲";
        private string _updateResultText = "";

        public override string Title { get { return "高级与设备"; } }
        public override string Subtitle { get { return "钩子状态、采集与诊断开关、输入设备画像（仅统计）"; } }

        public AdvancedPage(IShellActions shell)
        {
            _shell = shell;
        }

        protected override void Draw(Graphics g)
        {
            Palette p = Theme.P;
            DrawHeader(g);

            int y = 64;
            int gap = 14;
            int leftW = (Width - gap) / 2;
            int rightW = Width - leftW - gap;
            int cardH = Math.Min(420, ContentH - y - 16);
            int updateH = Math.Min(200, ContentH - y - cardH - gap * 2);
            if (updateH < 130) updateH = 130;

            DrawControls(g, new Rectangle(0, y, leftW, cardH), p);
            DrawDevices(g, new Rectangle(leftW + gap, y, rightW, cardH), p);
            DrawUpdateCard(g, new Rectangle(0, y + cardH + gap, ContentW, updateH), p);
        }

        private void DrawUpdateCard(Graphics g, Rectangle card, Palette p)
        {
            SectionCard(g, card, "更新与线路",
                "签名清单 + 多线路下载 + 独立更新器 + 失败回滚（路线图 PR-4/5/6）");

            int x = card.X + 18;
            int w = card.Width - 36;
            int y = card.Y + 56;

            // 状态行
            string stateLabel = "状态";
            string stateValue = _updateRunning ? _updateStateText : ("空闲 · " + _updateResultText);
            if (!_updateRunning && _updateResultText.Length == 0) stateValue = "空闲";
            Color sc = _updateRunning ? p.Info : p.Muted;
            Ui.TextLeft(g, stateLabel, Ui.BodySmall, p.Muted, new Rectangle(x, y, 50, 20));
            Ui.TextLeftEllipsis(g, stateValue, Ui.BodySmall, sc, new Rectangle(x + 54, y, w - 54 - 260, 20));

            // 操作按钮
            bool canInstall = _shell.Updater != null && _shell.Updater.State ==
                KeyInsight.Update.Orchestration.UpdateState.ReadyToInstall;
            _updateCheckRect = new Rectangle(x + w - 300, y - 2, 92, 26);
            _updateInstallRect = new Rectangle(x + w - 200, y - 2, 92, 26);
            _routeDiagRect = new Rectangle(x + w - 100, y - 2, 100, 26);
            Charts.Button(g, _updateCheckRect, _updateRunning ? "…" : "检查更新", false, p);
            Charts.Button(g, _updateInstallRect, "安装并重启", canInstall, p);
            Charts.Button(g, _routeDiagRect, "线路诊断", false, p);
            y += 34;

            // 下载进度条
            if (_updateRunning && _updateTotal > 0)
            {
                double frac = Math.Min(1.0, _updateDone / (double)_updateTotal);
                Ui.TextLeft(g, "下载", Ui.BodySmall, p.Muted, new Rectangle(x, y, 50, 20));
                Rectangle bar = new Rectangle(x + 54, y + 4, w - 54 - 100, 12);
                Ui.FillRound(g, bar, 6, p.HeatEmpty);
                if (frac > 0)
                {
                    Ui.FillRound(g, new RectangleF(bar.X, bar.Y, bar.Width * (float)frac, bar.Height), 6, p.Accent1);
                }
                Ui.TextRight(g, (_updateDone / 1024) + " / " + (_updateTotal / 1024) + " KB",
                    Ui.Label, p.Dim, new Rectangle(x + w - 96, y, 96, 20));
                y += 26;
            }

            // 线路列表（前 5 条）
            if (_shell.RouteCatalog != null)
            {
                List<KeyInsight.Update.Routing.RouteEndpoint> routes = _shell.RouteCatalog.Routes;
                int shown = Math.Min(routes.Count, 5);
                for (int i = 0; i < shown; i++)
                {
                    KeyInsight.Update.Routing.RouteEndpoint r = routes[i];
                    KeyInsight.Update.Routing.RouteHealth h = _shell.RouteCatalog.HealthOf(r);
                    double score = KeyInsight.Update.Routing.RouteScorer.Score(h) * 100;
                    string health = h.State.ToString();
                    Color hc = h.State == KeyInsight.Update.Routing.RouteHealthState.Healthy ? p.Good
                        : h.State == KeyInsight.Update.Routing.RouteHealthState.Quarantined ? p.Bad
                        : h.State == KeyInsight.Update.Routing.RouteHealthState.OpenCircuit ? p.Warn
                        : p.Muted;
                    int ry = y + i * 22;
                    Ui.DrawDot(g, x + 5, ry + 10, 3, hc);
                    Ui.TextLeft(g, r.Name, Ui.BodySmall, p.Text, new Rectangle(x + 16, ry, 120, 20));
                    Ui.TextLeft(g, health, Ui.Label, hc, new Rectangle(x + 140, ry, 90, 20));
                    Ui.TextRight(g, score.ToString("F0") + " 分", Ui.Label, p.Muted,
                        new Rectangle(x + w - 80, ry, 80, 20));
                }
            }
        }

        private void DrawControls(Graphics g, Rectangle card, Palette p)
        {
            SectionCard(g, card, "输入与诊断", "状态仅代表“进程认为已安装 + 最近事件时间”，不是健康证明");
            int x = card.X + 18;
            int w = card.Width - 36;
            int y = card.Y + 62;

            DrawToggleRow(g, p, x, y, w, "按键采集", "关闭后钩子只放行、不统计（过滤独立）", _shell.CaptureEnabled, out _switchCapture);
            y += 50;
            DrawToggleRow(g, p, x, y, w, "诊断聚合", "记录拦截间隔直方图（用于策略模拟/画像，仅聚合摘要）",
                _shell.DiagnosticsEnabled, out _switchDiagnostics);
            y += 50;
            DrawToggleRow(g, p, x, y, w, "桌面悬浮摘要", "置顶小窗显示今日按键/鼠标/拦截（开启后约 2 秒内出现）", _shell.FloatingSummaryOn, out _switchFloating);
            y += 56;

            Ui.Divider(g, x, y, w, p);
            y += 8;

            HookSupervisor sup = _shell.Supervisor;
            string state = sup == null ? "未知（截图模式）" : sup.StateText;
            _reinstallRect = new Rectangle(x + w - 96, y - 2, 96, 24);
            if (sup != null) Charts.Button(g, _reinstallRect, "重新安装", false, p);
            Ui.TextLeft(g, "钩子状态", Ui.BodySmall, p.Muted, new Rectangle(x, y, 90, 20));
            Ui.TextLeft(g, state, Ui.BodySmall, p.Text, new Rectangle(x + 92, y, w - 190, 20));
            y += 22;
            Ui.TextLeft(g, "最近事件", Ui.BodySmall, p.Muted, new Rectangle(x, y, 90, 20));
            Ui.TextLeft(g, sup == null ? "—" : sup.LastEventAgoText, Ui.BodySmall, p.Text,
                new Rectangle(x + 92, y, w - 92, 20));
            y += 22;
            if (sup != null && !string.IsNullOrEmpty(sup.LastError))
            {
                Ui.TextLeftEllipsis(g, "最近错误：" + sup.LastError, Ui.Label, p.Bad,
                    new Rectangle(x, y, w, 18));
                y += 20;
            }

            long dropped = _shell.TelemetryDropped;
            Color dc = dropped > 0 ? p.Warn : p.Good;
            Ui.TextLeftEllipsis(g,
                dropped > 0
                    ? "遥测队列丢弃 " + Charts.Number(dropped) + " 条 —— 统计可能不完整（输入未受影响）"
                    : "遥测队列无丢弃（数据完整）",
                Ui.Label, dc, new Rectangle(x, y, w, 18));
        }

        private void DrawToggleRow(Graphics g, Palette p, int x, int y, int w,
            string title, string sub, bool on, out Rectangle switchRect)
        {
            Ui.TextLeft(g, title, Ui.Body, p.Text, new Rectangle(x, y, w - 60, 22));
            Ui.TextLeftEllipsis(g, sub, Ui.Label, p.Dim, new Rectangle(x, y + 22, w - 60, 18));
            switchRect = new Rectangle(x + w - 44, y + 6, 44, 24);
            Charts.Switch(g, switchRect, on, p);
        }

        private void DrawDevices(Graphics g, Rectangle card, Palette p)
        {
            List<DeviceProfile> devices = _shell.RawInput == null
                ? new List<DeviceProfile>()
                : _shell.RawInput.SnapshotProfiles();
            SectionCard(g, card, "输入设备（Raw Input）",
                devices.Count + " 个设备 · 仅统计，不参与任何过滤决策");

            int x = card.X + 18;
            int w = card.Width - 36;
            int y = card.Y + 62;

            if (devices.Count == 0)
            {
                Ui.TextLeft(g, "尚未检测到设备事件。移动鼠标或按键后刷新即可看到。",
                    Ui.BodySmall, p.Dim, new Rectangle(x, y, w, 22));
            }
            else
            {
                int shown = Math.Min(devices.Count, 8);
                for (int i = 0; i < shown; i++)
                {
                    DeviceProfile d = devices[i];
                    int ry = y + i * 44;
                    Ui.DrawDot(g, x + 5, ry + 14, 4, d.Kind == "keyboard" ? p.Accent1 : p.Accent3);
                    Ui.TextLeft(g, d.DisplayName, Ui.BodySmall, p.Text, new Rectangle(x + 16, ry, 160, 20));
                    Ui.TextRight(g, Charts.Number(d.EventCount) + " 次", Ui.Label, p.Muted,
                        new Rectangle(x + w - 120, ry, 120, 20));
                    Ui.TextLeftEllipsis(g, d.IdSuffix, Ui.Label, p.Dim, new Rectangle(x + 16, ry + 20, w - 20, 16));
                }
                if (devices.Count > shown)
                {
                    Ui.TextLeft(g, "+ 还有 " + (devices.Count - shown) + " 个设备未显示",
                        Ui.Label, p.Dim, new Rectangle(x, y + shown * 44, w, 18));
                }
            }

            int fy = card.Bottom - 66;
            Ui.Divider(g, x, fy - 8, w, p);
            Ui.TextLeft(g, "滚轮原始 delta（范围内）", Ui.BodySmall, p.Muted, new Rectangle(x, fy, 200, 20));
            Ui.TextRight(g, Charts.Number(Snap == null ? 0 : Snap.RawWheelDeltaTotal), Ui.BodySmall, p.Text,
                new Rectangle(x + 200, fy, w - 200, 20));
            Ui.TextLeftEllipsis(g,
                "事件 " + Charts.Number(Snap == null ? 0 : Snap.RawWheelEventsTotal) +
                " · 保留高分辨率小步滚动；旧「滚轮格数」口径不变",
                Ui.Label, p.Dim, new Rectangle(x, fy + 22, w, 18));
        }

        private void StartUpdateCheck()
        {
            if (_shell.Updater == null || _updateRunning) return;
            _updateRunning = true;
            _updateStateText = "检查中…";
            _updateDone = 0;
            _updateTotal = 0;

            _shell.Updater.StateChanged += delegate(KeyInsight.Update.Orchestration.UpdateState st, string detail)
            {
                _updateStateText = st + (string.IsNullOrEmpty(detail) ? "" : " · " + detail);
                Invalidate();
            };
            _shell.Updater.DownloadProgress += delegate(long done, long total)
            {
                _updateDone = done;
                _updateTotal = total;
                Invalidate();
            };

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Version current = new Version(AppInfo.Version);
                    bool ready = _shell.Updater.CheckAndDownload(current, "stable",
                        _shell.Cfg.UpdateCohort, "windows-x64-setup");
                    _shell.Updater.SaveRouteHealth();
                    _updateResultText = ready
                        ? "安装包已就绪，点击「安装并重启」完成更新"
                        : (_shell.Updater.State == KeyInsight.Update.Orchestration.UpdateState.UpToDate
                            ? "已是最新版本" : "更新失败：" + (_shell.Updater.LastError ?? "未知"));
                }
                catch (Exception ex)
                {
                    _updateResultText = "更新异常：" + ex.Message;
                }
                finally
                {
                    _updateRunning = false;
                    Invalidate();
                }
            });
        }

        private void InstallReadyUpdate()
        {
            if (_shell.Updater == null || _shell.Updater.State !=
                KeyInsight.Update.Orchestration.UpdateState.ReadyToInstall) return;
            string installerPath = _shell.Updater.ReadyInstallerPath;
            string updaterExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "KeyInsight.Updater.exe");
            if (!File.Exists(updaterExe))
            {
                _updateResultText = "缺少 KeyInsight.Updater.exe";
                Invalidate();
                return;
            }

            bool isPortable = true; // 3.2 起默认保持 portable 形态
            string package = File.Exists(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "unins000.exe")) ? "setup" : "portable";
            if (!isPortable && package == "portable") { /* 便携版升级 */ }

            string nonce = Guid.NewGuid().ToString("N");
            string dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = updaterExe;
            psi.Arguments = "--wait-pid " + Process.GetCurrentProcess().Id +
                " --installer \"" + installerPath + "\"" +
                " --package " + package +
                " --target \"" + AppDomain.CurrentDomain.BaseDirectory +
                "\" --data-dir \"" + dataDir +
                "\" --health-nonce " + nonce +
                " --health-timeout 60";
            psi.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
            psi.UseShellExecute = true;
            Process.Start(psi);

            // 给 updater 几秒启动时间，然后正常退出主程序
            Thread.Sleep(1500);
            Application.Exit();
        }

        private void RunRouteDiagnostics()
        {
            if (_shell.RouteCatalog == null) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                var result = KeyInsight.Update.Routing.RouteProbe.FetchManifestWithHedging(_shell.RouteCatalog);
                _shell.RouteCatalog.SaveHealth();
                if (result.Ok)
                {
                    _updateResultText = "线路诊断：成功经由 " + result.RouteName +
                        "（TTFB " + result.TtfbMs.ToString("F0") + "ms），清单验签通过";
                }
                else
                {
                    _updateResultText = "线路诊断：全部失败（" + result.ErrorCategory + "）";
                }
                Invalidate();
            });
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Point pt = ToContent(e.Location);

            if (_switchCapture.Contains(pt)) { _shell.SetCaptureEnabled(!_shell.CaptureEnabled); Invalidate(); return; }
            if (_switchDiagnostics.Contains(pt)) { _shell.SetDiagnosticsEnabled(!_shell.DiagnosticsEnabled); Invalidate(); return; }
            if (_switchFloating.Contains(pt)) { _shell.SetFloatingSummary(!_shell.FloatingSummaryOn); Invalidate(); return; }

            if (_updateCheckRect.Contains(pt) && !_updateRunning)
            {
                StartUpdateCheck();
                Invalidate();
                return;
            }
            if (_updateInstallRect.Contains(pt))
            {
                InstallReadyUpdate();
                return;
            }
            if (_routeDiagRect.Contains(pt))
            {
                RunRouteDiagnostics();
                Invalidate();
                return;
            }
            // 手动恢复入口（设计 §5.1）：重装输入钩子
            if (_shell.Supervisor != null && _reinstallRect.Contains(pt))
            {
                string detail;
                bool ok = _shell.Supervisor.RestartAll(out detail);
                MessageBox.Show(FindForm(), ok ? "输入钩子已重新安装。\n" + detail : "重新安装失败：" + detail,
                    "KeyInsight", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Error);
                _shell.RefreshSnapshot();
                Invalidate();
            }
        }
    }
}
