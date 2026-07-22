using System.Collections.Immutable;
using MarketMafioso.Quartermaster;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Tests.WorkshopPrep;

public sealed class WorkshopMaterialAvailabilityServiceTests
{
    private static readonly QuartermasterOwnerScope CurrentOwner = new(100, 40, "Wei Ning", "Maduin");

    [Fact]
    public void BuildAvailability_MapsOwnerScopedQuartermasterStock()
    {
        var requirements = new[] { new WorkshopMaterialRequirement(100, "Elm Lumber", 123, 55) };
        var playerInventory = new Dictionary<uint, int> { [100] = 20 };
        var snapshot = Snapshot(
            new QuartermasterOwner(100, 40, "Wei Ning", "Maduin"),
            Retainer(10, "Current Owner", 100, 25),
            Retainer(11, "Also Current", 100, 10));

        var result = WorkshopMaterialAvailabilityService.BuildAvailability(
            requirements,
            playerInventory,
            snapshot,
            CurrentOwner);

        var item = Assert.Single(result);
        Assert.Equal(55, item.Required);
        Assert.Equal(20, item.PlayerInventory);
        Assert.Equal(35, item.QuartermasterStock);
        Assert.Equal(35, item.Shortage);
        Assert.Equal(0, item.TotalMissing);
        Assert.Equal(0, item.StockDifferential);
        Assert.Equal([10UL, 11UL], item.QuartermasterRetainers.Select(candidate => candidate.RetainerId));
    }

    [Fact]
    public void BuildAvailability_RejectsSnapshotForDifferentStableOwnerScope()
    {
        var snapshot = Snapshot(
            new QuartermasterOwner(999, 40, "Other Character", "Maduin"),
            Retainer(10, "Other Owner", 100, 999));

        var item = Assert.Single(WorkshopMaterialAvailabilityService.BuildAvailability(
            [new WorkshopMaterialRequirement(100, "Elm Lumber", 123, 55)],
            new Dictionary<uint, int> { [100] = 20 },
            snapshot,
            CurrentOwner));

        Assert.Equal(0, item.QuartermasterStock);
        Assert.Equal(35, item.TotalMissing);
        Assert.Empty(item.QuartermasterRetainers);
    }

    [Fact]
    public void BuildAvailability_WithoutQuartermaster_StillReportsPlayerInventory()
    {
        var item = Assert.Single(WorkshopMaterialAvailabilityService.BuildAvailability(
            [new WorkshopMaterialRequirement(100, "Elm Lumber", 123, 55)],
            new Dictionary<uint, int> { [100] = 20 },
            snapshot: null,
            CurrentOwner));

        Assert.Equal(20, item.PlayerInventory);
        Assert.Equal(0, item.QuartermasterStock);
        Assert.Equal(35, item.Shortage);
        Assert.Equal(35, item.TotalMissing);
    }

    [Fact]
    public void BuildAvailability_WhenPlayerHasEnough_KeepsStockVisibleWithoutTransferCandidates()
    {
        var snapshot = Snapshot(
            new QuartermasterOwner(100, 40, "Wei Ning", "Maduin"),
            Retainer(10, "Current Owner", 100, 99));

        var item = Assert.Single(WorkshopMaterialAvailabilityService.BuildAvailability(
            [new WorkshopMaterialRequirement(100, "Elm Lumber", 123, 55)],
            new Dictionary<uint, int> { [100] = 60 },
            snapshot,
            CurrentOwner));

        Assert.Equal(99, item.QuartermasterStock);
        Assert.Equal(0, item.Shortage);
        Assert.Equal(104, item.StockDifferential);
        Assert.Empty(item.QuartermasterRetainers);
    }

    private static QuartermasterSnapshot Snapshot(
        QuartermasterOwner owner,
        params QuartermasterRetainerSnapshot[] retainers) => new(
        "provider-a",
        7,
        new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero),
        owner,
        retainers.ToImmutableArray());

    private static QuartermasterRetainerSnapshot Retainer(
        ulong id,
        string name,
        uint itemId,
        uint quantity) => new(
        id,
        name,
        new DateTimeOffset(2026, 7, 21, 11, 58, 0, TimeSpan.Zero),
        0,
        ImmutableArray.Create(new QuartermasterBagSnapshot(
            "RetainerInventory1",
            "Retainer",
            ImmutableArray.Create(new QuartermasterItemSnapshot(
                itemId,
                "Elm Lumber",
                "Lumber",
                quantity,
                false,
                0,
                "RetainerInventory1",
                0,
                null,
                false)))),
        ImmutableArray<QuartermasterListingSnapshot>.Empty);
}
