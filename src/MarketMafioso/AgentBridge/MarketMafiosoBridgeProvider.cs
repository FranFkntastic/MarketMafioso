using System;
using System.Collections.Generic;
using Franthropy.Dalamud.AgentBridge;
using MarketMafioso.Squire.Observation;

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
    void OpenCharacterUi();
    bool TryCloseCharacterUi();
    bool TryCloseBlockingSelectStringUi();
    bool TrySwitchCalibrationJobUi(string target);
    bool TryHoverCharacterNodeUi(string target);
    bool RestoreCharacterUiCursor();
    AgentBridgeRenderedUiSnapshot CaptureCharacterUi();
    RenderedGatheringStatsObservation CaptureGatheringStatsUi();
    RenderedCharacterEquipmentLayout CaptureCharacterEquipmentLayoutUi();
    RenderedItemDetailObservation CaptureItemDetailUi();
    RenderedEquipmentScanProgress BeginCharacterEquipmentScanUi();
    RenderedEquipmentScanStepResult AdvanceCharacterEquipmentScanUi();
    RenderedEquipmentScanProgress CancelCharacterEquipmentScanUi();
    AgentBridgeUiAutomationCapabilities GetUiAutomationCapabilities();
    bool TryOpenSyntheticAdvisorReview();
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
        new("workshop-logistics.combined", "Workshop Logistics - Queue and Materials", "select-main-tab", "Workshop Logistics/Combined", 41),
        new("workshop-logistics.queue", "Workshop Logistics - Queue", "select-main-tab", "Workshop Logistics/Queue", 42),
        new("workshop-logistics.materials", "Workshop Logistics - Materials", "select-main-tab", "Workshop Logistics/Materials", 43),
        new("workshop-logistics.assembly", "Workshop Logistics - Assembly", "select-main-tab", "Workshop Logistics/Assembly", 44),
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
    private readonly Action openCharacterUi;
    private readonly Func<bool> tryCloseCharacterUi;
    private readonly Func<bool> tryCloseBlockingSelectStringUi;
    private readonly Func<string, bool> trySwitchCalibrationJobUi;
    private readonly Func<string, bool> tryHoverCharacterNodeUi;
    private readonly Func<bool> restoreCharacterUiCursor;
    private readonly Func<RenderedEquipmentScanProgress> beginCharacterEquipmentScanUi;
    private readonly Func<RenderedEquipmentScanStepResult> advanceCharacterEquipmentScanUi;
    private readonly Func<RenderedEquipmentScanProgress> cancelCharacterEquipmentScanUi;
    private readonly Func<AgentBridgeUiAutomationCapabilities> getUiAutomationCapabilities;
    private readonly Func<AgentBridgeRenderedUiSnapshot> captureCharacterUi;
    private readonly Func<RenderedGatheringStatsObservation> captureGatheringStatsUi;
    private readonly Func<bool> tryOpenSyntheticAdvisorReview;
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
        AgentBridgeUiReviewRegistry reviewRegistry,
        Action? openCharacterUi = null,
        Func<bool>? tryCloseCharacterUi = null,
        Func<AgentBridgeRenderedUiSnapshot>? captureCharacterUi = null,
        Func<bool>? tryCloseBlockingSelectStringUi = null,
        Func<string, bool>? trySwitchCalibrationJobUi = null,
        Func<RenderedGatheringStatsObservation>? captureGatheringStatsUi = null,
        Func<string, bool>? tryHoverCharacterNodeUi = null,
        Func<bool>? restoreCharacterUiCursor = null,
        Func<RenderedEquipmentScanProgress>? beginCharacterEquipmentScanUi = null,
        Func<RenderedEquipmentScanStepResult>? advanceCharacterEquipmentScanUi = null,
        Func<RenderedEquipmentScanProgress>? cancelCharacterEquipmentScanUi = null,
        Func<AgentBridgeUiAutomationCapabilities>? getUiAutomationCapabilities = null,
        Func<bool>? tryOpenSyntheticAdvisorReview = null)
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
        this.openCharacterUi = openCharacterUi ?? (() => { });
        this.tryCloseCharacterUi = tryCloseCharacterUi ?? (() => false);
        this.captureCharacterUi = captureCharacterUi ?? (() => new(DateTimeOffset.UtcNow, []));
        this.tryCloseBlockingSelectStringUi = tryCloseBlockingSelectStringUi ?? (() => false);
        this.trySwitchCalibrationJobUi = trySwitchCalibrationJobUi ?? (_ => false);
        this.captureGatheringStatsUi = captureGatheringStatsUi ?? (() => new(Guid.NewGuid(), DateTimeOffset.UtcNow, RenderedCharacterObservationStatus.Unavailable, null, null, null, null, null, [], "Rendered gathering observation is unavailable."));
        this.tryHoverCharacterNodeUi = tryHoverCharacterNodeUi ?? (_ => false);
        this.restoreCharacterUiCursor = restoreCharacterUiCursor ?? (() => false);
        this.beginCharacterEquipmentScanUi = beginCharacterEquipmentScanUi ?? (() => new(RenderedEquipmentScanStatus.Failed, 0, 0, null, [], "Rendered equipment scanning is unavailable."));
        this.advanceCharacterEquipmentScanUi = advanceCharacterEquipmentScanUi ?? (() => new(false, this.beginCharacterEquipmentScanUi(), "Rendered equipment scanning is unavailable."));
        this.cancelCharacterEquipmentScanUi = cancelCharacterEquipmentScanUi ?? (() => new(RenderedEquipmentScanStatus.Cancelled, 0, 0, null, [], "Rendered equipment scanning is unavailable."));
        this.getUiAutomationCapabilities = getUiAutomationCapabilities ?? (() => new(
            "unavailable", false, false, false, true, true, true,
            "Rendered UI automation capabilities were not registered."));
        this.tryOpenSyntheticAdvisorReview = tryOpenSyntheticAdvisorReview ?? (() => false);
    }

    public AgentBridgeTruth CreateSnapshot() => createSnapshot();
    public void OpenMainWindow() => openMainWindow();
    public void CloseMainWindow() => closeMainWindow();
    public void OpenAcquisitionDiagnostics() => openAcquisitionDiagnostics();
    public void OpenProof(string proofId) => openProof(proofId);
    public bool TrySelectMainTab(string tabName) => trySelectMainTab(tabName);
    public void CaptureInputState() => captureInputState();
    public void StopRoute() => stopRoute();
    public void OpenCharacterUi() => openCharacterUi();
    public bool TryCloseCharacterUi() => tryCloseCharacterUi();
    public bool TryCloseBlockingSelectStringUi() => tryCloseBlockingSelectStringUi();
    public bool TrySwitchCalibrationJobUi(string target) => trySwitchCalibrationJobUi(target);
    public bool TryHoverCharacterNodeUi(string target) => tryHoverCharacterNodeUi(target);
    public bool RestoreCharacterUiCursor() => restoreCharacterUiCursor();
    public AgentBridgeRenderedUiSnapshot CaptureCharacterUi() => captureCharacterUi();
    public RenderedGatheringStatsObservation CaptureGatheringStatsUi() => captureGatheringStatsUi();
    public RenderedCharacterEquipmentLayout CaptureCharacterEquipmentLayoutUi() =>
        RenderedCharacterEquipmentLayoutParser.Parse(captureCharacterUi());
    public RenderedItemDetailObservation CaptureItemDetailUi() =>
        RenderedItemDetailParser.Parse(captureCharacterUi());
    public RenderedEquipmentScanProgress BeginCharacterEquipmentScanUi() => beginCharacterEquipmentScanUi();
    public RenderedEquipmentScanStepResult AdvanceCharacterEquipmentScanUi() => advanceCharacterEquipmentScanUi();
    public RenderedEquipmentScanProgress CancelCharacterEquipmentScanUi() => cancelCharacterEquipmentScanUi();
    public AgentBridgeUiAutomationCapabilities GetUiAutomationCapabilities() => getUiAutomationCapabilities();
    public bool TryOpenSyntheticAdvisorReview() => tryOpenSyntheticAdvisorReview();
    public IReadOnlyList<AgentBridgeReviewSurfaceDescriptor> GetReviewSurfaces() => ReviewSurfaces;
    public AgentBridgeUiReviewFrame GetControlSurface() => reviewRegistry.Snapshot();
    public AgentBridgeUiControlReview ReviewControl(string controlId) => reviewRegistry.Review(controlId);
    public AgentBridgeUiControlInvocation InvokeControl(string controlId, long frameId) => reviewRegistry.Invoke(controlId, frameId);
}
