# Per-user install for the Dataverse GW (no admin required).
# Copies the app to %LOCALAPPDATA%\Programs\DataverseGW, puts a launcher on the
# user PATH, and prints the environment variables the gateway needs.
# ASCII-only.
param(
    [string]$Source = (Join-Path $PSScriptRoot '..\out\app')  # published app folder
)
$ErrorActionPreference = 'Stop'

$appName = 'DataverseGW'
$target  = Join-Path $env:LOCALAPPDATA "Programs\$appName"
$exeName = "$appName.exe"

if (-not (Test-Path (Join-Path $Source $exeName))) {
    throw "Published app not found under '$Source'. Run dist/publish.ps1 first."
}

# Stop a running instance (port 32010 sidecar) before replacing files.
Get-Process $appName -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

Write-Host "Installing to $target ..."
if (Test-Path $target) { Remove-Item $target -Recurse -Force }
New-Item -ItemType Directory -Path $target -Force | Out-Null
Copy-Item (Join-Path $Source '*') $target -Recurse -Force

# Add install dir to the user PATH (idempotent) so 'DataverseGW' is launchable.
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if (($userPath -split ';') -notcontains $target) {
    [Environment]::SetEnvironmentVariable('Path', ($userPath.TrimEnd(';') + ';' + $target), 'User')
    Write-Host "Added to user PATH: $target"
}

# Start Menu shortcut.
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$lnk = Join-Path $startMenu 'Dataverse GW.lnk'
$ws = New-Object -ComObject WScript.Shell
$sc = $ws.CreateShortcut($lnk)
$sc.TargetPath = Join-Path $target $exeName
$sc.WorkingDirectory = $target
$sc.Description = 'Dataverse GW (Arrow Flight SQL, localhost:32010)'
$sc.Save()

Write-Host ''
Write-Host "Installed. Launch with: $exeName   (or the Start Menu shortcut)"
Write-Host ''
Write-Host 'Before first run, set these USER environment variables (auth):'
Write-Host '  DATAVERSE_URL           https://<org>.crm7.dynamics.com/'
Write-Host '  DATAVERSE_AUTH          ClientSecret | Certificate | Interactive | DeviceCode'
Write-Host '  DATAVERSE_TENANT_ID     <tenant guid>          (ClientSecret/Certificate)'
Write-Host '  DATAVERSE_CLIENT_ID     <app registration id>'
Write-Host '  DATAVERSE_CLIENT_SECRET <secret>               (ClientSecret only; do NOT commit)'
Write-Host ''
Write-Host 'JDBC URL: jdbc:arrow-flight-sql://localhost:32010/?useEncryption=false'
