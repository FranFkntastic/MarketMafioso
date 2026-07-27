using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.TradeQueue;

public static class TradeQueuePlanner
{
    public const int MaximumTradeSlots = 5;

    public static TradeQueueValidationResult Validate(
        IReadOnlyList<TradeQueueItem> queue,
        IReadOnlyList<TradeQueueInventoryStack> inventory)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(inventory);

        if (queue.Count == 0)
            return new(false, TradeQueueValidationCode.Empty, "Trade queue is empty.");

        var requested = new Dictionary<TradeQueueItemKey, int>();
        foreach (var item in queue)
        {
            if (item.ItemId == 0 || item.Quantity <= 0)
                return new(false, TradeQueueValidationCode.InvalidQuantity, $"{Display(item)} has an invalid quantity.");

            var key = new TradeQueueItemKey(item.ItemId, item.IsHighQuality);
            requested[key] = checked(requested.GetValueOrDefault(key) + item.Quantity);
        }

        var available = CountInventory(inventory);
        foreach (var pair in requested)
        {
            var held = available.GetValueOrDefault(pair.Key);
            if (held < pair.Value)
            {
                var item = queue.First(value =>
                    value.ItemId == pair.Key.ItemId &&
                    value.IsHighQuality == pair.Key.IsHighQuality);
                return new(
                    false,
                    TradeQueueValidationCode.InsufficientInventory,
                    $"{Display(item)} needs {pair.Value:N0}, but only {held:N0} tradeable units are available.");
            }
        }

        return new(true, TradeQueueValidationCode.Ready, "Trade queue is ready.");
    }

    public static TradeQueueBatch BuildNextBatch(
        IReadOnlyList<TradeQueueItem> queue,
        IReadOnlyList<TradeQueueInventoryStack> inventory,
        int maximumSlots = MaximumTradeSlots)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(inventory);
        if (maximumSlots is <= 0 or > MaximumTradeSlots)
            throw new ArgumentOutOfRangeException(nameof(maximumSlots));

        var validation = Validate(queue, inventory);
        if (!validation.Success)
            throw new InvalidOperationException(validation.Message);

        var remainingByKey = queue
            .GroupBy(item => new TradeQueueItemKey(item.ItemId, item.IsHighQuality))
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Quantity));
        var orderedKeys = queue
            .Select(item => new TradeQueueItemKey(item.ItemId, item.IsHighQuality))
            .Distinct()
            .ToArray();
        var lines = new List<TradeQueueBatchLine>(maximumSlots);

        foreach (var key in orderedKeys)
        {
            var remaining = remainingByKey[key];
            foreach (var stack in inventory
                         .Where(value => value.ItemId == key.ItemId && value.IsHighQuality == key.IsHighQuality)
                         .OrderBy(value => value.ContainerId)
                         .ThenBy(value => value.SlotIndex))
            {
                if (remaining <= 0 || lines.Count == maximumSlots)
                    break;

                var quantity = Math.Min(remaining, stack.Quantity);
                if (quantity <= 0)
                    continue;

                lines.Add(new(
                    stack.ContainerId,
                    stack.SlotIndex,
                    stack.ItemId,
                    stack.ItemName,
                    stack.IsHighQuality,
                    quantity,
                    stack.Quantity));
                remaining -= quantity;
            }

            if (lines.Count == maximumSlots)
                break;
        }

        if (lines.Count == 0)
            throw new InvalidOperationException("No tradeable inventory stacks could be assigned to the next batch.");

        return new(lines, CountInventory(inventory));
    }

    public static bool HasExpectedInventoryDelta(
        TradeQueueBatch batch,
        IReadOnlyList<TradeQueueInventoryStack> inventoryAfter,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(inventoryAfter);
        var after = CountInventory(inventoryAfter);
        var expectedDelta = batch.Lines
            .GroupBy(line => new TradeQueueItemKey(line.ItemId, line.IsHighQuality))
            .ToDictionary(group => group.Key, group => group.Sum(line => line.Quantity));

        foreach (var pair in expectedDelta)
        {
            var before = batch.ExpectedInventoryBefore.GetValueOrDefault(pair.Key);
            var current = after.GetValueOrDefault(pair.Key);
            var observed = before - current;
            if (observed != pair.Value)
            {
                diagnostic =
                    $"Expected item {pair.Key.ItemId} {(pair.Key.IsHighQuality ? "HQ" : "NQ")} to decrease by {pair.Value:N0}, but observed {observed:N0} ({before:N0}->{current:N0}).";
                return false;
            }
        }

        diagnostic = $"Verified {batch.UnitCount:N0} traded units across {batch.SlotCount:N0} slots.";
        return true;
    }

    public static void ApplyCompletedBatch(IList<TradeQueueItem> queue, TradeQueueBatch batch)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(batch);

        foreach (var grouped in batch.Lines.GroupBy(line => new TradeQueueItemKey(line.ItemId, line.IsHighQuality)))
        {
            var remaining = grouped.Sum(line => line.Quantity);
            for (var index = 0; index < queue.Count && remaining > 0; index++)
            {
                var item = queue[index];
                if (item.ItemId != grouped.Key.ItemId || item.IsHighQuality != grouped.Key.IsHighQuality)
                    continue;

                var consumed = Math.Min(item.Quantity, remaining);
                item.Quantity -= consumed;
                remaining -= consumed;
                if (item.Quantity <= 0)
                {
                    queue.RemoveAt(index);
                    index--;
                }
            }

            if (remaining != 0)
                throw new InvalidOperationException($"Completed batch exceeded queued quantity for item {grouped.Key.ItemId}.");
        }
    }

    public static IReadOnlyDictionary<TradeQueueItemKey, int> CountInventory(
        IReadOnlyList<TradeQueueInventoryStack> inventory) =>
        inventory
            .GroupBy(stack => new TradeQueueItemKey(stack.ItemId, stack.IsHighQuality))
            .ToDictionary(group => group.Key, group => group.Sum(stack => stack.Quantity));

    public static int GetMaximumQueueQuantity(
        IReadOnlyList<TradeQueueItem> queue,
        IReadOnlyDictionary<TradeQueueItemKey, int> inventoryCounts,
        TradeQueueItemKey key,
        int? excludedRowIndex = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(inventoryCounts);
        if (excludedRowIndex is < 0 || excludedRowIndex >= queue.Count)
            throw new ArgumentOutOfRangeException(nameof(excludedRowIndex));

        long queuedElsewhere = 0;
        for (var index = 0; index < queue.Count; index++)
        {
            if (index == excludedRowIndex)
                continue;

            var item = queue[index];
            if (item.ItemId == key.ItemId && item.IsHighQuality == key.IsHighQuality)
                queuedElsewhere = checked(queuedElsewhere + Math.Max(0, item.Quantity));
        }

        var available = inventoryCounts.GetValueOrDefault(key);
        return (int)Math.Clamp((long)available - queuedElsewhere, 0, int.MaxValue);
    }

    private static string Display(TradeQueueItem item) =>
        $"{item.ItemName} {(item.IsHighQuality ? "HQ" : "NQ")}".Trim();
}
