#!/bin/bash
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
stage="$(mktemp -d /private/tmp/cxt12e4-build.XXXXXX)"
trap 'rm -rf "$stage"' EXIT
bundle="$stage/CXT12E4 Bridge.app"
mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"
xcrun swiftc -swift-version 5 -target "$(uname -m)-apple-macos13.0" -O "$root"/src/Cxt12E4Mac/*.swift -o "$bundle/Contents/MacOS/CXT12E4Bridge" -framework AppKit -framework IOKit -framework GameController
cat > "$bundle/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleExecutable</key><string>CXT12E4Bridge</string>
<key>CFBundleIdentifier</key><string>com.zhangyu.cxt12e4.bridge</string>
<key>CFBundleName</key><string>CXT12E4 Bridge</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleShortVersionString</key><string>1.1</string>
<key>CFBundleVersion</key><string>20261022</string>
<key>LSUIElement</key><true/>
<key>LSMinimumSystemVersion</key><string>13.0</string>
<key>NSInputMonitoringUsageDescription</key><string>读取 CXT12E4 专用键盘及 Xbox 手柄的按键与摇杆。</string>
<key>NSAccessibilityUsageDescription</key><string>把键盘和手柄操作转换为当前应用的快捷键、鼠标移动与滚动。</string>
<key>NSAppleEventsUsageDescription</key><string>执行用户按键触发的 Finder 操作。</string>
</dict></plist>
PLIST
for f in "$root"/src/Cxt12E4Mac/*.py; do [ ! -f "$f" ] || cp "$f" "$bundle/Contents/Resources/"; done
cp "$root/src/Cxt12E4Mac/使用说明.html" "$bundle/Contents/Resources/"
cp "$root/LICENSE" "$root/THIRD_PARTY_NOTICES.md" "$bundle/Contents/Resources/"
mkdir -p "$bundle/Contents/Resources/firmware/cxt12e4_chatgpt_bridge"
cp "$root/firmware/cxt12e4_chatgpt_bridge/LICENSE" "$bundle/Contents/Resources/firmware/cxt12e4_chatgpt_bridge/"
ditto --norsrc "$root/docs/cxt12e4-manual" "$bundle/Contents/Resources/manual"
xattr -cr "$bundle"
codesign --force --sign - --identifier com.zhangyu.cxt12e4.bridge -r='designated => identifier "com.zhangyu.cxt12e4.bridge"' "$bundle"
"$bundle/Contents/MacOS/CXT12E4Bridge" --self-test
codesign --verify --deep --strict "$bundle"
mkdir -p "$root/outputs"
ditto --norsrc "$bundle" "$root/outputs/CXT12E4 Bridge.app"
# File Provider can reattach FinderInfo under Documents after this cleanup.
# Signature validation uses the clean stage above and the installer's own stage.
xattr -cr "$root/outputs/CXT12E4 Bridge.app"
printf '%s\n' "$root/outputs/CXT12E4 Bridge.app"
