#!/bin/bash
# 编译并打包成 build/EZSnap.app。
# SIGN_IDENTITY 默认 "-"（ad-hoc 签名）；设成钥匙串里的证书名可以让屏幕录制授权在重新编译后保持有效。
# UNIVERSAL=1 同时编 Apple Silicon 和 Intel 两种架构（发布用，见 scripts/package-mac.sh）。
set -euo pipefail
cd "$(dirname "$0")/.."

ARCH_FLAGS=()
[ "${UNIVERSAL:-}" = 1 ] && ARCH_FLAGS=(--arch arm64 --arch x86_64)
swift build -c release ${ARCH_FLAGS[@]+"${ARCH_FLAGS[@]}"}
BIN=$(swift build -c release ${ARCH_FLAGS[@]+"${ARCH_FLAGS[@]}"} --show-bin-path)

APP=build/EZSnap.app
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN/EZSnap" "$APP/Contents/MacOS/EZSnap"
cp Resources/Info.plist "$APP/Contents/Info.plist"
cp Resources/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"
codesign --force --sign "${SIGN_IDENTITY:--}" "$APP"

echo "已生成 $APP"
