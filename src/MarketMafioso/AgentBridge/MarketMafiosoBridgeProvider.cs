using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Automation.Ui;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter.Utility;

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
    bool TryCloseRetainerUi();
    GearsetChangeCommand? TrySwitchCalibrationJobUi(string target);
    GearsetChangeCommand? TrySwitchGearsetSlotUi(string target);
    RenderedUiTextActionResult TryOpenGearsetListUi();
    RenderedUiTextActionResult TrySelectCalibrationGearsetUi(string target);
    RenderedUiTextActionResult TryEquipSelectedGearsetUi();
    AgentBridgeRenderedUiSnapshot CaptureCharacterUi();
    AgentBridgeRenderedUiSnapshot CaptureRetainerUi();
    RenderedRetainerUiPreparationProgress BeginRetainerObservationUi(string ownerHomeWorld);
    RenderedRetainerUiPreparationProgress AdvanceRetainerObservationUi();
    RenderedRetainerUiPreparationProgress CancelRetainerObservationUi();
    RenderedUiTextActionResult TryOpenRenderedRetainerUi(string retainerName);
    MinerBotanistAdvisorSessionState CaptureAdvisorStateUi();
    AgentBridgeInventoryStructSnapshot CaptureInventoryStructSnapshotUi();
    bool TryOpenArmouryBoardUi();
    bool TryCloseArmouryBoardUi();
    RenderedUiTextActionResult TryShowArmourySlotTooltipUi(string target);
    RenderedUiTextActionResult TryShowBagSlotTooltipUi(string target);
    RenderedUiTextActionResult TryOpenBagSlotContextUi(string target);
    RenderedUiTextActionResult TryInvokeBagSlotContextActionUi(string target);
    bool TryCloseBagSlotContextUi();
    string CaptureTooltipMapDiagnosticUi(string addonName);
    string CaptureInventoryContainerTableDiagnosticUi();
    string CaptureInventoryBuddyOccupancyDiagnosticUi();
    string CaptureInventoryWindowOccupancyDiagnosticUi();
    string SetInventoryTabDiagnosticUi(int tab);
    RenderedArmouryDifferentialProgress BeginArmouryDifferentialUi();
    RenderedArmouryDifferentialProgress AdvanceArmouryDifferentialUi();
    RenderedArmouryDifferentialProgress CancelArmouryDifferentialUi();
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
        new("retainers.overview", "Retainers - Overview", "select-main-tab", "Retainers/Overview", 51),
        new("retainers.stock", "Retainers - Browse stock", "select-main-tab", "Retainers/Browse stock", 52),
        new("retainers.listings", "Retainers - Browse listings", "select-main-tab", "Retainers/Browse listings", 53),
        new("retainers.deposit", "Retainers - Quick deposit", "select-main-tab", "Retainers/Quick deposit", 54),
        new("retainers.plan", "Retainers - Withdrawal plan", "select-main-tab", "Retainers/Withdrawal plan", 55),
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
    private readonly Func<bool> tryCloseRetainerUi;
    private readonly Func<string, GearsetChangeCommand?> trySwitchCalibrationJobUi;
    private readonly Func<string, GearsetChangeCommand?> trySwitchGearsetSlotUi;
    private readonly Func<RenderedUiTextActionResult> tryOpenGearsetListUi;
    private readonly Func<string, RenderedUiTextActionResult> trySelectCalibrationGearsetUi;
    private readonly Func<RenderedUiTextActionResult> tryEquipSelectedGearsetUi;
    private readonly Func<RenderedEquipmentScanProgress> beginCharacterEquipmentScanUi;
    private readonly Func<RenderedEquipmentScanStepResult> advanceCharacterEquipmentScanUi;
    private readonly Func<RenderedEquipmentScanProgress> cancelCharacterEquipmentScanUi;
    private readonly Func<AgentBridgeUiAutomationCapabilities> getUiAutomationCapabilities;
    private readonly Func<AgentBridgeRenderedUiSnapshot> captureCharacterUi;
    private readonly Func<AgentBridgeRenderedUiSnapshot> captureRetainerUi;
    private readonly Func<string, RenderedRetainerUiPreparationProgress> beginRetainerObservationUi;
    private readonly Func<RenderedRetainerUiPreparationProgress> advanceRetainerObservationUi;
    private readonly Func<RenderedRetainerUiPreparationProgress> cancelRetainerObservationUi;
    private readonly Func<string, RenderedUiTextActionResult> tryOpenRenderedRetainerUi;
    private readonly Func<MinerBotanistAdvisorSessionState> captureAdvisorStateUi;
    private readonly Func<AgentBridgeInventoryStructSnapshot>? captureInventoryStructSnapshotUi;
    private readonly Func<bool> tryOpenArmouryBoardUi;
    private readonly Func<bool> tryCloseArmouryBoardUi;
    private readonly Func<string, RenderedUiTextActionResult> tryShowArmourySlotTooltipUi;
    private readonly Func<string, RenderedUiTextActionResult> tryShowBagSlotTooltipUi;
    private readonly Func<string, RenderedUiTextActionResult> tryOpenBagSlotContextUi;
    private readonly Func<string, RenderedUiTextActionResult> tryInvokeBagSlotContextActionUi;
    private readonly Func<bool> tryCloseBagSlotContextUi;
    private readonly Func<string, string> captureTooltipMapDiagnosticUi;
    private readonly Func<string> captureInventoryContainerTableDiagnosticUi;
    private readonly Func<string> captureInventoryBuddyOccupancyDiagnosticUi;
    private readonly Func<string> captureInventoryWindowOccupancyDiagnosticUi;
    private readonly Func<int, string> setInventoryTabDiagnosticUi;
    private readonly Func<RenderedArmouryDifferentialProgress> beginArmouryDifferentialUi;
    private readonly Func<RenderedArmouryDifferentialProgress> advanceArmouryDifferentialUi;
    private readonly Func<RenderedArmouryDifferentialProgress> cancelArmouryDifferentialUi;
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
        Func<RenderedEquipmentScanProgress>? beginCharacterEquipmentScanUi = null,
        Func<RenderedEquipmentScanStepResult>? advanceCharacterEquipmentScanUi = null,
        Func<RenderedEquipmentScanProgress>? cancelCharacterEquipmentScanUi = null,
        Func<AgentBridgeUiAutomationCapabilities>? getUiAutomationCapabilities = null,
        Func<bool>? tryOpenSyntheticAdvisorReview = null,
        Func<AgentBridgeRenderedUiSnapshot>? captureRetainerUi = null,
        Func<string, RenderedRetainerUiPreparationProgress>? beginRetainerObservationUi = null,
        Func<RenderedRetainerUiPreparationProgress>? advanceRetainerObservationUi = null,
        Func<RenderedRetainerUiPreparationProgress>? cancelRetainerObservationUi = null,
        Func<string, RenderedUiTextActionResult>? tryOpenRenderedRetainerUi = null,
        Func<RenderedUiTextActionResult>? tryOpenGearsetListUi = null,
        Func<string, RenderedUiTextActionResult>? trySelectCalibrationGearsetUi = null,
        Func<RenderedUiTextActionResult>? tryEquipSelectedGearsetUi = null,
        Func<bool>? tryCloseRetainerUi = null,
        Func<MinerBotanistAdvisorSessionState>? captureAdvisorStateUi = null,
        Func<AgentBridgeInventoryStructSnapshot>? captureInventoryStructSnapshotUi = null,
        Func<bool>? tryOpenArmouryBoardUi = null,
        Func<bool>? tryCloseArmouryBoardUi = null,
        Func<string, RenderedUiTextActionResult>? tryShowArmourySlotTooltipUi = null,
        Func<string, RenderedUiTextActionResult>? tryShowBagSlotTooltipUi = null,
        Func<string, RenderedUiTextActionResult>? tryOpenBagSlotContextUi = null,
        Func<string, RenderedUiTextActionResult>? tryInvokeBagSlotContextActionUi = null,
        Func<bool>? tryCloseBagSlotContextUi = null,
        Func<string, string>? captureTooltipMapDiagnosticUi = null,
        Func<string>? captureInventoryContainerTableDiagnosticUi = null,
        Func<string>? captureInventoryBuddyOccupancyDiagnosticUi = null,
        Func<string>? captureInventoryWindowOccupancyDiagnosticUi = null,
        Func<int, string>? setInventoryTabDiagnosticUi = null,
        Func<RenderedArmouryDifferentialProgress>? beginArmouryDifferentialUi = null,
        Func<RenderedArmouryDifferentialProgress>? advanceArmouryDifferentialUi = null,
        Func<RenderedArmouryDifferentialProgress>? cancelArmouryDifferentialUi = null)
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
        this.beginRetainerObservationUi = beginRetainerObservationUi ?? (_ => new(RenderedRetainerUiPreparationStatus.Failed, 0, "Retainer UI preparation is unavailable."));
        this.advanceRetainerObservationUi = advanceRetainerObservationUi ?? (() => this.beginRetainerObservationUi(string.Empty));
        this.cancelRetainerObservationUi = cancelRetainerObservationUi ?? (() => new(RenderedRetainerUiPreparationStatus.Cancelled, 0, "Retainer UI preparation is unavailable."));
        this.tryOpenRenderedRetainerUi = tryOpenRenderedRetainerUi ?? (_ => new(false, "Unavailable", "Rendered retainer selection is unavailable.", "RetainerList", null));
        this.captureAdvisorStateUi = captureAdvisorStateUi ?? (() => new(
            MinerBotanistAdvisorSessionStage.Failed,
            "Advisor session state is unavailable.",
            "Coverage unavailable.",
            0,
            null,
            MinerBotanistUtilityContextKind.OrdinaryResourceBenchmark,
            null,
            false,
            DateTimeOffset.UtcNow));
        this.tryCloseBlockingSelectStringUi = tryCloseBlockingSelectStringUi ?? (() => false);
        this.tryCloseRetainerUi = tryCloseRetainerUi ?? (() => false);
        this.trySwitchCalibrationJobUi = trySwitchCalibrationJobUi ?? (_ => null);
        this.trySwitchGearsetSlotUi = trySwitchGearsetSlotUi ?? (_ => null);
        this.tryOpenGearsetListUi = tryOpenGearsetListUi ?? (() => new(false, "Unavailable", "Rendered gearset-list automation is unavailable.", "Character", null));
        this.trySelectCalibrationGearsetUi = trySelectCalibrationGearsetUi ?? (_ => new(false, "Unavailable", "Rendered gearset selection is unavailable.", "GearSetList", null));
        this.tryEquipSelectedGearsetUi = tryEquipSelectedGearsetUi ?? (() => new(false, "Unavailable", "Rendered gearset equipping is unavailable.", "GearSetList", null));
        this.captureGatheringStatsUi = captureGatheringStatsUi ?? (() => new(Guid.NewGuid(), DateTimeOffset.UtcNow, RenderedCharacterObservationStatus.Unavailable, null, null, null, null, null, [], "Rendered gathering observation is unavailable."));
        this.beginCharacterEquipmentScanUi = beginCharacterEquipmentScanUi ?? (() => new(RenderedEquipmentScanStatus.Failed, 0, 0, null, [], "Rendered equipment scanning is unavailable."));
        this.advanceCharacterEquipmentScanUi = advanceCharacterEquipmentScanUi ?? (() => new(false, this.beginCharacterEquipmentScanUi(), "Rendered equipment scanning is unavailable."));
        this.cancelCharacterEquipmentScanUi = cancelCharacterEquipmentScanUi ?? (() => new(RenderedEquipmentScanStatus.Cancelled, 0, 0, null, [], "Rendered equipment scanning is unavailable."));
        this.getUiAutomationCapabilities = getUiAutomationCapabilities ?? (() => new(
            "unavailable", false, false, false, true, true, true,
            "Rendered UI automation capabilities were not registered."));
        this.tryOpenSyntheticAdvisorReview = tryOpenSyntheticAdvisorReview ?? (() => false);
        this.captureInventoryStructSnapshotUi = captureInventoryStructSnapshotUi;
        this.tryOpenArmouryBoardUi = tryOpenArmouryBoardUi ?? (() => false);
        this.tryCloseArmouryBoardUi = tryCloseArmouryBoardUi ?? (() => false);
        this.tryShowArmourySlotTooltipUi = tryShowArmourySlotTooltipUi ?? (_ => new(false, "Unavailable", "Rendered armoury automation is unavailable.", "ArmouryBoard", null));
        this.tryShowBagSlotTooltipUi = tryShowBagSlotTooltipUi ?? (_ => new(false, "Unavailable", "Rendered bag automation is unavailable.", "Inventory", null));
        this.tryOpenBagSlotContextUi = tryOpenBagSlotContextUi ?? (_ => new(false, "Unavailable", "Inventory context automation is unavailable.", "ContextMenu", null));
        this.tryInvokeBagSlotContextActionUi = tryInvokeBagSlotContextActionUi ?? tryOpenBagSlotContextUi ?? (_ => new(false, "Unavailable", "Inventory context automation is unavailable.", "ContextMenu", null));
        this.tryCloseBagSlotContextUi = tryCloseBagSlotContextUi ?? (() => false);
        this.captureTooltipMapDiagnosticUi = captureTooltipMapDiagnosticUi ?? (_ => "Tooltip map diagnostics are unavailable.");
        this.captureInventoryContainerTableDiagnosticUi = captureInventoryContainerTableDiagnosticUi ?? (() => "Inventory container table diagnostics are unavailable.");
        this.captureInventoryBuddyOccupancyDiagnosticUi = captureInventoryBuddyOccupancyDiagnosticUi ?? (() => "Inventory buddy occupancy diagnostics are unavailable.");
        this.captureInventoryWindowOccupancyDiagnosticUi = captureInventoryWindowOccupancyDiagnosticUi ?? (() => "Inventory window occupancy diagnostics are unavailable.");
        this.setInventoryTabDiagnosticUi = setInventoryTabDiagnosticUi ?? (_ => "Inventory tab automation is unavailable.");
        this.beginArmouryDifferentialUi = beginArmouryDifferentialUi ?? (() => new(RenderedArmouryDifferentialStatus.Failed, 0, 0, string.Empty, 0, [], [], [], "The armoury differential proof is unavailable."));
        this.advanceArmouryDifferentialUi = advanceArmouryDifferentialUi ?? this.beginArmouryDifferentialUi;
        this.cancelArmouryDifferentialUi = cancelArmouryDifferentialUi ?? (() => new(RenderedArmouryDifferentialStatus.Cancelled, 0, 0, string.Empty, 0, [], [], [], "The armoury differential proof is unavailable."));
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
    public bool TryCloseRetainerUi() => tryCloseRetainerUi();
    public GearsetChangeCommand? TrySwitchCalibrationJobUi(string target) => trySwitchCalibrationJobUi(target);
    public GearsetChangeCommand? TrySwitchGearsetSlotUi(string target) => trySwitchGearsetSlotUi(target);
    public RenderedUiTextActionResult TryOpenGearsetListUi() => tryOpenGearsetListUi();
    public RenderedUiTextActionResult TrySelectCalibrationGearsetUi(string target) => trySelectCalibrationGearsetUi(target);
    public RenderedUiTextActionResult TryEquipSelectedGearsetUi() => tryEquipSelectedGearsetUi();
    public AgentBridgeRenderedUiSnapshot CaptureCharacterUi() => captureCharacterUi();
    public AgentBridgeRenderedUiSnapshot CaptureRetainerUi() => captureRetainerUi();
    public RenderedRetainerUiPreparationProgress BeginRetainerObservationUi(string ownerHomeWorld) => beginRetainerObservationUi(ownerHomeWorld);
    public RenderedRetainerUiPreparationProgress AdvanceRetainerObservationUi() => advanceRetainerObservationUi();
    public RenderedRetainerUiPreparationProgress CancelRetainerObservationUi() => cancelRetainerObservationUi();
    public RenderedUiTextActionResult TryOpenRenderedRetainerUi(string retainerName) => tryOpenRenderedRetainerUi(retainerName);
    public MinerBotanistAdvisorSessionState CaptureAdvisorStateUi() => captureAdvisorStateUi();
    public AgentBridgeInventoryStructSnapshot CaptureInventoryStructSnapshotUi() =>
        captureInventoryStructSnapshotUi?.Invoke() ?? new(
            "Unavailable",
            0,
            DateTimeOffset.UtcNow,
            [],
            [],
            "The inventory struct snapshot source is not registered.");
    public bool TryOpenArmouryBoardUi() => tryOpenArmouryBoardUi();
    public bool TryCloseArmouryBoardUi() => tryCloseArmouryBoardUi();
    public RenderedUiTextActionResult TryShowArmourySlotTooltipUi(string target) => tryShowArmourySlotTooltipUi(target);
    public RenderedUiTextActionResult TryShowBagSlotTooltipUi(string target) => tryShowBagSlotTooltipUi(target);
    public RenderedUiTextActionResult TryOpenBagSlotContextUi(string target) => tryOpenBagSlotContextUi(target);
    public RenderedUiTextActionResult TryInvokeBagSlotContextActionUi(string target) => tryInvokeBagSlotContextActionUi(target);
    public bool TryCloseBagSlotContextUi() => tryCloseBagSlotContextUi();
    public string CaptureTooltipMapDiagnosticUi(string addonName) => captureTooltipMapDiagnosticUi(addonName);
    public string CaptureInventoryContainerTableDiagnosticUi() => captureInventoryContainerTableDiagnosticUi();
    public string CaptureInventoryBuddyOccupancyDiagnosticUi() => captureInventoryBuddyOccupancyDiagnosticUi();
    public string CaptureInventoryWindowOccupancyDiagnosticUi() => captureInventoryWindowOccupancyDiagnosticUi();
    public string SetInventoryTabDiagnosticUi(int tab) => setInventoryTabDiagnosticUi(tab);
    public RenderedArmouryDifferentialProgress BeginArmouryDifferentialUi() => beginArmouryDifferentialUi();
    public RenderedArmouryDifferentialProgress AdvanceArmouryDifferentialUi() => advanceArmouryDifferentialUi();
    public RenderedArmouryDifferentialProgress CancelArmouryDifferentialUi() => cancelArmouryDifferentialUi();
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
