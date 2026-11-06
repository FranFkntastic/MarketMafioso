using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using MarketMafioso.Contracts.Inventory;
using MarketMafioso.Quartermaster;

namespace MarketMafioso.WorkshopPrep;

public static class WorkshopMaterialAvailabilityService
{
    public static IReadOnlyList<WorkshopMaterialAvailability> BuildAvailabilityWithFallback(
        IReadOnlyList<WorkshopMaterialRequirement> requirements,
        IReadOnlyDictionary<uint, int> playerInventory,
        bool hasDirectRetainerEvidence,
        IReadOnlyList<RetainerReport> directRetainers,
        QuartermasterSnapshot? quartermasterSnapshot,
        QuartermasterOwnerScope ownerScope)
    {
        ArgumentNullException.ThrowIfNull(directRetainers);
        ArgumentNullException.ThrowIfNull(ownerScope);

        var hasSnapshot = quartermasterSnapshot is not null && ownerScope.Matches(quartermasterSnapshot.Owner);
        var fallbackRetainers = hasSnapshot
            ? quartermasterSnapshot!.Retainers
            : [];
        // Reports are obtained through the owner-scoped observation source. Never use them
        // without an owner, or when their optional owner labels contradict that scope.
        var reports = hasDirectRetainerEvidence && ownerScope.IsAvailable
            ? directRetainers.Where(report =>
                (report.OwnerCharacterName is null || string.Equals(report.OwnerCharacterName, ownerScope.CharacterName, StringComparison.OrdinalIgnoreCase)) &&
                (report.OwnerHomeWorld is null || string.Equals(report.OwnerHomeWorld, ownerScope.HomeWorldName, StringComparison.OrdinalIgnoreCase))).ToArray()
            : [];
        var merged = BuildAvailability(
            requirements,
            playerInventory,
            itemId => reports.Select(retainer => new QuartermasterRetainerCandidate(
                    retainer.RetainerId, retainer.RetainerName, InventoryObservedAt(retainer),
                    retainer.Bags.SelectMany(bag => bag.Items).Where(item => item.ItemId == itemId)
                        .Sum(item => checked((int)item.Quantity))))
                .Concat(fallbackRetainers
                .Select(retainer => new QuartermasterRetainerCandidate(
                    retainer.RetainerId,
                    retainer.RetainerName,
                    retainer.ObservedAtUtc,
                    retainer.Bags.SelectMany(bag => bag.Items)
                        .Where(item => item.ItemId == itemId)
                        .Sum(item => checked((int)item.Quantity)))))
                .GroupBy(candidate => candidate.RetainerId)
                // RQ wins ties. Keep zero observations until after source selection.
                .Select(group => group.OrderBy(candidate => candidate.ObservedAtUtc).Last())
                .Where(candidate => candidate.Quantity > 0)
                .OrderByDescending(candidate => candidate.Quantity)
                .ToList());
        return merged.Select(item => item with
        {
            RetrievableStock = item.QuartermasterRetainers.Sum(candidate =>
                Math.Min(candidate.Quantity, fallbackRetainers
                    .Where(retainer => retainer.RetainerId == candidate.RetainerId)
                    .SelectMany(retainer => retainer.Bags).SelectMany(bag => bag.Items)
                    .Where(stock => stock.ItemId == item.ItemId).Sum(stock => checked((int)stock.Quantity)))),
            RetrievalUnavailableReason = hasSnapshot ? null :
                "Retainer retrieval unavailable: no owner-matched Quartermaster snapshot.",
        }).ToArray();
    }

    private static DateTimeOffset InventoryObservedAt(RetainerReport report)
    {
        var bagTimes = report.Bags.Select(bag => ParseTimestamp(bag.ObservedAtUtc))
            .Where(time => time != DateTimeOffset.MinValue).ToArray();
        // No inventory observation is no evidence: roster/gil/listing headers must never
        // make an absent inventory look like a fresh zero that outranks RQ's cache.
        return bagTimes.Length > 0 ? bagTimes.Min() : DateTimeOffset.MinValue;
    }

    private static DateTimeOffset ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)
            ? time : DateTimeOffset.MinValue;

    public static IReadOnlyList<WorkshopMaterialAvailability> BuildAvailabilityFromRetainers(
        IReadOnlyList<WorkshopMaterialRequirement> requirements,
        IReadOnlyDictionary<uint, int> playerInventory,
        IReadOnlyList<RetainerReport> retainers)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(playerInventory);
        ArgumentNullException.ThrowIfNull(retainers);

        return BuildAvailability(
            requirements,
            playerInventory,
            itemId => retainers
                .Select(retainer => new QuartermasterRetainerCandidate(
                    retainer.RetainerId,
                    retainer.RetainerName,
                    DateTime.TryParse(retainer.LastUpdated, out var observedAt) ? observedAt.ToUniversalTime() : DateTime.MinValue,
                    retainer.Bags.SelectMany(bag => bag.Items)
                        .Where(item => item.ItemId == itemId)
                        .Sum(item => checked((int)item.Quantity))))
                .Where(candidate => candidate.Quantity > 0)
                .OrderByDescending(candidate => candidate.Quantity)
                .ToList());
    }

    private static IReadOnlyList<WorkshopMaterialAvailability> BuildAvailability(
        IReadOnlyList<WorkshopMaterialRequirement> requirements,
        IReadOnlyDictionary<uint, int> playerInventory,
        Func<uint, IReadOnlyList<QuartermasterRetainerCandidate>> getRetainerStock)
    {
        return requirements
            .GroupBy(requirement => requirement.ItemId)
            .Select(group =>
            {
                var first = group.First();
                var required = group.Sum(requirement => requirement.Quantity);
                var playerCount = playerInventory.GetValueOrDefault(first.ItemId);
                var retainerStock = getRetainerStock(first.ItemId);
                var retainerCount = retainerStock.Sum(candidate => candidate.Quantity);
                var shortage = Math.Max(0, required - playerCount);
                return new WorkshopMaterialAvailability(
                    first.ItemId,
                    first.ItemName,
                    first.IconId,
                    required,
                    playerCount,
                    retainerCount,
                    shortage,
                    Math.Max(0, required - playerCount - retainerCount),
                    shortage == 0 ? [] : retainerStock);
            })
            .OrderBy(availability => availability.ItemName)
            .ToList();
    }

}
