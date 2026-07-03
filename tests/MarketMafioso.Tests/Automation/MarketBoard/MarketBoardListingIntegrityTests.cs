using MarketMafioso.Automation.MarketBoard;

namespace MarketMafioso.Tests.Automation.MarketBoard;

public sealed class MarketBoardListingIntegrityTests
{
    [Fact]
    public void IsMeaningfulObservation_ReturnsFalseForJokePrices()
    {
        var listing = CreateListing(unitPrice: 999_999_999);

        Assert.False(MarketBoardListingIntegrity.IsMeaningfulObservation(listing, maxMeaningfulUnitPrice: 250));
    }

    [Fact]
    public void IsMeaningfulObservation_ReturnsTrueForOrdinaryAboveThresholdPrices()
    {
        var listing = CreateListing(unitPrice: 101);

        Assert.True(MarketBoardListingIntegrity.IsMeaningfulObservation(listing, maxMeaningfulUnitPrice: 250));
    }

    private static MarketBoardLiveListing CreateListing(uint unitPrice) =>
        new()
        {
            ItemId = 2,
            WorldName = "Maduin",
            ListingId = "listing-1",
            RetainerId = "retainer-1",
            Quantity = 99,
            UnitPrice = unitPrice,
        };
}
