# リリース手順（macOS 版バイナリの公開）

macOS で `dist/publish-mac.sh` を使ってビルドし、Windows 版と同じ GitHub リリースページに公開するまでの操作手順。
Windows 版の手順・ポリシーは `dist/README.md`、機能内容は `CHANGELOG.md` を参照。

> **バージョン方針**: バージョンは `src/Directory.Build.props` の `<VersionPrefix>` で単一管理。SemVer 準拠で、
> **明示的な bump up 指示がない限り版数は変更しない**。本手順は現行版（例 `1.0.0`）をそのまま公開する前提。
> 以降のコマンド例の `1.0.0` は実際の版数に読み替えること。ただし `$VER` を使う箇所は 1. で自動設定されるため読み替え不要。

> **RID 方針**: `osx-arm64`（Apple Silicon）と `osx-x64`（Intel）の**両方を毎回リリースする**。
> 本手順のコマンドは両 RID を含めてコピペで完結するよう記載している（読み替え不要）。

> **実行環境**: **2.（ビルド）は Mac 実機が必要**。それ以外の作業（1・3〜6）は Mac / Windows いずれでも実行可能なため、
> 各ステップに **macOS/Linux（bash）** と **Windows（PowerShell）** の両方を併記する。
> Windows で 3. 以降を行う場合は、Mac でビルドした `dist/out/app-<rid>/` を当該 Windows 端末に持ち込んでおくこと。

---

## 0. 前提

- ビルド（2.）は macOS 13 以降（Apple Silicon = `osx-arm64` / Intel = `osx-x64`）。
- .NET SDK 8 以降（SDK 10 でも可。スクリプトが `DOTNET_ROLL_FORWARD=LatestMajor` を設定）。
- リポジトリを clone 済みで、**リリース対象コミットが origin に push 済み**であること（push は担当者が実施）。
- GitHub CLI `gh` がインストール済み・認証済み（`gh auth status`。未認証なら `gh auth login`）。`repo` スコープが必要。
- Windows で 3〜6 を行う場合、`tar` は Windows 10/11 同梱の `tar.exe`（bsdtar）を使用（AppleDouble 混入の問題は無いため `COPYFILE_DISABLE` 不要）。

## 1. リポジトリ同期・バージョン確認

以降の手順で `$VER` を使うため、同一シェルセッションで続けて実行する。

**macOS/Linux（bash）**
```bash
cd <リポジトリ>
git pull                                    # リリース対象コミットまで同期
VER=$(grep -oE '<VersionPrefix>[^<]+</VersionPrefix>' src/Directory.Build.props | head -1 | sed -E 's:</?VersionPrefix>::g' | tr -d '[:space:]'); echo "$VER"
```

**Windows（PowerShell）**
```powershell
cd <リポジトリ>
git pull                                    # リリース対象コミットまで同期
$VER = ([xml](Get-Content src\Directory.Build.props)).SelectSingleNode('//VersionPrefix').InnerText.Trim(); $VER
```

## 2. ビルド（自己完結 single-file）※ Mac 実機のみ

```bash
bash dist/publish-mac.sh osx-arm64          # Apple Silicon
bash dist/publish-mac.sh osx-x64            # Intel Mac
```

- 出力: `dist/out/app-<rid>/DataverseGW`（.NET ランタイム同梱・別途インストール不要）。
- 版数は `src/Directory.Build.props` からアセンブリに刻まれる（Windows 版と一致）。

## 3. パッケージ化（tar.gz）

両 RID をまとめて tar.gz 化する。

**macOS/Linux（bash）** — `COPYFILE_DISABLE=1` で AppleDouble（`._*`）を混入させない。
```bash
for RID in osx-arm64 osx-x64; do
  COPYFILE_DISABLE=1 tar -czf "dist/out/DataverseGW-$VER-$RID.tar.gz" -C "dist/out/app-$RID" .
done
```

**Windows（PowerShell）** — 同梱 `tar.exe`。AppleDouble は発生しない。
```powershell
foreach ($RID in 'osx-arm64','osx-x64') {
  tar -czf "dist/out/DataverseGW-$VER-$RID.tar.gz" -C "dist/out/app-$RID" .
}
```

- 生成物: `dist/out/DataverseGW-<ver>-osx-arm64.tar.gz` および `-osx-x64.tar.gz`。

## 4. チェックサム（SHA-256）生成

ファイル名だけ記録するため対象ディレクトリで実行。両 RID 分を生成する。

**macOS/Linux（bash）** — `shasum -a 256`。
```bash
cd dist/out
for RID in osx-arm64 osx-x64; do
  shasum -a 256 "DataverseGW-$VER-$RID.tar.gz" > "DataverseGW-$VER-$RID.tar.gz.sha256"
  cat "DataverseGW-$VER-$RID.tar.gz.sha256"
done
cd ../..
```

**Windows（PowerShell）** — `Get-FileHash`（`shasum -c` 互換形式で出力）。
```powershell
Push-Location dist\out
foreach ($RID in 'osx-arm64','osx-x64') {
  $a = "DataverseGW-$VER-$RID.tar.gz"
  "$((Get-FileHash $a -Algorithm SHA256).Hash.ToLower())  $a" | Out-File "$a.sha256" -Encoding ascii -NoNewline
  Get-Content "$a.sha256"
}
Pop-Location
```

- 生成物: `...tar.gz.sha256`（`<hash>  <ファイル名>` 形式。`shasum -c` で検証可能）。

## 5. GitHub リリースへ公開

> **重要**: 同一バージョンのリリースが既に存在する場合（Windows 版で `v1.0.0` を作成済み等）は、
> **新規作成せず既存リリースへ資産を追加**する（`gh release upload`）。バージョンごとに 1 リリースにまとめる。

リリースが存在するかは `gh release view v$VER` で確認できる（無ければ 5b、あれば 5a）。両 RID の tar.gz と .sha256 をまとめて添付する。

### 5a. 既存の v1.0.0 リリースに macOS 資産を追加（通常こちら）

**macOS/Linux（bash）**
```bash
gh release upload v$VER \
  "dist/out/DataverseGW-$VER-osx-arm64.tar.gz" \
  "dist/out/DataverseGW-$VER-osx-arm64.tar.gz.sha256" \
  "dist/out/DataverseGW-$VER-osx-x64.tar.gz" \
  "dist/out/DataverseGW-$VER-osx-x64.tar.gz.sha256"
# 上書き（再アップロード）する場合は --clobber を付与
```

**Windows（PowerShell）**
```powershell
gh release upload "v$VER" `
  "dist\out\DataverseGW-$VER-osx-arm64.tar.gz" `
  "dist\out\DataverseGW-$VER-osx-arm64.tar.gz.sha256" `
  "dist\out\DataverseGW-$VER-osx-x64.tar.gz" `
  "dist\out\DataverseGW-$VER-osx-x64.tar.gz.sha256"
# 上書き（再アップロード）する場合は --clobber を付与
```

### 5b. リリースがまだ無い場合（新規作成）

**macOS/Linux（bash）**
```bash
gh release create v$VER \
  "dist/out/DataverseGW-$VER-osx-arm64.tar.gz" \
  "dist/out/DataverseGW-$VER-osx-arm64.tar.gz.sha256" \
  "dist/out/DataverseGW-$VER-osx-x64.tar.gz" \
  "dist/out/DataverseGW-$VER-osx-x64.tar.gz.sha256" \
  --title "v$VER" --notes-file CHANGELOG.md --target main
```

**Windows（PowerShell）**
```powershell
gh release create "v$VER" `
  "dist\out\DataverseGW-$VER-osx-arm64.tar.gz" `
  "dist\out\DataverseGW-$VER-osx-arm64.tar.gz.sha256" `
  "dist\out\DataverseGW-$VER-osx-x64.tar.gz" `
  "dist\out\DataverseGW-$VER-osx-x64.tar.gz.sha256" `
  --title "v$VER" --notes-file CHANGELOG.md --target main
```

## 6. 公開後の検証

**macOS/Linux（bash）**
```bash
gh release view v$VER                        # 添付資産一覧を確認(両 RID の tar.gz と .sha256 が揃っているか)
```

**Windows（PowerShell）**
```powershell
gh release view "v$VER"                       # 添付資産一覧を確認(両 RID の tar.gz と .sha256 が揃っているか)
```

受領側の整合性検証:
```bash
# macOS/Linux
shasum -a 256 -c DataverseGW-1.0.0-osx-arm64.tar.gz.sha256
shasum -a 256 -c DataverseGW-1.0.0-osx-x64.tar.gz.sha256
```
```powershell
# Windows(PowerShell)
foreach ($a in 'DataverseGW-1.0.0-osx-arm64.tar.gz','DataverseGW-1.0.0-osx-x64.tar.gz') {
  (Get-FileHash $a -Algorithm SHA256).Hash.ToLower() -eq ((Get-Content "$a.sha256") -split '\s+')[0]  # True なら一致
}
```

利用側の展開・実行（macOS 実機で実施）:
```bash
tar -xzf DataverseGW-1.0.0-osx-arm64.tar.gz   # Intel は -osx-x64
chmod +x DataverseGW                 # 実行権限（tar 展開で失われる場合に付与）
./DataverseGW
```
> Gatekeeper で警告が出る場合は、組織の配布ポリシーに従い許可（公証なし tar.gz のため）。詳細は `doc/macos対応手順書-3.2.1.md`。

---

## 注意事項

- **`git push` は担当者が実施**（本手順の対象外）。リリースは push 済みコミットに対して作成/追加すること。
- **版数は変更しない**（`src/Directory.Build.props`）。新バージョンを出すときのみ、明示指示のうえ `VersionPrefix` を bump し、`CHANGELOG.md` に新セクションを追記してから本手順を実行する。
- 配布物（`dist/out/`）は git 管理外。
- **2.（ビルド）のみ Mac 実機が必須**。3〜6 は Windows（PowerShell）でも実行可能（Mac でビルドした `dist/out/app-<rid>/` を持ち込む）。
- 認証は共有配置では ClientSecret / Certificate のみ（対話型不可）。接続手順は `doc/setup-guide-クライアント接続.md`。

*作成日: 2026-07-21 / 更新日: 2026-07-23 / 関連: `dist/publish-mac.sh`, `dist/README.md`, `CHANGELOG.md`, `doc/macos対応手順書-3.2.1.md`, `doc/リリース手順-Windows.md`*
