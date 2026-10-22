using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Cxt1024Bridge
{
    // Only receipt-time bookkeeping runs on the hook. No business locks, disk
    // writes, UI Automation or external processes are allowed on that thread.
    internal sealed class HookInputPump : IDisposable
    {
        private sealed class Item
        {
            internal BridgeTrigger Trigger;
            internal IntPtr Foreground;
            internal long Generation;
            internal long ContextGeneration;
            internal int Received;
        }

        private readonly BlockingCollection<Item> _queue = new BlockingCollection<Item>(256);
        private readonly TriggerProcessor _triggers;
        private readonly Func<IntPtr> _foreground;
        private readonly Func<long> _contextGeneration;
        private readonly object _receiptGate = new object();
        private readonly PrivateTriggerDecoder _decoder = new PrivateTriggerDecoder();
        private readonly Thread _worker;
        private long _generation;
        private int _stopping;
        private int _faults;
        private string _lastFault;
        private long _received, _consumed;
        private readonly Timer _healthTimer;

        internal HookInputPump(TriggerProcessor triggers, Func<IntPtr> foreground, Func<long> contextGeneration = null)
        {
            _triggers = triggers;
            _foreground = foreground;
            _contextGeneration = contextGeneration ?? (() => 0);
            _worker = new Thread(Run) { IsBackground = true, Name = "CXT12E4 input business worker" };
            _worker.Start();
            _healthTimer = new Timer(delegate
            {
                if (Volatile.Read(ref _stopping) != 0) return;
                Logger.Info("Input health: received=" + Interlocked.Read(ref _received) +
                    "; consumed=" + Interlocked.Read(ref _consumed) + "; worker_alive=" + _worker.IsAlive);
            }, null, 60000, 60000);
        }

        internal void Receive(BridgeTrigger trigger)
        {
            if (trigger == null || Volatile.Read(ref _stopping) != 0) return;
            lock (_receiptGate)
            {
                if (Volatile.Read(ref _stopping) != 0) return;
                if (trigger.PrivateVirtualKey != 0)
                {
                    PrivateDecodeResult decoded = _decoder.Process(trigger.PrivateVirtualKey, trigger.IsDown,
                        trigger.PrivateRightControlDown, trigger.Timestamp, CaptureContext());
                    if (decoded.Fault != null)
                    {
                        FaultReceipt(decoded.Fault);
                        return;
                    }
                    for (int i = 0; i < decoded.Triggers.Count; i++)
                        EnqueueDecoded(decoded.Triggers[i]);
                    return;
                }
                // Compatibility entry point for existing pure business tests.
                EnqueueDecoded(trigger);
            }
        }

        private PrivateInputContext CaptureContext()
        {
            long before;
            PrivateInputContext context;
            do
            {
                before = _contextGeneration();
                context = new PrivateInputContext { Foreground = _foreground(), Generation = _contextGeneration() };
            }
            while (before != context.Generation);
            return context;
        }

        private void EnqueueDecoded(BridgeTrigger trigger)
        {
            if (!trigger.IsEncoder)
                trigger.PhysicalPressToken = _triggers.PublishPhysicalButtonEdge(trigger.Number, trigger.IsDown);
            PrivateInputContext context = trigger.HasReceiptContext
                ? new PrivateInputContext { Foreground = trigger.ReceiptForeground, Generation = trigger.ReceiptContextGeneration }
                : CaptureContext();
            var item = new Item { Trigger = trigger, Foreground = context.Foreground,
                Generation = Interlocked.Read(ref _generation), ContextGeneration = context.Generation,
                // Queue TTL starts when a completed gesture is queued, NOT at
                // physical make; a deliberate 700 ms hold is still fresh.
                Received = Environment.TickCount };
            try
            {
                if (_queue.TryAdd(item)) { Interlocked.Increment(ref _received); return; }
            }
            catch (InvalidOperationException) { return; }
            // Fail closed on overflow: queued releases must never become taps
            // or allow a stale 650 ms timer to fire.
            _decoder.CancelUntilReleased();
            FaultReceipt("input_queue_overflow");
        }

        private void FaultReceipt(string reason)
        {
            _triggers.InvalidatePhysicalLedger();
            long generation = Interlocked.Increment(ref _generation);
            Interlocked.Exchange(ref _lastFault, reason);
            Interlocked.Increment(ref _faults);
            // Wake a worker with no further valid gestures. Generation
            // invalidation already protects physical timers before this runs.
            try { _queue.TryAdd(new Item { Generation = generation }); }
            catch (InvalidOperationException) { }
        }

        internal static bool IsExpired(int received, int now)
        {
            return unchecked((uint)(now - received)) > 500u;
        }

        private void Run()
        {
            long generation = 0;
            foreach (Item item in _queue.GetConsumingEnumerable())
            {
                if (Volatile.Read(ref _stopping) != 0) break;
                try
                {
                    long current = Interlocked.Read(ref _generation);
                    if (generation != current)
                    {
                        _triggers.CancelBusinessInputs();
                        generation = current;
                    }
                    int faults = Interlocked.Exchange(ref _faults, 0);
                    if (faults != 0) Logger.Warn("Input receipt faults cancelled stale inputs; count=" + faults +
                        "; last=" + Interlocked.Exchange(ref _lastFault, null));
                    if (item.Generation != current) continue;
                    if (item.Trigger == null) continue;
                    if (IsExpired(item.Received, Environment.TickCount) || item.Foreground != _foreground() ||
                        item.ContextGeneration != _contextGeneration())
                    {
                        // Cancel only business state, not the physical ledger:
                        // the producer may already have published a newer edge.
                        _triggers.CancelBusinessInputs();
                        Logger.Warn("Input event discarded: stale queue event or foreground changed.");
                        continue;
                    }
                    _triggers.ProcessPublished(item.Trigger);
                }
                catch (Exception ex)
                {
                    _triggers.CancelBusinessInputs();
                    Logger.Error("Input business worker recovered from failure", ex);
                }
                finally { Interlocked.Increment(ref _consumed); }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
            _healthTimer.Dispose();
            lock (_receiptGate) _decoder.CancelUntilReleased();
            _triggers.InvalidatePhysicalLedger();
            _queue.CompleteAdding();
            if (_worker.Join(2000)) _queue.Dispose();
            else Logger.Warn("Input business worker did not stop within 2000ms.");
        }
    }
}
