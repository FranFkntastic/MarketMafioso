using MarketMafioso.CraftArchitectCompanion;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Tests.CraftArchitectCompanion;

public sealed class CraftArchitectMarketAppraisalServiceTests
{
    [Fact]
    public void Build_FiltersByUserThresholdWithoutTreatingCraftQuoteAsAuthority()
    {
        var request = CreateRequest(buyThreshold: 120);
        var quote = new CraftAppraisalQuote
        {
            ItemId = 2,
            ItemName = "Fire Shard",
            EstimatedUnitCost = 80,
            EstimatedTotalCost = 800,
            Source = "Manual",
        };
        var listings = new[]
        {
            CreateListing("Siren", quantity: 5, unitPrice: 100, listingId: "at-user-threshold"),
            CreateListing("Siren", quantity: 5, unitPrice: 90, listingId: "above-craft-below-threshold"),
            CreateListing("Siren", quantity: 5, unitPrice: 130, listingId: "above-threshold"),
        };

        var result = CraftArchitectMarketAppraisalService.Build(request, listings, quote);

        Assert.Same(quote, result.CraftQuote);
        Assert.Equal(10u, result.SupportedQuantity);
        Assert.Equal(2u, result.SupportedListingCount);
        Assert.Equal(950ul, result.SupportedTotalGil);
        var world = Assert.Single(result.Worlds);
        Assert.Equal("Siren", world.WorldName);
        Assert.Equal(10u, world.Quantity);
        Assert.Equal(2u, world.ListingCount);
        Assert.Equal(950ul, world.TotalGil);
        Assert.Equal(90u, world.LowestUnitPrice);
    }

    [Fact]
    public void Build_AppliesHqOnlyPolicy()
    {
        var request = CreateRequest(hqPolicy: "HqOnly");
        var listings = new[]
        {
            CreateListing("Siren", quantity: 5, unitPrice: 100, hq: false, listingId: "nq"),
            CreateListing("Siren", quantity: 3, unitPrice: 110, hq: true, listingId: "hq"),
        };

        var result = CraftArchitectMarketAppraisalService.Build(request, listings);

        Assert.Equal(3u, result.SupportedQuantity);
        Assert.Equal(1u, result.SupportedListingCount);
        Assert.Equal(330ul, result.SupportedTotalGil);
    }

    [Fact]
    public void Build_GroupsSupportedListingsByWorld()
    {
        var request = CreateRequest();
        var listings = new[]
        {
            CreateListing("Siren", quantity: 5, unitPrice: 100, listingId: "s-1"),
            CreateListing("Siren", quantity: 2, unitPrice: 90, listingId: "s-2"),
            CreateListing("Faerie", quantity: 10, unitPrice: 80, listingId: "f-1"),
        };

        var result = CraftArchitectMarketAppraisalService.Build(request, listings);

        Assert.Equal(17u, result.SupportedQuantity);
        Assert.Equal(2u, result.SupportedWorldCount);
        Assert.Equal(["Faerie", "Siren"], result.Worlds.Select(world => world.WorldName).ToArray());
        Assert.Equal(800ul, result.Worlds[0].TotalGil);
        Assert.Equal(680ul, result.Worlds[1].TotalGil);
    }

    [Fact]
    public void Build_ReturnsWarningWhenNoStockIsUnderThreshold()
    {
        var request = CreateRequest(buyThreshold: 50);
        var listings = new[]
        {
            CreateListing("Siren", quantity: 5, unitPrice: 100, listingId: "expensive"),
        };

        var result = CraftArchitectMarketAppraisalService.Build(request, listings);

        Assert.Equal(0u, result.SupportedQuantity);
        Assert.Empty(result.Worlds);
        Assert.Contains(result.Warnings, warning => warning.Contains("No market stock", StringComparison.OrdinalIgnoreCase));
    }

    private static MarketAppraisalRequest CreateRequest(uint buyThreshold = 120, string hqPolicy = "Either") => new()
    {
        ItemId = 2,
        ItemName = "Fire Shard",
        Quantity = 10,
        HqPolicy = hqPolicy,
        BuyThresholdUnitPrice = buyThreshold,
        Region = "North America",
        WorldMode = "Recommended",
        SweepScope = "Region",
    };

    private static MarketAcquisitionListing CreateListing(
        string world,
        uint quantity,
        uint unitPrice,
        string listingId,
        bool hq = false,
        uint itemId = 2) => new()
    {
        ItemId = itemId,
        ItemName = itemId == 2 ? "Fire Shard" : "Other",
        ListingId = listingId,
        WorldName = world,
        WorldId = 1,
        RetainerName = $"Retainer-{listingId}",
        RetainerId = $"RetainerId-{listingId}",
        Quantity = quantity,
        UnitPrice = unitPrice,
        IsHq = hq,
        LastReviewTimeUtc = DateTimeOffset.UnixEpoch,
    };
}
