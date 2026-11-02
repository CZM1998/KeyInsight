using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using KeyInsight.Core.Input;
using KeyInsight.Core.Policy;
using KeyInsight.Stats;

namespace KeyInsight.UI.Pages
{
    /// <summary>
    /// 按键详情窗（P1-03）：从键盘热力图点选进入，展示该键的
    /// 概览 / 健康（旧口径标注）/ 策略 / 维护入口。数据来自 KeyProfileProjector 投影。
    /// </summary>
    public sealed class KeyDetailForm : Form
    {
        private readonly IShellActions _shell;
        private readonly int _vk;
        private readonly Label _info;
        private readonly Label _health;
        private readonly Label _policy;
        private readonly Label _maintain;

        public KeyDetailForm(IShellActions shell, int vk)
        {
            _shell = shell;
            _vk = vk;

            Text = "按键详情 · " + KeyLayout.LabelFor(vk);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(400, 330);
            BackColor = Theme.P.Card;
            ForeColor = Theme.P.Text;
            Font = Ui.Body;

            _info = AddSection("概览", 12, 96);
            _health = AddSection("健康（旧口径 · 基于拦截）", 116, 52);
            _policy = AddSection("单键策略", 176, 44);
            _maintain = AddSection("维护记录", 228, 40);

            Button wizard = new Button();
            wizard.Text = "诊断测试";
            wizard.SetBounds(16, 282, 108, 32);
            wizard.Click += delegate
            {
                using (DiagnosisWizardForm f = new DiagnosisWizardForm(_shell, _vk)) f.ShowDialog(FindForm());
                RefreshInfo();
            };
            Controls.Add(wizard);

            Button close = new Button();
            close.Text = "关闭";
            close.SetBounds(272, 282, 112, 32);
            close.DialogResult = DialogResult.Cancel;
            Controls.Add(close);

            CancelButton = close;
            RefreshInfo();
        }

        private Label AddSection(string title, int y, int height)
        {
            Label t = new Label();
            t.Text = title;
            t.SetBounds(16, y, 368, 18);
            t.ForeColor = Theme.P.Muted;
            Controls.Add(t);

            Label body = new Label();
            body.SetBounds(16, y + 18, 368, height - 18);
            Controls.Add(body);
            return body;
        }

        private void RefreshInfo()
        {
            StatsSnapshot snap = _shell.Source.GetSnapshot(DateTime.Today.AddDays(-29), DateTime.Today);
            KeyProfileView v = KeyProfileProjector.Project(_vk, snap, _shell.KeyPolicies, _shell.PolicyStatus.Mode);

            _info.Text = "按下 " + v.ObservedDown.ToString("N0") + " 次 · 拦截 " + v.SuppressedDown.ToString("N0") +
                " 次 · 抖动率 " + (v.Rate * 100).ToString("F2") + "%\n最近 30 天拦截样本：" +
                (v.HasGapHistogram ? v.CandidateEvents30d.ToString("N0") + " 条" : "无") +
                (v.InsufficientData ? " · 样本不足，仅供观察" : "");

            _health.Text = v.LegacyScore > 0
                ? "健康分 " + v.LegacyScore + "（警告线 ≥60 / 严重 <60）· 本周拦截 " + v.Swallowed7 +
                  " 次 / 上周 " + v.SwallowedPrev7 + " 次"
                : "该键在统计范围内没有拦截记录";
            if (v.InsufficientData) _health.Text += "\n样本量低，旧健康分波动大，请以专用测试为准";

            KeyPolicyEntry entry = null;
            if (_shell.KeyPolicies != null)
            {
                int scan = InputIdentityResolver.CanonicalScanFor(_vk);
                entry = _shell.KeyPolicies.Find(_vk, scan, false);
                if (entry == null) entry = _shell.KeyPolicies.FindByVirtualKey(_vk);
            }
            _policy.Text = (entry != null && entry.Enabled)
                ? "已启用单键保守过滤 · 阈值 " + entry.ThresholdMs + " ms（模式：" + ModeText(_shell.PolicyStatus.Mode) + "）"
                : "未启用单键策略（在按键健康页可开启并调阈值）";

            List<MaintenanceEvent> events = MaintenanceStore.Load(_shell.MaintenanceDir);
            int count = 0;
            MaintenanceEvent latest = null;
            for (int i = events.Count - 1; i >= 0; i--)
            {
                if (events[i].Vk != _vk) continue;
                count++;
                if (latest == null) latest = events[i];
            }
            if (latest == null)
            {
                _maintain.Text = "暂无维护记录";
            }
            else
            {
                MaintenanceStore.BeforeAfter cmp = MaintenanceStore.Compare(_vk, latest, snap);
                _maintain.Text = count + " 条记录 · 最近：" + latest.LocalTime.ToString("MM-dd HH:mm") + " " +
                    latest.ActionText + "\n" + cmp.Verdict;
            }
        }

        private static string ModeText(PolicyMode mode)
        {
            switch (mode)
            {
                case PolicyMode.ObserveOnly: return "观察";
                case PolicyMode.PerKeyConservative: return "单键保守";
                default: return "兼容 v2.1";
            }
        }
    }
}
