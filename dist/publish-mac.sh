#!/usr/bin/env bash
# macOS 向けに自己完結 single-file ビルドを発行する(Windows 版 dist/publish.ps1 の macOS 対応版)。
# バージョンは Windows と同一の単一管理点 src/Directory.Build.props(<VersionPrefix>)から自動取得され、
# アセンブリに刻まれる(→ Windows 版と版数が揃う)。
#
# 使い方:
#   bash dist/publish-mac.sh                 # 既定 RID = osx-arm64(Apple Silicon)
#   bash dist/publish-mac.sh osx-x64         # Intel Mac
#   CONFIGURATION=Release bash dist/publish-mac.sh
#
# 出力: dist/out/app-<rid>/DataverseGW (自己完結・.NET ランタイム同梱→別途インストール不要)
set -euo pipefail

RID="${1:-osx-arm64}"
CONFIGURATION="${CONFIGURATION:-Release}"

# スクリプトの場所からリポジトリルートを解決
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJ="$ROOT/src/Gateway.Host/Gateway.Host.csproj"
PROPS="$ROOT/src/Directory.Build.props"
OUT="$SCRIPT_DIR/out/app-$RID"

# バージョンを単一管理点から取得(表示・確認用。アセンブリへの反映は publish が自動で行う)
# 開始〜終了タグを揃って要求(コメント中の <VersionPrefix> 言及に誤マッチしないように)
VERSION="$(grep -oE '<VersionPrefix>[^<]+</VersionPrefix>' "$PROPS" | head -1 | sed -E 's:</?VersionPrefix>::g' | tr -d '[:space:]')"
if [ -z "$VERSION" ]; then echo "VersionPrefix not found in $PROPS" >&2; exit 1; fi
echo "Version: $VERSION (RID=$RID, $CONFIGURATION) — 単一管理点 src/Directory.Build.props より"

# SDK 10 の端末でも net8.0 ターゲットをビルドできるようにする
export DOTNET_ROLL_FORWARD=LatestMajor
# tar/zip 時に AppleDouble(._*)を作らない(macOS 手順書 §14)
export COPYFILE_DISABLE=1

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
echo "配布(tar.gz)にする場合は COPYFILE_DISABLE=1 tar -czf DataverseGW-$VERSION-$RID.tar.gz -C \"$OUT\" ."
