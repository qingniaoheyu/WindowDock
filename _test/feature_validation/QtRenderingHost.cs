using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using WindowDock;

// 重现 Qt 从普通窗口切换到 UpdateLayeredWindow 的绘制路径，不依赖安装 Qt SDK。
internal static class QtRenderingHost
{
    private delegate IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
    private static readonly WindowProc Callback = WndProc;
    private static IntPtr _root;
    private static IntPtr _shadow;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style;
        public WindowProc Procedure;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string MenuName, ClassName;
        public IntPtr SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; public Point(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)]
    private struct Size { public int Width, Height; public Size(int w, int h) { Width = w; Height = h; } }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Blend { public byte Operation, Flags, Alpha, Format; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(int exStyle, string className, string title, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowText(IntPtr hwnd, string title);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr screen, ref Point destination, ref Size size, IntPtr source, ref Point origin, uint key, ref Blend blend, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);

    private static void Register(string name)
    {
        WindowClass wc = new WindowClass();
        wc.Size = (uint)Marshal.SizeOf(typeof(WindowClass));
        wc.Procedure = Callback;
        wc.Instance = GetModuleHandle(null);
        wc.ClassName = name;
        ushort atom = RegisterClassEx(ref wc);
        if (atom == 0) throw new Exception("RegisterClassEx " + name + " failed: " + Marshal.GetLastWin32Error());
    }

    public static void Open(IntPtr main)
    {
        if (_root != IntPtr.Zero) return;
        Register("QtValidationQWindowIcon");
        Register("Microsoft.IME.ValidationCandidateWindow.Host");
        _root = CreateWindowEx(0, "QtValidationQWindowIcon", "Validation Qt content", 0x10CF0000,
            950, 700, 340, 230, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (_root == IntPtr.Zero) throw new Exception("Qt root creation failed: " + Marshal.GetLastWin32Error());
        _shadow = CreateWindowEx(Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW,
            "QtValidationQWindowIcon", "Validation Qt shadow", unchecked((int)0x90000000),
            938, 688, 364, 254, _root, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (_shadow == IntPtr.Zero) throw new Exception("Qt shadow creation failed: " + Marshal.GetLastWin32Error());
        Render(_shadow, true);
        CreateWindowEx(Native.WS_EX_TOOLWINDOW, "Microsoft.IME.ValidationCandidateWindow.Host",
            "Validation IME candidate", unchecked((int)0x90000000), 100, 100, 220, 40,
            _root, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
    }

    private static bool Render(IntPtr hwnd, bool shadow)
    {
        Rectangle rect = Native.GetWindowRectangle(hwnd);
        int style = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, style | Native.WS_EX_LAYERED);
        using (Bitmap bitmap = new Bitmap(rect.Width, rect.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        {
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(shadow ? Color.Transparent : Color.FromArgb(255, 50, 120, 220));
                if (shadow)
                    using (Pen pen = new Pen(Color.FromArgb(80, 0, 0, 0), 10))
                        graphics.DrawRectangle(pen, 5, 5, rect.Width - 10, rect.Height - 10);
            }
            IntPtr hbitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
            IntPtr old = SelectObject(dc, hbitmap);
            Point destination = new Point(rect.X, rect.Y), origin = new Point(0, 0);
            Size size = new Size(rect.Width, rect.Height);
            Blend blend = new Blend();
            blend.Alpha = 255;
            blend.Format = 1;
            bool result = UpdateLayeredWindow(hwnd, IntPtr.Zero, ref destination, ref size, dc, ref origin, 0, ref blend, 2);
            SelectObject(dc, old);
            DeleteDC(dc);
            Native.DeleteObject(hbitmap);
            return result;
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam)
    {
        if (message == 0x0047 && hwnd == _root && _shadow != IntPtr.Zero && Native.IsWindow(_shadow))
        {
            Rectangle rect = Native.GetWindowRectangle(hwnd);
            Native.SetWindowPos(_shadow, IntPtr.Zero, rect.X - 12, rect.Y - 12,
                rect.Width + 24, rect.Height + 24,
                Native.SWP_NOACTIVATE | Native.SWP_NOZORDER | Native.SWP_NOOWNERZORDER);
            Render(_shadow, true);
        }
        if (message == 0x8110)
        {
            bool result = Render(hwnd, false);
            SetWindowText(hwnd, result ? "Validation Qt render OK" : "Validation Qt render FAILED");
            return IntPtr.Zero;
        }
        if (message == 0x8111) { DestroyWindow(hwnd); return IntPtr.Zero; }
        return DefWindowProc(hwnd, message, wparam, lparam);
    }
}
