using System.Text.Json;
using MarketMafioso.Quartermaster;
using MarketMafioso.SpecTests.Windows;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.SpecTests.WorkshopPrep;

public sealed class WorkshopMaterialAvailabilityServiceTests
{
    [Fact]
    public void Stowage_capability_contract()
    {
        VerifyStowageCapabilityGuard();
        WindowPlacementRecoveryTests.VerifyContract();
    }

    private static void VerifyStowageCapabilityGuard()
    {
        var adapter = new StowageQuartermasterIpcAdapter
        {
            CapabilitiesJson = CapabilitiesJson(QuartermasterIpcClient.StowagePlansCapability),
            SnapshotJson = StowageSnapshotJson(),
        };
        using var client = new QuartermasterIpcClient(adapter);

        Assert.True(client.TryGetSnapshot(out var advertised, out var advertisedError), advertisedError);
        Assert.True(advertised!.HasStowageEvidence);
        var plan = Assert.Single(advertised!.StowagePlans);
        var rule = Assert.Single(plan.Rules);
        Assert.Equal("General", plan.Name);
        Assert.Equal((uint)100, rule.ItemId);
        Assert.Equal("HomeFirst", rule.RoutingMode);
        Assert.Equal("AnyOwnerRetainer", rule.Overflow);
        Assert.Equal("deposit", rule.Action);
        Assert.Equal(4, rule.ActionQuantity);
        Assert.Equal((uint)25, Assert.Single(Assert.Single(advertised.Retainers).Bags).Items.Single().Quantity);

        var report = HttpReporter.BuildStowageReport(advertised, includeItemNames: false);
        Assert.Null(Assert.Single(Assert.Single(report!.Plans).Rules).ItemName);

        var stringRoutingAdapter = new StowageQuartermasterIpcAdapter
        {
            CapabilitiesJson = CapabilitiesJson(QuartermasterIpcClient.StowagePlansCapability),
            SnapshotJson = StowageSnapshotJson(useNumericRouting: false),
        };
        using var stringRoutingClient = new QuartermasterIpcClient(stringRoutingAdapter);
        Assert.True(stringRoutingClient.TryGetSnapshot(out var stringRouting, out var stringRoutingError), stringRoutingError);
        Assert.Equal("HomeFirst", Assert.Single(Assert.Single(stringRouting!.StowagePlans).Rules).RoutingMode);

        adapter.CapabilitiesJson = CapabilitiesJson();
        using var unadvertisedClient = new QuartermasterIpcClient(adapter);
        Assert.True(unadvertisedClient.TryGetSnapshot(out var unadvertised, out var unadvertisedError), unadvertisedError);
        Assert.False(unadvertised!.HasStowageEvidence);
        Assert.Empty(unadvertised!.StowagePlans);
        Assert.Null(HttpReporter.BuildStowageReport(unadvertised, includeItemNames: true));

        var malformedAdapter = new StowageQuartermasterIpcAdapter
        {
            CapabilitiesJson = CapabilitiesJson(QuartermasterIpcClient.StowagePlansCapability),
            SnapshotJson = MalformedStowageSnapshotJson(),
        };
        using var malformedClient = new QuartermasterIpcClient(malformedAdapter);
        Assert.True(malformedClient.TryGetSnapshot(out var coreSnapshot, out var coreError), coreError);
        Assert.False(coreSnapshot!.HasStowageEvidence);
        Assert.Empty(coreSnapshot!.StowagePlans);
        Assert.Equal((uint)25, Assert.Single(Assert.Single(coreSnapshot.Retainers).Bags).Items.Single().Quantity);
        Assert.Contains("Optional Stowage Plans data was ignored", malformedClient.LastStatus, StringComparison.Ordinal);
    }

    private static string CapabilitiesJson(params string[] capabilities) => JsonSerializer.Serialize(new
    {
        schema = QuartermasterIpcClient.CapabilitiesSchema,
        providerInstanceId = "provider-a",
        revision = 7,
        generatedAtUtc = "2026-07-21T12:00:00Z",
        capabilities,
    });

    private static string StowageSnapshotJson(bool useNumericRouting = true) => JsonSerializer.Serialize(new
    {
        schema = QuartermasterIpcClient.SnapshotSchema,
        providerInstanceId = "provider-a",
        revision = 7,
        generatedAtUtc = "2026-07-21T12:00:00Z",
        owner = new { localContentId = 100UL, homeWorldId = 40U, characterName = "Wei Ning", homeWorldName = "Maduin" },
        retainers = new[]
        {
            new
            {
                retainerId = 10UL,
                retainerName = "Current Owner",
                observedAtUtc = "2026-07-21T11:59:00Z",
                bags = new[]
                {
                    new
                    {
                        bagName = "RetainerInventory1",
                        items = new[]
                        {
                            new { itemId = 100U, itemName = "Elm Lumber", quantity = 25U },
                        },
                    },
                },
            },
        },
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
                            routing = new
                            {
                                mode = useNumericRouting ? (object)1 : "HomeFirst",
                                preferredRetainerIds = Array.Empty<ulong>(),
                                overflow = useNumericRouting ? (object)0 : "AnyOwnerRetainer",
                            },
                            evaluated = new { action = "deposit", quantity = 4, playerQuantity = 14, desiredPlayerQuantity = 10 },
                        },
                    },
                },
            },
        },
    });

    private static string MalformedStowageSnapshotJson() => JsonSerializer.Serialize(new
    {
        schema = QuartermasterIpcClient.SnapshotSchema,
        providerInstanceId = "provider-a",
        revision = 7,
        generatedAtUtc = "2026-07-21T12:00:00Z",
        owner = new { localContentId = 100UL, homeWorldId = 40U, characterName = "Wei Ning", homeWorldName = "Maduin" },
        retainers = new[]
        {
            new
            {
                retainerId = 10UL,
                retainerName = "Current Owner",
                observedAtUtc = "2026-07-21T11:59:00Z",
                bags = new[]
                {
                    new
                    {
                        bagName = "RetainerInventory1",
                        items = new[]
                        {
                            new { itemId = 100U, itemName = "Elm Lumber", quantity = 25U },
                        },
                    },
                },
            },
        },
        stowagePlans = new
        {
            schema = QuartermasterIpcClient.StowagePlansSchema,
            plans = new[] { new { id = Guid.Empty } },
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

}
