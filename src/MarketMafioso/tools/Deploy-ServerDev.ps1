param(
    [string]$Ref = "local-dev",

    [switch]$NoWait,

    [switch]$SkipSmoke,

    [int]$TimeoutSeconds = 600
)

$ErrorActionPreference = "Stop"

$projectDir = Split-Path -Parent $PSScriptRoot
$srcDir = Split-Path -Parent $projectDir
$repoRoot = Split-Path -Parent $srcDir
$workflow = "deploy-vps-marketmafioso-dev.yml"
$healthUrl = "https://dev.xivcraftarchitect.com/marketmafioso/health"
$inventoryUrl = "https://dev.xivcraftarchitect.com/marketmafioso/api/inventory"
$dashboardUrl = "https://dev.xivcraftarchitect.com/marketmafioso/"
$retiredDashboardUrl = "https://dev.xivcraftarchitect.com/api/marketmafioso/"
$ingestKeyPath = Join-Path -Path $env:USERPROFILE -ChildPath ".ssh\marketmafioso_dev_api_key.txt"
$dashboardPasswordPath = Join-Path -Path $env:USERPROFILE -ChildPath ".ssh\marketmafioso_dashboard_password.txt"
$sampleReportPath = Join-Path -Path $repoRoot -ChildPath "docs\samples\inventory-report.sample.json"

function Assert-CommandAvailable {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if ($null -eq (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' was not found on PATH."
    }
}

function Invoke-Gh {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    & gh @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "gh $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Get-LatestWorkflowRun {
    param(
        [Parameter(Mandatory = $true)]
        [DateTimeOffset]$StartedAfter
    )

    $json = & gh run list `
        --workflow $workflow `
        --branch $Ref `
        --limit 10 `
        --json databaseId,headBranch,headSha,status,conclusion,createdAt,event,displayTitle

    if ($LASTEXITCODE -ne 0) {
        throw "Unable to list GitHub workflow runs."
    }

    $runs = $json | ConvertFrom-Json
    $matchingRuns = @($runs | Where-Object {
            $_.event -eq "workflow_dispatch" -and
            [DateTimeOffset]::Parse($_.createdAt) -ge $StartedAfter
        })

    if ($matchingRuns.Count -eq 0) {
        return $null
    }

    return $matchingRuns | Sort-Object -Property createdAt -Descending | Select-Object -First 1
}

function Invoke-PublicSmoke {
    Write-Host "Running public server smoke checks..."

    $health = Invoke-RestMethod -Uri $healthUrl
    if ($true -ne $health.ok) {
        throw "Health check did not return ok=true."
    }

    $shell = Invoke-WebRequest -Uri $dashboardUrl -UseBasicParsing
    if ($shell.StatusCode -ne 200 -or $shell.Content -notlike "*_framework/blazor*") {
        throw "Dashboard shell smoke check failed."
    }

    $retiredRouteStillServes = $false
    try {
        $retiredShell = Invoke-WebRequest -Uri $retiredDashboardUrl -UseBasicParsing
        if ($retiredShell.StatusCode -eq 200) {
            $retiredRouteStillServes = $true
        }
    }
    catch {
        # Expected when the retired route is gone.
    }
    if ($retiredRouteStillServes) {
        throw "Retired /api/marketmafioso route still returns 200."
    }

    if (Test-Path -LiteralPath $dashboardPasswordPath) {
        $password = (Get-Content -LiteralPath $dashboardPasswordPath -Raw).Trim()
        $login = Invoke-WebRequest `
            -Method Post `
            -Uri "$($dashboardUrl)auth/login" `
            -ContentType "application/json" `
            -Body (@{ username = "marketmafioso"; password = $password } | ConvertTo-Json -Compress) `
            -SessionVariable dashboardSession `
            -UseBasicParsing
        if ($login.StatusCode -ne 200) {
            throw "Dashboard login smoke check failed."
        }

        $response = Invoke-WebRequest -Uri "$($dashboardUrl)api/acquisition/requests" -WebSession $dashboardSession -UseBasicParsing
        if ($response.StatusCode -ne 200) {
            throw "Authenticated dashboard API smoke check failed."
        }
    }
    else {
        Write-Warning "Skipping authenticated dashboard smoke; missing $dashboardPasswordPath"
    }

    if ((Test-Path -LiteralPath $ingestKeyPath) -and (Test-Path -LiteralPath $sampleReportPath)) {
        $key = (Get-Content -LiteralPath $ingestKeyPath -Raw).Trim()
        $response = Invoke-RestMethod `
            -Method Post `
            -Uri $inventoryUrl `
            -Headers @{ "X-Api-Key" = $key } `
            -ContentType "application/json" `
            -Body (Get-Content -LiteralPath $sampleReportPath -Raw)

        if ([string]::IsNullOrWhiteSpace($response.dashboardUrl)) {
            throw "Ingest smoke succeeded without a dashboardUrl in the response."
        }

        Write-Host "Ingest smoke report: $($response.id)"
    }
    else {
        Write-Warning "Skipping ingest smoke; missing local ingest key or sample report."
    }

    Write-Host "Public server smoke checks passed."
}

Assert-CommandAvailable -Name "git"
Assert-CommandAvailable -Name "gh"

Push-Location $repoRoot
try {
    $startedAfter = [DateTimeOffset]::Now.AddSeconds(-10)
    $resolvedSha = (& git rev-parse $Ref).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($resolvedSha)) {
        throw "Unable to resolve git ref '$Ref'."
    }

    Write-Host "Triggering MarketMafioso dev server deploy for $Ref@$($resolvedSha.Substring(0, 7))"
    Invoke-Gh -Arguments @("workflow", "run", $workflow, "--ref", $Ref)

    if ($NoWait) {
        Write-Host "Deploy triggered. Not waiting because -NoWait was supplied."
        return
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $run = $null
    while ((Get-Date) -lt $deadline) {
        $run = Get-LatestWorkflowRun -StartedAfter $startedAfter
        if ($null -ne $run) {
            break
        }

        Start-Sleep -Seconds 3
    }

    if ($null -eq $run) {
        throw "Timed out waiting for a new $workflow run for '$Ref'."
    }

    Write-Host "Watching GitHub Actions run $($run.databaseId) ($($run.displayTitle))"
    Invoke-Gh -Arguments @("run", "watch", [string]$run.databaseId, "--exit-status")

    if (-not $SkipSmoke) {
        Invoke-PublicSmoke
    }
}
finally {
    Pop-Location
}
