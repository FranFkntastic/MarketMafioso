using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Franthropy.Dalamud.AgentBridge;

namespace MarketMafioso.AgentBridge;

public sealed class AgentBridgeHost : IDisposable
{
    private const int MaxRequestCharacters = 16_384;
    private readonly Configuration config;
    private readonly string configDirectory;
    private readonly Func<Action, Task> dispatchOnFramework;
    private readonly IMarketMafiosoBridgeProvider provider;
    private readonly AgentBridgeProofStore proofStore;
    private readonly Func<bool, CancellationToken, Task<AgentBridgeCaptureReceipt>> captureViewport;
    private readonly Func<bool> screenshotsEnabled;
    private readonly Func<string, AgentBridgeUiCaptureTransactionHandle> beginCapturePresentation;
    private readonly Func<string, AgentBridgeUiCaptureTransactionResult> completeCapturePresentation;
    private readonly Func<string, AgentBridgeUiCaptureTransactionResult> cancelCapturePresentation;
    private readonly JsonSerializerOptions jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private CancellationTokenSource? cancellation;
    private Task? listenTask;
    private string? accessToken;
    private long revision;

    public AgentBridgeHost(
        Configuration config,
        string configDirectory,
        Func<Action, Task> dispatchOnFramework,
        IMarketMafiosoBridgeProvider provider,
        AgentBridgeProofStore proofStore,
        Func<bool, CancellationToken, Task<AgentBridgeCaptureReceipt>> captureViewport,
        Func<bool> screenshotsEnabled,
        Func<string, AgentBridgeUiCaptureTransactionHandle> beginCapturePresentation,
        Func<string, AgentBridgeUiCaptureTransactionResult> completeCapturePresentation,
        Func<string, AgentBridgeUiCaptureTransactionResult> cancelCapturePresentation)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.configDirectory = configDirectory ?? throw new ArgumentNullException(nameof(configDirectory));
        this.dispatchOnFramework = dispatchOnFramework ?? throw new ArgumentNullException(nameof(dispatchOnFramework));
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
        this.proofStore = proofStore ?? throw new ArgumentNullException(nameof(proofStore));
        this.captureViewport = captureViewport ?? throw new ArgumentNullException(nameof(captureViewport));
        this.screenshotsEnabled = screenshotsEnabled ?? throw new ArgumentNullException(nameof(screenshotsEnabled));
        this.beginCapturePresentation = beginCapturePresentation ?? throw new ArgumentNullException(nameof(beginCapturePresentation));
        this.completeCapturePresentation = completeCapturePresentation ?? throw new ArgumentNullException(nameof(completeCapturePresentation));
        this.cancelCapturePresentation = cancelCapturePresentation ?? throw new ArgumentNullException(nameof(cancelCapturePresentation));
    }

    public string PipeName => $"MarketMafioso.AgentBridge.{Environment.ProcessId}";

    public void Tick()
    {
        if (IsBridgeEnabledForThisBuild())
        {
            EnsureStarted();
            return;
        }

        Stop();
    }

    private bool IsBridgeEnabledForThisBuild()
    {
#if DEBUG
        return config.EnableAgentBridge;
#else
        return false;
#endif
    }

    public void Dispose() => Stop();

    private void EnsureStarted()
    {
        if (listenTask != null)
            return;

        accessToken = GetOrCreateAccessToken();

        Directory.CreateDirectory(BridgeDirectory);
        if (!config.EnableAgentBridgeAudit && File.Exists(AuditPath))
            File.Delete(AuditPath);
        File.WriteAllText(DiscoveryPath, JsonSerializer.Serialize(new AgentBridgeDiscovery
        {
            SchemaVersion = 1,
            PipeName = PipeName,
            ProcessId = Environment.ProcessId,
            PluginInstanceId = config.PluginInstanceId,
        }, jsonOptions));
        cancellation = new CancellationTokenSource();
        listenTask = Task.Run(() => ListenLoopAsync(cancellation.Token));
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                await using var writer = new StreamWriter(pipe) { AutoFlush = true };
                var requestJson = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                var response = await HandleRequestAsync(requestJson, cancellationToken).ConfigureAwait(false);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, jsonOptions)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                AppendAudit("host-error", ex.GetType().Name);
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<AgentBridgeResponse> HandleRequestAsync(string? requestJson, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestJson) || requestJson.Length > MaxRequestCharacters)
            return AgentBridgeResponse.Fail("Invalid bridge request.");

        AgentBridgeRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<AgentBridgeRequest>(requestJson, jsonOptions);
        }
        catch (JsonException)
        {
            return AgentBridgeResponse.Fail("Bridge request JSON is invalid.");
        }

        if (request == null || !string.Equals(request.Token, accessToken, StringComparison.Ordinal))
            return AgentBridgeResponse.Fail("Bridge authentication failed.");

        AgentBridgeProofReceipt? receipt = null;
        switch (request.Command?.Trim().ToLowerInvariant())
        {
            case "hello":
                return AgentBridgeResponse.Ok("Bridge is ready.");
            case "get-snapshot":
                await dispatchOnFramework(() => receipt = AgentBridgeProofFactory.Create(
                    provider.CreateSnapshot(),
                    Interlocked.Increment(ref revision),
                    challenge: null)).ConfigureAwait(false);
                return AgentBridgeResponse.Ok("Snapshot captured.", receipt);
            case "get-control-surface":
                return AgentBridgeResponse.Ok("Control surface captured.", provider.GetControlSurface());
            case "get-control":
                if (string.IsNullOrWhiteSpace(request.Target))
                    return AgentBridgeResponse.Fail("A control ID is required.");
                var controlReview = provider.ReviewControl(request.Target);
                return controlReview.Control == null
                    ? new AgentBridgeResponse { Success = false, Message = "The requested control is not rendered.", Receipt = controlReview }
                    : AgentBridgeResponse.Ok("Reviewed control captured.", controlReview);
            case "get-review-surfaces":
                return AgentBridgeResponse.Ok("Review surfaces captured.", provider.GetReviewSurfaces());
            case "get-ui-automation-capabilities":
                return AgentBridgeResponse.Ok("Rendered UI automation capabilities captured.", provider.GetUiAutomationCapabilities());
            case "open-synthetic-advisor-review":
                var syntheticAdvisorOpened = false;
                await dispatchOnFramework(() => syntheticAdvisorOpened = provider.TryOpenSyntheticAdvisorReview()).ConfigureAwait(false);
                return syntheticAdvisorOpened
                    ? AgentBridgeResponse.Ok("Debug-only synthetic advisor review opened.")
                    : AgentBridgeResponse.Fail("Synthetic advisor review is unavailable in this build.");
            case "invoke-control":
                if (string.IsNullOrWhiteSpace(request.Target) || request.FrameId is null)
                    return AgentBridgeResponse.Fail("Control ID and reviewed frame ID are required.");
                AgentBridgeUiControlInvocation? invocation = null;
                await dispatchOnFramework(() => invocation = provider.InvokeControl(request.Target, request.FrameId.Value)).ConfigureAwait(false);
                if (invocation == null)
                    return AgentBridgeResponse.Fail("Control invocation did not complete on the framework thread.");
                AppendAudit("invoke-control", invocation.Success ? request.Target : "rejected");
                return invocation.Success
                    ? AgentBridgeResponse.Ok(invocation.Message, invocation.Frame)
                    : AgentBridgeResponse.Fail(invocation.Message);
            case "open-main-window":
                await dispatchOnFramework(provider.OpenMainWindow).ConfigureAwait(false);
                AppendAudit("open-main-window", "accepted");
                return AgentBridgeResponse.Ok("Main window opened.");
            case "close-main-window":
                await dispatchOnFramework(provider.CloseMainWindow).ConfigureAwait(false);
                AppendAudit("close-main-window", "accepted");
                return AgentBridgeResponse.Ok("Main window closed.");
            case "begin-capture-presentation":
                if (!screenshotsEnabled())
                    return AgentBridgeResponse.Fail("Agent bridge screenshots are disabled by local configuration.");
                if (request.Target is not ("mmf.main-window" or "mmf.main-window.compact"))
                    return AgentBridgeResponse.Fail("The requested capture presentation target is not registered.");
                AgentBridgeUiCaptureTransactionHandle? handle = null;
                try
                {
                    await dispatchOnFramework(() => handle = beginCapturePresentation(request.Target!)).ConfigureAwait(false);
                    var ready = await handle!.Ready.WaitAsync(cancellationToken).ConfigureAwait(false);
                    return AgentBridgeResponse.Ok("Capture presentation rendered and ready.", ready);
                }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or OperationCanceledException)
                {
                    if (handle != null)
                        await dispatchOnFramework(() => cancelCapturePresentation(handle.TransactionId)).ConfigureAwait(false);
                    return AgentBridgeResponse.Fail($"Capture presentation failed: {ex.Message}");
                }
            case "complete-capture-presentation":
            case "cancel-capture-presentation":
                if (string.IsNullOrWhiteSpace(request.TransactionId))
                    return AgentBridgeResponse.Fail("A capture transaction identifier is required.");
                AgentBridgeUiCaptureTransactionResult? transactionResult = null;
                await dispatchOnFramework(() => transactionResult = string.Equals(request.Command, "complete-capture-presentation", StringComparison.OrdinalIgnoreCase)
                    ? completeCapturePresentation(request.TransactionId)
                    : cancelCapturePresentation(request.TransactionId)).ConfigureAwait(false);
                return transactionResult!.Success
                    ? AgentBridgeResponse.Ok(transactionResult.Message, transactionResult)
                    : AgentBridgeResponse.Fail(transactionResult.Message);
            case "open-acquisition-diagnostics":
                await dispatchOnFramework(provider.OpenAcquisitionDiagnostics).ConfigureAwait(false);
                AppendAudit("open-acquisition-diagnostics", "accepted");
                return AgentBridgeResponse.Ok("Acquisition diagnostics opened.");
            case "capture-proof":
                await dispatchOnFramework(() => receipt = CaptureProof(request.Challenge, openWindow: true)).ConfigureAwait(false);
                AppendAudit("capture-proof", receipt!.ProofId);
                return AgentBridgeResponse.Ok("Proof captured; wait for the in-game proof window to render before reading it again.", receipt);
            case "select-main-tab":
                var tabSelected = false;
                await dispatchOnFramework(() => tabSelected = provider.TrySelectMainTab(request.Target ?? string.Empty)).ConfigureAwait(false);
                AppendAudit("select-main-tab", tabSelected ? request.Target ?? string.Empty : "rejected");
                return tabSelected
                    ? AgentBridgeResponse.Ok($"Queued main tab {request.Target} for the next in-game frame.")
                    : AgentBridgeResponse.Fail("Requested main tab is unavailable or not allowed.");
            case "capture-input-state":
                await dispatchOnFramework(provider.CaptureInputState).ConfigureAwait(false);
                AppendAudit("capture-input-state", "accepted");
                return AgentBridgeResponse.Ok("Market-board input state capture requested.");
            case "open-character-ui":
                await dispatchOnFramework(provider.OpenCharacterUi).ConfigureAwait(false);
                AppendAudit("open-character-ui", "accepted");
                return AgentBridgeResponse.Ok("Character UI open requested.");
            case "close-character-ui":
                var characterUiClosed = false;
                await dispatchOnFramework(() => characterUiClosed = provider.TryCloseCharacterUi()).ConfigureAwait(false);
                return characterUiClosed
                    ? AgentBridgeResponse.Ok("Visible Character UI closed through its rendered addon.")
                    : AgentBridgeResponse.Fail("No visible Character UI was available to close.");
            case "close-blocking-select-string-ui":
                var selectStringClosed = false;
                await dispatchOnFramework(() => selectStringClosed = provider.TryCloseBlockingSelectStringUi()).ConfigureAwait(false);
                return selectStringClosed
                    ? AgentBridgeResponse.Ok("Visible SelectString UI closed through its rendered addon.")
                    : AgentBridgeResponse.Fail("No visible SelectString UI was available to close.");
            case "switch-calibration-job-ui":
                var calibrationJobSwitched = false;
                await dispatchOnFramework(() => calibrationJobSwitched = provider.TrySwitchCalibrationJobUi(request.Target ?? string.Empty)).ConfigureAwait(false);
                return calibrationJobSwitched
                    ? AgentBridgeResponse.Ok($"Calibration job switch requested through the rendered command UI: {request.Target}.")
                    : AgentBridgeResponse.Fail("Target must be Miner, Botanist, or Blacksmith.");
            case "get-character-ui":
                AgentBridgeRenderedUiSnapshot? characterUi = null;
                await dispatchOnFramework(() => characterUi = provider.CaptureCharacterUi()).ConfigureAwait(false);
                return AgentBridgeResponse.Ok("Rendered Character UI captured.", characterUi);
            case "get-retainer-ui":
                AgentBridgeRenderedUiSnapshot? retainerUi = null;
                await dispatchOnFramework(() => retainerUi = provider.CaptureRetainerUi()).ConfigureAwait(false);
                return AgentBridgeResponse.Ok("Already-visible rendered retainer UI captured without changing window focus or UI state.", retainerUi);
            case "begin-retainer-observation-ui":
                Squire.Observation.RenderedRetainerUiPreparationProgress? begunRetainerPreparation = null;
                await dispatchOnFramework(() => begunRetainerPreparation = provider.BeginRetainerObservationUi()).ConfigureAwait(false);
                return begunRetainerPreparation!.Status is Squire.Observation.RenderedRetainerUiPreparationStatus.Traveling or Squire.Observation.RenderedRetainerUiPreparationStatus.Complete
                    ? AgentBridgeResponse.Ok(begunRetainerPreparation.Diagnostic, begunRetainerPreparation)
                    : new AgentBridgeResponse { Success = false, Message = begunRetainerPreparation.Diagnostic, Receipt = begunRetainerPreparation };
            case "advance-retainer-observation-ui":
                Squire.Observation.RenderedRetainerUiPreparationProgress? advancedRetainerPreparation = null;
                await dispatchOnFramework(() => advancedRetainerPreparation = provider.AdvanceRetainerObservationUi()).ConfigureAwait(false);
                return advancedRetainerPreparation!.Status != Squire.Observation.RenderedRetainerUiPreparationStatus.Failed
                    ? AgentBridgeResponse.Ok(advancedRetainerPreparation.Diagnostic, advancedRetainerPreparation)
                    : new AgentBridgeResponse { Success = false, Message = advancedRetainerPreparation.Diagnostic, Receipt = advancedRetainerPreparation };
            case "cancel-retainer-observation-ui":
                Squire.Observation.RenderedRetainerUiPreparationProgress? cancelledRetainerPreparation = null;
                await dispatchOnFramework(() => cancelledRetainerPreparation = provider.CancelRetainerObservationUi()).ConfigureAwait(false);
                return AgentBridgeResponse.Ok(cancelledRetainerPreparation!.Diagnostic, cancelledRetainerPreparation);
            case "hover-character-node-ui":
                var characterNodeHovered = false;
                await dispatchOnFramework(() => characterNodeHovered = provider.TryHoverCharacterNodeUi(request.Target ?? string.Empty)).ConfigureAwait(false);
                return characterNodeHovered
                    ? AgentBridgeResponse.Ok($"Virtual UI rollover dispatched to rendered Character node {request.Target}; the OS cursor and window focus were not changed.")
                    : AgentBridgeResponse.Fail("The requested rendered Character drag/drop node is unavailable or has no registered rollover event.");
            case "release-character-node-ui":
            case "restore-character-ui-cursor":
                var characterCursorRestored = false;
                await dispatchOnFramework(() => characterCursorRestored = provider.RestoreCharacterUiCursor()).ConfigureAwait(false);
                return characterCursorRestored
                    ? AgentBridgeResponse.Ok("Virtual Character-node rollover released; the OS cursor was never changed.")
                    : AgentBridgeResponse.Fail("No virtual Character-node rollover was active.");
            case "get-gathering-stats-ui":
                Squire.Observation.RenderedGatheringStatsObservation? gatheringStats = null;
                await dispatchOnFramework(() => gatheringStats = provider.CaptureGatheringStatsUi()).ConfigureAwait(false);
                return gatheringStats!.Status == Squire.Observation.RenderedCharacterObservationStatus.Complete
                    ? AgentBridgeResponse.Ok("Rendered gathering stats captured.", gatheringStats)
                    : new AgentBridgeResponse { Success = false, Message = gatheringStats.Diagnostic, Receipt = gatheringStats };
            case "get-character-equipment-layout-ui":
                Squire.Observation.RenderedCharacterEquipmentLayout? equipmentLayout = null;
                await dispatchOnFramework(() => equipmentLayout = provider.CaptureCharacterEquipmentLayoutUi()).ConfigureAwait(false);
                return equipmentLayout!.Status == Squire.Observation.RenderedEquipmentLayoutStatus.Complete
                    ? AgentBridgeResponse.Ok("Rendered Character equipment layout captured.", equipmentLayout)
                    : new AgentBridgeResponse { Success = false, Message = equipmentLayout.Diagnostic, Receipt = equipmentLayout };
            case "get-item-detail-ui":
                Squire.Observation.RenderedItemDetailObservation? itemDetail = null;
                await dispatchOnFramework(() => itemDetail = provider.CaptureItemDetailUi()).ConfigureAwait(false);
                return itemDetail!.Status == Squire.Observation.RenderedItemDetailStatus.Complete
                    ? AgentBridgeResponse.Ok("Rendered Item Detail captured.", itemDetail)
                    : new AgentBridgeResponse { Success = false, Message = itemDetail.Diagnostic, Receipt = itemDetail };
            case "begin-character-equipment-scan-ui":
                Squire.Observation.RenderedEquipmentScanProgress? begunEquipmentScan = null;
                await dispatchOnFramework(() => begunEquipmentScan = provider.BeginCharacterEquipmentScanUi()).ConfigureAwait(false);
                return begunEquipmentScan!.Status == Squire.Observation.RenderedEquipmentScanStatus.ReadyToHover
                    ? AgentBridgeResponse.Ok(begunEquipmentScan.Diagnostic, begunEquipmentScan)
                    : new AgentBridgeResponse { Success = false, Message = begunEquipmentScan.Diagnostic, Receipt = begunEquipmentScan };
            case "advance-character-equipment-scan-ui":
                Squire.Observation.RenderedEquipmentScanStepResult? advancedEquipmentScan = null;
                await dispatchOnFramework(() => advancedEquipmentScan = provider.AdvanceCharacterEquipmentScanUi()).ConfigureAwait(false);
                return advancedEquipmentScan!.ActionAccepted
                    ? AgentBridgeResponse.Ok(advancedEquipmentScan.Message, advancedEquipmentScan.Progress)
                    : new AgentBridgeResponse { Success = false, Message = advancedEquipmentScan.Message, Receipt = advancedEquipmentScan.Progress };
            case "cancel-character-equipment-scan-ui":
                Squire.Observation.RenderedEquipmentScanProgress? cancelledEquipmentScan = null;
                await dispatchOnFramework(() => cancelledEquipmentScan = provider.CancelCharacterEquipmentScanUi()).ConfigureAwait(false);
                return AgentBridgeResponse.Ok(cancelledEquipmentScan!.Diagnostic, cancelledEquipmentScan);
            case "stop-route":
                await dispatchOnFramework(provider.StopRoute).ConfigureAwait(false);
                AppendAudit("stop-route", "accepted");
                return AgentBridgeResponse.Ok("Route stop requested.");
            case "capture-screen":
                if (!screenshotsEnabled())
                    return AgentBridgeResponse.Fail("Agent bridge screenshots are disabled by local configuration.");
                if (!string.IsNullOrWhiteSpace(request.Target))
                {
                    var captureTabSelected = false;
                    await dispatchOnFramework(() => captureTabSelected = provider.TrySelectMainTab(request.Target)).ConfigureAwait(false);
                    if (!captureTabSelected)
                        return AgentBridgeResponse.Fail("Requested capture tab is unavailable or not allowed.");
                }
                using (var captureTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    captureTimeout.CancelAfter(TimeSpan.FromSeconds(12));
                    try
                    {
                        var capture = await captureViewport(request.FullViewport, captureTimeout.Token).ConfigureAwait(false);
                        return AgentBridgeResponse.Ok("Rendered viewport captured.", capture);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        AppendAudit("capture-screen", "timeout");
                        return AgentBridgeResponse.Fail("Rendered viewport capture timed out.");
                    }
                    catch (Exception ex)
                    {
                        AppendAudit("capture-screen", $"failed:{ex.GetType().Name}");
                        return AgentBridgeResponse.Fail($"Rendered viewport capture failed: {ex.Message}");
                    }
                }
            case "get-proof":
                receipt = string.IsNullOrWhiteSpace(request.ProofId)
                    ? proofStore.GetCurrent()
                    : proofStore.Get(request.ProofId);
                return receipt == null
                    ? AgentBridgeResponse.Fail("No proof has been captured.")
                    : AgentBridgeResponse.Ok("Current proof returned.", receipt);
            default:
                return AgentBridgeResponse.Fail("Bridge command is not allowed.");
        }
    }

    private AgentBridgeProofReceipt CaptureProof(string? challenge, bool openWindow)
    {
        var receipt = proofStore.Capture(provider.CreateSnapshot(), Interlocked.Increment(ref revision), challenge);
        if (openWindow)
            provider.OpenProof(receipt.ProofId);
        return receipt;
    }

    private void Stop()
    {
        var activeCancellation = Interlocked.Exchange(ref cancellation, null);
        if (activeCancellation != null)
        {
            activeCancellation.Cancel();
            activeCancellation.Dispose();
        }

        listenTask = null;
        accessToken = null;
        if (File.Exists(DiscoveryPath))
            File.Delete(DiscoveryPath);
    }

    private string BridgeDirectory => Path.Combine(configDirectory, "agent-bridge");
    private string DiscoveryPath => Path.Combine(BridgeDirectory, $"discovery-{Environment.ProcessId}.json");
    private string AuditPath => Path.Combine(BridgeDirectory, "audit.jsonl");

    private void AppendAudit(string action, string result)
    {
        if (!config.EnableAgentBridgeAudit)
            return;
        Directory.CreateDirectory(BridgeDirectory);
        File.AppendAllText(AuditPath, JsonSerializer.Serialize(new { atUtc = DateTimeOffset.UtcNow, action, result }, jsonOptions) + Environment.NewLine);
    }

    private string GetOrCreateAccessToken()
    {
        var entropy = Encoding.UTF8.GetBytes(config.PluginInstanceId);
        if (!string.IsNullOrWhiteSpace(config.AgentBridgeProtectedAccessToken))
        {
            try
            {
                var protectedBytes = Convert.FromBase64String(config.AgentBridgeProtectedAccessToken);
                try
                {
                    return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                        protectedBytes,
                        entropy,
                        DataProtectionScope.CurrentUser));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(protectedBytes);
                }
            }
            catch (CryptographicException)
            {
                config.AgentBridgeProtectedAccessToken = string.Empty;
            }
            catch (FormatException)
            {
                config.AgentBridgeProtectedAccessToken = string.Empty;
            }
        }

        var token = string.IsNullOrWhiteSpace(config.AgentBridgeAccessToken)
            ? Guid.NewGuid().ToString("N")
            : config.AgentBridgeAccessToken;
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), entropy, DataProtectionScope.CurrentUser);
        try
        {
            config.AgentBridgeProtectedAccessToken = Convert.ToBase64String(encrypted);
            config.AgentBridgeAccessToken = string.Empty;
            config.Save();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
        }
        return token;
    }
}

public sealed record AgentBridgeDiscovery
{
    public required int SchemaVersion { get; init; }
    public required string PipeName { get; init; }
    public required int ProcessId { get; init; }
    public required string PluginInstanceId { get; init; }
}

public sealed record AgentBridgeRequest
{
    public string? Token { get; init; }
    public string? Command { get; init; }
    public string? Challenge { get; init; }
    public string? Target { get; init; }
    public long? FrameId { get; init; }
    public string? ProofId { get; init; }
    public bool FullViewport { get; init; }
    public string? TransactionId { get; init; }
}

public sealed record AgentBridgeResponse
{
    public required bool Success { get; init; }
    public required string Message { get; init; }
    public object? Receipt { get; init; }

    public static AgentBridgeResponse Ok(string message, object? receipt = null) => new() { Success = true, Message = message, Receipt = receipt };
    public static AgentBridgeResponse Fail(string message) => new() { Success = false, Message = message };
}
