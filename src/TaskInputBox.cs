using System.Windows.Forms;

namespace WindowDock
{
    internal sealed class TaskInputBox : TextBox
    {
        public bool IsComposing { get; private set; }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x010D) IsComposing = true; // WM_IME_STARTCOMPOSITION
            else if (message.Msg == 0x010E) IsComposing = false; // WM_IME_ENDCOMPOSITION
            base.WndProc(ref message);
        }

        protected override void OnLostFocus(System.EventArgs e)
        {
            base.OnLostFocus(e);
            IsComposing = false;
        }
    }
}
