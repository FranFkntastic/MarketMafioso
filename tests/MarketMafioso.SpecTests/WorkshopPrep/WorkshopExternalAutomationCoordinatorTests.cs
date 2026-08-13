using System.Reflection;
using Dalamud.Plugin.Services;
using MarketMafioso.Automation.Runtime;

namespace MarketMafioso.SpecTests.WorkshopPrep;

public sealed class WorkshopExternalAutomationCoordinatorTests
{
    [Fact]
    public void Coordinator_OwnsAndRestoresScopedAutomationStopRequests()
    {
        SuppressTextAdvanceAddsMarketMafiosoStopRequest();
        RestoreTextAdvanceRemovesOnlyMarketMafiosoStopRequest();
        DisposeRestoresTextAdvanceStopRequest();
        TradeAutoConfirmUsesYesAlreadyOwnerScopedStopRequest();
        TradeAutoAcceptUsesDropboxOwnerScopedStopRequest();
        WorkshopRequestTemporarilyDisablesEnabledPandoraFeature();
        WorkshopRequestDoesNotEnablePandoraFeatureThatBeganDisabled();
        DisposeRestoresPandoraFeatureSuppressedByWorkshopRequest();
        WorkshopRequestFailsBeforeOpeningUiWhenPandoraCannotReleaseOwnership();
    }

    private static void SuppressTextAdvanceAddsMarketMafiosoStopRequest()
    {
        var stopRequests = new HashSet<string>();
        using var coordinator = new ExternalAutomationCoordinator(
            new FakePluginDataStore(stopRequests),
            TestPluginLog.Create());

        coordinator.SuppressTextAdvance();

        Assert.Contains("MarketMafioso", stopRequests);
    }

    private static void RestoreTextAdvanceRemovesOnlyMarketMafiosoStopRequest()
    {
        var stopRequests = new HashSet<string> { "OtherPlugin" };
        using var coordinator = new ExternalAutomationCoordinator(
            new FakePluginDataStore(stopRequests),
            TestPluginLog.Create());

        coordinator.SuppressTextAdvance();
        coordinator.RestoreTextAdvance();

        Assert.DoesNotContain("MarketMafioso", stopRequests);
        Assert.Contains("OtherPlugin", stopRequests);
    }

    private static void DisposeRestoresTextAdvanceStopRequest()
    {
        var stopRequests = new HashSet<string>();
        var coordinator = new ExternalAutomationCoordinator(
            new FakePluginDataStore(stopRequests),
            TestPluginLog.Create());

        coordinator.SuppressTextAdvance();
        coordinator.Dispose();

        Assert.DoesNotContain("MarketMafioso", stopRequests);
    }

    private static void TradeAutoConfirmUsesYesAlreadyOwnerScopedStopRequest()
    {
        var stopRequests = new HashSet<string> { "OtherPlugin" };
        using var coordinator = new ExternalAutomationCoordinator(
            new FakePluginDataStore(stopRequests, "YesAlready.StopRequests"),
            TestPluginLog.Create());

        coordinator.SuppressTradeAutoConfirm();
        Assert.Contains("MarketMafioso", stopRequests);
        coordinator.RestoreTradeAutoConfirm();

        Assert.DoesNotContain("MarketMafioso", stopRequests);
        Assert.Contains("OtherPlugin", stopRequests);
    }

    private static void TradeAutoAcceptUsesDropboxOwnerScopedStopRequest()
    {
        var stopRequests = new HashSet<string> { "OtherPlugin" };
        using var coordinator = new ExternalAutomationCoordinator(
            new FakePluginDataStore(stopRequests, "Dropbox.StopRequests"),
            TestPluginLog.Create());

        coordinator.SuppressDropboxAutoAccept();
        Assert.Contains("MarketMafioso", stopRequests);
        coordinator.RestoreDropboxAutoAccept();

        Assert.DoesNotContain("MarketMafioso", stopRequests);
        Assert.Contains("OtherPlugin", stopRequests);
    }

    private static void WorkshopRequestTemporarilyDisablesEnabledPandoraFeature()
    {
        var pandora = new FakePandoraFeatureControl(enabled: true);
        using var coordinator = new ExternalAutomationCoordinator(
            new FakePluginDataStore([]),
            TestPluginLog.Create(),
            pandora);

        coordinator.SuppressWorkshopRequestAutomation();
        coordinator.SuppressWorkshopRequestAutomation();
        Assert.False(pandora.Enabled);
        Assert.Equal(new[] { false }, pandora.Writes);

        coordinator.RestoreWorkshopRequestAutomation();
        coordinator.RestoreWorkshopRequestAutomation();
        Assert.True(pandora.Enabled);
        Assert.Equal(new[] { false, true }, pandora.Writes);
    }

    private static void WorkshopRequestDoesNotEnablePandoraFeatureThatBeganDisabled()
    {
        var pandora = new FakePandoraFeatureControl(enabled: false);
        using var coordinator = new ExternalAutomationCoordinator(
            new FakePluginDataStore([]),
            TestPluginLog.Create(),
            pandora);

        coordinator.SuppressWorkshopRequestAutomation();
        coordinator.RestoreWorkshopRequestAutomation();

        Assert.False(pandora.Enabled);
        Assert.Empty(pandora.Writes);
    }

    private static void DisposeRestoresPandoraFeatureSuppressedByWorkshopRequest()
    {
        var pandora = new FakePandoraFeatureControl(enabled: true);
        var coordinator = new ExternalAutomationCoordinator(
            new FakePluginDataStore([]),
            TestPluginLog.Create(),
            pandora);

        coordinator.SuppressWorkshopRequestAutomation();
        coordinator.Dispose();

        Assert.True(pandora.Enabled);
        Assert.Equal(new[] { false, true }, pandora.Writes);
    }

    private static void WorkshopRequestFailsBeforeOpeningUiWhenPandoraCannotReleaseOwnership()
    {
        var pandora = new FakePandoraFeatureControl(enabled: true, failDisable: true);
        using var coordinator = new ExternalAutomationCoordinator(
            new FakePluginDataStore([]),
            TestPluginLog.Create(),
            pandora);

        var error = Assert.Throws<InvalidOperationException>(coordinator.SuppressWorkshopRequestAutomation);

        Assert.Contains("Request-window ownership", error.Message);
        Assert.True(pandora.Enabled);
    }

    private sealed class FakePluginDataStore(
        HashSet<string> stopRequests,
        string expectedKey = "TextAdvance.StopRequests") : IPluginDataStore
    {
        public bool TryGetData<T>(string key, out T? data)
            where T : class
        {
            if (key != expectedKey)
            {
                data = null;
                return false;
            }

            data = (T)(object)stopRequests;
            return true;
        }
    }

    private sealed class FakePandoraFeatureControl(bool enabled, bool failDisable = false) : IPandoraFeatureControl
    {
        public bool Enabled { get; private set; } = enabled;
        public List<bool> Writes { get; } = [];

        public bool? IsEnabled(string internalFeatureName)
        {
            Assert.Equal("AutoSelectTurnin", internalFeatureName);
            return Enabled;
        }

        public void SetEnabled(string internalFeatureName, bool enabled)
        {
            Assert.Equal("AutoSelectTurnin", internalFeatureName);
            if (!enabled && failDisable)
                throw new InvalidOperationException("Pandora refused the disable request.");

            Enabled = enabled;
            Writes.Add(enabled);
        }
    }

    private class TestPluginLog : DispatchProxy
    {
        public static IPluginLog Create() => DispatchProxy.Create<IPluginLog, TestPluginLog>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
    }
}
