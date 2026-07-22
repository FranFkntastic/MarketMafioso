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
#if DEBUG
    bool TryOpenSyntheticAdvisorReview();
#endif
    IReadOnlyList<AgentBridgeReviewSurfaceDescriptor> GetReviewSurfaces();
    AgentBridgeUiReviewFrame GetControlSurface();
    AgentBridgeUiControlReview ReviewControl(string controlId);
    AgentBridgeUiControlInvocation InvokeControl(string controlId, long frameId);
}

public sealed record MarketMafiosoBridgeBindings(
    Action OpenCharacterUi,
    Func<bool> TryCloseCharacterUi,
    Func<bool> TryCloseBlockingSelectStringUi,
    Func<bool> TryCloseRetainerUi,
    Func<string, GearsetChangeCommand?> TrySwitchCalibrationJobUi,
    Func<string, GearsetChangeCommand?> TrySwitchGearsetSlotUi,
    Func<RenderedUiTextActionResult> TryOpenGearsetListUi,
    Func<string, RenderedUiTextActionResult> TrySelectCalibrationGearsetUi,
    Func<RenderedUiTextActionResult> TryEquipSelectedGearsetUi,
    Func<AgentBridgeRenderedUiSnapshot> CaptureCharacterUi,
    Func<AgentBridgeRenderedUiSnapshot> CaptureRetainerUi,
    Func<string, RenderedRetainerUiPreparationProgress> BeginRetainerObservationUi,
    Func<RenderedRetainerUiPreparationProgress> AdvanceRetainerObservationUi,
    Func<RenderedRetainerUiPreparationProgress> CancelRetainerObservationUi,
    Func<string, RenderedUiTextActionResult> TryOpenRenderedRetainerUi,
    Func<MinerBotanistAdvisorSessionState> CaptureAdvisorStateUi,
    Func<AgentBridgeInventoryStructSnapshot> CaptureInventoryStructSnapshotUi,
    Func<bool> TryOpenArmouryBoardUi,
    Func<bool> TryCloseArmouryBoardUi,
    Func<string, RenderedUiTextActionResult> TryShowArmourySlotTooltipUi,
    Func<string, RenderedUiTextActionResult> TryShowBagSlotTooltipUi,
    Func<string, RenderedUiTextActionResult> TryOpenBagSlotContextUi,
    Func<string, RenderedUiTextActionResult> TryInvokeBagSlotContextActionUi,
    Func<bool> TryCloseBagSlotContextUi,
    Func<string, string> CaptureTooltipMapDiagnosticUi,
    Func<string> CaptureInventoryContainerTableDiagnosticUi,
    Func<string> CaptureInventoryBuddyOccupancyDiagnosticUi,
    Func<string> CaptureInventoryWindowOccupancyDiagnosticUi,
    Func<int, string> SetInventoryTabDiagnosticUi,
    Func<RenderedArmouryDifferentialProgress> BeginArmouryDifferentialUi,
    Func<RenderedArmouryDifferentialProgress> AdvanceArmouryDifferentialUi,
    Func<RenderedArmouryDifferentialProgress> CancelArmouryDifferentialUi,
    Func<RenderedGatheringStatsObservation> CaptureGatheringStatsUi,
    Func<RenderedEquipmentScanProgress> BeginCharacterEquipmentScanUi,
    Func<RenderedEquipmentScanStepResult> AdvanceCharacterEquipmentScanUi,
    Func<RenderedEquipmentScanProgress> CancelCharacterEquipmentScanUi,
#if DEBUG
    Func<bool> TryOpenSyntheticAdvisorReview,
#endif
    Func<AgentBridgeUiAutomationCapabilities> GetUiAutomationCapabilities
);

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
    private readonly MarketMafiosoBridgeBindings bindings;
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
        MarketMafiosoBridgeBindings bindings,
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
        this.bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
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
    public void OpenCharacterUi() => bindings.OpenCharacterUi();
    public bool TryCloseCharacterUi() => bindings.TryCloseCharacterUi();
    public bool TryCloseBlockingSelectStringUi() => bindings.TryCloseBlockingSelectStringUi();
    public bool TryCloseRetainerUi() => bindings.TryCloseRetainerUi();
    public GearsetChangeCommand? TrySwitchCalibrationJobUi(string target) => bindings.TrySwitchCalibrationJobUi(target);
    public GearsetChangeCommand? TrySwitchGearsetSlotUi(string target) => bindings.TrySwitchGearsetSlotUi(target);
    public RenderedUiTextActionResult TryOpenGearsetListUi() => bindings.TryOpenGearsetListUi();
    public RenderedUiTextActionResult TrySelectCalibrationGearsetUi(string target) => bindings.TrySelectCalibrationGearsetUi(target);
    public RenderedUiTextActionResult TryEquipSelectedGearsetUi() => bindings.TryEquipSelectedGearsetUi();
    public AgentBridgeRenderedUiSnapshot CaptureCharacterUi() => bindings.CaptureCharacterUi();
    public AgentBridgeRenderedUiSnapshot CaptureRetainerUi() => bindings.CaptureRetainerUi();
    public RenderedRetainerUiPreparationProgress BeginRetainerObservationUi(string ownerHomeWorld) => bindings.BeginRetainerObservationUi(ownerHomeWorld);
    public RenderedRetainerUiPreparationProgress AdvanceRetainerObservationUi() => bindings.AdvanceRetainerObservationUi();
    public RenderedRetainerUiPreparationProgress CancelRetainerObservationUi() => bindings.CancelRetainerObservationUi();
    public RenderedUiTextActionResult TryOpenRenderedRetainerUi(string retainerName) => bindings.TryOpenRenderedRetainerUi(retainerName);
    public MinerBotanistAdvisorSessionState CaptureAdvisorStateUi() => bindings.CaptureAdvisorStateUi();
    public AgentBridgeInventoryStructSnapshot CaptureInventoryStructSnapshotUi() => bindings.CaptureInventoryStructSnapshotUi();
    public bool TryOpenArmouryBoardUi() => bindings.TryOpenArmouryBoardUi();
    public bool TryCloseArmouryBoardUi() => bindings.TryCloseArmouryBoardUi();
    public RenderedUiTextActionResult TryShowArmourySlotTooltipUi(string target) => bindings.TryShowArmourySlotTooltipUi(target);
    public RenderedUiTextActionResult TryShowBagSlotTooltipUi(string target) => bindings.TryShowBagSlotTooltipUi(target);
    public RenderedUiTextActionResult TryOpenBagSlotContextUi(string target) => bindings.TryOpenBagSlotContextUi(target);
    public RenderedUiTextActionResult TryInvokeBagSlotContextActionUi(string target) => bindings.TryInvokeBagSlotContextActionUi(target);
    public bool TryCloseBagSlotContextUi() => bindings.TryCloseBagSlotContextUi();
    public string CaptureTooltipMapDiagnosticUi(string addonName) => bindings.CaptureTooltipMapDiagnosticUi(addonName);
    public string CaptureInventoryContainerTableDiagnosticUi() => bindings.CaptureInventoryContainerTableDiagnosticUi();
    public string CaptureInventoryBuddyOccupancyDiagnosticUi() => bindings.CaptureInventoryBuddyOccupancyDiagnosticUi();
    public string CaptureInventoryWindowOccupancyDiagnosticUi() => bindings.CaptureInventoryWindowOccupancyDiagnosticUi();
    public string SetInventoryTabDiagnosticUi(int tab) => bindings.SetInventoryTabDiagnosticUi(tab);
    public RenderedArmouryDifferentialProgress BeginArmouryDifferentialUi() => bindings.BeginArmouryDifferentialUi();
    public RenderedArmouryDifferentialProgress AdvanceArmouryDifferentialUi() => bindings.AdvanceArmouryDifferentialUi();
    public RenderedArmouryDifferentialProgress CancelArmouryDifferentialUi() => bindings.CancelArmouryDifferentialUi();
    public RenderedGatheringStatsObservation CaptureGatheringStatsUi() => bindings.CaptureGatheringStatsUi();
    public RenderedCharacterEquipmentLayout CaptureCharacterEquipmentLayoutUi() =>
        RenderedCharacterEquipmentLayoutParser.Parse(bindings.CaptureCharacterUi());
    public RenderedItemDetailObservation CaptureItemDetailUi() =>
        RenderedItemDetailParser.Parse(bindings.CaptureCharacterUi());
    public RenderedEquipmentScanProgress BeginCharacterEquipmentScanUi() => bindings.BeginCharacterEquipmentScanUi();
    public RenderedEquipmentScanStepResult AdvanceCharacterEquipmentScanUi() => bindings.AdvanceCharacterEquipmentScanUi();
    public RenderedEquipmentScanProgress CancelCharacterEquipmentScanUi() => bindings.CancelCharacterEquipmentScanUi();
    public AgentBridgeUiAutomationCapabilities GetUiAutomationCapabilities() => bindings.GetUiAutomationCapabilities();
#if DEBUG
    public bool TryOpenSyntheticAdvisorReview() => bindings.TryOpenSyntheticAdvisorReview();
#endif
    public IReadOnlyList<AgentBridgeReviewSurfaceDescriptor> GetReviewSurfaces() => isMarketAcquisitionUnlocked()
        ? PublicReviewSurfaces.Concat(MarketAcquisitionReviewSurfaces).OrderBy(surface => surface.Order).ToArray()
        : PublicReviewSurfaces;
    public AgentBridgeUiReviewFrame GetControlSurface() => reviewRegistry.Snapshot();
    public AgentBridgeUiControlReview ReviewControl(string controlId) => reviewRegistry.Review(controlId);
    public AgentBridgeUiControlInvocation InvokeControl(string controlId, long frameId) => reviewRegistry.Invoke(controlId, frameId);
}
