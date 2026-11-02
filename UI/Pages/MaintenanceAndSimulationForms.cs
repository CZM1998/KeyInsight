using System;
using System.Drawing;
using System.Windows.Forms;
using KeyInsight.Stats;

namespace KeyInsight.UI.Pages
{
    /// <summary>维护事件记录对话框（P2-02）：动作 + 可选备注，仅存元数据。</summary>
    public sealed class MaintenanceForm : Form
    {
        private readonly ComboBox _action;
        private readonly TextBox _note;

        public string ActionKey { get; private set; }
        public string NoteText { get; private set; }

        public MaintenanceForm(int vk)
        {
            Text = "记录维护 · " + KeyLayout.LabelFor(vk);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(360, 170);
            BackColor = Theme.P.Card;
            ForeColor = Theme.P.Text;
            Font = Ui.Body;

            Label label = new Label();
            label.Text = "动作（仅记录事件时间与类型，不上传任何数据）";
            label.AutoSize = false;
            label.SetBounds(16, 12, 330, 18);
            label.ForeColor = Theme.P.Muted;
            Controls.Add(label);

            _action = new ComboBox();
            _action.DropDownStyle = ComboBoxStyle.DropDownList;
            _action.Items.AddRange(new object[] { "清洁触点", "更换轴体", "更换键盘", "复测", "备注" });
            _action.SelectedIndex = 0;
            _action.SetBounds(16, 36, 328, 24);
            Controls.Add(_action);

            _note = new TextBox();
            _note.SetBounds(16, 70, 328, 24);
            _note.Text = "";
            Controls.Add(_note);

            Label hint = new Label();
            hint.Text = "备注（可选）";
            hint.SetBounds(16, 98, 200, 18);
            hint.ForeColor = Theme.P.Muted;
            Controls.Add(hint);

            Button ok = new Button();
            ok.Text = "记录";
            ok.SetBounds(168, 128, 80, 28);
            ok.DialogResult = DialogResult.OK;
            ok.Click += delegate { CaptureInput(); };
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.SetBounds(256, 128, 88, 28);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        private void CaptureInput()
        {
            string[] keys = new string[] { "clean", "switch", "replace-device", "retest", "note" };
            ActionKey = keys[Math.Max(0, Math.Min(keys.Length - 1, _action.SelectedIndex))];
            NoteText = _note.Text.Trim();
        }
    }

    /// <summary>策略模拟对话框（P2-01）：直方图预测表 + 推荐阈值；只有点“应用”才写配置。</summary>
    public sealed class SimulationForm : Form
    {
        public int AppliedThreshold { get; private set; }

        public SimulationForm(StatsSnapshot snap, int vk)
        {
            Text = "策略模拟 · " + KeyLayout.LabelFor(vk);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(430, 330);
            BackColor = Theme.P.Card;
            ForeColor = Theme.P.Text;
            Font = Ui.Body;

            SimulationResult result = PolicySimulator.Simulate(vk, snap);

            Label title = new Label();
            title.Text = result.HasData
                ? "最近 30 天拦截样本 " + (result.Rows.Count > 0 ? result.Rows[0].TotalCandidateEvents : 0) +
                  " 条；不同阈值的预测如下"
                : "无法模拟";
            title.SetBounds(16, 12, 400, 20);
            title.ForeColor = Theme.P.Text;
            Controls.Add(title);

            ListView list = new ListView();
            list.View = View.Details;
            list.FullRowSelect = true;
            list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            list.SetBounds(16, 40, 398, 190);
            list.Columns.Add("阈值", 80);
            list.Columns.Add("预计过滤", 100);
            list.Columns.Add("占候选", 80);
            list.Columns.Add("风险提示", 130);
            foreach (SimulationRow row in result.Rows)
            {
                ListViewItem item = new ListViewItem(row.ThresholdMs + " ms");
                item.SubItems.Add(row.EstimatedFiltered.ToString("N0") + " 次");
                item.SubItems.Add(row.SharePercent.ToString("F0") + "%");
                item.SubItems.Add(row.RiskNote);
                list.Items.Add(item);
            }
            Controls.Add(list);

            Label note = new Label();
            note.Text = result.Note;
            note.SetBounds(16, 236, 398, 34);
            note.ForeColor = Theme.P.Muted;
            Controls.Add(note);

            Button apply = new Button();
            apply.Text = "应用推荐 " + (result.RecommendedThresholdMs > 0 ? result.RecommendedThresholdMs + " ms" : "—");
            apply.SetBounds(130, 282, 140, 30);
            apply.Enabled = result.HasData && result.RecommendedThresholdMs > 0;
            apply.Click += delegate
            {
                AppliedThreshold = result.RecommendedThresholdMs;
                DialogResult = DialogResult.OK;
            };
            Controls.Add(apply);

            Button close = new Button();
            close.Text = "关闭";
            close.SetBounds(280, 282, 134, 30);
            close.DialogResult = DialogResult.Cancel;
            Controls.Add(close);

            AcceptButton = apply;
            CancelButton = close;
        }
    }
}
