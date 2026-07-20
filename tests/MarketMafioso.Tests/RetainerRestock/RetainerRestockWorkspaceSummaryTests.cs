using MarketMafioso.RetainerRestock;
using MarketMafioso.Windows.RetainerRestock;

namespace MarketMafioso.Tests.RetainerRestock;

public sealed class RetainerRestockWorkspaceSummaryTests
{
    [Fact]
    public void Build_SummarizesOnlyCurrentOwnerAndActionablePlanLines()
    {
        var now = new DateTime(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc);
        var plan = new RetainerRestockPlan(
            now,
            [
                Line(needed: 5, cached: 3, missing: 2, hasCandidate: true),
                Line(needed: 4, cached: 0, missing: 4, hasCandidate: false),
                Line(needed: 0, cached: 20, missing: 0, hasCandidate: true),
            ]);
        CachedRetainer[] retainers =
        [
            new() { OwnerCharacterName = "Wei Ning", OwnerHomeWorld = "Siren", LastUpdated = now.AddMinutes(-2) },
            new() { OwnerCharacterName = "Wei Ning", OwnerHomeWorld = "Siren", LastUpdated = now.AddMinutes(-8) },
            new() { OwnerCharacterName = "Eriana Ning", OwnerHomeWorld = "Siren", LastUpdated = now },
        ];

        var summary = RetainerRestockWorkspaceSummary.Build(
            plan,
            new RetainerOwnerScope("Wei Ning", "Siren"),
            retainers,
            accessibleItemCount: 2);

        Assert.Equal("Wei Ning @ Siren", summary.Owner);
        Assert.Equal(2, summary.AccessibleItemCount);
        Assert.Equal(3, summary.PlanLineCount);
        Assert.Equal(1, summary.ReadyLineCount);
        Assert.Equal(3, summary.UnitsToRetrieve);
        Assert.Equal(6, summary.MissingUnits);
        Assert.Equal(2, summary.ObservedRetainerCount);
    }

    [Fact]
    public void Build_ReportsUnavailableOwnerWithoutBorrowingCachedRetainers()
    {
        var summary = RetainerRestockWorkspaceSummary.Build(
            new RetainerRestockPlan(DateTime.UtcNow, []),
            new RetainerOwnerScope(null, null),
            [new CachedRetainer { OwnerCharacterName = "Wei Ning", OwnerHomeWorld = "Siren", LastUpdated = DateTime.UtcNow }],
            accessibleItemCount: 1);

        Assert.Equal("Character unavailable", summary.Owner);
        Assert.Equal(1, summary.AccessibleItemCount);
        Assert.Equal(0, summary.ObservedRetainerCount);
    }

    private static RetainerRestockPlanLine Line(int needed, int cached, int missing, bool hasCandidate) =>
        new(
            Guid.NewGuid(),
            1,
            "Test Item",
            10,
            5,
            needed,
            cached,
            missing,
            hasCandidate ? [new RetainerRestockCandidate(1, "Retainer", DateTime.UtcNow, cached)] : [],
            hasCandidate ? RetainerRestockPlanLineStatus.Ready : RetainerRestockPlanLineStatus.NoCachedStock,
            TimeSpan.FromMinutes(5));
}
