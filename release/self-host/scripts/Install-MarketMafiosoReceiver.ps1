param(
    [string]$PublicOrigin = "http://localhost:5088",
    [string]$DashboardUsername = "marketmafioso",
    [switch]$Force,
    # Accepted for older scripts; the installer no longer prompts.
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
    $craftQuoteAvailable = $capabilityIds -contains "craft.appraise"

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

    if ($craftQuoteAvailable) {
        Write-Host "Workshop Host advertises the configured Craft Architect appraisal API."
    } else {
        Write-Host "Craft Architect appraisal is not configured; Workshop Host correctly omits craft.appraise."
    }
    Write-Host "Workshop Host quote route auth and schema smoke checks passed."
}

function Get-ErrorDetail {
    param([System.Management.Automation.ErrorRecord]$ErrorRecord)

    $response = $ErrorRecord.Exception.Response
    if ($response -and $response.StatusCode) {
        return "HTTP $([int]$response.StatusCode) ($($response.StatusCode))"
    }

    return $ErrorRecord.Exception.Message
}

function New-PluginConnectionCode {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BaseUrl,

        [Parameter(Mandatory = $true)]
        [string]$HostBaseUrl,

        [Parameter(Mandatory = $true)]
        [string]$Username,

        [Parameter(Mandatory = $true)]
        [string]$Password
    )

    $session = $null
    $loginBody = @{ username = $Username; password = $Password } | ConvertTo-Json -Compress
    try {
        Invoke-RestMethod -Method Post -Uri "$BaseUrl/auth/login" -ContentType "application/json" -Body $loginBody -SessionVariable session -TimeoutSec 10 | Out-Null
    }
    catch {
        $detail = Get-ErrorDetail -ErrorRecord $_
        throw "dashboard sign-in as '$Username' at $BaseUrl/auth/login failed: $detail"
    }

    try {
        $created = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/settings/client-pairing" -WebSession $session -TimeoutSec 10
    }
    catch {
        $detail = Get-ErrorDetail -ErrorRecord $_
        throw "connection code request to $BaseUrl/api/settings/client-pairing failed: $detail"
    }

    if ([string]::IsNullOrWhiteSpace($created.token)) {
        throw "connection code request to $BaseUrl/api/settings/client-pairing returned no code."
    }

    # Same format the dashboard's Connect game client button produces: mmf1_<base64url(host)>.<token>
    $encodedHost = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($HostBaseUrl)).TrimEnd("=").Replace("+", "-").Replace("/", "_")
    return [pscustomobject]@{
        Code = "mmf1_$encodedHost.$($created.token)"
        ExpiresAt = ([DateTimeOffset]$created.expiresAtUtc).ToLocalTime()
    }
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

Write-Section "MarketMafioso Workshop Host Installer"
Write-Host "This installs the private Workshop Host backend on this computer."
Write-Host "The underlying service is still named receiver in scripts, routes, and Docker image names for compatibility."
Write-Host "It will create config\marketmafioso.env and data\marketmafioso\marketmafioso.db."
Write-Host "Dashboard address: $PublicOrigin. For remote hosting, read docs\ADVANCED-CONFIGURATION.md."

if ((Test-Path -LiteralPath $envPath) -and -not $Force) {
    throw "config\marketmafioso.env already exists. Run scripts\Update-MarketMafiosoReceiver.ps1 to update, or re-run this installer with -Force to replace configuration."
}

$docker = Get-DockerCommand
Test-DockerReady -Docker $docker

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
    "MarketMafioso__StorageLabel=self-hosted Workshop Host storage",
    "MarketMafioso__DatabasePath=/data/marketmafioso.db",
    "",
    "MarketMafioso__RawJsonRetentionCount=20",
    "MarketMafioso__InventoryHistoryRetentionPerCharacter=100",
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
    Invoke-WorkshopHostSmoke -BaseUrl "http://localhost:5088" -ClientApiKey $clientKey
}
else {
    Write-Warning "Workshop Host was started, but the health check did not pass within 60 seconds. Check Docker Desktop and run: docker compose -f config\compose.yaml logs marketmafioso"
}

$connection = $null
$connectionFailure = "the health check did not pass"
if ($healthPassed) {
    try {
        $connection = New-PluginConnectionCode -BaseUrl "http://localhost:5088" -HostBaseUrl $PublicOrigin -Username $DashboardUsername -Password $dashboardPassword
    }
    catch {
        $connectionFailure = $_.Exception.Message
    }
}

Write-Section "Connect The Plugin"
if ($null -ne $connection) {
    Write-Host "In game, type /mmf, open Settings > General / Server Connection,"
    Write-Host "paste this into Connect with code, then press Connect:"
    Write-Host ""
    Write-Host "  $($connection.Code)"
    Write-Host ""
    $minutesLeft = [Math]::Max(1, [int][Math]::Round(($connection.ExpiresAt - [DateTimeOffset]::Now).TotalMinutes))
    Write-Host "The code works once and expires in $minutesLeft minutes (at $($connection.ExpiresAt.ToString('HH:mm')))."
    Write-Host "For a new code, sign in to the dashboard, open Settings > Authentication and press Connect game client."
}
else {
    Write-Warning "Install finished, but no connection code was created: $connectionFailure."
    Write-Host "Use the manual setup below, or make a code later in the dashboard: Settings > Authentication > Connect game client."
}

Write-Host ""
Write-Host "Manual setup instead (Settings > General / Server Connection > Manual setup):"
Write-Host "  Server URL: $PublicOrigin/inventory"
Write-Host "  MarketMafioso Client Key: $clientKey"

Write-Section "Dashboard"
Write-Host "Dashboard: $PublicOrigin/"
Write-Host "Dashboard username: $DashboardUsername"
Write-Host "Dashboard password: $dashboardPassword"
Write-Host ""
Write-Host "Keep config\marketmafioso.env and data\. They contain the Workshop Host configuration and database."
Write-Host "For setting explanations, read docs\RECEIVER-SETTINGS.md."
