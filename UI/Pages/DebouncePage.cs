using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using KeyInsight.Stats;
using KeyInsight.Update.Routing;

namespace KeyInsight.UI.Pages
{
    public sealed class DebouncePage : PageBase
    {
        private readonly IShellActions _shell;
        private Rectangle _switchRect;
        private Rectangle[] _thRects = new Rectangle[0];
        private List<RankRow> _topKeys = new List<RankRow>();
        private List<RankRow> _topApps = new List<RankRow>();

        public override void SetSnapshot(StatsSnapshot s)
        {
            base.SetSnapshot(s);
            _topKeys = TopKeyRows(s.Range.Keys, 8, true);
            _topApps = TopAppRows(s.Range.Apps, 8, true);
        }
        private static readonly int[] Windows = new int[] { 10, 15, 20, 25, 30 };

        public override string Title { get { return "防抖统计"; } }
        public override string Subtitle { get { return "按键抖动拦截记录、热点按键与防抖窗口设置"; } }

        public DebouncePage(IShellActions shell)
        {
            _shell = shell;
            Range = RangeKind.Week;
        }

        protected override void Draw(Graphics g)
        {
            Palette p = Theme.P;
            DrawHeader(g);
            DrawRangeChips(g, 0, 58);

            int y = 100;
            int gap = 14;
            int cardW = (ContentW - gap * 3) / 4;
            int cardH = 100;

            double rate = Snap.Range.Swallowed / (double)Math.Max(1, Snap.Range.Total + Snap.Range.Swallowed);
            int keyCount = 0;
            foreach (KeyValuePair<int, KeyAgg> kv in Snap.Range.Keys) if (kv.Value.Swallowed > 0) keyCount++;
            int appCount = 0;
            foreach (KeyValuePair<string, AppAgg> kv in Snap.Range.Apps) if (kv.Value.Swallowed > 0) appCount++;

            Charts.StatCard(g, new Rectangle(0, y, cardW, cardH), p, "拦截总数", Charts.Number(Snap.Range.Swallowed),
                "范围：" + RangeText(), p.Bad);
            Charts.StatCard(g, new Rectangle(cardW + gap, y, cardW, cardH), p, "抖动率",
                (rate * 100).ToString("F1") + "%", "拦截 / (按键 + 拦截)", p.Warn);
            Charts.StatCard(g, new Rectangle((cardW + gap) * 2, y, cardW, cardH), p, "涉及按键",
                Charts.Number(keyCount), "出现过抖动的按键", p.Accent2);
            Charts.StatCard(g, new Rectangle((cardW + gap) * 3, y, cardW, cardH), p, "涉及应用",
                Charts.Number(appCount), "出现过抖动的应用", p.Accent3);

            int rankY = y + cardH + gap;
            int rankH = 250;
            int leftW = (int)(ContentW * 0.5) - gap / 2;
            int rightW = ContentW - leftW - gap;

            SectionCard(g, new Rectangle(0, rankY, leftW, rankH), "按键拦截排行", "按拦截次数排序");
            if (_topKeys.Count == 0) Ui.TextCenter(g, "这个范围内没有拦截记录", Ui.BodySmall, p.Dim,
                new Rectangle(18, rankY + 90, leftW - 36, 40));
            else Charts.DrawRankList(g, new Rectangle(18, rankY + 58, leftW - 36, rankH - 70), _topKeys, p, Ui.BodySmall);

            SectionCard(g, new Rectangle(leftW + gap, rankY, rightW, rankH), "应用拦截排行", "按应用统计");
            if (_topApps.Count == 0) Ui.TextCenter(g, "这个范围内没有拦截记录", Ui.BodySmall, p.Dim,
                new Rectangle(leftW + gap + 18, rankY + 90, rightW - 36, 40));
            else Charts.DrawRankList(g, new Rectangle(leftW + gap + 18, rankY + 58, rightW - 36, rankH - 70), _topApps, p, Ui.BodySmall);

            int bottomY = rankY + rankH + gap;
            int bottomH = ContentH - bottomY;
            SectionCard(g, new Rectangle(0, bottomY, ContentW, bottomH), "防抖设置与实时拦截", "修改后立即生效");
            DrawSettings(g, new Rectangle(20, bottomY + 56, 440, bottomH - 70), p);
            DrawLive(g, new Rectangle(ContentW / 2 + 20, bottomY + 56, ContentW / 2 - 40, bottomH - 70), p);
        }

        private void DrawSettings(Graphics g, Rectangle r, Palette p)
        {
            bool on = _shell.Cfg.FilterEnabled;
            _switchRect = new Rectangle(r.X, r.Y + 2, 44, 24);
            Charts.Switch(g, _switchRect, on, p);
            Ui.TextLeft(g, on ? "防抖过滤已开启" : "防抖过滤已关闭（仍统计）", Ui.Body, on ? p.Good : p.Warn,
                new Rectangle(r.X + 58, r.Y, 260, 26));
            Ui.TextLeftEllipsis(g, "仅控制过滤；关闭后仍继续记录按键统计", Ui.Label, p.Dim,
                new Rectangle(r.X, r.Y + 30, r.Width, 18));

            Ui.TextLeft(g, "防抖窗口", Ui.BodySmall, p.Muted, new Rectangle(r.X, r.Y + 62, 100, 22));
            string[] labels = new string[Windows.Length];
            for (int i = 0; i < Windows.Length; i++) labels[i] = Windows[i] + " ms";
            int sel = 1;
            for (int i = 0; i < Windows.Length; i++) if (Windows[i] == _shell.Cfg.ThresholdMs) sel = i;
            DrawChips(g, labels, sel, r.X, r.Y + 86, 26, out _thRects);

            Ui.TextLeftEllipsis(g, "按下间隔短于窗口或 30ms 内的机械弹跳被过滤；" +
                "正常重按 / 连续组合键（Ctrl+C 等）不拦。", Ui.Label, p.Dim,
                new Rectangle(r.X, r.Y + 112, r.Width, 30));
        }

        private void DrawLive(Graphics g, Rectangle r, Palette p)
        {
            Ui.TextLeft(g, "最近拦截", Ui.BodySmall, p.Muted, new Rectangle(r.X, r.Y - 4, 120, 22));
            List<RecentSwallow> list = Snap.RecentSwallows;
            if (list == null || list.Count == 0)
            {
                Ui.TextCenter(g, "暂无拦截记录", Ui.BodySmall, p.Dim, new Rectangle(r.X, r.Y + 30, r.Width, 40));
                return;
            }

            int rowH = 24;
            int maxRows = Math.Max(1, r.Height / rowH);
            int count = Math.Min(Math.Min(5, maxRows), list.Count);
            for (int i = 0; i < count; i++)
            {
                RecentSwallow s = list[i];
                int y = r.Y + 24 + i * rowH;
                Ui.TextLeft(g, s.At.ToString("HH:mm:ss"), Ui.Mono, p.Dim, new Rectangle(r.X, y, 66, rowH));
                Ui.TextLeft(g, KeyLayout.LabelFor(s.Vk), Ui.BodySmall, p.Text, new Rectangle(r.X + 72, y, 48, rowH));
                Ui.TextLeftEllipsis(g, AppNames.Display(s.App), Ui.BodySmall, p.Muted,
                    new Rectangle(r.X + 124, y, r.Width - 260, rowH));
                Ui.TextRight(g, s.GapDown.ToString("F1") + " ms", Ui.Mono, p.Muted,
                    new Rectangle(r.Right - 90, y, 90, rowH));
            }
        }

        /// <summary>PR-8：阈值模拟——用拦截间隔直方图估算不同阈值下的过滤影响。</summary>
        private void DrawThresholdSimulation(Graphics g, Rectangle r, Palette p)
        {
            Ui.TextLeft(g, "阈值模拟（PR-8）", Ui.BodySmall, p.Muted, new Rectangle(r.X, r.Y - 4, 200, 20));

            SwallowGapHist hist = null;
            if (Snap != null && Snap.GapHistograms != null &&
                Snap.GapHistograms.TryGetValue(0x45, out hist) && hist.Count > 0)
            {
                SimulationResult result = PolicySimulator.Simulate(0x45, Snap);
                int rowH = 18;
                int maxRows = Math.Max(1, (r.Height - 24) / rowH);
                int count = Math.Min(result.Rows.Count, maxRows);
                for (int i = 0; i < count; i++)
                {
                    SimulationRow row = result.Rows[i];
                    int ry = r.Y + 22 + i * rowH;
                    bool isCurrent = row.ThresholdMs == _shell.Cfg.ThresholdMs;
                    Ui.TextLeft(g, row.ThresholdMs + " ms", Ui.Label, isCurrent ? p.Accent1 : p.Muted,
                        new Rectangle(r.X, ry, 50, rowH));
                    Ui.TextLeft(g, row.EstimatedFiltered + " 条", Ui.Label, p.Text,
                        new Rectangle(r.X + 54, ry, 60, rowH));
                    double pct = row.SharePercent;
                    Ui.TextLeft(g, pct.ToString("F0") + "%", Ui.Label,
                        pct > 50 ? p.Warn : p.Muted, new Rectangle(r.X + 118, ry, 40, rowH));
                    if (!string.IsNullOrEmpty(row.RiskNote))
                        Ui.TextLeftEllipsis(g, row.RiskNote, Ui.Label, p.Dim,
                            new Rectangle(r.X + 162, ry, r.Width - 162, rowH));
                }
                if (result.RecommendedThresholdMs > 0)
                {
                    Ui.TextLeft(g, "推荐：" + result.RecommendedThresholdMs + " ms（样本 " +
                        hist.Count + "，预测不等于真实误拦截）", Ui.Label, p.Good,
                        new Rectangle(r.X, r.Bottom - 18, r.Width, 16));
                }
            }
            else
            {
                Ui.TextCenter(g, "数据不足——正常使用后此处显示不同阈值的拦截模拟",
                    Ui.Label, p.Dim, new Rectangle(r.X, r.Y + 30, r.Width, 30));
            }
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

            if (_switchRect.Contains(pt))
            {
                _shell.SetEnabled(!_shell.Cfg.FilterEnabled);
                Invalidate();
                return;
            }
            int idx = HitRects(pt, _thRects);
            if (idx >= 0 && idx < Windows.Length)
            {
                _shell.SetThreshold(Windows[idx]);
                Invalidate();
            }
        }
    }
}
