param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$franthropyRef = (Get-Content -LiteralPath (Join-Path $repoRoot 'eng\franthropy.ref') -Raw).Trim()
$franthropyRepository = [System.IO.Path]::GetFullPath((Join-Path $repoRoot '..\Franthropy'))
$worktreePath = $null
$candidatePath = $null
$candidateHead = $null
foreach ($line in @(& git -C $franthropyRepository worktree list --porcelain) + '') {
    if ([string]::IsNullOrWhiteSpace($line)) {
        if ($candidateHead -eq $franthropyRef -and -not [string]::IsNullOrWhiteSpace($candidatePath)) {
            $dirty = @(& git -C $candidatePath status --porcelain --untracked-files=normal)
            if ($LASTEXITCODE -eq 0 -and $dirty.Count -eq 0) {
                $worktreePath = $candidatePath
                break
            }
        }
        $candidatePath = $null
        $candidateHead = $null
        continue
    }
    if ($line.StartsWith('worktree ')) { $candidatePath = $line.Substring('worktree '.Length) }
    elseif ($line.StartsWith('HEAD ')) { $candidateHead = $line.Substring('HEAD '.Length) }
}
if ([string]::IsNullOrWhiteSpace($worktreePath)) {
    throw "No clean Franthropy worktree is checked out at pinned revision '$franthropyRef'."
}
$franthropySource = Join-Path $worktreePath 'src'
$franthropyProperties = @(
    "-p:FranthropyDalamudProject=$(Join-Path $franthropySource 'Franthropy.Dalamud\Franthropy.Dalamud.csproj')",
    "-p:FranthropyFilteringProject=$(Join-Path $franthropySource 'Franthropy.Filtering\Franthropy.Filtering.csproj')"
)
$projects = @(
    @('tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj', 'Category!=Performance'),
    @('tests\MarketMafioso.Server.Tests\MarketMafioso.Server.Tests.csproj', $null),
    @('tests\MarketMafioso.ContractTests\MarketMafioso.ContractTests.csproj', $null)
)

foreach ($entry in $projects) {
    $project = Join-Path $repoRoot $entry[0]
    $arguments = @('test', $project, '--configuration', $Configuration, '-p:SkipDevPluginSync=true') + $franthropyProperties
    if ($entry[1]) {
        $arguments += @('--filter', $entry[1])
    }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Test project '$project' failed with exit code $LASTEXITCODE."
    }
    if ($entry[0] -eq 'tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj') {
        $pinnedAssembly = Join-Path $worktreePath "src\Franthropy.Dalamud\bin\$Configuration\net10.0-windows\Franthropy.Dalamud.dll"
        $testAssembly = Join-Path $repoRoot "tests\MarketMafioso.Tests\bin\$Configuration\net10.0-windows7.0\Franthropy.Dalamud.dll"
        if (-not (Test-Path -LiteralPath $pinnedAssembly) -or -not (Test-Path -LiteralPath $testAssembly) -or
            (Get-FileHash -LiteralPath $pinnedAssembly -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $testAssembly -Algorithm SHA256).Hash) {
            throw 'MarketMafioso tests did not execute against the pinned Franthropy deployment assembly.'
        }
    }
}
