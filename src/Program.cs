using System;
using System.Threading;
using System.Windows.Forms;

namespace WindowDock
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Native.EnableDpiAwareness();

            bool createdNew;
            Mutex mutex = new Mutex(true, AppMessages.MutexName, out createdNew);

            if (!createdNew)
            {
                SendCommand(args);
                return;
            }

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
                {
                    LogError(e.Exception);
                    MessageBox.Show("程序发生错误：" + e.Exception.Message + "\n详细信息已写入 WindowDock_error.log。",
                        "WindowDock", MessageBoxButtons.OK, MessageBoxIcon.Error);
                };

                Settings cfg = Settings.Load();
                try
                {
                    bool startup = args.Length > 0 && string.Equals(args[0], "--startup", StringComparison.OrdinalIgnoreCase);
                    Application.Run(new DockBar(cfg, startup));
                }
                catch (Exception ex)
                {
                    LogError(ex);
                    throw;
                }
            }
            finally
            {
                mutex.Dispose();
            }
        }

        private static void LogError(Exception ex)
        {
            try
            {
                string path = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(Application.ExecutablePath), "WindowDock_error.log");
                System.IO.File.AppendAllText(path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n" + ex + "\r\n\r\n");
            }
            catch (Exception)
            {
            }
        }

        private static void SendCommand(string[] args)
        {
            int msg = AppMessages.Toggle;
            string cmd = args.Length > 0 ? args[0].TrimStart('-', '/').ToLowerInvariant() : "toggle";

            if (cmd == "show") msg = AppMessages.Show;
            else if (cmd == "startup") return;
            else if (cmd == "hide") msg = AppMessages.Hide;
            else if (cmd == "exit" || cmd == "quit") msg = AppMessages.Exit;
            else if (cmd == "settings") msg = AppMessages.Settings;
            else if (cmd == "slots") msg = AppMessages.Slots;

            IntPtr hwnd = IntPtr.Zero;
            for (int i = 0; i < 30 && hwnd == IntPtr.Zero; i++)
            {
                hwnd = Native.FindWindow(null, "WindowDock");
                if (hwnd == IntPtr.Zero) Thread.Sleep(100);
            }

            if (hwnd != IntPtr.Zero)
                Native.PostMessage(hwnd, (uint)msg, IntPtr.Zero, IntPtr.Zero);
        }
    }
}
