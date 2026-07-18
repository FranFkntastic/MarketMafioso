param(
    [switch]$SkipBuild,

    [ValidateSet("Debug", "Release")]
    [string]$Configuration,

    [string]$TargetDll,

    [string]$ConfigPath
)

$ErrorActionPreference = "Stop"

$projectDir = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $projectDir
$workspaceRoot = Split-Path -Parent $repoRoot
$projectPath = Join-Path -Path $projectDir -ChildPath "MarketMafioso.csproj"
$syncScript = Join-Path -Path $PSScriptRoot -ChildPath "Sync-DevPlugin.ps1"
$defaultConfigPath = Join-Path -Path $projectDir -ChildPath "dev-plugin.local.json"

function Resolve-PinnedFranthropyRoot {
    $refPath = Join-Path -Path $workspaceRoot -ChildPath "eng\franthropy.ref"
    if (-not (Test-Path -LiteralPath $refPath)) {
        throw "Pinned Franthropy revision was not found: $refPath"
    }

    $expectedRevision = (Get-Content -LiteralPath $refPath -Raw).Trim()
    if ($expectedRevision -notmatch '^[0-9a-fA-F]{40}$') {
        throw "Pinned Franthropy revision is invalid: '$expectedRevision'."
    }

    $sibling = [System.IO.Path]::GetFullPath((Join-Path -Path $workspaceRoot -ChildPath "..\Franthropy"))
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
        if ($LASTEXITCODE -eq 0 -and $dirty.Count -eq 0) {
            Write-Host "Using pinned Franthropy worktree: $candidate@$expectedRevision"
            return $candidate
        }
    }

    throw "No clean Franthropy worktree is checked out at pinned revision '$expectedRevision'. Create a detached worktree at that revision before deploying."
}

function Resolve-ConfigPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$BaseDir
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path -Path $BaseDir -ChildPath $Path))
}

function Resolve-RegisteredDeployedTarget {
    $dalamudConfigPath = Join-Path -Path $env:APPDATA -ChildPath "XIVLauncher\dalamudConfig.json"
    if (-not (Test-Path -LiteralPath $dalamudConfigPath)) {
        throw "No deploy target was configured and Dalamud config was not found at '$dalamudConfigPath'. Pass -TargetDll or create '$defaultConfigPath'."
    }

    $dalamudConfig = Get-Content -LiteralPath $dalamudConfigPath -Raw | ConvertFrom-Json
    $registeredTargets = @(
        $dalamudConfig.DevPluginSettings.PSObject.Properties.Name |
            Where-Object { $_ -match '[\\/]_deployed[\\/]MarketMafioso[\\/]MarketMafioso\.dll$' }
    )

    if ($registeredTargets.Count -ne 1) {
        $registeredSummary = if ($registeredTargets.Count -eq 0) { "none" } else { $registeredTargets -join "', '" }
        throw "No deploy target was configured and expected exactly one registered _deployed MarketMafioso DLL, but found $($registeredTargets.Count): '$registeredSummary'. Pass -TargetDll or create '$defaultConfigPath'."
    }

    Write-Host "Using registered _deployed target: $($registeredTargets[0])"
    return $registeredTargets[0]
}

$effectiveConfigPath = if ([string]::IsNullOrWhiteSpace($ConfigPath)) { $defaultConfigPath } else { Resolve-ConfigPath -Path $ConfigPath -BaseDir $repoRoot }
$localConfig = $null
if (Test-Path -LiteralPath $effectiveConfigPath) {
    $localConfig = Get-Content -LiteralPath $effectiveConfigPath -Raw | ConvertFrom-Json
    Write-Host "Using dev-plugin config: $effectiveConfigPath"
}

$effectiveConfiguration = $Configuration
if ([string]::IsNullOrWhiteSpace($effectiveConfiguration) -and $null -ne $localConfig -and -not [string]::IsNullOrWhiteSpace($localConfig.Configuration)) {
    $effectiveConfiguration = $localConfig.Configuration
}

if ([string]::IsNullOrWhiteSpace($effectiveConfiguration)) {
    $effectiveConfiguration = "Debug"
}

if ($effectiveConfiguration -ne "Debug" -and $effectiveConfiguration -ne "Release") {
    throw "Unsupported dev-plugin configuration '$effectiveConfiguration'. Expected Debug or Release."
}

$configuredTargetDll = $TargetDll
if ([string]::IsNullOrWhiteSpace($configuredTargetDll) -and $null -ne $localConfig -and -not [string]::IsNullOrWhiteSpace($localConfig.TargetDll)) {
    $configuredTargetDll = $localConfig.TargetDll
}

if ([string]::IsNullOrWhiteSpace($configuredTargetDll) -and $null -ne $localConfig -and -not [string]::IsNullOrWhiteSpace($localConfig.TargetDir)) {
    $configuredTargetDll = Join-Path -Path $localConfig.TargetDir -ChildPath "MarketMafioso.dll"
}

if ([string]::IsNullOrWhiteSpace($configuredTargetDll)) {
    $configuredTargetDll = Resolve-RegisteredDeployedTarget
}

$sourceDir = Join-Path -Path $projectDir -ChildPath "bin\$effectiveConfiguration"
$sourceDll = Join-Path -Path $sourceDir -ChildPath "MarketMafioso.dll"
$destDll = Resolve-ConfigPath -Path $configuredTargetDll -BaseDir $projectDir
$destDir = Split-Path -Parent $destDll

if ((Split-Path -Leaf $destDll) -ne "MarketMafioso.dll") {
    throw "Dalamud target DLL must be named MarketMafioso.dll: $destDll"
}

Push-Location $repoRoot
try {
    $branch = git branch --show-current
    if ([string]::IsNullOrWhiteSpace($branch)) {
        $branch = "(detached)"
    }

    $commit = git rev-parse --short HEAD
    Write-Host "Deploying MarketMafioso dev plugin from $branch@$commit"

    if (-not $SkipBuild) {
        $franthropyRoot = Resolve-PinnedFranthropyRoot
        $franthropySource = Join-Path -Path $franthropyRoot -ChildPath "src"
        $buildArguments = @(
            'build',
            $projectPath,
            '-c', $effectiveConfiguration,
            '-p:UseSharedCompilation=false',
            "-p:FranthropyDalamudProject=$(Join-Path -Path $franthropySource -ChildPath 'Franthropy.Dalamud\Franthropy.Dalamud.csproj')",
            "-p:FranthropyFilteringProject=$(Join-Path -Path $franthropySource -ChildPath 'Franthropy.Filtering\Franthropy.Filtering.csproj')"
        )
        & dotnet @buildArguments
        if ($LASTEXITCODE -ne 0) {
            throw "$effectiveConfiguration build failed with exit code $LASTEXITCODE."
        }
    }

    if (-not (Test-Path -LiteralPath $sourceDll)) {
        throw "Expected $effectiveConfiguration build output was not found: $sourceDll"
    }

    $sourceFullPath = [System.IO.Path]::GetFullPath($sourceDll)
    $destFullPath = [System.IO.Path]::GetFullPath($destDll)
    if (-not [string]::Equals($sourceFullPath, $destFullPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        & $syncScript -SourceDir $sourceDir -DestDir $destDir -PluginName "MarketMafioso"
        if ($LASTEXITCODE -ne 0) {
            throw "Dev-plugin sync failed with exit code $LASTEXITCODE."
        }
    }

    if (-not (Test-Path -LiteralPath $destDll)) {
        throw "Expected Dalamud target DLL was not found after deploy: $destDll"
    }

    $sourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $sourceDll).Hash
    $destHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $destDll).Hash
    if ($sourceHash -ne $destHash) {
        throw "Dalamud target DLL hash does not match $effectiveConfiguration output. Source=$sourceHash Destination=$destHash"
    }

    $destInfo = Get-Item -LiteralPath $destDll
    Write-Host "Build output DLL: $sourceDll"
    Write-Host "Verified Dalamud target DLL: $destDll"
    Write-Host "SHA256: $destHash"
    Write-Host "Last write: $($destInfo.LastWriteTime)"
    Write-Host "Dalamud will automatically reload MarketMafioso from the watched DLL."
}
finally {
    Pop-Location
}
