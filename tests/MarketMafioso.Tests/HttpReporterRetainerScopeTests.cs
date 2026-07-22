using System.Collections.Immutable;
using MarketMafioso.Automation.Items;
using MarketMafioso.Quartermaster;

namespace MarketMafioso.Tests;

public sealed class HttpReporterRetainerScopeTests
{
    [Fact]
    public void BuildRetainerReports_PreservesOwnerAndListingProvenance()
    {
        const string listedAt = "2026-07-21T11:44:12.3456789Z";
        var observedAt = new DateTimeOffset(2026, 7, 21, 11, 58, 0, TimeSpan.Zero);
        var snapshot = new QuartermasterSnapshot(
            "provider-a",
            9,
            new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero),
            new QuartermasterOwner(100, 40, "Wei Ning", "Maduin"),
            ImmutableArray.Create(new QuartermasterRetainerSnapshot(
                10,
                "Taffy-swordsman",
                observedAt,
                1234,
                ImmutableArray.Create(new QuartermasterBagSnapshot(
                    "RetainerInventory1",
                    "Retainer",
                    ImmutableArray.Create(new QuartermasterItemSnapshot(
                        100,
                        "Elm Lumber",
                        "Lumber",
                        25,
                        true,
                        77.5f,
                        "RetainerInventory1",
                        3,
                        77.5f,
                        false)))),
                ImmutableArray.Create(new QuartermasterListingSnapshot(
                    200,
                    "Cobalt Ingot",
                    "Metal",
                    12,
                    true,
                    42.5f,
                    "RetainerMarket",
                    7,
                    42.5f,
                    999,
                    listedAt)))
            {
                RequestedSources = ["RetainerInventory1", "RetainerMarket"],
                ObservedSources = ["RetainerInventory1"],
            }));

        var report = Assert.Single(HttpReporter.BuildRetainerReports(
            snapshot,
            new QuartermasterOwnerScope(100, 40, "Wei Ning", "Maduin"),
            includeOwnerFields: true));

        Assert.Equal("Wei Ning", report.OwnerCharacterName);
        Assert.Equal("Maduin", report.OwnerHomeWorld);
        Assert.Equal(observedAt.ToString("O"), report.LastUpdated);
        var item = Assert.Single(Assert.Single(report.Bags).Items);
        Assert.Equal(77.5f, item.Condition);
        Assert.Equal(77.5f, item.ConditionPercent);
        var listing = Assert.Single(report.MarketListings);
        Assert.Equal(42.5f, listing.Condition);
        Assert.Equal(42.5f, listing.ConditionPercent);
        Assert.Equal(999U, listing.UnitPrice);
        Assert.Equal("RetainerMarket", listing.ContainerKey);
        Assert.Equal(7, listing.SlotIndex);
        Assert.Equal(listedAt, listing.ListedAt);
        Assert.Equal(["RetainerInventory1", "RetainerMarket"], report.Storage.RequestedSources);
        Assert.Equal(["RetainerInventory1"], report.Storage.ObservedSources);
    }

    [Fact]
    public void BuildRetainerReports_RequiresStableOwnerMatch()
    {
        var snapshot = EmptySnapshot(new QuartermasterOwner(100, 40, "Wei Ning", "Maduin"));

        var reports = HttpReporter.BuildRetainerReports(
            snapshot,
            new QuartermasterOwnerScope(999, 40, "Other", "Maduin"),
            includeOwnerFields: true);

        Assert.Empty(reports);
    }

    [Fact]
    public void BuildRetainerReports_RehydratesMissingItemTypeWithoutReplacingWireEvidence()
    {
        var snapshot = new QuartermasterSnapshot(
            "provider-a",
            1,
            DateTimeOffset.UtcNow,
            new QuartermasterOwner(100, 40, "Wei Ning", "Maduin"),
            ImmutableArray.Create(new QuartermasterRetainerSnapshot(
                10,
                "Current Owner",
                DateTimeOffset.UtcNow,
                0,
                ImmutableArray.Create(new QuartermasterBagSnapshot(
                    "RetainerInventory1",
                    null,
                    ImmutableArray.Create(
                        new QuartermasterItemSnapshot(100, "Resolved", null, 1, false, 100, null, null, 100, null),
                        new QuartermasterItemSnapshot(200, "Known", "Existing category", 1, false, 0, null, null, null, null)))),
                ImmutableArray<QuartermasterListingSnapshot>.Empty)));
        var resolvedIds = new List<uint>();

        var report = Assert.Single(HttpReporter.BuildRetainerReports(
            snapshot,
            new QuartermasterOwnerScope(100, 40, "Wei Ning", "Maduin"),
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
        Assert.Equal(0, report.Bags[0].Items[0].Condition);
        Assert.Null(report.Bags[0].Items[0].ConditionPercent);
        Assert.Equal("Existing category", report.Bags[0].Items[1].ItemType);
        Assert.Equal([100U], resolvedIds);
    }

    [Fact]
    public void BuildRetainerReports_WhenItemNamesDisabledSuppressesBagAndListingNames()
    {
        var snapshot = new QuartermasterSnapshot(
            "provider-a",
            1,
            DateTimeOffset.UtcNow,
            new QuartermasterOwner(100, 40, "Wei Ning", "Maduin"),
            [new QuartermasterRetainerSnapshot(
                10,
                "Current Owner",
                DateTimeOffset.UtcNow,
                0,
                [new QuartermasterBagSnapshot("RetainerInventory1", null, [new QuartermasterItemSnapshot(100, "Secret item", null, 1, false, 0, null, null, null, null)])],
                [new QuartermasterListingSnapshot(200, "Secret listing", null, 1, false, 0, null, null, null, null, null)])]);

        var report = Assert.Single(HttpReporter.BuildRetainerReports(
            snapshot,
            new QuartermasterOwnerScope(100, 40, "Wei Ning", "Maduin"),
            includeOwnerFields: false,
            includeItemNames: false));

        Assert.Null(Assert.Single(Assert.Single(report.Bags).Items).ItemName);
        Assert.Null(Assert.Single(report.MarketListings).ItemName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void BuildRetainerReports_WhenNamesEnabledRehydratesBlankNames(string? itemName)
    {
        var snapshot = new QuartermasterSnapshot(
            "provider-a", 1, DateTimeOffset.UtcNow,
            new QuartermasterOwner(100, 40, "Wei Ning", "Maduin"),
            [new QuartermasterRetainerSnapshot(
                10, "Current Owner", DateTimeOffset.UtcNow, 0,
                [new QuartermasterBagSnapshot("RetainerInventory1", null, [new QuartermasterItemSnapshot(100, itemName, "Known type", 1, false, 0, null, null, null, null)])],
                [new QuartermasterListingSnapshot(200, itemName, "Known type", 1, false, 0, null, null, null, null, null)])]);

        var report = Assert.Single(HttpReporter.BuildRetainerReports(
            snapshot,
            new QuartermasterOwnerScope(100, 40, "Wei Ning", "Maduin"),
            includeOwnerFields: false,
            itemId => new AutomationItemMetadata(new AutomationItemIdentity(itemId, $"Resolved {itemId}", false), 999, "Resolved type"),
            includeItemNames: true));

        Assert.Equal("Resolved 100", Assert.Single(Assert.Single(report.Bags).Items).ItemName);
        Assert.Equal("Resolved 200", Assert.Single(report.MarketListings).ItemName);
    }

    private static QuartermasterSnapshot EmptySnapshot(QuartermasterOwner owner) => new(
        "provider-a",
        1,
        DateTimeOffset.UtcNow,
        owner,
        ImmutableArray<QuartermasterRetainerSnapshot>.Empty);
}
