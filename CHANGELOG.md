# 変更履歴 / リリースノート

本プロジェクト（Dataverse SQL ドライバ＝Arrow Flight SQL ゲートウェイ）のリリースノート。
バージョンは [セマンティック・バージョニング 2.0.0](https://semver.org/lang/ja/)（`MAJOR.MINOR.PATCH`）に従う。
版数の単一管理点は `src/Directory.Build.props`（`<VersionPrefix>`）。

---

## [Unreleased]

### 追加（Added）
- **リリースの CI 化**: タグ `v<版数>` の push で GitHub Actions（`.github/workflows/release.yml`）が Windows（ZIP / MSI）・macOS（osx-arm64 / osx-x64 の tar.gz。macOS ランナーでビルド）・Linux（linux-x64 の tar.gz / .deb）を作り、各 `.sha256` とともに GitHub リリースに添付する。タグと `src/Directory.Build.props` の版数が一致しなければ失敗する。リリースノートは本ファイルの該当版の節。手順は [doc/リリース手順-CI.md](doc/リリース手順-CI.md)。
- **Linux 版の配布物**: `dist/publish-linux.sh`（自己完結 single-file）と `dist/package-deb.sh`（.deb。`/opt/dataverse-gw` に配置し、コマンド `dataverse-gw` を用意）。

## [1.1.0] - 2026-07-28

2026-07-24 のセキュリティレビューの高深刻度指摘 H-1〜H-3 および中/低深刻度 M-1〜M-5・L-1〜L-3（全指摘）を是正。機能面の後方互換は維持（設定の追加のみ）。**SemVer: セキュリティ強化に伴う後方互換の変更のため MINOR bump（1.0.0 → 1.1.0）。**

### セキュリティ（Security）
- **本番環境ガードを fail-closed 化（H-1）**: `Gateway:IsProduction` 未設定時、リモート Dataverse（`*.dynamics.com`）は dev/prod を URL で区別できないため**本番扱い（書き込み既定ブロック）**へ変更。従来はホスト名に prod/prd/production を含む場合のみ本番と判定し、大半の本番組織が非本番＝書き込み許可に漏れていた。非本番は `Gateway:IsProduction=false` を明示する。localhost 等は非本番既定。
- **ネットワーク公開時の fail-closed 起動（H-2）**: 非 localhost バインド（`Gateway:Bind=any`/`<IP>`）で TLS 未設定または `Gateway:RequireToken` 未設定の場合、警告ではなく**起動を拒否**（平文・無認証アクセスを防止）。localhost バインドは不変。
- **CSV フォーミュラインジェクション対策（H-3）**: 失敗行レポート／監査 CSV で数式記号（`= + - @`・タブ）で始まる値の先頭に `'` を付与し、Excel での数式評価を防止。
- **なりすまし（IMPERSONATE）の特権事前検査（M-1）**: `SET IMPERSONATE` / dvload `--impersonate` で `CallerId` を設定する全経路の実行前に、特権 `prvActOnBehalfOfAnotherUser` を検査（`PrivilegeChecker.EnsureCanImpersonate`）。無ければ実行前に明示エラー（F-08 の設計思想に整合）。従来はゲートウェイ側で一切検査せず Dataverse 側の実行時検証に委ねていた。
- **特権事前検査の fail-closed 化（L-1）**: 特権照会自体が例外で失敗した場合、従来は許可扱い（fail-open）だったのを**安全側=不許可**へ変更（認可の穴を是正）。
- **詳細エラーの外部返却を既定 OFF ＋認証失敗例外のサニタイズ（M-4）**: gRPC `EnableDetailedErrors` を既定 `false` 化（`Gateway:EnableDetailedErrors=true` で開発時のみ有効）。加えて Dataverse 接続失敗時、`client.LastError`/`LastException`（接続文字列断片・secret を含み得る）をクライアントへ返さず**固定文言**を返し、詳細は secret を伏字化してサーバーログのみに記録。
- **入力の OOM 耐性（M-3）**: xlsx のセル参照解釈に **Excel 列上限（XFD=16384）** を課し、上限超過の参照（例 `AAAAAA1`）は即エラー化。従来は欠損セル埋めで数億セル分を確保しに行き OOM／int オーバーフローの恐れがあった。加えて dvload に入力ファイルサイズ上限 `Gateway:MaxInputFileMB`（既定 0＝無制限）を追加し、巨大ファイルの全読み込みをクラッシュではなく実行前の明示エラーに。**xlsx は展開後（非圧縮）サイズも同上限で検査**し、圧縮後サイズだけでは検知できない「展開ボム」（小さな .xlsx が展開後に巨大化）による OOM も遮断。
- **失敗行レポートの PII マスキング＋保護（M-2）**: 失敗行 CSV の `error` 列を**既定でエラーコード/例外型のみ**に変更（Dataverse の `ex.Message` 全文＝違反フィールドの実値・PII を含み得るため出さない。原因列は別列に保持。全文は `Gateway:FailureReportIncludeMessage=true` でオプトイン、かつ改行を畳んで CSV 行を保護）。加えて出力 CSV を**現ユーザーのみへ制限**（Windows=ACL／非 Windows=0600）し、`Gateway:ReportRetentionDays`（既定 0＝無制限）で**保持期間超過の古いレポートを自動削除**。サーバーログには従来どおり全文を残し調査可能性を維持。
- **secret を持つ record の `ToString()` 秘匿（M-5）**: `ConnParams`・`PrincipalConfig` に `ToString()` を明示オーバーライドし、`ClientSecret` を `***`（未設定は `(none)`）で伏字化。誤って record 全体を文字列化した場合の平文漏洩を防止。
- **レポートファイル名のパストラバーサル対策（L-2）**: 失敗行 CSV・監査 Markdown・ドライラン Markdown のファイル名に外部由来のテーブル名/ジョブラベルを使う際、`SafeName.ForFile` でパス区切り・`..`・無効文字を除去し出力先ディレクトリ外への逸脱を防止。
- **プロセストークンの定数時間比較＋ファイル ACL 明示（L-3）**: トークン照合を `CryptographicOperations.FixedTimeEquals` に変更（タイミング差の回避）。トークンファイルは継承 ACL に頼らず、現ユーザーのみへ明示制限（Windows=ACL 保護／非 Windows=0600）。

---

## [1.0.0] - 2026-07-21

初回リリース。DBeaver・DataGrip・Python(ADBC) 等の汎用 SQL クライアントから Microsoft Dataverse へ
高性能な CRUD（百万行オーダー）を行うためのローカル/サーバー配置型ゲートウェイ。
詳細な利用手順は [doc/setup-guide-クライアント接続.md](doc/setup-guide-クライアント接続.md)。

### 追加（Added）

**接続・認証**
- 認証 4 方式: サービスプリンシパル（シークレット／証明書）・対話型・デバイスコード。トークンは OS クレデンシャルストアで保護。
- Windows / macOS（13+、arm64・Intel）/ Linux コンテナ で動作。クライアントは DBeaver・DataGrip・Python(ADBC)、JDBC は flight-sql-jdbc-driver 19.0.0。

**読み取り**
- `SELECT` パススルー（TDS エンドポイント経由、不可時 FetchXML 自動切替）・Arrow ストリーミング。macOS/Linux でも TDS 読み取り動作を確認。

**書き込み**
- `INSERT` / `UPSERT`（`ON KEY` 代替キー・主キー指定）: バルク（CreateMultiple）／非対応テーブルは ExecuteMultiple 並列。
- 型ロスレス往復（Lookup / Choice / Money / DateTime 3 種 / activityparty）。
- チェックポイント自動再開（中断→再送で重複 0・欠損 0）、失敗行レポート（CSV）、サービス保護制限への適応（dop-hint 追従）、監査ログ。

**削除**
- `DELETE` / `TRUNCATE` の経路自動選択: 標準テーブル少量＝同期 ExecuteMultiple（即時）／大量＝BulkDelete（非同期）／エラスティック＝DeleteMultiple。

**その他 SQL**
- N:N 関連付け（INSERT 構文・ASSOCIATE 文）。
- DDL サブセット: `CREATE TABLE` / `ALTER TABLE ADD` / `DROP TABLE`（メタデータ API・移行前スキーマ準備用）。
- 書き込みヒント: `BYPASS_PLUGINS` / `BYPASS_FLOWS` / `SUPPRESS_DUPLICATE_DETECTION` / `IMPERSONATE` / `JOB_ID` / `NO_BULK` / `RESOLVE_NAMES`。

**安全性・運用**
- 本番環境ガード（本番と判定した接続先は書き込みを既定ブロック。`SET ALLOW_PRODUCTION_WRITES=ON` で許可）。
- 実行前ドライラン（`SET DRY_RUN=ON` で件数・API 消費・所要時間・必要特権を提示）。
- 名前→ID 自動解決（既定 OFF・曖昧/未一致は行エラー・TTL/明示リフレッシュ対応）。
- 進捗の可視化（仮想テーブル `SELECT * FROM gateway_progress`）と安全なキャンセル（バッチ境界停止＋チェックポイント再開）。
- CLI: `dvload`（CSV/Excel 直接ローダー）、`setup-nn` / `setup-altkey` / `setup-elastic`（検証環境準備）。

**配置形態**
- ローカルサイドカー（既定 `localhost:32010`）。
- Windows サービス常駐。
- 接続（クライアント）単位のセッション分離。
- ネットワーク公開（バインド設定）＋ TLS。
- 接続先のクライアント供給（B2）: 1 ゲートウェイで複数 Dataverse 環境を切替利用。
- 複数アプリケーションユーザー（SP）による書き込みスケールアウト。
- Linux コンテナ配置（`dist/Dockerfile`）。

**配布**
- 自己完結 single-file 実行ファイル（.NET ランタイム同梱・別途インストール不要）。
- Windows: ポータブル ZIP / MSI（WiX）／自己署名＋GPO 配布。macOS: tar.gz（osx-arm64 / osx-x64）。

### 既知の制約・運用条件（Known limitations）
- `modifiedon` / `modifiedby` の移行元値は**保持不可**（プラットフォーム制約）。`createdby` はなりすまし（`IMPERSONATE`）で対応。
- 共有サーバー／コンテナ配置の認証は **ClientSecret / Certificate のみ**（対話型・デバイスコードは不可）。
- ネットワーク公開／コンテナの TLS は **証明書パス（PFX）指定**（Windows 証明書ストアの拇印指定は Windows のみ）。
- バルク（CreateMultiple）は全標準テーブルが対応するわけではない（非対応は ExecuteMultiple へ自動フォールバック）。
- 複数 SP スケールアウトの「予算分離による ~2 倍」は、単一 SP が throttle 飽和する高スループット規模でのみ顕在化（低〜中負荷では並列度が律速）。
- `DeleteMultiple` はエラスティックテーブル専用（標準テーブルは同期 ExecuteMultiple／BulkDelete）。
- **TDS プロキシモード（SQL Server ネイティブツールが TDS で直接接続）は未提供**（設計検討のうえ保留。→ [design/tdsプロキシモード-設計検討-4.2.6.md](design/tdsプロキシモード-設計検討-4.2.6.md)）。

---

[1.0.0]: 初回リリース
