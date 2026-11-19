# Publish a self-contained, single-file win-x64 build of the gateway.
# ASCII-only (Windows PowerShell 5.1 reads .ps1 as the system ANSI codepage).
# Output: dist/out/app  (self-contained; bundles the .NET 8 runtime -> no runtime install needed)
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot          # repo root
$proj = Join-Path $root 'src/Gateway.Host/Gateway.Host.csproj'
$out  = Join-Path $PSScriptRoot 'out/app'

$env:DOTNET_ROLL_FORWARD = 'LatestMajor'          # SDK 10 box builds the net8.0 target

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

Write-Host "Publishing $proj ($Configuration/$Runtime, self-contained single-file)..."
dotnet publish $proj `
    -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none `
    -o $out
if ($LASTEXITCODE -ne 0) { throw "publish failed ($LASTEXITCODE)" }

$exe = Join-Path $out 'DataverseGW.exe'
if (-not (Test-Path $exe)) { throw "expected exe not found: $exe" }
Write-Host "OK -> $exe"
Get-Item $exe | Select-Object Name, @{n='SizeMB';e={[math]::Round($_.Length/1MB,1)}}
