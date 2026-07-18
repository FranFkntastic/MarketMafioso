using MarketMafioso.AgentBridge;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Automation.Ui;

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
            () => true,
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
        Assert.Contains(surfaces, surface => surface.Id == "retainers.deposit" && surface.Target == "Retainers/Quick deposit");
        Assert.Contains(surfaces, surface => surface.Id == "retainers.plan" && surface.Target == "Retainers/Withdrawal plan");
        Assert.Contains(surfaces, surface => surface.Id == "workshop-logistics.combined" && surface.Target == "Workshop Logistics/Combined");
        Assert.Contains(surfaces, surface => surface.Id == "workshop-logistics.materials" && surface.Target == "Workshop Logistics/Materials");
        Assert.Contains(surfaces, surface => surface.Id == "market-acquisition.inbox" && surface.Target == "Market Acquisition/Inbox");
        Assert.Contains(surfaces, surface => surface.Id == "market-acquisition.workbench" && surface.Target == "Market Acquisition/Workbench");
        Assert.Contains(surfaces, surface => surface.Id == "market-acquisition.route" && surface.Target == "Market Acquisition/Route");
        Assert.DoesNotContain(surfaces, surface => surface.Id == "overview");
        Assert.DoesNotContain(surfaces, surface => surface.Id == "inventory-reporter");
        Assert.Equal(surfaces.OrderBy(surface => surface.Order), surfaces);

        var lockedProvider = new MarketMafiosoBridgeProvider(
            CreateTruth, () => { }, () => { }, () => { }, _ => { }, _ => true, () => { }, () => { }, () => false, new AgentBridgeUiReviewRegistry());
        Assert.DoesNotContain(
            lockedProvider.GetReviewSurfaces(),
            surface => surface.Id.StartsWith("market-acquisition", StringComparison.Ordinal) ||
                       surface.Label.Contains("Market Acquisition", StringComparison.Ordinal));

        var registry = new AgentBridgeUiReviewRegistry();
        registry.BeginFrame();
        registry.Register("squire.probe", "Probe", AgentBridgeUiControlKind.Button, default, new(100, 20), true, false, "Ready", () => { });
        var registeredProvider = new MarketMafiosoBridgeProvider(
            CreateTruth, () => { }, () => { }, () => { }, _ => { }, _ => true, () => { }, () => { }, () => false, registry);
        registry.EndFrame();
        var review = registeredProvider.ReviewControl("squire.probe");
        Assert.Equal("squire.probe", Assert.IsType<AgentBridgeUiControl>(review.Control).Id);
        Assert.Null(registeredProvider.ReviewControl("missing").Control);
    }

    [Fact]
    public void Provider_reports_non_obtrusive_rendered_ui_automation_capabilities()
    {
        var expected = new AgentBridgeUiAutomationCapabilities(
            "registered-node-ui-events",
            MovesOperatingSystemCursor: false,
            ActivatesGameWindow: false,
            RequiresGameForeground: false,
            RequiresVisibleCharacterAddon: true,
            UsesRenderedTooltipAsAuthority: true,
            SupportsDeterministicReplay: true,
            "fixture");
        var provider = new MarketMafiosoBridgeProvider(
            CreateTruth, () => { }, () => { }, () => { }, _ => { }, _ => true, () => { }, () => { },
            () => true,
            new AgentBridgeUiReviewRegistry(),
            getUiAutomationCapabilities: () => expected);

        Assert.Same(expected, provider.GetUiAutomationCapabilities());
        Assert.False(provider.GetUiAutomationCapabilities().MovesOperatingSystemCursor);
        Assert.False(provider.GetUiAutomationCapabilities().ActivatesGameWindow);
    }

    [Fact]
    public void Provider_exposes_debug_synthetic_review_as_an_explicit_action()
    {
        var invoked = false;
        var provider = new MarketMafiosoBridgeProvider(
            CreateTruth, () => { }, () => { }, () => { }, _ => { }, _ => true, () => { }, () => { },
            () => true,
            new AgentBridgeUiReviewRegistry(),
            tryOpenSyntheticAdvisorReview: () => invoked = true);

        Assert.True(provider.TryOpenSyntheticAdvisorReview());
        Assert.True(invoked);
    }

    [Fact]
    public void Provider_returns_submitted_gearset_change_for_rendered_verification()
    {
        Assert.True(GearsetChangeCommand.TryCreate("Miner", out var expected));
        var provider = new MarketMafiosoBridgeProvider(
            CreateTruth, () => { }, () => { }, () => { }, _ => { }, _ => true, () => { }, () => { },
            () => true,
            new AgentBridgeUiReviewRegistry(),
            trySwitchCalibrationJobUi: _ => expected);

        var submitted = Assert.IsType<GearsetChangeCommand>(provider.TrySwitchCalibrationJobUi("Miner"));

        Assert.Same(expected, submitted);
        Assert.Equal("MIN", submitted.GearsetName);
        Assert.Equal("/gearset change \"MIN\"", submitted.Command);
    }

    [Fact]
    public void Provider_exposes_read_only_rendered_retainer_capture()
    {
        var expected = new AgentBridgeRenderedUiSnapshot(DateTimeOffset.UtcNow,
        [
            new("RetainerCharacter", true, true, true, 42, []),
        ]);
        var provider = new MarketMafiosoBridgeProvider(
            CreateTruth, () => { }, () => { }, () => { }, _ => { }, _ => true, () => { }, () => { },
            () => true,
            new AgentBridgeUiReviewRegistry(),
            captureRetainerUi: () => expected);

        Assert.Same(expected, provider.CaptureRetainerUi());
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
