using MarketMafioso.AgentBridge;
using Franthropy.Dalamud.AgentBridge;

namespace MarketMafioso.Tests.AgentBridge;

public sealed class MarketMafiosoBridgeProviderTests
{
    [Fact]
    public void Provider_exposes_only_unlocked_product_review_surfaces_in_stable_order()
    {
        var provider = CreateProvider(marketAcquisitionUnlocked: true, CreateBindings());

        var surfaces = provider.GetReviewSurfaces();
        Assert.Contains(surfaces, surface => surface.Id == "squire" && surface.Target == "Squire");
        Assert.DoesNotContain(surfaces, surface =>
            surface.Id.Contains("retainer", StringComparison.OrdinalIgnoreCase) ||
            surface.Target.Contains("Retainer", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(surfaces, surface => surface.Id == "workshop-logistics.combined" && surface.Target == "Workshop Logistics/Combined");
        Assert.Contains(surfaces, surface => surface.Id == "workshop-logistics.materials" && surface.Target == "Workshop Logistics/Materials");
        Assert.Contains(surfaces, surface => surface.Id == "market-acquisition.inbox" && surface.Target == "Market Acquisition/Inbox");
        Assert.Contains(surfaces, surface => surface.Id == "market-acquisition.workbench" && surface.Target == "Market Acquisition/Workbench");
        Assert.Contains(surfaces, surface => surface.Id == "market-acquisition.route" && surface.Target == "Market Acquisition/Route");
        Assert.DoesNotContain(surfaces, surface => surface.Id == "overview");
        Assert.DoesNotContain(surfaces, surface => surface.Id == "inventory-reporter");
        Assert.Equal(surfaces.OrderBy(surface => surface.Order), surfaces);

        var lockedProvider = CreateProvider(marketAcquisitionUnlocked: false, CreateBindings());
        Assert.DoesNotContain(
            lockedProvider.GetReviewSurfaces(),
            surface => surface.Id.StartsWith("market-acquisition", StringComparison.Ordinal) ||
                       surface.Label.Contains("Market Acquisition", StringComparison.Ordinal));
    }

    [Fact]
    public void TruthSerialization_DoesNotAdvertiseRemovedRetainersSurface()
    {
        var receipt = AgentBridgeProofFactory.Create(CreateTruth(), revision: 1);

        var json = AgentBridgeProofFactory.Serialize(receipt);

        Assert.DoesNotContain("\"retainers\"", json, StringComparison.OrdinalIgnoreCase);
    }

    private static MarketMafiosoBridgeProvider CreateProvider(
        bool marketAcquisitionUnlocked,
        MarketMafiosoBridgeBindings bindings) => new(
            CreateTruth,
            () => { },
            () => { },
            () => { },
            _ => { },
            _ => true,
            () => { },
            () => { },
            () => marketAcquisitionUnlocked,
            bindings,
            new AgentBridgeUiReviewRegistry());

    private static MarketMafiosoBridgeBindings CreateBindings() => new(
        OpenCharacterUi: () => { },
        TryCloseCharacterUi: () => false,
        TryCloseBlockingSelectStringUi: () => false,
        TryCloseRetainerUi: () => false,
        TrySwitchCalibrationJobUi: _ => null,
        TrySwitchGearsetSlotUi: _ => null,
        TryOpenGearsetListUi: () => null!,
        TrySelectCalibrationGearsetUi: _ => null!,
        TryEquipSelectedGearsetUi: () => null!,
        CaptureCharacterUi: () => null!,
        CaptureRetainerUi: () => null!,
        BeginRetainerObservationUi: _ => null!,
        AdvanceRetainerObservationUi: () => null!,
        CancelRetainerObservationUi: () => null!,
        TryOpenRenderedRetainerUi: _ => null!,
        CaptureAdvisorStateUi: () => null!,
        CaptureInventoryStructSnapshotUi: () => null!,
        TryOpenArmouryBoardUi: () => false,
        TryCloseArmouryBoardUi: () => false,
        TryShowArmourySlotTooltipUi: _ => null!,
        TryShowBagSlotTooltipUi: _ => null!,
        TryOpenBagSlotContextUi: _ => null!,
        TryInvokeBagSlotContextActionUi: _ => null!,
        TryCloseBagSlotContextUi: () => false,
        CaptureTooltipMapDiagnosticUi: _ => string.Empty,
        CaptureInventoryContainerTableDiagnosticUi: () => string.Empty,
        CaptureInventoryBuddyOccupancyDiagnosticUi: () => string.Empty,
        CaptureInventoryWindowOccupancyDiagnosticUi: () => string.Empty,
        SetInventoryTabDiagnosticUi: _ => string.Empty,
        BeginArmouryDifferentialUi: () => null!,
        AdvanceArmouryDifferentialUi: () => null!,
        CancelArmouryDifferentialUi: () => null!,
        CaptureGatheringStatsUi: () => null!,
        BeginCharacterEquipmentScanUi: () => null!,
        AdvanceCharacterEquipmentScanUi: () => null!,
        CancelCharacterEquipmentScanUi: () => null!,
#if DEBUG
        TryOpenSyntheticAdvisorReview: () => false,
#endif
        GetUiAutomationCapabilities: () => null!);

    private static AgentBridgeTruth CreateTruth() => new()
    {
        SchemaVersion = 1,
        PluginInstanceId = "test",
        ProcessId = 1,
        PluginVersion = "test",
        CharacterName = string.Empty,
        CurrentWorld = string.Empty,
        HomeWorld = string.Empty,
        MainWindowOpen = false,
        MainWindowPinned = false,
        AcquisitionDiagnosticsOpen = false,
        WorkspaceStatus = string.Empty,
        WorkspaceBusy = false,
        ClaimedRequestId = null,
        PreparedPlanStatus = null,
        Route = new AgentBridgeRouteTruth
        {
            State = string.Empty,
            StatusMessage = string.Empty,
            VisibleStatus = string.Empty,
            IsActive = false,
            IsRunning = false,
            IsPaused = false,
            ActiveWorld = null,
            ActiveStopStatus = null,
            ActiveOperationId = null,
            ActiveOperationKind = null,
            ActiveOperationPhase = null,
            ActiveOperationDisposition = null,
            StopCount = 0,
            CompletedOrProbedStopCount = 0,
        },
    };
}
