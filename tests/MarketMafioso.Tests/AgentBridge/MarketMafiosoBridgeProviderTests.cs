using MarketMafioso.AgentBridge;

namespace MarketMafioso.Tests.AgentBridge;

public sealed class MarketMafiosoBridgeProviderTests
{
    [Fact]
    public void Provider_DelegatesOnlyProductSpecificStateAndActions()
    {
        var opened = false;
        var selected = string.Empty;
        var provider = new MarketMafiosoBridgeProvider(
            CreateTruth,
            () => opened = true,
            () => { },
            _ => { },
            tab => { selected = tab; return tab == "Squire"; },
            () => { },
            () => { });

        Assert.Equal("test", provider.CreateSnapshot().PluginInstanceId);
        provider.OpenMainWindow();
        Assert.True(opened);
        Assert.True(provider.TrySelectMainTab("Squire"));
        Assert.Equal("Squire", selected);
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

