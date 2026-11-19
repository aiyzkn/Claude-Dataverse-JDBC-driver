# Dataverse GW（Arrow Flight SQL ゲートウェイ）

DBeaver・DataGrip・Python(ADBC) 等の汎用 SQL クライアントから Microsoft Dataverse に対して
高性能な CRUD（百万行オーダー）を行うためのゲートウェイです。

## 概要

- **目的**: 汎用 SQL クライアントから Dataverse へ INSERT/UPDATE/UPSERT/DELETE/SELECT を実行。データ移行・一括更新（書き込み）を最優先。
- **想定利用**: データ移行・一括更新の作業者のローカル PC、または常駐サーバー配置。
- **アーキテクチャ**: **Arrow Flight SQL ゲートウェイ型**（.NET 8 製プロセス ＋ 既製の Flight SQL JDBC/ADBC ドライバー）。
- **読み書き分離**: 読み取り=TDS パススルー（不可時 FetchXML 自動切替）、書き込み=Web API バルクメッセージ（非対応テーブルは ExecuteMultiple 並列）。

## リポジトリ構成

| ディレクトリ | 内容 |
|---|---|
| `src/` | **製品コード**（.NET 8 ソリューション `DataverseGW.slnx`）。[src/README.md](src/README.md) 参照 |
| `requirements/` | 要件定義書（**v1.2**） |
| `design/` | アーキテクチャ方針・設計検討 |
| `doc/` | 利用者向け [セットアップ/接続ガイド](doc/setup-guide-クライアント接続.md)、macOS 手順書、リリース手順、性能試験 |
| `bench/` | NBomber 負荷試験プロジェクト |
| `dist/` | 配布・Windows サービス導入スクリプト |

まず利用は [doc/setup-guide-クライアント接続.md](doc/setup-guide-クライアント接続.md)、仕様は [requirements/…v1.2](requirements/dataverse-sql-driver-要件定義書-v1.2.md) を参照してください。

## 主な機能（実装済み）

- **認証**: SP シークレット / 証明書 / 対話型 / デバイスコード（OS クレデンシャルストア保護のトークンキャッシュ）
- **読み取り**: `SELECT`（TDS パススルー、不可時 FetchXML 自動切替）・Arrow ストリーミング
- **書き込み**: `INSERT`/`UPSERT`（バルク CreateMultiple / ExecuteMultiple 並列）、`ON KEY` 代替キー・主キー UPSERT
- **削除**: `DELETE`/`TRUNCATE` の**経路自動選択**（標準少量=同期 ExecuteMultiple / 標準大量=BulkDelete / エラスティック=DeleteMultiple）（4.2.3）
- **N:N**: INSERT 構文 と ASSOCIATE 文
- **DDL サブセット**: `CREATE`/`ALTER ADD`/`DROP TABLE`（メタデータ API。4.2.1）
- **安全性・運用**: 本番環境ガード（安全側既定ブロック）、実行前ドライラン（`SET DRY_RUN`）、名前→ID 自動解決、進捗仮想テーブル `gateway_progress`、監査ログ、チェックポイント自動再開（重複0）、失敗行レポート
- **配布・サーバー化**: Windows サービス常駐（4.2.2a）、per-connection セッション分離（4.2.2b）、ネットワーク公開＋TLS（4.2.2c）、**接続先のクライアント供給（B2）**（4.2.2c）、**複数 SP スケールアウト**（4.2.4）
- **CLI**: `dvload`（CSV/Excel 直接ローダー）、`setup-nn`/`setup-altkey`/`setup-elastic`（検証環境準備）

## ステータス

- リリース履歴は [CHANGELOG.md](CHANGELOG.md) を参照。配布物は Windows `.zip`/`.msi`、macOS arm64/x64 `.tar.gz` を SHA-256 付きで作成できる。手順は [Windows](doc/リリース手順-Windows.md) / [macOS](doc/リリース手順-macOS.md) を参照。
- macOS（Intel）実機と Linux コンテナ（Podman・`dist/Dockerfile`）で TDS 読み取りの動作を確認済み。既定 `auto` のままで TDS が使われる。
- 未着手・確認事項: TDS プロキシモード（[設計検討](design/tdsプロキシモード-設計検討-4.2.6.md)。需要が具体化した時点で PoC から着手）、複数 SP の予算分離による 2 倍化の実測（単一 SP がスロットルで飽和する規模でのみ顕在化する。コードは実装済み）。
