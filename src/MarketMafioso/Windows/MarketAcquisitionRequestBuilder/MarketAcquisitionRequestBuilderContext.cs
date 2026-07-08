using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

public sealed record MarketAcquisitionRequestBuilderContext(
    string CharacterName,
    string World,
    bool HasCharacterScope,
    bool CharacterScopeTemporarilyUnavailable,
    bool IsBusy,
    bool IsRouteActive,
    MarketAcquisitionClaimView? ClaimedRequest,
    MarketAcquisitionPlan? CurrentPlan,
    string? CurrentPlanHash);

public sealed record MarketAcquisitionRequestBuilderSyncOutcome(
    MarketAcquisitionRequestDocument Document,
    string StatusMessage);

public sealed record MarketAcquisitionRequestBuilderRefreshOutcome(
    MarketAcquisitionRequestDocument Document,
    MarketAcquisitionRequestDocument? RemoteDocument,
    MarketAcquisitionRequestView? RemoteRequest,
    string StatusMessage);
