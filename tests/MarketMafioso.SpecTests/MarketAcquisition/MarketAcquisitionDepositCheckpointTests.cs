using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Quartermaster;

namespace MarketMafioso.SpecTests.MarketAcquisition;

public sealed class MarketAcquisitionDepositCheckpointTests
{
    private const uint Darksteel = 5057;
    private const uint LightningShard = 4;
    private static readonly QuartermasterOwner Owner = new(0x1111, 73, "Primary Char", "Adamantoise");

    // Why this test exists: a mid-route stow hands real items to Quartermaster from bags that also hold the user's own
    // stock. Asking for the whole stack instead of what this run bought stows inventory the user meant to keep carrying;
    // a request id minted after submit (or reused across checkpoints) lets a reload resubmit or adopt the wrong stow.
    // Either way the evidence is only a retainer audit after the fact, and the route keeps shopping as if all is well.
    [Fact]
    public void MarketAcquisitionDepositCheckpointGoesHomeStowsOnlyTheBoughtDeltaAndResumes()
    {
        var fixture = new Fixture();
        fixture.Runtime.Bags = [new(Darksteel, false, 5)];
        var checkpoints = fixture.Create();
        Assert.True(checkpoints.Prepare(Plan(), "run1", stowAfterDelivery: true).Enabled);

        checkpoints.RecordConfirmedPurchase(Purchase(Darksteel, 20), "Darksteel Ore");
        checkpoints.RecordConfirmedPurchase(Purchase(LightningShard, 100), "Lightning Shard");
        fixture.Runtime.Bags = [new(Darksteel, false, 25), new(LightningShard, false, 100)];
        Assert.True(checkpoints.RequestCheckpoint(false, "Bags full."));
        Assert.True(checkpoints.IsActive);

        // The board closes first, then Lifestream takes the character home before anything is submitted.
        fixture.Runtime.MarketBoardOpen = true;
        fixture.Tick(checkpoints);
        fixture.Runtime.MarketBoardOpen = false;
        fixture.Tick(checkpoints);
        fixture.Tick(checkpoints);
        Assert.Equal(["/li Adamantoise"], fixture.Runtime.Commands);
        Assert.Empty(fixture.Custody.Stows);
        fixture.Runtime.World = "Adamantoise";
        fixture.Runtime.ActionBlocker = "the character is zoning";
        fixture.Tick(checkpoints);
        fixture.Tick(checkpoints);
        Assert.Empty(fixture.Custody.Stows);
        fixture.Runtime.ActionBlocker = null;
        fixture.Tick(checkpoints);

        // I1 + I2: the id is durable before submit, and only the 20 bought (not the user's 5, not the shards) are asked for.
        var stow = Assert.Single(fixture.Custody.Stows);
        Assert.Equal("mmf-local-stow-run1-c1", stow.RequestId);
        Assert.Equal(stow.RequestId, fixture.Custody.PersistedIdAtSubmit);
        Assert.Equal([(Darksteel, 20)], stow.Items.Select(item => (item.ItemId, item.Quantity)).ToArray());

        // Quartermaster moves the 20 into retainers; the user's 5 and the shards stay carried.
        fixture.Runtime.Bags = [new(Darksteel, false, 5), new(LightningShard, false, 100)];
        var settled = fixture.TickUntilCompleted(checkpoints);
        Assert.True(settled.ResumeRoute);
        Assert.False(settled.NoProgress);
        Assert.False(checkpoints.IsActive);
        Assert.Equal(20u, checkpoints.MovedUnits(Darksteel, "Either", includeStowed: true));
        Assert.Equal(2, checkpoints.Snapshot.Cycle);

        // The next checkpoint compares against the bags after the stow and runs under its own id.
        checkpoints.RecordConfirmedPurchase(Purchase(Darksteel, 10), "Darksteel Ore");
        fixture.Runtime.Bags = [new(Darksteel, false, 15)];
        Assert.True(checkpoints.RequestCheckpoint(false, "Bags full."));
        fixture.Tick(checkpoints);
        fixture.Tick(checkpoints);
        Assert.Equal("mmf-local-stow-run1-c2", fixture.Custody.Stows[1].RequestId);
        Assert.Equal([(Darksteel, 10)], fixture.Custody.Stows[1].Items.Select(item => (item.ItemId, item.Quantity)).ToArray());
    }

    // Why this test exists: when every home is full the stow moves nothing. Resuming would refill the same bags and
    // checkpoint again forever, an unattended loop of travel and Quartermaster requests that never buys anything.
    [Fact]
    public void MarketAcquisitionDepositCheckpointThatMakesNoRoomEndsTheCycling()
    {
        var fixture = new Fixture();
        fixture.Runtime.World = "Adamantoise";
        fixture.Custody.MakesRoom = false;
        var checkpoints = fixture.Create();
        Assert.True(checkpoints.Prepare(Plan(), "run2", stowAfterDelivery: true).Enabled);
        checkpoints.RecordConfirmedPurchase(Purchase(Darksteel, 20), "Darksteel Ore");
        fixture.Runtime.Bags = [new(Darksteel, false, 20)];
        Assert.True(checkpoints.RequestCheckpoint(false, "Bags full."));

        var settled = fixture.TickUntilCompleted(checkpoints);

        Assert.True(settled.NoProgress);
        Assert.False(settled.ResumeRoute);
        Assert.False(checkpoints.IsActive);
        Assert.Single(fixture.Custody.Stows);
        checkpoints.RecordConfirmedPurchase(Purchase(Darksteel, 5), "Darksteel Ore");
        Assert.False(checkpoints.RequestCheckpoint(false, "Bags full."));
        Assert.False(checkpoints.RequestCheckpoint(true, "Route finished."));
    }

    private static MarketAcquisitionPlan Plan() => new()
    {
        RequestId = "request-1",
        Lines =
        [
            new() { LineId = "a", ItemId = Darksteel, ItemName = "Darksteel Ore" },
            new() { LineId = "b", ItemId = LightningShard, ItemName = "Lightning Shard" },
        ],
    };

    private static MarketBoardPurchaseCandidate Purchase(uint itemId, uint quantity) =>
        new() { ItemId = itemId, Quantity = quantity, UnitPrice = 100, WorldName = "Gilgamesh" };

    private sealed class Fixture
    {
        public FakeRuntime Runtime { get; } = new();
        public FakeCustody Custody { get; } = new();
        public MemoryStore Store { get; } = new();

        public MarketAcquisitionDepositCheckpointCoordinator Create()
        {
            Custody.Store = Store;
            return new(Custody, Runtime, Store);
        }

        public MarketAcquisitionDepositCheckpointTickResult Tick(MarketAcquisitionDepositCheckpointCoordinator checkpoints)
        {
            Runtime.Now = Runtime.Now.AddSeconds(3);
            return checkpoints.Tick();
        }

        public MarketAcquisitionDepositCheckpointTickResult TickUntilCompleted(MarketAcquisitionDepositCheckpointCoordinator checkpoints)
        {
            for (var tick = 0; tick < 20; tick++)
            {
                var result = Tick(checkpoints);
                Assert.False(result.Failed, result.Message);
                if (result.Completed)
                    return result;
            }
            throw new Xunit.Sdk.XunitException($"The checkpoint never settled: {checkpoints.Snapshot.Message}");
        }
    }

    private sealed class MemoryStore : IMarketAcquisitionDepositCheckpointStateStore
    {
        public MarketAcquisitionDepositCheckpointState? State { get; private set; }
        public MarketAcquisitionDepositCheckpointState? Restore() => State;
        public void Save(MarketAcquisitionDepositCheckpointState? state) => State = state;
    }

    private sealed class FakeRuntime : IMarketAcquisitionDepositCheckpointRuntime
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
        public string World { get; set; } = "Gilgamesh";
        public IReadOnlyList<CrossRegionBagStack>? Bags { get; set; } = [];
        public bool MarketBoardOpen { get; set; }
        public List<string> Commands { get; } = [];

        public DateTimeOffset UtcNow => Now;
        public string? CurrentWorldName => World;
        public string? ActionBlocker { get; set; }
        public bool CanForward => false;

        public bool TryGetOwner(out QuartermasterOwner owner)
        {
            owner = Owner;
            return true;
        }

        public IReadOnlyList<CrossRegionBagStack>? ReadCarriedBags() => Bags;
        public bool TryCloseMarketBoardWindows() => MarketBoardOpen;

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
    }

    /// <summary>Quartermaster's delivered stow: every unit stowed (MakesRoom) or none for lack of room.</summary>
    private sealed class FakeCustody : IQuartermasterRunCustodyClient
    {
        private readonly Dictionary<string, QuartermasterDeliveredStowStatus> statuses = new(StringComparer.Ordinal);
        public MemoryStore? Store { get; set; }
        public List<QuartermasterDeliveredStowRequest> Stows { get; } = [];
        public string? PersistedIdAtSubmit { get; private set; }
        public bool MakesRoom { get; set; } = true;

        public bool HasAutomaticRetrieval => false;
        public bool HasDeliveredStow => true;
        public bool HasShortageSourceRetainers => false;

        public bool TrySubmitShortages(QuartermasterShortageRequest request, out QuartermasterAcknowledgement? acknowledgement, out string error)
        {
            acknowledgement = null;
            error = "Retrieval is not used here.";
            return false;
        }

        public bool TryGetOperation(string operationId, QuartermasterOwnerScope expectedOwner, out QuartermasterOperationStatus? operationStatus, out string error)
        {
            operationStatus = null;
            error = "Retrieval is not used here.";
            return false;
        }

        public bool TrySubmitDeliveredStow(QuartermasterDeliveredStowRequest request, out QuartermasterDeliveredStowStatus? acknowledgement, out string error)
        {
            PersistedIdAtSubmit ??= Store?.State?.RequestId;
            Stows.Add(request);
            statuses[request.RequestId] = new()
            {
                RequestId = request.RequestId,
                OperationId = "op-" + request.RequestId,
                Status = "succeeded",
                Lines = request.Items.Select(item => new QuartermasterDeliveredStowLine
                {
                    ItemId = item.ItemId,
                    ItemName = item.ItemName,
                    IsHq = item.IsHq,
                    Requested = item.Quantity,
                    Carried = item.Quantity,
                    Planned = MakesRoom ? item.Quantity : 0,
                    Stowed = MakesRoom ? item.Quantity : 0,
                    Code = MakesRoom ? "Stowed" : "NoRoom",
                }).ToList(),
            };
            acknowledgement = statuses[request.RequestId] with { Status = "accepted" };
            error = string.Empty;
            return true;
        }

        public bool TryGetDeliveredStow(string requestId, out QuartermasterDeliveredStowStatus? status, out string error)
        {
            status = statuses.GetValueOrDefault(requestId) ?? new() { RequestId = requestId, Status = "not_found" };
            error = string.Empty;
            return true;
        }

        public bool TryCancelRequestedOperation(string requestId, string reason, out QuartermasterCancelAcknowledgement? acknowledgement, out string error)
        {
            acknowledgement = new() { RequestId = requestId, Status = "cancelled" };
            error = string.Empty;
            return true;
        }
    }
}
