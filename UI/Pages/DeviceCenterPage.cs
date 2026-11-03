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
    /// 设备中心（方案 §3.1/§4/§11，PR-4）。缓存优先：列表来自 DeviceStore 内存快照，
    /// 枚举全部在服务后台线程完成，本页不做任何设备查询。
    /// 未检测 ≠ 健康：所有设备初始「尚未检测」。
    /// </summary>
    public sealed class DeviceCenterPage : PageBase
    {
        private readonly IShellActions _shell;
        private int _categoryFilter;                 // 0=全部 1=输入 2=显示 3=音频/视频 4=USB 5=其他
        private Rectangle[] _filterRects = new Rectangle[0];
        private readonly List<DeviceRow> _rows = new List<DeviceRow>();
        private Rectangle _refreshRect;

        private sealed class DeviceRow
        {
            public DeviceRecord Record;
            public Rectangle Rect;
        }

        public override string Title { get { return "设备中心"; } }
        public override string Subtitle { get { return "统一设备档案、连接状态与诊断入口；状态来自系统与本地记录"; } }

        // 四格 KPI + 列表/事件双卡
        protected override int MinContentHeight { get { return 760; } }

        public DeviceCenterPage(IShellActions shell)
        {
            _shell = shell;
            Range = RangeKind.Today;
            DeviceCenterService svc = shell.DeviceCenter;
            if (svc != null) svc.Changed += delegate { Invalidate(); };
        }

        protected override void Draw(Graphics g)
        {
            Palette p = Theme.P;
            DrawHeader(g);

            int[] counts = new int[] { 0, 0, 0, 0 };
            DeviceCenterService svc = _shell.DeviceCenter;
            if (svc != null) counts = svc.Counts();

            int y = 58;
            int gap = 14;
            int cardW = (ContentW - gap * 3) / 4;
            int cardH = 96;
            Charts.StatCard(g, new Rectangle(0, y, cardW, cardH), p, "已连接",
                Charts.Number(counts[0]), "系统当前可见", p.Good);
            Charts.StatCard(g, new Rectangle(cardW + gap, y, cardW, cardH), p, "已断开",
                Charts.Number(counts[1]), "历史档案保留", p.Muted);
            Charts.StatCard(g, new Rectangle((cardW + gap) * 2, y, cardW, cardH), p, "需要关注",
                Charts.Number(counts[2]), "有证据的 Finding", p.Warn);
            Charts.StatCard(g, new Rectangle((cardW + gap) * 3, y, cardW, cardH), p, "尚未检测",
                Charts.Number(counts[3]), "不代表设备故障", p.Info);

            y += cardH + gap;

            // 分类过滤 chips + 手动刷新
            DrawFilterChips(g, 0, y, p);
            _refreshRect = new Rectangle(ContentW - 92, y - 4, 92, 26);
            Charts.Button(g, _refreshRect, "重新扫描", false, p);
            y += 40;

            int midH = ContentH - y - 16;
            int leftW = (int)(ContentW * 0.56);
            int rightW = ContentW - leftW - gap;

            SectionCard(g, new Rectangle(0, y, leftW, midH), "设备列表",
                "点击行查看档案、能力与诊断入口");
            DrawDeviceList(g, new Rectangle(18, y + 54, leftW - 36, midH - 68), p);

            SectionCard(g, new Rectangle(leftW + gap, y, rightW, midH), "最近设备变化",
                "插入 / 拔出 / 模式变化 / 关注候选");
            DrawEvents(g, new Rectangle(leftW + gap + 18, y + 54, rightW - 36, midH - 68), p);
        }

        private void DrawFilterChips(Graphics g, int x, int y, Palette p)
        {
            string[] labels = new string[] { "全部", "输入", "显示", "音视频", "USB", "其他" };
            _filterRects = new Rectangle[labels.Length];
            int cx = x;
            for (int i = 0; i < labels.Length; i++)
            {
                int w = Ui.MeasureWidth(g, labels[i], Ui.BodySmall) + 26;
                Rectangle r = new Rectangle(cx, y, w, 26);
                Charts.Chip(g, r, labels[i], i == _categoryFilter, p, Ui.BodySmall);
                _filterRects[i] = r;
                cx += w + 8;
            }
        }

        private List<DeviceRecord> Filtered()
        {
            DeviceCenterService svc = _shell.DeviceCenter;
            List<DeviceRecord> all = svc != null ? svc.Records() : new List<DeviceRecord>();
            List<DeviceRecord> result = new List<DeviceRecord>();
            for (int i = 0; i < all.Count; i++)
            {
                DeviceRecord r = all[i];
                switch (_categoryFilter)
                {
                    case 0: result.Add(r); break;
                    case 1: if (r.Category == DeviceCategory.Input) result.Add(r); break;
                    case 2: if (r.Category == DeviceCategory.Display) result.Add(r); break;
                    case 3: if (r.Category == DeviceCategory.Audio || r.Category == DeviceCategory.Video) result.Add(r); break;
                    case 4: if (r.Category == DeviceCategory.UsbHub) result.Add(r); break;
                    default: if (r.Category == DeviceCategory.Other || r.Category == DeviceCategory.Office) result.Add(r); break;
                }
            }
            // 已连接优先，其余按最近出现排序
            result.Sort(delegate(DeviceRecord a, DeviceRecord b)
            {
                int ca = a.Connection == ConnectionState.Connected ? 0 : 1;
                int cb = b.Connection == ConnectionState.Connected ? 0 : 1;
                if (ca != cb) return ca.CompareTo(cb);
                return b.LastSeenUtc.CompareTo(a.LastSeenUtc);
            });
            return result;
        }

        private void DrawDeviceList(Graphics g, Rectangle r, Palette p)
        {
            _rows.Clear();
            List<DeviceRecord> records = Filtered();
            if (records.Count == 0)
            {
                Ui.TextCenter(g, "还没有设备档案。移动鼠标 / 连接设备后自动发现。", Ui.BodySmall, p.Dim,
                    new Rectangle(r.X, r.Y + 40, r.Width, 40));
                return;
            }

            int rowH = 44;
            Point cur = CursorToContent();
            int maxRows = Math.Max(1, r.Height / rowH);
            int count = Math.Min(records.Count, maxRows);
            for (int i = 0; i < count; i++)
            {
                DeviceRecord rec = records[i];
                Rectangle rect = new Rectangle(r.X, r.Y + i * rowH, r.Width, rowH - 4);
                DeviceRow row = new DeviceRow();
                row.Record = rec;
                row.Rect = rect;
                _rows.Add(row);

                bool hover = rect.Contains(cur);
                if (hover) Ui.FillRound(g, rect, 8, p.Hover);

                Color cc = rec.Connection == ConnectionState.Connected ? p.Good
                    : rec.Connection == ConnectionState.Disconnected ? p.Muted : p.Dim;
                Ui.DrawDot(g, rect.X + 8, rect.Y + rect.Height / 2, 4, cc);
                string name = string.IsNullOrEmpty(rec.UserAlias) ? rec.Name : rec.UserAlias;
                Ui.TextLeftEllipsis(g, name, Ui.Body, p.Text,
                    new Rectangle(rect.X + 22, rect.Y + 3, rect.Width - 190, 20));
                string sub = DeviceCategoryNames.Get(rec.Category) + " · " +
                    DeviceLifecycle.PhaseText(PhaseOf(rec)) + " · " +
                    HealthStateNames.Get(rec.Health);
                Ui.TextLeftEllipsis(g, sub, Ui.Label, p.Muted,
                    new Rectangle(rect.X + 22, rect.Y + 22, rect.Width - 190, 16));
                Color hc = rec.Health == HealthState.ConfirmedIssue ? p.Bad
                    : rec.Health == HealthState.Attention ? p.Warn
                    : rec.Health == HealthState.NoIssueObserved ? p.Good : p.Dim;
                Ui.TextRight(g, HealthStateNames.Get(rec.Health), Ui.Label, hc,
                    new Rectangle(rect.Right - 150, rect.Y + 12, 140, 20));
            }
            if (records.Count > count)
            {
                Ui.TextLeft(g, "+ 还有 " + (records.Count - count) + " 台设备未显示", Ui.Label, p.Dim,
                    new Rectangle(r.X, r.Y + count * rowH + 2, r.Width, 18));
            }
        }

        private static LifecyclePhase PhaseOf(DeviceRecord r)
        {
            switch (r.Connection)
            {
                case ConnectionState.Connected: return LifecyclePhase.Connected;
                case ConnectionState.Disconnected: return LifecyclePhase.Disconnected;
                default: return LifecyclePhase.Unknown;
            }
        }

        private void DrawEvents(Graphics g, Rectangle r, Palette p)
        {
            DeviceCenterService svc = _shell.DeviceCenter;
            List<DeviceEventItem> events = svc != null ? svc.RecentEvents(60) : new List<DeviceEventItem>();
            if (events.Count == 0)
            {
                Ui.TextCenter(g, "暂无设备变化记录", Ui.BodySmall, p.Dim,
                    new Rectangle(r.X, r.Y + 40, r.Width, 40));
                return;
            }
            int rowH = 34;
            int maxRows = Math.Max(1, r.Height / rowH);
            int start = Math.Max(0, events.Count - maxRows);
            for (int i = events.Count - 1, row = 0; i >= start; i--, row++)
            {
                DeviceEventItem e = events[i];
                int ry = r.Y + row * rowH;
                Color dot = e.EventType == DeviceEventType.Connected ? p.Good
                    : e.EventType == DeviceEventType.Disconnected ? p.Muted
                    : e.EventType == DeviceEventType.DisplayModeChanged ? p.Accent2
                    : e.EventType == DeviceEventType.AttentionCandidate ? p.Warn : p.Accent1;
                Ui.DrawDot(g, r.X + 5, ry + 12, 3, dot);
                Ui.TextLeft(g, EventText(e.EventType), Ui.Label, p.Text,
                    new Rectangle(r.X + 16, ry, 88, rowH));
                string who = DeviceName(e.DeviceId);
                Ui.TextLeftEllipsis(g, who, Ui.BodySmall, p.Muted,
                    new Rectangle(r.X + 104, ry, r.Width - 220, rowH));
                Ui.TextRight(g, e.TimeUtc.ToLocalTime().ToString("MM-dd HH:mm:ss"), Ui.Label, p.Dim,
                    new Rectangle(r.Right - 108, ry, 108, rowH));
            }
        }

        private string DeviceName(string deviceId)
        {
            DeviceCenterService svc = _shell.DeviceCenter;
            if (svc != null)
            {
                DeviceRecord r = svc.Store.Get(deviceId);
                if (r != null)
                {
                    return string.IsNullOrEmpty(r.UserAlias) ? r.Name : r.UserAlias;
                }
            }
            return "（已删除档案）";
        }

        private static string EventText(DeviceEventType t)
        {
            switch (t)
            {
                case DeviceEventType.Connected: return "已插入";
                case DeviceEventType.Disconnected: return "已拔出";
                case DeviceEventType.DisplayModeChanged: return "模式变化";
                case DeviceEventType.DefaultEndpointChanged: return "默认设备变化";
                case DeviceEventType.AttentionCandidate: return "关注候选";
                case DeviceEventType.HealthChanged: return "健康变化";
                case DeviceEventType.Archived: return "已归档";
                default: return "身份变化";
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
            Point pt = ToContent(e.Location);
            if (_refreshRect.Contains(pt))
            {
                DeviceCenterService svc = _shell.DeviceCenter;
                if (svc != null) svc.RefreshNow();
                return;
            }
            for (int i = 0; i < _filterRects.Length; i++)
            {
                if (_filterRects[i].Contains(pt))
                {
                    _categoryFilter = i;
                    Invalidate();
                    return;
                }
            }
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].Rect.Contains(pt))
                {
                    using (DeviceDetailForm f = new DeviceDetailForm(_shell, _rows[i].Record))
                    {
                        f.ShowDialog(FindForm());
                    }
                    Invalidate();
                    return;
                }
            }
        }
    }
}
