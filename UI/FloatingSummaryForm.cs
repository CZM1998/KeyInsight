using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using KeyInsight.Stats;

namespace KeyInsight.UI
{
    /// <summary>
    /// 可选桌面悬浮摘要（P2-05）：置顶小窗，显示今日按键/鼠标/拦截与 KPM。
    /// 拖动移动（位置持久化到配置），点右上角关闭（等价于配置里关掉开关）。
    /// </summary>
    public sealed class FloatingSummaryForm : Form
    {
        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private readonly StatsEngine _stats;
        private readonly Config _cfg;
        private readonly Timer _timer;
        private Rectangle _closeRect;
        private bool _hoverClose;

        public FloatingSummaryForm(StatsEngine stats, Config cfg)
        {
            _stats = stats;
            _cfg = cfg;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ShowInTaskbar = false;
            ClientSize = new Size(300, 84);
            BackColor = Theme.P.Card;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint, true);

            int x = cfg.FloatingSummaryX;
            int y = cfg.FloatingSummaryY;
            if (x < 0 || y < 0)
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                x = wa.Right - ClientSize.Width - 24;
                y = 24;
            }
            Location = new Point(x, y);

            _timer = new Timer();
            _timer.Interval = 1000;
            _timer.Tick += delegate { Invalidate(); };
            _timer.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Ui.Prep(e.Graphics);
            Palette p = Theme.P;
            Graphics g = e.Graphics;
            g.Clear(BackColor);

            Ui.FillRound(g, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), 10, p.CardAlt);
            Ui.StrokeRound(g, new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1), 10, p.BorderStrong);

            long keys, swallowed;
            _stats.GetTodayCounts(out keys, out swallowed);
            long mouse = _stats.GetTodayMouseCount();

            Ui.TextLeft(g, "今日", Ui.Label, p.Muted, new Rectangle(14, 8, 40, 18));
            Ui.TextLeft(g, Charts.Number(keys) + " 键", Ui.Section, p.Text, new Rectangle(14, 26, 120, 24));
            Ui.TextLeft(g, Charts.Number(mouse) + " 鼠标", Ui.BodySmall, p.Muted, new Rectangle(14, 52, 120, 20));
            Ui.TextLeft(g, "拦截 " + Charts.Number(swallowed), Ui.BodySmall, p.Warn,
                new Rectangle(140, 30, 100, 22));

            _closeRect = new Rectangle(ClientSize.Width - 26, 8, 18, 18);
            Ui.TextCenter(g, "✕", Ui.GlyphSmall, _hoverClose ? p.Bad : p.Dim, _closeRect);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_closeRect.Contains(e.Location))
            {
                _cfg.FloatingSummary = false;
                _cfg.Save();
                Hide();
                return;
            }
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); // WM_NCLBUTTONDOWN / HTCAPTION
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool over = _closeRect.Contains(e.Location);
            if (over != _hoverClose)
            {
                _hoverClose = over;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _cfg.FloatingSummaryX = Location.X;
            _cfg.FloatingSummaryY = Location.Y;
            _cfg.Save();
        }

        public void StopUpdates()
        {
            _timer.Stop();
        }
    }
}
