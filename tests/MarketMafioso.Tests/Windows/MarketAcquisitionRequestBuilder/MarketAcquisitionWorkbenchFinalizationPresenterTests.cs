using MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

namespace MarketMafioso.Tests.Windows.MarketAcquisitionRequestBuilder;

public sealed class MarketAcquisitionWorkbenchFinalizationPresenterTests
{
    [Fact]
    public void Build_EnablesFinalizationForOwnedCleanWorkbench()
    {
        var result = MarketAcquisitionWorkbenchFinalizationPresenter.Build(ReadyState());

        Assert.True(result.CanFinalize);
        Assert.Equal("Ready to finalize", result.Title);
        Assert.Equal("2 line(s); 12 target units; up to 45,000 gil.", result.Detail);
    }

    [Fact]
    public void Build_PrioritizesDraftErrorOverSummary()
    {
        var result = MarketAcquisitionWorkbenchFinalizationPresenter.Build(
            ReadyState() with { IsDraftValid = false, FirstDraftError = "Line 2 needs a price ceiling." });

        Assert.False(result.CanFinalize);
        Assert.Equal("Complete the Workbench", result.Title);
        Assert.Equal("Line 2 needs a price ceiling.", result.Detail);
    }

    [Fact]
    public void Build_ReportsFailedSynchronizationWithoutLeakingLifecyclePlumbing()
    {
        var result = MarketAcquisitionWorkbenchFinalizationPresenter.Build(
            ReadyState() with { SyncStatus = "SyncFailed", VisibleSyncStatus = "Could not save changes." });

        Assert.False(result.CanFinalize);
        Assert.Equal("Workbench needs attention", result.Title);
        Assert.Equal("Could not save changes.", result.Detail);
    }

    [Fact]
    public void Build_RecognizesCurrentFinalizedPlan()
    {
        var result = MarketAcquisitionWorkbenchFinalizationPresenter.Build(
            ReadyState() with { HasCurrentPlan = true, IsCurrentPlanStale = false });

        Assert.Equal("Plan finalized", result.Title);
    }

    private static MarketAcquisitionWorkbenchFinalizationState ReadyState() => new(
        LineCount: 2,
        IsDraftValid: true,
        FirstDraftError: null,
        HasCharacterScope: true,
        IsBusy: false,
        IsRouteActive: false,
        IsSynchronizing: false,
        SyncStatus: "SyncedClean",
        VisibleSyncStatus: "Saved",
        ClaimStatus: "Claimed",
        HasClaimedRequest: true,
        HasCurrentPlan: false,
        IsCurrentPlanStale: true,
        WorkspaceStatus: "Idle",
        TotalSpendCeiling: 45_000,
        TargetQuantityTotal: 12);
}
