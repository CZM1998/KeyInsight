using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using KeyInsight.Core.Devices;
using KeyInsight.Services.DeviceCenter;
using KeyInsight.Stats;

namespace KeyInsight.UI.Pages
{
    /// <summary>
    /// USB 与扩展（方案 §9.2，PR-9）：父子拓扑（主机控制器 → Hub/扩展坞 → 设备）、
    /// VID/PID 与连接状态、插拔历史与「共同上游变化候选」批分析。
    /// 拓扑来自本地档案（允许节点信息不完整，按根展示）；不带任何带宽/速率断言。
    /// </summary>
    public sealed class UsbPage : PageBase
    {
        private readonly IShellActions _shell;
        private Rectangle _refreshRect;

        public override string Title { get { return "USB 与扩展"; } }
        public override string Subtitle { get { return "扩展坞与集线器拓扑、插拔历史、异常断连分析（共同上游候选）"; } }

        protected override int MinContentHeight { get { return 780; } }

        public UsbPage(IShellActions shell)
        {
            _shell = shell;
            Range = RangeKind.Today;
            if (shell.DeviceCenter != null)
            {
                shell.DeviceCenter.Changed += delegate { Invalidate(); };
            }
        }

        protected override void Draw(Graphics g)
        {
            Palette p = Theme.P;
            DrawHeader(g);

            int y = 58;
            _refreshRect = new Rectangle(ContentW - 92, y - 4, 92, 26);
            Charts.Button(g, _refreshRect, "重新扫描", false, p);

            DeviceCenterService svc = _shell.DeviceCenter;
            List<DeviceRecord> records = svc != null ? svc.Records() : new List<DeviceRecord>();
            List<DeviceAnalytics.TopoNode> topo = DeviceAnalytics.BuildUsbTopology(
                FilterUsb(records));

            int topoH = Math.Max(260, (int)(ContentH * 0.55));
            SectionCard(g, new Rectangle(0, y, ContentW, topoH), "USB 拓扑",
                topo.Count + " 个 USB 节点 · 缩进层级 = 上游关系（信息不完整时按根展示）");
            DrawTopology(g, new Rectangle(20, y + 52, ContentW - 40, topoH - 66), topo, p);

            y += topoH + 14;
            int bottomH = ContentH - y - 16;
            SectionCard(g, new Rectangle(0, y, ContentW, bottomH), "断连与异常",
                "同一窗口内多设备断开 → 标记「共同上游变化候选」（不下结论）");
            DrawDisconnects(g, new Rectangle(20, y + 52, ContentW - 40, bottomH - 66), p);
        }

        private static List<DeviceRecord> FilterUsb(List<DeviceRecord> records)
        {
            List<DeviceRecord> result = new List<DeviceRecord>();
            for (int i = 0; i < records.Count; i++)
            {
                DeviceRecord r = records[i];
                if (r.Category == DeviceCategory.UsbHub || r.Category == DeviceCategory.Input
                    || r.Category == DeviceCategory.Office)
                {
                    result.Add(r);
                }
            }
            return result;
        }

        private void DrawTopology(Graphics g, Rectangle r,
            List<DeviceAnalytics.TopoNode> topo, Palette p)
        {
            if (topo.Count == 0)
            {
                Ui.TextCenter(g, "还没有 USB 设备档案。插入设备后自动发现。",
                    Ui.BodySmall, p.Dim, new Rectangle(r.X, r.Y + 60, r.Width, 40));
                return;
            }
            int rowH = 30;
            int maxRows = Math.Max(1, r.Height / rowH);
            int shown = Math.Min(topo.Count, maxRows);
            Point cur = CursorToContent();
            for (int i = 0; i < shown; i++)
            {
                DeviceAnalytics.TopoNode n = topo[i];
                DeviceRecord rec = n.Record;
                int indent = Math.Min(10, n.Depth) * 22;
                int x = r.X + indent;
                Rectangle rect = new Rectangle(x, r.Y + i * rowH, r.Width - indent, rowH - 4);

                bool connected = rec.Connection == ConnectionState.Connected;
                Color cc = connected ? p.Good : p.Muted;
                Ui.DrawDot(g, rect.X + 6, rect.Y + rect.Height / 2, 4, cc);

                string name = string.IsNullOrEmpty(rec.UserAlias) ? rec.Name : rec.UserAlias;
                string vp = DeviceIdentityResolver.JsonField(rec.AttributesJson, "vidPid");
                string label = name;
                if (!string.IsNullOrEmpty(vp)) label += "  ·  " + vp;

                Ui.TextLeftEllipsis(g, label, connected ? Ui.BodySmall : Ui.BodySmall,
                    connected ? p.Text : p.Muted,
                    new Rectangle(rect.X + 20, rect.Y + 2, rect.Width - 130, rowH - 4));

                string right = connected ? "在线" : "离线";
                if (n.Depth == 0 && rec.Category == DeviceCategory.UsbHub)
                {
                    right += " · 上游";
                }
                Ui.TextRight(g, right, Ui.Label, connected ? p.Good : p.Dim,
                    new Rectangle(rect.Right - 110, rect.Y + 2, 108, rowH - 4));
            }
            if (topo.Count > shown)
            {
                Ui.TextLeft(g, "+ 还有 " + (topo.Count - shown) + " 个节点未显示", Ui.Label, p.Dim,
                    new Rectangle(r.X, r.Y + shown * rowH + 2, r.Width, 18));
            }
        }

        private void DrawDisconnects(Graphics g, Rectangle r, Palette p)
        {
            DeviceCenterService svc = _shell.DeviceCenter;
            List<DeviceEventItem> events = svc != null ? svc.RecentEvents(400) : new List<DeviceEventItem>();
            List<DeviceAnalytics.DisconnectBatch> batches =
                DeviceAnalytics.GroupDisconnects(events, TimeSpan.FromSeconds(5), 3);
            if (batches.Count == 0)
            {
                Ui.TextCenter(g, "暂无断连记录", Ui.BodySmall, p.Dim,
                    new Rectangle(r.X, r.Y + 40, r.Width, 40));
                return;
            }
            int rowH = 34;
            int maxRows = Math.Max(1, r.Height / rowH);
            int shown = Math.Min(batches.Count, maxRows);
            for (int i = 0; i < shown; i++)
            {
                DeviceAnalytics.DisconnectBatch b = batches[i];
                int ry = r.Y + i * rowH;
                Color c = b.CommonUpstreamCandidate ? p.Warn : p.Muted;
                Ui.DrawDot(g, r.X + 5, ry + rowH / 2, 3, c);
                string text = b.TimeUtc.ToLocalTime().ToString("MM-dd HH:mm:ss") + "  " +
                    b.Items.Count + " 台设备断开";
                if (b.CommonUpstreamCandidate)
                {
                    text += "  → 共同上游变化候选";
                }
                Ui.TextLeftEllipsis(g, text, Ui.BodySmall,
                    b.CommonUpstreamCandidate ? p.Warn : p.Muted,
                    new Rectangle(r.X + 16, ry, r.Width - 16, rowH));
            }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible)
            {
                DeviceCenterService svc = _shell.DeviceCenter;
                if (svc != null) svc.RefreshNow();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (_refreshRect.Contains(ToContent(e.Location)))
            {
                DeviceCenterService svc = _shell.DeviceCenter;
                if (svc != null) svc.RefreshNow();
            }
        }
    }
}
