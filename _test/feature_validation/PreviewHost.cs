using System;
using System.Drawing;
using System.Windows.Forms;

sealed class PreviewHost : Form
{
    private Form _owned;
    private Form _ownerless;
    private Form _independent;
    private Form _layered;

    public PreviewHost()
    {
        Text = "Validation host";
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(100, 400, 420, 300);
        Button image = new Button();
        image.Text = "Open image preview";
        image.Dock = DockStyle.Fill;
        image.Click += delegate { OpenOwned(); };
        Controls.Add(image);
        Shown += delegate
        {
            _independent = MakePreview("Validation independent", 600);
            _independent.Show();
        };
    }

    private Form MakePreview(string title, int x)
    {
        Form preview = new Form();
        preview.Text = title;
        preview.StartPosition = FormStartPosition.Manual;
        preview.Bounds = new Rectangle(x, 450, 350, 230);
        preview.BackColor = Color.FromArgb(235, 242, 252);
        preview.Paint += delegate(object sender, PaintEventArgs e)
        {
            e.Graphics.FillEllipse(Brushes.CornflowerBlue, 60, 45, 100, 100);
            e.Graphics.FillRectangle(Brushes.Orange, 170, 70, 90, 70);
            e.Graphics.DrawString("Image preview", preview.Font, Brushes.Black, 100, 165);
        };
        return preview;
    }

    private void OpenOwned()
    {
        if (_owned == null || _owned.IsDisposed) _owned = MakePreview("Validation owned preview", 1000);
        _owned.Show(this);
        _owned.Activate();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x8100) { OpenOwned(); return; }
        if (m.Msg == 0x8101)
        {
            if (_ownerless == null || _ownerless.IsDisposed) _ownerless = MakePreview("Validation ownerless preview", 1350);
            _ownerless.Show();
            _ownerless.Activate();
            return;
        }
        if (m.Msg == 0x8102) { if (_owned != null) _owned.Show(this); return; }
        if (m.Msg == 0x8103) { Application.Exit(); return; }
        if (m.Msg == 0x8104)
        {
            if (_layered == null || _layered.IsDisposed)
            {
                _layered = MakePreview("Validation layered preview", 1750);
                _layered.Opacity = 0.8;
            }
            _layered.Show(this);
            _layered.Activate();
            return;
        }
        if (m.Msg == 0x8105)
        {
            try { QtRenderingHost.Open(Handle); }
            catch (Exception ex) { System.IO.File.WriteAllText(System.IO.Path.Combine(Application.StartupPath, "qt-host-errors.log"), "[ysw] " + ex); }
            return;
        }
        base.WndProc(ref m);
    }

    [STAThread]
    public static void Main()
    {
        try { WindowDock.Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new PreviewHost());
    }
}
