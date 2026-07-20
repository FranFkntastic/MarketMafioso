[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [string]$FranthropyRoot,

    [string]$CraftArchitectRoot,

    [string]$TargetDll,

    [switch]$SkipFranthropyTests,

    [switch]$SkipDeploy,

    [switch]$ForceVerify,

    [switch]$DisableSharedCompilation
)

$ErrorActionPreference = "Stop"

$projectDir = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent (Split-Path -Parent $projectDir)
$gitCommonDir = (& git -C $repoRoot rev-parse --path-format=absolute --git-common-dir).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($gitCommonDir)) {
    throw "Could not resolve the MarketMafioso primary checkout from '$repoRoot'."
}
$primaryRepoRoot = Split-Path -Parent $gitCommonDir
$developmentRoot = Split-Path -Parent $primaryRepoRoot

if ([string]::IsNullOrWhiteSpace($FranthropyRoot)) {
    $FranthropyRoot = Join-Path $developmentRoot "Franthropy"
}
if ([string]::IsNullOrWhiteSpace($CraftArchitectRoot)) {
    $CraftArchitectRoot = Join-Path $developmentRoot "FFXIV Craft Architect C# Edition"
}

$franthropyProject = Join-Path $FranthropyRoot "src\Franthropy.Dalamud\Franthropy.Dalamud.csproj"
$franthropyFfxivProject = Join-Path $FranthropyRoot "src\Franthropy.FFXIV\Franthropy.FFXIV.csproj"
$franthropyFilteringProject = Join-Path $FranthropyRoot "src\Franthropy.Filtering\Franthropy.Filtering.csproj"
$franthropyTests = Join-Path $FranthropyRoot "tests\Franthropy.Dalamud.Tests\Franthropy.Dalamud.Tests.csproj"
$craftArchitectProject = Join-Path $CraftArchitectRoot "src\FFXIV Craft Architect.Core\FFXIV Craft Architect.Core.csproj"
$marketMafiosoProject = Join-Path $projectDir "MarketMafioso.csproj"
$marketMafiosoTests = Join-Path $repoRoot "tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj"
$deployScript = Join-Path $PSScriptRoot "Deploy-DevPlugin.ps1"
$verificationCachePath = Join-Path $projectDir "obj\squire-verification-$Configuration.json"

foreach ($requiredPath in @($franthropyProject, $franthropyFfxivProject, $franthropyFilteringProject, $franthropyTests, $craftArchitectProject, $marketMafiosoProject, $marketMafiosoTests, $deployScript)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required Squire build input was not found: $requiredPath"
    }
}

function Invoke-DotNet {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Get-VerificationSignature {
    param(
        [Parameter(Mandatory = $true)][string[]]$Roots,
        [Parameter(Mandatory = $true)][string]$Toolchain
    )

    $files = foreach ($root in $Roots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object {
            $_.FullName -notmatch '[\\/](bin|obj|\.git)[\\/]' -and
            ($_.Extension -in @('.cs', '.csproj', '.props', '.targets', '.json', '.ps1') -or $_.Name -eq 'global.json')
        }
    }

    $hasher = [System.Security.Cryptography.IncrementalHash]::CreateHash(
        [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        $toolchainBytes = [Text.Encoding]::UTF8.GetBytes("toolchain`n$Toolchain`n")
        $hasher.AppendData($toolchainBytes)
        foreach ($file in @($files | Sort-Object FullName -Unique)) {
            $relativeIdentity = $file.FullName.Replace('\\', '/').ToLowerInvariant()
            $identityBytes = [Text.Encoding]::UTF8.GetBytes("file`n$relativeIdentity`n$($file.Length)`n")
            $hasher.AppendData($identityBytes)
            $stream = [IO.File]::OpenRead($file.FullName)
            try {
                $buffer = [byte[]]::new(65536)
                while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $hasher.AppendData($buffer, 0, $read)
                }
            }
            finally {
                $stream.Dispose()
            }
        }
        return [BitConverter]::ToString($hasher.GetHashAndReset()).Replace('-', '')
    }
    finally {
        $hasher.Dispose()
    }
}

function Get-LatestTestAssembly {
    param(
        [Parameter(Mandatory = $true)][string]$TestProject,
        [Parameter(Mandatory = $true)][string]$AssemblyName
    )

    $testRoot = Split-Path -Parent $TestProject
    return Get-ChildItem -LiteralPath (Join-Path $testRoot "bin\$Configuration") -Recurse -Filter $AssemblyName -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
}

function Test-CachedVerification {
    param(
        [AllowNull()]$Cache,
        [Parameter(Mandatory = $true)][string]$Signature,
        [Parameter(Mandatory = $true)][string]$SignatureProperty,
        [Parameter(Mandatory = $true)][string]$AssemblyProperty,
        [Parameter(Mandatory = $true)][string]$AssemblyHashProperty
    )

    if ($ForceVerify -or $null -eq $Cache -or $Cache.$SignatureProperty -ne $Signature) { return $false }
    $assemblyPath = $Cache.$AssemblyProperty
    if ([string]::IsNullOrWhiteSpace($assemblyPath) -or -not (Test-Path -LiteralPath $assemblyPath)) { return $false }
    return (Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash -eq $Cache.$AssemblyHashProperty
}

$dotnetVersion = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($dotnetVersion)) {
    throw 'Could not determine the active .NET SDK version.'
}
$dalamudRoot = Join-Path $env:APPDATA 'XIVLauncher\addon\Hooks\dev'
$dalamudFingerprint = if (Test-Path -LiteralPath $dalamudRoot) {
    (Get-ChildItem -LiteralPath $dalamudRoot -Filter '*.dll' -File | Sort-Object Name | ForEach-Object {
        "$($_.Name):$($_.Length):$($_.LastWriteTimeUtc.Ticks)"
    }) -join ';'
} else {
    'missing'
}
$useSharedCompilation = (-not $DisableSharedCompilation).ToString().ToLowerInvariant()
$toolchainFingerprint = "dotnet=$dotnetVersion;dalamud=$dalamudFingerprint;sharedCompilation=$useSharedCompilation"

$franthropySignature = Get-VerificationSignature -Roots @(
    (Join-Path $FranthropyRoot 'src'),
    (Join-Path $FranthropyRoot 'tests')
) -Toolchain $toolchainFingerprint
$marketMafiosoSignature = Get-VerificationSignature -Roots @(
    (Join-Path $repoRoot 'src'),
    (Join-Path $repoRoot 'tests'),
    (Join-Path $FranthropyRoot 'src'),
    (Join-Path $CraftArchitectRoot 'src\FFXIV Craft Architect.Core')
) -Toolchain $toolchainFingerprint

$verificationCache = if (Test-Path -LiteralPath $verificationCachePath) {
    try { Get-Content -LiteralPath $verificationCachePath -Raw | ConvertFrom-Json } catch { $null }
} else {
    $null
}
$franthropyVerified = Test-CachedVerification $verificationCache $franthropySignature 'FranthropySignature' 'FranthropyTestAssembly' 'FranthropyTestAssemblyHash'
$marketMafiosoVerified = Test-CachedVerification $verificationCache $marketMafiosoSignature 'MarketMafiosoSignature' 'MarketMafiosoTestAssembly' 'MarketMafiosoTestAssemblyHash'
$franthropyVerificationSucceeded = $franthropyVerified

$sharedProperties = @(
    "-p:FranthropyDalamudProject=$franthropyProject",
    "-p:FranthropyFfxivProject=$franthropyFfxivProject",
    "-p:FranthropyFilteringProject=$franthropyFilteringProject",
    "-p:CraftArchitectCoreProject=$craftArchitectProject",
    "-p:SkipDevPluginSync=true",
    "-p:UseSharedCompilation=$useSharedCompilation"
)

Push-Location $repoRoot
try {
    if (-not $SkipFranthropyTests -and -not $franthropyVerified) {
        Write-Host "Building Franthropy test graph once..."
        Invoke-DotNet @("build", $franthropyTests, "-c", $Configuration, "--no-restore", "-p:UseSharedCompilation=$useSharedCompilation")
        Write-Host "Running Franthropy tests without rebuilding..."
        Invoke-DotNet @("test", $franthropyTests, "-c", $Configuration, "--no-build", "--no-restore")
        $franthropyVerificationSucceeded = $true
    } elseif (-not $SkipFranthropyTests) {
        Write-Host "Franthropy source and verified test output are unchanged; reusing the successful verification."
    }

    if (-not $marketMafiosoVerified) {
        Write-Host "Building the MarketMafioso Squire test graph once..."
        Invoke-DotNet (@("build", $marketMafiosoTests, "-c", $Configuration, "--no-restore") + $sharedProperties)
        Write-Host "Running focused Squire tests without rebuilding..."
        Invoke-DotNet (@("test", $marketMafiosoTests, "-c", $Configuration, "--no-build", "--no-restore", "--filter", "FullyQualifiedName~Squire") + $sharedProperties)
    } else {
        Write-Host "MarketMafioso source and verified Squire test output are unchanged; reusing the successful verification."
    }

    $franthropyAssembly = Get-LatestTestAssembly $franthropyTests 'Franthropy.Dalamud.Tests.dll'
    $marketMafiosoAssembly = Get-LatestTestAssembly $marketMafiosoTests 'MarketMafioso.Tests.dll'
    if ($null -eq $marketMafiosoAssembly) { throw 'The verified MarketMafioso test assembly was not found.' }
    $cache = [ordered]@{
        FranthropySignature = if ($franthropyVerificationSucceeded) { $franthropySignature } else { $verificationCache.FranthropySignature }
        FranthropyTestAssembly = if ($franthropyVerificationSucceeded -and $null -ne $franthropyAssembly) { $franthropyAssembly.FullName } else { $verificationCache.FranthropyTestAssembly }
        FranthropyTestAssemblyHash = if ($franthropyVerificationSucceeded -and $null -ne $franthropyAssembly) { (Get-FileHash -LiteralPath $franthropyAssembly.FullName -Algorithm SHA256).Hash } else { $verificationCache.FranthropyTestAssemblyHash }
        MarketMafiosoSignature = $marketMafiosoSignature
        MarketMafiosoTestAssembly = $marketMafiosoAssembly.FullName
        MarketMafiosoTestAssemblyHash = (Get-FileHash -LiteralPath $marketMafiosoAssembly.FullName -Algorithm SHA256).Hash
        VerifiedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    $cacheDirectory = Split-Path -Parent $verificationCachePath
    New-Item -ItemType Directory -Path $cacheDirectory -Force | Out-Null
    $cache | ConvertTo-Json | Set-Content -LiteralPath $verificationCachePath -Encoding utf8

    if (-not $SkipDeploy) {
        $deployParameters = @{
            SkipBuild = $true
            Configuration = $Configuration
        }
        if (-not [string]::IsNullOrWhiteSpace($TargetDll)) {
            $deployParameters.TargetDll = $TargetDll
        }
        & $deployScript @deployParameters
        if ($LASTEXITCODE -ne 0) {
            throw "Squire deployment failed with exit code $LASTEXITCODE."
        }
    }
}
finally {
    Pop-Location
}
