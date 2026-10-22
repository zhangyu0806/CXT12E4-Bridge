#!/usr/bin/env python3
"""Unit checks plus the real plugin on isolated sockets and stub Agent binaries."""

import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import shlex
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import unittest
from unittest.mock import patch


SCRIPT = Path(__file__).with_name("resurrect-command.py")
LIFECYCLE = Path(__file__).with_name("pane-lifecycle.py")
RESTORE = Path(__file__).with_name("resurrect-restore.py")
SPEC = importlib.util.spec_from_file_location("resurrect_command", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
TMUX = os.environ.get("TMUX_BIN") or shutil.which("tmux")
BASH = os.environ.get("BASH_BIN") or shutil.which("bash")
CC = shutil.which("cc")
PLUGIN = Path(os.environ.get("RESURRECT_PLUGIN", str(Path.home() / ".tmux/plugins/tmux-resurrect")))


class CommandTests(unittest.TestCase):
    def test_exact_foreground_names_only(self):
        for command in ("claude", "opencode", "grok", "bun", "2.1.72", "my-claude", "claude --resume", "/bin/claude"):
            with self.subTest(command=command):
                result = subprocess.CompletedProcess([], 0, "123\t" + command + "\n321\tclaude\n")
                with patch.object(MODULE.subprocess, "run", return_value=result) as run:
                    self.assertEqual(MODULE.agent_command("/tmux", "123"), command if command in MODULE.AGENTS else None)
                    self.assertEqual(run.call_args.args[0][:3], ["/tmux", "-N", "list-panes"])
                    self.assertNotIn("env", run.call_args.kwargs)

    def test_unknown_pid_and_inconsistent_inventory_use_fallback(self):
        for output in ("321\tclaude\n", "123\tclaude\n123\tzsh\n", "123\tclaude\textra\n"):
            with patch.object(MODULE.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, output)):
                self.assertIsNone(MODULE.agent_command("/tmux", "123"))

    def test_non_agent_delegates_to_original_strategy(self):
        with patch.object(MODULE, "agent_command", return_value=None), patch.object(MODULE.subprocess, "run", return_value=subprocess.CompletedProcess([], 7)) as run:
            self.assertEqual(MODULE.main(["--tmux", "/tmux", "--fallback", "/plugin/ps.sh", "--bash", "/bash", "123"]), 7)
            run.assert_called_once_with(["/bash", "/plugin/ps.sh", "123"], timeout=5)

    def test_agent_never_reads_original_arguments(self):
        with patch.object(MODULE, "agent_command", return_value="claude"), patch.object(MODULE.subprocess, "run") as run, contextlib.redirect_stdout(io.StringIO()) as output:
            self.assertEqual(MODULE.main(["--tmux", "/tmux", "--fallback", "/plugin/ps.sh", "--bash", "/bash", "123"]), 0)
            self.assertEqual(output.getvalue(), "claude\n")
            run.assert_not_called()

    def test_missing_server_and_timeout_use_original_strategy(self):
        for value in (subprocess.CompletedProcess([], 1, ""), OSError("missing"), subprocess.TimeoutExpired("tmux", 5)):
            kwargs = {"side_effect": value} if isinstance(value, Exception) else {"return_value": value}
            with patch.object(MODULE.subprocess, "run", **kwargs):
                self.assertIsNone(MODULE.agent_command("/tmux", "123"))


@unittest.skipUnless(TMUX and BASH and CC and (PLUGIN / "scripts/save.sh").exists(), "tmux, bash, cc and tmux-resurrect are required")
class PluginTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="cxt-resurrect-", dir="/tmp")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.bin = self.root / "bin"
        self.bin.mkdir()
        self.plugin = self.root / "plugin"
        shutil.copytree(PLUGIN, self.plugin, symlinks=True, ignore=shutil.ignore_patterns(".git"))
        self.log = self.root / "agent.log"
        source = self.root / "agent.c"
        source.write_text('''#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
int main(int argc, char **argv) {
    FILE *out = fopen(getenv("AGENT_TEST_LOG"), "a");
    if (!out) return 2;
    char *name = strrchr(argv[0], '/');
    fprintf(out, "%s", name ? name + 1 : argv[0]);
    for (int i = 1; i < argc; i++) fprintf(out, "\\t%s", argv[i]);
    fprintf(out, "\\n"); fclose(out);
    for (;;) pause();
}
''')
        subprocess.run([CC, str(source), "-o", str(self.bin / "claude")], check=True, capture_output=True)
        for agent in ("opencode", "grok"):
            shutil.copy2(self.bin / "claude", self.bin / agent)
        (self.bin / "tmux").symlink_to(TMUX)
        self.env = {**os.environ, "PATH": str(self.bin) + ":/usr/bin:/bin:/usr/sbin:/sbin",
                    "AGENT_TEST_LOG": str(self.log), "SHELL": "/bin/sh"}
        self.env.pop("TMUX", None)
        self.env.pop("TMUX_PANE", None)
        self.sockets = []
        self.addCleanup(self.stop_servers)
        wrapper = self.plugin / "save_command_strategies/cxt-macos.sh"
        # An installed strategy may be a symlink back to the user's checkout.
        # Replace the copied link itself so this fixture never edits its target.
        wrapper.unlink(missing_ok=True)
        wrapper.write_text("#!/bin/sh\nexec " + " ".join(shlex.quote(x) for x in [sys.executable, str(SCRIPT), "--tmux", TMUX, "--fallback", str(self.plugin / "save_command_strategies/ps.sh"), "--bash", BASH]) + ' "$1"\n')
        wrapper.chmod(0o755)
        for agent in MODULE.AGENTS:
            wrapper = self.plugin / "strategies" / (agent + "_cxt-macos.sh")
            wrapper.unlink(missing_ok=True)
            wrapper.write_text("#!/bin/sh\nexec " + shlex.join(
                [sys.executable, str(RESTORE), "--tmux", TMUX, "--python", sys.executable,
                 "--lifecycle", str(LIFECYCLE)]) + ' "$@"\n')
            wrapper.chmod(0o755)

    def stop_servers(self):
        for socket in self.sockets:
            subprocess.run([TMUX, "-N", "-S", str(socket), "kill-server"], capture_output=True)

    def tm(self, socket, *args):
        return subprocess.run([TMUX, "-N", "-S", str(socket), *args], env=self.env,
                              check=True, capture_output=True, text=True).stdout.strip()

    def start(self, name, session, *command):
        socket = self.root / name
        self.sockets.append(socket)
        subprocess.run([TMUX, "-S", str(socket), "-f", "/dev/null", "new-session", "-d", "-s", session, *command],
                       env=self.env, check=True, capture_output=True)
        self.tm(socket, "set-option", "-g", "default-shell", "/bin/sh")
        self.tm(socket, "set-option", "-g", "default-command", "/bin/sh -i")
        self.tm(socket, "set-option", "-g", "@resurrect-dir", str(self.root / "saved"))
        self.tm(socket, "set-option", "-g", "@resurrect-processes", "claude opencode grok")
        self.tm(socket, "set-option", "-g", "@resurrect-save-command-strategy", "cxt-macos")
        self.tm(socket, "set-option", "-g", "@resurrect-never-overwrite", "on")
        self.tm(socket, "set-window-option", "-g", "remain-on-exit", "on")
        for agent in MODULE.AGENTS:
            self.tm(socket, "set-option", "-g", "@resurrect-strategy-" + agent, "cxt-macos")
        server = self.tm(socket, "display-message", "-p", "#{pid}")
        hook = shlex.join([sys.executable, str(LIFECYCLE), "--tmux", TMUX,
                           "--socket", str(socket), "--server-pid", server, "recover", "#{pane_id}"])
        self.tm(socket, "set-hook", "-g", "pane-died[80]", "run-shell -b " + shlex.quote(hook))
        return socket

    def plugin_command(self, socket, name):
        server = self.tm(socket, "display-message", "-p", "#{pid}")
        env = {**self.env, "TMUX": str(socket) + "," + server + ",0"}
        return subprocess.run([BASH, str(self.plugin / "scripts" / name), "quiet"],
                              env=env, check=True, capture_output=True, text=True, timeout=20)

    def wait(self, condition):
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            if condition():
                return
            time.sleep(0.03)
        self.fail("isolated Agent fixture did not reach the expected state")

    def entries(self):
        return self.log.read_text().splitlines() if self.log.exists() else []

    def test_real_save_restore_uses_names_and_starts_fresh_agents(self):
        save = self.start("save.sock", "agents", str(self.bin / "claude"), "--private-fixture")
        for agent in ("opencode", "grok"):
            self.tm(save, "new-window", "-d", "-t", "agents", str(self.bin / agent), "--private-fixture")
        self.wait(lambda: len(self.entries()) == 3)
        self.wait(lambda: set(self.tm(save, "list-panes", "-a", "-F", "#{pane_current_command}").splitlines()) == MODULE.AGENTS)
        self.plugin_command(save, "save.sh")
        snapshot = (self.root / "saved/last").read_text()
        rows = [line.split("\t") for line in snapshot.splitlines() if line.startswith("pane\t")]
        self.assertEqual({row[10] for row in rows}, {":" + name for name in MODULE.AGENTS})
        self.assertNotIn("--private-fixture", snapshot)
        self.tm(save, "kill-server")
        restore = self.start("restore.sock", "anchor", "/bin/sh", "-i")
        anchor_pid = self.tm(restore, "display-message", "-p", "-t", "anchor", "#{pane_pid}")
        self.plugin_command(restore, "restore.sh")
        self.wait(lambda: len(self.entries()) == 6)
        self.assertEqual(set(self.entries()[3:]), MODULE.AGENTS)
        commands = self.tm(restore, "list-panes", "-a", "-F", "#{pane_current_command}").splitlines()
        self.assertEqual(len(commands), 4)
        self.assertTrue(MODULE.AGENTS.issubset(commands))
        self.assertEqual(self.tm(restore, "display-message", "-p", "-t", "anchor", "#{pane_pid}"), anchor_pid)
        panes = [line.split("\t") for line in self.tm(restore, "list-panes", "-a", "-F",
                 "#{pane_id}\t#{pane_pid}\t#{pane_current_command}").splitlines()]
        for pane, pid, command in panes:
            if command in MODULE.AGENTS:
                state = json.loads(self.tm(restore, "show-options", "-pqv", "-t", pane, "@cxt12e4-pane-original"))
                self.assertEqual(state["argv"], [command])
        pane, pid, _ = next(row for row in panes if row[2] == "claude")
        os.kill(int(pid), signal.SIGTERM)
        self.wait(lambda: self.tm(restore, "display-message", "-p", "-t", pane, "#{pane_dead}") == "0"
                  and json.loads(self.tm(restore, "show-options", "-pqv", "-t", pane, "@cxt12e4-pane-original"))["phase"] == "shell")
        subprocess.run([sys.executable, str(LIFECYCLE), "--tmux", TMUX, "--socket", str(restore),
                        "--server-pid", self.tm(restore, "display-message", "-p", "#{pid}"),
                        "restart", pane], env=self.env, check=True, capture_output=True)
        self.wait(lambda: len(self.entries()) == 7)
        self.assertEqual(self.entries()[-1], "claude")
        self.assertEqual(self.tm(restore, "display-message", "-p", "-t", "anchor", "#{pane_pid}"), anchor_pid)


if __name__ == "__main__":
    unittest.main()
