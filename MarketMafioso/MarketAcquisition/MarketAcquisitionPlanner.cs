using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.MarketAcquisition;

public static class MarketAcquisitionPlanner
{
    private static readonly IReadOnlyDictionary<string, string> NorthAmericaDataCenters =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Adamantoise"] = "Aether",
            ["Cactuar"] = "Aether",
            ["Faerie"] = "Aether",
            ["Gilgamesh"] = "Aether",
            ["Jenova"] = "Aether",
            ["Midgardsormr"] = "Aether",
            ["Sargatanas"] = "Aether",
            ["Siren"] = "Aether",

            ["Behemoth"] = "Primal",
            ["Excalibur"] = "Primal",
            ["Exodus"] = "Primal",
            ["Famfrit"] = "Primal",
            ["Hyperion"] = "Primal",
            ["Lamia"] = "Primal",
            ["Leviathan"] = "Primal",
            ["Ultros"] = "Primal",

            ["Balmung"] = "Crystal",
            ["Brynhildr"] = "Crystal",
            ["Coeurl"] = "Crystal",
            ["Diabolos"] = "Crystal",
            ["Goblin"] = "Crystal",
            ["Malboro"] = "Crystal",
            ["Mateus"] = "Crystal",
            ["Zalera"] = "Crystal",

            ["Cuchulainn"] = "Dynamis",
            ["Golem"] = "Dynamis",
            ["Halicarnassus"] = "Dynamis",
            ["Kraken"] = "Dynamis",
            ["Maduin"] = "Dynamis",
            ["Marilith"] = "Dynamis",
            ["Rafflesia"] = "Dynamis",
            ["Seraph"] = "Dynamis",
        };

    public static MarketAcquisitionPlan BuildPlan(
        MarketAcquisitionRequestView request,
        IEnumerable<MarketAcquisitionListing> listings,
        DateTimeOffset preparedAtUtc,
        string? currentWorld = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(listings);
        ValidateRequest(request);

        var sourceListings = listings.ToList();
        var diagnostics = BuildDiagnostics(request, sourceListings);
        var acceptedListings = sourceListings
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
        var batches = RouteSortBatches(
            BuildExecutableBatchPlan(request, candidateBatches),
            currentWorld);

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
            Diagnostics = diagnostics with
            {
                PlannedListingCount = batches.Sum(batch => batch.Listings.Count),
            },
            WorldBatches = batches,
        };
    }

    private static MarketAcquisitionPlanDiagnostics BuildDiagnostics(
        MarketAcquisitionRequestView request,
        IReadOnlyList<MarketAcquisitionListing> listings)
    {
        var nonZero = listings
            .Where(listing => listing.Quantity != 0 && listing.UnitPrice != 0)
            .ToList();
        var priceSupported = nonZero
            .Where(listing => listing.UnitPrice <= request.MaxUnitPrice)
            .ToList();
        var hqSupported = priceSupported
            .Where(listing => MarketAcquisitionPolicy.HqMatches(request.HqPolicy, listing.IsHq))
            .ToList();
        var worldSupported = hqSupported
            .Where(listing =>
                !request.WorldMode.Equals("CurrentWorldOnly", StringComparison.OrdinalIgnoreCase) ||
                listing.WorldName.Equals(request.TargetWorld, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return new MarketAcquisitionPlanDiagnostics
        {
            SourceListingCount = listings.Count,
            NonZeroListingCount = nonZero.Count,
            PriceSupportedListingCount = priceSupported.Count,
            HqSupportedListingCount = hqSupported.Count,
            WorldSupportedListingCount = worldSupported.Count,
        };
    }

    private static bool ListingMatchesRequest(MarketAcquisitionRequestView request, MarketAcquisitionListing listing)
    {
        if (listing.Quantity == 0 || listing.UnitPrice == 0)
            return false;

        if (listing.UnitPrice > request.MaxUnitPrice)
            return false;

        if (!MarketAcquisitionPolicy.HqMatches(request.HqPolicy, listing.IsHq))
            return false;

        if (request.WorldMode.Equals("CurrentWorldOnly", StringComparison.OrdinalIgnoreCase) &&
            !listing.WorldName.Equals(request.TargetWorld, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static void ValidateRequest(MarketAcquisitionRequestView request)
    {
        _ = MarketAcquisitionPolicy.NormalizeHqPolicy(request.HqPolicy);

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
            if (HasReachedQuantityCap(request, plannedQuantity))
                break;

            if (hasGilCap && plannedGil + batch.PlannedGil > request.MaxTotalGil)
                continue;

            batches.Add(batch);
            plannedQuantity += batch.PlannedQuantity;
            plannedGil += batch.PlannedGil;
        }

        return batches;
    }

    public static string ResolveNorthAmericaDataCenter(string worldName)
    {
        if (string.IsNullOrWhiteSpace(worldName))
            throw new InvalidOperationException("World name is required before route data center sorting.");

        return NorthAmericaDataCenters.TryGetValue(worldName.Trim(), out var dataCenter)
            ? dataCenter
            : throw new InvalidOperationException($"World {worldName} is not mapped to a North America data center.");
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
            if (HasReachedQuantityCap(request, plannedQuantity))
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
            DataCenter = ResolveNorthAmericaDataCenter(worldName),
            PlannedQuantity = plannedQuantity,
            PlannedGil = plannedGil,
            ExceedsRequestedQuantity = request.Quantity > 0 && plannedQuantity > request.Quantity,
            Listings = plannedListings,
        };
    }

    private static bool HasReachedQuantityCap(MarketAcquisitionRequestView request, uint plannedQuantity) =>
        !IsUnboundedAllBelowThreshold(request) && plannedQuantity >= request.Quantity;

    private static bool IsUnboundedAllBelowThreshold(MarketAcquisitionRequestView request) =>
        request.Quantity == 0 &&
        request.QuantityMode.Equals("AllBelowThreshold", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<MarketAcquisitionWorldBatch> RouteSortBatches(
        IReadOnlyList<MarketAcquisitionWorldBatch> batches,
        string? currentWorld)
    {
        if (string.IsNullOrWhiteSpace(currentWorld))
            return batches;

        var currentDataCenter = ResolveNorthAmericaDataCenter(currentWorld);
        var indexedBatches = batches
            .Select((batch, index) => new
            {
                Batch = batch,
                Index = index,
                DataCenter = string.IsNullOrWhiteSpace(batch.DataCenter)
                    ? ResolveNorthAmericaDataCenter(batch.WorldName)
                    : batch.DataCenter,
            })
            .ToList();

        if (indexedBatches.Count <= 1)
            return batches;

        return indexedBatches
            .OrderBy(entry => !entry.Batch.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase))
            .ThenBy(entry => !entry.DataCenter.Equals(currentDataCenter, StringComparison.OrdinalIgnoreCase))
            .ThenBy(entry => entry.DataCenter.Equals(currentDataCenter, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : entry.DataCenter,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Batch)
            .ToList();
    }
}
