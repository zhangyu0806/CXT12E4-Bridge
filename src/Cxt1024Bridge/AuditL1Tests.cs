using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Cxt1024Bridge
{
    // These checks use delegate fakes and synthetic rollout records only.
    // No UI Automation, key injection, real task, or model action is invoked.
    internal static class AuditL1Tests
    {
        private const string Url = "https://chatgpt.com/";

        internal static void Run()
        {
            ModelIntentTests.Run();
            SidePanelControlTests.Run();
            BrowserFocusChecks();
            MenuExpansionChecks();
            CompactPositionChecks();
            SidebarSelectionChecks();
            // Rollout/colour checks live in CodexTaskStateTests so Windows and
            // the portable status suite execute the same regression coverage.
        }

        private static void Check(bool condition, string name)
        {
            if (!condition)
                throw new InvalidOperationException("L1 audit regression: " + name);
        }

        private static void MenuExpansionChecks()
        {
            Check(ChatGptAutomation.ResolveReasoningMenuExpanded("选择强度", true, false), "visible menu beats stale native collapsed state");
            Check(ChatGptAutomation.ResolveReasoningMenuExpanded("Select effort", false, false), "open trigger beats stale native collapsed state");
            Check(!ChatGptAutomation.ResolveReasoningMenuExpanded("GPT-6 Astra 高", false, true), "closed label beats stale native expanded state");
            Check(ChatGptAutomation.ResolveReasoningMenuExpanded("GPT-6 Astra 高", true, false), "slider prevents premature closed result");
            Check(ChatGptAutomation.ResolveReasoningMenuExpanded("legacy menu", false, true), "legacy menu retains native expansion state");
            int requests = 0;
            int reads = 0;
            Check(ChatGptAutomation.ChangeMenuExpansion(false, delegate { return false; },
                delegate { requests++; }, delegate(int ms) { }), "closed menu stays closed");
            Check(requests == 0, "do not toggle an already closed menu");
            Check(ChatGptAutomation.ChangeMenuExpansion(false, delegate { return ++reads < 4; },
                delegate { requests++; }, delegate(int ms) { }), "wait for fresh asynchronous state");
            Check(requests == 1 && reads == 4, "only one collapse while waiting");
            requests = 0;
            reads = 0;
            Check(!ChatGptAutomation.ChangeMenuExpansion(false, delegate { reads++; return true; },
                delegate { requests++; }, delegate(int ms) { }), "API success without closed state fails");
            Check(requests == 1 && reads == 21, "broken provider wait is bounded");
            requests = 0;
            reads = 0;
            Check(ChatGptAutomation.ChangeMenuExpansion(true, delegate { return ++reads >= 3; },
                delegate { requests++; }, delegate(int ms) { }), "expansion also waits for current state");
            Check(requests == 1, "only one expand request");
        }

        private static void CompactPositionChecks()
        {
            for (int rank = 0; rank <= 5; rank++)
            {
                Check(ChatGptAutomation.ParseCompactAstraPosition("GPT-6 Astra 深度，第 " + (rank + 1) + " 项，共 6 项。") == rank,
                    "Chinese live slider position " + rank);
                Check(ChatGptAutomation.ParseCompactAstraPosition("GPT-6 Astra High, " + (rank + 1) + " of 6.") == rank,
                    "English live slider position " + rank);
            }
            Check(ChatGptAutomation.ParseCompactAstraPosition("GPT-5.6 Sol High, 3 of 6.") == -1, "reject other model live status");
            Check(ChatGptAutomation.ParseCompactAstraPosition("GPT-6 Astra High, 3 of 7.") == -1, "reject changed slider topology");
            Check(ChatGptAutomation.ParseCompactAstraPosition("GPT-6 Astra High, 0 of 6.") == -1, "reject zero position");
            Check(ChatGptAutomation.ParseCompactAstraPosition("a task called GPT-6 Astra High, 3 of 6.") == -1, "reject title-like prefix");
            Check(ChatGptAutomation.ParseCompactAstraPosition(null) == -1, "missing live status fails closed");
        }

        private static void BrowserFocusChecks()
        {
            string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"WindowsApps\OpenAI.Codex_26.924.2738.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe");
            var request = ChatGptAutomation.BuildBuiltInBrowserRequest(executable, Url);
            Check(request.FileName == executable && request.Arguments == "\"" + Url + "\"",
                "HTTP URL goes to exact Codex executable as one argument");
            Check(!request.UseShellExecute && request.CreateNoWindow,
                "no OS URL association or console window");
            string complex = "https://chatgpt.com/c/example?one=1&two=a%20b#part";
            Check(ChatGptAutomation.BuildBuiltInBrowserRequest(executable, complex).Arguments == "\"" + complex + "\"",
                "query and fragment stay within the URL argument");
            Check(ChatGptAutomation.BuildBuiltInBrowserRequest(executable, "http://example.com/").Arguments ==
                "\"http://example.com/\"", "HTTP compatibility retained");
            foreach (string path in new[] { null, "", @"C:\temp\ChatGPT.exe", executable.Replace("OpenAI.Codex_", "Other_"),
                executable.Replace("__2p2nqsd0c76g0", "__wrongpublisher"), executable.Replace("ChatGPT.exe", "cmd.exe") })
                Check(!ChatGptAutomation.IsPackagedCodexExecutable(path), "untrusted executable rejected");

            foreach (string invalid in new[] { null, "", "chatgpt.com", "file:///C:/test",
                "javascript:alert(1)", "codex://threads/new", "https://user:pass@chatgpt.com/",
                "https://chatgpt.com/\" --anything", "https://chatgpt.com/\r\n", " https://chatgpt.com/" })
            {
                bool rejected = false;
                try { ChatGptAutomation.BuildBuiltInBrowserRequest(executable, invalid); }
                catch (ArgumentException) { rejected = true; }
                Check(rejected, "non-web/credential URL rejected before dispatch");
            }

            int launches = 0;
            string lastArgument = null;
            string trace = null;
            Action<System.Diagnostics.ProcessStartInfo> launch = delegate(System.Diagnostics.ProcessStartInfo value)
                { launches++; lastArgument = value.Arguments; };
            Action<string> report = delegate(string value) { trace = value; };
            Check(ChatGptAutomation.DispatchBuiltInBrowserRequest(executable, Url, delegate { return true; }, launch, report),
                "valid original context dispatches");
            Check(launches == 1 && lastArgument == request.Arguments, "exactly one native launch, no typing callbacks");
            Check(trace.Contains("asynchronous"), "dispatch is not reported as loaded");

            Check(!ChatGptAutomation.DispatchBuiltInBrowserRequest(executable, Url, delegate { return false; }, launch, report),
                "abandoned input context cancels");
            Check(launches == 1 && trace.Contains("cancelled"), "cancelled action has no launch or fallback");
            Check(!ChatGptAutomation.DispatchBuiltInBrowserRequest(executable, Url, null, launch, report),
                "missing bound-window guard fails closed");
            Check(launches == 1, "unguarded entry point must not launch");

            launches = 0;
            Check(!ChatGptAutomation.DispatchBuiltInBrowserRequest(executable, Url, delegate { return true; },
                delegate(System.Diagnostics.ProcessStartInfo value) { launches++; throw new System.ComponentModel.Win32Exception(2); }, report),
                "missing native handler is an explicit failure");
            Check(launches == 1 && trace.Contains("Win32 error=2"), "handler failure never retries or launches external browser");

            launches = 0;
            Check(!ChatGptAutomation.DispatchBuiltInBrowserRequest(executable, Url, delegate { return true; },
                delegate(System.Diagnostics.ProcessStartInfo value) { launches++; throw new InvalidOperationException(); }, report),
                "invalid launch fails closed");
            Check(launches == 1, "invalid launch has no unsafe input fallback");

            int shortcuts = 0;
            Action send = delegate { shortcuts++; };
            Check(!ChatGptAutomation.DispatchStandaloneNewTask(delegate { return false; }, send, report), "new task cancels stale context");
            Check(!ChatGptAutomation.DispatchStandaloneNewTask(null, send, report), "new task requires a bound window");
            Check(shortcuts == 0, "cancelled new task sends nothing");
            Check(ChatGptAutomation.DispatchStandaloneNewTask(delegate { return true; }, send, report), "standalone command dispatches");
            Check(shortcuts == 1 && trace.Contains("newProjectlessTask") && trace.Contains("not yet confirmed"),
                "standalone command is one shot and not claimed as UI confirmation");
        }

        private static void SidebarSelectionChecks()
        {
            // A, B(X), C, D(X), E: names intentionally repeat, but the selection
            // resolver consumes only authoritative selection scores, never names.
            string[] titles = { "A", "X", "C", "X", "E" };
            Check(titles[1] == titles[3], "duplicate-title fixture");
            var scores = new[] { 0, 0, 10, 0, 0 };
            Check(ChatGptAutomation.AdjacentSidebarTaskIndex(scores, true) == 3, "C -> D with duplicate titles");
            scores[2] = 0;
            scores[3] = 10;
            Check(ChatGptAutomation.AdjacentSidebarTaskIndex(scores, true) == 4, "D -> E, not title-matched B -> C");
            scores[3] = 0;
            scores[0] = 10;
            Check(ChatGptAutomation.AdjacentSidebarTaskIndex(scores, true) == 1, "manual switch overrides previous detent");
            Check(ChatGptAutomation.AdjacentSidebarTaskIndex(scores, false) == -1, "no wrap before first task");
            Check(ChatGptAutomation.AdjacentSidebarTaskIndex(new[] { 0, 10 }, true) == -1, "no wrap after last task");
            Check(ChatGptAutomation.AdjacentSidebarTaskIndex(new[] { 10, 10, 0 }, true) == -1, "ambiguous selection fails closed");
            Check(ChatGptAutomation.AdjacentSidebarTaskIndex(new[] { 0, 1, 2 }, true) == -1, "hover-only rows are not selected");
        }


    }
}
