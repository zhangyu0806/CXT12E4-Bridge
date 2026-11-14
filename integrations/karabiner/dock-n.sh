#!/bin/bash
# Keep fixed apps, then fill free slots from the remaining apps in live Dock order.
exec /usr/bin/python3 "$(/usr/bin/dirname "$0")/dock-launcher.py" "$@"
