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

    [Fact]
    public void BuildReadResult_ReportsInfoProxyListingCapacity()
    {
        var listings = Enumerable.Range(0, 100)
            .Select(index => new MarketMafioso.MarketAcquisition.MarketBoardLiveListing
            {
                ItemId = 18,
                WorldName = "Siren",
                ListingId = $"listing-{index}",
                RetainerId = $"retainer-{index}",
                UnitPrice = (uint)(100 + index),
                Quantity = 1,
            })
            .ToArray();

        var result = MarketMafioso.MarketAcquisition.MarketBoardListingReader.BuildReadResult(
            waitingForListings: false,
            itemId: 18,
            currentWorld: "Siren",
            listings,
            reportedListingCount: 100,
            listingCapacity: 100);

        Assert.Equal(100, result.ReportedListingCount);
        Assert.Equal(100, result.ListingCapacity);
        Assert.True(result.IsAtListingCapacity);
        Assert.False(result.IsListingCountTruncated);
        Assert.Contains("capacity 100/100", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildReadResult_ReportsTruncatedInfoProxyListingCount()
    {
        var listings = Enumerable.Range(0, 100)
            .Select(index => new MarketMafioso.MarketAcquisition.MarketBoardLiveListing
            {
                ItemId = 18,
                WorldName = "Siren",
                ListingId = $"listing-{index}",
                RetainerId = $"retainer-{index}",
                UnitPrice = (uint)(100 + index),
                Quantity = 1,
            })
            .ToArray();

        var result = MarketMafioso.MarketAcquisition.MarketBoardListingReader.BuildReadResult(
            waitingForListings: false,
            itemId: 18,
            currentWorld: "Siren",
            listings,
            reportedListingCount: 120,
            listingCapacity: 100);

        Assert.Equal(120, result.ReportedListingCount);
        Assert.Equal(100, result.ListingCapacity);
        Assert.True(result.IsAtListingCapacity);
        Assert.True(result.IsListingCountTruncated);
        Assert.Contains("truncated", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildReadResult_CarriesInfoProxyRequestIds()
    {
        var result = MarketMafioso.MarketAcquisition.MarketBoardListingReader.BuildReadResult(
            waitingForListings: false,
            itemId: 18,
            currentWorld: "Siren",
            listings: [],
            reportedListingCount: 120,
            listingCapacity: 100,
            currentRequestId: 7,
            nextRequestId: 8);

        Assert.Equal(7, result.CurrentRequestId);
        Assert.Equal(8, result.NextRequestId);
    }

    [Fact]
    public void BuildReadResult_NormalizesProxyListingItemIdsToCurrentSearchItem()
    {
        var listings = new[]
        {
            new MarketMafioso.MarketAcquisition.MarketBoardLiveListing
            {
                ItemId = 5121,
                WorldName = "Siren",
                ListingId = "listing-1",
                RetainerId = "retainer-1",
                UnitPrice = 800,
                Quantity = 4,
            },
        };

        var result = MarketMafioso.MarketAcquisition.MarketBoardListingReader.BuildReadResult(
            waitingForListings: false,
            itemId: 5066,
            currentWorld: "Siren",
            listings);

        var listing = Assert.Single(result.Listings);
        Assert.Equal((uint)5066, listing.ItemId);
        Assert.Equal((uint)5121, listing.RawItemId);
    }

    [Fact]
    public void BuildReadResult_FiltersDefaultInfoProxyRowsBeforeNormalizing()
    {
        var listings = new[]
        {
            new MarketMafioso.MarketAcquisition.MarketBoardLiveListing
            {
                ItemId = 5066,
                WorldName = "Coeurl",
                ListingId = "0",
                RetainerId = "0",
                UnitPrice = 0,
                Quantity = 0,
            },
            new MarketMafioso.MarketAcquisition.MarketBoardLiveListing
            {
                ItemId = 5066,
                WorldName = "Coeurl",
                ListingId = "5207287899074833",
                RetainerId = "33777097237431849",
                UnitPrice = 900,
                Quantity = 10,
            },
        };

        var result = MarketMafioso.MarketAcquisition.MarketBoardListingReader.BuildReadResult(
            waitingForListings: false,
            itemId: 5066,
            currentWorld: "Coeurl",
            listings,
            reportedListingCount: 17,
            listingCapacity: 100);

        var listing = Assert.Single(result.Listings);
        Assert.Equal("5207287899074833", listing.ListingId);
        Assert.Equal(900u, listing.UnitPrice);
        Assert.Equal(10u, listing.Quantity);
    }
}
