param(
    [string]$OutputPath = "",
    [switch]$AllowDirty
)

$ErrorActionPreference = "Stop"

$projectDir = Split-Path -Parent $PSScriptRoot
$srcDir = Split-Path -Parent $projectDir
$repoRoot = Split-Path -Parent $srcDir
$projectPath = Join-Path $projectDir "MarketMafioso.csproj"
$franthropyRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "..\Franthropy"))
$franthropyProject = Join-Path $franthropyRoot "src\Franthropy.Dalamud\Franthropy.Dalamud.csproj"
$releaseDir = Join-Path $projectDir "bin\Release"
$releaseArchive = Join-Path $releaseDir "MarketMafioso\latest.zip"
$releaseFranthropy = Join-Path $franthropyRoot "src\Franthropy.Dalamud\bin\Release\net10.0-windows\Franthropy.Dalamud.dll"
$copiedFranthropy = Join-Path $releaseDir "Franthropy.Dalamud.dll"

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repoRoot "dist\plugin\MarketMafioso.zip"
}

function Get-RepositoryState {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath (Join-Path $Path ".git"))) {
        throw "Expected Git repository was not found: $Path"
    }

    $commit = (& git -C $Path rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "Could not read Git commit for $Path."
    }
    $changes = @(& git -C $Path status --porcelain)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not read Git status for $Path."
    }

    return [pscustomobject]@{
        Commit = $commit
        Dirty = $changes.Count -gt 0
    }
}

function Get-StreamSha256 {
    param([Parameter(Mandatory = $true)][System.IO.Stream]$Stream)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [System.BitConverter]::ToString($sha.ComputeHash($Stream)).Replace("-", "")
    }
    finally {
        $sha.Dispose()
    }
}

if (-not (Test-Path -LiteralPath $franthropyProject)) {
    throw "Franthropy.Dalamud was not found at '$franthropyProject'."
}

$mmfState = Get-RepositoryState -Path $repoRoot
$franthropyState = Get-RepositoryState -Path $franthropyRoot
if (-not $AllowDirty -and ($mmfState.Dirty -or $franthropyState.Dirty)) {
    throw "Release inputs must be clean. Commit MMF and Franthropy first, or use -AllowDirty for a local verification build."
}

Write-Host "Building the MarketMafioso plugin release directly so sibling projects inherit Release configuration."
& dotnet build $projectPath -c Release -t:Rebuild -p:SkipDevPluginSync=true -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) {
    throw "MarketMafioso Release build failed with exit code $LASTEXITCODE."
}

foreach ($requiredPath in @($releaseArchive, $releaseFranthropy, $copiedFranthropy)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Expected release artifact was not found: $requiredPath"
    }
}

$sourceFranthropyHash = (Get-FileHash -LiteralPath $releaseFranthropy -Algorithm SHA256).Hash
$copiedFranthropyHash = (Get-FileHash -LiteralPath $copiedFranthropy -Algorithm SHA256).Hash
if ($sourceFranthropyHash -ne $copiedFranthropyHash) {
    throw "Packaged Franthropy copy does not match its Release output. Source=$sourceFranthropyHash Copy=$copiedFranthropyHash"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($releaseArchive)
try {
    $requiredEntries = @(
        "MarketMafioso.dll",
        "MarketMafioso.json",
        "MarketMafioso.Contracts.dll",
        "Franthropy.Dalamud.dll",
        "ECommons.dll"
    )
    $entryNames = @($archive.Entries | ForEach-Object FullName)
    $missingEntries = @($requiredEntries | Where-Object { $_ -notin $entryNames })
    if ($missingEntries.Count -gt 0) {
        throw "Release archive is missing required entries: $($missingEntries -join ', ')"
    }

    $franthropyEntry = $archive.Entries | Where-Object FullName -eq "Franthropy.Dalamud.dll" | Select-Object -First 1
    $stream = $franthropyEntry.Open()
    try {
        $archivedFranthropyHash = Get-StreamSha256 -Stream $stream
    }
    finally {
        $stream.Dispose()
    }
    if ($archivedFranthropyHash -ne $sourceFranthropyHash) {
        throw "Archived Franthropy DLL does not match its Release output. Source=$sourceFranthropyHash Archive=$archivedFranthropyHash"
    }
}
finally {
    $archive.Dispose()
}

$outputFullPath = [System.IO.Path]::GetFullPath($OutputPath)
$outputDir = Split-Path -Parent $outputFullPath
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
Copy-Item -LiteralPath $releaseArchive -Destination $outputFullPath -Force
$archiveHash = (Get-FileHash -LiteralPath $outputFullPath -Algorithm SHA256).Hash
$receiptPath = [System.IO.Path]::ChangeExtension($outputFullPath, ".release.json")
[ordered]@{
    schemaVersion = 1
    builtAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    archive = $outputFullPath
    archiveSha256 = $archiveHash
    marketMafiosoCommit = $mmfState.Commit
    marketMafiosoDirty = $mmfState.Dirty
    franthropyCommit = $franthropyState.Commit
    franthropyDirty = $franthropyState.Dirty
    franthropySha256 = $sourceFranthropyHash
} | ConvertTo-Json | Set-Content -LiteralPath $receiptPath -Encoding UTF8

Write-Host "Verified plugin release: $outputFullPath"
Write-Host "SHA256: $archiveHash"
Write-Host "Receipt: $receiptPath"
