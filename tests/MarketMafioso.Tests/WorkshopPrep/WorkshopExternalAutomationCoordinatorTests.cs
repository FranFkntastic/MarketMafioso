using System.Reflection;
using Dalamud.Plugin.Services;
using MarketMafioso.Automation.Runtime;

namespace MarketMafioso.Tests.WorkshopPrep;

public sealed class WorkshopExternalAutomationCoordinatorTests
{
    [Fact]
    public void SuppressTextAdvance_adds_marketmafioso_stop_request()
    {
        var stopRequests = new HashSet<string>();
        using var coordinator = new ExternalAutomationCoordinator(
            new FakePluginDataStore(stopRequests),
            TestPluginLog.Create());

        coordinator.SuppressTextAdvance();

        Assert.Contains("MarketMafioso", stopRequests);
    }

    [Fact]
    public void RestoreTextAdvance_removes_only_marketmafioso_stop_request()
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

    [Fact]
    public void Dispose_restores_textadvance_stop_request()
    {
        var stopRequests = new HashSet<string>();
        var coordinator = new ExternalAutomationCoordinator(
            new FakePluginDataStore(stopRequests),
            TestPluginLog.Create());

        coordinator.SuppressTextAdvance();
        coordinator.Dispose();

        Assert.DoesNotContain("MarketMafioso", stopRequests);
    }

    [Fact]
    public void TradeAutoConfirm_UsesYesAlreadyOwnerScopedStopRequest()
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

    private class TestPluginLog : DispatchProxy
    {
        public static IPluginLog Create() => DispatchProxy.Create<IPluginLog, TestPluginLog>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
    }
}
