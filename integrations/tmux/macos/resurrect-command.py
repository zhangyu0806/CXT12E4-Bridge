#!/usr/bin/env python3
"""Save supported Agent names for tmux-resurrect without their arguments.

Resurrect's ps strategy only sees children of pane_pid. Native executables may
be pane_pid itself, while wrappers may report an interpreter command instead.
Use tmux's foreground command for supported Agents and retain the installed
plugin's original strategy for every other process. Restoring starts a fresh
Agent; it does not resume a conversation or replay the original command line.
"""

import argparse
import re
import subprocess
import sys


AGENTS = {"claude", "opencode", "grok"}


def agent_command(tmux, pane_pid):
    try:
        # Inherit TMUX so plugin invocations stay on their own server. -N must
        # prevent a stale/missing socket from starting an unrelated server.
        result = subprocess.run(
            [tmux, "-N", "list-panes", "-a", "-F",
             "#{pane_pid}\t#{pane_current_command}"],
            capture_output=True, text=True, timeout=5,
        )
    except (OSError, subprocess.TimeoutExpired):
        return None
    if result.returncode:
        return None
    commands = set()
    for line in result.stdout.splitlines():
        fields = line.split("\t")
        if len(fields) == 2 and fields[0] == pane_pid:
            commands.add(fields[1])
    if len(commands) == 1:
        command = commands.pop()
        if command in AGENTS:
            return command
    return None


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tmux", required=True)
    parser.add_argument("--fallback", required=True)
    parser.add_argument("--bash", required=True)
    parser.add_argument("pane_pid")
    args = parser.parse_args(argv)
    if not re.fullmatch(r"[1-9][0-9]*", args.pane_pid):
        parser.error("pane_pid must be a positive process ID")
    command = agent_command(args.tmux, args.pane_pid)
    if command:
        print(command)
        return 0
    try:
        return subprocess.run(
            [args.bash, args.fallback, args.pane_pid], timeout=5,
        ).returncode
    except (OSError, subprocess.TimeoutExpired):
        print("resurrect command: the original save strategy failed", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
