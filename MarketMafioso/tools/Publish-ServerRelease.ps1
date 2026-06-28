param(
    [string]$Configuration = "Release",
    [string]$Runtime = "",
    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"

$projectDir = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $projectDir
$serverProject = Join-Path $repoRoot "MarketMafioso.Server\MarketMafioso.Server.csproj"

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repoRoot "dist\server"
}

$arguments = @(
    "publish",
    $serverProject,
    "-c",
    $Configuration,
    "-o",
    $OutputPath
)

if (-not [string]::IsNullOrWhiteSpace($Runtime)) {
    $arguments += @("-r", $Runtime, "--self-contained", "true")
}

Write-Host "Publishing MarketMafioso.Server to $OutputPath"
& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

Write-Host "Server publish complete."
Write-Host "Copy docs/samples/marketmafioso.env.example or MarketMafioso.Server/appsettings.SelfHost.example.json and replace all secrets before hosting."
