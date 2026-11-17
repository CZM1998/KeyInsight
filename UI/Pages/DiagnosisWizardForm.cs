using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using KeyInsight.Core.Input;
using KeyInsight.Core.Policy;

namespace KeyInsight.UI.Pages
{
    /// <summary>
    /// 诊断测试向导（P1-04）：
    /// 1. 选择按键 → 采集专用测试（单击 / 快速双击 / 长按）；
    /// 2. 采集期间全局暂停过滤（PolicyController 临时快照），绝不改动用户配置；
    /// 3. 分析只在内存中进行，输出样本量、候选数与建议阈值；
    /// 4. 只有用户点「应用」才写入单键策略并切到单键保守模式；
    /// 5. 失焦 / 关闭 / 异常一律恢复原策略（FormClosed 中兜底）。
    /// </summary>
    public sealed class DiagnosisWizardForm : Form, IMessageFilter
    {
        private const int WmKeyDown = 0x0100;
        private const int WmKeyUp = 0x0101;
        private const int WmSysKeyDown = 0x0104;
        private const int WmSysKeyUp = 0x0105;
        private const int NumpadEnter = 0x1000D;

        [DllImport("user32.dll")]
        private static extern uint GetMessageTime();

        private readonly IShellActions _shell;
        private readonly int _vk;
        private readonly Label _progress;
        private readonly Label _result;
        private readonly TextBox _box;
        private readonly Button _apply;

        private int _presses;
        private int _completedPresses;
        private int _candidateCount;
        private uint _minCandidate;
        private uint _maxCandidate;
        private uint _downMs;
        private uint _previousUpMs;
        private PhysicalKeyId _capturedKey;
        private bool _previousPressWasShort;
        private bool _isDown;
        private bool _capturing;
        private int _suggestedThreshold;

        public DiagnosisWizardForm(IShellActions shell, int vk)
        {
            _shell = shell;
            _vk = vk;

            Text = "诊断测试 · " + KeyLayout.LabelFor(vk);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(460, 360);
            BackColor = Theme.P.Card;
            ForeColor = Theme.P.Text;
            Font = Ui.Body;
            TopMost = true;

            Label title = new Label();
            title.Text = "1) 在下方文本框内操作目标按键：" + KeyLayout.LabelFor(vk) + "（其它按键不计入）";
            title.SetBounds(16, 12, 430, 20);
            Controls.Add(title);

            Label hint = new Label();
            hint.Text = "建议：单击 20 次 → 快速双击 10 组 → 长按 5 轮。停止采集后自动恢复防抖过滤。";
            hint.SetBounds(16, 34, 430, 32);
            hint.ForeColor = Theme.P.Muted;
            Controls.Add(hint);

            _box = new TextBox();
            _box.Multiline = true;
            _box.ReadOnly = true;
            _box.ScrollBars = ScrollBars.Vertical;
            _box.SetBounds(16, 70, 428, 110);
            _box.BackColor = Theme.P.CardAlt;
            _box.ForeColor = Theme.P.Text;
            Controls.Add(_box);

            _progress = new Label();
            _progress.SetBounds(16, 188, 428, 20);
            _progress.ForeColor = Theme.P.Muted;
            Controls.Add(_progress);

            _result = new Label();
            _result.SetBounds(16, 210, 428, 70);
            _result.ForeColor = Theme.P.Text;
            Controls.Add(_result);

            Button start = new Button();
            start.Text = "开始采集";
            start.SetBounds(16, 300, 96, 32);
            start.Click += delegate { StartCapture(); };
            Controls.Add(start);

            Button analyze = new Button();
            analyze.Text = "完成并分析";
            analyze.SetBounds(120, 300, 110, 32);
            analyze.Click += delegate { Analyze(); };
            Controls.Add(analyze);

            _apply = new Button();
            _apply.Text = "应用建议";
            _apply.SetBounds(238, 300, 100, 32);
            _apply.Enabled = false;
            _apply.Click += delegate { ApplySuggestion(); };
            Controls.Add(_apply);

            Button close = new Button();
            close.Text = "关闭";
            close.SetBounds(346, 300, 98, 32);
            close.DialogResult = DialogResult.Cancel;
            Controls.Add(close);

            _box.LostFocus += delegate { StopCapture(); };
            Deactivate += delegate { StopCapture(); };
        }

        private void SuspendFiltering()
        {
            PolicyController.Update(PolicyController.Current.WithoutFiltering());
        }

        private void StopCapture()
        {
            if (!_capturing) return;
            _capturing = false;
            Application.RemoveMessageFilter(this);
            PolicyController.Refresh(_shell.Cfg);
            UpdateProgress();
        }

        private void StartCapture()
        {
            _presses = 0;
            _completedPresses = 0;
            _candidateCount = 0;
            _minCandidate = uint.MaxValue;
            _maxCandidate = 0;
            _previousPressWasShort = false;
            _isDown = false;
            _suggestedThreshold = 0;
            _apply.Enabled = false;
            _result.Text = "";
            if (_box.Focus() && !_capturing)
            {
                SuspendFiltering();
                _capturing = true;
                Application.AddMessageFilter(this);
            }
            UpdateProgress();
        }

        bool IMessageFilter.PreFilterMessage(ref Message message)
        {
            bool down = message.Msg == WmKeyDown || message.Msg == WmSysKeyDown;
            if (!down && message.Msg != WmKeyUp && message.Msg != WmSysKeyUp) return false;
            if (!_capturing || message.HWnd != _box.Handle) return false;

            int vk = message.WParam.ToInt32();
            uint keyData = unchecked((uint)message.LParam.ToInt64());
            int scan = (int)((keyData >> 16) & 0xFF);
            bool extended = (keyData & 0x01000000) != 0;
            if (vk == 0x10) vk = scan == 0x36 ? 0xA1 : 0xA0;
            if (vk == 0x11) vk = extended ? 0xA3 : 0xA2;
            if (vk == 0x12) vk = extended ? 0xA5 : 0xA4;
            int statsVk = vk == 0x0D && extended ? NumpadEnter : vk;
            if (statsVk != _vk) return false;

            PhysicalKeyId key = new PhysicalKeyId(vk, scan, extended);
            if (_presses > 0 && !key.Equals(_capturedKey)) return true;
            uint now = GetMessageTime();
            if (down)
            {
                if (_isDown || (keyData & 0x40000000) != 0) return true;
                _isDown = true;
                _capturedKey = key;
                _presses++;
                uint gap = unchecked(now - _previousUpMs);
                if (_previousPressWasShort && gap < LegacyPolicyAdapter.ChatterPressMaxMs)
                {
                    _candidateCount++;
                    _minCandidate = Math.Min(_minCandidate, gap);
                    _maxCandidate = Math.Max(_maxCandidate, gap);
                }
                _downMs = now;
            }
            else
            {
                if (!_isDown) return true;
                _isDown = false;
                _completedPresses++;
                _previousUpMs = now;
                _previousPressWasShort = unchecked(now - _downMs) < LegacyPolicyAdapter.ChatterPressMaxMs;
            }
            UpdateProgress();
            return true;
        }

        private void UpdateProgress()
        {
            _progress.Text = "已采集按下：" + _presses + " 次 · 完整按压：" + _completedPresses +
                (_capturing ? " · 采集中…" : "");
        }

        private void Analyze()
        {
            StopCapture();

            if (_completedPresses < 8)
            {
                _result.Text = "样本不足：" + _completedPresses + " 个完整按压（至少需要 8 个）。请按提示多测几次。";
                return;
            }

            if (_candidateCount == 0)
            {
                _result.Text = "样本 " + _completedPresses + " 个完整按压，未观察到短促按压后的快速重按。";
                _suggestedThreshold = 0;
                _apply.Enabled = false;
                return;
            }

            int suggested = Math.Max(10, (int)_maxCandidate + 1);
            _suggestedThreshold = suggested;
            _result.Text = "样本 " + _completedPresses + " 个完整按压，候选 " + _candidateCount +
                " 个（释放间隔 " + _minCandidate + "–" + _maxCandidate + " ms）。\n" +
                "建议阈值：" + suggested + " ms（预测，需实机确认；应用后可在按键健康页随时关闭）。";
            _apply.Enabled = true;
        }

        private void ApplySuggestion()
        {
            if (_suggestedThreshold <= 0 || _shell.KeyPolicies == null) return;
            _shell.KeyPolicies.BeginEdit();
            KeyPolicyEntry entry = _shell.KeyPolicies.GetOrCreate(
                _capturedKey.VirtualKey, _capturedKey.ScanCode, _capturedKey.Extended);
            entry.ThresholdMs = _suggestedThreshold;
            entry.Enabled = true;
            entry.EnabledAtUtcTicks = DateTime.UtcNow.Ticks;

            // 应用单键策略需要「单键保守」模式才生效
            if (_shell.Cfg.PolicyMode != (int)PolicyMode.PerKeyConservative)
            {
                _shell.Cfg.PolicyMode = (int)PolicyMode.PerKeyConservative;
                _shell.Cfg.Save();
            }
            _shell.RefreshPolicy();
            string status = _shell.Cfg.FilterEnabled ? "已启用 " : "已保存 ";
            string detail = _shell.Cfg.FilterEnabled ? "可在按键健康页随时调整。" : "打开防抖总开关后生效。";
            MessageBox.Show(this, status + KeyLayout.LabelFor(_vk) + " 的单键保守过滤（阈值 " +
                _suggestedThreshold + " ms）。" + detail,
                "KeyInsight", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _apply.Enabled = false;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            StopCapture();
            base.OnFormClosing(e);
        }
    }
}
