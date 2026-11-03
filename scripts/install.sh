#!/bin/bash
# 打包并装到 /Applications/EZSnap.app，和从 DMG 拖进“应用程序”的位置一样（D-23）。
# 管理员账户不用输密码；没有写权限时提示换管理员账户或用 DMG 安装，不要用 sudo 跑（编译产物会变成 root 所有）。
# 以前装在 ~/Applications 的旧副本一并删掉，免得两份同名的 app 并存。DEST 可以改安装目录，验证脚本时用。
set -euo pipefail
cd "$(dirname "$0")/.."

if pgrep -xq EZSnap; then
    echo "EZSnap 正在运行，请先从菜单栏剪刀图标 → 退出，再重新安装。" >&2
    exit 1
fi

DEST="${DEST:-/Applications}"
if [ ! -w "$DEST" ]; then
    echo "没有写入 $DEST 的权限。请用管理员账户运行，或者打开 DMG 把 EZSnap 拖进“应用程序”。" >&2
    exit 1
fi

./scripts/build.sh

rm -rf "$DEST/EZSnap.app"
ditto build/EZSnap.app "$DEST/EZSnap.app"

OLD="$HOME/Applications/EZSnap.app"
if [ "$DEST" = /Applications ] && [ -d "$OLD" ]; then
    rm -rf "$OLD"
    echo "已删除以前装在 ~/Applications 的旧版本。"
fi

echo "已安装到 $DEST/EZSnap.app，双击它或在启动台里找 EZSnap 打开。"
