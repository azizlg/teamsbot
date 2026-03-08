<#
.SYNOPSIS
  Import the Caddy TLS certificate into the Windows Local Machine certificate store
  so the RMP SDK can use it for media channel encryption on port 8445.

.DESCRIPTION
  Caddy stores Let's Encrypt certs as PEM files under C:\CaddyData (or the path
  configured via CADDY_DATA_DIR). This script:
    1. Finds the cert + private key PEM files for your domain
    2. Combines them into a temporary PFX
    3. Imports the PFX into LocalMachine\My
    4. Prints the thumbprint to paste into appsettings.json

.NOTES
  Run as Administrator.
  Requires: openssl.exe on PATH (install via winget install ShiningLight.OpenSSL)
#>
param(
    [string]$Domain    = "teams-bot.westeurope.cloudapp.azure.com",
    [string]$CaddyData = "C:\Users\azizadmin\AppData\Roaming\Caddy",
    [string]$PfxPass   = "temp-rmp-cert-1234"
)

$ErrorActionPreference = "Stop"

Write-Host "=== Teams Bot — Certificate Setup ===" -ForegroundColor Cyan
Write-Host "Domain:    $Domain"
Write-Host "CaddyData: $CaddyData"

# ── 1. Find Caddy cert files ──────────────────────────────────────────────────
$certBase = Join-Path $CaddyData "certificates\acme-v02.api.letsencrypt.org-directory\$Domain"
$certFile = Join-Path $certBase "$Domain.crt"
$keyFile  = Join-Path $certBase "$Domain.key"

# Try alternative Caddy v2 paths
if (!(Test-Path $certFile)) {
    $certBase = Join-Path $CaddyData "pki\authorities\local"
    $certFile = Get-ChildItem "$CaddyData\certificates" -Recurse -Filter "*.crt" |
                Where-Object { $_.Name -notlike "*ca*" } |
                Sort-Object LastWriteTime -Descending |
                Select-Object -First 1 -ExpandProperty FullName
    $keyFile  = $certFile -replace "\.crt$", ".key"
}

if (!(Test-Path $certFile) -or !(Test-Path $keyFile)) {
    Write-Error @"
Could not find Caddy certificate files at:
  $certFile
  $keyFile

Options:
  A) Set -CaddyData to your Caddy data directory
  B) Use a self-signed cert (see option below)

To create a self-signed cert instead, run:
  New-SelfSignedCertificate -DnsName '$Domain' -CertStoreLocation Cert:\LocalMachine\My -KeyAlgorithm RSA -KeyLength 2048 -NotAfter (Get-Date).AddYears(2)
Then get the thumbprint with:
  Get-ChildItem Cert:\LocalMachine\My | Sort-Object NotAfter -Descending | Select-Object Thumbprint,Subject | Format-Table
"@
}

Write-Host "Found cert: $certFile" -ForegroundColor Green
Write-Host "Found key:  $keyFile"  -ForegroundColor Green

# ── 2. Convert PEM → PFX using openssl ───────────────────────────────────────
$pfxPath = Join-Path $env:TEMP "rmp-cert.pfx"
Write-Host "Converting to PFX..."

try {
    openssl pkcs12 -export -inkey $keyFile -in $certFile -out $pfxPath -passout "pass:$PfxPass"
} catch {
    Write-Error "openssl failed. Install with: winget install ShiningLight.OpenSSL`n$_"
}

# ── 3. Import PFX into LocalMachine\My ───────────────────────────────────────
Write-Host "Importing PFX into LocalMachine\My..."

$securePass = ConvertTo-SecureString -String $PfxPass -Force -AsPlainText
$imported   = Import-PfxCertificate -FilePath $pfxPath `
    -CertStoreLocation Cert:\LocalMachine\My `
    -Password $securePass `
    -Exportable

Write-Host ""
Write-Host "✅ Certificate imported successfully!" -ForegroundColor Green
Write-Host ""
Write-Host "Thumbprint: $($imported.Thumbprint)" -ForegroundColor Yellow
Write-Host ""
Write-Host "Add this to appsettings.json (or NSSM env vars):" -ForegroundColor Cyan
Write-Host "  `"MediaPlatform`": {"
Write-Host "    `"ServiceFqdn`":      `"$Domain`","
Write-Host "    `"CertThumbprint`":   `"$($imported.Thumbprint)`","
Write-Host "    `"InstancePublicPort`": 8445"
Write-Host "  }"
Write-Host ""
Write-Host "Or as NSSM env var: MediaPlatform__CertThumbprint=$($imported.Thumbprint)"

# ── 4. Clean up temp PFX ─────────────────────────────────────────────────────
Remove-Item $pfxPath -Force -ErrorAction SilentlyContinue
Write-Host "Temp PFX removed."
