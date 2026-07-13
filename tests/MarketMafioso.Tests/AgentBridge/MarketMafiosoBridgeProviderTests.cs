using MarketMafioso.AgentBridge;
using Franthropy.Dalamud.AgentBridge;

namespace MarketMafioso.Tests.AgentBridge;

public sealed class MarketMafiosoBridgeProviderTests
{
    [Fact]
    public void Provider_DelegatesOnlyProductSpecificStateAndActions()
    {
        var opened = false;
        var closed = false;
        var selected = string.Empty;
        var provider = new MarketMafiosoBridgeProvider(
            CreateTruth,
            () => opened = true,
            () => closed = true,
            () => { },
            _ => { },
            tab => { selected = tab; return tab == "Squire"; },
            () => { },
            () => { },
            new AgentBridgeUiReviewRegistry());

        Assert.Equal("test", provider.CreateSnapshot().PluginInstanceId);
        provider.OpenMainWindow();
        Assert.True(opened);
        provider.CloseMainWindow();
        Assert.True(closed);
        Assert.True(provider.TrySelectMainTab("Squire"));
        Assert.Equal("Squire", selected);
        var surfaces = provider.GetReviewSurfaces();
        Assert.Contains(surfaces, surface => surface.Id == "squire" && surface.Target == "Squire");
        Assert.Equal(surfaces.OrderBy(surface => surface.Order), surfaces);

        var registry = new AgentBridgeUiReviewRegistry();
        registry.BeginFrame();
        registry.Register("squire.probe", "Probe", AgentBridgeUiControlKind.Button, default, new(100, 20), true, false, "Ready", () => { });
        var registeredProvider = new MarketMafiosoBridgeProvider(
            CreateTruth, () => { }, () => { }, () => { }, _ => { }, _ => true, () => { }, () => { }, registry);
        registry.EndFrame();
        var review = registeredProvider.ReviewControl("squire.probe");
        Assert.Equal("squire.probe", Assert.IsType<AgentBridgeUiControl>(review.Control).Id);
        Assert.Null(registeredProvider.ReviewControl("missing").Control);
    }

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
