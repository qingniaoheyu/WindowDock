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
                name.IndexOf("IME.UIManager", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            // 普通 Qt 悬浮提示和提示窗口的阴影仍然跳过；ToolTipSaveBits 是 UME 的
            // 表情/贴纸面板本体，需要作为弹层参与置顶，否则会被停靠槽位窗口盖住。
            if (name.IndexOf("QWindowToolTip", StringComparison.Ordinal) >= 0 &&
                (name.IndexOf("SaveBits", StringComparison.Ordinal) < 0 ||
                 name.IndexOf("DropShadowSaveBits", StringComparison.Ordinal) >= 0))
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
            // 阴影窗口总是比 owner 四周各大一圈；与 owner 同尺寸的是 Qt Quick 的
            // 透明覆盖窗口（表情面板等弹层绘制在这里），必须作为弹层接管。
            return ownerRect.Width > 0 && ownerRect.Height > 0 &&
                rect.Width > ownerRect.Width && rect.Height > ownerRect.Height &&
                rect.Contains(ownerRect);
        }

        private static bool IsNotificationWindow(IntPtr hwnd)
        {
            // Yealink UME 等 Qt 应用用 ToolSaveBits 工具窗口显示“新消息”提醒气泡。
            return Native.GetWindowClassName(hwnd).IndexOf("QWindowToolSaveBits", StringComparison.Ordinal) >= 0;
        }

        private void ClearUnreadForForeground(IntPtr foreground)
        {
            if (foreground == IntPtr.Zero) return;
            IntPtr root = Native.GetAncestor(foreground, Native.GA_ROOT);
            for (int i = 0; i < _slots.Count; i++)
            {
                SlotState s = _slots[i];
                if (!s.Unread || s.Hwnd == IntPtr.Zero) continue;
                if (root == s.Hwnd || foreground == s.Hwnd) SetSlotUnread(i, false);
            }
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
                // 工具窗口（消息提醒气泡等）不参与“绑定前已存在”快照，
                // 否则绑定后弹出的通知无法被接管置顶。
                if (pid == processId && Native.IsWindowVisible(hwnd) &&
                    (Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) == 0)
                    slot.ExistingWindows.Add(hwnd);
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

            uint popupPid;
            Native.GetWindowThreadProcessId(hwnd, out popupPid);
            if (popupPid == 0) return null;

            // 无 owner 的 Qt 工具窗口（表情/贴纸面板、消息提醒等）按进程归属到已停靠窗口。
            // 绑定前已有的窗口不参与，避免把应用的其他主窗口误判为弹层。
            bool toolWindow = (Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0;
            foreach (SlotState slot in _slots)
            {
                if (slot.Hwnd == IntPtr.Zero || !Native.IsWindow(slot.Hwnd) || slot.ExistingWindows.Contains(hwnd)) continue;
                uint slotPid;
                Native.GetWindowThreadProcessId(slot.Hwnd, out slotPid);
                if (slotPid != popupPid) continue;
                if (toolWindow || Native.GetAncestor(foreground, Native.GA_ROOT) == hwnd) return slot;
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
                    int exStyle = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
                    popup.OriginalTopmost = (exStyle & Native.WS_EX_TOPMOST) != 0;
                    bool notification = IsNotificationWindow(hwnd);
                    // 通知气泡即使挂在主窗口下也不能随停靠栏隐藏，否则错过消息时看不到提示。
                    popup.Transient = notification || ((exStyle & Native.WS_EX_TOOLWINDOW) != 0 &&
                        Native.GetWindow(hwnd, Native.GW_OWNER) == IntPtr.Zero);
                    _popups.Add(hwnd, popup);
                    ApplyWindowOpacity(hwnd);
                    YswLog("track popup hwnd=" + hwnd + " cls=" + Native.GetWindowClassName(hwnd) +
                        " owner=" + slot.Hwnd + (popup.Transient ? " transient" : "") +
                        (notification ? " notification" : ""));
                    if (notification) SetSlotUnread(_slots.IndexOf(slot), true);
                }
                // 停靠栏隐藏期间新出现的弹窗通常是应用自己的消息提醒，保持显示，
                // 只有原本就随停靠栏一起隐藏的弹窗才会在显示时恢复。
                return true;
            }, IntPtr.Zero);
            if (_visible) RaisePopupsAboveDocked();
        }

        private void HidePopups()
        {
            foreach (PopupState popup in _popups.Values)
            {
                if (popup.Transient) continue;
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
            if (!_visible || _popups.Count == 0 || HasManagedDialog) return;
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
            if (HasManagedDialog) return;
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
