using MarketMafioso.MarketAcquisition.MarketBoard;

namespace MarketMafioso.SpecTests.MarketAcquisition;

public sealed class MarketListingPresentationSessionTests
{
    [Fact]
    public void AutomaticVerificationKeepsPresentationActiveWhileNativeResultIsHidden()
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

    private static MarketListingPresentationSession ActiveSession()
    {
        var session = new MarketListingPresentationSession();
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
