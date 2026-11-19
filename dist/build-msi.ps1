# Build the MSI installer with WiX v4. Installs the WiX .NET tool locally if missing.
# ASCII-only. Output: dist/out/DataverseGW-<version>-win-x64.msi
# Signing is a separate, opt-in step (see dist/sign.ps1) that needs the org code-signing cert.
param(
    [string]$Version,
    [string]$Manufacturer = 'Dataverse GW',
    [switch]$SkipPublish
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Version) {
    $props = Join-Path $root 'src/Directory.Build.props'
    $Version = ([xml](Get-Content $props)).SelectSingleNode('//VersionPrefix').InnerText.Trim()
    if (-not $Version) { throw "VersionPrefix not found in $props" }
}
Write-Host "Version: $Version"
$out = Join-Path $PSScriptRoot 'out'
$app = Join-Path $out 'app'
$wxs = Join-Path $PSScriptRoot 'wix/Package.wxs'
$msi = Join-Path $out "DataverseGW-$Version-win-x64.msi"

if (-not $SkipPublish) { & (Join-Path $PSScriptRoot 'publish.ps1') }
if (-not (Test-Path $app)) { throw "Published app not found. Run dist/publish.ps1 first." }

# Ensure the WiX v4 CLI is available. Prefer the repo-local tool manifest
# (dist/dotnet-tools.json, pins WiX 4.0.5) so we do not touch the machine-global tool set.
$env:DOTNET_ROLL_FORWARD = 'LatestMajor'
$wixCmd = Get-Command wix -ErrorAction SilentlyContinue
if ($wixCmd) {
    $wix = { param($a) wix @a }
} else {
    Push-Location $PSScriptRoot
    try {
        if (Test-Path (Join-Path $PSScriptRoot 'dotnet-tools.json')) {
            dotnet tool restore 2>&1 | Out-Null           # restore pinned WiX from manifest
        } else {
            dotnet new tool-manifest | Out-Null
            dotnet tool install wix --version 4.0.5 2>&1 | Out-Null
        }
        $wix = { param($a) dotnet tool run wix @a }
    } finally { Pop-Location }
}

Write-Host "Building MSI ($Version, manufacturer '$Manufacturer')..."
Push-Location $PSScriptRoot
try {
    & $wix @('build', $wxs,
        '-d', "Version=$Version",
        '-d', "Manufacturer=$Manufacturer",
        '-bindpath', "pub=$app",
        '-arch', 'x64',
        '-o', $msi)
    if ($LASTEXITCODE -ne 0) { throw "wix build failed ($LASTEXITCODE)" }
} finally { Pop-Location }

Write-Host "OK -> $msi"
Get-Item $msi | Select-Object Name, @{n='SizeMB';e={[math]::Round($_.Length/1MB,1)}}
Write-Host "To code-sign: dist/sign.ps1 -File `"$msi`" -Thumbprint <cert-thumbprint>"
