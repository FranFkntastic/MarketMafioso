using Franthropy.Dalamud.Automation.MarketBoard;
using MarketMafioso.Automation.MarketBoard;

namespace MarketMafioso.MarketAcquisition.MarketBoard;

internal static class MarketListingBrowseEvidenceAdapter
{
    public static MarketBoardBrowseEvidence? FromRuntime(MarketBoardBrowseSnapshot browse) =>
        browse.IsComplete &&
        browse.Owner == MarketBoardBrowseOwner.MarketListingAcquisition &&
        !string.IsNullOrWhiteSpace(browse.OperationId) &&
        browse.RequestId is { } requestId
            ? new(
                browse.OperationId,
                browse.ItemId,
                requestId,
                browse.ExpectedListingCount,
                true)
            : null;
}
