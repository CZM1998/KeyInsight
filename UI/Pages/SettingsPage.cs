using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using KeyInsight.Stats;

namespace KeyInsight.UI.Pages
{
    public sealed class SettingsPage : PageBase
    {
        private readonly IShellActions _shell;
        private Rectangle _switchDebounce;
        private Rectangle _switchInjected;
        private Rectangle _switchStartup;
        private Rectangle[] _thresholdRects = new Rectangle[0];
        private Rectangle[] _themeRects = new Rectangle[0];
        private Rectangle[] _modeRects = new Rectangle[0];
        private Rectangle _switchWhitelist;
        private Rectangle _weeklyRect;
        private static readonly string[] ModeNames = new string[] { "观察", "兼容 v2.1", "单键保守" };
        private Rectangle _reinstallRect;
        private Rectangle _openFolderRect;
        private Rectangle _exportRect;
        private Rectangle _clearRect;
        private Rectangle _manageRect;
        private Rectangle _checkUpdateRect;
        private int _busyPhase;
        private Rectangle _autoUpdateRect;
        private Rectangle[] _monthDeleteRects = new Rectangle[0];
        private string[] _monthKeys = new string[0];
        private static readonly int[] Windows = new int[] { 10, 15, 20, 25, 30 };

        public override string Title { get { return "设置与数据"; } }
        public override string Subtitle { get { return "防抖、启动、主题，以及本地数据的清理与导出"; } }

        private readonly Timer _animTimer;

        public SettingsPage(IShellActions shell)
        {
            _shell = shell;
            // 更新检查动画：busy 期间 120ms 重绘；空闲时停止
            _animTimer = new Timer();
            _animTimer.Interval = 120;
            _animTimer.Tick += delegate
            {
                if (_shell.UpdateCheckBusy) Invalidate();
                else _animTimer.Stop();
            };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_shell.UpdateCheckBusy && !_animTimer.Enabled) _animTimer.Start();
        }

        protected override void Draw(Graphics g)
        {
            Palette p = Theme.P;
            DrawHeader(g);

            int top = 64;
            int gap = 14;
            int leftW = (ContentW - gap) / 2;
            int cardH = 470;
            int rightW = ContentW - leftW - gap;

            Rectangle left = new Rectangle(0, top, leftW, cardH);
            Rectangle right = new Rectangle(leftW + gap, top, rightW, cardH);
            Rectangle bottom = new Rectangle(0, top + cardH + gap, ContentW, ContentH - (top + cardH + gap));

            DrawBehavior(g, left, p);
            DrawData(g, right, p);
            DrawAbout(g, bottom, p);
        }

        private void DrawBehavior(Graphics g, Rectangle card, Palette p)
        {
            Charts.Card(g, card, p);
            Ui.TextLeft(g, "防抖与启动", Ui.Section, p.Text, new Rectangle(card.X + 18, card.Y + 14, card.Width - 36, 24));
            Ui.TextLeft(g, "修改后立即生效，配置保存在 KeyInsight.ini", Ui.Label, p.Dim,
                new Rectangle(card.X + 18, card.Y + 36, card.Width - 36, 18));

            int x = card.X + 18;
            int w = card.Width - 36;
            int y = card.Y + 62;

            DrawRow(g, p, x, y, w, "防抖开关", "仅控制过滤；关闭后仍继续统计按键", _shell.Cfg.FilterEnabled);
            _switchDebounce = new Rectangle(x + w - 44, y + 6, 44, 24);
            Charts.Switch(g, _switchDebounce, _shell.Cfg.FilterEnabled, p);

            y += 48;
            Ui.TextLeft(g, "防抖窗口", Ui.Body, p.Text, new Rectangle(x, y, w, 22));
            string[] thLabels = new string[Windows.Length];
            for (int i = 0; i < Windows.Length; i++) thLabels[i] = Windows[i] + " ms";
            int thSel = 1;
            for (int i = 0; i < Windows.Length; i++) if (Windows[i] == _shell.Cfg.ThresholdMs) thSel = i;
            DrawChips(g, thLabels, thSel, x, y + 26, 24, out _thresholdRects);

            y += 62;
            Ui.TextLeft(g, "策略模式", Ui.Body, p.Text, new Rectangle(x, y, w, 22));
            Ui.TextLeftEllipsis(g, "「单键保守」只拦截按键健康页中显式启用的键", Ui.Label, p.Dim,
                new Rectangle(x + 90, y + 3, w - 90, 18));
            int msel = Math.Max(0, Math.Min(2, _shell.Cfg.PolicyMode));
            DrawChips(g, ModeNames, msel, x, y + 24, 24, out _modeRects);

            y += 60;
            DrawRow(g, p, x, y, w, "跳过注入事件", "忽略 SendInput 等软件模拟的按键", _shell.Cfg.SkipInjected);
            _switchInjected = new Rectangle(x + w - 44, y + 6, 44, 24);
            Charts.Switch(g, _switchInjected, _shell.Cfg.SkipInjected, p);

            y += 46;
            string currentApp = Stats.ForegroundTracker.CurrentApp;
            bool appWhitelisted = IsWhitelisted(currentApp);
            DrawRow(g, p, x, y, w, "当前应用永不拦截",
                "当前前台：" + currentApp + (appWhitelisted ? "（已在白名单）" : ""), appWhitelisted);
            _switchWhitelist = new Rectangle(x + w - 44, y + 6, 44, 24);
            Charts.Switch(g, _switchWhitelist, appWhitelisted, p);

            y += 46;
            DrawRow(g, p, x, y, w, "开机自启", "登录 Windows 后自动运行 KeyInsight", _shell.AutoStart);
            _switchStartup = new Rectangle(x + w - 44, y + 6, 44, 24);
            Charts.Switch(g, _switchStartup, _shell.AutoStart, p);

            y += 46;
            Ui.TextLeft(g, "主题", Ui.Body, p.Text, new Rectangle(x, y, w, 22));
            string[] themes = new string[] { "跟随系统", "深色", "浅色" };
            int tsel = (int)Theme.Mode;
            DrawChips(g, themes, tsel, x, y + 24, 24, out _themeRects);

            _reinstallRect = new Rectangle(x, card.Bottom - 44, 168, 32);
            Charts.Button(g, _reinstallRect, "重新安装输入钩子", false, p);
        }

        private void DrawRow(Graphics g, Palette p, int x, int y, int w, string title, string sub, bool on)
        {
            Ui.TextLeft(g, title, Ui.Body, p.Text, new Rectangle(x, y, w - 60, 22));
            Ui.TextLeftEllipsis(g, sub, Ui.Label, p.Dim, new Rectangle(x, y + 22, w - 60, 18));
        }

        private void DrawData(Graphics g, Rectangle card, Palette p)
        {
            Charts.Card(g, card, p);
            Ui.TextLeft(g, "数据管理", Ui.Section, p.Text, new Rectangle(card.X + 18, card.Y + 14, card.Width - 36, 24));
            Ui.TextLeft(g, "按月保存，永久保留；可随时删除某个月", Ui.Label, p.Dim,
                new Rectangle(card.X + 18, card.Y + 36, card.Width - 36, 18));

            int x = card.X + 18;
            int w = card.Width - 36;

            Ui.TextLeft(g, "数据目录", Ui.Label, p.Muted, new Rectangle(x, card.Y + 62, 100, 18));
            Ui.TextLeftEllipsis(g, _shell.Source.DataDirectory, Ui.Mono, p.Text,
                new Rectangle(x, card.Y + 82, w - 110, 20));
            _openFolderRect = new Rectangle(x + w - 100, card.Y + 62, 100, 28);
            Charts.Button(g, _openFolderRect, "打开文件夹", false, p);

            List<MonthInfo> months = Snap.Months;
            if (months == null) months = new List<MonthInfo>();
            List<MonthInfo> valid = new List<MonthInfo>();
            for (int i = 0; i < months.Count; i++)
            {
                if (months[i].Year <= 0) continue;
                valid.Add(months[i]);
            }
            Ui.TextLeft(g, "月份文件（" + valid.Count + "）", Ui.BodySmall, p.Muted, new Rectangle(x, card.Y + 116, 200, 20));
            _manageRect = new Rectangle(x + w - 92, card.Y + 112, 92, 24);
            Charts.Button(g, _manageRect, "管理全部", false, p);

            int listY = card.Y + 140;
            int rowH = 28;
            int maxRows = 6;
            if (valid.Count == 0)
            {
                Ui.TextLeft(g, "还没有数据文件", Ui.BodySmall, p.Dim, new Rectangle(x, listY, w, 22));
                _monthKeys = new string[0];
                _monthDeleteRects = new Rectangle[0];
            }
            else
            {
                int count = Math.Min(maxRows, valid.Count);
                _monthKeys = new string[count];
                _monthDeleteRects = new Rectangle[count];
                for (int i = 0; i < count; i++)
                {
                    MonthInfo m = valid[i];
                    int y = listY + i * rowH;
                    Ui.TextLeft(g, m.Year + " 年 " + m.Month.ToString("D2") + " 月", Ui.BodySmall, p.Text,
                        new Rectangle(x, y, 120, rowH));
                    Ui.TextRight(g, FormatSize(m.Size), Ui.Label, p.Dim, new Rectangle(x + 130, y, 70, rowH));
                    Ui.TextRight(g, m.Loaded ? Charts.Number(m.Total) + " 键" : "—", Ui.Label, p.Muted,
                        new Rectangle(x + 206, y, w - 300, rowH));
                    _monthDeleteRects[i] = new Rectangle(x + w - 48, y + 3, 48, 22);
                    _monthKeys[i] = m.Key;
                    bool hover = _monthDeleteRects[i].Contains(CursorToContent());
                    Ui.TextCenter(g, "删除", Ui.Label, hover ? p.Bad : p.Dim, _monthDeleteRects[i]);
                }
                if (valid.Count > maxRows)
                {
                    Ui.TextLeft(g, "+ 还有 " + (valid.Count - maxRows) + " 个月未显示", Ui.Label, p.Dim,
                        new Rectangle(x, listY + count * rowH, w, 18));
                }
            }

            int by = card.Bottom - 52;
            int btw = (w - 16) / 3;
            _exportRect = new Rectangle(x, by, btw, 32);
            _weeklyRect = new Rectangle(x + btw + 8, by, btw, 32);
            _clearRect = new Rectangle(x + (btw + 8) * 2, by, btw, 32);
            Charts.Button(g, _exportRect, "导出 CSV", false, p);
            Charts.Button(g, _weeklyRect, "生成本周报告", false, p);
            Charts.Button(g, _clearRect, "清空全部数据", false, p);
        }

        private void DrawAbout(Graphics g, Rectangle card, Palette p)
        {
            Charts.Card(g, card, p);
            Ui.TextLeft(g, "关于与隐私", Ui.Section, p.Text, new Rectangle(card.X + 18, card.Y + 14, card.Width - 36, 24));
            Ui.TextLeft(g, "本工具只记录虚拟键码、按键次数、时间与前台进程名；不记录窗口标题，不记录输入内容，不上传任何数据。",
                Ui.BodySmall, p.Muted, new Rectangle(card.X + 18, card.Y + 48, card.Width - 36, 22));
            Ui.TextLeft(g, "数据全部保存在本机，你可以按月删除，或点「清空全部数据」彻底清除。",
                Ui.BodySmall, p.Muted, new Rectangle(card.X + 18, card.Y + 74, card.Width - 36, 22));
            Ui.TextLeft(g, "KeyInsight v" + AppInfo.Version + " · 防抖基于 WH_KEYBOARD_LL · 使用前行者 X87S 实测校准",
                Ui.Label, p.Dim, new Rectangle(card.X + 18, card.Bottom - 30, card.Width - 200, 18));

            _autoUpdateRect = new Rectangle(card.Right - 188, card.Bottom - 42, 40, 22);
            Charts.Switch(g, _autoUpdateRect, _shell.AutoCheckUpdates, p);
            Ui.TextLeft(g, "自动检查更新", Ui.BodySmall, p.Muted,
                new Rectangle(card.Right - 300, card.Bottom - 40, 108, 20));

            bool busy = _shell.UpdateCheckBusy;
            _checkUpdateRect = new Rectangle(card.Right - 140, card.Bottom - 52, 104, 28);
            if (busy)
            {
                // 动画：转圈点阵（画在按钮文字前）
                if (_busyPhase == 0) _busyPhase = 1;
                Charts.Button(g, _checkUpdateRect, "检查中" + new string('.', (Environment.TickCount / 400) % 4), false, p);
                int cx = _checkUpdateRect.X + 16;
                int cy = _checkUpdateRect.Y + _checkUpdateRect.Height / 2;
                for (int i = 0; i < 6; i++)
                {
                    double ang = (Environment.TickCount / 120.0) * Math.PI / 3 + i * Math.PI / 3;
                    int dx = (int)Math.Round(Math.Cos(ang) * 5);
                    int dy = (int)Math.Round(Math.Sin(ang) * 5);
                    int alpha = 255 - i * 34;
                    Ui.DrawDot(g, cx + dx, cy + dy, 2, Ui.WithAlpha(p.Accent1, alpha));
                }
            }
            else
            {
                Charts.Button(g, _checkUpdateRect, "检查更新", false, p);
            }

            string checkResult = _shell.LastUpdateCheckResult;
            if (!string.IsNullOrEmpty(checkResult))
            {
                Color rc = checkResult.StartsWith("发现", StringComparison.Ordinal) ? p.Good
                    : checkResult.StartsWith("检查失败", StringComparison.Ordinal) ? p.Bad : p.Muted;
                Ui.TextLeftEllipsis(g, checkResult, Ui.Label, rc,
                    new Rectangle(card.X + 18, card.Bottom - 50, card.Width - 36 - 150, 18));
            }

        }

        private bool IsWhitelisted(string app)
        {
            if (string.IsNullOrEmpty(app) || app == "unknown") return false;
            string[] list = Core.Policy.PolicyController.ParseWhitelist(_shell.Cfg.FilterAppWhitelist);
            for (int i = 0; i < list.Length; i++)
            {
                if (string.Equals(list[i], app, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private void ToggleWhitelist(string app)
        {
            if (string.IsNullOrEmpty(app) || app == "unknown") return;
            string[] list = Core.Policy.PolicyController.ParseWhitelist(_shell.Cfg.FilterAppWhitelist);
            List<string> next = new List<string>(list);
            bool removed = false;
            for (int i = 0; i < next.Count; i++)
            {
                if (string.Equals(next[i], app, StringComparison.OrdinalIgnoreCase))
                {
                    next.RemoveAt(i);
                    removed = true;
                    break;
                }
            }
            if (!removed) next.Add(app.ToLowerInvariant());
            _shell.Cfg.FilterAppWhitelist = string.Join(",", next.ToArray());
            _shell.Cfg.Save();
            _shell.RefreshPolicy();
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F0") + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("F1") + " MB";
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Point pt = ToContent(e.Location);

            if (_checkUpdateRect.Contains(pt))
            {
                _shell.CheckUpdates();
                return;
            }
            if (_autoUpdateRect.Contains(pt))
            {
                _shell.AutoCheckUpdates = !_shell.AutoCheckUpdates;
                Invalidate();
                return;
            }

            if (_switchDebounce.Contains(pt))
            {
                _shell.SetEnabled(!_shell.Cfg.FilterEnabled);
                Invalidate();
                return;
            }
            if (_switchInjected.Contains(pt))
            {
                _shell.SetSkipInjected(!_shell.Cfg.SkipInjected);
                Invalidate();
                return;
            }
            if (_switchStartup.Contains(pt))
            {
                _shell.AutoStart = !_shell.AutoStart;
                Invalidate();
                return;
            }
            int th = HitRects(pt, _thresholdRects);
            if (th >= 0 && th < Windows.Length)
            {
                _shell.SetThreshold(Windows[th]);
                Invalidate();
                return;
            }
            int tm = HitRects(pt, _themeRects);
            if (tm >= 0)
            {
                _shell.SetThemeMode((ThemeMode)tm);
                Invalidate();
                return;
            }
            int md = HitRects(pt, _modeRects);
            if (md >= 0)
            {
                _shell.SetPolicyMode(md);
                Invalidate();
                return;
            }
            if (_switchWhitelist.Contains(pt))
            {
                ToggleWhitelist(Stats.ForegroundTracker.CurrentApp);
                Invalidate();
                return;
            }
            if (_weeklyRect.Contains(pt))
            {
                string path = Stats.WeeklyReport.Generate(
                    System.IO.Path.Combine(_shell.Source.DataDirectory, "exports"),
                    _shell.Source.GetSnapshot(DateTime.Today.AddDays(-29), DateTime.Today));
                MessageBox.Show(this, path != null ? "周报已生成：\n" + path : "周报生成失败。",
                    "KeyInsight", MessageBoxButtons.OK,
                    path != null ? MessageBoxIcon.Information : MessageBoxIcon.Error);
                return;
            }
            if (_reinstallRect.Contains(pt))
            {
                _shell.ReinstallHook();
                Dialogs.Show(this, "输入钩子", "键盘钩子已重新安装。");
                return;
            }
            if (_openFolderRect.Contains(pt))
            {
                try { Process.Start("explorer.exe", "\"" + _shell.Source.DataDirectory + "\""); }
                catch { }
                return;
            }
            if (_manageRect.Contains(pt))
            {
                using (MonthManagerForm f = new MonthManagerForm(_shell.Source, _shell.RefreshSnapshot))
                {
                    f.ShowDialog(FindForm());
                }
                Invalidate();
                return;
            }
            if (_exportRect.Contains(pt))
            {
                ExportCsv();
                return;
            }
            if (_clearRect.Contains(pt))
            {
                if (Dialogs.Confirm(this, "清空全部数据", "确定清空全部历史数据？此操作不可撤销。"))
                {
                    _shell.Source.DeleteAllMonths();
                    _shell.RefreshSnapshot();
                    Invalidate();
                }
                return;
            }

            int idx = HitRects(pt, _monthDeleteRects);
            if (idx >= 0 && idx < _monthKeys.Length)
            {
                string key = _monthKeys[idx];
                if (Dialogs.Confirm(this, "删除月份数据",
                    "确定删除 " + key + " 的统计数据？此操作不可撤销。"))
                {
                    _shell.Source.DeleteMonth(key);
                    _shell.RefreshSnapshot();
                    Invalidate();
                }
            }
        }

        private void ExportCsv()
        {
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Title = "导出键盘统计数据";
                dlg.Filter = "CSV 文件|*.csv";
                dlg.FileName = "keyinsight";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    string dir = Path.GetDirectoryName(dlg.FileName);
                    string baseName = Path.Combine(dir, Path.GetFileNameWithoutExtension(dlg.FileName));
                    _shell.Source.ExportCsv(baseName, null);
                    Stats.WeeklyReport.WriteExportManifest(dir, DateTime.MinValue, DateTime.MaxValue,
                        new string[]
                        {
                            Path.GetFileName(baseName + "-days.csv"),
                            Path.GetFileName(baseName + "-apps.csv"),
                            Path.GetFileName(baseName + "-keys.csv"),
                            Path.GetFileName(baseName + "-mouse.csv")
                        });
                    Dialogs.Show(this, "导出完成",
                        "已导出：\n" + baseName + "-days.csv\n" + baseName + "-apps.csv\n" +
                        baseName + "-keys.csv\n" + baseName + "-mouse.csv\n（同目录 export-manifest.json 标注数据区间与精度）");
                }
                catch (Exception ex)
                {
                    Dialogs.Error(this, "导出失败", ex.Message);
                }
            }
        }
    }
}
