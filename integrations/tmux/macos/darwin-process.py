#!/usr/bin/env python3
"""Translate the Linux process queries used by winsettings on macOS."""
import os
from pathlib import Path
import subprocess
import sys

def elapsed_seconds(value):
    days, sep, clock = value.strip().rpartition("-")
    seconds = 0
    for part in clock.split(":"):
        seconds = seconds * 60 + int(part)
    return seconds + (int(days) * 86400 if sep else 0)

def main():
    name = sys.argv[1] if len(sys.argv) > 1 else ""
    args = sys.argv[2:]
    if name == "ps":
        if args[:2] == ["-o", "etimes="]:
            result = subprocess.run(["/bin/ps", "-o", "etime=", *args[2:]], capture_output=True, text=True)
            if result.returncode:
                return result.returncode
            try:
                for line in result.stdout.splitlines():
                    print(elapsed_seconds(line))
            except ValueError:
                return 1
            return 0
        os.execv("/bin/ps", ["ps", *args])
    if name == "pgrep":
        args = ["-fl" if arg == "-af" else arg for arg in args]
        os.execv("/usr/bin/pgrep", ["pgrep", *args])
    return 2

if __name__ == "__main__":
    sys.exit(main())
