param(
    [switch]$SkipBackup
)

$ErrorActionPreference = "Stop"

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

    throw "Docker was not found. Install Docker Desktop first, start it, then open a new PowerShell window."
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

$root = Get-PackageRoot
$configDir = Join-Path $root "config"
$envPath = Join-Path $configDir "marketmafioso.env"
$composePath = Join-Path $configDir "compose.yaml"
$dataDir = Join-Path (Join-Path $root "data") "marketmafioso"
$databasePath = Join-Path $dataDir "marketmafioso.db"

if (-not (Test-Path -LiteralPath $envPath)) {
    throw "config\marketmafioso.env does not exist. Run scripts\Install-MarketMafiosoReceiver.ps1 first."
}

$docker = Get-DockerCommand

if (-not $SkipBackup -and (Test-Path -LiteralPath $databasePath)) {
    $backupDir = Join-Path $root "backups"
    New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $backupPath = Join-Path $backupDir "marketmafioso-$stamp.db"
    Copy-Item -LiteralPath $databasePath -Destination $backupPath
    Write-Host "Backed up SQLite database to $backupPath"
}

Push-Location $root
try {
    Invoke-DockerChecked -Docker $docker -Arguments @("compose", "-f", $composePath, "pull") -FailureMessage "docker compose pull failed."
    Invoke-DockerChecked -Docker $docker -Arguments @("compose", "-f", $composePath, "up", "-d") -FailureMessage "docker compose up failed."
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
            Write-Host "Health check passed at $healthUrl"
            break
        }
    }
    catch {
        Start-Sleep -Seconds 2
    }
} while ((Get-Date) -lt $deadline)

Write-Host "MarketMafioso receiver update complete."
