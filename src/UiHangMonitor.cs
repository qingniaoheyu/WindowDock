using System;
using System.Diagnostics;
using System.Threading;

namespace WindowDock
{
    internal sealed class UiHangMonitor : IDisposable
    {
        private static volatile UiHangMonitor _current;
        private readonly int _uiThread;
        private readonly Action<string> _log;
        private readonly Timer _timer;
        private volatile Operation _operation;
        private long _heartbeat;
        private long _reportedStart;
        private volatile bool _disposed;

        public UiHangMonitor(Action<string> log)
        {
            _uiThread = Thread.CurrentThread.ManagedThreadId;
            _log = log;
            _heartbeat = Stopwatch.GetTimestamp();
            _current = this;
            _timer = new Timer(Check, null, 1000, 1000);
        }

        public static IDisposable Track(string name)
        {
            UiHangMonitor monitor = _current;
            if (monitor == null || monitor._disposed || Thread.CurrentThread.ManagedThreadId != monitor._uiThread)
                return null;
            Operation operation = new Operation(monitor, name, monitor._operation);
            monitor._operation = operation;
            Interlocked.Exchange(ref monitor._heartbeat, Stopwatch.GetTimestamp());
            return operation;
        }

        private void Check(object unused)
        {
            if (_disposed) return;
            Operation operation = _operation;
            long start = operation == null ? Interlocked.Read(ref _heartbeat) : operation.Start;
            long elapsed = (Stopwatch.GetTimestamp() - start) * 1000 / Stopwatch.Frequency;
            if (elapsed < 2000 || start == _reportedStart) return;
            _reportedStart = start;
            _log("UI stalled ms=" + elapsed + " phase=" + (operation == null ? "message loop" : operation.Path));
        }

        public void Dispose()
        {
            _disposed = true;
            _timer.Dispose();
            if (_current == this) _current = null;
        }

        private sealed class Operation : IDisposable
        {
            private readonly UiHangMonitor _monitor;
            private readonly Operation _parent;
            public readonly long Start = Stopwatch.GetTimestamp();
            public readonly string Path;

            public Operation(UiHangMonitor monitor, string name, Operation parent)
            {
                _monitor = monitor;
                _parent = parent;
                Path = parent == null ? name : parent.Path + " > " + name;
            }

            public void Dispose()
            {
                _monitor._operation = _parent;
                Interlocked.Exchange(ref _monitor._heartbeat, Stopwatch.GetTimestamp());
                long elapsed = (Stopwatch.GetTimestamp() - Start) * 1000 / Stopwatch.Frequency;
                if (elapsed >= 2000) _monitor._log("UI operation recovered ms=" + elapsed + " phase=" + Path);
            }
        }
    }
}
