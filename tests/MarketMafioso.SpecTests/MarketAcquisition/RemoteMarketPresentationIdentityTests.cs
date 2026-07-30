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
    public void ExactNativeIdentity_IsCurrentWithoutOwnedBrowse()
    {
        Assert.True(IsCurrent(NativeIdentity()));
    }

    [Fact]
    public void DifferentNativeItem_HidesStaleOverlayUntilRecapture()
    {
        Assert.False(IsCurrent(NativeIdentity(), nativeItemId: 13117));
    }

    [Fact]
    public void SameItemWithDifferentNativeRequest_HidesStaleOverlayUntilRecapture()
    {
        Assert.False(IsCurrent(NativeIdentity(), nativeRequestId: RequestId + 1));
    }

    [Fact]
    public void SameItemWithDifferentNativeListingCount_HidesStaleOverlayUntilRecapture()
    {
        Assert.False(IsCurrent(NativeIdentity(), nativeListingCount: ListingCount + 1));
    }

    [Fact]
    public void MissingNativeResultOrPublishedIdentity_HidesOverlay()
    {
        Assert.False(IsCurrent(NativeIdentity(), resultVisible: false));
        Assert.False(IsCurrent(null));
    }

    [Fact]
    public void MatchingOwnedBrowse_VerifiesSnapshotForPurchase()
    {
        var browse = CompletedBrowse();
        var operationId = RemoteMarketController.GetVerifiedBrowseOperationId(
            browse,
            ItemId,
            RequestId,
            ListingCount,
            ListingCount);
        var identity = NativeIdentity(operationId);

        Assert.Equal(OperationId, operationId);
        Assert.True(RemoteMarketController.IsListingSnapshotVerifiedForPurchase(identity, browse));
    }

    [Fact]
    public void ManualNativeSearch_RemainsVisibleButIsNotVerifiedForPurchase()
    {
        var identity = NativeIdentity();

        Assert.True(IsCurrent(identity));
        Assert.False(RemoteMarketController.IsListingSnapshotVerifiedForPurchase(identity, CompletedBrowse()));
    }

    [Fact]
    public void PartialNativePrefix_RemainsVisibleButCannotVerifyForPurchase()
    {
        const int capturedListingCount = 10;
        var browse = CompletedBrowse();
        var operationId = RemoteMarketController.GetVerifiedBrowseOperationId(
            browse,
            ItemId,
            RequestId,
            ListingCount,
            capturedListingCount);
        var identity = NativeIdentity(operationId, capturedListingCount);

        Assert.True(IsCurrent(identity));
        Assert.Null(operationId);
        Assert.False(RemoteMarketController.IsListingSnapshotVerifiedForPurchase(identity, browse));
    }

    [Fact]
    public void SupersedingBrowse_InvalidatesPurchaseVerificationWithoutInvalidatingPresentation()
    {
        var identity = NativeIdentity(OperationId);
        var browse = CompletedBrowse() with { OperationId = "market-browse:2" };

        Assert.True(IsCurrent(identity));
        Assert.False(RemoteMarketController.IsListingSnapshotVerifiedForPurchase(identity, browse));
    }

    [Fact]
    public void ForeignBrowseOwner_CannotVerifyNativeListingsForPurchase()
    {
        var browse = CompletedBrowse() with { Owner = MarketBoardBrowseOwner.MarketAcquisition };

        Assert.Null(RemoteMarketController.GetVerifiedBrowseOperationId(
            browse,
            ItemId,
            RequestId,
            ListingCount,
            ListingCount));
    }

    private static bool IsCurrent(
        RemoteMarketListingSnapshotIdentity? identity,
        bool resultVisible = true,
        uint nativeItemId = ItemId,
        uint nativeListingCount = ListingCount,
        byte? nativeRequestId = RequestId) =>
        RemoteMarketController.IsListingSnapshotCurrent(
            identity,
            resultVisible,
            nativeItemId,
            nativeListingCount,
            nativeRequestId);

    private static RemoteMarketListingSnapshotIdentity NativeIdentity(
        string? operationId = null,
        int capturedListingCount = ListingCount) =>
        new(ItemId, RequestId, ListingCount, capturedListingCount, operationId);

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
