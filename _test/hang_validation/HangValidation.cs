using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WindowDock;

static class HangValidation
{
    private static int _passed;
    private static EventWaitHandle _blocked, _release;
    private static IntPtr _main, _popup;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool PtInRegion(IntPtr region, int x, int y);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);

    private static byte Alpha(IntPtr hwnd)
    {
        uint key, flags; byte alpha;
        return Native.GetLayeredWindowAttributes(hwnd, out key, out alpha, out flags) ? alpha : (byte)255;
    }

    private static void Check(bool result, string message)
    {
        if (!result) throw new Exception("FAIL: " + message);
        Console.WriteLine("PASS " + (++_passed) + ": " + message);
    }

    private static void Pump(int milliseconds)
    {
        Stopwatch clock = Stopwatch.StartNew();
        do { Application.DoEvents(); Thread.Sleep(10); } while (clock.ElapsedMilliseconds < milliseconds);
    }

    private static void Block()
    {
        _blocked.Reset(); _release.Reset();
        Native.PostMessage(_main, 0x8210, IntPtr.Zero, IntPtr.Zero);
        if (!_blocked.WaitOne(2000)) throw new Exception("Test host failed to enter blocked state");
    }

    private static void Resume() { _release.Set(); Pump(500); }

    private static void Fast(Action action, string message)
    {
        Stopwatch clock = Stopwatch.StartNew();
        action();
        Check(clock.ElapsedMilliseconds < 500, message + " (" + clock.ElapsedMilliseconds + " ms)");
    }

    private static object Field(object value, string name) { return value.GetType().GetField(name, PrivateInstance).GetValue(value); }
    private static void Call(object value, string name, params object[] args) { value.GetType().GetMethod(name, PrivateInstance).Invoke(value, args); }

    [STAThread]
    private static int Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Native.EnableDpiAwareness();
        string prefix = "yswHangTest" + Guid.NewGuid().ToString("N");
        string directory = AppDomain.CurrentDomain.BaseDirectory;
        File.WriteAllText(Path.Combine(directory, "WindowDock_dbg.log"), "");
        using (_blocked = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + "Blocked"))
        using (_release = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + "Release"))
        using (Process host = Process.Start(Path.Combine(directory, "HangHost.exe"), prefix))
        {
            DockBar dock = null;
            try
            {
                for (int i = 0; i < 100 && _popup == IntPtr.Zero; i++)
                {
                    Pump(50);
                    _main = Native.FindWindow(null, prefix);
                    _popup = Native.FindWindow(null, prefix + "Popup");
                }
                Check(_main != IntPtr.Zero && _popup != IntPtr.Zero, "Isolated app and image preview started");
                Rectangle original = Native.GetWindowRectangle(_main);
                Settings config = new Settings();
                config.Edge = "Top";
                config.AutoHide = false;
                config.SlotWidth = 450;
                config.SlotHeight = 420;
                dock = new DockBar(config);
                dock.Show(); Pump(200);
                dock.ActiveControl = null;
                Check(dock.BindSlot(0, _main), "External app bound");
                Pump(1200);
                Check(((System.Collections.IDictionary)Field(dock, "_popups")).Contains(_popup), "Owned preview is managed");

                TextBox input = (TextBox)Field(dock, "_taskInput");
                input.Text = "ysw Chinese composition draft";
                Native.PostMessage(input.Handle, 0x010D, IntPtr.Zero, IntPtr.Zero);
                Pump(50);
                Check((bool)input.GetType().GetProperty("IsComposing").GetValue(input, null), "Task input recognizes active IME composition");
                KeyEventArgs compositionEnter = new KeyEventArgs(Keys.Enter);
                Call(dock, "TaskInputKeyDown", input, compositionEnter);
                Check(config.Tasks.Count == 0 && input.Text.Length > 0 && !compositionEnter.SuppressKeyPress, "IME candidate Enter preserves draft and lets IME handle the key");
                Call(dock, "TaskInputKeyDown", input, new KeyEventArgs(Keys.Escape));
                Check(input.Text.Length > 0, "IME Escape does not clear the task draft");
                Block();
                Fast(delegate { Call(dock, "UiTickCore"); Call(dock, "EnforceTickCore"); }, "Background enforcement pauses during task composition");
                Resume();
                Native.PostMessage(input.Handle, 0x010E, IntPtr.Zero, IntPtr.Zero);
                Pump(50);
                Call(dock, "TaskInputKeyDown", input, new KeyEventArgs(Keys.Enter));
                Check(config.Tasks.Count == 1 && input.Text.Length == 0, "Enter saves the task once IME composition ends");

                Block();
                Check(!Native.IsWindowResponsive(_main), "Host really stops processing messages");
                Fast(dock.HideDock, "Hiding toolbar stays responsive while app is blocked");
                Check(!dock.Visible, "Toolbar itself hides immediately");
                Resume();
                Check(!Native.IsWindowVisible(_main) && !Native.IsWindowVisible(_popup), "App and preview hide after processing resumes");

                Block();
                Fast(dock.ShowDock, "Showing toolbar stays responsive while app is blocked");
                Check(dock.Visible, "Toolbar itself shows immediately");
                Resume(); Pump(1000);
                Check(Native.IsWindowVisible(_main) && Native.IsWindowVisible(_popup), "App and preview restore after processing resumes");
                Rectangle target = ((Rectangle[])Field(dock, "_slotRects"))[0];
                Check(Native.GetWindowRectangle(_main) == target, "Restored app fits its slot");
                IntPtr region = CreateRectRgn(0, 0, 0, 0);
                try
                {
                    Point client = dock.PointToClient(new Point(target.X + target.Width / 2, target.Y + target.Height / 2));
                    Check(GetWindowRgn(dock.Handle, region) > 0 && !PtInRegion(region, client.X, client.Y),
                        "Toolbar region clears after asynchronous app visibility settles");
                }
                finally { Native.DeleteObject(region); }

                Native.SetWindowPos(_main, IntPtr.Zero, original.X, original.Y, original.Width, original.Height,
                    Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                Pump(100); Block();
                Fast(delegate { Call(dock, "LayoutDocked", true); }, "Relayout stays responsive while app is blocked");
                Resume();
                target = ((Rectangle[])Field(dock, "_slotRects"))[0];
                Check(Native.GetWindowRectangle(_main) == target, "Queued relayout applies after processing resumes");

                Block();
                input.Text = "ysw independent task while external app is blocked";
                Fast(delegate { Call(dock, "CommitTask"); }, "Task input stays responsive while app is blocked");
                Check(config.Tasks.Count == 2, "Task is saved while app is blocked");
                Settings changed = config.Clone();
                changed.AlwaysOnTop = false;
                changed.OpacityPercent = 55;
                Fast(delegate { dock.ApplySettings(changed); }, "Appearance and topmost settings stay responsive while app is blocked");
                Resume();
                Check(Alpha(_main) == 140 && Alpha(_popup) == 140, "Queued opacity applies to app and preview after processing resumes");

                Block();
                Fast(delegate
                {
                    Settings latest = ((Settings)Field(dock, "_cfg")).Clone();
                    latest.OpacityPercent = 100;
                    dock.ApplySettings(latest);
                    Thread.Sleep(30);
                    latest = latest.Clone(); latest.OpacityPercent = 35; dock.ApplySettings(latest);
                    latest = latest.Clone(); latest.OpacityPercent = 85; dock.ApplySettings(latest);
                }, "Rapid opacity changes stay responsive while app is blocked");
                Resume();
                Check(Alpha(_main) == 216 && Alpha(_popup) == 216, "Serialized opacity updates preserve the latest requested value");

                Block();
                long longest = 0;
                for (int i = 0; i < 10; i++)
                {
                    Stopwatch cycle = Stopwatch.StartNew();
                    dock.HideDock(); dock.ShowDock();
                    longest = Math.Max(longest, cycle.ElapsedMilliseconds);
                }
                Check(longest < 500, "Repeated hide/show stays responsive (longest " + longest + " ms)");
                Resume(); Pump(1200);
                Check(Native.IsWindowVisible(_main) && Native.IsWindowVisible(_popup), "Repeated requests preserve final visible state");

                dock.HideDock(); Pump(400); Block();
                Fast(delegate { dock.UnbindSlot(0, true); }, "Unbinding stays responsive while app is blocked");
                Resume();
                Check(Native.IsWindowVisible(_main) && Native.IsWindowVisible(_popup), "Unbinding restores hidden app and preview");
                Check(Native.GetWindowRectangle(_main) == original, "Unbinding restores original app geometry");
                Check(!Native.IsLayeredWindow(_main), "Unbinding restores original opacity style");

                using (Form own = new Form())
                {
                    own.Show();
                    Native.SetWindowPos(own.Handle, IntPtr.Zero, 100, 150, 300, 250, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                    Check(Native.GetWindowRectangle(own.Handle) == new Rectangle(100, 150, 300, 250), "Same-thread position changes remain synchronous");
                    Native.ShowWindow(own.Handle, Native.SW_HIDE);
                    Check(!Native.IsWindowVisible(own.Handle), "Same-thread hide remains synchronous");
                }

                string logPath = Path.Combine(directory, "WindowDock_dbg.log");
                Type monitor = typeof(DockBar).Assembly.GetType("WindowDock.UiHangMonitor");
                using ((IDisposable)monitor.GetMethod("Track", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { "ysw simulated UI stall" }))
                    Thread.Sleep(3300);
                string log = File.ReadAllText(logPath);
                Check(log.Contains("[ysw] UI stalled") && log.Contains("phase=ysw simulated UI stall"), "Background watchdog records the blocked operation");
                Check(log.Contains("UI operation recovered"), "Watchdog records recovery duration");
                Pump(100);
                dock.ShowDock();
                Check(dock.BindSlot(0, _main), "App can be rebound after restoration");
                Pump(500); Block();
                Fast(dock.ExitApp, "Exit has a bounded wait while the external app is blocked");
                Resume();
                Check(!Native.IsLayeredWindow(_main) && Native.GetWindowRectangle(_main) == original,
                    "Closing toolbar restores geometry and opacity after app resumes");
                dock.Dispose(); dock = null;
                Console.WriteLine("RESULT: " + _passed + " hang and recovery checks passed.");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally
            {
                _release.Set();
                if (dock != null) { dock.ExitApp(); dock.Dispose(); }
                if (!host.HasExited) host.Kill();
            }
        }
    }
}
