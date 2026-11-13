using System;
using System.Drawing;
using System.Windows.Forms;

namespace WindowDock
{
    public sealed class TaskDeadlineForm : Form
    {
        private CheckBox _enabled;
        private DateTimePicker _date;
        public DateTime? Deadline;

        public TaskDeadlineForm(TaskItem task)
        {
            Text = "任务截止时间";
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(420, 210);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = new Font("Microsoft YaHei UI", 9f);

            Label title = new Label();
            title.Text = task.Text;
            title.AutoEllipsis = true;
            title.SetBounds(18, 16, 384, 28);
            Controls.Add(title);
            _enabled = new CheckBox();
            _enabled.Text = "为此任务设置截止时间";
            _enabled.Checked = task.Deadline.HasValue;
            _enabled.SetBounds(18, 56, 384, 26);
            Controls.Add(_enabled);
            _date = new DateTimePicker();
            _date.Format = DateTimePickerFormat.Custom;
            _date.CustomFormat = "yyyy-MM-dd HH:mm";
            DateTime value = task.Deadline.HasValue ? task.Deadline.Value : DateTime.Now.AddDays(1);
            _date.Value = value < _date.MinDate ? _date.MinDate : (value > _date.MaxDate ? _date.MaxDate : value);
            _date.Enabled = _enabled.Checked;
            _date.SetBounds(18, 94, 384, 28);
            Controls.Add(_date);
            _enabled.CheckedChanged += delegate { _date.Enabled = _enabled.Checked; };

            Button ok = new Button();
            ok.Text = "确定";
            ok.SetBounds(222, 156, 84, 32);
            ok.Click += delegate
            {
                Deadline = _enabled.Checked ? (DateTime?)_date.Value : null;
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(ok);
            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.SetBounds(318, 156, 84, 32);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
            DpiScaleUtil.ScaleDialog(this);
        }
    }
}
