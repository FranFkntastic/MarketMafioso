using System.Numerics;
using Franthropy.Dalamud.Automation.Vendors;
using Franthropy.Dalamud.Automation.Vendors.Coordination;
using MarketMafioso.Quartermaster;
using MarketMafioso.WorkshopPrep;
using Newtonsoft.Json;

namespace MarketMafioso.SpecTests.WorkshopPrep;

public sealed class WorkshopVendorRestockRunnerTests
{
    private static readonly QuartermasterOwnerScope Owner = new(10, 20, "Tester", "World");

    [Fact]
    public void Review_maps_workshop_quantity_gil_and_offer_ceilings_into_engine_plan()
    {
        var runtime = new FakeRuntime();
        var config = new Configuration();
        runtime.Config = config;
        var runner = new WorkshopVendorRestockRunner(config, runtime, new FakeQuartermaster(), () => { });
        var primary = new WorkshopVendorCandidate(
            Offer(1, 100),
            new(GilVendorAccessState.Verified, "test", "Verified."));
        var alternative = new WorkshopVendorCandidate(
            Offer(1, 200),
            new(GilVendorAccessState.Probeable, "test", "Probeable."));
        var selected = new WorkshopMaterialProcurement(
            Availability(1, required: 8, player: 2, retainer: 0),
            0, 6, [primary, alternative], primary, false, true, 4);
        var review = Review(selected, [Stop(selected)]);

        Assert.True(runner.TryStart(review, Owner, true, out var error), error);

        var run = runner.ActiveRun!;
        var engineLine = Assert.Single(runtime.Config!.ActiveWorkshopVendorBuyRun!.Lines);
        Assert.Equal(4, engineLine.ApprovedQuantity);
        Assert.Equal(40UL, engineLine.ApprovedGilCeiling);
        Assert.Equal(10u, engineLine.UnitPriceGil);
        Assert.Equal(100u, engineLine.Offer!.NpcId);
        Assert.Equal(200u, Assert.Single(engineLine.AlternativeOffers).NpcId);
        Assert.Equal(40UL, runtime.Config.ActiveWorkshopVendorBuyRun.MaximumApprovedGil);
        Assert.Equal(4, Assert.Single(run.Lines).ApprovedVendorQuantity);
    }

    [Fact]
    public void Queue_signature_ignores_inventory_refresh_but_changes_with_requirements()
    {
        var planner = new WorkshopVendorProcurementPlanner(
            GilVendorCatalog.Create([Offer(1)]),
            _ => new(GilVendorAccessState.Verified, "test", "Verified."),
            _ => false);
        var quantities = new Dictionary<uint, int>();
        var included = new HashSet<uint>();
        var excluded = new HashSet<uint>();
        var first = planner.Build([Availability(1, 10, 0, 2)], quantities, included, excluded);
        var refreshed = planner.Build([Availability(1, 10, 7, 1)], quantities, included, excluded);
        var changed = planner.Build([Availability(1, 11, 7, 1)], quantities, included, excluded);

        Assert.Equal(first.QueueSignature, refreshed.QueueSignature);
        Assert.NotEqual(first.QueueSignature, changed.QueueSignature);
    }

    [Fact]
    public void Partial_quartermaster_result_never_expands_reviewed_vendor_ceiling()
    {
        var runtime = new FakeRuntime();
        runtime.Counts[1] = 2;
        var quartermaster = new FakeQuartermaster
        {
            Progress = new(WorkshopQuartermasterProgressState.PartiallySucceeded, "Quartermaster finished partially."),
        };
        var config = new Configuration();
        runtime.Config = config;
        var runner = new WorkshopVendorRestockRunner(config, runtime, quartermaster, () => { });
        var material = Material(1, required: 20, player: 2, retainer: 3, vendor: 15);
        var review = Review(material, [Stop(material)]);

        Assert.True(runner.TryStart(review, Owner, true, out var error), error);
        runner.Tick(review.QueueSignature, Owner); // submit Quartermaster request
        runner.Tick(review.QueueSignature, Owner); // observe partial terminal result
        runner.Tick(review.QueueSignature, Owner); // refresh inventory and start engine

        Assert.Equal(15, Assert.Single(config.ActiveWorkshopVendorBuyRun!.Lines).ApprovedQuantity);
        Assert.Equal(150UL, Assert.Single(config.ActiveWorkshopVendorBuyRun.Lines).ApprovedGilCeiling);
    }

    [Fact]
    public void Disabled_vendor_toggle_creates_no_vendor_engine_authority()
    {
        var runtime = new FakeRuntime();
        var quartermaster = new FakeQuartermaster
        {
            Progress = new(WorkshopQuartermasterProgressState.Completed, "Quartermaster complete."),
        };
        var config = new Configuration();
        runtime.Config = config;
        var runner = new WorkshopVendorRestockRunner(config, runtime, quartermaster, () => { });
        var material = Material(1, required: 10, player: 0, retainer: 4, vendor: 6);
        var review = Review(material, [Stop(material)]);

        Assert.True(runner.TryStart(review, Owner, false, out var error), error);
        runner.Tick(review.QueueSignature, Owner);
        runner.Tick(review.QueueSignature, Owner);
        runner.Tick(review.QueueSignature, Owner);

        Assert.Equal(WorkshopVendorRestockPhase.Completed, runner.ActiveRun!.Phase);
        Assert.Null(config.ActiveWorkshopVendorBuyRun);
        Assert.Equal(0, runtime.SubmitCalls);
    }

    [Theory]
    [InlineData(WorkshopQuartermasterProgressState.Failed, WorkshopVendorRestockPhase.Failed)]
    [InlineData(WorkshopQuartermasterProgressState.Indeterminate, WorkshopVendorRestockPhase.Indeterminate)]
    public void Quartermaster_terminal_failures_remain_workshop_terminal_states(
        WorkshopQuartermasterProgressState progress,
        WorkshopVendorRestockPhase expected)
    {
        var runtime = new FakeRuntime();
        var quartermaster = new FakeQuartermaster { Progress = new(progress, "Quartermaster terminal result.") };
        var config = new Configuration();
        runtime.Config = config;
        var runner = new WorkshopVendorRestockRunner(config, runtime, quartermaster, () => { });
        var material = Material(1, required: 10, player: 0, retainer: 4, vendor: 0);
        var review = Review(material, []);

        Assert.True(runner.TryStart(review, Owner, false, out var error), error);
        runner.Tick(review.QueueSignature, Owner);
        runner.Tick(review.QueueSignature, Owner);

        Assert.Equal(expected, runner.ActiveRun!.Phase);
    }

    [Fact]
    public void Owner_and_queue_context_signature_pauses_and_refuses_mismatched_resume()
    {
        var runtime = new FakeRuntime();
        var config = new Configuration();
        runtime.Config = config;
        var runner = new WorkshopVendorRestockRunner(config, runtime, new FakeQuartermaster(), () => { });
        var material = Material(1, required: 2, player: 0, retainer: 0, vendor: 2);
        var review = Review(material, [Stop(material)]);

        Assert.True(runner.TryStart(review, Owner, true, out var error), error);
        Assert.Equal("10|20|QUEUE", config.ActiveWorkshopVendorBuyRun!.ContextSignature);
        runner.Tick("DIFFERENT", Owner);

        Assert.Equal(WorkshopVendorRestockPhase.Paused, runner.ActiveRun!.Phase);
        Assert.False(runner.Resume(new(11, 20, "Other", "World"), review.QueueSignature, out _));
        Assert.False(runner.Resume(Owner, "DIFFERENT", out _));
        Assert.True(runner.Resume(Owner, review.QueueSignature, out error), error);
    }

    [Fact]
    public void Legacy_armed_purchase_converts_once_and_reconciles_through_coordinator_evidence_drain()
    {
        var legacyJson = JsonConvert.SerializeObject(new
        {
            ActiveWorkshopVendorRestock = LegacyArmedConfiguration().LegacyActiveWorkshopVendorRestock,
        });
        var config = JsonConvert.DeserializeObject<Configuration>(legacyJson)!;
        var runtime = new FakeRuntime { Config = config, Gil = 980 };
        runtime.Counts[1] = 2;
        var logs = new List<string>();

        using (var convertingRunner = new WorkshopVendorRestockRunner(
                   config, runtime, new FakeQuartermaster(), () => { }, logs.Add))
        {
            Assert.Null(config.LegacyActiveWorkshopVendorRestock);
            Assert.Equal(1, config.WorkshopVendorRestockLegacyConversions);
            Assert.NotNull(config.ActiveWorkshopVendorBuyRun!.ArmedPurchase);
        }

        var roundTripped = JsonConvert.DeserializeObject<Configuration>(JsonConvert.SerializeObject(config))!;
        runtime.Config = roundTripped;
        using var reloaded = new WorkshopVendorRestockRunner(roundTripped, runtime, new FakeQuartermaster(), () => { }, logs.Add);
        reloaded.Tick("QUEUE", Owner);

        Assert.Equal(1, roundTripped.WorkshopVendorRestockLegacyConversions);
        Assert.Single(logs);
        Assert.Single(roundTripped.ActiveWorkshopVendorBuyRun!.Receipts);
        Assert.Null(roundTripped.ActiveWorkshopVendorBuyRun.ArmedPurchase);
        Assert.Equal(GilVendorBuyPhase.PurchaseLine, roundTripped.ActiveWorkshopVendorBuyRun.Phase);
        Assert.Equal(0, runtime.SubmitCalls);
    }

    private static Configuration LegacyArmedConfiguration() => new()
    {
        LegacyActiveWorkshopVendorRestock = new PersistedWorkshopVendorRestockRun
        {
            RunId = "legacy-run",
            LocalContentId = 10,
            HomeWorldId = 20,
            CharacterName = "Tester",
            QueueSignature = "QUEUE",
            AutomaticallyBuyVendorMaterials = true,
            MaximumApprovedGil = 20,
            Phase = WorkshopVendorRestockPhase.VerifyReceipt,
            ResumePhase = WorkshopVendorRestockPhase.PurchaseLine,
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            UpdatedAtUtc = DateTime.UtcNow,
            Lines =
            [
                new()
                {
                    ItemId = 1,
                    ItemName = "Item 1",
                    RequiredQuantity = 2,
                    ApprovedVendorQuantity = 2,
                    UnitPriceGil = 10,
                    ApprovedGilCeiling = 20,
                    Offer = LegacyOffer(Offer(1)),
                },
            ],
            Stops =
            [
                new()
                {
                    NpcId = 100,
                    ShopId = 50,
                    TerritoryId = 129,
                    NpcName = "Vendor 100",
                    ItemIds = [1],
                    ShopValidated = true,
                    MatchedShopRows = new() { [1] = 0 },
                },
            ],
            ArmedPurchase = new()
            {
                ItemId = 1,
                Quantity = 2,
                ExpectedGil = 20,
                ShopRowIndex = 0,
                BeforeItemCount = 0,
                BeforeGil = 1_000,
                ArmedAtUtc = DateTime.UtcNow,
            },
        },
    };

    private static PersistedGilVendorOffer LegacyOffer(GilVendorOffer offer) => new()
    {
        ItemId = offer.ItemId,
        ItemName = offer.ItemName,
        IconId = offer.IconId,
        UnitPriceGil = offer.UnitPriceGil,
        ShopId = offer.ShopId,
        ShopRowIndex = offer.ShopRowIndex,
        NpcId = offer.NpcId,
        NpcName = offer.NpcName,
        TerritoryId = offer.TerritoryId,
        PositionX = offer.Position.X,
        PositionY = offer.Position.Y,
        PositionZ = offer.Position.Z,
        RouteAetheryteIds = [.. offer.RouteAetheryteIds],
    };

    private static WorkshopVendorRestockReview Review(
        WorkshopMaterialProcurement material,
        IReadOnlyList<WorkshopVendorStopReview> stops) => new("QUEUE", [material], stops);

    private static WorkshopMaterialProcurement Material(uint itemId, int required, int player, int retainer, int vendor)
    {
        var candidate = new WorkshopVendorCandidate(
            Offer(itemId),
            new(GilVendorAccessState.Verified, "test", "Verified."));
        return new(
            Availability(itemId, required, player, retainer),
            retainer,
            vendor,
            [candidate],
            candidate,
            false,
            true,
            vendor);
    }

    private static WorkshopVendorStopReview Stop(WorkshopMaterialProcurement material) =>
        new(100, 50, 129, "Vendor 100", [material]);

    private static WorkshopMaterialAvailability Availability(uint itemId, int required, int player, int retainer)
    {
        var shortage = Math.Max(0, required - player);
        return new(itemId, $"Item {itemId}", 1, required, player, retainer, shortage,
            Math.Max(0, shortage - retainer), []);
    }

    private static GilVendorOffer Offer(uint itemId, uint npcId = 100) => new(
        itemId, $"Item {itemId}", 1, 10, 50, 0, npcId, $"Vendor {npcId}", 129,
        new Vector3(1, 2, 3), [2]);

    private sealed class FakeQuartermaster : IWorkshopQuartermasterRestockService
    {
        public string LastStatus { get; private set; } = "Quartermaster ready.";
        public WorkshopQuartermasterProgress Progress { get; set; } =
            new(WorkshopQuartermasterProgressState.Running, "Quartermaster running.");
        public bool Submit(QuartermasterOwnerScope owner, IReadOnlyList<WorkshopMaterialAvailability> availability) => true;
        public WorkshopQuartermasterProgress GetProgress(QuartermasterOwnerScope owner) => Progress;
    }

    private sealed class FakeRuntime : IGilVendorBuyRuntime
    {
        public Configuration? Config { get; set; }
        public Dictionary<uint, int> Counts { get; } = [];
        public ulong Gil { get; set; } = 1_000_000;
        public int SubmitCalls { get; private set; }

        public GilVendorInventorySnapshot CaptureInventory(IReadOnlyCollection<uint> itemIds) =>
            new(true, Gil, itemIds.ToDictionary(id => id, id => Counts.GetValueOrDefault(id)), "Inventory ready.");
        public bool HasCapacity(IReadOnlyDictionary<uint, int> quantities, out string message)
        {
            message = "Capacity ready.";
            return true;
        }
        public GilVendorReachResult AdvanceToOpenShop(GilVendorOffer offer) =>
            new(GilVendorReachState.ShopOpen, "Shop open.");
        public void ResetVendorApproach() { }
        public GilVendorShopReadResult ReadShopRows() => GilVendorShopReadResult.Success([new(0, 1, 10)]);
        public bool TrySubmitPurchase(GilVendorShopRow row, uint quantity, out string error)
        {
            SubmitCalls++;
            Counts[row.ItemId] = checked(Counts.GetValueOrDefault(row.ItemId) + (int)quantity);
            Gil -= row.UnitPriceGil * quantity;
            error = string.Empty;
            return true;
        }
        public bool TryConfirmPurchasePrompt() => false;
        public int ResolveMaximumBatch(uint itemId) => 99;
        public void CloseShop() { }
        public void BeginAutomation() { }
        public void EndAutomation() { }
    }
}
