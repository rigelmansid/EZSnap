#!/bin/bash
# 编译并打包成 build/Snapshot.app。
# SIGN_IDENTITY 默认 "-"（ad-hoc 签名）；设成钥匙串里的证书名可以让屏幕录制授权在重新编译后保持有效。
set -euo pipefail
cd "$(dirname "$0")/.."

swift build -c release

APP=build/Snapshot.app
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp .build/release/Snapshot "$APP/Contents/MacOS/Snapshot"
cp Resources/Info.plist "$APP/Contents/Info.plist"
codesign --force --sign "${SIGN_IDENTITY:--}" "$APP"

echo "已生成 $APP"
