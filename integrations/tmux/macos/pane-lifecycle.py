#!/usr/bin/env python3
"""Recover an exited Agent to the default shell and explicitly relaunch it.

Runtime arguments stay in a pane-local tmux option, never in a repository file.
Every invocation is tied to a socket, server PID and exact pane ID. No shell
evaluation is used to decode tmux's quoted pane_start_command representation.
"""

import argparse
import json
import os
from pathlib import Path
import re
import shlex
import shutil
import stat
import subprocess
import sys


OPTION = "@cxt12e4-pane-original"
AGENTS = {"claude", "opencode", "grok"}
PANE_ID = re.compile(r"%[0-9]+\Z")


class LifecycleError(Exception):
    pass


def agent_argv(raw):
    """Decode direct argv or a simple Agent/exec shell command; reject grammar.

    tmux quotes each original argv element. A one-element shell command therefore
    needs a second tokenization, while a direct argv must retain literal arguments.
    Complex shell expressions and expansions are deliberately unsupported.
    """
    try:
        argv = shlex.split(raw, posix=True)
        if not argv:
            return None
        if len(argv) == 1 and Path(argv[0]).name not in AGENTS:
            command = argv[0]
            if any(c in command for c in "$`\n\r"):
                return None
            lexer = shlex.shlex(command, posix=True, punctuation_chars=";&|<>()")
            lexer.whitespace_split = True
            lexer.commenters = ""
            argv = list(lexer)
            if any(word and all(c in ";&|<>()" for c in word) for word in argv):
                return None
            if argv and argv[0] == "exec":
                argv = argv[1:]
        if not argv or Path(argv[0]).name not in AGENTS:
            return None
        if any("\0" in word for word in argv):
            return None
        return argv
    except ValueError:
        return None


class Lifecycle:
    def __init__(self, binary, socket, server_pid):
        self.binary = binary
        self.socket = os.path.realpath(socket)
        self.server_pid = str(server_pid)
        if not self.server_pid.isdecimal():
            raise LifecycleError("an explicit server PID is required")
        try:
            if not stat.S_ISSOCK(os.stat(self.socket).st_mode):
                raise LifecycleError("the tmux socket is unavailable")
        except OSError as error:
            raise LifecycleError("the tmux socket is unavailable") from error
        self.identity()
        # tmux may take PATH from an outside invoking client during respawn.
        # Use the selected server's configured executable search path instead.
        self.environment = dict(os.environ, PATH=self.server_path()[5:])

    def call(self, *args):
        result = subprocess.run(
            [self.binary, "-N", "-S", self.socket, *args],
            capture_output=True, text=True, timeout=5,
            env=getattr(self, "environment", None),
        )
        if result.returncode:
            raise LifecycleError("tmux command failed: " + args[0])
        return result.stdout.rstrip("\n")

    def identity(self):
        identity = self.call("display-message", "-p", "#{pid}\t#{socket_path}")
        fields = identity.split("\t")
        if len(fields) != 2 or fields[0] != self.server_pid or os.path.realpath(fields[1]) != self.socket:
            raise LifecycleError("tmux server identity changed")

    def pane(self, pane_id):
        if not PANE_ID.fullmatch(pane_id):
            raise LifecycleError("an exact pane ID is required")
        self.identity()
        fields = {}
        # Separate reads preserve literal tabs/newlines in paths and arguments.
        for key in ("pane_id", "pane_pid", "pane_dead", "pane_start_command", "pane_start_path", "pane_current_path"):
            fields[key] = self.call("display-message", "-p", "-t", pane_id, "#{" + key + "}")
        if fields["pane_id"] != pane_id:
            raise LifecycleError("pane identity changed")
        return fields

    def read_state(self, pane_id):
        raw = self.call("show-options", "-p", "-q", "-v", "-t", pane_id, OPTION)
        if not raw:
            return None
        try:
            state = json.loads(raw)
            valid = (
                state["schema"] == 1 and state["pane_id"] == pane_id
                and state["server_pid"] == self.server_pid and state["socket"] == self.socket
                and isinstance(state["argv"], list) and state["argv"]
                and all(isinstance(x, str) and "\0" not in x for x in state["argv"])
                and Path(state["argv"][0]).name in AGENTS
                and isinstance(state["cwd"], str) and os.path.isabs(state["cwd"])
                and state["phase"] in ("agent", "shell")
            )
            if valid:
                return state
        except (ValueError, TypeError, KeyError):
            pass
        raise LifecycleError("saved pane identity or command is invalid")

    def write_state(self, state):
        self.identity()
        self.call("set-option", "-p", "-t", state["pane_id"], OPTION,
                  json.dumps(state, ensure_ascii=True, separators=(",", ":")))

    def server_path(self):
        value = self.call("show-environment", "-g", "PATH")
        if not value.startswith("PATH="):
            raise LifecycleError("the tmux server PATH is unavailable")
        return value

    def default_shell(self, pane_id):
        shell = self.call("display-message", "-p", "-t", pane_id, "#{default-shell}")
        if not os.path.isabs(shell) or not os.access(shell, os.X_OK):
            raise LifecycleError("the configured default shell is unavailable")
        return shell

    def launch_restored(self, pane_id, agent):
        """Called by exec inside a newly restored pane, before execing its Agent."""
        pane = self.pane(pane_id)
        if agent not in AGENTS or os.environ.get("TMUX_PANE") != pane_id:
            raise LifecycleError("the restored Agent or pane identity is invalid")
        if pane["pane_dead"] != "0" or pane["pane_pid"] != str(os.getpid()):
            raise LifecycleError("restore launch must replace the target pane's own shell")
        state = {"schema": 1, "socket": self.socket, "server_pid": self.server_pid,
                 "pane_id": pane_id, "argv": [agent], "cwd": os.getcwd(), "phase": "agent"}
        program = shutil.which(agent, path=self.server_path()[5:])
        if not program:
            state["phase"] = "shell"
            self.write_state(state)
            shell = self.default_shell(pane_id)
            print("{} is not installed; returning to the default shell.".format(agent), flush=True)
            os.execv(shell, [shell, "-l"])
        self.write_state(state)
        try:
            os.execv(program, [agent])
        except OSError:
            # Keep the pane useful even if the executable vanished after lookup.
            state["phase"] = "shell"
            self.write_state(state)
            shell = self.default_shell(pane_id)
            print("{} could not start; returning to the default shell.".format(agent), flush=True)
            os.execv(shell, [shell, "-l"])

    def remember(self, pane_id, pane=None):
        pane = pane or self.pane(pane_id)
        state = self.read_state(pane_id)
        if state:
            return state
        argv = agent_argv(pane["pane_start_command"])
        if not argv:
            return None
        state = {"schema": 1, "socket": self.socket, "server_pid": self.server_pid,
                 "pane_id": pane_id, "argv": argv,
                 "cwd": pane["pane_start_path"] or pane["pane_current_path"], "phase": "agent"}
        if not os.path.isabs(state["cwd"]):
            raise LifecycleError("the original Agent directory is unavailable")
        self.write_state(state)
        return state

    def recover(self, pane_id):
        pane = self.pane(pane_id)
        if pane["pane_dead"] != "1":
            return False
        state = self.remember(pane_id, pane)
        if not state or state["phase"] == "shell":
            return False
        shell = self.default_shell(pane_id)
        current = self.pane(pane_id)
        if current["pane_dead"] != "1" or current["pane_pid"] != pane["pane_pid"]:
            return False
        state["phase"] = "shell"
        self.write_state(state)
        directory = state["cwd"] if os.path.isdir(state["cwd"]) else str(Path.home())
        # No -k: tmux itself refuses if this pane became live during the hook.
        self.call("respawn-pane", "-t", pane_id, "-c", directory,
                  "-e", self.server_path(), shell, "-l")
        self.call("display-message", "-t", pane_id,
                  "Agent exited; prefix + Shift+R relaunches the original Agent")
        return True

    def restart(self, pane_id):
        pane = self.pane(pane_id)
        state = self.remember(pane_id, pane)
        if not state:
            # Preserve native command argv for other explicit respawn requests.
            # Empty/default shell panes were a no-op in the original binding.
            if not pane["pane_start_command"]:
                return False
            self.identity()
            self.call("respawn-pane", "-k", "-t", pane_id)
            return True
        current = self.pane(pane_id)
        if current["pane_pid"] != pane["pane_pid"]:
            raise LifecycleError("pane process changed before restart")
        if not os.path.isdir(state["cwd"]):
            raise LifecycleError("the original Agent directory is unavailable")
        program = state["argv"][0]
        program = (os.path.join(state["cwd"], program) if "/" in program
                   else shutil.which(program, path=self.server_path()[5:]))
        if not program or not os.path.isfile(program) or not os.access(program, os.X_OK):
            raise LifecycleError("the original Agent executable is unavailable")
        state["phase"] = "agent"
        self.write_state(state)
        # A single tmux command argument is interpreted by a shell. env provides
        # multiple argv elements even for a bare Agent name, with no evaluation.
        self.call("respawn-pane", "-k", "-t", pane_id, "-c", state["cwd"],
                  "-e", self.server_path(),
                  "/usr/bin/env", "--", program, *state["argv"][1:])
        return True


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tmux", default=os.environ.get("TMUX_BIN") or shutil.which("tmux") or "tmux")
    parser.add_argument("--socket", required=True)
    parser.add_argument("--server-pid", required=True)
    commands = parser.add_subparsers(dest="command", required=True)
    remember = commands.add_parser("remember")
    target = remember.add_mutually_exclusive_group(required=True)
    target.add_argument("--all", action="store_true")
    target.add_argument("--pane")
    for action in ("recover", "restart"):
        commands.add_parser(action).add_argument("pane")
    restored = commands.add_parser("launch-restored")
    restored.add_argument("agent", choices=sorted(AGENTS))
    restored.add_argument("--pane", required=True)
    args = parser.parse_args()
    try:
        lifecycle = Lifecycle(args.tmux, args.socket, args.server_pid)
        if args.command == "remember":
            panes = lifecycle.call("list-panes", "-a", "-F", "#{pane_id}").splitlines() if args.all else [args.pane]
            count = sum(bool(lifecycle.remember(pane)) for pane in panes)
            print("Remembered {} Agent pane(s); running processes unchanged.".format(count))
        elif args.command == "launch-restored":
            lifecycle.launch_restored(args.pane, args.agent)
        else:
            getattr(lifecycle, args.command)(args.pane)
    except (LifecycleError, OSError, subprocess.TimeoutExpired) as error:
        print("pane lifecycle: " + str(error), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
