using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Automation.Ui;
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
    GearsetChangeCommand? TrySwitchCalibrationJobUi(string target);
    GearsetChangeCommand? TrySwitchGearsetSlotUi(string target);
    RenderedUiTextActionResult TryOpenGearsetListUi();
    RenderedUiTextActionResult TryEquipCalibrationGearsetUi(string target);
    bool TryHoverCharacterNodeUi(string target);
    bool RestoreCharacterUiCursor();
    AgentBridgeRenderedUiSnapshot CaptureCharacterUi();
    AgentBridgeRenderedUiSnapshot CaptureRetainerUi();
    RenderedRetainerUiPreparationProgress BeginRetainerObservationUi();
    RenderedRetainerUiPreparationProgress AdvanceRetainerObservationUi();
    RenderedRetainerUiPreparationProgress CancelRetainerObservationUi();
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
    private readonly Action openCharacterUi;
    private readonly Func<bool> tryCloseCharacterUi;
    private readonly Func<bool> tryCloseBlockingSelectStringUi;
    private readonly Func<string, GearsetChangeCommand?> trySwitchCalibrationJobUi;
    private readonly Func<string, GearsetChangeCommand?> trySwitchGearsetSlotUi;
    private readonly Func<RenderedUiTextActionResult> tryOpenGearsetListUi;
    private readonly Func<string, RenderedUiTextActionResult> tryEquipCalibrationGearsetUi;
    private readonly Func<string, bool> tryHoverCharacterNodeUi;
    private readonly Func<bool> restoreCharacterUiCursor;
    private readonly Func<RenderedEquipmentScanProgress> beginCharacterEquipmentScanUi;
    private readonly Func<RenderedEquipmentScanStepResult> advanceCharacterEquipmentScanUi;
    private readonly Func<RenderedEquipmentScanProgress> cancelCharacterEquipmentScanUi;
    private readonly Func<AgentBridgeUiAutomationCapabilities> getUiAutomationCapabilities;
    private readonly Func<AgentBridgeRenderedUiSnapshot> captureCharacterUi;
    private readonly Func<AgentBridgeRenderedUiSnapshot> captureRetainerUi;
    private readonly Func<RenderedRetainerUiPreparationProgress> beginRetainerObservationUi;
    private readonly Func<RenderedRetainerUiPreparationProgress> advanceRetainerObservationUi;
    private readonly Func<RenderedRetainerUiPreparationProgress> cancelRetainerObservationUi;
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
        Func<bool> isMarketAcquisitionUnlocked,
        AgentBridgeUiReviewRegistry reviewRegistry,
        Action? openCharacterUi = null,
        Func<bool>? tryCloseCharacterUi = null,
        Func<AgentBridgeRenderedUiSnapshot>? captureCharacterUi = null,
        Func<bool>? tryCloseBlockingSelectStringUi = null,
        Func<string, GearsetChangeCommand?>? trySwitchCalibrationJobUi = null,
        Func<string, GearsetChangeCommand?>? trySwitchGearsetSlotUi = null,
        Func<RenderedGatheringStatsObservation>? captureGatheringStatsUi = null,
        Func<string, bool>? tryHoverCharacterNodeUi = null,
        Func<bool>? restoreCharacterUiCursor = null,
        Func<RenderedEquipmentScanProgress>? beginCharacterEquipmentScanUi = null,
        Func<RenderedEquipmentScanStepResult>? advanceCharacterEquipmentScanUi = null,
        Func<RenderedEquipmentScanProgress>? cancelCharacterEquipmentScanUi = null,
        Func<AgentBridgeUiAutomationCapabilities>? getUiAutomationCapabilities = null,
        Func<bool>? tryOpenSyntheticAdvisorReview = null,
        Func<AgentBridgeRenderedUiSnapshot>? captureRetainerUi = null,
        Func<RenderedRetainerUiPreparationProgress>? beginRetainerObservationUi = null,
        Func<RenderedRetainerUiPreparationProgress>? advanceRetainerObservationUi = null,
        Func<RenderedRetainerUiPreparationProgress>? cancelRetainerObservationUi = null,
        Func<RenderedUiTextActionResult>? tryOpenGearsetListUi = null,
        Func<string, RenderedUiTextActionResult>? tryEquipCalibrationGearsetUi = null)
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
        this.openCharacterUi = openCharacterUi ?? (() => { });
        this.tryCloseCharacterUi = tryCloseCharacterUi ?? (() => false);
        this.captureCharacterUi = captureCharacterUi ?? (() => new(DateTimeOffset.UtcNow, []));
        this.captureRetainerUi = captureRetainerUi ?? (() => new(DateTimeOffset.UtcNow, []));
        this.beginRetainerObservationUi = beginRetainerObservationUi ?? (() => new(RenderedRetainerUiPreparationStatus.Failed, 0, "Retainer UI preparation is unavailable."));
        this.advanceRetainerObservationUi = advanceRetainerObservationUi ?? this.beginRetainerObservationUi;
        this.cancelRetainerObservationUi = cancelRetainerObservationUi ?? (() => new(RenderedRetainerUiPreparationStatus.Cancelled, 0, "Retainer UI preparation is unavailable."));
        this.tryCloseBlockingSelectStringUi = tryCloseBlockingSelectStringUi ?? (() => false);
        this.trySwitchCalibrationJobUi = trySwitchCalibrationJobUi ?? (_ => null);
        this.trySwitchGearsetSlotUi = trySwitchGearsetSlotUi ?? (_ => null);
        this.tryOpenGearsetListUi = tryOpenGearsetListUi ?? (() => new(false, "Unavailable", "Rendered gearset-list automation is unavailable.", "Character", null));
        this.tryEquipCalibrationGearsetUi = tryEquipCalibrationGearsetUi ?? (_ => new(false, "Unavailable", "Rendered gearset equipping is unavailable.", "GearSetList", null));
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
    public GearsetChangeCommand? TrySwitchCalibrationJobUi(string target) => trySwitchCalibrationJobUi(target);
    public GearsetChangeCommand? TrySwitchGearsetSlotUi(string target) => trySwitchGearsetSlotUi(target);
    public RenderedUiTextActionResult TryOpenGearsetListUi() => tryOpenGearsetListUi();
    public RenderedUiTextActionResult TryEquipCalibrationGearsetUi(string target) => tryEquipCalibrationGearsetUi(target);
    public bool TryHoverCharacterNodeUi(string target) => tryHoverCharacterNodeUi(target);
    public bool RestoreCharacterUiCursor() => restoreCharacterUiCursor();
    public AgentBridgeRenderedUiSnapshot CaptureCharacterUi() => captureCharacterUi();
    public AgentBridgeRenderedUiSnapshot CaptureRetainerUi() => captureRetainerUi();
    public RenderedRetainerUiPreparationProgress BeginRetainerObservationUi() => beginRetainerObservationUi();
    public RenderedRetainerUiPreparationProgress AdvanceRetainerObservationUi() => advanceRetainerObservationUi();
    public RenderedRetainerUiPreparationProgress CancelRetainerObservationUi() => cancelRetainerObservationUi();
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
    public IReadOnlyList<AgentBridgeReviewSurfaceDescriptor> GetReviewSurfaces() => isMarketAcquisitionUnlocked()
        ? PublicReviewSurfaces.Concat(MarketAcquisitionReviewSurfaces).OrderBy(surface => surface.Order).ToArray()
        : PublicReviewSurfaces;
    public AgentBridgeUiReviewFrame GetControlSurface() => reviewRegistry.Snapshot();
    public AgentBridgeUiControlReview ReviewControl(string controlId) => reviewRegistry.Review(controlId);
    public AgentBridgeUiControlInvocation InvokeControl(string controlId, long frameId) => reviewRegistry.Invoke(controlId, frameId);
}
