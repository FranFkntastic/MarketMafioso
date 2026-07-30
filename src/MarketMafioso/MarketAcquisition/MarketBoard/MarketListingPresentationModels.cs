using System.Collections.Generic;

namespace MarketMafioso.MarketAcquisition.MarketBoard;

internal sealed record MarketListingRowView(
    ulong ListingId,
    uint ItemId,
    string ItemName,
    bool IsHighQuality,
    uint Quantity,
    uint UnitPrice,
    uint TotalTax,
    ulong TotalGil,
    byte MateriaCount,
    ulong RetainerId,
    string RetainerName,
    bool AlreadyPurchased,
    MarketListingBatchStatus? BatchStatus);

internal sealed record MarketListingBatchView(
    int TotalCount,
    int CompletedCount,
    int FailedCount,
    bool Active);

internal sealed record MarketListingVerificationView(
    int ListingCount,
    long Quantity,
    ulong TotalGil);

internal sealed record MarketListingEconomics(
    uint CheapestUnitPrice,
    uint MedianUnitPrice,
    double MeanUnitPrice,
    double? TrendDelta);

internal sealed record MarketListingNativePresentation(
    bool ResultAddonVisible,
    bool AgentActive,
    uint ItemId,
    uint ListingCount,
    byte? RequestId,
    bool MatchesSnapshot);

internal sealed record MarketListingView(
    long Revision,
    bool Available,
    IReadOnlyList<MarketListingRowView> Listings,
    int ExpectedListingCount,
    MarketListingBatchView? Batch,
    MarketListingVerificationView? Verification,
    string? LastOutcome,
    string? ContextBlockReason,
    uint? GilOnHand,
    CmbMarketContext? MarketContext,
    MarketListingEconomics? Economics,
    string? MarketContextSummary,
    string BrowseMessage);
