param(
    [switch]$SkipBuild,

    [string]$DestDir = "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\_deployed\MarketMafioso"
)

$ErrorActionPreference = "Stop"

$projectDir = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $projectDir
$projectPath = Join-Path -Path $projectDir -ChildPath "MarketMafioso.csproj"
$sourceDir = Join-Path -Path $projectDir -ChildPath "bin\Debug"
$sourceDll = Join-Path -Path $sourceDir -ChildPath "MarketMafioso.dll"
$destDll = Join-Path -Path $DestDir -ChildPath "MarketMafioso.dll"

Push-Location $repoRoot
try {
    $branch = git branch --show-current
    if ([string]::IsNullOrWhiteSpace($branch)) {
        $branch = "(detached)"
    }

    $commit = git rev-parse --short HEAD
    Write-Host "Deploying MarketMafioso dev plugin from $branch@$commit"

    if (-not $SkipBuild) {
        dotnet build $projectPath -c Debug -p:UseSharedCompilation=false
        if ($LASTEXITCODE -ne 0) {
            throw "Debug build failed with exit code $LASTEXITCODE."
        }
    }

    if (-not (Test-Path -LiteralPath $sourceDll)) {
        throw "Expected Debug build output was not found: $sourceDll"
    }

    $manifest = Join-Path -Path $sourceDir -ChildPath "MarketMafioso.json"
    if (-not (Test-Path -LiteralPath $manifest)) {
        throw "Expected Debug build manifest was not found: $manifest"
    }

    & "$projectDir\tools\Sync-DevPlugin.ps1" -SourceDir $sourceDir -DestDir $DestDir -PluginName "MarketMafioso"
    if ($LASTEXITCODE -ne 0) {
        throw "Dev-plugin sync failed with exit code $LASTEXITCODE."
    }

    if (-not (Test-Path -LiteralPath $destDll)) {
        throw "Expected deployed dev-plugin DLL was not found after sync: $destDll"
    }

    $sourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $sourceDll).Hash
    $destHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $destDll).Hash
    if ($sourceHash -ne $destHash) {
        throw "Deployed dev-plugin DLL hash does not match Debug output. Source=$sourceHash Destination=$destHash"
    }

    Write-Host "Verified deployed dev-plugin DLL: $destDll"
    Write-Host "Reload MarketMafioso in Dalamud if it is already loaded."
}
finally {
    Pop-Location
}
