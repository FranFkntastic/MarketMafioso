using MarketMafioso.Windows;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionClaimStatusTests
{
    [Fact]
    public void ShouldFailWorldPurchaseBatchOnNoCandidate_ReturnsTrueForVisibleCacheExhausted()
    {
        var candidatePlan = new MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlan
        {
            Status = "VisibleCacheExhausted",
        };

        Assert.True(MainWindow.ShouldFailWorldPurchaseBatchOnNoCandidate(candidatePlan));
    }

    [Theory]
    [InlineData("NoSafeListings")]
    [InlineData("UnderProcured")]
    [InlineData("Ready")]
    public void ShouldFailWorldPurchaseBatchOnNoCandidate_ReturnsFalseForOtherCandidateStatuses(string status)
    {
        var candidatePlan = new MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlan
        {
            Status = status,
        };

        Assert.False(MainWindow.ShouldFailWorldPurchaseBatchOnNoCandidate(candidatePlan));
    }

    [Theory]
    [InlineData("AcceptedInPlugin")]
    [InlineData("Running")]
    [InlineData("Failed")]
    public void CanPrepareAcquisitionPlanForStatus_AllowsAcceptedRunningAndFailed(string status)
    {
        Assert.True(MainWindow.CanPrepareAcquisitionPlanForStatus(status));
    }

    [Theory]
    [InlineData("Claimed")]
    [InlineData("PendingPickup")]
    [InlineData("Complete")]
    [InlineData("Rejected")]
    [InlineData("Cancelled")]
    public void CanPrepareAcquisitionPlanForStatus_BlocksStatusesThatNeedClaimOrAreTerminal(string status)
    {
        Assert.False(MainWindow.CanPrepareAcquisitionPlanForStatus(status));
    }
}
