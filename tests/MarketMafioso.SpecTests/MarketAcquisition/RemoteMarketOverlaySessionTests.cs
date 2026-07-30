using MarketMafioso.MarketAcquisition.RemoteMarket;

namespace MarketMafioso.SpecTests.MarketAcquisition;

public sealed class RemoteMarketOverlaySessionTests
{
    [Fact]
    public void AutomaticVerificationKeepsOverlayActiveWhileNativeResultIsHidden()
    {
        var session = ActiveSession();

        session.ObserveNativeState(
            resultVisible: false,
            resultMatchesSnapshot: false,
            searchVisible: false,
            agentActive: false,
            recoveryActive: true);

        Assert.True(session.IsActive);
    }

    [Fact]
    public void ActiveAgentKeepsTruthfulListingsPresentedAcrossTransientResultHide()
    {
        var session = ActiveSession();

        session.ObserveNativeState(
            resultVisible: false,
            resultMatchesSnapshot: false,
            searchVisible: false,
            agentActive: true,
            recoveryActive: false);

        Assert.True(session.IsActive);
    }

    [Fact]
    public void ReturningToSearchEndsListingPresentation()
    {
        var session = ActiveSession();

        session.ObserveNativeState(
            resultVisible: false,
            resultMatchesSnapshot: false,
            searchVisible: true,
            agentActive: true,
            recoveryActive: false);

        Assert.False(session.IsActive);
    }

    [Fact]
    public void ClosingAgentEndsListingPresentationOutsideRecovery()
    {
        var session = ActiveSession();

        session.ObserveNativeState(
            resultVisible: false,
            resultMatchesSnapshot: false,
            searchVisible: false,
            agentActive: false,
            recoveryActive: false);

        Assert.False(session.IsActive);
    }

    private static RemoteMarketOverlaySession ActiveSession()
    {
        var session = new RemoteMarketOverlaySession();
        session.ObserveSnapshot();
        return session;
    }

    [Fact]
    public void DifferentVisibleNativeResultHidesStaleSnapshotUntilRecapture()
    {
        var session = ActiveSession();

        session.ObserveNativeState(
            resultVisible: true,
            resultMatchesSnapshot: false,
            searchVisible: false,
            agentActive: true,
            recoveryActive: false);

        Assert.False(session.IsActive);
    }
}
