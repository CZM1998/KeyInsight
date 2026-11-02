using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using KeyInsight.Core.Input;
using KeyInsight.Core.Policy;
using KeyInsight.Stats;

namespace KeyInsight.UI.Pages
{
    public sealed class HealthPage : PageBase
    {
        private readonly IShellActions _shell;
        private int _selectedVk;
        private Rectangle[] _rowRects = new Rectangle[0];
        private int[] _rowVks = new int[0];
        private Rectangle _switchPolicy;
        private Rectangle[] _policyThRects = new Rectangle[0];
        private Rectangle _wizardRect;
        private Rectangle _simRect;
        private Rectangle _maintainRect;
        private Rectangle _revertRect;
        private static readonly int[] PolicyThresholds = new int[] { 10, 15, 20, 25, 30 };
        private int _overall;
        private long _weekSwallowed;
        private int _needCount;

        public override string Title { get { return "按键健康"; } }
        public override string Subtitle { get { return "按抖动率、趋势与拦截次数评估键况；可为单键启用保守过滤"; } }

        public HealthPage(IShellActions shell)
        {
            _shell = shell;
            Range = RangeKind.Month;
        }

        public override void SetSnapshot(StatsSnapshot s)
        {
            base.SetSnapshot(s);
            // PR-2：综合健康分/周拦截/需关注计数在快照阶段计算
            double weighted = 0;
            long weight = 0;
            long week = 0;
            int need = 0;
            int worst = 100;
            foreach (KeyHealthRow row in s.KeyHealth)
            {
                weighted += row.Score * row.Total;
                weight += row.Total;
                week += row.Swallowed7;
                if (row.Status >= 2) need++;
                if (row.Score < worst) worst = row.Score;
            }
            _overall = weight > 0
                ? (int)Math.Round((weighted / weight) * 0.6 + worst * 0.4)
                : 100;
            _weekSwallowed = week;
            _needCount = need;

            bool found = false;
            for (int i = 0; i < s.KeyHealth.Count; i++)
            {
                if (s.KeyHealth[i].Vk == _selectedVk) { found = true; break; }
            }
            if (!found)
            {
                _selectedVk = s.KeyHealth.Count > 0 ? s.KeyHealth[0].Vk : 0;
            }
        }

        protected override void Draw(Graphics g)
        {
            Palette p = Theme.P;
            DrawHeader(g);
            DrawRangeChips(g, 0, 58);

            int overall = _overall;
            long week = _weekSwallowed;
            int need = _needCount;

            int y = 100;
            int gap = 14;
            int cardW = (ContentW - gap * 3) / 4;
            int cardH = 100;
            Color overallColor = StatusColor(overall >= 90 ? 0 : overall >= 70 ? 1 : overall >= 50 ? 2 : 3, p);

            Charts.StatCard(g, new Rectangle(0, y, cardW, cardH), p, "综合健康",
                overall.ToString(), StatusText(overall >= 90 ? 0 : overall >= 70 ? 1 : overall >= 50 ? 2 : 3),
                overallColor);
            Charts.StatCard(g, new Rectangle(cardW + gap, y, cardW, cardH), p, "抖动按键",
                Charts.Number(Snap.KeyHealth.Count), "范围内出现过拦截", p.Warn);
            Charts.StatCard(g, new Rectangle((cardW + gap) * 2, y, cardW, cardH), p, "本周拦截",
                Charts.Number(week), "最近 7 天", p.Bad);
            Charts.StatCard(g, new Rectangle((cardW + gap) * 3, y, cardW, cardH), p, "需关注",
                Charts.Number(need), "警告或严重", need > 0 ? p.Bad : p.Good);

            int contentY = y + cardH + gap;
            int leftW = (int)(ContentW * 0.44);
            int rightW = ContentW - leftW - gap;
            int midH = ContentH - contentY;

            SectionCard(g, new Rectangle(0, contentY, leftW, midH), "按键列表", "按健康分从低到高");
            DrawList(g, new Rectangle(18, contentY + 56, leftW - 36, midH - 70), p);

            SectionCard(g, new Rectangle(leftW + gap, contentY, rightW, midH), "按键详情", "抖动趋势与建议");
            DrawDetail(g, new Rectangle(leftW + gap, contentY, rightW, midH), p);
        }

        private void DrawList(Graphics g, Rectangle r, Palette p)
        {
            List<KeyHealthRow> rows = Snap.KeyHealth;
            if (rows.Count == 0)
            {
                Ui.TextCenter(g, "这个范围内没有抖动记录", Ui.BodySmall, p.Dim,
                    new Rectangle(r.X, r.Y + 40, r.Width, 40));
                _rowRects = new Rectangle[0];
                _rowVks = new int[0];
                return;
            }

            int rowH = 38;
            int maxRows = Math.Max(1, r.Height / rowH);
            int count = Math.Min(rows.Count, maxRows);
            _rowRects = new Rectangle[count];
            _rowVks = new int[count];

            for (int i = 0; i < count; i++)
            {
                KeyHealthRow row = rows[i];
                int ry = r.Y + i * rowH;
                Rectangle rect = new Rectangle(r.X, ry, r.Width, rowH - 4);
                _rowRects[i] = rect;
                _rowVks[i] = row.Vk;

                bool selected = row.Vk == _selectedVk;
                if (selected) Ui.FillRound(g, rect, 8, p.Selected);
                else if (rect.Contains(CursorToContent())) Ui.FillRound(g, rect, 8, p.Hover);

                Color sc = StatusColor(row.Status, p);
                Ui.DrawDot(g, rect.X + 10, rect.Y + rect.Height / 2, 4, sc);
                Ui.TextLeft(g, KeyLayout.LabelFor(row.Vk), Ui.Body, p.Text,
                    new Rectangle(rect.X + 24, rect.Y + 3, 96, 20));
                Ui.TextLeftEllipsis(g,
                    "按键 " + Charts.Number(row.Total) + " · 拦截 " + Charts.Number(row.Swallowed) +
                    " · 抖动率 " + (row.Rate * 100).ToString("F1") + "%",
                    Ui.Label, p.Muted, new Rectangle(rect.X + 24, rect.Y + 20, rect.Width - 120, 16));

                Rectangle pill = new Rectangle(rect.Right - 58, rect.Y + 7, 48, 20);
                Ui.FillRound(g, pill, 10, Ui.WithAlpha(sc, 32));
                Ui.TextCenter(g, row.Score.ToString(), Ui.Label, sc, pill);
            }

            if (rows.Count > count)
            {
                Ui.TextLeft(g, "+ 还有 " + (rows.Count - count) + " 个按键未显示", Ui.Label, p.Dim,
                    new Rectangle(r.X, r.Y + count * rowH + 2, r.Width, 18));
            }
        }

        private void DrawDetail(Graphics g, Rectangle card, Palette p)
        {
            KeyHealthRow row = Find(_selectedVk);
            if (row == null)
            {
                Ui.TextCenter(g, "选择左侧按键查看详情", Ui.BodySmall, p.Dim,
                    new Rectangle(card.X, card.Y + 120, card.Width, 50));
                return;
            }

            Color sc = StatusColor(row.Status, p);
            int x = card.X + 22;
            int y = card.Y + 56;

            Ui.TextLeft(g, KeyLayout.LabelFor(row.Vk), Ui.PageTitle, p.Text,
                new Rectangle(x, y, card.Width - 150, 34));
            Ui.TextLeft(g, StatusText(row.Status) + " · 健康分 " + row.Score, Ui.BodySmall, sc,
                new Rectangle(x, y + 32, card.Width - 150, 20));

            DrawScoreRing(g, new Rectangle(card.Right - 96, card.Y + 54, 64, 64), row.Score, sc, p);

            int sy = y + 62;
            int gap = 6;
            int bw = (card.Width - 44 - gap) / 2;
            int bh = 48;
            Mini(g, new Rectangle(x, sy, bw, bh), p, "按键次数", Charts.Number(row.Total), p.Accent1);
            Mini(g, new Rectangle(x + bw + gap, sy, bw, bh), p, "拦截次数", Charts.Number(row.Swallowed), p.Bad);
            Mini(g, new Rectangle(x, sy + bh + gap, bw, bh), p, "抖动率",
                (row.Rate * 100).ToString("F2") + "%", p.Warn);
            Mini(g, new Rectangle(x + bw + gap, sy + bh + gap, bw, bh), p, "最近 7 天",
                Charts.Number(row.Swallowed7) + " 次", p.Accent2);

            int labelY = sy + (bh + gap) * 2 + 4;
            int policyTop = card.Bottom - 170;
            int chartTop = labelY + 20;
            int chartBottomLimit = policyTop - 28;
            int chartH = chartBottomLimit - chartTop;
            if (chartH < 36) chartH = 36;

            Ui.TextLeft(g, "最近 30 天抖动次数", Ui.BodySmall, p.Muted,
                new Rectangle(x, labelY, 200, 20));
            Color tc = p.Muted;
            string trendText;
            if (row.Swallowed7 > row.SwallowedPrev7 + 1)
            {
                trendText = "↑ 本周 " + row.Swallowed7 + " / 上周 " + row.SwallowedPrev7 + "，变多";
                tc = p.Bad;
            }
            else if (row.Swallowed7 < row.SwallowedPrev7 - 1)
            {
                trendText = "↓ 本周 " + row.Swallowed7 + " / 上周 " + row.SwallowedPrev7 + "，变少";
                tc = p.Good;
            }
            else
            {
                trendText = "→ 本周 " + row.Swallowed7 + " / 上周 " + row.SwallowedPrev7 + "，持平";
            }
            Ui.TextRight(g, trendText, Ui.Label, tc, new Rectangle(x + 200, labelY, card.Width - 44 - 200, 20));

            int[] trend;
            if (!Snap.KeySwallowTrend.TryGetValue(row.Vk, out trend)) trend = new int[30];
            DrawTrend(g, new Rectangle(x, chartTop, card.Width - 44, chartH), trend, Snap.KeyTrendStart, p);

            Ui.TextLeftEllipsis(g, Advice(row), Ui.Label, p.Muted,
                new Rectangle(x, chartTop + chartH + 4, card.Width - 44, 18));

            DrawPolicySection(g, card, row, p);
        }

        private void DrawPolicySection(Graphics g, Rectangle card, KeyHealthRow row, Palette p)
        {
            int x = card.X + 22;
            int w = card.Width - 44;
            int policyY = card.Bottom - 170;

            Ui.Divider(g, x, policyY, w, p);
            policyY += 4;

            KeyPolicyEntry entry = null;
            if (_shell.KeyPolicies != null)
            {
                int scan = InputIdentityResolver.CanonicalScanFor(row.Vk);
                entry = _shell.KeyPolicies.Find(row.Vk, scan, false);
                if (entry == null) entry = _shell.KeyPolicies.FindByVirtualKey(row.Vk);
            }
            bool enabled = entry != null && entry.Enabled;
            int threshold = entry != null ? entry.ThresholdMs : 20;

            Ui.TextLeft(g, "单键保守过滤（仅此键）", Ui.Body, p.Text, new Rectangle(x, policyY, 220, 22));
            _switchPolicy = new Rectangle(x + w - 44, policyY + 1, 44, 22);
            Charts.Switch(g, _switchPolicy, enabled, p);

            string modeHint = "策略模式：" + ModeText(_shell.PolicyStatus.Mode) +
                (enabled ? " · 阈值 " + threshold + " ms" : "");
            Ui.TextLeftEllipsis(g, modeHint, Ui.Label, p.Dim, new Rectangle(x, policyY + 24, w, 16));

            Ui.TextLeft(g, "阈值", Ui.Label, p.Muted, new Rectangle(x, policyY + 44, 40, 18));
            string[] labels = new string[PolicyThresholds.Length];
            for (int i = 0; i < PolicyThresholds.Length; i++) labels[i] = PolicyThresholds[i] + "";
            int sel = -1;
            for (int i = 0; i < PolicyThresholds.Length; i++) if (PolicyThresholds[i] == threshold) sel = i;
            int cx = x + 44;
            _policyThRects = new Rectangle[PolicyThresholds.Length];
            for (int i = 0; i < labels.Length; i++)
            {
                int bw = Ui.MeasureWidth(g, labels[i] + " ms", Ui.BodySmall) + 18;
                Rectangle r = new Rectangle(cx, policyY + 40, bw, 24);
                Charts.Chip(g, r, labels[i] + " ms", i == sel && enabled, p, Ui.BodySmall);
                _policyThRects[i] = r;
                cx += bw + 6;
            }

            int btnY = policyY + 72;
            int bw2 = (w - 24) / 4;
            _wizardRect = new Rectangle(x, btnY, bw2, 30);
            _simRect = new Rectangle(x + bw2 + 8, btnY, bw2, 30);
            _maintainRect = new Rectangle(x + (bw2 + 8) * 2, btnY, bw2, 30);
            _revertRect = new Rectangle(x + (bw2 + 8) * 3, btnY, bw2, 30);
            Charts.Button(g, _wizardRect, "诊断测试", true, p);
            Charts.Button(g, _simRect, "策略模拟", false, p);
            Charts.Button(g, _maintainRect, "记录维护", false, p);
            bool canRevert = _shell.KeyPolicies != null && _shell.KeyPolicies.HasUndo;
            Charts.Button(g, _revertRect, canRevert ? "回退上次" : "无可回退", false, p);

            // 维护前后对比（P2-02）
            List<MaintenanceEvent> events = MaintenanceStore.Load(_shell.MaintenanceDir);
            MaintenanceEvent latest = null;
            for (int i = events.Count - 1; i >= 0; i--)
            {
                if (events[i].Vk == row.Vk) { latest = events[i]; break; }
            }
            if (latest != null)
            {
                MaintenanceStore.BeforeAfter cmp = MaintenanceStore.Compare(row.Vk, latest, Snap);
                string line = latest.LocalTime.ToString("MM-dd HH:mm") + " " + latest.ActionText + " · " + cmp.Verdict;
                Ui.TextLeftEllipsis(g, line, Ui.Label, p.Dim,
                    new Rectangle(x, card.Bottom - 24, w, 18));
            }
        }

        private static string ModeText(PolicyMode mode)
        {
            switch (mode)
            {
                case PolicyMode.ObserveOnly: return "观察";
                case PolicyMode.PerKeyConservative: return "单键保守";
                default: return "兼容 v2.1";
            }
        }

        private static void Mini(Graphics g, Rectangle r, Palette p, string label, string value, Color accent)
        {
            Ui.FillRound(g, r, 10, p.CardAlt);
            Ui.StrokeRound(g, r, 10, p.Border);
            Ui.FillRound(g, new RectangleF(r.X + 14, r.Y + 14, 3, 16), 1.5f, accent);
            Ui.TextLeft(g, label, Ui.Label, p.Muted, new Rectangle(r.X + 24, r.Y + 8, r.Width - 34, 18));
            Ui.TextLeft(g, value, Ui.CardValueSmall, p.Text, new Rectangle(r.X + 24, r.Y + 21, r.Width - 34, 28));
        }

        private static void DrawScoreRing(Graphics g, Rectangle r, int score, Color color, Palette p)
        {
            using (Pen bg = new Pen(p.Border, 7f))
            {
                g.DrawArc(bg, r, -90f, 360f);
            }
            using (Pen fg = new Pen(color, 7f))
            {
                fg.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                fg.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                g.DrawArc(fg, r, -90f, (float)(360.0 * Math.Max(0, Math.Min(100, score)) / 100.0));
            }
            Ui.TextCenter(g, score.ToString(), Ui.CardValueSmall, p.Text,
                new Rectangle(r.X, r.Y + 14, r.Width, 28));
            Ui.TextCenter(g, "健康分", Ui.Label, p.Dim,
                new Rectangle(r.X, r.Y + 40, r.Width, 16));
        }

        private static void DrawTrend(Graphics g, Rectangle r, int[] values, DateTime start, Palette p)
        {
            int left = 30;
            int bottom = 18;
            int top = 6;
            int right = 4;
            Rectangle plot = new Rectangle(r.X + left, r.Y + top, Math.Max(10, r.Width - left - right), Math.Max(10, r.Height - top - bottom));

            int max = 1;
            for (int i = 0; i < values.Length; i++) if (values[i] > max) max = values[i];

            using (Pen grid = new Pen(p.Grid, 1f))
            {
                g.DrawLine(grid, plot.X, plot.Bottom, plot.Right, plot.Bottom);
            }

            int slot = Math.Max(1, plot.Width / Math.Max(1, values.Length));
            int barW = Math.Max(2, slot - 2);
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] <= 0) continue;
                double t = Math.Sqrt(values[i] / (double)max);
                int bh = (int)Math.Round(plot.Height * t);
                if (bh < 2) bh = 2;
                RectangleF br = new RectangleF(plot.X + i * slot + (slot - barW) / 2f, plot.Bottom - bh, barW, bh);
                using (System.Drawing.Drawing2D.LinearGradientBrush b =
                    new System.Drawing.Drawing2D.LinearGradientBrush(
                        new Rectangle((int)br.X, plot.Bottom - Math.Max(bh, 8), (int)br.Width, Math.Max(bh, 8)),
                        Color.FromArgb(248, 113, 113), Color.FromArgb(153, 27, 27), 90f))
                {
                    g.FillRectangle(b, br);
                }
            }

            Ui.TextRight(g, max.ToString(), Ui.Label, p.Dim, new Rectangle(r.X, plot.Y - 4, left - 4, 14));
            Ui.TextLeft(g, start.ToString("MM-dd"), Ui.Label, p.Dim,
                new Rectangle(plot.X, plot.Bottom + 2, 50, 14));
            Ui.TextCenter(g, start.AddDays(14).ToString("MM-dd"), Ui.Label, p.Dim,
                new Rectangle(plot.X + plot.Width / 2 - 25, plot.Bottom + 2, 50, 14));
            Ui.TextRight(g, start.AddDays(29).ToString("MM-dd"), Ui.Label, p.Dim,
                new Rectangle(plot.Right - 50, plot.Bottom + 2, 50, 14));
        }

        private KeyHealthRow Find(int vk)
        {
            foreach (KeyHealthRow row in Snap.KeyHealth)
            {
                if (row.Vk == vk) return row;
            }
            return null;
        }

        private static Color StatusColor(int status, Palette p)
        {
            switch (status)
            {
                case 0: return p.Good;
                case 1: return p.Warn;
                case 2: return Ui.Blend(p.Warn, p.Bad, 0.5f);
                default: return p.Bad;
            }
        }

        private static string StatusText(int status)
        {
            switch (status)
            {
                case 0: return "健康";
                case 1: return "观察";
                case 2: return "警告";
                default: return "严重";
            }
        }

        private static string Advice(KeyHealthRow row)
        {
            if (row.Status >= 3)
            {
                return "抖动率 " + (row.Rate * 100).ToString("F1") +
                    "%，建议尽快清洁触点或更换轴体；可临时把防抖窗口调到 30 ms。";
            }
            if (row.Status == 2)
            {
                return "抖动较频繁，建议清洁触点/换轴，并观察本周趋势是否继续上升。";
            }
            if (row.Status == 1)
            {
                return "偶发抖动，暂时继续观察；如果变频繁，再清洁或换轴。";
            }
            return "状态良好，保持观察即可。";
        }

        private string RangeText()
        {
            switch (Range)
            {
                case RangeKind.Today: return "今日";
                case RangeKind.Week: return "最近 7 天";
                case RangeKind.Month: return "最近 30 天";
                default: return "全部";
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Point pt = ToContent(e.Location);
            if (HandleRangeClick(pt)) return;
            int idx = HitRects(pt, _rowRects);
            if (idx >= 0 && idx < _rowVks.Length)
            {
                _selectedVk = _rowVks[idx];
                Invalidate();
                return;
            }

            if (_shell.KeyPolicies == null) return;
            KeyHealthRow row = Find(_selectedVk);
            if (row == null) return;
            int scan = InputIdentityResolver.CanonicalScanFor(row.Vk);

            if (_switchPolicy.Contains(pt))
            {
                _shell.KeyPolicies.BeginEdit();
                KeyPolicyEntry entry = _shell.KeyPolicies.GetOrCreate(row.Vk, scan, false);
                entry.Enabled = !entry.Enabled;
                entry.EnabledAtUtcTicks = entry.Enabled ? DateTime.UtcNow.Ticks : 0;
                if (entry.ThresholdMs < 10 || entry.ThresholdMs > 30) entry.ThresholdMs = 20;
                CommitPolicy();
                return;
            }
            int th = HitRects(pt, _policyThRects);
            if (th >= 0 && th < PolicyThresholds.Length)
            {
                _shell.KeyPolicies.BeginEdit();
                KeyPolicyEntry entry = _shell.KeyPolicies.GetOrCreate(row.Vk, scan, false);
                entry.ThresholdMs = PolicyThresholds[th];
                entry.Enabled = true;
                entry.EnabledAtUtcTicks = DateTime.UtcNow.Ticks;
                CommitPolicy();
                return;
            }
            if (_wizardRect.Contains(pt))
            {
                using (DiagnosisWizardForm f = new DiagnosisWizardForm(_shell, row.Vk))
                {
                    f.ShowDialog(FindForm());
                }
                Invalidate();
                return;
            }
            if (_simRect.Contains(pt))
            {
                using (SimulationForm f = new SimulationForm(Snap, row.Vk))
                {
                    if (f.ShowDialog(FindForm()) == DialogResult.OK && f.AppliedThreshold > 0)
                    {
                        _shell.KeyPolicies.BeginEdit();
                        KeyPolicyEntry entry = _shell.KeyPolicies.GetOrCreate(row.Vk, scan, false);
                        entry.ThresholdMs = f.AppliedThreshold;
                        entry.Enabled = true;
                        entry.EnabledAtUtcTicks = DateTime.UtcNow.Ticks;
                        CommitPolicy();
                    }
                }
                Invalidate();
                return;
            }
            if (_revertRect.Contains(pt))
            {
                if (_shell.KeyPolicies.Revert())
                {
                    _shell.RefreshPolicy();
                    Invalidate();
                }
                return;
            }
            if (_maintainRect.Contains(pt))
            {
                using (MaintenanceForm f = new MaintenanceForm(row.Vk))
                {
                    if (f.ShowDialog(FindForm()) == DialogResult.OK)
                    {
                        MaintenanceEvent ev = new MaintenanceEvent();
                        ev.TimeUtcTicks = DateTime.UtcNow.Ticks;
                        ev.Vk = row.Vk;
                        ev.Action = f.ActionKey;
                        ev.Note = f.NoteText;
                        MaintenanceStore.Append(_shell.MaintenanceDir, ev);
                    }
                }
                Invalidate();
            }
        }

        private void CommitPolicy()
        {
            _shell.RefreshPolicy();
            Invalidate();
        }
    }
}
