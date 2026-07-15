using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Automation.Retainers;

namespace MarketMafioso.RetainerRestock;

public sealed record ElementalDepositPlanLine(
    uint ItemId,
    string ItemName,
    int PlayerQuantity,
    int PotentialCapacity,
    int PlannedQuantity,
    int UnplannedQuantity);

public sealed record ElementalDepositRetainerCandidate(
    ulong RetainerId,
    string RetainerName,
    DateTime LastUpdatedUtc,
    IReadOnlyDictionary<uint, int> CapacityByItem,
    int UsableCapacity,
    bool CapacityIsKnown);

public sealed record ElementalDepositPlan(
    DateTime BuiltAtUtc,
    IReadOnlyList<ElementalDepositPlanLine> Lines,
    IReadOnlyList<ElementalDepositRetainerCandidate> Candidates,
    int ScopedRetainerCount,
    int UnknownCrystalCacheCount)
{
    public int PlayerQuantity => Lines.Sum(line => line.PlayerQuantity);
    public int PlannedQuantity => Lines.Sum(line => line.PlannedQuantity);
    public int UnplannedQuantity => Lines.Sum(line => line.UnplannedQuantity);
    public bool CanRun => PlannedQuantity > 0 && Candidates.Count > 0;
}

public static class ElementalDepositPlanner
{
    private const string CrystalBagName = "RetainerCrystals";

    public static ElementalDepositPlan Build(
        IReadOnlyDictionary<uint, int> playerCrystals,
        Configuration config,
        RetainerOwnerScope ownerScope,
        Func<uint, string?> resolveItemName,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(playerCrystals);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(ownerScope);
        ArgumentNullException.ThrowIfNull(resolveItemName);

        var carried = playerCrystals
            .Where(entry => ElementalCurrencyCatalog.IsShardOrCrystal(entry.Key) && entry.Value > 0)
            .ToDictionary(entry => entry.Key, entry => entry.Value);
        var scopedRetainers = config.RetainerCache.Values
            .Where(retainer => ownerScope.Matches(retainer.OwnerCharacterName, retainer.OwnerHomeWorld))
            .ToList();
        var candidates = scopedRetainers
            .Select(retainer => new
            {
                Retainer = retainer,
                CrystalBag = retainer.Bags.FirstOrDefault(bag => bag.BagName == CrystalBagName),
            })
            .Select(entry => BuildCandidate(entry.Retainer, entry.CrystalBag, carried))
            .Where(candidate => candidate.UsableCapacity > 0)
            .OrderByDescending(candidate => candidate.UsableCapacity)
            .ThenByDescending(candidate => candidate.CapacityIsKnown)
            .ThenByDescending(candidate => candidate.LastUpdatedUtc)
            .ThenBy(candidate => candidate.RetainerName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var lines = carried
            .OrderBy(entry => entry.Key)
            .Select(entry =>
            {
                var capacity = candidates.Sum(candidate => candidate.CapacityByItem[entry.Key]);
                var planned = Math.Min(entry.Value, capacity);
                return new ElementalDepositPlanLine(
                    entry.Key,
                    resolveItemName(entry.Key) ?? $"Item {entry.Key}",
                    entry.Value,
                    capacity,
                    planned,
                    entry.Value - planned);
            })
            .ToList();

        return new ElementalDepositPlan(
            nowUtc,
            lines,
            candidates,
            scopedRetainers.Count,
            scopedRetainers.Count(retainer => retainer.Bags.All(bag => bag.BagName != CrystalBagName)));
    }

    private static ElementalDepositRetainerCandidate BuildCandidate(
        CachedRetainer retainer,
        CachedBag? crystalBag,
        IReadOnlyDictionary<uint, int> carried)
    {
        var capacity = carried.Keys.ToDictionary(
            itemId => itemId,
            itemId => crystalBag == null
                ? ElementalCurrencyCatalog.PerItemCapacity
                : Math.Max(
                    0,
                    ElementalCurrencyCatalog.PerItemCapacity -
                    crystalBag.Items.Where(item => item.ItemId == itemId).Sum(item => (int)item.Quantity)));
        var usable = capacity.Sum(entry => Math.Min(entry.Value, carried[entry.Key]));
        return new ElementalDepositRetainerCandidate(
            retainer.RetainerId,
            retainer.RetainerName,
            retainer.LastUpdated,
            capacity,
            usable,
            crystalBag != null);
    }
}
