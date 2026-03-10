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

Write-Host ""
Write-Host "======================================================" -ForegroundColor Cyan
Write-Host "  Teams Bot .NET Deployment  ->  ${VmUser}@${VmHost}" -ForegroundColor Cyan
Write-Host "  Password needed 3 times." -ForegroundColor Yellow
Write-Host "======================================================" -ForegroundColor Cyan

# 1. Publish -------------------------------------------------------------------
Write-Host "`n=== [1/5] Publishing ===" -ForegroundColor Cyan
$pub = Join-Path $env:TEMP "tb-pub"
if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
dotnet publish $ProjectDir -c Release -o $pub
if ($LASTEXITCODE -ne 0) { Write-Host "Publish failed." -ForegroundColor Red; exit 1 }

# Copy wwwroot if not already in publish output
$wsrc = Join-Path $ProjectDir "wwwroot\dashboard.html"
if (Test-Path $wsrc) {
    $wdst = Join-Path $pub "wwwroot"; New-Item $wdst -ItemType Directory -Force | Out-Null
    Copy-Item $wsrc (Join-Path $wdst "dashboard.html") -Force
}
Write-Host "Published to $pub" -ForegroundColor Green

# 2. Write remote setup script into publish dir --------------------------------
$appId = $env:MICROSOFT_APP_ID; $appPw = $env:MICROSOFT_APP_PASSWORD
$tid   = $env:MICROSOFT_APP_TENANT_ID; $sk = $env:AZURE_SPEECH_KEY
$sr    = $env:AZURE_SPEECH_REGION; $ct = $env:CERT_THUMBPRINT

@"
`$n = "nssm.exe"
& `$n stop   TeamsBot       confirm 2>`$null | Out-Null
& `$n remove TeamsBot       confirm 2>`$null | Out-Null
& `$n stop   TeamsBotDotNet confirm 2>`$null | Out-Null
& `$n remove TeamsBotDotNet confirm 2>`$null | Out-Null
Start-Sleep 2
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
& `$n set TeamsBotDotNet DisplayName      "Teams Bot .NET"
& `$n set TeamsBotDotNet AppEnvironmentExtra 'MicrosoftAppId=$appId' 'MicrosoftAppPassword=$appPw' 'MicrosoftAppTenantId=$tid' 'AzureSpeechKey=$sk' 'AzureSpeechRegion=$sr' 'TunnelUrl=https://teams-bot.westeurope.cloudapp.azure.com' 'MediaPlatform__ServiceFqdn=teams-bot.westeurope.cloudapp.azure.com' 'MediaPlatform__InstancePublicPort=8445' 'MediaPlatform__CertThumbprint=$ct'
Write-Host "NSSM configured."
if (-not (Get-NetFirewallRule -DisplayName "Teams Bot RMP" -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName "Teams Bot RMP" -Direction Inbound -Protocol TCP -LocalPort 8445 -Action Allow | Out-Null
    Write-Host "Firewall rule created."
} else { Write-Host "Firewall rule already exists." }
& `$n start TeamsBotDotNet
Start-Sleep 8
`$svc = Get-Service TeamsBotDotNet -ErrorAction SilentlyContinue
Write-Host "Service status: `$(`$svc.Status)"
if (`$svc.Status -ne "Running") { Write-Host "ERROR: Service not running!"; exit 1 }
Write-Host "SUCCESS: Service is RUNNING."
"@ | Set-Content (Join-Path $pub "deploy-remote.ps1") -Encoding UTF8
Write-Host "Remote script written." -ForegroundColor Green

# 3. Stop old service + wipe (password 1) -------------------------------------
Write-Host "`n=== [2/5] Stop old service + clear directory ===" -ForegroundColor Cyan
Write-Host ">>> PASSWORD PROMPT 1 of 3 <<<" -ForegroundColor Yellow
$stop = 'Stop-Service TeamsBotDotNet -Force -EA SilentlyContinue; Stop-Service TeamsBot -Force -EA SilentlyContinue; Start-Sleep 2; if (Test-Path C:\bot-dotnet){Remove-Item C:\bot-dotnet -Recurse -Force}; New-Item C:\bot-dotnet\logs -Force -ItemType Directory|Out-Null; Write-Host DONE'
ssh "${VmUser}@${VmHost}" "powershell -NonInteractive -Command `"$stop`""
if ($LASTEXITCODE -ne 0) { Write-Host "Warning: cleanup had issues (likely first deploy). Continuing." -ForegroundColor Yellow }
Write-Host "VM directory ready." -ForegroundColor Green

# 4. Zip + SCP single file (password 2) ---------------------------------------
Write-Host "`n=== [3/5] Upload files (zip) ===" -ForegroundColor Cyan
Write-Host ">>> PASSWORD PROMPT 2 of 3 <<<" -ForegroundColor Yellow
$zip = Join-Path $env:TEMP "teamsbot.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "$pub\*" -DestinationPath $zip
scp $zip "${VmUser}@${VmHost}:C:/teamsbot.zip"
if ($LASTEXITCODE -ne 0) { Write-Host "SCP failed." -ForegroundColor Red; exit 1 }
Write-Host "Zip uploaded." -ForegroundColor Green

# 5. Unzip + NSSM + start (password 3) ----------------------------------------
Write-Host "`n=== [4/5] Unzip + configure service + start ===" -ForegroundColor Cyan
Write-Host ">>> PASSWORD PROMPT 3 of 3 <<<" -ForegroundColor Yellow
$remote = 'Expand-Archive C:\teamsbot.zip -DestinationPath C:\bot-dotnet -Force; Remove-Item C:\teamsbot.zip; powershell -ExecutionPolicy Bypass -File C:\bot-dotnet\deploy-remote.ps1'
ssh "${VmUser}@${VmHost}" "powershell -NonInteractive -Command `"$remote`""
if ($LASTEXITCODE -ne 0) {
    Write-Host "Remote setup failed. SSH in and check: type C:\bot-dotnet\logs\bot-error.log" -ForegroundColor Red
    exit 1
}

# 6. Health check --------------------------------------------------------------
Write-Host "`n=== [5/5] Health check ===" -ForegroundColor Cyan
Write-Host "Waiting 12 seconds for startup..." -ForegroundColor Yellow
Start-Sleep 12
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
Write-Host "  Logs      : ssh ${VmUser}@${VmHost}  then:  type C:\bot-dotnet\logs\bot-error.log"
Write-Host ""
Write-Host "MANUAL STEPS STILL REQUIRED:" -ForegroundColor Yellow
Write-Host "  1. Azure Portal > VM > Networking > Add inbound rule: TCP port 8445"
Write-Host "  2. Azure Portal > Bot Services > your bot > Configuration >"
Write-Host "     Messaging endpoint: https://teams-bot.westeurope.cloudapp.azure.com/api/messages"
