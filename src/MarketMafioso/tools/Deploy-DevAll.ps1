param(
    [string]$Ref = "local-dev",

    [switch]$SkipServer,

    [switch]$SkipPlugin,

    [switch]$SkipServerSmoke,

    [ValidateSet("Debug", "Release")]
    [string]$PluginConfiguration
)

$ErrorActionPreference = "Stop"

$serverScript = Join-Path -Path $PSScriptRoot -ChildPath "Deploy-ServerDev.ps1"
$pluginScript = Join-Path -Path $PSScriptRoot -ChildPath "Deploy-PluginDev.ps1"

if (-not $SkipServer) {
    Write-Host "=== Deploying dev server backend ==="
    $serverArgs = @("-Ref", $Ref)
    if ($SkipServerSmoke) {
        $serverArgs += "-SkipSmoke"
    }

    & $serverScript @serverArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Server deploy failed with exit code $LASTEXITCODE."
    }
}

if (-not $SkipPlugin) {
    Write-Host "=== Deploying local dev plugin ==="
    $pluginArgs = @()
    if (-not [string]::IsNullOrWhiteSpace($PluginConfiguration)) {
        $pluginArgs += @("-Configuration", $PluginConfiguration)
    }

    & $pluginScript @pluginArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Plugin deploy failed with exit code $LASTEXITCODE."
    }
}

Write-Host "Dev deployment sequence complete."
