using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MarketMafioso.MarketAcquisition;

public interface IMarketAcquisitionListingSource
{
    Task<IReadOnlyDictionary<uint, IReadOnlyList<MarketAcquisitionListing>>> FetchListingsAsync(
        string worldDataCenterOrRegion,
        IReadOnlyCollection<uint> itemIds,
        CancellationToken cancellationToken);
}

public sealed class MarketAcquisitionPlanPreparationService
{
    public static bool CanPrepareForStatus(string status) =>
        string.Equals(status, "AcceptedInPlugin", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "Running", StringComparison.OrdinalIgnoreCase) ||
        IsFailedStatus(status);

    public static bool IsFailedStatus(string status) =>
        string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase);

    private readonly IMarketAcquisitionListingSource listingSource;
    private readonly MarketAcquisitionWorldVisitCatalog worldVisitCatalog;
    private readonly IMarketAcquisitionInventoryObserver? inventoryObserver;

    public MarketAcquisitionPlanPreparationService(
        IMarketAcquisitionListingSource listingSource,
        MarketAcquisitionWorldVisitCatalog worldVisitCatalog,
        IMarketAcquisitionInventoryObserver? inventoryObserver = null)
    {
        this.listingSource = listingSource ?? throw new ArgumentNullException(nameof(listingSource));
        this.inventoryObserver = inventoryObserver;
        this.worldVisitCatalog = worldVisitCatalog ?? throw new ArgumentNullException(nameof(worldVisitCatalog));
    }

    public async Task<MarketAcquisitionPlanPreparationResult> PrepareAsync(
        MarketAcquisitionPlanPreparationRequest request,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        var claimed = request.Claim ?? throw new InvalidOperationException("No acquisition request is available.");
        var planLines = GetPlanLines(claimed);
        var baselines = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var line in planLines.Where(line => line.QuantityMode == "TargetQuantity" &&
                     MarketAcquisitionTargetBases.Normalize(line.TargetBasis) == MarketAcquisitionTargetBases.OnHandTotal))
        {
            var observation = inventoryObserver?.Observe(line.ItemId, line.HqPolicy);
            if (observation?.IsAvailable != true)
                throw new InvalidOperationException($"Cannot finalize safely for {line.ItemName}: {observation?.Message ?? "Inventory observer unavailable."}");
            baselines[line.LineId] = observation.Value.Quantity;
        }
        var listings = new List<MarketAcquisitionListing>();
        var sweepWorldExclusions = new List<MarketAcquisitionSweepWorldExclusion>();
        var freshEvidenceWorldCount = 0;
        var recentSkippedWorldCount = 0;
        var preparedAtUtc = request.PreparedAtUtc;
        var isAllWorldSweep = claimed.WorldMode.Equals("AllWorldSweep", StringComparison.OrdinalIgnoreCase);
        IReadOnlyDictionary<uint, IReadOnlyList<MarketAcquisitionListing>> listingsByItem;
        try
        {
            listingsByItem = await listingSource.FetchListingsAsync(
                claimed.Region,
                planLines.Select(line => line.ItemId).Distinct().ToArray(),
                token).ConfigureAwait(false);
        }
        catch (UniversalisMarketListingsUnavailableException ex)
        {
            var affectedNames = planLines
                .Where(line => ex.MissingItemIds.Contains(line.ItemId))
                .Select(line => line.ItemName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var subject = affectedNames.Length == 0
                ? "one or more plan items"
                : string.Join(", ", affectedNames);
            throw new InvalidOperationException(
                $"Market listings could not be refreshed for {subject} after retry. Select Finalize Plan to retry.",
                ex);
        }

        foreach (var line in planLines)
        {
            token.ThrowIfCancellationRequested();
            if (!listingsByItem.TryGetValue(line.ItemId, out var lineListings))
            {
                throw new InvalidOperationException(
                    $"Market listings were not returned for {line.ItemName}. Select Finalize Plan to retry.");
            }

            if (isAllWorldSweep || claimed.WorldMode.Equals("Recommended", StringComparison.OrdinalIgnoreCase))
            {
                var evidenceWorlds = FindWorldsWithNewerUsefulUniversalisEvidence(
                    line,
                    lineListings,
                    preparedAtUtc,
                    request.RecentWorldTtl,
                    isAllWorldSweep && request.IgnoreRecentWorldVisitsForSweep);
                freshEvidenceWorldCount += evidenceWorlds.Count;

                var filterResult = MarketAcquisitionRecentWorldPolicy.FilterListings(
                    line,
                    lineListings,
                    worldVisitCatalog,
                    preparedAtUtc,
                    request.RecentWorldTtl,
                    isAllWorldSweep && request.IgnoreRecentWorldVisitsForSweep,
                    evidenceWorlds);
                lineListings = filterResult.Listings;

                var lineExclusions = MarketAcquisitionRecentWorldPolicy.BuildSweepWorldExclusions(
                    line,
                    worldVisitCatalog,
                    preparedAtUtc,
                    request.RecentWorldTtl,
                    isAllWorldSweep && request.IgnoreRecentWorldVisitsForSweep,
                    evidenceWorlds);
                sweepWorldExclusions.AddRange(lineExclusions);
                recentSkippedWorldCount += lineExclusions.Count;
            }

            listings.AddRange(lineListings);
        }

        if (string.IsNullOrWhiteSpace(request.CurrentWorld))
            throw new InvalidOperationException("Current world is required before preparing a route-aware advisory plan.");

        var plan = MarketAcquisitionPlanner.BuildPlan(
            claimed,
            listings,
            preparedAtUtc,
            request.CurrentWorld,
            sweepWorldExclusions,
            baselines,
            request.MinimumStopValueGil,
            request.ExhaustiveResearchMode);
        var recommendedTargetLines = plan.Lines
            .Where(line => line.QuantityMode.Equals("TargetQuantity", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var recommendedPlannedQuantity = recommendedTargetLines.Aggregate(
            0u,
            (total, line) => checked(total + Math.Min(line.PlannedQuantity, RemainingNeed(line))));
        var recommendedRequestedQuantity = recommendedTargetLines.Aggregate(
            0u,
            (total, line) => checked(total + RemainingNeed(line)));
        var statusMessage = plan.Status == "Ready"
            ? BuildPreparedPlanStatus(plan, recentSkippedWorldCount, freshEvidenceWorldCount)
            : plan.Lines.All(line => line.QuantityMode == "TargetQuantity" && RemainingNeed(line) == 0)
                ? "All stock-up targets are already met. No travel or purchases are needed."
                : BuildNoSupportedListingsStatus(plan);
        var lowValueStops = plan.Diagnostics.StopDecisions.Count(decision => decision.Skipped);
        if (lowValueStops > 0)
            statusMessage += $" Skipped {lowValueStops:N0} world(s) below the minimum stop value of {plan.MinimumStopValueGil:N0} gil; see the plan for estimates.";
        if (claimed.WorldMode.Equals("Recommended", StringComparison.OrdinalIgnoreCase) &&
            recommendedPlannedQuantity < recommendedRequestedQuantity)
        {
            statusMessage +=
                $" Recommended listings support {recommendedPlannedQuantity:N0}/{recommendedRequestedQuantity:N0} target item(s); {recommendedRequestedQuantity - recommendedPlannedQuantity:N0} remain unplanned. No unsupported worlds were added.";
        }

        return new MarketAcquisitionPlanPreparationResult
        {
            Plan = plan,
            StatusMessage = statusMessage,
            RecentSkippedWorldCount = recentSkippedWorldCount,
            FreshEvidenceWorldCount = freshEvidenceWorldCount,
        };
    }

    public static IReadOnlyList<MarketAcquisitionBatchLineView> GetPlanLines(MarketAcquisitionClaimView claimed)
    {
        ArgumentNullException.ThrowIfNull(claimed);

        if (claimed.Lines.Count > 0)
            return claimed.Lines
                .OrderBy(line => line.Ordinal)
                .ToList();

        return
        [
            new MarketAcquisitionBatchLineView
            {
                LineId = claimed.Id,
                Ordinal = 0,
                ItemId = claimed.ItemId,
                ItemName = claimed.ItemName,
                QuantityMode = claimed.QuantityMode,
                TargetQuantity = claimed.Quantity,
                TargetBasis = MarketAcquisitionTargetBases.Normalize(claimed.TargetBasis),
                MaximumOverage = claimed.MaximumOverage,
                MaxQuantity = claimed.Quantity,
                HqPolicy = claimed.HqPolicy,
                MaxUnitPrice = claimed.MaxUnitPrice,
                GilCap = claimed.MaxTotalGil,
            },
        ];
    }

    private static uint RemainingNeed(MarketAcquisitionPlanLine line) =>
        line.TargetBasis == MarketAcquisitionTargetBases.OnHandTotal
            ? line.RequestedQuantity > line.InitialOnHandQuantity ? line.RequestedQuantity - line.InitialOnHandQuantity : 0u
            : line.RequestedQuantity;

    private IReadOnlyList<string> FindWorldsWithNewerUsefulUniversalisEvidence(
        MarketAcquisitionBatchLineView line,
        IReadOnlyList<MarketAcquisitionListing> regionalListings,
        DateTimeOffset preparedAtUtc,
        TimeSpan ttl,
        bool ignoreRecentVisits)
    {
        if (ignoreRecentVisits || ttl <= TimeSpan.Zero)
            return [];

        var hqPolicy = MarketAcquisitionPolicy.NormalizeHqPolicy(line.HqPolicy);
        var recentVisits = worldVisitCatalog.FindRecentWorlds(
            line.ItemId,
            hqPolicy,
            line.MaxUnitPrice,
            preparedAtUtc,
            ttl);
        if (recentVisits.Count == 0)
            return [];

        var evidenceWorlds = new List<string>();
        foreach (var visit in recentVisits)
        {
            var checkedAtUtc = DateTime.SpecifyKind(visit.CheckedAtUtc, DateTimeKind.Utc);
            if (regionalListings.Any(listing =>
                    listing.WorldName.Equals(visit.WorldName, StringComparison.OrdinalIgnoreCase) &&
                    IsNewerUsefulUniversalisListing(line, hqPolicy, checkedAtUtc, listing)))
            {
                evidenceWorlds.Add(visit.WorldName);
            }
        }

        return evidenceWorlds;
    }

    private static bool IsNewerUsefulUniversalisListing(
        MarketAcquisitionBatchLineView line,
        string hqPolicy,
        DateTime checkedAtUtc,
        MarketAcquisitionListing listing) =>
        listing.ItemId == line.ItemId &&
        listing.Quantity > 0 &&
        listing.UnitPrice > 0 &&
        listing.UnitPrice <= line.MaxUnitPrice &&
        MarketAcquisitionPolicy.HqMatches(hqPolicy, listing.IsHq) &&
        listing.LastReviewTimeUtc > new DateTimeOffset(checkedAtUtc);

    private static string BuildNoSupportedListingsStatus(MarketAcquisitionPlan plan)
    {
        var diagnostics = plan.Diagnostics;
        return "No supported listings found under the configured thresholds. " +
               $"Fetched {diagnostics.SourceListingCount:N0}; " +
               $"{diagnostics.NonZeroListingCount:N0} non-zero; " +
               $"{diagnostics.PriceSupportedListingCount:N0} at/below max unit; " +
               $"{diagnostics.HqSupportedListingCount:N0} after HQ policy; " +
               $"{diagnostics.WorldSupportedListingCount:N0} after world mode; " +
               $"{diagnostics.PlannedListingCount:N0} planned.";
    }

    private static string BuildPreparedPlanStatus(
        MarketAcquisitionPlan plan,
        int recentSkippedWorldCount,
        int freshEvidenceWorldCount)
    {
        var status = $"Prepared {plan.WorldBatches.Count:N0} world batch(es).";
        if (recentSkippedWorldCount > 0)
            status += $" Skipped {recentSkippedWorldCount:N0} recently cleared world/item check(s).";
        if (freshEvidenceWorldCount > 0)
            status += $" Reopened {freshEvidenceWorldCount:N0} recent world/item check(s) with fresh Universalis evidence.";

        return status;
    }
}

public sealed record MarketAcquisitionPlanPreparationRequest
{
    public int MinimumStopValueGil { get; init; } = 5_000;
    public bool ExhaustiveResearchMode { get; init; }
    public MarketAcquisitionClaimView? Claim { get; init; }
    public string CurrentWorld { get; init; } = string.Empty;
    public DateTimeOffset PreparedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public TimeSpan RecentWorldTtl { get; init; }
    public bool IgnoreRecentWorldVisitsForSweep { get; init; }
}

public sealed record MarketAcquisitionPlanPreparationResult
{
    public MarketAcquisitionPlan Plan { get; init; } = new();
    public string StatusMessage { get; init; } = string.Empty;
    public int RecentSkippedWorldCount { get; init; }
    public int FreshEvidenceWorldCount { get; init; }
}
