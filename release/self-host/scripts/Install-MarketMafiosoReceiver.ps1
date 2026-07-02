param(
    [string]$PublicOrigin = "",
    [string]$DashboardUsername = "",
    [switch]$Force,
    [switch]$NonInteractive
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

function Get-PackageRoot {
    return Split-Path -Parent $PSScriptRoot
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

    throw "Docker was not found. Install Docker Desktop first, start it, then open a new PowerShell window. Official guide: https://docs.docker.com/desktop/setup/install/windows-install/"
}

function Invoke-DockerChecked {
    param(
        [string]$Docker,
        [string[]]$Arguments,
        [string]$FailureMessage
    )

    & $Docker @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw $FailureMessage
    }
}

function Test-DockerReady {
    param([string]$Docker)

    & $Docker version | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Docker is installed but is not ready. Start Docker Desktop, wait until it says Docker is running, then try again."
    }

    & $Docker compose version | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Docker Compose is not available. Update Docker Desktop, then try again."
    }
}

function Read-Defaulted {
    param(
        [string]$Prompt,
        [string]$DefaultValue
    )

    $value = Read-Host "$Prompt [$DefaultValue]"
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $DefaultValue
    }

    return $value.Trim()
}

function Read-YesNo {
    param(
        [string]$Prompt,
        [bool]$DefaultYes
    )

    $suffix = if ($DefaultYes) { "Y/n" } else { "y/N" }
    $value = Read-Host "$Prompt [$suffix]"
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $DefaultYes
    }

    return $value.Trim().StartsWith("y", [StringComparison]::OrdinalIgnoreCase)
}

function Get-NormalizedOrigin {
    param([string]$Origin)

    $trimmed = $Origin.Trim().TrimEnd("/")
    if ([string]::IsNullOrWhiteSpace($trimmed)) {
        throw "Public origin cannot be blank."
    }

    $parsedUri = $null
    if (-not [Uri]::TryCreate($trimmed, [UriKind]::Absolute, [ref]$parsedUri)) {
        throw "Public origin must be a full URL, for example http://localhost:5088."
    }

    return $trimmed
}

function Write-Section {
    param([string]$Text)

    Write-Host ""
    Write-Host $Text
    Write-Host ("-" * $Text.Length)
}

$root = Get-PackageRoot
$configDir = Join-Path $root "config"
$envPath = Join-Path $configDir "marketmafioso.env"
$composePath = Join-Path $configDir "compose.yaml"

Write-Section "MarketMafioso Receiver Installer"
Write-Host "This wizard installs the private receiver backend on this computer."
Write-Host "It will create config\marketmafioso.env and data\marketmafioso\marketmafioso.db."

if ((Test-Path -LiteralPath $envPath) -and -not $Force) {
    throw "config\marketmafioso.env already exists. Run scripts\Update-MarketMafiosoReceiver.ps1 to update, or re-run this installer with -Force to replace configuration."
}

$docker = Get-DockerCommand
Test-DockerReady -Docker $docker

if ([string]::IsNullOrWhiteSpace($DashboardUsername)) {
    $DashboardUsername = if ($NonInteractive) {
        "marketmafioso"
    }
    else {
        Read-Defaulted -Prompt "Dashboard username" -DefaultValue "marketmafioso"
    }
}

if ([string]::IsNullOrWhiteSpace($PublicOrigin)) {
    if ($NonInteractive) {
        $PublicOrigin = "http://localhost:5088"
    }
    else {
        $localOnly = Read-YesNo -Prompt "Will you use the receiver only on this computer?" -DefaultYes $true
        if ($localOnly) {
            $PublicOrigin = "http://localhost:5088"
        }
        else {
            $PublicOrigin = Read-Defaulted -Prompt "Public dashboard address" -DefaultValue "http://localhost:5088"
        }
    }
}

$PublicOrigin = Get-NormalizedOrigin -Origin $PublicOrigin
$DashboardUsername = $DashboardUsername.Trim()
if ([string]::IsNullOrWhiteSpace($DashboardUsername)) {
    throw "Dashboard username cannot be blank."
}

$clientKey = New-Secret
$dashboardPassword = New-Secret

New-Item -ItemType Directory -Path $configDir -Force | Out-Null

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
    "MarketMafioso__RequireDashboardAuth=true",
    "MarketMafioso__DashboardBootstrapUsername=$DashboardUsername",
    "MarketMafioso__DashboardBootstrapPassword=$dashboardPassword",
    "MarketMafioso__DashboardSessionMinutes=720"
)

Set-Content -LiteralPath $envPath -Value $lines -Encoding utf8

Push-Location $root
try {
    Invoke-DockerChecked -Docker $docker -Arguments @("compose", "-f", $composePath, "pull") -FailureMessage "docker compose pull failed."
    Invoke-DockerChecked -Docker $docker -Arguments @("compose", "-f", $composePath, "up", "-d") -FailureMessage "docker compose up failed."
}
finally {
    Pop-Location
}

$healthUrl = "http://localhost:5088/health"
$healthPassed = $false
$deadline = (Get-Date).AddSeconds(60)
do {
    try {
        $health = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 3
        if ($true -eq $health.ok) {
            $healthPassed = $true
            break
        }
    }
    catch {
        Start-Sleep -Seconds 2
    }
} while ((Get-Date) -lt $deadline)

Write-Section "Install Summary"
if ($healthPassed) {
    Write-Host "Health check passed: $healthUrl"
}
else {
    Write-Warning "The receiver was started, but the health check did not pass within 60 seconds. Check Docker Desktop and run: docker compose -f config\compose.yaml logs marketmafioso"
}

Write-Host "Dashboard: $PublicOrigin/"
Write-Host "Plugin Server URL: $PublicOrigin/inventory"
Write-Host "Plugin Client API Key: $clientKey"
Write-Host "Dashboard username: $DashboardUsername"
Write-Host "Dashboard password: $dashboardPassword"
Write-Host ""
Write-Host "Keep config\marketmafioso.env and data\. They contain the receiver configuration and database."
Write-Host "For setting explanations, read docs\RECEIVER-SETTINGS.md."
