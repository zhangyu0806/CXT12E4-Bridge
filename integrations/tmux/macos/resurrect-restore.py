#!/usr/bin/env python3
"""Return a safe tmux-resurrect strategy command for three named Agents only."""

import argparse
import os
import shlex
import sys


AGENTS = {"claude", "opencode", "grok"}


def restore_command(python, lifecycle, tmux, agent, environment):
    if agent not in AGENTS:
        raise ValueError("only an exact supported Agent name can be restored")
    fields = environment.get("TMUX", "").rsplit(",", 2)
    if len(fields) != 3 or not os.path.isabs(fields[0]) or not fields[1].isdecimal():
        raise ValueError("the tmux restore server identity is unavailable")
    command = [python, lifecycle, "--tmux", tmux, "--socket", fields[0],
               "--server-pid", fields[1], "launch-restored", agent]
    # This variable is deliberately expanded by the target pane's shell, not
    # the strategy process (whose TMUX_PANE belongs to the restore caller).
    return "exec " + shlex.join(command) + ' --pane "$TMUX_PANE"'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tmux", required=True)
    parser.add_argument("--python", required=True)
    parser.add_argument("--lifecycle", required=True)
    parser.add_argument("agent", choices=sorted(AGENTS))
    parser.add_argument("directory")
    args = parser.parse_args()
    try:
        print(restore_command(args.python, args.lifecycle, args.tmux, args.agent, os.environ))
    except ValueError as error:
        print("resurrect restore: " + str(error), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
