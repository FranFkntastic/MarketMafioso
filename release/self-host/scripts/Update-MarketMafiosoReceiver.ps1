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

function Get-MarketMafiosoEnvValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -match "^$([regex]::Escape($Name))=(.*)$") {
            return $matches[1].Trim()
        }
    }

    return ""
}

function Get-HttpStatusCode {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Request
    )

    try {
        $response = Invoke-WebRequest @Request -UseBasicParsing -TimeoutSec 10
        return [int]$response.StatusCode
    }
    catch {
        if ($_.Exception.Response -and $_.Exception.Response.StatusCode) {
            return [int]$_.Exception.Response.StatusCode
        }

        throw
    }
}

function Invoke-WorkshopHostSmoke {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BaseUrl,

        [Parameter(Mandatory = $true)]
        [string]$ClientApiKey
    )

    if ([string]::IsNullOrWhiteSpace($ClientApiKey)) {
        Write-Warning "Skipping Workshop Host smoke checks because the client API key is blank."
        return
    }

    $headers = @{ "X-Api-Key" = $ClientApiKey }
    $capabilities = Invoke-RestMethod -Uri "$BaseUrl/api/capabilities" -Headers $headers -TimeoutSec 10
    $capabilityIds = @($capabilities.capabilities | ForEach-Object { $_.id })
    if ($capabilityIds -notcontains "craft.appraise") {
        throw "Workshop Host capabilities did not include craft.appraise."
    }

    $unauthenticatedQuoteStatus = Get-HttpStatusCode -Request @{
        Method = "Post"
        Uri = "$BaseUrl/api/craft/appraise"
        ContentType = "application/json"
        Body = "{}"
    }
    if ($unauthenticatedQuoteStatus -ne 401) {
        throw "Unauthenticated craft quote smoke returned HTTP $unauthenticatedQuoteStatus; expected 401."
    }

    $invalidQuoteStatus = Get-HttpStatusCode -Request @{
        Method = "Post"
        Uri = "$BaseUrl/api/craft/appraise"
        Headers = $headers
        ContentType = "application/json"
        Body = (@{ schemaVersion = 2; itemId = 2; itemName = "Fire Shard"; quantity = 1 } | ConvertTo-Json -Compress)
    }
    if ($invalidQuoteStatus -ne 400) {
        throw "Invalid-schema craft quote smoke returned HTTP $invalidQuoteStatus; expected 400."
    }

    Write-Host "Workshop Host quote smoke checks passed: $BaseUrl/api/capabilities and $BaseUrl/api/craft/appraise"
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
$healthPassed = $false
$deadline = (Get-Date).AddSeconds(60)
do {
    try {
        $health = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 3
        if ($true -eq $health.ok) {
            Write-Host "Health check passed at $healthUrl"
            $healthPassed = $true
            break
        }
    }
    catch {
        Start-Sleep -Seconds 2
    }
} while ((Get-Date) -lt $deadline)

if ($healthPassed) {
    $clientKey = Get-MarketMafiosoEnvValue -Path $envPath -Name "MarketMafioso__ClientApiKey"
    Invoke-WorkshopHostSmoke -BaseUrl "http://localhost:5088" -ClientApiKey $clientKey
}
else {
    Write-Warning "Workshop Host was updated, but the health check did not pass within 60 seconds. Check Docker Desktop and run: docker compose -f config\compose.yaml logs marketmafioso"
}

Write-Host "MarketMafioso Workshop Host update complete."
