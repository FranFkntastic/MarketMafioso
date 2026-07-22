param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$testProject = Join-Path $repoRoot 'tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj'

& dotnet test $testProject `
    --configuration $Configuration `
    --no-build `
    --no-restore `
    --filter 'Category=Performance' `
    --maxcpucount:1 `
    --logger 'console;verbosity=detailed'
if ($LASTEXITCODE -ne 0) {
    throw "Squire performance gate failed with exit code $LASTEXITCODE."
}
