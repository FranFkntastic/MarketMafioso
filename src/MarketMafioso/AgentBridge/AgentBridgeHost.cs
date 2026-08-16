using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Automation.Ui;
using SharedAgentBridgeHost = Franthropy.Dalamud.AgentBridge.AgentBridgeHost;

namespace MarketMafioso.AgentBridge;

/// <summary>MarketMafioso-specific policy layered on Franthropy's shared authenticated host.</summary>
public sealed class AgentBridgeHost : IDisposable
{
    private readonly Configuration config;
    private readonly Func<Action, Task> scheduleOnFramework;
    private readonly IMarketMafiosoBridgeProvider provider;
    private readonly AgentBridgeProofStore proofStore;
    private readonly Func<bool, CancellationToken, Task<AgentBridgeCaptureReceipt>> captureViewport;
    private readonly Func<bool> screenshotsEnabled;
    private readonly Func<string, AgentBridgeUiCaptureTransactionHandle> beginCapturePresentation;
    private readonly Func<string, AgentBridgeUiCaptureTransactionResult> completeCapturePresentation;
    private readonly Func<string, AgentBridgeUiCaptureTransactionResult> cancelCapturePresentation;
    private readonly AgentBridgeCommandRouter router = new();
    private readonly SharedAgentBridgeHost host;
    private readonly AgentBridgeRuntimeIdentity runtimeIdentity;
    private readonly (string Id, string Alias) profile;
    private long revision;

    public AgentBridgeHost(
        Configuration config,
        string configDirectory,
        string mainDllPath,
        Func<Action, Task> dispatchOnFramework,
        IMarketMafiosoBridgeProvider provider,
        AgentBridgeProofStore proofStore,
        Func<bool, CancellationToken, Task<AgentBridgeCaptureReceipt>> captureViewport,
        Func<bool> screenshotsEnabled,
        Func<string, AgentBridgeUiCaptureTransactionHandle> beginCapturePresentation,
        Func<string, AgentBridgeUiCaptureTransactionResult> completeCapturePresentation,
        Func<string, AgentBridgeUiCaptureTransactionResult> cancelCapturePresentation,
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IFramework framework)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        scheduleOnFramework = dispatchOnFramework ?? throw new ArgumentNullException(nameof(dispatchOnFramework));
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
        this.proofStore = proofStore ?? throw new ArgumentNullException(nameof(proofStore));
        this.captureViewport = captureViewport ?? throw new ArgumentNullException(nameof(captureViewport));
        this.screenshotsEnabled = screenshotsEnabled ?? throw new ArgumentNullException(nameof(screenshotsEnabled));
        this.beginCapturePresentation = beginCapturePresentation ?? throw new ArgumentNullException(nameof(beginCapturePresentation));
        this.completeCapturePresentation = completeCapturePresentation ?? throw new ArgumentNullException(nameof(completeCapturePresentation));
        this.cancelCapturePresentation = cancelCapturePresentation ?? throw new ArgumentNullException(nameof(cancelCapturePresentation));
        profile = AgentBridgeProfileIdentity.FromPluginConfigDirectory(configDirectory);
        runtimeIdentity = AgentBridgeRuntimeIdentity.FromAssembly("MarketMafioso", Assembly.GetExecutingAssembly(), mainDllPath);
        RegisterCommands();
        new DalamudPluginLifecycleBridge(pluginInterface, commandManager, framework).RegisterCommands(router);
        host = new SharedAgentBridgeHost(new AgentBridgeHostOptions
        {
            ConfigDirectory = configDirectory,
            PluginInstanceId = config.PluginInstanceId,
            PipeName = $"MarketMafioso.AgentBridge.{Environment.ProcessId}",
            GetProtectedAccessToken = () => config.AgentBridgeProtectedAccessToken,
            SetProtectedAccessToken = value =>
            {
                config.AgentBridgeProtectedAccessToken = value;
                config.AgentBridgeAccessToken = string.Empty;
            },
            SaveConfiguration = config.Save,
            CreateManifest = CreateManifest,
            HandleRequestAsync = router.HandleAsync,
            EnableAudit = config.EnableAgentBridgeAudit,
            RequestTimeout = TimeSpan.FromSeconds(15),
        });
    }

    public string PipeName => $"MarketMafioso.AgentBridge.{Environment.ProcessId}";

    public void Tick()
    {
#if DEBUG
        if (config.EnableAgentBridge)
            host.Start();
        else
            host.Stop();
#else
        host.Stop();
#endif
    }

    public void Dispose() => host.Dispose();

    private AgentBridgeManifest CreateManifest() => new(
        2,
        runtimeIdentity,
        profile.Id,
        profile.Alias,
        "MarketMafioso.proof.v2",
        [
            new("snapshot"), new("reviewed-actions"), new("proofs"),
            new("encrypted-capture"), new("capture-transactions"), new("plugin-lifecycle"),
            new("market-actor-capability-probe"),
        ],
        provider.GetReviewSurfaces(),
        provider.GetCaptureSurfaces(),
        provider.GetActions());

    private void RegisterCommands()
    {
        router.Register("get-snapshot", GetSnapshotAsync);
        router.Register("get-control-surface", _ => AgentBridgeResponse.Ok("Control surface captured.", provider.GetControlSurface()));
        router.Register("get-control", GetControl);
        router.Register("get-review-surfaces", _ => AgentBridgeResponse.Ok("Review surfaces captured.", provider.GetReviewSurfaces()));
        router.Register("get-capture-surfaces", _ => AgentBridgeResponse.Ok("Capture surfaces captured.", provider.GetCaptureSurfaces()));
        router.Register("invoke-control", InvokeControlAsync);
        router.Register("open-main-window", async (_, token) => await RunAsync(provider.OpenMainWindow, token, "Main window opened.").ConfigureAwait(false));
        router.Register("close-main-window", async (_, token) => await RunAsync(provider.CloseMainWindow, token, "Main window closed.").ConfigureAwait(false));
        router.Register("open-acquisition-diagnostics", async (_, token) => await RunAsync(provider.OpenAcquisitionDiagnostics, token, "Acquisition diagnostics opened.").ConfigureAwait(false));
        router.Register("select-main-tab", SelectMainTabAsync);
        router.Register("capture-input-state", async (_, token) => await RunAsync(provider.CaptureInputState, token, "Market-board input state capture requested.").ConfigureAwait(false));
        router.Register("stop-route", async (_, token) => await RunAsync(provider.StopRoute, token, "Route stop requested.").ConfigureAwait(false));
        router.Register("probe-market-actor-names", ProbeMarketActorNamesAsync);
        router.Register("capture-proof", CaptureProofAsync);
        router.Register("get-proof", GetProof);
        router.Register("begin-capture-presentation", BeginCapturePresentationAsync);
        router.Register("complete-capture-presentation", CompleteCapturePresentationAsync);
        router.Register("cancel-capture-presentation", CompleteCapturePresentationAsync);
        router.Register("capture-screen", CaptureScreenAsync);
    }

    private async ValueTask<AgentBridgeResponse> GetSnapshotAsync(AgentBridgeRequest _, CancellationToken token)
    {
        AgentBridgeProofReceipt? receipt = null;
        await DispatchAsync(() => receipt = AgentBridgeProofFactory.Create(provider.CreateSnapshot(), Interlocked.Increment(ref revision), null), token).ConfigureAwait(false);
        return AgentBridgeResponse.Ok("Snapshot captured.", receipt);
    }

    private async ValueTask<AgentBridgeResponse> ProbeMarketActorNamesAsync(AgentBridgeRequest _, CancellationToken token)
    {
        AgentBridgeMarketActorCapabilityTruth? receipt = null;
        await DispatchAsync(() => receipt = provider.ProbeMarketActorNames(), token).ConfigureAwait(false);
        return AgentBridgeResponse.Ok("Requested names for up to 24 actors from the current correlated market book.", receipt);
    }

    private AgentBridgeResponse GetControl(AgentBridgeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Target)) return AgentBridgeResponse.Fail("A control ID is required.");
        var review = provider.ReviewControl(request.Target);
        return review.Control is null
            ? new AgentBridgeResponse { Success = false, Message = "The requested control is not rendered.", Receipt = review }
            : AgentBridgeResponse.Ok("Reviewed control captured.", review);
    }

    private async ValueTask<AgentBridgeResponse> InvokeControlAsync(AgentBridgeRequest request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.Target) || request.FrameId is null)
            return AgentBridgeResponse.Fail("Control ID and reviewed frame ID are required.");
        AgentBridgeUiControlInvocation? invocation = null;
        await DispatchAsync(() => invocation = provider.InvokeControl(request.Target, request.FrameId.Value, request.Arguments), token).ConfigureAwait(false);
        if (invocation is null) return AgentBridgeResponse.Fail("Control invocation did not complete on the framework thread.");
        return invocation.Success
            ? AgentBridgeResponse.Ok(invocation.Message, invocation, invocation.Action?.OperationId)
            : new AgentBridgeResponse { Success = false, Message = invocation.Message, Receipt = invocation };
    }

    private async ValueTask<AgentBridgeResponse> SelectMainTabAsync(AgentBridgeRequest request, CancellationToken token)
    {
        var selected = false;
        await DispatchAsync(() => selected = provider.TrySelectMainTab(request.Target ?? string.Empty), token).ConfigureAwait(false);
        return selected ? AgentBridgeResponse.Ok($"Queued main tab {request.Target} for the next in-game frame.") : AgentBridgeResponse.Fail("Requested main tab is unavailable or not allowed.");
    }

    private async ValueTask<AgentBridgeResponse> CaptureProofAsync(AgentBridgeRequest request, CancellationToken token)
    {
        AgentBridgeProofReceipt? receipt = null;
        await DispatchAsync(() =>
        {
            receipt = proofStore.Capture(provider.CreateSnapshot(), Interlocked.Increment(ref revision), request.Challenge);
            provider.OpenProof(receipt.ProofId);
        }, token).ConfigureAwait(false);
        return AgentBridgeResponse.Ok("Proof captured; wait for the in-game proof window to render before reading it again.", receipt);
    }

    private AgentBridgeResponse GetProof(AgentBridgeRequest request)
    {
        var receipt = string.IsNullOrWhiteSpace(request.ProofId) ? proofStore.GetCurrent() : proofStore.Get(request.ProofId);
        return receipt is null ? AgentBridgeResponse.Fail("No proof has been captured.") : AgentBridgeResponse.Ok("Current proof returned.", receipt);
    }

    private async ValueTask<AgentBridgeResponse> BeginCapturePresentationAsync(AgentBridgeRequest request, CancellationToken token)
    {
        if (!screenshotsEnabled()) return AgentBridgeResponse.Fail("Agent bridge screenshots are disabled by local configuration.");
        if (string.IsNullOrWhiteSpace(request.Target) || !provider.GetCaptureSurfaces().Any(surface => surface.Id == request.Target))
            return AgentBridgeResponse.Fail("The requested capture presentation target is not registered.");
        AgentBridgeUiCaptureTransactionHandle? handle = null;
        try
        {
            await DispatchAsync(() => handle = beginCapturePresentation(request.Target), token).ConfigureAwait(false);
            return AgentBridgeResponse.Ok("Capture presentation rendered and ready.", await handle!.Ready.WaitAsync(token).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            if (handle is not null) await DispatchAsync(() => cancelCapturePresentation(handle.TransactionId), token).ConfigureAwait(false);
            return AgentBridgeResponse.Fail($"Capture presentation failed: {exception.Message}");
        }
    }

    private async ValueTask<AgentBridgeResponse> CompleteCapturePresentationAsync(AgentBridgeRequest request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.TransactionId)) return AgentBridgeResponse.Fail("A capture transaction identifier is required.");
        AgentBridgeUiCaptureTransactionResult? result = null;
        await DispatchAsync(() => result = request.Command == "complete-capture-presentation"
            ? completeCapturePresentation(request.TransactionId)
            : cancelCapturePresentation(request.TransactionId), token).ConfigureAwait(false);
        return result!.Success ? AgentBridgeResponse.Ok(result.Message, result) : AgentBridgeResponse.Fail(result.Message);
    }

    private async ValueTask<AgentBridgeResponse> CaptureScreenAsync(AgentBridgeRequest request, CancellationToken token)
    {
        if (!screenshotsEnabled()) return AgentBridgeResponse.Fail("Agent bridge screenshots are disabled by local configuration.");
        if (!string.IsNullOrWhiteSpace(request.Target))
        {
            var selected = false;
            await DispatchAsync(() => selected = provider.TrySelectMainTab(request.Target), token).ConfigureAwait(false);
            if (!selected) return AgentBridgeResponse.Fail("Requested capture tab is unavailable or not allowed.");
        }
        try { return AgentBridgeResponse.Ok("Rendered viewport captured.", await captureViewport(request.FullViewport, token).ConfigureAwait(false)); }
        catch (OperationCanceledException) { return AgentBridgeResponse.Fail("Rendered viewport capture timed out."); }
        catch (Exception exception) { return AgentBridgeResponse.Fail($"Rendered viewport capture failed: {exception.Message}"); }
    }

    private async ValueTask<AgentBridgeResponse> RunAsync(Action action, CancellationToken token, string message)
    {
        await DispatchAsync(action, token).ConfigureAwait(false);
        return AgentBridgeResponse.Ok(message);
    }

    private Task DispatchAsync(Action action, CancellationToken token) => scheduleOnFramework(action).WaitAsync(token);
}
