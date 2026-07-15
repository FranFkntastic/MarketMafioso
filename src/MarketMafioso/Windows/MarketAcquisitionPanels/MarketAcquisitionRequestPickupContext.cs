using System.Collections.Generic;
using System.Numerics;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Windows.MarketAcquisitionPanels;

internal sealed record MarketAcquisitionRequestPickupContext(
    bool CompactWhenClaimed,
    bool RouteOwnsUi,
    MarketAcquisitionClaimView? ClaimedRequest,
    IReadOnlyList<MarketAcquisitionRequestView> PendingRequests,
    IReadOnlySet<uint> WorkbenchItemIds,
    bool IsBusy,
    bool HasApiKey,
    bool HasCharacterScope,
    string CharacterName,
    string World,
    bool IsExpectedCharacterScopeGap,
    string VisibleStatus,
    Vector4 VisibleStatusColor);
