namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketBoardPaginationStateTests
{
    [Fact]
    public void CanRequestNextPage_ReturnsFalseWhenReadIsNotTruncated()
    {
        var state = new MarketMafioso.MarketAcquisition.MarketBoardPaginationState(
            ItemId: 5064,
            WorldName: "Siren",
            ReportedListingCount: 42,
            ReadableListingCount: 42,
            ListingCapacity: 100,
            CurrentRequestId: 1,
            NextRequestId: 2);

        Assert.False(state.CanRequestNextPage);
        Assert.False(state.IsTruncated);
    }

    [Fact]
    public void CanRequestNextPage_ReturnsTrueWhenTruncatedAndRequestIdsAreCoherent()
    {
        var state = new MarketMafioso.MarketAcquisition.MarketBoardPaginationState(
            ItemId: 5064,
            WorldName: "Siren",
            ReportedListingCount: 180,
            ReadableListingCount: 100,
            ListingCapacity: 100,
            CurrentRequestId: 1,
            NextRequestId: 2);

        Assert.True(state.CanRequestNextPage);
        Assert.True(state.IsTruncated);
    }

    [Fact]
    public void CanRequestNextPage_ReturnsFalseWhenRequestIdsAreNotCoherent()
    {
        var state = new MarketMafioso.MarketAcquisition.MarketBoardPaginationState(
            ItemId: 5064,
            WorldName: "Siren",
            ReportedListingCount: 180,
            ReadableListingCount: 100,
            ListingCapacity: 100,
            CurrentRequestId: 1,
            NextRequestId: 1);

        Assert.False(state.CanRequestNextPage);
        Assert.False(state.HasCoherentRequestIds);
    }

    [Fact]
    public void IsContinuationOf_RejectsDifferentItem()
    {
        var first = new MarketMafioso.MarketAcquisition.MarketBoardPaginationState(
            ItemId: 5064,
            WorldName: "Siren",
            ReportedListingCount: 180,
            ReadableListingCount: 100,
            ListingCapacity: 100,
            CurrentRequestId: 1,
            NextRequestId: 2);
        var next = new MarketMafioso.MarketAcquisition.MarketBoardPaginationState(
            ItemId: 2,
            WorldName: "Siren",
            ReportedListingCount: 80,
            ReadableListingCount: 80,
            ListingCapacity: 100,
            CurrentRequestId: 2,
            NextRequestId: 3);

        Assert.False(next.IsContinuationOf(first));
    }

    [Fact]
    public void IsContinuationOf_AllowsSameItemAndWorldIgnoringCase()
    {
        var first = new MarketMafioso.MarketAcquisition.MarketBoardPaginationState(
            ItemId: 5064,
            WorldName: "Siren",
            ReportedListingCount: 180,
            ReadableListingCount: 100,
            ListingCapacity: 100,
            CurrentRequestId: 1,
            NextRequestId: 2);
        var next = new MarketMafioso.MarketAcquisition.MarketBoardPaginationState(
            ItemId: 5064,
            WorldName: "siren",
            ReportedListingCount: 180,
            ReadableListingCount: 100,
            ListingCapacity: 100,
            CurrentRequestId: 2,
            NextRequestId: 3);

        Assert.True(next.IsContinuationOf(first));
    }

    [Fact]
    public void FromReadResult_CopiesPaginationFields()
    {
        var readResult = new MarketMafioso.MarketAcquisition.MarketBoardReadResult
        {
            ItemId = 5064,
            WorldName = "Siren",
            ReportedListingCount = 180,
            ListingCapacity = 100,
            Listings =
            [
                new MarketMafioso.MarketAcquisition.MarketBoardLiveListing(),
                new MarketMafioso.MarketAcquisition.MarketBoardLiveListing(),
            ],
        };

        var state = MarketMafioso.MarketAcquisition.MarketBoardPaginationState.FromReadResult(
            readResult,
            currentRequestId: 7,
            nextRequestId: 8);

        Assert.Equal(5064u, state.ItemId);
        Assert.Equal("Siren", state.WorldName);
        Assert.Equal(180, state.ReportedListingCount);
        Assert.Equal(2, state.ReadableListingCount);
        Assert.Equal(100, state.ListingCapacity);
        Assert.Equal(7, state.CurrentRequestId);
        Assert.Equal(8, state.NextRequestId);
    }
}
