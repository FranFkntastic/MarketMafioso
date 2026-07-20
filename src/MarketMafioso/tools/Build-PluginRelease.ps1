param(
    [string]$OutputPath = "",
    [switch]$AllowDirty
)

$ErrorActionPreference = "Stop"

$projectDir = Split-Path -Parent $PSScriptRoot
$srcDir = Split-Path -Parent $projectDir
$repoRoot = Split-Path -Parent $srcDir
$workspaceRoot = $repoRoot
$projectPath = Join-Path $projectDir "MarketMafioso.csproj"
$releaseDir = Join-Path $projectDir "bin\Release"
$releaseArchive = Join-Path $releaseDir "MarketMafioso\latest.zip"

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

function Resolve-PinnedFranthropyRoot {
    $refPath = Join-Path -Path $repoRoot -ChildPath "eng\franthropy.ref"
    if (-not (Test-Path -LiteralPath $refPath)) {
        throw "Pinned Franthropy revision was not found: $refPath"
    }

    $expectedRevision = (Get-Content -LiteralPath $refPath -Raw).Trim()
    if ($expectedRevision -notmatch '^[0-9a-fA-F]{40}$') {
        throw "Pinned Franthropy revision is invalid: '$expectedRevision'."
    }

    $sibling = [System.IO.Path]::GetFullPath((Join-Path -Path $workspaceRoot -ChildPath "..\Franthropy"))
    if (-not (Test-Path -LiteralPath (Join-Path -Path $sibling -ChildPath ".git"))) {
        $gitCommonDir = (& git -C $repoRoot rev-parse --path-format=absolute --git-common-dir).Trim()
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($gitCommonDir)) {
            throw "Could not resolve the MarketMafioso primary checkout from '$repoRoot'."
        }

        $primaryRepoRoot = Split-Path -Parent $gitCommonDir
        $developmentRoot = Split-Path -Parent $primaryRepoRoot
        $sibling = Join-Path -Path $developmentRoot -ChildPath "Franthropy"
    }
    if (-not (Test-Path -LiteralPath (Join-Path -Path $sibling -ChildPath ".git"))) {
        throw "Franthropy was not found beside MarketMafioso at '$sibling'."
    }

    $worktreeLines = @(& git -C $sibling worktree list --porcelain)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not enumerate Franthropy worktrees."
    }

    $candidates = New-Object System.Collections.Generic.List[string]
    $currentPath = $null
    $currentHead = $null
    foreach ($line in @($worktreeLines + '')) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            if (-not [string]::IsNullOrWhiteSpace($currentPath) -and $currentHead -eq $expectedRevision) {
                $candidates.Add([System.IO.Path]::GetFullPath($currentPath))
            }
            $currentPath = $null
            $currentHead = $null
            continue
        }
        if ($line.StartsWith('worktree ')) { $currentPath = $line.Substring('worktree '.Length) }
        elseif ($line.StartsWith('HEAD ')) { $currentHead = $line.Substring('HEAD '.Length) }
    }

    foreach ($candidate in $candidates) {
        $dirty = @(& git -C $candidate status --porcelain --untracked-files=normal)
        if ($LASTEXITCODE -eq 0 -and ($AllowDirty -or $dirty.Count -eq 0)) {
            Write-Host "Using pinned Franthropy worktree: $candidate@$expectedRevision"
            return $candidate
        }
    }

    throw "No eligible Franthropy worktree is checked out at pinned revision '$expectedRevision'. Create a detached worktree at that revision before building a release."
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

$franthropyRoot = Resolve-PinnedFranthropyRoot
$franthropySource = Join-Path -Path $franthropyRoot -ChildPath "src"
$franthropyDalamudProject = Join-Path -Path $franthropySource -ChildPath "Franthropy.Dalamud\Franthropy.Dalamud.csproj"
$franthropyFfxivProject = Join-Path -Path $franthropySource -ChildPath "Franthropy.FFXIV\Franthropy.FFXIV.csproj"
$franthropyFilteringProject = Join-Path -Path $franthropySource -ChildPath "Franthropy.Filtering\Franthropy.Filtering.csproj"
$releaseFranthropyDalamud = Join-Path -Path $franthropySource -ChildPath "Franthropy.Dalamud\bin\Release\net10.0-windows\Franthropy.Dalamud.dll"
$releaseFranthropyFfxiv = Join-Path -Path $franthropySource -ChildPath "Franthropy.FFXIV\bin\Release\net10.0\Franthropy.FFXIV.dll"
$releaseFranthropyFiltering = Join-Path -Path $franthropySource -ChildPath "Franthropy.Filtering\bin\Release\net10.0\Franthropy.Filtering.dll"
$copiedFranthropyDalamud = Join-Path -Path $releaseDir -ChildPath "Franthropy.Dalamud.dll"
$copiedFranthropyFfxiv = Join-Path -Path $releaseDir -ChildPath "Franthropy.FFXIV.dll"
$copiedFranthropyFiltering = Join-Path -Path $releaseDir -ChildPath "Franthropy.Filtering.dll"

foreach ($project in @($franthropyDalamudProject, $franthropyFfxivProject, $franthropyFilteringProject)) {
    if (-not (Test-Path -LiteralPath $project)) {
        throw "Required Franthropy project was not found: '$project'."
    }
}

$mmfState = Get-RepositoryState -Path $repoRoot
$franthropyState = Get-RepositoryState -Path $franthropyRoot
if (-not $AllowDirty -and ($mmfState.Dirty -or $franthropyState.Dirty)) {
    throw "Release inputs must be clean. Commit MMF and Franthropy first, or use -AllowDirty for a local verification build."
}

Write-Host "Building the MarketMafioso plugin release directly so sibling projects inherit Release configuration."
& dotnet build $projectPath -c Release -t:Rebuild -p:SkipDevPluginSync=true -p:UseSharedCompilation=false "-p:FranthropyDalamudProject=$franthropyDalamudProject" "-p:FranthropyFfxivProject=$franthropyFfxivProject" "-p:FranthropyFilteringProject=$franthropyFilteringProject"
if ($LASTEXITCODE -ne 0) {
    throw "MarketMafioso Release build failed with exit code $LASTEXITCODE."
}

foreach ($requiredPath in @(
    $releaseArchive,
    $releaseFranthropyDalamud,
    $copiedFranthropyDalamud,
    $releaseFranthropyFfxiv,
    $copiedFranthropyFfxiv,
    $releaseFranthropyFiltering,
    $copiedFranthropyFiltering
)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Expected release artifact was not found: $requiredPath"
    }
}

$sourceFranthropyDalamudHash = (Get-FileHash -LiteralPath $releaseFranthropyDalamud -Algorithm SHA256).Hash
$copiedFranthropyDalamudHash = (Get-FileHash -LiteralPath $copiedFranthropyDalamud -Algorithm SHA256).Hash
if ($sourceFranthropyDalamudHash -ne $copiedFranthropyDalamudHash) {
    throw "Packaged Franthropy.Dalamud copy does not match its Release output. Source=$sourceFranthropyDalamudHash Copy=$copiedFranthropyDalamudHash"
}

$sourceFranthropyFfxivHash = (Get-FileHash -LiteralPath $releaseFranthropyFfxiv -Algorithm SHA256).Hash
$copiedFranthropyFfxivHash = (Get-FileHash -LiteralPath $copiedFranthropyFfxiv -Algorithm SHA256).Hash
if ($sourceFranthropyFfxivHash -ne $copiedFranthropyFfxivHash) {
    throw "Packaged Franthropy.FFXIV copy does not match its Release output. Source=$sourceFranthropyFfxivHash Copy=$copiedFranthropyFfxivHash"
}

$sourceFranthropyFilteringHash = (Get-FileHash -LiteralPath $releaseFranthropyFiltering -Algorithm SHA256).Hash
$copiedFranthropyFilteringHash = (Get-FileHash -LiteralPath $copiedFranthropyFiltering -Algorithm SHA256).Hash
if ($sourceFranthropyFilteringHash -ne $copiedFranthropyFilteringHash) {
    throw "Packaged Franthropy.Filtering copy does not match its Release output. Source=$sourceFranthropyFilteringHash Copy=$copiedFranthropyFilteringHash"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($releaseArchive)
try {
    $requiredEntries = @(
        "MarketMafioso.dll",
        "MarketMafioso.json",
        "MarketMafioso.Contracts.dll",
        "Franthropy.Dalamud.dll",
        "Franthropy.FFXIV.dll",
        "Franthropy.Filtering.dll",
        "ECommons.dll"
    )
    $entryNames = @($archive.Entries | ForEach-Object FullName)
    $missingEntries = @($requiredEntries | Where-Object { $_ -notin $entryNames })
    if ($missingEntries.Count -gt 0) {
        throw "Release archive is missing required entries: $($missingEntries -join ', ')"
    }

    $franthropyDependencies = @(
        [pscustomobject]@{ Name = "Franthropy.Dalamud.dll"; SourceHash = $sourceFranthropyDalamudHash },
        [pscustomobject]@{ Name = "Franthropy.FFXIV.dll"; SourceHash = $sourceFranthropyFfxivHash },
        [pscustomobject]@{ Name = "Franthropy.Filtering.dll"; SourceHash = $sourceFranthropyFilteringHash }
    )
    foreach ($dependency in $franthropyDependencies) {
        $entry = $archive.Entries | Where-Object FullName -eq $dependency.Name | Select-Object -First 1
        $stream = $entry.Open()
        try {
            $archivedHash = Get-StreamSha256 -Stream $stream
        }
        finally {
            $stream.Dispose()
        }
        if ($archivedHash -ne $dependency.SourceHash) {
            throw "Archived $($dependency.Name) does not match its Release output. Source=$($dependency.SourceHash) Archive=$archivedHash"
        }
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
    franthropySha256 = $sourceFranthropyDalamudHash
    franthropyFfxivSha256 = $sourceFranthropyFfxivHash
    franthropyFilteringSha256 = $sourceFranthropyFilteringHash
} | ConvertTo-Json | Set-Content -LiteralPath $receiptPath -Encoding UTF8

Write-Host "Verified plugin release: $outputFullPath"
Write-Host "SHA256: $archiveHash"
Write-Host "Receipt: $receiptPath"
