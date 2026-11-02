using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using KeyInsight.Stats;

namespace KeyInsight.UI
{
    public sealed class MonthManagerForm : Form
    {
        private readonly IStatsSource _source;
        private readonly Action _changed;
        private readonly ListView _list;
        private readonly Button _delete;
        private readonly Button _close;

        public MonthManagerForm(IStatsSource source, Action changed)
        {
            _source = source;
            _changed = changed;

            Text = "管理月份数据";
            ClientSize = new Size(600, 540);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Theme.P.Bg;
            ForeColor = Theme.P.Text;

            Label title = new Label();
            title.Text = "全部月份";
            title.Font = new Font(Ui.Section.FontFamily, 12F, FontStyle.Bold);
            title.ForeColor = Theme.P.Text;
            title.AutoSize = true;
            title.Location = new Point(18, 14);
            Controls.Add(title);

            Label sub = new Label();
            sub.Text = "勾选要删除的月份，删除后不可恢复。";
            sub.ForeColor = Theme.P.Muted;
            sub.AutoSize = true;
            sub.Location = new Point(20, 42);
            Controls.Add(sub);

            _list = new ListView();
            _list.View = View.Details;
            _list.CheckBoxes = true;
            _list.FullRowSelect = true;
            _list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            _list.Bounds = new Rectangle(18, 70, 564, 400);
            _list.BackColor = Theme.P.Card;
            _list.ForeColor = Theme.P.Text;
            _list.BorderStyle = BorderStyle.FixedSingle;
            _list.Columns.Add("月份", 130, HorizontalAlignment.Left);
            _list.Columns.Add("文件大小", 110, HorizontalAlignment.Right);
            _list.Columns.Add("按键数", 140, HorizontalAlignment.Right);
            _list.Columns.Add("拦截次数", 120, HorizontalAlignment.Right);
            Controls.Add(_list);

            _delete = new Button();
            _delete.Text = "删除选中月份";
            _delete.Bounds = new Rectangle(18, 490, 140, 32);
            _delete.Click += delegate { DeleteSelected(); };
            Controls.Add(_delete);

            _close = new Button();
            _close.Text = "关闭";
            _close.Bounds = new Rectangle(500, 490, 82, 32);
            _close.Click += delegate { Close(); };
            Controls.Add(_close);

            LoadMonths();
        }

        private void LoadMonths()
        {
            _list.Items.Clear();
            List<MonthInfo> months = _source.GetMonths();
            for (int i = 0; i < months.Count; i++)
            {
                MonthInfo m = months[i];
                ListViewItem item = new ListViewItem(m.Key);
                item.SubItems.Add(FormatSize(m.Size));
                item.SubItems.Add(m.Loaded ? Charts.Number(m.Total) : "—");
                item.SubItems.Add(m.Loaded ? Charts.Number(m.Swallowed) : "—");
                item.Tag = m.Key;
                _list.Items.Add(item);
            }
            if (_list.Items.Count > 0) _list.Items[0].Selected = true;
        }

        private void DeleteSelected()
        {
            List<string> keys = new List<string>();
            for (int i = 0; i < _list.Items.Count; i++)
            {
                if (_list.Items[i].Checked) keys.Add((string)_list.Items[i].Tag);
            }
            if (keys.Count == 0)
            {
                Dialogs.Show(this, "提示", "请先勾选要删除的月份。");
                return;
            }

            if (!Dialogs.Confirm(this, "删除月份",
                "确定删除选中的 " + keys.Count + " 个月份？此操作不可撤销。")) return;

            for (int i = 0; i < keys.Count; i++) _source.DeleteMonth(keys[i]);
            if (_changed != null) _changed();
            LoadMonths();
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F0") + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("F1") + " MB";
        }
    }
}
