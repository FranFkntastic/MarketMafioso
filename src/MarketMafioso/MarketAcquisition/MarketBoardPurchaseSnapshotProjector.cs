using System;
using System.Linq;
using MarketMafioso.Automation.MarketBoard;

namespace MarketMafioso.MarketAcquisition;

internal static class MarketBoardPurchaseSnapshotProjector
{
    public static MarketBoardReadResult ApplyConfirmedPurchase(
        MarketBoardReadResult snapshot,
        MarketBoardPurchaseCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(candidate);

        if (!snapshot.IsFresh ||
            snapshot.ItemId != candidate.ItemId ||
            !snapshot.WorldName.Equals(candidate.WorldName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "A confirmed purchase can only advance its matching fresh market-board snapshot.");
        }

        var purchasedIndex = snapshot.Listings
            .Select((listing, index) => (listing, index))
            .FirstOrDefault(pair => IsExactListing(pair.listing, candidate))
            .index;
        if (purchasedIndex < 0 ||
            purchasedIndex >= snapshot.Listings.Count ||
            !IsExactListing(snapshot.Listings[purchasedIndex], candidate))
        {
            throw new InvalidOperationException(
                $"Confirmed listing {candidate.ListingId} is absent from the authoritative purchase snapshot.");
        }

        var remaining = snapshot.Listings
            .Where((_, index) => index != purchasedIndex)
            .ToArray();
        var reportedListingCount = Math.Max(
            remaining.Length,
            Math.Max(0, snapshot.ReportedListingCount - 1));
        var isListingCountTruncated = reportedListingCount > remaining.Length;

        return snapshot with
        {
            Status = remaining.Length == 0 ? "NoListings" : "Ready",
            Message = remaining.Length == 0
                ? $"Confirmed purchase removed listing {candidate.ListingId}; no remembered listings remain."
                : $"Confirmed purchase removed listing {candidate.ListingId}; continuing from {remaining.Length:N0} remembered listing(s).",
            ReadState = isListingCountTruncated
                ? MarketBoardListingReadState.FreshPartial
                : MarketBoardListingReadState.FreshComplete,
            ReportedListingCount = reportedListingCount,
            IsAtListingCapacity =
                snapshot.ListingCapacity > 0 &&
                remaining.Length >= snapshot.ListingCapacity,
            IsListingCountTruncated = isListingCountTruncated,
            Listings = remaining,
        };
    }

    private static bool IsExactListing(
        MarketBoardLiveListing listing,
        MarketBoardPurchaseCandidate candidate) =>
        listing.ItemId == candidate.ItemId &&
        listing.WorldName.Equals(candidate.WorldName, StringComparison.OrdinalIgnoreCase) &&
        listing.ListingId.Equals(candidate.ListingId, StringComparison.Ordinal) &&
        listing.RetainerId.Equals(candidate.RetainerId, StringComparison.Ordinal) &&
        listing.UnitPrice == candidate.UnitPrice &&
        listing.Quantity == candidate.Quantity &&
        listing.IsHq == candidate.IsHq;
}
