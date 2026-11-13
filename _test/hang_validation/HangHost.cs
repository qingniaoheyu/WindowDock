using System;
using System.Drawing;
using System.Threading;
using System.Runtime.InteropServices;
using System.Windows.Forms;

sealed class HangHost : Form
{
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    private EventWaitHandle _blocked;
    private EventWaitHandle _release;
    private Form _popup;

    private HangHost(string prefix)
    {
        _blocked = EventWaitHandle.OpenExisting(prefix + "Blocked");
        _release = EventWaitHandle.OpenExisting(prefix + "Release");
        Text = prefix;
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(100, 300, 450, 320);
        Shown += delegate
        {
            _popup = new Form();
            _popup.Text = prefix + "Popup";
            _popup.Bounds = new Rectangle(600, 300, 320, 220);
            _popup.Show(this);
        };
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x8210)
        {
            _blocked.Set();
            // 直接等待内核事件，避开 CLR 在 STA WaitHandle 等待期间分派窗口消息。
            WaitForSingleObject(_release.SafeWaitHandle.DangerousGetHandle(), 5000);
            return;
        }
        base.WndProc(ref m);
    }

    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new HangHost(args[0]));
    }
}
