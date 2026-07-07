using MarketMafioso.RetainerRestock;

namespace MarketMafioso.Tests.RetainerRestock;

public sealed class RetainerRestockPlannerTests
{
    [Fact]
    public void BuildPlan_CalculatesNeedFromDesiredPlayerQuantity()
    {
        var rows = new[]
        {
            new RetainerRestockPlanItem
            {
                Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                ItemId = 100,
                ItemName = "Elm Lumber",
                DesiredPlayerQuantity = 55,
                Enabled = true,
            },
        };
        var playerInventory = new Dictionary<uint, int> { [100] = 20 };
        var config = BuildConfigWithRetainer(10, "A", itemId: 100, quantity: 99);

        var plan = RetainerRestockPlanner.BuildPlan(rows, playerInventory, config, new DateTime(2026, 7, 7, 12, 0, 0, DateTimeKind.Utc));

        var line = Assert.Single(plan.Lines);
        Assert.Equal(55, line.DesiredPlayerQuantity);
        Assert.Equal(20, line.PlayerQuantity);
        Assert.Equal(35, line.NeededQuantity);
        Assert.Equal(99, line.CachedRetainerQuantity);
        Assert.Equal(0, line.MissingQuantity);
        Assert.Equal(RetainerRestockPlanLineStatus.Ready, line.Status);
    }

    [Fact]
    public void BuildPlan_IgnoresDisabledRows()
    {
        var rows = new[]
        {
            new RetainerRestockPlanItem
            {
                Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                ItemId = 100,
                ItemName = "Elm Lumber",
                DesiredPlayerQuantity = 55,
                Enabled = false,
            },
        };

        var plan = RetainerRestockPlanner.BuildPlan(rows, new Dictionary<uint, int>(), new Configuration(), DateTime.UtcNow);

        Assert.Empty(plan.Lines);
    }

    [Fact]
    public void BuildPlan_RanksCandidatesByQuantityThenCacheFreshnessThenName()
    {
        var rows = new[]
        {
            new RetainerRestockPlanItem
            {
                Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                ItemId = 100,
                ItemName = "Elm Lumber",
                DesiredPlayerQuantity = 80,
                Enabled = true,
            },
        };
        var config = new Configuration
        {
            RetainerCache =
            {
                [10] = BuildRetainer(10, "Old Large", new DateTime(2026, 7, 7, 10, 0, 0, DateTimeKind.Utc), 100, 40),
                [11] = BuildRetainer(11, "Fresh Large", new DateTime(2026, 7, 7, 11, 0, 0, DateTimeKind.Utc), 100, 40),
                [12] = BuildRetainer(12, "Small", new DateTime(2026, 7, 7, 11, 30, 0, DateTimeKind.Utc), 100, 15),
            },
        };

        var plan = RetainerRestockPlanner.BuildPlan(rows, new Dictionary<uint, int>(), config, new DateTime(2026, 7, 7, 12, 0, 0, DateTimeKind.Utc));

        var candidates = Assert.Single(plan.Lines).Candidates;
        Assert.Collection(
            candidates,
            candidate => Assert.Equal("Fresh Large", candidate.RetainerName),
            candidate => Assert.Equal("Old Large", candidate.RetainerName),
            candidate => Assert.Equal("Small", candidate.RetainerName));
    }

    [Fact]
    public void BuildPlan_ReportsPartialCoverageAndOldestRelevantCacheAge()
    {
        var rows = new[]
        {
            new RetainerRestockPlanItem
            {
                Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                ItemId = 100,
                ItemName = "Elm Lumber",
                DesiredPlayerQuantity = 100,
                Enabled = true,
            },
        };
        var config = BuildConfigWithRetainer(10, "A", itemId: 100, quantity: 15);
        var now = new DateTime(2026, 7, 7, 13, 30, 0, DateTimeKind.Utc);

        var plan = RetainerRestockPlanner.BuildPlan(rows, new Dictionary<uint, int> { [100] = 20 }, config, now);

        var line = Assert.Single(plan.Lines);
        Assert.Equal(80, line.NeededQuantity);
        Assert.Equal(15, line.CachedRetainerQuantity);
        Assert.Equal(65, line.MissingQuantity);
        Assert.Equal(RetainerRestockPlanLineStatus.Partial, line.Status);
        Assert.Equal(TimeSpan.FromMinutes(90), line.OldestRelevantCacheAge);
    }

    [Fact]
    public void BuildPlan_ReportsNoNeedWhenPlayerInventoryAlreadySatisfiesRow()
    {
        var rows = new[]
        {
            new RetainerRestockPlanItem
            {
                Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                ItemId = 100,
                ItemName = "Elm Lumber",
                DesiredPlayerQuantity = 55,
                Enabled = true,
            },
        };

        var plan = RetainerRestockPlanner.BuildPlan(rows, new Dictionary<uint, int> { [100] = 60 }, new Configuration(), DateTime.UtcNow);

        var line = Assert.Single(plan.Lines);
        Assert.Equal(0, line.NeededQuantity);
        Assert.Equal(0, line.MissingQuantity);
        Assert.Equal(RetainerRestockPlanLineStatus.NoNeed, line.Status);
        Assert.Empty(line.Candidates);
    }

    [Fact]
    public void BuildPlan_WithOwnerScope_ExcludesOtherCharactersAndLegacyUnscopedRetainers()
    {
        var rows = new[]
        {
            new RetainerRestockPlanItem
            {
                Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                ItemId = 100,
                ItemName = "Elm Lumber",
                DesiredPlayerQuantity = 50,
                Enabled = true,
            },
        };
        var currentOwner = BuildRetainer(
            10,
            "Current Owner",
            new DateTime(2026, 7, 7, 12, 0, 0, DateTimeKind.Utc),
            100,
            25);
        currentOwner.OwnerCharacterName = "Wei Ning";
        currentOwner.OwnerHomeWorld = "Maduin";

        var otherOwner = BuildRetainer(
            11,
            "Other Owner",
            new DateTime(2026, 7, 7, 12, 0, 0, DateTimeKind.Utc),
            100,
            999);
        otherOwner.OwnerCharacterName = "Alt Character";
        otherOwner.OwnerHomeWorld = "Maduin";

        var config = new Configuration
        {
            RetainerCache =
            {
                [10] = currentOwner,
                [11] = otherOwner,
                [12] = BuildRetainer(
                    12,
                    "Legacy Unscoped",
                    new DateTime(2026, 7, 7, 12, 0, 0, DateTimeKind.Utc),
                    100,
                    999),
            },
        };

        var plan = RetainerRestockPlanner.BuildPlan(
            rows,
            new Dictionary<uint, int>(),
            config,
            new DateTime(2026, 7, 7, 12, 0, 0, DateTimeKind.Utc),
            new RetainerOwnerScope("Wei Ning", "Maduin"));

        var line = Assert.Single(plan.Lines);
        Assert.Equal(25, line.CachedRetainerQuantity);
        Assert.Equal(25, line.MissingQuantity);
        var candidate = Assert.Single(line.Candidates);
        Assert.Equal("Current Owner", candidate.RetainerName);
    }

    private static Configuration BuildConfigWithRetainer(ulong retainerId, string retainerName, uint itemId, uint quantity)
    {
        return new Configuration
        {
            RetainerCache =
            {
                [retainerId] = BuildRetainer(
                    retainerId,
                    retainerName,
                    new DateTime(2026, 7, 7, 12, 0, 0, DateTimeKind.Utc),
                    itemId,
                    quantity),
            },
        };
    }

    private static CachedRetainer BuildRetainer(
        ulong retainerId,
        string retainerName,
        DateTime lastUpdated,
        uint itemId,
        uint quantity)
    {
        return new CachedRetainer
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
                        new CachedItem { ItemId = itemId, ItemName = "Elm Lumber", Quantity = quantity },
                    ],
                },
            ],
        };
    }
}
