namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketBoardListingReaderTests
{
    [Fact]
    public void BuildReadResult_TreatsVisibleListingsAsReadyWhenWaitingFlagSticks()
    {
        var listings = new[]
        {
            new MarketMafioso.MarketAcquisition.MarketBoardLiveListing
            {
                ItemId = 7017,
                WorldName = "Maduin",
                ListingId = "2885118519228000",
                RetainerId = "2885118519228000",
                UnitPrice = 1_099,
                Quantity = 99,
            },
        };

        var result = MarketMafioso.MarketAcquisition.MarketBoardListingReader.BuildReadResult(
            waitingForListings: true,
            itemId: 7017,
            currentWorld: "Maduin",
            listings);

        Assert.Equal("Ready", result.Status);
        Assert.Contains("waiting flag", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(result.Listings);
    }
}
