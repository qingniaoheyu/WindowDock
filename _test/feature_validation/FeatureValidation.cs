using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using WindowDock;

static class FeatureValidation
{
    private static int _passed;
    private static Exception _uiError;
    private static string _dir;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint first, uint second, bool attach);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    private static void FocusWindow(IntPtr hwnd)
    {
        uint pid;
        uint foregroundThread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out pid);
        uint currentThread = GetCurrentThreadId();
        bool attached = currentThread != foregroundThread && AttachThreadInput(currentThread, foregroundThread, true);
        try { Native.SetForegroundWindow(hwnd); }
        finally { if (attached) AttachThreadInput(currentThread, foregroundThread, false); }
        if (Native.GetForegroundWindow() != hwnd)
        {
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
            Rectangle rect = Native.GetWindowRectangle(hwnd);
            Native.SetCursorPos(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
            mouse_event(2, 0, 0, 0, UIntPtr.Zero);
            mouse_event(4, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(120);
            Native.SetWindowPos(hwnd, Native.HWND_NOTOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
        }
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL: " + description);
        _passed++;
        Console.WriteLine("PASS " + _passed + ": " + description);
    }

    private static void Pump(int milliseconds)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        do
        {
            Application.DoEvents();
            if (_uiError != null) throw new Exception("UI exception", _uiError);
            Thread.Sleep(10);
        } while (elapsed.ElapsedMilliseconds < milliseconds);
    }

    private static object Field(object obj, string name)
    {
        return obj.GetType().GetField(name, PrivateInstance).GetValue(obj);
    }

    private static void Call(object obj, string name, params object[] args)
    {
        obj.GetType().GetMethod(name, PrivateInstance).Invoke(obj, args);
    }

    private static IntPtr Find(int processId, string title)
    {
        IntPtr found = IntPtr.Zero;
        Native.EnumWindows(delegate(IntPtr hwnd, IntPtr ignored)
        {
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            if (pid == processId && Native.GetWindowTitle(hwnd) == title) { found = hwnd; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static bool Topmost(IntPtr hwnd)
    {
        return (Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TOPMOST) != 0;
    }

    private static byte Alpha(IntPtr hwnd)
    {
        uint colorKey, flags;
        byte alpha;
        if (!Native.GetLayeredWindowAttributes(hwnd, out colorKey, out alpha, out flags))
            throw new Exception("Cannot read alpha: " + hwnd);
        return alpha;
    }

    private static bool Above(IntPtr popup, IntPtr lower)
    {
        HashSet<IntPtr> seen = new HashSet<IntPtr>();
        for (IntPtr window = Native.GetWindow(lower, Native.GW_HWNDPREV);
            window != IntPtr.Zero && seen.Add(window); window = Native.GetWindow(window, Native.GW_HWNDPREV))
            if (window == popup) return true;
        return false;
    }

    private static int Pixels(ListBox list, int index, string kind)
    {
        using (Bitmap bitmap = new Bitmap(440, list.ItemHeight))
        {
            using (Graphics g = Graphics.FromImage(bitmap))
                Call(list, "OnDrawItem", new DrawItemEventArgs(g, list.Font,
                    new Rectangle(0, 0, bitmap.Width, bitmap.Height), index, DrawItemState.None));
            bitmap.Save(Path.Combine(_dir, "task-" + index + "-" + kind + ".png"), ImageFormat.Png);
            int count = 0;
            for (int x = 35; x < bitmap.Width; x++)
                for (int y = 0; y < bitmap.Height; y++)
                {
                    Color c = bitmap.GetPixel(x, y);
                    if (kind == "red" && c.R > 150 && c.G < 95 && c.B < 95) count++;
                    if (kind == "gray" && c.R >= 110 && c.R <= 145 && Math.Abs(c.R - c.G) < 4 && Math.Abs(c.G - c.B) < 4) count++;
                }
            return count;
        }
    }

    private static void Capture(Form form, string name)
    {
        using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(_dir, name), ImageFormat.Png);
        }
    }

    [STAThread]
    public static int Main()
    {
        _dir = Path.GetDirectoryName(Application.ExecutablePath);
        Native.EnableDpiAwareness();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs args) { _uiError = args.Exception; };
        Process previewHost = null;
        DockBar dock = null;
        try
        {
            previewHost = Process.Start(Path.Combine(_dir, "PreviewHost.exe"));
            IntPtr main = IntPtr.Zero, independent = IntPtr.Zero;
            for (int i = 0; i < 50 && (main == IntPtr.Zero || independent == IntPtr.Zero); i++)
            {
                Pump(50);
                main = Find(previewHost.Id, "Validation host");
                independent = Find(previewHost.Id, "Validation independent");
            }
            Check(main != IntPtr.Zero && independent != IntPtr.Zero, "Isolated external app started");
            Rectangle originalRect = Native.GetWindowRectangle(main);

            Settings cfg = new Settings();
            cfg.Slots.Clear();
            cfg.Slots.Add(new SlotConfig());
            cfg.AutoHide = false;
            cfg.HotkeyMods = Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_SHIFT;
            cfg.HotkeyKey = (int)Keys.F12;
            cfg.Edge = "Right";
            cfg.SlotWidth = 420;
            cfg.SlotHeight = 300;
            cfg.Tasks.Add(new TaskItem { Text = "Normal task" });
            cfg.Tasks.Add(new TaskItem { Text = "Completed task", Checked = true, Deadline = DateTime.Now.AddHours(-1) });
            cfg.Tasks.Add(new TaskItem { Text = "Deadline soon", Deadline = DateTime.Now.AddHours(1) });
            cfg.Tasks.Add(new TaskItem { Text = "Future deadline", Deadline = DateTime.Now.AddHours(48) });
            cfg.Tasks.Add(new TaskItem { Text = "Overdue task", Deadline = DateTime.Now.AddHours(-2) });
            dock = new DockBar(cfg);
            dock.Show();
            Pump(250);
            Check(dock.BindSlot(0, main), "Main window bound");
            Pump(250);
            Check(Topmost(main), "Bound main window is topmost");

            ListBox tasks = (ListBox)Field(dock, "_taskList");
            Check(Pixels(tasks, 1, "gray") > 20, "Completed task renders gray pixels");
            Check(((Font)Field(tasks, "_completedFont")).Strikeout, "Completed task uses strikeout font");
            Check(Pixels(tasks, 1, "red") == 0, "Completed overdue task remains gray");
            Check(Pixels(tasks, 2, "red") > 20, "Upcoming deadline renders red text");
            Check(Pixels(tasks, 3, "red") == 0, "Future deadline uses normal color");
            Check(Pixels(tasks, 4, "red") > 20, "Overdue task remains red");
            cfg.Tasks[3].Deadline = DateTime.Now.AddHours(24).AddMilliseconds(500);
            Check(Pixels(tasks, 3, "red") == 0, "Task before reminder boundary is normal");
            Pump(1100);
            Check(Pixels(tasks, 3, "red") > 20, "Deadline color updates after time crosses boundary");

            tasks.SelectedIndex = 0;
            Call(tasks, "OnKeyDown", new KeyEventArgs(Keys.Space));
            Check(cfg.Tasks[0].Checked && Settings.Load().Tasks[0].Checked, "Space toggles and persists completion");
            Call(tasks, "OnKeyDown", new KeyEventArgs(Keys.Space));
            Check(!cfg.Tasks[0].Checked, "Unchecking restores unfinished state");
            TextBox input = (TextBox)Field(dock, "_taskInput");
            DateTimePicker deadline = (DateTimePicker)Field(dock, "_taskDeadline");
            Button save = (Button)Field(dock, "_taskSave");
            DateTime assignedDeadline = new DateTime(2027, 1, 2, 14, 30, 0);
            input.Text = "Task with saved deadline";
            deadline.Value = assignedDeadline;
            deadline.Checked = true;
            save.PerformClick();
            Settings loaded = Settings.Load();
            Check(cfg.Tasks.Count == 6 && loaded.Tasks[5].Deadline == assignedDeadline, "Adding task saves deadline round trip");
            tasks.SelectedIndex = 5;
            Call(tasks, "OnDoubleClick", EventArgs.Empty);
            Check(input.Text == cfg.Tasks[5].Text && deadline.Checked, "Double click loads task and deadline for editing");
            input.Text = "Edited task without deadline";
            deadline.Checked = false;
            save.PerformClick();
            Check(cfg.Tasks[5].Text == "Edited task without deadline", "Edited task text saved");
            Check(!Settings.Load().Tasks[5].Deadline.HasValue, "Removing deadline persists");
            tasks.SelectedIndex = 5;
            Call(tasks, "OnKeyDown", new KeyEventArgs(Keys.Delete));
            Check(cfg.Tasks.Count == 5 && input.Text.Length == 0 && !deadline.Checked, "Delete removes task and resets editor");
            Capture(dock, "tasks-dark.png");

            Native.PostMessage(main, 0x8100, IntPtr.Zero, IntPtr.Zero);
            Pump(500);
            IntPtr owned = Find(previewHost.Id, "Validation owned preview");
            Check(owned != IntPtr.Zero && Topmost(owned) && Above(owned, main) && Above(owned, dock.Handle), "Owned image preview is above dock and main window");
            Native.PostMessage(main, 0x8101, IntPtr.Zero, IntPtr.Zero);
            Pump(150);
            IntPtr ownerless = Find(previewHost.Id, "Validation ownerless preview");
            System.Windows.Forms.Timer scan = (System.Windows.Forms.Timer)Field(dock, "_uiTimer");
            scan.Stop();
            try { FocusWindow(ownerless); }
            finally { scan.Start(); }
            Check(dock.GetType().GetMethod("FindPopupSlot", PrivateInstance).Invoke(dock,
                new object[] { ownerless, ownerless }) != null, "New ownerless foreground window belongs to its bound app");
            Check(dock.GetType().GetMethod("FindPopupSlot", PrivateInstance).Invoke(dock,
                new object[] { independent, independent }) == null, "Existing same-process window is excluded from ownerless matching");
            if (Native.GetForegroundWindow() != ownerless)
            {
                // 自动化桌面未提供前台输入时，暂时关联测试主窗口来纳入管理，再移除 owner。
                // 后续置顶、透明度和隐藏恢复仍在真实无 owner 窗口上验证。
                Settings original = cfg.Clone();
                Settings normal = cfg.Clone(); normal.AlwaysOnTop = false;
                dock.ApplySettings(normal);
                SetWindowLongPtr(ownerless, -8, main);
                Pump(200);
                SetWindowLongPtr(ownerless, -8, IntPtr.Zero);
                dock.ApplySettings(original);
            }
            Pump(350);
            Console.WriteLine("Ownerless focus: " + Native.GetForegroundWindow() + ", preview: " + ownerless + ", topmost: " + Topmost(ownerless));
            Check(ownerless != IntPtr.Zero && Topmost(ownerless) && Above(ownerless, main), "New ownerless preview is tracked and raised");
            Check(!Topmost(independent), "Existing independent window is not made topmost");

            using (SettingsForm settings = new SettingsForm(cfg))
            {
                settings.Show(dock);
                Pump(150);
                Capture(settings, "settings.png");
                ((NumericUpDown)Field(settings, "_opacity")).Value = 55;
                ((NumericUpDown)Field(settings, "_reminderHours")).Value = 12;
                settings.GetType().GetField("_selectedBackground", PrivateInstance).SetValue(settings, Color.FromArgb(225, 233, 245));
                ((Button)Field(settings, "_ok")).PerformClick();
                Check(settings.Result != null && settings.Result.OpacityPercent == 55 && settings.Result.DeadlineReminderHours == 12, "Settings dialog accepts opacity and reminder duration");
                dock.ApplySettings(settings.Result);
            }
            Pump(250);
            Check(Math.Abs(dock.Opacity - 0.55) < 0.01 && Alpha(main) == 140 && Alpha(owned) == 140 && Alpha(ownerless) == 140, "Opacity applies to toolbar, main, and both previews");
            Check(!Native.IsLayeredWindow(independent), "Independent window opacity remains unchanged");
            Check(dock.BackColor.ToArgb() == Color.FromArgb(225, 233, 245).ToArgb() && tasks.ForeColor.GetBrightness() < 0.3f, "Light background uses readable dark text");
            loaded = Settings.Load();
            Check(loaded.OpacityPercent == 55 && loaded.BackgroundColorArgb == cfg.BackgroundColorArgb && loaded.DeadlineReminderHours == 12, "Appearance settings persist");
            Capture(dock, "tasks-light.png");

            dock.HideDock();
            Pump(300);
            Check(!Native.IsWindowVisible(main) && !Native.IsWindowVisible(owned) && !Native.IsWindowVisible(ownerless), "Hiding toolbar hides main and both previews");
            Check(Native.IsWindowVisible(independent), "Hiding toolbar preserves existing independent window");
            Native.PostMessage(main, 0x8102, IntPtr.Zero, IntPtr.Zero);
            Pump(400);
            Check(!Native.IsWindowVisible(owned), "Preview reopened by app while hidden is hidden again");
            dock.ShowDock();
            Pump(300);
            Check(Native.IsWindowVisible(main) && Native.IsWindowVisible(owned) && Native.IsWindowVisible(ownerless), "Showing toolbar restores main and both previews");
            Check(Above(owned, main) && Above(ownerless, main), "Restored previews stay above main window");
            Native.SetWindowPos(dock.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            Pump(300);
            Check(Above(owned, dock.Handle) && Above(ownerless, dock.Handle), "Previews remain above toolbar after toolbar is raised");

            Native.PostMessage(main, 0x8104, IntPtr.Zero, IntPtr.Zero);
            Pump(500);
            IntPtr layered = Find(previewHost.Id, "Validation layered preview");
            Check(Alpha(layered) >= 110 && Alpha(layered) <= 112, "Existing alpha is combined with configured opacity");
            Settings opaque = cfg.Clone();
            opaque.OpacityPercent = 100;
            dock.ApplySettings(opaque);
            Pump(150);
            Check(!Native.IsLayeredWindow(main) && !Native.IsLayeredWindow(owned) && !Native.IsLayeredWindow(ownerless), "100 percent restores originally opaque window styles");
            Check(Alpha(layered) == 204, "100 percent restores originally layered alpha");

            Settings topOff = cfg.Clone();
            topOff.AlwaysOnTop = false;
            dock.ApplySettings(topOff);
            Pump(200);
            Check(!Topmost(main) && !Topmost(ownerless), "Turning off topmost restores main and ownerless preview stacking");
            dock.HideDock();
            Pump(100);
            dock.UnbindSlot(0, true);
            Pump(150);
            Check(Native.IsWindowVisible(main) && Native.IsWindowVisible(owned) && Native.IsWindowVisible(ownerless), "Unbinding while hidden restores external windows");
            Check(Native.GetWindowRectangle(main) == originalRect, "Unbinding restores main window position and size");
            Check(!Native.IsLayeredWindow(main) && Alpha(layered) == 204, "Unbinding restores original transparency");

            Native.PostMessage(main, 0x8105, IntPtr.Zero, IntPtr.Zero);
            Pump(200);
            IntPtr qt = Find(previewHost.Id, "Validation Qt content");
            IntPtr shadow = Find(previewHost.Id, "Validation Qt shadow");
            IntPtr candidate = Find(previewHost.Id, "Validation IME candidate");
            Check(qt != IntPtr.Zero && shadow != IntPtr.Zero && candidate != IntPtr.Zero, "Qt rendering and auxiliary windows started");
            Settings qtOpacity = cfg.Clone();
            qtOpacity.OpacityPercent = 97;
            qtOpacity.AlwaysOnTop = true;
            dock.ApplySettings(qtOpacity);
            dock.ShowDock();
            Check(dock.BindSlot(0, qt), "Qt window binds with opacity enabled");
            Pump(350);
            Check(!Native.IsLayeredWindow(qt), "Opacity does not convert Qt window to attribute layering");
            Native.PostMessage(qt, 0x8110, IntPtr.Zero, IntPtr.Zero);
            Pump(250);
            Check(Native.GetWindowTitle(qt) == "Validation Qt render OK", "Qt UpdateLayeredWindow succeeds after interface switch");
            qtOpacity = cfg.Clone();
            qtOpacity.OpacityPercent = 55;
            dock.ApplySettings(qtOpacity);
            Native.PostMessage(qt, 0x8110, IntPtr.Zero, IntPtr.Zero);
            Pump(250);
            Check(Native.GetWindowTitle(qt) == "Validation Qt render OK", "Qt rendering survives later opacity changes");
            System.Collections.IDictionary popups = (System.Collections.IDictionary)Field(dock, "_popups");
            Check(!popups.Contains(shadow), "Qt shadow is excluded from managed previews");
            Check(!popups.Contains(candidate), "IME candidate is excluded from managed previews");
            dock.HideDock();
            Pump(200);
            dock.ShowDock();
            Pump(200);
            Native.PostMessage(qt, 0x8110, IntPtr.Zero, IntPtr.Zero);
            Pump(150);
            Check(Native.IsWindowVisible(qt) && Native.GetWindowTitle(qt) == "Validation Qt render OK", "Qt rendering survives toolbar hide and show");
            Check(Native.SetLayeredWindowAttributes(qt, 0, 247, Native.LWA_ALPHA), "Isolated reproduction applies the old attribute opacity API");
            Native.PostMessage(qt, 0x8110, IntPtr.Zero, IntPtr.Zero);
            Pump(150);
            Check(Native.GetWindowTitle(qt) == "Validation Qt render FAILED", "Old opacity API reproduces the interface-switch rendering failure");
            Native.SetWindowLong(qt, Native.GWL_EXSTYLE,
                Native.GetWindowLong(qt, Native.GWL_EXSTYLE) & ~Native.WS_EX_LAYERED);
            Native.PostMessage(qt, 0x8110, IntPtr.Zero, IntPtr.Zero);
            Pump(150);
            Check(Native.GetWindowTitle(qt) == "Validation Qt render OK", "Clearing attribute layering restores rendering");
            dock.UnbindSlot(0, true);
            Native.PostMessage(qt, 0x8111, IntPtr.Zero, IntPtr.Zero);
            Pump(100);

            File.WriteAllText(Settings.ConfigPath, "SlotCount=0\nTaskCount=1\nTask0Text=Legacy task\nTask0Checked=1\n");
            loaded = Settings.Load();
            Check(loaded.OpacityPercent == 100 && loaded.DeadlineReminderHours == 24 && loaded.Tasks[0].Checked && !loaded.Tasks[0].Deadline.HasValue, "Legacy config loads default appearance and optional deadline");
            Console.WriteLine("RESULT: " + _passed + " checks passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            Console.Error.WriteLine("RESULT: failed after " + _passed + " checks.");
            return 1;
        }
        finally
        {
            if (dock != null) { dock.ExitApp(); dock.Dispose(); }
            if (previewHost != null && !previewHost.HasExited)
            {
                IntPtr main = Find(previewHost.Id, "Validation host");
                if (main != IntPtr.Zero) Native.PostMessage(main, 0x8103, IntPtr.Zero, IntPtr.Zero);
                if (!previewHost.WaitForExit(3000)) previewHost.Kill();
            }
        }
    }
}
