namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionEvidenceRefreshPlanBuilderTests
{
    [Fact]
    public void Build_SelectedWorldRequestCreatesEvidenceSubtasksWithoutPurchaseListings()
    {
        var claim = MarketAcquisitionRouteEngineTestData.AcceptedClaim() with
        {
            WorldMode = "Selected",
            SelectedWorlds = ["Siren", "Siren"],
            Lines =
            [
                new MarketMafioso.MarketAcquisition.MarketAcquisitionBatchLineView
                {
                    LineId = "line-1",
                    Ordinal = 0,
                    ItemId = 7017,
                    ItemName = "Varnish",
                    QuantityMode = "Exact",
                    TargetQuantity = 10,
                    MaxQuantity = 10,
                    HqPolicy = "Either",
                    MaxUnitPrice = 1_000,
                    GilCap = 10_000,
                },
            ],
        };

        var plan = MarketMafioso.MarketAcquisition.MarketAcquisitionEvidenceRefreshPlanBuilder.Build(
            claim,
            "Siren",
            DateTimeOffset.UtcNow);

        Assert.Equal("Ready", plan.Status);
        var batch = Assert.Single(plan.WorldBatches);
        Assert.Equal("Siren", batch.WorldName);
        Assert.Empty(batch.Listings);
        var subtask = Assert.Single(batch.ItemSubtasks);
        Assert.Equal("EvidenceRefresh", subtask.Source);
        Assert.Equal(7017u, subtask.ItemId);
        Assert.Equal(0u, subtask.PlannedQuantity);
    }

    [Fact]
    public void Build_RegionalSweepRefusesUnboundedEvidenceTravel()
    {
        var claim = MarketAcquisitionRouteEngineTestData.AcceptedClaim() with
        {
            WorldMode = "AllWorldSweep",
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            MarketMafioso.MarketAcquisition.MarketAcquisitionEvidenceRefreshPlanBuilder.Build(
                claim,
                "Siren",
                DateTimeOffset.UtcNow));

        Assert.Contains("unintended regional sweep", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
