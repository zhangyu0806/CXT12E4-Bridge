using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Automation;

namespace Cxt1024Bridge
{
    internal sealed class ObsidianPaletteSnapshot
    {
        internal int[] InputRuntimeId;
        internal int[] PaletteRuntimeId;
        internal string Value;
        internal bool ExactCommandSelected;
    }

    internal sealed class ObsidianCommandCallbacks
    {
        internal Func<bool> Guard;
        internal Action OpenPalette;
        internal Func<ObsidianPaletteSnapshot> ReadFocusedPalette;
        internal Action SelectAll;
        internal Action<string> TypeText;
        internal Action PressEnter;
        internal Action<int> Wait;
    }

    internal static class ApplicationInputSafety
    {
        internal static bool RunObsidianCommand(string command, IntPtr window, Func<bool> contextIsCurrent)
        {
            try
            {
                AutomationElement root;
                Func<bool> guard;
                if (!TryBindWindowRoot(window, "obsidian", contextIsCurrent, out root, out guard))
                    return false;
                return RunObsidianCommandSequence(command, new ObsidianCommandCallbacks
                {
                    Guard = guard,
                    OpenPalette = delegate { InputSender.Chord(Vk.P, Vk.LeftControl); },
                    ReadFocusedPalette = delegate { return ReadFocusedPalette(root, command); },
                    SelectAll = delegate { InputSender.Chord(Vk.A, Vk.LeftControl); },
                    TypeText = InputSender.TypeText,
                    PressEnter = delegate { InputSender.Tap(Vk.Enter); },
                    Wait = Thread.Sleep
                });
            }
            catch (Exception ex)
            {
                Logger.Warn("Obsidian command stopped before further input: " + ex.Message);
                return false;
            }
        }

        internal static bool RunObsidianCommandSequence(string command, ObsidianCommandCallbacks callbacks)
        {
            if (string.IsNullOrWhiteSpace(command) || command.Length > 256 ||
                command.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0 || callbacks == null)
                return false;
            if (!callbacks.Guard())
                return false;
            callbacks.OpenPalette();
            if (!callbacks.Guard())
                return false;

            ObsidianPaletteSnapshot bound = null;
            for (int attempt = 0; attempt < 15; attempt++)
            {
                if (!callbacks.Guard())
                    return false;
                bound = callbacks.ReadFocusedPalette();
                if (!callbacks.Guard())
                    return false;
                if (ValidPalette(bound))
                    break;
                callbacks.Wait(40);
                if (!callbacks.Guard())
                    return false;
            }
            if (!ValidPalette(bound) || ReadBoundPalette(callbacks, bound) == null)
                return false;
            callbacks.SelectAll();
            if (ReadBoundPalette(callbacks, bound) == null)
                return false;
            callbacks.TypeText(command);
            if (ReadBoundPalette(callbacks, bound) == null)
                return false;

            for (int attempt = 0; attempt < 15; attempt++)
            {
                ObsidianPaletteSnapshot current = ReadBoundPalette(callbacks, bound);
                if (current == null)
                    return false;
                if (string.Equals(current.Value, command, StringComparison.Ordinal) && current.ExactCommandSelected)
                {
                    // Re-read immediately before Enter. A generic focused edit,
                    // replacement prompt or unmatched fuzzy result must never
                    // receive the final confirmation key.
                    current = ReadBoundPalette(callbacks, bound);
                    if (current == null || !string.Equals(current.Value, command, StringComparison.Ordinal) ||
                        !current.ExactCommandSelected)
                        return false;
                    callbacks.PressEnter();
                    return true;
                }
                callbacks.Wait(40);
                if (!callbacks.Guard())
                    return false;
            }
            return false;
        }

        private static ObsidianPaletteSnapshot ReadBoundPalette(ObsidianCommandCallbacks callbacks, ObsidianPaletteSnapshot bound)
        {
            if (!callbacks.Guard())
                return null;
            ObsidianPaletteSnapshot current = callbacks.ReadFocusedPalette();
            return callbacks.Guard() && ValidPalette(current) &&
                SameRuntimeId(current.InputRuntimeId, bound.InputRuntimeId) &&
                SameRuntimeId(current.PaletteRuntimeId, bound.PaletteRuntimeId) ? current : null;
        }

        private static bool ValidPalette(ObsidianPaletteSnapshot palette)
        {
            return palette != null && palette.InputRuntimeId != null && palette.InputRuntimeId.Length > 0 &&
                palette.PaletteRuntimeId != null && palette.PaletteRuntimeId.Length > 0;
        }

        internal static bool TryBindWindowRoot(IntPtr window, string processName, Func<bool> contextIsCurrent,
            out AutomationElement root, out Func<bool> guard)
        {
            root = null;
            guard = null;
            if (window == IntPtr.Zero || contextIsCurrent == null || !contextIsCurrent() ||
                !NativeMethods.IsWindow(window) || NativeMethods.GetForegroundWindow() != window)
                return false;
            uint processId;
            NativeMethods.GetWindowThreadProcessId(window, out processId);
            if (processId == 0)
                return false;
            using (Process process = Process.GetProcessById(unchecked((int)processId)))
            {
                if (!string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            AutomationElement capturedRoot = AutomationElement.FromHandle(window);
            int[] rootIdentity = capturedRoot.GetRuntimeId();
            guard = delegate
            {
                if (!contextIsCurrent() || !NativeMethods.IsWindow(window) || NativeMethods.GetForegroundWindow() != window)
                    return false;
                uint currentProcessId;
                NativeMethods.GetWindowThreadProcessId(window, out currentProcessId);
                return currentProcessId == processId && capturedRoot.Current.NativeWindowHandle == unchecked((int)window.ToInt64()) &&
                    SameRuntimeId(rootIdentity, capturedRoot.GetRuntimeId()) && contextIsCurrent() &&
                    NativeMethods.GetForegroundWindow() == window;
            };
            if (!guard())
                return false;
            root = capturedRoot;
            return true;
        }

        internal static bool SameRuntimeId(int[] left, int[] right)
        {
            if (left == null || right == null || left.Length == 0 || left.Length != right.Length)
                return false;
            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                    return false;
            }
            return true;
        }

        internal static bool IsWithinRoot(AutomationElement element, AutomationElement root)
        {
            int[] rootIdentity = root.GetRuntimeId();
            for (int depth = 0; element != null && depth < 64; depth++)
            {
                if (SameRuntimeId(element.GetRuntimeId(), rootIdentity))
                    return true;
                element = TreeWalker.RawViewWalker.GetParent(element);
            }
            return false;
        }

        private static ObsidianPaletteSnapshot ReadFocusedPalette(AutomationElement root, string command)
        {
            try
            {
                AutomationElement input = AutomationElement.FocusedElement;
                if (input == null || !input.Current.HasKeyboardFocus || input.Current.IsOffscreen || !input.Current.IsEnabled ||
                    (input.Current.ControlType != ControlType.Edit && input.Current.ControlType != ControlType.ComboBox) ||
                    !IsPaletteInputIdentity(input.Current.ClassName, input.Current.AutomationId, input.Current.Name) ||
                    !IsWithinRoot(input, root))
                    return null;
                object pattern;
                if (!input.TryGetCurrentPattern(ValuePattern.Pattern, out pattern))
                    return null;
                var value = (ValuePattern)pattern;
                if (value.Current.IsReadOnly)
                    return null;
                AutomationElement palette = TreeWalker.RawViewWalker.GetParent(input);
                for (int depth = 0; palette != null && depth < 32; depth++)
                {
                    if (SameRuntimeId(palette.GetRuntimeId(), root.GetRuntimeId()))
                        return null;
                    if (IsPaletteContainerIdentity(palette.Current.ClassName))
                        break;
                    palette = TreeWalker.RawViewWalker.GetParent(palette);
                }
                if (palette == null || !IsPaletteContainerIdentity(palette.Current.ClassName) ||
                    palette.Current.IsOffscreen || !IsWithinRoot(palette, root))
                    return null;
                var snapshot = new ObsidianPaletteSnapshot
                {
                    InputRuntimeId = input.GetRuntimeId(),
                    PaletteRuntimeId = palette.GetRuntimeId(),
                    Value = value.Current.Value,
                    ExactCommandSelected = HasExactSelectedCommand(palette, command)
                };
                // Descendant enumeration can take time; do not mistake the
                // edit that was focused before the read for the current focus.
                AutomationElement stillFocused = AutomationElement.FocusedElement;
                return stillFocused != null && input.Current.HasKeyboardFocus &&
                    SameRuntimeId(stillFocused.GetRuntimeId(), snapshot.InputRuntimeId) ? snapshot : null;
            }
            catch (ElementNotAvailableException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        internal static bool IsPaletteInputIdentity(string className, string automationId, string name)
        {
            return HasClass(className, "prompt-input") || HasClass(className, "suggestion-input") ||
                string.Equals(automationId, "command-palette-input", StringComparison.Ordinal) ||
                string.Equals(name, "Type a command...", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Type a command…", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "输入命令…", StringComparison.Ordinal) ||
                string.Equals(name, "输入命令...", StringComparison.Ordinal);
        }

        private static bool IsPaletteContainerIdentity(string className)
        {
            return HasClass(className, "prompt") || HasClass(className, "command-palette") ||
                HasClass(className, "suggestion-modal");
        }

        private static bool HasExactSelectedCommand(AutomationElement palette, string command)
        {
            int selectedCount = 0;
            bool matched = false;
            AutomationElementCollection items = palette.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            for (int i = 0; i < items.Count; i++)
            {
                AutomationElement item = items[i];
                if (item.Current.IsOffscreen || !item.Current.IsEnabled)
                    continue;
                object selection;
                bool selected = HasClass(item.Current.ClassName, "is-selected") || HasClass(item.Current.ClassName, "mod-selected") ||
                    (item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out selection) &&
                     ((SelectionItemPattern)selection).Current.IsSelected);
                if (!selected)
                    continue;
                selectedCount++;
                if (string.Equals((item.Current.Name ?? string.Empty).Trim(), command, StringComparison.OrdinalIgnoreCase))
                    matched = true;
                else
                {
                    AutomationElement text = item.FindFirst(TreeScope.Descendants,
                        new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                            new PropertyCondition(AutomationElement.NameProperty, command)));
                    if (text != null && !text.Current.IsOffscreen)
                        matched = true;
                }
            }
            return selectedCount == 1 && matched;
        }

        private static bool HasClass(string className, string expected)
        {
            string[] classes = (className ?? string.Empty).Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < classes.Length; i++)
            {
                if (string.Equals(classes[i], expected, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }
}
