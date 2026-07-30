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
        Assert.False(RemoteMarketController.RequiresAutomaticPurchaseVerification(identity, browse));
    }

    [Fact]
    public void ConfirmedPurchase_AdvancesTheVerifiedBrowseInPlace()
    {
        var browse = CompletedBrowse();
        var identity = NativeIdentity(OperationId);

        var advanced = RemoteMarketController.AdvanceConfirmedPurchase(identity);

        Assert.NotNull(advanced);
        Assert.Equal(ListingCount - 1, advanced.CurrentListingCount);
        Assert.Equal(ListingCount - 1, advanced.CapturedListingCount);
        Assert.Equal(ListingCount, advanced.BrowseListingCount);
        Assert.True(IsCurrent(advanced, nativeListingCount: ListingCount - 1));
        Assert.True(RemoteMarketController.IsListingSnapshotVerifiedForPurchase(advanced, browse));
        Assert.False(RemoteMarketController.RequiresAutomaticPurchaseVerification(advanced, browse));
    }

    [Fact]
    public void UnverifiedOrPartialSnapshot_CannotAdvanceAfterPurchase()
    {
        Assert.Null(RemoteMarketController.AdvanceConfirmedPurchase(NativeIdentity()));
        Assert.Null(RemoteMarketController.AdvanceConfirmedPurchase(
            NativeIdentity(OperationId, capturedListingCount: ListingCount - 1)));
    }

    [Fact]
    public void NativeRefresh_PreservesVerifiedLineageAcrossRowReordering()
    {
        var identity = new RemoteMarketListingSnapshotIdentity(
            ItemId,
            RequestId,
            2,
            2,
            OperationId,
            3);
        var nativeIdentity = new RemoteMarketNativeListingIdentity(ItemId, RequestId, 2);

        Assert.True(RemoteMarketController.PreservesVerifiedListingLineage(
            identity,
            [Listing(10), Listing(20)],
            nativeIdentity,
            [Listing(20), Listing(10)]));
        Assert.False(RemoteMarketController.PreservesVerifiedListingLineage(
            identity,
            [Listing(10), Listing(20)],
            nativeIdentity,
            [Listing(10), Listing(30)]));
    }

    [Fact]
    public void InconsistentSameRequestRefresh_RetainsTheConfirmedDerivedRevision()
    {
        var identity = new RemoteMarketListingSnapshotIdentity(
            ItemId,
            RequestId,
            2,
            2,
            OperationId,
            3);

        Assert.True(RemoteMarketController.RetainsDerivedListingLineage(
            identity,
            new RemoteMarketNativeListingIdentity(ItemId, RequestId, 2)));
        Assert.False(RemoteMarketController.RetainsDerivedListingLineage(
            identity,
            new RemoteMarketNativeListingIdentity(ItemId, RequestId + 1, 2)));
        Assert.False(RemoteMarketController.RetainsDerivedListingLineage(
            NativeIdentity(OperationId),
            new RemoteMarketNativeListingIdentity(ItemId, RequestId, ListingCount)));
    }

    [Fact]
    public void ManualNativeSearch_RemainsVisibleButIsNotVerifiedForPurchase()
    {
        var identity = NativeIdentity();

        Assert.True(IsCurrent(identity));
        Assert.False(RemoteMarketController.IsListingSnapshotVerifiedForPurchase(identity, CompletedBrowse()));
        Assert.True(RemoteMarketController.RequiresAutomaticPurchaseVerification(identity, CompletedBrowse()));
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

    [Theory]
    [InlineData((int)RemoteMarketPurchasePhase.Sending, true)]
    [InlineData((int)RemoteMarketPurchasePhase.Sent, true)]
    [InlineData((int)RemoteMarketPurchasePhase.AwaitingConfirmation, false)]
    [InlineData((int)RemoteMarketPurchasePhase.Confirmed, false)]
    [InlineData((int)RemoteMarketPurchasePhase.Failed, false)]
    public void PurchasePacketCorrelationIncludesTheSynchronousSendPhase(
        int phase,
        bool expected) =>
        Assert.Equal(
            expected,
            RemoteMarketController.IsPurchaseRequestCorrelatablePhase(
                (RemoteMarketPurchasePhase)phase));

    [Theory]
    [InlineData(null, "market-browse:1", true)]
    [InlineData("market-browse:1", "market-browse:1", false)]
    [InlineData("market-browse:1", "market-browse:2", true)]
    public void TerminalBrowseLatchOnlyResetsForANewOperation(
        string? previousOperationId,
        string nextOperationId,
        bool expected) =>
        Assert.Equal(
            expected,
            RemoteMarketController.ShouldResetTrackedBrowseTerminalLatch(
                previousOperationId,
                nextOperationId));

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
        int capturedListingCount = ListingCount,
        int currentListingCount = ListingCount,
        int browseListingCount = ListingCount) =>
        new(
            ItemId,
            RequestId,
            currentListingCount,
            capturedListingCount,
            operationId,
            browseListingCount);

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

    private static RemoteMarketListingView Listing(ulong listingId) =>
        new(
            listingId,
            ItemId,
            "Test Item",
            false,
            1,
            1,
            0,
            1,
            0,
            "Test Retainer",
            false,
            null);
}
