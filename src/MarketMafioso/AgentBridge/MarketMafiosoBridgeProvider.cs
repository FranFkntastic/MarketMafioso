using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.AgentBridge;

namespace MarketMafioso.AgentBridge;

/// <summary>
/// Product-specific state and semantic actions exposed by MarketMafioso.
/// Transport, authentication, discovery, capture storage, and review orchestration do not belong here.
/// </summary>
public interface IMarketMafiosoBridgeProvider
{
    AgentBridgeTruth CreateSnapshot();
    void OpenMainWindow();
    void CloseMainWindow();
    void OpenAcquisitionDiagnostics();
    void OpenProof(string proofId);
    bool TrySelectMainTab(string tabName);
    void CaptureInputState();
    void StopRoute();
    IReadOnlyList<AgentBridgeReviewSurfaceDescriptor> GetReviewSurfaces();
    AgentBridgeUiReviewFrame GetControlSurface();
    AgentBridgeUiControlReview ReviewControl(string controlId);
    AgentBridgeUiControlInvocation InvokeControl(string controlId, long frameId);
}

public sealed class MarketMafiosoBridgeProvider : IMarketMafiosoBridgeProvider
{
    private static readonly IReadOnlyList<AgentBridgeReviewSurfaceDescriptor> PublicReviewSurfaces =
    [
        new("squire", "Squire", "select-main-tab", "Squire", 30),
        new("workshop-logistics", "Workshop Logistics", "select-main-tab", "Workshop Logistics", 40),
        new("workshop-logistics.combined", "Workshop Logistics - Queue and Materials", "select-main-tab", "Workshop Logistics/Combined", 41),
        new("workshop-logistics.queue", "Workshop Logistics - Queue", "select-main-tab", "Workshop Logistics/Queue", 42),
        new("workshop-logistics.materials", "Workshop Logistics - Materials", "select-main-tab", "Workshop Logistics/Materials", 43),
        new("workshop-logistics.assembly", "Workshop Logistics - Assembly", "select-main-tab", "Workshop Logistics/Assembly", 44),
        new("retainers", "Retainers", "select-main-tab", "Retainers", 50),
        new("retainers.deposit", "Retainers - Quick deposit", "select-main-tab", "Retainers/Quick deposit", 51),
        new("retainers.stock", "Retainers - Browse stock", "select-main-tab", "Retainers/Browse stock", 52),
        new("retainers.plan", "Retainers - Withdrawal plan", "select-main-tab", "Retainers/Withdrawal plan", 53),
        new("diagnostics", "Diagnostics", "select-main-tab", "Diagnostics", 70),
        new("settings", "Settings", "select-main-tab", "Settings", 80),
        new("status", "Status", "select-main-tab", "Status", 90),
    ];

    private static readonly IReadOnlyList<AgentBridgeReviewSurfaceDescriptor> MarketAcquisitionReviewSurfaces =
    [
        new("market-acquisition", "Market Acquisition", "select-main-tab", "Market Acquisition", 60),
        new("market-acquisition.inbox", "Market Acquisition - Inbox", "select-main-tab", "Market Acquisition/Inbox", 62),
        new("market-acquisition.workbench", "Market Acquisition - Workbench", "select-main-tab", "Market Acquisition/Workbench", 63),
        new("market-acquisition.route", "Market Acquisition - Route", "select-main-tab", "Market Acquisition/Route", 64),
    ];

    private readonly Func<AgentBridgeTruth> createSnapshot;
    private readonly Action openMainWindow;
    private readonly Action closeMainWindow;
    private readonly Action openAcquisitionDiagnostics;
    private readonly Action<string> openProof;
    private readonly Func<string, bool> trySelectMainTab;
    private readonly Action captureInputState;
    private readonly Action stopRoute;
    private readonly Func<bool> isMarketAcquisitionUnlocked;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;

    public MarketMafiosoBridgeProvider(
        Func<AgentBridgeTruth> createSnapshot,
        Action openMainWindow,
        Action closeMainWindow,
        Action openAcquisitionDiagnostics,
        Action<string> openProof,
        Func<string, bool> trySelectMainTab,
        Action captureInputState,
        Action stopRoute,
        Func<bool> isMarketAcquisitionUnlocked,
        AgentBridgeUiReviewRegistry reviewRegistry)
    {
        this.createSnapshot = createSnapshot ?? throw new ArgumentNullException(nameof(createSnapshot));
        this.openMainWindow = openMainWindow ?? throw new ArgumentNullException(nameof(openMainWindow));
        this.closeMainWindow = closeMainWindow ?? throw new ArgumentNullException(nameof(closeMainWindow));
        this.openAcquisitionDiagnostics = openAcquisitionDiagnostics ?? throw new ArgumentNullException(nameof(openAcquisitionDiagnostics));
        this.openProof = openProof ?? throw new ArgumentNullException(nameof(openProof));
        this.trySelectMainTab = trySelectMainTab ?? throw new ArgumentNullException(nameof(trySelectMainTab));
        this.captureInputState = captureInputState ?? throw new ArgumentNullException(nameof(captureInputState));
        this.stopRoute = stopRoute ?? throw new ArgumentNullException(nameof(stopRoute));
        this.isMarketAcquisitionUnlocked = isMarketAcquisitionUnlocked ?? throw new ArgumentNullException(nameof(isMarketAcquisitionUnlocked));
        this.reviewRegistry = reviewRegistry ?? throw new ArgumentNullException(nameof(reviewRegistry));
    }

    public AgentBridgeTruth CreateSnapshot() => createSnapshot();
    public void OpenMainWindow() => openMainWindow();
    public void CloseMainWindow() => closeMainWindow();
    public void OpenAcquisitionDiagnostics() => openAcquisitionDiagnostics();
    public void OpenProof(string proofId) => openProof(proofId);
    public bool TrySelectMainTab(string tabName) => trySelectMainTab(tabName);
    public void CaptureInputState() => captureInputState();
    public void StopRoute() => stopRoute();
    public IReadOnlyList<AgentBridgeReviewSurfaceDescriptor> GetReviewSurfaces() => isMarketAcquisitionUnlocked()
        ? PublicReviewSurfaces.Concat(MarketAcquisitionReviewSurfaces).OrderBy(surface => surface.Order).ToArray()
        : PublicReviewSurfaces;
    public AgentBridgeUiReviewFrame GetControlSurface() => reviewRegistry.Snapshot();
    public AgentBridgeUiControlReview ReviewControl(string controlId) => reviewRegistry.Review(controlId);
    public AgentBridgeUiControlInvocation InvokeControl(string controlId, long frameId) => reviewRegistry.Invoke(controlId, frameId);
}
