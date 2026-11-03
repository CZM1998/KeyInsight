using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using KeyInsight.Stats;

namespace KeyInsight.UI.Pages
{
    /// <summary>
    /// 鼠标统计页（复审方案 PR-1/PR-2）：
    /// - 统计口径：点击（键 1-5）与滚轮刻度（6-9）分开统计；最常用按键不含滚轮。
    /// - 视觉：分层矢量鼠标插画（阴影/外壳/分件/热区/标注），热区只用单色相蓝色阶，
    ///   计数走外部引导线标注，不写在鼠标表面。
    /// - 热区与命中区共用同一套归一化几何（Zones），缩放时统一变换。
    /// </summary>
    public sealed class MousePage : PageBase
    {
        private readonly IShellActions _shell;
        private List<RankRow> _appRows = new List<RankRow>();
        private long _wheelSum;
        private long _clickSum;
        private long _sideSum;
        private int _topButton;
        private int _selectedButton;                 // 0 = 无；点击热区或排行行后保持高亮
        private int _hoverZone;                      // 鼠标插画热区悬停（0 = 无）
        private RectangleF _mouseBody;               // 最近一帧鼠标本体矩形（命中反算用）
        private bool _mouseBodyValid;

        private sealed class RankEntry
        {
            public int Button;
            public string Label;
            public long Value;
        }
        private List<RankEntry> _rankEntries = new List<RankEntry>();
        private readonly List<Rectangle> _rankRects = new List<Rectangle>();
        private int _hoverRank = -1;

        public override string Title { get { return "鼠标统计"; } }
        public override string Subtitle { get { return "点击与滚轮分开统计 · 微动异常仅检测不拦截"; } }

        // 鼠标主体 + 两侧标注需要约 300px 卡内高；底部两卡再需 ~230px
        protected override int MinContentHeight { get { return 800; } }

        public MousePage(IShellActions shell)
        {
            _shell = shell;
            Range = RangeKind.Week;
        }

        public override void SetSnapshot(StatsSnapshot s)
        {
            base.SetSnapshot(s);
            _appRows = AppRows(s.Range.Apps);
            _wheelSum = WheelSum(s.Range.Mouse);
            long clicks = 0;
            for (int b = 1; b <= 5; b++) clicks += Count(s.Range.Mouse, b);
            _clickSum = clicks;
            _sideSum = Count(s.Range.Mouse, MouseIds.Back) + Count(s.Range.Mouse, MouseIds.Forward);
            _topButton = TopButton(s.Range.Mouse);

            // 排行：五个物理键 + 四向滚轮；行可点击并联动热区高亮
            List<RankEntry> entries = new List<RankEntry>();
            for (int b = 1; b <= 9; b++)
            {
                long v = Count(s.Range.Mouse, b);
                if (v <= 0) continue;
                RankEntry e = new RankEntry();
                e.Button = b;
                e.Label = MouseNames.Full(b);
                e.Value = v;
                entries.Add(e);
            }
            entries.Sort(delegate(RankEntry a, RankEntry b)
            {
                if (a.Value != b.Value) return b.Value.CompareTo(a.Value);
                return a.Button.CompareTo(b.Button);
            });
            _rankEntries = entries;
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

            Charts.StatCard(g, new Rectangle(0, y, cardW, cardH), p, "点击次数", Charts.Number(_clickSum),
                "左/右/中/侧键（不含滚轮）", p.Accent1);
            Charts.StatCard(g, new Rectangle(cardW + gap, y, cardW, cardH), p, "滚轮刻度", Charts.Number(_wheelSum),
                "上下左右累计 · 高分辨率按 120 归格", p.Info);
            Charts.StatCard(g, new Rectangle((cardW + gap) * 2, y, cardW, cardH), p, "最常用按键",
                _topButton > 0 ? MouseNames.Short(_topButton) : "-",
                _topButton > 0 ? Charts.Number(Count(Snap.Range.Mouse, _topButton)) + " 次" : "暂无数据", p.Accent3);
            Charts.StatCard(g, new Rectangle((cardW + gap) * 3, y, cardW, cardH), p, "侧键 4/5",
                Charts.Number(_sideSum), "网页前进 / 后退", p.Accent2);

            int contentY = y + cardH + gap;
            int leftW = (int)(ContentW * 0.46);
            int rightW = ContentW - leftW - gap;
            int midH = 340;

            SectionCard(g, new Rectangle(0, contentY, leftW, midH), "鼠标结构", "蓝色热度 = 相对使用频率");
            DrawMouse(g, new Rectangle(12, contentY + 50, leftW - 24, midH - 64), Snap.Range.Mouse, p);

            SectionCard(g, new Rectangle(leftW + gap, contentY, rightW, midH), "按键排行",
                _selectedButton > 0 ? "点击行可在左侧高亮热区（已选 " + MouseNames.Short(ZoneButton(_selectedButton)) + "）"
                                    : "点击行可在左侧高亮热区");
            DrawButtonRank(g, new Rectangle(leftW + gap + 18, contentY + 54, rightW - 36, midH - 68), p);

            int bottomY = contentY + midH + gap;
            int bottomH = ContentH - bottomY;
            int appsW = (int)(ContentW * 0.58);
            int funW = ContentW - appsW - gap;

            SectionCard(g, new Rectangle(0, bottomY, appsW, bottomH), "Top 应用（鼠标点击）", "按前台应用统计点击与滚轮");
            List<RankRow> apps = _appRows;
            if (apps.Count == 0)
            {
                Ui.TextCenter(g, "这个范围内还没有应用数据", Ui.BodySmall, p.Dim,
                    new Rectangle(18, bottomY + 80, appsW - 36, 40));
            }
            else
            {
                Charts.DrawRankList(g, new Rectangle(18, bottomY + 58, appsW - 36, bottomH - 120),
                    apps, p, Ui.BodySmall);
            }
            Ui.TextLeftEllipsis(g,
                "Windows 只暴露 5 个鼠标键；更多侧键见键盘热力图。",
                Ui.Label, p.Dim, new Rectangle(18, bottomY + bottomH - 24, appsW - 36, 18));
            Ui.TextLeftEllipsis(g,
                "V2 观察：原始滚轮 delta " + Charts.Number(_shell.MouseRawWheelDelta) +
                " · 微动候选 " + Charts.Number(_shell.MouseCycleCandidates) +
                " 次（被动统计仅是候选，主动测试后才能下结论）",
                Ui.Label, p.Dim, new Rectangle(18, bottomY + bottomH - 42, appsW - 36, 16));

            SectionCard(g, new Rectangle(appsW + gap, bottomY, funW, bottomH), "趣味换算（估算）",
                "滚轮刻度按 1 格 ≈ 3 cm 估算，非真实滚动距离");
            DrawFun(g, new Rectangle(appsW + gap + 18, bottomY + 56, funW - 36, bottomH - 70), p);
        }

        // ---------------------------------------------------------------- 排行（可点击联动热区）

        private void DrawButtonRank(Graphics g, Rectangle r, Palette p)
        {
            _rankRects.Clear();
            _hoverRank = -1;
            List<RankEntry> rows = _rankEntries;
            if (rows.Count == 0)
            {
                Ui.TextCenter(g, "这个范围内还没有鼠标数据", Ui.BodySmall, p.Dim,
                    new Rectangle(r.X, r.Y + 60, r.Width, 40));
                return;
            }

            long max = 1;
            for (int i = 0; i < rows.Count; i++) if (rows[i].Value > max) max = rows[i].Value;
            int rowH = Math.Min(30, Math.Max(24, r.Height / rows.Count));
            Point cur = CursorToContent();
            int count = Math.Min(rows.Count, r.Height / rowH);

            for (int i = 0; i < count; i++)
            {
                RankEntry row = rows[i];
                Rectangle rect = new Rectangle(r.X, r.Y + i * rowH, r.Width, rowH - 4);
                _rankRects.Add(rect);

                bool selected = row.Button != 0 && _selectedButton != 0 &&
                    ZoneButton(_selectedButton) == ZoneButton(row.Button);
                bool hover = rect.Contains(cur);
                if (selected) Ui.FillRound(g, rect, 7, p.Selected);
                else if (hover) Ui.FillRound(g, rect, 7, p.Hover);

                Ui.TextLeft(g, (i + 1).ToString(), Ui.Mono, p.Dim, new Rectangle(rect.X + 2, rect.Y, 22, rowH));
                Ui.TextLeft(g, row.Label, Ui.BodySmall, p.Text,
                    new Rectangle(rect.X + 26, rect.Y, rect.Width - 120, rowH));
                Ui.TextRight(g, Charts.Number(row.Value) + " 次", Ui.Label, p.Muted,
                    new Rectangle(rect.Right - 88, rect.Y, 86, rowH));

                RectangleF barBg = new RectangleF(rect.X + 26, rect.Bottom - 5, r.Width - 30, 3f);
                Ui.FillRound(g, barBg, 1.5f, p.CardAlt);
                float w = (r.Width - 30) * (row.Value / (float)max);
                if (w < 2 && row.Value > 0) w = 2;
                if (w > 0)
                {
                    Ui.GradientRound(g, new RectangleF(barBg.X, barBg.Y, w, 3f), 1.5f,
                        p.Accent2, p.Accent1, 0f);
                }
            }
        }

        /// <summary>排行行的按钮 → 热区（四向滚轮都落在中键/滚轮槽热区）。</summary>
        private static int ZoneButton(int button)
        {
            return button >= MouseIds.WheelUp ? MouseIds.Middle : button;
        }

        // ---------------------------------------------------------------- 鼠标插画（分层矢量）

        private sealed class ZoneGeom
        {
            public int Button;              // MouseIds.*
            public GraphicsPath Path;       // 归一化 0..100 × 0..160；显示与命中共用
            public PointF Anchor;           // 引导线锚点（归一化）
            public string Label;
            public bool AnchorLeft;         // 标注框放左侧
            public int SlotY;               // 同侧标注槽位（0 上 … 2 下）
        }

        private static readonly ZoneGeom[] Zones = BuildZones();

        private static ZoneGeom[] BuildZones()
        {
            List<ZoneGeom> list = new List<ZoneGeom>();

            // 左键（与外壳同源的贝塞尔，沿用现版造型）
            GraphicsPath left = new GraphicsPath();
            left.AddBezier(P(50, 2), P(35, 2), P(25, 8), P(21, 22));
            left.AddBezier(P(21, 22), P(17, 38), P(14, 56), P(14, 76));
            left.AddBezier(P(14, 76), P(22, 82), P(34, 84), P(48, 84));
            left.AddLine(P(48, 84), P(49, 2));
            left.CloseFigure();

            // 右键（镜像）
            GraphicsPath right = new GraphicsPath();
            right.AddBezier(P(50, 2), P(65, 2), P(75, 8), P(79, 22));
            right.AddBezier(P(79, 22), P(83, 38), P(86, 56), P(86, 76));
            right.AddBezier(P(86, 76), P(78, 82), P(66, 84), P(52, 84));
            right.AddLine(P(52, 84), P(51, 2));
            right.CloseFigure();

            // 滚轮槽 / 中键热区（中央凹槽）
            GraphicsPath wheel = RoundPath(42.5f, 13, 57.5f, 64, 7);

            // 侧键实体（与左侧壳体连成一体的凸起结构）
            GraphicsPath back = RoundPath(4, 72, 19, 94, 6);
            GraphicsPath fwd = RoundPath(4, 102, 19, 124, 6);

            list.Add(new ZoneGeom { Button = MouseIds.Left, Path = left, Anchor = P(30, 34), Label = "左键", AnchorLeft = true, SlotY = 0 });
            list.Add(new ZoneGeom { Button = MouseIds.Right, Path = right, Anchor = P(70, 34), Label = "右键", AnchorLeft = false, SlotY = 0 });
            list.Add(new ZoneGeom { Button = MouseIds.Middle, Path = wheel, Anchor = P(50, 46), Label = "中键", AnchorLeft = false, SlotY = 1 });
            list.Add(new ZoneGeom { Button = MouseIds.Back, Path = back, Anchor = P(11, 82), Label = "侧键 4", AnchorLeft = true, SlotY = 1 });
            list.Add(new ZoneGeom { Button = MouseIds.Forward, Path = fwd, Anchor = P(11, 113), Label = "侧键 5", AnchorLeft = false, SlotY = 2 });
            return list.ToArray();
        }

        private static PointF P(float x, float y)
        {
            return new PointF(x, y);
        }

        private static GraphicsPath RoundPath(float x1, float y1, float x2, float y2, float radius)
        {
            RectangleF r = new RectangleF(Math.Min(x1, x2), Math.Min(y1, y2),
                Math.Abs(x2 - x1), Math.Abs(y2 - y1));
            return Ui.RoundRect(r, radius);
        }

        /// <summary>单色相蓝色阶（复审方案 §4.3）：低 #245AA0 → 中 #347FE0 → 高 #58A9FF → 极高 #72D4E5。</summary>
        private static Color HeatRamp(double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            Color c0 = Hex(0x245AA0);
            Color c1 = Hex(0x347FE0);
            Color c2 = Hex(0x58A9FF);
            Color c3 = Hex(0x72D4E5);
            if (t < 1.0 / 3.0) return Ui.Blend(c0, c1, (float)(t * 3.0));
            if (t < 2.0 / 3.0) return Ui.Blend(c1, c2, (float)((t - 1.0 / 3.0) * 3.0));
            return Ui.Blend(c2, c3, (float)((t - 2.0 / 3.0) * 3.0));
        }

        private static Color Hex(int v)
        {
            return Color.FromArgb((v >> 16) & 255, (v >> 8) & 255, v & 255);
        }

        private static Color NeutralZone(Palette p)
        {
            return p.Dark ? Hex(0x26384F) : Hex(0x334B66);
        }

        private void DrawMouse(Graphics g, Rectangle r, Dictionary<int, long> mouse, Palette p)
        {
            long max = 1;
            for (int b = 1; b <= 5; b++)
            {
                long v = Count(mouse, b);
                if (v > max) max = v;
            }
            long wheelMax = Math.Max(1, WheelSum(mouse));

            // 主体自适应：高度优先，宽度受两侧标注约束（复审方案：主体约 180×250 起步）
            int bodyH = Math.Min(r.Height - 4, 300);
            int bodyW = (int)Math.Min(bodyH * 0.66, Math.Max(96, r.Width * 0.46));
            if (bodyH < 120) bodyH = Math.Min(160, Math.Max(120, r.Height - 4));
            bodyW = Math.Max(70, (int)(bodyH * 0.66));
            if (bodyW > r.Width - 60) bodyW = Math.Max(64, r.Width - 60);

            RectangleF body = new RectangleF(
                r.X + (r.Width - bodyW) / 2f,
                r.Y + (r.Height - bodyH) / 2f,
                bodyW, bodyH);
            _mouseBody = body;
            _mouseBodyValid = true;

            Func<float, float, PointF> at = delegate(float x, float y)
            {
                return new PointF(body.X + x * body.Width / 100f,
                                  body.Y + y * body.Height / 160f);
            };

            // L0 接触阴影：柔和椭圆，避免悬浮感
            using (SolidBrush sh = new SolidBrush(Color.FromArgb(p.Dark ? 90 : 46, 8, 16, 30)))
            {
                RectangleF shadow = new RectangleF(body.X + 6, body.Bottom - 12, body.Width * 0.9f, 16);
                g.FillEllipse(sh, shadow);
            }

            // L1 外壳：石墨蓝分层渐变 + 顶部高光
            Color shellTop = p.Dark ? Hex(0x33465F) : Color.White;
            Color shellBottom = p.Dark ? Hex(0x1B2A3F) : Color.FromArgb(228, 235, 244);
            using (GraphicsPath bp = BodyPathOf(at))
            {
                using (LinearGradientBrush bg = new LinearGradientBrush(
                    new RectangleF(body.X, body.Y, body.Width, body.Height), shellTop, shellBottom, 90f))
                {
                    g.FillPath(bg, bp);
                }
                using (Pen border = new Pen(p.Dark ? Hex(0x4A6180) : Hex(0x8FA6C4), 1.4f))
                {
                    g.DrawPath(border, bp);
                }
            }
            using (Pen hl = new Pen(Color.FromArgb(p.Dark ? 40 : 170, 255, 255, 255), 2f))
            {
                g.DrawBezier(hl, at(50, 4), at(33, 4), at(23, 11), at(20, 26));
            }

            // 左手拇指托（结构与分件，不属于热区）
            using (GraphicsPath thumb = new GraphicsPath())
            {
                thumb.AddBezier(at(12, 76), at(20, 88), at(21, 118), at(16, 150));
                thumb.AddLine(at(16, 150), at(28, 150));
                thumb.AddBezier(at(28, 150), at(35, 118), at(35, 88), at(27, 76));
                thumb.CloseFigure();
                using (SolidBrush tb = new SolidBrush(Ui.WithAlpha(
                    p.Dark ? Color.Black : Color.White, p.Dark ? 42 : 90)))
                {
                    g.FillPath(tb, thumb);
                }
            }

            // L3 动态热区（先收集命中结果，再统一绘制，保证热区=命中区）
            _hoverZone = -1;
            int hovered = -1;
            Point cur = CursorToContent();
            for (int i = 0; i < Zones.Length; i++)
            {
                if (Zones[i].Path.IsVisible(NormPoint(cur, body))) hovered = Zones[i].Button;
            }

            for (int i = 0; i < Zones.Length; i++)
            {
                ZoneGeom z = Zones[i];
                long n = Count(mouse, z.Button);
                double t = n <= 0 ? 0 : Math.Log(1 + n) / Math.Log(1 + max);
                Color heat = n <= 0 ? NeutralZone(p) : HeatRamp(t);

                bool isHover = _hoverZonePending == z.Button || hovered == z.Button;
                bool selected = _selectedButton != 0 && ZoneButton(_selectedButton) == z.Button;

                using (GraphicsPath zp = Transformed(z.Path, at))
                {
                    int alpha = p.Dark ? (n > 0 ? 175 : 120) : (n > 0 ? 150 : 92);
                    Color fill = Color.FromArgb(alpha, heat);
                    if (isHover) fill = Ui.Blend(fill, Hex(0xA6D8FF), 0.45f);
                    using (SolidBrush sb = new SolidBrush(fill))
                    {
                        g.FillPath(sb, zp);
                    }
                    Color stroke = isHover || selected ? Hex(0xA6D8FF) : Ui.Blend(heat, Color.White, 0.22f);
                    using (Pen pen = new Pen(stroke, selected ? 2f : 1.1f))
                    {
                        g.DrawPath(pen, zp);
                    }
                    // 键面高光：体积感
                    if (z.Button == MouseIds.Left || z.Button == MouseIds.Right)
                    {
                        using (Pen sheen = new Pen(Color.FromArgb(60, 255, 255, 255), 1.2f))
                        {
                            if (z.Button == MouseIds.Left)
                                g.DrawBezier(sheen, at(24, 8), at(20, 30), at(18, 52), at(19, 72));
                            else
                                g.DrawBezier(sheen, at(76, 8), at(80, 30), at(82, 52), at(81, 72));
                        }
                    }
                }
            }
            _hoverZone = hovered;

            // L2 分件细节：中缝、键缝、滚轮槽、DPI 键（外观结构，不计入热区）
            using (Pen split = new Pen(Ui.WithAlpha(p.Dark ? Color.Black : Color.White, 80), 1.2f))
            {
                g.DrawLine(split, at(50, 3), at(50, 16));
                g.DrawLine(split, at(50, 60), at(50, 83));
            }
            using (Pen seam = new Pen(Ui.WithAlpha(p.Dark ? Hex(0x0E1826) : Hex(0xC7D2E2), 190), 1.2f))
            {
                g.DrawBezier(seam, at(14, 78), at(30, 85), at(70, 85), at(86, 78));
            }

            // 滚轮橡胶体（在槽内，带纹理；中键点击区独立于滚动）
            RectangleF recess = RectBetween(at(42.5f, 13), at(57.5f, 64));
            Ui.FillRound(g, recess, 7, p.Dark ? Hex(0x141F30) : Hex(0xB9C4D6));
            long midCount = Count(mouse, MouseIds.Middle);
            RectangleF wheel = RectBetween(at(45.5f, 18), at(54.5f, 60));
            Color wheelBase = p.Dark ? Hex(0x3A4459) : Hex(0xD7DEE9);
            if (midCount > 0)
            {
                double wt = Math.Log(1 + midCount) / Math.Log(1 + max);
                wheelBase = Ui.Blend(wheelBase, HeatRamp(wt), 0.55f);
            }
            using (GraphicsPath wp = Ui.RoundRect(wheel, 5))
            using (LinearGradientBrush wb = new LinearGradientBrush(wheel,
                Ui.Blend(wheelBase, Color.White, 0.16f),
                Ui.Blend(wheelBase, Color.Black, 0.18f), 90f))
            {
                g.FillPath(wb, wp);
            }
            using (Pen ridge = new Pen(Ui.WithAlpha(p.TextOn(wheelBase), 110), 1f))
            {
                for (int i = 0; i < 4; i++)
                {
                    float yy = wheel.Y + wheel.Height * (0.2f + i * 0.18f);
                    g.DrawLine(ridge, wheel.X + 2.5f, yy, wheel.Right - 2.5f, yy);
                }
            }

            // DPI 键（外观结构）
            RectangleF dpi = RectBetween(at(46.5f, 68), at(53.5f, 74));
            Ui.FillRound(g, dpi, 2, Ui.Blend(shellTop, shellBottom, 0.5f));
            Ui.StrokeRound(g, dpi, 2, p.Border);

            // L4 外部引导线标注（计数不写在鼠标表面）
            DrawCallouts(g, body, r, mouse, max, wheelMax, p);
        }

        /// <summary>归一化坐标 → 画布坐标后的外壳路径。</summary>
        private static GraphicsPath BodyPathOf(Func<float, float, PointF> at)
        {
            GraphicsPath body = new GraphicsPath();
            body.AddBezier(at(50, 1), at(34, 1), at(24, 8), at(20, 22));
            body.AddBezier(at(20, 22), at(15, 40), at(11, 62), at(11, 86));
            body.AddBezier(at(11, 86), at(11, 112), at(15, 136), at(27, 150));
            body.AddBezier(at(27, 150), at(34, 157), at(42, 160), at(50, 160));
            body.AddBezier(at(50, 160), at(58, 157), at(66, 157), at(73, 150));
            body.AddBezier(at(73, 150), at(85, 136), at(89, 112), at(89, 86));
            body.AddBezier(at(89, 86), at(89, 62), at(85, 40), at(80, 22));
            body.AddBezier(at(80, 22), at(76, 8), at(66, 1), at(50, 1));
            body.CloseFigure();
            return body;
        }

        private static GraphicsPath Transformed(GraphicsPath source, Func<float, float, PointF> at)
        {
            GraphicsPath clone = (GraphicsPath)source.Clone();
            Matrix m = new Matrix();
            PointF p0 = at(0, 0);
            PointF p100 = at(100, 160);
            m.Translate(p0.X, p0.Y);
            m.Scale((p100.X - p0.X) / 100f, (p100.Y - p0.Y) / 160f);
            clone.Transform(m);
            m.Dispose();
            return clone;
        }

        private static PointF NormPoint(Point pt, RectangleF body)
        {
            if (body.Width <= 0 || body.Height <= 0) return new PointF(-1, -1);
            return new PointF((pt.X - body.X) * 100f / body.Width,
                              (pt.Y - body.Y) * 160f / body.Height);
        }

        // 引导线标注：左侧 左键/侧键4，右侧 右键/中键/侧键5（槽位防重叠）
        private void DrawCallouts(Graphics g, RectangleF body, Rectangle r,
            Dictionary<int, long> mouse, long max, long wheelMax, Palette p)
        {
            int boxW = Math.Min(96, (int)((r.Width - body.Width) / 2f) - 8);
            if (boxW < 56) return;   // 窄卡：只保留热区，不画标注框

            int boxH = 38;
            for (int side = 0; side < 2; side++)
            {
                bool isLeft = side == 0;
                List<ZoneGeom> zs = new List<ZoneGeom>();
                foreach (ZoneGeom z in Zones)
                {
                    if (z.AnchorLeft == isLeft) zs.Add(z);
                }
                zs.Sort(delegate(ZoneGeom a, ZoneGeom b) { return a.SlotY.CompareTo(b.SlotY); });

                for (int i = 0; i < zs.Count; i++)
                {
                    ZoneGeom z = zs[i];
                    long n = Count(mouse, z.Button);
                    bool selected = _selectedButton != 0 && ZoneButton(_selectedButton) == z.Button;
                    bool hover = _hoverZone == z.Button;

                    // 槽位均匀分布在本侧高度上
                    float fy = (i + 0.5f) / zs.Count;
                    int by = (int)(body.Y + body.Height * fy - boxH / 2f);
                    if (by < r.Y) by = r.Y;
                    if (by + boxH > r.Bottom) by = (int)(r.Bottom - boxH);
                    int bx = isLeft ? r.X : (int)(r.Right - boxW);

                    Rectangle box = new Rectangle(bx, by, boxW, boxH);
                    using (GraphicsPath bp = Ui.RoundRect(new RectangleF(box.X, box.Y, box.Width, box.Height), 8))
                    {
                        using (SolidBrush fb = new SolidBrush(Ui.WithAlpha(p.Card, 235)))
                        {
                            g.FillPath(fb, bp);
                        }
                        Color bc = selected || hover ? p.Accent1 : p.Border;
                        using (Pen pen = new Pen(bc, selected ? 1.6f : 1f))
                        {
                            g.DrawPath(pen, bp);
                        }
                    }
                    Ui.TextCenter(g, z.Label, Ui.Label,
                        selected ? p.Accent1 : p.Text, new Rectangle(box.X, box.Y + 4, box.Width, 16));
                    Ui.TextCenter(g, Charts.Number(n), Ui.BodySmall,
                        n > 0 ? p.Muted : p.Dim, new Rectangle(box.X, box.Y + 20, box.Width, 16));

                    // 引导线：标注框内侧 → 热区锚点，端点小圆点
                    PointF anchor = AtNorm(body, z.Anchor);
                    PointF start = new PointF(isLeft ? box.Right : box.X, box.Y + boxH / 2f);
                    Color lc = selected || hover ? p.Accent1 : Ui.WithAlpha(p.Border, 220);
                    using (Pen pen = new Pen(lc, 1f))
                    {
                        g.DrawLine(pen, start.X, start.Y, anchor.X, anchor.Y);
                    }
                    using (SolidBrush dot = new SolidBrush(lc))
                    {
                        g.FillEllipse(dot, anchor.X - 2.5f, anchor.Y - 2.5f, 5, 5);
                    }
                }
            }
        }

        private static PointF AtNorm(RectangleF body, PointF n)
        {
            return new PointF(body.X + n.X * body.Width / 100f,
                              body.Y + n.Y * body.Height / 160f);
        }

        private static RectangleF RectBetween(PointF a, PointF b)
        {
            return new RectangleF(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
                Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        }

        // ---------------------------------------------------------------- 趣味换算

        private void DrawFun(Graphics g, Rectangle r, Palette p)
        {
            long ticks = _wheelSum;
            double meters = ticks * 0.03;
            string distance = meters >= 1000
                ? (meters / 1000.0).ToString("F1") + " km"
                : meters.ToString("F1") + " m";
            string scrollSub;
            if (ticks <= 0) scrollSub = "还没有滚轮数据";
            else if (meters < 100) scrollSub = "≈ 爬 " + Math.Max(1, (int)Math.Round(meters / 3.0)) + " 层楼（估算）";
            else if (meters < 1000) scrollSub = "≈ " + (meters / 400.0).ToString("F2") + " 个 400 米操场（估算）";
            else if (meters < 10000) scrollSub = "≈ " + (meters / 330.0).ToString("F1") + " 个埃菲尔铁塔（估算）";
            else scrollSub = "≈ 珠峰的 " + (meters / 8848.0 * 100).ToString("F1") + "%（估算）";

            long clicks = _clickSum;
            long side = _sideSum;

            int rowH = 36;
            int maxRows = Math.Max(1, r.Height / rowH);
            if (maxRows >= 1)
            {
                DrawFunRow(g, new Rectangle(r.X, r.Y, r.Width, rowH), p,
                    "滚轮距离（估算）", distance, scrollSub);
            }
            if (maxRows >= 2)
            {
                DrawFunRow(g, new Rectangle(r.X, r.Y + rowH, r.Width, rowH), p,
                    "鼠标点击", Charts.Number(clicks) + " 次",
                    clicks > 0 ? "≈ 每秒 2 次连点 " + Math.Max(1, (int)Math.Round(clicks / 120.0)) + " 分钟" : "还没有点击数据");
            }
            if (maxRows >= 3)
            {
                DrawFunRow(g, new Rectangle(r.X, r.Y + rowH * 2, r.Width, rowH), p,
                    "侧键 4/5", Charts.Number(side) + " 次",
                    side > 0 ? "≈ " + Charts.Number(side) + " 次网页前进/后退" : "还没有侧键数据");
            }
        }

        private static void DrawFunRow(Graphics g, Rectangle r, Palette p, string label, string value, string sub)
        {
            Ui.TextLeft(g, label, Ui.BodySmall, p.Muted, new Rectangle(r.X, r.Y, r.Width - 130, 20));
            Ui.TextRight(g, value, Ui.Section, p.Text, new Rectangle(r.Right - 150, r.Y, 150, 20));
            Ui.TextLeftEllipsis(g, sub, Ui.Label, p.Dim, new Rectangle(r.X, r.Y + 18, r.Width, 16));
        }

        // ---------------------------------------------------------------- 数据辅助

        private static long Count(Dictionary<int, long> mouse, int button)
        {
            long v;
            return mouse != null && mouse.TryGetValue(button, out v) ? v : 0;
        }

        private static long WheelSum(Dictionary<int, long> mouse)
        {
            long n = 0;
            for (int b = MouseIds.WheelUp; b <= MouseIds.WheelRight; b++) n += Count(mouse, b);
            return n;
        }

        /// <summary>最常用按键仅统计物理键 1..5，滚轮单独排名（复审方案 §2.4）。</summary>
        private static int TopButton(Dictionary<int, long> mouse)
        {
            int best = 0;
            long max = 0;
            for (int b = MouseIds.Left; b <= MouseIds.Forward; b++)
            {
                long v = Count(mouse, b);
                if (v > max) { max = v; best = b; }
            }
            return best;
        }

        private static List<RankRow> AppRows(Dictionary<string, AppAgg> apps)
        {
            List<KeyValuePair<string, long>> list = new List<KeyValuePair<string, long>>();
            foreach (KeyValuePair<string, AppAgg> kv in apps)
            {
                long n = 0;
                Dictionary<int, long> m = kv.Value.Mouse;
                if (m != null) foreach (long v in m.Values) n += v;
                if (n > 0) list.Add(new KeyValuePair<string, long>(kv.Key, n));
            }
            list.Sort(delegate(KeyValuePair<string, long> a, KeyValuePair<string, long> b)
            {
                if (a.Value != b.Value) return b.Value.CompareTo(a.Value);
                return string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
            });
            List<RankRow> rows = new List<RankRow>();
            for (int i = 0; i < list.Count && rows.Count < 8; i++)
            {
                RankRow row = new RankRow();
                row.Label = AppNames.Display(list[i].Key);
                row.Value = list[i].Value;
                rows.Add(row);
            }
            return rows;
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

        // ---------------------------------------------------------------- 交互

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Point pt = ToContent(e.Location);
            if (HandleRangeClick(pt)) return;

            // 排行行：点击高亮对应热区
            for (int i = 0; i < _rankRects.Count && i < _rankEntries.Count; i++)
            {
                if (_rankRects[i].Contains(pt))
                {
                    int btn = _rankEntries[i].Button;
                    _selectedButton = _selectedButton == btn ? 0 : btn;
                    Invalidate();
                    return;
                }
            }

            // 鼠标热区：点击切换保持高亮（命中区与显示热区同一几何）
            if (_mouseBodyValid)
            {
                PointF norm = NormPoint(pt, _mouseBody);
                for (int i = 0; i < Zones.Length; i++)
                {
                    if (Zones[i].Path.IsVisible(norm))
                    {
                        int btn = Zones[i].Button;
                        _selectedButton = _selectedButton == btn ? 0 : btn;
                        Invalidate();
                        return;
                    }
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_mouseBodyValid) return;
            PointF norm = NormPoint(ToContent(e.Location), _mouseBody);
            int hover = 0;
            for (int i = 0; i < Zones.Length; i++)
            {
                if (Zones[i].Path.IsVisible(norm)) { hover = Zones[i].Button; break; }
            }
            if (hover != _hoverZonePending)
            {
                _hoverZonePending = hover;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverZonePending != 0)
            {
                _hoverZonePending = 0;
                Invalidate();
            }
        }

        private int _hoverZonePending;
    }
}
