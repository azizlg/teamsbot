# Re-import the RSA cert with CSP key storage (not CNG) for RMP SDK compatibility
# First delete the old CNG-imported cert
$old = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Thumbprint -eq "4DE2376BE885516F2ABE6F2AC691D828E5E1C542" }
if ($old) {
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store("My", "LocalMachine")
    $store.Open("ReadWrite")
    $store.Remove($old)
    $store.Close()
    Write-Host "Removed old CNG cert"
}

# Re-create PFX with legacy CSP format
$env:PATH += ";C:\Program Files\OpenSSL-Win64\bin"
$certDir = "C:\Windows\System32\config\systemprofile\AppData\Roaming\Caddy\certificates\acme-v02.api.letsencrypt.org-directory\teams-bot.westeurope.cloudapp.azure.com"
Copy-Item "$certDir\*" C:\temp\ -Force

# Use openssl with -legacy -certpbe PBE-SHA1-3DES -keypbe PBE-SHA1-3DES -macalg sha1 for CSP compatibility
& openssl pkcs12 -export -out C:\temp\teams-bot-csp.pfx `
    -inkey C:\temp\teams-bot.westeurope.cloudapp.azure.com.key `
    -in C:\temp\teams-bot.westeurope.cloudapp.azure.com.crt `
    -passout pass:bot123 `
    -certpbe PBE-SHA1-3DES `
    -keypbe PBE-SHA1-3DES `
    -macalg sha1

if (Test-Path C:\temp\teams-bot-csp.pfx) {
    Write-Host "PFX created with legacy CSP format"
} else {
    Write-Host "PFX creation failed"
    exit 1
}

# Import using certutil with CSP provider specified
& certutil -importpfx -f -p bot123 -csp "Microsoft RSA SChannel Cryptographic Provider" My C:\temp\teams-bot-csp.pfx
Write-Host ""

# Find the new cert
$certs = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -eq "CN=teams-bot.westeurope.cloudapp.azure.com" -and $_.Issuer -like "*Let's Encrypt*" }
foreach ($c in $certs) {
    Write-Host "Thumbprint: $($c.Thumbprint)"
    Write-Host "Issuer: $($c.Issuer)"
    Write-Host "HasPrivateKey: $($c.HasPrivateKey)"
    # Check if private key is CSP
    try {
        $pk = $c.PrivateKey
        Write-Host "PrivateKey type: $($pk.GetType().Name)"
        Write-Host "CSP Provider: $($pk.CspKeyContainerInfo.ProviderName)"
    } catch {
        Write-Host "PrivateKey check error: $_"
    }
}
