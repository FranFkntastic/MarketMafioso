using MarketMafioso.Windows;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionClaimStatusTests
{
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
