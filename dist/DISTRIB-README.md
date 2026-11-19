# Dataverse GW — 配布パッケージ

Microsoft Dataverse を SQL（JDBC/ODBC/ADBC）で読み書きするためのローカルゲートウェイです。
`localhost:32010` で Arrow Flight SQL サーバーとして動作します（外部公開はしません）。

## 同梱物

```
app/                       自己完結の実行ファイル（.NET ランタイム同梱・別途インストール不要）
  DataverseGW.exe
install/
  Install.ps1              %LOCALAPPDATA%\Programs へ配置し PATH/スタートメニュー登録
  Uninstall.ps1            アンインストール
```

## インストール（管理者権限不要）

```powershell
powershell -ExecutionPolicy Bypass -File install\Install.ps1
```

## 認証設定（初回のみ・ユーザー環境変数）

| 変数 | 例 / 値 |
|---|---|
| `DATAVERSE_URL` | `https://<org>.crm7.dynamics.com/` |
| `DATAVERSE_AUTH` | `ClientSecret` / `Certificate` / `Interactive` / `DeviceCode` |
| `DATAVERSE_TENANT_ID` | テナント GUID（ClientSecret/Certificate） |
| `DATAVERSE_CLIENT_ID` | アプリ登録の Client ID |
| `DATAVERSE_CLIENT_SECRET` | シークレット（ClientSecret のみ。**共有・コミット厳禁**） |

> シークレットはコード/ドキュメントに直書きせず、ユーザー環境変数または資格情報ストアで管理してください。

## 起動

```powershell
DataverseGW.exe
```

起動すると次を表示します:

```
JDBC URL: jdbc:arrow-flight-sql://localhost:32010/?useEncryption=false
```

## クライアント接続

DBeaver / DataGrip / Python(ADBC) からの接続手順は、リポジトリの
`doc/setup-guide-クライアント接続.md` を参照してください。

## アンインストール

```powershell
powershell -ExecutionPolicy Bypass -File install\Uninstall.ps1
```
