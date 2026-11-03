using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using KeyInsight.Adapters.Display;
using KeyInsight.Core.Devices;
using KeyInsight.Stats;

namespace KeyInsight.UI.Pages
{
    /// <summary>
    /// 显示设备（方案 §8，PR-6）：多屏拓扑（相对位置信息呈现，不拖拽修改系统布局）+
    /// 每屏档案卡。当前模式与支持能力分列；刷新率按 DisplayConfig 分数优先；
    /// HDR 未知显示「未知」，不把未读取当 0。枚举在后台线程，UI 只用缓存。
    /// </summary>
    public sealed class DisplayCenterPage : PageBase
    {
        private readonly IShellActions _shell;
        private List<DisplayAdapter.MonitorInfo> _monitors =
            new List<DisplayAdapter.MonitorInfo>();
        private volatile bool _loading;
        private readonly List<Rectangle> _hitRects = new List<Rectangle>();
        private Rectangle _refreshRect;

        public override string Title { get { return "显示设备"; } }
        public override string Subtitle { get { return "多屏拓扑、当前模式与支持能力、显示器档案与体检入口"; } }

        protected override int MinContentHeight { get { return 780; } }

        public DisplayCenterPage(IShellActions shell)
        {
            _shell = shell;
            Range = RangeKind.Today;
            if (shell.DeviceCenter != null)
            {
                shell.DeviceCenter.Changed += delegate { BeginBackgroundRefresh(); };
            }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) BeginBackgroundRefresh();
        }

        private void BeginBackgroundRefresh()
        {
            if (_loading) return;
            _loading = true;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                List<DisplayAdapter.MonitorInfo> monitors = null;
                try
                {
                    if (_shell.DeviceCenter != null)
                    {
                        monitors = _shell.DeviceCenter.Display.EnumerateMonitors();
                    }
                }
                catch { }
                if (monitors != null) _monitors = monitors;
                _loading = false;
                Invalidate();
            });
        }

        protected override void Draw(Graphics g)
        {
            Palette p = Theme.P;
            DrawHeader(g);

            int y = 58;
            _refreshRect = new Rectangle(ContentW - 92, y - 4, 92, 26);
            Charts.Button(g, _refreshRect, _loading ? "扫描中…" : "重新扫描", false, p);

            List<DisplayAdapter.MonitorInfo> monitors = _monitors;
            if (monitors.Count == 0)
            {
                Ui.TextCenter(g, _loading ? "正在读取显示器信息…" : "未读取到活动显示器",
                    Ui.BodySmall, p.Dim, new Rectangle(0, 120, ContentW, 40));
                return;
            }

            // 拓扑卡（信息呈现；第一版不提供拖拽改布局 —— 方案 §8.3）
            int topoH = 190;
            SectionCard(g, new Rectangle(0, y, ContentW, topoH), "多屏布局",
                "按系统相对位置呈现（仅信息，不在此修改系统布局）");
            DrawTopology(g, new Rectangle(24, y + 50, ContentW - 48, topoH - 64), monitors, p);

            y += topoH + 14;
            int restH = ContentH - y - 16;
            int cardW = (ContentW - 14) / 2;
            int cardH = Math.Max(220, restH);

            _hitRects.Clear();
            for (int i = 0; i < monitors.Count; i++)
            {
                int col = i % 2;
                int row = i / 2;
                Rectangle card = new Rectangle(col * (cardW + 14), y + row * (cardH + 14), cardW, cardH);
                if (card.Y + card.Height > ContentH + 60) cardH = Math.Max(200, card.Height);
                DrawMonitorCard(g, card, monitors[i], i, p);
                _hitRects.Add(card);
                if (card.Bottom > ContentH - 10 && i < monitors.Count - 1)
                {
                    // 后续卡片由页面滚动承载（MinContentHeight 覆盖）
                }
            }
        }

        private void DrawTopology(Graphics g, Rectangle r, List<DisplayAdapter.MonitorInfo> monitors, Palette p)
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            for (int i = 0; i < monitors.Count; i++)
            {
                minX = Math.Min(minX, monitors[i].PositionX);
                minY = Math.Min(minY, monitors[i].PositionY);
                maxX = Math.Max(maxX, monitors[i].PositionX + monitors[i].CurrentWidth);
                maxY = Math.Max(maxY, monitors[i].PositionY + monitors[i].CurrentHeight);
            }
            if (minX == int.MaxValue) return;
            int spanW = Math.Max(1, maxX - minX);
            int spanH = Math.Max(1, maxY - minY);
            double scale = Math.Min((r.Width - 24) / (double)spanW, (r.Height - 24) / (double)spanH);
            scale = Math.Min(scale, 0.16);
            int offsetX = r.X + (r.Width - (int)(spanW * scale)) / 2;
            int offsetY = r.Y + (r.Height - (int)(spanH * scale)) / 2;

            using (Font f = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold))
            for (int i = 0; i < monitors.Count; i++)
            {
                DisplayAdapter.MonitorInfo m = monitors[i];
                Rectangle rect = new Rectangle(
                    offsetX + (int)((m.PositionX - minX) * scale),
                    offsetY + (int)((m.PositionY - minY) * scale),
                    Math.Max(28, (int)(m.CurrentWidth * scale)),
                    Math.Max(20, (int)(m.CurrentHeight * scale)));
                bool primary = m.Primary;
                Ui.FillRound(g, rect, 6, primary ? p.Selected : p.CardAlt);
                using (Pen pen = new Pen(primary ? p.Accent1 : p.Border, primary ? 2f : 1.2f))
                {
                    g.DrawRectangle(pen, rect);
                }
                string label = (i + 1) + (primary ? " · 主" : "");
                Ui.TextCenter(g, label, f, p.Text, rect);
                // 分辨率标注在框下
                Ui.TextCenter(g, m.CurrentWidth + "x" + m.CurrentHeight, Ui.Label, p.Dim,
                    new Rectangle(rect.X - 20, rect.Bottom + 2, rect.Width + 40, 16));
            }
        }

        private void DrawMonitorCard(Graphics g, Rectangle card, DisplayAdapter.MonitorInfo m,
            int index, Palette p)
        {
            bool hover = card.Contains(CursorToContent());
            SectionCard(g, card, "显示器 " + (index + 1) + (m.Primary ? " · 主显示器" : ""),
                hover ? "点击卡片打开档案 / 体检" : "EDID 档案 · 当前模式 ≠ 支持能力");
            if (hover)
            {
                using (Pen pen = new Pen(p.Accent1, 1.4f)) g.DrawRectangle(pen, card);
            }

            int x = card.X + 22;
            int w = card.Width - 44;
            int y = card.Y + 58;

            // 身份
            Ui.TextLeft(g, string.IsNullOrEmpty(m.Model) ? "未确认设备" : m.Model, Ui.Section, p.Text,
                new Rectangle(x, y, w, 26));
            y += 30;
            Ui.TextLeftEllipsis(g,
                "身份：" + (m.EdidValid
                    ? (m.EdidVendor ?? "?") + " · 产品 " + m.EdidProduct +
                      " · 序列 " + (m.EdidSerial != 0 ? m.EdidSerial.ToString() : "未读取") +
                      (m.ManufactureYear > 1990 ? " · " + m.ManufactureYear + " 年产" : "")
                    : "EDID 未读取"),
                Ui.Label, p.Muted, new Rectangle(x, y, w, 18));
            y += 24;

            // 当前模式（GDI/DisplayConfig 分数）vs 原生（EDID 首选时序）
            Ui.TextLeft(g, "当前模式", Ui.Label, p.Muted, new Rectangle(x, y, 90, 20));
            Ui.TextLeft(g, m.CurrentWidth + " × " + m.CurrentHeight + "  @ " +
                m.CurrentRefreshHz + " Hz  ·  " + m.BitsPerPixel + " bit",
                Ui.Body, p.Text, new Rectangle(x + 94, y, w - 94, 20));
            y += 24;
            Ui.TextLeft(g, "连接", Ui.Label, p.Muted, new Rectangle(x, y, 90, 20));
            Ui.TextLeftEllipsis(g,
                (string.IsNullOrEmpty(m.Connector) ? "未知" : m.Connector) +
                (m.OrientationDeg > 0 ? "  ·  旋转 " + m.OrientationDeg + "°" : ""),
                Ui.Body, p.Text, new Rectangle(x + 94, y, w - 94, 20));
            y += 24;
            Ui.TextLeft(g, "原生面板", Ui.Label, p.Muted, new Rectangle(x, y, 90, 20));
            Ui.TextLeft(g, m.NativeWidth > 0
                    ? m.NativeWidth + " × " + m.NativeHeight + "（EDID 首选时序）"
                    : "未知",
                Ui.Body, p.Text, new Rectangle(x + 94, y, w - 94, 20));
            y += 24;
            Ui.TextLeft(g, "HDR", Ui.Label, p.Muted, new Rectangle(x, y, 90, 20));
            string hdr = "未知";
            Color hc = p.Dim;
            if (m.HdrKnown)
            {
                hdr = m.HdrActive ? "已开启" : (m.HdrSupported ? "支持 · 未开启" : "不支持");
                hc = m.HdrActive ? p.Good : (m.HdrSupported ? p.Info : p.Muted);
            }
            Ui.TextLeft(g, hdr, Ui.Body, hc, new Rectangle(x + 94, y, w - 94, 20));
            y += 24;
            Ui.TextLeft(g, "缩放", Ui.Label, p.Muted, new Rectangle(x, y, 90, 20));
            Ui.TextLeft(g, "未读取（第一版未接入 per-DPI 查询）", Ui.Body, p.Dim,
                new Rectangle(x + 94, y, w - 94, 20));
            y += 30;

            // 支持模式摘要（前 6 个，区分当前 —— 方案 §8.2）
            Ui.TextLeft(g, "支持模式（注册表枚举）", Ui.Label, p.Muted, new Rectangle(x, y, w, 18));
            y += 22;
            List<DisplayAdapter.SupportedMode> modes = m.SupportedModes;
            modes.Sort(delegate(DisplayAdapter.SupportedMode a, DisplayAdapter.SupportedMode b)
            {
                if (a.Width != b.Width) return b.Width.CompareTo(a.Width);
                if (a.Height != b.Height) return b.Height.CompareTo(a.Height);
                return b.RefreshHz.CompareTo(a.RefreshHz);
            });
            int shown = Math.Min(6, modes.Count);
            for (int i = 0; i < shown; i++)
            {
                DisplayAdapter.SupportedMode mode = modes[i];
                bool current = mode.Width == m.CurrentWidth && mode.Height == m.CurrentHeight &&
                    mode.RefreshHz == m.CurrentRefreshHz;
                Ui.TextLeft(g,
                    mode.Width + " × " + mode.Height + "  @ " + mode.RefreshHz + " Hz" +
                    (current ? "  ← 当前" : ""),
                    Ui.Label, current ? p.Accent1 : p.Muted,
                    new Rectangle(x + (i % 2) * (w / 2), y + (i / 2) * 18, w / 2 - 8, 16));
            }
            if (modes.Count == 0)
            {
                Ui.TextLeft(g, "未读取", Ui.Label, p.Dim, new Rectangle(x, y, w, 16));
            }
            y += 3 * 18 + 8;

            // 体检历史摘要
            if (_shell.DeviceCenter != null)
            {
                List<DiagnosticFinding> findings =
                    _shell.DeviceCenter.Diagnostics.FindingsForDevice(FindDeviceId(m, index));
                if (findings.Count > 0)
                {
                    DiagnosticFinding latest = findings[findings.Count - 1];
                    Ui.TextLeftEllipsis(g,
                        "最近体检：" + latest.CreatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm") +
                        " · " + latest.DiagnosticCode + " · " +
                        (latest.Status == FindingStatus.UserConfirmed ? "用户确认异常" : "正常"),
                        Ui.Label, latest.Status == FindingStatus.UserConfirmed ? p.Warn : p.Good,
                        new Rectangle(x, y, w, 18));
                }
                else
                {
                    Ui.TextLeft(g, "尚未体检（不代表设备健康）", Ui.Label, p.Dim,
                        new Rectangle(x, y, w, 18));
                }
            }
        }

        private string FindDeviceId(DisplayAdapter.MonitorInfo m, int index)
        {
            if (_shell.DeviceCenter == null) return "";
            string identityKey = DisplayAdapter.MonitorInstanceKey(m, index);
            DeviceRecord r = _shell.DeviceCenter.Store.FindByIdentityKey(identityKey);
            return r != null ? r.DeviceId : "";
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Point pt = ToContent(e.Location);
            if (_refreshRect.Contains(pt))
            {
                BeginBackgroundRefresh();
                return;
            }
            for (int i = 0; i < _hitRects.Count && i < _monitors.Count; i++)
            {
                if (_hitRects[i].Contains(pt))
                {
                    OpenDetail(_monitors[i], i);
                    return;
                }
            }
        }

        private void OpenDetail(DisplayAdapter.MonitorInfo m, int index)
        {
            if (_shell.DeviceCenter == null) return;
            string identityKey = DisplayAdapter.MonitorInstanceKey(m, index);
            DeviceRecord record = _shell.DeviceCenter.Store.FindByIdentityKey(identityKey);
            if (record == null)
            {
                // 档案尚未生成（服务首扫未完成）：提示而不是造空档案
                Dialogs.Info(null, "档案未就绪",
                    "该显示器的统一档案还在生成中，稍后再试；或先在「设备中心」页触发一次重新扫描。");
                return;
            }
            using (DeviceDetailForm f = new DeviceDetailForm(_shell, record))
            {
                f.ShowDialog(FindForm());
            }
            Invalidate();
        }
    }
}
