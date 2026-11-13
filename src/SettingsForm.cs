using System;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace WindowDock
{
    internal class ComboItem
    {
        public string Text;
        public string Value;

        public ComboItem(string text, string value)
        {
            Text = text;
            Value = value;
        }

        public override string ToString()
        {
            return Text;
        }
    }

    public class SettingsForm : Form
    {
        private Settings _cfg;
        private ComboBox _edge;
        private ComboBox _monitor;
        private NumericUpDown _slotWidth;
        private NumericUpDown _slotHeight;
        private CheckBox _autoHide;
        private NumericUpDown _delay;
        private CheckBox _topMost;
        private CheckBox _allowExpand;
        private CheckBox _tasksVisible;
        private NumericUpDown _opacity;
        private Button _backgroundColor;
        private Color _selectedBackground;
        private NumericUpDown _reminderHours;
        private CheckBox _startWithWindows;
        private ComboBox _layoutMode;
        private NumericUpDown _gridRows;
        private NumericUpDown _gridColumns;
        private TextBox _hotkey;
        private Button _ok;
        private Button _cancel;
        private int _hkMods;
        private int _hkKey;

        public Settings Result;

        public SettingsForm(Settings cfg)
        {
            _cfg = cfg;
            _hkMods = cfg.HotkeyMods;
            _hkKey = cfg.HotkeyKey;

            Text = "WindowDock 设置";
            AutoScaleMode = AutoScaleMode.None;
            AutoScaleDimensions = new SizeF(96F, 96F);
            ClientSize = new Size(520, 760);
            AutoScroll = true;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = new Font("Microsoft YaHei UI", 9f);

            const int labelX = 18;
            const int ctrlX = 150;
            const int ctrlW = 350;
            const int labelW = 124;
            int y = 18;
            const int step = 40;

            AddLabel("停放边缘", labelX, y + 4, labelW);
            _edge = new ComboBox();
            _edge.DropDownStyle = ComboBoxStyle.DropDownList;
            _edge.Location = new Point(ctrlX, y);
            _edge.Size = new Size(ctrlW, 26);
            _edge.Items.Add(new ComboItem("右侧边缘", "Right"));
            _edge.Items.Add(new ComboItem("左侧边缘", "Left"));
            _edge.Items.Add(new ComboItem("顶部边缘", "Top"));
            _edge.Items.Add(new ComboItem("底部边缘", "Bottom"));
            SelectEdge(cfg.Edge);
            Controls.Add(_edge);
            y += step;

            AddLabel("显示器", labelX, y + 4, labelW);
            _monitor = new ComboBox();
            _monitor.DropDownStyle = ComboBoxStyle.DropDownList;
            _monitor.Location = new Point(ctrlX, y);
            _monitor.Size = new Size(ctrlW, 26);
            Screen[] screens = Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                Screen s = screens[i];
                string text = (i + 1) + ": " + s.Bounds.Width + "x" + s.Bounds.Height +
                              (s.Primary ? "（主显示器）" : "");
                _monitor.Items.Add(text);
            }
            int mi = cfg.MonitorIndex;
            if (mi < 0 || mi >= _monitor.Items.Count) mi = 0;
            _monitor.SelectedIndex = mi;
            Controls.Add(_monitor);
            y += step;

            AddLabel("窗口排列", labelX, y + 4, labelW);
            _layoutMode = new ComboBox();
            _layoutMode.DropDownStyle = ComboBoxStyle.DropDownList;
            _layoutMode.SetBounds(ctrlX, y, ctrlW, 26);
            _layoutMode.Items.Add("横向单排");
            _layoutMode.Items.Add("田字格（2 × 2）");
            _layoutMode.Items.Add("自定义网格");
            Controls.Add(_layoutMode);
            y += step;

            AddLabel("网格行列", labelX, y + 4, labelW);
            _gridRows = new NumericUpDown();
            _gridRows.SetBounds(ctrlX, y, 90, 26);
            _gridRows.Minimum = 1;
            _gridRows.Maximum = Settings.MaxSlots;
            _gridRows.Value = Clamp(cfg.GridRows, 1, Settings.MaxSlots);
            Controls.Add(_gridRows);
            AddLabel("行 ×", ctrlX + 98, y + 4, 48);
            _gridColumns = new NumericUpDown();
            _gridColumns.SetBounds(ctrlX + 146, y, 90, 26);
            _gridColumns.Minimum = 1;
            _gridColumns.Maximum = Settings.MaxSlots;
            _gridColumns.Value = Clamp(cfg.GridColumns, 1, Settings.MaxSlots);
            Controls.Add(_gridColumns);
            AddLabel("列（最多 8 格）", ctrlX + 244, y + 4, 106);
            _layoutMode.SelectedIndexChanged += delegate
            {
                bool custom = _layoutMode.SelectedIndex == 2;
                _gridRows.Enabled = custom;
                _gridColumns.Enabled = custom;
                if (_layoutMode.SelectedIndex == 1) { _gridRows.Value = 2; _gridColumns.Value = 2; }
            };
            _layoutMode.SelectedIndex = cfg.LayoutMode != "Grid" ? 0 :
                (cfg.GridRows == 2 && cfg.GridColumns == 2 ? 1 : 2);
            y += step;

            AddLabel("槽位宽度（像素）", labelX, y + 4, labelW);
            _slotWidth = new NumericUpDown();
            _slotWidth.Location = new Point(ctrlX, y);
            _slotWidth.Size = new Size(120, 26);
            _slotWidth.Minimum = 200;
            _slotWidth.Maximum = 1600;
            _slotWidth.Increment = 20;
            _slotWidth.Value = Clamp(cfg.SlotWidth, 200, 1600);
            Controls.Add(_slotWidth);
            y += step;

            AddLabel("槽位高度（像素）", labelX, y + 4, labelW);
            _slotHeight = new NumericUpDown();
            _slotHeight.Location = new Point(ctrlX, y);
            _slotHeight.Size = new Size(120, 26);
            _slotHeight.Minimum = 240;
            _slotHeight.Maximum = 1600;
            _slotHeight.Increment = 20;
            _slotHeight.Value = Clamp(cfg.SlotHeight, 240, 1600);
            Controls.Add(_slotHeight);
            y += step;

            _autoHide = new CheckBox();
            _autoHide.Text = "鼠标离开后自动隐藏";
            _autoHide.Location = new Point(ctrlX, y);
            _autoHide.Size = new Size(ctrlW, 24);
            _autoHide.Checked = cfg.AutoHide;
            Controls.Add(_autoHide);
            y += step;

            AddLabel("隐藏延迟（毫秒）", labelX, y + 4, labelW);
            _delay = new NumericUpDown();
            _delay.Location = new Point(ctrlX, y);
            _delay.Size = new Size(120, 26);
            _delay.Minimum = 200;
            _delay.Maximum = 5000;
            _delay.Increment = 100;
            _delay.Value = Clamp(cfg.AutoHideDelayMs, 200, 5000);
            Controls.Add(_delay);
            y += step;

            _topMost = new CheckBox();
            _topMost.Text = "停靠栏和窗口总在最前";
            _topMost.Location = new Point(ctrlX, y);
            _topMost.Size = new Size(ctrlW, 24);
            _topMost.Checked = cfg.AlwaysOnTop;
            Controls.Add(_topMost);
            y += step;

            _allowExpand = new CheckBox();
            _allowExpand.Text = "应用有最小尺寸时自动加宽槽位";
            _allowExpand.Location = new Point(ctrlX, y);
            _allowExpand.Size = new Size(ctrlW, 24);
            _allowExpand.Checked = cfg.AllowSlotExpand;
            Controls.Add(_allowExpand);
            y += step;

            AddLabel("全局快捷键", labelX, y + 4, labelW);
            _hotkey = new TextBox();
            _hotkey.Location = new Point(ctrlX, y);
            _hotkey.Size = new Size(200, 26);
            _hotkey.ReadOnly = true;
            _hotkey.BackColor = Color.White;
            _hotkey.KeyDown += HotkeyKeyDown;
            _hotkey.Text = FormatHotkey(_hkMods, _hkKey);
            Controls.Add(_hotkey);
            AddLabel("点击后按下组合键", ctrlX + 210, y + 4, 140);
            y += step;

            _tasksVisible = new CheckBox();
            _tasksVisible.Text = "显示头部任务清单";
            _tasksVisible.Location = new Point(ctrlX, y);
            _tasksVisible.Size = new Size(ctrlW, 24);
            _tasksVisible.Checked = cfg.TasksVisible;
            Controls.Add(_tasksVisible);
            y += step;

            _startWithWindows = new CheckBox();
            _startWithWindows.Text = "开机自动启动（登录后驻留托盘）";
            _startWithWindows.SetBounds(ctrlX, y, ctrlW, 24);
            _startWithWindows.Checked = cfg.StartWithWindows;
            Controls.Add(_startWithWindows);
            y += step;

            AddLabel("整体不透明度（%）", labelX, y + 4, labelW);
            _opacity = new NumericUpDown();
            _opacity.Location = new Point(ctrlX, y);
            _opacity.Size = new Size(120, 26);
            _opacity.Minimum = 20;
            _opacity.Maximum = 100;
            _opacity.Increment = 5;
            _opacity.Value = Clamp(cfg.OpacityPercent, 20, 100);
            Controls.Add(_opacity);
            AddLabel("100 为完全不透明", ctrlX + 130, y + 4, 210);
            y += step;

            AddLabel("背景颜色", labelX, y + 4, labelW);
            _selectedBackground = Color.FromArgb(255, Color.FromArgb(cfg.BackgroundColorArgb));
            _backgroundColor = new Button();
            _backgroundColor.Location = new Point(ctrlX, y);
            _backgroundColor.Size = new Size(180, 28);
            _backgroundColor.Text = "选择颜色...";
            _backgroundColor.UseVisualStyleBackColor = false;
            UpdateColorPreview();
            _backgroundColor.Click += delegate
            {
                using (ColorDialog dialog = new ColorDialog())
                {
                    dialog.Color = _selectedBackground;
                    dialog.FullOpen = true;
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        _selectedBackground = dialog.Color;
                        UpdateColorPreview();
                    }
                }
            };
            Controls.Add(_backgroundColor);
            y += step;

            AddLabel("截止前提醒（小时）", labelX, y + 4, labelW);
            _reminderHours = new NumericUpDown();
            _reminderHours.Location = new Point(ctrlX, y);
            _reminderHours.Size = new Size(120, 26);
            _reminderHours.Minimum = 1;
            _reminderHours.Maximum = 720;
            _reminderHours.Value = Clamp(cfg.DeadlineReminderHours, 1, 720);
            Controls.Add(_reminderHours);
            AddLabel("未完成任务临近截止时变红", ctrlX + 130, y + 4, 220);
            y += step;

            Label tip = new Label();
            tip.Location = new Point(labelX, y + 2);
            tip.Size = new Size(484, 44);
            tip.AutoSize = false;
            tip.ForeColor = Color.FromArgb(90, 90, 90);
            tip.Text = "提示：隐藏后，把鼠标移到屏幕对应边缘即可重新显示；全局快捷键也可以切换显示/隐藏。";
            Controls.Add(tip);

            _ok = new Button();
            _ok.Text = "确定";
            _ok.Location = new Point(330, 714);
            _ok.Size = new Size(80, 32);
            _ok.Click += delegate { SaveAndClose(); };
            Controls.Add(_ok);

            _cancel = new Button();
            _cancel.Text = "取消";
            _cancel.Location = new Point(422, 714);
            _cancel.Size = new Size(80, 32);
            _cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(_cancel);

            AcceptButton = _ok;
            CancelButton = _cancel;

            DpiScaleUtil.ScaleDialog(this);
            Rectangle work = Screen.FromControl(this).WorkingArea;
            if (Height > work.Height - 40)
            {
                AutoScrollMinSize = ClientSize;
                Height = Math.Max(320, work.Height - 40);
            }
        }

        private void UpdateColorPreview()
        {
            _backgroundColor.BackColor = _selectedBackground;
            _backgroundColor.ForeColor = _selectedBackground.GetBrightness() > 0.55f ? Color.Black : Color.White;
        }

        private void AddLabel(string text, int x, int y, int w)
        {
            Label label = new Label();
            label.Text = text;
            label.Location = new Point(x, y);
            label.Size = new Size(w, 22);
            label.AutoSize = false;
            label.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(label);
        }

        private static int Clamp(int v, int min, int max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        private void SelectEdge(string edge)
        {
            for (int i = 0; i < _edge.Items.Count; i++)
            {
                ComboItem item = _edge.Items[i] as ComboItem;
                if (item != null && item.Value == edge)
                {
                    _edge.SelectedIndex = i;
                    return;
                }
            }
            _edge.SelectedIndex = 0;
        }

        private void HotkeyKeyDown(object sender, KeyEventArgs e)
        {
            e.SuppressKeyPress = true;
            e.Handled = true;

            Keys k = e.KeyCode;
            if (k == Keys.ControlKey || k == Keys.LControlKey || k == Keys.RControlKey ||
                k == Keys.ShiftKey || k == Keys.LShiftKey || k == Keys.RShiftKey ||
                k == Keys.Menu || k == Keys.LMenu || k == Keys.RMenu ||
                k == Keys.LWin || k == Keys.RWin)
                return;

            int mods = 0;
            if ((Control.ModifierKeys & Keys.Control) != 0) mods |= Native.MOD_CONTROL;
            if ((Control.ModifierKeys & Keys.Alt) != 0) mods |= Native.MOD_ALT;
            if ((Control.ModifierKeys & Keys.Shift) != 0) mods |= Native.MOD_SHIFT;

            if (mods == 0)
            {
                _hotkey.Text = "请按 Ctrl/Alt/Shift + 键";
                return;
            }

            _hkMods = mods;
            _hkKey = (int)k;
            _hotkey.Text = FormatHotkey(_hkMods, _hkKey);
        }

        public static string FormatHotkey(int mods, int key)
        {
            StringBuilder sb = new StringBuilder();
            if ((mods & Native.MOD_CONTROL) != 0) sb.Append("Ctrl+");
            if ((mods & Native.MOD_ALT) != 0) sb.Append("Alt+");
            if ((mods & Native.MOD_SHIFT) != 0) sb.Append("Shift+");
            if ((mods & Native.MOD_WIN) != 0) sb.Append("Win+");
            sb.Append(KeyName(key));
            return sb.ToString();
        }

        private static string KeyName(int key)
        {
            Keys k = (Keys)key;
            if (k >= Keys.D0 && k <= Keys.D9)
                return ((char)('0' + (key - (int)Keys.D0))).ToString();
            if (k == Keys.Space) return "Space";
            return k.ToString();
        }

        private void SaveAndClose()
        {
            if (_hkKey < 1)
            {
                MessageBox.Show(this, "请设置一个快捷键。", "WindowDock",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Settings s = _cfg.Clone();
            s.StartWithWindows = _startWithWindows.Checked;
            s.LayoutMode = _layoutMode.SelectedIndex == 0 ? "Row" : "Grid";
            s.GridRows = (int)_gridRows.Value;
            s.GridColumns = (int)_gridColumns.Value;
            if (s.LayoutMode == "Grid" && (s.GridRows * s.GridColumns > Settings.MaxSlots ||
                s.GridRows * s.GridColumns < s.Slots.Count))
            {
                MessageBox.Show(this, "网格最多支持 8 个窗口，且格数不能少于已有槽位。请调整行列数。", "WindowDock");
                return;
            }
            ComboItem edgeItem = _edge.SelectedItem as ComboItem;
            if (edgeItem != null) s.Edge = edgeItem.Value;
            s.MonitorIndex = _monitor.SelectedIndex < 0 ? 0 : _monitor.SelectedIndex;
            s.SlotWidth = (int)_slotWidth.Value;
            s.SlotHeight = (int)_slotHeight.Value;
            s.AutoHide = _autoHide.Checked;
            s.AutoHideDelayMs = (int)_delay.Value;
            s.AlwaysOnTop = _topMost.Checked;
            s.AllowSlotExpand = _allowExpand.Checked;
            s.TasksVisible = _tasksVisible.Checked;
            s.OpacityPercent = (int)_opacity.Value;
            s.BackgroundColorArgb = _selectedBackground.ToArgb();
            s.DeadlineReminderHours = (int)_reminderHours.Value;
            s.HotkeyMods = _hkMods;
            s.HotkeyKey = _hkKey;

            Result = s;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
