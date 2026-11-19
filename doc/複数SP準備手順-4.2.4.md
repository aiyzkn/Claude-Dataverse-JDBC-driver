# 複数SPスケールアウト — 2つ目のSP準備手順（4.2.4）

| 項目 | 内容 |
|---|---|
| 目的 | 書き込みスループットのスケールアウト（4.2.4）を実測するため、**2つ目の異なるアプリケーションユーザー（SP）** を用意する |
| 背景 | サービス保護制限（throttle）は SP 単位。複数の異なる SP に書き込みを分散すると予算が合算され、単一 SP 飽和時のスループットが向上する |
| 関連 | [setup-guide §7.5](setup-guide-クライアント接続.md) / `bench/measure_write_throughput.py` |
| 所要 | 15〜30 分（権限があれば） |
| 必要権限 | Entra（Azure AD）でアプリ登録できる権限、対象 Dataverse 環境のシステム管理者 |

> **セキュリティ厳守**: クライアントシークレット・証明書はコード/リポジトリ/チャットに**直書き・貼り付け禁止**。
> 環境変数または OS クレデンシャルストアで管理する。既存の 1 つ目 SP と**同一テナント・同一環境**であること。

---

## 全体像

```
[Entra] 2つ目のアプリ登録 → クライアントシークレット発行
   │  (Client ID / Tenant ID / Secret)
   ▼
[Dataverse] そのアプリを「アプリケーションユーザー」として追加 → 書き込みロール割当
   │
   ▼
[Gateway] Gateway:ScaleOut:Principals:0 に 2つ目SPを設定して起動
   │
   ▼
[実測] bench/measure_write_throughput.py で baseline(1SP) と scaleout(2SP) を比較
```

---

## 1. Entra（Azure AD）で 2つ目のアプリを登録

Microsoft Entra 管理センター（<https://entra.microsoft.com>）または Azure ポータル → **アプリの登録**。

1. **新規登録** をクリック。
   - 名前: 例 `DataverseGW-SP2`
   - サポートされるアカウントの種類: **この組織ディレクトリのみ（単一テナント）**
   - リダイレクト URI: **不要**（ClientSecret＝クライアント資格情報フローのため）
2. 登録後の「概要」で以下を控える:
   - **アプリケーション (クライアント) ID** → `SP2_CLIENT_ID`
   - **ディレクトリ (テナント) ID** → 既存 1つ目 SP と同一のはず（`DATAVERSE_TENANT_ID`）

> API のアクセス許可（Dynamics CRM 等）の追加は **不要**。サーバー間（S2S）認証では、権限は Dataverse 側の
> アプリケーションユーザーに割り当てるセキュリティロールで決まる（デリゲート権限は使わない）。

## 2. クライアントシークレットを発行

1. 登録したアプリ → **証明書とシークレット** → **新しいクライアント シークレット**。
2. 説明・有効期限を設定して追加。
3. 表示された **値（Value）** を安全に控える → `SP2_CLIENT_SECRET`（**この画面を離れると再表示不可**）。

> 証明書認証を使う場合は、代わりに証明書をアップロードし拇印を控える（後述の設定で `Auth=Certificate`＋`CertThumbprint`）。

## 3. Dataverse にアプリケーションユーザーを作成

Power Platform 管理センター（<https://admin.powerplatform.microsoft.com>）。

1. **環境** → 対象環境（1つ目 SP と同じ環境）→ **設定** → **ユーザー + アクセス許可** → **アプリケーション ユーザー**。
2. **+ 新しいアプリ ユーザー** → **アプリの追加** → 手順 1 の `SP2_CLIENT_ID` のアプリを選択。
3. **ビジネス ユニット** を選択（既定でよい）。
4. **セキュリティ ロール** を割り当て。**対象テーブルへの書き込み権限（Create/Write、削除も測るなら Delete）を含むロール**を選ぶ。
   - 検証用途なら「システム カスタマイザー」等の広めのロールでも可。
   - 本番運用に近づけるなら、1つ目 SP と**同一のロール**を割り当てて条件を揃える。
5. 作成を確定。

> **確認**: 作成したアプリユーザーが対象テーブル（例 `poc_gadget`）へ Create/Write を持つこと。
> 権限不足だとスケールアウト時に当該 SP の書き込みが失敗し、ゲートウェイは主接続へ処理を寄せる（＝分散効果が出ない）。

## 4. ゲートウェイに 2つ目SPを設定して起動

2つ目 SP の資格情報を**環境変数**で与える（接続先 URL は起動時 `DATAVERSE_URL` と共有）。

### Windows (PowerShell)
```powershell
$env:DATAVERSE_URL           = "https://<org>.crm7.dynamics.com/"
$env:DATAVERSE_AUTH          = "ClientSecret"
$env:DATAVERSE_CLIENT_ID     = "<1つ目SP の Client ID>"
$env:DATAVERSE_CLIENT_SECRET = "<1つ目SP のシークレット>"
$env:DATAVERSE_TENANT_ID     = "<テナント GUID>"
# 2つ目SP（スケールアウト）
$env:Gateway__ScaleOut__Principals__0__ClientId     = "<SP2_CLIENT_ID>"
$env:Gateway__ScaleOut__Principals__0__ClientSecret = "<SP2_CLIENT_SECRET>"
$env:Gateway__ScaleOut__Principals__0__TenantId     = "<テナント GUID>"
$env:Gateway__ScaleOut__Principals__0__Auth         = "ClientSecret"
cd src; ./run.ps1
```

### macOS / bash
```bash
export DATAVERSE_URL="https://<org>.crm7.dynamics.com/"
export DATAVERSE_AUTH=ClientSecret
export DATAVERSE_CLIENT_ID="<1つ目SP>" DATAVERSE_CLIENT_SECRET="<1つ目SPシークレット>" DATAVERSE_TENANT_ID="<テナント>"
export Gateway__ScaleOut__Principals__0__ClientId="<SP2_CLIENT_ID>"
export Gateway__ScaleOut__Principals__0__ClientSecret="<SP2_CLIENT_SECRET>"
export Gateway__ScaleOut__Principals__0__TenantId="<テナント>"
export Gateway__ScaleOut__Principals__0__Auth=ClientSecret
./DataverseGW
```

**起動ログの確認**（成功していれば）:
```
スケールアウト SP#0(ClientId=<SP2_CLIENT_ID>)を接続
スケールアウト有効: 主+1 SP(2 SP)で書き込みを分散
```
> 証明書認証にする場合は `__Auth=Certificate` と `__CertThumbprint=<拇印>`（`__ClientSecret` は不要）。
> 3つ目以降は `Principals__1__...`, `Principals__2__...` と添字を増やす。

## 5. スループット実測（baseline 対 scaleout）

計測ハーネス `bench/measure_write_throughput.py` を使う（別ターミナルから）。

```bash
# (A) ベースライン: Gateway__ScaleOut を設定せずに起動したゲートウェイに対して
ROWS=200000 CHUNK=5000 LABEL=baseline-1sp python bench/measure_write_throughput.py

# (B) スケールアウト: 手順4(2SP)で起動したゲートウェイに対して同じワークロード
ROWS=200000 CHUNK=5000 LABEL=scaleout-2sp python bench/measure_write_throughput.py
```

- 出力 `throughput=... rows/sec` を A/B で比較。
- **重要（測定の勘所）**: 予算分離の効果は **単一 SP が throttle 上限に達する規模**で顕在化する。
  少量では往復レイテンシ×並列度が律速となり差が出にくい（dev 実測 §report 参照）。
  `ROWS` を十分大きく（数十万〜100 万行）し、A で単一 SP がスロットルされる状態を作ってから B と比較すること。
- dev 環境で大量投入する場合はサービス保護・ストレージ quota に配慮し、投入データは検証用（一意値・後で削除）とする。

## 6. 結果の記録

- A/B の rows/sec、実効 worker 数（ゲートウェイログ `スケールアウト: N SP × dop=… → worker=…`）、
  スロットル発生の有無を記録する。
- 期待: 単一 SP 飽和時、2 SP で概ね **最大 2 倍**（SP 数に比例、他要因で頭打ちあり）。

---

## 補足・トラブルシューティング

| 事象 | 対処 |
|---|---|
| 起動ログに「スケールアウト SP#0 …接続に失敗。このピアをスキップ」 | SP2 の資格情報誤り、またはアプリユーザー未作成/ロール未割当。手順 2〜3 を確認 |
| スケールアウトにしても速くならない | 単一 SP が未飽和（`ROWS` が小さい）。手順 5 の勘所どおり大規模ワークロードで測る |
| 当該 SP の書き込みだけ失敗する | SP2 のセキュリティロールに対象テーブルの Create/Write が無い。手順 3-4 を確認 |
| `Interactive/デバイスコードは非対応` 警告 | スケールアウトは ClientSecret / Certificate のみ。`__Auth` を修正 |
| シークレットの有効期限切れ | Entra で新しいシークレットを発行し環境変数を更新 |

*作成日: 2026-07-21 / 対象: WBS 4.2.4 / 関連: `doc/setup-guide-クライアント接続.md` §7.5*
