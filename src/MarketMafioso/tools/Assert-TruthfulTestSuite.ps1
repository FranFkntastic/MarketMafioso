[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$legacyRoots = @(
    'tests\MarketMafioso.Tests',
    'tests\MarketMafioso.Server.Tests'
)
$testRoots = @(
    'tests\MarketMafioso.SpecTests',
    'tests\MarketMafioso.ContractTests'
)

$violations = New-Object System.Collections.Generic.List[string]
foreach ($legacyRoot in $legacyRoots) {
    if (Test-Path -LiteralPath (Join-Path $repoRoot $legacyRoot)) {
        $violations.Add("Legacy test root still exists: $legacyRoot")
    }
}

$sourceFiles = foreach ($testRoot in $testRoots) {
    $absoluteRoot = Join-Path $repoRoot $testRoot
    if (-not (Test-Path -LiteralPath $absoluteRoot)) {
        $violations.Add("Required truthful-suite root is missing: $testRoot")
        continue
    }

    Get-ChildItem -LiteralPath $absoluteRoot -Filter '*.cs' -File -Recurse |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
}

$methodCount = 0
$lineCount = 0
foreach ($file in $sourceFiles) {
    $text = Get-Content -LiteralPath $file.FullName -Raw
    $lineCount += @(Get-Content -LiteralPath $file.FullName).Count
    $methodCount += [regex]::Matches(
        $text,
        '(?m)^\s*\[(Fact|Theory)(?:\([^\]]*\))?\]\s*$').Count

    if ($text -match '(?i)\bSkip\s*=') {
        $violations.Add("$($file.FullName): skipped test")
    }
    if ($text -match '\[(Fact|Theory)' -and $text -notmatch '\bAssert\.') {
        $violations.Add("$($file.FullName): test file has no assertion")
    }
    if ($text -match '(?i)\bCategory\s*=\s*"Performance"|\bTrait\s*\(\s*"Category"\s*,\s*"Performance"') {
        $violations.Add("$($file.FullName): performance test belongs outside the ordinary suite")
    }
}

if ($methodCount -eq 0) {
    $violations.Add('No test methods were discovered.')
}
if ($methodCount -gt 150) {
    $violations.Add("Test method ceiling exceeded: $methodCount > 150.")
}
if ($lineCount -gt 10000) {
    $violations.Add("Test source ceiling exceeded: $lineCount > 10000.")
}

if ($violations.Count -gt 0) {
    $violations | ForEach-Object { Write-Error $_ }
    throw 'Truthful test-suite structure is invalid.'
}

Write-Output "Truthful suite structure valid: $methodCount test methods, $lineCount source lines."
