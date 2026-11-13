using System;
using System.Collections.Generic;
using System.Drawing;

namespace WindowDock
{
    public partial class DockBar
    {
        private static bool IsAuxiliaryWindow(IntPtr hwnd)
        {
            string name = Native.GetWindowClassName(hwnd);
            if (name == "#32768" || name == "tooltips_class32" || name == "SysShadow" ||
                name == "IME" || name == "MSCTFIME UI" || name == "NativeHWNDHost" ||
                name.StartsWith("Microsoft.IME.", StringComparison.Ordinal) ||
                name.IndexOf("IME.UIManager", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("QWindowToolTip", StringComparison.Ordinal) >= 0 ||
                name.IndexOf("QWindowToolSaveBits", StringComparison.Ordinal) >= 0)
                return true;

            // Qt 的阴影是独立的逐像素透明窗口，围在真正的窗口外，不能作为预览置顶或隐藏。
            if (!Native.IsQtWindow(hwnd) || !Native.IsLayeredWindow(hwnd) ||
                (Native.GetWindowLong(hwnd, Native.GWL_STYLE) & Native.WS_CAPTION) != 0) return false;
            IntPtr owner = Native.GetWindow(hwnd, Native.GW_OWNER);
            if (owner == IntPtr.Zero || !Native.IsWindow(owner)) return false;
            uint colorKey, flags;
            byte alpha;
            if (Native.GetLayeredWindowAttributes(hwnd, out colorKey, out alpha, out flags)) return false;
            Rectangle rect = Native.GetWindowRectangle(hwnd);
            Rectangle ownerRect = Native.GetWindowRectangle(owner);
            return ownerRect.Width > 0 && ownerRect.Height > 0 && rect.Contains(ownerRect);
        }

        private void CaptureExistingWindows(SlotState slot)
        {
            slot.ExistingWindows.Clear();
            uint processId;
            Native.GetWindowThreadProcessId(slot.Hwnd, out processId);
            Native.EnumWindows(delegate(IntPtr hwnd, IntPtr param)
            {
                uint pid;
                Native.GetWindowThreadProcessId(hwnd, out pid);
                if (pid == processId && Native.IsWindowVisible(hwnd)) slot.ExistingWindows.Add(hwnd);
                return true;
            }, IntPtr.Zero);
        }

        private SlotState FindPopupSlot(IntPtr hwnd, IntPtr foreground)
        {
            foreach (SlotState slot in _slots)
                if (slot.Hwnd == hwnd) return null;

            // 图片预览、模态对话框等通常通过 owner 关联主窗口；跨进程的 owner 也可识别。
            IntPtr owner = Native.GetWindow(hwnd, Native.GW_OWNER);
            for (int depth = 0; owner != IntPtr.Zero && depth < 32; depth++)
            {
                foreach (SlotState slot in _slots)
                    if (slot.Hwnd != IntPtr.Zero && Native.IsWindow(slot.Hwnd) && slot.Hwnd == owner)
                        return slot;
                PopupState parent;
                if (_popups.TryGetValue(owner, out parent) && parent.IsValid)
                    foreach (SlotState slot in _slots)
                        if (slot.Hwnd == parent.DockedHwnd) return slot;
                owner = Native.GetWindow(owner, Native.GW_OWNER);
            }

            // Qt/Electron 有时创建无 owner 的预览，只接管绑定后新出现且被激活的同进程窗口。
            // 绑定前已有的同进程主窗口不参与联动。
            if (Native.GetAncestor(foreground, Native.GA_ROOT) != hwnd) return null;
            uint popupPid;
            Native.GetWindowThreadProcessId(hwnd, out popupPid);
            if (popupPid == 0) return null;
            foreach (SlotState slot in _slots)
            {
                if (slot.Hwnd == IntPtr.Zero || !Native.IsWindow(slot.Hwnd) || slot.ExistingWindows.Contains(hwnd)) continue;
                uint slotPid;
                Native.GetWindowThreadProcessId(slot.Hwnd, out slotPid);
                if (slotPid == popupPid) return slot;
            }
            return null;
        }

        private void TrackPopupWindows()
        {
            foreach (IntPtr hwnd in new List<IntPtr>(_windowOpacity.Keys))
                if (!_windowOpacity[hwnd].IsValid) _windowOpacity.Remove(hwnd);
            foreach (PopupState popup in new List<PopupState>(_popups.Values))
            {
                if (!popup.IsValid)
                {
                    RestoreWindowOpacity(popup.Hwnd);
                    _popups.Remove(popup.Hwnd);
                }
            }

            IntPtr foreground = Native.GetForegroundWindow();
            Native.EnumWindows(delegate(IntPtr hwnd, IntPtr param)
            {
                if (!Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd) || Native.IsCloaked(hwnd)) return true;
                PopupState popup;
                if (!_popups.TryGetValue(hwnd, out popup))
                {
                    if (IsAuxiliaryWindow(hwnd)) return true;
                    SlotState slot = FindPopupSlot(hwnd, foreground);
                    if (slot == null) return true;
                    Rectangle rect = Native.GetWindowRectangle(hwnd);
                    if (rect.Width <= 0 || rect.Height <= 0) return true;
                    popup = new PopupState();
                    popup.Hwnd = hwnd;
                    popup.DockedHwnd = slot.Hwnd;
                    popup.ThreadId = Native.GetWindowThreadProcessId(hwnd, out popup.ProcessId);
                    popup.OriginalTopmost = (Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TOPMOST) != 0;
                    _popups.Add(hwnd, popup);
                    ApplyWindowOpacity(hwnd);
                    YswLog("track popup hwnd=" + hwnd + " owner=" + slot.Hwnd);
                }
                if (!_visible)
                {
                    popup.HiddenByUs = true;
                    Native.ShowWindow(hwnd, Native.SW_HIDE);
                }
                return true;
            }, IntPtr.Zero);
            if (_visible) RaisePopupsAboveDocked();
        }

        private void HidePopups()
        {
            foreach (PopupState popup in _popups.Values)
            {
                if (!popup.IsValid || !Native.IsWindowVisible(popup.Hwnd) || Native.IsIconic(popup.Hwnd)) continue;
                popup.HiddenByUs = true;
            }
            foreach (PopupState popup in _popups.Values)
                if (popup.IsValid && popup.HiddenByUs) Native.ShowWindow(popup.Hwnd, Native.SW_HIDE);
        }

        private void RestoreHiddenPopups()
        {
            foreach (PopupState popup in _popups.Values)
            {
                if (!popup.IsValid || !popup.HiddenByUs) continue;
                Native.ShowWindow(popup.Hwnd, Native.SW_SHOWNOACTIVATE);
                popup.HiddenByUs = false;
            }
        }

        private bool IsPointInPopup(Point point)
        {
            foreach (PopupState popup in _popups.Values)
                if (popup.IsValid && Native.IsWindowVisible(popup.Hwnd) && !Native.IsIconic(popup.Hwnd) &&
                    Native.GetWindowRectangle(popup.Hwnd).Contains(point)) return true;
            return false;
        }

        private void RaisePopupsAboveDocked()
        {
            if (!_visible || _popups.Count == 0) return;
            IntPtr foreground = Native.GetForegroundWindow();
            if (_suspendAutoHide && foreground != Handle && IsOwnWindow(foreground)) return;
            HashSet<IntPtr> docked = new HashSet<IntPtr>();
            docked.Add(Handle);
            foreach (SlotState slot in _slots)
                if (slot.Hwnd != IntPtr.Zero && Native.IsWindowVisible(slot.Hwnd)) docked.Add(slot.Hwnd);

            List<PopupState> raise = new List<PopupState>();
            foreach (PopupState popup in _popups.Values)
            {
                if (!popup.IsValid || !Native.IsWindowVisible(popup.Hwnd) || Native.IsIconic(popup.Hwnd)) continue;
                bool needsRaise = _cfg.AlwaysOnTop &&
                    (Native.GetWindowLong(popup.Hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TOPMOST) == 0;
                HashSet<IntPtr> visited = new HashSet<IntPtr>();
                for (IntPtr above = Native.GetWindow(popup.Hwnd, Native.GW_HWNDPREV);
                    !needsRaise && above != IntPtr.Zero && visited.Add(above);
                    above = Native.GetWindow(above, Native.GW_HWNDPREV))
                    if (docked.Contains(above)) needsRaise = true;
                if (needsRaise) raise.Add(popup);
            }
            // 非激活的预览先抬高，最后抬高用户当前正在操作的弹窗，避免抢占其层级。
            foreach (PopupState popup in raise)
                if (popup.Hwnd != foreground) RaisePopup(popup);
            foreach (PopupState popup in raise)
                if (popup.Hwnd == foreground) RaisePopup(popup);
        }

        private void RaisePopup(PopupState popup)
        {
            Native.SetWindowPos(popup.Hwnd,
                _cfg.AlwaysOnTop || popup.OriginalTopmost ? Native.HWND_TOPMOST : Native.HWND_TOP,
                0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
        }

        private void ApplyPopupTopmost()
        {
            foreach (PopupState popup in _popups.Values)
                if (popup.IsValid)
                    Native.SetWindowPos(popup.Hwnd,
                        _cfg.AlwaysOnTop || popup.OriginalTopmost ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST,
                        0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
            RaisePopupsAboveDocked();
        }

        private void ReleasePopups(IntPtr dockedHwnd)
        {
            foreach (PopupState popup in new List<PopupState>(_popups.Values))
            {
                if (popup.DockedHwnd != dockedHwnd) continue;
                RestoreWindowOpacity(popup.Hwnd);
                if (popup.IsValid)
                {
                    if (popup.HiddenByUs) Native.ShowWindow(popup.Hwnd, Native.SW_SHOWNOACTIVATE);
                    Native.SetWindowPos(popup.Hwnd, popup.OriginalTopmost ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST,
                        0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
                }
                _popups.Remove(popup.Hwnd);
            }
        }

        private void ApplyWindowOpacity(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return;
            WindowOpacityState state;
            if (!_windowOpacity.TryGetValue(hwnd, out state) || !state.IsValid)
            {
                state = new WindowOpacityState(hwnd);
                _windowOpacity[hwnd] = state;
            }
            state.Apply(_cfg.OpacityPercent);
        }

        private void RestoreWindowOpacity(IntPtr hwnd)
        {
            WindowOpacityState state;
            if (!_windowOpacity.TryGetValue(hwnd, out state)) return;
            state.Restore();
            // 保留有效窗口的状态，重新绑定时复用同一队列，避免旧恢复操作覆盖新的透明度设置。
            if (!state.IsValid) _windowOpacity.Remove(hwnd);
        }
    }
}
