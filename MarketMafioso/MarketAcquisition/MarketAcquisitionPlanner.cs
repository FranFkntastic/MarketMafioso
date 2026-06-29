using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.MarketAcquisition;

public static class MarketAcquisitionPlanner
{
    private static readonly string[] NorthAmericaDataCenterOrder =
    [
        "Aether",
        "Primal",
        "Crystal",
        "Dynamis",
    ];

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

        var requestLines = BuildRequestLines(request);
        var sourceListings = listings.ToList();
        var diagnostics = BuildDiagnostics(request, requestLines, sourceListings);
        var selectedSubtasks = new List<MarketAcquisitionWorldItemSubtask>();
        var planLines = new List<MarketAcquisitionPlanLine>();
        var isAllWorldSweep = request.WorldMode.Equals("AllWorldSweep", StringComparison.OrdinalIgnoreCase);
        var sweepWorlds = isAllWorldSweep
            ? ResolveSweepWorlds(request, currentWorld)
            : [];

        foreach (var line in requestLines)
        {
            var matchingListings = sourceListings
                .Where(listing => ListingMatchesLine(request, line, listing))
                .OrderBy(listing => listing.UnitPrice)
                .ThenByDescending(listing => listing.Quantity)
                .ThenBy(listing => listing.LastReviewTimeUtc)
                .ThenBy(listing => listing.WorldName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(listing => listing.RetainerName, StringComparer.OrdinalIgnoreCase)
                .GroupBy(listing => listing.WorldName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.AsEnumerable(),
                    StringComparer.OrdinalIgnoreCase);

            var candidates = isAllWorldSweep
                ? sweepWorlds
                    .Select(world =>
                    {
                        var hasListings = matchingListings.TryGetValue(world, out var worldListings);
                        return BuildWorldSubtask(
                            line,
                            world,
                            hasListings ? worldListings! : [],
                            hasListings ? "Planned" : "SweepProbe");
                    })
                    .ToList()
                : matchingListings
                    .Select(group => BuildWorldSubtask(line, group.Key, group.Value))
                    .Where(subtask => subtask.Listings.Count > 0)
                .OrderByDescending(subtask => LineSatisfiesQuantity(line, subtask.PlannedQuantity))
                .ThenBy(subtask => subtask.ExceedsRequestedQuantity)
                .ThenByDescending(subtask => subtask.PlannedQuantity)
                .ThenBy(subtask => subtask.PlannedGil)
                .ThenBy(subtask => subtask.Listings.Count == 0 ? uint.MaxValue : subtask.Listings[0].UnitPrice)
                .ThenBy(subtask => subtask.WorldName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var lineSubtasks = BuildExecutableLinePlan(line, candidates, isAllWorldSweep);
            selectedSubtasks.AddRange(lineSubtasks);
            planLines.Add(new MarketAcquisitionPlanLine
            {
                LineId = line.LineId,
                Ordinal = line.Ordinal,
                ItemId = line.ItemId,
                ItemName = line.ItemName,
                QuantityMode = line.QuantityMode,
                RequestedQuantity = line.Quantity,
                HqPolicy = line.HqPolicy,
                MaxUnitPrice = line.MaxUnitPrice,
                GilCap = line.MaxTotalGil,
                Status = lineSubtasks.Count == 0 ? "NoSupportedListings" : "Ready",
                PlannedQuantity = (uint)lineSubtasks.Sum(subtask => subtask.PlannedQuantity),
                PlannedGil = (uint)lineSubtasks.Sum(subtask => subtask.PlannedGil),
            });
        }

        var batches = RouteSortBatches(
            selectedSubtasks
                .GroupBy(subtask => subtask.WorldName, StringComparer.OrdinalIgnoreCase)
                .Select(group => BuildWorldBatch(group.Key, group))
                .ToList(),
            currentWorld);

        var primaryLine = requestLines[0];
        var totalQuantity = (uint)batches.Sum(batch => batch.PlannedQuantity);
        var totalGil = (uint)batches.Sum(batch => batch.PlannedGil);

        return new MarketAcquisitionPlan
        {
            RequestId = request.Id,
            Status = batches.Count == 0 ? "NoSupportedListings" : "Ready",
            WorldMode = request.WorldMode,
            ItemId = primaryLine.ItemId,
            RequestedQuantity = (uint)requestLines.Sum(line => line.Quantity),
            PlannedQuantity = totalQuantity,
            PlannedGil = totalGil,
            PreparedAtUtc = preparedAtUtc,
            Diagnostics = diagnostics with
            {
                PlannedListingCount = batches.Sum(batch => batch.Listings.Count),
            },
            Lines = planLines,
            WorldBatches = batches,
        };
    }

    private static IReadOnlyList<PlannerLine> BuildRequestLines(MarketAcquisitionRequestView request)
    {
        var lines = request.Lines.Count == 0
            ? new[]
            {
                new MarketAcquisitionBatchLineView
                {
                    LineId = request.Id,
                    Ordinal = 0,
                    ItemId = request.ItemId,
                    ItemName = request.ItemName,
                    QuantityMode = request.QuantityMode,
                    TargetQuantity = request.Quantity,
                    MaxQuantity = request.Quantity,
                    HqPolicy = request.HqPolicy,
                    MaxUnitPrice = request.MaxUnitPrice,
                    GilCap = request.MaxTotalGil,
                },
            }.ToList()
            : request.Lines
                .OrderBy(line => line.Ordinal)
                .ToList();

        if (lines.Count == 0)
            throw new InvalidOperationException("At least one acquisition line is required before planning.");

        return lines
            .Select(line => new PlannerLine
            {
                LineId = string.IsNullOrWhiteSpace(line.LineId) ? request.Id : line.LineId,
                Ordinal = line.Ordinal,
                ItemId = line.ItemId,
                ItemName = line.ItemName,
                QuantityMode = NormalizeQuantityMode(line.QuantityMode),
                Quantity = ResolveLineQuantity(line),
                HqPolicy = MarketAcquisitionPolicy.NormalizeHqPolicy(line.HqPolicy),
                MaxUnitPrice = line.MaxUnitPrice,
                MaxTotalGil = line.GilCap,
            })
            .ToList();
    }

    private static uint ResolveLineQuantity(MarketAcquisitionBatchLineView line) =>
        line.QuantityMode.Equals("AllBelowThreshold", StringComparison.OrdinalIgnoreCase)
            ? line.MaxQuantity
            : line.TargetQuantity;

    private static MarketAcquisitionPlanDiagnostics BuildDiagnostics(
        MarketAcquisitionRequestView request,
        IReadOnlyList<PlannerLine> requestLines,
        IReadOnlyList<MarketAcquisitionListing> listings)
    {
        var lineItemIds = requestLines.Select(line => line.ItemId).ToHashSet();
        var relevantListings = listings
            .Where(listing => lineItemIds.Contains(listing.ItemId))
            .ToList();
        var nonZero = relevantListings
            .Where(listing => listing.Quantity != 0 && listing.UnitPrice != 0)
            .ToList();
        var priceSupported = nonZero
            .Where(listing => requestLines.Any(line => line.ItemId == listing.ItemId && listing.UnitPrice <= line.MaxUnitPrice))
            .ToList();
        var hqSupported = priceSupported
            .Where(listing => requestLines.Any(line => line.ItemId == listing.ItemId && MarketAcquisitionPolicy.HqMatches(line.HqPolicy, listing.IsHq)))
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

    private static bool ListingMatchesLine(
        MarketAcquisitionRequestView request,
        PlannerLine line,
        MarketAcquisitionListing listing)
    {
        if (listing.Quantity == 0 || listing.UnitPrice == 0)
            return false;

        if (listing.ItemId != line.ItemId)
            return false;

        if (listing.UnitPrice > line.MaxUnitPrice)
            return false;

        if (!MarketAcquisitionPolicy.HqMatches(line.HqPolicy, listing.IsHq))
            return false;

        if (request.WorldMode.Equals("CurrentWorldOnly", StringComparison.OrdinalIgnoreCase) &&
            !listing.WorldName.Equals(request.TargetWorld, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static void ValidateRequest(MarketAcquisitionRequestView request)
    {
        if (!string.IsNullOrWhiteSpace(request.HqPolicy))
            _ = MarketAcquisitionPolicy.NormalizeHqPolicy(request.HqPolicy);

        if (request.WorldMode is not ("Recommended" or "CurrentWorldOnly" or "Selected" or "AllWorldSweep"))
            throw new InvalidOperationException($"Unknown world mode {request.WorldMode}.");

        if (request.WorldMode == "Selected")
            throw new InvalidOperationException("Selected world mode requires selected worlds in the request payload before it can be planned.");
    }

    private static IReadOnlyList<MarketAcquisitionWorldItemSubtask> BuildExecutableLinePlan(
        PlannerLine line,
        IReadOnlyList<MarketAcquisitionWorldItemSubtask> candidates,
        bool keepProbeSubtasksAfterCaps = false)
    {
        var subtasks = new List<MarketAcquisitionWorldItemSubtask>();
        uint plannedQuantity = 0;
        uint plannedGil = 0;
        var hasGilCap = line.MaxTotalGil > 0;

        foreach (var subtask in candidates)
        {
            if (HasReachedQuantityCap(line, plannedQuantity))
            {
                if (keepProbeSubtasksAfterCaps)
                    subtasks.Add(ToProbeSubtask(subtask));
                continue;
            }

            if (hasGilCap && plannedGil + subtask.PlannedGil > line.MaxTotalGil)
            {
                if (keepProbeSubtasksAfterCaps)
                    subtasks.Add(ToProbeSubtask(subtask));
                continue;
            }

            subtasks.Add(subtask);
            plannedQuantity += subtask.PlannedQuantity;
            plannedGil += subtask.PlannedGil;
        }

        return subtasks;
    }

    public static string ResolveNorthAmericaDataCenter(string worldName)
    {
        if (string.IsNullOrWhiteSpace(worldName))
            throw new InvalidOperationException("World name is required before route data center sorting.");

        return NorthAmericaDataCenters.TryGetValue(worldName.Trim(), out var dataCenter)
            ? dataCenter
            : throw new InvalidOperationException($"World {worldName} is not mapped to a North America data center.");
    }

    public static IReadOnlyList<string> ResolveNorthAmericaWorldsForDataCenters(IEnumerable<string> dataCenters)
    {
        ArgumentNullException.ThrowIfNull(dataCenters);

        var normalizedDataCenters = dataCenters
            .Select(NormalizeNorthAmericaDataCenterName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (normalizedDataCenters.Count == 0)
            throw new InvalidOperationException("At least one data center is required for a scoped all-world sweep.");

        return NorthAmericaDataCenters
            .Where(entry => normalizedDataCenters.Contains(entry.Value))
            .Select(entry => entry.Key)
            .ToList();
    }

    private static IReadOnlyList<string> ResolveSweepWorlds(
        MarketAcquisitionRequestView request,
        string? currentWorld)
    {
        if (!request.Region.Equals("North America", StringComparison.OrdinalIgnoreCase) &&
            !request.Region.Equals("North-America", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("All-world sweep currently supports the North America region only.");

        var scope = string.IsNullOrWhiteSpace(request.SweepScope)
            ? "Region"
            : request.SweepScope.Trim();

        return scope switch
        {
            "Region" => NorthAmericaDataCenters.Keys.ToList(),
            "CurrentDataCenter" => ResolveNorthAmericaWorldsForDataCenters(
                [ResolveNorthAmericaDataCenter(string.IsNullOrWhiteSpace(currentWorld) ? request.TargetWorld : currentWorld)]),
            "DataCenters" => ResolveNorthAmericaWorldsForDataCenters(request.SweepDataCenters),
            _ => throw new InvalidOperationException($"Unknown all-world sweep scope {request.SweepScope}."),
        };
    }

    private static string NormalizeNorthAmericaDataCenterName(string dataCenter)
    {
        if (string.IsNullOrWhiteSpace(dataCenter))
            throw new InvalidOperationException("Data center name is required for a scoped all-world sweep.");

        var normalized = NorthAmericaDataCenterOrder
            .FirstOrDefault(candidate => candidate.Equals(dataCenter.Trim(), StringComparison.OrdinalIgnoreCase));
        return normalized ?? throw new InvalidOperationException($"{dataCenter} is not a North America data center.");
    }

    private static MarketAcquisitionWorldItemSubtask BuildWorldSubtask(
        PlannerLine line,
        string worldName,
        IEnumerable<MarketAcquisitionListing> listings,
        string source = "Planned")
    {
        var plannedListings = new List<MarketAcquisitionPlannedListing>();
        uint plannedQuantity = 0;
        uint plannedGil = 0;
        var hasGilCap = line.MaxTotalGil > 0;

        foreach (var listing in listings)
        {
            if (HasReachedQuantityCap(line, plannedQuantity))
                break;

            if (hasGilCap && plannedGil + listing.TotalGil > line.MaxTotalGil)
                continue;

            plannedListings.Add(new MarketAcquisitionPlannedListing
            {
                LineId = line.LineId,
                ItemId = line.ItemId,
                ItemName = line.ItemName,
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

        return new MarketAcquisitionWorldItemSubtask
        {
            LineId = line.LineId,
            LineOrdinal = line.Ordinal,
            Source = source,
            ItemId = line.ItemId,
            ItemName = line.ItemName,
            WorldName = worldName,
            DataCenter = ResolveNorthAmericaDataCenter(worldName),
            QuantityMode = line.QuantityMode,
            RequestedQuantity = line.Quantity,
            HqPolicy = line.HqPolicy,
            MaxUnitPrice = line.MaxUnitPrice,
            GilCap = line.MaxTotalGil,
            PlannedQuantity = plannedQuantity,
            PlannedGil = plannedGil,
            ExceedsRequestedQuantity = line.Quantity > 0 && plannedQuantity > line.Quantity,
            Listings = plannedListings,
        };
    }

    private static MarketAcquisitionWorldItemSubtask ToProbeSubtask(MarketAcquisitionWorldItemSubtask subtask) =>
        subtask with
        {
            Source = "SweepProbe",
            PlannedQuantity = 0,
            PlannedGil = 0,
            ExceedsRequestedQuantity = false,
            Listings = [],
        };

    private static MarketAcquisitionWorldBatch BuildWorldBatch(
        string worldName,
        IEnumerable<MarketAcquisitionWorldItemSubtask> subtasks)
    {
        var orderedSubtasks = subtasks
            .OrderBy(subtask => subtask.LineOrdinal)
            .ToList();
        var listings = orderedSubtasks
            .SelectMany(subtask => subtask.Listings)
            .ToList();

        return new MarketAcquisitionWorldBatch
        {
            WorldName = worldName,
            DataCenter = ResolveNorthAmericaDataCenter(worldName),
            PlannedQuantity = (uint)orderedSubtasks.Sum(subtask => subtask.PlannedQuantity),
            PlannedGil = (uint)orderedSubtasks.Sum(subtask => subtask.PlannedGil),
            ExceedsRequestedQuantity = orderedSubtasks.Any(subtask => subtask.ExceedsRequestedQuantity),
            ItemSubtasks = orderedSubtasks,
            Listings = listings,
        };
    }

    private static bool HasReachedQuantityCap(PlannerLine line, uint plannedQuantity) =>
        !IsUnboundedAllBelowThreshold(line) && plannedQuantity >= line.Quantity;

    private static bool IsUnboundedAllBelowThreshold(PlannerLine line) =>
        line.Quantity == 0 &&
        line.QuantityMode.Equals("AllBelowThreshold", StringComparison.OrdinalIgnoreCase);

    private static bool LineSatisfiesQuantity(PlannerLine line, uint plannedQuantity) =>
        IsUnboundedAllBelowThreshold(line) || plannedQuantity >= line.Quantity;

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

    private static string NormalizeQuantityMode(string quantityMode) =>
        quantityMode switch
        {
            "TargetQuantity" => "TargetQuantity",
            "AllBelowThreshold" => "AllBelowThreshold",
            _ => throw new InvalidOperationException($"Unknown quantity mode {quantityMode}."),
        };

    private sealed record PlannerLine
    {
        public string LineId { get; init; } = string.Empty;
        public int Ordinal { get; init; }
        public uint ItemId { get; init; }
        public string? ItemName { get; init; }
        public string QuantityMode { get; init; } = string.Empty;
        public uint Quantity { get; init; }
        public string HqPolicy { get; init; } = string.Empty;
        public uint MaxUnitPrice { get; init; }
        public uint MaxTotalGil { get; init; }
    }
}
