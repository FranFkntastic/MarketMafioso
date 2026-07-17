using MarketMafioso.MarketAcquisition;
using MarketMafioso.Squire.Outfitter.Acquisition;

namespace MarketMafioso.Windows.MarketAcquisitionPanels;

public enum MarketAcquisitionGuidedRoutePrimaryAction
{
    None,
    RetryOutfitterRecovery,
    ResumeManualPause,
    PauseActiveRoute,
}

public static class MarketAcquisitionGuidedRouteActionPresenter
{
    public static MarketAcquisitionGuidedRoutePrimaryAction Resolve(MarketAcquisitionRouteEngineSnapshot snapshot)
    {
        var phase = snapshot.OutfitterExecution?.Phase;
        if (phase == OutfitterRouteAuthorityPhase.RecoveryNeeded ||
            phase == OutfitterRouteAuthorityPhase.Paused && !snapshot.IsPaused)
        {
            return MarketAcquisitionGuidedRoutePrimaryAction.RetryOutfitterRecovery;
        }
        if (snapshot.IsPaused)
            return MarketAcquisitionGuidedRoutePrimaryAction.ResumeManualPause;
        if (snapshot.IsRunning)
            return MarketAcquisitionGuidedRoutePrimaryAction.PauseActiveRoute;
        return MarketAcquisitionGuidedRoutePrimaryAction.None;
    }
}
