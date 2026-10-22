#!/bin/bash
set -euo pipefail

if [ "$(uname -s)" != "Darwin" ]; then
  printf '%s\n' 'This test requires macOS AppKit and Xcode Command Line Tools.' >&2
  exit 2
fi

root="$(cd "$(dirname "$0")/.." && pwd)"
stage="$(mktemp -d "${TMPDIR:-/tmp}/cxt-settings-controls.XXXXXX")"
trap 'rm -rf "$stage"' EXIT

# Compile a stable source snapshot without the application entry point.
# The fixture creates local controls only: no windows, activation, or input.
sources=()
for source in "$root"/src/Cxt12E4Mac/*.swift; do
  name="$(basename "$source")"
  [ "$name" != "main.swift" ] || continue
  cp "$source" "$stage/$name"
  sources+=("$stage/$name")
done
cp "$root/tests/fixtures/macos-settings-controls.swift" "$stage/main.swift"
xcrun swiftc -swift-version 5 -target "$(uname -m)-apple-macos13.0" -O \
  "${sources[@]}" "$stage/main.swift" -o "$stage/settings-controls" \
  -framework AppKit -framework IOKit -framework GameController
"$stage/settings-controls"
