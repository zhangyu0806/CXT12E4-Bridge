#!/usr/bin/env python3
"""Resolve each press from fixed overrides or fresh Dock preferences (Python 3.9+)."""
import argparse
import json
from pathlib import Path
import plistlib
import subprocess
import sys
from urllib.parse import unquote, urlsplit


CONFIG = Path(__file__).resolve().with_name("dock-slots.txt")


class LaunchError(Exception):
    pass


def fixed_apps(config):
    try:
        text = config.read_text(encoding="utf-8")
    except FileNotFoundError:
        return {}
    apps = {}
    for line in text.splitlines():
        parts = line.strip().split(maxsplit=1)
        if len(parts) == 2 and parts[0] in {str(n) for n in range(1, 12)}:
            apps.setdefault(int(parts[0]), parts[1])
    return apps


def app_name(value):
    """Match a fixed app against either the Dock label or its bundle filename."""
    name = Path(value.rstrip("/")).name
    return (name[:-4] if name.lower().endswith(".app") else name).casefold()


def remaining_dock_apps(apps, fixed):
    names = {app_name(value) for value in fixed.values()}
    remaining = []
    for tile in apps:
        data = tile.get("tile-data", {}) if isinstance(tile, dict) else {}
        label = data.get("file-label", "")
        path = unquote(urlsplit(data.get("file-data", {}).get("_CFURLString", "")).path)
        if any(value and app_name(value) in names for value in [label, path]):
            continue
        remaining.append(tile)
    return remaining


def read_dock():
    # defaults queries cfprefsd; reading the plist file directly can see stale order.
    result = subprocess.run(["/usr/bin/defaults", "export", "com.apple.dock", "-"],
                            capture_output=True, check=True, timeout=3)
    data = plistlib.loads(result.stdout)
    apps = data.get("persistent-apps")
    if not isinstance(apps, list):
        raise LaunchError("无法读取 Dock 常驻应用顺序")
    return apps


def resolve_slot(slot, config=CONFIG, dock_reader=read_dock):
    if not 1 <= slot <= 11:
        raise LaunchError("应用位必须为 1–11；普通键盘的 0 对应第 10 位")
    fixed = fixed_apps(config)
    app = fixed.get(slot)
    if app:
        return {"slot": slot, "source": "fixed", "name": app, "target": app}
    # Fixed apps occupy their own slots even when absent from persistent-apps
    # (notably Finder). Fill free slots from the remaining Dock apps in order.
    apps = remaining_dock_apps(dock_reader(), fixed)
    index = sum(number not in fixed for number in range(1, slot + 1)) - 1
    if index >= len(apps):
        raise LaunchError("Dock 没有足够的剩余应用填入第 %d 位" % slot)
    # Empty tiles retain a place; an invalid target must not launch its neighbor.
    tile = apps[index]
    data = tile.get("tile-data", {}) if isinstance(tile, dict) else {}
    raw_url = data.get("file-data", {}).get("_CFURLString", "")
    url = urlsplit(raw_url)
    path = Path(unquote(url.path))
    if (url.scheme != "file" or url.netloc not in ("", "localhost")
            or url.query or url.fragment or not path.is_absolute()
            or path.suffix.lower() != ".app"):
        raise LaunchError("启动位 %d 对应的 Dock 项不是应用（可能是空白占位）" % slot)
    if not path.is_dir():
        raise LaunchError("启动位 %d 对应的应用已移走，请重新固定到 Dock" % slot)
    return {"slot": slot, "source": "dock", "name": data.get("file-label") or path.stem,
            "target": str(path)}


def launch(target):
    if target["source"] == "fixed" and target["target"] == "Finder" and Path("/Applications/Hammerspoon.app").is_dir():
        # Retain the existing Finder helper, including its current-screen placement.
        args = ["/usr/bin/open", "-g", "hammerspoon://show-finder"]
    else:
        args = ["/usr/bin/open", "-a", target["target"]]
    subprocess.run(args, capture_output=True, check=True, timeout=5)


def notify(message):
    # Pass text as argv, never interpolate an app name into AppleScript or shell code.
    script = ('on run argv\n'
              'display notification (item 1 of argv) with title "数字快启"\n'
              'end run\n')
    try:
        subprocess.run(["/usr/bin/osascript", "-", message], input=script,
                       text=True, capture_output=True, timeout=3)
    except (OSError, subprocess.SubprocessError):
        pass


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("slot", nargs="?", type=int, choices=range(0, 12))
    parser.add_argument("--resolve", action="store_true", help="print target without launching")
    parser.add_argument("--list", action="store_true", help="print slots 1–10 without launching")
    args = parser.parse_args(argv)
    if args.slot is None and not args.list:
        parser.error("provide a slot or --list")
    try:
        if args.list:
            apps = read_dock()
            rows = []
            for slot in range(1, 11):
                try:
                    rows.append(resolve_slot(slot, dock_reader=lambda: apps))
                except LaunchError as error:
                    rows.append({"slot": slot, "error": str(error)})
            print(json.dumps(rows, ensure_ascii=False, indent=2))
        else:
            slot = 10 if args.slot == 0 else args.slot
            target = resolve_slot(slot)
            if args.resolve:
                print(json.dumps(target, ensure_ascii=False))
            else:
                launch(target)
        return 0
    except (LaunchError, OSError, ValueError, plistlib.InvalidFileException,
            subprocess.SubprocessError) as error:
        message = str(error) if isinstance(error, LaunchError) else "读取或打开应用失败，请检查 Dock 和 dock-slots.txt"
        print(message, file=sys.stderr)
        if not (args.resolve or args.list):
            notify(message)
        return 1


if __name__ == "__main__":
    sys.exit(main())
