using System;
using System.Drawing;

namespace WindowDock
{
    public partial class DockBar
    {
        private Color ThemeColor(Color original)
        {
            Color background = Color.FromArgb(_cfg.BackgroundColorArgb);
            return Color.FromArgb(255,
                Math.Max(0, Math.Min(255, original.R + background.R - 30)),
                Math.Max(0, Math.Min(255, original.G + background.G - 31)),
                Math.Max(0, Math.Min(255, original.B + background.B - 35)));
        }

        private Color ThemeText(Color original)
        {
            return Color.FromArgb(_cfg.BackgroundColorArgb).GetBrightness() > 0.55f
                ? Color.FromArgb(40, 44, 52) : original;
        }

        private void ApplyAppearance()
        {
            Opacity = Math.Max(20, Math.Min(100, _cfg.OpacityPercent)) / 100.0;
            BackColor = Color.FromArgb(255, Color.FromArgb(_cfg.BackgroundColorArgb));
            _taskList.BackColor = ThemeColor(Color.FromArgb(36, 38, 44));
            _taskList.ForeColor = ThemeText(Color.FromArgb(214, 220, 230));
            _taskList.ReminderHours = _cfg.DeadlineReminderHours;
            _taskInput.BackColor = ThemeColor(Color.FromArgb(48, 51, 58));
            _taskInput.ForeColor = ThemeText(Color.FromArgb(230, 234, 242));
            _taskSave.UseVisualStyleBackColor = false;
            _taskSave.BackColor = _taskInput.BackColor;
            _taskSave.ForeColor = _taskInput.ForeColor;
            foreach (SlotState slot in _slots) ApplyWindowOpacity(slot.Hwnd);
            foreach (PopupState popup in _popups.Values)
                if (popup.IsValid) ApplyWindowOpacity(popup.Hwnd);
            _taskList.Invalidate();
            Invalidate();
        }
    }
}
