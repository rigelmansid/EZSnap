#!/bin/bash
# 编译并打包成 build/EZSnap.app。
# 签名：钥匙串里有“EZSnap Code Signing”（scripts/make-signing-cert.sh 生成）就用它，屏幕录制授权换版本后仍然有效；
# 没有就用 ad-hoc 签名（每次编译都要重新授权，坑 11）。SIGN_IDENTITY 可以指定别的证书，设成 "-" 强制 ad-hoc（D-24）。
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
if [ -z "${SIGN_IDENTITY:-}" ]; then
    if security find-certificate -c "EZSnap Code Signing" >/dev/null 2>&1; then
        SIGN_IDENTITY="EZSnap Code Signing"
    else
        SIGN_IDENTITY="-"
        echo "钥匙串里没有“EZSnap Code Signing”，用 ad-hoc 签名；换版本后要重新授权屏幕录制（见 scripts/make-signing-cert.sh）。"
    fi
fi
codesign --force --sign "$SIGN_IDENTITY" "$APP"

echo "已生成 $APP"
