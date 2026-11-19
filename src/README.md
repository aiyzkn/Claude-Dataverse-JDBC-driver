# DataverseGW（製品コード）

Microsoft Dataverse を Arrow Flight SQL で公開するゲートウェイ。Phase 0 PoC で実証したコアを移植し、
Phase 1〜3 で機能拡張・安定化した。方針は [Phase 1 アーキテクチャ](../design/phase1-architecture-v1.0.md)（ハイブリッド）。

| 項目 | 内容 |
|---|---|
| スタック | .NET 8 / Apache Arrow Flight SQL / Sql4Cds.Engine / Dataverse.Client(1.2.x) / MSAL |
| ソリューション | `DataverseGW.slnx` |
| 利用手順 | [../doc/setup-guide-クライアント接続.md](../doc/setup-guide-クライアント接続.md) |

## プロジェクト構成

```
src/
├── Gateway.Core/       … AuthOptions/AuthKind 等のオプション・モデル(依存なし)
├── Gateway.Checkpoint/ … チェックポイント(SQLite)。中断→再開の冪等性(F-11)
├── Gateway.Dataverse/  … 認証(MSAL 4フロー)・ServiceClient・Sql4Cds接続・バルク/EM/Upsert・
│                         BulkDelete/DeleteMultiple・Associate・DDL・名前解決・本番ガード・
│                         メタデータ/経路判定・ドライラン・進捗・セッション・接続コンテキスト(B2)・スケールアウト
├── Gateway.Flight/     … Flight SQL プロトコル(FlightSqlService・Arrow変換・スキーマ・メタデータ)
└── Gateway.Host/       … Kestrel/Flight ホスティング・DI・ライフサイクル・TLS/バインド・CLI・Windows サービス
```

## 実行

```powershell
# 環境変数(手順書 §1.2)設定済みが前提。認証方式は DATAVERSE_AUTH で選択
#   ClientSecret(既定) / Certificate / Interactive / DeviceCode
cd src
./run.ps1
```

起動ログに `Gateway listening on grpc://localhost:32010` が出れば疎通 OK。
起動時 `DATAVERSE_URL` 未設定でも起動でき、接続先はクライアント供給ヘッダ（B2。手順書 §7.6）で受け付ける。

### CLI サブコマンド

| コマンド | 用途 |
|---|---|
| `dvload --file <csv/xlsx> --table <t> [--mode upsert --key <k>] [--dry-run]` | CSV/Excel 直接ローダー（サーバー起動不要） |
| `setup-nn` / `setup-altkey` / `setup-elastic` | 検証用の N:N リレーション / 代替キー / エラスティックテーブル作成 |

## 実装状況（2026-07-21）

| 機能 | 状態 |
|---|---|
| 認証（SP シークレット/証明書/対話型/デバイスコード・OS ストア保護キャッシュ）(F-01) | ✅ 実機検証（対話型/デバイスコードは macOS 実機含む） |
| ライフサイクル（バインド・多重起動防止・アイドル終了・プロセストークン・OS スリープ抑止） | ✅ |
| SELECT パススルー（TDS/FetchXML 自動切替・Arrow ストリーミング）(F-15) | ✅ 実環境検証 |
| INSERT/UPSERT バルク/EM 実行＋並列（クローンプール）・型ロスレス往復(F-13) | ✅ 実環境検証 |
| TRUNCATE/DELETE 経路自動選択（同期 EM / BulkDelete / DeleteMultiple）(F-10/4.2.3) | ✅ 実環境検証 |
| N:N（INSERT/ASSOCIATE）・ヒント句・DDL サブセット(4.2.1) | ✅ 実環境検証 |
| チェックポイント自動再開(F-11)・失敗行レポート(F-14)・監査ログ | ✅ 実環境検証 |
| 本番ガード(3.1.2)・ドライラン(3.1.1)・名前→ID 解決(3.1.4)・進捗(3.1.5) | ✅ 実環境検証 |
| サーバー配置: Windows サービス/セッション分離/TLS/バインド/クライアント供給接続(B2)(4.2.2) | ✅ 実環境検証 |
| 複数 SP スケールアウト(4.2.4) | ✅ 機構完成・異なる 2 つ目 SP で実測済（dev では予算分離の有意差なし＝レイテンシ律速。2x は単一 SP が throttle 飽和する本番相当規模でのみ顕在化） |

## 主な設定（環境変数 `Gateway__<Key>` / `--Gateway:<Key>`）

| キー | 既定 | 意味 |
|---|---|---|
| `Gateway:Port` | 32010 | Flight サーバーポート |
| `Gateway:BatchSize` / `Gateway:Dop` | 200 / dop-hint | 1 リクエスト行数 / 並列度 |
| `Gateway:UseTdsEndpoint` | auto | 読み取り経路（auto/true=TDS/false=FetchXML） |
| `Gateway:IsProduction` / `Gateway:AllowProductionWrites` | 自動 / false | 本番判定 / 本番書き込み許可（安全側既定） |
| `Gateway:SyncDeleteMaxRows` | 5000 | 標準テーブルの同期削除上限（超過は BulkDelete。4.2.3） |
| `Gateway:Bind` / `Gateway:Tls:*` | localhost / — | バインド先 / TLS 証明書（4.2.2c） |
| `Gateway:ScaleOut:Principals` | — | 書き込み分散する追加 SP の配列（4.2.4） |
| `Gateway:IdleShutdownMinutes` / `Gateway:RequireToken` | 30 / false | アイドル自動終了 / プロセストークン強制 |

> 全設定は [../doc/setup-guide-クライアント接続.md §7](../doc/setup-guide-クライアント接続.md) を参照。
