<#
.SYNOPSIS
  Deploy the .NET Teams Bot to the Azure VM.
  3 password prompts total (Ctrl+C to abort).
#>
param(
    [string]$VmHost     = "40.114.142.12",
    [string]$VmUser     = "azizadmin",
    [string]$ProjectDir = $PSScriptRoot
)
$ErrorActionPreference = "Stop"

$env:MICROSOFT_APP_ID        = "fec00466-c8a1-453d-9bc5-033b9212771b"
$env:MICROSOFT_APP_PASSWORD  = "FYk8Q~aCabxJXZcDRpmPxi8yRfhfooUichkQpbcL"
$env:MICROSOFT_APP_TENANT_ID = "95ea510f-498a-4089-af48-be9d9b9a2ccc"
$env:AZURE_SPEECH_KEY        = "FFFoQXXkyo0W2a6DMvreuVuJPL1TgLM7ehsYfN8Csyt0VogQKIlQJQQJ99CCACYeBjFXJ3w3AAAYACOGc1Hp"
$env:AZURE_SPEECH_REGION     = "eastus"
$env:CERT_THUMBPRINT         = "9DF94ED45B981B0553B172618F2E8B6927B4E3FE"
$env:MEDIA_SERVICE_FQDN      = "teams-bot.westeurope.cloudapp.azure.com"
$env:MEDIA_PUBLIC_PORT       = "8445"
$env:MEDIA_PRIVATE_PORT      = "8445"

Write-Host ""
Write-Host "======================================================" -ForegroundColor Cyan
Write-Host "  Teams Bot .NET Deployment  ->  ${VmUser}@${VmHost}" -ForegroundColor Cyan
Write-Host "  Two-process deploy: TeamsBot.Web (net9) + TeamsBot.Media (net48)" -ForegroundColor Cyan
Write-Host "  Password needed 4 times." -ForegroundColor Yellow
Write-Host "======================================================"  -ForegroundColor Cyan

# 1. Publish TeamsBot.Web -------------------------------------------------------
Write-Host "`n=== [1/6] Publishing TeamsBot.Web ===" -ForegroundColor Cyan
$pub = Join-Path $env:TEMP "tb-pub"
if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
dotnet publish $ProjectDir -c Release -o $pub
if ($LASTEXITCODE -ne 0) { Write-Host "Web publish failed." -ForegroundColor Red; exit 1 }

# Copy wwwroot if not already in publish output
$wsrc = Join-Path $ProjectDir "wwwroot\dashboard.html"
if (Test-Path $wsrc) {
    $wdst = Join-Path $pub "wwwroot"; New-Item $wdst -ItemType Directory -Force | Out-Null
    Copy-Item $wsrc (Join-Path $wdst "dashboard.html") -Force
}
Write-Host "Web published to $pub" -ForegroundColor Green

# 2. Build TeamsBot.Media (net48) -----------------------------------------------
Write-Host "`n=== [2/6] Building TeamsBot.Media (net48) ===" -ForegroundColor Cyan
$mediaProj = Join-Path $ProjectDir "TeamsBot.Media\TeamsBot.Media.csproj"
dotnet build $mediaProj -c Release
if ($LASTEXITCODE -ne 0) { Write-Host "TeamsBot.Media build failed." -ForegroundColor Red; exit 1 }
$mediaBin = Join-Path $ProjectDir "TeamsBot.Media\bin\Release\net48"
Write-Host "Media built → $mediaBin" -ForegroundColor Green

# 3. Write remote deploy script ------------------------------------------------
$appId = $env:MICROSOFT_APP_ID; $appPw = $env:MICROSOFT_APP_PASSWORD
$tid   = $env:MICROSOFT_APP_TENANT_ID; $sk = $env:AZURE_SPEECH_KEY
$sr    = $env:AZURE_SPEECH_REGION; $ct = $env:CERT_THUMBPRINT
$fqdn  = $env:MEDIA_SERVICE_FQDN
$mpub  = $env:MEDIA_PUBLIC_PORT; $mpriv = $env:MEDIA_PRIVATE_PORT

@"
`$n = "nssm.exe"

# ── Stop + remove old services ──────────────────────────────────────────────
foreach (`$svc in @("TeamsBot","TeamsBotDotNet","TeamsBotMedia")) {
    & `$n stop   `$svc confirm 2>`$null | Out-Null
    & `$n remove `$svc confirm 2>`$null | Out-Null
}
Start-Sleep 2

# ── Unzip web + media ───────────────────────────────────────────────────────
Expand-Archive C:\teamsbot.zip      -DestinationPath C:\bot-dotnet      -Force
Expand-Archive C:\teamsbot-media.zip -DestinationPath C:\bot-dotnet-media -Force
Remove-Item C:\teamsbot.zip, C:\teamsbot-media.zip -Force

# ── Create log directories (idempotent) ──────────────────────────────────────
New-Item C:\bot-dotnet\logs        -Force -ItemType Directory | Out-Null
New-Item C:\bot-dotnet-media\logs  -Force -ItemType Directory | Out-Null

# ── NSSM: TeamsBotMedia (net48 console, starts first) ────────────────────────
& `$n install TeamsBotMedia "C:\bot-dotnet-media\TeamsBot.Media.exe"
& `$n set TeamsBotMedia AppDirectory     "C:\bot-dotnet-media"
& `$n set TeamsBotMedia AppExit          Default Restart
& `$n set TeamsBotMedia AppRestartDelay  5000
& `$n set TeamsBotMedia AppStdout        "C:\bot-dotnet-media\logs\media.log"
& `$n set TeamsBotMedia AppStderr        "C:\bot-dotnet-media\logs\media-error.log"
& `$n set TeamsBotMedia AppRotateFiles   1
& `$n set TeamsBotMedia AppRotateSeconds 86400
& `$n set TeamsBotMedia Start            SERVICE_AUTO_START
& `$n set TeamsBotMedia DisplayName      "Teams Bot Media (net48)"
& `$n set TeamsBotMedia AppEnvironmentExtra 'MEDIA_SERVICE_FQDN=$fqdn' 'MEDIA_CERT_THUMBPRINT=$ct' 'MEDIA_PUBLIC_PORT=$mpub' 'MEDIA_PRIVATE_PORT=$mpriv' 'AZURE_SPEECH_KEY=$sk' 'AZURE_SPEECH_REGION=$sr' 'MICROSOFT_APP_ID=$appId'

# ── NSSM: TeamsBotDotNet (net9 web) ─────────────────────────────────────────
& `$n install TeamsBotDotNet "C:\bot-dotnet\TeamsBot.exe"
& `$n set TeamsBotDotNet AppDirectory     "C:\bot-dotnet"
& `$n set TeamsBotDotNet AppParameters    "--urls http://0.0.0.0:8000"
& `$n set TeamsBotDotNet AppExit          Default Restart
& `$n set TeamsBotDotNet AppRestartDelay  3000
& `$n set TeamsBotDotNet AppStdout        "C:\bot-dotnet\logs\bot.log"
& `$n set TeamsBotDotNet AppStderr        "C:\bot-dotnet\logs\bot-error.log"
& `$n set TeamsBotDotNet AppRotateFiles   1
& `$n set TeamsBotDotNet AppRotateSeconds 86400
& `$n set TeamsBotDotNet Start            SERVICE_AUTO_START
& `$n set TeamsBotDotNet DisplayName      "Teams Bot .NET Web"
& `$n set TeamsBotDotNet AppEnvironmentExtra 'MicrosoftAppId=$appId' 'MicrosoftAppPassword=$appPw' 'MicrosoftAppTenantId=$tid' 'AzureSpeechKey=$sk' 'AzureSpeechRegion=$sr' 'TunnelUrl=https://teams-bot.westeurope.cloudapp.azure.com' 'MediaPlatform__ServiceFqdn=$fqdn' 'MediaPlatform__InstancePublicPort=$mpub' 'MediaPlatform__CertThumbprint=$ct'

# ── Firewall rules ───────────────────────────────────────────────────────────
foreach (`$rule in @(
    @{Name="Teams Bot RMP TCP"; Proto="TCP"; Port=8445},
    @{Name="Teams Bot RMP UDP"; Proto="UDP"; Port=8445}
)) {
    if (-not (Get-NetFirewallRule -DisplayName `$rule.Name -EA SilentlyContinue)) {
        New-NetFirewallRule -DisplayName `$rule.Name -Direction Inbound -Protocol `$rule.Proto -LocalPort `$rule.Port -Action Allow | Out-Null
        Write-Host "Firewall rule created: `$(`$rule.Name)"
    }
}

# ── Register HttpListener URL ACL for media API port ─────────────────────────
netsh http add urlacl url=http://localhost:8446/ user="NT AUTHORITY\NETWORK SERVICE" 2>`$null | Out-Null
netsh http add urlacl url=http://localhost:8446/ user="BUILTIN\Administrators"        2>`$null | Out-Null

# ── Start services (media first, then web) ───────────────────────────────────
& `$n start TeamsBotMedia
Start-Sleep 6
& `$n start TeamsBotDotNet
Start-Sleep 10

`$msvc = Get-Service TeamsBotMedia    -EA SilentlyContinue
`$wsvc = Get-Service TeamsBotDotNet   -EA SilentlyContinue
Write-Host "TeamsBotMedia status : `$(`$msvc.Status)"
Write-Host "TeamsBotDotNet status: `$(`$wsvc.Status)"
if (`$wsvc.Status -ne "Running") { Write-Host "ERROR: TeamsBotDotNet not running!"; exit 1 }
Write-Host "SUCCESS: Both services RUNNING."
"@ | Set-Content (Join-Path $pub "deploy-remote.ps1") -Encoding UTF8
Write-Host "Remote script written." -ForegroundColor Green

# 4. Stop both services + wipe (password 1) ------------------------------------
Write-Host "`n=== [3/6] Stop old services + clear directories ===" -ForegroundColor Cyan
Write-Host ">>> PASSWORD PROMPT 1 of 4 <<<" -ForegroundColor Yellow
$stop = 'Stop-Service TeamsBotMedia -Force -EA SilentlyContinue; Stop-Service TeamsBotDotNet -Force -EA SilentlyContinue; Stop-Service TeamsBot -Force -EA SilentlyContinue; Start-Sleep 2; if (Test-Path C:\bot-dotnet) { Remove-Item C:\bot-dotnet -Recurse -Force }; if (Test-Path C:\bot-dotnet-media) { Remove-Item C:\bot-dotnet-media -Recurse -Force }; Write-Host DONE'
ssh "${VmUser}@${VmHost}" "powershell -NonInteractive -Command `"$stop`""
if ($LASTEXITCODE -ne 0) { Write-Host "Warning: cleanup had issues (likely first deploy). Continuing." -ForegroundColor Yellow }
Write-Host "VM directories ready." -ForegroundColor Green

# 5. Upload web zip (password 2) -----------------------------------------------
Write-Host "`n=== [4/6] Upload TeamsBot.Web (zip) ===" -ForegroundColor Cyan
Write-Host ">>> PASSWORD PROMPT 2 of 4 <<<" -ForegroundColor Yellow
$zip = Join-Path $env:TEMP "teamsbot.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "$pub\*" -DestinationPath $zip
scp $zip "${VmUser}@${VmHost}:C:/teamsbot.zip"
if ($LASTEXITCODE -ne 0) { Write-Host "SCP failed." -ForegroundColor Red; exit 1 }
Write-Host "Zip uploaded." -ForegroundColor Green

# 6. Upload media zip (password 3) ---------------------------------------------
Write-Host "`n=== [5/6] Upload TeamsBot.Media (zip) ===" -ForegroundColor Cyan
Write-Host ">>> PASSWORD PROMPT 3 of 4 <<<" -ForegroundColor Yellow
$mediaZip = Join-Path $env:TEMP "teamsbot-media.zip"
if (Test-Path $mediaZip) { Remove-Item $mediaZip }
Compress-Archive -Path "$mediaBin\*" -DestinationPath $mediaZip
scp $mediaZip "${VmUser}@${VmHost}:C:/teamsbot-media.zip"
if ($LASTEXITCODE -ne 0) { Write-Host "SCP (media) failed." -ForegroundColor Red; exit 1 }
Write-Host "Media zip uploaded." -ForegroundColor Green

# 7. Unzip both + configure both services + start (password 4) ----------------
Write-Host "`n=== [6/6] Configure + start services ===" -ForegroundColor Cyan
Write-Host ">>> PASSWORD PROMPT 4 of 4 <<<" -ForegroundColor Yellow
$remote = 'Expand-Archive C:\teamsbot.zip -DestinationPath C:\bot-dotnet -Force; Expand-Archive C:\teamsbot-media.zip -DestinationPath C:\bot-dotnet-media -Force; Remove-Item C:\teamsbot.zip -EA SilentlyContinue; Remove-Item C:\teamsbot-media.zip -EA SilentlyContinue; New-Item C:\bot-dotnet\logs -Force -ItemType Directory; New-Item C:\bot-dotnet-media\logs -Force -ItemType Directory; powershell -ExecutionPolicy Bypass -File C:\bot-dotnet\deploy-remote.ps1'
ssh "${VmUser}@${VmHost}" "powershell -NonInteractive -Command `"$remote`""
if ($LASTEXITCODE -ne 0) {
    Write-Host "Remote setup failed." -ForegroundColor Red
    Write-Host "Check logs: ssh ${VmUser}@${VmHost} 'type C:\bot-dotnet-media\logs\media-error.log'"
    exit 1
}

# 8. Health check --------------------------------------------------------------
Write-Host "`n=== Health check ===" -ForegroundColor Cyan
Write-Host "Waiting 15 seconds for startup..." -ForegroundColor Yellow
Start-Sleep 15
try {
    $h = Invoke-RestMethod -Uri "https://teams-bot.westeurope.cloudapp.azure.com/health" -TimeoutSec 20
    Write-Host "Health: $($h | ConvertTo-Json -Compress)" -ForegroundColor Green
} catch {
    Write-Host "Health check failed: $($_.Exception.Message)" -ForegroundColor Yellow
    Write-Host "The service may still be starting. Try again in 30 seconds." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "=========================================================" -ForegroundColor Green
Write-Host "  Deployment complete!" -ForegroundColor Green
Write-Host "=========================================================" -ForegroundColor Green
Write-Host "  Health    : https://teams-bot.westeurope.cloudapp.azure.com/health"
Write-Host "  Dashboard : https://teams-bot.westeurope.cloudapp.azure.com/dashboard"
Write-Host "  Media log : ssh ${VmUser}@${VmHost}  then:  type C:\bot-dotnet-media\logs\media-error.log"
Write-Host "  Bot log   : ssh ${VmUser}@${VmHost}  then:  type C:\bot-dotnet\logs\bot-error.log"
Write-Host ""
Write-Host "MANUAL STEPS STILL REQUIRED:" -ForegroundColor Yellow
Write-Host "  1. Azure Portal > VM > Networking > Add inbound rules:"
Write-Host "       TCP 8445  (RMP media — if not already open)"
Write-Host "       UDP 8445  (RMP SRTP  — required for audio)"
Write-Host "  2. Azure Portal > Bot Services > your bot > Configuration >"
Write-Host "     Messaging endpoint: https://teams-bot.westeurope.cloudapp.azure.com/api/messages"
