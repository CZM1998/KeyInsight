using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using KeyInsight.Stats;

namespace KeyInsight.UI.Pages
{
    public sealed class DebouncePage : PageBase
    {
        private readonly IShellActions _shell;
        private Rectangle _switchRect;
        private Rectangle[] _thRects = new Rectangle[0];
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
            int cardW = (Width - gap * 3) / 4;
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
            int leftW = (int)(Width * 0.5) - gap / 2;
            int rightW = Width - leftW - gap;

            SectionCard(g, new Rectangle(0, rankY, leftW, rankH), "按键拦截排行", "按拦截次数排序");
            List<RankRow> keys = TopKeyRows(Snap.Range.Keys, 8, true);
            if (keys.Count == 0) Ui.TextCenter(g, "这个范围内没有拦截记录", Ui.BodySmall, p.Dim,
                new Rectangle(18, rankY + 90, leftW - 36, 40));
            else Charts.DrawRankList(g, new Rectangle(18, rankY + 58, leftW - 36, rankH - 70), keys, p, Ui.BodySmall);

            SectionCard(g, new Rectangle(leftW + gap, rankY, rightW, rankH), "应用拦截排行", "按应用统计");
            List<RankRow> apps = TopAppRows(Snap.Range.Apps, 8, true);
            if (apps.Count == 0) Ui.TextCenter(g, "这个范围内没有拦截记录", Ui.BodySmall, p.Dim,
                new Rectangle(leftW + gap + 18, rankY + 90, rightW - 36, 40));
            else Charts.DrawRankList(g, new Rectangle(leftW + gap + 18, rankY + 58, rightW - 36, rankH - 70), apps, p, Ui.BodySmall);

            int bottomY = rankY + rankH + gap;
            int bottomH = Height - bottomY;
            SectionCard(g, new Rectangle(0, bottomY, Width, bottomH), "防抖设置与实时拦截", "修改后立即生效");
            DrawSettings(g, new Rectangle(20, bottomY + 56, 440, bottomH - 70), p);
            DrawLive(g, new Rectangle(Width / 2 + 20, bottomY + 56, Width / 2 - 40, bottomH - 70), p);
        }

        private void DrawSettings(Graphics g, Rectangle r, Palette p)
        {
            bool on = _shell.Cfg.Enabled;
            _switchRect = new Rectangle(r.X, r.Y + 2, 44, 24);
            Charts.Switch(g, _switchRect, on, p);
            Ui.TextLeft(g, on ? "防抖已开启" : "防抖已暂停", Ui.Body, on ? p.Good : p.Warn,
                new Rectangle(r.X + 58, r.Y, 220, 26));
            Ui.TextLeftEllipsis(g, "相同按键在窗口内重复按下会被吞掉", Ui.Label, p.Dim,
                new Rectangle(r.X, r.Y + 30, r.Width, 18));

            Ui.TextLeft(g, "防抖窗口", Ui.BodySmall, p.Muted, new Rectangle(r.X, r.Y + 62, 100, 22));
            string[] labels = new string[Windows.Length];
            for (int i = 0; i < Windows.Length; i++) labels[i] = Windows[i] + " ms";
            int sel = 1;
            for (int i = 0; i < Windows.Length; i++) if (Windows[i] == _shell.Cfg.ThresholdMs) sel = i;
            DrawChips(g, labels, sel, r.X, r.Y + 86, 26, out _thRects);

            Ui.TextLeftEllipsis(g, "按下间隔 25 ms + 松开后 40 ms 双窗口；实测抖动 0.7–6.2 ms，" +
                "仍漏网可提高到 30 ms。", Ui.Label, p.Dim,
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
            int count = Math.Min(5, list.Count);
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
            if (HandleRangeClick(e.Location)) return;

            if (_switchRect.Contains(e.Location))
            {
                _shell.SetEnabled(!_shell.Cfg.Enabled);
                Invalidate();
                return;
            }
            int idx = HitRects(e.Location, _thRects);
            if (idx >= 0 && idx < Windows.Length)
            {
                _shell.SetThreshold(Windows[idx]);
                Invalidate();
            }
        }
    }
}
