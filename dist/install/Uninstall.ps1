# Per-user uninstall for the Dataverse GW. ASCII-only.
$ErrorActionPreference = 'Stop'
$appName = 'DataverseGW'
$target  = Join-Path $env:LOCALAPPDATA "Programs\$appName"

Get-Process $appName -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

if (Test-Path $target) { Remove-Item $target -Recurse -Force; Write-Host "Removed $target" }

# Remove from user PATH.
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$kept = ($userPath -split ';' | Where-Object { $_ -and $_ -ne $target }) -join ';'
[Environment]::SetEnvironmentVariable('Path', $kept, 'User')

$lnk = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Dataverse GW.lnk'
if (Test-Path $lnk) { Remove-Item $lnk -Force }

Write-Host 'Uninstalled. (Auth env vars and %LOCALAPPDATA%\DataverseGW data left intact.)'
