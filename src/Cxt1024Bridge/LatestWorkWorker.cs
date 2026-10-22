using System;
using System.Threading;

namespace Cxt1024Bridge
{
    // For optional state mirrors, never for key actions: keep only the newest
    // pending snapshot while a previous file write is blocked.
    internal sealed class LatestWorkWorker : IDisposable
    {
        private readonly object _gate = new object();
        private readonly AutoResetEvent _ready = new AutoResetEvent(false);
        private readonly Thread _thread;
        private Action _pending;
        private bool _stopping;

        internal LatestWorkWorker()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "CXT12E4 optional state export" };
            _thread.Start();
        }

        internal void Submit(Action work)
        {
            lock (_gate)
            {
                if (_stopping) return;
                _pending = work;
                _ready.Set();
            }
        }

        private void Run()
        {
            while (true)
            {
                _ready.WaitOne();
                Action work;
                lock (_gate)
                {
                    if (_stopping) return;
                    work = _pending;
                    _pending = null;
                }
                try { if (work != null) work(); }
                catch (Exception ex) { Logger.Error("Optional state export failed", ex); }
            }
        }

        public void Dispose()
        {
            lock (_gate) { if (_stopping) return; _stopping = true; _pending = null; _ready.Set(); }
            if (_thread.Join(1000)) _ready.Dispose();
        }
    }
}
