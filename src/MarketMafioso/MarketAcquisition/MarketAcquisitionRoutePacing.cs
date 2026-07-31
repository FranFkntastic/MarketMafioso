using System;

namespace MarketMafioso.MarketAcquisition;

internal static class MarketAcquisitionRoutePacing
{
    public static readonly TimeSpan PurchaseEvidencePollInterval = TimeSpan.FromMilliseconds(200);

    public static bool ShouldCloseMarketBoardForNextStop(
        string currentWorld,
        MarketAcquisitionGuidedRouteStop? nextStop) =>
        nextStop == null ||
        !nextStop.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase);
}
