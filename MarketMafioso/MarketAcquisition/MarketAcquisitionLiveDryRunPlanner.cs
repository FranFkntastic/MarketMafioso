using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.MarketAcquisition;

public static class MarketAcquisitionLiveDryRunPlanner
{
    public static MarketAcquisitionLiveDryRun BuildDryRun(
        MarketAcquisitionRequestView request,
        MarketAcquisitionPlan plan,
        string currentWorld,
        uint itemId,
        IEnumerable<MarketBoardLiveListing> liveListings)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(liveListings);

        Validate(request, plan, currentWorld, itemId);

        var mode = NormalizeQuantityMode(request.QuantityMode);
        var hasGilCap = request.MaxTotalGil > 0;
        var hasMaxQuantity = mode == "AllBelowThreshold" && request.Quantity > 0;
        var selectedQuantity = 0u;
        var selectedGil = 0u;
        var rows = new List<MarketAcquisitionLiveDryRunRow>();

        var candidates = liveListings
            .Select(listing => ValidateLiveListing(listing, currentWorld, itemId))
            .OrderBy(listing => listing.UnitPrice)
            .ThenByDescending(listing => listing.Quantity)
            .ThenBy(listing => listing.RetainerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(listing => listing.ListingId, StringComparer.Ordinal)
            .ToList();

        foreach (var listing in candidates)
        {
            if (listing.UnitPrice > request.MaxUnitPrice)
            {
                rows.Add(Skipped(listing, "AboveThreshold", "Live unit price is above the configured max unit price.", selectedQuantity, selectedGil));
                continue;
            }

            if (!MarketAcquisitionPolicy.HqMatches(request.HqPolicy, listing.IsHq))
            {
                rows.Add(Skipped(listing, "HqPolicyMismatch", "Live HQ flag does not match the request HQ policy.", selectedQuantity, selectedGil));
                continue;
            }

            if (mode == "TargetQuantity" && selectedQuantity >= request.Quantity)
            {
                rows.Add(Skipped(listing, "TargetSatisfied", "Target quantity is already satisfied by cheaper confirmed live listings.", selectedQuantity, selectedGil));
                continue;
            }

            var nextQuantity = checked(selectedQuantity + listing.Quantity);
            if (hasMaxQuantity && nextQuantity > request.Quantity)
            {
                rows.Add(Skipped(listing, "MaxQuantityExceeded", "Buying this whole listing would exceed the configured max quantity.", selectedQuantity, selectedGil));
                continue;
            }

            var listingGil = checked(listing.UnitPrice * listing.Quantity);
            if (hasGilCap && selectedGil + listingGil > request.MaxTotalGil)
            {
                rows.Add(Skipped(listing, "GilCapExceeded", "Buying this whole listing would exceed the configured gil cap.", selectedQuantity, selectedGil));
                continue;
            }

            selectedQuantity = nextQuantity;
            selectedGil = checked(selectedGil + listingGil);
            rows.Add(new MarketAcquisitionLiveDryRunRow
            {
                Decision = "WouldBuy",
                Reason = "SafeLiveCandidate",
                Message = "Would buy this confirmed live listing in a guarded purchase pass.",
                LiveListing = listing,
                RunningQuantityAfter = selectedQuantity,
                RunningGilAfter = selectedGil,
            });
        }

        var status = ResolveStatus(mode, request.Quantity, selectedQuantity);
        return new MarketAcquisitionLiveDryRun
        {
            Status = status,
            Message = ResolveMessage(status, mode, request.Quantity, selectedQuantity),
            RequestedQuantity = request.Quantity,
            WouldBuyQuantity = selectedQuantity,
            WouldSpendGil = selectedGil,
            Rows = rows,
        };
    }

    private static void Validate(
        MarketAcquisitionRequestView request,
        MarketAcquisitionPlan plan,
        string currentWorld,
        uint itemId)
    {
        if (string.IsNullOrWhiteSpace(currentWorld))
            throw new InvalidOperationException("Current market board world is required.");

        if (request.ItemId != itemId || plan.ItemId != itemId)
            throw new InvalidOperationException("Current market board search item does not match the acquisition request.");

        if (!plan.WorldBatches.Any(batch => batch.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Current market board world {currentWorld} is not present in the prepared plan.");

        _ = NormalizeQuantityMode(request.QuantityMode);

        _ = MarketAcquisitionPolicy.NormalizeHqPolicy(request.HqPolicy);
    }

    private static MarketBoardLiveListing ValidateLiveListing(
        MarketBoardLiveListing listing,
        string currentWorld,
        uint itemId)
    {
        if (listing.ItemId != itemId)
            throw new InvalidOperationException("Live market board rows include a different item id than the current search item.");

        if (!listing.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Live market board rows include a different world than the current market board world.");

        return listing;
    }

    private static string NormalizeQuantityMode(string quantityMode) =>
        quantityMode switch
        {
            "TargetQuantity" => "TargetQuantity",
            "AllBelowThreshold" => "AllBelowThreshold",
            _ => throw new InvalidOperationException($"Unknown quantity mode {quantityMode}."),
        };

    private static MarketAcquisitionLiveDryRunRow Skipped(
        MarketBoardLiveListing listing,
        string reason,
        string message,
        uint runningQuantity,
        uint runningGil) =>
        new()
        {
            Decision = "Skipped",
            Reason = reason,
            Message = message,
            LiveListing = listing,
            RunningQuantityAfter = runningQuantity,
            RunningGilAfter = runningGil,
        };

    private static string ResolveStatus(string mode, uint requestedQuantity, uint selectedQuantity)
    {
        if (selectedQuantity == 0)
            return "NoSafeListings";

        if (mode == "TargetQuantity" && selectedQuantity < requestedQuantity)
            return "UnderProcured";

        return "Ready";
    }

    private static string ResolveMessage(string status, string mode, uint requestedQuantity, uint selectedQuantity) =>
        status switch
        {
            "Ready" when mode == "TargetQuantity" => $"Would satisfy target quantity with {selectedQuantity:N0}/{requestedQuantity:N0} confirmed live item(s).",
            "Ready" => $"Would buy {selectedQuantity:N0} confirmed live item(s) below threshold.",
            "UnderProcured" => $"Only {selectedQuantity:N0}/{requestedQuantity:N0} requested item(s) are safely available on this live market board page.",
            _ => "No visible live listings satisfy the request constraints.",
        };
}
