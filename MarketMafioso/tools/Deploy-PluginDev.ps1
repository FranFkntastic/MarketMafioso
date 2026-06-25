param(
    [switch]$SkipBuild,

    [ValidateSet("Debug", "Release")]
    [string]$Configuration,

    [string]$TargetDll,

    [string]$ConfigPath
)

$ErrorActionPreference = "Stop"

$deployScript = Join-Path -Path $PSScriptRoot -ChildPath "Deploy-DevPlugin.ps1"
$arguments = @()

if ($SkipBuild) {
    $arguments += "-SkipBuild"
}

if (-not [string]::IsNullOrWhiteSpace($Configuration)) {
    $arguments += @("-Configuration", $Configuration)
}

if (-not [string]::IsNullOrWhiteSpace($TargetDll)) {
    $arguments += @("-TargetDll", $TargetDll)
}

if (-not [string]::IsNullOrWhiteSpace($ConfigPath)) {
    $arguments += @("-ConfigPath", $ConfigPath)
}

Write-Host "Deploying MarketMafioso dev plugin to the local Dalamud watched DLL..."
& $deployScript @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Plugin deploy failed with exit code $LASTEXITCODE."
}

$projectDir = Split-Path -Parent $PSScriptRoot
$defaultConfigPath = Join-Path -Path $projectDir -ChildPath "dev-plugin.local.json"
$effectiveConfigPath = if ([string]::IsNullOrWhiteSpace($ConfigPath)) { $defaultConfigPath } else { $ConfigPath }

if (Test-Path -LiteralPath $effectiveConfigPath) {
    $localConfig = Get-Content -LiteralPath $effectiveConfigPath -Raw | ConvertFrom-Json
    $configuredTargetDll = if (-not [string]::IsNullOrWhiteSpace($TargetDll)) {
        $TargetDll
    }
    elseif (-not [string]::IsNullOrWhiteSpace($localConfig.TargetDll)) {
        $localConfig.TargetDll
    }
    elseif (-not [string]::IsNullOrWhiteSpace($localConfig.TargetDir)) {
        Join-Path -Path $localConfig.TargetDir -ChildPath "MarketMafioso.dll"
    }
    else {
        $null
    }

    if (-not [string]::IsNullOrWhiteSpace($configuredTargetDll)) {
        $targetDir = Split-Path -Parent $configuredTargetDll
        $manifestPath = Join-Path -Path $targetDir -ChildPath "MarketMafioso.json"
        if (Test-Path -LiteralPath $manifestPath) {
            $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
            $parsedVersion = [Version]$manifest.AssemblyVersion
            Write-Host "Verified parseable visible plugin version: $parsedVersion"
        }
    }
}

Write-Host "Reload MarketMafioso in Dalamud before testing plugin behavior."
