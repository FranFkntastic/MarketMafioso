using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.TradeQueue;

public static class TradeQueuePlanner
{
    public const uint GilItemId = 1;
    public const int MaximumTradeSlots = 5;
    public const int MaximumGilPerTrade = 1_000_000;

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

            var key = new TradeQueueItemKey(item.ItemId);
            requested[key] = checked(requested.GetValueOrDefault(key) + item.Quantity);
        }

        var available = CountCombinedInventory(inventory);
        foreach (var pair in requested)
        {
            var held = available.GetValueOrDefault(pair.Key);
            if (held < pair.Value)
            {
                var item = queue.First(value => value.ItemId == pair.Key.ItemId);
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
        int maximumSlots = MaximumTradeSlots,
        bool allowHighQualityItems = false)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(inventory);
        if (maximumSlots is <= 0 or > MaximumTradeSlots)
            throw new ArgumentOutOfRangeException(nameof(maximumSlots));

        var validation = Validate(queue, inventory);
        if (!validation.Success)
            throw new InvalidOperationException(validation.Message);

        var remainingByKey = queue
            .GroupBy(item => new TradeQueueItemKey(item.ItemId))
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Quantity));
        var orderedKeys = queue
            .Select(item => new TradeQueueItemKey(item.ItemId))
            .Distinct()
            .ToArray();
        var lines = new List<TradeQueueBatchLine>(maximumSlots);
        var gilKey = new TradeQueueItemKey(GilItemId);
        var gilAmount = remainingByKey.TryGetValue(gilKey, out var requestedGil)
            ? Math.Min(requestedGil, MaximumGilPerTrade)
            : 0;

        foreach (var key in orderedKeys.Where(key => key != gilKey))
        {
            var remaining = remainingByKey[key];
            foreach (var stack in inventory
                         .Where(value =>
                             value.ItemId == key.ItemId &&
                             (allowHighQualityItems || !value.IsHighQuality))
                         .OrderBy(value => value.IsHighQuality)
                         .ThenBy(value => value.ContainerId)
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

            if (remaining > 0)
            {
                throw new InvalidOperationException(
                    $"{queue.First(item => item.ItemId == key.ItemId).ItemName} still needs " +
                    (allowHighQualityItems
                        ? $"{remaining:N0} units after assigning all eligible inventory stacks."
                        : $"{remaining:N0} normal-quality units after quality normalization."));
            }
        }

        if (lines.Count == 0 && gilAmount == 0)
            throw new InvalidOperationException("No tradeable inventory stacks could be assigned to the next batch.");

        return new(lines, gilAmount, CountExactInventory(inventory));
    }

    public static bool HasExpectedInventoryDelta(
        TradeQueueBatch batch,
        IReadOnlyList<TradeQueueInventoryStack> inventoryAfter,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(inventoryAfter);
        var after = CountExactInventory(inventoryAfter);
        var expectedDelta = batch.Lines
            .GroupBy(line => new TradeQueueInventoryKey(line.ItemId, line.IsHighQuality))
            .ToDictionary(group => group.Key, group => group.Sum(line => line.Quantity));
        if (batch.GilAmount > 0)
            expectedDelta[new(GilItemId, false)] = batch.GilAmount;

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

        var parts = new List<string>();
        if (batch.ItemUnitCount > 0)
            parts.Add($"{batch.ItemUnitCount:N0} item units across {batch.SlotCount:N0} slots");
        if (batch.GilAmount > 0)
            parts.Add($"{batch.GilAmount:N0} gil");
        diagnostic = $"Verified {string.Join(" and ", parts)}.";
        return true;
    }

    public static void ApplyCompletedBatch(IList<TradeQueueItem> queue, TradeQueueBatch batch)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(batch);

        var completed = batch.Lines
            .GroupBy(line => new TradeQueueItemKey(line.ItemId))
            .ToDictionary(group => group.Key, group => group.Sum(line => line.Quantity));
        if (batch.GilAmount > 0)
            completed[new(GilItemId)] = batch.GilAmount;

        foreach (var grouped in completed)
        {
            var remaining = grouped.Value;
            for (var index = 0; index < queue.Count && remaining > 0; index++)
            {
                var item = queue[index];
                if (item.ItemId != grouped.Key.ItemId)
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

    public static IReadOnlyDictionary<TradeQueueInventoryKey, int> CountExactInventory(
        IReadOnlyList<TradeQueueInventoryStack> inventory) =>
        inventory
            .GroupBy(stack => new TradeQueueInventoryKey(stack.ItemId, stack.IsHighQuality))
            .ToDictionary(group => group.Key, group => group.Sum(stack => stack.Quantity));

    public static IReadOnlyDictionary<TradeQueueItemKey, int> CountCombinedInventory(
        IReadOnlyList<TradeQueueInventoryStack> inventory) =>
        inventory
            .GroupBy(stack => new TradeQueueItemKey(stack.ItemId))
            .ToDictionary(group => group.Key, group => group.Sum(stack => stack.Quantity));

    private static string Display(TradeQueueItem item) =>
        item.ItemId == GilItemId ? "Gil" : item.ItemName;
}
