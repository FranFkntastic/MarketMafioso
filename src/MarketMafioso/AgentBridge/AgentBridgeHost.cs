using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MarketMafioso.AgentBridge;

public sealed class AgentBridgeHost : IDisposable
{
    private const int MaxRequestCharacters = 16_384;
    private readonly Configuration config;
    private readonly string configDirectory;
    private readonly Func<Action, Task> dispatchOnFramework;
    private readonly Func<AgentBridgeTruth> captureTruth;
    private readonly AgentBridgeProofStore proofStore;
    private readonly Action openMainWindow;
    private readonly Action openAcquisitionDiagnostics;
    private readonly Action<string> openProofWindow;
    private readonly Func<string, bool> selectMainTab;
    private readonly Action captureInputState;
    private readonly Action stopRoute;
    private readonly Func<CancellationToken, Task<AgentBridgeCaptureReceipt>> captureViewport;
    private readonly JsonSerializerOptions jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private CancellationTokenSource? cancellation;
    private Task? listenTask;
    private long revision;

    public AgentBridgeHost(
        Configuration config,
        string configDirectory,
        Func<Action, Task> dispatchOnFramework,
        Func<AgentBridgeTruth> captureTruth,
        AgentBridgeProofStore proofStore,
        Action openMainWindow,
        Action openAcquisitionDiagnostics,
        Action<string> openProofWindow,
        Func<string, bool> selectMainTab,
        Action captureInputState,
        Action stopRoute,
        Func<CancellationToken, Task<AgentBridgeCaptureReceipt>> captureViewport)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.configDirectory = configDirectory ?? throw new ArgumentNullException(nameof(configDirectory));
        this.dispatchOnFramework = dispatchOnFramework ?? throw new ArgumentNullException(nameof(dispatchOnFramework));
        this.captureTruth = captureTruth ?? throw new ArgumentNullException(nameof(captureTruth));
        this.proofStore = proofStore ?? throw new ArgumentNullException(nameof(proofStore));
        this.openMainWindow = openMainWindow ?? throw new ArgumentNullException(nameof(openMainWindow));
        this.openAcquisitionDiagnostics = openAcquisitionDiagnostics ?? throw new ArgumentNullException(nameof(openAcquisitionDiagnostics));
        this.openProofWindow = openProofWindow ?? throw new ArgumentNullException(nameof(openProofWindow));
        this.selectMainTab = selectMainTab ?? throw new ArgumentNullException(nameof(selectMainTab));
        this.captureInputState = captureInputState ?? throw new ArgumentNullException(nameof(captureInputState));
        this.stopRoute = stopRoute ?? throw new ArgumentNullException(nameof(stopRoute));
        this.captureViewport = captureViewport ?? throw new ArgumentNullException(nameof(captureViewport));
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

        if (string.IsNullOrWhiteSpace(config.AgentBridgeAccessToken))
        {
            config.AgentBridgeAccessToken = Guid.NewGuid().ToString("N");
            config.Save();
        }

        Directory.CreateDirectory(BridgeDirectory);
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
                    PipeOptions.Asynchronous);
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

        if (request == null || !string.Equals(request.Token, config.AgentBridgeAccessToken, StringComparison.Ordinal))
            return AgentBridgeResponse.Fail("Bridge authentication failed.");

        AgentBridgeProofReceipt? receipt = null;
        switch (request.Command?.Trim().ToLowerInvariant())
        {
            case "hello":
                return AgentBridgeResponse.Ok("Bridge is ready.");
            case "get-snapshot":
                await dispatchOnFramework(() => receipt = AgentBridgeProofFactory.Create(
                    captureTruth(),
                    Interlocked.Increment(ref revision),
                    challenge: null)).ConfigureAwait(false);
                return AgentBridgeResponse.Ok("Snapshot captured.", receipt);
            case "open-main-window":
                await dispatchOnFramework(openMainWindow).ConfigureAwait(false);
                AppendAudit("open-main-window", "accepted");
                return AgentBridgeResponse.Ok("Main window opened.");
            case "open-acquisition-diagnostics":
                await dispatchOnFramework(openAcquisitionDiagnostics).ConfigureAwait(false);
                AppendAudit("open-acquisition-diagnostics", "accepted");
                return AgentBridgeResponse.Ok("Acquisition diagnostics opened.");
            case "capture-proof":
                await dispatchOnFramework(() => receipt = CaptureProof(request.Challenge, openWindow: true)).ConfigureAwait(false);
                AppendAudit("capture-proof", receipt!.ProofId);
                return AgentBridgeResponse.Ok("Proof captured; wait for the in-game proof window to render before reading it again.", receipt);
            case "select-main-tab":
                var tabSelected = false;
                await dispatchOnFramework(() => tabSelected = selectMainTab(request.Target ?? string.Empty)).ConfigureAwait(false);
                AppendAudit("select-main-tab", tabSelected ? request.Target ?? string.Empty : "rejected");
                return tabSelected
                    ? AgentBridgeResponse.Ok($"Queued main tab {request.Target} for the next in-game frame.")
                    : AgentBridgeResponse.Fail("Requested main tab is unavailable or not allowed.");
            case "capture-input-state":
                await dispatchOnFramework(captureInputState).ConfigureAwait(false);
                AppendAudit("capture-input-state", "accepted");
                return AgentBridgeResponse.Ok("Market-board input state capture requested.");
            case "stop-route":
                await dispatchOnFramework(stopRoute).ConfigureAwait(false);
                AppendAudit("stop-route", "accepted");
                return AgentBridgeResponse.Ok("Route stop requested.");
            case "capture-screen":
                if (!string.IsNullOrWhiteSpace(request.Target))
                {
                    var captureTabSelected = false;
                    await dispatchOnFramework(() => captureTabSelected = selectMainTab(request.Target)).ConfigureAwait(false);
                    if (!captureTabSelected)
                        return AgentBridgeResponse.Fail("Requested capture tab is unavailable or not allowed.");
                }
                using (var captureTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    captureTimeout.CancelAfter(TimeSpan.FromSeconds(12));
                    try
                    {
                        var capture = await captureViewport(captureTimeout.Token).ConfigureAwait(false);
                        AppendAudit("capture-screen", capture.CaptureId);
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
        var receipt = proofStore.Capture(captureTruth(), Interlocked.Increment(ref revision), challenge);
        if (openWindow)
            openProofWindow(receipt.ProofId);
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
        if (File.Exists(DiscoveryPath))
            File.Delete(DiscoveryPath);
    }

    private string BridgeDirectory => Path.Combine(configDirectory, "agent-bridge");
    private string DiscoveryPath => Path.Combine(BridgeDirectory, $"discovery-{Environment.ProcessId}.json");
    private string AuditPath => Path.Combine(BridgeDirectory, "audit.jsonl");

    private void AppendAudit(string action, string result)
    {
        Directory.CreateDirectory(BridgeDirectory);
        File.AppendAllText(AuditPath, JsonSerializer.Serialize(new { atUtc = DateTimeOffset.UtcNow, action, result }, jsonOptions) + Environment.NewLine);
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
    public string? ProofId { get; init; }
}

public sealed record AgentBridgeResponse
{
    public required bool Success { get; init; }
    public required string Message { get; init; }
    public object? Receipt { get; init; }

    public static AgentBridgeResponse Ok(string message, object? receipt = null) => new() { Success = true, Message = message, Receipt = receipt };
    public static AgentBridgeResponse Fail(string message) => new() { Success = false, Message = message };
}
