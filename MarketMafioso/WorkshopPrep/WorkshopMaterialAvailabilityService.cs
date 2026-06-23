using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.WorkshopPrep;

public static class WorkshopMaterialAvailabilityService
{
    public static IReadOnlyList<WorkshopMaterialAvailability> BuildAvailability(
        IReadOnlyList<WorkshopMaterialRequirement> requirements,
        IReadOnlyDictionary<uint, int> playerInventory,
        Configuration config)
    {
        return requirements
            .GroupBy(x => x.ItemId)
            .Select(group =>
            {
                var first = group.First();
                var required = group.Sum(x => x.Quantity);
                var playerCount = playerInventory.TryGetValue(first.ItemId, out var count) ? count : 0;
                var shortage = Math.Max(0, required - playerCount);
                var candidates = shortage == 0
                    ? []
                    : BuildCandidates(first.ItemId, config);
                var retainerCount = candidates.Sum(x => x.Quantity);

                return new WorkshopMaterialAvailability(
                    first.ItemId,
                    first.ItemName,
                    first.IconId,
                    required,
                    playerCount,
                    retainerCount,
                    shortage,
                    candidates);
            })
            .OrderBy(x => x.ItemName)
            .ToList();
    }

    private static IReadOnlyList<RetainerMaterialCandidate> BuildCandidates(uint itemId, Configuration config)
    {
        return config.RetainerCache.Values
            .Select(retainer => new
            {
                Retainer = retainer,
                Quantity = retainer.Bags
                    .SelectMany(x => x.Items)
                    .Where(x => x.ItemId == itemId)
                    .Sum(x => (int)x.Quantity),
            })
            .Where(x => x.Quantity > 0)
            .OrderByDescending(x => x.Quantity)
            .Select(x => new RetainerMaterialCandidate(
                x.Retainer.RetainerId,
                x.Retainer.RetainerName,
                x.Retainer.LastUpdated,
                x.Quantity))
            .ToList();
    }
}
