using System;

namespace MarketMafioso.MarketAcquisition.ExactAuthority;

public static class ExactAcquisitionRouteRecoveryLifecycle
{
    public static bool ClearOrphanedState(
        bool isRouteActive,
        ExactAcquisitionRouteExecutionState? persisted,
        ExactAcquisitionExecutionContract? finalizedContract,
        IExactAcquisitionRouteExecutionStateStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (isRouteActive || persisted is null ||
            (finalizedContract is not null &&
             finalizedContract.ContractId == persisted.ContractId &&
             finalizedContract.CanonicalIntentHash == persisted.CanonicalIntentHash))
            return false;

        store.Clear();
        return true;
    }

    public static bool CanAutoResume(ExactAcquisitionRouteExecutionState? state) =>
        state?.Phase is ExactAcquisitionRouteAuthorityPhase.Active or
            ExactAcquisitionRouteAuthorityPhase.Preparing or
            ExactAcquisitionRouteAuthorityPhase.RecoveryNeeded;

    public static ExactAcquisitionRouteExecutionState PauseUnavailable(
        ExactAcquisitionRouteExecutionState state,
        string message,
        DateTimeOffset? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return state with
        {
            Phase = ExactAcquisitionRouteAuthorityPhase.Paused,
            Message = message,
            UpdatedAtUtc = nowUtc ?? DateTimeOffset.UtcNow,
        };
    }
}
