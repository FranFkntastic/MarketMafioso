param(
    [Parameter(Mandatory = $true)]
    [string]$SourceDir,

    [Parameter(Mandatory = $true)]
    [string]$DestDir,

    [Parameter(Mandatory = $true)]
    [string]$PluginName,

    [switch]$CapabilityOnly,

    [int]$RetryCount = 20,
    [int]$RetryDelayMs = 200
)

$ErrorActionPreference = "Stop"

function Sync-ManifestAssemblyVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AssemblyPath,

        [Parameter(Mandatory = $true)]
        [string]$ManifestPath
    )

    $visibleVersion = Get-VisibleManifestVersion -AssemblyPath $AssemblyPath

    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    $manifest.AssemblyVersion = $visibleVersion
    $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $ManifestPath -Encoding utf8
    Write-Host "Visible manifest version: $visibleVersion"
}

function Write-ProfileManagerCapabilityManifest {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AssemblyPath,

        [Parameter(Mandatory = $true)]
        [string]$DestinationDirectory,

        [Parameter(Mandatory = $true)]
        [string]$InternalName
    )

    if (-not [string]::Equals($InternalName, 'MarketMafioso', [System.StringComparison]::OrdinalIgnoreCase)) {
        return
    }

    $manifestPath = Join-Path $DestinationDirectory "$InternalName.profile-manager-capabilities.json"
    $temporaryPath = "$manifestPath.$([Guid]::NewGuid().ToString('N')).tmp"
    $manifest = [ordered]@{
        SchemaVersion = 1
        PluginInternalName = $InternalName
        PluginSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $AssemblyPath).Hash
        DirectCharacterLoginProtocolVersions = @(1)
    }
    try {
        $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $temporaryPath -Encoding utf8
        Move-Item -LiteralPath $temporaryPath -Destination $manifestPath -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force }
    }
}

function Get-VisibleManifestVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AssemblyPath
    )

    $assemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName($AssemblyPath).Version
    $projectDir = Split-Path -Parent $PSScriptRoot
    $repoRoot = Split-Path -Parent $projectDir

    try {
        $commitCountText = git -C $repoRoot rev-list --count HEAD
        $commitCount = [int]$commitCountText
        $assemblyHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $AssemblyPath).Hash
        $revision = [Convert]::ToInt32($assemblyHash.Substring(0, 4), 16)

        return "$($assemblyVersion.Major).$($assemblyVersion.Minor).$commitCount.$revision"
    }
    catch {
        return $assemblyVersion.ToString()
    }
}

function Copy-WithRetry {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Source,

        [Parameter(Mandatory = $true)]
        [string]$Destination,

        [Parameter(Mandatory = $true)]
        [int]$MaxAttempts,

        [Parameter(Mandatory = $true)]
        [int]$DelayMs
    )

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            Copy-Item -LiteralPath $Source -Destination $Destination -Force
            return
        }
        catch {
            if ($attempt -eq $MaxAttempts) {
                throw
            }

            Start-Sleep -Milliseconds $DelayMs
        }
    }
}

if (-not (Test-Path -LiteralPath $SourceDir)) {
    throw "Source directory does not exist: $SourceDir"
}

New-Item -ItemType Directory -Path $DestDir -Force | Out-Null

$requiredFiles = @(
    "$PluginName.dll",
    "$PluginName.json"
)

$optionalFiles = @(
    "$PluginName.deps.json",
    "$PluginName.pdb",
    "$PluginName.xml"
)

if (-not $CapabilityOnly) {
    foreach ($file in $requiredFiles) {
        $sourcePath = Join-Path -Path $SourceDir -ChildPath $file
        if (-not (Test-Path -LiteralPath $sourcePath)) {
            throw "Required build output not found: $sourcePath"
        }

        Copy-WithRetry -Source $sourcePath -Destination $DestDir -MaxAttempts $RetryCount -DelayMs $RetryDelayMs
    }

    foreach ($file in $optionalFiles) {
        $sourcePath = Join-Path -Path $SourceDir -ChildPath $file
        if (Test-Path -LiteralPath $sourcePath) {
            Copy-WithRetry -Source $sourcePath -Destination $DestDir -MaxAttempts $RetryCount -DelayMs $RetryDelayMs
        }
    }

    $dependencyAssemblies = Get-ChildItem -LiteralPath $SourceDir -Filter "*.dll" -File |
        Where-Object { -not [string]::Equals($_.Name, "$PluginName.dll", [System.StringComparison]::OrdinalIgnoreCase) }

    foreach ($dependencyAssembly in $dependencyAssemblies) {
        Copy-WithRetry -Source $dependencyAssembly.FullName -Destination $DestDir -MaxAttempts $RetryCount -DelayMs $RetryDelayMs
    }

    if ($dependencyAssemblies.Count -gt 0) {
        Write-Host "Synced dependency assemblies: $($dependencyAssemblies.Name -join ', ')"
    }
}

$assemblyPath = Join-Path -Path $DestDir -ChildPath "$PluginName.dll"
$manifestPath = Join-Path -Path $DestDir -ChildPath "$PluginName.json"
Sync-ManifestAssemblyVersion -AssemblyPath $assemblyPath -ManifestPath $manifestPath
Write-ProfileManagerCapabilityManifest -AssemblyPath $assemblyPath -DestinationDirectory $DestDir -InternalName $PluginName

Write-Host "Synced plugin artifacts to $DestDir"
