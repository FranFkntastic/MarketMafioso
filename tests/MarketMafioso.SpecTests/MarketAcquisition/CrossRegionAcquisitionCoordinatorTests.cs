using System.Collections.Immutable;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Quartermaster;

namespace MarketMafioso.SpecTests.MarketAcquisition;

public sealed class CrossRegionAcquisitionCoordinatorTests
{
    private static readonly CrossRegionCharacter Primary = new(0x1111, 73, "Primary Char", "Adamantoise");
    private static readonly CrossRegionCharacter Shopper = new(0x2222, 49, "Shopper Char", "Atomos");
    private static readonly CrossRegionCharacter FirstAlt = new(0x5555, 73, "First Alt", "Adamantoise");
    private static readonly CrossRegionCharacter SecondAlt = new(0x6666, 73, "Second Alt", "Adamantoise");

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
        // A fresh failure is re-read once after the settle grace before it is final.
        fixture.TickAfter(restarted);
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

    // Why this test exists: a counted-stock collection courier-trades real items off another character. If a
    // persisted collect request that already completed were submitted again after a crash or Resume, the courier
    // would move a second batch of that character's stock, and nothing in the logs tells the duplicate apart.
    [Fact]
    public void CompletedCollectRequestIsNotResubmittedOnRestartOrResume()
    {
        using var fixture = new Fixture();
        var session = ShoppingSession();
        var finishedRun = Guid.NewGuid();
        var shippedRun = Guid.NewGuid();
        session.Phase = CrossRegionAcquisitionPhase.Failed;
        session.InterruptedPhase = CrossRegionAcquisitionPhase.CollectingCountedStock;
        session.CanResumeCourier = true;
        session.HaulDelivered = true;
        session.CountedStockPlanned = true;
        session.Collections =
        [
            new()
            {
                Source = new(0x3333, 73, "First Alt", "Adamantoise"),
                Lines = [new(5057, "Iron Ingot", "Either", 10, 0)],
                State = CrossRegionCollectionState.Completed,
                RetrievalSettled = true,
                RequestId = CrossRegionAcquisitionSession.CollectRequestIdFor(session.SessionId, 0, 1),
                RunId = finishedRun,
                Attempt = 1,
                Manifest = [new(5057, "Iron Ingot", false, 10)],
            },
            new()
            {
                Source = new(0x4444, 73, "Second Alt", "Adamantoise"),
                Lines = [new(5057, "Iron Ingot", "Either", 6, 0)],
                State = CrossRegionCollectionState.Shipping,
                RetrievalSettled = true,
                RequestId = CrossRegionAcquisitionSession.CollectRequestIdFor(session.SessionId, 1, 1),
                RunId = shippedRun,
                Attempt = 1,
                Manifest = [new(5057, "Iron Ingot", false, 6)],
            },
        ];
        fixture.Store.Save(session);
        // Quartermaster finished the second transfer while MMF was down; the courier ended on the primary.
        fixture.Courier.Run = Run(shippedRun, session.Collections[1].RequestId, "Completed");
        fixture.Game.LoggedIn = Primary;

        var restarted = fixture.CreateCoordinator();
        Assert.True(restarted.CanResume);
        Assert.True(restarted.Resume().Success);
        fixture.TickAfter(restarted);
        fixture.TickAfter(restarted);

        Assert.Empty(fixture.Courier.Submitted);
        Assert.Empty(fixture.Courier.Resumed);
        Assert.Equal(CrossRegionAcquisitionPhase.Completed, restarted.Phase);
        Assert.All(fixture.Store.Load()!.Collections, collection => Assert.Equal(CrossRegionCollectionState.Completed, collection.State));
    }

    // Why this test exists: where a haul lands is decided from Quartermaster's arrival plan over the item's global,
    // capped home chain. Routing the whole consignment straight to an alt when the primary's first home is already at
    // its cap, yet never when the units split across two characters (a courier transfer has one destination), is easy
    // to break silently: a wrong call ships goods to the wrong character in another region, and the only evidence is
    // a later inventory audit across characters, which the user must untangle by hand.
    [Fact]
    public void ReturnHaulGoesStraightToTheOnlyCharacterThePlanFillsAndToThePrimaryWhenItSplits()
    {
        // Primary's #1 home is at its cap and takes nothing; every unit is planned for First Alt's retainer.
        using var atCap = new Fixture();
        atCap.Direct.Plan = quantity =>
        [
            Destination(Primary, isOwner: true, 0),
            Destination(FirstAlt, isOwner: false, quantity, (9001, quantity)),
        ];
        var straight = HaulDarksteel(atCap);
        Assert.Equal(FirstAlt.LocalContentId, straight.Destination.LocalContentId);
        Assert.Equal([(5057u, 12, (ulong?)9001)], straight.Items.Select(item => (item.ItemId, item.Quantity, item.RetainerId)).ToArray());

        // The same units split across two alts: one trip to the primary, whose stow forwards both shares.
        using var split = new Fixture();
        split.Direct.Plan = quantity =>
        [
            Destination(Primary, isOwner: true, 0),
            Destination(FirstAlt, isOwner: false, 5, (9001, 5)),
            Destination(SecondAlt, isOwner: false, quantity - 5, (9002, quantity - 5)),
        ];
        var primary = HaulDarksteel(split);
        Assert.Equal(Primary.LocalContentId, primary.Destination.LocalContentId);
        Assert.All(primary.Items, item => Assert.Null(item.RetainerId));
    }

    // Why this test exists: a shopper whose bags fill mid-route hands the haul back, stows it, and shops again. Each
    // cycle moves real items and gil between regions. A second fund moves gil twice, a re-used haul or stow id is
    // adopted as already done (so the purchases are never shipped or stowed), counted stock riding an intermediate
    // haul or collected twice ships another character's stock twice, and a cycle-2 route that re-buys what cycle 1
    // already bought silently overspends. None of it is visible until a cross-character inventory and wallet audit.
    [Fact]
    public void InventoryFullCycleStowsThenShopsOnlyTheRemainderWithoutRefundingOrCollectingTwice()
    {
        using var fixture = new Fixture();
        fixture.Game.BagsByCharacter = new()
        {
            [Primary.LocalContentId] = [],
            [Shopper.LocalContentId] = [new(IronIngot, false, 3)],
            [FirstAlt.LocalContentId] = [new(IronIngot, false, 4)],
        };
        fixture.Courier.OnSubmit = request =>
        {
            var runId = Guid.NewGuid();
            fixture.Courier.Run = Run(runId, request.RequestId, "Completed");
            fixture.Game.Move(request.Source.LocalContentId, request.Destination.LocalContentId, request.Items);
            return Accept(request, runId);
        };
        fixture.Custody.OnStowed = request => fixture.Game.Take(Primary.LocalContentId, request.Items);
        var coordinator = fixture.CreateCoordinator();
        var document = CycleDocument();
        Assert.True(coordinator.Start(new(Shopper, "Japan", 100_000, document, new Dictionary<string, uint>())).Success);
        var sessionId = coordinator.Session.SessionId;

        // Cycle 1: bags fill after 20 of the 30 required Darksteel Ore.
        fixture.DriveUntil(coordinator, CrossRegionAcquisitionPhase.Shopping);
        fixture.Shopping.Raise(new(Darksteel, "Darksteel Ore", false, 20, 40_000, "Atomos", "a"));
        fixture.Game.Give(Shopper.LocalContentId, Darksteel, 20);
        fixture.Shopping.CompletedWithInventoryFull = true;
        fixture.Shopping.State = CrossRegionShoppingState.Completed;
        fixture.DriveUntil(coordinator, CrossRegionAcquisitionPhase.PreparingShopRoute);

        Assert.Equal(2, coordinator.Session.CycleNumber);
        var firstHaul = fixture.Courier.Submitted[1];
        Assert.Equal(CrossRegionAcquisitionSession.HaulRequestIdFor(sessionId, 1), firstHaul.RequestId);
        // I5: the shopper's 3 counted Iron Ingots do not ride an intermediate haul.
        Assert.Equal([(Darksteel, 20)], firstHaul.Items.Select(item => (item.ItemId, item.Quantity)).ToArray());
        var firstStow = Assert.Single(fixture.Custody.Stows);
        Assert.Equal(CrossRegionAcquisitionSession.StowRequestIdFor(sessionId), firstStow.RequestId);
        Assert.DoesNotContain(fixture.Courier.Submitted, request => request.Source.LocalContentId == FirstAlt.LocalContentId);

        // Cycle 2: no second fund (D4), and the route is prepared for the 10 still required (I4).
        fixture.DriveUntil(coordinator, CrossRegionAcquisitionPhase.Shopping);
        Assert.Equal([30u, 10u], fixture.Shopping.PreparedDarksteelTargets);
        fixture.Shopping.Raise(new(Darksteel, "Darksteel Ore", false, 10, 20_000, "Atomos", "b"));
        fixture.Game.Give(Shopper.LocalContentId, Darksteel, 10);
        fixture.Shopping.CompletedWithInventoryFull = false;
        fixture.Shopping.State = CrossRegionShoppingState.Completed;
        fixture.DriveUntil(coordinator, CrossRegionAcquisitionPhase.Completed);

        var submitted = fixture.Courier.Submitted;
        Assert.Single(submitted, request => request.GilAmount > 0);
        var secondHaul = submitted[2];
        Assert.Equal(CrossRegionAcquisitionSession.HaulRequestIdFor(sessionId, 1, cycle: 2), secondHaul.RequestId);
        Assert.EndsWith("-c2", secondHaul.RequestId);
        Assert.Equal(
            [(IronIngot, 3), (Darksteel, 10)],
            secondHaul.Items.Select(item => (item.ItemId, item.Quantity)).OrderBy(item => item.ItemId).ToArray());
        var collect = Assert.Single(submitted, request => request.Source.LocalContentId == FirstAlt.LocalContentId);
        Assert.Equal(3, submitted.IndexOf(collect));
        Assert.Equal(
            [CrossRegionAcquisitionSession.StowRequestIdFor(sessionId), CrossRegionAcquisitionSession.StowRequestIdFor(sessionId, cycle: 2)],
            fixture.Custody.Stows.Select(stow => stow.RequestId).ToArray());
        Assert.Equal(
            [(IronIngot, 7), (Darksteel, 10)],
            fixture.Custody.Stows[1].Items.Select(item => (item.ItemId, item.Quantity)).OrderBy(item => item.ItemId).ToArray());
    }

    // Why this test exists: a cycle whose stow makes no room (every home full) must end the session. Starting another
    // cycle would switch regions again and buy into the same full bags forever, burning gil and hours unattended.
    [Fact]
    public void InventoryFullCycleWhoseStowMakesNoRoomEndsInsteadOfShoppingAgain()
    {
        using var fixture = new Fixture();
        fixture.Game.LoggedIn = Shopper;
        fixture.Game.BagsByCharacter = new()
        {
            [Primary.LocalContentId] = [],
            [Shopper.LocalContentId] = [new(Darksteel, false, 20)],
        };
        fixture.Courier.OnSubmit = request =>
        {
            var runId = Guid.NewGuid();
            fixture.Courier.Run = Run(runId, request.RequestId, "Completed");
            fixture.Game.Move(request.Source.LocalContentId, request.Destination.LocalContentId, request.Items);
            return Accept(request, runId);
        };
        fixture.Custody.MakesRoom = false;
        var session = ShoppingSession();
        session.RequestDocumentSnapshot = CycleDocument() with { Lines = [CycleDocument().Lines[0]] };
        session.PrimaryBagBaseline = [];
        fixture.Store.Save(session);
        var coordinator = fixture.CreateCoordinator();

        fixture.Shopping.Raise(new(Darksteel, "Darksteel Ore", false, 20, 40_000, "Atomos", "a"));
        fixture.Shopping.CompletedWithInventoryFull = true;
        fixture.Shopping.State = CrossRegionShoppingState.Completed;
        fixture.DriveUntil(coordinator, CrossRegionAcquisitionPhase.Completed);

        Assert.Equal(1, coordinator.Session.CycleNumber);
        Assert.False(coordinator.Session.CycleEndedWithInventoryFull);
        Assert.Single(fixture.Custody.Stows);
        Assert.Single(fixture.Courier.Submitted);
        Assert.Equal([Primary], fixture.Game.SwitchRequests);
        Assert.Empty(fixture.Shopping.PreparedDarksteelTargets);
        Assert.Contains("Stopped after cycle 1", coordinator.Session.LastMessage);
    }

    private const uint Darksteel = 5057;
    private const uint IronIngot = 5056;

    /// <summary>30 Darksteel Ore to buy outright, and Iron Ingots counted on the shopper's and First Alt's bags.</summary>
    private static MarketAcquisitionRequestDocument CycleDocument() => new()
    {
        Region = "Japan",
        StowAfterDelivery = true,
        Lines =
        [
            new()
            {
                ItemId = Darksteel, ItemName = "Darksteel Ore", QuantityMode = "TargetQuantity", TargetQuantity = 30,
                TargetBasis = MarketAcquisitionTargetBases.RequiredPurchaseQuantity,
            },
            new()
            {
                ItemId = IronIngot, ItemName = "Iron Ingot", QuantityMode = "TargetQuantity", TargetQuantity = 10,
                CountedStock =
                [
                    new(Shopper.LocalContentId, Shopper.HomeWorldId, Shopper.CharacterName, Shopper.HomeWorldName, "bags", 3, DateTimeOffset.UnixEpoch),
                    new(FirstAlt.LocalContentId, FirstAlt.HomeWorldId, FirstAlt.CharacterName, FirstAlt.HomeWorldName, "bags", 4, DateTimeOffset.UnixEpoch),
                ],
            },
        ],
    };

    /// <summary>Shops 12 Darksteel Ore with Stow after delivery on and returns the haul the coordinator submits.</summary>
    private static QuartermasterCourierTransferRequest HaulDarksteel(Fixture fixture)
    {
        fixture.Game.LoggedIn = Shopper;
        var session = ShoppingSession();
        session.RequestDocumentSnapshot = new MarketAcquisitionRequestDocument { Region = "Japan", StowAfterDelivery = true };
        fixture.Store.Save(session);
        var coordinator = fixture.CreateCoordinator();
        fixture.Shopping.Raise(new(5057, "Darksteel Ore", false, 12, 24_000, "Atomos", "a"));
        fixture.Game.Bags = [new(5057, false, 12)];
        fixture.Shopping.State = CrossRegionShoppingState.Completed;
        fixture.TickAfter(coordinator);
        fixture.TickAfter(coordinator);
        Assert.Equal(CrossRegionAcquisitionPhase.HaulingToPrimary, coordinator.Phase);
        return Assert.Single(fixture.Courier.Submitted);
    }

    private static QuartermasterArrivalDestination Destination(CrossRegionCharacter character, bool isOwner, int units, params (ulong RetainerId, int Units)[] retainers) =>
        new()
        {
            Character = new() { LocalContentId = character.LocalContentId, HomeWorldId = character.HomeWorldId, CharacterName = character.CharacterName, HomeWorldName = character.HomeWorldName },
            IsOwner = isOwner,
            Units = units,
            Retainers = retainers.Select(retainer => new QuartermasterArrivalRetainer { RetainerId = retainer.RetainerId, RetainerName = $"Retainer {retainer.RetainerId}", Units = retainer.Units }).ToArray(),
        };

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
        public FakeShopping Shopping { get; } = new();
        public FakeDirectDelivery Direct { get; } = new();
        public FakeCustody Custody { get; } = new();

        public CrossRegionAcquisitionCoordinator CreateCoordinator() =>
            new(Courier, Game, Shopping, Store, () => now, custody: Custody, directDelivery: Direct);

        /// <summary>Ticks until the phase is reached; fails on Failed or after a bounded number of ticks.</summary>
        public void DriveUntil(CrossRegionAcquisitionCoordinator coordinator, CrossRegionAcquisitionPhase phase)
        {
            for (var tick = 0; tick < 60 && coordinator.Phase != phase; tick++)
            {
                Assert.NotEqual(CrossRegionAcquisitionPhase.Failed, coordinator.Phase);
                TickAfter(coordinator);
            }
            Assert.True(phase == coordinator.Phase, $"Expected {phase}, stuck in {coordinator.Phase}: {coordinator.Session.LastMessage}");
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
                    new(FirstAlt.LocalContentId, FirstAlt.HomeWorldId, FirstAlt.CharacterName, FirstAlt.HomeWorldName, "na", "North America", true, "Bell", false),
                    new(SecondAlt.LocalContentId, SecondAlt.HomeWorldId, SecondAlt.CharacterName, SecondAlt.HomeWorldName, "na", "North America", true, "Bell", false),
                ],
                Run);
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

        public bool TryClearCourierRun(string runId, string requestId, out QuartermasterCourierAcknowledgement? acknowledgement, out string error)
        {
            acknowledgement = new("qm", requestId, Guid.Parse(runId), QuartermasterCourierAckStatuses.Clearing, null, null);
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

    /// <summary>Quartermaster's arrival planner: Plan maps the asked quantity to destinations; null means no direct delivery.</summary>
    private sealed class FakeDirectDelivery : IQuartermasterDirectDeliveryClient
    {
        public Func<int, IReadOnlyList<QuartermasterArrivalDestination>>? Plan { get; set; }
        public bool HasDirectDelivery => Plan is not null;
        public bool HasDeliveredStowForward => Plan is not null;

        public bool TryPlanArrival(QuartermasterOwner owner, IReadOnlyList<QuartermasterArrivalRequestItem> items, out QuartermasterArrivalPlan? plan, out string error)
        {
            plan = new QuartermasterArrivalPlan
            {
                Schema = QuartermasterIpcClient.ArrivalPlanSchema,
                Status = "planned",
                Items = items.Select(item => new QuartermasterArrivalItem
                {
                    ItemId = item.ItemId,
                    IsHq = item.IsHq,
                    Quantity = item.Quantity,
                    Destinations = Plan!(item.Quantity),
                }).ToArray(),
            };
            error = string.Empty;
            return true;
        }
    }

    /// <summary>Quartermaster's delivered stow: every unit stowed (MakesRoom) or none for lack of room.</summary>
    private sealed class FakeCustody : IQuartermasterRunCustodyClient
    {
        private readonly Dictionary<string, QuartermasterDeliveredStowStatus> statuses = new(StringComparer.Ordinal);
        public List<QuartermasterDeliveredStowRequest> Stows { get; } = [];
        public bool MakesRoom { get; set; } = true;
        public Action<QuartermasterDeliveredStowRequest>? OnStowed { get; set; }

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
            Stows.Add(request);
            var lines = request.Items.Select(item => new QuartermasterDeliveredStowLine
            {
                ItemId = item.ItemId,
                ItemName = item.ItemName,
                IsHq = item.IsHq,
                Requested = item.Quantity,
                Carried = item.Quantity,
                Planned = MakesRoom ? item.Quantity : 0,
                Stowed = MakesRoom ? item.Quantity : 0,
                Code = MakesRoom ? "Stowed" : "NoRoom",
            }).ToList();
            if (MakesRoom)
                OnStowed?.Invoke(request);
            statuses[request.RequestId] = new() { RequestId = request.RequestId, OperationId = "op-" + request.RequestId, Status = "succeeded", Lines = lines };
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

    private sealed class FakeGame : ICrossRegionGameState
    {
        public CrossRegionCharacter? LoggedIn { get; set; } = Primary;
        public IReadOnlyList<CrossRegionBagStack>? Bags { get; set; } = [];
        public CrossRegionCharacter? LoggedInCharacter => LoggedIn;
        public string? CurrentWorldName => LoggedIn?.HomeWorldName;
        public ulong? CarriedGil => 2_000_000;
        public bool OtherAutomationActive => false;
        /// <summary>When set, each character carries its own bags and courier transfers move stacks between them.</summary>
        public Dictionary<ulong, List<CrossRegionBagStack>>? BagsByCharacter { get; set; }
        public IReadOnlyList<CrossRegionBagStack>? ReadCarriedBags() =>
            BagsByCharacter is null ? Bags : LoggedIn is { } character ? BagsByCharacter.GetValueOrDefault(character.LocalContentId, []).ToList() : null;
        public List<CrossRegionCharacter> SwitchRequests { get; } = [];

        public void Give(ulong character, uint itemId, uint quantity) => BagsByCharacter![character].Add(new(itemId, false, quantity));

        public void Take(ulong character, IEnumerable<QuartermasterDeliveredStowItem> items)
        {
            foreach (var item in items)
                Remove(character, item.ItemId, (uint)item.Quantity);
        }

        public void Move(ulong from, ulong to, IEnumerable<QuartermasterCourierItem> items)
        {
            if (BagsByCharacter is null)
                return;
            foreach (var item in items)
            {
                Remove(from, item.ItemId, (uint)item.Quantity);
                Give(to, item.ItemId, (uint)item.Quantity);
            }
        }

        private void Remove(ulong character, uint itemId, uint quantity)
        {
            var bags = BagsByCharacter![character];
            var left = bags.Where(stack => stack.ItemId == itemId).Aggregate(0UL, (total, stack) => total + stack.Quantity) - quantity;
            bags.RemoveAll(stack => stack.ItemId == itemId);
            if (left > 0)
                bags.Add(new(itemId, false, (uint)left));
        }

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
        public event Action<MarketAcquisitionConfirmedPurchase>? ConfirmedPurchase;
        public void Raise(MarketAcquisitionConfirmedPurchase purchase) => ConfirmedPurchase?.Invoke(purchase);
        public IReadOnlyDictionary<string, uint> CaptureShopperStartingStock(CrossRegionAcquisitionSession session) => new Dictionary<string, uint>();
        public bool CompletedWithInventoryFull { get; set; }
        public bool HasUnmetTargets(CrossRegionAcquisitionSession session) => true;
        /// <summary>The Darksteel target of each prepared route, from the real resume rule over the session's purchases.</summary>
        public List<uint> PreparedDarksteelTargets { get; } = [];

        public void BeginPrepareAndStart(CrossRegionAcquisitionSession session)
        {
            if (session.RequestDocumentSnapshot is { } document)
                PreparedDarksteelTargets.AddRange(MarketAcquisitionCrossRegionShoppingRoute.ResumedDocument(document, session.PurchasedManifest).Document.Lines
                    .Where(line => line.ItemId == Darksteel).Select(line => line.TargetQuantity));
            State = CrossRegionShoppingState.Running;
        }
        public void Tick() { }
        public bool Pause(out string message) { message = string.Empty; return true; }
        public bool ResumeInPlace(out string message) { message = string.Empty; return false; }
        public void Stop() => State = CrossRegionShoppingState.Stopped;
        public bool TryCloseMarketBoard() => true;
    }
}
