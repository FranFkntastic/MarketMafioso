using System;
using System.Collections.Generic;
using System.Linq;
using MarketMafioso.TradeQueue;

namespace MarketMafioso.Windows.TradeQueue;

internal enum TradeQueueBulkAction
{
    QueueAllAvailable,
    SetQuantity,
    RemoveFromQueue,
}

internal static class TradeQueueBulkEdit
{
    public static IReadOnlyList<TradeQueueItem> Apply(
        IReadOnlyList<TradeQueueItem> currentQueue,
        IReadOnlyList<TradeQueueInventoryRow> inventoryRows,
        IReadOnlySet<uint> selectedItemIds,
        TradeQueueBulkAction action,
        int quantity = 0)
    {
        ArgumentNullException.ThrowIfNull(currentQueue);
        ArgumentNullException.ThrowIfNull(inventoryRows);
        ArgumentNullException.ThrowIfNull(selectedItemIds);

        var selected = inventoryRows
            .Where(row =>
                row.Key.ItemId != TradeQueuePlanner.GilItemId &&
                row.AvailableQuantity > 0 &&
                selectedItemIds.Contains(row.Key.ItemId))
            .Select(row => row.Key.ItemId)
            .ToHashSet();
        var updated = currentQueue
            .Where(item => !selected.Contains(item.ItemId))
            .Select(Clone)
            .ToList();

        if (action == TradeQueueBulkAction.RemoveFromQueue)
            return updated;
        if (action is not (TradeQueueBulkAction.QueueAllAvailable or TradeQueueBulkAction.SetQuantity))
            throw new ArgumentOutOfRangeException(nameof(action), action, null);

        foreach (var row in inventoryRows
                     .Where(row =>
                         selected.Contains(row.Key.ItemId) &&
                         row.AvailableQuantity > 0)
                     .OrderBy(row => row.ItemName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(row => row.Key.ItemId))
        {
            var selectedQuantity = action == TradeQueueBulkAction.SetQuantity
                ? Math.Clamp(quantity, 0, row.AvailableQuantity)
                : row.AvailableQuantity;
            if (selectedQuantity <= 0)
                continue;
            updated.Add(new TradeQueueItem
            {
                ItemId = row.Key.ItemId,
                ItemName = row.ItemName,
                Quantity = selectedQuantity,
            });
        }

        return updated;
    }

    private static TradeQueueItem Clone(TradeQueueItem item) => new()
    {
        ItemId = item.ItemId,
        ItemName = item.ItemName,
        Quantity = item.Quantity,
    };
}
