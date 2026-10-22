using System;
using System.Collections.Generic;

namespace Cxt1024Bridge
{
    internal static class AuditApplicationTests
    {
        internal static void Run()
        {
            var success = new PaletteHarness();
            Assert(success.Run() && string.Join(",", success.Actions.ToArray()) == "open,select,type,enter",
                "stable exact palette executes each stage once");
            var absent = new PaletteHarness { NeverFind = true };
            Assert(!absent.Run() && absent.Actions.Count == 1 && absent.Waits == 15,
                "missing palette only permits bounded lookup, never typing");
            var loading = new PaletteHarness { InitialMissingReads = 2 };
            Assert(loading.Run() && loading.Waits == 2, "initial palette creation may be retried while context is stable");
            var unselected = new PaletteHarness { ExactSelected = false };
            Assert(!unselected.Run() && !unselected.Actions.Contains("enter") && unselected.Waits == 15,
                "fuzzy or unverified command results never receive Enter");
            var wrongValue = new PaletteHarness { WrongValue = true };
            Assert(!wrongValue.Run() && !wrongValue.Actions.Contains("enter"), "unexpected input text cannot be confirmed");
            for (int read = 1; read <= 6; read++)
            {
                var lost = new PaletteHarness { LoseGuardAtRead = read };
                Assert(!lost.Run() && !lost.Actions.Contains("enter"), "window loss during palette read " + read + " cancels later input");
            }
            for (int read = 2; read <= 6; read++)
            {
                var lostFocus = new PaletteHarness { LoseFocusAtRead = read };
                Assert(!lostFocus.Run() && !lostFocus.Actions.Contains("enter"), "same-window focus loss at stage " + read + " cancels");
                var replacedInput = new PaletteHarness { ReplaceInputAtRead = read };
                Assert(!replacedInput.Run() && !replacedInput.Actions.Contains("enter"), "replacement input at stage " + read + " cancels");
                var replacedPalette = new PaletteHarness { ReplacePaletteAtRead = read };
                Assert(!replacedPalette.Run() && !replacedPalette.Actions.Contains("enter"), "replacement prompt at stage " + read + " cancels");
            }
            foreach (string action in new[] { "open", "select", "type" })
            {
                var lost = new PaletteHarness { LoseAfterAction = action };
                Assert(!lost.Run() && lost.Actions[lost.Actions.Count - 1] == action,
                    "loss after " + action + " prevents every later input stage");
            }
            var initialWaitLoss = new PaletteHarness { NeverFind = true, LoseDuringWait = true };
            Assert(!initialWaitLoss.Run() && initialWaitLoss.Waits == 1 && initialWaitLoss.Actions.Count == 1,
                "focus loss during initial wait cancels without typing");
            var resultWaitLoss = new PaletteHarness { ExactSelected = false, LoseDuringWait = true };
            Assert(!resultWaitLoss.Run() && resultWaitLoss.Waits == 1 && !resultWaitLoss.Actions.Contains("enter"),
                "focus loss during result wait cancels without Enter");
            var invalid = new PaletteHarness();
            Assert(!ApplicationInputSafety.RunObsidianCommandSequence("bad\ncommand", invalid.Callbacks()) && invalid.Actions.Count == 0,
                "multiline command cannot become keyboard input");
            Assert(!ApplicationInputSafety.IsPaletteInputIdentity("cm-content", "", "Search"), "ordinary editor/search is not a palette");
            Assert(!ApplicationInputSafety.IsPaletteInputIdentity("not-prompt-input", "", ""), "class substring does not authorize input");
            Assert(ApplicationInputSafety.IsPaletteInputIdentity("prompt-input extra", "", ""), "exact prompt-input class is recognized");
            Assert(!ApplicationInputSafety.SameRuntimeId(new[] { 1, 2 }, new[] { 1, 3 }), "runtime identity must match every component");

            TestNotionSequence();
            Console.WriteLine("Audit application regression tests passed (injected callbacks; no real windows, UIA or keyboard input).");
        }

        private static void TestNotionSequence()
        {
            foreach (string loss in new[] { "none", "before", "expand", "find", "wait" })
            {
                bool current = loss != "before";
                int expanded = 0, invoked = 0, collapsed = 0, waited = 0;
                bool result = NotionAutomation.ToggleFullWidthSequence<string>(
                    delegate { return current; },
                    delegate { expanded++; if (loss == "expand") current = false; return true; },
                    delegate { if (loss == "find") current = false; return loss == "wait" ? null : "original-window-item"; },
                    delegate(string item) { invoked++; return item == "original-window-item"; },
                    delegate { collapsed++; },
                    delegate(int milliseconds) { waited++; if (loss == "wait") current = false; });
                Assert(result == (loss == "none") && invoked == (loss == "none" ? 1 : 0), "Notion loss at " + loss + " gates invocation");
                Assert(collapsed == 0, "Notion never collapses a menu after focus loss: " + loss);
                if (loss == "before") Assert(expanded == 0, "Notion does not expand after preflight loss");
                if (loss == "wait") Assert(waited == 1, "Notion does not keep polling after wait loses context");
            }
            int attempts = 0, collapses = 0;
            Assert(!NotionAutomation.ToggleFullWidthSequence<string>(delegate { return true; }, delegate { return true; },
                delegate { attempts++; return null; }, delegate(string item) { throw new InvalidOperationException("Unexpected invoke"); },
                delegate { collapses++; }, delegate(int milliseconds) { }) && attempts == 20 && collapses == 1,
                "Notion unavailable menu times out only within original root and safely collapses once");
        }

        private sealed class PaletteHarness
        {
            internal readonly List<string> Actions = new List<string>();
            internal bool Current = true;
            internal bool NeverFind;
            internal bool ExactSelected = true;
            internal bool WrongValue;
            internal bool LoseDuringWait;
            internal int InitialMissingReads;
            internal int LoseGuardAtRead;
            internal int LoseFocusAtRead;
            internal int ReplaceInputAtRead;
            internal int ReplacePaletteAtRead;
            internal string LoseAfterAction;
            internal int Waits;
            private int _reads;
            private string _value = string.Empty;

            internal bool Run()
            {
                return ApplicationInputSafety.RunObsidianCommandSequence("Copy Obsidian URL", Callbacks());
            }

            internal ObsidianCommandCallbacks Callbacks()
            {
                return new ObsidianCommandCallbacks
                {
                    Guard = delegate { return Current; },
                    OpenPalette = delegate { Record("open"); },
                    SelectAll = delegate { Record("select"); },
                    TypeText = delegate(string text) { _value = WrongValue ? "Other command" : text; Record("type"); },
                    PressEnter = delegate { Record("enter"); },
                    Wait = delegate(int milliseconds) { Waits++; if (LoseDuringWait) Current = false; },
                    ReadFocusedPalette = delegate
                    {
                        _reads++;
                        if (_reads == LoseGuardAtRead) Current = false;
                        if (NeverFind || _reads <= InitialMissingReads || _reads == LoseFocusAtRead) return null;
                        return new ObsidianPaletteSnapshot
                        {
                            InputRuntimeId = new[] { 10, _reads == ReplaceInputAtRead ? 99 : 1 },
                            PaletteRuntimeId = new[] { 20, _reads == ReplacePaletteAtRead ? 99 : 1 },
                            Value = _value,
                            ExactCommandSelected = ExactSelected
                        };
                    }
                };
            }

            private void Record(string action)
            {
                Actions.Add(action);
                if (LoseAfterAction == action) Current = false;
            }
        }

        private static void Assert(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Audit application regression failed: " + name);
        }
    }
}
