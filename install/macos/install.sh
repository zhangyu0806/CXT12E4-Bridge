#!/bin/bash
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
"$root/install/macos/build.sh"
/usr/bin/python3 -m unittest discover -s "$root/src/Cxt12E4Mac" -p 'test_*.py'
/usr/bin/python3 "$root/install/macos/setup_tmux.py" --if-configured --apply --reload
# File Provider may reattach Finder metadata to a bundle under Documents.
# Validate a clean local staging copy before stopping the running Bridge.
stage="$(mktemp -d /private/tmp/cxt12e4-install.XXXXXX)"
trap 'rm -rf "$stage"' EXIT
ditto --norsrc "$root/outputs/CXT12E4 Bridge.app" "$stage/CXT12E4 Bridge.app"
xattr -cr "$stage/CXT12E4 Bridge.app"
codesign --verify --deep --strict "$stage/CXT12E4 Bridge.app"
app='/Applications/CXT12E4 Bridge.app'
support="$HOME/Library/Application Support/CXT12E4Bridge"
mkdir -p "$support/backups"
if [ -d "$app" ]; then
  ditto --norsrc "$app" "$support/backups/CXT12E4 Bridge-$(date +%Y%m%d-%H%M%S).app"
fi
launchctl bootout "gui/$(id -u)/com.zhangyu.cxt12e4.bridge" 2>/dev/null || true
pkill -x CXT12E4Bridge 2>/dev/null || true
ditto --norsrc "$stage/CXT12E4 Bridge.app" "$app"
xattr -cr "$app"
codesign --verify --deep --strict "$app"
/usr/bin/python3 "$root/install/macos/install_integrations.py"
launchctl bootstrap "gui/$(id -u)" "$HOME/Library/LaunchAgents/com.zhangyu.cxt12e4.bridge.plist"
printf 'Installed %s\n' "$app"
