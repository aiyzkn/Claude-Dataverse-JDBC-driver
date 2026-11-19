# Phase 1 アーキテクチャ方針

| 項目 | 内容 |
|---|---|
| 文書バージョン | 1.0 |
| 作成日 | 2026-07-06 |
| 前提 | [要件定義書 v1.2](../requirements/dataverse-sql-driver-要件定義書-v1.2.md) |
| コードベース方針 | **ハイブリッド**（PoC のコア資産を選択的移植 ＋ 製品用スケルトン新規構築） |

## 1. コードベース方針（決定）

Phase 0 の PoC は使い捨て前提だが、Phase 0 で実証済みの部分は品質・設計とも流用価値が高い。製品用に新規のレイヤー分離アーキテクチャを `src/` に構築し、実証済みコアを移植する。

| 区分 | PoC からの扱い | 対象 |
|---|---|---|
| **移植（品質良・実証済み）** | ロジックを製品構造へ移す | Flight SQL プロトコル層、並列チャンク実行（クローンプール）、UPSERT（EM+UpsertRequest）、SqlInfo/parameter/dataset schema、スキーマ IPC シリアライズ |
| **作り直し（PoC 簡易実装）** | 設計し直して実装 | 型マッピング（ロスレス往復・F-13）、メタデータ（全件・経路事前判定）、SQL 種別判定（Sql4Cds パーサーへ寄せる） |
| **新規** | Phase 1 で新規実装 | 対話型認証、チェックポイント/SQLite、スロットリング適応、失敗行レポート、BulkDelete/TRUNCATE、N:N、Lookup 解決、バイパス、FetchXML フォールバック、配布 |

製品コードは `src/` 配下（PoC コードは本リポジトリに含めない）。

## 2. レイヤー構成（src/）

```
src/
├── DataverseGW.sln
├── Gateway.Host/          … エントリポイント・DI・Kestrel/Flight 設定・ライフサイクル
├── Gateway.Flight/        … Flight SQL プロトコル(FlightServer 実装・スキーマ変換・メタデータ応答)
├── Gateway.Dataverse/     … 認証(MSAL)・ServiceClient 管理・バルク/EM 実行・メタデータ取得
├── Gateway.Sql/           … Sql4Cds 統合・独自構文拡張(UPSERT/TRUNCATE/N:N/ヒント)
├── Gateway.Core/          … ドメインモデル・経路選択・型マッピング・チェックポイント抽象
└── Gateway.Tests/         … xUnit(単体)・E2E(開発者環境)
```

依存方向: Host → Flight/Sql → Dataverse/Core（Core は他に依存しない）。DI で疎結合にし、テスト可能性を確保する。

## 3. スプリント順（基盤優先）

Phase 0 のクリティカルパス知見と「基盤優先」の方針に基づく。

| スプリント | WBS | 内容 | 主な移植/新規 |
|---|---|---|---|
| **S1: 基盤**（今ここ） | 2.1.1〜2.1.4 | 認証(SP+対話型)、ライフサイクル、Sql4Cds 統合、型ロスレス・メタデータ | 認証=新規、プロトコル層=移植 |
| S2: 書き込み中核 | 2.2.1〜2.2.8 | 経路メタデータ判定、バルク/EM、Upsert、Lookup、N:N、監査列、バイパス | 並列実行・UPSERT=移植 |
| S3: 信頼性 | 2.4.1〜2.4.4 | チェックポイント/SQLite、3段階冪等化、スロットリング適応、ローカル耐性 | 冪等化ロジック=移植/拡張 |
| S4: 削除・読み取り・品質 | 2.3/2.5/2.6 | BulkDelete/TRUNCATE、TDS/FetchXML、失敗行レポート、監査ログ、E2E、性能受入 | SELECT=移植 |
| S5: 配布・投入 | 2.7 | MSI、セットアップガイド、投入判定 | 新規 |

## 4. S1（基盤）の設計要点

- **認証（F-01, 2.1.1）**: MSAL の 3 フロー — クライアントシークレット / 証明書 / 対話型（ブラウザー・デバイスコード）。トークンは OS クレデンシャルストア（Windows: DPAPI）へ永続化し、8 時間ジョブ無断絶。対話型実行時は「監査列に実行者が記録される」旨を接続時に表示。
- **ライフサイクル（2.1.2）**: localhost バインド、プロセス間認証トークン（起動時発行→クライアントへ受け渡し）、アイドル自動終了、多重起動防止（ポート/ミューテックス）。
- **SQL パーサー統合（2.1.3）**: Sql4Cds.Engine 依存 ＋ UPSERT/TRUNCATE/ASSOCIATE/ヒント句の前処理レイヤー。PoC の正規表現ベース簡易解析を、文型分類も含め整理。
- **型ロスレス・メタデータ（2.1.4/F-13）**: EntityMetadata から属性型を取得し、Lookup=EntityReference・Choice=OptionSetValue・Money=Money・DateTime(3種) を Arrow ↔ SDK でロスレス変換。メタデータキャッシュ。

## 5. 移行の互換性

- Flight SQL の URL・接続仕様は PoC と互換に保つ（DBeaver/ADBC の再設定不要）。
- 認証情報は引き続き環境変数 / OS クレデンシャルストアで管理（コミット禁止方針を継続）。

## 6. Phase 3 追補（アーキテクチャの発展・2026-07-21）

本節は Phase 1 方針の上に Phase 3（P2 選択的実装）で加わった構造変更の記録。

- **接続コンテキスト化と DI 反転（4.2.2c / B2）**: 起動時単一接続に束ねていた DI を、**接続先（URL＋認証）ごとの `DataverseContext`**
  （接続・メタデータ/経路キャッシュ・本番ガード・各エグゼキュータの束）へ反転。`DataverseContextRegistry` が接続先ごとに
  動的生成・キャッシュし、`FlightSqlService` は **`AsyncLocal<DataverseContext>`** で RPC ごとに解決する。クライアントは
  gRPC ヘッダ（`x-dataverse-url/auth/...`）で接続先を供給できる（1 ゲートウェイ多環境）。横断的関心（監査・チェックポイント・
  進捗・ドライラン・セッション）はプロセス共有のまま。
- **セッションのピア分離（4.2.2b）**: `SessionState` を gRPC ピア別に保持する `SessionRegistry`。本番ガードは不変判定＋
  `EnsureWriteAllowed(op, target, sessionAllows)` に再設計し、全書き込み経路へセッション許可を配線。
- **ネットワーク公開＋TLS（4.2.2c）**: Kestrel の `Gateway:Bind`（localhost/any/IP）＋ `Gateway:Tls:*`（PFX/拇印）で h2 over TLS。
- **削除の経路自動選択（4.2.3）**: `BulkDeleteExecutor` が対象テーブル種別・件数で経路を選択（標準少量=同期 ExecuteMultiple /
  標準大量=BulkDelete 非同期 / エラスティック=DeleteMultiple）。`EntityMetadataCache.IsElastic` で判定。
- **複数 SP スケールアウト（4.2.4）**: `DataverseConnection.ScaleOutPool`（主＋追加 SP）を `PreparedInsertExecutor` の並列書込
  （ワークキュー方式）で巡回利用し、SP 単位の throttle 予算を合算。単一 SP 時は従来挙動を維持。
- **DDL サブセット（4.2.1）**: `DdlExecutor` が `CREATE`/`ALTER ADD`/`DROP TABLE` をメタデータ API へ変換。
