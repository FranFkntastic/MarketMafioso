param(
    [Parameter(Mandatory = $true)]
    [string]$SourceDir,

    [Parameter(Mandatory = $true)]
    [string]$DestDir,

    [Parameter(Mandatory = $true)]
    [string]$PluginName,

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

    $assemblyName = [System.Reflection.AssemblyName]::GetAssemblyName($AssemblyPath)
    $assemblyVersion = $assemblyName.Version.ToString()
    $informationalVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($AssemblyPath).ProductVersion
    $visibleVersion = if ([string]::IsNullOrWhiteSpace($informationalVersion)) { $assemblyVersion } else { $informationalVersion }

    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    $manifest.AssemblyVersion = $visibleVersion
    $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $ManifestPath -Encoding utf8
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

$assemblyPath = Join-Path -Path $DestDir -ChildPath "$PluginName.dll"
$manifestPath = Join-Path -Path $DestDir -ChildPath "$PluginName.json"
Sync-ManifestAssemblyVersion -AssemblyPath $assemblyPath -ManifestPath $manifestPath

Write-Host "Synced plugin artifacts to $DestDir"
