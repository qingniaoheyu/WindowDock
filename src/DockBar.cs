using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace WindowDock
{
    public class SlotState
    {
        public IntPtr Hwnd = IntPtr.Zero;
        public string ProcessName = "";
        public string TitleHint = "";
        public Rectangle OriginalRect = Rectangle.Empty;
        public bool OriginalVisible;
        public bool OriginalTopmost;
        public bool OriginalPlacementValid;
        public Native.WINDOWPLACEMENT OriginalPlacement;
        public bool HiddenByUs;
        public int InvisibleTicks;
        public int LastForceShowTick;
        public int RestoreGraceUntil;
        public bool Unread;
        public HashSet<IntPtr> ExistingWindows = new HashSet<IntPtr>();

        public void ClearBinding()
        {
            Hwnd = IntPtr.Zero;
            OriginalRect = Rectangle.Empty;
            OriginalVisible = false;
            OriginalTopmost = false;
            OriginalPlacementValid = false;
            HiddenByUs = false;
            InvisibleTicks = 0;
            LastForceShowTick = 0;
            RestoreGraceUntil = 0;
            Unread = false;
            ExistingWindows.Clear();
        }
    }

    internal sealed class PopupState
    {
        public IntPtr Hwnd;
        public IntPtr DockedHwnd;
        public uint ProcessId;
        public uint ThreadId;
        public bool OriginalTopmost;
        public bool HiddenByUs;
        // 无 owner 的工具窗口（消息提醒、悬浮面板等）只负责置顶，不随停靠栏隐藏/恢复，
        // 否则会把刚好弹出的通知立刻藏掉，恢复时还可能把已过期的提醒重新弹出来。
        public bool Transient;

        public bool IsValid
        {
            get
            {
                uint pid;
                return Native.IsWindow(Hwnd) &&
                    Native.GetWindowThreadProcessId(Hwnd, out pid) == ThreadId && pid == ProcessId;
            }
        }
    }

    internal class RailButton
    {
        public Rectangle Rect;
        public string Glyph;
        public string Tip;
        public string Action;

        public RailButton(Rectangle rect, string glyph, string tip, string action)
        {
            Rect = rect;
            Glyph = glyph;
            Tip = tip;
            Action = action;
        }
    }

    public partial class DockBar : Form
    {
        private const int RAIL = 46;
        private const int MARGIN = 10;
        private const int GAP = 8;
        private const int HEADER_H = 192;
        private const int HOTKEY_ID = 1;
        private const int WM_HOTKEY = 0x0312;
        private const int WM_DISPLAYCHANGE = 0x007E;
        private const int WM_ACTIVATE = 0x0006;
        private const int WM_DPICHANGED = 0x02E0;
        private const int WM_CONTEXTMENU = 0x007B;

        private Settings _cfg;
        private List<SlotState> _slots = new List<SlotState>();
        private Dictionary<IntPtr, PopupState> _popups = new Dictionary<IntPtr, PopupState>();
        private Dictionary<IntPtr, WindowOpacityState> _windowOpacity = new Dictionary<IntPtr, WindowOpacityState>();
        private int _popupScanMs;
        private List<RailButton> _buttons = new List<RailButton>();
        private Rectangle _panelRect;
        private Rectangle _railRect;
        private Rectangle _headerRect;
        private Rectangle[] _slotRects = new Rectangle[0];
        private int[] _slotMinW = new int[0];
        private int _slotMinH;
        private int[] _slotMinHeights = new int[0];
        private bool _layoutExpanding;

        private bool _visible;
        private bool _exiting;
        private bool _cleaned;
        private bool _initialized;
        private bool _startHidden;
        private UiHangMonitor _hangMonitor;
        private int _lastLayoutTick;
        private string _regionSignature;
        private bool _hotkeyRegistered;
        private bool _suspendAutoHide;
        private Form _activeDialog;
        private int _edgeDwellMs;
        private int _leaveMs;
        private int _hoverIndex = -1;
        private int _dragSlot = -1;
        private int _rebindCounter;
        private bool _relayoutPending;
        private float _scale = 1f;
        private IntPtr _preShowForeground = IntPtr.Zero;

        private Timer _uiTimer;
        private Timer _enforceTimer;
        private NotifyIcon _tray;
        private Icon _trayBaseIcon;
        private Icon _trayUnreadIcon;
        private IntPtr _trayUnreadHandle = IntPtr.Zero;
        private ContextMenuStrip _barMenu;
        private ContextMenuStrip _trayMenu;
        private ToolTip _tip;
        private string _tipShowing;

        private Font _glyphFont;
        private Font _plusFont;
        private Font _slotTitleFont;
        private Font _slotHintFont;
        private Font _taskHintFont;

        private TaskListBox _taskList;
        private TaskInputBox _taskInput;
        private DateTimePicker _taskDeadline;
        private Button _taskSave;
        private bool _taskSyncing;
        private int _editingTask = -1;

        public DockBar(Settings cfg) : this(cfg, false) { }

        public DockBar(Settings cfg, bool startHidden)
        {
            _cfg = cfg;
            _startHidden = startHidden;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(30, 31, 35);
            Text = "WindowDock";
            TopMost = cfg.AlwaysOnTop;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

            Font = new Font("Microsoft YaHei UI", 9f);
            _glyphFont = new Font("Segoe UI Symbol", 15f, FontStyle.Regular, GraphicsUnit.Point);
            _plusFont = new Font("Segoe UI", 26f, FontStyle.Regular, GraphicsUnit.Point);
            _slotTitleFont = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            _slotHintFont = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular);
            _taskHintFont = new Font("Microsoft YaHei UI", 8f, FontStyle.Regular);

            try
            {
                Icon appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (appIcon != null) Icon = appIcon;
            }
            catch (Exception)
            {
            }

            BuildSlotsFromConfig();
            CreateTaskControls();
            ApplyAppearance();

            _tip = new ToolTip();
            _tip.SetToolTip(_taskDeadline, "勾选后设置截止日期和时间；取消勾选表示不设截止时间。");
            _tip.SetToolTip(_taskList, "点击复选框或按空格切换完成状态；双击文字编辑；点击右侧日期或按 F2 设置该任务的截止时间。");
            _barMenu = BuildMenu();
            ContextMenuStrip = _barMenu;

            _tray = new NotifyIcon();
            _tray.Icon = Icon != null ? Icon : SystemIcons.Application;
            _tray.Text = "WindowDock 窗口停靠栏";
            _tray.Visible = true;
            _tray.DoubleClick += delegate { ToggleVisibility(); };
            _trayMenu = BuildMenu();
            _tray.ContextMenuStrip = _trayMenu;

            ComputeLayout();
            SetBounds(_panelRect.X, _panelRect.Y, _panelRect.Width, _panelRect.Height);

            _uiTimer = new Timer();
            _uiTimer.Interval = 50;
            _uiTimer.Tick += UiTick;

            _enforceTimer = new Timer();
            _enforceTimer.Interval = 1000;
            _enforceTimer.Tick += EnforceTick;
            _hangMonitor = new UiHangMonitor(YswLog);

            // WM_DISPLAYCHANGE 之外再兜底监听：远程桌面/虚拟机切换分辨率时有时只触发系统事件。
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        }

        public List<SlotState> Slots
        {
            get { return _slots; }
        }

        public Settings Config
        {
            get { return _cfg; }
        }

        public int SlotCapacity
        {
            get { return _cfg.LayoutMode == "Grid" ? Math.Min(Settings.MaxSlots, _cfg.GridRows * _cfg.GridColumns) : Settings.MaxSlots; }
        }

        public string SlotLimitMessage
        {
            get { return _cfg.LayoutMode == "Grid" && SlotCapacity < Settings.MaxSlots
                ? "当前网格已满，请在设置中增加行列数。最多支持 8 个窗口。" : "最多支持 8 个窗口槽位。"; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        private void BuildSlotsFromConfig()
        {
            _slots.Clear();
            foreach (SlotConfig sc in _cfg.Slots)
            {
                SlotState s = new SlotState();
                s.ProcessName = sc.ProcessName == null ? "" : sc.ProcessName;
                s.TitleHint = sc.TitleHint == null ? "" : sc.TitleHint;
                _slots.Add(s);
            }
            if (_cfg.LayoutMode == "Grid")
                while (_slots.Count < SlotCapacity) _slots.Add(new SlotState());
            ResetSlotMeasurements();
        }

        private void CreateTaskControls()
        {
            _taskList = new TaskListBox();
            _taskList.IntegralHeight = false;
            _taskList.BorderStyle = BorderStyle.None;
            _taskList.BackColor = Color.FromArgb(36, 38, 44);
            _taskList.ForeColor = Color.FromArgb(214, 220, 230);
            _taskList.Font = new Font("Microsoft YaHei UI", 9f);
            _taskList.ItemCheck += TaskItemCheck;
            _taskList.DeadlineEditRequested += delegate(object sender, TaskDeadlineEventArgs e) { EditTaskDeadline(e.Index); };
            _taskList.DoubleClick += delegate { EditTask(_taskList.SelectedIndex); };
            _taskList.KeyDown += TaskListKeyDown;
            Controls.Add(_taskList);

            _taskInput = new TaskInputBox();
            _taskInput.BorderStyle = BorderStyle.FixedSingle;
            _taskInput.BackColor = Color.FromArgb(48, 51, 58);
            _taskInput.ForeColor = Color.FromArgb(230, 234, 242);
            _taskInput.Font = new Font("Microsoft YaHei UI", 9f);
            _taskInput.KeyDown += TaskInputKeyDown;
            _taskInput.HandleCreated += delegate
            {
                try { Native.SendMessage(_taskInput.Handle, 0x1501, new IntPtr(1), "输入任务，回车添加"); }
                catch (Exception) { }
            };
            Controls.Add(_taskInput);

            _taskDeadline = new DateTimePicker();
            _taskDeadline.Format = DateTimePickerFormat.Custom;
            _taskDeadline.CustomFormat = "'截止 'yyyy-MM-dd HH:mm";
            _taskDeadline.ShowCheckBox = true;
            _taskDeadline.Font = _taskInput.Font;
            _taskDeadline.KeyDown += TaskInputKeyDown;
            Controls.Add(_taskDeadline);

            _taskSave = new Button();
            _taskSave.Text = "添加";
            _taskSave.Font = _taskInput.Font;
            _taskSave.Click += delegate { CommitTask(); };
            Controls.Add(_taskSave);
            ResetTaskEditor();

            RebuildTaskList();
            ApplyTasksVisibility();
        }

        private void TaskItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (_taskSyncing) return;
            int i = e.Index;
            if (i < 0 || i >= _cfg.Tasks.Count) return;
            _cfg.Tasks[i].Checked = e.NewValue == CheckState.Checked;
            SaveConfig();
        }

        private void TaskListKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F2)
            {
                EditTaskDeadline(_taskList.SelectedIndex);
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
            if (e.KeyCode != Keys.Delete) return;
            int i = _taskList.SelectedIndex;
            if (i < 0 || i >= _cfg.Tasks.Count) return;
            _cfg.Tasks.RemoveAt(i);
            ResetTaskEditor();
            RebuildTaskList();
            SaveConfig();
            e.Handled = true;
        }

        private void TaskInputKeyDown(object sender, KeyEventArgs e)
        {
            // 回车和 Esc 在中文组词时由输入法处理，不能清空输入框或提交任务。
            if (_taskInput.IsComposing) return;
            if (e.KeyCode == Keys.Escape)
            {
                ResetTaskEditor();
                e.SuppressKeyPress = true;
                e.Handled = true;
                return;
            }
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            e.Handled = true;
            CommitTask();
        }

        private void CommitTask()
        {
            using (UiHangMonitor.Track("CommitTask")) CommitTaskCore();
        }

        private void CommitTaskCore()
        {
            string text = _taskInput.Text.Trim();
            if (text.Length == 0)
            {
                return;
            }
            if (text.Length > 500) text = text.Substring(0, 500);
            DateTime? deadline = _taskDeadline.Checked ? (DateTime?)_taskDeadline.Value : null;

            if (_editingTask >= 0 && _editingTask < _cfg.Tasks.Count)
            {
                _cfg.Tasks[_editingTask].Text = text;
                _cfg.Tasks[_editingTask].Deadline = deadline;
            }
            else
            {
                if (_cfg.Tasks.Count >= 50)
                {
                    MessageBox.Show(this, "最多支持 50 个任务，请先删除已完成的任务。", "WindowDock");
                    return;
                }
                TaskItem t = new TaskItem();
                t.Text = text;
                t.Deadline = deadline;
                _cfg.Tasks.Add(t);
            }
            ResetTaskEditor();
            RebuildTaskList();
            SaveConfig();
        }

        private void EditTask(int index)
        {
            if (index < 0 || index >= _cfg.Tasks.Count) return;
            _editingTask = index;
            _taskInput.Text = _cfg.Tasks[index].Text;
            SetTaskDeadline(_cfg.Tasks[index].Deadline);
            _taskSave.Text = "保存";
            _taskInput.Focus();
            _taskInput.SelectAll();
        }

        private void SetTaskDeadline(DateTime? deadline)
        {
            DateTime value = deadline.HasValue ? deadline.Value : DateTime.Now.AddDays(1);
            if (value < _taskDeadline.MinDate) value = _taskDeadline.MinDate;
            if (value > _taskDeadline.MaxDate) value = _taskDeadline.MaxDate;
            _taskDeadline.Value = value;
            _taskDeadline.Checked = deadline.HasValue;
        }

        private void EditTaskDeadline(int index)
        {
            if (index < 0 || index >= _cfg.Tasks.Count) return;
            TaskItem task = _cfg.Tasks[index];
            using (TaskDeadlineForm dialog = new TaskDeadlineForm(task))
            {
                if (ShowManagedDialog(dialog) == DialogResult.OK)
                {
                    task.Deadline = dialog.Deadline;
                    if (_editingTask == index) SetTaskDeadline(task.Deadline);
                    _taskList.Invalidate();
                    SaveConfig();
                }
            }
        }

        private void ResetTaskEditor()
        {
            _editingTask = -1;
            _taskInput.Clear();
            SetTaskDeadline(null);
            _taskSave.Text = "添加";
        }

        private void RebuildTaskList()
        {
            _taskSyncing = true;
            try
            {
                _taskList.Items.Clear();
                foreach (TaskItem t in _cfg.Tasks)
                    _taskList.Items.Add(t);
            }
            finally
            {
                _taskSyncing = false;
            }
        }

        private void ApplyTasksVisibility()
        {
            bool v = _cfg.TasksVisible;
            if (_taskList != null) _taskList.Visible = v;
            if (_taskInput != null) _taskInput.Visible = v;
            if (_taskDeadline != null) _taskDeadline.Visible = v;
            if (_taskSave != null) _taskSave.Visible = v;
        }

        private void ToggleTasks()
        {
            if (_cfg.LayoutMode == "Grid" && !_cfg.TasksVisible)
            {
                Settings candidate = _cfg.Clone();
                candidate.TasksVisible = true;
                string error;
                if (!CanFitGrid(candidate, -1, IntPtr.Zero, out error))
                {
                    MessageBox.Show(this, error, "WindowDock");
                    return;
                }
            }
            _cfg.TasksVisible = !_cfg.TasksVisible;
            ApplyTasksVisibility();
            RelayoutVisible();
            SaveConfig();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RegisterHotkey(false);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_initialized) return;
            _initialized = true;
            YswLog("OnShown");
            _visible = true;
            AutoBindSlots();
            ComputeLayout();
            SetBounds(_panelRect.X, _panelRect.Y, _panelRect.Width, _panelRect.Height);
            LayoutDocked(true);
            _uiTimer.Start();
            _enforceTimer.Start();
            Invalidate();
            if (_startHidden) HideDock();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
            {
                ToggleVisibility();
                return;
            }
            if (m.Msg == WM_ACTIVATE)
            {
                // 点击工具栏会使本窗体在置顶带内抬到停靠窗口之上，巨大的深色背景会盖住槽位，
                // 表现为"点几次就黑屏"。激活后把停靠窗口重新压回工具栏上方。
                base.WndProc(ref m);
                if (m.WParam.ToInt32() != 0 && _visible)
                {
                    try { BeginInvoke(new MethodInvoker(RaiseDockedAboveBar)); }
                    catch (Exception) { }
                }
                return;
            }
            if (m.Msg == AppMessages.Toggle)
            {
                ToggleVisibility();
                return;
            }
            if (m.Msg == AppMessages.Show)
            {
                YswLog("msg Show");
                ShowDock();
                return;
            }
            if (m.Msg == AppMessages.Hide)
            {
                YswLog("msg Hide");
                HideDock();
                return;
            }
            if (m.Msg == AppMessages.Exit)
            {
                ExitApp();
                return;
            }
            if (m.Msg == AppMessages.Settings)
            {
                OpenSettingsDialog();
                return;
            }
            if (m.Msg == AppMessages.Slots)
            {
                OpenSlotsDialog();
                return;
            }
            if (m.Msg == WM_DISPLAYCHANGE || m.Msg == WM_DPICHANGED)
            {
                ResetSlotMeasurements();
                ScheduleRelayout(m.Msg == WM_DPICHANGED ? "DPICHANGED" : "DISPLAYCHANGE");
                if (m.Msg == WM_DPICHANGED) return;
            }
            if (m.Msg == WM_CONTEXTMENU && m.WParam == Handle)
            {
                long lp = m.LParam.ToInt64();
                int sx = (short)(lp & 0xFFFF);
                int sy = (short)((lp >> 16) & 0xFFFF);
                Point sp = new Point(sx, sy);
                int idx = HitSlot(PointToClient(sp));
                if (idx >= 0)
                {
                    ShowSlotMenu(idx, PointToClient(sp));
                    return;
                }
            }
            base.WndProc(ref m);
        }

        private ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem toggle = new ToolStripMenuItem("隐藏工具栏");
            toggle.Click += delegate { ToggleVisibility(); };

            ToolStripMenuItem manage = new ToolStripMenuItem("管理窗口槽位...");
            manage.Click += delegate { OpenSlotsDialog(); };

            ToolStripMenuItem add = new ToolStripMenuItem("添加窗口槽位");
            add.Click += delegate { AddSlot(); };

            ToolStripMenuItem refresh = new ToolStripMenuItem("刷新停靠窗口画面");
            refresh.Click += delegate { RefreshAllSlots(); };

            ToolStripMenuItem settings = new ToolStripMenuItem("设置...");
            settings.Click += delegate { OpenSettingsDialog(); };

            ToolStripMenuItem exit = new ToolStripMenuItem("恢复窗口并退出");
            exit.Click += delegate { ExitApp(); };

            menu.Items.Add(toggle);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(manage);
            menu.Items.Add(add);
            menu.Items.Add(refresh);
            menu.Items.Add(settings);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exit);

            menu.Opening += delegate
            {
                toggle.Text = _visible ? "隐藏工具栏" : "显示工具栏";
            };
            return menu;
        }

        public void ToggleVisibility()
        {
            if (_visible) HideDock();
            else ShowDock();
        }

        public void ShowDock()
        {
            using (UiHangMonitor.Track("ShowDock")) ShowDockCore();
        }

        private void ShowDockCore()
        {
            if (_exiting || _visible) return;
            YswLog("ShowDock enter");

            IntPtr fg = Native.GetForegroundWindow();
            if (fg != IntPtr.Zero && !IsDockedWindow(fg) && !IsOwnWindow(fg))
                _preShowForeground = fg;

            ComputeLayout();
            SetBounds(_panelRect.X, _panelRect.Y, _panelRect.Width, _panelRect.Height);
            TopMost = _cfg.AlwaysOnTop;

            base.Show();
            _visible = true;

            foreach (SlotState s in _slots)
            {
                if (s.Hwnd != IntPtr.Zero && Native.IsWindow(s.Hwnd) && s.HiddenByUs)
                {
                    Native.ShowWindow(s.Hwnd, Native.SW_SHOWNOACTIVATE);
                    s.HiddenByUs = false;
                }
            }
            foreach (SlotState s in _slots)
            {
                if (s.Hwnd == IntPtr.Zero || !Native.IsWindow(s.Hwnd)) continue;
                if (Native.IsWindowVisible(s.Hwnd)) continue;
                // 应用自己隐藏了窗口（会议中、收起到托盘等）。打开工具栏时立即尝试恢复，
                // 否则槽位只剩深色背景，看起来就是黑屏；仍受限流，避免与应用反复拉锯。
                if (unchecked(Environment.TickCount - s.LastForceShowTick) <= 30000) continue;
                s.LastForceShowTick = Environment.TickCount;
                s.RestoreGraceUntil = Environment.TickCount + 1500;
                Native.ShowWindow(s.Hwnd, Native.SW_SHOWNOACTIVATE);
            }
            LayoutDocked(true);
            // 槽位窗口显示后立即刷新窗口区域挖孔；否则区域要等到 150ms 的弹层扫描才更新，
            // 这期间不透明的工具栏背景会盖住槽位，看起来就是黑屏。
            UpdateWindowRegion();
            RestoreHiddenPopups();
            RaisePopupsAboveDocked();
            _taskList.Invalidate();
            Invalidate();
            YswLog("ShowDock done vis=" + Native.IsWindowVisible(Handle) + " rect=" + Bounds);
        }

        public void HideDock()
        {
            using (UiHangMonitor.Track("HideDock")) HideDockCore();
        }

        private void HideDockCore()
        {
            if (!_visible) return;
            YswLog("HideDock");

            IntPtr fg = Native.GetForegroundWindow();
            TrackPopupWindows();
            bool fgInDock = (fg == Handle) || IsDockedWindow(fg);
            HidePopups();

            foreach (SlotState s in _slots)
            {
                if (s.Hwnd == IntPtr.Zero || !Native.IsWindow(s.Hwnd)) continue;
                if (s.HiddenByUs) continue;
                if (!Native.IsWindowVisible(s.Hwnd)) continue;
                if (Native.IsIconic(s.Hwnd)) continue;
                Native.ShowWindow(s.Hwnd, Native.SW_HIDE);
                s.HiddenByUs = true;
            }

            Hide();
            _visible = false;
            _leaveMs = 0;
            _edgeDwellMs = 0;

            if (fgInDock && _preShowForeground != IntPtr.Zero && Native.IsWindow(_preShowForeground))
                Native.SetForegroundWindow(_preShowForeground);
            _preShowForeground = IntPtr.Zero;
        }

        public void ExitApp()
        {
            if (_exiting) return;
            _exiting = true;
            Close();
        }

        private static void YswLog(string msg)
        {
            try
            {
                string path = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(Application.ExecutablePath), "WindowDock_dbg.log");
                System.IO.File.AppendAllText(path,
                    DateTime.Now.ToString("HH:mm:ss.fff") + " [ysw] " + msg + "\r\n");
            }
            catch (Exception)
            {
            }
        }

        private void UiTick(object sender, EventArgs e)
        {
            try
            {
                using (UiHangMonitor.Track("UiTick")) UiTickCore();
            }
            catch (Exception ex)
            {
                string key = ex.GetType().Name + " " + ex.Message;
                if (key != _lastTickError)
                {
                    _lastTickError = key;
                    YswLog("UiTick ex: " + key);
                }
            }
        }

        private string _lastTickError = "";

        private bool IsEditingTasks
        {
            get
            {
                if (!_visible) return false;
                if (_taskInput != null && _taskInput.IsComposing) return true;
                return Native.GetForegroundWindow() == Handle &&
                    ((_taskInput != null && _taskInput.ContainsFocus) ||
                     (_taskDeadline != null && _taskDeadline.ContainsFocus) ||
                     (_taskList != null && _taskList.ContainsFocus));
            }
        }

        private void UiTickCore()
        {
            if (_exiting || !_initialized) return;

            // 跨线程的 ShowWindow 是异步生效的，槽位真正可见前算出的挖孔区域会漏掉它，
            // 工具栏背景就会一直盖在槽位上（黑屏）。每帧按签名自检并修复，50ms 内收敛。
            // 必须放在下面的早退分支之前：编辑任务或弹出对话框时 UiTick 会提前返回，
            // 若恰好在恢复缓冲期结束后走到这里，区域会一直停在不挖孔的状态。
            if (_visible) UpdateWindowRegion();

            if (HasManagedDialog)
            {
                EnsureManagedDialogAboveWindows();
                _leaveMs = 0;
                return;
            }

            _popupScanMs += _uiTimer.Interval;
            bool scanPopups = _popupScanMs >= 150;
            if (scanPopups) _popupScanMs = 0;

            if (IsEditingTasks)
            {
                _leaveMs = 0;
                _edgeDwellMs = 0;
                return;
            }
            if (scanPopups) TrackPopupWindows();

            Point p = Cursor.Position;

            if (!_visible)
            {
                if (EdgeHit(p))
                {
                    _edgeDwellMs += _uiTimer.Interval;
                    if (_edgeDwellMs >= 120)
                    {
                        _edgeDwellMs = 0;
                        YswLog("edge hit -> ShowDock p=" + p.X + "," + p.Y);
                        ShowDock();
                    }
                }
                else
                {
                    _edgeDwellMs = 0;
                }
                return;
            }

            _edgeDwellMs = 0;
            QuickRestoreIconic();
            TrackDockedDrag();
            EnsureDockedAboveBar();
            RaisePopupsAboveDocked();
            ClearUnreadForForeground(Native.GetForegroundWindow());

            if (!_cfg.AutoHide || _suspendAutoHide)
            {
                _leaveMs = 0;
                return;
            }

            bool inside = _panelRect.Contains(p);
            if (!inside) inside = IsPointInPopup(p);
            IntPtr fg = Native.GetForegroundWindow();
            bool keepOpen = inside || (fg == Handle) || IsDockedWindow(fg) || IsOwnWindow(fg);

            if (keepOpen)
            {
                _leaveMs = 0;
                return;
            }

            _leaveMs += _uiTimer.Interval;
            if (_leaveMs >= _cfg.AutoHideDelayMs)
            {
                _leaveMs = 0;
                HideDock();
            }
        }

        private void QuickRestoreIconic()
        {
            for (int i = 0; i < _slots.Count && i < _slotRects.Length; i++)
            {
                SlotState s = _slots[i];
                if (s.Hwnd == IntPtr.Zero || !Native.IsWindow(s.Hwnd)) continue;
                if (!Native.IsWindowVisible(s.Hwnd) || !Native.IsIconic(s.Hwnd)) continue;

                Native.ShowWindow(s.Hwnd, Native.SW_RESTORE);
                Rectangle r = _slotRects[i];
                Native.SetWindowPos(s.Hwnd, IntPtr.Zero, r.X, r.Y, r.Width, r.Height,
                    Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
                RefreshSlotWindow(i);
            }
        }

        private int SlotIndexAt(Point p)
        {
            for (int i = 0; i < _slotRects.Length; i++)
            {
                if (_slotRects[i].Contains(p)) return i;
            }
            return -1;
        }

        private void TrackDockedDrag()
        {
            bool down = (Native.GetAsyncKeyState(Native.VK_LBUTTON) & 0x8000) != 0;

            if (down)
            {
                if (_dragSlot >= 0) return;

                IntPtr fg = Native.GetForegroundWindow();
                if (fg == IntPtr.Zero || fg == Handle) return;

                for (int i = 0; i < _slots.Count && i < _slotRects.Length; i++)
                {
                    SlotState s = _slots[i];
                    if (s.Hwnd == IntPtr.Zero || s.Hwnd != fg) continue;
                    if (!Native.IsWindowVisible(s.Hwnd) || Native.IsIconic(s.Hwnd) || Native.IsZoomed(s.Hwnd))
                        return;

                    Rectangle cur = Native.GetWindowRectangle(s.Hwnd);
                    Rectangle tgt = _slotRects[i];
                    int t = S(30);
                    if (Math.Abs(cur.X - tgt.X) > t || Math.Abs(cur.Y - tgt.Y) > t ||
                        Math.Abs(cur.Width - tgt.Width) > t || Math.Abs(cur.Height - tgt.Height) > t)
                    {
                        _dragSlot = i;
                    }
                    return;
                }
                return;
            }

            if (_dragSlot < 0) return;

            int src = _dragSlot;
            _dragSlot = -1;
            int target = SlotIndexAt(Cursor.Position);
            if (target >= 0 && target != src)
            {
                MoveSlot(src, target);
                return;
            }

            // 只是普通点击时窗口没有真正移动，避免无谓的重新布局和重绘造成闪黑。
            SlotState st = _slots[src];
            if (st.Hwnd == IntPtr.Zero || !Native.IsWindow(st.Hwnd)) return;
            Rectangle rcur = Native.GetWindowRectangle(st.Hwnd);
            Rectangle rtgt = _slotRects[src];
            int tol = S(30);
            if (Math.Abs(rcur.X - rtgt.X) > tol || Math.Abs(rcur.Y - rtgt.Y) > tol ||
                Math.Abs(rcur.Width - rtgt.Width) > tol || Math.Abs(rcur.Height - rtgt.Height) > tol)
                RelayoutVisible();
        }

        private void EnforceTick(object sender, EventArgs e)
        {
            using (UiHangMonitor.Track("EnforceTick")) EnforceTickCore();
        }

        private void EnforceTickCore()
        {
            if (_exiting) return;
            if (_visible && _cfg.TasksVisible) _taskList.Invalidate();
            if (HasManagedDialog) { EnsureManagedDialogAboveWindows(); return; }
            if (IsEditingTasks) return;

            bool changed = false;
            foreach (SlotState s in _slots)
            {
                if (s.Hwnd != IntPtr.Zero && !Native.IsWindow(s.Hwnd))
                {
                    ReleasePopups(s.Hwnd);
                    RestoreWindowOpacity(s.Hwnd);
                    s.ClearBinding();
                    changed = true;
                }
            }
            if (changed)
            {
                SaveConfig();
                UpdateWindowRegion();
                Invalidate();
            }

            _rebindCounter++;
            if (_rebindCounter >= 3)
            {
                _rebindCounter = 0;
                foreach (SlotState s in _slots)
                {
                    if (s.Hwnd == IntPtr.Zero && s.ProcessName.Length > 0)
                    {
                        AutoBindSlots();
                        break;
                    }
                }
            }

            if (!_visible)
            {
                // 应用自己重新显示了窗口（典型场景：收到新消息时弹出提醒窗口），
                // 跟随应用把停靠栏显示出来，避免提醒窗口孤立地浮在桌面或根本看不到。
                IntPtr fg = Native.GetForegroundWindow();
                foreach (SlotState s in _slots)
                {
                    if (s.Hwnd == IntPtr.Zero || !s.HiddenByUs) continue;
                    if (!Native.IsWindow(s.Hwnd) || !Native.IsWindowVisible(s.Hwnd)) continue;
                    // 只接管应用到前台的显示行为；后台周期性自显不应反复拉起停靠栏。
                    if (fg != IntPtr.Zero && Native.GetAncestor(fg, Native.GA_ROOT) != s.Hwnd) continue;
                    YswLog("app self-show hwnd=" + s.Hwnd + " -> ShowDock");
                    ShowDock();
                    return;
                }
                return;
            }

            if (_cfg.AllowSlotExpand && unchecked(Environment.TickCount - _lastLayoutTick) >= 500 &&
                MeasureSlotMinimums(true))
                RelayoutVisible();

            for (int i = 0; i < _slots.Count && i < _slotRects.Length; i++)
            {
                SlotState s = _slots[i];
                if (s.Hwnd == IntPtr.Zero || !Native.IsWindow(s.Hwnd)) continue;
                IntPtr h = s.Hwnd;

                if (s.HiddenByUs)
                {
                    Native.ShowWindow(h, Native.SW_SHOWNOACTIVATE);
                    s.HiddenByUs = false;
                }
                if (!Native.IsWindowVisible(h))
                {
                    if (!Native.IsWindowResponsive(h)) continue;
                    // 应用自行隐藏了绑定的窗口（关闭到托盘、临时弹层切换等）。先尝试重新
                    // 显示并放回槽位，避免槽位只剩工具栏背景（看起来就是黑屏）；仍不可见
                    // 时再改绑到同进程的可见主窗口。
                    s.InvisibleTicks++;
                    // 30 秒内只强制显示一次，避免与应用“关闭到托盘”的隐藏行为反复拉锯。
                    if (s.InvisibleTicks == 1 &&
                        unchecked(Environment.TickCount - s.LastForceShowTick) > 30000)
                    {
                        s.LastForceShowTick = Environment.TickCount;
                        s.RestoreGraceUntil = Environment.TickCount + 1500;
                        Rectangle r = _slotRects[i];
                        Native.ShowWindow(h, Native.SW_SHOWNOACTIVATE);
                        Native.SetWindowPos(h, IntPtr.Zero, r.X, r.Y, r.Width, r.Height,
                            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
                        UpdateWindowRegion();
                    }
                    if (s.InvisibleTicks >= 2) TryRebindInvisibleSlot(i, s);
                    continue;
                }
                if (s.InvisibleTicks > 0)
                {
                    s.InvisibleTicks = 0;
                    UpdateWindowRegion();
                }
                if (i == _dragSlot) continue;
                if (Native.IsIconic(h))
                {
                    Native.ShowWindow(h, Native.SW_RESTORE);
                    Native.SetWindowPos(h, IntPtr.Zero, _slotRects[i].X, _slotRects[i].Y,
                        _slotRects[i].Width, _slotRects[i].Height,
                        Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
                    continue;
                }

                if (Native.IsZoomed(h))
                {
                    Native.ShowWindow(h, Native.SW_RESTORE);
                    Native.SetWindowPos(h, IntPtr.Zero, _slotRects[i].X, _slotRects[i].Y, _slotRects[i].Width, _slotRects[i].Height,
                        Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
                    continue;
                }

                Rectangle cur = Native.GetWindowRectangle(h);
                Rectangle target = _slotRects[i];
                if (cur != target)
                {
                    Native.SetWindowPos(h, IntPtr.Zero, target.X, target.Y, target.Width, target.Height,
                        Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
                }
            }
        }

        private void TryRebindInvisibleSlot(int index, SlotState s)
        {
            s.InvisibleTicks = 0;
            if (Native.IsWindowVisible(s.Hwnd)) return;
            foreach (PopupState popup in _popups.Values)
                if (popup.DockedHwnd == s.Hwnd && popup.IsValid && Native.IsWindowVisible(popup.Hwnd)) return;

            List<WindowInfo> wins = Native.GetTopLevelWindows(Process.GetCurrentProcess().Id);
            HashSet<IntPtr> used = new HashSet<IntPtr>();
            foreach (SlotState other in _slots)
            {
                if (other.Hwnd != IntPtr.Zero) used.Add(other.Hwnd);
            }
            foreach (PopupState popup in _popups.Values) used.Add(popup.Hwnd);

            WindowInfo best = SelectBestWindow(wins, s.ProcessName, s.TitleHint, used);
            if (best == null) return;

            // 旧窗口已被应用隐藏，不再恢复其原位置，直接改绑到可见窗口。
            ReleasePopups(s.Hwnd);
            RestoreWindowOpacity(s.Hwnd);
            s.Hwnd = IntPtr.Zero;
            BindSlot(index, best.Handle);
        }

        private Rectangle GetWorkArea()
        {
            Screen[] screens = Screen.AllScreens;
            int idx = _cfg.MonitorIndex;
            if (idx < 0 || idx >= screens.Length) idx = 0;
            return screens[idx].WorkingArea;
        }

        private int S(int v)
        {
            return (int)Math.Round(v * _scale);
        }

        private int SlotMinWAt(int i)
        {
            if (i < 0 || i >= _slotMinW.Length) return 0;
            return _slotMinW[i];
        }

        private void SetSlotMinW(int i, int value)
        {
            if (i < 0 || i >= _slotMinW.Length) return;
            _slotMinW[i] = Math.Max(0, value);
        }

        private void ResetSlotMeasurements()
        {
            _slotMinW = new int[_slots.Count];
            _slotMinHeights = new int[_slots.Count];
            _slotMinH = 0;
        }

        private void ComputeLayout()
        {
            Rectangle wa = GetWorkArea();
            _scale = Native.GetScaleForPoint(new Point(wa.Left + wa.Width / 2, wa.Top + wa.Height / 2));
            if (_cfg.LayoutMode == "Grid") { ComputeGridLayout(wa); return; }
            int rail = S(RAIL);
            int margin = S(MARGIN);
            int gap = S(GAP);
            int n = _slots.Count;
            string edge = _cfg.Edge;
            bool vertical = edge == "Left" || edge == "Right";
            int headerH = _cfg.TasksVisible ? S(HEADER_H) : 0;
            int headerGap = headerH > 0 ? gap : 0;
            int floorW = S(160);

            int nominalW = S(_cfg.SlotWidth);
            int availW = vertical ? wa.Width - rail - 2 * margin : wa.Width - 2 * margin;
            int gapTotal = n > 1 ? (n - 1) * gap : 0;

            int maxContent = availW - gapTotal;
            if (maxContent < floorW) maxContent = floorW;

            int[] widths = new int[n];
            int total = 0;
            for (int i = 0; i < n; i++)
            {
                widths[i] = Math.Max(nominalW, SlotMinWAt(i));
                total += widths[i];
            }

            if (n > 0 && total > maxContent)
            {
                int slackTotal = 0;
                for (int i = 0; i < n; i++)
                {
                    int minW = Math.Max(floorW, SlotMinWAt(i));
                    if (widths[i] > minW) slackTotal += widths[i] - minW;
                }
                int need = total - maxContent;
                if (slackTotal > 0)
                {
                    int cut = Math.Min(slackTotal, need);
                    int used = 0;
                    for (int i = 0; i < n; i++)
                    {
                        int minW = Math.Max(floorW, SlotMinWAt(i));
                        int slack = widths[i] - minW;
                        if (slack <= 0) continue;
                        int c = (int)((long)slack * cut / slackTotal);
                        widths[i] -= c;
                        used += c;
                    }
                    int rem = cut - used;
                    int guard = 0;
                    while (rem > 0 && guard < n * 8)
                    {
                        for (int i = 0; i < n && rem > 0; i++)
                        {
                            int minW = Math.Max(floorW, SlotMinWAt(i));
                            if (widths[i] > minW) { widths[i]--; rem--; }
                        }
                        guard++;
                    }
                    total = 0;
                    for (int i = 0; i < n; i++) total += widths[i];
                }

                if (total > maxContent)
                {
                    int scaled = 0;
                    for (int i = 0; i < n; i++)
                    {
                        widths[i] = Math.Max(S(80), (int)((long)widths[i] * maxContent / total));
                        scaled += widths[i];
                    }
                    int diff = maxContent - scaled;
                    for (int i = 0; i < n && diff > 0; i++) { widths[i]++; diff--; }
                    total = 0;
                    for (int i = 0; i < n; i++) total += widths[i];
                }
            }

            int contentW = n > 0 ? total + gapTotal : (headerH > 0 ? S(420) : S(280));

            int availH = wa.Height - 2 * margin - headerH - headerGap - S(24);
            if (!vertical) availH -= rail;
            int slotH = S(_cfg.SlotHeight);
            if (slotH > availH) slotH = availH;
            if (slotH < S(160)) slotH = availH >= S(160) ? S(160) : Math.Max(S(80), availH);
            if (_cfg.AllowSlotExpand && _slotMinH > slotH) slotH = _slotMinH;
            if (slotH > availH) slotH = Math.Max(S(80), availH);

            int panelW = (vertical ? rail + 2 * margin : 2 * margin) + contentW;
            if (panelW > wa.Width) panelW = wa.Width;
            int panelH = (vertical ? 2 * margin : rail + 2 * margin) + headerH + headerGap + slotH;
            if (panelH > wa.Height) panelH = wa.Height;

            int px;
            int py;
            if (vertical)
            {
                px = edge == "Right" ? wa.Right - panelW : wa.Left;
                py = wa.Top + (wa.Height - panelH) / 2;
            }
            else
            {
                px = wa.Left + Math.Max(0, (wa.Width - panelW) / 2);
                py = edge == "Top" ? wa.Top : wa.Bottom - panelH;
            }

            _panelRect = new Rectangle(px, py, panelW, panelH);


            int contentX;
            int contentY;
            if (vertical)
            {
                contentX = edge == "Right" ? wa.Right - rail - margin - contentW : wa.Left + rail + margin;
                contentY = py + margin;
                _railRect = edge == "Right"
                    ? new Rectangle(wa.Right - rail, py, rail, panelH)
                    : new Rectangle(wa.Left, py, rail, panelH);
            }
            else
            {
                contentX = px + margin;
                contentY = py + (edge == "Top" ? rail + margin : margin);
                _railRect = edge == "Top"
                    ? new Rectangle(px, py, panelW, rail)
                    : new Rectangle(px, py + panelH - rail, panelW, rail);
            }

            if (headerH > 0)
                _headerRect = new Rectangle(contentX, contentY, contentW, headerH);
            else
                _headerRect = Rectangle.Empty;

            int slotY = contentY + headerH + headerGap;
            _slotRects = new Rectangle[n];
            int cx = contentX;
            for (int i = 0; i < n; i++)
            {
                if (vertical && edge == "Right")
                {
                    int right = contentX + contentW;
                    for (int k = 0; k <= i; k++)
                        right -= widths[k] + (k < i ? gap : 0);
                    _slotRects[i] = new Rectangle(right, slotY, widths[i], slotH);
                }
                else
                {
                    _slotRects[i] = new Rectangle(cx, slotY, widths[i], slotH);
                    cx += widths[i] + gap;
                }
            }

            BuildButtons();
            UpdateTaskControlsBounds();
        }

        private void UpdateTaskControlsBounds()
        {
            if (_taskList == null || _taskInput == null) return;
            if (_headerRect.Width <= 0 || !_cfg.TasksVisible)
            {
                _taskList.Visible = false;
                _taskInput.Visible = false;
                _taskDeadline.Visible = false;
                _taskSave.Visible = false;
                return;
            }

            Rectangle hr = new Rectangle(_headerRect.X - _panelRect.X, _headerRect.Y - _panelRect.Y,
                _headerRect.Width, _headerRect.Height);
            int pad = S(10);
            int titleH = S(26);
            int inputH = S(28);
            int deadlineY = hr.Bottom - pad - inputH;
            int inputY = deadlineY - S(6) - inputH;
            int listY = hr.Y + titleH;
            int listH = inputY - S(6) - listY;
            if (listH < S(24)) listH = S(24);

            _taskList.Visible = true;
            _taskInput.Visible = true;
            _taskDeadline.Visible = true;
            _taskSave.Visible = true;
            _taskList.SetBounds(hr.X + pad, listY, Math.Max(S(40), hr.Width - 2 * pad), listH);
            int saveW = S(60);
            _taskInput.SetBounds(hr.X + pad, inputY, Math.Max(S(40), hr.Width - 2 * pad - saveW - S(6)), inputH);
            _taskSave.SetBounds(hr.Right - pad - saveW, inputY, saveW, inputH);
            _taskDeadline.CustomFormat = hr.Width >= S(280) ? "'截止 'yyyy-MM-dd HH:mm" : "yy-MM-dd HH:mm";
            _taskDeadline.SetBounds(hr.X + pad, deadlineY, Math.Max(S(40), hr.Width - 2 * pad), inputH);
        }

        private Rectangle GetHiddenRect()
        {
            Rectangle wa = GetWorkArea();
            Rectangle p = _panelRect;
            switch (_cfg.Edge)
            {
                case "Left":
                    return new Rectangle(wa.Left - p.Width, p.Top, p.Width, p.Height);
                case "Top":
                    return new Rectangle(p.Left, wa.Top - p.Height, p.Width, p.Height);
                case "Bottom":
                    return new Rectangle(p.Left, wa.Bottom, p.Width, p.Height);
                default:
                    return new Rectangle(wa.Right, p.Top, p.Width, p.Height);
            }
        }

        private bool EdgeHit(Point p)
        {
            Rectangle wa = GetWorkArea();
            int pad = S(40);
            int depth = Math.Max(3, S(4));
            switch (_cfg.Edge)
            {
                case "Left":
                    return p.X <= wa.Left + depth &&
                           p.Y >= _panelRect.Top - pad && p.Y <= _panelRect.Bottom + pad;
                case "Top":
                    return p.Y <= wa.Top + depth &&
                           p.X >= _panelRect.Left - pad && p.X <= _panelRect.Right + pad;
                case "Bottom":
                    return p.Y >= wa.Bottom - depth &&
                           p.X >= _panelRect.Left - pad && p.X <= _panelRect.Right + pad;
                default:
                    return p.X >= wa.Right - depth &&
                           p.Y >= _panelRect.Top - pad && p.Y <= _panelRect.Bottom + pad;
            }
        }

        public void AutoBindSlots()
        {
            List<WindowInfo> wins = Native.GetTopLevelWindows(Process.GetCurrentProcess().Id);
            HashSet<IntPtr> used = new HashSet<IntPtr>();
            foreach (SlotState s in _slots)
            {
                if (s.Hwnd != IntPtr.Zero) used.Add(s.Hwnd);
            }
            foreach (PopupState popup in _popups.Values) used.Add(popup.Hwnd);

            for (int i = 0; i < _slots.Count; i++)
            {
                SlotState s = _slots[i];
                if (s.Hwnd != IntPtr.Zero) continue;
                if (s.ProcessName.Length == 0) continue;

                WindowInfo best = SelectBestWindow(wins, s.ProcessName, s.TitleHint, used);
                if (best != null && BindSlot(i, best.Handle, false)) used.Add(best.Handle);
            }
        }

        // 同一进程常有多个同名顶层窗口（如 UME 的临时弹层），临时窗口被应用自身隐藏后
        // 槽位会变成空白。这里优先选非分层、面积更大的可见窗口作为主窗口。
        private static WindowInfo SelectBestWindow(List<WindowInfo> wins, string processName, string titleHint, HashSet<IntPtr> used)
        {
            WindowInfo best = null;
            int bestScore = -1;
            long bestArea = -1;
            bool bestLayered = true;

            foreach (WindowInfo w in wins)
            {
                if (used.Contains(w.Handle)) continue;
                if (!string.Equals(w.ProcessName, processName, StringComparison.OrdinalIgnoreCase)) continue;

                int score;
                if (titleHint.Length == 0)
                {
                    score = 2;
                }
                else if (string.Equals(w.Title, titleHint, StringComparison.OrdinalIgnoreCase))
                {
                    score = 3;
                }
                else if (w.Title.IndexOf(titleHint, StringComparison.OrdinalIgnoreCase) >= 0 ||
                         titleHint.IndexOf(w.Title, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    score = 2;
                }
                else
                {
                    score = 1;
                }

                Rectangle r = Native.GetWindowRectangle(w.Handle);
                long area = (long)r.Width * r.Height;
                bool layered = Native.IsLayeredWindow(w.Handle);

                bool better;
                if (score != bestScore) better = score > bestScore;
                else if (best == null) better = true;
                else if (layered != bestLayered) better = !layered;
                else better = area > bestArea;

                if (better)
                {
                    best = w;
                    bestScore = score;
                    bestArea = area;
                    bestLayered = layered;
                }
            }
            return best;
        }

        public bool BindSlot(int index, IntPtr hwnd)
        {
            return BindSlot(index, hwnd, true);
        }

        private bool BindSlot(int index, IntPtr hwnd, bool showError)
        {
            if (index < 0 || index >= _slots.Count) return false;
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return false;
            if (IsOwnWindow(hwnd)) return false;
            string gridError;
            if (_cfg.LayoutMode == "Grid" && !CanFitGrid(_cfg, index, hwnd, out gridError))
            {
                if (showError) MessageBox.Show(this, gridError, "WindowDock");
                return false;
            }

            foreach (SlotState bound in _slots)
                if (bound.Hwnd == hwnd && bound != _slots[index]) return false;
            PopupState trackedPopup;
            if (_popups.TryGetValue(hwnd, out trackedPopup)) ReleasePopups(trackedPopup.DockedHwnd);

            UnbindSlot(index, false);

            SlotState s = _slots[index];
            Native.WINDOWPLACEMENT wp = new Native.WINDOWPLACEMENT();
            wp.length = Marshal.SizeOf(typeof(Native.WINDOWPLACEMENT));
            bool hasPlacement = Native.GetWindowPlacement(hwnd, ref wp);

            if (Native.IsIconic(hwnd))
                Native.ShowWindow(hwnd, Native.SW_RESTORE);

            Rectangle rect = Native.GetWindowRectangle(hwnd);
            if (rect.Width <= 0 || rect.Height <= 0) return false;

            s.Hwnd = hwnd;
            s.OriginalRect = rect;
            s.OriginalVisible = Native.IsWindowVisible(hwnd);
            s.OriginalTopmost = (Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TOPMOST) != 0;
            s.OriginalPlacement = wp;
            s.OriginalPlacementValid = hasPlacement;
            s.HiddenByUs = false;

            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            s.ProcessName = Native.GetProcessName((int)pid);
            s.TitleHint = Native.GetWindowTitle(hwnd);
            CaptureExistingWindows(s);
            ApplyWindowOpacity(hwnd);
            SetSlotMinW(index, 0);
            _slotMinHeights[index] = 0;
            _slotMinH = 0;

            if (_visible)
                LayoutDocked(true);
            else if (Native.IsWindowVisible(hwnd))
            {
                TrackPopupWindows();
                Native.ShowWindow(hwnd, Native.SW_HIDE);
                s.HiddenByUs = true;
            }

            SaveConfig();
            Invalidate();
            return true;
        }

        public void UnbindSlot(int index, bool save)
        {
            if (index < 0 || index >= _slots.Count) return;
            SlotState s = _slots[index];
            bool hadUnread = s.Unread;
            RestoreSlotWindow(s);
            s.ClearBinding();
            SetSlotMinW(index, 0);
            _slotMinHeights[index] = 0;
            _slotMinH = 0;
            if (hadUnread)
            {
                UpdateTrayStatus();
                Invalidate();
            }
            if (save)
            {
                s.ProcessName = "";
                s.TitleHint = "";
                SaveConfig();
                Invalidate();
            }
        }

        private void RestoreSlotWindow(SlotState s)
        {
            IntPtr h = s.Hwnd;
            RestoreWindowOpacity(h);
            if (h == IntPtr.Zero || !Native.IsWindow(h))
            {
                ReleasePopups(h);
                return;
            }

            if (s.HiddenByUs)
            {
                Native.ShowWindow(h, Native.SW_SHOWNOACTIVATE);
                s.HiddenByUs = false;
            }

            if (!s.OriginalTopmost)
            {
                Native.SetWindowPos(h, Native.HWND_NOTOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }

            Rectangle target = s.OriginalRect;
            if (s.OriginalPlacementValid && s.OriginalPlacement.showCmd == Native.SW_SHOWMAXIMIZED)
                target = s.OriginalPlacement.rcNormalPosition.ToRectangle();

            if (target.Width > 0 && target.Height > 0)
            {
                Native.SetWindowPos(h, IntPtr.Zero, target.X, target.Y, target.Width, target.Height,
                    Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
            }

            if (s.OriginalPlacementValid)
            {
                int sc = s.OriginalPlacement.showCmd;
                if (sc == Native.SW_SHOWMAXIMIZED) Native.ShowWindow(h, Native.SW_SHOWMAXIMIZED);
                else if (sc == Native.SW_SHOWMINIMIZED) Native.ShowWindow(h, Native.SW_MINIMIZE);
                else if (!s.OriginalVisible) Native.ShowWindow(h, Native.SW_HIDE);
            }
            else if (!s.OriginalVisible)
            {
                Native.ShowWindow(h, Native.SW_HIDE);
            }
            ReleasePopups(h);
        }

        private void LayoutDocked(bool setZOrder)
        {
            if (!_layoutExpanding && _cfg.AllowSlotExpand && _visible && MeasureSlotMinimums(false))
            {
                _layoutExpanding = true;
                try
                {
                    ComputeLayout();
                    SetBounds(_panelRect.X, _panelRect.Y, _panelRect.Width, _panelRect.Height);
                }
                finally { _layoutExpanding = false; }
            }
            LayoutDockedCore(setZOrder);
            _lastLayoutTick = Environment.TickCount;
            UpdateWindowRegion();
            RepaintDocked();
        }

        private bool MeasureSlotMinimums(bool includeActual)
        {
            bool changed = false;
            for (int i = 0; i < _slots.Count && i < _slotRects.Length; i++)
            {
                IntPtr hwnd = _slots[i].Hwnd;
                if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd) || i == _dragSlot || Native.IsIconic(hwnd) ||
                    Native.IsZoomed(hwnd)) continue;
                Size minimum = Native.GetWindowMinimumSize(hwnd);
                // 异步调整后不能马上把旧尺寸当作应用最小尺寸；等窗口处理完消息再采样。
                if (includeActual && Native.IsWindowVisible(hwnd) && Native.IsWindowResponsive(hwnd))
                {
                    Rectangle actual = Native.GetWindowRectangle(hwnd);
                    minimum.Width = Math.Max(minimum.Width, actual.Width);
                    minimum.Height = Math.Max(minimum.Height, actual.Height);
                }
                if (minimum.Width > _slotRects[i].Width + S(2) && minimum.Width > SlotMinWAt(i))
                {
                    SetSlotMinW(i, minimum.Width);
                    changed = true;
                }
                if (minimum.Height > _slotRects[i].Height + S(2) && minimum.Height > _slotMinH)
                {
                    _slotMinH = minimum.Height;
                    changed = true;
                }
                if (_cfg.LayoutMode == "Grid" && minimum.Height > _slotRects[i].Height + S(2) &&
                    minimum.Height > _slotMinHeights[i])
                {
                    _slotMinHeights[i] = minimum.Height;
                    changed = true;
                }
            }
            return changed;
        }

        // 刚被强制显示的窗口需要一小段重绘时间；期间保持槽位由工具栏绘制，避免露出空白画面。
        private static bool IsRestoring(SlotState s)
        {
            return s.RestoreGraceUntil != 0 &&
                unchecked(s.RestoreGraceUntil - Environment.TickCount) > 0;
        }

        // 返回 true 表示槽位当前需要由工具栏自己绘制（窗口不可见或仍在恢复中）。
        private static bool SlotNeedsPlaceholder(SlotState s)
        {
            if (s.Hwnd == IntPtr.Zero || !Native.IsWindow(s.Hwnd)) return false;
            return !Native.IsWindowVisible(s.Hwnd) || Native.IsIconic(s.Hwnd) || IsRestoring(s);
        }

        private void UpdateWindowRegion()
        {
            // 把已停靠且可见的槽位从本窗体区域里挖掉。这样即使工具栏被激活抬到置顶带最上层，
            // 也无法再盖住槽位（黑屏的根因），窗口区域的命中测试同样会直接落到停靠窗口上。
            if (!IsHandleCreated || _panelRect.Width <= 0 || _panelRect.Height <= 0) return;
            StringBuilder sig = new StringBuilder();
            sig.Append(Width).Append('x').Append(Height).Append("|");
            for (int i = 0; i < _slots.Count && i < _slotRects.Length; i++)
            {
                SlotState s = _slots[i];
                Rectangle r = _slotRects[i];
                sig.Append(s.Hwnd).Append(':');
                if (s.Hwnd == IntPtr.Zero || !Native.IsWindow(s.Hwnd)) sig.Append('x');
                else if (SlotNeedsPlaceholder(s)) sig.Append('0');
                else sig.Append('1');
                sig.Append(':').Append(r.X).Append(',').Append(r.Y).Append(',')
                    .Append(r.Width).Append(',').Append(r.Height).Append(';');
            }
            string signature = sig.ToString();
            if (signature == _regionSignature) return;
            try
            {
                bool applied = false;
                int cuts = 0;
                using (Region region = new Region(new Rectangle(0, 0, Width, Height)))
                {
                    for (int i = 0; i < _slots.Count && i < _slotRects.Length; i++)
                    {
                        SlotState s = _slots[i];
                        if (s.Hwnd == IntPtr.Zero || !Native.IsWindow(s.Hwnd)) continue;
                        // 恢复缓冲期内保留工具栏绘制，等窗口重绘完成后再挖孔露出。
                        if (SlotNeedsPlaceholder(s)) continue;
                        Rectangle r = _slotRects[i];
                        region.Exclude(new Rectangle(r.X - _panelRect.X, r.Y - _panelRect.Y,
                            r.Width, r.Height));
                        cuts++;
                    }
                    using (Graphics g = CreateGraphics())
                    {
                        IntPtr hrgn = region.GetHrgn(g);
                        // SetWindowRgn 成功后由系统接管 HRGN，不能再删除。
                        if (Native.SetWindowRgn(Handle, hrgn, true) == 0)
                        {
                            Native.DeleteObject(hrgn);
                            YswLog("SetWindowRgn failed");
                        }
                        else
                        {
                            applied = true;
                        }
                    }
                }
                if (applied)
                {
                    _regionSignature = signature;
                    YswLog("region apply cuts=" + cuts + " sig=" + signature);
                }
            }
            catch (Exception)
            {
                _regionSignature = null;
            }
        }

        private void LayoutDockedCore(bool setZOrder)
        {
            if (HasManagedDialog) setZOrder = false;
            for (int i = 0; i < _slots.Count && i < _slotRects.Length; i++)
            {
                SlotState s = _slots[i];
                if (s.Hwnd == IntPtr.Zero || !Native.IsWindow(s.Hwnd)) continue;
                IntPtr h = s.Hwnd;

                if (s.HiddenByUs)
                {
                    Native.ShowWindow(h, Native.SW_SHOWNOACTIVATE);
                    s.HiddenByUs = false;
                }

                if (!Native.IsWindowVisible(h)) continue;
                if (Native.IsIconic(h)) Native.ShowWindow(h, Native.SW_RESTORE);
                if (Native.IsZoomed(h)) Native.ShowWindow(h, Native.SW_RESTORE);

                Rectangle r = _slotRects[i];
                uint flags = Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER;
                IntPtr after;

                if (!setZOrder)
                {
                    flags |= Native.SWP_NOZORDER;
                    after = IntPtr.Zero;
                }
                else if (_cfg.AlwaysOnTop)
                {
                    after = Native.HWND_TOPMOST;
                }
                else
                {
                    after = Native.HWND_TOP;
                }

                Native.SetWindowPos(h, after, r.X, r.Y, r.Width, r.Height, flags);
            }
            RaisePopupsAboveDocked();
        }

        private void RaiseDockedAboveBar()
        {
            if (!_visible || HasManagedDialog) return;
            IntPtr after = _cfg.AlwaysOnTop ? Native.HWND_TOPMOST : Native.HWND_TOP;
            for (int i = 0; i < _slots.Count; i++)
            {
                SlotState s = _slots[i];
                if (s.Hwnd == IntPtr.Zero || !Native.IsWindow(s.Hwnd)) continue;
                if (!Native.IsWindowVisible(s.Hwnd) || Native.IsIconic(s.Hwnd)) continue;
                Native.SetWindowPos(s.Hwnd, after, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
            }
            RaisePopupsAboveDocked();
        }

        private void EnsureDockedAboveBar()
        {
            if (HasManagedDialog) return;
            // 点击工具栏会让它在置顶带内被抬到停靠窗口之上，这个时序无法只靠 WM_ACTIVATE 保证。
            // 每 50ms 用 WindowFromPoint 检查槽位中心是否被本窗体盖住，被盖住才把该窗口重新抬高。
            for (int i = 0; i < _slots.Count && i < _slotRects.Length; i++)
            {
                SlotState s = _slots[i];
                if (s.Hwnd == IntPtr.Zero || !Native.IsWindow(s.Hwnd)) continue;
                if (!Native.IsWindowVisible(s.Hwnd) || Native.IsIconic(s.Hwnd)) continue;
                Rectangle r = _slotRects[i];
                Native.POINT p = new Native.POINT(r.X + r.Width / 2, r.Y + r.Height / 2);
                IntPtr hit = Native.WindowFromPoint(p);
                if (hit == IntPtr.Zero) continue;
                if (Native.GetAncestor(hit, Native.GA_ROOT) != Handle) continue;
                IntPtr after = _cfg.AlwaysOnTop ? Native.HWND_TOPMOST : Native.HWND_TOP;
                Native.SetWindowPos(s.Hwnd, after, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
            }
        }

        private void RepaintDocked()
        {
            for (int i = 0; i < _slots.Count; i++)
            {
                SlotState s = _slots[i];
                if (s.Hwnd == IntPtr.Zero || !Native.IsWindow(s.Hwnd)) continue;
                if (!Native.IsWindowVisible(s.Hwnd) || Native.IsIconic(s.Hwnd)) continue;
                // 不擦除背景，避免重绘瞬间出现黑色闪屏。
                try { Native.InvalidateRect(s.Hwnd, IntPtr.Zero, false); }
                catch (Exception) { }
            }
        }

        public void RefreshAllSlots()
        {
            for (int i = 0; i < _slots.Count; i++)
                RefreshSlotWindow(i);
        }

        public void RefreshSlotWindow(int index)
        {
            if (index < 0 || index >= _slots.Count) return;
            SlotState s = _slots[index];
            if (s.Hwnd == IntPtr.Zero || !Native.IsWindow(s.Hwnd)) return;
            try
            {
                Native.InvalidateRect(s.Hwnd, IntPtr.Zero, true);
                Rectangle r = Native.GetWindowRectangle(s.Hwnd);
                if (r.Width > S(4) && r.Height > S(4))
                {
                    Native.SetWindowPos(s.Hwnd, IntPtr.Zero, r.X, r.Y, r.Width - 1, r.Height,
                        Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
                    Native.SetWindowPos(s.Hwnd, IntPtr.Zero, r.X, r.Y, r.Width, r.Height,
                        Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
                }
            }
            catch (Exception)
            {
            }
        }

        private void ApplyAlwaysOnTop()
        {
            TopMost = _cfg.AlwaysOnTop;
            if (_cfg.AlwaysOnTop)
            {
                LayoutDocked(true);
            }
            else
            {
                foreach (SlotState s in _slots)
                {
                    if (s.Hwnd == IntPtr.Zero || !Native.IsWindow(s.Hwnd)) continue;
                    if (!s.OriginalTopmost)
                    {
                        Native.SetWindowPos(s.Hwnd, Native.HWND_NOTOPMOST, 0, 0, 0, 0,
                            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                    }
                }
            }
            ApplyPopupTopmost();
        }

        public bool AddSlot()
        {
            if (_slots.Count >= SlotCapacity) return false;
            _slots.Add(new SlotState());
            ResetSlotMeasurements();
            RelayoutVisible();
            SaveConfig();
            return true;
        }

        public bool AddSlotAt(int index)
        {
            if (_slots.Count >= SlotCapacity) return false;
            if (index < 0 || index > _slots.Count) index = _slots.Count;
            _slots.Insert(index, new SlotState());
            ResetSlotMeasurements();
            RelayoutVisible();
            SaveConfig();
            return true;
        }

        public void RemoveSlot(int index)
        {
            if (index < 0 || index >= _slots.Count) return;
            UnbindSlot(index, false);
            _slots.RemoveAt(index);
            ResetSlotMeasurements();
            RelayoutVisible();
            SaveConfig();
        }

        public void MoveSlot(int from, int to)
        {
            if (from < 0 || from >= _slots.Count) return;
            if (to < 0 || to >= _slots.Count || to == from) return;

            SlotState s = _slots[from];
            _slots.RemoveAt(from);
            _slots.Insert(to, s);
            ResetSlotMeasurements();
            RelayoutVisible();
            SaveConfig();
        }

        private void RelayoutVisible()
        {
            _relayoutPending = false;
            Screen[] screens = Screen.AllScreens;
            if (screens.Length > 0 && (_cfg.MonitorIndex < 0 || _cfg.MonitorIndex >= screens.Length))
            {
                _cfg.MonitorIndex = 0;
                SaveConfig();
            }
            ComputeLayout();
            if (_visible)
            {
                SetBounds(_panelRect.X, _panelRect.Y, _panelRect.Width, _panelRect.Height);
                LayoutDocked(true);
            }
            Invalidate();
        }

        // 分辨率/DPI/显示器变化可能连续触发多条系统消息，合并成一次重排。
        private void ScheduleRelayout(string reason)
        {
            if (!IsHandleCreated || _relayoutPending) return;
            _relayoutPending = true;
            YswLog("display change -> relayout: " + reason);
            try
            {
                BeginInvoke(new MethodInvoker(RelayoutVisible));
            }
            catch (Exception)
            {
                _relayoutPending = false;
            }
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            if (!IsHandleCreated || IsDisposed) return;
            try { BeginInvoke(new MethodInvoker(delegate { ScheduleRelayout("SystemEvents"); })); }
            catch (Exception) { }
        }

        public void ApplySettings(Settings newCfg)
        {
            string gridError;
            if (newCfg.LayoutMode == "Grid" && !CanFitGrid(newCfg, -1, IntPtr.Zero, out gridError))
            {
                MessageBox.Show(this, gridError, "WindowDock");
                return;
            }
            if (newCfg.StartWithWindows != _cfg.StartWithWindows)
            {
                try { StartupRegistration.SetEnabled(newCfg.StartWithWindows); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "开机启动设置失败：" + ex.Message, "WindowDock");
                    return;
                }
            }
            int oldMods = _cfg.HotkeyMods;
            int oldKey = _cfg.HotkeyKey;
            bool oldTopMost = _cfg.AlwaysOnTop;

            _cfg.StartWithWindows = newCfg.StartWithWindows;
            _cfg.LayoutMode = newCfg.LayoutMode;
            _cfg.GridRows = newCfg.GridRows;
            _cfg.GridColumns = newCfg.GridColumns;
            if (_cfg.LayoutMode == "Grid")
                while (_slots.Count < SlotCapacity) _slots.Add(new SlotState());

            ResetSlotMeasurements();

            _cfg.Edge = newCfg.Edge;
            _cfg.MonitorIndex = newCfg.MonitorIndex;
            _cfg.SlotWidth = newCfg.SlotWidth;
            _cfg.SlotHeight = newCfg.SlotHeight;
            _cfg.AutoHide = newCfg.AutoHide;
            _cfg.AutoHideDelayMs = newCfg.AutoHideDelayMs;
            _cfg.AlwaysOnTop = newCfg.AlwaysOnTop;
            _cfg.HotkeyMods = newCfg.HotkeyMods;
            _cfg.HotkeyKey = newCfg.HotkeyKey;
            _cfg.AllowSlotExpand = newCfg.AllowSlotExpand;
            _cfg.OpacityPercent = newCfg.OpacityPercent;
            _cfg.BackgroundColorArgb = newCfg.BackgroundColorArgb;
            _cfg.DeadlineReminderHours = newCfg.DeadlineReminderHours;
            ApplyAppearance();
            if (_cfg.TasksVisible != newCfg.TasksVisible)
            {
                _cfg.TasksVisible = newCfg.TasksVisible;
                ApplyTasksVisibility();
            }

            if (_hotkeyRegistered)
            {
                Native.UnregisterHotKey(Handle, HOTKEY_ID);
                _hotkeyRegistered = false;
            }
            if (!RegisterHotkey(true))
            {
                _cfg.HotkeyMods = oldMods;
                _cfg.HotkeyKey = oldKey;
                RegisterHotkey(false);
                MessageBox.Show(this, "快捷键注册失败，可能已被其他程序占用。\n已恢复为原来的快捷键。",
                    "WindowDock", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            if (oldTopMost != _cfg.AlwaysOnTop || _visible)
                ApplyAlwaysOnTop();

            RelayoutVisible();
            SaveConfig();
        }

        private bool RegisterHotkey(bool showError)
        {
            if (_hotkeyRegistered)
            {
                Native.UnregisterHotKey(Handle, HOTKEY_ID);
                _hotkeyRegistered = false;
            }
            if (_cfg.HotkeyKey < 1) return true;

            _hotkeyRegistered = Native.RegisterHotKey(Handle, HOTKEY_ID,
                (uint)(_cfg.HotkeyMods | Native.MOD_NOREPEAT), (uint)_cfg.HotkeyKey);

            if (!_hotkeyRegistered && showError)
            {
                MessageBox.Show(this, "快捷键注册失败，可能已被其他程序占用。",
                    "WindowDock", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return _hotkeyRegistered;
        }

        private void SyncConfigSlots()
        {
            _cfg.Slots.Clear();
            foreach (SlotState s in _slots)
            {
                SlotConfig sc = new SlotConfig();
                sc.ProcessName = s.ProcessName;
                sc.TitleHint = s.TitleHint;
                _cfg.Slots.Add(sc);
            }
        }

        private void SaveConfig()
        {
            try
            {
                SyncConfigSlots();
                using (UiHangMonitor.Track("SaveConfig")) _cfg.Save();
            }
            catch (Exception)
            {
            }
        }

        private bool IsOwnWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            return (int)pid == Process.GetCurrentProcess().Id;
        }

        private bool IsDockedWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            IntPtr windowRoot = Native.GetAncestor(hwnd, Native.GA_ROOT);
            PopupState popup;
            if (_popups.TryGetValue(windowRoot, out popup) && popup.IsValid) return true;
            foreach (SlotState s in _slots)
            {
                if (s.Hwnd == IntPtr.Zero) continue;
                if (s.Hwnd == hwnd) return true;
                IntPtr root = Native.GetAncestor(hwnd, Native.GA_ROOTOWNER);
                if (root == s.Hwnd) return true;
            }
            return false;
        }

        private bool HasManagedDialog
        {
            get { return _activeDialog != null && !_activeDialog.IsDisposed; }
        }

        private void EnsureManagedDialogAboveWindows()
        {
            Form dialog = _activeDialog;
            if (dialog == null || dialog.IsDisposed || !dialog.Visible || !dialog.IsHandleCreated) return;
            HashSet<IntPtr> seen = new HashSet<IntPtr>();
            for (IntPtr above = Native.GetWindow(dialog.Handle, Native.GW_HWNDPREV);
                above != IntPtr.Zero && seen.Add(above); above = Native.GetWindow(above, Native.GW_HWNDPREV))
            {
                if (Native.IsWindowVisible(above) && (above == Handle || IsDockedWindow(above)))
                {
                    Native.SetWindowPos(dialog.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                        Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
                    break;
                }
            }
        }

        internal DialogResult ShowManagedDialog(Form dialog)
        {
            Form previousDialog = _activeDialog;
            bool previousSuspend = _suspendAutoHide;
            _activeDialog = dialog;
            _suspendAutoHide = true;
            // 即使全局关闭置顶，应用原本置顶的窗口也不能盖住自己的设置对话框。
            dialog.TopMost = true;
            EventHandler shown = delegate
            {
                Native.SetWindowPos(dialog.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
                dialog.Activate();
                YswLog("managed dialog shown: " + dialog.Text);
            };
            dialog.Shown += shown;
            try
            {
                return dialog.ShowDialog(previousDialog != null && !previousDialog.IsDisposed ? previousDialog : this);
            }
            finally
            {
                dialog.Shown -= shown;
                _activeDialog = previousDialog;
                _suspendAutoHide = previousSuspend;
                _leaveMs = 0;
                if (HasManagedDialog) EnsureManagedDialogAboveWindows();
                else if (_visible) RaiseDockedAboveBar();
            }
        }

        public void OpenSlotsDialog()
        {
            using (SlotsForm f = new SlotsForm(this)) ShowManagedDialog(f);
            Invalidate();
        }

        private void OpenSettingsDialog()
        {
            using (SettingsForm f = new SettingsForm(_cfg))
                if (ShowManagedDialog(f) == DialogResult.OK) ApplySettings(f.Result);
        }

        private void OpenPickerForSlot(int index)
        {
            using (PickerForm f = new PickerForm())
            {
                if (ShowManagedDialog(f) == DialogResult.OK && f.SelectedHandle != IntPtr.Zero)
                    BindSlot(index, f.SelectedHandle);
            }
            Invalidate();
        }

        private void BuildButtons()
        {
            _buttons.Clear();
            bool vertical = _cfg.Edge == "Left" || _cfg.Edge == "Right";
            Rectangle rail = new Rectangle(_railRect.X - _panelRect.X, _railRect.Y - _panelRect.Y,
                _railRect.Width, _railRect.Height);

            string[] glyphs = new string[] { HideGlyph(), "☑", "+", "≡", "⚙", "×" };
            string[] tips = new string[] { "隐藏工具栏", "任务清单", "添加窗口槽位", "管理窗口槽位", "设置", "退出" };
            string[] actions = new string[] { "toggle", "tasks", "add", "manage", "settings", "exit" };

            int btn = S(40);
            int step = S(46);
            int pad = S(10);
            for (int i = 0; i < glyphs.Length; i++)
            {
                Rectangle br;
                if (vertical)
                {
                    int x = rail.Left + (rail.Width - btn) / 2;
                    int y = rail.Top + pad + i * step;
                    br = new Rectangle(x, y, btn, btn);
                }
                else
                {
                    int x = rail.Left + pad + i * step;
                    int y = rail.Top + (rail.Height - btn) / 2;
                    br = new Rectangle(x, y, btn, btn);
                }
                _buttons.Add(new RailButton(br, glyphs[i], tips[i], actions[i]));
            }
        }

        private string HideGlyph()
        {
            switch (_cfg.Edge)
            {
                case "Left": return "◀";
                case "Top": return "▲";
                case "Bottom": return "▼";
                default: return "▶";
            }
        }

        private Rectangle RectToClient(Rectangle r)
        {
            return new Rectangle(r.X - Left, r.Y - Top, r.Width, r.Height);
        }

        private int HitButton(Point p)
        {
            for (int i = 0; i < _buttons.Count; i++)
            {
                if (_buttons[i].Rect.Contains(p)) return i;
            }
            return -1;
        }

        private int HitEmptySlot(Point p)
        {
            for (int i = 0; i < _slots.Count && i < _slotRects.Length; i++)
            {
                SlotState s = _slots[i];
                bool bound = s.Hwnd != IntPtr.Zero && Native.IsWindow(s.Hwnd);
                if (!bound && RectToClient(_slotRects[i]).Contains(p)) return i;
            }
            return -1;
        }

        private int HitHiddenSlot(Point p)
        {
            for (int i = 0; i < _slots.Count && i < _slotRects.Length; i++)
            {
                SlotState s = _slots[i];
                if (!SlotNeedsPlaceholder(s)) continue;
                if (RectToClient(_slotRects[i]).Contains(p)) return i;
            }
            return -1;
        }

        private void RestoreHiddenSlot(int index)
        {
            SlotState s = _slots[index];
            if (s.Hwnd == IntPtr.Zero || !Native.IsWindow(s.Hwnd)) return;
            s.LastForceShowTick = Environment.TickCount;
            s.RestoreGraceUntil = Environment.TickCount + 1500;
            if (!Native.IsWindowVisible(s.Hwnd) || Native.IsIconic(s.Hwnd))
            {
                if (Native.IsIconic(s.Hwnd)) Native.ShowWindow(s.Hwnd, Native.SW_RESTORE);
                else Native.ShowWindow(s.Hwnd, Native.SW_SHOWNOACTIVATE);
            }
            else
            {
                // 窗口可见但仍停留在恢复提示上时，点击直接把它切到前台，促使应用立即重绘。
                Native.SetForegroundWindow(s.Hwnd);
            }
            s.HiddenByUs = false;
            s.InvisibleTicks = 0;
            LayoutDocked(true);
            UpdateWindowRegion();
            Invalidate();
        }

        private int HitSlot(Point p)
        {
            for (int i = 0; i < _slots.Count && i < _slotRects.Length; i++)
            {
                if (RectToClient(_slotRects[i]).Contains(p)) return i;
            }
            return -1;
        }

        private void ShowSlotMenu(int index, Point location)
        {
            if (index < 0 || index >= _slots.Count) return;
            SlotState s = _slots[index];
            bool bound = s.Hwnd != IntPtr.Zero && Native.IsWindow(s.Hwnd);

            ContextMenuStrip menu = new ContextMenuStrip();
            // 不能在 Closed 里同步 Dispose：ToolStripManager 在关闭流程结束后仍会访问该实例，
            // 否则再次弹出菜单时会抛 ObjectDisposedException 并弹出错误框。
            menu.Closed += delegate
            {
                if (IsDisposed) return;
                try
                {
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        try { menu.Dispose(); } catch (Exception) { }
                    }));
                }
                catch (Exception)
                {
                    try { menu.Dispose(); } catch (Exception) { }
                }
            };

            if (bound)
            {
                ToolStripMenuItem rebind = new ToolStripMenuItem("重新选择窗口...");
                rebind.Click += delegate { OpenPickerForSlot(index); };
                menu.Items.Add(rebind);

                ToolStripMenuItem refresh = new ToolStripMenuItem("刷新窗口画面");
                refresh.Click += delegate { RefreshSlotWindow(index); };
                menu.Items.Add(refresh);

                ToolStripMenuItem unbind = new ToolStripMenuItem("解除绑定");
                unbind.Click += delegate { UnbindSlot(index, true); RelayoutVisible(); };
                menu.Items.Add(unbind);
            }
            else
            {
                ToolStripMenuItem bind = new ToolStripMenuItem("选择要停靠的窗口...");
                bind.Click += delegate { OpenPickerForSlot(index); };
                menu.Items.Add(bind);
            }

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem up = new ToolStripMenuItem("上移");
            up.Enabled = index > 0;
            up.Click += delegate { MoveSlot(index, index - 1); };
            menu.Items.Add(up);

            ToolStripMenuItem down = new ToolStripMenuItem("下移");
            down.Enabled = index < _slots.Count - 1;
            down.Click += delegate { MoveSlot(index, index + 1); };
            menu.Items.Add(down);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem del = new ToolStripMenuItem("删除槽位");
            del.Click += delegate { RemoveSlot(index); };
            menu.Items.Add(del);

            menu.Show(this, location);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hover = HitButton(e.Location);
            if (hover != _hoverIndex)
            {
                _hoverIndex = hover;
                if (hover >= 0)
                {
                    _tipShowing = _buttons[hover].Tip;
                    _tip.Show(_tipShowing, this, e.X + 24, e.Y + 18, 1500);
                }
                else
                {
                    _tipShowing = null;
                    _tip.Hide(this);
                }
                Invalidate();
            }

            if (hover >= 0 || HitEmptySlot(e.Location) >= 0 || HitUnreadBadge(e.Location) >= 0)
                Cursor = Cursors.Hand;
            else
                Cursor = Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverIndex != -1)
            {
                _hoverIndex = -1;
                _tip.Hide(this);
                Invalidate();
            }
            Cursor = Cursors.Default;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            int b = HitButton(e.Location);
            if (b >= 0)
            {
                DoAction(_buttons[b].Action);
                return;
            }

            int badge = HitUnreadBadge(e.Location);
            if (badge >= 0)
            {
                SetSlotUnread(badge, false);
                return;
            }

            int hiddenSlot = HitHiddenSlot(e.Location);
            if (hiddenSlot >= 0)
            {
                RestoreHiddenSlot(hiddenSlot);
                return;
            }

            int s = HitEmptySlot(e.Location);
            if (s >= 0)
            {
                OpenPickerForSlot(s);
            }
        }

        private void DoAction(string action)
        {
            switch (action)
            {
                case "toggle":
                    ToggleVisibility();
                    break;
                case "tasks":
                    ToggleTasks();
                    break;
                case "add":
                    if (!AddSlot())
                        MessageBox.Show(this, SlotLimitMessage, "WindowDock",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    break;
                case "manage":
                    OpenSlotsDialog();
                    break;
                case "settings":
                    OpenSettingsDialog();
                    break;
                case "exit":
                    ExitApp();
                    break;
            }
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static void DrawCenteredString(Graphics g, string text, Font font, Brush brush, Rectangle r)
        {
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(text, font, brush, r, sf);
            }
        }

        private Rectangle UnreadBadgeRect(int index)
        {
            Rectangle r = RectToClient(_slotRects[index]);
            int d = S(12);
            return new Rectangle(r.Right - d - S(2), r.Top - d - S(2), d, d);
        }

        private int HitUnreadBadge(Point point)
        {
            for (int i = 0; i < _slots.Count && i < _slotRects.Length; i++)
                if (_slots[i].Unread && UnreadBadgeRect(i).Contains(point)) return i;
            return -1;
        }

        private void SetSlotUnread(int index, bool unread)
        {
            if (index < 0 || index >= _slots.Count) return;
            SlotState s = _slots[index];
            if (s.Unread == unread) return;
            s.Unread = unread;
            YswLog("slot " + (unread ? "unread" : "read") + " index=" + index);
            UpdateTrayStatus();
            Invalidate();
        }

        private void UpdateTrayStatus()
        {
            if (_tray == null) return;
            if (_trayBaseIcon == null) _trayBaseIcon = Icon != null ? Icon : SystemIcons.Application;
            List<string> unread = new List<string>();
            for (int i = 0; i < _slots.Count; i++)
            {
                SlotState s = _slots[i];
                if (!s.Unread) continue;
                string name = s.ProcessName;
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    name = name.Substring(0, name.Length - 4);
                if (name.Length == 0) name = s.TitleHint;
                if (name.Length == 0) name = "槽位 " + (i + 1);
                unread.Add(name);
            }
            if (unread.Count == 0)
            {
                _tray.Icon = _trayBaseIcon;
                _tray.Text = "WindowDock 窗口停靠栏";
                return;
            }
            if (_trayUnreadIcon == null) _trayUnreadIcon = BuildUnreadTrayIcon();
            _tray.Icon = _trayUnreadIcon != null ? _trayUnreadIcon : _trayBaseIcon;
            string text = "WindowDock - 未读消息：" + string.Join("、", unread.ToArray());
            _tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }

        private Icon BuildUnreadTrayIcon()
        {
            try
            {
                Size size = SystemInformation.SmallIconSize;
                if (size.Width <= 0 || size.Height <= 0) size = new Size(16, 16);
                using (Bitmap bmp = new Bitmap(size.Width, size.Height))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        if (_trayBaseIcon != null) g.DrawIcon(_trayBaseIcon, new Rectangle(Point.Empty, size));
                        int d = Math.Max(6, size.Width / 2);
                        Rectangle dot = new Rectangle(size.Width - d, size.Height - d, d - 1, d - 1);
                        using (SolidBrush fill = new SolidBrush(Color.FromArgb(255, 196, 0)))
                            g.FillEllipse(fill, dot);
                        using (Pen edge = new Pen(Color.FromArgb(35, 32, 26), 1f))
                            g.DrawEllipse(edge, dot);
                    }
                    _trayUnreadHandle = bmp.GetHicon();
                    return Icon.FromHandle(_trayUnreadHandle);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            using (SolidBrush bg = new SolidBrush(ThemeColor(Color.FromArgb(30, 31, 35))))
                g.FillRectangle(bg, ClientRectangle);

            Rectangle rail = RectToClient(_railRect);
            using (SolidBrush railBrush = new SolidBrush(ThemeColor(Color.FromArgb(17, 18, 21))))
                g.FillRectangle(railBrush, rail);

            using (Pen edgePen = new Pen(ThemeColor(Color.FromArgb(58, 62, 70)), 1f))
            {
                if (_cfg.Edge == "Left")
                    g.DrawLine(edgePen, rail.Right - 1, rail.Top, rail.Right - 1, rail.Bottom);
                else if (_cfg.Edge == "Right")
                    g.DrawLine(edgePen, rail.Left, rail.Top, rail.Left, rail.Bottom);
                else if (_cfg.Edge == "Top")
                    g.DrawLine(edgePen, rail.Left, rail.Bottom - 1, rail.Right, rail.Bottom - 1);
                else
                    g.DrawLine(edgePen, rail.Left, rail.Top, rail.Right, rail.Top);
            }

            if (_cfg.TasksVisible && _headerRect.Width > 0)
            {
                Rectangle hr = RectToClient(_headerRect);
                using (GraphicsPath path = RoundedRect(hr, S(8)))
                {
                    using (SolidBrush fill = new SolidBrush(ThemeColor(Color.FromArgb(38, 40, 46))))
                        g.FillPath(fill, path);
                    using (Pen pen = new Pen(ThemeColor(Color.FromArgb(58, 62, 70)), Math.Max(1f, 1f * _scale)))
                        g.DrawPath(pen, path);
                }
                using (SolidBrush tb = new SolidBrush(ThemeText(Color.FromArgb(205, 211, 222))))
                    g.DrawString("任务清单", _slotTitleFont, tb, hr.X + S(10), hr.Y + S(5));
                TextRenderer.DrawText(g, "回车添加 / 双击编辑 / Delete 删除", _taskHintFont,
                    new Rectangle(hr.X + S(78), hr.Y + S(5), Math.Max(0, hr.Width - S(88)), S(22)),
                    ThemeText(Color.FromArgb(120, 128, 142)),
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }

            for (int i = 0; i < _slots.Count && i < _slotRects.Length; i++)
            {
                Rectangle r = RectToClient(_slotRects[i]);
                SlotState s = _slots[i];
                bool bound = s.Hwnd != IntPtr.Zero && Native.IsWindow(s.Hwnd);

                if (bound)
                {
                    using (Pen bp = new Pen(Color.FromArgb(72, 132, 214), Math.Max(2f, 2f * _scale)))
                    {
                        Rectangle b = Rectangle.Inflate(r, S(2), S(2));
                        g.DrawRectangle(bp, b.Left, b.Top, b.Width - 1, b.Height - 1);
                    }
                    if (s.Unread)
                    {
                        Rectangle badge = UnreadBadgeRect(i);
                        using (SolidBrush fill = new SolidBrush(Color.FromArgb(255, 196, 0)))
                            g.FillEllipse(fill, badge);
                        using (Pen edge = new Pen(Color.FromArgb(40, 34, 22), Math.Max(1f, 1f * _scale)))
                            g.DrawEllipse(edge, badge);
                    }
                    if (SlotNeedsPlaceholder(s))
                    {
                        // 应用把绑定窗口藏起来了，或刚恢复还没重绘。不绘制提示时槽位只剩下
                        // 工具栏背景，看起来像黑屏。
                        Rectangle nameRect = new Rectangle(r.Left, r.Top + r.Height / 2 - S(26), r.Width, S(26));
                        using (SolidBrush nameBrush = new SolidBrush(ThemeText(Color.FromArgb(205, 211, 222))))
                            DrawCenteredString(g, s.ProcessName, _slotTitleFont, nameBrush, nameRect);
                        Rectangle hiddenRect = new Rectangle(r.Left, r.Top + r.Height / 2 + S(6), r.Width, S(22));
                        using (SolidBrush hiddenBrush = new SolidBrush(ThemeText(Color.FromArgb(140, 148, 162))))
                            DrawCenteredString(g, IsRestoring(s) ? "正在恢复窗口…" : "窗口已隐藏，点击恢复",
                                _slotHintFont, hiddenBrush, hiddenRect);
                    }
                    continue;
                }

                using (GraphicsPath path = RoundedRect(r, S(10)))
                {
                    using (SolidBrush fill = new SolidBrush(ThemeColor(Color.FromArgb(41, 43, 48))))
                        g.FillPath(fill, path);
                    using (Pen pen = new Pen(ThemeColor(Color.FromArgb(84, 90, 102)), Math.Max(1.4f, 1.4f * _scale)))
                    {
                        pen.DashStyle = DashStyle.Dash;
                        g.DrawPath(pen, path);
                    }
                }

                Rectangle plusRect = new Rectangle(r.Left, r.Top + r.Height / 2 - S(76), r.Width, S(46));
                using (SolidBrush plusBrush = new SolidBrush(ThemeText(Color.FromArgb(120, 128, 142))))
                    DrawCenteredString(g, "+", _plusFont, plusBrush, plusRect);

                Rectangle titleRect = new Rectangle(r.Left, r.Top + r.Height / 2 - S(20), r.Width, S(26));
                using (SolidBrush titleBrush = new SolidBrush(ThemeText(Color.FromArgb(205, 211, 222))))
                    DrawCenteredString(g, "槽位 " + (i + 1), _slotTitleFont, titleBrush, titleRect);

                Rectangle hintRect = new Rectangle(r.Left, r.Top + r.Height / 2 + S(8), r.Width, S(22));
                using (SolidBrush hintBrush = new SolidBrush(ThemeText(Color.FromArgb(128, 136, 150))))
                    DrawCenteredString(g, "点击选择要停靠的窗口", _slotHintFont, hintBrush, hintRect);
            }

            if (_slots.Count == 0)
            {
                Rectangle content = ClientRectangle;
                if (_cfg.Edge == "Left") content = new Rectangle(rail.Right, 0, ClientSize.Width - rail.Right, ClientSize.Height);
                else if (_cfg.Edge == "Right") content = new Rectangle(0, 0, rail.Left, ClientSize.Height);
                else if (_cfg.Edge == "Top") content = new Rectangle(0, rail.Bottom, ClientSize.Width, ClientSize.Height - rail.Bottom);
                else content = new Rectangle(0, 0, ClientSize.Width, rail.Top);

                if (_cfg.TasksVisible && !_headerRect.IsEmpty)
                {
                    Rectangle hc = RectToClient(_headerRect);
                    content.Y = hc.Bottom + S(4);
                    content.Height = Math.Max(S(20), content.Bottom - content.Y);
                }

                using (SolidBrush hintBrush = new SolidBrush(ThemeText(Color.FromArgb(140, 148, 162))))
                    DrawCenteredString(g, "点击 + 添加槽位", _slotTitleFont, hintBrush, content);
            }

            for (int i = 0; i < _buttons.Count; i++)
            {
                RailButton bt = _buttons[i];
                if (i == _hoverIndex)
                {
                    using (GraphicsPath path = RoundedRect(bt.Rect, S(8)))
                    using (SolidBrush hover = new SolidBrush(ThemeColor(Color.FromArgb(58, 63, 74))))
                        g.FillPath(hover, path);
                }
                using (SolidBrush glyphBrush = new SolidBrush(i == _hoverIndex ? ThemeText(Color.White) : ThemeText(Color.FromArgb(196, 202, 214))))
                    DrawCenteredString(g, bt.Glyph, _glyphFont, glyphBrush, bt.Rect);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideDock();
                return;
            }

            Cleanup();
            base.OnFormClosing(e);
        }

        private void Cleanup()
        {
            if (_cleaned) return;
            _cleaned = true;

            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

            try
            {
                if (_hotkeyRegistered)
                {
                    Native.UnregisterHotKey(Handle, HOTKEY_ID);
                    _hotkeyRegistered = false;
                }
            }
            catch (Exception)
            {
            }

            if (_uiTimer != null) _uiTimer.Stop();
            if (_enforceTimer != null) _enforceTimer.Stop();

            foreach (SlotState s in _slots)
                RestoreSlotWindow(s);

            Stopwatch restoreClock = Stopwatch.StartNew();
            foreach (WindowOpacityState state in _windowOpacity.Values)
            {
                int remaining = Math.Max(0, 200 - (int)restoreClock.ElapsedMilliseconds);
                if (remaining == 0) break;
                state.WaitForIdle(remaining);
            }

            SaveConfig();

            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
            if (_trayUnreadIcon != null)
            {
                _trayUnreadIcon.Dispose();
                _trayUnreadIcon = null;
            }
            if (_trayUnreadHandle != IntPtr.Zero)
            {
                Native.DestroyIcon(_trayUnreadHandle);
                _trayUnreadHandle = IntPtr.Zero;
            }
            if (_tip != null)
            {
                _tip.Dispose();
                _tip = null;
            }

            if (_glyphFont != null) _glyphFont.Dispose();
            if (_plusFont != null) _plusFont.Dispose();
            if (_slotTitleFont != null) _slotTitleFont.Dispose();
            if (_slotHintFont != null) _slotHintFont.Dispose();
            if (_taskHintFont != null) _taskHintFont.Dispose();
            if (_hangMonitor != null) { _hangMonitor.Dispose(); _hangMonitor = null; }
        }
    }
}
