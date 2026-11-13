using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace WindowDock
{
    public class PickerForm : Form
    {
        private ListView _list;
        private Button _refresh;
        private Button _pick;
        private Button _ok;
        private Button _cancel;
        private Timer _pickTimer;
        private bool _picking;
        private bool _sawDown;
        private int _armAt;
        private int _timeoutAt;

        public IntPtr SelectedHandle = IntPtr.Zero;

        public PickerForm()
        {
            Text = "选择要停靠的窗口";
            AutoScaleMode = AutoScaleMode.None;
            AutoScaleDimensions = new SizeF(96F, 96F);
            ClientSize = new Size(660, 440);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = new Font("Microsoft YaHei UI", 9f);

            _list = new ListView();
            _list.Location = new Point(12, 12);
            _list.Size = new Size(636, 366);
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.MultiSelect = false;
            _list.HideSelection = false;
            _list.GridLines = true;
            _list.Columns.Add("窗口标题", 350);
            _list.Columns.Add("进程", 170);
            _list.Columns.Add("句柄", 100);
            _list.DoubleClick += delegate { AcceptSelection(); };
            Controls.Add(_list);

            _refresh = new Button();
            _refresh.Text = "刷新";
            _refresh.Location = new Point(12, 390);
            _refresh.Size = new Size(90, 32);
            _refresh.Click += delegate { LoadWindows(); };
            Controls.Add(_refresh);

            _pick = new Button();
            _pick.Text = "点击选择窗口...";
            _pick.Location = new Point(110, 390);
            _pick.Size = new Size(150, 32);
            _pick.Click += delegate { StartClickPick(); };
            Controls.Add(_pick);

            _ok = new Button();
            _ok.Text = "绑定";
            _ok.Location = new Point(470, 390);
            _ok.Size = new Size(88, 32);
            _ok.Click += delegate { AcceptSelection(); };
            Controls.Add(_ok);

            _cancel = new Button();
            _cancel.Text = "取消";
            _cancel.Location = new Point(564, 390);
            _cancel.Size = new Size(84, 32);
            _cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(_cancel);

            AcceptButton = _ok;
            CancelButton = _cancel;

            DpiScaleUtil.ScaleDialog(this);

            _pickTimer = new Timer();
            _pickTimer.Interval = 25;
            _pickTimer.Tick += PickTimerTick;

            LoadWindows();
        }

        private void LoadWindows()
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            List<WindowInfo> wins = Native.GetTopLevelWindows(Process.GetCurrentProcess().Id);
            foreach (WindowInfo w in wins)
            {
                ListViewItem item = new ListViewItem(w.Title);
                item.SubItems.Add(w.ProcessName);
                item.SubItems.Add("0x" + w.Handle.ToInt64().ToString("X"));
                item.Tag = w;
                _list.Items.Add(item);
            }
            _list.EndUpdate();
            if (_list.Items.Count > 0)
                _list.Items[0].Selected = true;
        }

        private void AcceptSelection()
        {
            if (_picking) return;
            if (_list.SelectedItems.Count == 0)
            {
                MessageBox.Show(this, "请先选择一个窗口。", "WindowDock",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            WindowInfo w = _list.SelectedItems[0].Tag as WindowInfo;
            if (w == null || !Native.IsWindow(w.Handle))
            {
                MessageBox.Show(this, "该窗口已失效，请刷新列表后重试。", "WindowDock",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                LoadWindows();
                return;
            }
            SelectedHandle = w.Handle;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void StartClickPick()
        {
            _picking = true;
            _sawDown = false;
            _armAt = Environment.TickCount + 300;
            _timeoutAt = Environment.TickCount + 20000;
            Hide();
            _pickTimer.Start();
        }

        private void PickTimerTick(object sender, EventArgs e)
        {
            if (!_picking) return;

            if (Environment.TickCount >= _timeoutAt)
            {
                StopClickPick();
                Show();
                MessageBox.Show(this, "未选择窗口（超时）。", "WindowDock",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (Environment.TickCount < _armAt) return;

            bool down = (Native.GetAsyncKeyState(Native.VK_LBUTTON) & 0x8000) != 0;

            if (down)
            {
                _sawDown = true;
                return;
            }

            if (!_sawDown) return;

            Native.POINT pt = new Native.POINT(Cursor.Position.X, Cursor.Position.Y);
            IntPtr hwnd = Native.WindowFromPoint(pt);
            if (hwnd != IntPtr.Zero)
                hwnd = Native.GetAncestor(hwnd, Native.GA_ROOT);

            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd) || IsOwnWindow(hwnd))
            {
                _sawDown = false;
                return;
            }

            StopClickPick();
            SelectedHandle = hwnd;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void StopClickPick()
        {
            _picking = false;
            _pickTimer.Stop();
        }

        private bool IsOwnWindow(IntPtr hwnd)
        {
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            return (int)pid == Process.GetCurrentProcess().Id;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            StopClickPick();
            base.OnFormClosing(e);
        }
    }
}
