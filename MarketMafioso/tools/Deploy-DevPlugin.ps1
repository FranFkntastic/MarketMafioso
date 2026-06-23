param(
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$projectDir = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $projectDir
$projectPath = Join-Path -Path $projectDir -ChildPath "MarketMafioso.csproj"
$sourceDll = Join-Path -Path $projectDir -ChildPath "bin\Debug\MarketMafioso.dll"
$destDir = Join-Path -Path $env:APPDATA -ChildPath "XIVLauncher\devPlugins\MarketMafioso"
$destDll = Join-Path -Path $destDir -ChildPath "MarketMafioso.dll"

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

    if (-not (Test-Path -LiteralPath $destDll)) {
        throw "Expected dev-plugin DLL was not found after build sync: $destDll"
    }

    $sourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $sourceDll).Hash
    $destHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $destDll).Hash
    if ($sourceHash -ne $destHash) {
        throw "Dev-plugin DLL hash does not match Debug output. Source=$sourceHash Destination=$destHash"
    }

    Write-Host "Verified dev-plugin DLL: $destDll"
    Write-Host "Reload MarketMafioso in Dalamud if it is already loaded."
}
finally {
    Pop-Location
}
