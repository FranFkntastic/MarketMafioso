param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
. (Join-Path $PSScriptRoot 'Resolve-PinnedFranthropyRoot.ps1')
$worktreePath = Resolve-PinnedFranthropyRoot -MarketMafiosoRepoRoot $repoRoot
$franthropyProperty = "-p:FranthropyRoot=$worktreePath"
$projects = @(
    @('tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj', 'Category!=Performance'),
    @('tests\MarketMafioso.Server.Tests\MarketMafioso.Server.Tests.csproj', $null),
    @('tests\MarketMafioso.ContractTests\MarketMafioso.ContractTests.csproj', $null)
)

foreach ($entry in $projects) {
    $project = Join-Path $repoRoot $entry[0]
    $arguments = @('test', $project, '--configuration', $Configuration, '-p:SkipDevPluginSync=true', $franthropyProperty)
    if ($entry[1]) {
        $arguments += @('--filter', $entry[1])
    }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Test project '$project' failed with exit code $LASTEXITCODE."
    }
}

$assemblyChecks = @(
    @("src\Franthropy.Dalamud\bin\$Configuration\net10.0-windows\Franthropy.Dalamud.dll", "tests\MarketMafioso.Tests\bin\$Configuration\net10.0-windows7.0\Franthropy.Dalamud.dll"),
    @("src\Franthropy.Filtering\bin\$Configuration\net10.0\Franthropy.Filtering.dll", "tests\MarketMafioso.Tests\bin\$Configuration\net10.0-windows7.0\Franthropy.Filtering.dll"),
    @("src\Franthropy.FFXIV\bin\$Configuration\net10.0\Franthropy.FFXIV.dll", "tests\MarketMafioso.Server.Tests\bin\$Configuration\net10.0\Franthropy.FFXIV.dll"),
    @("src\Franthropy.Web\bin\$Configuration\net10.0\Franthropy.Web.dll", "tests\MarketMafioso.Server.Tests\bin\$Configuration\net10.0\Franthropy.Web.dll")
)
foreach ($check in $assemblyChecks) {
    $pinnedAssembly = Join-Path $worktreePath $check[0]
    $testAssembly = Join-Path $repoRoot $check[1]
    if (-not (Test-Path -LiteralPath $pinnedAssembly) -or -not (Test-Path -LiteralPath $testAssembly) -or
        (Get-FileHash -LiteralPath $pinnedAssembly -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $testAssembly -Algorithm SHA256).Hash) {
        throw "Test assembly '$testAssembly' does not match pinned Franthropy output '$pinnedAssembly'."
    }
}
