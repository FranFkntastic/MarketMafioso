using System.Text.Json;
using Franthropy.Dalamud.Travel;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Quartermaster;
using MarketMafioso.Tests.Quartermaster;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class ShardAcquisitionCheckpointCoordinatorTests
{
    [Fact]
    public void Checkpoint_ReturnsHomeDepositsExactPurchaseDeltaAndResumes()
    {
        var adapter = new FakeQuartermasterIpcAdapter
        {
            CapabilitiesJson = JsonSerializer.Serialize(new
            {
                schema = QuartermasterIpcClient.CapabilitiesSchema,
                providerInstanceId = "provider-a",
                revision = 1,
                capabilities = new[] { QuartermasterIpcClient.AutomaticElementalDepositCapability },
            }),
            SnapshotJson = JsonSerializer.Serialize(new
            {
                schema = QuartermasterIpcClient.SnapshotSchema,
                providerInstanceId = "provider-a",
                revision = 1,
                generatedAtUtc = "2026-07-23T12:00:00Z",
                owner = new { localContentId = 100ul, homeWorldId = 40u, characterName = "Wei Ning", homeWorldName = "Maduin" },
                retainers = new[]
                {
                    new
                    {
                        retainerId = 10ul,
                        retainerName = "Alpha",
                        observedAtUtc = "2026-07-23T12:00:00Z",
                        gil = 0,
                        bags = new[]
                        {
                            new
                            {
                                bagName = "RetainerCrystals",
                                observedAtUtc = "2026-07-23T12:00:00Z",
                                items = Array.Empty<object>(),
                            },
                        },
                        listings = Array.Empty<object>(),
                    },
                },
            }),
        };
        adapter.SubmitResponse = requestJson =>
        {
            using var request = JsonDocument.Parse(requestJson);
            return JsonSerializer.Serialize(new
            {
                schema = QuartermasterIpcClient.AcknowledgementSchema,
                providerInstanceId = "provider-a",
                requestId = request.RootElement.GetProperty("requestId").GetString(),
                operationId = request.RootElement.GetProperty("operationId").GetString(),
                status = "queued",
            });
        };
        adapter.OperationJson = JsonSerializer.Serialize(new
        {
            schema = QuartermasterIpcClient.OperationSchema,
            providerInstanceId = "provider-a",
            operationId = "placeholder",
            requestId = "placeholder",
            revision = 4,
            owner = new { localContentId = 100ul, homeWorldId = 40u, characterName = "Wei Ning", homeWorldName = "Maduin" },
            status = "succeeded",
            updatedAtUtc = "2026-07-23T12:01:00Z",
            completedAtUtc = "2026-07-23T12:01:00Z",
            message = "Deposit completed.",
            receipts = new[]
            {
                new
                {
                    revision = 3,
                    occurredAtUtc = "2026-07-23T12:01:00Z",
                    status = "running",
                    code = "CrystalDepositVerified",
                    message = "Deposited 500.",
                    itemId = 2u,
                    retainerId = 10ul,
                    quantity = 500,
                },
            },
        });
        using var client = new QuartermasterIpcClient(adapter);
        var runtime = new FakeCheckpointRuntime();
        var store = new MemoryCheckpointStore();
        var coordinator = new ShardAcquisitionCheckpointCoordinator(client, runtime, store);
        var plan = Plan(
            new MarketAcquisitionPlannedListing { ItemId = 2, ItemName = "Fire Shard", ListingId = "lot-1", Quantity = 500 },
            new MarketAcquisitionPlannedListing { ItemId = 2, ItemName = "Fire Shard", ListingId = "lot-2", Quantity = 600 });

        var preflight = coordinator.Prepare(plan, "run-1");
        runtime.Shards[2] = 9_500;
        var requested = coordinator.RecordConfirmedPurchase(new MarketBoardPurchaseCandidate
        {
            ItemId = 2,
            WorldName = "Halicarnassus",
            ListingId = "lot-1",
            Quantity = 500,
            UnitPrice = 1,
        });

        Assert.True(preflight.Success);
        Assert.True(requested);
        coordinator.Tick();
        coordinator.Tick();
        Assert.Equal(["/li Maduin"], runtime.Commands);
        runtime.CurrentWorldName = "Maduin";
        coordinator.Tick();
        coordinator.Tick();
        coordinator.Tick();
        coordinator.Tick();
        using var submitted = JsonDocument.Parse(Assert.Single(adapter.SubmittedRequests));
        var operationId = submitted.RootElement.GetProperty("operationId").GetString()!;
        adapter.OperationJson = adapter.OperationJson
            .Replace("\"placeholder\"", JsonSerializer.Serialize(operationId), StringComparison.Ordinal);
        var completed = coordinator.Tick();

        Assert.True(completed.Completed);
        Assert.True(completed.ResumeRoute);
        Assert.False(coordinator.IsActive);
        Assert.Equal(500, submitted.RootElement.GetProperty("items")[0].GetProperty("maximumQuantity").GetInt32());
        Assert.Equal(0, coordinator.Snapshot.OutstandingByItem[2]);
    }

    [Fact]
    public void Preflight_RejectsPlanWhenKnownRetainerCapacityIsTooSmall()
    {
        var adapter = new FakeQuartermasterIpcAdapter
        {
            CapabilitiesJson = JsonSerializer.Serialize(new
            {
                schema = QuartermasterIpcClient.CapabilitiesSchema,
                providerInstanceId = "provider-a",
                revision = 1,
                capabilities = new[] { QuartermasterIpcClient.AutomaticElementalDepositCapability },
            }),
            SnapshotJson = JsonSerializer.Serialize(new
            {
                schema = QuartermasterIpcClient.SnapshotSchema,
                providerInstanceId = "provider-a",
                revision = 1,
                generatedAtUtc = "2026-07-23T12:00:00Z",
                owner = new { localContentId = 100ul, homeWorldId = 40u, characterName = "Wei Ning", homeWorldName = "Maduin" },
                retainers = new[]
                {
                    new
                    {
                        retainerId = 10ul,
                        retainerName = "Alpha",
                        observedAtUtc = "2026-07-23T12:00:00Z",
                        gil = 0,
                        bags = new[]
                        {
                            new
                            {
                                bagName = "RetainerCrystals",
                                observedAtUtc = "2026-07-23T12:00:00Z",
                                items = new[] { new { itemId = 2u, itemName = "Fire Shard", quantity = 9_900u } },
                            },
                        },
                        listings = Array.Empty<object>(),
                    },
                },
            }),
        };
        using var client = new QuartermasterIpcClient(adapter);
        var coordinator = new ShardAcquisitionCheckpointCoordinator(client, new FakeCheckpointRuntime(), new MemoryCheckpointStore());

        var result = coordinator.Prepare(
            Plan(new MarketAcquisitionPlannedListing { ItemId = 2, ItemName = "Fire Shard", ListingId = "lot", Quantity = 500 }),
            "run-1");

        Assert.False(result.Success);
        Assert.Contains("below the planned", result.Message);
    }

    [Fact]
    public void Preflight_PreservesUnresolvedLedgerWhenNextRouteHasNoShards()
    {
        var adapter = new FakeQuartermasterIpcAdapter
        {
            CapabilitiesJson = JsonSerializer.Serialize(new
            {
                schema = QuartermasterIpcClient.CapabilitiesSchema,
                providerInstanceId = "provider-a",
                revision = 1,
                capabilities = new[] { QuartermasterIpcClient.AutomaticElementalDepositCapability },
            }),
            SnapshotJson = JsonSerializer.Serialize(new
            {
                schema = QuartermasterIpcClient.SnapshotSchema,
                providerInstanceId = "provider-a",
                revision = 1,
                generatedAtUtc = "2026-07-23T12:00:00Z",
                owner = new { localContentId = 100ul, homeWorldId = 40u, characterName = "Wei Ning", homeWorldName = "Maduin" },
                retainers = new[]
                {
                    new
                    {
                        retainerId = 10ul,
                        retainerName = "Alpha",
                        observedAtUtc = "2026-07-23T12:00:00Z",
                        gil = 0,
                        bags = new[]
                        {
                            new
                            {
                                bagName = "RetainerCrystals",
                                observedAtUtc = "2026-07-23T12:00:00Z",
                                items = Array.Empty<object>(),
                            },
                        },
                        listings = Array.Empty<object>(),
                    },
                },
            }),
        };
        using var client = new QuartermasterIpcClient(adapter);
        var store = new MemoryCheckpointStore();
        var coordinator = new ShardAcquisitionCheckpointCoordinator(client, new FakeCheckpointRuntime(), store);
        Assert.True(coordinator.Prepare(
            Plan(new MarketAcquisitionPlannedListing { ItemId = 2, ItemName = "Fire Shard", ListingId = "lot", Quantity = 500 }),
            "run-1").Success);
        coordinator.RecordConfirmedPurchase(new MarketBoardPurchaseCandidate
        {
            ItemId = 2,
            WorldName = "Halicarnassus",
            ListingId = "lot",
            Quantity = 500,
            UnitPrice = 1,
        });

        var result = coordinator.Prepare(
            new MarketAcquisitionPlan { RequestId = "non-shard-route", Status = "Ready" },
            "run-2");

        Assert.False(result.Success);
        Assert.True(result.Enabled);
        Assert.NotNull(store.State);
        Assert.Equal(500, coordinator.Snapshot.OutstandingByItem[2]);
    }

    private static MarketAcquisitionPlan Plan(params MarketAcquisitionPlannedListing[] listings) => new()
    {
        RequestId = "route-1",
        Status = "Ready",
        Lines =
        [
            new MarketAcquisitionPlanLine
            {
                LineId = "line-1",
                ItemId = 2,
                ItemName = "Fire Shard",
                PlannedQuantity = (uint)listings.Sum(listing => listing.Quantity),
            },
        ],
        WorldBatches =
        [
            new MarketAcquisitionWorldBatch
            {
                WorldName = "Halicarnassus",
                ItemSubtasks =
                [
                    new MarketAcquisitionWorldItemSubtask
                    {
                        LineId = "line-1",
                        ItemId = 2,
                        ItemName = "Fire Shard",
                        WorldName = "Halicarnassus",
                        Listings = listings,
                    },
                ],
                Listings = listings,
            },
        ],
    };

    private sealed class MemoryCheckpointStore : IShardAcquisitionCheckpointStateStore
    {
        public ShardAcquisitionCheckpointState? State { get; private set; }
        public ShardAcquisitionCheckpointState? Restore() => State;
        public void Save(ShardAcquisitionCheckpointState? state) => State = state;
    }

    private sealed class FakeCheckpointRuntime : IShardAcquisitionCheckpointRuntime
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
        public string? CurrentWorldName { get; set; } = "Halicarnassus";
        public Dictionary<uint, int> Shards { get; } = new() { [2] = 9_000 };
        public List<string> Commands { get; } = [];
        public bool TryGetOwner(out QuartermasterOwner owner)
        {
            owner = new(100, 40, "Wei Ning", "Maduin");
            return true;
        }
        public IReadOnlyDictionary<uint, int> CountPlayerShards() => Shards;
        public bool TryCloseMarketBoardWindows() => true;
        public bool ProcessCommand(string command)
        {
            Commands.Add(command);
            return true;
        }
        public bool TryIsLifestreamBusy(out bool busy)
        {
            busy = false;
            return true;
        }
        public PrivateEstateTravelResult TryTravelToPrivateEstate() =>
            new(PrivateEstateTravelState.Submitted, "Submitted", "Accepted.");
        public bool TryOpenSummoningBell() => true;
    }
}
