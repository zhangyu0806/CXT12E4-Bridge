using System;
using System.Collections.Generic;

namespace Cxt1024Bridge
{
    internal sealed partial class ActionEngine
    {
        internal WindowsProfileStore Profiles { get; set; }
        internal event Action<string, IntPtr, long, double> ProfileHelpRequested;
        internal event Action<IntPtr, long, double> ProfileChooserRequested;

        // Finite IDs only. Mapped buttons never recursively consult another
        // control's override; they execute that action's built-in semantics.
        internal void NamedAction(string id, int layer, IntPtr window, long generation,
            double received, string device)
        {
            if (id == "none" || string.IsNullOrEmpty(id)) return;
            double age = WindowsControllerClock.Now - received;
            if (age < 0 || age > 0.5 || !InputContextGuard(window, generation)()) return;
            string[] parts = id.Split('.');
            int number, gesture;
            if (parts.Length == 3 && parts[0] == "encoder" &&
                int.TryParse(parts[1], out number) && number >= 1 && number <= 4 &&
                int.TryParse(parts[2], out gesture) && gesture >= 0 && gesture <= 2)
            {
                if (gesture != 1 && age > 0.25) return;
                EncoderDefault(layer, number, gesture == 0 ? EncoderGesture.CounterClockwise :
                    gesture == 1 ? EncoderGesture.Press : EncoderGesture.Clockwise, window, generation, received + (gesture == 1 ? 0.5 : 0.25));
                return;
            }
            EnqueueForContext(window, generation, delegate
            {
                if (WindowsControllerClock.Now - received > 0.5) return;
                if (!PrepareDesktopShortcut(layer, window, generation, received + 0.5)) return;
                if (parts.Length == 2 && parts[0] == "button" &&
                    int.TryParse(parts[1], out number) && number >= 1 && number <= 12)
                {
                    ExecuteButton(layer, number, false, window, generation, received + 0.5);
                    return;
                }
                if (id == "bridge.help")
                {
                    var handler = ProfileHelpRequested;
                    if (handler != null) handler(device, window, generation, received + 0.5);
                    return;
                }
                if (id == "bridge.chooser")
                {
                    var handler = ProfileChooserRequested;
                    if (handler != null) handler(window, generation, received + 0.5);
                    return;
                }
                int key;
                if (TryNamedKey(id, out key)) { InputSender.Tap(key); return; }
                if (id == "edit.copy" || id == "edit.paste")
                {
                    key = id == "edit.copy" ? Vk.C : Vk.V;
                    // Ctrl+C interrupts a terminal job; use Windows Terminal's
                    // explicit clipboard shortcuts instead.
                    if (layer == 2) InputSender.Chord(key, Vk.LeftControl, Vk.LeftShift);
                    else InputSender.Chord(key, Vk.LeftControl);
                    return;
                }
                if (id == "edit.undo" || id == "edit.redo")
                {
                    if (layer == 2) { Logger.Warn("Terminal undo/redo is application-specific; no Ctrl+Z was sent."); return; }
                    if (id == "edit.undo") InputSender.Chord(Vk.Z, Vk.LeftControl);
                    else if (layer == 5) InputSender.Chord(Vk.Z, Vk.LeftControl, Vk.LeftShift);
                    else InputSender.Chord(Vk.Y, Vk.LeftControl);
                    return;
                }
                if (id == "navigation.previous" || id == "navigation.next")
                {
                    NavigateProfile(layer, id == "navigation.next"); return;
                }
                if (id == "codex.composer" && layer == 1) { _chatGpt.FocusComposer(); return; }
                if (id == "files.open" && layer == 6) { InputSender.Tap(Vk.Enter); return; }
                if (layer == 2 && TryControllerTmuxAction(id, out key))
                {
                    if (ForegroundResolver.Capture().AutomaticLayer != 2) return;
                    TmuxSessionClient.ExecuteActiveAction((TerminalTmuxAction)key);
                    return;
                }
                Logger.Warn("Unrecognized or unavailable mapped action: " + id);
            });
        }

        internal static bool TryNamedKey(string id, out int key)
        {
            switch (id)
            {
                case "key.enter": key = Vk.Enter; return true;
                case "key.escape": key = Vk.Escape; return true;
                case "key.up": key = Vk.Up; return true;
                case "key.down": key = Vk.Down; return true;
                case "key.left": key = Vk.Left; return true;
                case "key.right": key = Vk.Right; return true;
                case "key.pageup": key = Vk.PageUp; return true;
                case "key.pagedown": key = Vk.PageDown; return true;
                case "key.home": key = Vk.Home; return true;
                case "key.end": key = Vk.End; return true;
                case "key.tab": key = Vk.Tab; return true;
                case "key.space": key = Vk.Space; return true;
                default: key = 0; return false;
            }
        }

        internal static bool TryControllerTmuxAction(string id, out int action)
        {
            switch (id)
            {
                case "tmux.pane.left": action = (int)TerminalTmuxAction.SelectLeft; return true;
                case "tmux.pane.right": action = (int)TerminalTmuxAction.SelectRight; return true;
                case "tmux.pane.up": action = (int)TerminalTmuxAction.SelectUp; return true;
                case "tmux.pane.down": action = (int)TerminalTmuxAction.SelectDown; return true;
                case "tmux.session.previous": action = (int)TerminalTmuxAction.PreviousSession; return true;
                case "tmux.session.next": action = (int)TerminalTmuxAction.NextSession; return true;
                default: action = 0; return false;
            }
        }

        private void NavigateProfile(int layer, bool forward)
        {
            switch (layer)
            {
                case 1: _chatGpt.NavigateAdjacentSidebarTask(forward); break;
                case 2: TmuxSessionClient.ExecuteActiveAction(forward ? TerminalTmuxAction.NextWindow : TerminalTmuxAction.PreviousWindow); break;
                case 4: InputSender.Chord(forward ? Vk.Right : Vk.Left, Vk.LeftAlt); break;
                case 3: InputSender.Chord(forward ? Vk.OemCloseBrackets : Vk.OemOpenBrackets, Vk.LeftControl); break;
                case 5: InputSender.Tap(forward ? Vk.Right : Vk.Left); break;
                case 6: TotalCommanderCommand(forward ? 571 : 570); break;
                case 7: MessagingButton(forward ? 2 : 1); break;
                default: InputSender.Chord(forward ? Vk.Right : Vk.Left, Vk.LeftAlt); break;
            }
        }
    }
}
