#!/bin/bash
# 打 Mac 安装包：dist/EZSnap-<版本>.dmg，里面是 EZSnap.app（Apple Silicon + Intel）和“应用程序”文件夹的快捷方式，
# 打开后把 EZSnap 拖进去即可。版本号取自 Resources/Info.plist。
# 发布版必须用“EZSnap Code Signing”签名，每一版都是同一张证书，用户升级后不用重新授权屏幕录制（D-24、坑 11）。
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' Resources/Info.plist)
IDENTITY="EZSnap Code Signing"
if ! security find-certificate -c "$IDENTITY" >/dev/null 2>&1; then
    echo "钥匙串里没有“${IDENTITY}”。发布版必须用它签名：从备份的 .p12 导入；确实丢了才运行 scripts/make-signing-cert.sh 生成新的。" >&2
    exit 1
fi
UNIVERSAL=1 SIGN_IDENTITY="$IDENTITY" ./scripts/build.sh
if ! codesign -d -r- build/EZSnap.app 2>&1 | grep -q 'certificate leaf'; then
    echo "build/EZSnap.app 的签名要求里没有证书，没有用“${IDENTITY}”签上。" >&2
    exit 1
fi

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
