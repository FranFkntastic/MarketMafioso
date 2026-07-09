using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.RetainerRestock;

public enum RetainerRestockStockSourceKind
{
    PlayerInventory,
    Retainer,
    FcChest,
}

public sealed record RetainerRestockStockSource(
    RetainerRestockStockSourceKind Kind,
    string SourceName,
    int Quantity,
    ulong? RetainerId,
    DateTime? LastUpdatedUtc);

public sealed record RetainerRestockStockRow(
    uint ItemId,
    string ItemName,
    int TotalQuantity,
    int PlayerQuantity,
    int RetainerQuantity,
    IReadOnlyList<RetainerRestockStockSource> Sources,
    IReadOnlyList<RetainerRestockStockSource> RetainerSources,
    TimeSpan? OldestRetainerCacheAge,
    TimeSpan? NewestRetainerCacheAge)
{
    public bool HasRetainerStock => RetainerQuantity > 0;
}

public static class RetainerRestockStockCatalog
{
    public static IReadOnlyList<RetainerRestockStockRow> Build(
        IReadOnlyList<InventoryBag> playerBags,
        Configuration config,
        DateTime nowUtc,
        RetainerOwnerScope? ownerScope)
    {
        ArgumentNullException.ThrowIfNull(playerBags);
        ArgumentNullException.ThrowIfNull(config);

        var rows = new Dictionary<uint, StockRowBuilder>();

        foreach (var item in playerBags.SelectMany(bag => bag.Items))
        {
            if (!IsPositiveItem(item.ItemId, item.Quantity))
                continue;

            var row = GetOrAdd(rows, item.ItemId);
            row.SetItemName(item.ItemName);
            row.PlayerQuantity += (int)item.Quantity;
        }

        foreach (var retainer in config.RetainerCache.Values
                     .Where(retainer => ownerScope is null || ownerScope.Matches(retainer.OwnerCharacterName, retainer.OwnerHomeWorld)))
        {
            foreach (var retainerItem in AggregateRetainerItems(retainer))
            {
                var row = GetOrAdd(rows, retainerItem.ItemId);
                row.SetItemName(retainerItem.ItemName);
                row.RetainerSources.Add(new RetainerRestockStockSource(
                    RetainerRestockStockSourceKind.Retainer,
                    retainer.RetainerName,
                    retainerItem.Quantity,
                    retainer.RetainerId,
                    retainer.LastUpdated));
            }
        }

        return rows.Values
            .Select(row => row.Build(nowUtc))
            .OrderBy(row => row.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.ItemId)
            .ToList();
    }

    public static IReadOnlyList<RetainerRestockStockRow> Search(
        IReadOnlyList<RetainerRestockStockRow> rows,
        string search,
        int limit = 50)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (limit <= 0)
            return [];

        var normalizedSearch = search.Trim();
        if (normalizedSearch.Length == 0)
            return rows.Take(limit).ToList();

        return rows
            .Where(row => row.ItemName.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(row => row.ItemName.StartsWith(normalizedSearch, StringComparison.OrdinalIgnoreCase))
            .ThenBy(row => row.ItemName.Length)
            .ThenBy(row => row.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.ItemId)
            .Take(limit)
            .ToList();
    }

    private static bool IsPositiveItem(uint itemId, uint quantity) =>
        itemId > 0 && quantity > 0;

    private static StockRowBuilder GetOrAdd(Dictionary<uint, StockRowBuilder> rows, uint itemId)
    {
        if (!rows.TryGetValue(itemId, out var row))
        {
            row = new StockRowBuilder(itemId);
            rows[itemId] = row;
        }

        return row;
    }

    private static IReadOnlyList<RetainerItemAggregate> AggregateRetainerItems(CachedRetainer retainer)
    {
        return retainer.Bags
            .SelectMany(bag => bag.Items)
            .Where(item => IsPositiveItem(item.ItemId, item.Quantity))
            .GroupBy(item => item.ItemId)
            .Select(group =>
            {
                var itemName = group
                    .Select(item => item.ItemName)
                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));

                return new RetainerItemAggregate(
                    group.Key,
                    itemName,
                    group.Sum(item => (int)item.Quantity));
            })
            .Where(item => item.Quantity > 0)
            .ToList();
    }

    private sealed class StockRowBuilder(uint itemId)
    {
        private string? itemName;

        public uint ItemId { get; } = itemId;
        public int PlayerQuantity { get; set; }
        public List<RetainerRestockStockSource> RetainerSources { get; } = [];

        public void SetItemName(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(itemName) && !string.IsNullOrWhiteSpace(candidate))
                itemName = candidate;
        }

        public RetainerRestockStockRow Build(DateTime nowUtc)
        {
            var sortedRetainerSources = RetainerSources
                .OrderByDescending(source => source.Quantity)
                .ThenByDescending(source => source.LastUpdatedUtc ?? DateTime.MinValue)
                .ThenBy(source => source.SourceName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var sources = new List<RetainerRestockStockSource>();

            if (PlayerQuantity > 0)
            {
                sources.Add(new RetainerRestockStockSource(
                    RetainerRestockStockSourceKind.PlayerInventory,
                    "Player",
                    PlayerQuantity,
                    RetainerId: null,
                    LastUpdatedUtc: null));
            }

            sources.AddRange(sortedRetainerSources);

            var retainerQuantity = sortedRetainerSources.Sum(source => source.Quantity);
            var retainerUpdatedTimes = sortedRetainerSources
                .Select(source => source.LastUpdatedUtc)
                .OfType<DateTime>()
                .ToList();

            return new RetainerRestockStockRow(
                ItemId,
                string.IsNullOrWhiteSpace(itemName) ? $"Item {ItemId}" : itemName,
                PlayerQuantity + retainerQuantity,
                PlayerQuantity,
                retainerQuantity,
                sources,
                sortedRetainerSources,
                retainerUpdatedTimes.Count == 0 ? null : nowUtc - retainerUpdatedTimes.Min(),
                retainerUpdatedTimes.Count == 0 ? null : nowUtc - retainerUpdatedTimes.Max());
        }
    }

    private sealed record RetainerItemAggregate(uint ItemId, string? ItemName, int Quantity);
}
