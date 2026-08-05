using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.MarketAcquisition.MarketBoard;

internal sealed record PendingMarketListingPurchaseVerification(
    uint ItemId,
    string ItemName,
    IReadOnlyList<MarketListingSelection> IntendedSelections,
    DateTimeOffset DeadlineAtUtc);

internal sealed record MarketListingPurchaseVerificationResult(
    bool Succeeded,
    IReadOnlyList<MarketListingSelection> RefreshedSelections,
    string? FailureReason)
{
    public static MarketListingPurchaseVerificationResult Success(
        IReadOnlyList<MarketListingSelection> selections) =>
        new(true, selections, null);

    public static MarketListingPurchaseVerificationResult Failure(string reason) =>
        new(false, Array.Empty<MarketListingSelection>(), reason);
}

internal static class MarketListingPurchaseVerification
{
    public static MarketListingPurchaseVerificationResult Reconcile(
        IReadOnlyList<MarketListingSelection> intended,
        IReadOnlyList<MarketListingSelection> refreshed)
    {
        if (intended.Count == 0)
            return MarketListingPurchaseVerificationResult.Failure("No purchase intent was preserved.");

        var refreshedByListingId = refreshed
            .GroupBy(selection => selection.ListingId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var reconciled = new List<MarketListingSelection>(intended.Count);
        foreach (var original in intended)
        {
            if (!refreshedByListingId.TryGetValue(original.ListingId, out var candidates) ||
                candidates.Length != 1)
            {
                return MarketListingPurchaseVerificationResult.Failure(
                    "A selected listing is no longer available after prices were verified.");
            }

            var current = candidates[0];
            if (!HasSamePurchaseTerms(original, current))
            {
                return MarketListingPurchaseVerificationResult.Failure(
                    "A selected listing changed while prices were verified.");
            }

            reconciled.Add(current);
        }

        return MarketListingPurchaseVerificationResult.Success(reconciled);
    }

    internal static bool HasSamePurchaseTerms(
        MarketListingSelection original,
        MarketListingSelection current) =>
        original.ListingId == current.ListingId &&
        original.ItemId == current.ItemId &&
        original.IsHighQuality == current.IsHighQuality &&
        original.Quantity == current.Quantity &&
        original.UnitPrice == current.UnitPrice &&
        original.TotalTax == current.TotalTax &&
        original.TotalGil == current.TotalGil &&
        original.RetainerId == current.RetainerId;
}
