using MarketMafioso.AgentBridge;
using Franthropy.Dalamud.AgentBridge;

namespace MarketMafioso.Tests.AgentBridge;

public sealed class MarketMafiosoBridgeProviderTests
{
    [Fact]
    public void Provider_advertises_capture_surfaces_without_requiring_client_product_knowledge()
    {
        var provider = CreateProvider(marketAcquisitionUnlocked: false);

        var surfaces = provider.GetCaptureSurfaces();

        Assert.Collection(
            surfaces.OrderBy(surface => surface.Order),
            surface =>
            {
                Assert.Equal("mmf.main-window", surface.Id);
                Assert.True(surface.IsDefault);
            },
            surface =>
            {
                Assert.Equal("mmf.main-window.compact", surface.Id);
                Assert.False(surface.IsDefault);
            });
    }

    [Fact]
    public void Provider_exposes_only_unlocked_product_review_surfaces_in_stable_order()
    {
        var provider = CreateProvider(marketAcquisitionUnlocked: true);

        var surfaces = provider.GetReviewSurfaces();
        Assert.DoesNotContain(surfaces, surface => surface.Id.Contains("squire", StringComparison.OrdinalIgnoreCase));
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

        var lockedProvider = CreateProvider(marketAcquisitionUnlocked: false);
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

    private static MarketMafiosoBridgeProvider CreateProvider(bool marketAcquisitionUnlocked) => new(
            CreateTruth,
            () => { },
            () => { },
            () => { },
            _ => { },
            _ => true,
            () => { },
            () => { },
            () => marketAcquisitionUnlocked,
            new AgentBridgeUiReviewRegistry());

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
