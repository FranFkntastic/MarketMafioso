using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.MarketAcquisition.RemoteMarket;

namespace MarketMafioso.SpecTests.MarketAcquisition;

public sealed class RemoteMarketPresentationIdentityTests
{
    private const uint ItemId = 22528;
    private const byte RequestId = 7;
    private const int ListingCount = 12;
    private const string OperationId = "market-browse:1";

    [Fact]
    public void ExactPublishedBrowseAndNativeIdentity_IsCurrent()
    {
        Assert.True(IsCurrent(Identity(), CompletedBrowse()));
    }

    [Fact]
    public void DifferentNativeItem_HidesStaleOverlay()
    {
        Assert.False(IsCurrent(Identity(), CompletedBrowse(), nativeItemId: 13117));
    }

    [Fact]
    public void SameItemWithDifferentNativeRequest_HidesStaleOverlay()
    {
        Assert.False(IsCurrent(Identity(), CompletedBrowse(), nativeRequestId: RequestId + 1));
    }

    [Fact]
    public void SameItemWithDifferentNativeListingCount_HidesStaleOverlay()
    {
        Assert.False(IsCurrent(Identity(), CompletedBrowse(), nativeListingCount: ListingCount + 1));
    }

    [Fact]
    public void SupersedingBrowseOperation_HidesStaleOverlay()
    {
        Assert.False(IsCurrent(
            Identity(),
            CompletedBrowse() with { OperationId = "market-browse:2" }));
    }

    [Fact]
    public void ForeignBrowseOwner_HidesStaleOverlay()
    {
        Assert.False(IsCurrent(
            Identity(),
            CompletedBrowse() with { Owner = MarketBoardBrowseOwner.MarketAcquisition }));
    }

    [Fact]
    public void MissingNativeResultOrPublishedIdentity_HidesOverlay()
    {
        Assert.False(IsCurrent(Identity(), CompletedBrowse(), resultVisible: false));
        Assert.False(IsCurrent(null, CompletedBrowse()));
    }

    [Fact]
    public void FailedBrowse_HidesPreviouslyPublishedOverlay()
    {
        Assert.False(IsCurrent(
            Identity(),
            CompletedBrowse() with { Phase = MarketBoardBrowsePhase.Failed }));
    }

    private static bool IsCurrent(
        RemoteMarketListingSnapshotIdentity? identity,
        MarketBoardBrowseSnapshot browse,
        bool resultVisible = true,
        uint nativeItemId = ItemId,
        uint nativeListingCount = ListingCount,
        byte? nativeRequestId = RequestId) =>
        RemoteMarketController.IsListingSnapshotCurrent(
            identity,
            browse,
            resultVisible,
            nativeItemId,
            nativeListingCount,
            nativeRequestId);

    private static RemoteMarketListingSnapshotIdentity Identity() =>
        new(OperationId, ItemId, RequestId, ListingCount);

    private static MarketBoardBrowseSnapshot CompletedBrowse() =>
        new()
        {
            OperationId = OperationId,
            Owner = MarketBoardBrowseOwner.RemoteMarketController,
            Phase = MarketBoardBrowsePhase.Completed,
            ItemId = ItemId,
            RequestObserved = true,
            RequestAccepted = true,
            HeaderObserved = true,
            HeaderStatus = 0,
            ExpectedListingCount = ListingCount,
            ExpectedPageCount = 2,
            RequestId = RequestId,
            PageCount = 2,
            ListingCount = ListingCount,
            TerminalPageObserved = true,
            HistoryObserved = true,
            HistoryItemId = ItemId,
        };
}
