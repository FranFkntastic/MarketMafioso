using System;
using System.Collections.Generic;
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
    private static readonly IReadOnlyList<AgentBridgeReviewSurfaceDescriptor> ReviewSurfaces =
    [
        new("overview", "Overview", "select-main-tab", "Overview", 10),
        new("squire", "Squire", "select-main-tab", "Squire", 30),
        new("workshop-logistics", "Workshop Logistics", "select-main-tab", "Workshop Logistics", 40),
        new("workshop-logistics.queue", "Workshop Logistics - Queue", "select-main-tab", "Workshop Logistics/Queue", 41),
        new("workshop-logistics.materials", "Workshop Logistics - Materials", "select-main-tab", "Workshop Logistics/Materials", 42),
        new("workshop-logistics.assembly", "Workshop Logistics - Assembly", "select-main-tab", "Workshop Logistics/Assembly", 43),
        new("restock", "Restock", "select-main-tab", "Restock", 50),
        new("restock.stock", "Restock - Browse stock", "select-main-tab", "Restock/Browse stock", 51),
        new("restock.plan", "Restock - Plan and run", "select-main-tab", "Restock/Plan and run", 52),
        new("market-acquisition", "Market Acquisition", "select-main-tab", "Market Acquisition", 60),
        new("market-acquisition.request", "Market Acquisition - Request", "select-main-tab", "Market Acquisition/Request", 61),
        new("market-acquisition.plan", "Market Acquisition - Plan", "select-main-tab", "Market Acquisition/Plan", 62),
        new("market-acquisition.route", "Market Acquisition - Route", "select-main-tab", "Market Acquisition/Route", 63),
        new("diagnostics", "Diagnostics", "select-main-tab", "Diagnostics", 70),
        new("settings", "Settings", "select-main-tab", "Settings", 80),
        new("status", "Status", "select-main-tab", "Status", 90),
    ];

    private readonly Func<AgentBridgeTruth> createSnapshot;
    private readonly Action openMainWindow;
    private readonly Action closeMainWindow;
    private readonly Action openAcquisitionDiagnostics;
    private readonly Action<string> openProof;
    private readonly Func<string, bool> trySelectMainTab;
    private readonly Action captureInputState;
    private readonly Action stopRoute;
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
    public IReadOnlyList<AgentBridgeReviewSurfaceDescriptor> GetReviewSurfaces() => ReviewSurfaces;
    public AgentBridgeUiReviewFrame GetControlSurface() => reviewRegistry.Snapshot();
    public AgentBridgeUiControlReview ReviewControl(string controlId) => reviewRegistry.Review(controlId);
    public AgentBridgeUiControlInvocation InvokeControl(string controlId, long frameId) => reviewRegistry.Invoke(controlId, frameId);
}
