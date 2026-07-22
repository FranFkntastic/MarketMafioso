using MarketMafioso.MarketAcquisition;
using MarketMafioso.MarketAcquisition.ExactAuthority;

namespace MarketMafioso.Windows.MarketAcquisitionPanels;

public enum MarketAcquisitionGuidedRoutePrimaryAction
{
    None,
    RetryExactAcquisitionRecovery,
    ResumeManualPause,
    PauseActiveRoute,
}

public static class MarketAcquisitionGuidedRouteActionPresenter
{
    public static MarketAcquisitionGuidedRoutePrimaryAction Resolve(MarketAcquisitionRouteEngineSnapshot snapshot)
    {
        var phase = snapshot.ExactAcquisitionExecution?.Phase;
        if (phase == ExactAcquisitionRouteAuthorityPhase.RecoveryNeeded ||
            phase == ExactAcquisitionRouteAuthorityPhase.Paused && !snapshot.IsPaused)
        {
            return MarketAcquisitionGuidedRoutePrimaryAction.RetryExactAcquisitionRecovery;
        }
        if (snapshot.IsPaused)
            return MarketAcquisitionGuidedRoutePrimaryAction.ResumeManualPause;
        if (snapshot.IsRunning)
            return MarketAcquisitionGuidedRoutePrimaryAction.PauseActiveRoute;
        return MarketAcquisitionGuidedRoutePrimaryAction.None;
    }
}
