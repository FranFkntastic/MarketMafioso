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
        Assert.Equal(8, engineLine.TargetTotalQuantity);
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
    public void Coordinator_messages_are_projected_in_workshop_wording()
    {
        var runtime = new FakeRuntime();
        var config = new Configuration();
        runtime.Config = config;
        var runner = new WorkshopVendorRestockRunner(config, runtime, new FakeQuartermaster(), () => { });
        var material = Material(1, required: 2, player: 0, retainer: 0, vendor: 2);
        var review = Review(material, [Stop(material)]);

        Assert.True(runner.TryStart(review, Owner, true, out var error), error);
        Assert.Equal("Workshop restock started.", runner.ActiveRun!.Message);
        runner.Tick(review.QueueSignature, Owner);
        runner.Tick(review.QueueSignature, Owner);
        runner.Tick(review.QueueSignature, Owner);

        Assert.Equal("Validated 1 material line(s) at Vendor 100.", runner.ActiveRun!.Message);
    }

    [Fact]
    public void Coordinator_precondition_failure_uses_reviewed_workshop_wording()
    {
        var runtime = new FakeRuntime();
        var config = new Configuration();
        runtime.Config = config;
        var runner = new WorkshopVendorRestockRunner(config, runtime, new FakeQuartermaster(), () => { });
        var material = Material(1, required: 2, player: 0, retainer: 0, vendor: 2);
        var review = Review(material, [Stop(material)]);

        Assert.True(runner.TryStart(review, Owner, true, out var error), error);
        runtime.Gil = 0;
        runner.Tick(review.QueueSignature, Owner);

        Assert.Equal(
            "Remaining reviewed purchases require up to 20 gil, but only 0 gil is available.",
            runner.ActiveRun!.Message);
    }

    [Fact]
    public void Coordinator_start_failure_uses_reviewed_vendor_plan_wording()
    {
        var runtime = new FakeRuntime();
        runtime.CaptureGils.Enqueue(100);
        runtime.CaptureGils.Enqueue(100);
        runtime.CaptureGils.Enqueue(0);
        var config = new Configuration();
        runtime.Config = config;
        var runner = new WorkshopVendorRestockRunner(config, runtime, new FakeQuartermaster(), () => { });
        var material = Material(1, required: 2, player: 0, retainer: 0, vendor: 2);

        Assert.False(runner.TryStart(Review(material, [Stop(material)]), Owner, true, out var error));
        Assert.Equal("The reviewed vendor plan requires up to 20 gil, but only 0 gil is available.", error);
    }

    [Fact]
    public void Coordinator_fallback_message_continues_the_restock_plan()
    {
        var runtime = new FakeRuntime();
        runtime.ReachResults.Enqueue(new(GilVendorReachState.Unavailable, "Vendor unavailable."));
        var config = new Configuration();
        runtime.Config = config;
        var runner = new WorkshopVendorRestockRunner(config, runtime, new FakeQuartermaster(), () => { });
        var material = Material(1, required: 2, player: 0, retainer: 0, vendor: 2);
        var review = Review(material, [Stop(material)]);

        Assert.True(runner.TryStart(review, Owner, true, out var error), error);
        runner.Tick(review.QueueSignature, Owner);
        runner.Tick(review.QueueSignature, Owner);

        Assert.Equal(
            "Skipped Item 1 because no reviewed accessible vendor remains; continuing the restock plan.",
            runner.ActiveRun!.Message);
    }

    [Fact]
    public void Workshop_target_buys_full_approved_delta_against_existing_stock()
    {
        var runtime = new FakeRuntime();
        runtime.Counts[1] = 4;
        var config = new Configuration();
        runtime.Config = config;
        var runner = new WorkshopVendorRestockRunner(config, runtime, new FakeQuartermaster(), () => { });
        var material = Material(1, required: 10, player: 4, retainer: 0, vendor: 6);
        var review = Review(material, [Stop(material)]);

        Assert.True(runner.TryStart(review, Owner, true, out var error), error);
        Assert.Equal(10, Assert.Single(config.ActiveWorkshopVendorBuyRun!.Lines).TargetTotalQuantity);
        runner.Tick(review.QueueSignature, Owner);
        runner.Tick(review.QueueSignature, Owner);
        runner.Tick(review.QueueSignature, Owner);
        runner.Tick(review.QueueSignature, Owner);

        Assert.Equal([6], runtime.SubmittedQuantities);
    }

    [Fact]
    public void Mid_run_stock_gain_reduces_later_workshop_batch()
    {
        var runtime = new FakeRuntime { MaximumBatch = 3 };
        runtime.Counts[1] = 4;
        var config = new Configuration();
        runtime.Config = config;
        var runner = new WorkshopVendorRestockRunner(config, runtime, new FakeQuartermaster(), () => { });
        var material = Material(1, required: 10, player: 4, retainer: 0, vendor: 6);
        var review = Review(material, [Stop(material)]);

        Assert.True(runner.TryStart(review, Owner, true, out var error), error);
        runner.Tick(review.QueueSignature, Owner);
        runner.Tick(review.QueueSignature, Owner);
        runner.Tick(review.QueueSignature, Owner);
        runner.Tick(review.QueueSignature, Owner); // buy 3: observed 4 -> 7
        runner.Tick(review.QueueSignature, Owner); // verify first receipt
        runtime.Counts[1] += 2; // unrelated stock gain: observed 7 -> 9
        runner.Tick(review.QueueSignature, Owner);

        Assert.Equal([3, 1], runtime.SubmittedQuantities);
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
    public void Partial_quartermaster_stock_uses_live_need_for_post_retrieval_gil_and_capacity_preflight()
    {
        var runtime = new FakeRuntime();
        runtime.Counts[1] = 1;
        runtime.CaptureGils.Enqueue(60);
        runtime.CaptureGils.Enqueue(30);
        runtime.CaptureGils.Enqueue(30);
        var quartermaster = new FakeQuartermaster
        {
            Progress = new(WorkshopQuartermasterProgressState.PartiallySucceeded, "Quartermaster finished partially."),
        };
        var config = new Configuration();
        runtime.Config = config;
        var runner = new WorkshopVendorRestockRunner(config, runtime, quartermaster, () => { });
        var material = Material(1, required: 10, player: 1, retainer: 3, vendor: 6);
        var review = Review(material, [Stop(material)]);

        Assert.True(runner.TryStart(review, Owner, true, out var error), error);
        runner.Tick(review.QueueSignature, Owner);
        runtime.Counts[1] = 7;
        runner.Tick(review.QueueSignature, Owner);
        runner.Tick(review.QueueSignature, Owner);

        Assert.True(runner.IsRunning);
        Assert.Equal(GilVendorBuyPhase.RefreshPreconditions, config.ActiveWorkshopVendorBuyRun!.Phase);
        Assert.Equal([6, 3, 3], runtime.CapacityRequests.Select(request => request[1]));
        Assert.Equal(60UL, config.ActiveWorkshopVendorBuyRun.MaximumApprovedGil);
        Assert.Equal(60UL, Assert.Single(config.ActiveWorkshopVendorBuyRun.Lines).ApprovedGilCeiling);
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
            Assert.Equal(2, Assert.Single(config.ActiveWorkshopVendorBuyRun.Lines).TargetTotalQuantity);
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

    [Fact]
    public void Persisted_indeterminate_armed_purchase_migrates_and_reconciles_without_resubmission()
    {
        var offer = GilVendorBuyOfferSnapshot.From(Offer(1));
        var config = new Configuration
        {
            ActiveWorkshopVendorRestockState = new()
            {
                LocalContentId = 10,
                HomeWorldId = 20,
                CharacterName = "Tester",
                QueueSignature = "QUEUE",
                AutomaticallyBuyVendorMaterials = true,
                Phase = WorkshopVendorRestockPhase.Indeterminate,
                Message = "Legacy indeterminate receipt.",
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
                        Offer = offer,
                    },
                ],
                Stops = [new() { NpcId = 100, ShopId = 50, TerritoryId = 129, NpcName = "Vendor 100", ItemIds = [1] }],
            },
            ActiveWorkshopVendorBuyRun = new()
            {
                RunId = "persisted-run",
                ContextSignature = "10|20|QUEUE",
                MaximumApprovedGil = 20,
                Phase = GilVendorBuyPhase.Indeterminate,
                Message = "Observed item delta 0 and exact gil delta.",
                StartedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                UpdatedAtUtc = DateTime.UtcNow,
                Lines =
                [
                    new()
                    {
                        ItemId = 1,
                        ItemName = "Item 1",
                        ApprovedQuantity = 2,
                        TargetTotalQuantity = 2,
                        UnitPriceGil = 10,
                        ApprovedGilCeiling = 20,
                        Offer = offer,
                    },
                ],
                Stops = [new() { NpcId = 100, ShopId = 50, TerritoryId = 129, NpcName = "Vendor 100", ItemIds = [1] }],
                ArmedPurchase = new()
                {
                    ItemId = 1,
                    Quantity = 2,
                    ExpectedGil = 20,
                    ShopRowIndex = 0,
                    BeforeItemCount = 0,
                    BeforeGil = 1_000,
                    ArmedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                },
                Receipts =
                [
                    new()
                    {
                        ItemId = 9,
                        Quantity = 3,
                        SpentGil = 30,
                        BeforeItemCount = 0,
                        AfterItemCount = 3,
                        BeforeGil = 1_030,
                        AfterGil = 1_000,
                        VerifiedAtUtc = DateTime.UtcNow.AddMinutes(-2),
                    },
                ],
            },
        };
        var runtime = new FakeRuntime { Config = config, Gil = 980 };
        runtime.Counts[1] = 2;
        using var runner = new WorkshopVendorRestockRunner(config, runtime, new FakeQuartermaster(), () => { });

        Assert.True(runner.IsRunning);
        Assert.Equal(WorkshopVendorRestockPhase.ReconcileReceipt, runner.ActiveRun!.Phase);
        runner.Tick("QUEUE", Owner);

        Assert.Equal(WorkshopVendorRestockPhase.Completed, runner.ActiveRun!.Phase);
        Assert.Equal(2, config.ActiveWorkshopVendorBuyRun!.Receipts.Count);
        Assert.Equal(9u, config.ActiveWorkshopVendorBuyRun.Receipts[0].ItemId);
        Assert.Null(config.ActiveWorkshopVendorBuyRun.ArmedPurchase);
        Assert.Equal(0, runtime.SubmitCalls);
    }

    [Fact]
    public void Legacy_conversion_save_failure_retains_old_authority_and_later_retry_converts_once()
    {
        var config = LegacyArmedConfiguration();
        var runtime = new FakeRuntime { Config = config };
        var saveAttempts = 0;

        Assert.Throws<InvalidOperationException>(() => new WorkshopVendorRestockRunner(
            config,
            runtime,
            new FakeQuartermaster(),
            () =>
            {
                saveAttempts++;
                throw new InvalidOperationException("Synthetic save failure.");
            }));

        Assert.NotNull(config.LegacyActiveWorkshopVendorRestock);
        Assert.Null(config.ActiveWorkshopVendorRestockState);
        Assert.Null(config.ActiveWorkshopVendorBuyRun);
        Assert.Equal(0, config.WorkshopVendorRestockLegacyConversions);

        using var retried = new WorkshopVendorRestockRunner(
            config,
            runtime,
            new FakeQuartermaster(),
            () => saveAttempts++);

        Assert.Null(config.LegacyActiveWorkshopVendorRestock);
        Assert.NotNull(config.ActiveWorkshopVendorBuyRun);
        Assert.Equal(1, config.WorkshopVendorRestockLegacyConversions);
        Assert.Equal(2, saveAttempts);
    }

    [Fact]
    public void Throwing_conversion_diagnostic_cannot_leave_legacy_authority_for_double_conversion()
    {
        var config = LegacyArmedConfiguration();
        var runtime = new FakeRuntime { Config = config };
        var saveAttempts = 0;

        Assert.Throws<InvalidOperationException>(() => new WorkshopVendorRestockRunner(
            config,
            runtime,
            new FakeQuartermaster(),
            () => saveAttempts++,
            _ => throw new InvalidOperationException("Synthetic diagnostic failure.")));

        Assert.Null(config.LegacyActiveWorkshopVendorRestock);
        Assert.Equal(1, config.WorkshopVendorRestockLegacyConversions);
        using var retried = new WorkshopVendorRestockRunner(
            config,
            runtime,
            new FakeQuartermaster(),
            () => saveAttempts++);
        Assert.Equal(1, config.WorkshopVendorRestockLegacyConversions);
        Assert.Equal(1, saveAttempts);
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
        public int MaximumBatch { get; set; } = 99;
        public List<int> SubmittedQuantities { get; } = [];
        public Queue<ulong> CaptureGils { get; } = [];
        public Queue<GilVendorReachResult> ReachResults { get; } = [];
        public List<IReadOnlyDictionary<uint, int>> CapacityRequests { get; } = [];

        public GilVendorInventorySnapshot CaptureInventory(IReadOnlyCollection<uint> itemIds) =>
            new(
                true,
                CaptureGils.Count > 0 ? CaptureGils.Dequeue() : Gil,
                itemIds.ToDictionary(id => id, id => Counts.GetValueOrDefault(id)),
                "Inventory ready.");
        public bool HasCapacity(IReadOnlyDictionary<uint, int> quantities, out string message)
        {
            CapacityRequests.Add(new Dictionary<uint, int>(quantities));
            message = "Capacity ready.";
            return true;
        }
        public GilVendorReachResult AdvanceToOpenShop(GilVendorOffer offer) =>
            ReachResults.Count > 0
                ? ReachResults.Dequeue()
                : new(GilVendorReachState.ShopOpen, "Shop open.");
        public void ResetVendorApproach() { }
        public GilVendorShopReadResult ReadShopRows() => GilVendorShopReadResult.Success([new(0, 1, 10)]);
        public bool TrySubmitPurchase(GilVendorShopRow row, uint quantity, out string error)
        {
            SubmitCalls++;
            SubmittedQuantities.Add(checked((int)quantity));
            Counts[row.ItemId] = checked(Counts.GetValueOrDefault(row.ItemId) + (int)quantity);
            Gil -= row.UnitPriceGil * quantity;
            error = string.Empty;
            return true;
        }
        public bool TryConfirmPurchasePrompt() => false;
        public int ResolveMaximumBatch(uint itemId) => MaximumBatch;
        public void CloseShop() { }
        public void BeginAutomation() { }
        public void EndAutomation() { }
    }
}
