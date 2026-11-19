# Production gateway launcher (Phase 1).
# Loads User-scope env vars (per tejunsho 1.1.2 section 7) then starts Gateway.Host.
# ASCII-only (Windows PowerShell 5.1 reads .ps1 as the system ANSI codepage).
$ErrorActionPreference = 'Stop'

foreach ($name in 'DATAVERSE_URL','DATAVERSE_TENANT_ID','DATAVERSE_CLIENT_ID','DATAVERSE_CLIENT_SECRET','DATAVERSE_AUTH') {
    $val = [Environment]::GetEnvironmentVariable($name, 'User')
    if ($val) { Set-Item -Path "Env:$name" -Value $val }
}

if (-not $env:DATAVERSE_URL) {
    Write-Error 'DATAVERSE_URL is not set. See tejunsho 1.1.2 section 7.'
    exit 1
}

$env:DOTNET_ROLL_FORWARD = 'LatestMajor'

Push-Location $PSScriptRoot/Gateway.Host
try {
    dotnet run --no-build @args
} finally {
    Pop-Location
}
