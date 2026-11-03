#!/bin/bash
# 在登录钥匙串里生成 EZSnap 的自签名代码签名证书（D-24）。只需要做一次；已经有了就什么都不做。
# 用固定证书签名后，系统按“bundle id + 这张证书”记屏幕录制授权，换版本不用重新授权（坑 11）。
# 私钥只在钥匙串里。换电脑或重装系统前，请在“钥匙串访问”里把“EZSnap Code Signing”导出成 .p12 备份；
# 丢了就只能换新证书，所有用户要重新授权一次。
set -euo pipefail

NAME="EZSnap Code Signing"
KEYCHAIN="$HOME/Library/Keychains/login.keychain-db"

if security find-certificate -c "$NAME" "$KEYCHAIN" >/dev/null 2>&1; then
    echo "钥匙串里已经有“${NAME}”，不用再生成。"
    exit 0
fi

WORK=$(mktemp -d /tmp/ezsnap-cert.XXXXXX)
trap 'rm -rf "$WORK"' EXIT
cat > "$WORK/cert.cnf" <<CNF
[req]
distinguished_name = dn
x509_extensions = ext
prompt = no
[dn]
CN = $NAME
[ext]
basicConstraints = critical, CA:false
keyUsage = critical, digitalSignature
extendedKeyUsage = critical, codeSigning
CNF

# 用系统自带的 LibreSSL：它导出的 .p12 用老算法，security import 认得（OpenSSL 3 默认的要加 -legacy）。
/usr/bin/openssl req -x509 -newkey rsa:2048 -nodes -days 7300 -config "$WORK/cert.cnf" \
    -keyout "$WORK/key.pem" -out "$WORK/cert.pem" 2>/dev/null
# .p12 的密码只用来把私钥交给 security import，用完随临时目录一起删掉。
PASS=$(/usr/bin/openssl rand -hex 16)
/usr/bin/openssl pkcs12 -export -name "$NAME" -inkey "$WORK/key.pem" -in "$WORK/cert.pem" \
    -out "$WORK/id.p12" -passout "pass:$PASS"
security import "$WORK/id.p12" -k "$KEYCHAIN" -P "$PASS" -T /usr/bin/codesign >/dev/null

echo "已在登录钥匙串里生成“${NAME}”（20 年有效）。第一次签名时如果弹出钥匙串提示，输入登录密码并点“始终允许”。"
echo "请在“钥匙串访问”里把它导出成 .p12 备份到安全的地方。"
