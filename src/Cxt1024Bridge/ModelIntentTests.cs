using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Automation;

namespace Cxt1024Bridge
{
    internal static class ModelIntentTests
    {
        private static void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("Model intent regression: " + name);
        }

        internal static void Run()
        {
            for (int length = 1; length <= 10; length++)
                for (int bits = 0; bits < (1 << length); bits++)
                {
                    var map = new ReasoningAdjustment();
                    for (int step = 0; step < length; step++) map.Add((bits & (1 << step)) != 0);
                    for (int start = 0; start <= ReasoningAdjustment.SolUltraState; start++)
                    {
                        int expected = start;
                        for (int step = 0; step < length; step++)
                        {
                            bool clockwise = (bits & (1 << step)) != 0;
                            expected = expected == 10 ? 9 : expected == 11 ? 4 :
                                Math.Max(0, Math.Min(9, expected + (clockwise ? 1 : -1)));
                        }
                        bool astra = start >= ReasoningAdjustment.AstraOffset && start != ReasoningAdjustment.SolUltraState;
                        int rank = start == ReasoningAdjustment.SolUltraState ? 5 : astra ? start - ReasoningAdjustment.AstraOffset : start;
                        Check(map.Target(astra, rank) == expected, "all ordered boundary transitions");
                    }
                    Check(map.Target(true, -1) == -1 && map.Target(false, 6) == -1, "unknown rank fails closed");
                }

            Check(ReasoningAdjustment.Resolve(4, true) == 5, "Sol Max crosses into Astra Light");
            Check(ReasoningAdjustment.Resolve(5, false) == 4, "Astra Light crosses into Sol Max");
            Check(ReasoningAdjustment.Resolve(0, false) == 0 && ReasoningAdjustment.Resolve(9, true) == 9,
                "rotary ladder clamps at both ends");
            Check(ReasoningAdjustment.Resolve(10, true) == 9 && ReasoningAdjustment.Resolve(10, false) == 9,
                "Astra Ultra exits through Astra Max");
            Check(ReasoningAdjustment.Resolve(11, true) == 4 && ReasoningAdjustment.Resolve(11, false) == 4,
                "manually chosen Sol Ultra exits through Sol Max");

            int attempts = 0, fallbacks = 0;
            Check(ChatGptAutomation.RetryReplacedMenu(() => true, delegate
            {
                if (++attempts == 1) throw new ElementNotAvailableException();
                return true;
            }, delegate { fallbacks++; return true; }), "reacquire replaced menu");
            Check(attempts == 2 && fallbacks == 0, "one replacement retry");
            attempts = 0;
            Check(ChatGptAutomation.RetryReplacedMenu(() => true, delegate
            {
                attempts++; throw new ElementNotAvailableException();
            }, delegate { fallbacks++; return true; }), "bounded stale provider fallback");
            Check(attempts == 2 && fallbacks == 1, "not an unbounded retry loop");
            bool current = true;
            Check(!ChatGptAutomation.RetryReplacedMenu(() => current, delegate
            {
                current = false; throw new ElementNotAvailableException();
            }, delegate { throw new Exception("must not send fallback input"); }), "focus loss cancels retry");
            Check(!new ChatGptAutomation().RunModelAction(() => false,
                delegate { throw new Exception("stale preset must not run"); }), "stale model context");
            QueueChecks();
            Check(ActionEngine.CanRunQueuedModelTurn(0, 251, () => true), "slow model may retain anchored turns");
            Check(!ActionEngine.CanRunQueuedModelTurn(0, 251, () => false), "manual task switch rejects old turns");
            Check(!ActionEngine.CanRunQueuedModelTurn(0, 251, null), "unknown task does not retain old turns");
            Check(ActionEngine.CanRunQueuedModelTurn(0, 20, null), "fresh turn preserves normal behavior");
        }

        private static void QueueChecks()
        {
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            using (var completed = new ManualResetEvent(false))
            {
                var dispatcher = new ActionDispatcher();
                var results = new List<int>();
                try
                {
                    dispatcher.Enqueue(delegate { entered.Set(); release.WaitOne(5000); });
                    Check(entered.WaitOne(2000), "worker reached slow action");
                    Action<ReasoningAdjustment> apply = delegate(ReasoningAdjustment batch) { results.Add(batch.Target(true, 4)); };
                    dispatcher.EnqueueReasoning(new IntPtr(1), 1, 1, true, apply);
                    dispatcher.EnqueueReasoning(new IntPtr(1), 1, 1, false, apply);
                    dispatcher.Enqueue(delegate { results.Add(99); }); // preset/task barrier
                    dispatcher.EnqueueReasoning(new IntPtr(1), 1, 1, true, apply);
                    dispatcher.EnqueueReasoning(new IntPtr(1), 1, 2, false, apply); // task changed
                    dispatcher.Enqueue(delegate { completed.Set(); });
                    Thread.Sleep(300); // exceeds old 250ms expiry
                    release.Set();
                    Check(completed.WaitOne(3000), "worker completed batches");
                    Check(results.Count == 4 && results[0] == 8 && results[1] == 99 &&
                        results[2] == 9 && results[3] == 8, "coalescing, expiry, preset order, task boundary");
                }
                finally { release.Set(); dispatcher.Dispose(); }
            }
        }
    }
}
