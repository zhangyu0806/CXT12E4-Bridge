using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Cxt1024Bridge
{
    internal sealed class ActionDispatcher : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        private readonly object _lifecycleGate = new object();
        private readonly Thread _thread;
        private readonly Action _beforeActionLinearizationForTest;
        private volatile bool _disposeIntent;
        private volatile bool _disposeRequested;
        private readonly Timer _healthTimer;
        private long _activeSince;
        private int _stallReported;
        private int _queuedRotary;
        private readonly object _enqueueGate = new object();
        private ReasoningAdjustment _pendingReasoning;
        private IntPtr _reasoningWindow;
        private long _reasoningGeneration;
        private long _reasoningContent;
        internal const int MaximumQueuedRotary = 24;
        internal const int RotaryLifetimeMilliseconds = 250;

        internal void EnqueueReasoning(IntPtr window, long generation, long content,
            bool increase, Action<ReasoningAdjustment> action)
        {
            lock (_enqueueGate)
            {
                if (_disposeIntent) return;
                if (_pendingReasoning != null && _reasoningWindow == window &&
                    _reasoningGeneration == generation && _reasoningContent == content)
                {
                    _pendingReasoning.Add(increase);
                    return;
                }
                var batch = new ReasoningAdjustment();
                batch.Add(increase);
                _pendingReasoning = batch;
                _reasoningWindow = window;
                _reasoningGeneration = generation;
                _reasoningContent = content;
                AddToQueue(delegate
                {
                    lock (_enqueueGate)
                    {
                        if (ReferenceEquals(_pendingReasoning, batch)) _pendingReasoning = null;
                    }
                    action(batch);
                });
            }
        }

        internal static bool IsRotaryExpired(int received, int now)
        {
            return unchecked((uint)(now - received)) > RotaryLifetimeMilliseconds;
        }

        // Do not drop buttons or holds. Only rotary steps have a bounded
        // backlog and expiry, so an old turn cannot replay after a slow action.
        internal bool EnqueueRotary(Action action, Func<int> clock = null)
        {
            if (action == null || _disposeIntent) return false;
            if (Interlocked.Increment(ref _queuedRotary) > MaximumQueuedRotary)
            {
                Interlocked.Decrement(ref _queuedRotary);
                return false;
            }
            Func<int> now = clock ?? (() => Environment.TickCount);
            int received = now();
            Enqueue(delegate
            {
                Interlocked.Decrement(ref _queuedRotary);
                if (!IsRotaryExpired(received, now())) action();
            });
            return true;
        }

        internal bool IsDisposed
        {
            get { return _disposeRequested; }
        }

        internal bool IsDisposalPending
        {
            get { return _disposeIntent; }
        }

        internal ActionDispatcher(Action beforeActionLinearizationForTest = null)
        {
            _beforeActionLinearizationForTest = beforeActionLinearizationForTest;
            _thread = new Thread(Run);
            _thread.Name = "CXT 12E4 action dispatcher";
            _thread.IsBackground = true;
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _healthTimer = new Timer(delegate
            {
                long since = Interlocked.Read(ref _activeSince);
                if (!_disposeIntent && since != 0 && unchecked((uint)(Environment.TickCount - (int)since)) > 10000u &&
                    Interlocked.Exchange(ref _stallReported, 1) == 0)
                    Logger.Warn("Action dispatcher stalled >10s; no parallel retry will be issued.");
            }, null, 2000, 2000);
        }

        internal void Enqueue(Action action)
        {
            lock (_enqueueGate)
            {
                // Every other action is an ordering barrier (especially presets
                // and task navigation). Never merge turns across that action.
                _pendingReasoning = null;
                AddToQueue(action);
            }
        }

        private void AddToQueue(Action action)
        {
            if (action == null || _disposeIntent)
                return;
            try
            {
                _queue.Add(action);
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void Run()
        {
            foreach (Action action in _queue.GetConsumingEnumerable())
            {
                Action testHook = _beforeActionLinearizationForTest;
                if (testHook != null)
                    testHook();
                if (_disposeIntent)
                    break;
                lock (_lifecycleGate)
                {
                    // Dispose publishes its intent first, but can publish the final
                    // request only while holding this same gate. Acquiring the gate
                    // linearizes this action as in-flight; otherwise it is rejected
                    // without executing its first instruction.
                    if (_disposeIntent || _disposeRequested)
                        break;
                    try
                    {
                        Interlocked.Exchange(ref _activeSince, Environment.TickCount);
                        action();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("Dispatched action failed", ex);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _activeSince, 0);
                        if (Interlocked.Exchange(ref _stallReported, 0) != 0)
                            Logger.Info("Action dispatcher recovered after a slow action.");
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_disposeIntent)
                return;
            // Stop new enqueues and prevent the worker from taking another action
            // while this thread waits for an already-running action to leave the
            // lifecycle gate. _disposeRequested is published only inside the gate,
            // so it cannot race between the worker's final check and action().
            _disposeIntent = true;
            _healthTimer.Dispose();
            lock (_lifecycleGate)
            {
                if (_disposeRequested)
                    return;
                _disposeRequested = true;
                Action discarded;
                while (_queue.TryTake(out discarded))
                {
                }
                _queue.CompleteAdding();
            }
            if (!_thread.Join(1500))
            {
                Logger.Warn("Action dispatcher did not stop within 1500ms.");
                return;
            }
            _queue.Dispose();
        }
    }
}
