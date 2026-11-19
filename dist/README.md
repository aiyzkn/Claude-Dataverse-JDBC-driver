# 配布パッケージのビルド (2.7.1)

Dataverse GW の配布物を作るためのスクリプト群です。
生成物（`dist/out/`・`*.msi`・`*.zip`）は git 管理外です。

## 前提
- .NET SDK（8 以降。SDK 10 でも `DOTNET_ROLL_FORWARD=LatestMajor` で net8.0 をビルド）
- MSI をビルドする場合のみ WiX v4（`build-msi.ps1` が未導入ならローカルツールとして取得）
- 署名する場合のみ Windows SDK の `signtool.exe` と組織のコード署名証明書

## バージョン管理（単一管理点）
バージョンは **`src/Directory.Build.props` の `<VersionPrefix>`** で一元管理する。ここを更新するだけで、
全アセンブリ（AssemblyVersion/FileVersion/InformationalVersion）と配布物（ZIP/MSI のファイル名）に一貫反映される。

**採番ポリシー（厳守）**
- **セマンティック・バージョニング（[SemVer 2.0.0](https://semver.org/lang/ja/) / `MAJOR.MINOR.PATCH`）** に従う。
  - `MAJOR`: 後方非互換の変更 / `MINOR`: 後方互換の機能追加 / `PATCH`: 後方互換のバグ修正
  - プレリリースは `<VersionSuffix>`（例 `rc.1` → `1.0.0-rc.1`）、ビルドメタデータ `+<sha>` は自動付与
- **ユーザーが明示的に bump up を指示しない限り、バージョン番号を変更しない。** 機能追加・修正のたびに
  自動で上げてはならない。リリース（版数を上げる）判断はユーザーが行う。

```xml
<!-- src/Directory.Build.props -->
<VersionPrefix>1.0.0</VersionPrefix>
<!-- プレリリースは <VersionSuffix>rc.1</VersionSuffix> を設定(Version=1.0.0-rc.1) -->
```

- `publish.ps1` は props の値でアセンブリを刻む（exe の ProductVersion/FileVersion に反映）。
- `package-zip.ps1` / `build-msi.ps1` は `-Version` 省略時に props の `VersionPrefix` を自動で読む
  （明示的に `-Version` を渡せば上書き可）。
- リリース手順: ① `VersionPrefix` を更新 → ② `publish.ps1` → ③ `package-zip.ps1`（必要なら `build-msi.ps1`）。

## 成果物の種類
1. **ポータブル ZIP**（推奨・追加ツール不要）: 自己完結 exe ＋ インストールスクリプト
2. **MSI**（WiX 必要）: 署名可能なインストーラ

## 手順

### 1. 発行（Windows）
```powershell
powershell -ExecutionPolicy Bypass -File dist\publish.ps1
# -> dist/out/app/DataverseGW.exe (self-contained single-file, .NET 8 同梱)
```

### 1b. 発行（macOS）
バージョンは Windows と同一の単一管理点（`src/Directory.Build.props`）から取得され、アセンブリに刻まれる（版数一致）。
```bash
bash dist/publish-mac.sh              # 既定 RID = osx-arm64 (Apple Silicon)
bash dist/publish-mac.sh osx-x64      # Intel Mac
# -> dist/out/app-<rid>/DataverseGW (self-contained single-file, .NET 8 同梱)
# tar.gz 化: COPYFILE_DISABLE=1 tar -czf DataverseGW-<ver>-<rid>.tar.gz -C dist/out/app-<rid> .
```
> macOS 実機での発行・動作確認手順の詳細は `doc/macos対応手順書-3.2.1.md` §14 を参照。
> **ビルド→tar.gz→SHA-256→GitHub リリース公開までの操作手順**は `doc/リリース手順-macOS.md` を参照。

### 2a. ZIP を作る
```powershell
powershell -ExecutionPolicy Bypass -File dist\package-zip.ps1
# -Version 省略時は src/Directory.Build.props の VersionPrefix を自動使用
# -> dist/out/DataverseGW-<version>-win-x64.zip
```

### 2b. MSI を作る（WiX v4）
```powershell
powershell -ExecutionPolicy Bypass -File dist\build-msi.ps1
# -Version 省略時は props の VersionPrefix を自動使用。未導入なら WiX をローカルツールとして取得
# -> dist/out/DataverseGW-<version>-win-x64.msi (perUser, 管理者不要)
```
WiX ソースは `dist/wix/Package.wxs`。`LocalAppData\Programs\DataverseGW` へ配置し、
スタートメニューショートかットと `MajorUpgrade` を含みます。

### 3. コード署名（組織内配布：自己署名 ＋ GPO 信頼配布）

組織内で配布する場合は、**自己署名のコード署名証明書**を作り、公開部を
**Active Directory グループポリシーで「信頼された発行元／ルート」に配布**すれば、
無償で組織内端末の SmartScreen/警告を抑止できます。証明書はリポジトリに含めません。

署名ツールは Windows SDK 不要のクロスプラットフォーム **`sign` .NET ツール**を使います:
```powershell
dotnet tool install --global sign --prerelease
```

#### 3-1. 署名証明書を作成（初回のみ・鍵は安全に保管）
```powershell
powershell -File dist\New-InternalSigningCert.ps1
# -> Cert:\CurrentUser\My に秘密鍵、dist\out\*.cer に公開証明書(GPO配布用)
#    出力の Thumbprint を控える
```

#### 3-2. 署名する
```powershell
# ストアの証明書(SHA-1 拇印指定。SHA-256 は自動導出)
powershell -File dist\sign.ps1 -File dist\out\DataverseGW-1.0.0-win-x64.msi -Thumbprint <thumbprint>
# PFX の場合
powershell -File dist\sign.ps1 -File <path> -PfxPath <signing.pfx> -PfxPassword <pwd>
```
`sign` ツールでタイムスタンプ付き（DigiCert RFC3161）SHA-256 署名を付与します。
`signtool.exe` があればそちらにフォールバックします。

> 署名直後の `Get-AuthenticodeSignature` は自己署名のため `UnknownError`
> （＝信頼されていないルートで終端）になります。これは正常で、下記 GPO 配布後に
> 組織内端末では `Valid` になります。タイムスタンプ済みのため証明書失効後も署名は有効です。

#### 3-3. 組織内端末へ信頼を配布（GPO）
`dist\out\*.cer`（公開証明書のみ）を次のストアに配布します:
- **信頼された発行元 (Trusted Publishers)** — 発行元として信頼
- **信頼されたルート証明機関 (Trusted Root)** — 自己署名のため必要

GPO: `コンピューターの構成 > ポリシー > Windows 設定 > セキュリティ設定 > 公開キーのポリシー`
の該当ストアに `.cer` をインポート（または `certutil -addstore` を配布スクリプトで実行）。
社外配布が必要になった場合は Azure Trusted Signing 等の公的 CA へ切り替えてください。

## ファイル一覧
| ファイル | 役割 |
|---|---|
| `publish.ps1` | self-contained single-file publish（win-x64） |
| `publish-mac.sh` | self-contained single-file publish（macOS。osx-arm64 既定 / osx-x64。版数は Windows と共通） |
| `package-zip.ps1` | ポータブル ZIP 生成 |
| `build-msi.ps1` | WiX v4 で MSI 生成（未導入時はローカルツール取得） |
| `sign.ps1` | signtool によるコード署名（証明書は実行時指定） |
| `wix/Package.wxs` | WiX v4 パッケージ定義（perUser） |
| `install/Install.ps1` `install/Uninstall.ps1` | ZIP 版のインストール/削除 |
| `DISTRIB-README.md` | 配布物同梱の利用者向け README |

クライアント接続手順は `doc/setup-guide-クライアント接続.md` を参照。
