using MarketMafioso.RetainerRestock;

namespace MarketMafioso.Tests.RetainerRestock;

public sealed class ElementalDepositPlannerTests
{
    private static readonly RetainerOwnerScope Owner = new("Wei Ning", "Gilgamesh");

    [Fact]
    public void Build_treats_empty_scanned_crystal_bag_as_full_capacity()
    {
        var config = Config(Retainer(1, "Alpha", Owner, []));

        var plan = ElementalDepositPlanner.Build(
            new Dictionary<uint, int> { [2] = 8_000 },
            config,
            Owner,
            Name,
            DateTime.UtcNow);

        Assert.True(plan.CanRun);
        Assert.Equal(8_000, Assert.Single(plan.Lines).PlannedQuantity);
        Assert.Equal(9_999, Assert.Single(plan.Candidates).CapacityByItem[2]);
    }

    [Fact]
    public void Build_splits_capacity_across_retainers_and_orders_most_useful_first()
    {
        var config = Config(
            Retainer(1, "Nearly full", Owner, [Item(2, 9_000)]),
            Retainer(2, "Roomy", Owner, [Item(2, 2_000)]));

        var plan = ElementalDepositPlanner.Build(
            new Dictionary<uint, int> { [2] = 9_500 },
            config,
            Owner,
            Name,
            DateTime.UtcNow);

        Assert.Equal(8_998, Assert.Single(plan.Lines).PlannedQuantity);
        Assert.Equal(502, Assert.Single(plan.Lines).UnplannedQuantity);
        Assert.Equal("Roomy", plan.Candidates[0].RetainerName);
    }

    [Fact]
    public void Build_uses_legacy_cache_as_live_checked_fallback_and_excludes_other_owners()
    {
        var config = Config(
            Retainer(1, "Legacy", Owner, items: null),
            Retainer(2, "Other", new("Someone Else", "Gilgamesh"), []));

        var plan = ElementalDepositPlanner.Build(
            new Dictionary<uint, int> { [8] = 100 },
            config,
            Owner,
            Name,
            DateTime.UtcNow);

        Assert.True(plan.CanRun);
        Assert.Equal(1, plan.ScopedRetainerCount);
        Assert.Equal(1, plan.UnknownCrystalCacheCount);
        Assert.False(Assert.Single(plan.Candidates).CapacityIsKnown);
    }

    private static Configuration Config(params CachedRetainer[] retainers) => new()
    {
        RetainerCache = retainers.ToDictionary(retainer => retainer.RetainerId),
    };

    private static CachedRetainer Retainer(
        ulong id,
        string name,
        RetainerOwnerScope owner,
        IReadOnlyList<CachedItem>? items) => new()
    {
        RetainerId = id,
        RetainerName = name,
        OwnerCharacterName = owner.CharacterName,
        OwnerHomeWorld = owner.HomeWorld,
        LastUpdated = DateTime.UtcNow,
        Bags = items == null
            ? []
            : [new CachedBag { BagName = "RetainerCrystals", Items = items.ToList() }],
    };

    private static CachedItem Item(uint id, uint quantity) => new() { ItemId = id, Quantity = quantity };
    private static string Name(uint id) => $"Element {id}";
}
