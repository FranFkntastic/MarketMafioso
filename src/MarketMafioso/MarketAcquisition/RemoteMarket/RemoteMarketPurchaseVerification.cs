using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.MarketAcquisition.RemoteMarket;

internal sealed record RemoteMarketPendingPurchaseVerification(
    uint ItemId,
    string ItemName,
    IReadOnlyList<RemoteMarketSelectionView> IntendedSelections,
    DateTimeOffset DeadlineAtUtc);

internal sealed record RemoteMarketPendingPostPurchaseRefresh(
    uint ItemId,
    string ItemName,
    IReadOnlyList<RemoteMarketSelectionView> RemainingSelections,
    DateTimeOffset DeadlineAtUtc);

internal sealed record RemoteMarketPurchaseVerificationResult(
    bool Succeeded,
    IReadOnlyList<RemoteMarketSelectionView> RefreshedSelections,
    string? FailureReason)
{
    public static RemoteMarketPurchaseVerificationResult Success(
        IReadOnlyList<RemoteMarketSelectionView> selections) =>
        new(true, selections, null);

    public static RemoteMarketPurchaseVerificationResult Failure(string reason) =>
        new(false, Array.Empty<RemoteMarketSelectionView>(), reason);
}

internal static class RemoteMarketPurchaseVerification
{
    public static RemoteMarketPurchaseVerificationResult Reconcile(
        IReadOnlyList<RemoteMarketSelectionView> intended,
        IReadOnlyList<RemoteMarketSelectionView> refreshed)
    {
        if (intended.Count == 0)
            return RemoteMarketPurchaseVerificationResult.Failure("No purchase intent was preserved.");

        var refreshedByListingId = refreshed
            .GroupBy(selection => selection.ListingId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var reconciled = new List<RemoteMarketSelectionView>(intended.Count);
        foreach (var original in intended)
        {
            if (!refreshedByListingId.TryGetValue(original.ListingId, out var candidates) ||
                candidates.Length != 1)
            {
                return RemoteMarketPurchaseVerificationResult.Failure(
                    "A selected listing is no longer available after prices were verified.");
            }

            var current = candidates[0];
            if (!HasSamePurchaseTerms(original, current))
            {
                return RemoteMarketPurchaseVerificationResult.Failure(
                    "A selected listing changed while prices were verified.");
            }

            reconciled.Add(current);
        }

        return RemoteMarketPurchaseVerificationResult.Success(reconciled);
    }

    internal static bool HasSamePurchaseTerms(
        RemoteMarketSelectionView original,
        RemoteMarketSelectionView current) =>
        original.ListingId == current.ListingId &&
        original.ItemId == current.ItemId &&
        original.IsHighQuality == current.IsHighQuality &&
        original.Quantity == current.Quantity &&
        original.UnitPrice == current.UnitPrice &&
        original.TotalTax == current.TotalTax &&
        original.TotalGil == current.TotalGil &&
        original.RetainerId == current.RetainerId;
}
