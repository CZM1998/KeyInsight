using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace KeyInsight.UI
{
    /// <summary>
    /// 主题化消息弹窗（替代系统 MessageBox，风格与主面板一致）：
    /// 圆角卡片、标题 + 正文 + 按钮区，深浅主题跟随 Theme.P。
    /// 用法：Dialogs.Show(owner, "标题", "正文") / Dialogs.Confirm(...) / Dialogs.Error(...)。
    /// </summary>
    public static class Dialogs
    {
        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public static void Show(IWin32Window owner, string title, string message)
        {
            Run(owner, title, message, false);
        }

        public static bool Confirm(IWin32Window owner, string title, string message)
        {
            return Run(owner, title, message, true);
        }

        public static void Info(IWin32Window owner, string title, string message)
        {
            Show(owner, title, message);
        }

        public static void Error(IWin32Window owner, string title, string message)
        {
            DialogForm f = new DialogForm(title, message, false, true);
            f.ShowDialog(owner);
        }

        private static bool Run(IWin32Window owner, string title, string message, bool confirm)
        {
            DialogForm f = new DialogForm(title, message, confirm, false);
            f.ShowDialog(owner);
            return f.Confirmed;
        }

        private sealed class DialogForm : Form
        {
            private readonly Button _ok;
            private readonly Button _cancel;
            private bool _hoverClose;

            public bool Confirmed { get; private set; }

            public DialogForm(string title, string message, bool confirm, bool isError)
            {
                Palette p = Theme.P;
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.CenterParent;
                ShowInTaskbar = false;
                MaximizeBox = false;
                MinimizeBox = false;
                BackColor = p.Card;
                Font = Ui.Body;
                DoubleBuffered = true;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                    ControlStyles.UserPaint, true);

                int width = 420;
                using (Graphics g = CreateGraphics())
                {
                    Size needed = Ui.Measure(g, message, Ui.BodySmall);
                    int textW = Math.Max(180, Math.Min(360, needed.Width));
                    int lines = Math.Max(1, (int)Math.Ceiling(message.Length * 7.2 / 340.0));
                    ClientSize = new Size(width, 96 + lines * 20 + (confirm ? 74 : 62));
                }

                Label titleL = new Label();
                titleL.Text = title;
                titleL.Font = Ui.Section;
                titleL.ForeColor = isError ? p.Bad : p.Text;
                titleL.SetBounds(20, 18, ClientSize.Width - 56, 26);
                titleL.BackColor = Color.Transparent;
                Controls.Add(titleL);

                Label body = new Label();
                body.Text = message;
                body.Font = Ui.BodySmall;
                body.ForeColor = p.Text;
                body.SetBounds(20, 52, ClientSize.Width - 40, ClientSize.Height - (confirm ? 118 : 106));
                body.BackColor = Color.Transparent;
                Controls.Add(body);

                int btnY = ClientSize.Height - 46;
                _ok = new Button();
                _ok.Text = confirm ? "确定" : "知道了";
                _ok.Size = new Size(96, 32);
                _ok.FlatStyle = FlatStyle.Flat;
                _ok.FlatAppearance.BorderSize = 0;
                _ok.BackColor = p.Accent1;
                _ok.ForeColor = Color.White;
                _ok.Font = Ui.Body;
                if (confirm)
                {
                    _ok.SetBounds(ClientSize.Width - 210, btnY, 96, 32);
                    _ok.Click += delegate { Confirmed = true; DialogResult = DialogResult.OK; };
                }
                else
                {
                    _ok.SetBounds(ClientSize.Width - 116, btnY, 96, 32);
                    _ok.Click += delegate { DialogResult = DialogResult.OK; };
                }
                Controls.Add(_ok);

                if (confirm)
                {
                    _cancel = new Button();
                    _cancel.Text = "取消";
                    _cancel.Size = new Size(96, 32);
                    _cancel.FlatStyle = FlatStyle.Flat;
                    _cancel.FlatAppearance.BorderSize = 0;
                    _cancel.BackColor = p.Hover;
                    _cancel.ForeColor = p.Text;
                    _cancel.Font = Ui.Body;
                    _cancel.SetBounds(ClientSize.Width - 106, btnY, 96, 32);
                    _cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
                    Controls.Add(_cancel);
                    CancelButton = _cancel;
                }
                else
                {
                    CancelButton = _ok;
                }

                KeyPreview = true;
                KeyDown += delegate(object s, KeyEventArgs e)
                {
                    if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel;
                };
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                Palette p = Theme.P;
                Graphics g = e.Graphics;
                Ui.Prep(g);
                using (GraphicsPath path = Ui.RoundRect(new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1), 10))
                {
                    g.FillPath(RenderResources.Brush(p.Card), path);
                    g.DrawPath(RenderResources.Pen(p.BorderStrong, 1f), path);
                }
                using (SolidBrush b = new SolidBrush(p.Accent1))
                {
                    g.FillRectangle(b, 0, 0, 4, ClientSize.Height);
                }
                Ui.TextCenter(g, "✕", Ui.GlyphSmall, _hoverClose ? p.Bad : p.Dim,
                    new Rectangle(ClientSize.Width - 26, 12, 18, 18));
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Y < 40)
                {
                    ReleaseCapture();
                    SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
                }
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                bool over = new Rectangle(ClientSize.Width - 26, 12, 18, 18).Contains(e.Location);
                if (over != _hoverClose)
                {
                    _hoverClose = over;
                    Invalidate();
                }
            }
        }
    }
}
