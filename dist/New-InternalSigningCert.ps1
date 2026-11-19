# Create a self-signed CODE-SIGNING certificate for INTERNAL distribution and export
# its public part (.cer) for trust distribution via Active Directory Group Policy.
# ASCII-only. The private key stays in the current user's certificate store (Cert:\CurrentUser\My).
#
# Internal-only: Windows trusts this after the public .cer is deployed to
# "Trusted Publishers" (and, for a self-signed cert, "Trusted Root") via GPO.
# For broader/external distribution use a publicly-trusted CA (e.g. Azure Trusted Signing).
param(
    [string]$Subject   = 'CN=Dataverse GW (Code Signing)',
    [int]   $Years     = 3,
    [string]$ExportCer = (Join-Path $PSScriptRoot 'out\DataverseGW-InternalCodeSigning.cer')
)
$ErrorActionPreference = 'Stop'

$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject `
    -CertStoreLocation 'Cert:\CurrentUser\My' -KeyUsage DigitalSignature `
    -KeyExportPolicy Exportable -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears($Years)

$dir = Split-Path $ExportCer
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
Export-Certificate -Cert $cert -FilePath $ExportCer | Out-Null   # public cert only (safe to distribute)

Write-Host "Thumbprint : $($cert.Thumbprint)"
Write-Host "Subject    : $($cert.Subject)"
Write-Host "Expires    : $($cert.NotAfter.ToString('yyyy-MM-dd'))"
Write-Host "Public cert: $ExportCer   (deploy to Trusted Publishers/Root via GPO)"
Write-Host ""
Write-Host "Sign with:  dist\sign.ps1 -File <msi> -Thumbprint $($cert.Thumbprint)"
