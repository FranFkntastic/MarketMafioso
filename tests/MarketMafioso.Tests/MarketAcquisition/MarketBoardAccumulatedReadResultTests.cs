namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketBoardAccumulatedReadResultTests
{
    [Fact]
    public void Append_MergesSameItemAndWorldByListingId()
    {
        var first = MarketMafioso.Automation.MarketBoard.MarketBoardAccumulatedReadResult.FromReadResult(CreateRead(
            reportedListingCount: 4,
            listings:
            [
                CreateListing("first", unitPrice: 100),
                CreateListing("duplicate", unitPrice: 110),
            ]));

        var second = CreateRead(
            reportedListingCount: 4,
            currentRequestId: 2,
            nextRequestId: 3,
            listings:
            [
                CreateListing("duplicate", unitPrice: 110),
                CreateListing("second-page-cheap", unitPrice: 50),
            ]);

        var accumulated = first.Append(second);

        Assert.Equal(2, accumulated.PageCount);
        Assert.Equal(4, accumulated.ReportedListingCount);
        Assert.Equal(2, accumulated.CurrentRequestId);
        Assert.Equal(3, accumulated.NextRequestId);
        Assert.Equal(["first", "duplicate", "second-page-cheap"], accumulated.Listings.Select(listing => listing.ListingId).ToArray());
    }

    [Fact]
    public void Append_RejectsDifferentItem()
    {
        var first = MarketMafioso.Automation.MarketBoard.MarketBoardAccumulatedReadResult.FromReadResult(CreateRead());
        var second = CreateRead(itemId: 4);

        var ex = Assert.Throws<InvalidOperationException>(() => first.Append(second));

        Assert.Contains("different item", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Append_RejectsDifferentWorld()
    {
        var first = MarketMafioso.Automation.MarketBoard.MarketBoardAccumulatedReadResult.FromReadResult(CreateRead());
        var second = CreateRead(worldName: "Faerie");

        var ex = Assert.Throws<InvalidOperationException>(() => first.Append(second));

        Assert.Contains("different world", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToReadResult_ReportsStillTruncatedUntilAccumulatedListingsReachReportedCount()
    {
        var first = MarketMafioso.Automation.MarketBoard.MarketBoardAccumulatedReadResult.FromReadResult(CreateRead(
            reportedListingCount: 3,
            listingCapacity: 2,
            listings:
            [
                CreateListing("first", unitPrice: 100),
                CreateListing("second", unitPrice: 110),
            ]));

        Assert.True(first.ToReadResult().IsListingCountTruncated);

        var accumulated = first.Append(CreateRead(
            reportedListingCount: 3,
            listingCapacity: 2,
            listings:
            [
                CreateListing("third", unitPrice: 90),
            ]));

        var readResult = accumulated.ToReadResult();

        Assert.False(readResult.IsListingCountTruncated);
        Assert.Equal(3, readResult.Listings.Count);
    }

    [Fact]
    public void ToReadResult_ReportsStillTruncatedWhenReadableRowsAreBelowCapacity()
    {
        var accumulated = MarketMafioso.Automation.MarketBoard.MarketBoardAccumulatedReadResult.FromReadResult(CreateRead(
            reportedListingCount: 6,
            listingCapacity: 100,
            listings:
            [
                CreateListing("visible-expensive-1", unitPrice: 7_999),
                CreateListing("visible-expensive-2", unitPrice: 8_000),
            ]));

        var readResult = accumulated.ToReadResult();

        Assert.True(readResult.IsListingCountTruncated);
        Assert.Equal(MarketMafioso.Automation.MarketBoard.MarketBoardListingReadState.FreshPartial, readResult.ReadState);
        Assert.Contains("2/6", readResult.Message, StringComparison.Ordinal);
    }

    private static MarketMafioso.Automation.MarketBoard.MarketBoardReadResult CreateRead(
        uint itemId = 2,
        string worldName = "Gilgamesh",
        int reportedListingCount = 1,
        int listingCapacity = 100,
        byte currentRequestId = 1,
        byte nextRequestId = 2,
        params MarketMafioso.Automation.MarketBoard.MarketBoardLiveListing[] listings) =>
        new()
        {
            Status = "Ready",
            ItemId = itemId,
            WorldName = worldName,
            ReportedListingCount = reportedListingCount,
            ListingCapacity = listingCapacity,
            IsAtListingCapacity = listings.Length >= listingCapacity,
            IsListingCountTruncated = reportedListingCount > listings.Length,
            CurrentRequestId = currentRequestId,
            NextRequestId = nextRequestId,
            Listings = listings,
        };

    private static MarketMafioso.Automation.MarketBoard.MarketBoardLiveListing CreateListing(
        string listingId = "listing",
        uint itemId = 2,
        string worldName = "Gilgamesh",
        uint unitPrice = 100) =>
        new()
        {
            ItemId = itemId,
            WorldName = worldName,
            ListingId = listingId,
            RetainerId = $"retainer-{listingId}",
            RetainerName = $"Retainer {listingId}",
            UnitPrice = unitPrice,
            Quantity = 1,
        };
}

