#!/usr/bin/env python3
"""Real tmux integration checks; every test owns a separate temporary socket."""

import importlib.util
import json
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import sys
import tempfile
import time
import unittest


SCRIPT = Path(__file__).with_name("pane-lifecycle.py")
SPEC = importlib.util.spec_from_file_location("pane_lifecycle", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
TMUX = os.environ.get("TMUX_BIN") or shutil.which("tmux")


class DecodeTests(unittest.TestCase):
    def test_bare_agents(self):
        for command in ("claude", "opencode", "grok"):
            self.assertEqual(MODULE.agent_argv(command), [command])

    def test_single_command_representation(self):
        self.assertEqual(MODULE.agent_argv('"exec claude --model sonnet"'),
                         ["claude", "--model", "sonnet"])

    def test_direct_arguments_keep_literals(self):
        self.assertEqual(MODULE.agent_argv('claude "argument with spaces" "$(touch /tmp/no)"'),
                         ["claude", "argument with spaces", "$(touch /tmp/no)"])

    def test_shell_grammar_is_not_evaluated(self):
        for value in ('"exec claude; touch /tmp/no"', '"claude $(id)"',
                      '"cd /tmp && exec claude"', '"exec claude | cat"', '"bash -lc \'exec claude\'"'):
            self.assertIsNone(MODULE.agent_argv(value), value)


@unittest.skipUnless(TMUX, "tmux is required for isolated integration tests")
class TmuxTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="cxt-pane-test-")
        self.directory = Path(self.temp.name)
        self.socket = str(self.directory / "socket")
        self.bin = self.directory / "bin"
        self.bin.mkdir()
        self.log = self.directory / "agent.jsonl"
        agent = self.bin / "claude"
        agent.write_text(
            "#!" + sys.executable + "\nimport json,os,sys,time\n"
            "with open(os.environ['AGENT_LOG'],'a') as f: "
            "f.write(json.dumps({'argv':sys.argv[1:],'cwd':os.getcwd()})+'\\n')\n"
            "time.sleep(120) if os.environ.get('AGENT_WAIT') == '1' else None\n"
        )
        agent.chmod(0o700)
        for name in ("opencode", "grok"):
            (self.bin / name).symlink_to(agent)
        self.shell = self.bin / "default-shell"
        self.shell.write_text("#!/bin/sh\nexec /bin/sleep 120\n")
        self.shell.chmod(0o700)
        self.env = dict(os.environ, PATH=str(self.bin) + ":" + os.environ["PATH"],
                        AGENT_LOG=str(self.log), AGENT_WAIT="1")
        self.env.pop("TMUX", None)
        self.tm("-f", "/dev/null", "new-session", "-d", "-s", "audit", "/bin/sleep 120")
        self.tm("set-option", "-g", "remain-on-exit", "on")
        self.tm("set-option", "-g", "default-shell", str(self.shell))
        self.server = self.tm("display-message", "-p", "#{pid}")
        self.lifecycle = MODULE.Lifecycle(TMUX, self.socket, self.server)

    def tearDown(self):
        subprocess.run([TMUX, "-N", "-S", self.socket, "kill-server"], capture_output=True)
        self.temp.cleanup()

    def tm(self, *args):
        result = subprocess.run([TMUX, "-S", self.socket, *args], env=self.env,
                                capture_output=True, text=True, timeout=5)
        self.assertEqual(result.returncode, 0, result.stderr)
        return result.stdout.rstrip("\n")

    def wait(self, predicate):
        deadline = time.monotonic() + 3
        while time.monotonic() < deadline:
            if predicate():
                return
            time.sleep(0.02)
        self.fail("isolated pane did not reach its expected state")

    def pane(self, *command):
        return self.tm("new-window", "-d", "-P", "-F", "#{pane_id}",
                       "-c", str(self.directory), *command)

    def dead(self, pane):
        return self.tm("display-message", "-p", "-t", pane, "#{pane_dead}") == "1"

    def entries(self):
        return [json.loads(line) for line in self.log.read_text().splitlines()] if self.log.exists() else []

    def test_remember_does_not_restart_live_agent(self):
        pane = self.pane("/usr/bin/env", "claude")
        # Seed the exact bare-command form used by the office importer.
        self.tm("set-option", "-g", "default-shell", "/bin/sh")
        pane = self.pane("claude")
        self.wait(lambda: len(self.entries()) >= 2)
        before = self.lifecycle.pane(pane)
        state = self.lifecycle.remember(pane)
        self.assertEqual(state["argv"], ["claude"])
        self.assertEqual(self.lifecycle.pane(pane)["pane_pid"], before["pane_pid"])
        self.assertFalse(self.lifecycle.recover(pane))

    def test_bare_agent_exit_recovery_and_explicit_restart(self):
        self.tm("set-environment", "-g", "AGENT_WAIT", "0")
        self.tm("set-option", "-g", "default-shell", "/bin/sh")
        pane = self.pane("claude")
        self.wait(lambda: self.dead(pane))
        self.tm("set-option", "-g", "default-shell", str(self.shell))
        self.assertTrue(self.lifecycle.recover(pane))
        self.assertFalse(self.dead(pane))
        start = self.lifecycle.pane(pane)["pane_start_command"]
        self.assertIn("default-shell", start)
        self.tm("set-environment", "-g", "AGENT_WAIT", "1")
        self.assertTrue(self.lifecycle.restart(pane))
        self.wait(lambda: len(self.entries()) == 2)
        self.assertEqual(self.entries(), [{"argv": [], "cwd": str(self.directory.resolve())}] * 2)
        self.assertFalse(self.dead(pane))

    def test_args_and_cwd_are_preserved_without_shell_evaluation(self):
        self.tm("set-environment", "-g", "AGENT_WAIT", "0")
        literal = "$(touch " + str(self.directory / "must-not-exist") + ")"
        pane = self.pane("claude", "a b", literal, "semi;colon")
        self.wait(lambda: self.dead(pane))
        self.assertTrue(self.lifecycle.recover(pane))
        self.tm("set-environment", "-g", "AGENT_WAIT", "1")
        self.assertTrue(self.lifecycle.restart(pane))
        self.wait(lambda: len(self.entries()) == 2)
        self.assertEqual(self.entries()[0], self.entries()[1])
        self.assertEqual(self.entries()[1]["argv"], ["a b", literal, "semi;colon"])
        self.assertFalse((self.directory / "must-not-exist").exists())

    def test_common_exec_command(self):
        self.tm("set-option", "-g", "default-shell", "/bin/sh")
        self.tm("set-environment", "-g", "AGENT_WAIT", "0")
        pane = self.pane("exec opencode --model 'one two'")
        self.wait(lambda: self.dead(pane))
        self.tm("set-option", "-g", "default-shell", str(self.shell))
        self.assertTrue(self.lifecycle.recover(pane))
        self.tm("set-environment", "-g", "AGENT_WAIT", "1")
        self.assertTrue(self.lifecycle.restart(pane))
        self.wait(lambda: len(self.entries()) == 2)
        self.assertEqual(self.entries()[0], self.entries()[1])

    def test_wrong_server_or_pane_identity_is_rejected(self):
        with self.assertRaises(MODULE.LifecycleError):
            MODULE.Lifecycle(TMUX, self.socket, str(int(self.server) + 1))
        with self.assertRaises(MODULE.LifecycleError):
            self.lifecycle.restart("audit")
        with self.assertRaises(MODULE.LifecycleError):
            self.lifecycle.restart("%99999")

    def test_restore_launcher_cannot_replace_another_live_pane(self):
        self.tm("set-option", "-g", "default-shell", "/bin/sh")
        pane = self.pane("claude")
        before = self.lifecycle.pane(pane)["pane_pid"]
        with self.assertRaises(MODULE.LifecycleError):
            self.lifecycle.launch_restored(pane, "claude")
        self.assertEqual(self.lifecycle.pane(pane)["pane_pid"], before)
        self.assertIsNone(self.lifecycle.read_state(pane))

    def test_missing_restored_agent_returns_to_default_shell(self):
        self.tm("set-option", "-g", "default-shell", "/bin/sh")
        self.tm("set-environment", "-g", "PATH", "/usr/bin:/bin")
        command = shlex.join([sys.executable, str(SCRIPT), "--tmux", TMUX,
                              "--socket", self.socket, "--server-pid", self.server,
                              "launch-restored", "grok"])
        pane = self.pane("exec " + command + ' --pane "$TMUX_PANE"')
        self.wait(lambda: bool(self.lifecycle.read_state(pane)))
        self.assertEqual(self.lifecycle.read_state(pane)["argv"], ["grok"])
        self.assertEqual(self.lifecycle.read_state(pane)["phase"], "shell")
        self.assertFalse(self.dead(pane))

    def test_stale_saved_identity_is_rejected(self):
        self.tm("set-option", "-g", "default-shell", "/bin/sh")
        pane = self.pane("claude")
        state = self.lifecycle.remember(pane)
        state["server_pid"] = "1"
        self.tm("set-option", "-p", "-t", pane, MODULE.OPTION, json.dumps(state))
        before = self.lifecycle.pane(pane)["pane_pid"]
        with self.assertRaises(MODULE.LifecycleError):
            self.lifecycle.restart(pane)
        self.assertEqual(self.lifecycle.pane(pane)["pane_pid"], before)

    def test_recovery_shell_exit_does_not_loop(self):
        self.tm("set-option", "-g", "default-shell", "/bin/sh")
        self.tm("set-environment", "-g", "AGENT_WAIT", "0")
        pane = self.pane("grok")
        self.wait(lambda: self.dead(pane))
        self.assertTrue(self.lifecycle.recover(pane))
        self.tm("respawn-pane", "-k", "-t", pane, "/usr/bin/true")
        self.wait(lambda: self.dead(pane))
        self.assertFalse(self.lifecycle.recover(pane))

    def test_missing_socket_never_starts_server(self):
        missing = str(self.directory / "missing")
        with self.assertRaises(MODULE.LifecycleError):
            MODULE.Lifecycle(TMUX, missing, self.server)
        self.assertFalse(Path(missing).exists())

    def test_real_hook_and_restart_recover_a_second_exit(self):
        self.tm("set-option", "-g", "default-shell", "/bin/sh")
        command = [sys.executable, str(SCRIPT), "--tmux", TMUX,
                   "--socket", self.socket, "--server-pid", self.server,
                   "recover", "#{pane_id}"]
        self.tm("set-hook", "-g", "pane-died[80]",
                "run-shell -b " + shlex.quote(shlex.join(command)))
        self.tm("set-environment", "-g", "AGENT_WAIT", "0")
        pane = self.pane("grok")
        self.wait(lambda: len(self.entries()) == 1 and not self.dead(pane)
                  and self.lifecycle.pane(pane)["pane_start_command"] != "grok")
        self.assertEqual(self.lifecycle.read_state(pane)["phase"], "shell")
        self.lifecycle.restart(pane)
        self.wait(lambda: len(self.entries()) == 2 and not self.dead(pane)
                  and self.lifecycle.read_state(pane)["phase"] == "shell")

    def test_missing_agent_does_not_kill_recovery_shell(self):
        self.tm("set-environment", "-g", "AGENT_WAIT", "0")
        self.tm("set-option", "-g", "default-shell", "/bin/sh")
        pane = self.pane("claude")
        self.wait(lambda: self.dead(pane))
        self.lifecycle.recover(pane)
        (self.bin / "claude").unlink()
        self.tm("set-environment", "-g", "PATH", str(self.bin))
        before = self.lifecycle.pane(pane)["pane_pid"]
        with self.assertRaises(MODULE.LifecycleError):
            self.lifecycle.restart(pane)
        self.assertEqual(self.lifecycle.pane(pane)["pane_pid"], before)

    def test_other_explicit_command_uses_native_respawn_without_requoting(self):
        self.tm("set-option", "-g", "default-shell", "/bin/sh")
        pane = self.pane("printf '%s' 'literal two words'; sleep 120")
        self.wait(lambda: "literal two words" in self.tm("capture-pane", "-p", "-t", pane))
        before = self.lifecycle.pane(pane)
        self.assertTrue(self.lifecycle.restart(pane))
        self.wait(lambda: "literal two words" in self.tm("capture-pane", "-p", "-t", pane))
        after = self.lifecycle.pane(pane)
        self.assertNotEqual(before["pane_pid"], after["pane_pid"])
        self.assertEqual(before["pane_start_command"], after["pane_start_command"])


if __name__ == "__main__":
    unittest.main()
