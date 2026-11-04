using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;
using KeyInsight.Adapters.Camera;
using KeyInsight.Core.Devices;

namespace KeyInsight.UI
{
    /// <summary>
    /// 摄像头预览测试（方案 §9.3，PR-10 预览项）：
    /// - 必须由用户在设备详情页点击「预览测试」启动（§9.3 硬约束）；
    /// - 界面常驻「摄像头正在使用」指示；窗口关闭即停流并释放
    ///   Reader / Source（ShutdownObject）/ MF（§9.3 资源释放要求）；
    /// - 不录制、不截图、不保存任何画面（§12.3 隐私红线）。
    /// </summary>
    public sealed class CameraPreviewForm : Form
    {
        private readonly string _symbolicLink;
        private readonly string _cameraName;
        private readonly Panel _view;
        private readonly Label _status;
        private CameraAdapter.PreviewSession _session;
        private volatile bool _cancel;
        private Bitmap _frame;
        private readonly object _frameGate = new object();

        public CameraPreviewForm(string symbolicLink, string cameraName)
        {
            _symbolicLink = symbolicLink;
            _cameraName = cameraName;

            Text = "摄像头预览 · " + (string.IsNullOrEmpty(cameraName) ? "摄像头" : cameraName);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            ClientSize = new Size(680, 540);
            BackColor = Theme.P.Card;
            ForeColor = Theme.P.Text;
            Font = Ui.Body;

            _view = new Panel();
            _view.SetBounds(16, 14, 648, 400);
            _view.BackColor = Color.FromArgb(10, 14, 22);
            _view.Paint += delegate(object s, PaintEventArgs e)
            {
                Bitmap frame = null;
                lock (_frameGate) { frame = _frame; }
                if (frame == null) return;
                Graphics g = e.Graphics;
                g.InterpolationMode = InterpolationMode.Bilinear;
                double scale = Math.Min(_view.Width / (double)frame.Width,
                    _view.Height / (double)frame.Height);
                int w = Math.Max(1, (int)(frame.Width * scale));
                int h = Math.Max(1, (int)(frame.Height * scale));
                g.DrawImage(frame, (_view.Width - w) / 2, (_view.Height - h) / 2, w, h);
            };
            Controls.Add(_view);

            _status = new Label();
            _status.SetBounds(16, 424, 648, 40);
            _status.ForeColor = Theme.P.Warn;
            _status.Text = "正在打开摄像头…";
            Controls.Add(_status);

            Button stop = new Button();
            stop.Text = "停止预览并关闭";
            stop.SetBounds(16, 476, 150, 32);
            stop.FlatStyle = FlatStyle.Flat;
            stop.FlatAppearance.BorderColor = Theme.P.Border;
            stop.BackColor = Theme.P.CardAlt;
            stop.ForeColor = Theme.P.Text;
            stop.Click += delegate { Close(); };
            Controls.Add(stop);

            Label hint = new Label();
            hint.Text = "预览仅在窗口打开期间取流；不录制、不截图、不保存任何画面。";
            hint.ForeColor = Theme.P.Dim;
            hint.SetBounds(180, 484, 470, 20);
            Controls.Add(hint);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            ThreadPool.QueueUserWorkItem(delegate { OpenAndStream(); });
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _cancel = true;
            base.OnFormClosing(e);
        }

        private void OpenAndStream()
        {
            try { CoInit(); } catch { }
            using (CameraAdapter.PreviewSession session =
                CameraAdapter.OpenPreview(_symbolicLink))
            {
                _session = session;
                if (!session.Ok)
                {
                    SafeStatus("无法打开摄像头（设备离线、被占用或不支持）");
                    return;
                }
                SafeStatus("摄像头正在使用（预览中 · " + session.Width + "x" + session.Height +
                    "）· 关闭窗口即停止取流并释放");
                while (!_cancel)
                {
                    Bitmap frame = CameraAdapter.ReadPreviewFrame(
                        session.Reader, session.Width, session.Height);
                    if (frame != null)
                    {
                        Bitmap old = null;
                        try
                        {
                            BeginInvoke((MethodInvoker)delegate
                            {
                                lock (_frameGate)
                                {
                                    old = _frame;
                                    _frame = frame;
                                }
                                _view.Invalidate();
                            });
                        }
                        catch (ObjectDisposedException) { break; }
                        if (old != null) old.Dispose();
                    }
                }
            }
        }

        private void SafeStatus(string text)
        {
            try
            {
                BeginInvoke((MethodInvoker)delegate { _status.Text = text; });
            }
            catch { }
        }

        private static void CoInit()
        {
            // OpenPreview 内部会按需 CoInitializeEx；此处无需额外操作
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _cancel = true;
                SessionDisposeWait wait = new SessionDisposeWait(_session);
                System.Threading.ThreadPool.QueueUserWorkItem(delegate { wait.Run(); });
                lock (_frameGate)
                {
                    if (_frame != null) { _frame.Dispose(); _frame = null; }
                }
            }
            base.Dispose(disposing);
        }

        /// <summary>后台等待预览线程结束后再 Dispose 会话（避免与 ReadSample 并发释放）。</summary>
        private sealed class SessionDisposeWait
        {
            private readonly CameraAdapter.PreviewSession _session;

            public SessionDisposeWait(CameraAdapter.PreviewSession session)
            {
                _session = session;
            }

            public void Run()
            {
                Thread.Sleep(300);      // 给取流线程留出退出窗口
                if (_session != null) _session.Dispose();
            }
        }
    }
}
