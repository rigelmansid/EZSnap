#!/bin/bash
# 打包并装到 ~/Applications/EZSnap.app：只装给当前用户，不需要管理员密码，启动台和 Spotlight 都能找到（D-9）。
set -euo pipefail
cd "$(dirname "$0")/.."

if pgrep -xq EZSnap; then
    echo "EZSnap 正在运行，请先从菜单栏剪刀图标 → 退出，再重新安装。" >&2
    exit 1
fi

./scripts/build.sh

DEST="$HOME/Applications"
mkdir -p "$DEST"
rm -rf "$DEST/EZSnap.app"
ditto build/EZSnap.app "$DEST/EZSnap.app"

echo "已安装到 $DEST/EZSnap.app，双击它或在启动台里找 EZSnap 打开。"
