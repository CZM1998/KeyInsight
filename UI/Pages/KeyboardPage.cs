using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using KeyInsight.Stats;

namespace KeyInsight.UI.Pages
{
    public sealed class KeyboardPage : PageBase
    {
        private readonly IShellActions _shell;
        private string _appFilter;
        private string[] _appKeys = new string[0];
        private Rectangle[] _appRects = new Rectangle[0];
        private Rectangle[] _layoutRects = new Rectangle[0];
        private Rectangle[] _layerRects = new Rectangle[0];
        private Dictionary<KeyDef, RectangleF> _hits = new Dictionary<KeyDef, RectangleF>();
        private KeyDef _hover;
        private int _layer; // 0=使用 1=疑似异常候选 2=实际拦截
        private Dictionary<int, long> _layerValues;
        private Rectangle _kbArea;
        private float _keyRadius = 6f;

        private static readonly string[] LayerNames = new string[] { "使用频率", "疑似异常候选", "实际拦截" };

        public override string Title { get { return "键盘热力图"; } }
        public override string Subtitle { get { return "使用频率 / 疑似异常候选 / 实际拦截 三图层；点击按键查看详情"; } }

        public KeyboardPage(IShellActions shell)
        {
            _shell = shell;
            Range = RangeKind.Week;
        }

        private KeyLayoutDef LayoutDef
        {
            get
            {
                switch (_shell.Cfg.KeyboardLayout)
                {
                    case 0: return KeyLayout.Layout87;
                    case 2: return KeyLayout.Layout87Iso;
                    case 3: return KeyLayout.Layout108Iso;
                    default: return KeyLayout.Layout108;
                }
            }
        }

        public override void SetSnapshot(StatsSnapshot s)
        {
            base.SetSnapshot(s);
            // PR-2：图层指标预计算（Draw 内不做聚合）
            _layerValues = BuildLayerValues(s.Range.Keys);
            if (_appFilter != null && !s.Range.Apps.ContainsKey(_appFilter))
            {
                _appFilter = null;
                _hover = null;
            }
        }

        protected override void Draw(Graphics g)
        {
            Palette p = Theme.P;
            DrawHeader(g);
            DrawRangeChips(g, 0, 58);
            DrawLayerChips(g, 58);
            DrawAppChips(g, 0, 92);

            int cardY = 128;
            int statsH = 106;
            int keyboardH = ContentH - cardY - 14 - statsH;
            Rectangle card = new Rectangle(0, cardY, ContentW, keyboardH);
            KeyLayoutDef layout = LayoutDef;
            string scope = _appFilter == null ? "范围：" + RangeText() : "应用：" + AppNames.Display(_appFilter);
            SectionCard(g, card, "按键分布", scope + " · " + layout.ShortName);
            DrawLayoutChips(g, card);

            Dictionary<int, KeyAgg> counts;
            long total;
            long swallowed;
            if (_appFilter == null)
            {
                counts = Snap.Range.Keys;
                total = Snap.Range.Total;
                swallowed = Snap.Range.Swallowed;
            }
            else
            {
                AppAgg app;
                if (Snap.Range.Apps.TryGetValue(_appFilter, out app))
                {
                    counts = app.Keys;
                    total = app.Total;
                    swallowed = app.Swallowed;
                }
                else
                {
                    counts = new Dictionary<int, KeyAgg>();
                    total = 0;
                    swallowed = 0;
                }
            }

            Rectangle kbArea = new Rectangle(card.X + 16, card.Y + 54, card.Width - 32, card.Height - 54 - 42);
            _kbArea = kbArea;
            float unit = Math.Min(kbArea.Width / (layout.WidthUnits + 0.45f),
                kbArea.Height / (KeyLayout.HeightUnits + 0.6f));
            if (unit > 54f) unit = 54f;
            _keyRadius = Math.Max(4f, unit * 0.16f);
            if (_layer == 0)
            {
                _hits = Charts.DrawKeyboard(g, kbArea, counts, p, null, layout);
            }
            else
            {
                if (_layerValues == null) _layerValues = BuildLayerValues(counts);
                _hits = Charts.DrawKeyboardValues(g, kbArea, _layerValues, p, null, layout);
            }
            DrawLegend(g, new Rectangle(card.X + 22, card.Bottom - 30, card.Width - 44, 18), p, counts, total);

            int gap = 14;
            int cardW = (ContentW - gap * 3) / 4;
            int sy = cardY + keyboardH + gap;

            string topKey = "-";
            long topVal = 0;
            int used = 0;
            foreach (KeyDef kd in layout.Keys)
            {
                long n = KeyLayout.CountFor(kd, counts);
                if (n > 0) used++;
                if (n > topVal) { topVal = n; topKey = kd.Label; }
            }

            Charts.StatCard(g, new Rectangle(0, sy, cardW, statsH), p, "已按键数", Charts.Number(total),
                "范围：" + RangeText(), p.Accent1);
            Charts.StatCard(g, new Rectangle(cardW + gap, sy, cardW, statsH), p, "最常用键", topKey,
                topVal > 0 ? Charts.Number(topVal) + " 次" : "暂无数据", p.Accent2);
            Charts.StatCard(g, new Rectangle((cardW + gap) * 2, sy, cardW, statsH), p, "使用过的键",
                used + " / " + layout.KeyCount, "至少按下一次", p.Accent3);
            Charts.StatCard(g, new Rectangle((cardW + gap) * 3, sy, cardW, statsH), p, "拦截次数",
                Charts.Number(swallowed), "该范围内的抖动", p.Bad);
        }

        /// <summary>悬停只重画按键描边与信息卡，复用键盘静态层。</summary>
        protected override void DrawOverlay(Graphics g)
        {
            if (_hover != null)
            {
                RectangleF rec;
                if (_hits.TryGetValue(_hover, out rec))
                {
                    Ui.StrokeRound(g, rec, _keyRadius, Theme.P.Accent1, 2f);
                }
            }
            DrawHoverTip(g);
        }

        private void DrawLayerChips(Graphics g, int y)
        {
            int total = 0;
            int[] ws = new int[LayerNames.Length];
            for (int i = 0; i < LayerNames.Length; i++)
            {
                ws[i] = Ui.MeasureWidth(g, LayerNames[i], Ui.BodySmall) + 26;
                total += ws[i] + 8;
            }
            DrawChips(g, LayerNames, _layer, ContentW - total + 8, y, 26, out _layerRects);
        }

        private void DrawLayoutChips(Graphics g, Rectangle card)
        {
            string[] labels = new string[] { "87", "108", "87 ISO", "108 ISO", "75%", "96%" };
            int total = 0;
            int[] ws = new int[labels.Length];
            for (int i = 0; i < labels.Length; i++)
            {
                ws[i] = Ui.MeasureWidth(g, labels[i], Ui.BodySmall) + 26;
                total += ws[i] + 8;
            }
            int x = card.Right - 18 - total + 8;
            int y = card.Y + 12;
            int sel = Math.Max(0, Math.Min(labels.Length - 1, _shell.Cfg.KeyboardLayout));
            DrawChips(g, labels, sel, x, y, 24, out _layoutRects);
        }

        private Dictionary<int, long> BuildLayerValues(Dictionary<int, KeyAgg> counts)
        {
            Dictionary<int, long> values = new Dictionary<int, long>();
            if (_layer == 2)
            {
                if (counts != null)
                {
                    foreach (KeyValuePair<int, KeyAgg> kv in counts)
                    {
                        if (kv.Value.Swallowed > 0) values[kv.Key] = kv.Value.Swallowed;
                    }
                }
                return values;
            }
            // 图层 1：疑似异常候选 —— 来自诊断侧车聚合直方图（最近 30 天，与统计范围无关）
            if (Snap != null && Snap.GapHistograms != null)
            {
                foreach (KeyValuePair<int, SwallowGapHist> kv in Snap.GapHistograms)
                {
                    if (kv.Value.Count > 0) values[kv.Key] = kv.Value.Count;
                }
            }
            return values;
        }

        private void DrawAppChips(Graphics g, int x, int y)
        {
            List<KeyValuePair<string, AppAgg>> list = new List<KeyValuePair<string, AppAgg>>(Snap.Range.Apps);
            list.Sort(delegate(KeyValuePair<string, AppAgg> a, KeyValuePair<string, AppAgg> b)
            {
                return b.Value.Total.CompareTo(a.Value.Total);
            });

            List<string> labels = new List<string>();
            List<string> keys = new List<string>();
            labels.Add("全部应用");
            keys.Add(null);
            int usedW = Ui.MeasureWidth(g, "全部应用", Ui.BodySmall) + 26 + 8;
            int budget = Math.Max(240, ContentW - 40);
            for (int i = 0; i < list.Count && labels.Count < 7; i++)
            {
                if (list[i].Value.Total <= 0) continue;
                string label = AppNames.Display(list[i].Key);
                int w = Ui.MeasureWidth(g, label, Ui.BodySmall) + 26 + 8;
                if (usedW + w > budget) break;
                labels.Add(label);
                keys.Add(list[i].Key);
                usedW += w;
            }
            if (_appFilter != null && !keys.Contains(_appFilter))
            {
                labels.Add(AppNames.Display(_appFilter));
                keys.Add(_appFilter);
            }

            int selected = 0;
            for (int i = 1; i < keys.Count; i++) if (keys[i] == _appFilter) { selected = i; break; }

            _appKeys = keys.ToArray();
            DrawChips(g, labels.ToArray(), selected, x, y, 26, out _appRects);
        }

        private void DrawLegend(Graphics g, Rectangle r, Palette p, Dictionary<int, KeyAgg> counts, long total)
        {
            int x = r.X;
            Ui.TextLeft(g, "少", Ui.Label, p.Dim, new Rectangle(x, r.Y, 18, r.Height));
            x += 20;
            int block = 16;
            for (int i = 0; i < p.Heat.Length; i++)
            {
                Color c = p.HeatColor(i / (double)(p.Heat.Length - 1));
                Ui.FillRound(g, new RectangleF(x, r.Y + 5, block, r.Height - 10), 2, c);
                x += block + 2;
            }
            Ui.TextLeft(g, "多", Ui.Label, p.Dim, new Rectangle(x + 4, r.Y, 18, r.Height));
            int lx = x + 42;
            if (_layer == 0)
            {
                int cy = r.Y + r.Height / 2;
                using (SolidBrush ring = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                {
                    g.FillEllipse(ring, lx - 6, cy - 6, 12, 12);
                }
                using (SolidBrush b = new SolidBrush(Color.FromArgb(190, 30, 30)))
                {
                    g.FillEllipse(b, lx - 4, cy - 4, 8, 8);
                }
                Ui.TextLeft(g, "红点 = 抖动，颜色越深次数越多", Ui.Label, p.Muted,
                    new Rectangle(lx + 12, r.Y, 320, r.Height));
            }
            else
            {
                Ui.TextLeft(g, _layer == 1 ? "颜色 = 疑似异常候选（30 天拦截样本，仅观察）" : "颜色 = 实际拦截次数",
                    Ui.Label, p.Muted, new Rectangle(lx, r.Y, 380, r.Height));
            }
            Ui.TextRight(g, "颜色越深 = " + (_layer == 0 ? "使用越多" : _layer == 1 ? "候选越多" : "拦截越多"),
                Ui.Label, p.Dim, new Rectangle(r.Right - 220, r.Y, 220, r.Height));
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

        public void PreviewHover(string label)
        {
            if (label == null)
            {
                _hover = null;
                Invalidate();
                return;
            }
            foreach (KeyDef key in KeyLayout.Layout108.Keys)
            {
                if (key.Label == label)
                {
                    _hover = key;
                    Invalidate();
                    return;
                }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Point pt = ToContent(e.Location);
            if (HandleRangeClick(pt)) return;

            int la = HitRects(pt, _layerRects);
            if (la >= 0 && la < LayerNames.Length)
            {
                _layer = la;
                _layerValues = BuildLayerValues(CurrentCounts());
                Invalidate();
                return;
            }

            int li = HitRects(pt, _layoutRects);
            if (li >= 0)
            {
                _shell.Cfg.KeyboardLayout = li;
                _shell.Cfg.Save();
                _hover = null;
                Invalidate();
                return;
            }

            int idx = HitRects(pt, _appRects);
            if (idx >= 0 && idx < _appKeys.Length)
            {
                _appFilter = _appKeys[idx];
                _hover = null;
                Invalidate();
                return;
            }

            // P1-03：点击按键 → 打开唯一按键详情（物理键身份经 VK 映射到统计口径）
            foreach (KeyValuePair<KeyDef, RectangleF> kv in _hits)
            {
                if (kv.Value.Contains(pt.X, pt.Y) && kv.Key.Vks.Length > 0)
                {
                    using (KeyDetailForm f = new KeyDetailForm(_shell, kv.Key.Vks[0]))
                    {
                        f.ShowDialog(FindForm());
                    }
                    Invalidate();
                    return;
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            KeyDef found = null;
            foreach (KeyValuePair<KeyDef, RectangleF> kv in _hits)
            {
                Point mp = ToContent(e.Location);
                if (kv.Value.Contains(mp.X, mp.Y)) { found = kv.Key; break; }
            }
            if (found != _hover)
            {
                _hover = found;
                InvalidateOverlay();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover != null)
            {
                _hover = null;
                InvalidateOverlay();
            }
        }

        private void DrawHoverTip(Graphics g)
        {
            if (_hover == null || Snap == null) return;
            RectangleF keyRect;
            if (!_hits.TryGetValue(_hover, out keyRect)) return;

            Dictionary<int, KeyAgg> counts = CurrentCounts();
            long n = KeyLayout.CountFor(_hover, counts);
            long sw = KeyLayout.SwallowedFor(_hover, counts);
            long total = CurrentTotal();
            double share = total > 0 ? n * 100.0 / total : 0;
            Palette p = Theme.P;

            int w = 214;
            int h = 116;
            int x = (int)keyRect.Right + 12;
            if (x + w > ContentW - 8) x = (int)keyRect.Left - w - 12;
            if (x < 8) x = 8;
            int y = (int)keyRect.Top - 10;
            if (y + h > ViewBottom - 8) y = ViewBottom - h - 8;
            if (y < ViewTop + 8) y = ViewTop + 8;
            Rectangle r = new Rectangle(x, y, w, h);

            Ui.FillRound(g, new Rectangle(r.X + 3, r.Y + 5, w, h), 12, Color.FromArgb(80, 0, 0, 0));
            Ui.FillRound(g, r, 12, p.Card);
            Ui.StrokeRound(g, r, 12, p.BorderStrong);

            Rectangle accentRect = new Rectangle(r.X + 14, r.Y + 10, w - 28, 3);
            using (System.Drawing.Drawing2D.GraphicsPath accent = Ui.RoundRect(accentRect, 1.5f))
            using (System.Drawing.Drawing2D.LinearGradientBrush b =
                new System.Drawing.Drawing2D.LinearGradientBrush(accentRect, p.Accent1, p.Accent3, 0f))
            {
                g.FillPath(b, accent);
            }

            Ui.TextLeft(g, _hover.Label, Ui.Section, p.Text,
                new Rectangle(r.X + 14, r.Y + 18, 120, 24));
            Ui.TextRight(g, share.ToString("F1") + "%", Ui.BodySmall, p.Muted,
                new Rectangle(r.Right - 76, r.Y + 18, 62, 24));
            Ui.TextLeft(g, Charts.Number(n) + " 次", Ui.CardValueSmall, p.Text,
                new Rectangle(r.X + 14, r.Y + 40, 150, 26));
            Ui.TextLeft(g, _appFilter == null ? "全部应用" : AppNames.Display(_appFilter),
                Ui.Label, p.Dim, new Rectangle(r.X + 14, r.Y + 66, w - 28, 16));
            Ui.Divider(g, r.X + 14, r.Y + 86, w - 28, p);

            Color sc = sw > 0 ? p.Bad : p.Good;
            Ui.DrawDot(g, r.X + 19, r.Y + 100, 4, sc);
            Ui.TextLeft(g, sw > 0 ? "抖动拦截" : "未出现抖动", Ui.BodySmall, p.Muted,
                new Rectangle(r.X + 28, r.Y + 90, 110, 20));
            Ui.TextRight(g, Charts.Number(sw) + " 次", Ui.Label, sc,
                new Rectangle(r.Right - 80, r.Y + 90, 66, 20));
        }

        private Dictionary<int, KeyAgg> CurrentCounts()
        {
            if (Snap == null) return null;
            if (_appFilter == null) return Snap.Range.Keys;
            AppAgg app;
            if (Snap.Range.Apps.TryGetValue(_appFilter, out app)) return app.Keys;
            return null;
        }

        private long CurrentTotal()
        {
            if (Snap == null) return 0;
            if (_appFilter == null) return Snap.Range.Total;
            AppAgg app;
            if (Snap.Range.Apps.TryGetValue(_appFilter, out app)) return app.Total;
            return 0;
        }
    }
}
