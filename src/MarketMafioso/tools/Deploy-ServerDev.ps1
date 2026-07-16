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
$capabilitiesUrl = "https://dev.xivcraftarchitect.com/marketmafioso/api/capabilities"
$quoteUrl = "https://dev.xivcraftarchitect.com/marketmafioso/api/craft/appraise"
$dashboardUrl = "https://dev.xivcraftarchitect.com/marketmafioso/"
$retiredDashboardUrl = "https://dev.xivcraftarchitect.com/api/marketmafioso/"
$ingestKeyPath = Join-Path -Path $env:USERPROFILE -ChildPath ".ssh\marketmafioso_dev_api_key.txt"
$dashboardPasswordPath = Join-Path -Path $env:USERPROFILE -ChildPath ".ssh\marketmafioso_dashboard_password.txt"

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

function Get-HttpStatusCode {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Request
    )

    try {
        $response = Invoke-WebRequest @Request -UseBasicParsing
        return [int]$response.StatusCode
    }
    catch {
        if ($_.Exception.Response -and $_.Exception.Response.StatusCode) {
            return [int]$_.Exception.Response.StatusCode
        }

        throw
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

        $timelineStatus = Get-HttpStatusCode -Request @{
            Uri = "$($dashboardUrl)api/acquisition/requests/smoke-missing/timeline"
            WebSession = $dashboardSession
        }
        if ($timelineStatus -ne 404) {
            throw "Dashboard timeline auth smoke returned HTTP $timelineStatus; expected 404."
        }
    }
    else {
        Write-Warning "Skipping authenticated dashboard smoke; missing $dashboardPasswordPath"
    }

    if (Test-Path -LiteralPath $ingestKeyPath) {
        $key = (Get-Content -LiteralPath $ingestKeyPath -Raw).Trim()
        $invalidIngestStatus = Get-HttpStatusCode -Request @{
            Method = "Post"
            Uri = $inventoryUrl
            Headers = @{ "X-Api-Key" = $key }
            ContentType = "application/json"
            Body = "{}"
        }
        if ($invalidIngestStatus -ne 400) {
            throw "Authenticated invalid-ingest smoke returned HTTP $invalidIngestStatus; expected 400."
        }

        $capabilities = Invoke-RestMethod -Uri $capabilitiesUrl -Headers @{ "X-Api-Key" = $key }
        $capabilityIds = @($capabilities.capabilities | ForEach-Object { $_.id })
        if ($capabilityIds -notcontains "craft.appraise") {
            throw "Capabilities smoke did not include craft.appraise."
        }

        foreach ($origin in @("https://dev.xivcraftarchitect.com", "https://xivcraftarchitect.com")) {
            $preflight = Invoke-WebRequest `
                -Method Options `
                -Uri $capabilitiesUrl `
                -Headers @{
                    Origin = $origin
                    "Access-Control-Request-Method" = "GET"
                    "Access-Control-Request-Headers" = "X-Api-Key"
                } `
                -UseBasicParsing
            if ($preflight.Headers["Access-Control-Allow-Origin"] -ne $origin) {
                throw "CORS preflight did not grant origin $origin."
            }
            if ($preflight.Headers["Access-Control-Allow-Headers"] -notmatch "(?i)x-api-key") {
                throw "CORS preflight did not grant X-Api-Key for $origin."
            }
        }

        $quoteWithoutKeyStatus = Get-HttpStatusCode -Request @{
            Method = "Post"
            Uri = $quoteUrl
            ContentType = "application/json"
            Body = "{}"
        }
        if ($quoteWithoutKeyStatus -ne 401) {
            throw "Unauthenticated quote smoke returned HTTP $quoteWithoutKeyStatus; expected 401."
        }

        $invalidQuoteStatus = Get-HttpStatusCode -Request @{
            Method = "Post"
            Uri = $quoteUrl
            Headers = @{ "X-Api-Key" = $key }
            ContentType = "application/json"
            Body = (@{ schemaVersion = 2; itemId = 2; itemName = "Fire Shard"; quantity = 1 } | ConvertTo-Json -Compress)
        }
        if ($invalidQuoteStatus -ne 400) {
            throw "Invalid-schema quote smoke returned HTTP $invalidQuoteStatus; expected 400."
        }

    }
    else {
        Write-Warning "Skipping ingest smoke; missing local ingest key."
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
