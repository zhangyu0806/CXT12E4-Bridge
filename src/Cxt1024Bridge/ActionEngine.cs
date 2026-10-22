using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Cxt1024Bridge
{
    internal sealed partial class ActionEngine
    {
        private readonly ActionDispatcher _dispatcher;
        private readonly LayerManager _layers;
        private readonly ChatGptAutomation _chatGpt;
        private readonly int[] _lastTaskTap = new int[6];
        private readonly object _scrollGate = new object();
        private readonly int[] _lastScrollAt = new int[5];
        private readonly int[] _scrollStreak = new int[5];
        private readonly int[] _lastScrollLayer = new int[5];
        private readonly EncoderGesture[] _lastScrollDirection = new EncoderGesture[5];

        internal event Action<int> TaskSlotOpened;
        internal event Action<TerminalSessionTarget> TerminalSessionOpened;

        internal ActionEngine(ActionDispatcher dispatcher, LayerManager layers, ChatGptAutomation chatGpt)
        {
            _dispatcher = dispatcher;
            _layers = layers;
            _chatGpt = chatGpt;
        }

        private void EnqueueForContext(IntPtr expectedForeground, long expectedGeneration, Action action, bool rotary = false)
        {
            Action work = delegate
            {
                IntPtr currentForeground;
                long currentGeneration;
                CaptureForegroundContext(out currentForeground, out currentGeneration);
                if (!IsApplicationWindow(expectedForeground) || !IsForegroundContextCurrent(expectedForeground, expectedGeneration, currentForeground, currentGeneration))
                {
                    Logger.Info(string.Format(
                        CultureInfo.InvariantCulture,
                        "Queued action cancelled after foreground changed from 0x{0:X} to 0x{1:X}.",
                        expectedForeground.ToInt64(),
                        currentForeground.ToInt64()));
                    return;
                }
                action();
            };
            if (rotary) _dispatcher.EnqueueRotary(work);
            else _dispatcher.Enqueue(work);
        }

        private Func<bool> InputContextGuard(IntPtr expectedForeground, long expectedGeneration)
        {
            return delegate
            {
                IntPtr current;
                long generation;
                CaptureForegroundContext(out current, out generation);
                return expectedForeground != IntPtr.Zero && IsApplicationWindow(expectedForeground) &&
                    IsForegroundContextCurrent(expectedForeground, expectedGeneration, current, generation);
            };
        }

        private bool IsApplicationWindow(IntPtr expected)
        {
            ForegroundInfo foreground = _layers.CurrentForeground;
            return foreground != null && foreground.Window == expected &&
                foreground.ProcessId != Process.GetCurrentProcess().Id;
        }

        private void CaptureForegroundContext(out IntPtr foreground, out long generation)
        {
            long before;
            do
            {
                before = _layers.ContextGeneration;
                foreground = NativeMethods.GetForegroundWindow();
                generation = _layers.ContextGeneration;
            }
            while (before != generation);
        }

        internal static bool IsForegroundContextCurrent(
            IntPtr expected,
            long expectedGeneration,
            IntPtr current,
            long currentGeneration)
        {
            return expected == current && expectedGeneration == currentGeneration;
        }

        internal void Button(int layer, int key, bool longPress, IntPtr expectedForeground, long expectedGeneration)
        {
            if (!longPress && Profiles != null)
            {
                NamedAction(Profiles.Action("keyboard", "button." + key, layer), layer, expectedForeground,
                    expectedGeneration, WindowsControllerClock.Now, "keyboard");
                return;
            }
            ButtonDefault(layer, key, longPress, expectedForeground, expectedGeneration);
        }

        private void ButtonDefault(int layer, int key, bool longPress, IntPtr expectedForeground, long expectedGeneration)
        {
            Logger.Info(string.Format(CultureInfo.InvariantCulture, "L{0} key {1}{2}", BridgeDefinitions.GetLayer(layer).Number, key, longPress ? " long" : string.Empty));
            EnqueueForContext(expectedForeground, expectedGeneration, delegate { ExecuteButton(layer, key, longPress, expectedForeground, expectedGeneration); });
        }

        internal void Encoder(int layer, int encoder, EncoderGesture gesture, IntPtr expectedForeground, long expectedGeneration)
        {
            if (Profiles != null)
            {
                int g = gesture == EncoderGesture.CounterClockwise ? 0 : gesture == EncoderGesture.Press ? 1 : 2;
                string control = "encoder." + encoder + "." + g;
                string mapped = Profiles.Action("keyboard", control, layer);
                if (!string.Equals(mapped, control, StringComparison.Ordinal))
                {
                    NamedAction(mapped, layer, expectedForeground, expectedGeneration, WindowsControllerClock.Now, "keyboard");
                    return;
                }
            }
            EncoderDefault(layer, encoder, gesture, expectedForeground, expectedGeneration);
        }

        private void EncoderDefault(int layer, int encoder, EncoderGesture gesture, IntPtr expectedForeground, long expectedGeneration, double deadline = double.MaxValue)
        {
            Func<bool> fresh = delegate { return WindowsControllerClock.Now <= deadline; };
            if (layer == 1 && (encoder == 1 || (encoder == 2 && gesture == EncoderGesture.Press)))
            {
                long content = _layers.ContentGeneration;
                int received = Environment.TickCount;
                Func<bool> taskAnchor = _chatGpt.CaptureModelTaskGuard();
                Func<bool> windowGuard = InputContextGuard(expectedForeground, expectedGeneration);
                Func<bool> guard = delegate { return windowGuard() && _layers.ContentGeneration == content; };
                Logger.Info(string.Format(CultureInfo.InvariantCulture, "L1 K{0} {1} model intent", encoder, gesture));
                if (gesture != EncoderGesture.Press)
                    _dispatcher.EnqueueReasoning(expectedForeground, expectedGeneration, content,
                        gesture == EncoderGesture.Clockwise, delegate(ReasoningAdjustment adjustment)
                        {
                            if (!fresh() || !guard() || !CanRunQueuedModelTurn(received, Environment.TickCount, taskAnchor))
                            {
                                Logger.Info("Queued model turn cancelled: stale context or no unchanged task anchor.");
                                return;
                            }
                            if (!_chatGpt.RunModelAction(guard, delegate { return _chatGpt.AdjustReasoning(adjustment); }))
                                Logger.Warn("L1 reasoning batch cancelled or failed verification.");
                        });
                else
                    _dispatcher.Enqueue(delegate
                    {
                        if (!fresh()) return;
                        if (!_chatGpt.RunModelAction(guard, delegate
                        {
                            return encoder == 1 ? _chatGpt.SetReasoningUltraAndFast() : _chatGpt.SetReasoningHighAndStandard();
                        })) Logger.Warn("L1 model preset cancelled or failed verification.");
                    });
                return;
            }
            if (gesture != EncoderGesture.Press && IsAcceleratedScrollEncoder(layer, encoder))
            {
                int multiplier = GetScrollMultiplier(layer, encoder, gesture, Environment.TickCount);
                int delta = (gesture == EncoderGesture.CounterClockwise ? 120 : -120) * multiplier;
                Logger.Info(string.Format(
                    CultureInfo.InvariantCulture,
                    "L{0} K{1} {2} accelerated x{3}",
                    BridgeDefinitions.GetLayer(layer).Number,
                    encoder,
                    gesture,
                    multiplier));
                EnqueueForContext(expectedForeground, expectedGeneration, delegate { if (fresh()) ExecuteAcceleratedScroll(layer, delta, expectedForeground, expectedGeneration, deadline); }, true);
                return;
            }
            Logger.Info(string.Format(CultureInfo.InvariantCulture, "L{0} K{1} {2}", BridgeDefinitions.GetLayer(layer).Number, encoder, gesture));
            EnqueueForContext(expectedForeground, expectedGeneration, delegate { if (fresh()) ExecuteEncoder(layer, encoder, gesture, expectedForeground, expectedGeneration, deadline); }, gesture != EncoderGesture.Press);
        }

        internal static bool IsAcceleratedScrollEncoder(int layer, int encoder)
        {
            return layer >= 0 && layer <= 7 && encoder == 4;
        }

        private int GetScrollMultiplier(int layer, int encoder, EncoderGesture direction, int timestamp)
        {
            lock (_scrollGate)
            {
                int elapsed = unchecked(timestamp - _lastScrollAt[encoder]);
                bool continues = _scrollStreak[encoder] > 0 &&
                    _lastScrollLayer[encoder] == layer &&
                    _lastScrollDirection[encoder] == direction &&
                    elapsed >= 0 &&
                    elapsed <= 380;
                _scrollStreak[encoder] = continues ? _scrollStreak[encoder] + 1 : 1;
                _lastScrollAt[encoder] = timestamp;
                _lastScrollLayer[encoder] = layer;
                _lastScrollDirection[encoder] = direction;
                return ScrollMultiplierForStreak(_scrollStreak[encoder]);
            }
        }

        internal static int ScrollMultiplierForStreak(int streak)
        {
            if (streak <= 2) return 1;
            if (streak <= 4) return 2;
            if (streak <= 7) return 4;
            if (streak <= 11) return 7;
            return 10;
        }

        internal static int StandardWheelEventCount(int delta)
        {
            return Math.Max(1, Math.Abs(delta) / 120);
        }

        private void ExecuteAcceleratedScroll(int layer, int delta, IntPtr expectedForeground, long expectedGeneration, double deadline = double.MaxValue)
        {
            // Encoder triggers are encoded as RightCtrl+F13..F24. The private
            // F-key is suppressed, but Windows still sees the marker modifier.
            // Sending a wheel event before its key-up turns browser scrolling
            // into Ctrl+wheel zoom. Wait for the marker to leave the global key
            // state and fail closed instead of emitting a modified wheel event.
            if (!PrepareDesktopShortcut(layer, expectedForeground, expectedGeneration, deadline))
                return;
            if (layer == 1 && _chatGpt.ScrollConversation(delta))
                return;
            if (layer == 2)
            {
                // Full-screen terminal TUIs can collapse a large wheel delta
                // into one mouse-report event. Repeat standard notches so the
                // acceleration remains observable in OpenCode/Claude/Grok.
                int unit = delta < 0 ? -120 : 120;
                int steps = StandardWheelEventCount(delta);
                for (int i = 0; i < steps; i++)
                    InputSender.VerticalWheel(unit);
                return;
            }
            InputSender.VerticalWheel(delta);
        }

        internal void LaunchTaskbarSlot(int slot, IntPtr expectedForeground, long expectedGeneration)
        {
            Logger.Info(string.Format(CultureInfo.InvariantCulture, "Launching Windows taskbar slot {0}.", slot));
            EnqueueForContext(expectedForeground, expectedGeneration, delegate { TaskbarLauncher.LaunchSlot(slot); });
        }

        internal void FocusTaskForKey(int key, IntPtr expectedForeground, long expectedGeneration)
        {
            int slot = TaskSlotForKey(key);
            if (slot <= 0)
                return;
            Logger.Info(string.Format(CultureInfo.InvariantCulture, "Global task hold requested ChatGPT slot {0} with foreground activation.", slot));
            EnqueueForContext(expectedForeground, expectedGeneration, delegate { FocusTask(slot); });
        }

        internal void FocusTerminalSessionForKey(int key, IntPtr expectedForeground, long expectedGeneration)
        {
            int slot = TerminalSessionSlotForKey(key);
            if (slot <= 0)
                return;
            Logger.Info(string.Format(CultureInfo.InvariantCulture, "Global session hold requested tmux slot {0}.", slot));
            EnqueueForContext(expectedForeground, expectedGeneration, delegate
            {
                try
                {
                    TerminalSessionTarget target;
                    if (TmuxSessionClient.SwitchToSlot(slot, out target))
                    {
                        Action<TerminalSessionTarget> opened = TerminalSessionOpened;
                        if (opened != null)
                            opened(target);
                        TerminalWindowActivator.FocusWindow(_layers.LastTerminalWindow);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("Unable to switch to tmux session slot " + slot + ".", ex);
                }
            });
        }

        private void ExecuteButton(int layer, int key, bool longPress, IntPtr expectedForeground, long expectedGeneration, double deadline = double.MaxValue)
        {
            if (layer == 0 || layer == 5)
            {
                ExecuteDesktopShortcut(layer, DesktopLayerShortcuts.ForKey(layer, key), expectedForeground, expectedGeneration, deadline);
                return;
            }
            switch (layer)
            {
                case 1:
                    ChatGptButton(key, InputContextGuard(expectedForeground, expectedGeneration));
                    break;
                case 2:
                    TerminalButton(key);
                    break;
                case 3:
                    NotionButton(key);
                    break;
                case 4:
                    ObsidianButton(key, expectedForeground, InputContextGuard(expectedForeground, expectedGeneration));
                    break;
                case 6:
                    TotalCommanderButton(key);
                    break;
                case 7:
                    MessagingButton(key);
                    break;
            }
        }

        private void ExecuteEncoder(int layer, int encoder, EncoderGesture gesture, IntPtr expectedForeground, long expectedGeneration, double deadline = double.MaxValue)
        {
            // Decoder waits for physical release; guard against another marker
            // or the main keyboard's RightCtrl pressed before execution.
            if (!PrepareDesktopShortcut(layer, expectedForeground, expectedGeneration, deadline)) return;
            if (layer == 0 || layer == 5)
            {
                ExecuteDesktopShortcut(layer, DesktopLayerShortcuts.ForEncoder(layer, encoder, gesture), expectedForeground, expectedGeneration, deadline);
                return;
            }
            switch (layer)
            {
                case 1:
                    ChatGptEncoder(encoder, gesture);
                    break;
                case 2:
                    TerminalEncoder(encoder, gesture);
                    break;
                case 3:
                    NotionEncoder(encoder, gesture, expectedForeground, InputContextGuard(expectedForeground, expectedGeneration));
                    break;
                case 4:
                    ObsidianEncoder(encoder, gesture, expectedForeground, InputContextGuard(expectedForeground, expectedGeneration));
                    break;
                case 6:
                    TotalCommanderEncoder(encoder, gesture);
                    break;
                case 7:
                    MessagingEncoder(encoder, gesture);
                    break;
            }
        }

        private bool PrepareDesktopShortcut(int layer, IntPtr expectedWindow, long expectedGeneration, double deadline = double.MaxValue)
        {
            // Check expiry after waiting for the private RightCtrl marker,
            // immediately before starting the action. A running UI operation
            // keeps its context guard, not this input-queue deadline.
            return PrepareTimedShortcut(deadline,
                delegate { return InputSender.WaitForKeyRelease(Vk.RightControl, 120); },
                delegate
                {
                    return IsDesktopShortcutContextCurrent(layer, expectedWindow, expectedGeneration,
                        ForegroundResolver.Capture(), NativeMethods.GetForegroundWindow(), _layers.ContextGeneration);
                }, delegate { return WindowsControllerClock.Now; });
        }

        internal static bool PrepareTimedShortcut(double deadline, Func<bool> releaseMarker, Func<bool> contextCurrent, Func<double> clock)
        {
            return clock() <= deadline && releaseMarker() && contextCurrent() && clock() <= deadline;
        }

        internal static bool CanRunQueuedModelTurn(int received, int now, Func<bool> taskAnchor)
        {
            return !ActionDispatcher.IsRotaryExpired(received, now) || (taskAnchor != null && taskAnchor());
        }

        internal static bool IsDesktopShortcutContextCurrent(int layer, IntPtr expectedWindow, long expectedGeneration,
            ForegroundInfo foreground, IntPtr actualWindow, long currentGeneration)
        {
            return layer >= 0 && layer <= 7 && foreground != null && expectedWindow != IntPtr.Zero &&
                IsForegroundContextCurrent(expectedWindow, expectedGeneration, foreground.Window, currentGeneration) &&
                actualWindow == expectedWindow &&
                (layer != 5 || ForegroundResolver.IsBlenderProcess(foreground.ProcessName));
        }

        private void ExecuteDesktopShortcut(int layer, DesktopShortcut shortcut, IntPtr expectedForeground, long expectedGeneration, double deadline = double.MaxValue)
        {
            if (shortcut == null || !PrepareDesktopShortcut(layer, expectedForeground, expectedGeneration, deadline))
                return;
            if (shortcut.IsCopyQ)
            {
                ShowCopyQ();
                return;
            }
            var modifiers = new List<int>();
            if ((shortcut.Modifiers & DesktopShortcutModifiers.Control) != 0) modifiers.Add(Vk.LeftControl);
            if ((shortcut.Modifiers & DesktopShortcutModifiers.Shift) != 0) modifiers.Add(Vk.LeftShift);
            if ((shortcut.Modifiers & DesktopShortcutModifiers.Alt) != 0) modifiers.Add(Vk.LeftAlt);
            InputSender.Chord(shortcut.Key, modifiers.ToArray());
        }


        private void ChatGptButton(int key, Func<bool> contextIsCurrent)
        {
            int slot = TaskSlotForKey(key);
            if (slot > 0)
            {
                SwitchTask(slot);
                return;
            }
            switch (key)
            {
                case 4: _chatGpt.ForkCurrentTask(); break;
                case 8: _chatGpt.ToggleTemporaryTask(); break;
                case 9:
                    if (_chatGpt.ToggleSidebar())
                        Logger.Info("L1 key 9 invoked the ChatGPT left sidebar toggle.");
                    else
                        Logger.Warn("L1 key 9 could not find the ChatGPT sidebar toggle.");
                    break;
                case 10: _chatGpt.ToggleSidePanel(); break;
                case 11: _chatGpt.OpenInBuiltInBrowser("https://chatgpt.com/", contextIsCurrent); break;
                case 12: _chatGpt.NewTask(contextIsCurrent); break;
            }
        }

        private void SwitchTask(int slot)
        {
            _chatGpt.ClearReasoningSelectionMemory();
            int now = Environment.TickCount;
            int previous = _lastTaskTap[slot - 1];
            bool doubleTap = previous != 0 && unchecked(now - previous) >= 0 && unchecked(now - previous) <= 350;
            _lastTaskTap[slot - 1] = doubleTap ? 0 : now;
            if (_chatGpt.SwitchTaskSlot(slot, doubleTap))
            {
                Action<int> opened = TaskSlotOpened;
                if (opened != null)
                    opened(slot);
            }
        }

        private void FocusTask(int slot)
        {
            _chatGpt.ClearReasoningSelectionMemory();
            _lastTaskTap[slot - 1] = 0;
            if (_chatGpt.SwitchTaskSlot(slot, true))
            {
                Action<int> opened = TaskSlotOpened;
                if (opened != null)
                    opened(slot);
            }
        }

        private static int TaskSlotForKey(int key)
        {
            switch (key)
            {
                case 1: return 1;
                case 2: return 2;
                case 3: return 3;
                case 5: return 4;
                case 6: return 5;
                case 7: return 6;
                default: return 0;
            }
        }

        internal static int TerminalSessionSlotForKey(int key)
        {
            switch (key)
            {
                case 9: return 1;
                case 10: return 2;
                case 11: return 3;
                case 12: return 4;
                case 8: return 5;
                case 4: return 6;
                default: return 0;
            }
        }

        internal static TerminalTmuxAction TerminalTmuxActionForKey(int key)
        {
            switch (key)
            {
                case 1: return TerminalTmuxAction.SplitRight;
                case 2: return TerminalTmuxAction.SplitDown;
                case 3: return TerminalTmuxAction.SelectHorizontal;
                case 4: return TerminalTmuxAction.SelectVertical;
                case 5: return TerminalTmuxAction.ToggleZoom;
                case 6: return TerminalTmuxAction.ToggleOrientation;
                case 7: return TerminalTmuxAction.RotatePanes;
                case 8: return TerminalTmuxAction.ChooseWindow;
                case 9: return TerminalTmuxAction.StashAgentInput;
                case 10: return TerminalTmuxAction.CopyMode;
                case 12: return TerminalTmuxAction.LastPane;
                default: return TerminalTmuxAction.None;
            }
        }

        private static void TerminalButton(int key)
        {
            if (key == 11)
            {
                // Windows Terminal officially binds Find to Ctrl+Shift+F, but
                // its input stack can miss an all-at-once synthetic chord.
                // Keep each phase down long enough for the terminal to observe
                // the modifiers, key press and release in order.
                InputSender.ChordWithDwell(Vk.F, 35, Vk.LeftControl, Vk.LeftShift);
                Logger.Info("L2 key 11 sent the stable Windows Terminal Ctrl+Shift+F search chord.");
                return;
            }

            TerminalTmuxAction action = TerminalTmuxActionForKey(key);
            if (action == TerminalTmuxAction.None)
                return;

            ForegroundInfo foreground = ForegroundResolver.Capture();
            if (foreground.AutomaticLayer != 2)
            {
                Logger.Warn("Direct L2 tmux action rejected because Windows Terminal is not the foreground application.");
                return;
            }
            TmuxSessionClient.ExecuteActiveAction(action);
        }

        private static void NotionButton(int key)
        {
            switch (key)
            {
                case 1: InputSender.Chord(Vk.N, Vk.LeftControl); break;
                case 2: InputSender.Chord(Vk.T, Vk.LeftControl); break;
                case 3: InputSender.Chord(Vk.F, Vk.LeftControl); break;
                case 4: InputSender.Chord(Vk.L, Vk.LeftControl, Vk.LeftShift); break;
                case 5: InputSender.Chord(Vk.M, Vk.LeftControl, Vk.LeftShift); break;
                case 6: InputSender.Chord(Vk.D, Vk.LeftControl); break;
                case 7: InputSender.Chord(Vk.OemSlash, Vk.LeftControl); break;
                case 8: InputSender.Chord(Vk.L, Vk.LeftControl); break;
                case 9: InputSender.Tap(Vk.Delete); break;
                case 10: InputSender.Chord(Vk.Four, Vk.LeftControl, Vk.LeftShift); break;
                case 11: InputSender.Chord(Vk.Eight, Vk.LeftControl, Vk.LeftShift); break;
                case 12: InputSender.Tap(Vk.F11); break;
            }
        }

        private static void TotalCommanderButton(int key)
        {
            switch (key)
            {
                case 1: InputSender.Tap(Vk.F5); break;
                case 2: InputSender.Tap(Vk.F6); break;
                case 3: InputSender.Tap(Vk.F7); break;
                case 4: TotalCommanderCommand(2400); break;
                case 5: InputSender.Chord(Vk.One, Vk.LeftControl); break;
                case 6: InputSender.Chord(Vk.Two, Vk.LeftControl); break;
                case 7: InputSender.Chord(Vk.Three, Vk.LeftControl); break;
                case 8: InputSender.Chord(Vk.Q, Vk.LeftAlt); break;
                case 9: InputSender.Tap(Vk.F8); break;
                case 10: TotalCommanderCommand(2022); break;
                case 11: TotalCommanderCommand(2020); break;
                case 12: InputSender.Chord(Vk.F5, Vk.LeftControl, Vk.LeftAlt); break;
            }
        }

        private void MessagingButton(int key)
        {
            switch (key)
            {
                case 1: InputSender.Chord(Vk.Up, Vk.LeftAlt); break;
                case 2: InputSender.Chord(Vk.Down, Vk.LeftAlt); break;
                case 3: InputSender.Chord(Vk.Down, Vk.LeftControl, Vk.LeftShift); break;
                case 4: InputSender.Chord(Vk.F, Vk.LeftControl); break;
                case 5: InputSender.Chord(Vk.C, Vk.LeftControl); break;
                case 6: InputSender.Chord(Vk.V, Vk.LeftControl, Vk.LeftShift); break;
                case 7:
                    // Telegram uses Ctrl+R for recording, not portable reply.
                    Logger.Warn("Messaging reply is unassigned until a client-specific action is verified.");
                    break;
                case 8: InputSender.Tap(Vk.Enter); break;
                case 9:
                    InputSender.Chord(Vk.A, Vk.LeftControl);
                    InputSender.Tap(Vk.Back);
                    break;
                case 10: InputSender.Chord(Vk.OemPeriod, Vk.LeftWindows); break;
                case 11: InputSender.Chord(Vk.Enter, Vk.LeftShift); break;
                case 12: break;
            }
        }


        private static void ObsidianButton(int key, IntPtr window, Func<bool> contextIsCurrent)
        {
            switch (key)
            {
                case 1: InputSender.Chord(Vk.N, Vk.LeftControl); break;
                case 2: InputSender.Chord(Vk.O, Vk.LeftControl); break;
                case 3: InputSender.Chord(Vk.F, Vk.LeftControl, Vk.LeftShift); break;
                case 4: InputSender.Chord(Vk.P, Vk.LeftControl); break;
                case 5: InputSender.TypeText("[["); break;
                case 6: InputSender.Chord(Vk.Enter, Vk.LeftControl); break;
                case 7: InputSender.Tap(Vk.F2); break;
                case 8: RunObsidianCommand("Copy Obsidian URL", window, contextIsCurrent); break;
                case 9: InputSender.Chord(Vk.W, Vk.LeftControl); break;
                case 10: RunObsidianCommand("Open today's daily note", window, contextIsCurrent); break;
                case 11: ObsidianTemplateOrOutline(window, contextIsCurrent); break;
                case 12: InputSender.Chord(Vk.E, Vk.LeftControl); break;
            }
        }

        private void ChatGptEncoder(int encoder, EncoderGesture gesture)
        {
            if (encoder == 1)
            {
                bool handled;
                if (gesture == EncoderGesture.CounterClockwise) handled = _chatGpt.AdjustReasoning(false);
                else if (gesture == EncoderGesture.Clockwise) handled = _chatGpt.AdjustReasoning(true);
                else handled = _chatGpt.SetReasoningUltraAndFast();
                if (!handled)
                    Logger.Warn("L1 K1 Astra reasoning action failed: " + gesture + ".");
                return;
            }
            if (encoder == 2)
            {
                bool handled;
                if (gesture == EncoderGesture.CounterClockwise) handled = _chatGpt.NavigateAdjacentSidebarTask(false);
                else if (gesture == EncoderGesture.Clockwise) handled = _chatGpt.NavigateAdjacentSidebarTask(true);
                else handled = _chatGpt.SetReasoningHighAndStandard();
                if (!handled)
                    Logger.Warn("L1 K2 action failed (press requires Astra High + Standard): " + gesture + ".");
                return;
            }
            if (encoder == 3)
            {
                if (gesture == EncoderGesture.CounterClockwise) InputSender.Chord(Vk.Left, Vk.LeftControl);
                else if (gesture == EncoderGesture.Clockwise) InputSender.Chord(Vk.Right, Vk.LeftControl);
                else _chatGpt.FocusComposer();
                return;
            }
            if (gesture == EncoderGesture.CounterClockwise)
            {
                if (!_chatGpt.ScrollConversation(true))
                    InputSender.VerticalWheel(120);
            }
            else if (gesture == EncoderGesture.Clockwise)
            {
                if (!_chatGpt.ScrollConversation(false))
                    InputSender.VerticalWheel(-120);
            }
            else if (!_chatGpt.ScrollConversationToBottom())
            {
                Logger.Warn("L1 K4 press could not find the current ChatGPT conversation scroll target.");
            }
        }

        internal static TerminalTmuxAction TerminalTmuxActionForEncoder(int encoder, EncoderGesture gesture)
        {
            if (encoder == 1)
            {
                if (gesture == EncoderGesture.CounterClockwise) return TerminalTmuxAction.ResizeLeft;
                if (gesture == EncoderGesture.Clockwise) return TerminalTmuxAction.ResizeRight;
                return TerminalTmuxAction.EqualizeHorizontal;
            }
            if (encoder == 2)
            {
                if (gesture == EncoderGesture.CounterClockwise) return TerminalTmuxAction.ResizeUp;
                if (gesture == EncoderGesture.Clockwise) return TerminalTmuxAction.ResizeDown;
                return TerminalTmuxAction.EqualizeVertical;
            }
            if (encoder == 3)
            {
                if (gesture == EncoderGesture.CounterClockwise) return TerminalTmuxAction.PreviousWindow;
                if (gesture == EncoderGesture.Clockwise) return TerminalTmuxAction.NextWindow;
                return TerminalTmuxAction.LastWindow;
            }
            return TerminalTmuxAction.None;
        }

        private static void TerminalEncoder(int encoder, EncoderGesture gesture)
        {
            TerminalTmuxAction action = TerminalTmuxActionForEncoder(encoder, gesture);
            if (action != TerminalTmuxAction.None)
            {
                ForegroundInfo foreground = ForegroundResolver.Capture();
                if (foreground.AutomaticLayer != 2)
                {
                    Logger.Warn("Direct L2 tmux encoder action rejected because Windows Terminal is not the foreground application.");
                    return;
                }
                TmuxSessionClient.ExecuteActiveAction(action);
                return;
            }
            if (gesture == EncoderGesture.CounterClockwise) InputSender.VerticalWheel(120);
            else if (gesture == EncoderGesture.Clockwise) InputSender.VerticalWheel(-120);
            else InputSender.Chord(Vk.End, Vk.LeftControl, Vk.LeftShift);
        }

        private static void NotionEncoder(int encoder, EncoderGesture gesture, IntPtr window, Func<bool> contextIsCurrent)
        {
            if (encoder == 1)
            {
                if (gesture == EncoderGesture.CounterClockwise) InputSender.Chord(Vk.OemOpenBrackets, Vk.LeftControl);
                else if (gesture == EncoderGesture.Clockwise) InputSender.Chord(Vk.OemCloseBrackets, Vk.LeftControl);
                else InputSender.Chord(Vk.U, Vk.LeftControl, Vk.LeftShift);
                return;
            }
            if (encoder == 2)
            {
                if (gesture == EncoderGesture.CounterClockwise) InputSender.Chord(Vk.Tab, Vk.LeftControl, Vk.LeftShift);
                else if (gesture == EncoderGesture.Clockwise) InputSender.Chord(Vk.Tab, Vk.LeftControl);
                else InputSender.Chord(Vk.P, Vk.LeftControl);
                return;
            }
            if (encoder == 3)
            {
                if (gesture == EncoderGesture.CounterClockwise) InputSender.HorizontalWheel(-120);
                else if (gesture == EncoderGesture.Clockwise) InputSender.HorizontalWheel(120);
                else NotionAutomation.ToggleFullWidth(window, contextIsCurrent);
                return;
            }
            if (gesture == EncoderGesture.CounterClockwise) InputSender.VerticalWheel(120);
            else if (gesture == EncoderGesture.Clockwise) InputSender.VerticalWheel(-120);
            else InputSender.Chord(Vk.Home, Vk.LeftControl);
        }

        private static void TotalCommanderEncoder(int encoder, EncoderGesture gesture)
        {
            if (encoder == 1)
            {
                if (gesture == EncoderGesture.CounterClockwise) TotalCommanderCommand(570);
                else if (gesture == EncoderGesture.Clockwise) TotalCommanderCommand(571);
                else TotalCommanderCommand(2002);
                return;
            }
            if (encoder == 2)
            {
                if (gesture == EncoderGesture.CounterClockwise) TotalCommanderCommand(4001);
                else if (gesture == EncoderGesture.Clockwise) TotalCommanderCommand(4002);
                else TotalCommanderCommand(531);
                return;
            }
            if (encoder == 3)
            {
                if (gesture == EncoderGesture.CounterClockwise) TotalCommanderCommand(3006);
                else if (gesture == EncoderGesture.Clockwise) TotalCommanderCommand(3005);
                else TotalCommanderCommand(3010);
                return;
            }
            if (gesture == EncoderGesture.CounterClockwise) InputSender.VerticalWheel(120);
            else if (gesture == EncoderGesture.Clockwise) InputSender.VerticalWheel(-120);
            else TotalCommanderCommand(304);
        }

        private static void MessagingEncoder(int encoder, EncoderGesture gesture)
        {
            if (encoder == 1)
            {
                if (gesture == EncoderGesture.CounterClockwise) InputSender.Chord(Vk.Left, Vk.LeftControl);
                else if (gesture == EncoderGesture.Clockwise) InputSender.Chord(Vk.Right, Vk.LeftControl);
                else InputSender.Tap(Vk.Home);
                return;
            }
            if (encoder == 2)
            {
                if (gesture == EncoderGesture.CounterClockwise) InputSender.Chord(Vk.Left, Vk.LeftControl, Vk.LeftShift);
                else if (gesture == EncoderGesture.Clockwise) InputSender.Chord(Vk.Right, Vk.LeftControl, Vk.LeftShift);
                else InputSender.Chord(Vk.A, Vk.LeftControl);
                return;
            }
            if (encoder == 3)
            {
                if (gesture == EncoderGesture.CounterClockwise) InputSender.Chord(Vk.OemMinus, Vk.LeftControl);
                else if (gesture == EncoderGesture.Clockwise) InputSender.Chord(Vk.OemPlus, Vk.LeftControl);
                else InputSender.Chord(Vk.Zero, Vk.LeftControl);
                return;
            }
            if (gesture == EncoderGesture.CounterClockwise) InputSender.VerticalWheel(120);
            else if (gesture == EncoderGesture.Clockwise) InputSender.VerticalWheel(-120);
            else InputSender.Tap(Vk.End);
        }


        private static void ObsidianEncoder(int encoder, EncoderGesture gesture, IntPtr window, Func<bool> contextIsCurrent)
        {
            if (encoder == 1)
            {
                if (gesture == EncoderGesture.CounterClockwise) InputSender.Chord(Vk.Left, Vk.LeftAlt);
                else if (gesture == EncoderGesture.Clockwise) InputSender.Chord(Vk.Right, Vk.LeftAlt);
                else RunObsidianCommand("Backlinks: Show backlinks", window, contextIsCurrent);
                return;
            }
            if (encoder == 2)
            {
                if (gesture == EncoderGesture.CounterClockwise) InputSender.Chord(Vk.Tab, Vk.LeftControl, Vk.LeftShift);
                else if (gesture == EncoderGesture.Clockwise) InputSender.Chord(Vk.Tab, Vk.LeftControl);
                else InputSender.Chord(Vk.T, Vk.LeftControl, Vk.LeftShift);
                return;
            }
            if (encoder == 3)
            {
                if (gesture == EncoderGesture.CounterClockwise) InputSender.Chord(Vk.OemMinus, Vk.LeftControl);
                else if (gesture == EncoderGesture.Clockwise) InputSender.Chord(Vk.OemPlus, Vk.LeftControl);
                else InputSender.Chord(Vk.Zero, Vk.LeftControl);
                return;
            }
            if (gesture == EncoderGesture.CounterClockwise) InputSender.VerticalWheel(120);
            else if (gesture == EncoderGesture.Clockwise) InputSender.VerticalWheel(-120);
            else InputSender.Chord(Vk.Home, Vk.LeftControl);
        }




        private static void RunObsidianCommand(string command, IntPtr window, Func<bool> contextIsCurrent)
        {
            if (!ApplicationInputSafety.RunObsidianCommand(command, window, contextIsCurrent))
                Logger.Warn("Obsidian command cancelled: original window or command palette focus was lost.");
        }

        private static void ObsidianTemplateOrOutline(IntPtr window, Func<bool> contextIsCurrent)
        {
            string templatesConfig = @"D:\LifeOS\.obsidian\templates.json";
            if (File.Exists(templatesConfig))
                RunObsidianCommand("Templates: Insert template", window, contextIsCurrent);
            else
                RunObsidianCommand("Outline: Show outline", window, contextIsCurrent);
        }

        private static void ShowCopyQ()
        {
            string[] candidates =
            {
                @"D:\Program Files\CopyQ\copyq.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "CopyQ", "copyq.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "CopyQ", "copyq.exe")
            };
            for (int i = 0; i < candidates.Length; i++)
            {
                if (!File.Exists(candidates[i]))
                    continue;
                Process.Start(new ProcessStartInfo(candidates[i], "menu") { UseShellExecute = true });
                return;
            }
            Logger.Warn("CopyQ executable was not found.");
        }

        private static void TotalCommanderCommand(int command)
        {
            IntPtr window = NativeMethods.GetForegroundWindow();
            ForegroundInfo foreground = ForegroundResolver.Capture();
            if (window == IntPtr.Zero ||
                (!string.Equals(foreground.ProcessName, "TOTALCMD64", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(foreground.ProcessName, "TOTALCMD", StringComparison.OrdinalIgnoreCase)))
            {
                Logger.Warn("Total Commander command " + command + " was ignored because TC is not foreground.");
                return;
            }
            NativeMethods.SendMessage(window, 0x0400 + 51, new IntPtr(command), IntPtr.Zero);
        }
    }
}
