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
        private Rectangle _reinstallRect;
        private Rectangle _openFolderRect;
        private Rectangle _exportRect;
        private Rectangle _clearRect;
        private Rectangle _manageRect;
        private Rectangle[] _monthDeleteRects = new Rectangle[0];
        private string[] _monthKeys = new string[0];
        private static readonly int[] Windows = new int[] { 10, 15, 20, 25, 30 };

        public override string Title { get { return "设置与数据"; } }
        public override string Subtitle { get { return "防抖、启动、主题，以及本地数据的清理与导出"; } }

        public SettingsPage(IShellActions shell)
        {
            _shell = shell;
        }

        protected override void Draw(Graphics g)
        {
            Palette p = Theme.P;
            DrawHeader(g);

            int top = 64;
            int gap = 14;
            int leftW = (Width - gap) / 2;
            int cardH = 372;
            int rightW = Width - leftW - gap;

            Rectangle left = new Rectangle(0, top, leftW, cardH);
            Rectangle right = new Rectangle(leftW + gap, top, rightW, cardH);
            Rectangle bottom = new Rectangle(0, top + cardH + gap, Width, Height - (top + cardH + gap));

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

            DrawRow(g, p, x, y, w, "防抖开关", "吞掉同一按键在窗口内的重复按下", _shell.Cfg.Enabled);
            _switchDebounce = new Rectangle(x + w - 44, y + 6, 44, 24);
            Charts.Switch(g, _switchDebounce, _shell.Cfg.Enabled, p);

            y += 52;
            Ui.TextLeft(g, "防抖窗口", Ui.Body, p.Text, new Rectangle(x, y, w, 22));
            string[] thLabels = new string[Windows.Length];
            for (int i = 0; i < Windows.Length; i++) thLabels[i] = Windows[i] + " ms";
            int thSel = 1;
            for (int i = 0; i < Windows.Length; i++) if (Windows[i] == _shell.Cfg.ThresholdMs) thSel = i;
            DrawChips(g, thLabels, thSel, x, y + 26, 24, out _thresholdRects);

            y += 64;
            DrawRow(g, p, x, y, w, "跳过注入事件", "忽略 SendInput 等软件模拟的按键", _shell.Cfg.SkipInjected);
            _switchInjected = new Rectangle(x + w - 44, y + 6, 44, 24);
            Charts.Switch(g, _switchInjected, _shell.Cfg.SkipInjected, p);

            y += 46;
            DrawRow(g, p, x, y, w, "开机自启", "登录 Windows 后自动运行 KeyInsight", _shell.AutoStart);
            _switchStartup = new Rectangle(x + w - 44, y + 6, 44, 24);
            Charts.Switch(g, _switchStartup, _shell.AutoStart, p);

            y += 42;
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
                    bool hover = _monthDeleteRects[i].Contains(PointToClient(Cursor.Position));
                    Ui.TextCenter(g, "删除", Ui.Label, hover ? p.Bad : p.Dim, _monthDeleteRects[i]);
                }
                if (valid.Count > maxRows)
                {
                    Ui.TextLeft(g, "+ 还有 " + (valid.Count - maxRows) + " 个月未显示", Ui.Label, p.Dim,
                        new Rectangle(x, listY + count * rowH, w, 18));
                }
            }

            int by = card.Bottom - 52;
            _exportRect = new Rectangle(x, by, 118, 32);
            _clearRect = new Rectangle(card.Right - 18 - 132, by, 132, 32);
            Charts.Button(g, _exportRect, "导出 CSV", false, p);
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
                Ui.Label, p.Dim, new Rectangle(card.X + 18, card.Bottom - 30, card.Width - 36, 18));
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

            if (_switchDebounce.Contains(e.Location))
            {
                _shell.SetEnabled(!_shell.Cfg.Enabled);
                Invalidate();
                return;
            }
            if (_switchInjected.Contains(e.Location))
            {
                _shell.SetSkipInjected(!_shell.Cfg.SkipInjected);
                Invalidate();
                return;
            }
            if (_switchStartup.Contains(e.Location))
            {
                _shell.AutoStart = !_shell.AutoStart;
                Invalidate();
                return;
            }
            int th = HitRects(e.Location, _thresholdRects);
            if (th >= 0 && th < Windows.Length)
            {
                _shell.SetThreshold(Windows[th]);
                Invalidate();
                return;
            }
            int tm = HitRects(e.Location, _themeRects);
            if (tm >= 0)
            {
                _shell.SetThemeMode((ThemeMode)tm);
                Invalidate();
                return;
            }
            if (_reinstallRect.Contains(e.Location))
            {
                _shell.ReinstallHook();
                MessageBox.Show(this, "键盘钩子已重新安装。", "KeyInsight",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (_openFolderRect.Contains(e.Location))
            {
                try { Process.Start("explorer.exe", "\"" + _shell.Source.DataDirectory + "\""); }
                catch { }
                return;
            }
            if (_manageRect.Contains(e.Location))
            {
                using (MonthManagerForm f = new MonthManagerForm(_shell.Source, _shell.RefreshSnapshot))
                {
                    f.ShowDialog(FindForm());
                }
                Invalidate();
                return;
            }
            if (_exportRect.Contains(e.Location))
            {
                ExportCsv();
                return;
            }
            if (_clearRect.Contains(e.Location))
            {
                if (MessageBox.Show(this, "确定清空全部历史数据？此操作不可撤销。", "KeyInsight",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                {
                    _shell.Source.DeleteAllMonths();
                    _shell.RefreshSnapshot();
                    Invalidate();
                }
                return;
            }

            int idx = HitRects(e.Location, _monthDeleteRects);
            if (idx >= 0 && idx < _monthKeys.Length)
            {
                string key = _monthKeys[idx];
                if (MessageBox.Show(this, "确定删除 " + key + " 的统计数据？此操作不可撤销。", "KeyInsight",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
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
                    MessageBox.Show(this, "已导出：\n" + baseName + "-days.csv\n" + baseName + "-apps.csv\n" +
                        baseName + "-keys.csv\n" + baseName + "-mouse.csv", "KeyInsight",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "导出失败：" + ex.Message, "KeyInsight",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
    }
}
