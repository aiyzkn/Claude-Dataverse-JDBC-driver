# Dataverse GW を Windows サービスとして導入/削除する (4.2.2a 常駐サービス化)。
# ASCII only (PowerShell 5.1 は .ps1 を ANSI で読むため)。
#
# 使い方:
#   powershell -ExecutionPolicy Bypass -File dist\install-service.ps1 -Action install -ExePath "C:\path\DataverseGW.exe"
#   powershell -ExecutionPolicy Bypass -File dist\install-service.ps1 -Action uninstall
#   powershell -ExecutionPolicy Bypass -File dist\install-service.ps1 -Action status
#
# 注意:
#  - 管理者権限で実行すること。
#  - サービスはユーザー環境変数を継承しない。認証情報(DATAVERSE_URL/CLIENT_ID/TENANT_ID/CLIENT_SECRET,
#    DATAVERSE_AUTH)は「システム環境変数」に設定するか、実行アカウントのプロファイルに設定する。
#    認証情報はスクリプト/リポジトリに直書きしないこと。
#  - ネットワーク公開(非 localhost バインド)は Gateway:RequireToken=true 等の保護を前提とする(4.2.2c)。

param(
    [ValidateSet('install', 'uninstall', 'status')]
    [string]$Action = 'status',
    [string]$ServiceName = 'DataverseGW',
    [string]$ExePath
)

$ErrorActionPreference = 'Stop'

switch ($Action) {
    'install' {
        if (-not $ExePath) { throw "install には -ExePath <DataverseGW.exe の絶対パス> が必要です" }
        if (-not (Test-Path $ExePath)) { throw "実行ファイルが見つかりません: $ExePath" }
        Write-Host "サービス '$ServiceName' を作成します: $ExePath"
        # 既存があれば削除
        if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
            & sc.exe stop $ServiceName | Out-Null
            & sc.exe delete $ServiceName | Out-Null
            Start-Sleep -Seconds 2
        }
        New-Service -Name $ServiceName -BinaryPathName "`"$ExePath`"" -DisplayName "Dataverse GW" `
            -StartupType Automatic -Description "Arrow Flight SQL gateway for Microsoft Dataverse" | Out-Null
        Write-Host "作成完了。開始するには: Start-Service $ServiceName"
        Write-Host "認証情報はシステム環境変数(DATAVERSE_URL 等)に設定してから開始してください。"
    }
    'uninstall' {
        if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
            & sc.exe stop $ServiceName | Out-Null
            Start-Sleep -Seconds 2
            & sc.exe delete $ServiceName | Out-Null
            Write-Host "サービス '$ServiceName' を削除しました。"
        } else {
            Write-Host "サービス '$ServiceName' は存在しません。"
        }
    }
    'status' {
        $svc = Get-Service $ServiceName -ErrorAction SilentlyContinue
        if ($svc) { $svc | Format-List Name, Status, StartType, DisplayName }
        else { Write-Host "サービス '$ServiceName' は未登録です。" }
    }
}
