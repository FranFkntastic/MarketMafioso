using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Windows.MarketAcquisitionPanels;

public sealed record MarketAcquisitionWorkbenchCompositionContext(
    MarketAcquisitionRequestDocument Document,
    string CharacterName,
    string World,
    bool HasCharacterScope,
    bool IsBusy,
    bool IsRouteActive);
