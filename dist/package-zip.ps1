# Build the portable ZIP distributable (app + install/uninstall scripts).
# ASCII-only. Output: dist/out/DataverseGW-<version>-win-x64.zip
# Version defaults to VersionPrefix in src/Directory.Build.props (single source of truth).
param(
    [string]$Version,
    [switch]$SkipPublish
)
$ErrorActionPreference = 'Stop'
$root   = Split-Path -Parent $PSScriptRoot
if (-not $Version) {
    $props = Join-Path $root 'src/Directory.Build.props'
    $Version = ([xml](Get-Content $props)).SelectSingleNode('//VersionPrefix').InnerText.Trim()
    if (-not $Version) { throw "VersionPrefix not found in $props" }
}
Write-Host "Version: $Version"
$out    = Join-Path $PSScriptRoot 'out'
$app    = Join-Path $out 'app'
$stage  = Join-Path $out "stage/DataverseGW-$Version"

if (-not $SkipPublish) { & (Join-Path $PSScriptRoot 'publish.ps1') }
if (-not (Test-Path $app)) { throw "Published app not found. Run dist/publish.ps1 first." }

if (Test-Path (Split-Path $stage)) { Remove-Item (Split-Path $stage) -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

Copy-Item (Join-Path $app '*') (New-Item -ItemType Directory -Path (Join-Path $stage 'app') -Force) -Recurse -Force
Copy-Item (Join-Path $PSScriptRoot 'install/*') (New-Item -ItemType Directory -Path (Join-Path $stage 'install') -Force) -Force
Copy-Item (Join-Path $PSScriptRoot 'DISTRIB-README.md') $stage -Force -ErrorAction SilentlyContinue

$zip = Join-Path $out "DataverseGW-$Version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Write-Host "OK -> $zip"
Get-Item $zip | Select-Object Name, @{n='SizeMB';e={[math]::Round($_.Length/1MB,1)}}
