[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$BaseRef = "origin/local-dev",

    [switch]$IgnoreUntracked,

    [switch]$SkipServerSmoke,

    [ValidateSet("Debug", "Release")]
    [string]$PluginConfiguration
)

$ErrorActionPreference = "Stop"

$projectDir = Split-Path -Parent $PSScriptRoot
$srcDir = Split-Path -Parent $projectDir
$repoRoot = Split-Path -Parent $srcDir
$serverScript = Join-Path -Path $PSScriptRoot -ChildPath "Deploy-ServerDev.ps1"
$pluginScript = Join-Path -Path $PSScriptRoot -ChildPath "Deploy-PluginDev.ps1"
$allScript = Join-Path -Path $PSScriptRoot -ChildPath "Deploy-DevAll.ps1"

function Invoke-GitLines {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $output = & git @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }

    return @($output | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function Test-GitRef {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Ref
    )

    & git rev-parse --verify --quiet $Ref | Out-Null
    return $LASTEXITCODE -eq 0
}

function Convert-ToRepoPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    return $Path.Replace("\", "/")
}

function Add-UniquePath {
    param(
        [System.Collections.Generic.HashSet[string]]$Set,

        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $normalized = Convert-ToRepoPath -Path $Path
    if (-not [string]::IsNullOrWhiteSpace($normalized)) {
        [void]$Set.Add($normalized)
    }
}

function Get-ChangedPaths {
    $paths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    if (Test-GitRef -Ref $BaseRef) {
        foreach ($path in Invoke-GitLines -Arguments @("diff", "--name-only", "$BaseRef...HEAD")) {
            Add-UniquePath -Set $paths -Path $path
        }
    }
    else {
        Write-Warning "Base ref '$BaseRef' was not found. Only staged, unstaged, and untracked paths will be classified."
    }

    foreach ($path in Invoke-GitLines -Arguments @("diff", "--name-only")) {
        Add-UniquePath -Set $paths -Path $path
    }

    foreach ($path in Invoke-GitLines -Arguments @("diff", "--cached", "--name-only")) {
        Add-UniquePath -Set $paths -Path $path
    }

    if (-not $IgnoreUntracked) {
        foreach ($path in Invoke-GitLines -Arguments @("ls-files", "--others", "--exclude-standard")) {
            Add-UniquePath -Set $paths -Path $path
        }
    }

    return @($paths | Sort-Object)
}

function Test-IsServerPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    return $Path.StartsWith("src/MarketMafioso.Server/", [System.StringComparison]::OrdinalIgnoreCase) -or
           $Path.StartsWith("src/MarketMafioso.Dashboard/", [System.StringComparison]::OrdinalIgnoreCase) -or
           $Path.StartsWith("tests/MarketMafioso.Server.Tests/", [System.StringComparison]::OrdinalIgnoreCase) -or
           [string]::Equals($Path, ".github/workflows/deploy-vps-marketmafioso-dev.yml", [System.StringComparison]::OrdinalIgnoreCase)
}

function Test-IsPluginPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if ($Path.StartsWith("src/MarketMafioso/tools/", [System.StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    return $Path.StartsWith("src/MarketMafioso/", [System.StringComparison]::OrdinalIgnoreCase) -or
           $Path.StartsWith("tests/MarketMafioso.Tests/", [System.StringComparison]::OrdinalIgnoreCase)
}

function Test-IsNoDeployPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    return $Path.StartsWith("docs/", [System.StringComparison]::OrdinalIgnoreCase) -or
           $Path.StartsWith("src/MarketMafioso/tools/", [System.StringComparison]::OrdinalIgnoreCase) -or
           [string]::Equals($Path, "AGENTS.md", [System.StringComparison]::OrdinalIgnoreCase) -or
           [string]::Equals($Path, "README.md", [System.StringComparison]::OrdinalIgnoreCase)
}

function Write-PathGroup {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Title,

        [string[]]$Paths
    )

    if ($Paths.Count -eq 0) {
        return
    }

    Write-Host $Title
    foreach ($path in $Paths) {
        Write-Host "  - $path"
    }
}

function Invoke-DeployScript {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ScriptPath,

        [string[]]$Arguments = @(),

        [Parameter(Mandatory = $true)]
        [string]$Reason
    )

    if (-not $PSCmdlet.ShouldProcess($Reason, "Run $ScriptPath $($Arguments -join ' ')")) {
        Write-Host "Would run: $ScriptPath $($Arguments -join ' ')"
        return
    }

    & $ScriptPath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$ScriptPath failed with exit code $LASTEXITCODE."
    }
}

Push-Location $repoRoot
try {
    $changedPaths = Get-ChangedPaths
    if ($changedPaths.Count -eq 0) {
        Write-Host "No changed paths found. No deployment needed."
        return
    }

    $serverPaths = @($changedPaths | Where-Object { Test-IsServerPath -Path $_ })
    $pluginPaths = @($changedPaths | Where-Object { Test-IsPluginPath -Path $_ })
    $noDeployPaths = @($changedPaths | Where-Object { Test-IsNoDeployPath -Path $_ })
    $classified = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($path in @($serverPaths + $pluginPaths + $noDeployPaths)) {
        [void]$classified.Add($path)
    }

    $ambiguousPaths = @($changedPaths | Where-Object { -not $classified.Contains($_) })

    Write-PathGroup -Title "Server deploy paths:" -Paths $serverPaths
    Write-PathGroup -Title "Plugin deploy paths:" -Paths $pluginPaths
    Write-PathGroup -Title "No-deploy paths:" -Paths $noDeployPaths

    if ($ambiguousPaths.Count -gt 0) {
        Write-PathGroup -Title "Ambiguous paths:" -Paths $ambiguousPaths
        throw "Changed paths include ambiguous deployment surfaces. Run Deploy-ServerDev.ps1, Deploy-PluginDev.ps1, or Deploy-DevAll.ps1 explicitly."
    }

    $hasServer = $serverPaths.Count -gt 0
    $hasPlugin = $pluginPaths.Count -gt 0

    if ($hasServer -and $hasPlugin) {
        $arguments = @("-Ref", "local-dev")
        if ($SkipServerSmoke) {
            $arguments += "-SkipServerSmoke"
        }

        if (-not [string]::IsNullOrWhiteSpace($PluginConfiguration)) {
            $arguments += @("-PluginConfiguration", $PluginConfiguration)
        }

        Invoke-DeployScript -ScriptPath $allScript -Arguments $arguments -Reason "server and plugin changes"
        return
    }

    if ($hasServer) {
        $arguments = @("-Ref", "local-dev")
        if ($SkipServerSmoke) {
            $arguments += "-SkipSmoke"
        }

        Invoke-DeployScript -ScriptPath $serverScript -Arguments $arguments -Reason "server changes"
        return
    }

    if ($hasPlugin) {
        $arguments = @()
        if (-not [string]::IsNullOrWhiteSpace($PluginConfiguration)) {
            $arguments += @("-Configuration", $PluginConfiguration)
        }

        Invoke-DeployScript -ScriptPath $pluginScript -Arguments $arguments -Reason "plugin changes"
        return
    }

    Write-Host "Only no-deploy paths changed. No deployment needed."
}
finally {
    Pop-Location
}
