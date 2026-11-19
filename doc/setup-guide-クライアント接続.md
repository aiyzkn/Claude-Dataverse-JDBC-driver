# Dataverse GW — セットアップ / クライアント接続ガイド

対象: DBeaver / DataGrip / Python(ADBC)。ゲートウェイは `localhost:32010` で
Arrow Flight SQL サーバーとして動作します（外部公開なし）。

---

## 1. ゲートウェイの導入と起動

### 1.1 インストール
配布 ZIP（`DataverseGW-<ver>-win-x64.zip`）を展開し、管理者権限不要でインストール:

```powershell
powershell -ExecutionPolicy Bypass -File install\Install.ps1
```

> MSI をご利用の場合は `DataverseGW-<ver>-win-x64.msi` を実行してください
> （ビルド手順は `dist/README.md`）。

### 1.2 認証（初回のみ・ユーザー環境変数）

| 変数 | 値 |
|---|---|
| `DATAVERSE_URL` | `https://<org>.crm7.dynamics.com/` |
| `DATAVERSE_AUTH` | `ClientSecret` / `Certificate` / `Interactive` / `DeviceCode` |
| `DATAVERSE_TENANT_ID` | テナント GUID |
| `DATAVERSE_CLIENT_ID` | アプリ登録 Client ID |
| `DATAVERSE_CLIENT_SECRET` | シークレット（ClientSecret のみ・**コミット/共有厳禁**） |

> **`Interactive` / `DeviceCode` を使う場合のアプリ登録要件**（デリゲートフロー）:
> 1. API のアクセス許可に **Dynamics CRM → 委任 → `user_impersonation`** を追加（必要に応じ管理者同意）
> 2. 認証 → 詳細設定 → **「パブリック クライアント フローを許可する」= はい**
>
> 未設定だと `AADSTS650057: Invalid resource`（Dataverse がリソース一覧に無い）で失敗します。
> `ClientSecret` はクライアント資格情報フローのため、この設定は不要です。

### 1.3 起動
`DataverseGW.exe` を実行。ログに接続情報が出ます:

```
Gateway listening on grpc://localhost:32010 (Arrow Flight SQL)
JDBC URL: jdbc:arrow-flight-sql://localhost:32010/?useEncryption=false
```

---

## 2. DBeaver

DBeaver は Arrow Flight SQL JDBC ドライバで接続します。

1. **ドライバ登録**: `データベース` → `ドライバーマネージャー` → `新規`
   - ドライバー名: `Arrow Flight SQL`
   - ライブラリ（Maven）: `org.apache.arrow:flight-sql-jdbc-driver:19.0.0`
     （またはローカルの `flight-sql-jdbc-driver-19.0.0.jar` を追加）
   - クラス名: `org.apache.arrow.driver.jdbc.ArrowFlightJdbcDriver`
   - URL テンプレート: `jdbc:arrow-flight-sql://{host}:{port}/?useEncryption=false`
2. **接続作成**: ホスト `localhost` / ポート `32010`
   - URL: `jdbc:arrow-flight-sql://localhost:32010/?useEncryption=false`
3. **JVM 引数（JDK 17 以降・特に JDK 21/25 で必須）**: netty が新しい JDK で
   リフレクション制限に触れるため、DBeaver の起動 `dbeaver.ini`（`-vmargs` 以下）に追記:

```
--add-opens=java.base/java.nio=ALL-UNNAMED
--add-opens=java.base/jdk.internal.misc=ALL-UNNAMED
--add-opens=java.base/sun.nio.ch=ALL-UNNAMED
-Dio.netty.tryReflectionSetAccessible=true
```

> 上記が無いと `UnsupportedOperationException`（netty の Unsafe/DirectByteBuffer）で
> 接続に失敗することがあります（Phase 0 で確認済み）。
>
> **JDK バージョン注意**: `--sun-misc-unsafe-memory-access=allow` は **JDK 24 以降専用**です。
> **JDK 21 以下に付けると JVM が起動しません**（DBeaver 25.x 同梱 JRE は Temurin 21 のため付けない）。
> JDK 24 以降を使う場合のみ、上記に加えて `--sun-misc-unsafe-memory-access=allow` を追記してください。
> DBeaver 同梱 JRE では既定の `--add-opens` が概ね揃っており、追加は
> `-Dio.netty.tryReflectionSetAccessible=true` 1 行で足りることが多いです（実機 JDK 21 で確認）。

4. **データ転送（一括投入）**: DBeaver の「データ転送」ウィザードで CSV/表→テーブルへ
   INSERT できます。転送はバッチ送信されるため大容量でも安定します。中断→再開は
   同一データ再送で自動再開します（チェックポイント F-11）。

---

## 3. DataGrip / IntelliJ

1. `Data Source` → `Driver` を複製し、ドライバ JAR に
   `flight-sql-jdbc-driver-19.0.0.jar`、クラス `org.apache.arrow.driver.jdbc.ArrowFlightJdbcDriver` を設定。
2. URL: `jdbc:arrow-flight-sql://localhost:32010/?useEncryption=false`
3. VM options（`Help` → `Edit Custom VM Options`）に §2-3 と同じ `--add-opens` 一式を追記。

---

## 3.5. dvload（CSV/Excel 直接ローダー CLI）

ゲートウェイ本体の実行ファイルに同梱の `dvload` サブコマンドで、CSV/Excel を直接投入できます
（サーバー起動不要・稼働中ゲートウェイと併用可）。書き込み経路はゲートウェイと同一
（バルク/EM・チェックポイント・失敗行レポート・監査・本番ガード・ドライランを共有）。

```powershell
# CSV を INSERT
DataverseGW.exe dvload --file data.csv --table poc_gadget

# Excel(.xlsx) を UPSERT（代替キー）
DataverseGW.exe dvload --file data.xlsx --table poc_widget --mode upsert --key poc_externalid

# 事前ドライラン（実行せず件数・API・所要時間・必要特権を提示）
DataverseGW.exe dvload --file data.csv --table poc_gadget --dry-run
```

| 主なオプション | 説明 |
|---|---|
| `--file` / `--table` | 入力ファイル（.csv/.xlsx）/ 投入先テーブル（必須） |
| `--mode insert\|upsert` | 既定 insert。upsert は `--key <代替キー列>` か `--id <主キー列>` が必須 |
| `--batch N` / `--delimiter c` | バッチ行数（既定200）/ CSV 区切り文字（既定 `,`） |
| `--dry-run` | 実行せず事前レポートのみ |
| `--allow-production` | 本番と判定された接続先への書き込みを許可（本番ガード 3.1.2） |
| `--resolve-names` | 名前→ID 自動解決を有効化（3.1.4。既定 OFF・曖昧/未一致は行エラー） |
| `--bypass-plugins` / `--bypass-flows` / `--suppress-duplicate-detection` / `--no-bulk` / `--impersonate <guid>` / `--job-id <id>` | ヒント相当 |

> CSV は先頭行をヘッダ（＝Dataverse 論理名）とし、空セルは NULL 扱い。Lookup/Choice/Money/日付等は
> ヘッダ名の属性型に基づき自動変換（`entity:guid`・`key=value` 等の型ヒント記法は §5・本体と共通）。

---

## 4. Python (ADBC)

```bash
pip install adbc-driver-flightsql pyarrow
```

```python
import adbc_driver_flightsql.dbapi as flightsql

conn = flightsql.connect("grpc://localhost:32010")
cur = conn.cursor()

# 読み取り
cur.execute("SELECT TOP 5 accountid, name FROM account")
print(cur.fetchall())

# 書き込み(パラメータ化 INSERT)
cur.executemany(
    "INSERT INTO poc_widget (poc_externalid, poc_name, poc_quantity) VALUES (?, ?, ?)",
    [("EXT-001", "sample", 1)])
cur.close(); conn.close()
```

> **大容量の注意**: ADBC は 1 回の `executemany` を単一 gRPC メッセージで送るため、
> 既定 16MB を超えると `ResourceExhausted` になります。数十万行単位では
> クライアント側で 10 万行程度に分割して送ってください（DBeaver は既定で分割送信）。

---

## 5. 書き込みヒント（SQL コメント）

`INSERT`/`UPSERT` の直後に `/*+ ... */` でジョブ単位の挙動を制御できます。

| ヒント | 効果 |
|---|---|
| `BYPASS_PLUGINS` / `BYPASS_ASYNC_PLUGINS` / `BYPASS_FLOWS` | 同期/非同期プラグイン・Power Automate をバイパス |
| `SUPPRESS_DUPLICATE_DETECTION` | 重複検出を抑制 |
| `IMPERSONATE=<guid>`（別名 `AS_USER=`） | なりすまし（`createdby` 保持） |
| `JOB_ID=<任意>` / `FORCE_NEW_JOB` | チェックポイントのジョブ同定を上書き/毎回新規 |
| `NO_BULK`（別名 `FORCE_EXECUTE_MULTIPLE`） | バルクを使わず ExecuteMultiple 経路を強制 |
| `RESOLVE_NAMES` | 名前→ID 自動解決を有効化（既定 OFF。曖昧/未一致は行エラー。3.1.4） |

例: `INSERT /*+ BYPASS_PLUGINS JOB_ID=migration-2026Q3 */ INTO poc_widget (...) VALUES (...)`

### セッション設定（`SET <key> = ON/OFF` / `RESET`）
`SET` はセッション全体の既定を切り替えます（単一ユーザー sidecar のためプロセス=1セッション）。

| キー | 効果 |
|---|---|
| `DRY_RUN` | **実行前ドライラン**。ON の間、書き込みは実行されず「件数・経路・推定API要求数・推定所要時間・必要特権」レポートをログと `%LOCALAPPDATA%\DataverseGW\dryrun\` に出力。投入前の見積り確認に使う |
| `ALLOW_PRODUCTION_WRITES` | 本番と判定された接続先への書き込みを許可（本番ガード 3.1.2） |
| `RESOLVE_NAMES` | **名前→ID 自動解決**（3.1.4）。ON の間、Lookup 列の値が GUID/代替キーでない「名前」のとき対象テーブルのプライマリ名で ID 解決。既定 OFF。**同名複数（曖昧）・未一致はその行がエラー**（誤参照防止）。ポリモーフィックは `entity:名前`（例 `account:Contoso`）で対象を指定。事前辞書化後に作成された参照先はミス時の直接照会で解決（フォールバック） |
| `REFRESH_NAME_CACHE` | **名前→ID 辞書の明示リフレッシュ**（4.1.3）。`SET REFRESH_NAME_CACHE` で全エンティティ、`SET REFRESH_NAME_CACHE = <entity>` で対象のみ辞書を無効化し次回アクセスで再構築。改名/削除された参照先の反映に使う（TTL は `Gateway:NameResolve:CacheTtlMinutes` で自動失効も可） |
| `BYPASS_PLUGINS` / `BYPASS_FLOWS` / `SUPPRESS_DUPLICATE_DETECTION` / `NO_BULK` / `IMPERSONATE` / `JOB_ID` | 上表ヒントのセッション既定版 |

例（移行前の見積り）: `SET DRY_RUN = ON;` → 実データ投入 → ログ/レポートで件数・所要時間・特権を確認 → `SET DRY_RUN = OFF;` で本実行。

---

## 5.5. 進捗の確認と安全なキャンセル（3.1.5）

**進捗の照会**: 実行中/直近の書き込み操作の進捗を別接続から確認できます。

```sql
SELECT * FROM gateway_progress
-- 列: id, table, operation, rows_done, rows_total, rows_per_sec, elapsed_sec, eta_sec, percent, status
```

DBeaver 等でデータ転送しながら、別接続でこのクエリをポーリングすると進捗・スループット・残時間(ETA)を
確認できます（`rows_total` が既知なら `eta_sec`/`percent` を算出。ストリーム投入で総数不明なら null）。
dvload はコンソールに `n/N 行 (x%, r 行/秒, 残り約 s 秒)` を出力します。

**安全なキャンセル**: クライアント側で操作を停止（DBeaver の停止ボタン・接続切断）すると gRPC 経由で
サーバーに伝播し、バッチ境界で安全に停止します。**コミット済みバッチはチェックポイント(F-11)に記録済み**の
ため、同一データを再投入すると未完了分から自動再開します（重複なし）。進捗の状態は `cancelled` になります。

> 進捗の更新粒度は DoPut バッチ単位です。ADBC の `executemany` は 1 バッチで送るため完了時に一括更新、
> DBeaver のデータ転送や dvload はチャンク分割のため逐次更新されます。

---

## 6. 主な機能と対応 SQL

| 操作 | SQL 例 |
|---|---|
| 読み取り | `SELECT ... FROM <table>`（TDS 経由・不可時は FetchXML 自動切替） |
| 追加 | `INSERT INTO <table> (...) VALUES (?, ...)` |
| 追加/更新 | `UPSERT INTO <table> (...) VALUES (...) ON KEY (<altkey>)` または主キー列指定 |
| N:N 関連付け | `INSERT INTO <relationship> (record1id, record2id) VALUES (?, ?)` |
| 削除 | `DELETE FROM <table> [WHERE ...]`（4.2.3 で経路自動選択。下記） |
| 全削除 | `TRUNCATE TABLE <table>`（同上・全行） |
| テーブル定義（DDL） | `CREATE TABLE <prefix_name> (<col> <type> [PRIMARY], ...)` / `ALTER TABLE <t> ADD <col> <type>` / `DROP TABLE <t>`（4.2.1） |

### DDL サブセット（4.2.1・移行前スキーマ準備）
Dataverse のメタデータ API で**カスタムテーブル/列を定義**する。論理名には**パブリッシャー接頭辞**（例 `poc_`）が必須。

```sql
CREATE TABLE poc_widget2 (poc_name string PRIMARY, poc_qty int, poc_price decimal(10,2));
ALTER TABLE poc_widget2 ADD poc_note nvarchar(100);
DROP TABLE poc_widget2;
```
- **主名属性**: `PRIMARY` 指定列（無ければ先頭の文字列列）。文字列型必須。
- **対応型**: `string`/`nvarchar(n)`/`varchar(n)`（String）・`int`・`bigint`・`decimal`/`numeric`（Decimal）・`money`・`float`/`double`（Double）・`datetime`/`date`・`bit`/`bool`（Boolean）。
- **注意**: `DROP TABLE` はテーブルとデータを削除（本番ガード適用）。`ALTER ... DROP COLUMN` は安全側で非対応。作成/変更は発行後すぐ利用可（自動発行）。

### 削除の経路自動選択（4.2.3）
`DELETE`/`TRUNCATE` は対象テーブル種別と件数に応じて最適な経路を自動選択する（利用者の指定は不要）。

| 条件 | 経路 | 特性 |
|---|---|---|
| エラスティックテーブル | `DeleteMultiple`（同期・即時） | Cosmos バッキング。カスケード無しで即時削除。100 件/リクエスト |
| 標準テーブルで対象 ≤ `Gateway:SyncDeleteMaxRows`（既定 5000） | `ExecuteMultiple`（同期・即時） | 非同期ジョブの生成・ポーリング無しで即時に削除件数を返す（少量削除が高速） |
| 標準テーブルで対象が上限超 | `BulkDelete`（非同期ジョブ） | 従来経路。`Gateway:BulkDeleteWaitMinutes` で完了待ち上限（既定 60 分） |

> `DeleteMultiple` は**エラスティックテーブル専用**（標準テーブルは Microsoft 仕様上非対応）。標準テーブルの高速化は同期 `ExecuteMultiple` 経路で行う。
> `SET DRY_RUN = ON` 時は選択される経路・推定 API 要求数・所要時間をレポートに出力する。
>
> 検証用にエラスティックテーブルを作成するには `DataverseGW.exe setup-elastic`（`poc_sensordata` を冪等作成）。
> ※ エラスティックテーブルの作成には `CanCreateCharts=false` の指定が必要（未指定は作成時にチャート生成でエラー）。

---

## 7. 主な設定（環境変数 `Gateway__<Key>` または `--Gateway:<Key>`）

| キー | 既定 | 説明 |
|---|---|---|
| `Gateway:Port` | 32010 | 待ち受けポート |
| `Gateway:BatchSize` | 200 | CreateMultiple 1 回あたりの行数 |
| `Gateway:Dop` | 環境推奨並列度 | 並列度（未設定時は推奨値に追従） |
| `Gateway:UseTdsEndpoint` | auto | 読み取り経路（auto/true=TDS/false=FetchXML） |
| `Gateway:BulkDeleteWaitMinutes` | 60 | BulkDelete 経路の完了待ち上限（0以下=発行のみ） |
| `Gateway:SyncDeleteMaxRows` | 5000 | 標準テーブルでこの件数以下の DELETE/TRUNCATE を同期 ExecuteMultiple で即時削除（超過は BulkDelete）。0以下=常に BulkDelete。上限 5000（4.2.3） |
| `Gateway:IsProduction` | 自動(安全側) | 本番判定の明示上書き。**未設定時は fail-closed**: リモート Dataverse(`*.dynamics.com`)は dev/prod を URL で区別できないため本番扱い(書き込みブロック)。ホスト名に prod/prd/production を含む場合も本番。**dev で書き込むには `false` を明示**(または `AllowProductionWrites=true`)。localhost 等は非本番既定 |
| `Gateway:AllowProductionWrites` | false | 本番と判定された接続先への書き込みを起動時に許可（既定は書き込みブロック＝安全側） |
| `Gateway:RequireToken` | false | プロセストークン必須化。**ネットワーク公開時は `true` 必須**（未設定なら起動拒否） |
| `Gateway:IdleShutdownMinutes` | — | アイドル自動終了 |
| `Gateway:NameResolve:CacheTtlMinutes` | 0 | 名前→ID 辞書の TTL（分）。0=失効なし。>0 で長時間セッションの陳腐化（改名/削除）に自動対応（4.1.3） |
| `Gateway:CheckpointDb` | %LOCALAPPDATA%\… | チェックポイント DB のパス |
| `Gateway:Bind` | localhost | バインド先。`localhost`/`any`/`<IP>`。非 localhost はネットワーク公開（**TLS＋`RequireToken=true` 必須。未設定なら起動拒否**。4.2.2c） |
| `Gateway:Tls:CertPath` / `Gateway:Tls:CertPassword` | — | TLS 証明書（PFX）のパスとパスワード（4.2.2c） |
| `Gateway:Tls:Thumbprint` | — | TLS 証明書を証明書ストア（LocalMachine\My）から拇印で指定（CertPath 未指定時。4.2.2c） |
| `Gateway:ScaleOut:Principals` | — | 書き込み分散する追加アプリユーザー（SP）の配列（`ClientId`/`ClientSecret`/`CertThumbprint`/`TenantId`/`Auth`）。接続先は `DATAVERSE_URL` と共有。ClientSecret/Certificate のみ（4.2.4。§7.5） |
| `Gateway:EnableDetailedErrors` | false | gRPC 詳細エラーの外部返却。**既定 OFF**（例外に接続文字列断片・secret が混入し得るため。M-4）。**開発時のみ `true`**。本番では無効のまま運用する |
| `Gateway:FailureReportIncludeMessage` | false | 失敗行 CSV の `error` 列にエラーメッセージ全文を出力するか。**既定 OFF**（全文は違反フィールドの実値＝PII を含み得るため、エラーコードのみ記録。M-2）。原因列は別列（`column`）に出る。全文が要るときだけ `true` |
| `Gateway:ReportRetentionDays` | 0 | 失敗行 CSV（`failed_*.csv`）の保持日数。**0＝無制限（掃除しない）**。>0 で、レポート書き込み時に最終更新が当日数を超えた古い失敗行 CSV を自動削除（M-2: 無期限残置の防止） |
| `Gateway:MaxInputFileMB` | 0 | dvload の入力ファイル（CSV/xlsx）サイズ上限（MB）。**0＝無制限**。>0 で、読み込み前にサイズを検査し超過分は明示エラー（M-3: 巨大ファイル全読み込みによる OOM を、クラッシュではなく実行前の分かりやすい失敗に変える）。**xlsx は展開後（非圧縮）サイズも同上限で検査**し、小さな .xlsx が展開後に膨張する「展開ボム」も遮断。信頼できない入力を扱う配置では設定推奨 |

---

## 7.5. サーバー配置（常駐サービス・複数ユーザー。4.2.2）

ローカルサイドカーに加え、**Windows サービスとして常駐**させ、複数クライアントから利用できる。

- **サービス化（4.2.2a）**: `dist/install-service.ps1` で登録/削除（管理者権限）。
  ```powershell
  powershell -ExecutionPolicy Bypass -File dist\install-service.ps1 -Action install -ExePath "C:\path\DataverseGW.exe"
  Start-Service DataverseGW
  ```
  サービスはユーザー環境変数を継承しないため、認証情報（`DATAVERSE_URL`/`CLIENT_ID`/`TENANT_ID`/`CLIENT_SECRET`・
  `DATAVERSE_AUTH`）は**システム環境変数**に設定する（コミット/共有厳禁）。
- **セッション分離（4.2.2b）**: `SET DRY_RUN`/バイパス既定/名前解決/本番書き込み許可は**接続（クライアント）ごとに独立**。
  1 クライアントの `SET` が他クライアントに漏れない。全書き込みは常駐サービスの認証 ID（例 SP）で実行される（共有 ID モデル）。
- **ネットワーク公開（4.2.2c）**: `Gateway:Bind=any`（または `<IP>`）で非 localhost バインド。
  資格情報・データが流れるため **TLS 必須**（`Gateway:Tls:CertPath`＋`Gateway:Tls:CertPassword`、または `Gateway:Tls:Thumbprint`）
  かつ **`Gateway:RequireToken=true` 必須**。
  TLS 有効時はクライアントは `grpc+tls://host:port`（JDBC は `useEncryption=true`）で接続し、証明書（自己署名なら内部 CA）を信頼する。
  **セキュリティ（fail-closed）**: ネットワーク公開で TLS 未設定、またはプロセストークン未強制の場合は、平文・無認証を許さず**起動を拒否する**（localhost バインドはループバックのため従来どおり TLS/トークン任意）。
- **複数アプリユーザーによるスケールアウト（4.2.4）**: 書き込みを**複数のアプリケーションユーザー（SP）に分散**して
  サービス保護制限（throttle）予算を合算し、書き込みスループットを向上させる。各 SP は個別の throttle 予算を持つため、
  N SP で実効並列度が概ね N 倍（`dop × SP数`）になる。設定 `Gateway:ScaleOut:Principals`（配列）に追加 SP の認証情報を与える。
  接続先 URL は起動時 `DATAVERSE_URL` と共有。対話型/デバイスコードは非対応（`ClientSecret`/`Certificate` のみ）。

```
Gateway__ScaleOut__Principals__0__ClientId=<SP1 の Client ID>
Gateway__ScaleOut__Principals__0__ClientSecret=<SP1 のシークレット>   # コミット/共有厳禁
Gateway__ScaleOut__Principals__0__TenantId=<テナント GUID>
Gateway__ScaleOut__Principals__1__ClientId=<SP2 の Client ID>
Gateway__ScaleOut__Principals__1__Auth=Certificate
Gateway__ScaleOut__Principals__1__CertThumbprint=<SP2 証明書拇印>
Gateway__ScaleOut__Principals__1__TenantId=<テナント GUID>
```

  > 各 SP には対象テーブルへの書き込み権限（セキュリティロール）が必要。ワークキュー方式で、throttle された SP の
  > worker は自然に処理数が減り、余裕のある SP が多く処理する（遅い SP に律速されない）。**制限回避の偽装ではなく、
  > 正規の複数アプリケーションユーザーによる水平分散**（要件 §準拠）。SP の認証失敗時はその SP をスキップし主接続で継続。
- **Linux コンテナ配置（4.2.5）**: `dist/Dockerfile` で Linux コンテナとして配置できる（コード変更不要）。
  認証は ClientSecret/Certificate のみ、TLS は `Gateway:Tls:CertPath`（PFX）、永続データは `/data` ボリューム。
  詳細は [doc/コンテナ配置-4.2.5.md](コンテナ配置-4.2.5.md)。

---

## 7.6. 接続先のクライアント供給（複数 Dataverse 環境の同時利用。4.2.2c B2）

1 台のゲートウェイに対し、**接続ごとに接続先（Dataverse 環境）をクライアントが指定**できる（方式 B2）。
これにより DBeaver / DataGrip で**環境ごとに別々の接続を作成し、切り替えながら同時利用**できる。
環境が増えても、ゲートウェイ側に環境変数を増やす必要はない（接続先は各クライアント接続が保持する）。

- **仕組み**: クライアントは gRPC ヘッダで接続先・認証を供給する。ゲートウェイは接続パラメータ（URL＋認証）
  ごとに接続コンテキストを生成・キャッシュして環境間を分離する。
- **起動時 `DATAVERSE_URL`**: 設定していればそれが**既定コンテキスト**（ヘッダ無し接続のフォールバック）。
  未設定なら純 B2 モードとなり、ヘッダ無し接続は `FailedPrecondition`（接続先未指定）で拒否される。
- **対応認証**: 共有サーバーはブラウザ対話ができないため **`ClientSecret` / `Certificate` のみ**。
  `Interactive` / `DeviceCode` はヘッダ指定不可（起動時の既定コンテキストでのみ利用可）。
- **セキュリティ**: `ClientSecret` をヘッダで送るため、**ネットワーク公開時は §7.5 の TLS 必須**
  （localhost 内のみ平文可）。シークレットはクライアント設定に保存され、リポジトリ/共有に置かない。

### 供給ヘッダ

| ヘッダ | 必須 | 説明 |
|---|---|---|
| `x-dataverse-url` | ○ | 接続先 `https://<org>.crm7.dynamics.com/` |
| `x-dataverse-auth` | — | `ClientSecret`（既定）/ `Certificate` |
| `x-dataverse-tenant-id` | ○ | テナント GUID |
| `x-dataverse-client-id` | ○ | アプリ登録 Client ID |
| `x-dataverse-client-secret` | ClientSecret 時 | シークレット（**コミット/共有厳禁**・要 TLS） |
| `x-dataverse-cert-thumbprint` | Certificate 時 | 証明書ストア（CurrentUser/LocalMachine\My）拇印 |

### Python (ADBC)
ADBC は `adbc.flight.sql.rpc.call_header.<name>` オプションで gRPC ヘッダを供給する。

```python
import adbc_driver_flightsql.dbapi as flightsql
H = "adbc.flight.sql.rpc.call_header."
conn = flightsql.connect("grpc://localhost:32010", db_kwargs={
    H + "x-dataverse-url": "https://org1.crm7.dynamics.com/",
    H + "x-dataverse-auth": "ClientSecret",
    H + "x-dataverse-tenant-id": "<tenant-guid>",
    H + "x-dataverse-client-id": "<client-id>",
    H + "x-dataverse-client-secret": "<secret>",   # TLS 前提。平文に置かない
})
```

### DBeaver / DataGrip（JDBC）
Arrow Flight SQL JDBC ドライバは、予約外の接続プロパティを gRPC 呼び出しヘッダとして送出する。
各接続の「ドライバのプロパティ」に上表のヘッダ名（`x-dataverse-url` 等）をプロパティとして追加する。
**接続ごとに別々の接続先を設定**でき、DBeaver の接続一覧で環境を切り替えながら同時利用できる。
（ネットワーク公開時は `useEncryption=true`＋TLS。シークレットは各接続の資格情報として保存し共有しない。）

---

## 8. トラブルシューティング

| 症状 | 対処 |
|---|---|
| DBeaver で `UnsupportedOperationException`（netty） | §2-3 の `--add-opens` を `dbeaver.ini` に追加 |
| ADBC で `ResourceExhausted`（16MB 超） | `executemany` を 10 万行程度に分割 |
| 起動直後に終了・`DATAVERSE_URL 未設定` | §1.2 の環境変数を設定 |
| `FailedPrecondition`「接続先が指定されていません」 | 純 B2 モード（起動時 `DATAVERSE_URL` 未設定）で接続ヘッダ無し。§7.6 の `x-dataverse-*` ヘッダを供給するか、起動時 `DATAVERSE_URL` を設定 |
| B2 で `対話型/デバイスコード認証は非対応` | ヘッダ供給接続は `ClientSecret`/`Certificate` のみ（§7.6）。`x-dataverse-auth` を修正 |
| 書き込みで一部行が失敗 | `%LOCALAPPDATA%\DataverseGW\reports\failed_*.csv`（行番号・列・エラー）を確認 |
| 書き込みが「本番環境ガード」でブロックされる | 接続先が本番と判定されています（fail-closed 既定）。**`*.dynamics.com` は明示設定が無いと dev 組織でも本番扱いでブロック**されます。dev なら接続プロファイル/環境に `Gateway:IsProduction=false` を設定。意図した本番書き込みなら `SET ALLOW_PRODUCTION_WRITES = ON`、恒常許可は起動時 `Gateway:AllowProductionWrites=true` |
| 実施状況の確認 | `%LOCALAPPDATA%\DataverseGW\audit\` の CSV / `job_*.md` |
