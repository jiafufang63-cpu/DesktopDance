#!/bin/bash
# 重新生成 AppIcon.icns（改了 tools/make-icon.swift 的设计之后运行，然后再 ./build.sh）
set -euo pipefail
cd "$(dirname "$0")/.."

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

swiftc -O tools/make-icon.swift -framework AppKit -o "$TMP/make-icon" 2>/dev/null
"$TMP/make-icon" "$TMP/icon-1024.png" >/dev/null

SET="$TMP/AppIcon.iconset"
mkdir -p "$SET"
for s in 16 32 128 256 512; do
  sips -z "$s" "$s" "$TMP/icon-1024.png" --out "$SET/icon_${s}x${s}.png" >/dev/null
  d=$((s * 2))
  sips -z "$d" "$d" "$TMP/icon-1024.png" --out "$SET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$SET" -o AppIcon.icns
cp "$TMP/icon-1024.png" tools/icon-preview.png
echo "生成：AppIcon.icns（预览 tools/icon-preview.png）"
