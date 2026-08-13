using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.SpecTests.MarketAcquisition;

public sealed class MarketAcquisitionEvidenceRefreshPlanBuilderTests
{
    [Fact]
    public void Build_UsesOnlyExplicitPreparedWorldsForRecommendedRequest()
    {
        var request = new MarketAcquisitionClaimView
        {
            Id = "request-1",
            WorldMode = "Recommended",
            ItemId = 5532,
            ItemName = "Hardened Sap",
            QuantityMode = "AllBelowThreshold",
            Quantity = 6000,
            HqPolicy = "Either",
            MaxUnitPrice = 300,
        };

        var plan = MarketAcquisitionEvidenceRefreshPlanBuilder.Build(
            request,
            ["Goblin", "Goblin", "Siren"],
            DateTimeOffset.UnixEpoch);

        Assert.Equal(["Goblin", "Siren"], plan.WorldBatches.Select(batch => batch.WorldName));
        Assert.All(plan.WorldBatches.SelectMany(batch => batch.ItemSubtasks),
            subtask => Assert.Equal("EvidenceRefresh", subtask.Source));
        Assert.Empty(plan.WorldBatches.SelectMany(batch => batch.Listings));
    }

    [Fact]
    public void Build_RefusesMissingPreparedWorlds()
    {
        var request = new MarketAcquisitionClaimView
        {
            Id = "request-1",
            WorldMode = "Recommended",
            ItemId = 5532,
            Quantity = 1,
        };

        Assert.Throws<InvalidOperationException>(() =>
            MarketAcquisitionEvidenceRefreshPlanBuilder.Build(
                request,
                Array.Empty<string>(),
                DateTimeOffset.UnixEpoch));
    }
}
