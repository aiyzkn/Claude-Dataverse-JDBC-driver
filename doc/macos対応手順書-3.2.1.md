# macOS 対応 実施手順書（WBS 3.1 系 / タスク 3.2.1）

対象タスク: **3.2.1 macOS 対応（Keychain 連携・tar.gz 配布・動作確認）**
本書は macOS 実機での作業手順・確認事項・提出アウトプットをまとめたものです。Windows 側（Claude）で
可能なクロスプラットフォームのコード変更は適用済みで、**残るのは macOS 実機でのビルド・Keychain 検証・
tar.gz パッケージング・動作確認**です。

---

## 0. 前提環境

| 項目 | 内容 |
|---|---|
| OS | macOS 12 以降（Apple Silicon / Intel いずれか） |
| .NET SDK | **.NET 8 SDK**（`dotnet --version` で 8.x。9/10 でも `DOTNET_ROLL_FORWARD=LatestMajor` で可） |
| ランタイム ID | Apple Silicon = `osx-arm64` / Intel = `osx-x64`（自機に合わせて選択） |
| ソース | 本リポジトリを clone（ブランチ `main`） |
| 接続情報 | 環境変数（下記）。**認証情報はファイルにコミットしない**（`.env` 禁止） |

### 接続用の環境変数（シェルで export）
```bash
export DATAVERSE_URL="https://<org>.crm7.dynamics.com/"
export DATAVERSE_TENANT_ID="<tenant-guid>"
export DATAVERSE_CLIENT_ID="<app-client-id>"
export DATAVERSE_AUTH="ClientSecret"          # まず ClientSecret で疎通確認
export DATAVERSE_CLIENT_SECRET="<secret>"     # ClientSecret のときのみ。履歴に残さない
# Interactive/DeviceCode を試す場合は DATAVERSE_AUTH を切替（アプリ登録要件は §7 参照）
```

---

## 1. コードの状態（Windows 側で適用済み・macOS で検証するもの）

| 箇所 | 状態 | macOS での意味 |
|---|---|---|
| `TokenCachePersistence.cs` | **適用済み**: `WithMacKeyChain(serviceName="DataverseGW", accountName="msal_cache")` と `WithLinuxKeyring` を追加 | 対話型/デバイスコード認証のトークンが **Keychain** に保護保存される（要 §6 検証） |
| `DataverseAuthenticator.cs` | **適用済み**: 対話型(Interactive)を DeviceCode と同じ `PublicClientApplication`＋Keychain 経路に**統一**（旧: ServiceClient の `TokenCacheStorePath` ファイル経路を廃止） | Interactive も DeviceCode と同様に **Keychain 保護**・共通キャッシュ(SSO)。旧実装では Interactive が Keychain を使わなかった不整合を解消 |
| `Lifecycle/SleepPreventer.cs` | 非 Windows は **no-op**（`OperatingSystem.IsWindows()` ガード） | macOS ではスリープ抑止しない。動作に支障なし。将来 `caffeinate` 対応は任意（§8） |
| `Lifecycle/SingleInstanceGuard.cs` | 名前付き Mutex `Local\DataverseGW_<port>` | macOS の .NET 名前付き Mutex はプロセス間で挙動が限定的。多重起動防止が効かない可能性あり（§9・確認事項） |
| `%LOCALAPPDATA%` 相当のパス | `Environment.SpecialFolder.LocalApplicationData` | macOS では **`~/Library/Application Support/DataverseGW/`**（本検証機で実測。.NET/環境により `~/.local/share/DataverseGW/` の場合あり）配下に audit/reports/dryrun/checkpoints/tokencache が作成される |
| 配布 | Windows は MSI/ZIP＋Authenticode。macOS は **tar.gz**（本書 §4-5）＋必要なら codesign/公証 | Gatekeeper の隔離属性に注意（§10） |

> `cacheName` は既定で `msal_cache`（`DataverseAuthenticator` が指定）。実際の値は起動ログ/コードで確認。

---

## 2. ビルド（self-contained single-file）

自機のランタイム ID（`osx-arm64` または `osx-x64`）を `RID` に設定して実行します。

```bash
cd <repo-root>
RID=osx-arm64   # Intel Mac は osx-x64
dotnet publish src/Gateway.Host/Gateway.Host.csproj \
  -c Release -r $RID --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=none \
  -o dist/out/app-$RID
```

- 成果物: `dist/out/app-$RID/DataverseGW`（単一実行ファイル、.NET 8 ランタイム同梱）
- 実行権限付与: `chmod +x dist/out/app-$RID/DataverseGW`

**確認事項(2)**: publish がエラーなく完了し、`DataverseGW` が生成される。サイズは 100MB 前後（自己完結）。

---

## 3. 単体テスト（macOS 上での回帰）

```bash
export DOTNET_ROLL_FORWARD=LatestMajor
dotnet test src/Gateway.Tests/Gateway.Tests.csproj -c Debug
```
**確認事項(3)**: 全テスト合格（Windows では 159 件）。OS 依存でスキップ/失敗が出たら記録。

---

## 4. tar.gz パッケージング

```bash
cd dist/out
tar -czf DataverseGW-1.0.0-$RID.tar.gz -C app-$RID .
shasum -a 256 DataverseGW-1.0.0-$RID.tar.gz > DataverseGW-1.0.0-$RID.tar.gz.sha256
```
**アウトプット(4)**: `DataverseGW-1.0.0-osx-*.tar.gz` と `.sha256`。

（任意）内部配布で Gatekeeper を通すなら Apple Developer ID で codesign/公証。無い場合は §10 の隔離属性解除で対応。

---

## 5. 展開・起動

```bash
mkdir -p ~/DataverseGW && tar -xzf DataverseGW-1.0.0-$RID.tar.gz -C ~/DataverseGW
cd ~/DataverseGW
chmod +x DataverseGW
export DOTNET_ROLL_FORWARD=LatestMajor
./DataverseGW
```
**確認事項(5)**: ログに以下が出る。
```
Dataverse 接続成功: ... / 認証=ClientSecret / ...
Gateway listening on grpc://localhost:32010 (Arrow Flight SQL)
```

---

## 6. Keychain 連携の検証（本タスクの核心）

対話型/デバイスコード認証でトークンが **Keychain** に保護保存され、再起動で**再認証不要**になることを確認します。

1. `export DATAVERSE_AUTH=Interactive`（または `DeviceCode`）で起動しサインイン（アプリ登録要件は §7）。
2. Keychain 保存の確認:
   ```bash
   security find-generic-password -s "DataverseGW"
   # ヒットすれば Keychain にキャッシュ項目が作成されている
   ```
   GUI の「キーチェーンアクセス」で `DataverseGW` を検索しても可。
3. ゲートウェイを停止→再起動し、**サインインプロンプトが出ずに接続成功**することを確認（キャッシュ再利用）。
4. キャッシュ保護の確認: `~/.local/share/DataverseGW/`（または起動ログのキャッシュ dir）に
   `msal_cache.bin` が生成されるが、**中身は Keychain 保護**で平文トークンではないこと。

**確認事項(6)**:
- [ ] `security find-generic-password -s "DataverseGW"` がヒット（**DeviceCode / Interactive の両方**で確認）
- [ ] 再起動後に再認証なしで接続（トークンキャッシュ再利用）
- [ ] **平文のトークンファイルが存在しない**（＝下記の通り、Keychain 保存なのでファイルは作られないのが正常）

> **重要（キャッシュファイルが無いのは正常）**: macOS では MSAL キャッシュの秘密は **Keychain に保存**され、
> ディスク上に平文の access/refresh token ファイルは作られません。tokencache ディレクトリにファイルが
> 無い＝**平文トークンがディスクに無い**＝望ましい状態です（3番目のチェック項目は「満たされている」）。
> `Environment.SpecialFolder.LocalApplicationData` の実体は macOS では **`~/Library/Application Support`**
> （本検証機で実測。.NET/環境により `~/.local/share` の場合あり）。存在確認は
> `ls -la ~/Library/Application\ Support/DataverseGW/tokencache/`（無ければ `~/.local/share/...` も確認）。

> **旧実装での注意（解消済み）**: 以前は Interactive のみ ServiceClient のファイルキャッシュ
> (`interactive.cache`) を使い Keychain に入りませんでした。本手順書のコードでは **Interactive も DeviceCode と
> 同じ Keychain 経路に統一済み**です。以前 Interactive で Keychain ヒットしなかった場合は、最新コードで
> **再ビルドして Interactive を再確認**してください（下記）。

**追加確認（Interactive 統一後の再確認）**:
```bash
# 最新コードで再ビルド後
export DATAVERSE_AUTH=Interactive
./DataverseGW          # 既定ブラウザでサインイン
# 別ターミナルで:
security find-generic-password -s "DataverseGW"   # 今度はヒットするはず
```
- [ ] Interactive でもブラウザサインインが成功し、`security find-generic-password` がヒット
- [ ] 停止→再起動で再認証なし（DeviceCode と共通キャッシュ＝SSO）

> うまく Keychain に入らない/例外が出る場合は §10 のトラブルシューティング（サンドボックス・エンタイトルメント）参照。

---

## 7. 認証方式ごとの前提（アプリ登録）

- **ClientSecret**: クライアント資格情報フロー。アプリ登録の API 権限設定は不要。
- **Interactive / DeviceCode**: デリゲートフロー。アプリ登録に
  **Dynamics CRM 委任（`user_impersonation`）＋「パブリック クライアント フロー許可」= はい** が必要
  （未設定だと `AADSTS650057`）。※本要件は Windows 実機で確認済み。
- **Interactive（追加要件）**: 本ドライバでは Interactive も **あなたのアプリ登録（`DATAVERSE_CLIENT_ID`）** を使う。
  ブラウザのループバックにリダイレクトするため、リダイレクト URI の登録が必要（未登録だと `AADSTS500113`）。
  **重要**: MSAL のデスクトップ対話はパブリッククライアント扱いのため、`http://localhost` は必ず
  **「モバイル アプリケーションとデスクトップ アプリケーション」プラットフォーム**に登録する。
  **Web / SPA に登録してはいけない**:
  - Web/SPA だと任意ポートのループバックが一致せず `AADSTS700218`、
  - さらに Web は機密クライアント扱いのため「シークレットが無い」と判定され `AADSTS7000218`
    （The request body must contain 'client_secret' or 'client_assertion'）になる。
  → **Web 側の `http://localhost` は削除**し、モバイル/デスクトップ側にのみ登録すること。
  - 認証 → プラットフォームを追加 → **モバイル/デスクトップ** → カスタム URI `http://localhost` を追加
    （併せて既定候補 `https://login.microsoftonline.com/common/oauth2/nativeclient` も有効化推奨）
  - Web/SPA 側に誤って `http://localhost` を入れていたら削除する
  - 詳細設定 → パブリック クライアント フローを許可する = はい
  DeviceCode はデバイスコードフローのためリダイレクト URI 不要。

---

## 8. スリープ抑止（macOS）

- 現状 `SleepPreventer` は macOS で **no-op**（抑止しない）。長時間ジョブ中の自動スリープを防ぐには、
  実行中だけ別ターミナルで `caffeinate -dimsu -w $(pgrep -f DataverseGW)` を併用するのが簡便。
- （任意・将来）ネイティブ対応として `caffeinate` をプロセス起動する実装を追加可能。今回は**動作に支障が
  ないこと**の確認で可（大量ジョブ時は上記 `caffeinate` 併用を手順として案内）。

**確認事項(8)**: 長時間ジョブ中にスリープしない運用（`caffeinate` 併用）で完走できる。

---

## 9. 単一起動ガード（改修済み）

- Windows は名前付き Mutex で事前に弾く。macOS/Linux では .NET 名前付き Mutex がプロセス間で効かず、
  以前は 2 つ目が Kestrel のポート二重 bind で **`address already in use` の IOException（スタックトレース）** で
  異常終了していた（本検証で確認）。
- **改修済み**: 全 OS で「アドレス使用中（＝既に起動）」を捕捉し、**友好的メッセージ＋正常終了(exit 0)** に統一
  （`Program.cs` の `IsAddressInUse` ハンドラ）。機能面（2 つ目は起動しない）は従来どおり。
- **確認事項(9)**: 最新コードで再ビルド後、同一ポートで 2 つ起動 → 2 つ目が
  「ゲートウェイは既に起動しているため二重起動を中止します」を出して**即終了(exit 0、スタックトレース無し)**すること。

---

## 10. 動作確認チェックリスト（機能横断）

ClientSecret 起動状態で、Python(ADBC) か DBeaver から確認（`grpc://localhost:32010` / JDBC は
`jdbc:arrow-flight-sql://localhost:32010/?useEncryption=false`）。JDBC 利用時は macOS でも netty の
`--add-opens` 一式が必要（`doc/setup-guide-クライアント接続.md` §2-3）。

### 一括実行スクリプト（推奨）
下表の #1〜#14 を **1 コマンド**で確認できる Python スクリプトを同梱（`doc/scripts/verify_checklist.py`）。

```bash
pip install adbc-driver-flightsql pyarrow openpyxl   # openpyxl は #7 xlsx 用(任意)
export GW=grpc://localhost:32010
export GW_EXE=~/DataverseGW/DataverseGW   # #6/#7 dvload・#9 本番ガードを検証(任意)
python3 -u doc/scripts/verify_checklist.py
```

- `GW_EXE` 未設定なら #6/#7/#9 は SKIP（ADBC 駆動の 10 項目を確認）。設定すると dvload・本番ガードも自動検証。
- #9 本番ガードは別ポート(32011)でゲートウェイを一時起動して検証するため、`GW_EXE` 設定時は数分かかる。
- #12 認証 4 方式は ClientSecret=稼働中で確認済み、Certificate/Interactive/DeviceCode は §6/§7 の手順で手動。
- 結果は各項目 PASS/FAIL/SKIP と末尾サマリで表示（FAIL があれば終了コード 1）。この出力を成果物(§11-2)とする。

各項目の詳細（手動で個別確認する場合の参考）:

| # | 機能 | 手順概略 | 期待 |
|---|---|---|---|
| 1 | SELECT（読み取り） | `SELECT TOP 5 accountid, name FROM account` | 行が返る |
| 2 | INSERT（バルク） | poc_gadget に数十行 | 全件作成 |
| 3 | UPSERT | 代替キー/GUID 指定 | 冪等（重複なし） |
| 4 | TRUNCATE/DELETE | `TRUNCATE TABLE <合成表>` / `DELETE ... WHERE` | 非同期削除ジョブ発行 |
| 5 | N:N 関連付け | 交差 INSERT / ASSOCIATE | 交差レコード作成 |
| 6 | dvload CSV | `./DataverseGW dvload --file x.csv --table poc_gadget` | 着地・ETA ログ |
| 7 | dvload xlsx | 同上 .xlsx | 着地 |
| 8 | ドライラン | `SET DRY_RUN=ON` または `--dry-run` | 未実行＋レポート |
| 9 | 本番ガード | `Gateway__IsProduction=true` で起動→書込 | ブロック→`SET ALLOW_PRODUCTION_WRITES=ON` で解除 |
| 10 | 名前→ID 解決 | `RESOLVE_NAMES` で Lookup を名前指定 | 一意=解決/曖昧=行エラー |
| 11 | 進捗照会 | 別接続で `SELECT * FROM gateway_progress` | running→completed |
| 12 | 認証 4 方式 | ClientSecret/Certificate/Interactive/DeviceCode | 各接続成功（Interactive/DeviceCode は §7） |
| 13 | チェックポイント再開 | 大きめ INSERT を中断→再投入 | 未完了分から再開・重複なし |
| 14 | 監査/失敗レポート | 書込後 `~/.local/share/DataverseGW/audit`・`reports` | CSV/MD 生成 |

> dvload は稼働中ゲートウェイと同一プロセス不要（別プロセスで直接 Dataverse 接続）。ポート未使用。

---

## 11. 提出アウトプット（このタスクの成果物）

1. **配布物**: `DataverseGW-1.0.0-osx-<arch>.tar.gz` ＋ `.sha256`
2. **動作確認結果表**: §10 チェックリストの各項目 PASS/所見（スクショ or ログ抜粋）
3. **Keychain 検証ログ**: §6 の `security find-generic-password` 出力・再起動再利用の確認
4. **既知の所見**: §9 単一起動ガードの実挙動、§8 スリープ抑止の運用可否、その他 OS 依存差異
5. **単体テスト結果**: §3 の合否（件数・スキップ/失敗）

---

## 12. トラブルシューティング

| 症状 | 対処 |
|---|---|
| `You must install .NET` | `export DOTNET_ROLL_FORWARD=LatestMajor`。または .NET 8 ランタイムを導入 |
| tar.gz 展開後に実行不可（"開発元を検証できません"） | 隔離属性を解除: `xattr -d com.apple.quarantine ./DataverseGW`（内部配布時） |
| 実行権限がない | `chmod +x DataverseGW` |
| Keychain に保存されない/例外 | サンドボックス無効で実行しているか確認。GUI 実行時は Keychain アクセス許可プロンプトを許可。CLI 実行推奨 |
| JDBC(DBeaver) で netty 例外 | `dbeaver.ini` に `--add-opens` 一式（setup-guide §2-3）。macOS の JDK も同様 |
| 多重起動を弾けない | §9 に記録。ポート二重 bind で 2 つ目は起動失敗するはず |
| 長時間ジョブ中にスリープ | `caffeinate -dimsu -w $(pgrep -f DataverseGW)` を併用（§8） |

---

## 13. 完了後の連携

上記 §11 の成果物・結果を共有いただければ、Windows 側で **WBS 3.2.1 を完了化**し、
必要なコード改修（例: 単一起動ガードのポートロック方式化、`caffeinate` ネイティブ対応）が要ると判明した
場合はチケット化して対応します。所見（特に §9 の実挙動）を添えてください。

---

## 14. Phase 3 追加作業（macOS 実機・4.1.1 / 4.1.2）

### 4.1.1 Intel(osx-x64) ビルド ＋ tar.gz の `._*` 除去
```bash
# Intel Mac もしくはクロス発行(実行確認は Intel Mac で)
RID=osx-x64
dotnet publish src/Gateway.Host/Gateway.Host.csproj -c Release -r $RID --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none \
  -o dist/out/app-$RID
chmod +x dist/out/app-$RID/DataverseGW

# tar.gz の AppleDouble(._*)を除去してパッケージ(COPYFILE_DISABLE=1)
cd dist/out
COPYFILE_DISABLE=1 tar -czf DataverseGW-1.0.0-$RID.tar.gz -C app-$RID .
shasum -a 256 DataverseGW-1.0.0-$RID.tar.gz > DataverseGW-1.0.0-$RID.tar.gz.sha256
tar -tzf DataverseGW-1.0.0-$RID.tar.gz | grep -c '\._'   # => 0 なら ._* 除去 OK
```
- **確認事項(4.1.1)**: Intel Mac で起動・§10 チェック通過、`._*` エントリ 0、SHA-256 記録。arm64 版も同様に `COPYFILE_DISABLE=1` で再パッケージ可。

### 4.1.2 macOS の TDS 利用可否調査
macOS で読み取りが TDS を使えるか(現状は FetchXML フォールバックと推定)を切り分ける。

> **注意**: 旧手順の `SELECT COUNT(*)` は判別に使えない。Phase 2 で別名なし集計を FetchXML でも
> 合成列名で扱えるよう修正したため、COUNT は**両経路で成功**する。TDS 強制時にエラーになる
> かどうかで切り分ける(下記)。また、実行時に出る
> `Warning: Cannot disable autocommit; conn will not be DB-API 2.0 compliant` は
> **ADBC が全 OS で毎回出す無害な警告**(Flight SQL がトランザクション非対応なため)で、TDS とは無関係。

```bash
# TDS を強制。TDS が使えない環境では TDS 対象クエリが例外になる
export DATAVERSE_AUTH=ClientSecret
Gateway__UseTdsEndpoint=true ./DataverseGW            # 起動(このターミナルはログ表示のまま)
# 別ターミナルで単純 SELECT(TDS 強制時は TDS 経路で実行される)
python3 -c "import adbc_driver_flightsql.dbapi as fs; c=fs.connect('grpc://localhost:32010'); cur=c.cursor(); cur.execute('SELECT TOP 1 name FROM account'); print('RESULT:', cur.fetchall())"
```
- **判定**:
  - `RESULT: [(...)]` と**行が返れば macOS でも TDS 利用可**(FetchXML 強制フォールバックは不要)。
  - **TDS/接続関連の例外**(タイムアウト・接続不可・SQL エラー等)が出れば **TDS 不可を確定**し、
    FetchXML 経路の最適化方針(別名なし集計は合成列名で安全化済、複雑集計はアプリ側で別名付与を推奨)を採る。
- **確認事項(4.1.2)**: 上記 `SELECT TOP 1 ...`(UseTdsEndpoint=true)の**成否**、失敗時の**例外本文**、
  および**ゲートウェイ側のログ**(TDS 接続エラーが出るか)を共有。可能なら `Gateway__UseTdsEndpoint=false` でも
  同じ SELECT を実行し、FetchXML では成功することを確認(＝TDS のみが不可という切り分け)。

#### 実機結果(2026-07-21・Intel Mac)
- `UseTdsEndpoint=true` → `SELECT TOP 1 name FROM account` = `[('altkey-acc',)]`(**成功**)
- `UseTdsEndpoint=false` → 同 = `[('取引先企業1',)]`(**成功**、別経路のため別の行)
- **決定確認(`SELECT @@VERSION`・2026-07-21)**:
  - `UseTdsEndpoint=true` → `Microsoft SQL Azure (RTM) - 12.0.2000.8 ...`(**実 TDS エンドポイント=Azure SQL の応答**)
  - `UseTdsEndpoint=false` → `Microsoft Dataverse ... SQL 4 CDS - 10.4.4.0 ... Mark Carrington`(**Sql4Cds ローカルエンジンのエミュレーション=FetchXML 経路**)
- **確定結論**: **macOS でも TDS 読み取りは動作する**。両モードで @@VERSION の応答元が異なる(Azure SQL vs Sql4Cds)
  ことから、TDS 強制時は確かに TDS を使用しており黙ったフォールバックは無い。従来の「macOS は TDS 不可・
  FetchXML 必須」推定は**読み取りについて誤り**と確定。**既定 `auto` のままで macOS でも TDS が使われる**
  (FetchXML 強制は不要)。FetchXML は TDS 無効化組織/表現不可クエリ時のフォールバックとして引き続き機能。

*(4.1.3 名前→ID 辞書 TTL・4.1.4 進捗粒度は Windows で実装・検証完了済。macOS でも同一コードで動作。)*

---

*作成日: 2026-07-14 / 更新: 2026-07-15(§14 Phase 3 4.1.1/4.1.2 追加) / 対象: WBS 3.2.1・4.1.x（macOS）/ 関連: `doc/setup-guide-クライアント接続.md`*
