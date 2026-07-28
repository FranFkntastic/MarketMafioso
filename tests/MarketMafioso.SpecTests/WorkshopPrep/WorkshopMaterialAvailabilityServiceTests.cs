using System.Collections.Immutable;
using System.Text.Json;
using MarketMafioso.Quartermaster;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.SpecTests.WorkshopPrep;

public sealed class WorkshopMaterialAvailabilityServiceTests
{
    private static readonly QuartermasterOwnerScope CurrentOwner = new(100, 40, "Wei Ning", "Maduin");

    [Theory]
    [InlineData(AvailabilityScenario.OwnerScopedStock)]
    [InlineData(AvailabilityScenario.RejectDifferentOwner)]
    [InlineData(AvailabilityScenario.WithoutQuartermaster)]
    [InlineData(AvailabilityScenario.PlayerAlreadyHasEnough)]
    public void Availability_contract(AvailabilityScenario scenario)
    {
        switch (scenario)
        {
            case AvailabilityScenario.OwnerScopedStock:
                BuildAvailability_MapsOwnerScopedQuartermasterStock();
                break;
            case AvailabilityScenario.RejectDifferentOwner:
                BuildAvailability_RejectsSnapshotForDifferentStableOwnerScope();
                break;
            case AvailabilityScenario.WithoutQuartermaster:
                BuildAvailability_WithoutQuartermaster_StillReportsPlayerInventory();
                break;
            case AvailabilityScenario.PlayerAlreadyHasEnough:
                BuildAvailability_WhenPlayerHasEnough_KeepsStockVisibleWithoutTransferCandidates();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }
    }

    private void BuildAvailability_MapsOwnerScopedQuartermasterStock()
    {
        var requirements = new[] { new WorkshopMaterialRequirement(100, "Elm Lumber", 123, 55) };
        var playerInventory = new Dictionary<uint, int> { [100] = 20 };
        var snapshot = Snapshot(
            new QuartermasterOwner(100, 40, "Wei Ning", "Maduin"),
            Retainer(10, "Current Owner", 100, 25),
            Retainer(11, "Also Current", 100, 10));

        var result = WorkshopMaterialAvailabilityService.BuildAvailability(
            requirements,
            playerInventory,
            snapshot,
            CurrentOwner);

        var item = Assert.Single(result);
        Assert.Equal(55, item.Required);
        Assert.Equal(20, item.PlayerInventory);
        Assert.Equal(35, item.QuartermasterStock);
        Assert.Equal(35, item.Shortage);
        Assert.Equal(0, item.TotalMissing);
        Assert.Equal(0, item.StockDifferential);
        Assert.Equal([10UL, 11UL], item.QuartermasterRetainers.Select(candidate => candidate.RetainerId));

        VerifyStowageCapabilityGuard();
    }

    private void BuildAvailability_RejectsSnapshotForDifferentStableOwnerScope()
    {
        var snapshot = Snapshot(
            new QuartermasterOwner(999, 40, "Other Character", "Maduin"),
            Retainer(10, "Other Owner", 100, 999));

        var item = Assert.Single(WorkshopMaterialAvailabilityService.BuildAvailability(
            [new WorkshopMaterialRequirement(100, "Elm Lumber", 123, 55)],
            new Dictionary<uint, int> { [100] = 20 },
            snapshot,
            CurrentOwner));

        Assert.Equal(0, item.QuartermasterStock);
        Assert.Equal(35, item.TotalMissing);
        Assert.Empty(item.QuartermasterRetainers);
    }

    private void BuildAvailability_WithoutQuartermaster_StillReportsPlayerInventory()
    {
        var item = Assert.Single(WorkshopMaterialAvailabilityService.BuildAvailability(
            [new WorkshopMaterialRequirement(100, "Elm Lumber", 123, 55)],
            new Dictionary<uint, int> { [100] = 20 },
            snapshot: null,
            CurrentOwner));

        Assert.Equal(20, item.PlayerInventory);
        Assert.Equal(0, item.QuartermasterStock);
        Assert.Equal(35, item.Shortage);
        Assert.Equal(35, item.TotalMissing);
    }

    private void BuildAvailability_WhenPlayerHasEnough_KeepsStockVisibleWithoutTransferCandidates()
    {
        var snapshot = Snapshot(
            new QuartermasterOwner(100, 40, "Wei Ning", "Maduin"),
            Retainer(10, "Current Owner", 100, 99));

        var item = Assert.Single(WorkshopMaterialAvailabilityService.BuildAvailability(
            [new WorkshopMaterialRequirement(100, "Elm Lumber", 123, 55)],
            new Dictionary<uint, int> { [100] = 60 },
            snapshot,
            CurrentOwner));

        Assert.Equal(99, item.QuartermasterStock);
        Assert.Equal(0, item.Shortage);
        Assert.Equal(104, item.StockDifferential);
        Assert.Empty(item.QuartermasterRetainers);
    }

    private static QuartermasterSnapshot Snapshot(
        QuartermasterOwner owner,
        params QuartermasterRetainerSnapshot[] retainers) => new(
        "provider-a",
        7,
        new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero),
        owner,
        retainers.ToImmutableArray());

    private static QuartermasterRetainerSnapshot Retainer(
        ulong id,
        string name,
        uint itemId,
        uint quantity) => new(
        id,
        name,
        new DateTimeOffset(2026, 7, 21, 11, 58, 0, TimeSpan.Zero),
        0,
        ImmutableArray.Create(new QuartermasterBagSnapshot(
            "RetainerInventory1",
            "Retainer",
            ImmutableArray.Create(new QuartermasterItemSnapshot(
                itemId,
                "Elm Lumber",
                "Lumber",
                quantity,
                false,
                0,
                "RetainerInventory1",
                0,
                null,
                false)))),
        ImmutableArray<QuartermasterListingSnapshot>.Empty);

    private static void VerifyStowageCapabilityGuard()
    {
        var adapter = new StowageQuartermasterIpcAdapter
        {
            CapabilitiesJson = CapabilitiesJson(QuartermasterIpcClient.StowagePlansCapability),
            SnapshotJson = StowageSnapshotJson(),
        };
        using var client = new QuartermasterIpcClient(adapter);

        Assert.True(client.TryGetSnapshot(out var advertised, out var advertisedError), advertisedError);
        var plan = Assert.Single(advertised!.StowagePlans);
        var rule = Assert.Single(plan.Rules);
        Assert.Equal("General", plan.Name);
        Assert.Equal((uint)100, rule.ItemId);
        Assert.Equal("deposit", rule.Action);
        Assert.Equal(4, rule.ActionQuantity);

        var report = HttpReporter.BuildStowageReport(advertised, includeItemNames: false);
        Assert.Null(Assert.Single(Assert.Single(report!.Plans).Rules).ItemName);

        adapter.CapabilitiesJson = CapabilitiesJson();
        using var unadvertisedClient = new QuartermasterIpcClient(adapter);
        Assert.True(unadvertisedClient.TryGetSnapshot(out var unadvertised, out var unadvertisedError), unadvertisedError);
        Assert.Empty(unadvertised!.StowagePlans);
        Assert.Null(HttpReporter.BuildStowageReport(unadvertised, includeItemNames: true));
    }

    private static string CapabilitiesJson(params string[] capabilities) => JsonSerializer.Serialize(new
    {
        schema = QuartermasterIpcClient.CapabilitiesSchema,
        providerInstanceId = "provider-a",
        revision = 7,
        generatedAtUtc = "2026-07-21T12:00:00Z",
        capabilities,
    });

    private static string StowageSnapshotJson() => JsonSerializer.Serialize(new
    {
        schema = QuartermasterIpcClient.SnapshotSchema,
        providerInstanceId = "provider-a",
        revision = 7,
        generatedAtUtc = "2026-07-21T12:00:00Z",
        owner = new { localContentId = 100UL, homeWorldId = 40U, characterName = "Wei Ning", homeWorldName = "Maduin" },
        retainers = Array.Empty<object>(),
        stowagePlans = new
        {
            schema = QuartermasterIpcClient.StowagePlansSchema,
            plans = new[]
            {
                new
                {
                    id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    revision = 3,
                    owner = new { localContentId = 100UL, homeWorldId = 40U, characterName = "Wei Ning", homeWorldName = "Maduin" },
                    name = "General",
                    enabled = true,
                    priority = 0,
                    rules = new[]
                    {
                        new
                        {
                            id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                            itemId = 100U,
                            itemName = "Elm Lumber",
                            desiredPlayerQuantity = 10,
                            quality = "Any",
                            enabled = true,
                            routing = new { mode = "HomeFirst", preferredRetainerIds = Array.Empty<ulong>(), overflow = "AnyOwnerRetainer" },
                            evaluated = new { action = "deposit", quantity = 4, playerQuantity = 14, desiredPlayerQuantity = 10 },
                        },
                    },
                },
            },
        },
    });

    private sealed class StowageQuartermasterIpcAdapter : IQuartermasterIpcAdapter
    {
        public bool HasCapabilities => true;
        public bool HasSnapshot => true;
        public bool HasSubmitShortages => false;
        public bool HasSubmitElementalDeposit => false;
        public bool HasOperation => false;
        public required string CapabilitiesJson { get; set; }
        public required string SnapshotJson { get; init; }
        public string GetCapabilities() => CapabilitiesJson;
        public string GetSnapshot() => SnapshotJson;
        public string SubmitShortages(string requestJson) => throw new NotSupportedException();
        public string SubmitElementalDeposit(string requestJson) => throw new NotSupportedException();
        public string GetOperation(string operationId) => throw new NotSupportedException();
        public void SubscribeChanged(Action<string> handler) { }
        public void UnsubscribeChanged(Action<string> handler) { }
    }

    public enum AvailabilityScenario
    {
        OwnerScopedStock,
        RejectDifferentOwner,
        WithoutQuartermaster,
        PlayerAlreadyHasEnough,
    }
}
