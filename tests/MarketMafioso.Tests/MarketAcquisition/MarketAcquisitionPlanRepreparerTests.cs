namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionPlanRepreparerTests
{
    [Fact]
    public void FilterCompletedOrProbedStops_RemovesCompletedWorldsAndRecalculatesTotals()
    {
        var plan = CreatePlan("Siren", "Maduin", "Rafflesia");
        var completed = new[]
        {
            new MarketMafioso.MarketAcquisition.MarketAcquisitionCompletedRouteStop
            {
                WorldName = "Siren",
                Result = "NoSafeListings",
            },
            new MarketMafioso.MarketAcquisition.MarketAcquisitionCompletedRouteStop
            {
                WorldName = "Maduin",
                Result = "Purchased",
            },
        };

        var result = MarketMafioso.MarketAcquisition.MarketAcquisitionPlanRepreparer.FilterCompletedOrProbedStops(
            plan,
            completed,
            DateTimeOffset.UnixEpoch.AddHours(1));

        Assert.True(result.CanStart);
        Assert.Equal(["Siren", "Maduin"], result.SkippedWorlds);
        Assert.Equal(["Rafflesia"], result.Plan.WorldBatches.Select(batch => batch.WorldName).ToArray());
        Assert.Equal(10u, result.Plan.PlannedQuantity);
        Assert.Equal(1_000u, result.Plan.PlannedGil);
        Assert.Equal(1, result.Plan.Diagnostics.PlannedListingCount);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddHours(1), result.Plan.PreparedAtUtc);
    }

    [Fact]
    public void FilterCompletedOrProbedStops_ReturnsNoRemainingWorlds()
    {
        var plan = CreatePlan("Siren");
        var completed = new[]
        {
            new MarketMafioso.MarketAcquisition.MarketAcquisitionCompletedRouteStop
            {
                WorldName = "Siren",
                Result = "NoSafeListings",
            },
        };

        var result = MarketMafioso.MarketAcquisition.MarketAcquisitionPlanRepreparer.FilterCompletedOrProbedStops(
            plan,
            completed,
            DateTimeOffset.UnixEpoch.AddHours(1));

        Assert.False(result.CanStart);
        Assert.Empty(result.Plan.WorldBatches);
        Assert.Contains("No unvisited worlds remain", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static MarketMafioso.MarketAcquisition.MarketAcquisitionPlan CreatePlan(params string[] worlds) =>
        new()
        {
            RequestId = "request-1",
            Status = "Ready",
            WorldMode = "Recommended",
            ItemId = 2,
            RequestedQuantity = 30,
            PlannedQuantity = (uint)(worlds.Length * 10),
            PlannedGil = (uint)(worlds.Length * 1_000),
            PreparedAtUtc = DateTimeOffset.UnixEpoch,
            Diagnostics = new MarketMafioso.MarketAcquisition.MarketAcquisitionPlanDiagnostics
            {
                PlannedListingCount = worlds.Length,
            },
            WorldBatches = worlds
                .Select(world => new MarketMafioso.MarketAcquisition.MarketAcquisitionWorldBatch
                {
                    WorldName = world,
                    DataCenter = MarketMafioso.MarketAcquisition.MarketAcquisitionPlanner.ResolveNorthAmericaDataCenter(world),
                    PlannedQuantity = 10,
                    PlannedGil = 1_000,
                    Listings =
                    [
                        new MarketMafioso.MarketAcquisition.MarketAcquisitionPlannedListing
                        {
                            LineId = "line-1",
                            ItemId = 2,
                            ListingId = $"{world}-listing",
                            Quantity = 10,
                            UnitPrice = 100,
                            TotalGil = 1_000,
                        },
                    ],
                })
                .ToArray(),
        };
}
