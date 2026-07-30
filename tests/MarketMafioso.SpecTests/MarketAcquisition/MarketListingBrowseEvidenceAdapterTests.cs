using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.MarketAcquisition.MarketBoard;

namespace MarketMafioso.SpecTests.MarketAcquisition;

public sealed class MarketListingBrowseEvidenceAdapterTests
{
    [Fact]
    public void CompletedOwnedBrowseBecomesSharedSessionEvidence()
    {
        var evidence = MarketListingBrowseEvidenceAdapter.FromRuntime(CompletedBrowse());

        Assert.NotNull(evidence);
        Assert.Equal("market-browse:1", evidence.OperationId);
        Assert.Equal(22528u, evidence.ItemId);
        Assert.Equal(7, evidence.RequestId);
        Assert.Equal(12, evidence.ListingCount);
        Assert.True(evidence.IsComplete);
    }

    [Theory]
    [InlineData(MarketBoardBrowsePhase.AwaitingHistory, MarketBoardBrowseOwner.MarketListingAcquisition)]
    [InlineData(MarketBoardBrowsePhase.Completed, MarketBoardBrowseOwner.MarketAcquisition)]
    [InlineData(MarketBoardBrowsePhase.Completed, MarketBoardBrowseOwner.RemoteAccessProbe)]
    public void IncompleteOrForeignBrowseCannotVerifyAListingSession(
        MarketBoardBrowsePhase phase,
        MarketBoardBrowseOwner owner)
    {
        var browse = CompletedBrowse() with { Phase = phase, Owner = owner };

        Assert.Null(MarketListingBrowseEvidenceAdapter.FromRuntime(browse));
    }

    [Theory]
    [InlineData((int)MarketListingPurchasePhase.Sending, true)]
    [InlineData((int)MarketListingPurchasePhase.Sent, true)]
    [InlineData((int)MarketListingPurchasePhase.AwaitingConfirmation, false)]
    [InlineData((int)MarketListingPurchasePhase.Confirmed, false)]
    [InlineData((int)MarketListingPurchasePhase.Failed, false)]
    public void PurchasePacketCorrelationIncludesTheSynchronousSendPhase(
        int phase,
        bool expected) =>
        Assert.Equal(
            expected,
            MarketListingPurchaseCoordinator.IsPurchaseRequestCorrelatablePhase(
                (MarketListingPurchasePhase)phase));

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
            MarketListingBrowseCoordinator.ShouldResetTerminalLatch(
                previousOperationId,
                nextOperationId));

    private static MarketBoardBrowseSnapshot CompletedBrowse() =>
        new()
        {
            OperationId = "market-browse:1",
            Owner = MarketBoardBrowseOwner.MarketListingAcquisition,
            Phase = MarketBoardBrowsePhase.Completed,
            ItemId = 22528,
            RequestObserved = true,
            RequestAccepted = true,
            HeaderObserved = true,
            HeaderStatus = 0,
            ExpectedListingCount = 12,
            ExpectedPageCount = 2,
            RequestId = 7,
            PageCount = 2,
            ListingCount = 12,
            TerminalPageObserved = true,
            HistoryObserved = true,
            HistoryItemId = 22528,
        };
}
