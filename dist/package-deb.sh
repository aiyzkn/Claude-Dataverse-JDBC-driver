#!/usr/bin/env bash
# Linux 版の .deb パッケージを作る(dist/publish-linux.sh の発行結果を包む)。
#
# 使い方:
#   bash dist/publish-linux.sh linux-x64     # 先に発行
#   bash dist/package-deb.sh                 # 既定 RID = linux-x64
#   bash dist/package-deb.sh linux-arm64
#
# 出力: dist/out/DataverseGW-<ver>-<rid>.deb
# 配置: /opt/dataverse-gw/DataverseGW と、コマンド /usr/bin/dataverse-gw(シンボリックリンク)。
#       サービス登録(systemd)は行わない。データはユーザーごとの LocalApplicationData
#       (~/.local/share/DataverseGW)に置かれる(Windows / macOS と同じ)。
# 必要なもの: dpkg-deb(Debian / Ubuntu 系の標準)。
set -euo pipefail

RID="${1:-linux-x64}"
case "$RID" in
  linux-x64)   ARCH=amd64 ;;
  linux-arm64) ARCH=arm64 ;;
  *) echo "unsupported RID for .deb: $RID (linux-x64 / linux-arm64)" >&2; exit 1 ;;
esac

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PROPS="$ROOT/src/Directory.Build.props"
APP="$SCRIPT_DIR/out/app-$RID"

VERSION="$(grep -oE '<VersionPrefix>[^<]+</VersionPrefix>' "$PROPS" | head -1 | sed -E 's:</?VersionPrefix>::g' | tr -d '[:space:]')"
if [ -z "$VERSION" ]; then echo "VersionPrefix not found in $PROPS" >&2; exit 1; fi
if [ ! -f "$APP/DataverseGW" ]; then
  echo "Published app not found: $APP/DataverseGW (run: bash dist/publish-linux.sh $RID)" >&2; exit 1
fi

STAGE="$SCRIPT_DIR/out/deb-$RID"
DEB="$SCRIPT_DIR/out/DataverseGW-$VERSION-$RID.deb"
rm -rf "$STAGE" "$DEB"
mkdir -p "$STAGE/DEBIAN" "$STAGE/opt/dataverse-gw" "$STAGE/usr/bin" "$STAGE/usr/share/doc/dataverse-gw"

cp -a "$APP/." "$STAGE/opt/dataverse-gw/"
chmod 0755 "$STAGE/opt/dataverse-gw/DataverseGW"
ln -s /opt/dataverse-gw/DataverseGW "$STAGE/usr/bin/dataverse-gw"
cp "$ROOT/LICENSE" "$STAGE/usr/share/doc/dataverse-gw/copyright"
[ -f "$SCRIPT_DIR/DISTRIB-README.md" ] && cp "$SCRIPT_DIR/DISTRIB-README.md" "$STAGE/usr/share/doc/dataverse-gw/README.md"

INSTALLED_KB="$(du -sk "$STAGE" | cut -f1)"

# 自己完結ビルドでも .NET が実行時に読む OS ライブラリ(glibc / libstdc++ / zlib / OpenSSL / ICU)を依存に置く。
# ICU は版数ごとにパッケージ名が違うため、主な版を「|」で並べる。
cat > "$STAGE/DEBIAN/control" <<EOF
Package: dataverse-gw
Version: $VERSION
Section: database
Priority: optional
Architecture: $ARCH
Maintainer: Dataverse GW
Installed-Size: $INSTALLED_KB
Depends: libc6 (>= 2.27), libgcc-s1 | libgcc1, libstdc++6, zlib1g, libssl3 | libssl3t64 | libssl1.1, libicu76 | libicu74 | libicu72 | libicu71 | libicu70 | libicu67 | libicu66 | libicu63
Description: Microsoft Dataverse 用 SQL ドライバ (Arrow Flight SQL ゲートウェイ)
 DBeaver・DataGrip・Python(ADBC) などの汎用 SQL クライアントから
 Microsoft Dataverse へ CRUD を行うためのゲートウェイ。
 自己完結ビルド(.NET ランタイム同梱)。コマンドは dataverse-gw。
EOF

# 所有者を root に揃えて固める(fakeroot が無くても --root-owner-group で可)
dpkg-deb --root-owner-group --build "$STAGE" "$DEB"
dpkg-deb --info "$DEB"
echo "OK -> $DEB"
