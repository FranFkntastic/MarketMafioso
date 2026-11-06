using MarketMafioso.Quartermaster;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.SpecTests.WorkshopPrep;

public sealed class WorkshopQuartermasterCadenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IdentitySaveIsSynchronousAndFailurePreventsDispatch(bool failSave)
    {
        var adapter = new Adapter { SnapshotJson = StockSnapshot };
        using var client = new QuartermasterIpcClient(adapter);
        var config = new Configuration();
        var identitySaved = false;
        var deferred = new WorkshopRestockPersistence(() => { }, _ => { });
        using var service = new WorkshopQuartermasterRequestService(config, client, deferred.Request,
            persistIdentity: () =>
            {
                Assert.Null(adapter.SubmittedJson);
                var identity = Assert.Single(config.QuartermasterWorkshopRequests).Value;
                Assert.False(string.IsNullOrWhiteSpace(identity.OperationId));
                if (failSave) throw new IOException("disk unavailable");
                identitySaved = true;
            });
        service.Submit(new(10, 20, "Tester", "World"), [new(12580, "Holy Cedar Lumber", 0, 84, 0, 156, 84, 0, [])]);
        Assert.Equal(!failSave, identitySaved);
        Assert.Equal(!failSave, adapter.SubmittedJson is not null);
        if (failSave)
        {
            Assert.Empty(config.QuartermasterWorkshopRequests);
            Assert.Contains("retrieval was not started", service.LastStatus);
        }
    }

    [Fact]
    public void StoppedPolicyStillReconcilesTerminalRemoteStateOnChanged()
    {
        var adapter = new Adapter { OperationJson = Operation("running", 1) };
        using var client = new QuartermasterIpcClient(adapter);
        var config = new Configuration(); // No active local restock policy.
        config.QuartermasterWorkshopRequests["10:20"] = new()
        { RequestId = "request", OperationId = "operation", Status = "running" };
        var now = DateTimeOffset.UtcNow;
        using var service = new WorkshopQuartermasterRequestService(config, client, () => { }, () => now);
        QuartermasterOwnerScope Owner() => new(10, 20, "Tester", "World");
        Assert.True(WorkshopRestockService.TickRequestReconciliation(service, Owner));
        Assert.False(WorkshopRestockService.TickRequestReconciliation(service, Owner));
        adapter.OperationJson = Operation("completed", 2);
        adapter.Emit();
        Assert.True(WorkshopRestockService.TickRequestReconciliation(service, Owner));
        Assert.Equal("completed", config.QuartermasterWorkshopRequests["10:20"].Status);
        Assert.False(WorkshopRestockService.TickRequestReconciliation(service, Owner));
    }

    private static string Operation(string status, int revision) => System.Text.Json.JsonSerializer.Serialize(new
    {
        schema = QuartermasterIpcClient.OperationSchema, operationId = "operation", requestId = "request",
        providerInstanceId = "test", revision, status,
        owner = new { localContentId = 10, homeWorldId = 20, characterName = "Tester" },
    });

    private const string StockSnapshot = """
        {"schema":"gooseworks-quartermaster-snapshot/v1","providerInstanceId":"test","revision":1,
        "generatedAtUtc":"2026-11-06T08:20:41Z","owner":{"localContentId":10,"homeWorldId":20,"characterName":"Tester"},
        "retainers":[{"retainerId":200,"retainerName":"Scrongle","observedAtUtc":"2026-11-06T08:20:41Z",
        "bags":[{"bagName":"RetainerInventory1","items":[{"itemId":12580,"quantity":12}]}]}]}
        """;
    [Theory]
    [InlineData(12, 10)]
    [InlineData(0, 10)]
    [InlineData(12, 11)]
    public void Submission_refresh_bounds_quantity_and_rejects_empty_or_foreign_cache(uint stock, ulong ownerId)
    {
        var adapter = new Adapter { SnapshotJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            schema = QuartermasterIpcClient.SnapshotSchema, providerInstanceId = "test", revision = 1,
            generatedAtUtc = "2026-11-06T08:20:41Z",
            owner = new { localContentId = ownerId, homeWorldId = 20, characterName = "Tester" },
            retainers = new[] { new { retainerId = 200, retainerName = "Scrongle", observedAtUtc = "2026-11-06T08:20:41Z",
                bags = new[] { new { bagName = "RetainerInventory1", items = new[] { new { itemId = 12580, quantity = stock } } } } } },
        }) };
        using var client = new QuartermasterIpcClient(adapter);
        using var service = new WorkshopQuartermasterRequestService(new Configuration(), client, () => { });
        service.Submit(new(10, 20, "Tester", "World"), [new(12580, "Holy Cedar Lumber", 0, 84, 0, 156, 84, 0, [])]);
        if (stock == 0 || ownerId != 10)
            Assert.Null(adapter.SubmittedJson);
        else
        {
            Assert.NotNull(adapter.SubmittedJson);
            using var json = System.Text.Json.JsonDocument.Parse(adapter.SubmittedJson!);
            Assert.Equal(12, json.RootElement.GetProperty("items")[0].GetProperty("shortageQuantity").GetInt32());
        }
    }

    [Theory]
    [InlineData("failed", WorkshopQuartermasterProgressState.Failed)]
    [InlineData("partially_succeeded", WorkshopQuartermasterProgressState.PartiallySucceeded)]
    public void Refusal_wire_code_survives_persistence_progress_and_logging(string status, WorkshopQuartermasterProgressState expected)
    {
        var adapter = new Adapter { OperationJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            schema = QuartermasterIpcClient.OperationSchema, operationId = "operation", requestId = "request",
            providerInstanceId = "test", revision = 1, status, code = "NoCachedCandidates",
            message = "No owner-scoped cached retainer candidates cover this operation.",
            owner = new { localContentId = 10, homeWorldId = 20, characterName = "Tester" },
        }) };
        using var client = new QuartermasterIpcClient(adapter);
        var config = new Configuration();
        config.QuartermasterWorkshopRequests["10:20"] = new() { RequestId = "request", OperationId = "operation", Status = "running" };
        using var service = new WorkshopQuartermasterRequestService(config, client, () => { });
        var logs = new List<string>();
        service.DiagnosticLog = logs.Add;
        var owner = new QuartermasterOwnerScope(10, 20, "Tester", "World");
        service.PollOperationIfDue(owner);
        Assert.Equal(expected, service.GetProgress(owner).State);
        Assert.Contains("code=NoCachedCandidates", service.LastStatus);
        Assert.Contains("code=NoCachedCandidates", service.GetProgress(owner).Message);
        Assert.Contains("message=No owner-scoped", service.GetProgress(owner).Message);
        Assert.Contains(logs, log => log.Contains($"result={status} code=NoCachedCandidates message=No owner-scoped"));
        using var restored = new WorkshopQuartermasterRequestService(config, client, () => { });
        Assert.Contains("NoCachedCandidates", restored.GetProgress(owner).Message);
    }

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
        public bool HasCapabilities => OperationJson is not null || SnapshotJson is not null;
        public bool HasSnapshot => SnapshotJson is not null;
        public bool HasSubmitShortages => SnapshotJson is not null;
        public bool HasSubmitElementalDeposit => false;
        public string? OperationJson { get; set; }
        public string? SnapshotJson { get; init; }
        public string? SubmittedJson { get; private set; }
        public bool HasOperation => OperationJson is not null;
        public string GetCapabilities() => "{\"schema\":\"gooseworks-quartermaster-capabilities/v1\",\"providerInstanceId\":\"test\",\"revision\":1,\"capabilities\":[\"automaticRetrieval\"]}";
        public string GetSnapshot() => SnapshotJson ?? "{}";
        public string SubmitShortages(string json) { SubmittedJson = json; return "{}"; }
        public string SubmitElementalDeposit(string json) => "{}";
        public string GetOperation(string id) => OperationJson ?? "{}";
        public void SubscribeChanged(Action<string> handler) => changed += handler;
        public void UnsubscribeChanged(Action<string> handler) => changed -= handler;
        public void Emit() => changed?.Invoke("{\"schema\":\"gooseworks-quartermaster-changed/v1\",\"providerInstanceId\":\"test\",\"revision\":1,\"kind\":\"operation\",\"operationId\":\"operation\"}");
    }
}
