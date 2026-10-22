using System;
using System.Collections.Generic;

namespace Cxt1024Bridge
{
    // Pure regression checks: no WSL process, tmux server, live mutex, UI,
    // keyboard hook or HID access is performed by this test suite.
    internal static class AuditRuntimeTests
    {
        internal static void Run()
        {
            var target = new TerminalSessionTarget
            {
                Slot = 4,
                SessionId = "$4",
                SessionCreatedUnix = 1700000000,
                ServerPid = 624,
                SessionIdentity = "$4@1700000000:624",
                SessionName = "04-work"
            };
            IList<string> command = TmuxSessionClient.BuildGuardedSwitchCommand("/dev/pts/2", target);
            Assert(command.Count == 9 && command[0] == "--exec" && command[1] == "bash" && command[2] == "-c",
                "guarded switch uses an argv-based WSL command");
            Assert(command[3] == "cutoff=$(date +%s%N) || exit 70; printf '%s\\n' \"$cutoff\"; exec tmux if-shell -F -t \"$1\" \"$2\" \"$3\" \"$4\"",
                "WSL cutoff precedes a synchronous server-side guard with positional arguments");
            Assert(command[5] == "$4" && command[6] ==
                "#{&&:#{==:#{session_id},$4},#{&&:#{==:#{session_created},1700000000},#{==:#{pid},624}}}",
                "guard checks session ID, creation time and server PID together");
            Assert(command[7] == "switch-client -c '/dev/pts/2' -t '$4' ; display-message -p CXT-TMUX-SWITCHED",
                "success receipt follows the switch in the same non-yielding command group");
            Assert(command[8] == "display-message -p CXT-TMUX-IDENTITY-MISMATCH",
                "stale generation branch contains no session mutation");
            Assert(TmuxSessionClient.ParseGuardedSwitchResult("1700000000000000123\nCXT-TMUX-SWITCHED\n") == 1700000000000000123L,
                "confirmed switch retains the WSL nanosecond cutoff");
            AssertThrows<InvalidOperationException>(delegate { TmuxSessionClient.ParseGuardedSwitchResult("1700000000000000123\nCXT-TMUX-IDENTITY-MISMATCH\n"); },
                "reused ID with a different generation cannot be acknowledged");
            AssertThrows<InvalidOperationException>(delegate { TmuxSessionClient.ParseGuardedSwitchResult("1700000000000000123\n"); },
                "a cutoff alone is not proof of a switch");
            AssertThrows<InvalidOperationException>(delegate { TmuxSessionClient.ParseGuardedSwitchResult("0\nCXT-TMUX-SWITCHED\n"); },
                "nonpositive cutoff is rejected");
            AssertThrows<InvalidOperationException>(delegate { TmuxSessionClient.ParseGuardedSwitchResult("123\nCXT-TMUX-SWITCHED\nunexpected\n"); },
                "ambiguous command output is rejected");
            Assert(TmuxSessionClient.QuoteTmuxCommandArgument("/dev/pts/2'; display-message $HOME \\ \"") ==
                "'/dev/pts/2'\\''; display-message $HOME \\ \"'",
                "client metacharacters stay inside a literal tmux argument");
            AssertThrows<ArgumentException>(delegate { TmuxSessionClient.BuildGuardedSwitchCommand("client\ncommand", target); },
                "multiline client identity is rejected");
            AssertThrows<ArgumentException>(delegate { TmuxSessionClient.BuildGuardedSwitchCommand("/dev/pts/2", new TerminalSessionTarget { SessionId = "$4", SessionCreatedUnix = 1 }); },
                "missing server generation is rejected before process execution");
            AssertThrows<ArgumentException>(delegate { TmuxSessionClient.BuildGuardedSwitchCommand("/dev/pts/2", new TerminalSessionTarget { SessionId = "$4;kill-server", SessionCreatedUnix = 1, ServerPid = 2 }); },
                "command text cannot enter the session-ID format guard");
            Assert(TmuxSessionClient.ResolveSessionTarget(4, new[] { "$4\t1700000000\t624\t04-work" }) != null,
                "valid current session metadata still resolves");
            Assert(TmuxSessionClient.ResolveSessionTarget(4, new[] { "$4\t0\t624\t04-work", "$5\t1700000000\t-1\t04-work" }) == null,
                "incomplete generation metadata cannot authorize switching");

            int stops = 0;
            Func<bool> stop = delegate { stops++; return true; };
            Assert(BridgeApplicationContext.PrepareResidentExit(false, stop) && stops == 0,
                "deployment --exit leaves the watchdog running");
            Assert(BridgeApplicationContext.PrepareResidentExit(true, stop) && stops == 1,
                "explicit tray quit requires stopping the watchdog first");
            Assert(!BridgeApplicationContext.PrepareResidentExit(true, delegate { return false; }),
                "unconfirmed watchdog shutdown keeps the resident alive");
            AssertThrows<InvalidOperationException>(delegate
            {
                BridgeApplicationContext.PrepareResidentExit(true, delegate { throw new InvalidOperationException("test stop failure"); });
            }, "watchdog errors cannot silently approve resident teardown");
            Console.WriteLine("Audit runtime regression tests passed (pure logic; no live tmux/watchdog/UI actions).");
        }

        private static void Assert(bool condition, string name)
        {
            if (!condition)
                throw new InvalidOperationException("Audit runtime regression failed: " + name);
        }

        private static void AssertThrows<T>(Action action, string name) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new InvalidOperationException("Audit runtime regression failed: " + name);
        }
    }
}
