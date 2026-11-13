using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace WindowDock
{
    internal static class DpiScaleUtil
    {
        // WinForms 在按显示器 DPI 感知（PMv2）进程下不会自动缩放自建窗体，
        // 这里用系统 DPI 对手工布局的对话框做一次等比缩放。
        public static float ScaleDialog(Form f)
        {
            float s = Native.GetSystemScale();
            if (s < 1.05f || s > 4f) return 1f;

            Size cs = f.ClientSize;
            f.ClientSize = new Size(Round(cs.Width * s), Round(cs.Height * s));
            ScaleControls(f.Controls, s);
            return s;
        }

        private static void ScaleControls(Control.ControlCollection controls, float s)
        {
            foreach (Control c in controls)
            {
                Rectangle b = c.Bounds;
                c.SetBounds(Round(b.X * s), Round(b.Y * s), Round(b.Width * s), Round(b.Height * s));

                ListView lv = c as ListView;
                if (lv != null)
                {
                    foreach (ColumnHeader col in lv.Columns)
                        col.Width = Round(col.Width * s);
                }

                if (c.HasChildren) ScaleControls(c.Controls, s);
            }
        }

        private static int Round(float v)
        {
            return (int)Math.Round(v, MidpointRounding.AwayFromZero);
        }
    }

    internal static class AppMessages
    {
        public const string WindowClass = "WindowDockBar";
        public const string MutexName = "Local\\WindowDockBar_SingleInstance";

        public const int Toggle = 0x8000 + 10;
        public const int Show = 0x8000 + 11;
        public const int Hide = 0x8000 + 12;
        public const int Exit = 0x8000 + 13;
        public const int Settings = 0x8000 + 14;
        public const int Slots = 0x8000 + 15;
    }

    public class WindowInfo
    {
        public IntPtr Handle;
        public string Title = "";
        public string ProcessName = "";
        public int ProcessId;

        public override string ToString()
        {
            return Title;
        }
    }

    public static class Native
    {
        public const int GWL_STYLE = -16;
        public const int GWL_EXSTYLE = -20;
        public const int WS_CAPTION = 0x00C00000;
        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_LAYERED = 0x00080000;
        public const uint LWA_ALPHA = 0x00000002;
        public const uint GW_OWNER = 4;
        public const uint GW_HWNDPREV = 3;

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_NOOWNERZORDER = 0x0200;
        public const uint SWP_ASYNCWINDOWPOS = 0x4000;

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        public static readonly IntPtr HWND_TOP = IntPtr.Zero;

        public const int SW_HIDE = 0;
        public const int SW_SHOW = 5;
        public const int SW_SHOWNOACTIVATE = 4;
        public const int SW_RESTORE = 9;
        public const int SW_MINIMIZE = 6;
        public const int SW_SHOWMAXIMIZED = 3;
        public const int SW_SHOWMINIMIZED = 2;
        public const int SW_SHOWNORMAL = 1;

        public const uint GA_ROOT = 2;
        public const uint GA_ROOTOWNER = 3;

        public const int DWMWA_CLOAKED = 14;
        public const int VK_LBUTTON = 0x01;

        public const int MOD_ALT = 0x0001;
        public const int MOD_CONTROL = 0x0002;
        public const int MOD_SHIFT = 0x0004;
        public const int MOD_WIN = 0x0008;
        public const int MOD_NOREPEAT = 0x4000;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;

            public POINT(int x, int y)
            {
                X = x;
                Y = y;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public int Width { get { return Right - Left; } }
            public int Height { get { return Bottom - Top; } }

            public Rectangle ToRectangle()
            {
                return Rectangle.FromLTRB(Left, Top, Right, Bottom);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WINDOWPLACEMENT
        {
            public int length;
            public int flags;
            public int showCmd;
            public POINT ptMinPosition;
            public POINT ptMaxPosition;
            public RECT rcNormalPosition;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MINMAXINFO
        {
            public POINT Reserved;
            public POINT MaxSize;
            public POINT MaxPosition;
            public POINT MinTrackSize;
            public POINT MaxTrackSize;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wparam,
            ref MINMAXINFO info, uint flags, uint timeout, out IntPtr result);

        public static Size GetWindowMinimumSize(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return Size.Empty;
            using (UiHangMonitor.Track("GetWindowMinimumSize hwnd=" + hwnd))
            {
                MINMAXINFO info = new MINMAXINFO();
                IntPtr result;
                if (SendMessageTimeout(hwnd, 0x0024, IntPtr.Zero, ref info, 3, 100, out result) == IntPtr.Zero)
                    return Size.Empty;
                return new Size(Math.Max(0, info.MinTrackSize.X), Math.Max(0, info.MinTrackSize.Y));
            }
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        public static bool IsForeignWindowThread(IntPtr hwnd)
        {
            uint pid;
            uint thread = GetWindowThreadProcessId(hwnd, out pid);
            return thread != 0 && thread != GetCurrentThreadId();
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
        private static extern IntPtr SendMessageTimeoutValue(IntPtr hwnd, uint message, IntPtr first,
            IntPtr second, uint flags, uint timeout, out IntPtr result);

        public static bool IsWindowResponsive(IntPtr hwnd)
        {
            IntPtr result;
            return SendMessageTimeoutValue(hwnd, 0, IntPtr.Zero, IntPtr.Zero, 3, 20, out result) != IntPtr.Zero;
        }

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsZoomed(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", EntryPoint = "ShowWindow")]
        private static extern bool ShowWindowCore(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        public static bool ShowWindow(IntPtr hWnd, int nCmdShow)
        {
            using (UiHangMonitor.Track("ShowWindow hwnd=" + hWnd + " command=" + nCmdShow))
                return IsForeignWindowThread(hWnd) ? ShowWindowAsync(hWnd, nCmdShow) : ShowWindowCore(hWnd, nCmdShow);
        }

        [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
        private static extern bool SetWindowPosCore(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        public static bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags)
        {
            using (UiHangMonitor.Track("SetWindowPos hwnd=" + hWnd))
            {
                if (IsForeignWindowThread(hWnd)) uFlags |= SWP_ASYNCWINDOWPOS;
                return SetWindowPosCore(hWnd, hWndInsertAfter, X, Y, cx, cy, uFlags);
            }
        }

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLongCore(IntPtr hWnd, int nIndex, int dwNewLong);

        public static int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong)
        {
            using (UiHangMonitor.Track("SetWindowLong hwnd=" + hWnd))
                return SetWindowLongCore(hWnd, nIndex, dwNewLong);
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint colorKey, out byte alpha, out uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hwnd, uint command);

        [DllImport("user32.dll")]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll", EntryPoint = "WindowFromPoint")]
        private static extern IntPtr WindowFromPointCore(POINT point);

        public static IntPtr WindowFromPoint(POINT point)
        {
            using (UiHangMonitor.Track("WindowFromPoint")) return WindowFromPointCore(point);
        }

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        public static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

        [DllImport("user32.dll")]
        public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, string lParam);

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        [DllImport("shcore.dll")]
        public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        public static float GetScaleForPoint(Point p)
        {
            try
            {
                IntPtr mon = MonitorFromPoint(new POINT(p.X, p.Y), 2);
                uint dpiX;
                uint dpiY;
                if (mon != IntPtr.Zero && GetDpiForMonitor(mon, 0, out dpiX, out dpiY) == 0 && dpiX > 0)
                    return dpiX / 96f;
            }
            catch (Exception)
            {
            }
            return GetSystemScale();
        }

        public static float GetSystemScale()
        {
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    float s = g.DpiX / 96f;
                    if (s < 1f) s = 1f;
                    return s;
                }
            }
            catch (Exception)
            {
                return 1f;
            }
        }

        public static void EnableDpiAwareness()
        {
            try
            {
                if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return;
            }
            catch (Exception)
            {
            }
            try
            {
                SetProcessDPIAware();
            }
            catch (Exception)
            {
            }
        }

        public static string GetWindowTitle(IntPtr hwnd)
        {
            int len = GetWindowTextLength(hwnd);
            if (len <= 0) return "";
            StringBuilder sb = new StringBuilder(len + 2);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string GetWindowClassName(IntPtr hwnd)
        {
            StringBuilder name = new StringBuilder(256);
            GetClassName(hwnd, name, name.Capacity);
            return name.ToString();
        }

        public static bool IsQtWindow(IntPtr hwnd)
        {
            string name = GetWindowClassName(hwnd);
            return name.StartsWith("Qt", StringComparison.Ordinal) &&
                name.IndexOf("QWindow", StringComparison.Ordinal) >= 0;
        }

        public static string GetProcessName(int pid)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    return p.ProcessName + ".exe";
                }
            }
            catch (Exception)
            {
                return "";
            }
        }

        public static Rectangle GetWindowRectangle(IntPtr hwnd)
        {
            RECT r;
            if (!GetWindowRect(hwnd, out r)) return Rectangle.Empty;
            return r.ToRectangle();
        }

        public static bool IsLayeredWindow(IntPtr hwnd)
        {
            return (GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_LAYERED) != 0;
        }

        public static bool IsCloaked(IntPtr hwnd)
        {
            try
            {
                int cloaked;
                if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0)
                    return cloaked != 0;
            }
            catch (Exception)
            {
            }
            return false;
        }

        public static List<WindowInfo> GetTopLevelWindows(int excludePid)
        {
            List<WindowInfo> result = new List<WindowInfo>();
            try
            {
                EnumWindows(delegate(IntPtr hwnd, IntPtr lParam)
                {
                    try
                    {
                        if (!IsWindowVisible(hwnd)) return true;
                        int titleLen = GetWindowTextLength(hwnd);
                        if (titleLen <= 0) return true;

                        StringBuilder cls = new StringBuilder(256);
                        GetClassName(hwnd, cls, cls.Capacity);
                        string clsName = cls.ToString();
                        if (clsName == "Progman" || clsName == "WorkerW" || clsName == "Shell_TrayWnd" ||
                            clsName == "Windows.UI.Core.CoreWindow" || clsName == "ApplicationManager_DesktopShellWindow")
                            return true;

                        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                        if ((ex & WS_EX_TOOLWINDOW) != 0) return true;

                        uint pid;
                        GetWindowThreadProcessId(hwnd, out pid);
                        if (pid == 0 || (int)pid == excludePid) return true;

                        Rectangle r = GetWindowRectangle(hwnd);
                        if (r.Width <= 0 || r.Height <= 0) return true;
                        if (IsCloaked(hwnd)) return true;

                        StringBuilder title = new StringBuilder(titleLen + 2);
                        GetWindowText(hwnd, title, title.Capacity);

                        WindowInfo info = new WindowInfo();
                        info.Handle = hwnd;
                        info.Title = title.ToString();
                        info.ProcessId = (int)pid;
                        info.ProcessName = GetProcessName((int)pid);
                        result.Add(info);
                    }
                    catch (Exception)
                    {
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception)
            {
            }
            return result;
        }
    }
}
