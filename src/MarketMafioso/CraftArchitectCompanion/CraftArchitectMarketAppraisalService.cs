using System;
using System.Collections.Generic;
using System.Linq;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.CraftArchitectCompanion;

public static class CraftArchitectMarketAppraisalService
{
    public static MarketAppraisalResult Build(
        MarketAppraisalRequest request,
        IEnumerable<MarketAcquisitionListing> listings,
        CraftAppraisalQuote? craftQuote = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(listings);

        var supportedListings = listings
            .Where(listing => ListingMatchesRequest(request, listing))
            .ToList();
        var worlds = supportedListings
            .GroupBy(listing => listing.WorldName, StringComparer.OrdinalIgnoreCase)
            .Select(group => BuildWorldSummary(group.Key, group))
            .OrderByDescending(world => request.Quantity > 0 && world.Quantity >= request.Quantity)
            .ThenBy(world => world.TotalGil)
            .ThenBy(world => world.LowestUnitPrice)
            .ThenBy(world => world.WorldName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var warnings = new List<string>();
        if (worlds.Count == 0)
            warnings.Add("No market stock is available at or below the chosen buy threshold.");

        return new MarketAppraisalResult
        {
            Request = request,
            CraftQuote = craftQuote,
            SupportedQuantity = SumQuantity(supportedListings),
            SupportedListingCount = (uint)supportedListings.Count,
            SupportedWorldCount = (uint)worlds.Count,
            SupportedTotalGil = SumGil(supportedListings),
            Worlds = worlds,
            Warnings = warnings,
        };
    }

    private static bool ListingMatchesRequest(
        MarketAppraisalRequest request,
        MarketAcquisitionListing listing) =>
        listing.ItemId == request.ItemId &&
        listing.Quantity > 0 &&
        listing.UnitPrice > 0 &&
        listing.UnitPrice <= request.BuyThresholdUnitPrice &&
        HqPolicyMatches(request.HqPolicy, listing.IsHq);

    private static bool HqPolicyMatches(string? hqPolicy, bool isHq)
    {
        var normalized = (hqPolicy ?? "Either").Trim();
        if (normalized.Equals("HqOnly", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("HQOnly", StringComparison.OrdinalIgnoreCase))
            return isHq;
        if (normalized.Equals("NqOnly", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("NQOnly", StringComparison.OrdinalIgnoreCase))
            return !isHq;
        return true;
    }

    private static MarketAppraisalWorldSummary BuildWorldSummary(
        string worldName,
        IEnumerable<MarketAcquisitionListing> listings)
    {
        var rows = listings.ToList();
        return new MarketAppraisalWorldSummary
        {
            WorldName = worldName,
            Quantity = SumQuantity(rows),
            ListingCount = (uint)rows.Count,
            TotalGil = SumGil(rows),
            LowestUnitPrice = rows.Min(listing => listing.UnitPrice),
            HighestUnitPrice = rows.Max(listing => listing.UnitPrice),
            FreshestReviewTimeUtc = rows.Max(listing => listing.LastReviewTimeUtc),
        };
    }

    private static uint SumQuantity(IEnumerable<MarketAcquisitionListing> listings)
    {
        ulong total = 0;
        foreach (var listing in listings)
            total = checked(total + listing.Quantity);
        return total > uint.MaxValue ? uint.MaxValue : (uint)total;
    }

    private static ulong SumGil(IEnumerable<MarketAcquisitionListing> listings)
    {
        ulong total = 0;
        foreach (var listing in listings)
            total = checked(total + ((ulong)listing.UnitPrice * listing.Quantity));
        return total;
    }
}
