using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
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
    public sealed class DiagnosisWizardForm : Form
    {
        private readonly IShellActions _shell;
        private readonly int _vk;
        private readonly Label _progress;
        private readonly Label _result;
        private readonly TextBox _box;
        private readonly Button _apply;

        private readonly List<double> _downTimes = new List<double>();
        private readonly List<double> _cycles = new List<double>();
        private double _lastDeliveredDown = -1;
        private double _lastDeliveredUp = -1;
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
            KeyPreview = true;
            TopMost = true;

            Label title = new Label();
            title.Text = "1) 在下方文本框内操作目标按键：" + KeyLayout.LabelFor(vk) + "（其它按键不计入）";
            title.SetBounds(16, 12, 430, 20);
            Controls.Add(title);

            Label hint = new Label();
            hint.Text = "建议：单击 20 次 → 快速双击 10 组 → 长按 5 轮。采集期间防抖过滤临时暂停（关闭后自动恢复）。";
            hint.SetBounds(16, 34, 430, 32);
            hint.ForeColor = Theme.P.Muted;
            Controls.Add(hint);

            _box = new TextBox();
            _box.Multiline = true;
            _box.ScrollBars = ScrollBars.Vertical;
            _box.SetBounds(16, 70, 428, 110);
            _box.BackColor = Theme.P.CardAlt;
            _box.ForeColor = Theme.P.Text;
            _box.Text = "";
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

            SuspendFiltering();
            _box.KeyDown += OnCaptureKeyDown;
            _box.KeyUp += OnCaptureKeyUp;
            FormClosed += delegate { RestorePolicy(); };
            Deactivate += delegate
            {
                // 失焦：停止采集（会话取消），策略仍恢复在 FormClosed
                _capturing = false;
                UpdateProgress();
            };
        }

        private void SuspendFiltering()
        {
            PolicySnapshot cur = PolicyController.Current;
            Dictionary<KeyInsight.Core.Input.PhysicalKeyId, PerKeyRule> rules =
                new Dictionary<KeyInsight.Core.Input.PhysicalKeyId, PerKeyRule>();
            foreach (KeyValuePair<KeyInsight.Core.Input.PhysicalKeyId, PerKeyRule> kv in cur.SnapshotRules())
            {
                rules[kv.Key] = kv.Value;
            }
            PolicySnapshot suspended = new PolicySnapshot(cur.Version, cur.Mode, false, cur.SkipInjected,
                cur.GlobalThresholdMs, rules, cur.FilterAppWhitelist);
            PolicyController.Update(suspended);
        }

        private void RestorePolicy()
        {
            _capturing = false;
            PolicyController.Refresh(_shell.Cfg);
        }

        private void StartCapture()
        {
            _downTimes.Clear();
            _cycles.Clear();
            _lastDeliveredDown = -1;
            _lastDeliveredUp = -1;
            _isDown = false;
            _capturing = true;
            _suggestedThreshold = 0;
            _apply.Enabled = false;
            _result.Text = "";
            _box.Text = "";
            _box.Focus();
            UpdateProgress();
        }

        private void OnCaptureKeyDown(object sender, KeyEventArgs e)
        {
            if (!_capturing) return;
            if ((int)e.KeyValue != _vk) return;
            e.SuppressKeyPress = true;
            e.Handled = true;
            double now = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

            if (_isDown)
            {
                return; // 自动重复：仅记录首次 Down
            }
            _isDown = true;
            _downTimes.Add(now);

            if (_lastDeliveredUp >= 0)
            {
                double cycleGap = now - _lastDeliveredUp;
                if (cycleGap < 500) _cycles.Add(cycleGap);
            }
            _lastDeliveredDown = now;
            UpdateProgress();
        }

        private void OnCaptureKeyUp(object sender, KeyEventArgs e)
        {
            if (!_capturing) return;
            if ((int)e.KeyValue != _vk) return;
            e.SuppressKeyPress = true;
            e.Handled = true;
            double now = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
            _isDown = false;
            _lastDeliveredUp = now;
            UpdateProgress();
        }

        private void UpdateProgress()
        {
            _progress.Text = "已采集按下：" + _downTimes.Count + " 次 · 按压周期：" + _cycles.Count +
                (_capturing ? " · 采集中…" : "");
        }

        private void Analyze()
        {
            _capturing = false;
            UpdateProgress();

            if (_cycles.Count < 8)
            {
                _result.Text = "样本不足：" + _cycles.Count + " 个按压周期（至少需要 8 个）。请按提示多测几次。";
                return;
            }

            long candidates = 0;
            double maxCandidate = 0;
            for (int i = 0; i < _cycles.Count; i++)
            {
                if (_cycles[i] < 30)
                {
                    candidates++;
                    if (_cycles[i] > maxCandidate) maxCandidate = _cycles[i];
                }
            }

            if (candidates == 0)
            {
                _result.Text = "样本 " + _cycles.Count + " 个按压周期，未发现短间隔候选。\n" +
                    "该键无需启用单键过滤，建议仅观察。";
                _suggestedThreshold = 0;
                _apply.Enabled = false;
                return;
            }

            int suggested = (int)Math.Ceiling(maxCandidate) + 5;
            if (suggested < 10) suggested = 10;
            if (suggested > 30) suggested = 30;
            _suggestedThreshold = suggested;
            _result.Text = "样本 " + _cycles.Count + " 个按压周期，短间隔候选 " + candidates +
                " 个（最小 " + MinCycle().ToString("F1") + " ms，最大候选 " + maxCandidate.ToString("F1") + " ms）。\n" +
                "建议阈值：" + suggested + " ms（预测，需实机确认；应用后可在按键健康页随时关闭）。";
            _apply.Enabled = true;
        }

        private double MinCycle()
        {
            double min = double.MaxValue;
            for (int i = 0; i < _cycles.Count; i++) if (_cycles[i] < min) min = _cycles[i];
            return min == double.MaxValue ? 0 : min;
        }

        private void ApplySuggestion()
        {
            if (_suggestedThreshold <= 0 || _shell.KeyPolicies == null) return;
            _shell.KeyPolicies.BeginEdit();
            int scan = InputIdentityResolver.CanonicalScanFor(_vk);
            KeyPolicyEntry entry = _shell.KeyPolicies.GetOrCreate(_vk, scan, false);
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
            MessageBox.Show(this, "已启用 " + KeyLayout.LabelFor(_vk) + " 的单键保守过滤（阈值 " +
                _suggestedThreshold + " ms，模式：单键保守）。可在按键健康页随时关闭。",
                "KeyInsight", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _apply.Enabled = false;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _capturing = false;
            base.OnFormClosing(e);
        }
    }
}
