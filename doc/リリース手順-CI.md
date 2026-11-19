# リリース手順（CI：タグの push で Windows / macOS / Linux を公開）

タグ `v<版数>` を push すると、GitHub Actions（`.github/workflows/release.yml`）が 3 OS の配布物をビルドし、
GitHub リリースに添付する。手作業の手順（CI が使えないとき・手元で作り直すとき）は
`doc/リリース手順-Windows.md` / `doc/リリース手順-macOS.md` を参照。

> **バージョン方針**: 版数は `src/Directory.Build.props` の `<VersionPrefix>`（＋`<VersionSuffix>`）で単一管理。
> **明示的な bump up 指示がない限り版数は変更しない。** タグは必ずこの版数に `v` を付けたものにする
> （一致しないとワークフローが失敗する）。

---

## 1. 作られる配布物

| OS | ファイル | ビルドするランナー |
|---|---|---|
| Windows | `DataverseGW-<ver>-win-x64.zip` / `.msi`（perUser・署名なし） | `windows-latest` |
| macOS | `DataverseGW-<ver>-osx-arm64.tar.gz` / `-osx-x64.tar.gz` | `macos-latest`（Apple Silicon。osx-x64 はクロス発行） |
| Linux | `DataverseGW-<ver>-linux-x64.tar.gz` / `.deb` | `ubuntu-latest` |

- それぞれに `<ファイル名>.sha256`（`<hash>  <ファイル名>` 形式）を付ける。
- いずれも自己完結（.NET ランタイム同梱）。コンテナは従来どおり `dist/Dockerfile`（本ワークフローの対象外）。
- リリースノートは `CHANGELOG.md` の `## [<ver>]` の節。

## 2. 前提

- リリース対象のコミットが `main` に入っていること。
- `src/Directory.Build.props` の版数と、`CHANGELOG.md` の `## [<ver>] - <日付>` の節が揃っていること（節が無いと失敗する）。
- タグを push できる権限（Write 以上）。

## 3. 手順

```bash
git switch main && git pull
VER=$(python3 -c "import xml.etree.ElementTree as E;r=E.parse('src/Directory.Build.props').getroot();print(r.find('.//VersionPrefix').text.strip())"); echo "$VER"
git tag "v$VER"
git push origin "v$VER"
```

PowerShell の場合:
```powershell
git switch main; git pull
$VER = ([xml](Get-Content src\Directory.Build.props)).SelectSingleNode('//VersionPrefix').InnerText.Trim(); $VER
git tag "v$VER"
git push origin "v$VER"
```

## 4. 流れと確認

1. **prepare**: タグと版数の照合、単体テスト
2. **windows / macos / linux**: 並列にビルド・パッケージ化・チェックサム
   - macOS: アドホック署名の確認（`codesign --verify`）と、tar.gz への `._*` 混入の検査
   - Linux: ランナーに .deb を実際に導入して、配置を確認
3. **release**: チェックサムを照合し、リリースを作る（同じタグのリリースが既にあれば、資産だけを `--clobber` で差し替える）

```bash
gh run list --workflow release.yml --limit 3
gh run watch                 # 進行を見る
gh release view "v$VER"      # 添付資産が 12 個（6 ファイル＋各 .sha256）揃っているか
```

## 5. 失敗したとき

| 症状 | 対処 |
|---|---|
| prepare で「タグと版数が一致しない」 | タグを消して（`git push origin :refs/tags/v<ver>` と `git tag -d v<ver>`）、正しい版数で付け直す |
| release で「CHANGELOG.md に節が無い」 | `CHANGELOG.md` に節を足して main に入れ、タグを付け直す |
| 一部の OS のビルドだけ失敗 | 原因を直して main に入れ、タグを付け直す。またはジョブの再実行（Re-run failed jobs） |

- タグ・リリースの削除は取り消せない。消す前に、どのリリースが対象かを確かめる。

## 6. PR と手動実行

- 配布まわり（`dist/**`・`release.yml`・`src/Directory.Build.props`）を変える PR では、同じワークフローが
  **ビルドまで**を行う（リリースは作らない）。配布物は Actions の成果物（artifact）として残る（保持期間はリポジトリの設定。既定 90 日）。
- Actions の画面から手動実行（workflow_dispatch）しても、ビルドまでを行う。

## 7. 制約

- コード署名はしない（Windows は SmartScreen、macOS は Gatekeeper の警告が出うる）。
  Windows の組織内署名は `dist/README.md` §3。
- Linux の .deb は amd64 のみ（`dist/package-deb.sh` は arm64 にも対応しているが、CI ではビルドしていない）。
- .deb は systemd のサービス登録をしない。データはユーザーごとの `~/.local/share/DataverseGW`。

*作成日: 2026-11-19 / 関連: `.github/workflows/release.yml`, `dist/publish-linux.sh`, `dist/package-deb.sh`, `CHANGELOG.md`*
