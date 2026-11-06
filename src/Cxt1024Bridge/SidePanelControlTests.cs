using System;

namespace Cxt1024Bridge
{
    internal static class SidePanelControlTests
    {
        private static void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("Side panel regression: " + name);
        }

        internal static void Run()
        {
            foreach (string name in new[] { "Toggle side panel", "显示/隐藏侧边面板", "切換側邊面板" })
            {
                Check(SidePanelControl.IsToggle(name, true, true, true, true), "localized toggle");
                Check(!SidePanelControl.IsToggle(name, false, true, true, true), "not conversation text");
                Check(!SidePanelControl.IsToggle(name, true, false, true, true), "not hidden");
                Check(!SidePanelControl.IsToggle(name, true, true, false, true), "not disabled");
                Check(!SidePanelControl.IsToggle(name, true, true, true, false), "requires direct invocation");
            }
            foreach (string name in new[] { null, "", "Changes", "变更", "Show sidebar", "显示侧边栏", "贴图", "Toggle side panel notes" })
                Check(!SidePanelControl.IsToggle(name, true, true, true, true), "unrelated control");
            string[] candidates = { "hidden", "toggle", "other" };
            Func<string, bool> matches = delegate(string name) { return name == "toggle"; };
            Check(SidePanelControl.FindUnique(candidates, matches) == "toggle", "unique candidate");
            Check(SidePanelControl.FindUnique(new[] { "toggle", "toggle" }, matches) == null, "ambiguous candidates");
            Check(SidePanelControl.FindUnique(new[] { "other" }, matches) == null, "missing candidate");

            int finds = 0, invokes = 0;
            bool current = true;
            Func<string> find = delegate { finds++; return "toggle"; };
            Func<string, bool> invoke = delegate(string target) { invokes++; return true; };
            Check(!SidePanelControl.Dispatch(null, find, invoke), "missing guard");
            Check(!SidePanelControl.Dispatch(delegate { return false; }, find, invoke), "stale at entry");
            Check(finds == 0 && invokes == 0, "cancel before lookup");
            Check(!SidePanelControl.Dispatch(delegate { return current; }, delegate { current = false; return "toggle"; }, invoke), "focus changed during lookup");
            Check(invokes == 0, "cancel before invocation");
            Check(!SidePanelControl.Dispatch(delegate { return true; }, delegate { return (string)null; }, invoke), "missing UI");
            Check(invokes == 0, "no fallback for missing UI");
            Check(!SidePanelControl.Dispatch(delegate { return true; }, find, delegate(string target) { invokes++; return false; }), "failed invocation");
            Check(invokes == 1, "failure is not retried");
            Check(SidePanelControl.Dispatch(delegate { return true; }, find, invoke), "valid dispatch");
            Check(invokes == 2, "one invocation per press");
            Console.WriteLine("Side panel control checks passed (no UI or keyboard input).");
        }
    }
}
