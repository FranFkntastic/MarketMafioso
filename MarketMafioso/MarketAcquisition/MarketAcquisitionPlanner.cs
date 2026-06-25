using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.MarketAcquisition;

public static class MarketAcquisitionPlanner
{
    public static MarketAcquisitionPlan BuildPlan(
        MarketAcquisitionRequestView request,
        IEnumerable<MarketAcquisitionListing> listings,
        DateTimeOffset preparedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(listings);
        ValidateRequest(request);

        var acceptedListings = listings
            .Where(listing => ListingMatchesRequest(request, listing))
            .OrderBy(listing => listing.UnitPrice)
            .ThenByDescending(listing => listing.Quantity)
            .ThenBy(listing => listing.LastReviewTimeUtc)
            .ThenBy(listing => listing.WorldName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(listing => listing.RetainerName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var candidateBatches = acceptedListings
            .GroupBy(listing => listing.WorldName, StringComparer.OrdinalIgnoreCase)
            .Select(group => BuildWorldBatch(request, group.Key, group))
            .Where(batch => batch.Listings.Count > 0)
            .OrderByDescending(batch => batch.PlannedQuantity >= request.Quantity)
            .ThenBy(batch => batch.ExceedsRequestedQuantity)
            .ThenByDescending(batch => batch.PlannedQuantity)
            .ThenBy(batch => batch.PlannedGil)
            .ThenBy(batch => batch.Listings[0].UnitPrice)
            .ThenBy(batch => batch.WorldName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var batches = BuildExecutableBatchPlan(request, candidateBatches);

        var totalQuantity = (uint)batches.Sum(batch => batch.PlannedQuantity);
        var totalGil = (uint)batches.Sum(batch => batch.PlannedGil);

        return new MarketAcquisitionPlan
        {
            RequestId = request.Id,
            Status = batches.Count == 0 ? "NoSupportedListings" : "Ready",
            WorldMode = request.WorldMode,
            ItemId = request.ItemId,
            RequestedQuantity = request.Quantity,
            PlannedQuantity = totalQuantity,
            PlannedGil = totalGil,
            PreparedAtUtc = preparedAtUtc,
            WorldBatches = batches,
        };
    }

    private static bool ListingMatchesRequest(MarketAcquisitionRequestView request, MarketAcquisitionListing listing)
    {
        if (listing.Quantity == 0 || listing.UnitPrice == 0)
            return false;

        if (listing.UnitPrice > request.MaxUnitPrice)
            return false;

        if (!HqMatches(request.HqPolicy, listing.IsHq))
            return false;

        if (request.WorldMode.Equals("CurrentWorldOnly", StringComparison.OrdinalIgnoreCase) &&
            !listing.WorldName.Equals(request.TargetWorld, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static bool HqMatches(string hqPolicy, bool isHq) =>
        hqPolicy switch
        {
            "HqOnly" => isHq,
            "NqOnly" => !isHq,
            "Either" => true,
            _ => throw new InvalidOperationException($"Unknown HQ policy {hqPolicy}."),
        };

    private static void ValidateRequest(MarketAcquisitionRequestView request)
    {
        if (request.HqPolicy is not ("Either" or "HqOnly" or "NqOnly"))
            throw new InvalidOperationException($"Unknown HQ policy {request.HqPolicy}.");

        if (request.WorldMode is not ("Recommended" or "CurrentWorldOnly" or "Selected" or "AllWorldSweep"))
            throw new InvalidOperationException($"Unknown world mode {request.WorldMode}.");

        if (request.WorldMode == "Selected")
            throw new InvalidOperationException("Selected world mode requires selected worlds in the request payload before it can be planned.");
    }

    private static IReadOnlyList<MarketAcquisitionWorldBatch> BuildExecutableBatchPlan(
        MarketAcquisitionRequestView request,
        IReadOnlyList<MarketAcquisitionWorldBatch> candidates)
    {
        var batches = new List<MarketAcquisitionWorldBatch>();
        uint plannedQuantity = 0;
        uint plannedGil = 0;
        var hasGilCap = request.MaxTotalGil > 0;

        foreach (var batch in candidates)
        {
            if (plannedQuantity >= request.Quantity)
                break;

            if (hasGilCap && plannedGil + batch.PlannedGil > request.MaxTotalGil)
                continue;

            batches.Add(batch);
            plannedQuantity += batch.PlannedQuantity;
            plannedGil += batch.PlannedGil;
        }

        return batches;
    }

    private static MarketAcquisitionWorldBatch BuildWorldBatch(
        MarketAcquisitionRequestView request,
        string worldName,
        IEnumerable<MarketAcquisitionListing> listings)
    {
        var plannedListings = new List<MarketAcquisitionPlannedListing>();
        uint plannedQuantity = 0;
        uint plannedGil = 0;
        var hasGilCap = request.MaxTotalGil > 0;

        foreach (var listing in listings)
        {
            if (plannedQuantity >= request.Quantity)
                break;

            if (hasGilCap && plannedGil + listing.TotalGil > request.MaxTotalGil)
                continue;

            plannedListings.Add(new MarketAcquisitionPlannedListing
            {
                ListingId = listing.ListingId,
                RetainerName = listing.RetainerName,
                RetainerId = listing.RetainerId,
                Quantity = listing.Quantity,
                UnitPrice = listing.UnitPrice,
                TotalGil = listing.TotalGil,
                IsHq = listing.IsHq,
                LastReviewTimeUtc = listing.LastReviewTimeUtc,
            });
            plannedQuantity += listing.Quantity;
            plannedGil += listing.TotalGil;
        }

        return new MarketAcquisitionWorldBatch
        {
            WorldName = worldName,
            PlannedQuantity = plannedQuantity,
            PlannedGil = plannedGil,
            ExceedsRequestedQuantity = plannedQuantity > request.Quantity,
            Listings = plannedListings,
        };
    }
}
