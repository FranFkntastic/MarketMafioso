using MarketMafioso.CraftArchitectCompanion;

namespace MarketMafioso.Tests.CraftArchitectCompanion;

public sealed class CraftArchitectQuickShopDraftBuilderTests
{
    [Fact]
    public void Build_UsesUserThresholdAsMaxUnitPrice()
    {
        var request = CreateRequest() with { BuyThresholdUnitPrice = 150 };
        var quote = new CraftAppraisalQuote
        {
            ItemId = 2,
            ItemName = "Fire Shard",
            EstimatedUnitCost = 80,
            EstimatedTotalCost = 800,
        };

        var draft = CraftArchitectQuickShopDraftBuilder.Build(request, quote);

        var line = Assert.Single(draft.Lines);
        Assert.Equal(150u, line.MaxUnitPrice);
    }

    [Fact]
    public void Build_DefaultsMaxQuantityToRequestedQuantity()
    {
        var request = CreateRequest() with { Quantity = 25 };

        var draft = CraftArchitectQuickShopDraftBuilder.Build(request);

        var line = Assert.Single(draft.Lines);
        Assert.Equal("AllBelowThreshold", line.QuantityMode);
        Assert.Equal(25u, line.MaxQuantity);
        Assert.Equal(0u, line.TargetQuantity);
    }

    [Fact]
    public void Build_CopiesRouteScopeAndGilCap()
    {
        var request = CreateRequest() with
        {
            Region = "North America",
            WorldMode = "AllWorldSweep",
            SweepScope = "DataCenters",
            SweepDataCenters = ["Aether", "Primal"],
            GilCap = 9000,
        };

        var draft = CraftArchitectQuickShopDraftBuilder.Build(request);

        Assert.Equal("North America", draft.Region);
        Assert.Equal("AllWorldSweep", draft.WorldMode);
        Assert.Equal("DataCenters", draft.SweepScope);
        Assert.Equal(["Aether", "Primal"], draft.SweepDataCenters);
        Assert.Equal(9000u, Assert.Single(draft.Lines).GilCap);
    }

    private static MarketAppraisalRequest CreateRequest() => new()
    {
        ItemId = 2,
        ItemName = "Fire Shard",
        Quantity = 10,
        HqPolicy = "Either",
        BuyThresholdUnitPrice = 100,
        Region = "North America",
        WorldMode = "Recommended",
        SweepScope = "Region",
    };
}
