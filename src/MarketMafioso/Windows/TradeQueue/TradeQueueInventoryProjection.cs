using System;
using System.Collections.Generic;
using System.Linq;
using MarketMafioso.TradeQueue;

namespace MarketMafioso.Windows.TradeQueue;

internal static class TradeQueueInventoryProjection
{
    public static IReadOnlyList<TradeQueueInventoryRow> Build(
        IReadOnlyList<TradeQueueInventoryStack> inventory,
        IReadOnlyList<TradeQueueItem> queue)
    {
        var selected = queue
            .GroupBy(item => new TradeQueueItemKey(item.ItemId))
            .ToDictionary(
                group => group.Key,
                group => group.Sum(item => Math.Max(0, item.Quantity)));
        var rows = inventory
            .Where(stack => stack.ItemId > 0 && stack.Quantity > 0)
            .GroupBy(stack => new TradeQueueItemKey(stack.ItemId))
            .Select(group => new TradeQueueInventoryRow(
                group.Key,
                group.First().ItemName,
                group.Sum(stack => stack.Quantity),
                selected.GetValueOrDefault(group.Key)))
            .ToList();
        var observed = rows.Select(row => row.Key).ToHashSet();
        rows.AddRange(
            queue
                .Where(item => item.Quantity > 0 && !observed.Contains(new(item.ItemId)))
                .GroupBy(item => new TradeQueueItemKey(item.ItemId))
                .Select(group => new TradeQueueInventoryRow(
                    group.Key,
                    group.First().ItemName,
                    0,
                    group.Sum(item => item.Quantity))));

        return rows
            .OrderByDescending(row => row.SelectedQuantity > 0)
            .ThenBy(row => row.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Key.ItemId)
            .ToList();
    }
}

internal sealed record TradeQueueInventoryRow(
    TradeQueueItemKey Key,
    string ItemName,
    int AvailableQuantity,
    int SelectedQuantity);
