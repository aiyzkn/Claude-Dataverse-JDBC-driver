# リリース手順（Windows 版バイナリの公開）

Windows で `dist\package-zip.ps1`（必要に応じて `dist\build-msi.ps1`）を使ってビルド・パッケージ化し、
GitHub リリースページに公開するまでの操作手順。
配布ポリシー・スクリプトの詳細は `dist/README.md`、機能内容は `CHANGELOG.md`、macOS 版は `doc/リリース手順-macOS.md` を参照。

> **バージョン方針**: バージョンは `src/Directory.Build.props` の `<VersionPrefix>` で単一管理。SemVer 準拠で、
> **明示的な bump up 指示がない限り版数は変更しない**。本手順は現行版（例 `1.0.0`）をそのまま公開する前提。
> 以降のコマンド例の `1.0.0` は実際の版数に読み替えること（下記 1. で確認）。

---

## 0. 前提

- Windows 10/11（`win-x64`）。PowerShell 5.1 または PowerShell 7 以降。
- .NET SDK 8 以降（SDK 10 でも可。スクリプトが `DOTNET_ROLL_FORWARD=LatestMajor` を設定）。
- リポジトリを clone 済みで、**リリース対象コミットが origin に push 済み**であること（push は担当者が実施）。
- GitHub CLI `gh` がインストール済み・認証済み（`gh auth status`。未認証なら `gh auth login`）。`repo` スコープが必要。
- MSI も配布する場合のみ WiX v4（`build-msi.ps1` が未導入ならローカルツールとして取得）。署名する場合のみ `dist/README.md` §3 を参照。

## 1. リポジトリ同期・バージョン確認

```powershell
cd <リポジトリ>
git pull                                   # リリース対象コミットまで同期
# 版数確認(例 1.0.0)
$VER = ([xml](Get-Content src\Directory.Build.props)).SelectSingleNode('//VersionPrefix').InnerText.Trim(); $VER
```

- 以降の手順で `$VER` を使うため、同一 PowerShell セッションで続けて実行する。

## 2. ビルド＋パッケージ化（ZIP）

ビルド（自己完結 single-file exe）とパッケージ化を `package-zip.ps1` で一括実行する。
このスクリプトは内部で `publish.ps1` を呼ぶため、`publish.ps1` を個別に実行する必要はない。

```powershell
powershell -ExecutionPolicy Bypass -File dist\package-zip.ps1
# -Version 省略時は src\Directory.Build.props の VersionPrefix を自動使用
```

- 出力: `dist\out\DataverseGW-<ver>-win-x64.zip`（自己完結 exe ＋ install/uninstall スクリプト同梱・.NET ランタイム同梱で別途インストール不要）。
- 版数は `src\Directory.Build.props` からアセンブリに刻まれる（macOS 版と一致）。

### 2b.（任意）MSI も配布する場合

```powershell
powershell -ExecutionPolicy Bypass -File dist\build-msi.ps1
# -Version 省略時は props の VersionPrefix を自動使用。未導入なら WiX をローカルツールとして取得
# -> dist\out\DataverseGW-<ver>-win-x64.msi (perUser, 管理者不要)
```

- コード署名（自己署名 ＋ GPO 信頼配布）が必要な場合は `dist/README.md` §3（`New-InternalSigningCert.ps1` / `sign.ps1`）に従う。

## 3. チェックサム（SHA-256）生成

Windows は `Get-FileHash` を使う。ファイル名だけ記録するため対象ディレクトリで実行する。
ZIP と MSI の両方を配布する場合は両方の `.sha256` を生成する（**MSI を作っていない場合は配列から `.msi` の行を外す**）。

```powershell
Push-Location dist\out
foreach ($asset in "DataverseGW-$VER-win-x64.zip",
                   "DataverseGW-$VER-win-x64.msi") {
  "$((Get-FileHash $asset -Algorithm SHA256).Hash.ToLower())  $asset" |
    Out-File "$asset.sha256" -Encoding ascii -NoNewline
  Get-Content "$asset.sha256"
}
Pop-Location
```

- 生成物: `...zip.sha256` / `...msi.sha256`（`<hash>  <ファイル名>` 形式。macOS/Linux の `shasum -c` と互換）。

## 4. GitHub リリースへ公開

> **重要**: 同一バージョンのリリースが既に存在する場合（macOS 版で `v1.0.0` を作成済み等）は、
> **新規作成せず既存リリースへ資産を追加**する（`gh release upload`）。バージョンごとに 1 リリースにまとめる。

リリースが存在するかは `gh release view v$VER` で確認できる（無ければ 4b、あれば 4a）。
ZIP と MSI の両方を配布する場合は tar.gz と同様、両方の資産と `.sha256` をまとめて添付する（**MSI を作っていない場合は `.msi` / `.msi.sha256` の 2 行を外す**）。

### 4a. 既存の v1.0.0 リリースに Windows 資産を追加（通常こちら）
```powershell
gh release upload "v$VER" `
  "dist\out\DataverseGW-$VER-win-x64.zip" `
  "dist\out\DataverseGW-$VER-win-x64.zip.sha256" `
  "dist\out\DataverseGW-$VER-win-x64.msi" `
  "dist\out\DataverseGW-$VER-win-x64.msi.sha256"
# 上書き（再アップロード）する場合は --clobber を付与
```

### 4b. リリースがまだ無い場合（新規作成）
```powershell
gh release create "v$VER" `
  "dist\out\DataverseGW-$VER-win-x64.zip" `
  "dist\out\DataverseGW-$VER-win-x64.zip.sha256" `
  "dist\out\DataverseGW-$VER-win-x64.msi" `
  "dist\out\DataverseGW-$VER-win-x64.msi.sha256" `
  --title "v$VER" --notes-file CHANGELOG.md --target main
```

## 5. 公開後の検証

```powershell
gh release view "v$VER"                       # 添付資産一覧を確認(zip/msi と各 .sha256 が揃っているか)
```

受領側の整合性検証（MSI を配布していない場合は配列から `.msi` を外す）:
```powershell
# Windows(PowerShell)
foreach ($a in 'DataverseGW-1.0.0-win-x64.zip','DataverseGW-1.0.0-win-x64.msi') {
  (Get-FileHash $a -Algorithm SHA256).Hash.ToLower() -eq ((Get-Content "$a.sha256") -split '\s+')[0]  # True なら一致
}
```

利用側の展開・実行:
```powershell
Expand-Archive DataverseGW-1.0.0-win-x64.zip -DestinationPath DataverseGW
# ZIP 版: 同梱の install\Install.ps1 で配置、または app\DataverseGW.exe を直接実行
# MSI 版: DataverseGW-1.0.0-win-x64.msi をダブルクリック（perUser・管理者不要）
```
> 署名なし配布のため SmartScreen 警告が出る場合は、組織の配布ポリシー（GPO で自己署名証明書を信頼配布）に従う。詳細は `dist/README.md` §3。

---

## 注意事項

- **`git push` は担当者が実施**（本手順の対象外）。リリースは push 済みコミットに対して作成/追加すること。
- **版数は変更しない**（`src\Directory.Build.props`）。新バージョンを出すときのみ、明示指示のうえ `VersionPrefix` を bump し、`CHANGELOG.md` に新セクションを追記してから本手順を実行する。
- 配布物（`dist\out\`）は git 管理外。
- 認証は共有配置では ClientSecret / Certificate のみ（対話型不可）。接続手順は `doc/setup-guide-クライアント接続.md`。

*作成日: 2026-07-23 / 関連: `dist/package-zip.ps1`, `dist/publish.ps1`, `dist/build-msi.ps1`, `dist/README.md`, `CHANGELOG.md`, `doc/リリース手順-macOS.md`*
