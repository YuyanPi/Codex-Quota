#!/bin/zsh
set -euo pipefail

repository_root="${0:A:h:h}"
build_root="$repository_root/artifacts/macos"
app="$build_root/Codex Quota Bar.app"

mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"

clang \
  "$repository_root/CodexQuotaBarMac/Sources/main.m" \
  -fobjc-arc \
  -framework Cocoa \
  -framework CoreImage \
  -mmacosx-version-min=13.0 \
  -O2 \
  -o "$app/Contents/MacOS/CodexQuotaBar"

cp "$repository_root/CodexQuotaBarMac/Info.plist" "$app/Contents/Info.plist"
cp "$repository_root/CodexQuotaBarMac/Resources/AppIcon.icns" "$app/Contents/Resources/AppIcon.icns"
codesign --force --deep --sign - "$app"

echo "$app"
