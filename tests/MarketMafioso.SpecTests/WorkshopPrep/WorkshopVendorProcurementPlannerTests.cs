using Franthropy.Dalamud.Automation.Vendors;
using MarketMafioso.Windows.WorkshopLogistics;
using MarketMafioso.WorkshopPrep;
using System.Numerics;

namespace MarketMafioso.SpecTests.WorkshopPrep;

public sealed class WorkshopVendorProcurementPlannerTests
{
    [Theory]
    [InlineData(10, 0, 10, 2)]
    [InlineData(3, 7, 10, 1)]
    [InlineData(3, 2, 10, 0)]
    public void Stock_state_distinguishes_ready_retrievable_and_missing(
        int player,
        int retainer,
        int required,
        int expected)
    {
        var line = Procurement(1, required, player, retainer);

        Assert.Equal(expected, (int)WorkshopMaterialPanel.ResolveStockState(line));
        Assert.Equal($"{player + retainer:N0} / {required:N0}", WorkshopMaterialPanel.BuildStockText(line));
    }

    [Fact]
    public void Default_order_puts_missing_then_retrievable_then_ready()
    {
        var ready = Procurement(1, required: 10, player: 10, retainer: 0);
        var retrievable = Procurement(2, required: 10, player: 3, retainer: 7);
        var missing = Procurement(3, required: 10, player: 3, retainer: 2);

        var ordered = WorkshopMaterialPanel.OrderForDisplay([ready, retrievable, missing]);

        Assert.Equal([3u, 2u, 1u], ordered.Select(line => line.Availability.ItemId));
    }

    [Fact]
    public void Ready_row_keeps_acquisition_quiet_even_when_a_completed_run_line_remains()
    {
        var ready = Procurement(1, required: 10, player: 10, retainer: 0);
        var completed = new PersistedWorkshopVendorRestockLine
        {
            ItemId = 1,
            ItemName = ready.Availability.ItemName,
            Status = "Ready",
        };

        var text = WorkshopMaterialPanel.BuildAcquisitionFilterText(ready, completed);

        Assert.Empty(text);
    }

    [Fact]
    public void Market_acquisition_line_uses_only_the_post_retainer_shortage()
    {
        var line = Procurement(1, required: 20, player: 2, retainer: 3);

        var staged = WorkshopMaterialPanel.CreateMarketAcquisitionLine(line);

        Assert.Equal(1u, staged.ItemId);
        Assert.Equal("TargetQuantity", staged.QuantityMode);
        Assert.Equal(15u, staged.TargetQuantity);
        Assert.Equal("Either", staged.HqPolicy);
        Assert.Equal(0u, staged.MaxUnitPrice);
    }

    [Fact]
    public void Single_line_review_keeps_only_the_requested_material_and_vendor_stop()
    {
        var first = Procurement(1, required: 20, player: 2, retainer: 3);
        var second = Procurement(2, required: 10, player: 1, retainer: 0);
        var stop = new WorkshopVendorStopReview(100, 200, 300, "Vendor", [first, second]);
        var review = new WorkshopVendorRestockReview("queue", [first, second], [stop]);

        var scoped = WorkshopMaterialPanel.BuildSingleLineReview(review, 2);

        Assert.Equal("queue", scoped.QueueSignature);
        Assert.Equal(2u, Assert.Single(scoped.Materials).Availability.ItemId);
        Assert.Equal(2u, Assert.Single(Assert.Single(scoped.Stops).Lines).Availability.ItemId);
    }

    [Theory]
    [InlineData(PlannerScenario.SharedVerifiedStop)]
    [InlineData(PlannerScenario.ClampEditedQuantity)]
    [InlineData(PlannerScenario.ExcludeInaccessibleOffers)]
    [InlineData(PlannerScenario.PreserveUserExclusion)]
    [InlineData(PlannerScenario.CraftableRequiresExplicitInclusion)]
    [InlineData(PlannerScenario.StockDisplayUsesLiveAvailability)]
    public void Planner_contract(PlannerScenario scenario)
    {
        switch (scenario)
        {
            case PlannerScenario.SharedVerifiedStop:
                Planner_prefers_one_verified_vendor_covering_multiple_lines();
                break;
            case PlannerScenario.ClampEditedQuantity:
                Planner_clamps_edited_quantity_to_post_retainer_need();
                break;
            case PlannerScenario.ExcludeInaccessibleOffers:
                Planner_never_selects_unknown_or_unavailable_offers();
                break;
            case PlannerScenario.PreserveUserExclusion:
                Planner_preserves_user_exclusion();
                break;
            case PlannerScenario.CraftableRequiresExplicitInclusion:
                Planner_requires_explicit_inclusion_for_craftable_vendor_items();
                break;
            case PlannerScenario.StockDisplayUsesLiveAvailability:
                Stock_display_uses_live_availability_during_an_active_run();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }
    }

    private static void Stock_display_uses_live_availability_during_an_active_run()
    {
        var line = new WorkshopMaterialProcurement(
            Availability(1, required: 50, player: 25, retainer: 10),
            RetainerPlannedQuantity: 10,
            VendorNeed: 15,
            Candidates: [],
            SelectedCandidate: null,
            IsCraftable: false,
            Selected: false,
            ApprovedVendorQuantity: 0);
        var frozenRunLine = new PersistedWorkshopVendorRestockLine
        {
            ItemId = 1,
            LivePlayerQuantity = 0,
        };

        Assert.Equal(25, WorkshopMaterialPanel.ResolveDisplayedPlayerQuantity(line, frozenRunLine));
    }

    private void Planner_prefers_one_verified_vendor_covering_multiple_lines()
    {
        var shared = Offer(1, 100, 10);
        var catalog = GilVendorCatalog.Create(
        [
            shared,
            Offer(2, 100, 20),
            Offer(1, 200, 8),
        ]);
        var planner = new WorkshopVendorProcurementPlanner(
            catalog,
            offer => new(
                offer.NpcId == 100 ? GilVendorAccessState.Verified : GilVendorAccessState.Probeable,
                "test",
                "test"),
            _ => false);

        var review = planner.Build(
        [
            Availability(1, required: 20, player: 2, retainer: 3),
            Availability(2, required: 10, player: 1, retainer: 4),
        ],
            new Dictionary<uint, int>(),
            new HashSet<uint>(),
            new HashSet<uint>());

        Assert.Single(review.Stops);
        Assert.Equal(100u, review.Stops[0].NpcId);
        Assert.Equal(7, review.RetainerUnits);
        Assert.Equal(20, review.VendorUnits);
        Assert.Equal(250UL, review.MaximumGil);
        Assert.Equal("Restock 27", WorkshopMaterialPanel.BuildActionLabel(review, automatic: true));
        Assert.Equal("Retrieve 7", WorkshopMaterialPanel.BuildActionLabel(review, automatic: false));
    }

    private void Planner_clamps_edited_quantity_to_post_retainer_need()
    {
        var planner = new WorkshopVendorProcurementPlanner(
            GilVendorCatalog.Create([Offer(1, 100, 10)]),
            _ => new(GilVendorAccessState.Probeable, "test", "test"),
            _ => false);

        var review = planner.Build(
            [Availability(1, required: 20, player: 2, retainer: 3)],
            new Dictionary<uint, int> { [1] = 99 },
            new HashSet<uint>(),
            new HashSet<uint>());

        Assert.Equal(15, review.Materials[0].VendorNeed);
        Assert.Equal(15, review.Materials[0].ApprovedVendorQuantity);
    }

    private void Planner_never_selects_unknown_or_unavailable_offers()
    {
        var catalog = GilVendorCatalog.Create([Offer(1, 100, 10)]);
        foreach (var state in new[] { GilVendorAccessState.Unknown, GilVendorAccessState.Unavailable })
        {
            var planner = new WorkshopVendorProcurementPlanner(
                catalog,
                _ => new(state, "test", "test"),
                _ => false);

            var review = planner.Build(
                [Availability(1, required: 20, player: 2, retainer: 3)],
                new Dictionary<uint, int>(),
                new HashSet<uint>(),
                new HashSet<uint>());

            Assert.Empty(review.Stops);
            Assert.Null(review.Materials[0].SelectedCandidate);
        }
    }

    private void Planner_preserves_user_exclusion()
    {
        var planner = new WorkshopVendorProcurementPlanner(
            GilVendorCatalog.Create([Offer(1, 100, 10)]),
            _ => new(GilVendorAccessState.Verified, "test", "test"),
            _ => false);

        var review = planner.Build(
            [Availability(1, required: 20, player: 2, retainer: 3)],
            new Dictionary<uint, int>(),
            new HashSet<uint>(),
            new HashSet<uint> { 1 });

        Assert.False(review.Materials[0].Selected);
        Assert.Equal(0, review.VendorUnits);
    }

    private void Planner_requires_explicit_inclusion_for_craftable_vendor_items()
    {
        var planner = new WorkshopVendorProcurementPlanner(
            GilVendorCatalog.Create([Offer(1, 100, 4_452)]),
            _ => new(GilVendorAccessState.Verified, "test", "test"),
            itemId => itemId == 1);

        var defaultReview = planner.Build(
            [Availability(1, required: 360, player: 0, retainer: 0)],
            new Dictionary<uint, int>(),
            new HashSet<uint>(),
            new HashSet<uint>());

        Assert.True(defaultReview.Materials[0].IsCraftable);
        Assert.False(defaultReview.Materials[0].Selected);
        Assert.Equal(360, defaultReview.Materials[0].ApprovedVendorQuantity);
        Assert.Equal(0, defaultReview.VendorUnits);
        Assert.Empty(defaultReview.Stops);

        var optedInReview = planner.Build(
            [Availability(1, required: 360, player: 0, retainer: 0)],
            new Dictionary<uint, int>(),
            new HashSet<uint> { 1 },
            new HashSet<uint>());

        Assert.True(optedInReview.Materials[0].Selected);
        Assert.Equal(360, optedInReview.VendorUnits);
        Assert.Single(optedInReview.Stops);
    }

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
            retainer == 0 ? [] : [new(1, "Retainer", DateTimeOffset.UtcNow, retainer)]);
    }

    private static WorkshopMaterialProcurement Procurement(
        uint itemId,
        int required,
        int player,
        int retainer)
    {
        var availability = Availability(itemId, required, player, retainer);
        var retainerPlanned = Math.Min(availability.Shortage, retainer);
        var vendorNeed = Math.Max(0, availability.Shortage - retainerPlanned);
        return new(
            availability,
            retainerPlanned,
            vendorNeed,
            [],
            null,
            IsCraftable: true,
            Selected: false,
            ApprovedVendorQuantity: vendorNeed);
    }

    private static GilVendorOffer Offer(
        uint itemId,
        uint npcId,
        uint price) =>
        new(
            itemId,
            $"Item {itemId}",
            1,
            price,
            50,
            0,
            npcId,
            $"NPC {npcId}",
            129,
            new Vector3(1, 2, 3),
            [2]);

    public enum PlannerScenario
    {
        SharedVerifiedStop,
        ClampEditedQuantity,
        ExcludeInaccessibleOffers,
        PreserveUserExclusion,
        CraftableRequiresExplicitInclusion,
        StockDisplayUsesLiveAvailability,
    }
}
