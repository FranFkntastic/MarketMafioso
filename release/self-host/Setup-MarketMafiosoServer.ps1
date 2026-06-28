param(
    [string]$PublicOrigin = "",
    [string]$XivDataBaseUrl = "",
    [string]$DashboardUsername = "marketmafioso",
    [switch]$Force
)

$ErrorActionPreference = "Stop"

function New-Secret {
    $bytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $rng.GetBytes($bytes)
    }
    finally {
        $rng.Dispose()
    }

    return [Convert]::ToBase64String($bytes).TrimEnd("=").Replace("+", "-").Replace("/", "_")
}

function Get-DockerCommand {
    $command = Get-Command docker -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $dockerDesktopCommand = Join-Path $env:ProgramFiles "Docker\Docker\resources\bin\docker.exe"
    if (Test-Path -LiteralPath $dockerDesktopCommand) {
        return $dockerDesktopCommand
    }

    throw "Docker was not found. Install Docker Desktop or Docker Engine first."
}

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$envPath = Join-Path $root "marketmafioso.env"
$composePath = Join-Path $root "compose.yaml"

if ((Test-Path -LiteralPath $envPath) -and -not $Force) {
    throw "marketmafioso.env already exists. Re-run with -Force only if you want to replace it."
}

if ([string]::IsNullOrWhiteSpace($PublicOrigin)) {
    $PublicOrigin = Read-Host "Public dashboard origin, or press Enter for http://localhost:5088"
    if ([string]::IsNullOrWhiteSpace($PublicOrigin)) {
        $PublicOrigin = "http://localhost:5088"
    }
}

if ([string]::IsNullOrWhiteSpace($XivDataBaseUrl)) {
    $XivDataBaseUrl = Read-Host "XIV data URL for Market Acquisition item search, or press Enter to leave disabled"
}

$clientKey = New-Secret
$dashboardPassword = New-Secret

$lines = @(
    "ASPNETCORE_URLS=http://0.0.0.0:8080",
    "",
    "MarketMafioso__RequireApiKey=true",
    "MarketMafioso__ClientApiKey=$clientKey",
    "MarketMafioso__PreviousClientApiKey=",
    "",
    "MarketMafioso__BasePath=",
    "MarketMafioso__PublicOrigin=$PublicOrigin",
    "MarketMafioso__StorageLabel=self-hosted receiver storage",
    "MarketMafioso__DatabasePath=/data/marketmafioso.db",
    "",
    "MarketMafioso__RawJsonRetentionCount=20",
    "MarketMafioso__SnapshotRetentionCount=500",
    "MarketMafioso__DiagnosticsRetentionCount=5000",
    "",
    "MarketMafioso__XivDataBaseUrl=$XivDataBaseUrl",
    "",
    "MarketMafioso__RequireDashboardAuth=true",
    "MarketMafioso__DashboardBootstrapUsername=$DashboardUsername",
    "MarketMafioso__DashboardBootstrapPassword=$dashboardPassword",
    "MarketMafioso__DashboardSessionMinutes=720"
)

Set-Content -LiteralPath $envPath -Value $lines -Encoding utf8

$docker = Get-DockerCommand
Push-Location $root
try {
    & $docker compose -f $composePath pull
    if ($LASTEXITCODE -ne 0) {
        throw "docker compose pull failed."
    }

    & $docker compose -f $composePath up -d
    if ($LASTEXITCODE -ne 0) {
        throw "docker compose up failed."
    }
}
finally {
    Pop-Location
}

$healthUrl = "http://localhost:5088/health"
$deadline = (Get-Date).AddSeconds(60)
do {
    try {
        $health = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 3
        if ($true -eq $health.ok) {
            break
        }
    }
    catch {
        Start-Sleep -Seconds 2
    }
} while ((Get-Date) -lt $deadline)

Write-Host ""
Write-Host "MarketMafioso receiver is starting."
Write-Host "Dashboard: $PublicOrigin/"
Write-Host "Plugin server URL: $PublicOrigin/inventory"
Write-Host "Plugin client API key: $clientKey"
Write-Host "Dashboard username: $DashboardUsername"
Write-Host "Dashboard password: $dashboardPassword"
Write-Host ""
Write-Host "Keep marketmafioso.env and the data/ folder. They contain the receiver configuration and database."
