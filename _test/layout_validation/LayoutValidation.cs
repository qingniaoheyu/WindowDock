using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using WindowDock;

static class LayoutValidation
{
    private static int _passed;
    private static string _dir;
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL: " + description);
        Console.WriteLine("PASS " + (++_passed) + ": " + description);
    }
    private static object Field(object owner, string name) { return owner.GetType().GetField(name, Hidden).GetValue(owner); }
    private static void Call(object owner, string name, params object[] args) { owner.GetType().GetMethod(name, Hidden).Invoke(owner, args); }
    private static void Pump(int ms)
    {
        Stopwatch timer = Stopwatch.StartNew();
        do { Application.DoEvents(); Thread.Sleep(10); } while (timer.ElapsedMilliseconds < ms);
    }
    private static IntPtr Find(int processId, string title)
    {
        IntPtr result = IntPtr.Zero;
        Native.EnumWindows(delegate(IntPtr h, IntPtr ignored)
        {
            uint pid; Native.GetWindowThreadProcessId(h, out pid);
            if (pid == processId && Native.GetWindowTitle(h) == title) { result = h; return false; }
            return true;
        }, IntPtr.Zero);
        return result;
    }
    private static void Capture(Form form, string name)
    {
        using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(_dir, name), ImageFormat.Png);
        }
    }
    private static void ChangeDeadline(DockBar dock, ListBox tasks, int index, DateTime? value, bool cancel, bool mouse)
    {
        using (System.Windows.Forms.Timer closer = new System.Windows.Forms.Timer())
        {
            closer.Interval = 60;
            closer.Tick += delegate
            {
                foreach (Form form in Application.OpenForms)
                {
                    TaskDeadlineForm deadlineForm = form as TaskDeadlineForm;
                    if (deadlineForm == null) continue;
                    closer.Stop();
                    CheckBox enabled = (CheckBox)Field(form, "_enabled");
                    DateTimePicker date = (DateTimePicker)Field(form, "_date");
                    enabled.Checked = value.HasValue;
                    if (value.HasValue) date.Value = value.Value;
                    if (cancel) { form.DialogResult = DialogResult.Cancel; form.Close(); }
                    else ((Button)form.AcceptButton).PerformClick();
                    break;
                }
            };
            tasks.SelectedIndex = index;
            tasks.TopIndex = 0;
            closer.Start();
            if (mouse)
            {
                Rectangle row = tasks.GetItemRectangle(index);
                Call(tasks, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, tasks.ClientSize.Width - 20, row.Y + row.Height / 2, 0));
            }
            else Call(tasks, "OnKeyDown", new KeyEventArgs(Keys.F2));
            Pump(30);
        }
    }
    [STAThread]
    public static int Main()
    {
        _dir = Application.StartupPath;
        Native.EnableDpiAwareness();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        DockBar dock = null;
        Process host = null;
        try
        {
            Settings cfg = new Settings();
            cfg.Slots.Clear(); cfg.Slots.Add(new SlotConfig());
            cfg.AutoHide = false; cfg.HotkeyKey = 0;
            using (DockBar startup = new DockBar(cfg.Clone(), true))
            {
                startup.Show(); Pump(100);
                Check(!Native.IsWindowVisible(startup.Handle) && ((NotifyIcon)Field(startup, "_tray")).Visible, "Startup mode stays hidden with tray available");
                startup.ShowDock(); Pump(80);
                Check(Native.IsWindowVisible(startup.Handle), "Startup toolbar can be shown");
                startup.ExitApp();
            }
            cfg.HotkeyMods = Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_SHIFT;
            cfg.HotkeyKey = (int)Keys.F11;
            cfg.Tasks.Add(new TaskItem { Text = "Task A" });
            cfg.Tasks.Add(new TaskItem { Text = "Task B", Checked = true });
            dock = new DockBar(cfg); dock.Show(); Pump(100);
            using (SettingsForm settings = new SettingsForm(cfg))
            {
                settings.Show(dock); Pump(80);
                ComboBox mode = (ComboBox)Field(settings, "_layoutMode");
                mode.SelectedIndex = 1;
                Check(((NumericUpDown)Field(settings, "_gridRows")).Value == 2 &&
                    ((NumericUpDown)Field(settings, "_gridColumns")).Value == 2, "2x2 preset fills row and column controls");
                Capture(settings, "settings-grid.png");
                ((Button)Field(settings, "_ok")).PerformClick();
                dock.ApplySettings(settings.Result);
            }
            Check(dock.Config.LayoutMode == "Grid" && dock.Slots.Count == 4 && dock.SlotCapacity == 4, "2x2 layout creates four slots");
            Check(!dock.AddSlot() && !dock.AddSlotAt(0), "Full 2x2 grid refuses a fifth slot");

            host = Process.Start(Path.Combine(_dir, "PreviewHost.exe"));
            IntPtr main = IntPtr.Zero;
            for (int i = 0; i < 50 && main == IntPtr.Zero; i++) { Pump(50); main = Find(host.Id, "Validation host"); }
            Native.PostMessage(main, 0x8100, IntPtr.Zero, IntPtr.Zero);
            Native.PostMessage(main, 0x8101, IntPtr.Zero, IntPtr.Zero);
            Pump(200);
            IntPtr[] windows = new IntPtr[] { main, Find(host.Id, "Validation independent"),
                Find(host.Id, "Validation owned preview"), Find(host.Id, "Validation ownerless preview") };
            for (int i = 0; i < windows.Length; i++) Check(windows[i] != IntPtr.Zero && dock.BindSlot(i, windows[i]), "Grid slot " + i + " binds its window");
            foreach (string edge in new string[] { "Left", "Right", "Top", "Bottom" })
            {
                Settings next = cfg.Clone(); next.Edge = edge;
                dock.ApplySettings(next); Pump(100);
                Rectangle[] slots = (Rectangle[])Field(dock, "_slotRects");
                Rectangle screen = Screen.AllScreens[cfg.MonitorIndex].WorkingArea;
                Check(slots[0].X == slots[2].X && slots[0].Y == slots[1].Y && slots[2].Y > slots[0].Y && slots[1].X > slots[0].X, edge + " uses row-major 2x2 layout");
                bool valid = screen.Contains(dock.Bounds);
                for (int i = 0; i < slots.Length; i++)
                {
                    valid &= screen.Contains(slots[i]) && Native.GetWindowRectangle(windows[i]) == slots[i];
                    for (int j = 0; j < i; j++) valid &= !slots[i].IntersectsWith(slots[j]);
                }
                Check(valid, edge + " slots stay on screen without overlap and match actual windows");
            }
            dock.MoveSlot(0, 3); Pump(100);
            Check(dock.Slots[3].Hwnd == main && Native.GetWindowRectangle(main) == ((Rectangle[])Field(dock, "_slotRects"))[3], "Moving a slot updates its grid cell");
            dock.HideDock(); Pump(100);
            bool hidden = true; foreach (IntPtr window in windows) hidden &= !Native.IsWindowVisible(window);
            Check(hidden, "Hiding grid hides all four bound windows");
            dock.ShowDock(); Pump(100);
            bool visible = true; foreach (IntPtr window in windows) visible &= Native.IsWindowVisible(window);
            Check(visible, "Showing grid restores all four bound windows");
            Settings custom = cfg.Clone(); custom.GridRows = 2; custom.GridColumns = 3;
            dock.ApplySettings(custom); Pump(100);
            Check(dock.Slots.Count == 6 && dock.SlotCapacity == 6, "Custom 2x3 grid creates six slots");
            Settings loaded = Settings.Load();
            Check(loaded.LayoutMode == "Grid" && loaded.GridRows == 2 && loaded.GridColumns == 3 && loaded.Slots.Count == 6, "Custom layout persists");
            custom = cfg.Clone(); custom.GridRows = 2; custom.GridColumns = 4;
            dock.ApplySettings(custom); Pump(100);
            Check(dock.Slots.Count == Settings.MaxSlots && !dock.AddSlot(), "Eight-window global limit is enforced");

            ListBox tasks = (ListBox)Field(dock, "_taskList");
            DateTime first = new DateTime(2027, 2, 1, 10, 30, 0), second = new DateTime(2027, 3, 2, 16, 45, 0);
            ChangeDeadline(dock, tasks, 0, first, false, true);
            Check(cfg.Tasks[0].Deadline == first && !cfg.Tasks[1].Deadline.HasValue, "Clicking Task A deadline edits only Task A");
            ChangeDeadline(dock, tasks, 1, second, false, false);
            loaded = Settings.Load();
            Check(loaded.Tasks[0].Deadline == first && loaded.Tasks[1].Deadline == second && loaded.Tasks[1].Checked, "Each task persists its own deadline and completion");
            ChangeDeadline(dock, tasks, 0, second, true, false);
            Check(cfg.Tasks[0].Deadline == first, "Cancel preserves task deadline");
            ChangeDeadline(dock, tasks, 1, null, false, false);
            Check(!Settings.Load().Tasks[1].Deadline.HasValue && cfg.Tasks[0].Deadline == first, "Clearing Task B deadline preserves Task A deadline");
            Capture(dock, "grid-tasks.png");

            File.WriteAllText(Settings.ConfigPath, "Layout=Grid\nGridRows=2\nGridColumns=2\nSlotCount=8\nTaskCount=0\n");
            loaded = Settings.Load();
            Check(loaded.Slots.Count == 8 && loaded.LayoutMode == "Row", "Invalid small grid preserves eight configured slots");
            Console.WriteLine("RESULT: " + _passed + " layout and task checks passed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            if (dock != null) { dock.ExitApp(); dock.Dispose(); }
            if (host != null && !host.HasExited)
            {
                IntPtr main = Find(host.Id, "Validation host");
                Native.PostMessage(main, 0x8103, IntPtr.Zero, IntPtr.Zero);
                if (!host.WaitForExit(2000)) host.Kill();
            }
        }
    }
}
