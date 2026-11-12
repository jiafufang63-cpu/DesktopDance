#!/bin/bash
# 打一个可以直接发给别人的压缩包：dist/DesktopDance-mac.zip（Apple 芯片和 Intel 通用）。
# 传到 GitHub 上对应的 Release 后，README 里的下载按钮指向它：
#   https://github.com/<账号>/<仓库>/releases/download/<标签>/DesktopDance-mac.zip
set -euo pipefail
cd "$(dirname "$0")"

ARCHS="arm64 x86_64" ./build.sh
mkdir -p dist
rm -f dist/DesktopDance-mac.zip
# ditto 会保留签名和扩展属性；用普通 zip 压出来的包解压后签名可能失效
ditto -c -k --keepParent build/DesktopDance.app dist/DesktopDance-mac.zip
shasum -a 256 dist/DesktopDance-mac.zip
echo "打包完成：dist/DesktopDance-mac.zip"
