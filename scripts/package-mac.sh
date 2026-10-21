#!/bin/bash
# 打 Mac 安装包：dist/EZSnap-<版本>.dmg，里面是 EZSnap.app（Apple Silicon + Intel）和“应用程序”文件夹的快捷方式，
# 打开后把 EZSnap 拖进去即可。版本号取自 Resources/Info.plist。
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' Resources/Info.plist)
UNIVERSAL=1 ./scripts/build.sh

STAGE=$(mktemp -d /tmp/ezsnap-dmg.XXXXXX)
trap 'rm -rf "$STAGE"' EXIT
ditto build/EZSnap.app "$STAGE/EZSnap.app"
ln -s /Applications "$STAGE/Applications"

mkdir -p dist
DMG="dist/EZSnap-$VERSION.dmg"
rm -f "$DMG"
hdiutil create -quiet -volname "EZSnap $VERSION" -srcfolder "$STAGE" -format UDZO -fs HFS+ "$DMG"
shasum -a 256 "$DMG" | sed 's#dist/##' > "$DMG.sha256"

echo "已生成 $DMG"
