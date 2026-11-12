#!/bin/bash
# 构建 DesktopDance.app（只需要 Xcode 命令行工具：xcode-select --install）
# 默认只编当前这台 Mac 的架构；ARCHS="arm64 x86_64" ./build.sh 编通用版（release.sh 会这么做）。
set -euo pipefail
cd "$(dirname "$0")"

APP="build/DesktopDance.app"
ARCHS="${ARCHS:-$(uname -m)}"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources" build/obj

SLICES=()
for arch in $ARCHS; do
  swiftc -O -swift-version 5 \
    -target "${arch}-apple-macos14.2" \
    Sources/*.swift \
    -framework AppKit -framework AVFoundation -framework ScreenCaptureKit \
    -framework CoreAudio -framework Carbon -framework QuartzCore -framework QuickLookThumbnailing \
    -o "build/obj/DesktopDance-${arch}"
  SLICES+=("build/obj/DesktopDance-${arch}")
done
lipo -create "${SLICES[@]}" -output "$APP/Contents/MacOS/DesktopDance"

cp Info.plist "$APP/Contents/Info.plist"
# 系统弹窗和访达里显示的文字，中英文各一份（跟随系统语言）
cp -R Localization/zh-Hans.lproj Localization/en.lproj "$APP/Contents/Resources/"
cp lyrics/placeholder.lrc "$APP/Contents/Resources/" 2>/dev/null || true
# 应用图标（设计在 tools/make-icon.swift，改完运行 tools/make-icon.sh 重新生成）
cp AppIcon.icns "$APP/Contents/Resources/" 2>/dev/null || true
# 打篮球的动作数据（用 --extract-pose 从参考视频里提取）
cp motion/basketball-motion.json "$APP/Contents/Resources/" 2>/dev/null || true

# 本机临时签名（ad-hoc）。没有开发者证书，所以别的 Mac 上首次打开要到「隐私与安全性」里点「仍要打开」。
# 签名要求固定为 bundle id，尽量让重新构建后系统还认之前给过的授权。
codesign --force --sign - -r='designated => identifier "local.desktopdance.app"' "$APP" >/dev/null 2>&1 \
  || codesign --force --sign - "$APP" >/dev/null 2>&1 || echo "警告：codesign 失败，app 仍可在本机运行"

# 变量名后面紧跟中文标点时要加花括号：系统语言环境不是 UTF-8 时，旧版 bash 会把后面的字节当成变量名的一部分
echo "构建完成：${APP}（${ARCHS}）"
