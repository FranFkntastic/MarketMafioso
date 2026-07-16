using MarketMafioso.RetainerRestock;
using MarketMafioso.Automation.Items;

namespace MarketMafioso.Tests;

public sealed class HttpReporterRetainerScopeTests
{
    [Fact]
    public void BuildRetainerReports_WithOwnerScope_ExcludesOtherCharactersAndLegacyUnscopedRetainers()
    {
        var currentOwner = BuildRetainer(10, "Current Owner", 100, 25);
        currentOwner.OwnerCharacterName = "Wei Ning";
        currentOwner.OwnerHomeWorld = "Maduin";
        var otherOwner = BuildRetainer(11, "Other Owner", 100, 999);
        otherOwner.OwnerCharacterName = "Alt Character";
        otherOwner.OwnerHomeWorld = "Maduin";
        var config = new Configuration
        {
            RetainerCache =
            {
                [10] = currentOwner,
                [11] = otherOwner,
                [12] = BuildRetainer(12, "Legacy Unscoped", 100, 999),
            },
        };

        var reports = HttpReporter.BuildRetainerReports(
            config,
            new RetainerOwnerScope("Wei Ning", "Maduin"),
            includeOwnerFields: true);

        var report = Assert.Single(reports);
        Assert.Equal("Current Owner", report.RetainerName);
        Assert.Equal("Wei Ning", report.OwnerCharacterName);
        Assert.Equal("Maduin", report.OwnerHomeWorld);
    }

    [Fact]
    public void BuildRetainerReports_RehydratesMissingCachedItemTypesWithoutOverwritingKnownEvidence()
    {
        var retainer = BuildRetainer(10, "Current Owner", 100, 25);
        retainer.OwnerCharacterName = "Wei Ning";
        retainer.OwnerHomeWorld = "Maduin";
        retainer.Bags[0].Items[0].ConditionPercent = 100;
        retainer.Bags[0].Items.Add(new CachedItem
        {
            ItemId = 200,
            ItemName = "Known Item",
            ItemType = "Existing category",
            Quantity = 1,
        });
        var config = new Configuration { RetainerCache = { [10] = retainer } };
        var resolvedIds = new List<uint>();

        var report = Assert.Single(HttpReporter.BuildRetainerReports(
            config,
            new RetainerOwnerScope("Wei Ning", "Maduin"),
            includeOwnerFields: true,
            itemId =>
            {
                resolvedIds.Add(itemId);
                return new AutomationItemMetadata(
                    new AutomationItemIdentity(itemId, "Resolved Item", false),
                    999,
                    "Resolved category");
            }));

        Assert.Equal("Resolved category", report.Bags[0].Items[0].ItemType);
        Assert.Null(report.Bags[0].Items[0].ConditionPercent);
        Assert.Equal("Existing category", report.Bags[0].Items[1].ItemType);
        Assert.Equal([100u], resolvedIds);
    }

    private static CachedRetainer BuildRetainer(ulong id, string name, uint itemId, uint quantity) =>
        new()
        {
            RetainerId = id,
            RetainerName = name,
            LastUpdated = new DateTime(2026, 6, 23, 12, 0, 0, DateTimeKind.Utc),
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
