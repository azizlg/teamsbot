# Import Caddy Let's Encrypt cert into Windows cert store for RMP SDK
$certPath = "C:\temp\teams-bot.westeurope.cloudapp.azure.com.crt"
$keyPath  = "C:\temp\teams-bot.westeurope.cloudapp.azure.com.key"
$pfxPath  = "C:\temp\teams-bot.pfx"

# Use .NET to create PFX from PEM cert + key
$certPem = Get-Content $certPath -Raw
$keyPem  = Get-Content $keyPath -Raw

$cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPem($certPem, $keyPem)

# Export to PFX then re-import (needed for machine store with exportable private key)
$pfxBytes = $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, "")
[System.IO.File]::WriteAllBytes($pfxPath, $pfxBytes)

# Import into LocalMachine\My
$flags = [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::MachineKeySet -bor [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::PersistKeySet
$imported = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($pfxPath, "", $flags)

$store = New-Object System.Security.Cryptography.X509Certificates.X509Store("My", "LocalMachine")
$store.Open("ReadWrite")
$store.Add($imported)
$store.Close()

Write-Host "Imported cert thumbprint: $($imported.Thumbprint)"
Write-Host "Subject: $($imported.Subject)"
Write-Host "Issuer: $($imported.Issuer)"
Write-Host "Expires: $($imported.NotAfter)"
