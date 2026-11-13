using System;
using System.Threading;

namespace WindowDock
{
    internal sealed class WindowOpacityState
    {
        private readonly IntPtr _hwnd;
        private readonly uint _processId;
        private readonly uint _threadId;
        private readonly bool _originalLayered;
        private readonly bool _canApply;
        private readonly uint _colorKey;
        private readonly byte _alpha;
        private readonly uint _flags;
        private bool _addedLayered;
        private int _appliedPercent = 100;
        private readonly object _gate = new object();
        private readonly ManualResetEventSlim _idle = new ManualResetEventSlim(true);
        private int _pendingPercent = 100;
        private int _version;
        private bool _workerRunning;

        public WindowOpacityState(IntPtr hwnd)
        {
            _hwnd = hwnd;
            _threadId = Native.GetWindowThreadProcessId(hwnd, out _processId);
            _originalLayered = Native.IsLayeredWindow(hwnd);
            _alpha = 255;
            _canApply = !Native.IsQtWindow(hwnd) && (!_originalLayered ||
                Native.GetLayeredWindowAttributes(hwnd, out _colorKey, out _alpha, out _flags));
            // 使用 UpdateLayeredWindow 的应用弹层不能改用属性透明度，否则会破坏原有绘制。
            // Qt 会在主窗口、聊天记录等界面切换时改用该 API，即使当前尚未带有分层样式，
            // 也不能先设置属性透明度，否则之后的 UpdateLayeredWindow 会失败并导致内容消失。
        }

        public bool IsValid
        {
            get
            {
                uint pid;
                return Native.IsWindow(_hwnd) &&
                    Native.GetWindowThreadProcessId(_hwnd, out pid) == _threadId && pid == _processId;
            }
        }

        public void Apply(int percent)
        {
            Queue(percent);
        }

        public void Restore()
        {
            Queue(100);
        }

        public void WaitForIdle(int milliseconds)
        {
            _idle.Wait(milliseconds);
        }

        private void Queue(int percent)
        {
            if (!IsValid || !_canApply) return;
            if (!Native.IsForeignWindowThread(_hwnd)) { ApplyCore(percent); return; }
            lock (_gate)
            {
                _pendingPercent = percent;
                _version++;
                if (_workerRunning) return;
                _workerRunning = true;
                _idle.Reset();
                ThreadPool.QueueUserWorkItem(delegate { Drain(); });
            }
        }

        private void Drain()
        {
            while (true)
            {
                int percent, version;
                lock (_gate) { percent = _pendingPercent; version = _version; }
                try { ApplyCore(percent); }
                catch (Exception ex)
                {
                    try
                    {
                        string path = System.IO.Path.Combine(System.Windows.Forms.Application.StartupPath, "WindowDock_dbg.log");
                        System.IO.File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") +
                            " [ysw] opacity worker hwnd=" + _hwnd + " error=" + ex + "\r\n");
                    }
                    catch (Exception) { }
                }
                lock (_gate)
                {
                    if (version != _version) continue;
                    _workerRunning = false;
                    _idle.Set();
                    return;
                }
            }
        }

        private void ApplyCore(int percent)
        {
            if (!IsValid || !_canApply || _appliedPercent == percent) return;
            if (percent == 100)
            {
                RestoreCore();
                return;
            }
            if (!_originalLayered && !_addedLayered)
            {
                int style = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
                Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, style | Native.WS_EX_LAYERED);
                if (!Native.IsLayeredWindow(_hwnd)) return;
                _addedLayered = true;
            }
            byte baseAlpha = _originalLayered && (_flags & Native.LWA_ALPHA) != 0 ? _alpha : (byte)255;
            byte alpha = (byte)Math.Max(1, baseAlpha * percent / 100);
            if (Native.SetLayeredWindowAttributes(_hwnd, _colorKey, alpha, _flags | Native.LWA_ALPHA))
                _appliedPercent = percent;
            else if (_addedLayered) RestoreCore();
        }

        private void RestoreCore()
        {
            if (!IsValid) return;
            if (_addedLayered)
            {
                int style = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
                Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, style & ~Native.WS_EX_LAYERED);
                _addedLayered = false;
                Native.InvalidateRect(_hwnd, IntPtr.Zero, false);
            }
            else if (_originalLayered && _canApply && _appliedPercent != 100)
                Native.SetLayeredWindowAttributes(_hwnd, _colorKey, _alpha, _flags);
            _appliedPercent = 100;
        }
    }
}
