using System;
using System.Threading;

namespace Cxt1024Bridge
{
    internal sealed class TriggerProcessor : IDisposable
    {
        private const int ReverseTaskbarChordWindowMs = 250;
        internal const int GlobalTaskHoldMs = 650;
        private readonly object _processGate = new object();
        private readonly object _physicalGate = new object();
        private readonly object _gate = new object();
        private readonly Func<int> _currentLayer;
        private readonly Func<IntPtr> _currentForeground;
        private readonly Func<long> _currentContextGeneration;
        private readonly Action<int, int, bool, IntPtr, long> _buttonAction;
        private readonly Action<int, int, EncoderGesture, IntPtr, long> _encoderAction;
        private readonly Action<int, IntPtr, long> _focusTaskAction;
        private readonly Action<int, IntPtr, long> _focusTerminalSessionAction;
        private readonly Action<int, IntPtr, long> _taskbarAction;
        private readonly ButtonState[] _buttons = new ButtonState[13];
        private readonly int[] _lastEncoderAt = new int[5];
        private readonly EncoderGesture[] _lastEncoderGesture = new EncoderGesture[5];
        private readonly bool[] _hasEncoderGesture = new bool[5];
        private readonly bool[] _physicalDown = new bool[13];
        private readonly long[] _physicalPressToken = new long[13];
        private readonly TriggerProcessorTestHooks _testHooks;
        private bool _key12Chord;
        private int _pendingTaskbarSlot;
        private long _nextDownSequence;
        private long _nextPhysicalPressToken;
        private bool _stopping;
        private bool _disposed;

        internal TriggerProcessor(LayerManager layers, ActionEngine actions)
            : this(
                delegate { return layers.CurrentLayer; },
                delegate { return NativeMethods.GetForegroundWindow(); },
                delegate { return layers.ContextGeneration; },
                actions.Button,
                actions.Encoder,
                actions.FocusTaskForKey,
                actions.FocusTerminalSessionForKey,
                actions.LaunchTaskbarSlot)
        {
        }

        internal TriggerProcessor(
            Func<int> currentLayer,
            Func<IntPtr> currentForeground,
            Func<long> currentContextGeneration,
            Action<int, int, bool, IntPtr, long> buttonAction,
            Action<int, int, EncoderGesture, IntPtr, long> encoderAction,
            Action<int, IntPtr, long> focusTaskAction,
            Action<int, IntPtr, long> focusTerminalSessionAction,
            Action<int, IntPtr, long> taskbarAction,
            TriggerProcessorTestHooks testHooks = null)
        {
            if (currentLayer == null || currentForeground == null || currentContextGeneration == null ||
                buttonAction == null || encoderAction == null || focusTaskAction == null || focusTerminalSessionAction == null ||
                taskbarAction == null)
                throw new ArgumentNullException("TriggerProcessor dependency");
            _currentLayer = currentLayer;
            _currentForeground = currentForeground;
            _currentContextGeneration = currentContextGeneration;
            _buttonAction = buttonAction;
            _encoderAction = encoderAction;
            _focusTaskAction = focusTaskAction;
            _focusTerminalSessionAction = focusTerminalSessionAction;
            _taskbarAction = taskbarAction;
            _testHooks = testHooks;
            for (int i = 0; i < _buttons.Length; i++)
                _buttons[i] = new ButtonState();
        }

        internal void Process(BridgeTrigger trigger)
        {
            if (trigger == null)
                return;
            if (!trigger.IsEncoder && trigger.Number >= 1 && trigger.Number <= 12)
                trigger.PhysicalPressToken = PublishPhysicalButtonEdge(trigger.Number, trigger.IsDown);
            ProcessPublished(trigger);
        }

        // Raw Input calls this on its window thread before the business event is
        // queued.  It deliberately takes only _physicalGate, so an arriving
        // key-up becomes visible to a 650 ms timer even while the consumer is
        // delayed by logging or an action dispatch.
        internal long PublishPhysicalButtonEdge(int key, bool isDown)
        {
            if (key < 1 || key > 12)
                return 0;
            return ObservePhysicalEdge(key, isDown);
        }

        // Consumes an edge whose physical state has already been published.
        // Only the single ordered Raw Input consumer should call this in the
        // production path.  Process remains as a compatibility entry point for
        // pure software tests.
        internal void ProcessPublished(BridgeTrigger trigger)
        {
            if (trigger == null)
                return;
            if (trigger.IsEncoder)
            {
                lock (_gate)
                {
                    if (_disposed || _stopping)
                        return;
                    int layer = _currentLayer();
                    if (trigger.IsDown && ShouldDispatchEncoder(
                        layer,
                        trigger.Number,
                        trigger.EncoderGesture,
                        unchecked((int)trigger.Timestamp)))
                    {
                        IntPtr foreground;
                        long generation;
                        CaptureCurrentContext(out foreground, out generation);
                        if (trigger.HasReceiptContext)
                        {
                            if (trigger.ReceiptForeground != foreground ||
                                trigger.ReceiptContextGeneration != generation)
                                return;
                            foreground = trigger.ReceiptForeground;
                            generation = trigger.ReceiptContextGeneration;
                        }
                        NotifyActionDispatchStarting("encoder", trigger.Number);
                        _encoderAction(layer, trigger.Number, trigger.EncoderGesture, foreground, generation);
                    }
                    return;
                }
            }
            if (trigger.Number < 1 || trigger.Number > 12)
                return;
            if (trigger.PhysicalPressToken == 0)
                return;

            // Serialize physical button edges, but publish each edge before waiting
            // for the state-machine lock. A key-up that has already reached Process
            // must therefore be visible to a due timer even if that timer currently
            // owns _gate. _physicalGate is always released before _gate is acquired;
            // timer callbacks may safely take them in the opposite order.
            lock (_processGate)
            {
                lock (_gate)
                {
                    if (_disposed || _stopping)
                        return;
                    if (trigger.IsDown)
                        ButtonDown(trigger.Number, trigger.Timestamp, trigger.PhysicalPressToken);
                    else
                        ButtonUp(trigger.Number, trigger.Timestamp, trigger.PhysicalPressToken);
                }
            }
        }

        private void ButtonDown(int key, long timestamp, long physicalPressToken)
        {
            ButtonState state = _buttons[key];
            if (state.Down)
                return;

            if (key >= 1 && key <= 11 && _buttons[12].Down && ContextStillMatches(_buttons[12]))
            {
                state.Down = true;
                state.Suppress = true;
                state.Layer = _currentLayer();
                state.DownAt = unchecked((int)timestamp);
                CaptureContext(state);
                state.DownSequence = ++_nextDownSequence;
                state.PhysicalPressToken = physicalPressToken;
                // Once key 12's 650 ms tmux action has fired, the press is
                // consumed. A late selector must not retroactively turn the
                // same physical press into a second taskbar action.
                if (!_buttons[12].LongTriggered && !_key12Chord)
                    LaunchTaskbarChord(key);
                return;
            }

            state.Down = true;
            state.Suppress = false;
            state.LongTriggered = false;
            state.Layer = _currentLayer();
            state.DownAt = unchecked((int)timestamp);
            CaptureContext(state);
            state.DownSequence = ++_nextDownSequence;
            state.PhysicalPressToken = physicalPressToken;

            if (IsTerminalSessionKey(key))
            {
                if (key == 12)
                {
                    BeginTaskbarKey(unchecked((int)timestamp));
                    if (_key12Chord)
                        return;
                }
                state.TerminalSessionHoldEligible = !AnyOtherTerminalSessionKeyDown(key);
                if (state.TerminalSessionHoldEligible)
                    BeginTerminalSessionHold(state, key);
                return;
            }
            if (IsTaskKey(key))
            {
                state.TaskHoldEligible = !AnyOtherTaskKeyDown(key);
                if (state.TaskHoldEligible)
                    BeginGlobalTaskHold(state, key);
                return;
            }
        }

        private void ButtonUp(int key, long timestamp, long physicalPressToken)
        {
            ButtonState state = _buttons[key];
            if (!state.Down || state.PhysicalPressToken != physicalPressToken)
                return;
            state.Down = false;

            if (state.Suppress)
            {
                CancelTimer(ref state.TaskHoldTimer);
                state.TaskHoldEligible = false;
                state.TerminalSessionHoldEligible = false;
                state.Suppress = false;
                TryDispatchPendingTaskbarChord();
                return;
            }
            if (key == 12 && _key12Chord)
            {
                FinishTaskbarChord();
                return;
            }
            if (IsTerminalSessionKey(key))
            {
                CancelTimer(ref state.TaskHoldTimer);
                int heldMs = Elapsed(state.DownAt, unchecked((int)timestamp));
                bool contextMatches = ContextStillMatches(state);
                if (state.TerminalSessionHoldEligible && !state.LongTriggered &&
                    heldMs >= GlobalTaskHoldMs && contextMatches)
                {
                    state.LongTriggered = true;
                    DispatchTerminalSessionAction(key, state);
                }
                else if (!state.LongTriggered && heldMs < GlobalTaskHoldMs && contextMatches)
                {
                    NotifyActionDispatchStarting("button", key);
                    _buttonAction(state.Layer, key, false, state.ForegroundWindow, state.ContextGeneration);
                }
                state.LongTriggered = false;
                state.TerminalSessionHoldEligible = false;
                return;
            }
            if (IsTaskKey(key))
            {
                CancelTimer(ref state.TaskHoldTimer);
                int heldMs = Elapsed(state.DownAt, unchecked((int)timestamp));
                bool contextMatches = ContextStillMatches(state);

                // The timer normally dispatches the global task action at 650 ms.
                // It can run late when the CLR thread pool is busy, so key-up must
                // use the event timestamps as the authoritative fallback. Without
                // this branch, a genuine long press released before the delayed
                // callback runs is incorrectly dispatched as the layer's short
                // action (for example, Ctrl+D in the browser layer).
                if (state.TaskHoldEligible && !state.LongTriggered &&
                    heldMs >= GlobalTaskHoldMs && contextMatches)
                {
                    state.LongTriggered = true;
                    DispatchTaskAction(key, state);
                }
                else if (!state.LongTriggered && heldMs < GlobalTaskHoldMs && contextMatches)
                {
                    NotifyActionDispatchStarting("button", key);
                    _buttonAction(state.Layer, key, false, state.ForegroundWindow, state.ContextGeneration);
                }
                state.LongTriggered = false;
                state.TaskHoldEligible = false;
                return;
            }
            if (ContextStillMatches(state))
            {
                NotifyActionDispatchStarting("button", key);
                _buttonAction(state.Layer, key, false, state.ForegroundWindow, state.ContextGeneration);
            }
        }

        private void BeginGlobalTaskHold(ButtonState button, int key)
        {
            CancelTimer(ref button.TaskHoldTimer);
            int layer = button.Layer;
            long physicalPressToken = button.PhysicalPressToken;
            button.TaskHoldTimer = new Timer(delegate
            {
                NotifyTimerCallbackStarting("global-task", key, physicalPressToken);
                lock (_gate)
                {
                    ButtonState current = _buttons[key];
                    if (_disposed || _stopping || !current.Down || current.Suppress || !current.TaskHoldEligible || current.Layer != layer ||
                        current.LongTriggered || !TimerPressStillCurrent(current, key, physicalPressToken) ||
                        !ContextStillMatches(current))
                        return;
                    current.LongTriggered = true;
                    DispatchTaskAction(key, current);
                }
            }, null, GlobalTaskHoldMs, Timeout.Infinite);
        }

        private void BeginTerminalSessionHold(ButtonState button, int key)
        {
            CancelTimer(ref button.TaskHoldTimer);
            long physicalPressToken = button.PhysicalPressToken;
            button.TaskHoldTimer = new Timer(delegate
            {
                NotifyTimerCallbackStarting("terminal-session", key, physicalPressToken);
                lock (_gate)
                {
                    ButtonState current = _buttons[key];
                    if (_disposed || _stopping || !current.Down || current.Suppress ||
                        !current.TerminalSessionHoldEligible || current.LongTriggered ||
                        !TimerPressStillCurrent(current, key, physicalPressToken) ||
                        !ContextStillMatches(current))
                        return;
                    current.LongTriggered = true;
                    DispatchTerminalSessionAction(key, current);
                }
            }, null, GlobalTaskHoldMs, Timeout.Infinite);
        }

        private static bool IsTaskKey(int key)
        {
            return key == 1 || key == 2 || key == 3 || key == 5 || key == 6 || key == 7;
        }

        internal static bool IsTerminalSessionKey(int key)
        {
            return key == 9 || key == 10 || key == 11 || key == 12 || key == 8 || key == 4;
        }

        private bool AnyOtherTerminalSessionKeyDown(int key)
        {
            for (int candidate = 1; candidate <= 12; candidate++)
            {
                if (candidate != key && IsTerminalSessionKey(candidate) && _buttons[candidate].Down)
                    return true;
            }
            return false;
        }

        private bool AnyOtherTaskKeyDown(int key)
        {
            for (int candidate = 1; candidate <= 7; candidate++)
            {
                if (candidate != key && IsTaskKey(candidate) && _buttons[candidate].Down)
                    return true;
            }
            return false;
        }

        private bool ShouldDispatchEncoder(int layer, int encoder, EncoderGesture gesture, int timestamp)
        {
            if (gesture == EncoderGesture.Press || encoder < 1 || encoder > 4)
                return true;

            int minimumInterval = EncoderMinimumInterval(layer, encoder);
            if (_hasEncoderGesture[encoder] &&
                _lastEncoderGesture[encoder] == gesture &&
                Elapsed(_lastEncoderAt[encoder], timestamp) < minimumInterval)
                return false;

            _hasEncoderGesture[encoder] = true;
            _lastEncoderAt[encoder] = timestamp;
            _lastEncoderGesture[encoder] = gesture;
            return true;
        }

        internal static int EncoderMinimumInterval(int layer, int encoder)
        {
            if (layer == 1 && encoder == 1)
                return 650;
            if (layer == 1 && encoder == 2)
                return 300;
            return 35;
        }

        private void BeginTaskbarKey(int timestamp)
        {
            _key12Chord = false;
            _pendingTaskbarSlot = 0;

            // Normally key 12 is pressed first. Also accept a near-simultaneous
            // selector-first chord so the result does not depend on switch scan order.
            int owner = 0;
            long ownerSequence = long.MaxValue;
            for (int key = 1; key <= 11; key++)
            {
                ButtonState selector = _buttons[key];
                int elapsed = Elapsed(selector.DownAt, timestamp);
                if (!selector.Down || selector.Suppress || elapsed < 0 ||
                    elapsed > ReverseTaskbarChordWindowMs ||
                    !CapturedContextMatches(selector, _buttons[12]))
                    continue;
                if (selector.DownSequence < ownerSequence)
                {
                    owner = key;
                    ownerSequence = selector.DownSequence;
                }
            }
            for (int key = 1; key <= 11; key++)
            {
                ButtonState selector = _buttons[key];
                int elapsed = Elapsed(selector.DownAt, timestamp);
                if (!selector.Down || selector.Suppress || elapsed < 0 ||
                    elapsed > ReverseTaskbarChordWindowMs ||
                    !CapturedContextMatches(selector, _buttons[12]))
                    continue;
                CancelSelectorHold(selector, key);
                selector.Suppress = true;
            }
            if (owner > 0)
                LaunchTaskbarChord(owner);
        }

        private void LaunchTaskbarChord(int key)
        {
            CancelTimer(ref _buttons[12].TaskHoldTimer);
            _buttons[12].TerminalSessionHoldEligible = false;
            _buttons[12].LongTriggered = false;
            _key12Chord = true;
            _pendingTaskbarSlot = key;
            Logger.Info("Key 12 chord queued Windows taskbar slot " + key + " until both physical keys are released.");
        }

        private void FinishTaskbarChord()
        {
            if (_key12Chord)
                TryDispatchPendingTaskbarChord();
        }

        private void TryDispatchPendingTaskbarChord()
        {
            int slot = _pendingTaskbarSlot;
            if (slot <= 0 || _buttons[12].Down || _buttons[slot].Down)
                return;

            _pendingTaskbarSlot = 0;
            _key12Chord = false;
            if (!ContextStillMatches(_buttons[12]))
            {
                Logger.Info("Key 12 chord cancelled because foreground changed while keys were held.");
                return;
            }
            NotifyActionDispatchStarting("taskbar-chord", slot);
            _taskbarAction(
                slot,
                _buttons[12].ForegroundWindow,
                _buttons[12].ContextGeneration);
            Logger.Info("Key 12 chord launched Windows taskbar slot " + slot + " after physical key release.");
        }

        private void CancelSelectorHold(ButtonState selector, int key)
        {
            CancelTimer(ref selector.TaskHoldTimer);
            selector.LongTriggered = false;
            selector.TaskHoldEligible = false;
            selector.TerminalSessionHoldEligible = false;
        }

        private long ObservePhysicalEdge(int key, bool isDown)
        {
            long physicalPressToken;
            lock (_physicalGate)
            {
                if (isDown && !_physicalDown[key])
                {
                    _physicalDown[key] = true;
                    _nextPhysicalPressToken = unchecked(_nextPhysicalPressToken + 1);
                    if (_nextPhysicalPressToken == 0)
                        _nextPhysicalPressToken = 1;
                    _physicalPressToken[key] = _nextPhysicalPressToken;
                }
                else if (!isDown)
                {
                    _physicalDown[key] = false;
                }
                physicalPressToken = _physicalPressToken[key];
            }

            TriggerProcessorTestHooks hooks = _testHooks;
            if (hooks != null && hooks.PhysicalEdgeObserved != null)
                hooks.PhysicalEdgeObserved(key, isDown, physicalPressToken);
            return physicalPressToken;
        }

        internal void InvalidatePhysicalLedger()
        {
            lock (_physicalGate)
                InvalidatePhysicalLedgerUnderLock();
        }

        private void InvalidatePhysicalLedgerUnderLock()
        {
            for (int key = 1; key < _physicalDown.Length; key++)
            {
                _physicalDown[key] = false;
                _nextPhysicalPressToken = unchecked(_nextPhysicalPressToken + 1);
                if (_nextPhysicalPressToken == 0)
                    _nextPhysicalPressToken = 1;
                _physicalPressToken[key] = _nextPhysicalPressToken;
            }
        }

        private bool TimerPressStillCurrent(ButtonState state, int key, long physicalPressToken)
        {
            if (state.PhysicalPressToken != physicalPressToken)
                return false;
            lock (_physicalGate)
            {
                return _physicalDown[key] && _physicalPressToken[key] == physicalPressToken;
            }
        }

        private void NotifyTimerCallbackStarting(string timerName, int key, long physicalPressToken)
        {
            TriggerProcessorTestHooks hooks = _testHooks;
            if (hooks != null && hooks.TimerCallbackStarting != null)
                hooks.TimerCallbackStarting(timerName, key, physicalPressToken);
        }

        private void DispatchTaskAction(int key, ButtonState state)
        {
            NotifyActionDispatchStarting("global-task", key);
            _focusTaskAction(key, state.ForegroundWindow, state.ContextGeneration);
        }

        private void DispatchTerminalSessionAction(int key, ButtonState state)
        {
            NotifyActionDispatchStarting("terminal-session", key);
            _focusTerminalSessionAction(key, state.ForegroundWindow, state.ContextGeneration);
        }

        private void NotifyActionDispatchStarting(string actionName, int key)
        {
            TriggerProcessorTestHooks hooks = _testHooks;
            if (hooks != null && hooks.ActionDispatchStarting != null)
                hooks.ActionDispatchStarting(actionName, key);
        }

        private static int Elapsed(int start, int end)
        {
            return unchecked(end - start);
        }

        private static bool CapturedContextMatches(ButtonState first, ButtonState second)
        {
            return first != null && second != null &&
                   first.Layer == second.Layer &&
                   first.ForegroundWindow == second.ForegroundWindow &&
                   first.ContextGeneration == second.ContextGeneration;
        }

        private bool ContextStillMatches(ButtonState state)
        {
            if (state == null)
                return false;
            long before;
            long after;
            IntPtr foreground;
            do
            {
                before = _currentContextGeneration();
                foreground = _currentForeground();
                after = _currentContextGeneration();
            }
            while (before != after);
            return state.ForegroundWindow == foreground && state.ContextGeneration == after;
        }

        private void CaptureContext(ButtonState state)
        {
            CaptureCurrentContext(out state.ForegroundWindow, out state.ContextGeneration);
        }

        private void CaptureCurrentContext(out IntPtr foreground, out long generation)
        {
            long before;
            long after;
            do
            {
                before = _currentContextGeneration();
                foreground = _currentForeground();
                after = _currentContextGeneration();
            }
            while (before != after);
            generation = after;
        }

        private static void CancelTimer(ref Timer timer)
        {
            Timer current = timer;
            timer = null;
            if (current != null)
                current.Dispose();
        }

        // Clears a disconnected/faulted generation's business state without
        // interpreting cancellation as a physical key-up.  The adapter has
        // already invalidated the old physical ledger synchronously.
        internal void CancelBusinessInputs()
        {
            lock (_processGate)
            {
                lock (_gate)
                    ResetBusinessStateUnderGate();
            }
        }

        // Final input shutdown linearization.  The only permitted lock order is
        // _processGate -> _gate -> _physicalGate; timer callbacks already use
        // _gate -> _physicalGate and the publisher never waits while holding
        // _physicalGate, so this order has no cycle.
        internal void BeginStoppingAndCancel()
        {
            lock (_processGate)
            {
                lock (_gate)
                {
                    _stopping = true;
                    ResetBusinessStateUnderGate();
                    lock (_physicalGate)
                        InvalidatePhysicalLedgerUnderLock();
                }
            }
        }

        private void ResetBusinessStateUnderGate()
        {
            _key12Chord = false;
            _pendingTaskbarSlot = 0;
            for (int i = 1; i < _buttons.Length; i++)
            {
                CancelTimer(ref _buttons[i].TaskHoldTimer);
                _buttons[i] = new ButtonState();
            }
            Array.Clear(_hasEncoderGesture, 0, _hasEncoderGesture.Length);
            Array.Clear(_lastEncoderAt, 0, _lastEncoderAt.Length);
            Array.Clear(_lastEncoderGesture, 0, _lastEncoderGesture.Length);
        }

        public void Dispose()
        {
            BeginStoppingAndCancel();
            lock (_processGate)
            {
                lock (_gate)
                    _disposed = true;
            }
        }

        private sealed class ButtonState
        {
            internal bool Down;
            internal bool Suppress;
            internal bool LongTriggered;
            internal bool TaskHoldEligible;
            internal bool TerminalSessionHoldEligible;
            internal int Layer;
            internal int DownAt;
            internal IntPtr ForegroundWindow;
            internal long ContextGeneration;
            internal long DownSequence;
            internal long PhysicalPressToken;
            internal Timer TaskHoldTimer;
        }

    }

    internal sealed class TriggerProcessorTestHooks
    {
        internal Action<int, bool, long> PhysicalEdgeObserved;
        internal Action<string, int, long> TimerCallbackStarting;
        internal Action<string, int> ActionDispatchStarting;
    }
}
