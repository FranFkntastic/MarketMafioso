using System;

namespace MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

public sealed record MarketAcquisitionWorkbenchFinalizationState(
    int LineCount,
    bool IsDraftValid,
    string? FirstDraftError,
    bool HasCharacterScope,
    bool IsBusy,
    bool IsRouteActive,
    bool IsSynchronizing,
    string SyncStatus,
    string VisibleSyncStatus,
    string? ClaimStatus,
    bool HasClaimedRequest,
    bool HasCurrentPlan,
    bool IsCurrentPlanStale,
    string WorkspaceStatus,
    ulong TotalSpendCeiling,
    uint TargetQuantityTotal);

public sealed record MarketAcquisitionWorkbenchFinalizationPresentation(
    bool CanFinalize,
    string Title,
    string Detail);

public static class MarketAcquisitionWorkbenchFinalizationPresenter
{
    public static MarketAcquisitionWorkbenchFinalizationPresentation Build(
        MarketAcquisitionWorkbenchFinalizationState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var canFinalize = state.LineCount > 0 &&
                          state.IsDraftValid &&
                          state.HasCharacterScope &&
                          !state.IsBusy &&
                          !state.IsRouteActive;

        return new MarketAcquisitionWorkbenchFinalizationPresentation(
            canFinalize,
            BuildTitle(state),
            BuildDetail(state));
    }

    private static string BuildTitle(MarketAcquisitionWorkbenchFinalizationState state)
    {
        if (state.LineCount == 0)
            return "Add at least one item";
        if (!state.HasCharacterScope)
            return "Character unavailable";
        if (state.IsRouteActive)
            return "Route in progress";
        if (!state.IsDraftValid)
            return "Complete the Workbench";
        if (state.IsBusy)
            return "Updating work order...";
        if (state.HasCurrentPlan && !state.IsCurrentPlanStale)
            return "Plan finalized";
        return state.HasClaimedRequest ? "Ready to finalize" : "Ready to finalize locally";
    }

    private static string BuildDetail(MarketAcquisitionWorkbenchFinalizationState state)
    {
        if (!state.IsDraftValid)
            return state.FirstDraftError ?? "Workbench input needs attention.";
        if (state.IsBusy)
            return state.WorkspaceStatus;

        var spendText = state.TotalSpendCeiling == 0
            ? "spend ceiling unset"
            : $"up to {state.TotalSpendCeiling:N0} gil";
        var targetText = state.TargetQuantityTotal == 0
            ? "no target-quantity lines"
            : $"{state.TargetQuantityTotal:N0} target units";
        var hosting = state.HasClaimedRequest
            ? "hosted work order attached"
            : "local execution; hosting optional";
        return $"{state.LineCount:N0} line(s); {targetText}; {spendText}; {hosting}.";
    }
}
