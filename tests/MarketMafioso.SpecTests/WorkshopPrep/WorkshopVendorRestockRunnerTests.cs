using Franthropy.Dalamud.Automation.Vendors;
using Franthropy.Dalamud.Travel;
using MarketMafioso.Quartermaster;
using MarketMafioso.WorkshopPrep;
using System.Numerics;

namespace MarketMafioso.SpecTests.WorkshopPrep;

public sealed class WorkshopVendorRestockRunnerTests
{
    private static readonly QuartermasterOwnerScope Owner = new(10, 20, "Tester", "World");

    [Theory]
    [InlineData(RunnerScenario.DisabledToggle)]
    [InlineData(RunnerScenario.SameVendorBatch)]
    [InlineData(RunnerScenario.PartialQuartermaster)]
    [InlineData(RunnerScenario.AmbiguousEvidence)]
    [InlineData(RunnerScenario.StableQueueSignature)]
    [InlineData(RunnerScenario.AlternativeVendor)]
    [InlineData(RunnerScenario.AtomicShopValidation)]
    [InlineData(RunnerScenario.SplitLargeQuantity)]
    [InlineData(RunnerScenario.CapacityLoss)]
    [InlineData(RunnerScenario.ReloadReconciliation)]
    [InlineData(RunnerScenario.IdentityDrift)]
    [InlineData(RunnerScenario.ArmedStopReconciliation)]
    [InlineData(RunnerScenario.UnreachableFailure)]
    [InlineData(RunnerScenario.ApproachPolicy)]
    public void Runner_contract(RunnerScenario scenario)
    {
        switch (scenario)
        {
            case RunnerScenario.DisabledToggle: Disabled_vendor_toggle_creates_no_vendor_authority(); break;
            case RunnerScenario.SameVendorBatch: Same_vendor_lines_open_and_read_shop_once_and_commit_exact_receipts(); break;
            case RunnerScenario.PartialQuartermaster: Partial_quartermaster_result_never_expands_reviewed_vendor_ceiling(); break;
            case RunnerScenario.AmbiguousEvidence: Ambiguous_inventory_and_gil_evidence_stops_without_retry(); break;
            case RunnerScenario.StableQueueSignature: Queue_signature_ignores_inventory_refresh_but_changes_with_requirements(); break;
            case RunnerScenario.AlternativeVendor: Unavailable_stop_replans_to_a_reviewed_alternative_without_expanding_quantity(); break;
            case RunnerScenario.AtomicShopValidation: Every_stop_line_is_validated_before_the_first_callback(); break;
            case RunnerScenario.SplitLargeQuantity: Quantity_above_shop_limit_splits_without_reopening_or_rereading(); break;
            case RunnerScenario.CapacityLoss: Capacity_loss_after_start_pauses_before_vendor_mutation(); break;
            case RunnerScenario.ReloadReconciliation: Reload_reconciles_an_exact_armed_purchase_before_continuing(); break;
            case RunnerScenario.IdentityDrift: Owner_or_queue_drift_pauses_before_any_external_action(); break;
            case RunnerScenario.ArmedStopReconciliation: Purchase_is_persisted_as_verifying_before_the_callback_and_stop_reconciles_it(); break;
            case RunnerScenario.UnreachableFailure: Unreachable_vendor_is_skipped_without_blocking_later_stops(); break;
            case RunnerScenario.ApproachPolicy: Vendor_approach_requires_walking_before_interaction(); break;
            default: throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }
    }

    [Fact]
    public void Persisted_offer_preserves_an_aetheryte_plus_aethernet_route()
    {
        var offer = Offer(1) with
        {
            TravelRoutes = [new GilVendorTravelRoute(9, 17, 130)],
        };

        var restored = PersistedGilVendorOffer.From(offer).ToOffer();

        Assert.Equal([new GilVendorTravelRoute(9, 17, 130)], restored.EffectiveTravelRoutes);
    }

    [Fact]
    public void Aethernet_leg_waits_for_main_aetheryte_arrival()
    {
        Assert.Equal(
            WorkshopVendorTravelLeg.AwaitAetheryteArrival,
            DalamudWorkshopVendorRestockRuntime.DetermineTravelLeg(
                currentTerritoryId: 129,
                targetTerritoryId: 131,
                routeAetheryteId: 9,
                routeAethernetId: 3,
                routeAetheryteTerritoryId: 130,
                requestedAetheryteId: 9,
                requestedAethernetId: null));
        Assert.Equal(
            WorkshopVendorTravelLeg.SubmitAethernet,
            DalamudWorkshopVendorRestockRuntime.DetermineTravelLeg(
                currentTerritoryId: 130,
                targetTerritoryId: 131,
                routeAetheryteId: 9,
                routeAethernetId: 3,
                routeAetheryteTerritoryId: 130,
                requestedAetheryteId: 9,
                requestedAethernetId: null));
    }

    [Fact]
    public void Aethernet_route_without_arrival_territory_fails_closed()
    {
        Assert.Equal(
            WorkshopVendorTravelLeg.InvalidRoute,
            DalamudWorkshopVendorRestockRuntime.DetermineTravelLeg(
                currentTerritoryId: 129,
                targetTerritoryId: 131,
                routeAetheryteId: 9,
                routeAethernetId: 3,
                routeAetheryteTerritoryId: null,
                requestedAetheryteId: null,
                requestedAethernetId: null));
    }

    [Fact]
    public void Direct_aetheryte_route_waits_for_target_without_an_aethernet_gate()
    {
        Assert.Equal(
            WorkshopVendorTravelLeg.SubmitAetheryte,
            DalamudWorkshopVendorRestockRuntime.DetermineTravelLeg(
                currentTerritoryId: 129,
                targetTerritoryId: 131,
                routeAetheryteId: 9,
                routeAethernetId: null,
                routeAetheryteTerritoryId: null,
                requestedAetheryteId: null,
                requestedAethernetId: null));
        Assert.Equal(
            WorkshopVendorTravelLeg.AwaitDestination,
            DalamudWorkshopVendorRestockRuntime.DetermineTravelLeg(
                currentTerritoryId: 129,
                targetTerritoryId: 131,
                routeAetheryteId: 9,
                routeAethernetId: null,
                routeAetheryteTerritoryId: null,
                requestedAetheryteId: 9,
                requestedAethernetId: null));
    }

    [Fact]
    public void Pending_vendor_travel_waits_through_its_quest_ui_owner()
    {
        var readiness = new TravelReadinessResult(
            TravelReadinessState.Blocked,
            "UnknownUiOwner",
            "A quest or NPC interaction still owns the game UI after owned surfaces were released.");

        Assert.True(DalamudWorkshopVendorRestockRuntime.ShouldWaitForPendingTravelUi(readiness, true));
        Assert.False(DalamudWorkshopVendorRestockRuntime.ShouldWaitForPendingTravelUi(readiness, false));
        Assert.False(DalamudWorkshopVendorRestockRuntime.ShouldWaitForPendingTravelUi(
            readiness with { Code = "InCombat" },
            true));
    }

    private void Disabled_vendor_toggle_creates_no_vendor_authority()
    {
        var runtime = new FakeRuntime();
        var config = new Configuration();
        var runner = new WorkshopVendorRestockRunner(config, runtime, () => { });
        var review = Review(
            Material(1, required: 10, player: 0, retainer: 4, vendor: 6),
            [Stop(1)]);

        Assert.True(runner.TryStart(review, Owner, false, out var error), error);
        TickUntilTerminal(runner, review.QueueSignature, 10);

        Assert.Equal(WorkshopVendorRestockPhase.Completed, runner.ActiveRun!.Phase);
        Assert.Equal(0, runtime.ReachCalls);
        Assert.Equal(0, runtime.ShopReadCalls);
        Assert.Equal(0, runtime.SubmitCalls);
    }

    private void Same_vendor_lines_open_and_read_shop_once_and_commit_exact_receipts()
    {
        var runtime = new FakeRuntime();
        runtime.Counts[1] = 0;
        runtime.Counts[2] = 0;
        var config = new Configuration();
        var runner = new WorkshopVendorRestockRunner(config, runtime, () => { });
        var review = Review(
            [
                Material(1, required: 3, player: 0, retainer: 0, vendor: 3),
                Material(2, required: 2, player: 0, retainer: 0, vendor: 2),
            ],
            [Stop(1, 2)]);

        Assert.True(runner.TryStart(review, Owner, true, out var error), error);
        TickUntilTerminal(runner, review.QueueSignature, 30);

        Assert.Equal(WorkshopVendorRestockPhase.Completed, runner.ActiveRun!.Phase);
        Assert.Equal(1, runtime.ReachCalls);
        Assert.Equal(1, runtime.ShopReadCalls);
        Assert.Equal(2, runtime.SubmitCalls);
        Assert.Equal(2, runner.ActiveRun.Receipts.Count);
        Assert.Equal(5, runner.ActiveRun.Receipts.Sum(receipt => receipt.Quantity));
    }

    private void Partial_quartermaster_result_never_expands_reviewed_vendor_ceiling()
    {
        var runtime = new FakeRuntime();
        runtime.Counts[1] = 2;
        runtime.QuartermasterInventoryDelta = 0;
        var config = new Configuration();
        var runner = new WorkshopVendorRestockRunner(config, runtime, () => { });
        var material = Material(1, required: 20, player: 2, retainer: 3, vendor: 15);
        var review = Review(material, [Stop(1)]);

        Assert.True(runner.TryStart(review, Owner, true, out var error), error);
        TickUntilTerminal(runner, review.QueueSignature, 30);

        Assert.Equal(15, runner.ActiveRun!.Receipts.Sum(receipt => receipt.Quantity));
        Assert.Equal(17, runtime.Counts[1]);
        Assert.Equal("Ceiling reached", runner.ActiveRun.Lines[0].Status);
    }

    private void Ambiguous_inventory_and_gil_evidence_stops_without_retry()
    {
        var runtime = new FakeRuntime { MutateGilOnSubmit = false };
        runtime.Counts[1] = 0;
        var config = new Configuration();
        var runner = new WorkshopVendorRestockRunner(config, runtime, () => { });
        var review = Review(Material(1, required: 2, player: 0, retainer: 0, vendor: 2), [Stop(1)]);

        Assert.True(runner.TryStart(review, Owner, true, out var error), error);
        TickUntilTerminal(runner, review.QueueSignature, 15);

        Assert.Equal(WorkshopVendorRestockPhase.Indeterminate, runner.ActiveRun!.Phase);
        Assert.Equal(1, runtime.SubmitCalls);
        Assert.Empty(runner.ActiveRun.Receipts);
    }

    private void Queue_signature_ignores_inventory_refresh_but_changes_with_requirements()
    {
        var planner = Planner();
        var first = planner.Build(
            [Availability(1, 10, 0, 2)],
            new Dictionary<uint, int>(),
            new HashSet<uint>(),
            new HashSet<uint>());
        var refreshed = planner.Build(
            [Availability(1, 10, 7, 1)],
            new Dictionary<uint, int>(),
            new HashSet<uint>(),
            new HashSet<uint>());
        var changed = planner.Build(
            [Availability(1, 11, 7, 1)],
            new Dictionary<uint, int>(),
            new HashSet<uint>(),
            new HashSet<uint>());

        Assert.Equal(first.QueueSignature, refreshed.QueueSignature);
        Assert.NotEqual(first.QueueSignature, changed.QueueSignature);
    }

    private void Unavailable_stop_replans_to_a_reviewed_alternative_without_expanding_quantity()
    {
        var runtime = new FakeRuntime();
        runtime.Counts[1] = 0;
        runtime.ReachResults.Enqueue(new(WorkshopVendorReachState.Unavailable, "First vendor unavailable."));
        runtime.ReachResults.Enqueue(new(WorkshopVendorReachState.ShopOpen, "Alternative shop open."));
        var first = new WorkshopVendorCandidate(
            Offer(1, 100),
            new(GilVendorAccessState.Probeable, "test", "Probeable."));
        var alternative = new WorkshopVendorCandidate(
            Offer(1, 200),
            new(GilVendorAccessState.Verified, "test", "Verified."));
        var material = new WorkshopMaterialProcurement(
            Availability(1, 4, 0, 0),
            0,
            4,
            [first, alternative],
            first,
            false,
            true,
            4);
        var review = Review(material, [new(100, 50, 129, "First Vendor", [material])]);
        var runner = new WorkshopVendorRestockRunner(new Configuration(), runtime, () => { });

        Assert.True(runner.TryStart(review, Owner, true, out var error), error);
        TickUntilTerminal(runner, review.QueueSignature, 30);

        Assert.Equal(WorkshopVendorRestockPhase.Completed, runner.ActiveRun!.Phase);
        Assert.Equal(4, runner.ActiveRun.Receipts.Sum(receipt => receipt.Quantity));
        Assert.Equal(200u, runner.ActiveRun.Lines[0].Offer!.NpcId);
        Assert.Equal(2, runtime.ReachCalls);
    }

    private void Every_stop_line_is_validated_before_the_first_callback()
    {
        var runtime = new FakeRuntime
        {
            ShopRows = [new(0, 1, 10)],
        };
        var review = Review(
            [
                Material(1, required: 1, player: 0, retainer: 0, vendor: 1),
                Material(2, required: 1, player: 0, retainer: 0, vendor: 1),
            ],
            [Stop(1, 2)]);
        var runner = new WorkshopVendorRestockRunner(new Configuration(), runtime, () => { });

        Assert.True(runner.TryStart(review, Owner, true, out var error), error);
        TickUntilTerminal(runner, review.QueueSignature, 10);

        Assert.Equal(WorkshopVendorRestockPhase.Failed, runner.ActiveRun!.Phase);
        Assert.Equal(1, runtime.ShopReadCalls);
        Assert.Equal(0, runtime.SubmitCalls);
    }

    private void Unreachable_vendor_is_skipped_without_blocking_later_stops()
    {
        var runtime = new FakeRuntime();
        runtime.ReachResults.Enqueue(new(
            WorkshopVendorReachState.Unavailable,
            "The route timed out."));
        runtime.ReachResults.Enqueue(new(
            WorkshopVendorReachState.ShopOpen,
            "Later shop opened."));
        var first = Material(1, required: 1, player: 0, retainer: 0, vendor: 1);
        var later = Material(2, required: 1, player: 0, retainer: 0, vendor: 1);
        var review = Review([first, later], [Stop(1), Stop(2)]);
        var runner = new WorkshopVendorRestockRunner(new Configuration(), runtime, () => { });

        Assert.True(runner.TryStart(review, Owner, true, out var error), error);
        TickUntilTerminal(runner, review.QueueSignature, 10);

        Assert.Equal(WorkshopVendorRestockPhase.Completed, runner.ActiveRun!.Phase);
        Assert.True(runner.ActiveRun.Lines.Single(line => line.ItemId == 1).VendorUnavailable);
        Assert.Equal("No accessible vendor", runner.ActiveRun.Lines.Single(line => line.ItemId == 1).Status);
        Assert.Equal([2u], runner.ActiveRun.Receipts.Select(receipt => receipt.ItemId));
        Assert.Equal(2, runtime.ReachCalls);
        Assert.Equal(1, runtime.SubmitCalls);
    }

    private static void Vendor_approach_requires_walking_before_interaction()
    {
        Assert.Equal(
            WorkshopVendorApproachDecision.Interact,
            DalamudWorkshopVendorRestockRuntime.DecideApproach(3.5f, true, true, false, false));
        Assert.Equal(
            WorkshopVendorApproachDecision.WaitForNpc,
            DalamudWorkshopVendorRestockRuntime.DecideApproach(3.5f, false, true, false, false));
        Assert.Equal(
            WorkshopVendorApproachDecision.StartNavigation,
            DalamudWorkshopVendorRestockRuntime.DecideApproach(18f, false, true, false, false));
        Assert.Equal(
            WorkshopVendorApproachDecision.WaitForOwnedRoute,
            DalamudWorkshopVendorRestockRuntime.DecideApproach(18f, false, true, true, true));
        Assert.Equal(
            WorkshopVendorApproachDecision.BlockedByAnotherRoute,
            DalamudWorkshopVendorRestockRuntime.DecideApproach(18f, false, true, true, false));
        Assert.Equal(
            WorkshopVendorApproachDecision.NavigationUnavailable,
            DalamudWorkshopVendorRestockRuntime.DecideApproach(18f, false, false, false, false));
    }

    private void Quantity_above_shop_limit_splits_without_reopening_or_rereading()
    {
        var runtime = new FakeRuntime();
        var review = Review(
            Material(1, required: 120, player: 0, retainer: 0, vendor: 120),
            [Stop(1)]);
        var runner = new WorkshopVendorRestockRunner(new Configuration(), runtime, () => { });

        Assert.True(runner.TryStart(review, Owner, true, out var error), error);
        TickUntilTerminal(runner, review.QueueSignature, 20);

        Assert.Equal([99, 21], runner.ActiveRun!.Receipts.Select(receipt => receipt.Quantity));
        Assert.Equal(2, runtime.SubmitCalls);
        Assert.Equal(1, runtime.ShopReadCalls);
        Assert.Equal(1, runtime.ReachCalls);
    }

    private void Capacity_loss_after_start_pauses_before_vendor_mutation()
    {
        var runtime = new FakeRuntime();
        runtime.CapacityResults.Enqueue(true);
        runtime.CapacityResults.Enqueue(false);
        var review = Review(
            Material(1, required: 2, player: 0, retainer: 0, vendor: 2),
            [Stop(1)]);
        var runner = new WorkshopVendorRestockRunner(new Configuration(), runtime, () => { });

        Assert.True(runner.TryStart(review, Owner, true, out var error), error);
        runner.Tick(review.QueueSignature, Owner);

        Assert.Equal(WorkshopVendorRestockPhase.Paused, runner.ActiveRun!.Phase);
        Assert.Equal(0, runtime.SubmitCalls);
    }

    private void Reload_reconciles_an_exact_armed_purchase_before_continuing()
    {
        var offer = Offer(1);
        var config = new Configuration
        {
            ActiveWorkshopVendorRestock = new PersistedWorkshopVendorRestockRun
            {
                RunId = "run",
                LocalContentId = 10,
                HomeWorldId = 20,
                CharacterName = "Tester",
                QueueSignature = "QUEUE",
                AutomaticallyBuyVendorMaterials = true,
                MaximumApprovedGil = 20,
                Phase = WorkshopVendorRestockPhase.VerifyReceipt,
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
                        Offer = PersistedGilVendorOffer.From(offer),
                    },
                ],
                Stops =
                [
                    new()
                    {
                        NpcId = 100,
                        ShopId = 50,
                        TerritoryId = 129,
                        NpcName = "Vendor",
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
        var runtime = new FakeRuntime { Gil = 980 };
        runtime.Counts[1] = 2;
        var runner = new WorkshopVendorRestockRunner(config, runtime, () => { });

        runner.Tick("QUEUE", Owner);

        Assert.Single(runner.ActiveRun!.Receipts);
        Assert.Null(runner.ActiveRun.ArmedPurchase);
        Assert.Equal(WorkshopVendorRestockPhase.PurchaseLine, runner.ActiveRun.Phase);
        Assert.Equal(0, runtime.SubmitCalls);
    }

    private void Owner_or_queue_drift_pauses_before_any_external_action()
    {
        var runtime = new FakeRuntime();
        var review = Review(
            Material(1, required: 2, player: 0, retainer: 0, vendor: 2),
            [Stop(1)]);
        var runner = new WorkshopVendorRestockRunner(new Configuration(), runtime, () => { });
        Assert.True(runner.TryStart(review, Owner, true, out var error), error);

        runner.Tick("DIFFERENT", Owner);

        Assert.Equal(WorkshopVendorRestockPhase.Paused, runner.ActiveRun!.Phase);
        Assert.Equal(0, runtime.ReachCalls);
        Assert.Equal(0, runtime.SubmitCalls);
    }

    private void Purchase_is_persisted_as_verifying_before_the_callback_and_stop_reconciles_it()
    {
        var runtime = new FakeRuntime();
        var config = new Configuration();
        runtime.OnSubmit = () =>
        {
            Assert.Equal(
                WorkshopVendorRestockPhase.VerifyReceipt,
                config.ActiveWorkshopVendorRestock!.Phase);
            Assert.NotNull(config.ActiveWorkshopVendorRestock.ArmedPurchase);
        };
        var review = Review(
            Material(1, required: 2, player: 0, retainer: 0, vendor: 2),
            [Stop(1)]);
        var runner = new WorkshopVendorRestockRunner(config, runtime, () => { });
        Assert.True(runner.TryStart(review, Owner, true, out var error), error);
        while (runner.ActiveRun!.Phase != WorkshopVendorRestockPhase.VerifyReceipt)
            runner.Tick(review.QueueSignature, Owner);

        Assert.True(runner.Stop());
        Assert.True(runner.ActiveRun.StopRequested);
        runner.Tick(review.QueueSignature, Owner);

        Assert.Equal(WorkshopVendorRestockPhase.Stopped, runner.ActiveRun.Phase);
        Assert.Single(runner.ActiveRun.Receipts);
        Assert.Null(runner.ActiveRun.ArmedPurchase);
    }

    private static void TickUntilTerminal(
        WorkshopVendorRestockRunner runner,
        string signature,
        int maximumTicks)
    {
        for (var index = 0; index < maximumTicks && runner.IsRunning; index++)
            runner.Tick(signature, Owner);
        Assert.False(runner.IsRunning);
    }

    private static WorkshopVendorProcurementPlanner Planner() => new(
        GilVendorCatalog.Create([Offer(1), Offer(2)]),
        _ => new(GilVendorAccessState.Verified, "test", "Verified for test."),
        _ => false);

    private static WorkshopVendorRestockReview Review(
        WorkshopMaterialProcurement material,
        IReadOnlyList<WorkshopVendorStopReview> stops) =>
        Review([material], stops);

    private static WorkshopVendorRestockReview Review(
        IReadOnlyList<WorkshopMaterialProcurement> materials,
        IReadOnlyList<WorkshopVendorStopReview> stops) =>
        new("QUEUE", materials, stops);

    private static WorkshopMaterialProcurement Material(
        uint itemId,
        int required,
        int player,
        int retainer,
        int vendor)
    {
        var availability = Availability(itemId, required, player, retainer);
        var candidate = new WorkshopVendorCandidate(
            Offer(itemId),
            new(GilVendorAccessState.Verified, "test", "Verified for test."));
        return new(
            availability,
            retainer,
            vendor,
            [candidate],
            candidate,
            false,
            true,
            vendor);
    }

    private static WorkshopVendorStopReview Stop(params uint[] itemIds) =>
        new(
            100,
            50,
            129,
            "Shared Vendor",
            itemIds.Select(itemId => Material(itemId, 10, 0, 0, 10)).ToArray());

    private static WorkshopMaterialAvailability Availability(
        uint itemId,
        int required,
        int player,
        int retainer)
    {
        var shortage = Math.Max(0, required - player);
        return new(
            itemId,
            $"Item {itemId}",
            1,
            required,
            player,
            retainer,
            shortage,
            Math.Max(0, shortage - retainer),
            []);
    }

    private static GilVendorOffer Offer(uint itemId, uint npcId = 100) =>
        new(
            itemId,
            $"Item {itemId}",
            1,
            itemId == 1 ? 10u : 20u,
            50,
            itemId - 1,
            npcId,
            $"Vendor {npcId}",
            129,
            new Vector3(1, 2, 3),
            [2]);

    private sealed class FakeRuntime : IWorkshopVendorRestockRuntime
    {
        public Dictionary<uint, int> Counts { get; } = [];
        public ulong Gil { get; set; } = 1_000_000;
        public int ReachCalls { get; private set; }
        public int ShopReadCalls { get; private set; }
        public int SubmitCalls { get; private set; }
        public int QuartermasterInventoryDelta { get; set; }
        public bool MutateGilOnSubmit { get; set; } = true;
        public Queue<WorkshopVendorReachResult> ReachResults { get; } = [];
        public Queue<bool> CapacityResults { get; } = [];
        public IReadOnlyList<GilVendorShopRow> ShopRows { get; set; } =
        [
            new(0, 1, 10),
            new(1, 2, 20),
        ];
        public Action? OnSubmit { get; set; }

        public WorkshopVendorInventorySnapshot CaptureInventory(IReadOnlyCollection<uint> itemIds) =>
            new(
                true,
                Gil,
                itemIds.ToDictionary(itemId => itemId, itemId => Counts.GetValueOrDefault(itemId)),
                "Inventory ready.");

        public bool HasCapacity(IReadOnlyDictionary<uint, int> quantities, out string message)
        {
            var result = CapacityResults.Count == 0 || CapacityResults.Dequeue();
            message = result ? "Capacity ready." : "Player inventory has no safe capacity.";
            return result;
        }

        public bool TryStartQuartermaster(
            QuartermasterOwnerScope owner,
            IReadOnlyList<WorkshopMaterialAvailability> availability,
            out string error)
        {
            foreach (var line in availability)
                Counts[line.ItemId] = Counts.GetValueOrDefault(line.ItemId) + QuartermasterInventoryDelta;
            error = string.Empty;
            return true;
        }

        public WorkshopQuartermasterProgress GetQuartermasterProgress(QuartermasterOwnerScope owner) =>
            new(WorkshopQuartermasterProgressState.PartiallySucceeded, "Quartermaster finished.");

        public WorkshopVendorReachResult AdvanceToOpenShop(GilVendorOffer offer)
        {
            ReachCalls++;
            return ReachResults.Count > 0
                ? ReachResults.Dequeue()
                : new(WorkshopVendorReachState.ShopOpen, "Shop open.");
        }

        public void ResetVendorApproach()
        {
        }

        public GilVendorShopReadResult ReadShopRows()
        {
            ShopReadCalls++;
            return GilVendorShopReadResult.Success(ShopRows);
        }

        public bool TrySubmitPurchase(GilVendorShopRow row, uint quantity, out string error)
        {
            SubmitCalls++;
            OnSubmit?.Invoke();
            Counts[row.ItemId] = checked(Counts.GetValueOrDefault(row.ItemId) + (int)quantity);
            if (MutateGilOnSubmit)
                Gil -= row.UnitPriceGil * quantity;
            error = string.Empty;
            return true;
        }

        public bool TryConfirmPurchasePrompt() => false;
        public int ResolveMaximumBatch(uint itemId) => 99;
        public void CloseShop()
        {
        }
        public void BeginAutomation()
        {
        }
        public void EndAutomation()
        {
        }
    }

    public enum RunnerScenario
    {
        DisabledToggle,
        SameVendorBatch,
        PartialQuartermaster,
        AmbiguousEvidence,
        StableQueueSignature,
        AlternativeVendor,
        AtomicShopValidation,
        SplitLargeQuantity,
        CapacityLoss,
        ReloadReconciliation,
        IdentityDrift,
        ArmedStopReconciliation,
        UnreachableFailure,
        ApproachPolicy,
    }
}
