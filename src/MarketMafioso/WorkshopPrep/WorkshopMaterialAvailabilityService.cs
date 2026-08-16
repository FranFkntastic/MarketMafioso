using System;
using System.Collections.Generic;
using System.Linq;
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

        if (hasDirectRetainerEvidence)
            return BuildAvailabilityFromRetainers(requirements, playerInventory, directRetainers);

        var fallbackRetainers = quartermasterSnapshot is not null && ownerScope.Matches(quartermasterSnapshot.Owner)
            ? quartermasterSnapshot.Retainers
            : [];
        return BuildAvailability(
            requirements,
            playerInventory,
            itemId => fallbackRetainers
                .Select(retainer => new QuartermasterRetainerCandidate(
                    retainer.RetainerId,
                    retainer.RetainerName,
                    retainer.ObservedAtUtc,
                    retainer.Bags.SelectMany(bag => bag.Items)
                        .Where(item => item.ItemId == itemId)
                        .Sum(item => checked((int)item.Quantity))))
                .Where(candidate => candidate.Quantity > 0)
                .OrderByDescending(candidate => candidate.Quantity)
                .ToList());
    }

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
