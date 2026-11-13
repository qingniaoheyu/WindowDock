using System;
using System.Drawing;
using System.Windows.Forms;

namespace WindowDock
{
    internal sealed class TaskDeadlineEventArgs : EventArgs
    {
        public readonly int Index;
        public TaskDeadlineEventArgs(int index) { Index = index; }
    }

    // CheckedListBox 不支持自绘文字，使用 ListBox 保留键盘选择并绘制复选框。
    internal sealed class TaskListBox : ListBox
    {
        private Font _completedFont;
        public int ReminderHours = 24;
        public event ItemCheckEventHandler ItemCheck;
        public event EventHandler<TaskDeadlineEventArgs> DeadlineEditRequested;

        private Rectangle DeadlineBounds(Rectangle row)
        {
            int width = TextRenderer.MeasureText("截止 2026-12-31 23:59", Font).Width + 10;
            if (row.Width < width + Font.Height + 70) return Rectangle.Empty;
            return new Rectangle(row.Right - width - 4, row.Top, width, row.Height);
        }

        public TaskListBox()
        {
            DrawMode = DrawMode.OwnerDrawFixed;
            IntegralHeight = false;
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            if (_completedFont != null) _completedFont.Dispose();
            _completedFont = new Font(Font, Font.Style | FontStyle.Strikeout);
            ItemHeight = Math.Max(24, Font.Height + 8);
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= Items.Count) return;
            TaskItem task = Items[e.Index] as TaskItem;
            if (task == null) return;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            Color background = selected
                ? (BackColor.GetBrightness() > 0.55f ? ControlPaint.Dark(BackColor) : ControlPaint.Light(BackColor))
                : BackColor;
            using (SolidBrush brush = new SolidBrush(background))
                e.Graphics.FillRectangle(brush, e.Bounds);

            int boxSize = Math.Min(e.Bounds.Height - 6, Math.Max(14, Font.Height));
            Rectangle box = new Rectangle(e.Bounds.Left + 4,
                e.Bounds.Top + (e.Bounds.Height - boxSize) / 2, boxSize, boxSize);
            ControlPaint.DrawCheckBox(e.Graphics, box, task.Checked ? ButtonState.Checked : ButtonState.Normal);

            Color textColor = task.Checked ? Color.Gray : ForeColor;
            if (!task.Checked && task.Deadline.HasValue &&
                (task.Deadline.Value - DateTime.Now).TotalHours <= ReminderHours)
                textColor = Color.FromArgb(220, 50, 50);
            Font textFont = task.Checked && _completedFont != null ? _completedFont : Font;
            Rectangle textRect = new Rectangle(box.Right + 6, e.Bounds.Top,
                Math.Max(0, e.Bounds.Right - box.Right - 12), e.Bounds.Height);
            string text = task.Text;
            Rectangle dueRect = DeadlineBounds(e.Bounds);
            if (!dueRect.IsEmpty)
            {
                string dueText = task.Deadline.HasValue ? "截止 " + task.Deadline.Value.ToString("yyyy-MM-dd HH:mm") : "设置截止时间";
                TextRenderer.DrawText(e.Graphics, dueText, task.Deadline.HasValue ? textFont : Font,
                    dueRect, task.Deadline.HasValue ? textColor : Color.Gray,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                textRect.Width = Math.Max(0, dueRect.Left - textRect.Left - 8);
            }
            else if (task.Deadline.HasValue) text += "  截止 " + task.Deadline.Value.ToString("MM-dd HH:mm");
            TextRenderer.DrawText(e.Graphics, text, textFont, textRect, textColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int index = IndexFromPoint(e.Location);
            if (e.Button == MouseButtons.Left && index >= 0 && e.X <= Font.Height + 16)
                ToggleChecked(index);
            else if (e.Button == MouseButtons.Left && index >= 0 &&
                DeadlineBounds(GetItemRectangle(index)).Contains(e.Location) && DeadlineEditRequested != null)
                DeadlineEditRequested(this, new TaskDeadlineEventArgs(index));
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space && SelectedIndex >= 0)
            {
                ToggleChecked(SelectedIndex);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            base.OnKeyDown(e);
        }

        private void ToggleChecked(int index)
        {
            TaskItem task = Items[index] as TaskItem;
            if (task == null) return;
            ItemCheckEventArgs args = new ItemCheckEventArgs(index,
                task.Checked ? CheckState.Unchecked : CheckState.Checked,
                task.Checked ? CheckState.Checked : CheckState.Unchecked);
            if (ItemCheck != null) ItemCheck(this, args);
            task.Checked = args.NewValue == CheckState.Checked;
            Invalidate(GetItemRectangle(index));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _completedFont != null)
            {
                _completedFont.Dispose();
                _completedFont = null;
            }
            base.Dispose(disposing);
        }
    }
}
