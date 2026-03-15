<#
.SYNOPSIS
  Safely deploys TeamsBot.Web + TeamsBot.Media to the Azure VM.

.DESCRIPTION
  This deploy flow is hardened for the exact issues we debugged in production:
  - publishes the web project explicitly instead of the whole solution
  - builds and deploys the x64 media host only
  - validates the media cert thumbprint before touching services
  - stages new bits beside the old deployment and keeps timestamped backups
  - verifies service state and localhost health before declaring success
#>
param(
    [string]$VmHost     = "40.114.142.12",
    [string]$VmUser     = "azizadmin",
    [string]$ProjectDir = $PSScriptRoot,
    [switch]$AllowDirtyWorktree,
    [switch]$SkipExternalHealthCheck
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Require-Command([string]$Name)
{
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue))
    {
        throw "Required command '$Name' was not found in PATH."
    }
}

function Require-Value([string]$Name, [string]$Value)
{
    if ([string]::IsNullOrWhiteSpace($Value))
    {
        throw "Required configuration value '$Name' is missing or empty."
    }
}

function Get-PeMachine([string]$Path)
{
    $resolved = (Resolve-Path $Path).Path
    $fs = [System.IO.File]::OpenRead($resolved)
    try
    {
        $br = New-Object System.IO.BinaryReader($fs)
        $fs.Position = 0x3C
        $peOffset = $br.ReadInt32()
        $fs.Position = $peOffset + 4
        $machine = $br.ReadUInt16()
        switch ($machine)
        {
            0x14c  { return "x86" }
            0x8664 { return "x64" }
            0x200  { return "IA64" }
            default { return ("0x{0:X}" -f $machine) }
        }
    }
    finally
    {
        $fs.Dispose()
    }
}

$appSettingsPath = Join-Path $ProjectDir "appsettings.json"
if (-not (Test-Path $appSettingsPath))
{
    throw "appsettings.json not found at $appSettingsPath"
}

$appSettings = Get-Content $appSettingsPath -Raw | ConvertFrom-Json

$deployConfig = [ordered]@{
    MicrosoftAppId       = [string]$appSettings.MicrosoftAppId
    MicrosoftAppPassword = [string]$appSettings.MicrosoftAppPassword
    MicrosoftAppTenantId = [string]$appSettings.MicrosoftAppTenantId
    AzureSpeechKey       = [string]$appSettings.AzureSpeechKey
    AzureSpeechRegion    = [string]$appSettings.AzureSpeechRegion
    TunnelUrl            = [string]$appSettings.TunnelUrl
    MediaServiceFqdn     = [string]$appSettings.MediaPlatform.ServiceFqdn
    CertThumbprint       = [string]$appSettings.MediaPlatform.CertThumbprint
    MediaPublicPort      = [string]$appSettings.MediaPlatform.InstancePublicPort
    MediaPrivatePort     = [string]$appSettings.MediaPlatform.InstancePrivatePort
}

foreach ($entry in $deployConfig.GetEnumerator())
{
    Require-Value $entry.Key $entry.Value
}

Require-Command "dotnet"
Require-Command "ssh"
Require-Command "scp"
Require-Command "git"

$webProject = Join-Path $ProjectDir "TeamsBot.csproj"
$mediaProject = Join-Path $ProjectDir "TeamsBot.Media\TeamsBot.Media.csproj"
if (-not (Test-Path $webProject))   { throw "TeamsBot.csproj not found at $webProject" }
if (-not (Test-Path $mediaProject)) { throw "TeamsBot.Media.csproj not found at $mediaProject" }

$webOut     = Join-Path $env:TEMP "tb-web-publish"
$mediaOut   = Join-Path $ProjectDir "TeamsBot.Media\bin\Release\net48\win-x64"
$webZip     = Join-Path $env:TEMP "teamsbot-web-safe.zip"
$mediaZip   = Join-Path $env:TEMP "teamsbot-media-safe.zip"
$healthUri  = "$($deployConfig.TunnelUrl.TrimEnd('/'))/health"

$gitStatus = git -C $ProjectDir status --short --untracked-files=no
if ($LASTEXITCODE -ne 0)
{
    throw "Unable to read git status for $ProjectDir"
}

if ($gitStatus -and -not $AllowDirtyWorktree)
{
    throw "Working tree is dirty. Commit or stash your changes first, or rerun with -AllowDirtyWorktree if this is intentional."
}

$gitCommit = git -C $ProjectDir rev-parse --short HEAD
if ($LASTEXITCODE -ne 0)
{
    throw "Unable to read current git commit for $ProjectDir"
}

if (Test-Path $webOut)   { Remove-Item $webOut -Recurse -Force }
if (Test-Path $webZip)   { Remove-Item $webZip -Force }
if (Test-Path $mediaZip) { Remove-Item $mediaZip -Force }

Write-Host ""
Write-Host "======================================================" -ForegroundColor Cyan
Write-Host "  Teams Bot Safe Deployment  ->  ${VmUser}@${VmHost}" -ForegroundColor Cyan
Write-Host "  Web project  : TeamsBot.csproj" -ForegroundColor Cyan
Write-Host "  Media target : net48 / win-x64" -ForegroundColor Cyan
Write-Host "  Media cert   : $($deployConfig.CertThumbprint)" -ForegroundColor Cyan
Write-Host "  Git commit   : $gitCommit" -ForegroundColor Cyan
if ($gitStatus)
{
    Write-Host "  Git status   : dirty (allowed by flag)" -ForegroundColor Yellow
}
Write-Host "======================================================" -ForegroundColor Cyan

Write-Host "`n=== [1/6] Publishing TeamsBot.Web ===" -ForegroundColor Cyan
dotnet publish $webProject -c Release -o $webOut
if ($LASTEXITCODE -ne 0) { throw "TeamsBot.Web publish failed." }
Write-Host "Web published to $webOut" -ForegroundColor Green

Write-Host "`n=== [2/6] Building TeamsBot.Media (x64 only) ===" -ForegroundColor Cyan
dotnet build $mediaProject -c Release -r win-x64
if ($LASTEXITCODE -ne 0) { throw "TeamsBot.Media build failed." }
if (-not (Test-Path $mediaOut)) { throw "Expected x64 media output not found at $mediaOut" }

$mediaExe = Join-Path $mediaOut "TeamsBot.Media.exe"
$speechCore = Join-Path $mediaOut "Microsoft.CognitiveServices.Speech.core.dll"
if (-not (Test-Path $mediaExe))   { throw "Missing media executable: $mediaExe" }
if (-not (Test-Path $speechCore)) { throw "Missing Speech native DLL: $speechCore" }

$mediaExeMachine = Get-PeMachine $mediaExe
$speechMachine   = Get-PeMachine $speechCore
if ($mediaExeMachine -ne "x64") { throw "TeamsBot.Media.exe must be x64, found $mediaExeMachine" }
if ($speechMachine -ne "x64")   { throw "Microsoft.CognitiveServices.Speech.core.dll must be x64, found $speechMachine" }

Write-Host "Media built to $mediaOut" -ForegroundColor Green
Write-Host "Verified media architecture: exe=$mediaExeMachine, speechcore=$speechMachine" -ForegroundColor Green

Write-Host "`n=== [3/6] Packaging artifacts ===" -ForegroundColor Cyan
Compress-Archive -Path "$webOut\*" -DestinationPath $webZip
Compress-Archive -Path "$mediaOut\*" -DestinationPath $mediaZip
Write-Host "Created $webZip" -ForegroundColor Green
Write-Host "Created $mediaZip" -ForegroundColor Green

$appId  = $deployConfig.MicrosoftAppId
$appPw  = $deployConfig.MicrosoftAppPassword
$tid    = $deployConfig.MicrosoftAppTenantId
$sk     = $deployConfig.AzureSpeechKey
$sr     = $deployConfig.AzureSpeechRegion
$ct     = $deployConfig.CertThumbprint
$fqdn   = $deployConfig.MediaServiceFqdn
$tunnel = $deployConfig.TunnelUrl.TrimEnd('/')
$mpub   = $deployConfig.MediaPublicPort
$mpriv  = $deployConfig.MediaPrivatePort

@"
Set-StrictMode -Version Latest
`$ErrorActionPreference = "Stop"

function Get-PeMachine([string]`$Path)
{
    `$fs = [System.IO.File]::OpenRead(`$Path)
    try
    {
        `$br = New-Object System.IO.BinaryReader(`$fs)
        `$fs.Position = 0x3C
        `$peOffset = `$br.ReadInt32()
        `$fs.Position = `$peOffset + 4
        `$machine = `$br.ReadUInt16()
        switch (`$machine)
        {
            0x14c  { return "x86" }
            0x8664 { return "x64" }
            0x200  { return "IA64" }
            default { return ("0x{0:X}" -f `$machine) }
        }
    }
    finally
    {
        `$fs.Dispose()
    }
}

function Ensure-NssmService([string]`$Name, [string]`$Application)
{
    if (-not (Get-Service `$Name -ErrorAction SilentlyContinue))
    {
        & nssm.exe install `$Name `$Application | Out-Null
    }
}

function Configure-Services
{
    Ensure-NssmService "TeamsBotMedia" "C:\bot-dotnet-media\TeamsBot.Media.exe"
    & nssm.exe set TeamsBotMedia AppDirectory     "C:\bot-dotnet-media" | Out-Null
    & nssm.exe set TeamsBotMedia AppExit          Default Restart | Out-Null
    & nssm.exe set TeamsBotMedia AppRestartDelay  5000 | Out-Null
    & nssm.exe set TeamsBotMedia AppStdout        "C:\bot-dotnet-media\logs\media.log" | Out-Null
    & nssm.exe set TeamsBotMedia AppStderr        "C:\bot-dotnet-media\logs\media-error.log" | Out-Null
    & nssm.exe set TeamsBotMedia AppRotateFiles   1 | Out-Null
    & nssm.exe set TeamsBotMedia AppRotateSeconds 86400 | Out-Null
    & nssm.exe set TeamsBotMedia Start            SERVICE_AUTO_START | Out-Null
    & nssm.exe set TeamsBotMedia DisplayName      "Teams Bot Media (net48 x64)" | Out-Null
    & nssm.exe set TeamsBotMedia AppEnvironmentExtra 'MEDIA_SERVICE_FQDN=$fqdn' 'MEDIA_CERT_THUMBPRINT=$ct' 'MEDIA_PUBLIC_PORT=$mpub' 'MEDIA_PRIVATE_PORT=$mpriv' 'AZURE_SPEECH_KEY=$sk' 'AZURE_SPEECH_REGION=$sr' 'MICROSOFT_APP_ID=$appId' | Out-Null

    Ensure-NssmService "TeamsBotDotNet" "C:\bot-dotnet\TeamsBot.exe"
    & nssm.exe set TeamsBotDotNet AppDirectory     "C:\bot-dotnet" | Out-Null
    & nssm.exe set TeamsBotDotNet AppParameters    "--urls http://0.0.0.0:8000" | Out-Null
    & nssm.exe set TeamsBotDotNet AppExit          Default Restart | Out-Null
    & nssm.exe set TeamsBotDotNet AppRestartDelay  3000 | Out-Null
    & nssm.exe set TeamsBotDotNet AppStdout        "C:\bot-dotnet\logs\bot.log" | Out-Null
    & nssm.exe set TeamsBotDotNet AppStderr        "C:\bot-dotnet\logs\bot-error.log" | Out-Null
    & nssm.exe set TeamsBotDotNet AppRotateFiles   1 | Out-Null
    & nssm.exe set TeamsBotDotNet AppRotateSeconds 86400 | Out-Null
    & nssm.exe set TeamsBotDotNet Start            SERVICE_AUTO_START | Out-Null
    & nssm.exe set TeamsBotDotNet DisplayName      "Teams Bot .NET Web" | Out-Null
    & nssm.exe set TeamsBotDotNet AppEnvironmentExtra 'MicrosoftAppId=$appId' 'MicrosoftAppPassword=$appPw' 'MicrosoftAppTenantId=$tid' 'AzureSpeechKey=$sk' 'AzureSpeechRegion=$sr' 'TunnelUrl=$tunnel' 'MediaPlatform__ServiceFqdn=$fqdn' 'MediaPlatform__InstancePublicPort=$mpub' 'MediaPlatform__CertThumbprint=$ct' | Out-Null
}

function Stop-ServiceIfPresent([string]`$Name)
{
    `$svc = Get-Service `$Name -ErrorAction SilentlyContinue
    if (`$svc)
    {
        if (`$svc.Status -ne "Stopped")
        {
            Stop-Service -Name `$Name -Force -ErrorAction Stop
            `$svc.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
        }
    }
}

function Start-ServiceAndWait([string]`$Name, [int]`$TimeoutSeconds)
{
    Start-Service -Name `$Name -ErrorAction Stop
    `$svc = Get-Service `$Name -ErrorAction Stop
    `$svc.WaitForStatus("Running", [TimeSpan]::FromSeconds(`$TimeoutSeconds))
    `$svc = Get-Service `$Name -ErrorAction Stop
    if (`$svc.Status -ne "Running")
    {
        throw "Service `$Name failed to reach Running state. Current status: `$(`$svc.Status)"
    }
}

function Validate-Cert
{
    `$cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object Thumbprint -eq '$ct' | Select-Object -First 1
    if (-not `$cert) { throw "Certificate '$ct' was not found in LocalMachine\My." }
    if (-not `$cert.HasPrivateKey) { throw "Certificate '$ct' does not have a private key." }

    `$pk = `$cert.PrivateKey
    if (-not `$pk) { throw "Certificate '$ct' private key could not be loaded." }

    `$provider = `$pk.CspKeyContainerInfo.ProviderName
    `$keySpec  = [string]`$pk.CspKeyContainerInfo.KeyNumber
    if (`$provider -ne "Microsoft RSA SChannel Cryptographic Provider")
    {
        throw "Certificate '$ct' provider must be 'Microsoft RSA SChannel Cryptographic Provider', found '`$provider'."
    }
    if (`$keySpec -ne "Exchange")
    {
        throw "Certificate '$ct' KeySpec must be Exchange, found '`$keySpec'."
    }
}

`$webDir        = "C:\bot-dotnet"
`$mediaDir      = "C:\bot-dotnet-media"
`$webZip        = "C:\teamsbot.zip"
`$mediaZip      = "C:\teamsbot-media.zip"
`$stamp         = Get-Date -Format "yyyyMMdd-HHmmss"
`$webBackup     = "C:\bot-dotnet-backup-`$stamp"
`$mediaBackup   = "C:\bot-dotnet-media-backup-`$stamp"
`$backupCreated = `$false

Validate-Cert

try
{
    Stop-ServiceIfPresent "TeamsBotDotNet"
    Stop-ServiceIfPresent "TeamsBotMedia"
    Start-Sleep -Seconds 2

    if (Test-Path `$webDir)
    {
        Move-Item `$webDir `$webBackup
        `$backupCreated = `$true
    }

    if (Test-Path `$mediaDir)
    {
        Move-Item `$mediaDir `$mediaBackup
        `$backupCreated = `$true
    }

    New-Item `$webDir   -ItemType Directory -Force | Out-Null
    New-Item `$mediaDir -ItemType Directory -Force | Out-Null

    Expand-Archive `$webZip   -DestinationPath `$webDir   -Force
    Expand-Archive `$mediaZip -DestinationPath `$mediaDir -Force
    Remove-Item `$webZip, `$mediaZip -Force

    if ((Get-PeMachine "C:\bot-dotnet-media\TeamsBot.Media.exe") -ne "x64")
    {
        throw "Deployed TeamsBot.Media.exe is not x64."
    }
    if ((Get-PeMachine "C:\bot-dotnet-media\Microsoft.CognitiveServices.Speech.core.dll") -ne "x64")
    {
        throw "Deployed Microsoft.CognitiveServices.Speech.core.dll is not x64."
    }

    New-Item "C:\bot-dotnet\logs"       -ItemType Directory -Force | Out-Null
    New-Item "C:\bot-dotnet-media\logs" -ItemType Directory -Force | Out-Null

    Configure-Services

    foreach (`$rule in @(
        @{Name="Teams Bot RMP TCP"; Proto="TCP"; Port=$mpub},
        @{Name="Teams Bot RMP UDP"; Proto="UDP"; Port=$mpub}
    ))
    {
        if (-not (Get-NetFirewallRule -DisplayName `$rule.Name -ErrorAction SilentlyContinue))
        {
            New-NetFirewallRule -DisplayName `$rule.Name -Direction Inbound -Protocol `$rule.Proto -LocalPort `$rule.Port -Action Allow | Out-Null
        }
    }

    netsh http add urlacl url=http://localhost:8446/ user="NT AUTHORITY\NETWORK SERVICE" 2>`$null | Out-Null
    netsh http add urlacl url=http://localhost:8446/ user="BUILTIN\Administrators"        2>`$null | Out-Null

    Start-ServiceAndWait "TeamsBotMedia" 8
    Start-ServiceAndWait "TeamsBotDotNet" 10

    `$health = Invoke-RestMethod -Uri "http://localhost:8000/health" -TimeoutSec 20
    if (`$health.status -ne "healthy")
    {
        throw "Local health check returned unexpected payload: `$(`$health | ConvertTo-Json -Compress)"
    }

    Write-Host "Web backup  : `$webBackup"
    Write-Host "Media backup: `$mediaBackup"
    Write-Host "SUCCESS: deployment verified."
}
catch
{
    Write-Host "DEPLOY FAILED: `$($_.Exception.Message)" -ForegroundColor Red

    Stop-ServiceIfPresent "TeamsBotDotNet"
    Stop-ServiceIfPresent "TeamsBotMedia"
    Start-Sleep -Seconds 2

    if (Test-Path `$webDir)   { Remove-Item `$webDir   -Recurse -Force }
    if (Test-Path `$mediaDir) { Remove-Item `$mediaDir -Recurse -Force }

    if (Test-Path `$webBackup)   { Move-Item `$webBackup   `$webDir }
    if (Test-Path `$mediaBackup) { Move-Item `$mediaBackup `$mediaDir }

    if ((Test-Path `$webDir) -and (Test-Path `$mediaDir))
    {
        Configure-Services
        try { Start-ServiceAndWait "TeamsBotMedia" 8 } catch { }
        try { Start-ServiceAndWait "TeamsBotDotNet" 10 } catch { }
        Write-Host "Rollback attempted." -ForegroundColor Yellow
    }

    throw
}
"@ | Set-Content (Join-Path $webOut "deploy-remote.ps1") -Encoding UTF8

Write-Host "`n=== [4/6] Uploading web package ===" -ForegroundColor Cyan
Write-Host ">>> PASSWORD PROMPT 1 of 3 <<<" -ForegroundColor Yellow
scp $webZip "${VmUser}@${VmHost}:C:/teamsbot.zip"
if ($LASTEXITCODE -ne 0) { throw "Failed to upload $webZip" }
Write-Host "Uploaded web package." -ForegroundColor Green

Write-Host "`n=== [5/6] Uploading media package ===" -ForegroundColor Cyan
Write-Host ">>> PASSWORD PROMPT 2 of 3 <<<" -ForegroundColor Yellow
scp $mediaZip "${VmUser}@${VmHost}:C:/teamsbot-media.zip"
if ($LASTEXITCODE -ne 0) { throw "Failed to upload $mediaZip" }
Write-Host "Uploaded media package." -ForegroundColor Green

Write-Host "`n=== [6/6] Applying remote deployment ===" -ForegroundColor Cyan
Write-Host ">>> PASSWORD PROMPT 3 of 3 <<<" -ForegroundColor Yellow
$remoteCommand = "& { `$stage = 'C:\bot-dotnet-stage'; try { Remove-Item `$stage -Recurse -Force -ErrorAction SilentlyContinue; Expand-Archive C:\teamsbot.zip -DestinationPath `$stage -Force; powershell -ExecutionPolicy Bypass -File `"`$stage\deploy-remote.ps1`" } finally { Remove-Item `$stage -Recurse -Force -ErrorAction SilentlyContinue } }"
ssh "${VmUser}@${VmHost}" "powershell -NonInteractive -Command `"$remoteCommand`""
if ($LASTEXITCODE -ne 0)
{
    throw "Remote deployment failed. Inspect the VM logs and backup folders."
}

if ($SkipExternalHealthCheck)
{
    Write-Host "`n=== Post-Deploy Health Check ===" -ForegroundColor Cyan
    Write-Host "Skipped external health check by request. Local VM health was already verified during deployment." -ForegroundColor Yellow
}
else
{
    Write-Host "`n=== Post-Deploy Health Check ===" -ForegroundColor Cyan
    Write-Host "Waiting 10 seconds for external health..." -ForegroundColor Yellow
    Start-Sleep 10

    try
    {
        $health = Invoke-RestMethod -Uri $healthUri -TimeoutSec 20
        Write-Host "Health: $($health | ConvertTo-Json -Compress)" -ForegroundColor Green
    }
    catch
    {
        Write-Host "External health check failed: $($_.Exception.Message)" -ForegroundColor Yellow
        Write-Host "Local deploy succeeded, but the reverse proxy/public endpoint should be checked." -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "=========================================================" -ForegroundColor Green
Write-Host "  Safe deployment complete" -ForegroundColor Green
Write-Host "=========================================================" -ForegroundColor Green
Write-Host "  Health    : $healthUri"
Write-Host "  Dashboard : $($deployConfig.TunnelUrl.TrimEnd('/'))/dashboard"
Write-Host "  Bot logs  : ssh ${VmUser}@${VmHost} `"type C:\bot-dotnet\logs\bot.log`""
Write-Host "  Media logs: ssh ${VmUser}@${VmHost} `"type C:\bot-dotnet-media\logs\media.log`""
Write-Host ""
Write-Host "Backups are kept on the VM as C:\bot-dotnet-backup-<timestamp> and C:\bot-dotnet-media-backup-<timestamp>." -ForegroundColor Yellow
