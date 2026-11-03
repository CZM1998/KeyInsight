using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using KeyInsight.Adapters.Display;
using KeyInsight.Core.Devices;

namespace KeyInsight.UI
{
    /// <summary>
    /// 统一设备详情窗（方案 §3.1 六段式信息结构的本轮落地：概览/规格/能力/诊断/历史 +
    /// 用户别名与身份确认）。未支持能力不显示为可点击；未知能力显示「未知/未读取」。
    /// </summary>
    public sealed class DeviceDetailForm : Form
    {
        private readonly IShellActions _shell;
        private readonly DeviceRecord _record;
        private readonly TextBox _alias;
        private readonly Button _confirm;
        private readonly Button _archive;
        private readonly FlowLayoutPanel _capPanel;
        private readonly TextBox _note;

        public DeviceDetailForm(IShellActions shell, DeviceRecord record)
        {
            _shell = shell;
            _record = record;

            Text = "设备详情 · " + (string.IsNullOrEmpty(record.UserAlias) ? record.Name : record.UserAlias);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(620, 560);
            BackColor = Theme.P.Card;
            ForeColor = Theme.P.Text;
            Font = Ui.Body;
            KeyPreview = true;

            int x = 20, w = 580, y = 14;

            Label title = new Label();
            title.Text = Section("概览");
            title.SetBounds(x, y, w, 22);
            Controls.Add(title);
            y += 28;
            _alias = new TextBox();
            _alias.SetBounds(x, y, 320, 26);
            _alias.Text = record.UserAlias ?? "";
            _alias.BackColor = Theme.P.CardAlt;
            _alias.ForeColor = Theme.P.Text;
            _alias.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(_alias);
            Button saveAlias = MakeButton("保存别名", x + 330, y, 90);
            saveAlias.Click += delegate { SaveAlias(); };
            Controls.Add(saveAlias);
            y += 36;

            Label status = new Label();
            status.Text = DeviceCategoryNames.Get(record.Category) + " · " + ConnText() +
                " · 健康：" + HealthStateNames.Get(record.Health) +
                " · 诊断：" + DiagText(record.Diagnostic) +
                "\n首次记录 " + record.FirstSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") +
                " · 最近出现 " + record.LastSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            status.ForeColor = Theme.P.Muted;
            status.SetBounds(x, y, w, 44);
            Controls.Add(status);
            y += 52;

            title = new Label();
            title.Text = Section("规格");
            title.SetBounds(x, y, w, 22);
            Controls.Add(title);
            y += 26;
            Label spec = new Label();
            spec.Text = "型号：" + (string.IsNullOrEmpty(record.Model) ? "未知" : record.Model) +
                "\n制造商：" + (string.IsNullOrEmpty(record.Manufacturer) ? "未知" : record.Manufacturer) +
                "\n序列号：" + (string.IsNullOrEmpty(record.SerialNumber) ? "未读取" : record.SerialNumber) +
                "\n身份键：" + record.IdentityKey +
                "\n身份置信度：" + (record.IdentityConfirmedByUser
                    ? "用户确认"
                    : record.LastIdentityConfidence.ToString("F2") +
                      "（仅表示证据充分度，非硬件损坏概率）");
            spec.ForeColor = Theme.P.Text;
            spec.SetBounds(x, y, w, 96);
            Controls.Add(spec);
            y += 104;
            _confirm = MakeButton("这是我常用的设备（确认身份）", x, y, 240);
            _confirm.Click += delegate { ConfirmIdentity(); };
            Controls.Add(_confirm);
            if (!record.IdentityConfirmedByUser)
            {
                Label confirmHint = new Label();
                confirmHint.Text = "低置信度匹配不会被自动合并；确认后同型号设备不再混淆。";
                confirmHint.ForeColor = Theme.P.Dim;
                confirmHint.SetBounds(x + 250, y + 6, 330, 20);
                Controls.Add(confirmHint);
            }
            y += 40;

            title = new Label();
            title.Text = Section("能力");
            title.SetBounds(x, y, w, 22);
            Controls.Add(title);
            y += 26;
            _capPanel = new FlowLayoutPanel();
            _capPanel.SetBounds(x, y, w, 54);
            _capPanel.BackColor = Theme.P.Card;
            _capPanel.AutoScroll = false;
            BuildCapabilities();
            Controls.Add(_capPanel);
            y += 62;

            title = new Label();
            title.Text = Section("诊断");
            title.SetBounds(x, y, w, 22);
            Controls.Add(title);
            y += 26;
            _note = new TextBox();
            _note.SetBounds(x, y, w - 110, 24);
            _note.Text = record.UserNote ?? "";
            _note.BackColor = Theme.P.CardAlt;
            _note.ForeColor = Theme.P.Text;
            _note.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(_note);
            Button saveNote = MakeButton("保存备注", x + w - 100, y - 2, 100);
            saveNote.Click += delegate { SaveNote(); };
            Controls.Add(saveNote);
            Label noteHint = new Label();
            noteHint.Text = "诊断 / 维护备注（仅本地保存）";
            noteHint.ForeColor = Theme.P.Dim;
            noteHint.SetBounds(x, y + 26, w - 110, 18);
            Controls.Add(noteHint);
            y += 50;

            title = new Label();
            title.Text = Section("历史 Finding");
            title.SetBounds(x, y, w, 22);
            Controls.Add(title);
            y += 26;
            Label findings = new Label();
            findings.Text = FindingsText();
            findings.ForeColor = Theme.P.Text;
            findings.SetBounds(x, y, w, 56);
            Controls.Add(findings);
            y += 64;

            if (_record.Category == DeviceCategory.Display)
            {
                Button test = MakeButton("启动显示器体检", x, y, 150);
                test.Click += delegate { LaunchMonitorTest(); };
                Controls.Add(test);
                y += 40;
            }

            _archive = MakeButton("归档此设备", x, y, 110);
            _archive.Click += delegate { ArchiveDevice(); };
            Controls.Add(_archive);

            Button close = MakeButton("关闭", ClientSize.Width - 100, ClientSize.Height - 46, 80);
            close.Click += delegate { Close(); };
            Controls.Add(close);
        }

        private static string Section(string t)
        {
            return "■ " + t;
        }

        private Button MakeButton(string text, int x, int y, int width)
        {
            Button b = new Button();
            b.Text = text;
            b.SetBounds(x, y, width, 28);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Theme.P.Border;
            b.BackColor = Theme.P.CardAlt;
            b.ForeColor = Theme.P.Text;
            return b;
        }

        private string ConnText()
        {
            switch (_record.Connection)
            {
                case ConnectionState.Connected: return "已连接";
                case ConnectionState.Disconnected: return "已断开";
                default: return "连接未知";
            }
        }

        private static string DiagText(DiagnosticRunState s)
        {
            switch (s)
            {
                case DiagnosticRunState.Completed: return "已完成";
                case DiagnosticRunState.Failed: return "已失败";
                case DiagnosticRunState.Running: return "进行中";
                case DiagnosticRunState.Queued: return "排队中";
                default: return "尚未检测";
            }
        }

        private void BuildCapabilities()
        {
            _capPanel.Controls.Clear();
            // 能力随 Adapter 动态注册（方案 §4.3）；当前版本显示器能力最完整。
            if (_record.Category == DeviceCategory.Display)
            {
                AddCap("显示模式读取", "Read", "WindowsApi");
                AddCap("EDID 档案", "Read", "WindowsApi");
                AddCap("显示器体检（六类图案）", "Test", "UserConfirmed");
                AddCap("HDR 状态观察", "Observe", "WindowsApi");
                AddCap("亮度控制", "Control", "DDC_CI（可用性未知）");
            }
            else
            {
                Label none = new Label();
                none.Text = "此类设备的可编程能力尚未接入（未知/未读取）";
                none.ForeColor = Theme.P.Dim;
                none.AutoSize = true;
                _capPanel.Controls.Add(none);
            }
        }

        private void AddCap(string name, string op, string source)
        {
            Label l = new Label();
            l.Text = name + "  ·  " + op + "  ·  来源 " + source;
            l.ForeColor = Theme.P.Text;
            l.AutoSize = true;
            l.Margin = new Padding(0, 4, 16, 4);
            _capPanel.Controls.Add(l);
        }

        private string FindingsText()
        {
            if (_shell.DeviceCenter == null) return "暂无";
            List<DiagnosticFinding> findings = _shell.DeviceCenter.Diagnostics.FindingsForDevice(_record.DeviceId);
            if (findings.Count == 0) return "暂无诊断记录 —— 未测试不代表设备故障。";
            StringBuilder sb = new System.Text.StringBuilder();
            int shown = 0;
            for (int i = findings.Count - 1; i >= 0 && shown < 3; i--, shown++)
            {
                DiagnosticFinding f = findings[i];
                sb.Append(f.CreatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm"))
                  .Append("  ")
                  .Append(f.DiagnosticCode)
                  .Append("  ")
                  .Append(SeverityText(f.Severity))
                  .Append(" / ")
                  .Append(StatusText(f.Status))
                  .Append("\n");
            }
            return sb.ToString().TrimEnd();
        }

        private static string SeverityText(FindingSeverity s)
        {
            switch (s)
            {
                case FindingSeverity.Warning: return "警告";
                case FindingSeverity.Attention: return "关注";
                default: return "提示";
            }
        }

        private static string StatusText(FindingStatus s)
        {
            switch (s)
            {
                case FindingStatus.UserConfirmed: return "用户确认";
                case FindingStatus.Resolved: return "已解决";
                case FindingStatus.Dismissed: return "已忽略";
                default: return "候选";
            }
        }

        private void SaveNote()
        {
            if (_shell.DeviceCenter == null) return;
            _shell.DeviceCenter.SetDeviceNote(_record.DeviceId, _note.Text.Trim());
            _note.BackColor = Theme.P.Card;
        }

        private void SaveAlias()
        {
            if (_shell.DeviceCenter == null) return;
            _shell.DeviceCenter.SetUserAlias(_record.DeviceId, _alias.Text.Trim());
            Close();
        }

        private void ConfirmIdentity()
        {
            if (_shell.DeviceCenter == null) return;
            _shell.DeviceCenter.ConfirmIdentity(_record.DeviceId);
            _confirm.Enabled = false;
            _confirm.Text = "已确认";
        }

        private void ArchiveDevice()
        {
            if (_shell.DeviceCenter == null) return;
            if (Dialogs.Confirm(this, "归档设备",
                "归档后该设备从设备中心列表隐藏（历史记录保留）。\n\n确定归档「" +
                (string.IsNullOrEmpty(_record.UserAlias) ? _record.Name : _record.UserAlias) + "」？"))
            {
                _shell.DeviceCenter.Archive(_record.DeviceId);
                Close();
            }
        }

        private void LaunchMonitorTest()
        {
            DisplayAdapter.MonitorInfo monitor = ResolveMonitor();
            if (monitor == null)
            {
                Dialogs.Error(this, "无法定位显示器", "该显示器当前不在线，无法启动体检。");
                return;
            }
            using (MonitorTestForm f = new MonitorTestForm(_shell, _record, monitor))
            {
                f.RunTest();
            }
        }

        private DisplayAdapter.MonitorInfo ResolveMonitor()
        {
            if (_shell.DeviceCenter == null) return null;
            List<DisplayAdapter.MonitorInfo> monitors =
                _shell.DeviceCenter.Display.EnumerateMonitors();
            for (int i = 0; i < monitors.Count; i++)
            {
                string key = DisplayAdapter.MonitorInstanceKey(monitors[i], i);
                if (_record.InstanceKeys.Contains(key))
                {
                    return monitors[i];
                }
            }
            // 兜底：按 EDID 三元组匹配
            for (int i = 0; i < monitors.Count; i++)
            {
                DisplayAdapter.MonitorInfo m = monitors[i];
                if (!string.IsNullOrEmpty(_record.IdentityKey) &&
                    _record.IdentityKey.Contains("display:") &&
                    m.EdidVendor != null && _record.IdentityKey.Contains(m.EdidVendor + "." + m.EdidProduct))
                {
                    return m;
                }
            }
            return monitors.Count > 0 ? monitors[0] : null;
        }

        private string MatchInstanceId(DisplayAdapter.MonitorInfo m)
        {
            return m.MonitorDeviceId;
        }
    }
}
