using System;

namespace MarketMafioso.Squire.Outfitter.Acquisition;

public static class OutfitterRouteRecoveryLifecycle
{
    public static bool CanAutoResume(OutfitterRouteExecutionState? state) =>
        state?.Phase is OutfitterRouteAuthorityPhase.Active or
            OutfitterRouteAuthorityPhase.Preparing or
            OutfitterRouteAuthorityPhase.RecoveryNeeded;

    public static OutfitterRouteExecutionState PauseUnavailable(
        OutfitterRouteExecutionState state,
        string message,
        DateTimeOffset? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return state with
        {
            Phase = OutfitterRouteAuthorityPhase.Paused,
            Message = message,
            UpdatedAtUtc = nowUtc ?? DateTimeOffset.UtcNow,
        };
    }
}
