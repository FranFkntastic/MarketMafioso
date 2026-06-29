using System;

namespace MarketMafioso.MarketAcquisition;

public sealed record MarketBoardPaginationState(
    uint ItemId,
    string WorldName,
    int ReportedListingCount,
    int ReadableListingCount,
    int ListingCapacity,
    byte CurrentRequestId,
    byte NextRequestId)
{
    public bool IsTruncated =>
        ReportedListingCount > ReadableListingCount &&
        ListingCapacity > 0 &&
        ReadableListingCount >= ListingCapacity;

    public bool HasCoherentRequestIds => NextRequestId != CurrentRequestId;

    public bool CanRequestNextPage => IsTruncated && HasCoherentRequestIds;

    public bool IsContinuationOf(MarketBoardPaginationState previous) =>
        ItemId == previous.ItemId &&
        WorldName.Equals(previous.WorldName, StringComparison.OrdinalIgnoreCase);

    public static MarketBoardPaginationState FromReadResult(
        MarketBoardReadResult readResult,
        byte currentRequestId,
        byte nextRequestId) =>
        new(
            readResult.ItemId,
            readResult.WorldName,
            readResult.ReportedListingCount,
            readResult.Listings.Count,
            readResult.ListingCapacity,
            currentRequestId,
            nextRequestId);
}
