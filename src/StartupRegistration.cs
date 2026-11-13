using System;
using Microsoft.Win32;
using System.Windows.Forms;

namespace WindowDock
{
    public static class StartupRegistration
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "WindowDock";

        public static string Command
        {
            get { return "\"" + Application.ExecutablePath + "\" --startup"; }
        }

        public static bool IsEnabled
        {
            get
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                        return key != null && string.Equals(key.GetValue(ValueName) as string,
                            Command, StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception) { return false; }
            }
        }

        public static void SetEnabled(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (key == null) throw new InvalidOperationException("无法访问当前用户的开机启动设置。");
                if (enabled) key.SetValue(ValueName, Command, RegistryValueKind.String);
                else if (IsEnabled) key.DeleteValue(ValueName, false);
            }
        }
    }
}
