using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.RetainerRestock;

public static class RetainerRestockPlanner
{
    public static RetainerRestockPlan BuildPlan(
        IReadOnlyList<RetainerRestockPlanItem> rows,
        IReadOnlyDictionary<uint, int> playerInventory,
        Configuration config,
        DateTime nowUtc)
    {
        return BuildPlan(rows, playerInventory, config, nowUtc, ownerScope: null);
    }

    public static RetainerRestockPlan BuildPlan(
        IReadOnlyList<RetainerRestockPlanItem> rows,
        IReadOnlyDictionary<uint, int> playerInventory,
        Configuration config,
        DateTime nowUtc,
        RetainerOwnerScope? ownerScope)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(playerInventory);
        ArgumentNullException.ThrowIfNull(config);

        var lines = rows
            .Where(row => row.Enabled && row.ItemId > 0 && row.DesiredPlayerQuantity > 0)
            .Select(row => BuildLine(row, playerInventory, config, nowUtc, ownerScope))
            .OrderByDescending(line => line.NeededQuantity > 0)
            .ThenByDescending(line => line.MissingQuantity > 0)
            .ThenBy(line => line.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(line => line.ItemId)
            .ToList();

        return new RetainerRestockPlan(nowUtc, lines);
    }

    private static RetainerRestockPlanLine BuildLine(
        RetainerRestockPlanItem row,
        IReadOnlyDictionary<uint, int> playerInventory,
        Configuration config,
        DateTime nowUtc,
        RetainerOwnerScope? ownerScope)
    {
        var playerQuantity = playerInventory.TryGetValue(row.ItemId, out var count)
            ? count
            : 0;
        var neededQuantity = Math.Max(0, row.DesiredPlayerQuantity - playerQuantity);
        var candidates = neededQuantity == 0
            ? []
            : BuildCandidates(row.ItemId, config, ownerScope);
        var cachedRetainerQuantity = candidates.Sum(candidate => candidate.CachedQuantity);
        var missingQuantity = Math.Max(0, neededQuantity - cachedRetainerQuantity);
        var status = GetStatus(neededQuantity, cachedRetainerQuantity, missingQuantity);
        TimeSpan? oldestRelevantCacheAge = candidates.Count == 0
            ? null
            : nowUtc - candidates.Min(candidate => candidate.LastUpdatedUtc);

        return new RetainerRestockPlanLine(
            row.Id,
            row.ItemId,
            row.ItemName,
            row.DesiredPlayerQuantity,
            playerQuantity,
            neededQuantity,
            cachedRetainerQuantity,
            missingQuantity,
            candidates,
            status,
            oldestRelevantCacheAge);
    }

    private static RetainerRestockPlanLineStatus GetStatus(
        int neededQuantity,
        int cachedRetainerQuantity,
        int missingQuantity)
    {
        if (neededQuantity == 0)
            return RetainerRestockPlanLineStatus.NoNeed;

        if (cachedRetainerQuantity == 0)
            return RetainerRestockPlanLineStatus.NoCachedStock;

        return missingQuantity > 0
            ? RetainerRestockPlanLineStatus.Partial
            : RetainerRestockPlanLineStatus.Ready;
    }

    private static IReadOnlyList<RetainerRestockCandidate> BuildCandidates(
        uint itemId,
        Configuration config,
        RetainerOwnerScope? ownerScope)
    {
        return config.RetainerCache.Values
            .Where(retainer => ownerScope is null || ownerScope.Matches(retainer.OwnerCharacterName, retainer.OwnerHomeWorld))
            .Select(retainer => new
            {
                Retainer = retainer,
                Quantity = retainer.Bags
                    .SelectMany(bag => bag.Items)
                    .Where(item => item.ItemId == itemId)
                    .Sum(item => (int)item.Quantity),
            })
            .Where(candidate => candidate.Quantity > 0)
            .OrderByDescending(candidate => candidate.Quantity)
            .ThenByDescending(candidate => candidate.Retainer.LastUpdated)
            .ThenBy(candidate => candidate.Retainer.RetainerName, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => new RetainerRestockCandidate(
                candidate.Retainer.RetainerId,
                candidate.Retainer.RetainerName,
                candidate.Retainer.LastUpdated,
                candidate.Quantity))
            .ToList();
    }
}
