# Copy new RSA cert from Caddy, create PFX, import to machine store
$caddyCertDir = "C:\Windows\System32\config\systemprofile\AppData\Roaming\Caddy\certificates\acme-v02.api.letsencrypt.org-directory\teams-bot.westeurope.cloudapp.azure.com"
Copy-Item "$caddyCertDir\*" C:\temp\ -Force

$env:PATH += ";C:\Program Files\OpenSSL-Win64\bin"

# Create PFX
openssl pkcs12 -export -out C:\temp\teams-bot-rsa.pfx -inkey C:\temp\teams-bot.westeurope.cloudapp.azure.com.key -in C:\temp\teams-bot.westeurope.cloudapp.azure.com.crt -passout pass:bot123

# Import
certutil -importpfx -f -p bot123 My C:\temp\teams-bot-rsa.pfx

# Get thumbprint
$certs = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -eq "CN=teams-bot.westeurope.cloudapp.azure.com" }
foreach ($c in $certs) {
    Write-Host "Thumbprint: $($c.Thumbprint)  Issuer: $($c.Issuer)  HasKey: $($c.HasPrivateKey)"
}
