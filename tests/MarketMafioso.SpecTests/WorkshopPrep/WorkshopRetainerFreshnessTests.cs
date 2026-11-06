using Franthropy.Dalamud.Automation.Vendors;
using MarketMafioso.Contracts.Inventory;
using MarketMafioso.Quartermaster;
using MarketMafioso.WorkshopPrep;
using MarketMafioso.Windows.WorkshopLogistics;

namespace MarketMafioso.SpecTests.WorkshopPrep;

public sealed class WorkshopRetainerFreshnessTests
{
    private static readonly QuartermasterOwnerScope Owner = new(100, 40, "Wei Ning", "Maduin");

    [Theory]
    [InlineData(156, 0, false, 0, 0)]
    [InlineData(156, 12, true, 156, 12)]
    [InlineData(0, 156, true, 0, 0)]
    [InlineData(156, 12, false, 12, 12)]
    public void Freshest_inventory_wins_but_retrieval_never_exceeds_RQ(
        uint shared, uint rq, bool sharedNewer, int seen, int retrievable)
    {
        var report = Report(200, shared, sharedNewer ? "08:30:00" : "07:31:53");
        var line = Build([report], Snapshot(rq));
        Assert.Equal(seen, line.QuartermasterStock);
        Assert.Equal(retrievable, line.RetrievableStock);
        var planner = new WorkshopVendorProcurementPlanner(GilVendorCatalog.Create([]),
            _ => throw new InvalidOperationException(), _ => false);
        var review = planner.Build([line], new Dictionary<uint, int>(), new HashSet<uint>(), new HashSet<uint>());
        Assert.Equal(Math.Min(84, retrievable), review.RetainerUnits);
        if (retrievable == 0)
            Assert.Equal(string.Empty, WorkshopMaterialPanel.BuildActionLabel(review, false));
    }

    [Fact]
    public void Union_preserves_shared_only_stock_as_information()
    {
        var line = Build([Report(201, 156, "08:30:00")], Snapshot(12));
        Assert.Equal(168, line.QuartermasterStock);
        Assert.Equal(12, line.RetrievableStock);
        Assert.Contains("156 seen on retainers", line.RetrievalNotice);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_or_wrong_owner_RQ_never_offers_retrieval(bool wrongOwner)
    {
        var snapshot = wrongOwner ? Snapshot(156) with { Owner = new(101, 40, "Other", "Maduin") } : null;
        var line = Build([Report(200, 156, "07:31:53")], snapshot);
        Assert.Equal(156, line.QuartermasterStock);
        Assert.Equal(0, line.RetrievableStock);
        Assert.Contains("no owner-matched", line.RetrievalNotice);
    }

    [Fact]
    public void Bag_time_not_newer_gil_header_controls_inventory_freshness()
    {
        var report = Report(200, 156, "07:31:53") with { LastUpdated = "2026-11-06T09:00:00Z" };
        Assert.Equal(0, Build([report], Snapshot(0)).QuartermasterStock);
    }

    [Fact]
    public void Report_without_inventory_observation_never_outranks_RQ()
    {
        var report = Report(200, 0, "07:31:53") with { LastUpdated = "2026-11-06T09:00:00Z", Bags = [] };
        var line = Build([report], Snapshot(12));
        Assert.Equal(12, line.QuartermasterStock);
        Assert.Equal(12, line.RetrievableStock);
    }

    [Fact]
    public void Mismatched_shared_owner_is_excluded()
    {
        var report = Report(201, 156, "08:30:00") with { OwnerCharacterName = "Other" };
        Assert.Equal(12, Build([report], Snapshot(12)).QuartermasterStock);
    }

    private static WorkshopMaterialAvailability Build(RetainerReport[] reports, QuartermasterSnapshot? snapshot) =>
        Assert.Single(WorkshopMaterialAvailabilityService.BuildAvailabilityWithFallback(
            [new(12580, "Holy Cedar Lumber", 0, 84)], new Dictionary<uint, int>(), true, reports, snapshot, Owner));

    private static RetainerReport Report(ulong id, uint quantity, string time) => new()
    {
        RetainerId = id, RetainerName = "Scrongle", LastUpdated = $"2026-11-06T{time}Z",
        Bags = [new() { ObservedAtUtc = $"2026-11-06T{time}Z", Items = [new() { ItemId = 12580, Quantity = quantity }] }],
    };

    private static QuartermasterSnapshot Snapshot(uint quantity) => new("rq", 1,
        DateTimeOffset.Parse("2026-11-06T08:20:41Z"), new(100, 40, "Wei Ning", "Maduin"),
        [new(200, "Scrongle", DateTimeOffset.Parse("2026-11-06T08:20:41Z"), 0,
            [new("RetainerInventory1", null, [new(12580, null, null, quantity, false, 0, null, 0, null, null)])], [])]);
}
