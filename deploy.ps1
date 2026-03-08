<#
.SYNOPSIS
  Deploy the C# Teams Bot to the Azure VM and configure NSSM.

.DESCRIPTION
  1. Builds and publishes the .NET 8 project
  2. Copies the output to C:\bot-dotnet on the VM via SSH/SCP
  3. Removes the old Python TeamsBot service
  4. Creates/updates the TeamsBotDotNet NSSM service
  5. Opens port 8445 in Windows Firewall

.REQUIREMENTS
  - .NET 8 SDK installed locally
  - ssh.exe and scp.exe on PATH (built-in on Win10+)
  - NSSM already on the VM (already installed)
  - Run from the TeamsBot\ directory (or adjust $ProjectDir)
#>
param(
    [string]$VmHost   = "40.114.142.12",
    [string]$VmUser   = "azizadmin",
    [string]$VmPass   = "BotAdmin@2026!",    # Used only for display hints; use SSH key in prod
    [string]$VmDest   = "/C:/bot-dotnet",
    [string]$ProjectDir = $PSScriptRoot
)

$ErrorActionPreference = "Stop"

Write-Host "=== Teams Bot .NET Deploy ===" -ForegroundColor Cyan

# ── 1. Build + publish ────────────────────────────────────────────────────────
Write-Host "[1/5] Publishing .NET 8 release build..."
$publishDir = Join-Path $env:TEMP "teamsbot-publish"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

dotnet publish $ProjectDir `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -o $publishDir

Write-Host "Published to: $publishDir" -ForegroundColor Green

# ── 2. Copy dashboard.html into publish wwwroot ───────────────────────────────
$dashSrc = Join-Path $ProjectDir "wwwroot\dashboard.html"
$dashDst = Join-Path $publishDir "wwwroot\dashboard.html"
if (Test-Path $dashSrc) {
    New-Item (Join-Path $publishDir "wwwroot") -ItemType Directory -Force | Out-Null
    Copy-Item $dashSrc $dashDst -Force
    Write-Host "dashboard.html included" -ForegroundColor Green
} else {
    Write-Warning "wwwroot\dashboard.html not found — dashboard will be missing"
}

# ── 3. Deploy to VM via SCP ───────────────────────────────────────────────────
Write-Host "[2/5] Copying to VM $VmHost..."
Write-Host "  This may prompt for SSH password: $VmPass"

# Stop old service before overwriting files
$stopCmd = "powershell -NonInteractive -Command `"& { " +
    "Stop-Service TeamsBotDotNet -Force -ErrorAction SilentlyContinue; " +
    "Stop-Service TeamsBot -Force -ErrorAction SilentlyContinue; " +
    "if (Test-Path C:\bot-dotnet) { Remove-Item C:\bot-dotnet -Recurse -Force } " +
    "}`""
ssh "${VmUser}@${VmHost}" $stopCmd

# SCP the published output
scp -r "${publishDir}\*" "${VmUser}@${VmHost}:C:/bot-dotnet/"
Write-Host "Files copied" -ForegroundColor Green

# ── 4. Configure NSSM service on VM ──────────────────────────────────────────
Write-Host "[3/5] Configuring NSSM service on VM..."

$nssmCmds = @"
& {
    \$nssm = 'C:\ProgramData\chocolatey\lib\NSSM\tools\nssm.exe'

    # Remove old Python service
    & \$nssm stop  TeamsBot 2>`$null
    & \$nssm remove TeamsBot confirm 2>`$null

    # Install .NET service
    & \$nssm install TeamsBotDotNet 'C:\Program Files\dotnet\dotnet.exe'
    & \$nssm set TeamsBotDotNet AppParameters 'C:\bot-dotnet\TeamsBot.dll --urls http://0.0.0.0:8000'
    & \$nssm set TeamsBotDotNet AppDirectory   'C:\bot-dotnet'
    & \$nssm set TeamsBotDotNet AppExit Default Restart
    & \$nssm set TeamsBotDotNet AppStdout 'C:\bot-dotnet\logs\bot.log'
    & \$nssm set TeamsBotDotNet AppStderr 'C:\bot-dotnet\logs\bot-error.log'
    & \$nssm set TeamsBotDotNet Start SERVICE_AUTO_START
    & \$nssm set TeamsBotDotNet DisplayName 'Teams Bot (.NET)'

    # Environment variables (read from .env values)
    & \$nssm set TeamsBotDotNet AppEnvironmentExtra ':MicrosoftAppId=$env:MICROSOFT_APP_ID'
    & \$nssm set TeamsBotDotNet AppEnvironmentExtra '+MicrosoftAppPassword=$env:MICROSOFT_APP_PASSWORD'
    & \$nssm set TeamsBotDotNet AppEnvironmentExtra '+MicrosoftAppTenantId=$env:MICROSOFT_APP_TENANT_ID'
    & \$nssm set TeamsBotDotNet AppEnvironmentExtra '+AzureSpeechKey=$env:AZURE_SPEECH_KEY'
    & \$nssm set TeamsBotDotNet AppEnvironmentExtra '+AzureSpeechRegion=$env:AZURE_SPEECH_REGION'
    & \$nssm set TeamsBotDotNet AppEnvironmentExtra '+TunnelUrl=https://teams-bot.westeurope.cloudapp.azure.com'
    & \$nssm set TeamsBotDotNet AppEnvironmentExtra '+MediaPlatform__ServiceFqdn=teams-bot.westeurope.cloudapp.azure.com'
    & \$nssm set TeamsBotDotNet AppEnvironmentExtra '+MediaPlatform__CertThumbprint=$env:CERT_THUMBPRINT'
    & \$nssm set TeamsBotDotNet AppEnvironmentExtra '+MediaPlatform__InstancePublicPort=8445'

    New-Item 'C:\bot-dotnet\logs' -ItemType Directory -Force | Out-Null
    Write-Host 'NSSM configured'
}
"@

ssh "${VmUser}@${VmHost}" "powershell -NonInteractive -Command `"$nssmCmds`""

# ── 5. Open firewall port 8445 ────────────────────────────────────────────────
Write-Host "[4/5] Opening Windows Firewall port 8445 (RMP media)..."

$fwCmd = "powershell -NonInteractive -Command `"" +
    "New-NetFirewallRule -DisplayName 'Teams Bot RMP Media' " +
    "-Direction Inbound -Protocol TCP -LocalPort 8445 " +
    "-Action Allow -ErrorAction SilentlyContinue`""
ssh "${VmUser}@${VmHost}" $fwCmd

# ── 6. Start service ──────────────────────────────────────────────────────────
Write-Host "[5/5] Starting TeamsBotDotNet service..."

$startCmd = "powershell -NonInteractive -Command `"Start-Service TeamsBotDotNet`""
ssh "${VmUser}@${VmHost}" $startCmd

Write-Host ""
Write-Host "✅ Deploy complete!" -ForegroundColor Green
Write-Host ""
Write-Host "Dashboard: https://teams-bot.westeurope.cloudapp.azure.com/dashboard"
Write-Host "Health:    https://teams-bot.westeurope.cloudapp.azure.com/health"
Write-Host ""
Write-Host "⚠️  Don't forget:"
Write-Host "  1. Run setup-cert.ps1 on the VM first (if not done yet)"
Write-Host "  2. Set CERT_THUMBPRINT env var before running this script"
Write-Host "  3. Open port 8445 in Azure NSG (Network Security Group)"
Write-Host "     Azure Portal → VM → Networking → Add inbound port 8445 TCP"
