# Delete old EC cert from Caddy storage and restart to get RSA cert
$caddyCertDir = "C:\Windows\System32\config\systemprofile\AppData\Roaming\Caddy\certificates\acme-v02.api.letsencrypt.org-directory\teams-bot.westeurope.cloudapp.azure.com"

if (Test-Path $caddyCertDir) {
    Remove-Item -Recurse -Force $caddyCertDir
    Write-Host "Deleted old Caddy cert directory"
} else {
    Write-Host "Cert directory not found"
}

# Restart Caddy to trigger new RSA cert issuance
nssm restart CaddyProxy
Write-Host "Caddy restarted - waiting for RSA cert issuance..."
Start-Sleep -Seconds 15

# Check if new cert was issued
if (Test-Path "$caddyCertDir\teams-bot.westeurope.cloudapp.azure.com.crt") {
    Write-Host "New cert found!"
    # Check key type
    $env:PATH += ";C:\Program Files\OpenSSL-Win64\bin"
    & openssl x509 -in "$caddyCertDir\teams-bot.westeurope.cloudapp.azure.com.crt" -noout -text 2>&1 | Select-String "Public-Key|Algorithm"
} else {
    Write-Host "Cert not yet issued, check logs"
    Get-ChildItem $caddyCertDir -ErrorAction SilentlyContinue
}
