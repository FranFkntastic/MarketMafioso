param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$projects = @(
    @('tests\MarketMafioso.SpecTests\MarketMafioso.SpecTests.csproj', $null),
    @('tests\MarketMafioso.ContractTests\MarketMafioso.ContractTests.csproj', $null)
)

foreach ($entry in $projects) {
    $project = Join-Path $repoRoot $entry[0]
    $arguments = @('test', $project, '--configuration', $Configuration, '-p:SkipDevPluginSync=true')
    if ($entry[1]) {
        $arguments += @('--filter', $entry[1])
    }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Test project '$project' failed with exit code $LASTEXITCODE."
    }
}

[xml]$pin = Get-Content (Join-Path $repoRoot 'eng/Franthropy.Version.props')
$expectedCommit = ([string]$pin.Project.PropertyGroup.FranthropyPackageVersion).Replace('0.1.0-commit.', '')
$assemblies = @(Get-ChildItem (Join-Path $repoRoot 'tests') -Recurse -Filter 'Franthropy.*.dll' |
    Where-Object { $_.FullName -like "*\bin\$Configuration\*" })
if ($assemblies.Count -eq 0) { throw 'No Franthropy test outputs found.' }
foreach ($assembly in $assemblies) {
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo($assembly.FullName).ProductVersion -cne $expectedCommit) {
        throw "Test dependency identity mismatch: $($assembly.FullName)"
    }
}
