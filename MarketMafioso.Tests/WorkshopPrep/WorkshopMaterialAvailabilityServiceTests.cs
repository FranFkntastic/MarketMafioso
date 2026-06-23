using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Tests.WorkshopPrep;

public sealed class WorkshopMaterialAvailabilityServiceTests
{
    [Fact]
    public void BuildAvailability_UsesRetainersOnlyForShortage()
    {
        var requirements = new[]
        {
            new WorkshopMaterialRequirement(100, "Elm Lumber", 123, 55),
        };
        var playerInventory = new Dictionary<uint, int>
        {
            [100] = 20,
        };
        var config = new Configuration
        {
            RetainerCache =
            {
                [(ulong)10] = new CachedRetainer
                {
                    RetainerId = 10,
                    RetainerName = "A",
                    LastUpdated = new DateTime(2026, 6, 23, 12, 0, 0, DateTimeKind.Utc),
                    Bags =
                    [
                        new CachedBag
                        {
                            BagName = "RetainerInventory",
                            Items =
                            [
                                new CachedItem { ItemId = 100, ItemName = "Elm Lumber", Quantity = 99 },
                            ],
                        },
                    ],
                },
            },
        };

        var result = WorkshopMaterialAvailabilityService.BuildAvailability(requirements, playerInventory, config);

        var item = Assert.Single(result);
        Assert.Equal(55, item.Required);
        Assert.Equal(20, item.PlayerInventory);
        Assert.Equal(99, item.RetainerCache);
        Assert.Equal(35, item.Shortage);
        Assert.Equal(0, item.TotalMissing);
        Assert.Equal(10UL, Assert.Single(item.CandidateRetainers).RetainerId);
    }

    [Fact]
    public void BuildAvailability_ReportsTotalMissingAfterPlayerAndRetainers()
    {
        var requirements = new[]
        {
            new WorkshopMaterialRequirement(100, "Elm Lumber", 123, 55),
        };
        var playerInventory = new Dictionary<uint, int>
        {
            [100] = 20,
        };
        var config = new Configuration
        {
            RetainerCache =
            {
                [(ulong)10] = new CachedRetainer
                {
                    RetainerId = 10,
                    RetainerName = "A",
                    LastUpdated = new DateTime(2026, 6, 23, 12, 0, 0, DateTimeKind.Utc),
                    Bags =
                    [
                        new CachedBag
                        {
                            BagName = "RetainerInventory",
                            Items =
                            [
                                new CachedItem { ItemId = 100, ItemName = "Elm Lumber", Quantity = 15 },
                            ],
                        },
                    ],
                },
            },
        };

        var result = WorkshopMaterialAvailabilityService.BuildAvailability(requirements, playerInventory, config);

        var item = Assert.Single(result);
        Assert.Equal(35, item.Shortage);
        Assert.Equal(20, item.TotalMissing);
    }

    [Fact]
    public void BuildAvailability_ReportsNoShortageWhenPlayerHasEnough()
    {
        var requirements = new[]
        {
            new WorkshopMaterialRequirement(100, "Elm Lumber", 123, 55),
        };
        var playerInventory = new Dictionary<uint, int>
        {
            [100] = 60,
        };

        var result = WorkshopMaterialAvailabilityService.BuildAvailability(requirements, playerInventory, new Configuration());

        var item = Assert.Single(result);
        Assert.Equal(0, item.Shortage);
        Assert.Equal(0, item.TotalMissing);
        Assert.Empty(item.CandidateRetainers);
    }

    [Fact]
    public void BuildAvailability_ReportsRetainerCacheWhenPlayerHasEnough()
    {
        var requirements = new[]
        {
            new WorkshopMaterialRequirement(100, "Elm Lumber", 123, 55),
        };
        var playerInventory = new Dictionary<uint, int>
        {
            [100] = 60,
        };
        var config = new Configuration
        {
            RetainerCache =
            {
                [(ulong)10] = new CachedRetainer
                {
                    RetainerId = 10,
                    RetainerName = "A",
                    LastUpdated = new DateTime(2026, 6, 23, 12, 0, 0, DateTimeKind.Utc),
                    Bags =
                    [
                        new CachedBag
                        {
                            BagName = "RetainerInventory",
                            Items =
                            [
                                new CachedItem { ItemId = 100, ItemName = "Elm Lumber", Quantity = 99 },
                            ],
                        },
                    ],
                },
            },
        };

        var result = WorkshopMaterialAvailabilityService.BuildAvailability(requirements, playerInventory, config);

        var item = Assert.Single(result);
        Assert.Equal(0, item.Shortage);
        Assert.Equal(0, item.TotalMissing);
        Assert.Equal(99, item.RetainerCache);
        Assert.Empty(item.CandidateRetainers);
    }
}
