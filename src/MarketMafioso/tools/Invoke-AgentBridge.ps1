[CmdletBinding()]
param(
    [ValidateSet(
        "hello",
        "get-snapshot",
        "get-control-surface",
        "get-control",
        "get-review-surfaces",
        "get-ui-automation-capabilities",
        "open-synthetic-advisor-review",
        "invoke-control",
        "open-main-window",
        "close-main-window",
        "begin-capture-presentation",
        "complete-capture-presentation",
        "cancel-capture-presentation",
        "open-acquisition-diagnostics",
        "capture-proof",
        "select-main-tab",
        "capture-input-state",
        "open-character-ui",
        "close-character-ui",
        "close-blocking-select-string-ui",
        "switch-calibration-job-ui",
        "open-gearset-list-ui",
        "get-character-ui",
        "get-retainer-ui",
        "begin-retainer-observation-ui",
        "advance-retainer-observation-ui",
        "cancel-retainer-observation-ui",
        "hover-character-node-ui",
        "release-character-node-ui",
        "get-gathering-stats-ui",
        "get-character-equipment-layout-ui",
        "get-item-detail-ui",
        "begin-character-equipment-scan-ui",
        "advance-character-equipment-scan-ui",
        "cancel-character-equipment-scan-ui",
        "stop-route",
        "capture-screen",
        "get-proof"
    )]
    [string]$Command = "hello",

    [string]$Target,
    [long]$FrameId,
    [string]$TransactionId,
    [string]$Challenge,
    [switch]$FullViewport,
    [int]$ProcessId,
    [string]$ConfigRoot = (Join-Path $env:APPDATA "XIVLauncher\pluginConfigs")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Security

$configPath = Join-Path $ConfigRoot "MarketMafioso.json"
$bridgeDirectory = Join-Path $ConfigRoot "MarketMafioso\agent-bridge"

if (-not (Test-Path -LiteralPath $configPath)) {
    throw "MarketMafioso configuration was not found at '$configPath'."
}

$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
if (-not $config.EnableAgentBridge) {
    throw "The MarketMafioso Agent Bridge is disabled in local configuration."
}

$discoveries = @(
    Get-ChildItem -LiteralPath $bridgeDirectory -Filter "discovery-*.json" -File -ErrorAction SilentlyContinue |
        ForEach-Object {
            try {
                $discovery = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
            }
            catch {
                return
            }
            $process = Get-Process -Id $discovery.processId -ErrorAction SilentlyContinue
            if ($null -ne $process -and ($ProcessId -eq 0 -or $discovery.processId -eq $ProcessId)) {
                [pscustomobject]@{
                    Discovery = $discovery
                    LastWriteTimeUtc = $_.LastWriteTimeUtc
                }
            }
        } |
        Sort-Object LastWriteTimeUtc -Descending
)

if ($discoveries.Count -eq 0) {
    $suffix = if ($ProcessId -eq 0) { "" } else { " for process $ProcessId" }
    throw "No live MarketMafioso Agent Bridge discovery was found$suffix."
}

$selected = $discoveries[0].Discovery
$entropy = [Text.Encoding]::UTF8.GetBytes([string]$config.PluginInstanceId)
$protectedToken = [Convert]::FromBase64String([string]$config.AgentBridgeProtectedAccessToken)
$tokenBytes = [System.Security.Cryptography.ProtectedData]::Unprotect(
    $protectedToken,
    $entropy,
    [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
$token = [Text.Encoding]::UTF8.GetString($tokenBytes)

try {
    $request = @{ token = $token; command = $Command; fullViewport = [bool]$FullViewport }
    if (-not [string]::IsNullOrWhiteSpace($Target)) { $request.target = $Target }
    if ($FrameId -ne 0) { $request.frameId = $FrameId }
    if (-not [string]::IsNullOrWhiteSpace($TransactionId)) { $request.transactionId = $TransactionId }
    if (-not [string]::IsNullOrWhiteSpace($Challenge)) { $request.challenge = $Challenge }

    $pipe = [IO.Pipes.NamedPipeClientStream]::new(
        ".",
        [string]$selected.pipeName,
        [IO.Pipes.PipeDirection]::InOut,
        [IO.Pipes.PipeOptions]::None,
        [Security.Principal.TokenImpersonationLevel]::Impersonation)
    try {
        $pipe.Connect(5000)
        $writer = [IO.StreamWriter]::new($pipe)
        $writer.AutoFlush = $true
        $reader = [IO.StreamReader]::new($pipe)
        $writer.WriteLine(($request | ConvertTo-Json -Compress))
        $reader.ReadLine()
    }
    finally {
        $pipe.Dispose()
    }
}
finally {
    [Array]::Clear($tokenBytes, 0, $tokenBytes.Length)
    [Array]::Clear($protectedToken, 0, $protectedToken.Length)
}
