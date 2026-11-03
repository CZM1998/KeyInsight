using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using KeyInsight.Adapters.Display;
using KeyInsight.Core.Devices;

namespace KeyInsight.UI
{
    /// <summary>
    /// 显示器体检向导（方案 §8.4/§8.5，PR-7）：
    /// - 全屏图案绑定所选屏幕；Esc 随时退出并恢复窗口状态；
    /// - 六类测试：坏点（纯色）、灰阶、色带、均匀性、清晰度、动态表现；
    /// - 结果一律 UserConfirmed：用户对每类选择 正常/发现异常/无法判断；
    ///   用户确认类不伪造置信度（Confidence 为空，方案 §15.2）；
    /// - 启动前提示 HDR/缩放/夜间模式等环境因素。
    /// </summary>
    public sealed class MonitorTestForm : Form
    {
        private readonly IShellActions _shell;
        private readonly DeviceRecord _record;
        private readonly DisplayAdapter.MonitorInfo _monitor;
        private readonly List<TestPattern> _patterns = new List<TestPattern>();
        private int _index;

        /// <summary>结果行（向导与结果窗之间的公开 DTO）。</summary>
        public sealed class VerdictInput
        {
            public string Category;
            public string Pattern;
            public int State;               // 0=正常 1=发现异常 2=无法判断
        }

        private sealed class TestPattern
        {
            public string Code;
            public string Category;
            public string Title;
            public string Hint;
            public int Kind;                // 0 纯色 1 灰阶 2 渐变 3 清晰度 4 动态
            public Color Solid;
        }

        private readonly List<VerdictInput> _verdicts = new List<VerdictInput>();
        private bool _finished;

        public MonitorTestForm(IShellActions shell, DeviceRecord record, DisplayAdapter.MonitorInfo monitor)
        {
            _shell = shell;
            _record = record;
            _monitor = monitor;

            BuildPatterns();

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            DoubleBuffered = true;
            BackColor = Color.Black;
            Bounds = ScreenBoundsFor(monitor);
        }

        private static Rectangle ScreenBoundsFor(DisplayAdapter.MonitorInfo monitor)
        {
            if (monitor != null && !string.IsNullOrEmpty(monitor.GdiDeviceName))
            {
                foreach (Screen s in Screen.AllScreens)
                {
                    if (string.Equals(s.DeviceName, monitor.GdiDeviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        return s.Bounds;
                    }
                }
            }
            return Screen.PrimaryScreen.Bounds;
        }

        private void BuildPatterns()
        {
            // 坏点/亮点：纯色连测
            _patterns.Add(Make("display.dead_pixel.black", "坏点/亮点", "纯黑", 0, Color.Black, "寻找不随背景变化的亮点"));
            _patterns.Add(Make("display.dead_pixel.white", "坏点/亮点", "纯白", 0, Color.White, "寻找黑点/暗点"));
            _patterns.Add(Make("display.dead_pixel.red", "坏点/亮点", "纯红", 0, Color.FromArgb(255, 0, 0), "寻找异常亮点"));
            _patterns.Add(Make("display.dead_pixel.blue", "坏点/亮点", "纯蓝", 0, Color.FromArgb(0, 0, 255), "寻找异常亮点"));
            _patterns.Add(Make("display.grayscale.ramp", "灰阶", "灰阶阶梯", 1, Color.Black, "最暗/最亮档位是否可辨"));
            _patterns.Add(Make("display.band.gradient", "色带", "彩色渐变", 2, Color.Black, "渐变是否平滑、有无异常色带"));
            _patterns.Add(Make("display.uniformity.gray50", "均匀性", "50% 灰", 0, Color.FromArgb(128, 128, 128), "四角与中心亮度/色度是否一致"));
            _patterns.Add(Make("display.sharpness.grid", "清晰度", "棋盘格 + 细线", 3, Color.Black, "观察像素边界、摩尔纹与模糊"));
            _patterns.Add(Make("display.motion.block", "动态表现", "运动方块", 4, Color.Black, "观察拖影/撕裂（自动播放）"));
        }

        private static TestPattern Make(string code, string category, string title, int kind,
            Color solid, string hint)
        {
            TestPattern p = new TestPattern();
            p.Code = code;
            p.Category = category;
            p.Title = title;
            p.Kind = kind;
            p.Solid = solid;
            p.Hint = hint;
            return p;
        }

        /// <summary>启动体检（前置提示 → 全屏 → 结果记录）。</summary>
        public void RunTest()
        {
            bool go = Dialogs.Confirm(null, "显示器体检",
                "将在「" + (string.IsNullOrEmpty(_monitor.Model) ? _monitor.GdiDeviceName : _monitor.Model) +
                "」上全屏显示测试图案。\n\n" +
                "建议先退出全屏独占程序，并检查 HDR / 夜间模式 / 缩放设置（这些会影响观察）。\n" +
                "测试中：←/→ 或空格切换图案，Esc 退出并进入结果记录。");
            if (!go) return;

            _verdicts.Clear();
            for (int i = 0; i < _patterns.Count; i++)
            {
                VerdictInput v = new VerdictInput();
                v.Category = _patterns[i].Category;
                v.Pattern = _patterns[i].Title;
                v.State = 2;
                _verdicts.Add(v);
            }
            _index = 0;
            ShowDialog();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Cursor.Hide();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Cursor.Show();
            if (!_finished)
            {
                CollectResults();
            }
            base.OnFormClosed(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                Close();
                return true;
            }
            if (keyData == Keys.Right || keyData == Keys.Space || keyData == Keys.Down)
            {
                _index = Math.Min(_patterns.Count - 1, _index + 1);
                Invalidate();
                return true;
            }
            if (keyData == Keys.Left || keyData == Keys.Up)
            {
                _index = Math.Max(0, _index - 1);
                Invalidate();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            if (_index < 0 || _index >= _patterns.Count) return;
            TestPattern p = _patterns[_index];

            Rectangle full = ClientRectangle;
            switch (p.Kind)
            {
                case 0:
                    using (SolidBrush b = new SolidBrush(p.Solid)) g.FillRectangle(b, full);
                    break;
                case 1: DrawGraySteps(g, full); break;
                case 2: DrawGradientBands(g, full); break;
                case 3: DrawSharpness(g, full); break;
                case 4: DrawMotion(g, full); break;
            }
            DrawHud(g, p);
        }

        private int _motionPhase;

        private void DrawMotion(Graphics g, Rectangle full)
        {
            using (SolidBrush bg = new SolidBrush(Color.Black)) g.FillRectangle(bg, full);
            _motionPhase = (_motionPhase + 18) % Math.Max(1, full.Width - 64);
            using (SolidBrush b = new SolidBrush(Color.White))
            {
                g.FillRectangle(b, _motionPhase, full.Height / 2 - 40, 60, 80);
            }
            using (SolidBrush b2 = new SolidBrush(Color.FromArgb(0, 120, 255)))
            {
                g.FillRectangle(b2, full.Width - 64 - _motionPhase, full.Height / 2 + 60, 60, 80);
            }
            Invalidate();
        }

        private void DrawGraySteps(Graphics g, Rectangle full)
        {
            int steps = 17;
            int w = full.Width / steps;
            for (int i = 0; i < steps; i++)
            {
                int v = i * 255 / (steps - 1);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(v, v, v)))
                {
                    g.FillRectangle(b, i * w, 0, w + 1, full.Height);
                }
            }
        }

        private void DrawGradientBands(Graphics g, Rectangle full)
        {
            Rectangle top = new Rectangle(full.X, full.Y, full.Width, full.Height / 3);
            Rectangle mid = new Rectangle(full.X, full.Y + full.Height / 3, full.Width, full.Height / 3);
            Rectangle bot = new Rectangle(full.X, full.Y + full.Height * 2 / 3, full.Width, full.Height / 3 + 1);
            using (LinearGradientBrush b = new LinearGradientBrush(top, Color.Black, Color.White, 0f)) g.FillRectangle(b, top);
            using (LinearGradientBrush b = new LinearGradientBrush(mid, Color.Black, Color.FromArgb(255, 40, 40), 0f)) g.FillRectangle(b, mid);
            using (LinearGradientBrush b = new LinearGradientBrush(bot, Color.Black, Color.FromArgb(40, 60, 255), 0f)) g.FillRectangle(b, bot);
        }

        private void DrawSharpness(Graphics g, Rectangle full)
        {
            using (SolidBrush bg = new SolidBrush(Color.White)) g.FillRectangle(bg, full);
            using (SolidBrush blk = new SolidBrush(Color.Black))
            {
                int cell = 16;
                for (int y = 0; y < full.Height / cell + 1; y++)
                {
                    for (int x = 0; x < full.Width / cell + 1; x++)
                    {
                        if ((x + y) % 2 == 0)
                        {
                            g.FillRectangle(blk, x * cell, y * cell, cell, cell);
                        }
                    }
                }
                for (int x = 0; x < full.Width; x += 3)
                {
                    g.FillRectangle(blk, x, full.Height * 3 / 4, 1, full.Height / 4);
                }
            }
        }

        private void DrawHud(Graphics g, TestPattern p)
        {
            Rectangle bar = new Rectangle(0, ClientSize.Height - 64, ClientSize.Width, 64);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(190, 10, 16, 28))) g.FillRectangle(b, bar);
            using (Font f = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold))
            using (SolidBrush w = new SolidBrush(Color.White))
            {
                g.DrawString("[" + (_index + 1) + "/" + _patterns.Count + "]  " + p.Category + " · " + p.Title,
                    f, w, 16, ClientSize.Height - 54);
            }
            using (Font f2 = new Font("Microsoft YaHei UI", 9f))
            using (SolidBrush d = new SolidBrush(Color.FromArgb(210, 216, 224, 238)))
            {
                g.DrawString(p.Hint + "    ←/→ 切换 · Esc 退出并记录结果", f2, d, 16, ClientSize.Height - 28);
            }
        }

        // —— 结果记录 ——

        private void CollectResults()
        {
            _finished = true;
            MonitorTestResultForm rf = new MonitorTestResultForm(_verdicts);
            if (rf.ShowDialog() == DialogResult.OK)
            {
                SaveSession(rf.SessionNote);
            }
            rf.Dispose();
        }

        private void SaveSession(string note)
        {
            if (_shell == null || _shell.DeviceCenter == null) return;
            DiagnosticSession session = new DiagnosticSession();
            session.SessionId = Guid.NewGuid().ToString("N");
            session.DeviceId = _record.DeviceId;
            session.DeviceName = _record.Name;
            session.TestType = "display.checkup";
            session.StartedAtUtc = DateTime.UtcNow.AddMinutes(-2);
            session.EndedAtUtc = DateTime.UtcNow;
            session.Status = DiagnosticRunState.Completed;
            session.Note = note;

            List<DiagnosticFinding> findings = new List<DiagnosticFinding>();
            for (int i = 0; i < _verdicts.Count; i++)
            {
                VerdictInput v = _verdicts[i];
                if (v.State == 2) continue;   // 无法判断：不产生 Finding
                if (v.State == 1)
                {
                    DiagnosticFinding f = new DiagnosticFinding();
                    f.FindingId = Guid.NewGuid().ToString("N");
                    f.DiagnosticCode = CodeFor(v.Category);
                    f.Severity = FindingSeverity.Attention;
                    f.Status = FindingStatus.UserConfirmed;
                    f.Confidence = null;      // 用户确认类不伪造置信度（方案 §15.2）
                    f.SampleCount = 1;
                    f.EvidenceSource = EvidenceSource.UserConfirmed;
                    f.EvidenceJson = "{\"category\":\"" + v.Category +
                        "\",\"pattern\":\"" + v.Pattern +
                        "\",\"screen\":\"" + (_monitor.GdiDeviceName ?? "").Replace("\"", "'") + "\"}";
                    f.Recommendation = "建议换一张纯色图案复核，必要时联系设备售后确认。";
                    findings.Add(f);
                }
            }
            _shell.DeviceCenter.AddDiagnosticSession(session, findings);
        }

        private static string CodeFor(string category)
        {
            switch (category)
            {
                case "灰阶": return "display.grayscale.user_observed";
                case "色带": return "display.band.user_observed";
                case "均匀性": return "display.uniformity.user_observed";
                case "清晰度": return "display.sharpness.user_observed";
                case "动态表现": return "display.motion.user_observed";
                default: return "display.dead_pixel.user_observed";
            }
        }
    }

    /// <summary>体检结果记录窗：逐类 正常 / 发现异常 / 无法判断 + 备注。</summary>
    public sealed class MonitorTestResultForm : Form
    {
        private readonly List<MonitorTestForm.VerdictInput> _verdicts;
        private readonly TextBox _note = new TextBox();

        public string SessionNote
        {
            get { return _note.Text.Trim(); }
        }

        public MonitorTestResultForm(List<MonitorTestForm.VerdictInput> verdicts)
        {
            _verdicts = verdicts;

            Text = "显示器体检结果";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(520, Math.Max(360, 90 + verdicts.Count * 44));
            BackColor = Theme.P.Card;
            ForeColor = Theme.P.Text;
            Font = Ui.Body;

            Label title = new Label();
            title.Text = "逐类标记观察结果（记录为「用户确认」，不会自动判定硬件故障）";
            title.ForeColor = Theme.P.Muted;
            title.SetBounds(16, 12, 490, 20);
            Controls.Add(title);

            int y = 44;
            for (int i = 0; i < verdicts.Count; i++)
            {
                Controls.Add(BuildRow(verdicts[i], 16, y));
                y += 44;
            }

            _note.SetBounds(16, y, 490, 26);
            _note.BackColor = Theme.P.CardAlt;
            _note.ForeColor = Theme.P.Text;
            _note.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(_note);
            y += 38;

            Button ok = new Button();
            ok.Text = "保存结果";
            ok.SetBounds(300, y, 96, 30);
            Style(ok);
            ok.Click += delegate { DialogResult = DialogResult.OK; Close(); };
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "不保存";
            cancel.SetBounds(410, y, 96, 30);
            Style(cancel);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancel);
        }

        private static void Style(Button b)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Theme.P.Border;
            b.BackColor = Theme.P.CardAlt;
            b.ForeColor = Theme.P.Text;
        }

        private Control BuildRow(MonitorTestForm.VerdictInput row, int x, int y)
        {
            Panel p = new Panel();
            p.SetBounds(x, y, 490, 40);
            p.BackColor = Theme.P.Card;

            Label name = new Label();
            name.Text = row.Category + " · " + row.Pattern;
            name.SetBounds(0, 10, 236, 22);
            name.ForeColor = Theme.P.Text;
            name.BackColor = Theme.P.Card;
            p.Controls.Add(name);

            string[] options = new string[] { "正常", "发现异常", "无法判断" };
            int bx = 240;
            for (int i = 0; i < options.Length; i++)
            {
                int state = i;
                RadioButton rb = new RadioButton();
                rb.Text = options[i];
                rb.AutoSize = true;
                rb.SetBounds(bx, 10, 82, 22);
                rb.ForeColor = Theme.P.Text;
                rb.BackColor = Theme.P.Card;
                rb.Checked = row.State == state;
                rb.CheckedChanged += delegate { if (rb.Checked) row.State = state; };
                p.Controls.Add(rb);
                bx += 84;
            }
            return p;
        }
    }
}
