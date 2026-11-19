# TDS プロキシモード — 設計・実現可能性検討（4.2.6）

| 項目 | 内容 |
|---|---|
| 目的 | 要件定義書 §5.3(P2)「TDS プロキシモード」の実現可能性・スコープ・工数・リスクを検討し、着手方針を決める |
| 状態 | **検討完了・保留（2026-07-21 決定）**。Flight SQL で書き込み用途は充足しており、TDS 書き込みの具体的需要が現時点で不明のため**着手しない**。需要が具体化した際に §7 の PoC から着手する。本書は着手判断の起点として保存 |
| 関連 | [要件定義書 v1.2 §5.3](../requirements/dataverse-sql-driver-要件定義書-v1.2.md) |

---

## 1. TDS プロキシモードとは

クライアントが **SQL Server のワイヤプロトコル（TDS: Tabular Data Stream）** で直接ゲートウェイに接続し、
SSMS・sqlcmd・Power BI(SQL Server コネクタ)・ODBC/JDBC の SQL Server ドライバなど**SQL Server ネイティブなツール**を
そのまま使えるようにする配置形態。現行の Arrow Flight SQL（専用 JDBC/ADBC ドライバ）に対する代替フロントエンド。

## 2. 価値の見極め（重要）

- **Dataverse には読み取り専用の TDS エンドポイントが既に存在**する（`<org>.crm…dynamics.com,5558`）。
  SQL Server 系ツールは**読み取りなら Dataverse へ直接接続でき**、ゲートウェイは不要。
- 本ツールの固有価値は **書き込み（バルク INSERT/UPSERT/DELETE・チェックポイント・本番ガード等）**。
- したがって TDS プロキシの価値は「**SQL Server ネイティブなツールから書き込みパイプラインを使えること**」に限られる。
  読み取り専用なら Dataverse の既存 TDS で足り、プロキシの追加価値は小さい。
- 需要の広さ: 主要な書き込み用途（DBeaver・移行スクリプト）は既に Flight SQL で充足。TDS 書き込みの需要は
  「SSMS/sqlcmd で移行したい」等に限定的と見られる。**着手前に具体的な利用シナリオの確認が必要**。

## 3. 実装アプローチ

| # | 方式 | 内容 | 価値 | 複雑度 |
|---|---|---|---|---|
| A | 読み取りパススルー | すべて Dataverse の既存 TDS へ転送（リバースプロキシ） | 低（既存 TDS で足りる） | 中 |
| B | 書き込みインターセプト（ハイブリッド） | TDS の SQL バッチを解析し、SELECT は Dataverse TDS へ転送 / DML は本ツールの書き込みパイプラインへ。結果は TDS トークンで返す | 高 | **高** |
| C | 独立 TDS サーバー ＋ Sql4Cds | TDS サーバーを立て、SELECT/DML とも Sql4Cds エンジン（本ツールが既に使用）で実行し、結果を TDS トークンで返す | 高 | **高** |

- 現実的に価値があるのは B か C。いずれも「**TDS サーバー側の実装**（トークンストリーム生成）」が必須で難所。
- C は Sql4Cds が SELECT＋DML を一貫実行できる点で設計がクリーン（読み書きの経路分岐が要らない）。

## 4. 技術的難所

1. **TDS サーバー実装**: PRE-LOGIN（TDS 8.0 では TLS 必須化の流れ）、LOGIN7、SQL_BATCH/RPC の解釈、結果セットの
   トークンストリーム生成（COLMETADATA / ROW / DONE / ERROR）。手書きは大規模。→ 既存ライブラリの活用が前提。
2. **認証**: SQL Server クライアントは AAD アクセストークン（Azure AD 認証）または SQL 認証で接続する。
   プロキシはクライアント認証を受けて Dataverse 接続へマッピングする必要がある。設計上の要。
3. **SSMS 互換の沼**: SSMS は接続時に多数のシステムビュー/関数へメタデータ照会を投げる。完全対応は難易度が高く、
   「SSMS で完全に動く」を目標にすると工数が跳ね上がる（互換対応が長い尾）。sqlcmd/ODBC アプリに絞れば軽い。
4. **型・結果マッピング**: Sql4Cds / Dataverse の型を TDS の型トークンへ正確に写像。

## 5. 再利用可能な資産（調査結果）

| 資産 | 位置づけ | 使えるか |
|---|---|---|
| `Microsoft.Data.Tools.TdsLib`（microsoft/TdsLib） | 管理 C# の TDS 7.4 実装。login/汎用トークン中心。主にクライアント mock・プローブ用 | フレーミングの参考・部分利用 |
| `Rmg.Tds`（mgaffigan） | SQL Server 用リバース TDS プロキシ。転送・傍受・ルーティング | 方式 A/B のプロキシ土台候補 |
| `TDSBridge`（MindFlavor） | [MS-TDS] 準拠の TDS プロキシライブラリ | 方式 A/B の参考 |
| `Microsoft.SqlServer.TDS`（テスト用フェイクサーバー） | SqlClient テストで使われる TDS サーバー実装 | 方式 C のサーバー土台候補（入手性・ライセンス要確認） |
| `MarkMpn.Sql4Cds.Engine`（本ツールが使用中） | SELECT＋DML を Dataverse で実行 | 方式 C の実行エンジン（そのまま流用可） |

> Sql4Cds には SSMS/Azure Data Studio 向けプラグインがあるが、これは**クライアント側統合**であり、
> 任意の SQL Server クライアントが接続できる TDS サーバーではない（＝本件で新規に用意する部分）。

## 6. 工数・リスク（概算）

- **PoC（スパイク）**: TDS サーバーライブラリで 1 接続を受け、PRE-LOGIN/LOGIN（PoC は簡易認証）→ 単純 SELECT を
  Sql4Cds で実行 → 結果トークン返却、を sqlcmd で確認。**~1〜2 週間**。
- **本実装（方式 C・sqlcmd/ODBC 対象・SSMS 完全対応は非目標）**: 認証マッピング、DML/結果/エラーのトークン化、
  型写像、TLS。**~4〜6 週間**。
- **SSMS 完全対応まで含める**: メタデータ照会互換の対応で **さらに +数週間**（長い尾・保守負担大）。
- 主リスク: 認証（AAD トークン受け入れ）、TDS 8.0/TLS、SSMS 互換、ライブラリのライセンス/保守状況。

## 7. 推奨

**「PoC 先行 → Go/No-Go 判定」を推奨。** 需要（誰がどのツールで書き込みたいか）を確認した上で、方式 C（Sql4Cds ＋
TDS サーバーライブラリ）で最小 PoC を作り、sqlcmd/ODBC で「接続→SELECT→簡単な INSERT」が通るかを検証してから
本実装の可否を判断する。読み取り専用（方式 A）は Dataverse 既存 TDS で足りるため価値が薄く非推奨。SSMS 完全対応は
初期スコープから外す（対象を sqlcmd/ODBC アプリに限定）。

需要が具体化しない場合は、**Flight SQL で書き込み用途は充足しているため保留（着手しない）**も妥当な判断。

## 8. 決定が必要な事項

1. 具体的な利用シナリオ（どのツールで、読み取り/書き込みどちらを、なぜ TDS で）。
2. 進め方: PoC 先行 / 本実装まで一気 / 対象を絞る（sqlcmd・ODBC のみ） / 保留。
3. SSMS 完全対応をスコープに含めるか（含めると工数大）。

---

*作成日: 2026-07-21 / 対象: WBS 4.2.6（TDS プロキシモード・検討）/ 出典: 下記*

## 出典
- [SQL 4 CDS（DML 対応・TDS エンドポイント）— Mark Carrington](https://markcarrington.dev/sql-4-cds/)
- [Use SQL to query data (Dataverse) — Microsoft Learn](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/dataverse-sql-query)
- [microsoft/TdsLib（Microsoft.Data.Tools.TdsLib）](https://github.com/microsoft/TdsLib)
- [Rmg.Tds（Reverse TDS Proxy）](https://github.com/mgaffigan/Rmg.Tds)
- [TDSBridge（TDS Proxy library）](https://github.com/MindFlavor/TDSBridge)
- [TDS 8.0 — Microsoft Learn](https://learn.microsoft.com/en-us/sql/relational-databases/security/networking/tds-8)
