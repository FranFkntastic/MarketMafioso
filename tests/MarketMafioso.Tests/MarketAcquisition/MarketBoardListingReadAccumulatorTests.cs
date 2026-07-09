namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketBoardListingReadAccumulatorTests
{
    [Fact]
    public void Merge_AccumulatesPartialReadsForSameItemAndWorld()
    {
        var accumulator = new MarketMafioso.MarketAcquisition.MarketBoardListingReadAccumulator();

        var first = accumulator.Merge(CreateRead(
            reportedListingCount: 3,
            listings:
            [
                CreateListing("first", unitPrice: 400),
                CreateListing("second", unitPrice: 500),
            ]));
        var second = accumulator.Merge(CreateRead(
            reportedListingCount: 3,
            listings:
            [
                CreateListing("second", unitPrice: 500),
                CreateListing("third", unitPrice: 600),
            ]));

        Assert.True(first.IsListingCountTruncated);
        Assert.False(second.IsListingCountTruncated);
        Assert.Equal(["first", "second", "third"], second.Listings.Select(listing => listing.ListingId).ToArray());
    }

    [Fact]
    public void TryBeginContinuation_RequestsFirstUnreadRowForIncompleteListingCoverage()
    {
        var accumulator = new MarketMafioso.MarketAcquisition.MarketBoardListingReadAccumulator();
        var read = accumulator.Merge(CreateRead(
            reportedListingCount: 29,
            listings: Enumerable.Range(0, 24)
                .Select(index => CreateListing($"listing-{index}", unitPrice: 1_000))
                .ToArray()));
        var candidatePlan = new MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlan
        {
            Status = "IncompleteListingCoverage",
            ReadableListingCount = read.ReadableListingCount,
            ReportedListingCount = read.ReportedListingCount,
        };

        var continued = accumulator.TryBeginContinuation(read, candidatePlan, out var decision);

        Assert.True(continued);
        Assert.Equal(24, decision.RequestedRow);
        Assert.Contains("29", decision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryBeginContinuation_StopsAfterRepeatedNoProgressAttempts()
    {
        var accumulator = new MarketMafioso.MarketAcquisition.MarketBoardListingReadAccumulator(maxContinuationAttempts: 2);
        var read = accumulator.Merge(CreateRead(
            reportedListingCount: 29,
            listings: Enumerable.Range(0, 24)
                .Select(index => CreateListing($"listing-{index}", unitPrice: 1_000))
                .ToArray()));
        var candidatePlan = new MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlan
        {
            Status = "IncompleteListingCoverage",
            ReadableListingCount = read.ReadableListingCount,
            ReportedListingCount = read.ReportedListingCount,
        };

        Assert.True(accumulator.TryBeginContinuation(read, candidatePlan, out _));
        Assert.True(accumulator.TryBeginContinuation(read, candidatePlan, out _));
        Assert.False(accumulator.TryBeginContinuation(read, candidatePlan, out _));
    }

    [Fact]
    public void Merge_ResetsWhenReadBecomesComplete()
    {
        var accumulator = new MarketMafioso.MarketAcquisition.MarketBoardListingReadAccumulator();
        _ = accumulator.Merge(CreateRead(
            reportedListingCount: 3,
            listings:
            [
                CreateListing("first"),
                CreateListing("second"),
            ]));

        var complete = accumulator.Merge(CreateRead(
            reportedListingCount: 1,
            listings:
            [
                CreateListing("fresh-complete"),
            ]));

        Assert.False(complete.IsListingCountTruncated);
        Assert.Equal(["fresh-complete"], complete.Listings.Select(listing => listing.ListingId).ToArray());
    }

    private static MarketMafioso.Automation.MarketBoard.MarketBoardReadResult CreateRead(
        uint itemId = 5322,
        string worldName = "Malboro",
        int reportedListingCount = 1,
        int listingCapacity = 100,
        params MarketMafioso.Automation.MarketBoard.MarketBoardLiveListing[] listings) =>
        new()
        {
            Status = "Ready",
            ReadState = reportedListingCount > listings.Length
                ? MarketMafioso.Automation.MarketBoard.MarketBoardListingReadState.FreshPartial
                : MarketMafioso.Automation.MarketBoard.MarketBoardListingReadState.FreshComplete,
            ItemId = itemId,
            WorldName = worldName,
            ReportedListingCount = reportedListingCount,
            ListingCapacity = listingCapacity,
            IsAtListingCapacity = listings.Length >= listingCapacity,
            IsListingCountTruncated = reportedListingCount > listings.Length,
            Listings = listings,
        };

    private static MarketMafioso.Automation.MarketBoard.MarketBoardLiveListing CreateListing(
        string listingId,
        uint itemId = 5322,
        string worldName = "Malboro",
        uint unitPrice = 100) =>
        new()
        {
            ItemId = itemId,
            WorldName = worldName,
            ListingId = listingId,
            RetainerId = $"retainer-{listingId}",
            UnitPrice = unitPrice,
            Quantity = 1,
        };
}
