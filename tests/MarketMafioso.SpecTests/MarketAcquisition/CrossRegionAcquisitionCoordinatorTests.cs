using System.Collections.Immutable;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Quartermaster;

namespace MarketMafioso.SpecTests.MarketAcquisition;

public sealed class CrossRegionAcquisitionCoordinatorTests
{
    private static readonly CrossRegionCharacter Primary = new(0x1111, 73, "Primary Char", "Adamantoise");
    private static readonly CrossRegionCharacter Shopper = new(0x2222, 49, "Shopper Char", "Atomos");

    // Why this test exists: a client crash or relog in the I1 window (request id persisted, ack lost)
    // must never hand Quartermaster a second gil transfer. A duplicate silently moves real gil twice
    // and is invisible until the primary's wallet is audited, so it cannot be diagnosed after the fact.
    [Fact]
    public void InterruptedFundPhaseResumesWithoutDuplicateGilTransfer()
    {
        using var fixture = new Fixture();
        var runId = Guid.NewGuid();
        // QM accepts the transfer, but the ack never reaches MMF (crash/IPC timeout).
        fixture.Courier.OnSubmit = request =>
        {
            fixture.Courier.Run = Run(runId, request.RequestId, "Running");
            return null;
        };

        var first = fixture.CreateCoordinator();
        Assert.True(first.Start(fixture.StartRequest(fundGil: 500_000)).Success);
        var requestId = Assert.Single(fixture.Courier.Submitted).RequestId;
        Assert.Equal(requestId, fixture.Store.Load()!.FundRequestId);
        Assert.Null(fixture.Store.Load()!.FundRunId);

        // Restart: the reloaded session adopts QM's run by request id instead of resubmitting.
        var restarted = fixture.CreateCoordinator();
        fixture.TickAfter(restarted);
        Assert.Single(fixture.Courier.Submitted);
        Assert.Equal(runId, fixture.Store.Load()!.FundRunId);

        // The courier is interrupted; MMF fails resumably and Resume asks QM to resume the same run.
        fixture.Courier.Run = Run(runId, requestId, "Interrupted", canResume: true);
        fixture.TickAfter(restarted);
        Assert.Equal(CrossRegionAcquisitionPhase.Failed, restarted.Phase);
        Assert.True(restarted.CanResume);
        Assert.True(restarted.Resume().Success);
        Assert.Equal([runId.ToString("D")], fixture.Courier.Resumed);

        fixture.Courier.Run = Run(runId, requestId, "Completed");
        fixture.TickAfter(restarted);

        Assert.Single(fixture.Courier.Submitted);
        Assert.Equal(500_000, fixture.Courier.Submitted[0].GilAmount);
        Assert.True(fixture.Store.Load()!.FundCompleted);
        Assert.Equal(CrossRegionAcquisitionPhase.SwitchingToShopper, restarted.Phase);
    }

    // Why this test exists: the return haul trades items off the shopper. Shipping anything the
    // shopper did not buy, or more than is still carried, hands away unrelated inventory or makes
    // the courier fail mid-trade on another region, which the user can only untangle manually.
    [Fact]
    public void ReturnHaulReconcilesPurchasedManifestAgainstLiveBagsAndSkipsEmpty()
    {
        using var fixture = new Fixture();
        fixture.Game.LoggedIn = Shopper;
        fixture.Store.Save(ShoppingSession());
        string? persistedBeforeSubmit = null;
        fixture.Courier.OnSubmit = request =>
        {
            persistedBeforeSubmit = fixture.Store.Load()!.HaulRequestId;
            return Accept(request, Guid.NewGuid());
        };

        var coordinator = fixture.CreateCoordinator();
        fixture.Shopping.Raise(new(5057, "Darksteel Ore", false, 10, 20_000, "Atomos", "a"));
        fixture.Shopping.Raise(new(5057, "Darksteel Ore", false, 5, 10_000, "Atomos", "b"));
        fixture.Shopping.Raise(new(5057, "Darksteel Ore", false, 5, 10_000, "Atomos", "b")); // duplicate confirmation
        fixture.Shopping.Raise(new(4, "Lightning Shard", false, 100, 3_000, "Garuda", "c"));
        fixture.Shopping.Raise(new(7, "Water Shard", true, 3, 900, "Garuda", "d"));
        Assert.Equal(118u, coordinator.Session.PurchasedUnits);

        fixture.Game.Bags =
        [
            new(5057, false, 8), new(5057, false, 4), // 15 bought, 12 still carried
            new(4, false, 250),                        // shopper's own shards beyond the 100 bought
            new(7, false, 3),                          // NQ, but the purchase was HQ
            new(9999, false, 50),                      // unrelated
        ];
        fixture.Shopping.State = CrossRegionShoppingState.Completed;
        fixture.TickAfter(coordinator);
        Assert.Equal(CrossRegionAcquisitionPhase.ClosingMarketBoardForHaul, coordinator.Phase);
        fixture.TickAfter(coordinator);

        Assert.Equal(CrossRegionAcquisitionPhase.HaulingToPrimary, coordinator.Phase);
        var haul = Assert.Single(fixture.Courier.Submitted);
        Assert.Equal(haul.RequestId, persistedBeforeSubmit);
        Assert.Equal(Shopper.LocalContentId, haul.Source.LocalContentId);
        Assert.Equal(Primary.LocalContentId, haul.Destination.LocalContentId);
        Assert.Equal(0, haul.GilAmount);
        Assert.False(haul.ReturnOwner);
        Assert.False(haul.ReturnDestinationHome);
        Assert.True(haul.ReturnCarrier);
        Assert.Equal(
            [(4u, false, 100), (5057u, false, 12)],
            haul.Items.Select(item => (item.ItemId, item.IsHq, item.Quantity)).ToArray());

        // Nothing bought: the haul is skipped without a courier request, and the session returns to the primary.
        using var empty = new Fixture();
        empty.Game.LoggedIn = Shopper;
        empty.Store.Save(ShoppingSession());
        var nothingBought = empty.CreateCoordinator();
        empty.Shopping.State = CrossRegionShoppingState.Completed;
        empty.TickAfter(nothingBought);
        Assert.Equal(CrossRegionAcquisitionPhase.ReturningToPrimary, nothingBought.Phase);
        empty.TickAfter(nothingBought);
        empty.TickAfter(nothingBought);
        Assert.Equal([Primary], empty.Game.SwitchRequests);
        Assert.Equal(CrossRegionAcquisitionPhase.Completed, nothingBought.Phase);
        Assert.Equal("Shopping finished with no purchases; returned to primary.", nothingBought.Session.LastMessage);

        // Bought, but none of it is carried any more: the haul is skipped too.
        using var gone = new Fixture();
        gone.Game.LoggedIn = Shopper;
        gone.Store.Save(ShoppingSession());
        var soldOff = gone.CreateCoordinator();
        gone.Shopping.Raise(new(5057, "Darksteel Ore", false, 10, 20_000, "Atomos", "a"));
        gone.Game.Bags = [new(9999, false, 50)];
        gone.Shopping.State = CrossRegionShoppingState.Completed;
        gone.TickAfter(soldOff);
        gone.TickAfter(soldOff);
        Assert.Equal(CrossRegionAcquisitionPhase.ReturningToPrimary, soldOff.Phase);
        gone.TickAfter(soldOff);
        gone.TickAfter(soldOff);
        Assert.Equal([Primary], gone.Game.SwitchRequests);
        Assert.Equal(CrossRegionAcquisitionPhase.Completed, soldOff.Phase);
        Assert.Empty(empty.Courier.Submitted);
        Assert.Empty(gone.Courier.Submitted);
    }

    // Why this test exists: a multi-cycle run settles a finished haul and goes back out across a
    // relog, so a crash can land on either side of the settlement. Counting a delivery twice makes the
    // next cycle under-buy with every target looking met; reusing a finished haul's request id makes
    // Quartermaster acknowledge the next haul without moving anything, so the run reports Completed
    // while the purchases sit on the shopper in another region. Both only surface after a restart in a
    // three-character run that takes over half an hour per cycle to reproduce live, and both cost the
    // user gil or a manual cross-region recovery.
    [Fact]
    public void MultiCycleHaulSettlesOnceAcrossRestartsAndEachCycleShipsOnlyItsOwnPurchases()
    {
        using var fixture = new Fixture();
        fixture.Game.LoggedIn = Shopper;
        var saved = ShoppingSession();
        saved.MultiCycleEnabled = true;
        fixture.Store.Save(saved);
        // Like Quartermaster: a request id it already ran is acknowledged again and moves nothing.
        var runIds = new Dictionary<string, Guid>();
        var moved = new List<int>();
        fixture.Courier.OnSubmit = request =>
        {
            if (runIds.TryGetValue(request.RequestId, out var known))
                return new("qm", request.RequestId, known, QuartermasterCourierAckStatuses.AlreadyCompleted, null, null);
            runIds[request.RequestId] = Guid.NewGuid();
            moved.Add(request.Items.Sum(item => item.Quantity));
            fixture.Courier.Run = Run(runIds[request.RequestId], request.RequestId, "Running");
            return Accept(request, runIds[request.RequestId]);
        };

        var coordinator = fixture.CreateCoordinator();
        fixture.Shopping.Raise(new(5057, "Darksteel Ore", false, 10, 20_000, "Atomos", "a"));
        fixture.Game.Bags = [new(5057, false, 10)];
        fixture.Shopping.StoppedForBagSpace = true;
        fixture.Shopping.State = CrossRegionShoppingState.Completed;
        fixture.TickAfter(coordinator);
        fixture.TickAfter(coordinator);
        var first = Assert.Single(fixture.Courier.Submitted);
        Assert.True(first.DepositToRetainers);

        // Quartermaster finishes the haul, but the client restarts before MarketMafioso sees it.
        fixture.Courier.Run = Run(runIds[first.RequestId], first.RequestId, "Completed") with { Deposit = new(0, null) };
        fixture.Game.LoggedIn = Primary;
        var afterHaul = fixture.Restart();
        fixture.TickAfter(afterHaul);
        Assert.Equal(CrossRegionAcquisitionPhase.SwitchingToShopperForNextCycle, afterHaul.Phase);

        // And restarts again once the cycle is settled: the delivery is neither re-sent nor counted twice.
        var nextCycle = fixture.Restart();
        fixture.TickAfter(nextCycle);
        Assert.Single(fixture.Courier.Submitted);
        var persisted = fixture.Store.Load()!;
        Assert.Equal(2, persisted.CycleNumber);
        Assert.Equal(10u, Assert.Single(persisted.DeliveredManifest).Quantity);
        Assert.Empty(persisted.PurchasedManifest);
        Assert.Equal([Shopper], fixture.Game.SwitchRequests);

        // Cycle 2 buys five more while Quartermaster still shows the finished first haul.
        fixture.TickAfter(nextCycle);
        fixture.TickAfter(nextCycle);
        fixture.TickAfter(nextCycle);
        Assert.Equal(CrossRegionAcquisitionPhase.Shopping, nextCycle.Phase);
        fixture.Shopping.Raise(new(5057, "Darksteel Ore", false, 5, 10_000, "Atomos", "b"));
        fixture.Game.Bags = [new(5057, false, 5)];
        fixture.Shopping.StoppedForBagSpace = true;
        fixture.Shopping.State = CrossRegionShoppingState.Completed;
        fixture.TickAfter(nextCycle);
        fixture.TickAfter(nextCycle);
        Assert.Equal(CrossRegionAcquisitionPhase.HaulingToPrimary, nextCycle.Phase);
        Assert.Equal([10, 5], moved);
        var second = fixture.Courier.Submitted[^1];

        // The retainers take only part of it: the run ends on the primary instead of going back out.
        fixture.Courier.Run = Run(runIds[second.RequestId], second.RequestId, "Completed") with { Deposit = new(2, "DepositLimited") };
        fixture.Game.LoggedIn = Primary;
        fixture.TickAfter(nextCycle);
        Assert.Equal(CrossRegionAcquisitionPhase.Completed, nextCycle.Phase);
        Assert.Equal(15u, Assert.Single(fixture.Store.Load()!.DeliveredManifest).Quantity);
        Assert.Equal([Shopper], fixture.Game.SwitchRequests);
        Assert.Contains("retainers are full", nextCycle.Session.LastMessage);
    }

    // Why this test exists: nothing but this bound and the buys-nothing ending stops a run whose every
    // cycle fills the bags. A loop that keeps going sends three characters back and forth across data
    // centers unattended, spending the user's gil for hours; it cannot be observed short of a live run
    // of that length on the user's own accounts.
    [Fact]
    public void MultiCycleRunEndsAtTheCycleLimitAndOnACycleThatBuysNothing()
    {
        // Every cycle buys, fills the bags, and stows cleanly: only the cycle limit ends it.
        using var endless = new Fixture();
        DriveToEnd(endless, alwaysBuys: true);
        Assert.Equal(CrossRegionAcquisitionCoordinator.MaxCycles, endless.Courier.Submitted.Count);
        Assert.Contains("limit", endless.Store.Load()!.LastMessage);

        // The second cycle finds nothing to buy: no haul, no third trip, back on the primary.
        using var spent = new Fixture();
        DriveToEnd(spent, alwaysBuys: false);
        Assert.Single(spent.Courier.Submitted);
        Assert.Equal(Primary, spent.Game.LoggedIn);
        Assert.Contains("cycle 2 bought nothing", spent.Store.Load()!.LastMessage);

        static void DriveToEnd(Fixture fixture, bool alwaysBuys)
        {
            fixture.Game.LoggedIn = Shopper;
            var saved = ShoppingSession();
            saved.MultiCycleEnabled = true;
            fixture.Store.Save(saved);
            fixture.Courier.OnSubmit = request =>
            {
                var runId = Guid.NewGuid();
                fixture.Courier.Run = Run(runId, request.RequestId, "Completed") with { Deposit = new(0, null) };
                fixture.Game.LoggedIn = Primary;
                return Accept(request, runId);
            };
            var coordinator = fixture.CreateCoordinator();
            var listing = 0;
            for (var tick = 0; tick < 2_000 && !coordinator.Session.IsTerminal; tick++)
            {
                if (coordinator.Phase == CrossRegionAcquisitionPhase.Shopping && fixture.Shopping.State == CrossRegionShoppingState.Running)
                {
                    if (alwaysBuys || coordinator.Session.CycleNumber == 1)
                    {
                        fixture.Shopping.Raise(new(5057, "Darksteel Ore", false, 1, 2_000, "Atomos", (++listing).ToString()));
                        fixture.Game.Bags = [new(5057, false, 1)];
                        fixture.Shopping.StoppedForBagSpace = true;
                    }
                    fixture.Shopping.State = CrossRegionShoppingState.Completed;
                }
                fixture.TickAfter(coordinator);
            }
            Assert.Equal(CrossRegionAcquisitionPhase.Completed, coordinator.Phase);
        }
    }

    private static CrossRegionAcquisitionSession ShoppingSession() => new()
    {
        SessionId = Guid.NewGuid(),
        Phase = CrossRegionAcquisitionPhase.Shopping,
        Primary = Primary,
        Shopper = Shopper,
        TargetRegion = "Japan",
        FundCompleted = true,
        ShopperStartingStock = [],
        RequestDocumentSnapshot = new MarketAcquisitionRequestDocument { Region = "Japan" },
    };

    private static QuartermasterCourierRun Run(Guid runId, string? requestId, string state, bool canResume = false) =>
        new(runId, requestId, state, "Delivering", "Trading", 1, 1, null, null, state == "Running", canResume, false, 0, 0);

    private static QuartermasterCourierAcknowledgement Accept(QuartermasterCourierTransferRequest request, Guid runId) =>
        new("qm", request.RequestId, runId, QuartermasterCourierAckStatuses.Accepted, null, null);

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "mmf-xr-" + Guid.NewGuid().ToString("N"));
        private DateTimeOffset now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

        public Fixture()
        {
            Directory.CreateDirectory(directory);
            Store = new FileCrossRegionAcquisitionStore(directory);
        }

        public FileCrossRegionAcquisitionStore Store { get; }
        public FakeCourier Courier { get; } = new();
        public FakeGame Game { get; } = new();
        public FakeShopping Shopping { get; private set; } = new();

        public CrossRegionAcquisitionCoordinator CreateCoordinator() =>
            new(Courier, Game, Shopping, Store, () => now);

        /// <summary>A client restart: a new coordinator over the same store, with a route adapter that has run nothing.</summary>
        public CrossRegionAcquisitionCoordinator Restart()
        {
            Shopping = new FakeShopping();
            return CreateCoordinator();
        }

        public CrossRegionStartRequest StartRequest(int fundGil) =>
            new(Shopper, "Japan", fundGil, new MarketAcquisitionRequestDocument { Region = "Japan" }, new Dictionary<string, uint>());

        public void TickAfter(CrossRegionAcquisitionCoordinator coordinator)
        {
            now = now.AddSeconds(2);
            coordinator.Tick();
        }

        public void Dispose()
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    private sealed class FakeCourier : IQuartermasterCourierClient
    {
        public List<QuartermasterCourierTransferRequest> Submitted { get; } = [];
        public List<string> Resumed { get; } = [];
        public QuartermasterCourierRun? Run { get; set; }
        /// <summary>Returns the ack to deliver, or null to simulate a lost acknowledgement.</summary>
        public Func<QuartermasterCourierTransferRequest, QuartermasterCourierAcknowledgement?> OnSubmit { get; set; } =
            request => Accept(request, Guid.NewGuid());

        public bool HasCrossRegionCourier => true;

        public bool TryGetCourierStatus(out QuartermasterCourierStatus? status, out string error)
        {
            status = new QuartermasterCourierStatus(
                "qm",
                new QuartermasterCourierLoggedIn(Primary.LocalContentId, Primary.HomeWorldId, Primary.CharacterName, Primary.HomeWorldName,
                    "na", "North America", 2_000_000, 100, true, "Bell"),
                new QuartermasterCourierCarrier(true, true, 0x3333, 21, "Carrier Char", "Ravana", "oc", "Oceania", 10_000, 100),
                new QuartermasterCourierBridge(21, "Ravana", 130, "Ul'dah"),
                [
                    new(Shopper.LocalContentId, Shopper.HomeWorldId, Shopper.CharacterName, Shopper.HomeWorldName, "jp", "Japan", true, "Bell", false),
                ],
                Run)
            {
                Features = [QuartermasterCourierStatus.DepositToRetainersFeature],
            };
            error = string.Empty;
            return true;
        }

        public bool TrySubmitCourierTransfer(QuartermasterCourierTransferRequest request, out QuartermasterCourierAcknowledgement? acknowledgement, out string error)
        {
            Submitted.Add(request);
            acknowledgement = OnSubmit(request);
            error = acknowledgement is null ? "IPC timed out" : string.Empty;
            return acknowledgement is not null;
        }

        public bool TryStopCourierRun(string runId, out QuartermasterCourierAcknowledgement? acknowledgement, out string error)
        {
            acknowledgement = new("qm", null, Guid.Parse(runId), QuartermasterCourierAckStatuses.Stopping, null, null);
            error = string.Empty;
            return true;
        }

        public bool TryResumeCourierRun(string runId, out QuartermasterCourierAcknowledgement? acknowledgement, out string error)
        {
            Resumed.Add(runId);
            acknowledgement = new("qm", null, Guid.Parse(runId), QuartermasterCourierAckStatuses.Accepted, null, null);
            error = string.Empty;
            return true;
        }

        public bool TryPairCourierOwner(QuartermasterCourierCharacter owner, out QuartermasterCourierAcknowledgement? acknowledgement, out string error)
        {
            acknowledgement = new("qm", null, null, QuartermasterCourierAckStatuses.Accepted, null, null);
            error = string.Empty;
            return true;
        }
    }

    private sealed class FakeGame : ICrossRegionGameState
    {
        public CrossRegionCharacter? LoggedIn { get; set; } = Primary;
        public IReadOnlyList<CrossRegionBagStack>? Bags { get; set; } = [];
        public CrossRegionCharacter? LoggedInCharacter => LoggedIn;
        public string? CurrentWorldName => LoggedIn?.HomeWorldName;
        public ulong? CarriedGil => 2_000_000;
        public bool OtherAutomationActive => false;
        public IReadOnlyList<CrossRegionBagStack>? ReadCarriedBags() => Bags;
        public List<CrossRegionCharacter> SwitchRequests { get; } = [];

        /// <summary>Records the request and lands on the target, like a completed relog.</summary>
        public bool TrySwitchCharacter(CrossRegionCharacter target, out string message)
        {
            SwitchRequests.Add(target);
            LoggedIn = target;
            message = "switch requested";
            return true;
        }
    }

    private sealed class FakeShopping : ICrossRegionShoppingRoute
    {
        public CrossRegionShoppingState State { get; set; } = CrossRegionShoppingState.Running;
        public string StatusMessage => State.ToString();
        public bool CanResumeInPlace => false;
        public bool StoppedForBagSpace { get; set; }
        public event Action<MarketAcquisitionConfirmedPurchase>? ConfirmedPurchase;
        public void Raise(MarketAcquisitionConfirmedPurchase purchase) => ConfirmedPurchase?.Invoke(purchase);
        public IReadOnlyDictionary<string, uint> CaptureShopperStartingStock(CrossRegionAcquisitionSession session) => new Dictionary<string, uint>();
        public void BeginPrepareAndStart(CrossRegionAcquisitionSession session) => State = CrossRegionShoppingState.Running;
        public void Tick() { }
        public bool Pause(out string message) { message = string.Empty; return true; }
        public bool ResumeInPlace(out string message) { message = string.Empty; return false; }
        public void Stop() => State = CrossRegionShoppingState.Stopped;
        public bool TryCloseMarketBoard() => true;
    }
}
