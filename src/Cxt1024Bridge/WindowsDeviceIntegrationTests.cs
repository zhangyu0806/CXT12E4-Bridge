using System;
using System.Collections.Generic;

namespace Cxt1024Bridge
{
    internal static class WindowsDeviceIntegrationTests
    {
        internal static void Run()
        {
            int count = 0;
            Action<bool, string> check = delegate(bool condition, string name)
            { count++; if (!condition) throw new InvalidOperationException("Device integration: " + name); };
            string first = "10\t/dev/pts/1\t$1\t%7\t@3", second = "20\t/dev/pts/2\t$2\t%8\t@4";
            var one = TmuxSessionClient.ResolveUniqueClientTarget(new[] { first }, null);
            check(one.PaneId == "%7", "one client is exact");
            bool rejected = false;
            try { TmuxSessionClient.ResolveUniqueClientTarget(new[] { first, second }, null); }
            catch (InvalidOperationException) { rejected = true; }
            check(rejected, "ordinary actions reject ambiguous terminal clients");
            check(TmuxSessionClient.ResolveUniqueClientTarget(new[] { first, second }, "/dev/pts/1").PaneId == "%7", "explicit client wins over activity");
            rejected = false;
            try { TmuxSessionClient.ResolveUniqueClientTarget(new[] { first }, "/dev/pts/missing"); }
            catch (InvalidOperationException) { rejected = true; }
            check(rejected, "missing explicit client does not fall back");
            foreach (string direction in new[] { "left", "right", "up", "down" })
            {
                int action; check(ActionEngine.TryControllerTmuxAction("tmux.pane." + direction, out action), "pane action resolves");
                IList<string> command = TmuxSessionClient.BuildActiveActionArguments((TerminalTmuxAction)action, one);
                check(command[0] == "select-pane" && command[3] == "%7", "direction targets original pane");
            }
            foreach (string direction in new[] { "previous", "next" })
            {
                int action; check(ActionEngine.TryControllerTmuxAction("tmux.session." + direction, out action), "session action resolves");
                IList<string> command = TmuxSessionClient.BuildActiveActionArguments((TerminalTmuxAction)action, one);
                check(command[0] == "switch-client" && command[2] == "/dev/pts/1", "cycle only existing sessions on explicit client");
            }
            int key;
            check(!ActionEngine.TryNamedKey("key.command", out key), "unknown key is rejected");
            check(ActionEngine.TryNamedKey("key.enter", out key) && key == Vk.Enter, "Enter maps to native key");
            check(!ActionEngine.TryControllerTmuxAction("tmux.new-session", out key), "no implicit session creation action");
            var layers = new LayerManager();
            layers.ObserveForeground(new ForegroundInfo { Window = new IntPtr(100), ProcessId = 20, AutomaticLayer = 3 });
            long before = layers.ContextGeneration; layers.InvalidateInputContext();
            check(layers.ContextGeneration > before && layers.CurrentLayer == 3, "helper boundary invalidates queued work without changing layer");
            double now = 0.49;
            check(!ActionEngine.PrepareTimedShortcut(0.5, delegate { now += 0.1; return true; }, delegate { return true; }, delegate { return now; }), "marker wait cannot outlive input deadline");
            now = 0.1;
            check(ActionEngine.PrepareTimedShortcut(0.5, delegate { now += 0.1; return true; }, delegate { return true; }, delegate { return now; }), "fresh action survives marker release");
            check(!ActionEngine.PrepareTimedShortcut(0.5, delegate { return false; }, delegate { return true; }, delegate { return now; }), "held marker blocks shortcut");
            check(!ActionEngine.PrepareTimedShortcut(0.5, delegate { return true; }, delegate { return false; }, delegate { return now; }), "changed context blocks shortcut");
            Console.WriteLine("PASS " + count + " Windows device integration checks (no desktop input)");
        }
    }
}
