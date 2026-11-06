using MarketMafioso.Quartermaster;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.SpecTests.WorkshopPrep;

public sealed class WorkshopQuartermasterCadenceTests
{
    [Fact]
    public async Task Changed_wakes_on_next_framework_read_and_fallback_waits_twenty_seconds()
    {
        var now = DateTimeOffset.UtcNow;
        var adapter = new Adapter();
        using var client = new QuartermasterIpcClient(adapter);
        var config = new Configuration();
        config.QuartermasterWorkshopRequests["10:20"] = new()
        {
            RequestId = "request", OperationId = "operation", Status = "running",
        };
        using var service = new WorkshopQuartermasterRequestService(config, client, () => { }, () => now);
        var owner = new QuartermasterOwnerScope(10, 20, "Tester", "World");
        var wakes = new List<string>();
        service.DiagnosticLog = wakes.Add;
        service.PollOperationIfDue(owner);
        Assert.False(service.IsRefreshDue);
        now = now.AddSeconds(19);
        Assert.False(service.IsRefreshDue);
        await Task.Run(adapter.Emit);
        Assert.True(service.IsRefreshDue);
        Assert.Single(wakes);
        service.PollOperationIfDue(owner);
        Assert.Contains("wake=event", wakes[1]);
        Assert.False(service.IsRefreshDue);
        now = now.AddSeconds(20);
        Assert.True(service.IsRefreshDue);
        service.PollOperationIfDue(owner);
        Assert.Contains("wake=fallback", wakes[2]);
    }

    private sealed class Adapter : IQuartermasterIpcAdapter
    {
        private Action<string>? changed;
        public bool HasCapabilities => false;
        public bool HasSnapshot => false;
        public bool HasSubmitShortages => false;
        public bool HasSubmitElementalDeposit => false;
        public bool HasOperation => false;
        public string GetCapabilities() => "{}";
        public string GetSnapshot() => "{}";
        public string SubmitShortages(string json) => "{}";
        public string SubmitElementalDeposit(string json) => "{}";
        public string GetOperation(string id) => "{}";
        public void SubscribeChanged(Action<string> handler) => changed += handler;
        public void UnsubscribeChanged(Action<string> handler) => changed -= handler;
        public void Emit() => changed?.Invoke("{\"schema\":\"gooseworks-quartermaster-changed/v1\",\"providerInstanceId\":\"test\",\"revision\":1,\"kind\":\"operation\",\"operationId\":\"operation\"}");
    }
}
