# Code-sign a file (MSI or EXE) for INTERNAL distribution.
# ASCII-only. The certificate is NEVER committed; pass it in at sign time.
#
# Uses the cross-platform 'sign' .NET tool (dotnet tool install --global sign --prerelease)
# so no Windows SDK / signtool is required. Falls back to signtool.exe if present.
#
# Usage (choose one credential source):
#   dist\sign.ps1 -File out\DataverseGW-1.0.0-win-x64.msi -Thumbprint <sha1-thumbprint-in-CurrentUser\My>
#   dist\sign.ps1 -File <path> -PfxPath <company.pfx> -PfxPassword <pwd>
#
# The 'sign' tool selects the certificate by its SHA-256 fingerprint; this script derives
# that automatically from the SHA-1 thumbprint (store) or the PFX.
param(
    [Parameter(Mandatory = $true)][string]$File,
    [string]$Thumbprint,
    [string]$PfxPath,
    [string]$PfxPassword,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $File)) { throw "file not found: $File" }
$File = (Resolve-Path $File).Path

# Resolve the signing certificate and its SHA-256 fingerprint.
if ($Thumbprint) {
    $cert = Get-Item ("Cert:\CurrentUser\My\" + $Thumbprint) -ErrorAction SilentlyContinue
    if (-not $cert) { $cert = Get-Item ("Cert:\LocalMachine\My\" + $Thumbprint) -ErrorAction SilentlyContinue }
    if (-not $cert) { throw "certificate not found in store for thumbprint $Thumbprint" }
} elseif ($PfxPath) {
    if (-not (Test-Path $PfxPath)) { throw "pfx not found: $PfxPath" }
    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 `
        ((Resolve-Path $PfxPath).Path, $PfxPassword)
} else {
    throw 'Provide -Thumbprint (cert in CurrentUser\My) or -PfxPath (+ -PfxPassword).'
}
$sha256 = [System.BitConverter]::ToString(
    $cert.GetCertHash([System.Security.Cryptography.HashAlgorithmName]::SHA256)).Replace('-', '')

$dir  = Split-Path $File
$name = Split-Path $File -Leaf

# Prefer the 'sign' tool; fall back to signtool.exe.
$signTool = Get-Command sign -ErrorAction SilentlyContinue
if (-not $signTool -and (Test-Path "$env:USERPROFILE\.dotnet\tools\sign.exe")) {
    $signTool = "$env:USERPROFILE\.dotnet\tools\sign.exe"
}

if ($signTool) {
    $signArgs = @('code', 'certificate-store', $name, '-cfp', $sha256,
                  '-fd', 'sha256', '-t', $TimestampUrl, '-td', 'sha256', '-b', $dir)
    if ($PfxPath) { $signArgs += @('-cf', (Resolve-Path $PfxPath).Path); if ($PfxPassword) { $signArgs += @('-p', $PfxPassword) } }
    $exe = if ($signTool -is [string]) { $signTool } else { $signTool.Source }
    & $exe @signArgs
    if ($LASTEXITCODE -ne 0) { throw "sign failed ($LASTEXITCODE)" }
} else {
    $signtoolExe = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if (-not $signtoolExe) {
        throw "Neither 'sign' tool nor signtool.exe found. Install: dotnet tool install --global sign --prerelease"
    }
    $common = @('sign', '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256')
    if ($Thumbprint) { & signtool @common '/sha1' $Thumbprint $File }
    else { & signtool @common '/f' $PfxPath '/p' $PfxPassword $File }
    if ($LASTEXITCODE -ne 0) { throw "signtool failed ($LASTEXITCODE)" }
}

$sig = Get-AuthenticodeSignature $File
Write-Host ("Signed: {0}" -f $File)
Write-Host ("  Signer     : {0}" -f $sig.SignerCertificate.Subject)
Write-Host ("  Status     : {0}" -f $sig.Status)
Write-Host ("  Timestamped: {0}" -f ($null -ne $sig.TimeStamperCertificate))
if ($sig.Status -ne 'Valid') {
    Write-Host "  (Self-signed internal cert: status is trusted only after the public cert is"
    Write-Host "   deployed to Trusted Publishers/Root via Group Policy. See dist/README.md.)"
}
