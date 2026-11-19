#!/usr/bin/env bash
# Linux 向けに自己完結 single-file ビルドを発行する(dist/publish-mac.sh の Linux 版)。
# バージョンは Windows / macOS と同一の単一管理点 src/Directory.Build.props(<VersionPrefix>)から取得され、
# アセンブリに刻まれる(→ 各 OS で版数が揃う)。
#
# 使い方:
#   bash dist/publish-linux.sh               # 既定 RID = linux-x64
#   bash dist/publish-linux.sh linux-arm64
#   CONFIGURATION=Release bash dist/publish-linux.sh
#
# 出力: dist/out/app-<rid>/DataverseGW (自己完結・.NET ランタイム同梱→別途インストール不要)
# 実行には glibc・ICU・OpenSSL などの OS ライブラリが要る(.deb の Depends を参照: dist/package-deb.sh)。
# コンテナ配布は dist/Dockerfile(フレームワーク依存)を使う。
set -euo pipefail

RID="${1:-linux-x64}"
CONFIGURATION="${CONFIGURATION:-Release}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJ="$ROOT/src/Gateway.Host/Gateway.Host.csproj"
PROPS="$ROOT/src/Directory.Build.props"
OUT="$SCRIPT_DIR/out/app-$RID"

# 開始〜終了タグを揃って要求(コメント中の <VersionPrefix> 言及に誤マッチしないように)
VERSION="$(grep -oE '<VersionPrefix>[^<]+</VersionPrefix>' "$PROPS" | head -1 | sed -E 's:</?VersionPrefix>::g' | tr -d '[:space:]')"
if [ -z "$VERSION" ]; then echo "VersionPrefix not found in $PROPS" >&2; exit 1; fi
echo "Version: $VERSION (RID=$RID, $CONFIGURATION) — 単一管理点 src/Directory.Build.props より"

# SDK 10 の端末でも net8.0 ターゲットをビルドできるようにする
export DOTNET_ROLL_FORWARD=LatestMajor

rm -rf "$OUT"
echo "Publishing $PROJ ..."
dotnet publish "$PROJ" \
  -c "$CONFIGURATION" -r "$RID" --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=none \
  -o "$OUT"

BIN="$OUT/DataverseGW"
if [ ! -f "$BIN" ]; then echo "expected binary not found: $BIN" >&2; exit 1; fi
chmod +x "$BIN"
echo "OK -> $BIN"
ls -lh "$BIN" | awk '{print $5, $9}'
echo "tar.gz にする場合は tar -czf DataverseGW-$VERSION-$RID.tar.gz -C \"$OUT\" ."
echo ".deb にする場合は bash dist/package-deb.sh $RID"
