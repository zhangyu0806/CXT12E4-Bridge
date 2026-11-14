#!/bin/bash
# Fixed overrides in dock-slots.txt; every unconfigured slot follows the live Dock.
exec /usr/bin/python3 "$(/usr/bin/dirname "$0")/dock-launcher.py" "$@"
