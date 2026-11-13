using System;
using System.Drawing;

namespace WindowDock
{
    public partial class DockBar
    {
        private bool CanFitGrid(Settings cfg, int replacementIndex, IntPtr replacement, out string error)
        {
            error = "";
            int rows = cfg.GridRows, columns = cfg.GridColumns;
            if (rows < 1 || columns < 1 || rows * columns > Settings.MaxSlots || rows * columns < _slots.Count)
            {
                error = "网格最多支持 8 个窗口，且格数不能少于已有槽位。请调整行列数。";
                return false;
            }
            Rectangle work;
            float scale;
            ScreenInfo(cfg, out work, out scale);
            bool vertical = cfg.Edge == "Left" || cfg.Edge == "Right";
            int availableWidth = work.Width - (int)Math.Round((2 * MARGIN + (vertical ? RAIL : 0)) * scale);
            int availableHeight = work.Height - (int)Math.Round((2 * MARGIN + (vertical ? 0 : RAIL) +
                (cfg.TasksVisible ? HEADER_H + GAP : 0)) * scale);
            int[] widths = new int[columns], heights = new int[rows];
            for (int i = 0; i < _slots.Count; i++)
            {
                IntPtr hwnd = i == replacementIndex ? replacement : _slots[i].Hwnd;
                Size minimum = Native.GetWindowMinimumSize(hwnd);
                if (cfg.AllowSlotExpand && hwnd == _slots[i].Hwnd)
                {
                    minimum.Width = Math.Max(minimum.Width, SlotMinWAt(i));
                    minimum.Height = Math.Max(minimum.Height, _slotMinHeights[i]);
                }
                widths[i % columns] = Math.Max(widths[i % columns], minimum.Width);
                heights[i / columns] = Math.Max(heights[i / columns], minimum.Height);
            }
            int width = (int)Math.Round((columns - 1) * GAP * scale);
            int height = (int)Math.Round((rows - 1) * GAP * scale);
            foreach (int value in widths) width += Math.Max((int)Math.Round(80 * scale), value);
            foreach (int value in heights) height += Math.Max((int)Math.Round(80 * scale), value);
            if (width <= availableWidth && height <= availableHeight) return true;
            error = "已有窗口的最小尺寸无法放入所选网格。请减少行列数、隐藏任务区，或选择更大的显示器。";
            return false;
        }

        private static void ScreenInfo(Settings cfg, out Rectangle work, out float scale)
        {
            System.Windows.Forms.Screen[] screens = System.Windows.Forms.Screen.AllScreens;
            int index = Math.Max(0, Math.Min(screens.Length - 1, cfg.MonitorIndex));
            work = screens[index].WorkingArea;
            scale = Native.GetScaleForPoint(new Point(work.Left + work.Width / 2, work.Top + work.Height / 2));
        }

        private static int Sum(int[] sizes)
        {
            int sum = 0;
            foreach (int value in sizes) sum += value;
            return sum;
        }

        private static void FitTracks(int[] desired, int[] minimum, int available)
        {
            int need = Sum(desired) - available;
            if (need <= 0) return;
            int slack = Sum(desired) - Sum(minimum);
            if (slack <= 0) return;
            int cut = Math.Min(need, slack), used = 0;
            for (int i = 0; i < desired.Length; i++)
            {
                int amount = (int)((long)(desired[i] - minimum[i]) * cut / slack);
                desired[i] -= amount;
                used += amount;
            }
            for (int i = 0; used < cut; i = (i + 1) % desired.Length)
                if (desired[i] > minimum[i]) { desired[i]--; used++; }
        }

        private void ComputeGridLayout(Rectangle work)
        {
            string error;
            if (!CanFitGrid(_cfg, -1, IntPtr.Zero, out error))
            {
                _cfg.LayoutMode = "Row";
                YswLog("grid minimum size changed, restored row layout: " + error);
                ComputeLayout();
                SaveConfig();
                if (_tray != null)
                    _tray.ShowBalloonTip(3000, "窗口排列已调整", "窗口最小尺寸或屏幕空间发生变化，已改为单排以保持窗口可见。", System.Windows.Forms.ToolTipIcon.Info);
                return;
            }
            int rows = _cfg.GridRows, columns = _cfg.GridColumns;
            bool vertical = _cfg.Edge == "Left" || _cfg.Edge == "Right";
            int rail = S(RAIL), margin = S(MARGIN), gap = S(GAP);
            int header = _cfg.TasksVisible ? S(HEADER_H) : 0;
            int headerGap = header > 0 ? gap : 0;
            int[] widths = new int[columns], heights = new int[rows];
            int[] minimumWidths = new int[columns], minimumHeights = new int[rows];
            for (int c = 0; c < columns; c++) minimumWidths[c] = S(80);
            for (int r = 0; r < rows; r++) minimumHeights[r] = S(80);
            for (int i = 0; i < _slots.Count; i++)
            {
                int column = i % columns, row = i / columns;
                Size nativeMinimum = Native.GetWindowMinimumSize(_slots[i].Hwnd);
                int minWidth = nativeMinimum.Width;
                int minHeight = nativeMinimum.Height;
                if (_cfg.AllowSlotExpand)
                {
                    minWidth = Math.Max(minWidth, SlotMinWAt(i));
                    minHeight = Math.Max(minHeight, _slotMinHeights[i]);
                }
                minimumWidths[column] = Math.Max(minimumWidths[column], minWidth);
                minimumHeights[row] = Math.Max(minimumHeights[row], minHeight);
            }
            int availableWidth = work.Width - 2 * margin - (vertical ? rail : 0) - (columns - 1) * gap;
            int availableHeight = work.Height - 2 * margin - (vertical ? 0 : rail) - header - headerGap - (rows - 1) * gap;
            // 配置尺寸作为上限，屏幕放不下时按窗口最小尺寸收缩，不主动铺满。
            for (int c = 0; c < columns; c++) widths[c] = Math.Max(S(_cfg.SlotWidth), minimumWidths[c]);
            for (int r = 0; r < rows; r++) heights[r] = Math.Max(S(_cfg.SlotHeight), minimumHeights[r]);
            FitTracks(widths, minimumWidths, availableWidth);
            FitTracks(heights, minimumHeights, availableHeight);
            int contentWidth = Sum(widths) + (columns - 1) * gap;
            int contentHeight = Sum(heights) + (rows - 1) * gap;
            int panelWidth = contentWidth + 2 * margin + (vertical ? rail : 0);
            int panelHeight = contentHeight + header + headerGap + 2 * margin + (vertical ? 0 : rail);
            int x = vertical ? (_cfg.Edge == "Right" ? work.Right - panelWidth : work.Left)
                : work.Left + (work.Width - panelWidth) / 2;
            int y = vertical ? work.Top + (work.Height - panelHeight) / 2
                : (_cfg.Edge == "Top" ? work.Top : work.Bottom - panelHeight);
            _panelRect = new Rectangle(x, y, panelWidth, panelHeight);
            _railRect = vertical ? new Rectangle(_cfg.Edge == "Left" ? x : x + panelWidth - rail, y, rail, panelHeight)
                : new Rectangle(x, _cfg.Edge == "Top" ? y : y + panelHeight - rail, panelWidth, rail);
            int contentX = x + margin + (_cfg.Edge == "Left" ? rail : 0);
            int contentY = y + margin + (_cfg.Edge == "Top" ? rail : 0);
            _headerRect = header > 0 ? new Rectangle(contentX, contentY, contentWidth, header) : Rectangle.Empty;
            _slotRects = new Rectangle[_slots.Count];
            int slotY = contentY + header + headerGap;
            for (int r = 0; r < rows; r++)
            {
                int slotX = contentX;
                for (int c = 0; c < columns; c++)
                {
                    int index = r * columns + c;
                    if (index < _slotRects.Length) _slotRects[index] = new Rectangle(slotX, slotY, widths[c], heights[r]);
                    slotX += widths[c] + gap;
                }
                slotY += heights[r] + gap;
            }
            BuildButtons();
            UpdateTaskControlsBounds();
        }
    }
}
