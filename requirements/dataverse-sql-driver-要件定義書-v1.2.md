# Microsoft Dataverse SQLドライバー(JDBC/ODBC)要件定義書

| 項目 | 内容 |
|---|---|
| 文書バージョン | **1.2(Phase 0 実測反映版)** |
| 作成日 | 2026-07-06 |
| 改訂履歴 | 0.9: 初版 → 1.0: ヒアリング反映 → 1.1: 未解決課題クローズ → **1.2: Phase 0 PoC 実測結果を反映(R-8 Go・性能達成・R-1 再評価・Q-2 クローズ)** |
| 対象読者 | アーキテクト、開発リーダー、PM |
| ステータス | Phase 0 完了・Phase 1 計画のベースライン |
| 根拠 | Phase 0 PoC の実測結果・観測記録 OBS-1〜9(本リポジトリには含めない) |

> 本版は承認済み v1.1 に Phase 0 の実測結果を反映した更新版。変更箇所は「【v1.2】」で明示する。

---

## 0. Phase 0 実測サマリ(v1.2 で追加)

| ゲート/項目 | 結果 | 影響 |
|---|---|---|
| R-8: Flight SQL バッチ集約 | **Go**(JDBC/ADBC とも 1 バッチ集約→バルク実行) | 方式B継続。No-Go 回帰不要 |
| 書き込み性能 | **P0 達成**(開発環境 dop=32 で 581 rows/sec、429 なし) | 性能目標は現実的 |
| Q-2: modifiedon/modifiedby 保持 | **不可**(現在時刻・実行者で上書き) | R-2 確定。制約事項として明記 |
| バルク対応範囲(R-1) | 標準テーブルも Create/Update 対応。Upsert は業務テーブル非対応 | 経路設計・F-05 を更新 |
| 冪等化(F-05/D-14) | GUID/代替キー Upsert を EM+UpsertRequest で実証 | F-11 の土台確認 |

---

## 1. 背景・目的

### 1.1 問題提起

Microsoft Dataverse は Web API(OData v4)/ SDK(.NET)/ TDSエンドポイントという独自のアクセス手段を持つが、DBeaver・DataGrip・Python 等の汎用SQLクライアントから直接CRUD操作を行う標準的な手段が存在しない。特に**実案件でのデータ移行・大量データメンテナンス**において、SQLベースのワークフロー(DBeaverのデータ転送機能、Pythonスクリプト)から Dataverse へ百万行オーダーの書き込みを行いたいという需要が強い。

### 1.2 目的

データ移行・一括更新を行う作業者向けのツールとして、汎用SQLクライアントから Dataverse に対して **INSERT / UPDATE / UPSERT / DELETE / SELECT** を実行可能な高性能ドライバーを実装する。**書き込み(データ移行・一括更新)パスを最優先**とし、百万行オーダーのバルク処理を実用的な時間で、かつ中断からの自動再開付きで完了できることを性能・信頼性目標の中心に置く。

### 1.3 ヒアリング確定事項(2026-07-06)

| # | 論点 | 決定事項 | 設計への影響 |
|---|---|---|---|
| D-1 | 提供形態 | 非商用のツール | MITライセンスのSql4Cds.Engine採用可。商用配布要件(EV署名・自動更新等)は緩和 |
| D-2 | 最優先ユースケース | 大量データ移行・一括更新(書き込み中心) | 開発リソースを書き込みパスに傾斜。読み取りはTDSパススルー中心の簡素な実装 |
| D-3 | 必須クライアント | DBeaver / DataGrip(JDBC)、Python | JDBC(Flight SQL)がP0。PythonはADBC(Flight SQL)を第一推奨、pyodbc互換はFlight SQL ODBCで担保 |
| D-4 | 対象テーブル | カスタム+Account/Contact等コア+アクティビティ。エラスティックは対象外 | 【v1.2】標準テーブルもバルク対応と判明(OBS-3)。ExecuteMultiple並列は活動系向けに再定義。activityparty対応が必要 |
| D-5 | 実行主体(認証) | サービスプリンシパル・対話型の両方 | MSALの両フロー実装。監査列の実行者表示に注意喚起機能 |
| D-6 | プラグイン/フローバイパス | ジョブ/テーブルごとに選択 | セッションオプション+テーブル単位オーバーライドの2層設計 |
| D-7 | Lookup解決 | GUID直投入・代替キー・名前→ID自動解決・N:N関連付けすべて必要 | 代替キーUpsertがP0。名前解決は曖昧性制御付きでP1。N:N用SQL構文の設計が必要 |
| D-8 | 監査列 | 作成日時(OverriddenCreatedOn)・作成者/更新者・所有者(ownerid)の保持/割当が必要 | 【v1.2】createdon 保持は実証。**modifiedon/by は保持不可を確定(Q-2/OBS-9)**。createdby は要追加検証 |
| D-9 | 実行環境 | 担当者のローカルPC | ゲートウェイはローカルサイドカー構成。スリープ/ネットワーク断対策とチェックポイントが必須 |
| D-10 | 削除要件 | 百万行オーダーの一括削除+全行入替(TRUNCATE相当)あり | DELETE→BulkDelete非同期ジョブ変換を採用(サーバーサイド実行でクライアント律速を回避) |
| D-11 | リカバリー | チェックポイントからの自動再開が**必須** | ローカル状態ストア(SQLite)によるバッチ単位コミット記録をP0に昇格 |
| D-12 | データ供給元 | CSV/Excelファイル、SQL Server等の他DB(DBeaverデータ転送) | JDBCバッチAPI(addBatch/executeBatch)の完全対応がP0。CSV直接ローダーCLIはP1 |
| D-13 | N:N構文(旧Q-1) | INSERT構文とASSOCIATE文の両方を提供 | ファイル転送はINSERT(record1id/record2id)、明示操作はASSOCIATE文。両者を同一のAssociate/Disassociateに変換 |
| D-14 | 代替キー未定義テーブル(旧Q-3) | 代替キー無しでも動く代替手段が必要 | 【v1.2】GUID指定UpsertをEM+UpsertRequestで実証(OBS-8)。ローカル追跡キーはPhase 1 |
| D-15 | ジョブ同定(旧Q-4) | ハイブリッド(入力ハッシュ自動同定+明示ジョブIDによる手動上書き) | 既定は自動同定。`SET JOB_ID = '...'` で上書き可能。`SET FORCE_NEW_JOB = TRUE` を提供 |
| D-16 | TDS無効時の読み取り(旧Q-5) | TDS/FetchXML両対応。ただしFetchXML経路の性能改善は非スコープ | TDS無効環境ではFetchXMLで機能継続。性能最適化は行わず「動作すること」を保証する水準に留める |

### 1.4 技術的前提(調査結果の要約)

1. **DBeaver は JDBC ネイティブ**。ODBC単体では主目的を達成できないため、JDBCを第一ターゲットとする。
2. **TDSエンドポイントは読み取り専用**(SQL Server互換のT-SQLサブセット、Entra ID認証のみ、ポート1433/5558)。変更系は一切不可。SELECT高速パスとしてのみ利用する。
3. **書き込み最高スループットはバルク操作メッセージ**(CreateMultiple/UpdateMultiple/UpsertMultiple)。【v1.2】**Phase 0 で account/contact 等の標準テーブルも CreateMultiple/UpdateMultiple 対応と判明(OBS-3)**。ただし対応可否はテーブル×メッセージごとに異なるため、メタデータ事前判定が必要。バルクメッセージは1件のエラーで要求全体が失敗する。
4. **サービス保護制限**(目安: ユーザー×Webサーバーあたり6,000リクエスト/5分、実行時間累計20分、同時実行52)への適応制御(429リトライ・`x-ms-dop-hint`・アフィニティCookie無効化)が百万行処理の成否を決める。【v1.2】並列化には **ServiceClient のクローン**が必須(単一インスタンスは同期 Execute が直列化。OBS-7)。
5. **大量削除はBulkDeleteRequest(サーバーサイド非同期ジョブ)** が最適。クライアントからは条件(FetchXML)を送るだけで、削除自体はDataverse側で進行する。

---

## 2. ゴール / 非ゴール

### 2.1 ゴール

1. DBeaver のデータ転送機能および Python スクリプトから、百万行オーダーのINSERT/UPSERTが**追加コーディングなし**で実行できる。【v1.2】Phase 0 で JDBC/ADBC とも executeBatch がバルクメッセージへ集約されることを実証(R-8 Go)。
2. バルクメッセージ対応テーブルへの100万行INSERTを**60分以内(目標30分)**、非対応テーブル(活動系等)でも**3時間以内(目標90分)**に完了。【v1.2】開発環境で dop=32 時 581 rows/sec を実測、P0 目標の実現性を確認。
3. 百万行オーダーのDELETE/全行入替が、クライアントを占有せずBulkDeleteジョブとして実行・進捗監視できる。
4. ローカルPCのスリープ・ネットワーク断・アプリ強制終了が発生しても、**再実行時にチェックポイントから自動再開**し、重複・欠損なく完走できる。【v1.2】GUID/代替キー Upsert の冪等性を実証(OBS-8)。
5. 移行特有の要件(代替キーUpsert、監査列保持、所有者割当、プラグイン選択的バイパス)がSQLとセッション設定だけで完結する。【v1.2】ただし modifiedon/modifiedby の保持は不可(§7 R-2)。

### 2.2 非ゴール

- **T-SQL完全互換・DDL**: 将来P2で検討。
- **エラスティックテーブル対応**: 除外(D-4)。
- **BI/レポーティング向けの読み取り最適化**: TDSパススルーで十分(D-2)。
- **分散トランザクション**: バッチ単位の原子性のみ。
- **サーバー常駐/マルチユーザーゲートウェイ**: ローカルPC実行が前提(D-9)。
- **商用製品としての配布体制**: 非商用のため簡素化(D-1)。
- 【v1.2】**modifiedon/modifiedby の移行元値保持**: プラットフォーム制約により不可(Q-2/OBS-9)。制約事項として利用者に明示する。

---

## 3. アーキテクチャ

### 3.1 全体構成(ローカルサイドカー型)

```
┌────────────────────────────────────────────────┐
│ 担当者のローカルPC                              │
│ ┌──────────────┐  ┌──────────────────────────┐ │
│ │ DBeaver /    │  │ Python                   │ │
│ │ DataGrip     │  │  - ADBC (Flight SQL) 推奨│ │
│ │  Flight SQL  │  │  - pyodbc (Flight SQL    │ │
│ │  JDBCドライバ│  │    ODBC) 互換路          │ │
│ └──────┬───────┘  └──────────┬───────────────┘ │
│        │ gRPC (Arrow Flight SQL, localhost)    │
│ ┌──────▼──────────────────────▼──────────────┐ │
│ │ コアゲートウェイ (.NET 8 常駐プロセス)      │ │
│ │  1. SQLパーサー (Sql4Cds.Engineベース)      │ │
│ │  2. 認証 (MSAL: SP / 対話型 / トークンキャッシュ)│ │
│ │  3. 読取: TDSパススルー / FetchXMLフォールバック│ │
│ │  4. 書込: xMultiple / ExecuteMultiple並列   │ │
│ │  5. 削除: BulkDeleteジョブ変換+進捗監視    │ │
│ │  6. スロットリング適応制御                  │ │
│ │  7. チェックポイントストア (SQLite)         │ │
│ │  8. メタデータ/代替キー/名前解決キャッシュ  │ │
│ └──────┬─────────────────────────────────────┘ │
└────────┼───────────────────────────────────────┘
         │ TDS(1433/5558) + HTTPS(Web API/OData)
┌────────▼───────────────────────────────────────┐
│ Microsoft Dataverse(移行先環境)               │
└────────────────────────────────────────────────┘
```

採用理由: (1) Dataverse SDK の接続プール・リトライ・認証をC#で直接利用、(2) Arrow Flight SQL の既製ドライバーでクライアント側開発がほぼ不要、(3) SQL→FetchXML/TDS 変換に OSS の MarkMpn.Sql4Cds.Engine(MIT) を活用。【v1.2】Phase 0 でこの 3 点をいずれも実証。

### 3.2 書き込みパス(最重要)

**経路選択【v1.2 更新: メタデータ事前判定】:**

テーブル×メッセージ(Create/Update/Upsert)Multiple の対応可否を、`sdkmessagefilter`(primaryobjecttypecode)＋`entity`(OTC↔論理名) のメタデータで**事前判定してキャッシュ**する(実行時エラー任せにしない。Microsoft 公式 `IsMessageAvailable` と同一手法。OBS-3)。

| 対象 | 第一経路 | フォールバック |
|---|---|---|
| CreateMultiple/UpdateMultiple 対応テーブル(カスタム＋account/contact/task 等) | CreateMultiple / UpdateMultiple(バッチ100〜1,000自動調整) | ExecuteMultiple 並列 |
| バルク非対応テーブル(phonecall/email/appointment/annotation 等) | ExecuteMultiple 並列(バッチ100〜300) | 単件リクエスト並列 |
| アクティビティ | ExecuteMultiple 並列 + activityparty構築 | 同上 |
| **Upsert(全業務テーブル)** | **ExecuteMultiple + UpsertRequest**(UpsertMultiple は業務テーブル非対応。OBS-3/OBS-8) | 単件 UpsertRequest 並列 |

**並列・スロットリング制御【v1.2 更新】:**
- **ServiceClient クローンプール**でスレッドごとに独立接続(単一インスタンスは直列化。OBS-7)
- `x-ms-dop-hint`(`RecommendedDegreesOfParallelism`)に基づく並列度追従
- アフィニティCookie無効化(`EnableAffinityCookie=false`)によるWebサーバー分散
- 429受信時は `Retry-After` を尊重した待機+バッチサイズの動的縮小
- バルクメッセージ失敗時は二分探索リトライ→単件フォールバックで原因行を特定し、行番号付きエラーレポートを出力

**移行特有機能:**
- **代替キーUpsert / GUID Upsert【v1.2 更新】**: `UPSERT INTO t (...) VALUES (...) ON KEY <代替キー>` および GUID 指定 Upsert を、**`UpsertRequest` を `ExecuteMultiple` でバッチ**して実行(UpsertMultiple 非対応のため)。Phase 0 で冪等性(再投入で重複なし)を実証(OBS-8)。
- **N:N関連付け(D-13)**: INSERT構文とASSOCIATE/DISASSOCIATE文を同一の `AssociateRequest`/`DisassociateRequest` に変換。
- **Lookup解決の3方式**: ①GUID直接 ②代替キー参照 ③名前→ID自動解決(P1)。
- **監査列【v1.2 更新】**: `createdon`→`OverriddenCreatedOn` は保持可能(実証済み)。`createdby` は要 Phase 1 検証(属性設定では実行者になる。CallerId 等を調査)。**`modifiedon`/`modifiedby` は保持不可を確定(Q-2/OBS-9)。制約事項として利用者に明示する**。要 `prvOverrideCreatedOnCreatedBy` 特権を接続時に検査。
- **バイパス制御の2層設計**: セッション既定値＋テーブル/ステートメント単位ヒント。既定はバイパス**しない**。

### 3.3 削除パス

- `DELETE FROM t WHERE ...` のうち推定件数が閾値(既定10,000行)超は WHERE句をFetchXMLに変換し **BulkDeleteRequest** を発行、ジョブIDを返却。仮想テーブルとブロッキング待機(`SET DELETE_WAIT = TRUE`)で進捗監視。
- `TRUNCATE TABLE t` は「全行BulkDelete+完了待ち」の糖衣構文。
- 閾値以下は ExecuteMultiple(DeleteRequest)並列で同期実行。

### 3.4 チェックポイント・自動再開(P0)

- ジョブ開始時にジョブIDを採番し、SQLite ローカルストアへ「入力シーケンス×バッチ単位のコミット記録」を書き込む(WAL、fsync)。
- **ジョブ同定はハイブリッド(D-15)**: 既定は入力指紋による自動同定、`SET JOB_ID` で明示上書き、`SET FORCE_NEW_JOB` で意図的再投入。
- **再開時の重複防止(D-14)【v1.2: ①②実証済み】**:
  1. 代替キーあり → `UpsertRequest`(代替キー)へ昇格。**Phase 0 で実証(OBS-8)**
  2. 代替キー無し・主キーGUIDが入力にある → GUID指定 `UpsertRequest`。**Phase 0 で実証(OBS-8)**
  3. どちらも無い → ローカル追跡キー方式(入力行ハッシュ↔発番GUID をSQLiteに記録)。**Phase 1 で実装**
- ローカルPC対策: OSスリープ抑止、ネットワーク断の指数バックオフ再試行(最大30分)。

### 3.5 読み取りパス(簡素化方針)

1. TDS表現可能なSELECTは `Microsoft.Data.SqlClient` + Entra IDトークンでパススルー。【v1.2】Phase 0 で SELECT の実行と Arrow ストリーミング返却を実証。
2. TDS未サポートは FetchXML ページングへ自動フォールバック(**動作保証水準・性能最適化は非スコープ。D-16。Phase 0 未検証→Phase 1**)。
3. 結果は Arrow RecordBatch でストリーミング返却。

---

## 4. 技術スタック

| レイヤー | 技術 | 備考 |
|---|---|---|
| コアゲートウェイ | .NET 8 (C#)、単一ファイル発行 | Dataverse SDKが第一級。ローカル常駐 |
| SQLパーサー | MarkMpn.Sql4Cds.Engine(MIT)+独自拡張(UPSERT/TRUNCATE/N:N構文、ヒント句) | 依存+拡張レイヤー |
| Dataverse接続 | Microsoft.PowerPlatform.Dataverse.Client(ServiceClient) | 【v1.2】並列は `Clone()`、`EnableAffinityCookie=false` |
| 認証 | MSAL.NET(対話型/デバイスコード/クライアントシークレット・証明書) | トークンはOSクレデンシャルストアへ |
| TDS読み取り | Microsoft.Data.SqlClient + AccessToken | |
| 転送プロトコル | Apache Arrow Flight SQL(gRPC、localhostのみバインド) | |
| JDBC | Apache Arrow Flight SQLドライバー(既製) | 【v1.2】JDK16+ では JVM フラグ要(`--add-opens java.base/java.nio` 等。DBeaver 同梱JREなら不要。OBS-4) |
| Python | ADBC Flight SQL / 互換路: Flight SQL ODBC + pyodbc | |
| 状態ストア | SQLite(WAL) | チェックポイント・ジョブ履歴・名前解決キャッシュ |
| 可観測性 | Serilog + OpenTelemetry | スループット・429率・並列度・経路の記録 |
| テスト | xUnit、Dataverse開発者環境E2E、NBomber負荷試験、WireMock | |
| 配布 | GitHub Releases、署名付きMSI、macOS/Linuxはtar.gz | 非商用水準(D-1) |

---

## 5. 機能要件

### 5.1 Must-Have (P0)

| ID | 要件 | 受け入れ基準(抜粋) | Phase 0 |
|---|---|---|---|
| F-01 | Entra ID認証(SP+対話型) | 両方式で接続成功。トークン自動更新。対話型時に監査列実行者を表示 | SP 接続実証 |
| F-02 | JDBCバッチAPI完全対応 | DBeaverデータ転送で100万行が追加設定なしに完走。executeBatch がバルクメッセージへ集約 | 集約を実証(OBS-5) |
| F-03 | バルクメッセージ書き込み | 100万行INSERT ≤ 60分。対応可否のメタデータ事前判定とキャッシュ | 実証・性能達成 |
| F-04 | ExecuteMultipleフォールバック(活動系等) | 100万行INSERT ≤ 3時間。経路がログで確認できる | 経路実装済み |
| F-05 | 代替キーUpsert＋GUID Upsert【v1.2: EM+UpsertRequest】 | 新規/更新混在で正しく分岐。代替キー未定義でもGUID指定で冪等な再投入 | 冪等性実証(OBS-8) |
| F-06 | Lookup解決(GUID直接+代替キー参照) | 参照列への代替キー指定INSERTが成功。解決失敗行はレポート | Phase 1 |
| F-07 | N:N関連付け(INSERT構文+ASSOCIATE文) | 100万関連付けが完走 | Phase 1 |
| F-08 | 監査列・所有者【v1.2】 | createdon(OverriddenCreatedOn)投入成功。**modifiedon/by は非対象(保持不可)**。特権不足は実行前エラー | createdon 実証・modifiedon/by 不可確定 |
| F-09 | バイパス制御(2層) | 同期プラグイン・Flowトリガー・重複検出を個別制御。既定は非バイパス | Phase 1 |
| F-10 | 大量削除+TRUNCATE糖衣構文 | 100万行DELETEがジョブ発行≤30秒。全行入替がSQLのみで完結 | Phase 1 |
| F-11 | チェックポイント自動再開 | 50万行中断→再実行で残りのみ処理、最終件数一致。重複ゼロ | ①②冪等化を実証 |
| F-12 | スロットリング適応【v1.2】 | 429下でも失敗せず並列度・バッチサイズ自動調整。**クローンプール＋dop-hint 追従** | dop=32 まで429なし確認 |
| F-13 | メタデータ提供・型マッピング | ナビゲーターでテーブル/列/PK/代替キー表示。各型がロスレス往復 | 最小実装(GetTables 等)を実証 |
| F-14 | 失敗行レポート | 失敗行の行番号・列・エラーをCSV出力。二分探索で原因行特定 | Phase 1 |
| F-15 | 読み取り(TDS+FetchXMLフォールバック) | 移行検証用SELECT/COUNT/突合がSSMSと同一結果 | TDS SELECT 実証・FetchXML は Phase 1 |

### 5.2 Nice-to-Have (P1)

- 名前→ID自動解決(既定OFF・完全一致のみ)
- CSV/Excel直接ローダーCLI(`dvload`)
- 進捗UI連携・安全なキャンセル
- 実行前ドライラン(件数・推定API消費・所要時間・必要特権)
- 環境間コピー
- 本番環境ガード
- 【v1.2】createdby 保持手段(CallerId なりすまし等)の確定と実装

### 5.3 Future Considerations (P2)

- サーバー/コンテナ配置モード
- エラスティックテーブル対応(DeleteMultiple含む)
- TDSプロキシモード
- DDLサブセット
- 複数アプリケーションユーザーによる正規スケールアウト

---

## 6. 非機能要件

| カテゴリ | 要件 |
|---|---|
| 性能(書き込み) | バルク対応テーブル: 持続 ≥ 500 rows/sec(目標1,000)。【v1.2】開発環境で dop=32 時 581 rows/sec 実測。EM経路(Upsert・活動系): ≥ 100 rows/sec(目標300)。N:N: ≥ 200 assoc/sec |
| 性能(削除) | BulkDeleteジョブ発行 ≤ 30秒。進捗ポーリングは指数増加(5秒→60秒) |
| 性能(読み取り) | 100万行×20列 ≤ 15分(TDS経由)。TTFB ≤ 3秒 |
| 信頼性 | 一時障害(429/503/断)の自動回復率100%。再開後のデータ整合(重複0・欠損0)を代替キー/GUIDありテーブルで保証 |
| ローカルPC耐性 | OSスリープ抑止。ネットワーク断は最大30分再試行。強制終了後の再開はF-11で保証 |
| メモリ | ストリーミング処理により行数非依存(ゲートウェイ ≤ 1GB) |
| セキュリティ | トークン/シークレットはOSクレデンシャルストア。gRPCはlocalhostバインド+プロセス間認証。セキュリティロールを透過的に尊重 |
| 監査 | 全書き込みジョブの対象・件数・経路・所要時間・エラー・使用オプションをローカル監査ログへ記録し、実施報告に転用可能な形式で出力 |
| 互換性 | Windows 10/11、macOS 13+。DBeaver CE 23+ / DataGrip 2024+、Java 17+、Python 3.10+ |
| 準拠 | サービス保護制限の遵守。制限回避を目的とした偽装は行わない |

---

## 7. 制約事項・リスク

| # | リスク/制約 | 影響 | 対策/状況 |
|---|---|---|---|
| R-1 | 【v1.2 更新】バルクメッセージ対応はテーブル×メッセージごとに異なる | 経路誤りで性能低下 | **メタデータ事前判定で解消(OBS-3)**。標準テーブルの Create/Update は対応。活動系は EM |
| R-2 | 【v1.2 確定】`modifiedon`/`modifiedby` の保持がプラットフォーム上不可 | 完全な監査履歴移行が不可 | **Q-2/OBS-9 で不可を確定**。制約事項として利用者に明示する |
| R-3 | 同期プラグイン/リアルタイムWFの多い環境でのタイムアウト | スループット低下・バッチ失敗 | バッチサイズ自動縮小+テーブル単位バイパス+ドライラン警告 |
| R-4 | BulkDeleteはカスケード削除・監査により長時間化 | 全行入替の所要時間が読めない | ドライランでの件数提示+ジョブ進捗可視化 |
| R-5 | 名前→ID自動解決の曖昧一致 | 誤った参照の混入 | 既定OFF・完全一致のみ・曖昧時は行エラー(P1) |
| R-6 | ローカルPCのリソース/回線品質のばらつき | 性能目標未達 | 性能目標に測定条件を付記。回線診断機能 |
| R-7 | サービス保護制限の将来変更 | 性能目標未達 | 制限値をレスポンスヘッダー駆動で適応(dop-hint 追従) |
| R-8 | 【v1.2 解消】Flight SQL JDBC/ODBCのバッチAPI実装差 | バッチ集約効率 | **Phase 0 で JDBC/ADBC とも集約を実証(Go)** |
| R-9 | 【v1.2 追加】新しいJDK(16+)でArrow JDBCドライバーがJVMフラグ要 | 接続不可 | `--add-opens` 等を配布手順に明記。DBeaver 同梱JREなら不要(OBS-4) |
| R-10 | 【v1.2 追加】createdby 属性設定が実行者になる | 作成者移行の可否未確定 | Phase 1 で CallerId なりすまし等を検証(OBS-9) |

---

## 8. マイルストーン

| フェーズ | 期間目安 | スコープ | 出口基準 | 状況 |
|---|---|---|---|---|
| Phase 0: PoC | 4週 | R-8/Q-2/性能の実測 | 経路別スループット取得、Go/No-Go | **完了(Go・2026-07-06)** |
| Phase 1: MVP | 10週 | P0要件(F-01〜F-15)。DBeaver+Python。Windows配布 | 実移行 1 件に投入可能 | **完了(投入条件 C-1〜C-5 クリア)** |
| Phase 2: 安定化 | 6週 | P1要件、macOS対応、負荷試験自動化 | 移行チーム標準ツール化 | **完了(M3 条件付き Go)** |
| Phase 3: 拡張 | 継続 | P2要件の選択的実装 | — | **進行中(4.2.1 DDL/4.2.2 サーバー配置+B2/4.2.3 高速削除 完了、4.2.4 スケールアウト 機構完成)** |

---

## 9. 未解決の課題(Open Questions)

### 9.1 継続中の課題

| # | 課題 | 種別 | 担当 | ブロッキング |
|---|---|---|---|---|
| Q-6 | createdby の移行元値保持手段(CallerId なりすまし等) | 検証 | 開発 | Phase 1(制約事項に影響) |

### 9.2 クローズ済みの課題(決定内容)

| # | 課題 | 決定 |
|---|---|---|
| Q-1 | N:N関連付けのSQL構文設計 | 両構文を提供(D-13) |
| Q-2 | 【v1.2 クローズ】modifiedon/modifiedby の保持可否 | **保持不可を確定(OBS-9)。R-2 として制約事項化** |
| Q-3 | 代替キー未定義テーブルへの運用 | 代替キー非依存の代替手段(D-14)。Phase 0 で GUID Upsert 冪等性を実証 |
| Q-4 | チェックポイントのジョブ同定方法 | ハイブリッド(D-15) |
| Q-5 | TDS無効環境での読み取り | 両対応・非最適化(D-16) |

---

## 付録A: 性能目標の算定根拠

- サービス保護制限: ユーザー×サーバーあたり6,000リクエスト/5分 ≒ 20 req/sec。アフィニティCookie無効化で実効値は数倍。
- バルク経路: バッチ500行 × 実効2〜4 req/sec(並列8〜16)= 1,000〜2,000 rows/sec が理論上限帯 → 保証500 / 目標1,000。100万行 ≒ 17〜33分。
- 【v1.2 実測】開発環境(dop-hint=4、低リソース)でも並列度32で581 rows/secを達成。本番環境ではより高い並列度・高速処理が見込まれ、上記帯域は妥当。
- EM経路(Upsert・活動系): バッチ100〜300行 → 100〜300 rows/sec → 100万行 ≒ 55分〜2.8時間 → P0基準3時間。
- 削除: BulkDeleteはサーバーサイド実行のためSLA対象外とし、可視化で担保。

## 付録B: 参考リソース

- Microsoft Learn: Use SQL to query data(TDS)/ Bulk operation messages / Optimize performance for bulk operations / Send parallel requests / Service protection API limits / Delete data in bulk / Use Upsert / Define alternate keys / CreateMultipleRequest(IsMessageAvailable)
- OSS: MarkMpn/Sql4Cds、Apache Arrow Flight SQL(JDBC/ODBC/ADBC)、microsoft/PowerApps-Samples(BulkOperations)

## 付録C: 実装状況（2026-07-21 時点）

要件は実装・実機検証済み。利用は
[doc/setup-guide-クライアント接続.md](../doc/setup-guide-クライアント接続.md) を参照。

- **P0(F-01〜F-15)**: 認証4フロー・SELECT(TDS/FetchXML)・INSERT/UPSERT バルク/EM・TRUNCATE/DELETE・N:N・
  型ロスレス往復・チェックポイント自動再開・失敗行レポート・スロットル適応(dop-hint)・監査 を実装し実環境検証済み（M2）。
- **P1**: 本番ガード(3.1.2)・ドライラン(3.1.1)・dvload(3.1.3)・名前→ID 解決(3.1.4)・進捗(3.1.5)・macOS 対応(3.2.1・
  一部 Intel/TDS は実機待ち)・負荷試験自動化/CI(3.3.1) を実装（M3 条件付き Go）。
- **P2(§5.3・選択的実装)**: DDL サブセット(4.2.1)、サーバー配置=Windows サービス/セッション分離/TLS/バインド/
  **接続先クライアント供給(B2)**(4.2.2)、**エラスティック対応 + 高速削除(DeleteMultiple/同期経路)**(4.2.3) を実装・実機検証済み。
  **複数アプリユーザー・スケールアウト**(4.2.4) は機構完成（2 つ目 SP でのスループット実測は保留）。TDS プロキシモード・
  コンテナ配置は未着手。
- **制約(R-2)**: `modifiedon`/`modifiedby` の保持不可を確定済み（制約事項として明示）。
