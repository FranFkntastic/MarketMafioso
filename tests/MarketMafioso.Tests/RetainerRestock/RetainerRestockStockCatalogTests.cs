using MarketMafioso.RetainerRestock;

namespace MarketMafioso.Tests.RetainerRestock;

public sealed class RetainerRestockStockCatalogTests
{
    [Fact]
    public void Build_IncludesOnlyItemsWithPositiveAccessibleStock()
    {
        var playerBags = new[]
        {
            BuildBag(
                new ItemSlot { ItemId = 100, ItemName = "Darksteel Ore", Quantity = 12 },
                new ItemSlot { ItemId = 101, ItemName = "Spruce Log", Quantity = 0 }),
        };
        var config = new Configuration
        {
            RetainerCache =
            {
                [10] = BuildRetainer(10, "Eris", DateTime.UtcNow, 100, "Darksteel Ore", 144),
            },
        };

        var rows = RetainerRestockStockCatalog.Build(playerBags, config, DateTime.UtcNow, ownerScope: null);

        var row = Assert.Single(rows);
        Assert.Equal(100U, row.ItemId);
        Assert.Equal("Darksteel Ore", row.ItemName);
        Assert.Equal(156, row.TotalQuantity);
        Assert.Equal(12, row.PlayerQuantity);
        Assert.Equal(144, row.RetainerQuantity);
    }

    [Fact]
    public void Build_WithOwnerScope_ExcludesOtherCharactersAndLegacyUnscopedRetainers()
    {
        var currentOwner = BuildRetainer(10, "Eris", DateTime.UtcNow, 100, "Darksteel Ore", 144);
        currentOwner.OwnerCharacterName = "Wei Ning";
        currentOwner.OwnerHomeWorld = "Maduin";
        var otherOwner = BuildRetainer(11, "Other Owner", DateTime.UtcNow, 100, "Darksteel Ore", 999);
        otherOwner.OwnerCharacterName = "Alt Character";
        otherOwner.OwnerHomeWorld = "Maduin";

        var config = new Configuration
        {
            RetainerCache =
            {
                [10] = currentOwner,
                [11] = otherOwner,
                [12] = BuildRetainer(12, "Legacy Unscoped", DateTime.UtcNow, 100, "Darksteel Ore", 777),
            },
        };

        var rows = RetainerRestockStockCatalog.Build(
            [],
            config,
            DateTime.UtcNow,
            new RetainerOwnerScope("Wei Ning", "Maduin"));

        var row = Assert.Single(rows);
        Assert.Equal(144, row.TotalQuantity);
        Assert.Equal(0, row.PlayerQuantity);
        Assert.Equal(144, row.RetainerQuantity);
        var source = Assert.Single(row.RetainerSources);
        Assert.Equal("Eris", source.SourceName);
        Assert.Equal(10UL, source.RetainerId);
    }

    [Fact]
    public void Build_IncludesPlayerOnlyRowsAsAccessibleButNotWithdrawable()
    {
        var playerBags = new[]
        {
            BuildBag(new ItemSlot { ItemId = 200, ItemName = "Cobalt Ingot", Quantity = 18 }),
        };

        var rows = RetainerRestockStockCatalog.Build(playerBags, new Configuration(), DateTime.UtcNow, ownerScope: null);

        var row = Assert.Single(rows);
        Assert.Equal(18, row.TotalQuantity);
        Assert.Equal(18, row.PlayerQuantity);
        Assert.Equal(0, row.RetainerQuantity);
        Assert.False(row.HasRetainerStock);
    }

    [Fact]
    public void Search_FiltersByNameAndOrdersPrefixMatchesBeforeContainsMatches()
    {
        var rows = new[]
        {
            BuildRow(300, "Fire Shard"),
            BuildRow(301, "Shard Glue"),
            BuildRow(302, "Lightning Shard"),
        };

        var results = RetainerRestockStockCatalog.Search(rows, "shard");

        Assert.Collection(
            results,
            row => Assert.Equal("Shard Glue", row.ItemName),
            row => Assert.Equal("Fire Shard", row.ItemName),
            row => Assert.Equal("Lightning Shard", row.ItemName));
    }

    [Fact]
    public void Build_ReportsOldestAndNewestRetainerCacheAge()
    {
        var now = new DateTime(2026, 7, 9, 18, 0, 0, DateTimeKind.Utc);
        var config = new Configuration
        {
            RetainerCache =
            {
                [10] = BuildRetainer(10, "Old", now - TimeSpan.FromHours(2), 100, "Darksteel Ore", 12),
                [11] = BuildRetainer(11, "Fresh", now - TimeSpan.FromMinutes(15), 100, "Darksteel Ore", 8),
            },
        };

        var rows = RetainerRestockStockCatalog.Build([], config, now, ownerScope: null);

        var row = Assert.Single(rows);
        Assert.Equal(TimeSpan.FromHours(2), row.OldestRetainerCacheAge);
        Assert.Equal(TimeSpan.FromMinutes(15), row.NewestRetainerCacheAge);
    }

    private static InventoryBag BuildBag(params ItemSlot[] items) =>
        new() { BagName = "Inventory", Items = items.ToList() };

    private static RetainerRestockStockRow BuildRow(uint itemId, string itemName) =>
        new(
            itemId,
            itemName,
            TotalQuantity: 1,
            PlayerQuantity: 0,
            RetainerQuantity: 1,
            Sources: [],
            RetainerSources: [],
            OldestRetainerCacheAge: null,
            NewestRetainerCacheAge: null);

    private static CachedRetainer BuildRetainer(
        ulong retainerId,
        string retainerName,
        DateTime lastUpdated,
        uint itemId,
        string itemName,
        uint quantity) =>
        new()
        {
            RetainerId = retainerId,
            RetainerName = retainerName,
            LastUpdated = lastUpdated,
            Bags =
            [
                new CachedBag
                {
                    BagName = "RetainerInventory",
                    Items =
                    [
                        new CachedItem { ItemId = itemId, ItemName = itemName, Quantity = quantity },
                    ],
                },
            ],
        };
}
