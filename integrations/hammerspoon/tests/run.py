#!/usr/bin/env python3
"""Run mocked regressions in isolated Lua VMs; never connect to live Hammerspoon."""
import argparse
import ctypes
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_LUASKIN = Path("/Applications/Hammerspoon.app/Contents/Frameworks/LuaSkin.framework/Versions/A/LuaSkin")


def run_luaskin(library, source):
    lua = ctypes.CDLL(str(library))
    lua.luaL_newstate.restype = ctypes.c_void_p
    lua.luaL_openlibs.argtypes = [ctypes.c_void_p]
    lua.luaL_loadfilex.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_char_p]
    lua.lua_pcallk.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                            ctypes.c_ssize_t, ctypes.c_void_p]
    lua.lua_tolstring.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p]
    lua.lua_tolstring.restype = ctypes.c_char_p
    lua.lua_close.argtypes = [ctypes.c_void_p]
    state = lua.luaL_newstate()
    if not state:
        raise RuntimeError("Unable to create isolated Lua state")
    try:
        lua.luaL_openlibs(state)
        code = lua.luaL_loadfilex(state, str(source).encode(), None)
        if code == 0:
            code = lua.lua_pcallk(state, 0, 0, 0, 0, None)
        if code:
            message = lua.lua_tolstring(state, -1, None)
            print(message.decode(errors="replace") if message else "Lua error", file=sys.stderr)
        return 1 if code else 0
    finally:
        lua.lua_close(state)


def lua54(command):
    try:
        result = subprocess.run([command, "-e", "print(_VERSION)"], capture_output=True,
                                text=True, timeout=10, check=False)
        return result.returncode == 0 and result.stdout.strip() == "Lua 5.4"
    except (OSError, subprocess.TimeoutExpired):
        return False


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    runtime = parser.add_mutually_exclusive_group()
    runtime.add_argument("--lua", help="Lua 5.4 executable (auto-detected by default)")
    runtime.add_argument("--luaskin", type=Path, help="Hammerspoon LuaSkin library path")
    parser.add_argument("--verbose", action="store_true", help="Print every passing check")
    parser.add_argument("--luaskin-child", type=Path, help=argparse.SUPPRESS)
    args = parser.parse_args()
    if args.luaskin_child:
        if not args.luaskin:
            parser.error("--luaskin-child needs --luaskin")
        return run_luaskin(args.luaskin.resolve(), args.luaskin_child.resolve())

    command = args.lua
    if command and not lua54(command):
        parser.error("--lua must name a working Lua 5.4 executable")
    if not command and not args.luaskin:
        for name in ("lua5.4", "lua54", "lua"):
            candidate = shutil.which(name)
            if candidate and lua54(candidate):
                command = candidate
                break
    if command:
        prefix = [command]
        runtime_name = command
    else:
        library = (args.luaskin or DEFAULT_LUASKIN).resolve()
        if not library.is_file():
            parser.error("Install Lua 5.4, or use --luaskin with a macOS Hammerspoon LuaSkin library")
        prefix = [sys.executable, str(Path(__file__).resolve()), "--luaskin", str(library), "--luaskin-child"]
        runtime_name = str(library)

    print("Runtime:", runtime_name)
    failed = 0
    suites = sorted((ROOT / "tests").glob("test_*.lua"))
    for suite in suites:
        try:
            result = subprocess.run(prefix + [str(suite)], cwd=ROOT, capture_output=True,
                                    text=True, timeout=60, check=False)
        except (OSError, subprocess.TimeoutExpired) as exc:
            print("FAIL", suite.name, exc, file=sys.stderr)
            failed += 1
            continue
        output = result.stdout.strip()
        if args.verbose or result.returncode:
            print(output)
        elif output:
            print(suite.name + ": " + output.splitlines()[-1])
        if result.stderr:
            print(result.stderr, file=sys.stderr, end="" if result.stderr.endswith("\n") else "\n")
        failed += result.returncode != 0
    print(f"{len(suites)} suites, {failed} failed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
