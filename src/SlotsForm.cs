using System;
using System.Drawing;
using System.Windows.Forms;

namespace WindowDock
{
    public class SlotsForm : Form
    {
        private DockBar _bar;
        private ListView _list;
        private Button _bind;
        private Button _unbind;
        private Button _add;
        private Button _remove;
        private Button _up;
        private Button _down;
        private Button _close;

        public SlotsForm(DockBar bar)
        {
            _bar = bar;
            Text = "管理窗口槽位";
            AutoScaleMode = AutoScaleMode.None;
            AutoScaleDimensions = new SizeF(96F, 96F);
            ClientSize = new Size(720, 420);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = new Font("Microsoft YaHei UI", 9f);

            _list = new ListView();
            _list.Location = new Point(12, 12);
            _list.Size = new Size(696, 346);
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.MultiSelect = false;
            _list.HideSelection = false;
            _list.GridLines = true;
            _list.Columns.Add("槽位", 60);
            _list.Columns.Add("状态", 80);
            _list.Columns.Add("窗口标题", 380);
            _list.Columns.Add("进程", 150);
            _list.DoubleClick += delegate { BindSelected(); };
            _list.SelectedIndexChanged += delegate { UpdateButtons(); };
            _list.AllowDrop = true;
            _list.ItemDrag += delegate(object sender, ItemDragEventArgs e)
            {
                if (e.Item != null) _list.DoDragDrop(e.Item, DragDropEffects.Move);
            };
            _list.DragEnter += delegate(object sender, DragEventArgs e)
            {
                if (e.Data.GetDataPresent(typeof(ListViewItem))) e.Effect = DragDropEffects.Move;
            };
            _list.DragDrop += delegate(object sender, DragEventArgs e)
            {
                ListViewItem src = e.Data.GetData(typeof(ListViewItem)) as ListViewItem;
                if (src == null) return;
                Point pt = _list.PointToClient(new Point(e.X, e.Y));
                ListViewItem target = _list.GetItemAt(pt.X, pt.Y);
                if (target == null || target == src) return;
                _bar.MoveSlot(src.Index, target.Index);
                RefreshList(target.Index);
            };
            Controls.Add(_list);

            _bind = new Button();
            _bind.Text = "绑定窗口...";
            _bind.Location = new Point(12, 370);
            _bind.Size = new Size(110, 32);
            _bind.Click += delegate { BindSelected(); };
            Controls.Add(_bind);

            _unbind = new Button();
            _unbind.Text = "解除绑定";
            _unbind.Location = new Point(130, 370);
            _unbind.Size = new Size(100, 32);
            _unbind.Click += delegate { UnbindSelected(); };
            Controls.Add(_unbind);

            _add = new Button();
            _add.Text = "添加槽位";
            _add.Location = new Point(238, 370);
            _add.Size = new Size(100, 32);
            _add.Click += delegate
            {
                if (!_bar.AddSlot())
                {
                    MessageBox.Show(this, _bar.SlotLimitMessage, "WindowDock",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                RefreshList(_bar.Slots.Count - 1);
            };
            Controls.Add(_add);

            _remove = new Button();
            _remove.Text = "删除槽位";
            _remove.Location = new Point(346, 370);
            _remove.Size = new Size(100, 32);
            _remove.Click += delegate { RemoveSelected(); };
            Controls.Add(_remove);

            _up = new Button();
            _up.Text = "上移";
            _up.Location = new Point(454, 370);
            _up.Size = new Size(64, 32);
            _up.Click += delegate { MoveSelected(-1); };
            Controls.Add(_up);

            _down = new Button();
            _down.Text = "下移";
            _down.Location = new Point(526, 370);
            _down.Size = new Size(64, 32);
            _down.Click += delegate { MoveSelected(1); };
            Controls.Add(_down);

            _close = new Button();
            _close.Text = "关闭";
            _close.Location = new Point(598, 370);
            _close.Size = new Size(100, 32);
            _close.DialogResult = DialogResult.Cancel;
            Controls.Add(_close);

            CancelButton = _close;

            DpiScaleUtil.ScaleDialog(this);

            RefreshList(0);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _list.Focus();
            if (_list.Items.Count > 0)
                _list.Items[0].Selected = true;
        }

        private int SelectedIndex
        {
            get
            {
                if (_list.SelectedItems.Count == 0) return -1;
                return _list.SelectedItems[0].Index;
            }
        }

        private void RefreshList(int selectIndex)
        {
            _list.BeginUpdate();
            _list.Items.Clear();

            for (int i = 0; i < _bar.Slots.Count; i++)
            {
                SlotState s = _bar.Slots[i];
                bool valid = s.Hwnd != IntPtr.Zero && Native.IsWindow(s.Hwnd);
                string status = valid ? "已绑定" : (s.Hwnd != IntPtr.Zero ? "已失效" : "未绑定");
                string title = valid ? Native.GetWindowTitle(s.Hwnd) : "";
                ListViewItem item = new ListViewItem((i + 1).ToString());
                item.SubItems.Add(status);
                item.SubItems.Add(title);
                item.SubItems.Add(s.ProcessName);
                _list.Items.Add(item);
            }

            _list.EndUpdate();

            if (selectIndex >= 0 && selectIndex < _list.Items.Count)
            {
                _list.Items[selectIndex].Selected = true;
                _list.Items[selectIndex].Focused = true;
                _list.EnsureVisible(selectIndex);
            }

            UpdateButtons();
        }

        private void UpdateButtons()
        {
            int idx = SelectedIndex;
            bool has = idx >= 0 && idx < _bar.Slots.Count;
            _bind.Enabled = has;
            _unbind.Enabled = has;
            _remove.Enabled = has;
            _up.Enabled = has && idx > 0;
            _down.Enabled = has && idx < _bar.Slots.Count - 1;
            _add.Enabled = _bar.Slots.Count < _bar.SlotCapacity;
        }

        private void MoveSelected(int delta)
        {
            int idx = SelectedIndex;
            if (idx < 0 || idx >= _bar.Slots.Count) return;
            int to = idx + delta;
            if (to < 0 || to >= _bar.Slots.Count) return;
            _bar.MoveSlot(idx, to);
            RefreshList(to);
        }

        private void BindSelected()
        {
            int idx = SelectedIndex;
            if (idx < 0)
            {
                MessageBox.Show(this, "请先选择一个槽位。", "WindowDock",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (PickerForm f = new PickerForm())
            {
                f.TopMost = TopMost;
                if (f.ShowDialog(this) == DialogResult.OK && f.SelectedHandle != IntPtr.Zero)
                    _bar.BindSlot(idx, f.SelectedHandle);
            }
            RefreshList(idx);
        }

        private void UnbindSelected()
        {
            int idx = SelectedIndex;
            if (idx < 0)
            {
                MessageBox.Show(this, "请先选择一个槽位。", "WindowDock",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _bar.UnbindSlot(idx, true);
            RefreshList(idx);
        }

        private void RemoveSelected()
        {
            int idx = SelectedIndex;
            if (idx < 0)
            {
                MessageBox.Show(this, "请先选择一个槽位。", "WindowDock",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            SlotState s = _bar.Slots[idx];
            bool bound = s.Hwnd != IntPtr.Zero && Native.IsWindow(s.Hwnd);
            if (bound)
            {
                DialogResult r = MessageBox.Show(this,
                    "该槽位已绑定窗口，删除会解除绑定并恢复窗口原来的位置。\n确定删除？",
                    "WindowDock", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) return;
            }

            _bar.RemoveSlot(idx);
            int select = idx;
            if (select >= _bar.Slots.Count) select = _bar.Slots.Count - 1;
            RefreshList(select);
        }
    }
}
